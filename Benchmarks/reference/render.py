# Render a mesh the way a person looks at it, so the eye can be used as an instrument.
#
# WHY THIS EXISTS. Every geometric measurement in this project said our output matched or beat the
# competitor's while the user, looking at both in a viewport, said ours was plainly worse. That
# disagreement is not settled by adding another number - it is settled by looking, under identical
# framing, shading and light, at meshes that differ only in which tool made them.
#
# SMOOTH SHADING WITH THE MESH'S OWN NORMALS, because the complaint is about lighting and not about
# silhouettes. Flat shading would show the quads and hide exactly the defect being argued about.
#
# MATCHED FRAMING. The meshes come out of different tools at different scales (the competitor writes
# centimetres, this project writes metres), so each is normalised to a unit bounding box and the
# camera is placed from that box rather than from world coordinates. Otherwise "theirs looks
# smoother" can just mean "theirs is further away".
#
#   blender --background --factory-startup --python render.py -- out.png in.obj [view] [label]
#
# view: torso (default), head, hand, full

import bpy, sys, math
from mathutils import Vector


def clear():
    bpy.ops.wm.read_factory_settings(use_empty=True)


def main(out, path, view="torso"):
    clear()
    bpy.ops.wm.obj_import(filepath=path)

    obj = [o for o in bpy.context.scene.objects if o.type == "MESH"][0]

    # Normalise into a unit box centred on the origin, so framing is tool-independent.
    bb = [obj.matrix_world @ Vector(c) for c in obj.bound_box]
    lo = Vector((min(v.x for v in bb), min(v.y for v in bb), min(v.z for v in bb)))
    hi = Vector((max(v.x for v in bb), max(v.y for v in bb), max(v.z for v in bb)))
    size = hi - lo
    scale = 1.0 / max(size.x, size.y, size.z)

    obj.scale = (scale, scale, scale)
    bpy.context.view_layer.update()
    obj.location = -((lo + hi) * 0.5) * scale
    bpy.context.view_layer.update()

    # The mesh's own normals, smoothed. No auto-smooth angle: an angle threshold would hide a fold
    # by drawing it as a crease, and folds are the thing being looked for.
    for p in obj.data.polygons:
        p.use_smooth = True

    mat = bpy.data.materials.new("clay")
    mat.use_nodes = True
    bsdf = mat.node_tree.nodes["Principled BSDF"]
    bsdf.inputs["Base Color"].default_value = (0.8, 0.8, 0.82, 1)
    bsdf.inputs["Roughness"].default_value = 0.35
    if "Specular IOR Level" in bsdf.inputs:
        bsdf.inputs["Specular IOR Level"].default_value = 0.5
    obj.data.materials.append(mat)

    # Framing, in units of the normalised box. Height runs along Z after OBJ import.
    views = {
        #        target height   camera direction      distance
        "full":  (0.00,          Vector((0.0, -1.0, 0.15)), 1.30),
        "torso": (0.20,          Vector((-0.45, -1.0, 0.22)), 1.15),
        "head":  (0.38,          Vector((-0.30, -1.0, 0.18)), 0.30),
        # The collar, which is where the user says the worst of it is.
        "neck":  (0.40,          Vector((-0.35, -1.0, 0.30)), 0.42),
        "hand":  (0.28,          Vector((-0.20, -1.0, 0.30)), 0.22),
    }
    height, direction, distance = views.get(view, views["torso"])
    target = Vector((0.0, 0.0, height))
    if view == "hand":
        target = Vector((-0.42, 0.0, 0.30))

    direction = direction.normalized()
    cam_data = bpy.data.cameras.new("cam")
    cam_data.lens = 85
    cam = bpy.data.objects.new("cam", cam_data)
    bpy.context.scene.collection.objects.link(cam)
    cam.location = target + direction * distance
    cam.rotation_mode = "QUATERNION"
    cam.rotation_quaternion = (target - cam.location).to_track_quat("-Z", "Y")
    bpy.context.scene.camera = cam

    # A key light high and to the camera's left plus a weak fill. Grazing light is what makes a
    # dent visible; a flat ambient setup would hide the whole argument.
    for loc, energy, size in (((-2.2, -2.0, 2.4), 220.0, 1.4), ((2.0, -1.6, 0.6), 45.0, 2.5)):
        d = bpy.data.lights.new("key", "AREA")
        d.energy = energy
        d.size = size
        o = bpy.data.objects.new("key", d)
        bpy.context.scene.collection.objects.link(o)
        o.location = loc
        o.rotation_quaternion = (Vector(target) - Vector(loc)).to_track_quat("-Z", "Y")
        o.rotation_mode = "QUATERNION"

    world = bpy.data.worlds.new("w")
    world.use_nodes = True
    world.node_tree.nodes["Background"].inputs[0].default_value = (0.05, 0.06, 0.08, 1)
    world.node_tree.nodes["Background"].inputs[1].default_value = 1.0
    bpy.context.scene.world = world

    scene = bpy.context.scene
    scene.render.engine = "BLENDER_EEVEE_NEXT"
    scene.render.resolution_x = 900
    scene.render.resolution_y = 900
    scene.render.filepath = out
    scene.render.image_settings.file_format = "PNG"
    bpy.ops.render.render(write_still=True)
    print(f"wrote {out}")


if __name__ == "__main__":
    a = sys.argv[sys.argv.index("--") + 1:]
    main(a[0], a[1], a[2] if len(a) > 2 else "torso")
