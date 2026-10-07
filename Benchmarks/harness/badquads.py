import sys, numpy as np
from collections import defaultdict
def load(p):
    V=[];F=[]
    for l in open(p):
        if l.startswith('v '): V.append(list(map(float,l.split()[1:4])))
        elif l.startswith('f '): F.append([int(x.split('/')[0])-1 for x in l.split()[1:]])
    return np.array(V),F
for p in sys.argv[1:]:
    V,F=load(p)
    val=defaultdict(int); eu=defaultdict(int)
    for f in F:
        for k in range(len(f)):
            val[f[k]]+=1; eu[tuple(sorted((f[k],f[(k+1)%len(f)])))]+=1
    bnd=set()
    for e,c in eu.items():
        if c!=2: bnd.update(e)
    irr={v for v,c in val.items() if c!=4 and v not in bnd}
    # ring-1 of irregulars (vertices sharing a face)
    near=set()
    for f in F:
        if any(v in irr for v in f): near.update(f)
    mins=[]; touch=[]; touch2=[]; edgeRatio=[]
    for f in F:
        n=len(f); angs=[]; L=[]
        for k in range(n):
            a=V[f[k-1]]-V[f[k]]; b=V[f[(k+1)%n]]-V[f[k]]
            angs.append(np.degrees(np.arccos(np.clip(np.dot(a,b)/(np.linalg.norm(a)*np.linalg.norm(b)+1e-30),-1,1))))
            L.append(np.linalg.norm(b))
        mins.append(min(angs)); touch.append(any(v in irr for v in f)); touch2.append(any(v in near for v in f)); edgeRatio.append(max(L)/max(min(L),1e-12))
    mins=np.array(mins); touch=np.array(touch); touch2=np.array(touch2); er=np.array(edgeRatio)
    bad=mins<50
    print(f"{p}: bad(<50deg)={bad.sum()} ({100*bad.mean():.1f}%) | touching irregular: {touch[bad].mean()*100:.0f}% (of all faces {touch.mean()*100:.0f}%) | within 2 rings: {touch2[bad].mean()*100:.0f}% (all {touch2.mean()*100:.0f}%) | edge ratio>2 among bad {np.mean(er[bad]>2)*100:.0f}%")
    out=open(p+'.bad.obj','w')
    for v in V: out.write(f"v {v[0]} {v[1]} {v[2]}\n")
    for i,f in enumerate(F):
        if bad[i]: out.write('f '+' '.join(str(x+1) for x in f)+'\n')
