# Scores every retopology of the reference asset on the same metrics, outside Unity.
#
# Unity triangulates on import (keepQuads: 0), so quad topology cannot be measured through it --
# which is why a competitor FBX or the reference retopology has to be scored here instead.
#
# Run:  "/c/Program Files/Blender Foundation/Blender 4.5/blender.exe" \
#         --background --factory-startup --python Benchmarks/reference/compare-outputs.py
#
# Reports, per mesh: face-size and valence histograms, irregular-vertex count (the number that
# matters -- it is topological and barely moves with resolution), edge-length CV (how much adaptive
# sizing is happening), quad shape (aspect, min interior angle, planarity), and quad-strip lengths.
#
# Results and what they mean: Benchmarks/quadremesher-measured.md

import bpy, sys, os, collections, math, statistics

ROOT = r"C:\Users\user\Quadremesh"

TARGETS = [
    ("QR-full",   os.path.join(ROOT, r"Assets\QuadRemesher\Output\20260515221044_54b8f8f4_Retopo.fbx")),
    ("QR-second", os.path.join(ROOT, r"Assets\QuadRemesher\Output\20260515221044_54b8f8f4_Retopo 1.fbx")),
    ("ref_low",   os.path.join(ROOT, r"Headless\Benchmarks\reference\ref_low.obj")),
    ("ours-Photo", os.path.join(ROOT, r"Headless\Benchmarks\reference\ours-Photogrammetry.obj")),
    ("ours-Char", os.path.join(ROOT, r"Headless\Benchmarks\reference\ours-Character.obj")),
    ("ours-Game", os.path.join(ROOT, r"Headless\Benchmarks\reference\ours-GameAsset.obj")),
    ("QR-default2", os.path.join(ROOT, r"Assets\QuadRemesher\Output\default_Retopo 1.fbx")),
    ("QR-default", os.path.join(ROOT, r"Assets\QuadRemesher\Output\default_Retopo.fbx")),
    ("QR-torus", os.path.join(ROOT, r"Assets\QuadRemesher\Output\torus_Retopo.fbx")),
    ("QR-torus2", os.path.join(ROOT, r"Assets\QuadRemesher\Output\torus_Retopo 1.fbx")),
]


def load(path):
    bpy.ops.wm.read_factory_settings(use_empty=True)
    ext = os.path.splitext(path)[1].lower()
    if ext == ".fbx":
        bpy.ops.import_scene.fbx(filepath=path)
    else:
        bpy.ops.wm.obj_import(filepath=path, validate_meshes=False)
    return [o for o in bpy.data.objects if o.type == "MESH"]


def analyse(label, ob):
    me = ob.data
    faces = [list(p.vertices) for p in me.polygons]
    co = [ob.matrix_world @ v.co for v in me.vertices]
    nf = len(faces)
    if nf == 0:
        print("  %s: no faces" % label)
        return

    sizes = collections.Counter(len(f) for f in faces)
    quads = sizes.get(4, 0)

    inc = collections.Counter()
    ecount = collections.Counter()
    for f in faces:
        n = len(f)
        for i, vi in enumerate(f):
            inc[vi] += 1
            a, b = f[i], f[(i + 1) % n]
            ecount[(min(a, b), max(a, b))] += 1
    used = len(inc)
    bnd_edges = set(e for e, c in ecount.items() if c == 1)
    nonman = sum(1 for c in ecount.values() if c > 2)
    bnd_verts = set()
    for a, b in bnd_edges:
        bnd_verts.add(a)
        bnd_verts.add(b)

    vhist = collections.Counter(inc.values())
    ihist = collections.Counter(v for k, v in inc.items() if k not in bnd_verts)
    interior = sum(ihist.values())
    ireg_all = used - vhist.get(4, 0)
    ireg_int = interior - ihist.get(4, 0)
    defect = sum(abs(v - 4) * c for v, c in ihist.items())

    elen = []
    for (a, b) in ecount:
        elen.append((co[a] - co[b]).length)
    emean = statistics.fmean(elen)
    ecv = statistics.pstdev(elen) / emean if emean > 0 else 0.0
    elen.sort()
    p05 = elen[int(0.05 * (len(elen) - 1))]
    p95 = elen[int(0.95 * (len(elen) - 1))]

    quadIdx = [i for i, f in enumerate(faces) if len(f) == 4]
    edge_to_he = collections.defaultdict(list)
    for qi in quadIdx:
        f = faces[qi]
        for i in range(4):
            a, b = f[i], f[(i + 1) % 4]
            edge_to_he[(min(a, b), max(a, b))].append(qi * 4 + i)
    twin = {}
    for e, hes in edge_to_he.items():
        if len(hes) == 2:
            twin[hes[0]] = hes[1]
            twin[hes[1]] = hes[0]

    visited = set()
    strips = []
    for qi in quadIdx:
        for i in range(4):
            h = qi * 4 + i
            if h in visited:
                continue
            chain = []
            cur = h
            closed = False
            while cur is not None and cur not in visited:
                visited.add(cur)
                opp = (cur // 4) * 4 + ((cur % 4) + 2) % 4
                visited.add(opp)
                chain.append(cur // 4)
                nxt = twin.get(opp)
                if nxt is None:
                    break
                if nxt == h:
                    closed = True
                    break
                cur = nxt
            cur = twin.get(h)
            while (not closed) and cur is not None and cur not in visited:
                visited.add(cur)
                opp = (cur // 4) * 4 + ((cur % 4) + 2) % 4
                visited.add(opp)
                chain.append(cur // 4)
                cur = twin.get(opp)
            if chain:
                strips.append((len(chain), closed))

    slen = sorted((s for s, _ in strips), reverse=True)
    nclosed = sum(1 for _, c in strips if c)
    smean = statistics.fmean(slen) if slen else 0
    smed = statistics.median(slen) if slen else 0
    long_q = sum(s for s, _ in strips if s >= 10)
    frac_long = long_q / max(1, quads)

    aspects, minangles, planar = [], [], []
    for f in faces:
        if len(f) != 4:
            continue
        p = [co[i] for i in f]
        e = [(p[(i + 1) % 4] - p[i]).length for i in range(4)]
        if min(e) <= 1e-12:
            continue
        aspects.append(max(e) / min(e))
        ang = []
        for i in range(4):
            u = p[(i - 1) % 4] - p[i]
            w = p[(i + 1) % 4] - p[i]
            if u.length < 1e-12 or w.length < 1e-12:
                continue
            ang.append(math.degrees(math.acos(max(-1.0, min(1.0, u.normalized().dot(w.normalized()))))))
        if ang:
            minangles.append(min(ang))
        d1 = p[2] - p[0]
        d2 = p[3] - p[1]
        n = d1.cross(d2)
        if n.length > 1e-12:
            n.normalize()
            hh = abs((p[1] - p[0]).dot(n))
            planar.append(hh / max(1e-12, statistics.fmean(e)))

    dims = ob.dimensions
    diag = math.sqrt(dims.x ** 2 + dims.y ** 2 + dims.z ** 2)

    print("== %s [%s]" % (label, ob.name))
    print("   verts=%d used=%d faces=%d edges=%d  sizes=%s" % (
        len(me.vertices), used, nf, len(ecount), dict(sorted(sizes.items()))))
    print("   quadFrac=%.4f  boundaryEdges=%d  nonManifoldEdges=%d  bndVerts=%d" % (
        quads / nf, len(bnd_edges), nonman, len(bnd_verts)))
    print("   valence(all)=%s" % dict(sorted(vhist.items())))
    print("   valence(interior)=%s" % dict(sorted(ihist.items())))
    print("   irregular: all=%.1f%% (%d/%d)  interior=%.1f%% (%d/%d)  defect=%.3f/vert" % (
        100 * ireg_all / max(1, used), ireg_all, used,
        100 * ireg_int / max(1, interior), ireg_int, interior,
        defect / max(1, interior)))
    print("   edgeLen: mean=%.5f cv=%.3f p05=%.5f p95=%.5f  p95/p05=%.2f  bboxDiag=%.4f  mean/diag=%.5f" % (
        emean, ecv, p05, p95, p95 / max(1e-12, p05), diag, emean / max(1e-12, diag)))
    print("   strips: n=%d closed=%d mean=%.1f median=%.1f max=%s  quadsInStripsGE10=%.1f%%" % (
        len(strips), nclosed, smean, smed, slen[:6], 100 * frac_long))
    if aspects:
        aspects.sort()
        minangles.sort()
        planar.sort()
        print("   quadShape: aspect mean=%.2f p95=%.2f  minAngle mean=%.1f p05=%.1f  planarity mean=%.4f p95=%.4f" % (
            statistics.fmean(aspects), aspects[int(0.95 * (len(aspects) - 1))],
            statistics.fmean(minangles), minangles[int(0.05 * (len(minangles) - 1))],
            statistics.fmean(planar), planar[int(0.95 * (len(planar) - 1))]))
    print("   uvLayers=%s  materials=%s  dims=(%.3f,%.3f,%.3f)" % (
        [l.name for l in me.uv_layers], [m.name if m else None for m in me.materials],
        dims.x, dims.y, dims.z))


for label, path in TARGETS:
    if not os.path.exists(path):
        print("== %s MISSING %s" % (label, path))
        continue
    try:
        obs = load(path)
    except Exception as ex:
        print("== %s LOAD FAILED %s" % (label, ex))
        continue
    for ob in obs:
        analyse(label, ob)
    print()
