import sys, collections

def load(path):
    verts = 0
    faces = []
    with open(path) as f:
        for line in f:
            if line.startswith('v '): verts += 1
            elif line.startswith('f '):
                faces.append([int(t.split('/')[0]) - 1 for t in line.split()[1:]])
    return verts, faces

for path in sys.argv[1:]:
    verts, faces = load(path)
    sizes = collections.Counter(len(f) for f in faces)
    nf = len(faces)
    inc = collections.Counter()
    ecount = collections.Counter()
    for f in faces:
        for i, vi in enumerate(f):
            inc[vi] += 1
            a, b = f[i], f[(i + 1) % len(f)]
            ecount[(min(a, b), max(a, b))] += 1
    vhist = collections.Counter(inc.values())
    used = len(inc)
    bnd = sum(1 for c in ecount.values() if c == 1)
    nonman = sum(1 for c in ecount.values() if c > 2)
    reg = vhist.get(4, 0)
    defect = sum(abs(v - 4) * c for v, c in vhist.items())
    print(path.split('/')[-1])
    print("  verts=%d used=%d faces=%d edges=%d" % (verts, used, nf, len(ecount)))
    print("  quadFrac=%.4f  sizes=%s" % (sizes.get(4,0)/max(1,nf), dict(sorted(sizes.items()))))
    print("  valence=%s" % dict(sorted(vhist.items())))
    print("  valence4=%.1f%%  irregular=%.1f%%  defect=%d (%.3f/vert)"
          % (100*reg/max(1,used), 100*(used-reg)/max(1,used), defect, defect/max(1,used)))
    print("  boundaryEdges=%d nonManifoldEdges=%d" % (bnd, nonman))
