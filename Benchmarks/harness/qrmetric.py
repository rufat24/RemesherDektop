import sys, numpy as np
def load(p):
    V=[];F=[]
    for l in open(p):
        if l.startswith('v '): V.append(list(map(float,l.split()[1:4])))
        elif l.startswith('f '): F.append([int(x.split('/')[0])-1 for x in l.split()[1:]])
    return np.array(V),F
for p in sys.argv[1:]:
    V,F=load(p)
    mins=[];edges={}
    for f in F:
        n=len(f); angs=[]
        for k in range(n):
            a=V[f[k-1]]-V[f[k]]; b=V[f[(k+1)%n]]-V[f[k]]
            c=np.dot(a,b)/(np.linalg.norm(a)*np.linalg.norm(b)+1e-30)
            angs.append(np.degrees(np.arccos(np.clip(c,-1,1))))
            e=(min(f[k],f[(k+1)%n]),max(f[k],f[(k+1)%n])); edges[e]=np.linalg.norm(V[f[k]]-V[f[(k+1)%n]])
        mins.append(min(angs))
    mins=np.array(mins); L=np.array(list(edges.values()))
    print(f"{p}: faces={len(F)} minAngle p05={np.percentile(mins,5):.1f} p50={np.percentile(mins,50):.1f} mean={mins.mean():.1f} | edgeCV={L.std()/L.mean():.3f}")
