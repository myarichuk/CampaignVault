"""Scripted take_turn replay on a SFW scratch campaign (see TAKE_TURN_PLAN.md). Never point this at a real DB.

  CAMPAIGN_DB_PATH=$(mktemp -d) MCP_PORT=5399 dotnet run --no-build --project src/CampaignVault
  python3 scripts/measure/take_turn_replay.py        # ~5 min, paced under the commit rate limiter; writes out/
  python3 scripts/measure/take_turn_breakdown.py s1  # field composition per session
  python3 scripts/measure/take_turn_whatif.py s1     # offline what-if of each proposed trim
"""
import os, sys, json, time, statistics
sys.argv = ['m', '/']
HERE = os.path.dirname(os.path.abspath(__file__))
exec(open(os.path.join(HERE, 'mcp_client.py')).read())
OUT = os.path.join(HERE, 'out'); os.makedirs(OUT, exist_ok=True)

C = "saltmarsh-bell"
PC, CO = "chars/tamsin", "chars/bram"
L = {k: f"locations/{k}" for k in ["bell-square", "harbor", "lighthouse", "warehouse", "tavern", "chapel"]}
NPC_AT = {
    "bell-square": ["pim", "sera"],
    "harbor": ["oda", "fenn", "dock-guard"],
    "warehouse": ["vess", "thug-a", "thug-b"],
    "tavern": ["rhel", "marta", "old-jory", "lute-girl"],
    "chapel": ["sister-anne"],
    "lighthouse": [],
}
NAMES = {"pim": "Pim the Lamplighter", "sera": "Sera the Baker", "oda": "Oda the Harbormaster", "fenn": "Fenn the Netmender",
         "dock-guard": "Dock Guard Hal", "vess": "Vess", "thug-a": "Knife Thug", "thug-b": "Club Thug", "rhel": "Captain Rhel",
         "marta": "Marta the Innkeeper", "old-jory": "Old Jory", "lute-girl": "Ilsa the Lutist", "sister-anne": "Sister Anne"}
EXITS = {"bell-square": ["harbor", "tavern", "chapel"], "harbor": ["bell-square", "lighthouse", "warehouse"], "lighthouse": ["harbor"],
         "warehouse": ["harbor"], "tavern": ["bell-square"], "chapel": ["bell-square"]}

def stats(lvl, ac):
    return {"$system": "dnd5e", "armorClass": ac, "level": lvl, "strength": 12, "dexterity": 14, "constitution": 12,
            "intelligence": 12, "wisdom": 12, "charisma": 12, "hitDie": "d8"}

log = []  # (session, idx, kind, reqChars, respChars, file)
def rec(sn, kind, name, args, o, t):
    i = len(log)
    fn = f"{i:03d}_s{sn}_{kind.replace(' ', '_')}.json"
    json.dump({"tool": name, "args": args, "text": t}, open(os.path.join(OUT, fn), 'w'), indent=1)
    log.append((sn, i, kind, len(json.dumps(args, separators=(',', ':'))), len(t), fn, bool(o and o.get('success'))))
    if not o or not o.get('success'):
        print('FAIL', kind, t[:400])

def do(sn, kind, name, args):
    time.sleep(1.05)  # stay under the 10-per-10s commit limiter
    o, t = call(name, args)
    rec(sn, kind, name, args, o, t)
    return o

def seed():
    ok, _ = call('create_campaign', {"name": C, "initialSystem": "Dnd5e", "displayName": "Saltmarsh Bell"}), None
    locs = [{"id": L[k], "name": k.replace('-', ' ').title(), "type": "Building" if k not in ("bell-square", "harbor") else "District",
             "description": f"The {k.replace('-', ' ')} of a small fishing town; salt, tar and gulls.",
             "exits": [{"targetLocationId": L[e], "description": f"Path to the {e.replace('-', ' ')}", "travelCostHours": 0.25} for e in EXITS[k]]} for k in L]
    chars = [
        {"id": PC, "name": "Tamsin", "isPc": True, "currentLocationId": L["bell-square"], "currentHp": 21, "maxHp": 21, "systemStats": stats(3, 14)},
        {"id": CO, "name": "Bram", "isPartyCompanion": True, "currentLocationId": L["bell-square"], "currentHp": 24, "maxHp": 24, "systemStats": stats(3, 16)},
    ]
    for loc, ids in NPC_AT.items():
        for n in ids:
            chars.append({"id": f"chars/{n}", "name": NAMES[n], "currentLocationId": L[loc], "currentHp": 11, "maxHp": 11,
                          "systemStats": stats(2, 12), "notes": f"{NAMES[n]} knows something about the dark lighthouse."})
    items = [{"id": "items/tamsin-lockpicks", "name": "Thieves' Tools", "holderId": PC},
             {"id": "items/tamsin-shortsword", "name": "Shortsword", "holderId": PC, "isEquipped": True},
             {"id": "items/bram-mace", "name": "Mace", "holderId": CO, "isEquipped": True},
             {"id": "items/oil-ledger", "name": "Oil Ledger", "holderId": "chars/vess"}]
    quests = [{"id": "quests/dark-lamp", "title": "The Dark Lamp", "description": "Find out why the lighthouse went dark.",
               "objectives": [{"description": d} for d in ["Visit the lighthouse", "Find who took the oil", "Relight the lamp"]]}]
    o, t = call('world_build', {"campaignName": C, "batch": {"locations": locs, "characters": chars, "items": items, "quests": quests}})
    if not o or not o.get('success'):
        print('seed fail', t[:1500]); raise SystemExit(1)

state = {"loc": L["bell-square"], "fp": None, "n": 0}

def turn(sn, kind, changes=None, **extra):
    state["n"] += 1
    req = {"narrative": f"S{sn} beat {state['n']}: {kind}.", "partyLocationId": state["loc"]}
    if state["fp"]: req["clientPartyFingerprint"] = state["fp"]
    if changes: req["changes"] = changes
    req.update(extra)
    o = do(sn, kind, 'take_turn', {"campaignName": C, "request": req})
    if o and o.get('success'):
        state["fp"] = (o.get('data') or {}).get('partyFingerprint') or state["fp"]
    return o

def ev(sn, text, *ids):
    state["e"] = state.get("e", 0) + 1
    eid = f"events/s{sn}-e{state['e']}"
    return eid, {"$type": "event", "eventId": eid, "summary": text, "involvedEntityIds": [PC, *ids], "locationId": state["loc"]}

def route(src, dst):
    prev, q = {src: None}, [src]
    while q:
        cur = q.pop(0)
        for nx in EXITS[cur]:
            if nx not in prev: prev[nx] = cur; q.append(nx)
    path = [dst]
    while prev[path[-1]] != src: path.append(prev[path[-1]])
    return path[::-1]

def travel(sn, dest):
    here = state["loc"].split('/')[1]
    if here == dest: return None
    hops = route(here, dest)
    for h in hops[:-1]: travel_one(sn, h)
    return travel_one(sn, hops[-1])

def travel_one(sn, dest):
    state["loc"] = L[dest]
    return turn(sn, "arrival", [{"$type": "travel", "characterId": PC, "destinationLocationId": L[dest]},
                                {"$type": "travel", "characterId": CO, "destinationLocationId": L[dest]}],
                fullDetailLocationId=L[dest])

def talk(sn, npc, line, mood=None, rel=None, memory=None):
    eid, e = ev(sn, line, f"chars/{npc}")
    ch = [e]
    if mood: ch.append({"$type": "mood", "characterId": f"chars/{npc}", "newMood": mood})
    if rel: ch.append({"$type": "relationship", "characterId": f"chars/{npc}", "targetId": PC, "reason": line[:60], "delta": rel})
    if memory:
        ch.append({"$type": "knowledge_update", "characterId": PC, "topic": memory, "details": f"From {NAMES[npc]}: {line}",
                   "importance": "Important", "source": "Witnessed", "sourceEventIds": [eid], "relatedEntityIds": [f"chars/{npc}"]})
        ch.append({"$type": "knowledge_update", "characterId": f"chars/{npc}", "topic": f"Tamsin asked: {memory}",
                   "details": f"Tamsin the stranger pressed me about {memory}; I told her more than I meant to.",
                   "importance": "Important", "source": "Experienced", "sourceEventIds": [eid], "relatedEntityIds": [PC]})
    kind = "beat+memory" if memory else ("beat+social" if (mood or rel) else "beat")
    return turn(sn, kind, ch)

def check(sn, skill, dc, line):
    _, e = ev(sn, line)
    return turn(sn, "skill check", [{"$type": "ruleset_action", "characterId": PC, "actionType": "SkillCheck", "actionName": skill,
                                     "parameters": {"skill": skill, "dc": str(dc)}}, e])

def active_of(o):
    d = (o or {}).get('data') or {}
    for k in ('activeTurnId', 'ActiveTurnId'):
        if d.get(k): return d[k]
    enc = d.get('encounter') or d.get('combat') or {}
    return enc.get('activeTurnId')

def fight(sn):
    o = do(sn, "combat start", 'combat', {"campaignName": C, "action": "start", "locationId": state["loc"],
                                          "combatantIds": [PC, CO, "chars/thug-a", "chars/thug-b"]})
    who = active_of(o)
    if not who: print('no active turn in', json.dumps(o)[:600])
    weapon = {PC: "Shortsword", CO: "Mace", "chars/thug-a": "Knife", "chars/thug-b": "Club"}
    foe = {PC: "chars/thug-a", CO: "chars/thug-b", "chars/thug-a": PC, "chars/thug-b": CO}
    for r in range(6):
        _, e = ev(sn, f"Exchange {r + 1}: steel in the salt dust.", "chars/thug-a", "chars/thug-b")
        turn(sn, "combat attack", [{"$type": "ruleset_action", "characterId": who, "actionType": "Attack", "actionName": weapon[who],
                                    "targetIds": [foe[who]]}, e])
        o = do(sn, "combat next", 'combat', {"campaignName": C, "action": "next"})
        who = active_of(o) or who
    do(sn, "combat end", 'combat', {"campaignName": C, "action": "end"})

def session(sn):
    o = do(sn, "start_session", 'start_session', {"campaignName": C})
    party = (o or {}).get('data', {}).get('party') or []
    state["fp"] = (o or {}).get('data', {}).get('partyFingerprint')
    state["loc"] = (party[0].get('locationId') if party else None) or state["loc"]
    turn(sn, "scene load", fullDetailLocationId=state["loc"])
    here = state["loc"].split('/')[1]
    for n in NPC_AT.get(here, [])[:2]:
        talk(sn, n, f"Tamsin asks {NAMES[n]} about the dark lamp (s{sn}).", mood="curious")
    travel(sn, "harbor")
    talk(sn, "oda", f"Oda admits the oil ledger went missing (s{sn}).", mood="nervous", memory=f"missing ledger s{sn}")
    turn(sn, "npc focus", fullDetailCharacterId="chars/oda")
    talk(sn, "oda", "Oda begs them to keep it quiet.", rel=5)
    talk(sn, "fenn", "Fenn says boats moved at night tide.", memory=f"night boats s{sn}")
    check(sn, "Investigation", 13, "Tamsin studies the tar marks on the pier.")
    travel(sn, "lighthouse")
    check(sn, "Perception", 12, "Tamsin spots boot prints in the lamp room.")
    talk(sn, "oda", "Tamsin pockets a torn customs seal.", memory=f"customs seal s{sn}")
    turn(sn, "includeParty", [ev(sn, "The party regroups on the stairs.")[1]], includeParty=True)
    travel(sn, "harbor")
    travel(sn, "warehouse")
    talk(sn, "vess", "Vess denies everything, too quickly.", mood="hostile", rel=-5)
    turn(sn, "query only", extraCharacterIds=["chars/thug-a"])
    fight(sn)
    turn(sn, "includeParty", [ev(sn, "Bandaging after the fight.")[1]], includeParty=True)
    travel(sn, "harbor")
    travel(sn, "bell-square")
    travel(sn, "tavern")
    for n in ["rhel", "marta", "old-jory"]:
        talk(sn, n, f"{NAMES[n]} trades gossip about the smugglers (s{sn}).", mood="amused", rel=3)
    talk(sn, "rhel", "Rhel warns them off the warehouse.", memory=f"rhel warning s{sn}")
    turn(sn, "world state", includeWorldState=True)
    do(sn, "advance_world", 'advance_world', {"campaignName": C, "narrative": "They sleep at the tavern.", "hours": 8, "partyLocationId": state["loc"]})
    turn(sn, "after rest", [ev(sn, "Morning chowder at the tavern.")[1]])
    do(sn, "end_session", 'end_session', {"campaignName": C, "handoff": {
        "storySoFar": f"Harbor mystery, session {sn}: the lighthouse went dark; Vess moves oil at night; Oda owes smugglers.",
        "lastSession": f"Session {sn}: harbor, lighthouse, warehouse fight, tavern gossip.",
        "openThreads": ["Who holds the lamp key"], "npcsInPlay": [{"id": "chars/vess", "stance": "hostile"}],
        "partyIntent": "Watch the night tide.", "tone": "Slow-burn harbor mystery."}})

if __name__ == '__main__':
    seed()
    for sn in (1, 2, 3):
        session(sn)
    json.dump(log, open(os.path.join(OUT, 'log.json'), 'w'))
    by = {}
    for sn, i, kind, rq, rs, fn, ok in log:
        by.setdefault(kind, []).append(rs)
    print(f"{'call':20} {'n':>3} {'median':>7} {'max':>7} {'sum':>7}")
    for k, v in by.items():
        print(f"{k:20} {len(v):>3} {int(statistics.median(v)):>7} {max(v):>7} {sum(v):>7}")
    for sn in (1, 2, 3):
        tt = sum(r[4] for r in log if r[0] == sn and r[5].split('_', 2)[2] and 'take_turn' in open(os.path.join(OUT, r[5])).read(40))
        allc = sum(r[4] for r in log if r[0] == sn)
        print(f"session {sn}: all tool results {allc}, take_turn {tt}")
    print('fails:', [(r[2], r[5]) for r in log if not r[6]])
