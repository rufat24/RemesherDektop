# Export QuadRemesher's FBX output to OBJ, so it can be scored by the same code as ours.
#
# WHY THIS DETOUR EXISTS. Unity imports FBX with keepQuads off, so quad topology cannot be read back
# through Unity at all - every competitor mesh arrives triangulated and its face structure is gone.
# Blender preserves it, and OBJ carries it, so this is the only path that lets one measurement
# score both arms on equal terms.
#
# Geometric fidelity would survive triangulation (the surface is the same), but face-level measures -
# planarity, quad angles, valence - would not, and those are exactly what the visual complaints are
# about. So the conversion is worth the step.
#
# Run:
#   "/c/Program Files/Blender Foundation/Blender 4.5/blender.exe" --background --factory-startup \
#       --python Benchmarks/reference/export-competitor.py -- <input.fbx> <output.obj>

import sys
import bpy

argv = sys.argv
argv = argv[argv.index("--") + 1:] if "--" in argv else []

if len(argv) < 2:
    print("MEASURE_ERROR: need <input.fbx> <output.obj>")
    sys.exit(1)

source, destination = argv[0], argv[1]

# --factory-startup still opens with the default cube, and it would be written into the OBJ as if
# it were part of the competitor's output.
bpy.ops.object.select_all(action="SELECT")
bpy.ops.object.delete(use_global=False)

bpy.ops.import_scene.fbx(filepath=source)

meshes = [o for o in bpy.context.scene.objects if o.type == "MESH"]
if not meshes:
    print("MEASURE_ERROR: no mesh in " + source)
    sys.exit(1)

total_verts = 0
total_faces = 0
quads = 0
tris = 0
ngons = 0

for obj in meshes:
    mesh = obj.data
    total_verts += len(mesh.vertices)
    total_faces += len(mesh.polygons)
    for polygon in mesh.polygons:
        n = len(polygon.vertices)
        if n == 4:
            quads += 1
        elif n == 3:
            tris += 1
        else:
            ngons += 1

print("MEASURE objects=%d verts=%d faces=%d quads=%d tris=%d ngons=%d quadFrac=%.4f"
      % (len(meshes), total_verts, total_faces, quads, tris, ngons,
         quads / max(1, total_faces)))

bpy.ops.object.select_all(action="SELECT")

# Explicitly no triangulation and no modifier application: the point is to carry the topology
# through unchanged. Blender's OBJ exporter defaults are not all safe here, so each is stated.
bpy.ops.wm.obj_export(
    filepath=destination,
    export_selected_objects=True,
    export_triangulated_mesh=False,
    apply_modifiers=False,
    export_materials=False,
    export_uv=True,
    export_normals=True,
)

print("MEASURE wrote " + destination)
