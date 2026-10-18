"""Render the exported kit pieces for review, the way the plugin sees them: each piece is read back from its .pem
file and shaded with the baked atlas (colour, gloss as roughness, tangent-space normal map) in Cycles, from two
angles under a sun and a sky. All renders are then tiled into contact_sheet.png.

    blender -b --factory-startup -P tools/blender/preview.py -- OUTDIR [--lod 1] [--clay] [--kit DIR] [--atlas DIR]
            [--only a,b] [--res 640] [--samples 32] [--mikk]

--lod 1 renders the <piece>.lod1.pem files; --clay ignores the textures (geometry check); --mikk shades the normal
map with Blender's own MikkTSpace tangents instead of the file's (for comparison). --kit is where the .pem files
are (default: the shipped kit); --atlas is where kit_atlas.png / kit_atlas_n.png are (default: --kit), for .pem
files exported against another directory's atlas (build_kit.py prints the matching command). Without an atlas
the pieces are rendered in clay.

The normal map is applied with the tangents stored in the file (version 2), as Unity does: binormal =
cross(normal, tangent) * w, converted back to Blender axes. A wrong w sign or an unswapped axis therefore shows up
in the renders (bumps lit as dents), and every piece's frame is also checked numerically against its UVs.
Inverted variants (<piece>_inv) are rendered upside down, the way KitBuilder hangs them.
"""
import bpy, math, os, struct, sys
import numpy as np
from mathutils import Vector

HERE = os.path.dirname(os.path.abspath(__file__))
PIECES = ["turbopump_dual", "turbopump_single", "preburner", "gas_generator", "valve", "actuator", "dome_inlet", "injector_ring",
          "turbopump_dual_inv", "turbopump_single_inv"]


def args():
    a = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    o = {"out": None, "lod": 0, "clay": False, "kit": os.path.join(HERE, "..", "..", "GameData", "ProcEngines", "PluginData", "Kit"),
         "atlas": None, "only": None, "res": 640, "samples": 32, "mikk": False}
    i = 0
    while i < len(a):
        if a[i] == "--lod": o["lod"] = int(a[i + 1]); i += 1
        elif a[i] == "--clay": o["clay"] = True
        elif a[i] == "--mikk": o["mikk"] = True
        elif a[i] == "--kit": o["kit"] = a[i + 1]; i += 1
        elif a[i] == "--atlas": o["atlas"] = a[i + 1]; i += 1
        elif a[i] == "--only": o["only"] = a[i + 1].split(","); i += 1
        elif a[i] == "--res": o["res"] = int(a[i + 1]); i += 1
        elif a[i] == "--samples": o["samples"] = int(a[i + 1]); i += 1
        else: o["out"] = a[i]
        i += 1
    o["out"] = o["out"] or os.path.join(HERE, "preview")
    o["atlas"] = o["atlas"] or o["kit"]
    if not o["clay"] and not os.path.exists(os.path.join(o["atlas"], "kit_atlas.png")):
        print("[preview] no kit_atlas.png in", os.path.abspath(o["atlas"]), "- rendering in clay")
        o["clay"] = True
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


def frame_check(d):
    """Area share of triangle corners whose stored frame follows the UVs as Unity uses it: tangent.xyz along +U,
    cross(normal, tangent) * w along +V (Unity axes, as stored)."""
    t = d["idx"].reshape(-1, 3)
    p, u, tan, nor = d["pos"].astype(np.float64), d["uv"].astype(np.float64), d["tan"].astype(np.float64), d["nor"].astype(np.float64)
    e1, e2 = p[t[:, 1]] - p[t[:, 0]], p[t[:, 2]] - p[t[:, 0]]
    d1, d2 = u[t[:, 1]] - u[t[:, 0]], u[t[:, 2]] - u[t[:, 0]]
    det = d1[:, 0] * d2[:, 1] - d2[:, 0] * d1[:, 1]
    ok = np.abs(det) > 1e-12
    e1, e2, d1, d2, det, t = e1[ok], e2[ok], d1[ok], d2[ok], det[ok], t[ok]
    dpdu = (d2[:, 1:2] * e1 - d1[:, 1:2] * e2) / det[:, None]
    dpdv = (d1[:, 0:1] * e2 - d2[:, 0:1] * e1) / det[:, None]
    area = 0.5 * np.linalg.norm(np.cross(e1, e2), axis=1)
    fu = fv = 0.0
    for k in range(3):
        T, w, N = tan[t[:, k], :3], tan[t[:, k], 3], nor[t[:, k]]
        fu += (area * (np.sum(T * dpdu, axis=1) > 0)).sum()
        fv += (area * (np.sum(np.cross(N, T) * w[:, None] * dpdv, axis=1) > 0)).sum()
    return fu / (3 * area.sum()), fv / (3 * area.sum())


def make_mesh(name, d):
    """Mesh from .pem streams: Unity axes back to Blender, winding reversed back. Version 2 tangents become the
    corner attributes pe_tan (Blender axes) and pe_sign (Blender bitangent sign = -w)."""
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
    if d["tan"] is not None:
        tb = d["tan"][:, [0, 2, 1]].astype(np.float32)
        sg = (-d["tan"][:, 3]).astype(np.float32)
        me.attributes.new("pe_tan", "FLOAT_VECTOR", "CORNER").data.foreach_set("vector", tb[tris.ravel()].ravel())
        me.attributes.new("pe_sign", "FLOAT", "CORNER").data.foreach_set("value", sg[tris.ravel()])
    me.validate()
    return me


def material(atlas_dir, clay, file_tangents):
    m = bpy.data.materials.new("preview_file_tangents" if file_tangents else "preview")
    nt = m.node_tree
    bsdf = nt.nodes["Principled BSDF"]
    if clay:
        bsdf.inputs["Base Color"].default_value = (0.35, 0.35, 0.35, 1)
        bsdf.inputs["Roughness"].default_value = 0.5
        return m
    col = bpy.data.images.load(os.path.join(atlas_dir, "kit_atlas.png"), check_existing=True)
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
    npath = os.path.join(atlas_dir, "kit_atlas_n.png")
    if not os.path.exists(npath):
        return m
    nimg = bpy.data.images.load(npath, check_existing=True)
    nimg.colorspace_settings.name = "Non-Color"
    ntex = nt.nodes.new("ShaderNodeTexImage")
    ntex.image = nimg
    nt.links.new(uvn.outputs["UV"], ntex.inputs["Vector"])
    if not file_tangents:   # version 1 file: Blender's own MikkTSpace
        nmap = nt.nodes.new("ShaderNodeNormalMap")
        nmap.space = "TANGENT"
        nmap.uv_map = "atlas"
        nt.links.new(ntex.outputs["Color"], nmap.inputs["Color"])
        nt.links.new(nmap.outputs["Normal"], bsdf.inputs["Normal"])
        return m

    def vmath(op, a, b=None, scale=None):
        n = nt.nodes.new("ShaderNodeVectorMath")
        n.operation = op
        for i, v in enumerate((a, b)):
            if v is None: continue
            if isinstance(v, bpy.types.NodeSocket): nt.links.new(v, n.inputs[i])
            else: n.inputs[i].default_value = v
        if scale is not None:
            if isinstance(scale, bpy.types.NodeSocket): nt.links.new(scale, n.inputs["Scale"])
            else: n.inputs["Scale"].default_value = scale
        return n.outputs["Value" if op == "DOT_PRODUCT" else "Vector"]

    # the file's frame (object space attributes -> world, like the normal), Gram-Schmidt against the shading normal
    N = nt.nodes.new("ShaderNodeNewGeometry").outputs["Normal"]
    ta = nt.nodes.new("ShaderNodeAttribute")
    ta.attribute_type, ta.attribute_name = "GEOMETRY", "pe_tan"
    xf = nt.nodes.new("ShaderNodeVectorTransform")
    xf.vector_type, xf.convert_from, xf.convert_to = "VECTOR", "OBJECT", "WORLD"
    nt.links.new(ta.outputs["Vector"], xf.inputs["Vector"])
    sa = nt.nodes.new("ShaderNodeAttribute")
    sa.attribute_type, sa.attribute_name = "GEOMETRY", "pe_sign"
    T = xf.outputs["Vector"]
    T = vmath("NORMALIZE", vmath("SUBTRACT", T, vmath("SCALE", N, scale=vmath("DOT_PRODUCT", N, T))))
    B = vmath("SCALE", vmath("CROSS_PRODUCT", N, T), scale=sa.outputs["Fac"])
    c = vmath("MULTIPLY_ADD", ntex.outputs["Color"], (2.0, 2.0, 2.0))
    c.node.inputs[2].default_value = (-1.0, -1.0, -1.0)
    sep = nt.nodes.new("ShaderNodeSeparateXYZ")
    nt.links.new(c, sep.inputs["Vector"])
    n = vmath("ADD", vmath("ADD", vmath("SCALE", T, scale=sep.outputs["X"]), vmath("SCALE", B, scale=sep.outputs["Y"])),
              vmath("SCALE", N, scale=sep.outputs["Z"]))
    nt.links.new(vmath("NORMALIZE", n), bsdf.inputs["Normal"])
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
    mats = {}
    suffix = ".lod1" if o["lod"] else ""
    files, bad = [], []
    for name in o["only"] or PIECES:
        path = os.path.join(o["kit"], name + suffix + ".pem")
        if not os.path.exists(path):
            print("[preview] missing", path)
            continue
        d = read_pem(path)
        note = ""
        if d["tan"] is not None:
            fu, fv = frame_check(d)
            note = f", frame: T along +U {100 * fu:.1f} %, B along +V {100 * fv:.1f} %"
            if fu < 0.9 or fv < 0.9: bad.append(name + suffix)
        file_t = d["tan"] is not None and not o["mikk"]
        if file_t not in mats: mats[file_t] = material(o["atlas"], o["clay"], file_t)
        ob = bpy.data.objects.new(name, make_mesh(name, d))
        ob.data.materials.append(mats[file_t])
        if name.endswith("_inv"): ob.rotation_euler = (math.pi, 0, 0)   # hangs upside down in game
        sc.collection.objects.link(ob)
        bpy.context.view_layer.update()
        files += render_piece(sc, cam, ob, o["out"], "lod1_" if o["lod"] else "")
        print(f"[preview] {name}{suffix}: v{d['version']}, {len(d['pos'])} verts, {len(d['idx']) // 3} tris{note}")
        bpy.data.objects.remove(ob)
    if files:
        contact_sheet(files, os.path.join(o["out"], ("lod1_" if o["lod"] else "") + "contact_sheet.png"))
    if bad:
        print("[preview] ERROR the tangent frame does not follow the UVs in", bad)
    print("[preview] done")


main()
