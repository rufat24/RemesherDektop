import sys
from collections import defaultdict
for p in sys.argv[1:]:
    F=[[int(x.split('/')[0])-1 for x in l.split()[1:]] for l in open(p) if l.startswith('f ')]
    eu=defaultdict(int)
    for f in F:
        for k in range(len(f)): eu[tuple(sorted((f[k],f[(k+1)%len(f)])))]+=1
    adj=defaultdict(list)
    for (a,b),c in eu.items():
        if c==1: adj[a].append(b); adj[b].append(a)
    seen=set(); loops=[]
    for s in adj:
        if s in seen: continue
        st=[s]; n=0; seen.add(s)
        while st:
            v=st.pop(); n+=1
            for w in adj[v]:
                if w not in seen: seen.add(w); st.append(w)
        loops.append(n)
    loops.sort(reverse=True)
    print(p, 'loops', len(loops), 'sizes', loops[:12])
