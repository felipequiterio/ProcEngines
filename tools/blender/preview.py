"""Render the exported kit pieces for review, the way the plugin sees them: each piece is read back from its .pem
file and shaded with the baked atlas (colour, gloss as roughness, tangent-space normal map) in Cycles, from two
angles under a sun and a sky. All renders are then tiled into contact_sheet.png.

    blender -b --factory-startup -P tools/blender/preview.py -- OUTDIR [--lod 1] [--clay] [--kit DIR]
            [--only a,b] [--res 640] [--samples 32]

--lod 1 renders the <piece>.lod1.pem files; --clay ignores the textures (geometry check).
"""
import bpy, math, os, struct, sys
import numpy as np
from mathutils import Vector

HERE = os.path.dirname(os.path.abspath(__file__))
PIECES = ["turbopump_dual", "turbopump_single", "preburner", "gas_generator", "valve", "actuator", "dome_inlet", "injector_ring"]


def args():
    a = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    o = {"out": None, "lod": 0, "clay": False, "kit": os.path.join(HERE, "..", "..", "GameData", "ProcEngines", "PluginData", "Kit"),
         "only": None, "res": 640, "samples": 32}
    i = 0
    while i < len(a):
        if a[i] == "--lod": o["lod"] = int(a[i + 1]); i += 1
        elif a[i] == "--clay": o["clay"] = True
        elif a[i] == "--kit": o["kit"] = a[i + 1]; i += 1
        elif a[i] == "--only": o["only"] = a[i + 1].split(","); i += 1
        elif a[i] == "--res": o["res"] = int(a[i + 1]); i += 1
        elif a[i] == "--samples": o["samples"] = int(a[i + 1]); i += 1
        else: o["out"] = a[i]
        i += 1
    o["out"] = o["out"] or os.path.join(HERE, "preview")
    return o


def read_pem(path):
    with open(path, "rb") as f:
        data = f.read()
    assert data[:4] == b"PEM1", path
    ver, nv, ni = struct.unpack_from("<iii", data, 4)
    off = 16
    def take(n, fmt="<f4"):
        nonlocal off
        a = np.frombuffer(data, dtype=fmt, count=n, offset=off)
        off += a.nbytes
        return a
    pos = take(3 * nv).reshape(-1, 3)
    nor = take(3 * nv).reshape(-1, 3)
    tan = take(4 * nv).reshape(-1, 4) if ver >= 2 else None
    uv = take(2 * nv).reshape(-1, 2)
    idx = take(ni, "<i4")
    return dict(version=ver, pos=pos, nor=nor, tan=tan, uv=uv, idx=idx)


def make_mesh(name, d):
    """Mesh from .pem streams: Unity axes back to Blender, winding reversed back."""
    pos = d["pos"][:, [0, 2, 1]].astype(np.float32)
    nor = d["nor"][:, [0, 2, 1]].astype(np.float32)
    tris = d["idx"].reshape(-1, 3)[:, [0, 2, 1]].astype(np.int32)
    me = bpy.data.meshes.new(name)
    me.vertices.add(len(pos))
    me.vertices.foreach_set("co", pos.ravel())
    me.loops.add(tris.size)
    me.loops.foreach_set("vertex_index", tris.ravel())
    me.polygons.add(len(tris))
    me.polygons.foreach_set("loop_start", np.arange(0, tris.size, 3, dtype=np.int32))
    me.update()
    uvl = me.uv_layers.new(name="atlas")
    uvl.data.foreach_set("uv", d["uv"][tris.ravel()].astype(np.float32).ravel())
    me.polygons.foreach_set("use_smooth", np.ones(len(tris), dtype=bool))
    me.normals_split_custom_set_from_vertices([tuple(n) for n in nor])
    me.validate()
    return me


def material(kit, clay):
    m = bpy.data.materials.new("preview")
    nt = m.node_tree
    bsdf = nt.nodes["Principled BSDF"]
    if clay:
        bsdf.inputs["Base Color"].default_value = (0.35, 0.35, 0.35, 1)
        bsdf.inputs["Roughness"].default_value = 0.5
        return m
    col = bpy.data.images.load(os.path.join(kit, "kit_atlas.png"))
    col.alpha_mode = "CHANNEL_PACKED"
    tex = nt.nodes.new("ShaderNodeTexImage")
    tex.image = col
    uvn = nt.nodes.new("ShaderNodeUVMap")
    uvn.uv_map = "atlas"
    nt.links.new(uvn.outputs["UV"], tex.inputs["Vector"])
    nt.links.new(tex.outputs["Color"], bsdf.inputs["Base Color"])
    rough = nt.nodes.new("ShaderNodeMath")              # roughness = 1 - gloss (slightly compressed)
    rough.operation = "MULTIPLY_ADD"
    nt.links.new(tex.outputs["Alpha"], rough.inputs[0])
    rough.inputs[1].default_value = -0.8
    rough.inputs[2].default_value = 0.92
    nt.links.new(rough.outputs[0], bsdf.inputs["Roughness"])
    bsdf.inputs["Metallic"].default_value = 0.3
    npath = os.path.join(kit, "kit_atlas_n.png")
    if os.path.exists(npath):
        nimg = bpy.data.images.load(npath)
        nimg.colorspace_settings.name = "Non-Color"
        ntex = nt.nodes.new("ShaderNodeTexImage")
        ntex.image = nimg
        nt.links.new(uvn.outputs["UV"], ntex.inputs["Vector"])
        nmap = nt.nodes.new("ShaderNodeNormalMap")
        nmap.space = "TANGENT"
        nmap.uv_map = "atlas"
        nt.links.new(ntex.outputs["Color"], nmap.inputs["Color"])
        nt.links.new(nmap.outputs["Normal"], bsdf.inputs["Normal"])
    return m


def scene_setup(res, samples):
    sc = bpy.context.scene
    sc.render.engine = "CYCLES"
    sc.cycles.device = "CPU"
    sc.cycles.samples = samples
    sc.cycles.use_denoising = True
    sc.render.resolution_x = sc.render.resolution_y = res
    sc.render.film_transparent = False
    sc.world = bpy.data.worlds.new("sky")
    nt = sc.world.node_tree
    bg = nt.nodes["Background"]
    # a physical sky, desaturated toward neutral so it does not tint the albedo, plus a white sun (as in KSP)
    try:
        sky = nt.nodes.new("ShaderNodeTexSky")
        sky.sky_type = "MULTIPLE_SCATTERING"
        sky.sun_disc = False
        sky.sun_elevation = math.radians(35)
        sky.sun_rotation = math.radians(210)
        hsv = nt.nodes.new("ShaderNodeHueSaturation")
        hsv.inputs["Saturation"].default_value = 0.35
        nt.links.new(sky.outputs["Color"], hsv.inputs["Color"])
        nt.links.new(hsv.outputs["Color"], bg.inputs["Color"])
        bg.inputs["Strength"].default_value = 0.16
    except Exception as e:   # older sky models: plain grey ambient
        print("[preview] sky:", e)
        bg.inputs["Color"].default_value = (0.3, 0.32, 0.35, 1)
    sc.view_settings.view_transform = "Standard"
    sun = bpy.data.objects.new("sun", bpy.data.lights.new("sun", "SUN"))
    sun.data.energy = 3.2
    sun.data.angle = math.radians(1.5)
    sun.rotation_euler = (math.radians(52), 0, math.radians(30))
    sc.collection.objects.link(sun)
    cam = bpy.data.objects.new("cam", bpy.data.cameras.new("cam"))
    cam.data.lens = 70
    sc.collection.objects.link(cam)
    sc.camera = cam
    return sc, cam


def render_piece(sc, cam, ob, out, tag_prefix):
    corners = [ob.matrix_world @ Vector(v) for v in ob.bound_box]
    c = sum(corners, Vector()) / 8
    size = max((corners[6] - corners[0]).length, 0.1)
    files = []
    for tag, ang, el in (("a", 28, 18), ("b", 208, 12)):
        a, e = math.radians(ang), math.radians(el)
        cam.location = c + Vector((math.cos(a) * math.cos(e), math.sin(a) * math.cos(e), math.sin(e))) * size * 1.55
        cam.rotation_euler = (c - cam.location).to_track_quat("-Z", "Y").to_euler()
        sc.render.filepath = os.path.join(out, f"{tag_prefix}{ob.name}_{tag}.png")
        bpy.ops.render.render(write_still=True)
        files.append(sc.render.filepath)
    return files


def contact_sheet(files, out_path, cols=4, tile=480):
    tiles = []
    for f in files:
        img = bpy.data.images.load(f)
        w, h = img.size
        a = np.empty(w * h * 4, np.float32)
        img.pixels.foreach_get(a)
        a = a.reshape(h, w, 4)
        ys = np.linspace(0, h - 1, tile).astype(int)       # nearest-neighbour downscale to the tile size
        xs = np.linspace(0, w - 1, tile).astype(int)
        tiles.append(a[ys][:, xs])
        bpy.data.images.remove(img)
    rows = (len(tiles) + cols - 1) // cols
    th, tw = tiles[0].shape[:2]
    sheet = np.ones((rows * th, cols * tw, 4), np.float32) * 0.1
    sheet[..., 3] = 1
    for i, t in enumerate(tiles):
        r, c = divmod(i, cols)
        y0 = (rows - 1 - r) * th          # Blender rows run bottom-up
        sheet[y0:y0 + t.shape[0], c * tw:c * tw + t.shape[1]] = t
    img = bpy.data.images.new("sheet", sheet.shape[1], sheet.shape[0], alpha=True)
    img.colorspace_settings.name = "Non-Color"
    img.pixels.foreach_set(sheet.ravel())
    img.filepath_raw = out_path
    img.file_format = "PNG"
    img.save()
    print("[preview] sheet", out_path)


def main():
    o = args()
    os.makedirs(o["out"], exist_ok=True)
    bpy.ops.wm.read_factory_settings(use_empty=True)
    sc, cam = scene_setup(o["res"], o["samples"])
    mat = material(o["kit"], o["clay"])
    suffix = ".lod1" if o["lod"] else ""
    files = []
    for name in o["only"] or PIECES:
        path = os.path.join(o["kit"], name + suffix + ".pem")
        if not os.path.exists(path):
            print("[preview] missing", path)
            continue
        d = read_pem(path)
        ob = bpy.data.objects.new(name, make_mesh(name, d))
        ob.data.materials.append(mat)
        sc.collection.objects.link(ob)
        bpy.context.view_layer.update()
        files += render_piece(sc, cam, ob, o["out"], "lod1_" if o["lod"] else "")
        print(f"[preview] {name}{suffix}: v{d['version']}, {len(d['pos'])} verts, {len(d['idx']) // 3} tris")
        bpy.data.objects.remove(ob)
    if files:
        contact_sheet(files, os.path.join(o["out"], ("lod1_" if o["lod"] else "") + "contact_sheet.png"))
    print("[preview] done")


main()
