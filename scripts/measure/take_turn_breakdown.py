import os, json, sys, collections
HERE = os.path.dirname(os.path.abspath(__file__)); OUT = os.path.join(HERE, 'out')
SESS = sys.argv[1] if len(sys.argv) > 1 else 's1'
J = lambda v: len(json.dumps(v, separators=(',', ':'), ensure_ascii=False))

files = sorted(f for f in os.listdir(OUT) if f.endswith('.json') and f != 'log.json' and f'_{SESS}_' in f)
top = collections.Counter(); scene = collections.Counter(); npcf = collections.Counter(); nf = collections.Counter()
summ_lines = collections.Counter(); per_kind = collections.defaultdict(list); total = 0
env = collections.Counter()
for f in files:
    d = json.load(open(os.path.join(OUT, f)))
    if d['tool'] != 'take_turn': continue
    o = json.loads(d['text'])
    if not o.get('success'): continue
    total += len(d['text']); per_kind[f.split('_', 2)[2][:-5]].append(len(d['text']))
    for k, v in o.items():
        if k != 'data': env[k] += J(v)
    for k, v in o['data'].items():
        top[k] += J(v)
    fs = o['data'].get('fullScene')
    if fs:
        for k, v in fs.items(): scene[k] += J(v)
        for n in fs.get('presentNPCs') or fs.get('presentNpcs') or []:
            for k, v in n.items(): npcf[k] += J(v)
    for n in o['data'].get('npcs') or []:
        for k, v in n.items(): nf[k] += J(v)
    for s in o['data'].get('summary') or []:
        key = s.split(':')[0][:40] if not s.startswith('Event logged') else 'Event logged (id...)'
        summ_lines[key] += len(s)

def show(title, c, n=25):
    print(f'\n== {title} (sum {sum(c.values())})')
    for k, v in c.most_common(n): print(f'  {v:>7}  {k}')

print('take_turn total chars', total)
for k, v in per_kind.items(): print(f'  {k:16} n={len(v):2} sum={sum(v):6} each={v}')
show('envelope', env); show('data top-level', top); show('fullScene fields', scene)
show('fullScene NPC fields (all NPCs)', npcf); show('npcs[] (delta echo) fields', nf); show('summary line kinds', summ_lines, 20)
