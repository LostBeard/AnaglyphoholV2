# Who calls <target>: time per caller chain (depth N) in a .cpuprofile.
# usage: python cpuprof-callers.py <file.cpuprofile> <functionName substring> [depth=3]
import json, sys
from collections import defaultdict
p=json.load(open(sys.argv[1],encoding='utf-8')); target=sys.argv[2]; depth=int(sys.argv[3]) if len(sys.argv)>3 else 3
nodes={n['id']:n for n in p['nodes']}; parent={}
for n in p['nodes']:
    for c in n.get('children',[]): parent[c]=n['id']
fn=lambda i: nodes[i]['callFrame'].get('functionName') or '(anon)'
agg=defaultdict(float)
for s,d in zip(p['samples'], (p['timeDeltas'][1:]+[0])):
    cur=s; chain=[]
    while cur is not None:
        if target in fn(cur):
            up=parent.get(cur); names=[]
            for _ in range(depth):
                if up is None: break
                names.append(fn(up)[-70:]); up=parent.get(up)
            agg[' <- '.join(names)]+=d; break
        cur=parent.get(cur)
for k,v in sorted(agg.items(), key=lambda kv:-kv[1])[:8]: print(f"{v/1000:8.1f} ms  {k}")
