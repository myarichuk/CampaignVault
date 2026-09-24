import json, sys, urllib.request

import os; base = os.environ.get('MCP_BASE', 'http://localhost:5399'); path = sys.argv[1] if len(sys.argv) > 1 else '/'
H = {'Content-Type': 'application/json', 'Accept': 'application/json, text/event-stream'}

def post(body, sid=None):
    h = dict(H)
    if sid: h['Mcp-Session-Id'] = sid
    r = urllib.request.urlopen(urllib.request.Request(base + path, json.dumps(body).encode(), h), timeout=60)
    raw = r.read().decode()
    data = [l[6:] for l in raw.splitlines() if l.startswith('data: ')]
    return r.headers.get('Mcp-Session-Id'), (json.loads(data[-1]) if data else None)

sid, _ = post({"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"m","version":"1"}}})
post({"jsonrpc":"2.0","method":"notifications/initialized"}, sid)
_i = [10]

def call(name, args):
    _i[0] += 1
    _, res = post({"jsonrpc":"2.0","id":_i[0],"method":"tools/call","params":{"name":name,"arguments":args}}, sid)
    if 'error' in res: return None, json.dumps(res['error'])
    text = res['result']['content'][0]['text']
    try: return json.loads(text), text
    except Exception: return None, text

def breakdown(obj, depth=1, prefix='', minsize=150):
    rows = []
    if isinstance(obj, dict):
        for k, v in obj.items():
            s = len(json.dumps(v, separators=(',', ':'), ensure_ascii=False))
            if s >= minsize:
                rows.append((prefix + k, s))
                if depth > 1: rows += breakdown(v, depth - 1, prefix + k + '.', minsize)
    elif isinstance(obj, list) and obj:
        s0 = len(json.dumps(obj[0], separators=(',', ':'), ensure_ascii=False))
        rows.append((prefix + '[0]', s0))
        if depth > 1: rows += breakdown(obj[0], depth - 1, prefix + '[0].', minsize)
    return rows

def report(label, name, args, depth=3):
    obj, text = call(name, args)
    print(f'\n=== {label}: {len(text)} chars')
    if obj is None: print(text[:400]); return obj
    for k, s in breakdown(obj, depth): print(f'  {s:>7}  {k}')
    return obj

if __name__ == '__main__':
    exec(open(sys.argv[2]).read()) if len(sys.argv) > 2 else None
