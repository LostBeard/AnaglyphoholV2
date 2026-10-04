"""Summarize a .cpuprofile: top self time and top inclusive time per function name.
usage: python cpuprof.py <file.cpuprofile> [topN=40] [filter-substring]"""
import json, sys
from collections import defaultdict

p = json.load(open(sys.argv[1], encoding='utf-8'))
top = int(sys.argv[2]) if len(sys.argv) > 2 else 40
flt = sys.argv[3] if len(sys.argv) > 3 else None
nodes = {n['id']: n for n in p['nodes']}
parent = {}
for n in p['nodes']:
    for c in n.get('children', []):
        parent[c] = n['id']
# per-sample time deltas
deltas = p.get('timeDeltas') or []
samples = p['samples']
total_us = sum(deltas)
selfc = defaultdict(float)
incl = defaultdict(float)
def name(n):
    cf = n['callFrame']
    fn = cf.get('functionName') or '(anon)'
    url = cf.get('url', '')
    return fn if not url else f"{fn} [{url.rsplit('/', 1)[-1][:40]}]"
for s, d in zip(samples, deltas[1:] + [0]):
    n = nodes[s]
    selfc[name(n)] += d
    seen = set()
    cur = s
    while cur is not None:
        nm = name(nodes[cur])
        if nm not in seen:
            incl[nm] += d
            seen.add(nm)
        cur = parent.get(cur)
print(f"total {total_us/1000:.0f} ms, {len(samples)} samples")
print("== SELF ==")
for k, v in sorted(selfc.items(), key=lambda kv: -kv[1])[:top]:
    if flt and flt not in k: continue
    print(f"{v/1000:9.1f} ms {100*v/total_us:5.1f}%  {k[:150]}")
print("== INCLUSIVE ==")
for k, v in sorted(incl.items(), key=lambda kv: -kv[1])[:top]:
    if flt and flt not in k: continue
    print(f"{v/1000:9.1f} ms {100*v/total_us:5.1f}%  {k[:150]}")
