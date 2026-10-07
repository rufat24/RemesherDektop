import bpy, sys, collections

path = sys.argv[sys.argv.index('--') + 1]

bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.import_scene.fbx(filepath=path)

for ob in [o for o in bpy.data.objects if o.type == 'MESH']:
    me = ob.data
    sizes = collections.Counter(len(p.vertices) for p in me.polygons)
    nf = len(me.polygons)
    quads = sizes.get(4, 0)
    tris = sizes.get(3, 0)
    # valence
    val = collections.Counter()
    for v in me.vertices:
        val[0] += 0
    inc = collections.Counter()
    for p in me.polygons:
        for vi in p.vertices:
            inc[vi] += 1
    vhist = collections.Counter(inc.values())
    # boundary edges: edges used by exactly one face
    ecount = collections.Counter()
    for p in me.polygons:
        vs = list(p.vertices)
        for i in range(len(vs)):
            a, b = vs[i], vs[(i + 1) % len(vs)]
            ecount[(min(a, b), max(a, b))] += 1
    bnd = sum(1 for c in ecount.values() if c == 1)
    nonman = sum(1 for c in ecount.values() if c > 2)
    dims = ob.dimensions
    print("OBJ", ob.name)
    print("  verts=%d faces=%d edges=%d" % (len(me.vertices), nf, len(ecount)))
    print("  quads=%d (%.4f) tris=%d ngons=%d" % (quads, quads / max(1, nf), tris, nf - quads - tris))
    print("  faceSizes=%s" % dict(sorted(sizes.items())))
    print("  valence=%s" % dict(sorted(vhist.items())))
    print("  valenceDefect=%d" % sum(abs(v - 4) * c for v, c in vhist.items()))
    print("  boundaryEdges=%d nonManifoldEdges=%d" % (bnd, nonman))
    print("  uvLayers=%s" % [l.name for l in me.uv_layers])
    print("  dims=(%.4f, %.4f, %.4f)" % (dims.x, dims.y, dims.z))
    print("  materials=%s" % [m.name if m else None for m in me.materials])
