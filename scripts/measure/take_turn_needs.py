"""Offline what-if for a need-driven take_turn (TAKE_TURN_PLAN.md "Need-driven responses").

Starts from the trimmed responses (whatif levers minus A2/C3), then reshapes them:
  - scenes carry a roster line per NPC, not a card; description/events only on the first visit this session;
  - an NPC's card (traits/wants/fears, mood, notes, AC/level, top-2 memories) is sent once per session:
      N1 on arrival for every unbriefed NPC present,
      N2 on the first commit that involves the NPC (conversation), roster-only on arrival;
  - npcs[] echo keeps only involved NPCs whose mood changed; bystander tension echoes go;
  - explicit pulls (fullDetailCharacterId, includeParty, includeWorldState) are untouched.
Scratch NPCs have no traits/wants/fears, so every card gets a synthetic ~120-char set to stay honest.
"""
import os, json, sys, copy
HERE = os.path.dirname(os.path.abspath(__file__))
src = open(os.path.join(HERE, 'take_turn_whatif.py')).read()
exec(src[:src.index('def total(')])  # loads turns for SESS + lever functions

SYNTH = {"traits": ["guarded", "proud", "dutiful"], "wants": ["keep the harbor running"], "fears": ["the smugglers' collectors"]}
TRIMS = [l_npc_projection, l_party_out_of_scene, l_events, l_scene_chrome, l_pressure, l_memories, l_rest_drift, l_summary, l_echo_noise]

def trimmed():
    cur = [copy.deepcopy(o) for _, _, o in turns]
    for fn in TRIMS:
        st = {}
        for o in cur: fn(o['data'], st)
    for o in cur: l_envelope(o)
    return cur

def card(n):
    c = {k: v for k, v in n.items() if k not in ('id', 'name', 'currentActivity', 'knownNeeds', 'behavioralTension', 'activeInitiatives')}
    needs = {k: v for k, v in (n.get('knownNeeds') or {}).items() if v >= 60}
    if needs: c['pressingNeeds'] = needs
    return {"id": n['id'], **SYNTH, **c}

def roster_line(n, with_note):
    note = f" | {n['notes'][:80]}" if with_note and n.get('notes') else ''
    return f"{n.get('name')} [{n['id']}]: {n.get('currentMood') or '?'}, {n.get('currentActivity') or ''}{note}"

def involved_npcs(req):
    ids = []
    for ch in req.get('changes') or []:
        if ch.get('$type') == 'event':
            ids += [i for i in ch.get('involvedEntityIds') or [] if i.startswith('chars/') and i not in PARTY]
        elif ch.get('$type') in ('mood', 'relationship', 'knowledge_update') and ch.get('characterId') not in PARTY:
            ids.append(ch['characterId'])
    return list(dict.fromkeys(ids))

def reshape(cur, variant):
    briefed, visited, cards, moods = set(), set(), {}, {}
    out = []
    for (f, args, _), o in zip(turns, cur):
        o = copy.deepcopy(o); d = o['data']; req = args['request']
        scene_lists = ([('fullScene', d['fullScene'])] if d.get('fullScene') else []) + [('scenes', s) for s in d.get('scenes') or []]
        new_scenes = []
        for key, sc in scene_lists:
            loc = sc.get('location') or {}
            first = loc.get('id') not in visited and key == 'fullScene'
            visited.add(loc.get('id'))
            present = [n for n in sc.get('presentNPCs') or [] if n['id'] not in PARTY]
            for n in present:
                cards[n['id']] = card(n); moods[n['id']] = n.get('currentMood')
            ns = {"location": {"id": loc.get('id'), "name": loc.get('name'), "exits": [e['targetLocationId'] for e in loc.get('exits') or []]},
                  "present": [roster_line(n, variant == 'N2' and n['id'] not in briefed) for n in present]}
            if first:
                ns['location']['description'] = loc.get('description')
                if sc.get('recentEventSummaries'): ns['events'] = sc['recentEventSummaries'][:3]
            if sc.get('scenePressure'): ns['pressure'] = sc['scenePressure']
            if sc.get('associatedPlotThreads'): ns['plotThreads'] = sc['associatedPlotThreads']
            if variant == 'N1' and key == 'fullScene':
                fresh = [cards[n['id']] for n in present if n['id'] not in briefed]
                briefed.update(n['id'] for n in present)
                if fresh: ns['cards'] = fresh
            new_scenes.append((key, ns))
        d.pop('fullScene', None); d.pop('scenes', None)
        for key, ns in new_scenes:
            if key == 'fullScene': d['scene'] = ns
            else: d.setdefault('scenes', []).append(ns)
        inv = involved_npcs(req)
        echo = []
        for n in d.get('npcs') or []:
            nid = n.get('characterId')
            if nid in inv and n.get('currentMood') and n.get('currentMood') != moods.get(nid):
                echo.append({"id": nid, "mood": n['currentMood']}); moods[nid] = n['currentMood']
        d.pop('npcs', None)
        if echo: d['npcs'] = echo
        if variant == 'N2':
            fresh = [cards[i] for i in inv if i not in briefed and i in cards]
            briefed.update(i for i in inv if i in cards)
            if fresh: d['briefing'] = fresh
        out.append(o)
    return out

if __name__ == '__main__':
    base = [o for _, _, o in turns]
    trim = trimmed()
    n1, n2 = reshape(trim, 'N1'), reshape(trim, 'N2')
    tot = lambda xs: sum(J(x) for x in xs)
    print(f"{SESS}: {len(turns)} take_turn calls")
    print(f"  baseline            {tot(base):>7}")
    print(f"  trims (plan A-D)    {tot(trim):>7}")
    print(f"  N1 brief on arrival {tot(n1):>7}")
    print(f"  N2 brief on engage  {tot(n2):>7}")
    kinds = {}
    for (f, _, _), b, t, a, c in zip(turns, base, trim, n1, n2):
        k = f.split('_', 2)[2][:-5]; r = kinds.setdefault(k, [0, 0, 0, 0, 0]); r[0] += 1
        for i, x in enumerate((b, t, a, c)): r[i + 1] += J(x)
    print(f"\n  {'kind':16} {'n':>3} {'base':>6} {'trim':>6} {'N1':>6} {'N2':>6}  (avg)")
    for k, (n, *v) in kinds.items(): print(f"  {k:16} {n:>3} " + ' '.join(f"{x // n:>6}" for x in v))
    if len(sys.argv) > 2:
        which = {'N1': n1, 'N2': n2}[sys.argv[3] if len(sys.argv) > 3 else 'N2']
        print(json.dumps(which[int(sys.argv[2])], indent=1, ensure_ascii=False)[:5000])
