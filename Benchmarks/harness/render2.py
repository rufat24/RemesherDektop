# blender -b --python render2.py -- base.obj overlay.obj out_prefix views [x y z scale]
import bpy, sys, mathutils
a = sys.argv[sys.argv.index('--') + 1:]
base, over, prefix = a[0], a[1], a[2]
views = a[3].split(',')
focus = [float(x) for x in a[4:8]] if len(a) > 7 else None
bpy.ops.wm.read_factory_settings(use_empty=True)
def imp(p, col):
    bpy.ops.wm.obj_import(filepath=p, forward_axis='NEGATIVE_Z', up_axis='Y')
    o = bpy.context.selected_objects[0]
    m = bpy.data.materials.new('m'); m.diffuse_color = col; o.data.materials.append(m)
    return o
b = imp(base, (0.75, 0.75, 0.78, 1))
w = b.copy(); w.data = b.data.copy(); bpy.context.scene.collection.objects.link(w)
mod = w.modifiers.new('w', 'WIREFRAME'); mod.use_replace = True; mod.use_even_offset = False
o = imp(over, (0.9, 0.1, 0.1, 1))
o.scale = (1.002, 1.002, 1.002)
bb = [b.matrix_world @ mathutils.Vector(c) for c in b.bound_box]
lo = mathutils.Vector([min(v[i] for v in bb) for i in range(3)]); hi = mathutils.Vector([max(v[i] for v in bb) for i in range(3)])
size = (hi - lo).length; center = (lo + hi) / 2
mod.thickness = size * (0.0005 if focus is None else 0.00025)
wm = bpy.data.materials.new('w'); wm.diffuse_color = (0.02, 0.02, 0.02, 1); w.data.materials.clear(); w.data.materials.append(wm)
sc = bpy.context.scene; sc.render.engine = 'BLENDER_WORKBENCH'; sc.display.shading.light = 'STUDIO'; sc.display.shading.color_type = 'MATERIAL'
sc.render.resolution_x = sc.render.resolution_y = 1100
sc.world = bpy.data.worlds.new('w')
cam = bpy.data.objects.new('cam', bpy.data.cameras.new('cam')); sc.collection.objects.link(cam); sc.camera = cam; cam.data.type = 'ORTHO'
if focus: center = mathutils.Vector(focus[:3]); scale = focus[3]
else: scale = max(hi - lo) * 1.05
dirs = {'top': (0, 0.001, 1), 'front': (0, -1, 0), 'back': (0, 1, 0), 'side': (1, 0, 0), 'q': (0.7, -0.7, 0.2)}
for v in views:
    d = mathutils.Vector(dirs[v]).normalized()
    cam.location = center + d * size * 2; cam.rotation_euler = (-d).to_track_quat('-Z', 'Y').to_euler()
    cam.data.ortho_scale = scale; cam.data.clip_end = size * 10
    sc.render.filepath = f'{prefix}_{v}.png'; bpy.ops.render.render(write_still=True)
