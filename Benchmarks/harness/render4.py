# blender -b --python render4.py -- base.obj out_prefix views wire(0/1) [cx cy cz scale]
# Same framing as render2 (bbox of base), optional wireframe, optional focus window.
import bpy, sys, mathutils
a = sys.argv[sys.argv.index('--') + 1:]
base, prefix, views, wire = a[0], a[1], a[2].split(','), a[3] == '1'
focus = [float(x) for x in a[4:8]] if len(a) > 7 else None
bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.wm.obj_import(filepath=base, forward_axis='NEGATIVE_Z', up_axis='Y')
b = bpy.context.selected_objects[0]
m = bpy.data.materials.new('m'); m.diffuse_color = (0.75, 0.75, 0.78, 1); b.data.materials.append(m)
bb = [b.matrix_world @ mathutils.Vector(c) for c in b.bound_box]
lo = mathutils.Vector([min(v[i] for v in bb) for i in range(3)]); hi = mathutils.Vector([max(v[i] for v in bb) for i in range(3)])
size = (hi - lo).length; center = (lo + hi) / 2
if wire:
    w = b.copy(); w.data = b.data.copy(); bpy.context.scene.collection.objects.link(w)
    mod = w.modifiers.new('w', 'WIREFRAME'); mod.use_replace = True; mod.use_even_offset = False
    mod.thickness = size * (0.0005 if focus is None else 0.0002)
    wm = bpy.data.materials.new('w'); wm.diffuse_color = (0.02, 0.02, 0.02, 1); w.data.materials.clear(); w.data.materials.append(wm)
sc = bpy.context.scene; sc.render.engine = 'BLENDER_WORKBENCH'; sc.display.shading.light = 'STUDIO'; sc.display.shading.color_type = 'MATERIAL'
sc.render.resolution_x = sc.render.resolution_y = 1100
sc.world = bpy.data.worlds.new('w')
cam = bpy.data.objects.new('cam', bpy.data.cameras.new('cam')); sc.collection.objects.link(cam); sc.camera = cam; cam.data.type = 'ORTHO'
scale = max(hi - lo) * 1.05
if focus: center = mathutils.Vector(focus[:3]); scale = focus[3]
dirs = {'back': (0, 1, 0), 'front': (0, -1, 0), 'back': (0, 1, 0), 'side': (1, 0, 0), 'q': (0.7, -0.7, 0.2), 'top': (0, 0, 1), 'q2': (0.85, -0.45, 0.0)}
for v in views:
    d = mathutils.Vector(dirs[v]).normalized()
    cam.location = center + d * size * 2; cam.rotation_euler = (-d).to_track_quat('-Z', 'Y').to_euler()
    cam.data.ortho_scale = scale; cam.data.clip_end = size * 10
    sc.render.filepath = f'{prefix}_{v}.png'; bpy.ops.render.render(write_still=True)
