import sys, numpy as np
p=sys.argv[1]; k=float(sys.argv[2]) if len(sys.argv)>2 else 3
V=[];F=[]
for l in open(p):
    if l.startswith('v '): V.append(list(map(float,l.split()[1:4])))
    elif l.startswith('f '): F.append([int(x)-1 for x in l.split()[1:]])
V=np.array(V)
E=[max(np.linalg.norm(V[f[(i+1)%len(f)]]-V[f[i]]) for i in range(len(f))) for f in F]
med=np.median(E); out=[f for f,e in zip(F,E) if e>k*med]
with open(p+'.long.obj','w') as o:
    for v in V: o.write('v %r %r %r\n'%tuple(v))
    for f in out: o.write('f '+' '.join(str(i+1) for i in f[::-1])+'\n')
print(p,'long faces',len(out))
