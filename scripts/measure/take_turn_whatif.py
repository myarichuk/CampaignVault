"""Offline what-if: apply each proposed take_turn trim to the captured scratch responses and measure.
Levers are applied cumulatively in the listed order; each row shows its marginal saving."""
import os, json, sys, copy, re
HERE = os.path.dirname(os.path.abspath(__file__)); OUT = os.path.join(HERE, 'out')
SESS = sys.argv[1] if len(sys.argv) > 1 else 's1'
J = lambda v: len(json.dumps(v, separators=(',', ':'), ensure_ascii=False))

turns = []
for f in sorted(os.listdir(OUT)):
    if f == 'log.json' or f'_{SESS}_' not in f: continue
    d = json.load(open(os.path.join(OUT, f)))
    if d['tool'] != 'take_turn': continue
    o = json.loads(d['text'])
    if not o.get('success'): continue
    # harness noise: duplicate eventId warnings come from the script, not the server
    o['data']['summary'] = [s for s in o['data'].get('summary') or [] if 'already exists; generated a new ID' not in s]
    turns.append((f, d['args'], o))

def npc_lists(d):
    out = []
    if d.get('fullScene'): out.append(d['fullScene'])
    out += d.get('scenes') or []
    return out

PARTY = {"chars/tamsin", "chars/bram"}

def l_npc_projection(d, st):
    """Per-NPC projection in scenes: drop needDescriptors, engine internals, false defaults, restated summary;
    systemStats -> ac/level; needs -> ints, only non-zero; tension -> int; items -> names."""
    for sc in npc_lists(d):
        for n in sc.get('presentNPCs') or []:
            n.pop('needDescriptors', None); n.pop('behavioralSummary', None)
            for k in ('keepAlive', 'isPc', 'isPartyCompanion', 'notesTruncated'):
                if n.get(k) is False: n.pop(k)
            ss = n.get('systemStats')
            if ss: n['systemStats'] = {k: ss[k] for k in ('armorClass', 'level') if k in ss}
            if n.get('knownNeeds'):
                n['knownNeeds'] = {k: round(v) for k, v in n['knownNeeds'].items() if round(v) >= 1}
            if 'behavioralTension' in n: n['behavioralTension'] = round(n['behavioralTension'])
            for key in ('equippedItems', 'carriedItems'):
                if n.get(key): n[key] = [i['name'] for i in n[key]]

def l_scene_chrome(d, st):
    """Scene chrome: location defaults, exits -> id+hours, seededNpcIds, lastKnownTravel."""
    for sc in npc_lists(d):
        loc = sc.get('location') or {}
        for k, dv in (('dangerModifier', 0), ('descriptionTruncated', False)):
            if loc.get(k) == dv: loc.pop(k)
        for e in loc.get('exits') or []:
            if e.get('oneWay') is False: e.pop('oneWay')
            e.pop('description', None)
        sc.pop('seededNpcIds', None); sc.pop('lastKnownTravel', None)
        if sc.get('isLocationAnchored') is True: sc.pop('isLocationAnchored')

def l_events(d, st):
    """recentEventSummaries: drop engine travel events, -> 'day N: summary' strings, cap 4."""
    for sc in npc_lists(d):
        evs = [e for e in sc.get('recentEventSummaries') or [] if e.get('category') != 'Travel']
        if 'recentEventSummaries' in sc:
            sc['recentEventSummaries'] = [f"d{e.get('dayLogged')}: {e['summary']}" for e in evs[:4]]

def l_party_out_of_scene(d, st):
    """Party members don't need a full NPC card in every scene: the party block / fingerprint covers them."""
    for sc in npc_lists(d):
        sc['presentNPCs'] = [n if n.get('id') not in PARTY else {"id": n['id'], "name": n['name']} for n in sc.get('presentNPCs') or []]

def l_seen_npcs(d, st):
    """Session seen-set: an NPC card identical to the one already sent this session collapses to id+name+mood."""
    seen = st.setdefault('seen', {})
    for sc in npc_lists(d):
        new = []
        for n in sc.get('presentNPCs') or []:
            key = n.get('id'); body = json.dumps({k: v for k, v in n.items() if k not in ('knownNeeds', 'behavioralTension')}, sort_keys=True)
            if key in seen and seen[key] == body:
                new.append({"id": key, "name": n.get('name'), "seen": True, **({"currentMood": n['currentMood']} if n.get('currentMood') else {})})
            else:
                seen[key] = body; new.append(n)
        sc['presentNPCs'] = new

def l_pressure(d, st):
    """scenePressure: per-character 'looks vulnerable' templates (~1k each, canned bloody/wanted example)
    merged into one line naming everyone; example moves to lookup help."""
    for sc in npc_lists(d):
        ps = sc.get('scenePressure') or []
        vul = [re.match(r'SUGGESTION: (.*?) looks vulnerable', p).group(1) for p in ps if 'looks vulnerable' in p]
        rest = [p for p in ps if 'looks vulnerable' not in p]
        if vul:
            rest.append(f"SUGGESTION: {', '.join(vul)} look vulnerable here; the crowd may react (scene_interrupt_check). Example: lookup kind=help topic=scene-pressure.")
        if 'scenePressure' in sc: sc['scenePressure'] = rest

def l_memories(d, st):
    """relevantMemories per scene NPC: {topic, details, day} only, max 2."""
    for sc in npc_lists(d):
        for n in sc.get('presentNPCs') or []:
            if n.get('relevantMemories'):
                n['relevantMemories'] = [f"d{m.get('dayAcquired')} {m.get('topic')}: {m.get('details')}" for m in n['relevantMemories'][:2]]

def l_rest_drift(d, st):
    """HP-only fingerprint drift (rest/heal) sends partyDelta instead of a full scenes[] reseed of an unchanged room."""
    if d.get('scenes') and not d.get('fullScene'):
        d.pop('scenes'); d['partyDelta'] = [{"entityId": "chars/tamsin", "hp": "21/21"}, {"entityId": "chars/bram", "hp": "24/24"}]

def l_summary(d, st):
    """Summary: novelty hints only for model-authored events (not engine travel events), one per turn, short form;
    'Event logged (id: X)' dropped when X is in committedIds."""
    out, hinted = [], False
    ids = set(d.get('committedIds') or [])
    for s in d.get('summary') or []:
        m = re.match(r'Event logged \(id: (.*)\)\.', s)
        if m and m.group(1) in ids: continue
        if s.startswith('Hint: "Travel:'): continue
        if s.startswith('Hint: '):
            if hinted: continue
            hinted = True
            q = re.match(r'Hint: "(.*)" reads as novel', s)
            s = f'Novel event ("{(q.group(1) if q else "")[:40]}"): consider Important knowledge_update / plot_thread.'
        out.append(s)
    d['summary'] = out
    if not d['summary']: d.pop('summary')

def l_echo_noise(d, st):
    """Echo noise: npcs[] behavioralSummary + float tension + full item objects; knownCharacterIds; rateLimit when high."""
    for n in d.get('npcs') or []:
        n.pop('behavioralSummary', None)
        ini = n.get('initiative')
        if ini and 'behavioralTension' in ini: ini['behavioralTension'] = round(ini['behavioralTension'])
        if n.get('equipped'): n['equipped'] = [i['name'] for i in n['equipped']]
    d.pop('knownCharacterIds', None)
    if (d.get('rateLimitTokensRemaining') or 0) > 10: d.pop('rateLimitTokensRemaining')

def l_fingerprint_hash(d, st):
    """partyFingerprint -> 8-char hash."""
    if d.get('partyFingerprint'): d['partyFingerprint'] = 'a1b2c3d4'

def l_envelope(o):
    o.pop('tokensEst', None)
    if o.get('summary', '').startswith('World updated with'): o.pop('summary')

LEVERS = [
    ("A1 NPC card projection (scene)", l_npc_projection),
    ("A2 seen-this-session NPC stubs", l_seen_npcs),
    ("A3 party members as stubs in scenes", l_party_out_of_scene),
    ("A4 scene events: no travel, compact, cap 4", l_events),
    ("A5 scene chrome (defaults, exits, seeded ids)", l_scene_chrome),
    ("A6 scenePressure: merge per-character 'vulnerable' templates", l_pressure),
    ("A7 NPC relevantMemories compact, max 2", l_memories),
    ("B1 HP-only drift -> partyDelta, not scene reseed", l_rest_drift),
    ("C1 summary: travel hints, dup 'Event logged', 1 short hint", l_summary),
    ("C2 echo noise (npcs[] summary/floats, knownIds, rateLimit)", l_echo_noise),
    ("C3 fingerprint -> short hash", l_fingerprint_hash),
]

def total(objs): return sum(J(o) for o in objs)

base = [copy.deepcopy(o) for _, _, o in turns]
for o in base: pass
cur = [copy.deepcopy(o) for o in base]
print(f"{SESS}: {len(turns)} take_turn calls, baseline {total(base)} chars (compact JSON)\n")
prev = total(cur)
print(f"{'lever':62} {'saves':>7} {'after':>7}")
for name, fn in LEVERS:
    st = {}
    for o in cur: fn(o['data'], st)
    t = total(cur); print(f"{name:62} {prev - t:>7} {t:>7}"); prev = t
for o in cur: l_envelope(o)
t = total(cur); print(f"{'D1 envelope (tokensEst, generic summary)':62} {prev - t:>7} {t:>7}")
print(f"\nall levers: {total(base)} -> {t} ({100 * (total(base) - t) // total(base)}% less)")

kinds = {}
for (f, _, _), b, c in zip(turns, base, cur):
    k = f.split('_', 2)[2][:-5]; kinds.setdefault(k, [0, 0, 0]); kinds[k][0] += 1; kinds[k][1] += J(b); kinds[k][2] += J(c)
print(f"\n{'kind':16} {'n':>3} {'before':>7} {'after':>7}")
for k, (n, b, c) in kinds.items(): print(f"{k:16} {n:>3} {b // n:>7} {c // n:>7}   (avg)")
if len(sys.argv) > 2:
    idx = int(sys.argv[2]); print(json.dumps(cur[idx], indent=1, ensure_ascii=False)[:6000])
