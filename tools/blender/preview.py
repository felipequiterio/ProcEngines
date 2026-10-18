"""Render a quick look at each kit piece (with its baked atlas) for review:
    blender -b tools/blender/kit.blend -P tools/blender/preview.py -- OUTDIR
"""
import bpy, math, os, sys
from mathutils import Vector

out = sys.argv[sys.argv.index("--") + 1] if "--" in sys.argv else os.path.dirname(bpy.data.filepath)
os.makedirs(out, exist_ok=True)
sc = bpy.context.scene
sc.render.engine = "CYCLES"
sc.cycles.samples = 24
sc.cycles.device = "CPU"
sc.render.resolution_x = sc.render.resolution_y = 640
atlas = bpy.data.images.load(os.path.join(os.path.dirname(bpy.data.filepath), "..", "..", "GameData", "ProcEngines", "PluginData", "Kit", "kit_atlas.png"))
show = bpy.data.materials.new("preview")
show.use_nodes = True
nt = show.node_tree
tex = nt.nodes.new("ShaderNodeTexImage")
tex.image = atlas
bsdf = nt.nodes["Principled BSDF"]
bsdf.inputs["Roughness"].default_value = 0.45
bsdf.inputs["Metallic"].default_value = 0.3
nt.links.new(tex.outputs["Color"], bsdf.inputs["Base Color"])
pieces = [o for o in bpy.data.objects if o.type == "MESH"]
for o in pieces:
    for i in range(len(o.data.materials)): o.data.materials[i] = show
cam = bpy.data.objects.new("cam", bpy.data.cameras.new("cam"))
sc.collection.objects.link(cam)
sc.camera = cam
sun = bpy.data.objects.new("sun", bpy.data.lights.new("sun", "SUN"))
sun.data.energy = 4
sun.rotation_euler = (math.radians(50), 0, math.radians(30))
sc.collection.objects.link(sun)
if sc.world is None: sc.world = bpy.data.worlds.new("w")
sc.world.use_nodes = True
sc.world.node_tree.nodes["Background"].inputs["Color"].default_value = (0.35, 0.42, 0.55, 1)
for o in pieces:
    for p in pieces: p.hide_render = p is not o
    c = o.location + Vector((0, 0, -0.1))
    for tag, ang, el in (("a", 25, 15), ("b", 205, 10)):
        a, e = math.radians(ang), math.radians(el)
        cam.location = c + Vector((math.cos(a) * math.cos(e), math.sin(a) * math.cos(e), math.sin(e))) * 5.5
        cam.rotation_euler = (c - cam.location).to_track_quat("-Z", "Y").to_euler()
        sc.render.filepath = os.path.join(out, f"{o.name}_{tag}.png")
        bpy.ops.render.render(write_still=True)
print("[preview] done")
