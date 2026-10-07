# QuadriFlow (Blender's built-in) on the cases where we know the floor, plus the real scan.
#
# The torus is the decisive one: chi = 0, so zero irregular vertices are attainable, and our
# extractor produces 283. If QuadriFlow reaches the floor here, the architecture difference is
# the whole story and the rewrite is justified by a measurement rather than by a citation.

import bpy, bmesh, os, collections, math, statistics

ROOT = r"C:\Users\user\Quadremesh"


def score(label, ob, floor=None):
    me = ob.data
    faces = [list(p.vertices) for p in me.polygons]
    co = [ob.matrix_world @ v.co for v in me.vertices]
    nf = len(faces)
    if nf == 0:
        print("== %s : NO FACES" % label)
        return
    sizes = collections.Counter(len(f) for f in faces)
    quads = sizes.get(4, 0)
    inc = collections.Counter()
    ec = collections.Counter()
    for f in faces:
        n = len(f)
        for i, vi in enumerate(f):
            inc[vi] += 1
            a, b = f[i], f[(i + 1) % n]
            ec[(min(a, b), max(a, b))] += 1
    bnd = set(e for e, c in ec.items() if c == 1)
    nonman = sum(1 for c in ec.values() if c > 2)
    bv = set()
    for a, b in bnd:
        bv.add(a); bv.add(b)
    ihist = collections.Counter(v for k, v in inc.items() if k not in bv)
    interior = sum(ihist.values())
    ireg = interior - ihist.get(4, 0)
    defect = sum(abs(v - 4) * c for v, c in ihist.items())
    minang = []
    for f in faces:
        if len(f) != 4:
            continue
        p = [co[i] for i in f]
        ang = []
        for i in range(4):
            u = p[(i - 1) % 4] - p[i]; w = p[(i + 1) % 4] - p[i]
            if u.length < 1e-12 or w.length < 1e-12:
                continue
            ang.append(math.degrees(math.acos(max(-1.0, min(1.0, u.normalized().dot(w.normalized()))))))
        if ang:
            minang.append(min(ang))
    minang.sort()
    print("== %s" % label)
    print("   faces=%d sizes=%s quadFrac=%.4f  bnd=%d nonman=%d" % (
        nf, dict(sorted(sizes.items())), quads / nf, len(bnd), nonman))
    print("   valence=%s" % dict(sorted(ihist.items())))
    print("   IRREGULAR=%d / %d = %.1f%%  defect=%d%s" % (
        ireg, interior, 100 * ireg / max(1, interior), defect,
        ("   [floor=%d]" % floor) if floor is not None else ""))
    if minang:
        print("   minAngle mean=%.1f p05=%.1f" % (
            statistics.fmean(minang), minang[int(0.05 * (len(minang) - 1))]))


def nonmanifold_report(ob):
    bm = bmesh.new()
    bm.from_mesh(ob.data)
    ve = sum(1 for v in bm.verts if not v.is_manifold)
    ee = sum(1 for e in bm.edges if not e.is_manifold)
    ce = sum(1 for e in bm.edges if not e.is_contiguous)
    bm.free()
    print("   non-manifold: verts=%d edges=%d   non-contiguous(winding) edges=%d" % (ve, ee, ce))
    return ve, ee, ce


def run_quadriflow(ob, target):
    bpy.context.view_layer.objects.active = ob
    for o in bpy.data.objects:
        o.select_set(False)
    ob.select_set(True)
    try:
        bpy.ops.object.quadriflow_remesh(
            use_mesh_symmetry=False, use_preserve_sharp=False,
            use_preserve_boundary=False, preserve_attributes=False,
            smooth_normals=False, mode="FACES", target_faces=target, seed=0)
        return bpy.context.view_layer.objects.active
    except Exception as ex:
        print("   QUADRIFLOW THREW: %s: %s" % (type(ex).__name__, ex))
        return None


# ---------------------------------------------------------------- torus, floor 0
bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.mesh.primitive_torus_add(major_segments=128, minor_segments=64,
                                 major_radius=1.0, minor_radius=0.35)
ob = bpy.context.view_layer.objects.active
bpy.ops.object.mode_set(mode="EDIT")
bpy.ops.mesh.select_all(action="SELECT")
bpy.ops.mesh.quads_convert_to_tris()
bpy.ops.object.mode_set(mode="OBJECT")
print("torus input: %d verts %d tris" % (len(ob.data.vertices), len(ob.data.polygons)))
nonmanifold_report(ob)
out = run_quadriflow(ob, 2000)
if out:
    score("QUADRIFLOW torus @2000", out, floor=0)

# ---------------------------------------------------------------- icosphere, floor 8
bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.mesh.primitive_ico_sphere_add(subdivisions=6, radius=1.0)
ob = bpy.context.view_layer.objects.active
print("icosphere input: %d verts %d tris" % (len(ob.data.vertices), len(ob.data.polygons)))
out = run_quadriflow(ob, 2000)
if out:
    score("QUADRIFLOW icosphere @2000", out, floor=8)

# ---------------------------------------------------------------- the real scan
SRC = os.path.join(ROOT, r"Benchmarks\reference\ref_high.obj")
bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.wm.obj_import(filepath=SRC, validate_meshes=True)
ob = [o for o in bpy.data.objects if o.type == "MESH"][0]
print("scan input: %d verts %d polys" % (len(ob.data.vertices), len(ob.data.polygons)))
bpy.context.view_layer.objects.active = ob
ob.select_set(True)
nonmanifold_report(ob)

# Decimate to the same budget our own pipeline hands the solver, which also tends to remove the
# pathological vertices Blender's manifold check rejects.
mod = ob.modifiers.new("dec", "DECIMATE")
mod.decimate_type = "COLLAPSE"
mod.ratio = 54740.0 / 1500000.0
bpy.ops.object.modifier_apply(modifier="dec")
print("after decimate: %d verts %d polys" % (len(ob.data.vertices), len(ob.data.polygons)))

bpy.ops.object.mode_set(mode="EDIT")
bpy.ops.mesh.select_all(action="SELECT")
bpy.ops.mesh.remove_doubles(threshold=1e-6)
bpy.ops.mesh.delete_loose()
bpy.ops.mesh.normals_make_consistent(inside=False)
bpy.ops.object.mode_set(mode="OBJECT")
nonmanifold_report(ob)

out = run_quadriflow(ob, 5474)
if out:
    score("QUADRIFLOW scan @5474", out)
    bpy.ops.wm.obj_export(
        filepath=os.path.join(ROOT, r"Benchmarks\reference\quadriflow-scan-5474.obj"),
        export_selected_objects=True, export_materials=False,
        export_uv=False, export_normals=False)
