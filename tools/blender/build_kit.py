"""
ProcEngines kit pieces: modelled, UV-atlased, baked and exported headless from Blender (5.2).

    blender -b --factory-startup -P tools/blender/build_kit.py -- [--out DIR] [--size 2048] [--quality 1.0]
        Build every piece from this script, unwrap, bake, export, and save tools/blender/kit.blend.
    blender -b tools/blender/kit.blend -P tools/blender/build_kit.py -- --export-only [--unwrap] [--out DIR]
        Bake and export the scene as it is in kit.blend (after hand edits in Blender). --unwrap redoes the atlas UVs.
    Other options: --only NAME[,NAME] builds just those pieces (quick look, does not save kit.blend);
                   --no-bake exports meshes against the existing atlas; --quality F scales the bake samples;
                   --debug-maps also writes the intermediate mask / colour / roughness maps to DIR.
    A full build takes about 2 minutes on a CPU (most of it the four 2048 bakes).
    Review the result with tools/blender/preview.py.

The contract with the plugin is ASSETS.md (sections 2-4). In short:
  * Blender +Z = up (Unity +Y), +X = radially outward from the engine axis, +Y = tangential.
    Each piece has its own unit (pump radius, line radius, barrel radius or chamber radius).
  * Anchors are empties parented to their piece and named "<piece>.<anchor>"; their local +Z is the direction.
  * All pieces share one UV map ("atlas") and two baked textures:
      kit_atlas.png    RGBA: RGB = base colour with wear x ambient occlusion (sRGB), A = gloss (1 - roughness, shaped)
      kit_atlas_n.png  RGB: tangent-space normal map, OpenGL convention (green = +V)
  * <piece>.pem (version 2) and <piece>.lod1.pem (Decimate collapse of the final LOD0 mesh, <= ~30 % triangles).

How a piece is made:
  1. bmesh modelling with the Builder below: lathes with filleted profiles, swept tubes with chamfered ends,
     bevelled boxes and plates, hex bolts with washers, weld beads, cables with clips and connectors.
     A second UV map ("local") carries per-primitive coordinates (around / along) for directional texture detail.
  2. Atlas UVs: lathes and tubes are unrolled while they are built (one island per primitive or angular sector,
     face attribute pe_wrap = 1); boxes and plates get Smart UV Project. Islands are scaled to object units,
     weighted by the piece's in-game size (pe_texel) and packed.
  3. Modifiers applied by the export: Triangulate (n-gon caps only) and Weighted Normal (face area, keep sharp),
     which gives the bevels hard-surface shading.
  4. Procedural "wear" materials (node graphs built by wear_material) baked in four passes:
     masks (AO, convex edges, crevices) -> colour -> roughness -> normal (bevel shader + bump).
"""
import bpy, bmesh, math, os, random, struct, sys, time
from contextlib import contextmanager
from mathutils import Vector, Matrix
import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.abspath(os.path.join(HERE, "..", ".."))
T0 = time.time()


def log(msg):
    print(f"[kit {time.time() - T0:6.1f}s] {msg}", flush=True)


def args():
    a = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    o = {"out": os.path.join(REPO, "GameData", "ProcEngines", "PluginData", "Kit"), "size": 2048, "quality": 1.0,
         "export_only": False, "unwrap": False, "only": None, "bake": True, "debug": False}
    i = 0
    while i < len(a):
        if a[i] == "--out": o["out"] = a[i + 1]; i += 1
        elif a[i] == "--size": o["size"] = int(a[i + 1]); i += 1
        elif a[i] in ("--quality", "--samples"): o["quality"] = float(a[i + 1]); i += 1
        elif a[i] == "--export-only": o["export_only"] = True
        elif a[i] == "--unwrap": o["unwrap"] = True
        elif a[i] == "--no-bake": o["bake"] = False
        elif a[i] == "--debug-maps": o["debug"] = True
        elif a[i] == "--only": o["only"] = a[i + 1].split(","); i += 1
        i += 1
    return o


# ================================================================================================ materials
#
# Every material is the same procedural "wear" graph with different parameters. Colours are linear albedo.
#   var      mottling strength            rough   base roughness          metal  worn edges brighten (bare metal)
#   paint    colour showing through chips (painted / anodised parts)      heat   heat tint amount (straw..blue)
#   soot     soot deposits                cast    sand-cast surface bump  turned lathe tool marks (local v)
#   grime    dirt in crevices + streaks   pattern special normal/colour detail: weld, braid, knurl, text

MATERIALS = [
    ("cast",      dict(base=(0.19, 0.19, 0.185), var=0.22, rough=0.60, metal=1, cast=1.0, grime=1.0)),
    ("machined",  dict(base=(0.215, 0.215, 0.22), var=0.13, rough=0.36, metal=1, turned=1.0, grime=0.85)),
    ("bolt",      dict(base=(0.22, 0.205, 0.17), var=0.15, rough=0.42, metal=1, grime=1.0)),
    ("dark",      dict(base=(0.035, 0.035, 0.037), var=0.12, rough=0.58, paint=(0.20, 0.20, 0.205), grime=0.6)),
    ("turbine",   dict(base=(0.15, 0.135, 0.12), var=0.25, rough=0.55, metal=1, heat=0.85, soot=0.7, cast=0.6, grime=0.7)),
    ("hot",       dict(base=(0.18, 0.165, 0.15), var=0.22, rough=0.48, metal=1, heat=0.55, soot=0.45, grime=0.7)),
    ("paint",     dict(base=(0.27, 0.27, 0.255), var=0.08, rough=0.50, paint=(0.11, 0.11, 0.11), grime=1.0)),
    ("connector", dict(base=(0.07, 0.075, 0.04), var=0.12, rough=0.45, paint=(0.24, 0.22, 0.17), grime=0.7, pattern="knurl")),
    ("cable",     dict(base=(0.026, 0.024, 0.022), var=0.15, rough=0.78, grime=0.5, pattern="braid")),
    ("chrome",    dict(base=(0.45, 0.45, 0.46), var=0.04, rough=0.14, metal=1, grime=0.45)),
    ("plate",     dict(base=(0.40, 0.40, 0.38), var=0.06, rough=0.38, metal=1, grime=0.6, pattern="text")),
    ("weld",      dict(base=(0.14, 0.125, 0.11), var=0.25, rough=0.62, metal=1, heat=0.5, grime=0.8, pattern="weld")),
]
M = {name: i for i, (name, _) in enumerate(MATERIALS)}
CAST, MACH, BOLT, DARK, TURB, HOT, PAINT, CONN, CABLE, CHROME, PLATE, WELD = (M[n] for n in (
    "cast", "machined", "bolt", "dark", "turbine", "hot", "paint", "connector", "cable", "chrome", "plate", "weld"))

GRIME = (0.030, 0.024, 0.018)      # oily dirt in cavities and drips
FILM = (0.085, 0.075, 0.060)       # dull handling / test-stand film over everything
SOOT = (0.014, 0.013, 0.012)
DUST = (0.24, 0.215, 0.18)
# heat tint of oxidised stainless / Inconel, deliberately muted: straw, bronze, plum, steel blue, grey
HEAT_RAMP = [(0.00, (0.19, 0.175, 0.15)), (0.25, (0.27, 0.22, 0.14)), (0.45, (0.22, 0.15, 0.10)),
             (0.62, (0.15, 0.12, 0.115)), (0.78, (0.11, 0.12, 0.15)), (1.00, (0.13, 0.13, 0.135))]


def sock(sockets, ident):
    """A node socket by identifier (several Mix sockets share the name "Result")."""
    return next(s for s in sockets if s.identifier == ident)


class Graph:
    """Builds shader node trees from expressions: every helper adds a node and returns its output socket."""

    def __init__(self, nt):
        self.nt = nt

    def node(self, kind, inputs=None, name=None, **props):
        n = self.nt.nodes.new(kind)
        if name: n.name = n.label = name
        for k, v in props.items(): setattr(n, k, v)
        for key, val in (inputs or {}).items(): self.set(n, key, val)
        return n

    def set(self, n, key, val):
        if isinstance(key, int):
            sock = [s for s in n.inputs if s.enabled][key]
        else:
            sock = next(s for s in n.inputs if s.identifier == key) if any(s.identifier == key for s in n.inputs) else n.inputs[key]
        if isinstance(val, bpy.types.NodeSocket): self.nt.links.new(val, sock)
        elif isinstance(val, (tuple, list)) and sock.type == "RGBA" and len(val) == 3: sock.default_value = (*val, 1.0)
        elif val is not None: sock.default_value = val

    def math(self, op, a, b=0.0, c=0.0, clamp=False):
        n = self.node("ShaderNodeMath", operation=op, use_clamp=clamp)
        for i, v in enumerate((a, b, c)):
            if isinstance(v, bpy.types.NodeSocket): self.nt.links.new(v, n.inputs[i])
            else: n.inputs[i].default_value = v
        return n.outputs[0]

    def add(self, a, b): return self.math("ADD", a, b)
    def sub(self, a, b): return self.math("SUBTRACT", a, b)
    def mul(self, a, b): return self.math("MULTIPLY", a, b)
    def mad(self, a, b, c): return self.math("MULTIPLY_ADD", a, b, c)
    def mx(self, a, b): return self.math("MAXIMUM", a, b)
    def clamp(self, a): return self.math("ADD", a, 0.0, clamp=True)

    def vec(self, op, a, b=None, scale=None):
        n = self.node("ShaderNodeVectorMath", operation=op)
        self.set(n, "Vector", a)
        if b is not None: self.set(n, "Vector_001", b)
        if scale is not None: self.set(n, "Scale", scale)
        return n.outputs["Value"] if op in ("DOT_PRODUCT", "LENGTH", "DISTANCE") else n.outputs["Vector"]

    def xyz(self, v):
        n = self.node("ShaderNodeSeparateXYZ", {"Vector": v})
        return n.outputs[0], n.outputs[1], n.outputs[2]

    def comb(self, x, y, z):
        return self.node("ShaderNodeCombineXYZ", {"X": x, "Y": y, "Z": z}).outputs[0]

    def mapping(self, v, scale=(1, 1, 1), rot=(0, 0, 0), loc=(0, 0, 0)):
        return self.node("ShaderNodeMapping", {"Vector": v, "Location": loc, "Rotation": rot, "Scale": scale}).outputs[0]

    def noise(self, v, scale, detail=2.0, rough=0.5, distortion=0.0):
        return self.node("ShaderNodeTexNoise", {"Vector": v, "Scale": scale, "Detail": detail, "Roughness": rough,
                                                "Distortion": distortion}).outputs["Fac"]

    def voronoi(self, v, scale, feature="F1", out="Distance", randomness=1.0):
        return self.node("ShaderNodeTexVoronoi", {"Vector": v, "Scale": scale, "Randomness": randomness},
                         feature=feature).outputs[out]

    def lin(self, x, f0, f1, t0=0.0, t1=1.0):
        return self.node("ShaderNodeMapRange", {"Value": x, "From Min": f0, "From Max": f1, "To Min": t0, "To Max": t1},
                         clamp=True).outputs[0]

    def smooth(self, x, f0, f1):
        return self.node("ShaderNodeMapRange", {"Value": x, "From Min": f0, "From Max": f1}, interpolation_type="SMOOTHSTEP",
                         clamp=True).outputs[0]

    def mixc(self, fac, a, b, blend="MIX"):
        n = self.node("ShaderNodeMix", data_type="RGBA", blend_type=blend, clamp_result=False)
        self.set(n, "Factor_Float", fac); self.set(n, "A_Color", a); self.set(n, "B_Color", b)
        return sock(n.outputs, "Result_Color")

    def mixf(self, fac, a, b):
        n = self.node("ShaderNodeMix", data_type="FLOAT")
        self.set(n, "Factor_Float", fac); self.set(n, "A_Float", a); self.set(n, "B_Float", b)
        return sock(n.outputs, "Result_Float")

    def ramp(self, fac, stops):
        n = self.node("ShaderNodeValToRGB", {"Fac": fac})
        els = n.color_ramp.elements
        while len(els) < len(stops): els.new(0.5)
        for e, (pos, col) in zip(els, stops):
            e.position = pos
            e.color = (*col, 1.0)
        return n.outputs["Color"]


def wear_material(name, p):
    """One procedural wear material. The graph ends in named nodes the bake routes to the output:
    pe_mask_out (AO, convex edge, crevice: ray traced, baked first), pe_color, pe_rough, pe_normal."""
    m = bpy.data.materials.get("pe_" + name) or bpy.data.materials.new("pe_" + name)
    nt = m.node_tree
    nt.nodes.clear()
    g = Graph(nt)
    out = g.node("ShaderNodeOutputMaterial", name="pe_output")
    emit = g.node("ShaderNodeEmission", name="pe_emit")
    bsdf = g.node("ShaderNodeBsdfDiffuse", name="pe_bsdf")
    g.node("ShaderNodeTexImage", name="pe_bake")

    # coordinates: P is the object position in "pump units of game size" (object units x pe_texel), so a
    # pattern has the same physical size on every piece; inv converts lengths back to object units
    texel = g.node("ShaderNodeAttribute", attribute_type="OBJECT", attribute_name="pe_texel").outputs["Fac"]
    texel = g.mx(texel, 0.02)
    inv = g.math("DIVIDE", 1.0, texel)
    tc = g.node("ShaderNodeTexCoord")
    P = g.vec("SCALE", tc.outputs["Object"], scale=texel)
    geo = g.node("ShaderNodeNewGeometry")
    N = geo.outputs["Normal"]
    nz = g.xyz(N)[2]
    lu, lv, _ = g.xyz(g.node("ShaderNodeUVMap", uv_map="local").outputs["UV"])

    # ---- pass 1: masks (ray traced once, read back from the baked image in the later passes)
    ao_far = g.node("ShaderNodeAmbientOcclusion", {"Distance": g.mul(inv, 0.22)}, samples=16, only_local=True).outputs["AO"]
    ao_near = g.node("ShaderNodeAmbientOcclusion", {"Distance": g.mul(inv, 0.035)}, samples=8, only_local=True).outputs["AO"]
    bev_wide = g.node("ShaderNodeBevel", {"Radius": g.mul(inv, 0.018)}, samples=8).outputs["Normal"]
    convex = g.lin(g.sub(1.0, g.vec("DOT_PRODUCT", bev_wide, N)), 0.0, 0.06)
    g.node("ShaderNodeCombineXYZ", {"X": ao_far, "Y": convex, "Z": ao_near}, name="pe_mask_out")

    uv_atlas = g.node("ShaderNodeUVMap", uv_map="atlas").outputs["UV"]
    mask = g.node("ShaderNodeTexImage", {"Vector": uv_atlas}, name="pe_mask", interpolation="Closest").outputs["Color"]
    AO, EDGE, NEAR = g.xyz(mask)
    occl = g.smooth(AO, 0.25, 0.95)                       # 0 in deep cavities .. 1 open
    edge = g.mul(g.smooth(EDGE, 0.15, 0.85), g.smooth(AO, 0.55, 0.9))   # convex edges only (concave ones are occluded)
    crevice = g.smooth(g.sub(1.0, NEAR), 0.08, 0.45)

    # ---- breakup noises
    big = g.noise(P, 1.3, 3.0, 0.55)
    mid = g.noise(P, 5.0, 4.0, 0.6)
    fine = g.noise(P, 38.0, 2.0, 0.55)
    grain = g.noise(P, 140.0, 1.0, 0.5)

    # ---- base colour: value mottling at three scales and a slight warm / cool drift
    base = p["base"]
    mot = g.add(g.mul(g.sub(mid, 0.5), 2.2 * p["var"]), g.mul(g.sub(big, 0.5), 1.6 * p["var"]))
    mot = g.add(mot, g.mul(g.sub(fine, 0.5), 0.7 * p["var"]))
    col = g.mixc(1.0, base, g.add(1.0, mot), "MULTIPLY")
    hue = g.smooth(g.noise(P, 0.9, 2.0, 0.5), 0.3, 0.7)
    col = g.mixc(1.0, col, g.mixc(hue, (0.95, 0.985, 1.05), (1.05, 1.0, 0.92)), "MULTIPLY")
    rough = g.add(p["rough"], g.add(g.mul(g.sub(mid, 0.5), 0.22), g.mul(g.sub(fine, 0.5), 0.12)))
    height = g.mul(g.sub(fine, 0.5), 0.15)

    # ---- heat tint (straw -> bronze -> plum -> blue -> grey) in irregular, muted patches
    if p.get("heat"):
        ht = g.add(g.mul(g.noise(P, 1.4, 3.0, 0.6), 1.15 * p["heat"]), g.mul(g.sub(mid, 0.5), 0.4))
        tint = g.mixc(1.0, g.ramp(g.clamp(ht), HEAT_RAMP), g.add(0.85, g.mul(fine, 0.3)), "MULTIPLY")
        col = g.mixc(g.mul(g.smooth(ht, 0.08, 0.4), 0.45 + 0.35 * p["heat"]), col, tint)

    # ---- paint and chipping: chips follow worn edges and also appear as sparse dings on flat areas
    chip = None
    if p.get("paint"):
        chip_n = g.noise(P, 22.0, 3.0, 0.65)
        chip = g.mx(g.smooth(g.add(g.mul(edge, 0.9), g.mul(g.sub(chip_n, 0.5), 1.4)), 0.62, 0.70),
                    g.smooth(g.mul(g.noise(P, 9.0, 4.0, 0.7), g.noise(P, 2.0, 1.0, 0.5)), 0.43, 0.46))
        col = g.mixc(chip, col, p["paint"])
        rough = g.mixf(chip, rough, 0.38)
        height = g.sub(height, g.mul(chip, 0.35))

    # ---- worn bright edges (bare metal), broken up by noise
    if p.get("metal"):
        brk = g.noise(P, 24.0, 3.0, 0.65)
        ew = g.mul(g.smooth(g.mul(edge, g.add(0.2, g.mul(brk, 1.4))), 0.45, 0.8), 0.8)
        col = g.mixc(ew, col, g.mixc(1.0, col, (1.7, 1.7, 1.68), "MULTIPLY"))
        rough = g.mixf(ew, rough, g.sub(rough, 0.16))

    # ---- fine scratches: two families of thin lines, sparse
    scr = None
    for k, (rot, sc) in enumerate((((0.3, 0.7, 0.4), (2.0, 2.0, 30.0)), ((1.1, -0.4, 1.9), (2.0, 30.0, 2.0)))):
        s = g.noise(g.mapping(P, scale=sc, rot=rot), 1.6, 1.0, 0.5)
        line = g.sub(1.0, g.smooth(g.math("ABSOLUTE", g.sub(s, 0.5)), 0.0, 0.035))
        line = g.mul(line, g.smooth(g.noise(P, 3.0 + k, 1.0, 0.5), 0.58, 0.68))
        scr = line if scr is None else g.mx(scr, line)
    col = g.mixc(g.mul(scr, 0.35 if p.get("metal") else 0.2), col, g.mixc(1.0, col, (1.5, 1.5, 1.5), "MULTIPLY"))
    rough = g.sub(rough, g.mul(scr, 0.08))
    height = g.sub(height, g.mul(scr, 0.08))

    # ---- special patterns on the local UV map (u around / v along a primitive)
    pat = p.get("pattern")
    if pat == "weld":   # overlapping ripples along the bead and a darker crown
        rip = g.math("SINE", g.mul(g.add(g.mul(lv, g.mul(texel, 220.0)), g.mul(g.math("ABSOLUTE", g.sub(lu, 0.5)), 2.0)), 6.2832))
        height = g.add(height, g.mul(rip, 0.25))
        col = g.mixc(g.mul(g.add(rip, 1.0), 0.18), col, g.mixc(1.0, col, (0.6, 0.55, 0.5), "MULTIPLY"))
    elif pat == "braid":  # braided sleeve
        a1 = g.math("SINE", g.mul(g.add(g.mul(lu, 10.0), g.mul(lv, g.mul(texel, 60.0))), 6.2832))
        a2 = g.math("SINE", g.mul(g.sub(g.mul(lu, 10.0), g.mul(lv, g.mul(texel, 60.0))), 6.2832))
        weave = g.mul(a1, a2)
        height = g.add(height, g.mul(weave, 0.04))
        col = g.mixc(g.mul(g.add(weave, 1.0), 0.3), col, (0.06, 0.055, 0.05))
    elif pat == "knurl":  # straight knurl around connector coupling rings
        height = g.add(height, g.mul(g.math("SINE", g.mul(lu, 6.2832 * 12.0)), 0.04))
    elif pat == "text":   # data plate: rows of engraved "text" and a border line
        rows = g.math("FRACT", g.mul(lv, 7.0))
        band = g.mul(g.smooth(rows, 0.25, 0.32), g.sub(1.0, g.smooth(rows, 0.62, 0.69)))
        words = g.smooth(g.noise(g.comb(g.mul(lu, 9.0), g.math("FLOOR", g.mul(lv, 7.0)), 0.0), 2.0, 2.0, 0.5), 0.42, 0.46)
        inside = g.mul(g.mul(g.smooth(lu, 0.12, 0.13), g.sub(1.0, g.smooth(lu, 0.88, 0.89))),
                       g.mul(g.smooth(lv, 0.14, 0.15), g.sub(1.0, g.smooth(lv, 0.86, 0.87))))
        border = g.sub(g.mul(g.smooth(lu, 0.05, 0.06), g.mul(g.sub(1.0, g.smooth(lu, 0.94, 0.95)),
                       g.mul(g.smooth(lv, 0.07, 0.08), g.sub(1.0, g.smooth(lv, 0.92, 0.93))))),
                       g.mul(g.smooth(lu, 0.07, 0.08), g.mul(g.sub(1.0, g.smooth(lu, 0.92, 0.93)),
                             g.mul(g.smooth(lv, 0.10, 0.11), g.sub(1.0, g.smooth(lv, 0.89, 0.90))))))
        ink = g.clamp(g.add(g.mul(g.mul(band, words), inside), g.clamp(border)))
        col = g.mixc(g.mul(ink, 0.9), col, (0.03, 0.03, 0.03))
        rough = g.mixf(ink, rough, 0.6)
        height = g.sub(height, g.mul(ink, 0.12))

    # ---- surface finish in the normal map
    if p.get("cast"):
        height = g.add(height, g.mul(g.add(g.mul(grain, 0.7), g.mul(g.sub(fine, 0.5), 0.6)), 0.9 * p["cast"]))
        dents = g.voronoi(P, 9.0, "SMOOTH_F1")
        height = g.add(height, g.mul(dents, 1.6 * p["cast"]))
    if p.get("turned"):
        rings = g.math("SINE", g.mul(lv, g.mul(texel, 500.0)))
        height = g.add(height, g.mul(rings, 0.05 * p["turned"]))
        col = g.mixc(g.mul(g.add(rings, 1.0), 0.03), col, g.mixc(1.0, col, (1.4, 1.4, 1.4), "MULTIPLY"))

    # ---- grime: a dull film in patches, dirt packed into crevices and cavities, streaks and drips running down
    # (object -Z) the vertical faces, dust on up-facing surfaces
    gr = p.get("grime", 0.0)
    film = g.mul(g.smooth(g.noise(P, 2.2, 4.0, 0.6), 0.38, 0.72), 0.45 * gr)
    col = g.mixc(film, col, g.mixc(0.5, col, FILM))
    rough = g.mixf(film, rough, g.add(rough, 0.12))
    cav = g.mx(g.mul(g.sub(1.0, occl), 0.95), crevice)
    cav = g.mul(cav, g.add(0.5, g.mul(mid, 0.8)))
    side = g.smooth(g.sub(1.0, g.math("ABSOLUTE", nz)), 0.2, 0.7)
    drip_n = g.noise(g.mapping(P, scale=(18.0, 18.0, 0.7)), 1.0, 3.0, 0.55, distortion=0.1)
    streak = g.mul(g.smooth(drip_n, 0.50, 0.64), g.smooth(g.noise(P, 1.6, 2.0, 0.5), 0.38, 0.6))
    drip_f = g.noise(g.mapping(P, scale=(60.0, 60.0, 2.5)), 1.0, 2.0, 0.5)
    drips = g.mx(g.mul(streak, 0.5), g.mul(g.smooth(drip_f, 0.6, 0.68), g.smooth(g.noise(P, 4.0, 2.0, 0.5), 0.5, 0.65)))
    drips = g.mul(drips, side)
    grime = g.clamp(g.mul(g.mx(cav, drips), gr))
    col = g.mixc(g.mul(grime, 0.88), col, GRIME)
    rough = g.mixf(g.mul(grime, 0.85), rough, 0.88)
    dust = g.mul(g.mul(g.smooth(nz, 0.55, 0.95), g.smooth(g.noise(P, 7.0, 3.0, 0.6), 0.42, 0.68)), 0.35 * gr)
    col = g.mixc(dust, col, DUST)
    rough = g.mixf(dust, rough, 0.8)

    # ---- soot on hot parts: blotchy, heavier in cavities
    if p.get("soot"):
        so = g.add(g.noise(P, 2.4, 4.0, 0.6), g.mul(g.sub(1.0, occl), 0.4))
        so = g.add(so, g.mul(g.sub(g.noise(g.mapping(P, scale=(10.0, 10.0, 1.2)), 1.0, 3.0, 0.5), 0.5), 0.5))
        soot = g.mul(g.smooth(so, 0.50, 0.80), p["soot"])
        col = g.mixc(soot, col, SOOT)
        rough = g.mixf(soot, rough, 0.9)

    # ---- outputs
    g.node("ShaderNodeMix", {"Factor_Float": 1.0, "A_Color": col, "B_Color": (1, 1, 1)}, name="pe_color",
           data_type="RGBA", blend_type="MULTIPLY")
    g.node("ShaderNodeMath", {0: rough, 1: 0.0}, name="pe_rough", operation="ADD", use_clamp=True)
    bev_small = g.node("ShaderNodeBevel", {"Radius": g.mul(inv, 0.007)}, samples=8).outputs["Normal"]
    g.node("ShaderNodeBump", {"Height": height, "Normal": bev_small, "Strength": 1.0, "Distance": g.mul(inv, 0.004)},
           name="pe_normal")
    nt.links.new(nt.nodes["pe_emit"].outputs[0], out.inputs["Surface"])
    nt.links.new(sock(nt.nodes["pe_color"].outputs, "Result_Color"), emit.inputs["Color"])
    return m


def make_materials():
    return [wear_material(name, p) for name, p in MATERIALS]


# ================================================================================================ modelling helpers

def frame(axis, ref=None):
    """Right-handed basis (a, b, w) with w along axis; a is ref projected onto the plane (or any perpendicular)."""
    w = Vector(axis).normalized()
    a = Vector(ref) - w * w.dot(Vector(ref)) if ref is not None else w.orthogonal()
    if a.length < 1e-6: a = w.orthogonal()
    a.normalize()
    return a, w.cross(a), w


def fillet(prof, mat, rnd):
    """Round the corners of a lathe profile. Entries are (r, z[, mat[, radius]]): a material applies to the span
    starting at that point and carries on; a radius overrides the default rounding of that corner.
    Returns the points and one material per span."""
    pts = [Vector((p[0], p[1])) for p in prof]
    mats, m = [], mat
    for p in prof:
        if len(p) > 2 and p[2] is not None: m = p[2]
        mats.append(m)
    out, om = [pts[0]], []
    for i in range(1, len(pts) - 1):
        A, P, B = pts[i - 1], pts[i], pts[i + 1]
        rad = prof[i][3] if len(prof[i]) > 3 else rnd
        d1, d2 = P - A, B - P
        ang = d1.angle(d2, 0.0) if d1.length > 1e-9 and d2.length > 1e-9 else 0.0
        if rad <= 0 or ang < math.radians(20) or P.x < 1e-7:
            om.append(mats[i - 1]); out.append(P); continue
        t = min(rad * math.tan(ang / 2), 0.45 * d1.length, 0.45 * d2.length)
        T1, T2 = P - d1.normalized() * t, P + d2.normalized() * t
        n = 1 if ang <= math.radians(100) else 2
        om.append(mats[i - 1]); out.append(T1)
        for k in range(1, n + 1):
            s = k / n
            om.append(mats[i - 1] if k <= n // 2 else mats[i])
            out.append(T1 * (1 - s) ** 2 + P * (2 * s * (1 - s)) + T2 * (s * s))
    om.append(mats[-2])
    out.append(pts[-1])
    return out, om


def catmull(pts, per=6):
    """Smooth path through control points (Catmull-Rom), `per` samples per span."""
    P = [Vector(p) for p in pts]
    P = [P[0] * 2 - P[1]] + P + [P[-1] * 2 - P[-2]]
    out = []
    for i in range(1, len(P) - 2):
        p0, p1, p2, p3 = P[i - 1], P[i], P[i + 1], P[i + 2]
        for k in range(per):
            t = k / per
            out.append(0.5 * ((2 * p1) + (-p0 + p2) * t + (2 * p0 - 5 * p1 + 4 * p2 - p3) * t * t + (-p0 + 3 * p1 - 3 * p2 + p3) * t ** 3))
    out.append(P[-2])
    return out


def sdf_cyl(R, axis_xy=(0.0, 0.0)):
    return lambda p: math.hypot(p.x - axis_xy[0], p.y - axis_xy[1]) - R


def sdf_torus(c, R, r):
    c = Vector(c)
    return lambda p: math.hypot(math.hypot(p.x - c.x, p.y - c.y) - R, p.z - c.z) - r


def sdf_plane(c, n):
    c, n = Vector(c), Vector(n).normalized()
    return lambda p: (p - c).dot(n)


def sdf_ellipsoid(c, a, h):
    """Dome: semi-axes a (radial, about Z) and h (along Z) centred at c (only the sign matters)."""
    c = Vector(c)
    return lambda p: math.sqrt(((p.x - c.x) ** 2 + (p.y - c.y) ** 2) / a ** 2 + (p.z - c.z) ** 2 / h ** 2) - 1.0


def new_bmesh():
    bm = bmesh.new()
    bm.loops.layers.uv.new("atlas")
    bm.loops.layers.uv.new("local")
    bm.faces.layers.int.new("pe_wrap")
    return bm


class Builder:
    """Accumulates one piece in a bmesh. Faces carry a material index and a "local" UV (u around, v along).
    Tubes and small hardware are unwrapped as they are built ("wrap": atlas UV = unrolled surface, one island per
    primitive, face attribute pe_wrap = 1); everything else is left to Smart UV Project."""

    def __init__(self, segs=64, rnd=0.01, seed=1, wrap_r=0.05):
        self.bm = new_bmesh()
        self.segs = segs
        self.wrap_r = wrap_r     # lathes no wider than this are unrolled as they are built
        self.rnd = rnd
        self.rng = random.Random(seed)
        self.profiles = []   # lathes about the piece axis: arrays of (r, z) for cable routing
        self.tubes = []      # sweeps: (centres Nx3, radii N) for cable routing

    # -- low level
    def face(self, verts, mat, luv=None, auv=None):
        f = self.bm.faces.new(verts)
        f.material_index = mat
        f.smooth = True
        if luv:
            L = self.bm.loops.layers.uv["local"]
            for loop, t in zip(f.loops, luv): loop[L].uv = t
        if auv:
            L = self.bm.loops.layers.uv["atlas"]
            for loop, t in zip(f.loops, auv): loop[L].uv = t
            f[self.bm.faces.layers.int["pe_wrap"]] = 1
        return f

    @contextmanager
    def part(self, bevel=0.0, segs=2, angle=40.0):
        """Model into a scratch bmesh, bevel its sharp edges, then merge it into the piece."""
        main = self.bm
        self.bm = new_bmesh()
        try:
            yield
        finally:
            tmp, self.bm = self.bm, main
            if bevel > 0:
                tmp.normal_update()
                edges = [e for e in tmp.edges if len(e.link_faces) == 2 and e.calc_face_angle(0) > math.radians(angle)]
                if edges:
                    res = bmesh.ops.bevel(tmp, geom=edges, offset=bevel, offset_type="OFFSET", segments=segs, profile=0.5,
                                          affect="EDGES", clamp_overlap=True, material=-1)
                    for f in res["faces"]: f.smooth = True
            self.merge(tmp)
            tmp.free()

    def merge(self, src):
        layers = [(src.loops.layers.uv[n], self.bm.loops.layers.uv[n]) for n in ("local", "atlas")]
        Ws, W = src.faces.layers.int["pe_wrap"], self.bm.faces.layers.int["pe_wrap"]
        vmap = {v: self.bm.verts.new(v.co) for v in src.verts}
        for f in src.faces:
            nf = self.bm.faces.new([vmap[v] for v in f.verts])
            nf.material_index, nf.smooth = f.material_index, True
            nf[W] = f[Ws]
            for a, b in zip(f.loops, nf.loops):
                for ls, ld in layers: b[ld].uv = a[ls].uv

    # -- surfaces of revolution and tubes
    def lathe(self, prof, mat=CAST, segs=None, origin=(0, 0, 0), axis=(0, 0, 1), rnd=None, ref=None, phase=0.0, wrap=True):
        """Revolve an (r, z[, mat[, fillet]]) profile about `axis` through `origin`. A profile running outward or
        downward (toward -axis) faces outward. Corners are filleted with radius `rnd` unless given per point.
        The atlas UVs are the unrolled surface (u = angle x radius, v = profile length), cut into angular sectors
        on larger lathes so flat rings do not shear; wrap=False leaves the lathe to Smart UV Project."""
        segs = segs or self.segs
        pts, pm = fillet(prof, mat, self.rnd if rnd is None else rnd)
        a, b, w = frame(axis, ref)
        o = Vector(origin)
        s = [0.0]
        for i in range(1, len(pts)): s.append(s[-1] + (pts[i] - pts[i - 1]).length)
        dirs = [a * math.cos(2 * math.pi * (j + phase) / segs) + b * math.sin(2 * math.pi * (j + phase) / segs) for j in range(segs)]
        rings = []
        for p in pts:
            if p.x < 1e-7: rings.append([self.bm.verts.new(o + w * p.y)])
            else: rings.append([self.bm.verts.new(o + d * p.x + w * p.y) for d in dirs])
        # unrolled atlas UVs: sector k spans segs/nsec segments; u = (angle - sector centre) x radius
        tau = 2 * math.pi
        rmax = max(p.x for p in pts)
        nsec = 1
        if rmax > self.wrap_r:
            want = max(6, math.ceil(tau * rmax / max(s[-1], 1e-3)))
            nsec = min((d for d in range(1, segs + 1) if segs % d == 0), key=lambda d: (abs(d - want), d))
        m_sec = segs // nsec
        for i in range(len(rings) - 1):
            A, B, m = rings[i], rings[i + 1], pm[i]
            ra, rb = pts[i].x, pts[i + 1].x
            if len(A) == 1 and len(B) == 1: continue
            for j in range(segs):
                j2, u0, u1 = (j + 1) % segs, j / segs, (j + 1) / segs
                k = j // m_sec
                a0 = lambda jj, r, sv: (tau * (jj / segs - (k + 0.5) / nsec) * r, sv)
                if len(A) == 1:
                    self.face([A[0], B[j], B[j2]], m, [((u0 + u1) / 2, s[i]), (u0, s[i + 1]), (u1, s[i + 1])],
                              [a0(j + 0.5, 0, s[i]), a0(j, rb, s[i + 1]), a0(j + 1, rb, s[i + 1])] if wrap else None)
                elif len(B) == 1:
                    self.face([A[j], B[0], A[j2]], m, [(u0, s[i]), ((u0 + u1) / 2, s[i + 1]), (u1, s[i])],
                              [a0(j, ra, s[i]), a0(j + 0.5, 0, s[i + 1]), a0(j + 1, ra, s[i])] if wrap else None)
                else:
                    self.face([A[j], B[j], B[j2], A[j2]], m, [(u0, s[i]), (u0, s[i + 1]), (u1, s[i + 1]), (u1, s[i])],
                              [a0(j, ra, s[i]), a0(j, rb, s[i + 1]), a0(j + 1, rb, s[i + 1]), a0(j + 1, ra, s[i])] if wrap else None)
        if abs(w.z) > 0.999 and abs(o.x) < 1e-6 and abs(o.y) < 1e-6:
            self.profiles.append(np.array([(p.x, o.z + w.z * p.y) for p in pts]))

    def cyl(self, p0, p1, r, mat=MACH, segs=32, rnd=None, r1=None, wrap=True):
        """Closed cylinder (or cone to r1) from p0 to p1 with rounded rims."""
        p0, p1 = Vector(p0), Vector(p1)
        h = (p1 - p0).length
        r1 = r if r1 is None else r1
        rnd = min(0.12 * min(r, r1), 0.3 * h) if rnd is None else rnd
        self.lathe([(0, h), (r1, h), (r, 0), (0, 0)], mat, segs, p0, p1 - p0, rnd, wrap=wrap)

    def sweep(self, pts, radii, mat, sides=20, cap0=False, cap1=False, closed=False, chamfer=0.12, record=True, wrap=True):
        """Tube through points with parallel-transported frames. Ends can be capped with a small chamfer.
        Closed loops spread the frame's holonomy twist evenly so the seam does not kink."""
        pts = [Vector(p) for p in pts]
        n = len(pts)
        radii = [radii] * n if isinstance(radii, (int, float)) else list(radii)
        tans = []
        for i in range(n):
            t = (pts[(i + 1) % n] - pts[(i - 1) % n]) if closed else (pts[min(i + 1, n - 1)] - pts[max(i - 1, 0)])
            tans.append(t.normalized())
        u = tans[0].orthogonal().normalized()
        us = []
        for i in range(n):
            u = (u - u.dot(tans[i]) * tans[i]).normalized()
            us.append(u)
        if closed:   # transport once more back to the start and measure the twist
            ue = (us[-1] - us[-1].dot(tans[0]) * tans[0]).normalized()
            twist = math.atan2(tans[0].cross(us[0]).dot(ue), us[0].dot(ue))
            for i in range(n):
                q = Matrix.Rotation(-twist * i / n, 3, tans[i])
                us[i] = q @ us[i]
        s = [0.0]
        for i in range(1, n): s.append(s[-1] + (pts[i] - pts[i - 1]).length)
        rings = []
        for i in range(n):
            v = tans[i].cross(us[i])
            rings.append([self.bm.verts.new(pts[i] + (us[i] * math.cos(2 * math.pi * j / sides) + v * math.sin(2 * math.pi * j / sides)) * radii[i])
                          for j in range(sides)])
        L = s[-1] + ((pts[0] - pts[-1]).length if closed else 0.0)

        tau = 2 * math.pi
        chunk = 4.0 * tau * max(radii)     # unrolled strips longer than this become separate atlas islands

        def band(A, B, sa, sb, ra, rb):
            o = chunk * math.floor(sa / chunk + 1e-9)
            for j in range(sides):
                j2, u0, u1 = (j + 1) % sides, j / sides, (j + 1) / sides
                self.face([A[j], A[j2], B[j2], B[j]], mat, [(u0, sa), (u1, sa), (u1, sb), (u0, sb)],
                          [(tau * u0 * ra, sa - o), (tau * u1 * ra, sa - o), (tau * u1 * rb, sb - o), (tau * u0 * rb, sb - o)] if wrap else None)

        for i in range(n if closed else n - 1):
            band(rings[i], rings[(i + 1) % n], s[i], s[i + 1] if i + 1 < n else L, radii[i], radii[(i + 1) % n])
        if not closed:
            for end, cap in ((0, cap0), (n - 1, cap1)):
                if not cap: continue
                t = tans[end] * (1 if end else -1)
                c = chamfer * radii[end]
                rr = radii[end] * (1 - chamfer)
                ring = [self.bm.verts.new(pts[end] + (vv.co - pts[end]) * (1 - chamfer) + t * c) for vv in rings[end]]
                # the cap unrolls like a lathe pole: a fan toward the centre point
                ctr = self.bm.verts.new(pts[end] + t * c)
                if end:
                    band(rings[end], ring, s[end], s[end] + c, radii[end], rr)
                    se = s[end] + c - chunk * math.floor(s[end] / chunk + 1e-9)
                    for j in range(sides):
                        j2, u0, u1 = (j + 1) % sides, j / sides, (j + 1) / sides
                        self.face([ring[j], ring[j2], ctr], mat, None,
                                  [(tau * u0 * rr, se), (tau * u1 * rr, se), (0, se + rr)] if wrap else None)
                else:
                    band(ring, rings[0], -c, 0.0, rr, radii[0])
                    for j in range(sides):
                        j2, u0, u1 = (j + 1) % sides, j / sides, (j + 1) / sides
                        self.face([ring[j2], ring[j], ctr], mat, None,
                                  [(tau * u1 * rr, -c), (tau * u0 * rr, -c), (0, -c - rr)] if wrap else None)
        if record:
            self.tubes.append((np.array([tuple(p) for p in pts]), np.array(radii)))

    def torus(self, centre, R, r, mat, segs=96, sides=16, axis=(0, 0, 1)):
        a, b, w = frame(axis)
        c = Vector(centre)
        self.sweep([c + (a * math.cos(2 * math.pi * i / segs) + b * math.sin(2 * math.pi * i / segs)) * R for i in range(segs)],
                   r, mat, sides, closed=True)

    def pipe(self, ctrl, r, mat, sides=20, per=6, **kw):
        """Smooth pipe through control points (Catmull-Rom)."""
        path = catmull(ctrl, per)
        self.sweep(path, r, mat, sides, **kw)
        return path

    # -- hardware
    def bolt(self, p, axis, r, washer=True, stud=False, mat=BOLT):
        """Hex bolt head (or nut with the stud end showing) with a washer, sitting on the surface at p."""
        p, w = Vector(p), Vector(axis).normalized()
        z = 0.0
        if washer:
            t, rw = 0.2 * r, 1.32 * r
            self.lathe([(0, t), (rw * 0.94, t), (rw, 0)], mat, 10, p, w, 0, wrap=True)
            z = t
        h = 0.68 * r
        self.lathe([(0, z + h), (0.80 * r, z + h), (r, z + 0.72 * h), (r, z)], mat, 6, p, w, 0, phase=self.rng.random(), wrap=True)
        if stud:
            self.lathe([(0, z + h + 0.42 * r), (0.36 * r, z + h + 0.42 * r), (0.5 * r, z + h + 0.28 * r), (0.5 * r, z + h)], mat, 6, p, w, 0, wrap=True)

    def bolt_ring(self, centre, axis, R, count, r, phase=0.0, skip=None, **kw):
        a, b, w = frame(axis)
        c = Vector(centre)
        for k in range(count):
            th = 2 * math.pi * (k + phase) / count
            p = c + (a * math.cos(th) + b * math.sin(th)) * R
            if skip and skip(p): continue
            self.bolt(p, w, r, **kw)

    def weld_ring(self, centre, axis, R, bead, segs=64, sides=6):
        self.torus(centre, R, bead, WELD, segs, sides, axis)

    def weld_junction(self, p0, d, rp, sdf, bead, n=40, sides=6, reach=None):
        """Weld bead where a pipe of radius rp leaving p0 along d (p0 inside the host) breaks through the host
        surface given by sdf (negative inside)."""
        a, b, w = frame(d)
        p0 = Vector(p0)
        reach = reach or 4 * rp
        pts = []
        for k in range(n):
            off = (a * math.cos(2 * math.pi * k / n) + b * math.sin(2 * math.pi * k / n)) * rp
            lo, hi = 0.0, reach
            if sdf(p0 + off) > 0 or sdf(p0 + w * hi + off) < 0: continue
            for _ in range(40):
                mid = (lo + hi) / 2
                if sdf(p0 + w * mid + off) < 0: lo = mid
                else: hi = mid
            pts.append(p0 + w * lo + off)
        if len(pts) == n:
            self.sweep(pts, bead, WELD, sides, closed=True, record=False)

    def box(self, centre, size, mat, rot=None, bevel=None):
        """Box with bevelled edges; rot is a 3x3 Matrix or an angle about Z. Local UV = planar (x, y)."""
        R = Matrix.Rotation(rot, 3, "Z") if isinstance(rot, (int, float)) else (rot or Matrix.Identity(3))
        Mx = Matrix.Translation(Vector(centre)) @ R.to_4x4() @ Matrix.Diagonal((*size, 1))
        bev = 0.14 * min(size) if bevel is None else bevel
        with self.part(bevel=bev):
            res = bmesh.ops.create_cube(self.bm, size=1.0, matrix=Mx)
            L = self.bm.loops.layers.uv["local"]
            inv = Mx.inverted()
            for f in {f for v in res["verts"] for f in v.link_faces}:
                f.material_index, f.smooth = mat, True
                for loop in f.loops:
                    q = inv @ loop.vert.co
                    loop[L].uv = (q.x + 0.5, q.y + 0.5)

    def prism(self, poly, origin, ax_u, ax_v, thick, mat, bevel=None):
        """Plate cut to an outline: poly in (u, v) coordinates of the plane (origin, ax_u, ax_v), extruded
        symmetrically by `thick` along ax_u x ax_v, edges bevelled."""
        o, U, V = Vector(origin), Vector(ax_u).normalized(), Vector(ax_v).normalized()
        n = U.cross(V).normalized()
        area = sum(poly[i][0] * poly[(i + 1) % len(poly)][1] - poly[(i + 1) % len(poly)][0] * poly[i][1] for i in range(len(poly)))
        if area < 0: poly = list(reversed(poly))
        bev = 0.25 * thick if bevel is None else bevel
        with self.part(bevel=bev, segs=1):
            F = [self.bm.verts.new(o + U * u + V * v + n * thick / 2) for u, v in poly]
            B = [self.bm.verts.new(o + U * u + V * v - n * thick / 2) for u, v in poly]
            self.face(F, mat, [(u, v) for u, v in poly])
            self.face(list(reversed(B)), mat, [(u, v) for u, v in reversed(poly)])
            k = len(poly)
            for i in range(k):
                i2 = (i + 1) % k
                self.face([F[i], B[i], B[i2], F[i2]], mat, [(0, i / k), (1, i / k), (1, (i + 1) / k), (0, (i + 1) / k)])

    def rounded(self, cx, cy, r, w, base, n=10):
        """Outline of a lug: a half circle of radius r around (cx, cy), straight sides of half-width w running to
        v = base (the arc is on the side away from the base)."""
        sgn = 1.0 if base < cy else -1.0
        a0 = math.acos(max(-1.0, min(1.0, w / r)))
        pts = [(cx + w, base)]
        for k in range(n + 1):
            a = a0 + (math.pi - 2 * a0) * k / n
            pts.append((cx + r * math.cos(a), cy + sgn * r * math.sin(a)))
        pts.append((cx - w, base))
        return pts

    def plate(self, centre, normal, up, w, h, t, rivet=None):
        """Flat data plate: thin plate with bevelled edges and four rivets; local UV spans the plate 0..1."""
        n = Vector(normal).normalized()
        a, b, nn = frame(normal, up)          # a = up direction on the plate, b = across
        c = Vector(centre) + n * t / 2
        Rm = Matrix((b, a, nn)).transposed()
        self.box(c, (w, h, t), PLATE, Rm, bevel=t * 0.45)
        r = rivet or 0.06 * h
        for su in (-1, 1):
            for sv in (-1, 1):
                p = c + n * t / 2 + b * su * (w / 2 - 2.2 * r) + a * sv * (h / 2 - 2.2 * r)
                self.lathe([(0, 0.55 * r), (0.6 * r, 0.45 * r), (r, 0.0)], BOLT, 8, p, n, 0)

    def plate_curved(self, R, th0, th1, z0, z1, t, rivet=None, origin=(0, 0, 0), axis=(0, 0, 1), ref=(1, 0, 0)):
        """Data plate wrapped on a cylinder of radius R (default: about the piece's Z axis); th is the angle from
        `ref` around `axis`, z the position along it. Text rows run around."""
        a, b, w = frame(axis, ref)
        o = Vector(origin)
        nu = 8
        with self.part(bevel=t * 0.45):
            outer, inner = [], []
            for i in range(nu + 1):
                th = th0 + (th1 - th0) * i / nu
                d = a * math.cos(th) + b * math.sin(th)
                outer.append([self.bm.verts.new(o + d * (R + t) + w * z) for z in (z0, z1)])
                inner.append([self.bm.verts.new(o + d * R + w * z) for z in (z0, z1)])
            for i in range(nu):
                u0, u1 = i / nu, (i + 1) / nu
                self.face([outer[i][0], outer[i + 1][0], outer[i + 1][1], outer[i][1]], PLATE, [(u0, 0), (u1, 0), (u1, 1), (u0, 1)])
                self.face([inner[i][1], inner[i + 1][1], inner[i + 1][0], inner[i][0]], PLATE)
                self.face([outer[i][1], outer[i + 1][1], inner[i + 1][1], inner[i][1]], PLATE, [(u0, 1), (u1, 1), (u1, 1), (u0, 1)])
                self.face([inner[i][0], inner[i + 1][0], outer[i + 1][0], outer[i][0]], PLATE, [(u0, 0), (u1, 0), (u1, 0), (u0, 0)])
            self.face([outer[0][1], inner[0][1], inner[0][0], outer[0][0]], PLATE, [(0, 1), (0, 1), (0, 0), (0, 0)])
            self.face([outer[nu][0], inner[nu][0], inner[nu][1], outer[nu][1]], PLATE, [(1, 0), (1, 0), (1, 1), (1, 1)])
        r = rivet or 0.06 * (z1 - z0)
        dth = 2.2 * r / R
        for th in (th0 + dth, th1 - dth):
            for z in (z0 + 2.2 * r, z1 - 2.2 * r):
                d = a * math.cos(th) + b * math.sin(th)
                self.lathe([(0, 0.55 * r), (0.6 * r, 0.45 * r), (r, 0.0)], BOLT, 8, o + d * (R + t) + w * z, d, 0)

    def connector(self, p, axis, r, ref=None):
        """Circular electrical connector (receptacle flange, knurled coupling nut, plug, back shell).
        Returns the cable exit point."""
        p, w = Vector(p), Vector(axis).normalized()
        self.lathe([(0, 1.0 * r), (0.85 * r, 1.0 * r), (0.85 * r, 0.62 * r), (1.0 * r, 0.6 * r), (1.0 * r, 0.18 * r),
                    (1.22 * r, 0.16 * r), (1.22 * r, 0.0)], CONN, 16, p, w, 0)
        self.lathe([(0, 1.9 * r), (0.4 * r, 1.9 * r), (0.62 * r, 1.0 * r)], CONN, 10, p, w, 0)
        return p + w * 1.9 * r

    def cable(self, ctrl, r, per=6, clips=(), clip_dir=None, sides=6):
        """Cable through control points; `clips` are path fractions where a P-clip holds it, its tab pointing
        along clip_dir(point) (toward the surface it is bolted to)."""
        path = catmull(ctrl, per)
        self.sweep(path, r, CABLE, sides, record=False)
        s = [0.0]
        for i in range(1, len(path)): s.append(s[-1] + (path[i] - path[i - 1]).length)
        for f in clips:
            target = f * s[-1]
            i = max(1, min(len(path) - 1, next((k for k in range(len(path)) if s[k] >= target), len(path) - 1)))
            p, t = path[i], (path[i] - path[i - 1]).normalized()
            self.lathe([(1.15 * r, 1.4 * r), (1.6 * r, 1.4 * r), (1.6 * r, -1.4 * r), (1.15 * r, -1.4 * r)], BOLT, 8, p, t, 0)
            if clip_dir:
                d = Vector(clip_dir(p)).normalized()
                d = (d - t * t.dot(d)).normalized()
                foot = p + d * 2.3 * r
                Rm = Matrix((t, d.cross(t), d)).transposed()   # columns: x = along the cable, y, z = toward the surface
                self.box(foot, (2.8 * r, 0.5 * r, 2.2 * r), BOLT, Rm, bevel=0.15 * r)
        return path

    def sensor(self, p, axis, r, ref=None, cable_to=None, clip_dir=None, cable_r=None):
        """Instrumentation port: boss, hex fitting, transducer body and connector, optionally a cable to a point
        list `cable_to`. Returns the connector exit point."""
        p, w = Vector(p), Vector(axis).normalized()
        self.lathe([(0.8 * r, 0.55 * r), (1.3 * r, 0.55 * r), (1.5 * r, 0.25 * r), (1.5 * r, -0.4 * r)], MACH, 16, p, w, 0)
        self.lathe([(0, 1.05 * r), (0.8 * r, 1.05 * r), (r, 0.9 * r), (r, 0.55 * r)], BOLT, 6, p, w, 0, phase=self.rng.random())
        self.lathe([(0, 2.6 * r), (0.62 * r, 2.6 * r), (0.7 * r, 2.45 * r), (0.7 * r, 1.05 * r)], MACH, 12, p, w, 0)
        out = self.connector(p + w * 2.6 * r, w, 0.62 * r)
        if cable_to:
            cr = cable_r or 0.22 * r
            self.cable([out - w * 0.1 * r, out + w * 1.2 * r] + list(cable_to), cr, clips=(), clip_dir=clip_dir)
        return out

    # -- cable routing over the piece's own surfaces of revolution and tubes
    def surface_r(self, th, z):
        """Outer radius of the modelled surfaces at angle th and height z (lathes about Z and swept tubes)."""
        best = 0.0
        for pr in self.profiles:
            r0, z0, r1, z1 = pr[:-1, 0], pr[:-1, 1], pr[1:, 0], pr[1:, 1]
            hit = (np.minimum(z0, z1) <= z) & (np.maximum(z0, z1) >= z) & (np.abs(z1 - z0) > 1e-9)
            if hit.any():
                r = r0[hit] + (r1[hit] - r0[hit]) * (z - z0[hit]) / (z1[hit] - z0[hit])
                best = max(best, float(r.max()))
        c, s = math.cos(th), math.sin(th)
        for C, rad in self.tubes:
            dz = z - C[:, 2]
            ok = np.abs(dz) < rad
            if not ok.any(): continue
            h = np.sqrt(np.maximum(rad[ok] ** 2 - dz[ok] ** 2, 0))
            along = C[ok, 0] * c + C[ok, 1] * s
            perp = np.abs(C[ok, 0] * s - C[ok, 1] * c)
            hit = (perp < h) & (along > 0)
            if hit.any():
                best = max(best, float((along[hit] + np.sqrt(h[hit] ** 2 - perp[hit] ** 2)).max()))
        return best

    def drape(self, waypoints, r, gap, step=0.012):
        """Points of a cable lying over the piece's outer surfaces along (angle, height) waypoints: the radius is
        the surface envelope plus clearance, bridged over steps (flanges) and relaxed. Returns (points, surface radii)."""
        samples = []
        for (t0, z0), (t1, z1) in zip(waypoints[:-1], waypoints[1:]):
            n = max(2, int(math.hypot((t1 - t0) * 0.6, z1 - z0) / step))
            samples += [(t0 + (t1 - t0) * k / n, z0 + (z1 - z0) * k / n) for k in range(n)]
        samples.append(waypoints[-1])
        env = [self.surface_r(t, z) for t, z in samples]
        win = max(1, int(3.0 * r / step))
        rad = [max(env[max(0, i - win):i + win + 1]) + gap + r for i in range(len(env))]   # bridge over steps
        for _ in range(4):   # relax so the cable does not follow every bump
            rad = [rad[0]] + [max(rad[i], (rad[i - 1] + rad[i] + rad[i + 1]) / 3) for i in range(1, len(rad) - 1)] + [rad[-1]]
        pts = [Vector((rr * math.cos(t), rr * math.sin(t), z)) for rr, (t, z) in zip(rad, samples)]
        return pts, env

    def harness(self, waypoints, r, lead_in=(), lead_out=(), gap=None, clip_every=0.16):
        """Cable draped over the piece along (angle, height) waypoints, joined to lead-in/out points (connector
        exits), with P-clips every `clip_every` where it lies on a surface. Returns the draped points."""
        gap = 1.6 * r if gap is None else gap
        pts, env = self.drape(waypoints, r, gap)
        ctrl = [Vector(p) for p in lead_in] + pts[::3] + ([pts[-1]] if (len(pts) - 1) % 3 else []) + [Vector(p) for p in lead_out]
        self.sweep(catmull(ctrl, 2), r, CABLE, 6, record=False)
        run = 0.0
        for i in range(2, len(pts) - 2):
            run += (pts[i] - pts[i - 1]).length
            rr = math.hypot(pts[i].x, pts[i].y)
            if run >= clip_every and rr - (env[i] + r) < 1.2 * gap:
                t = (pts[i + 1] - pts[i - 1]).normalized()
                d = Vector((-pts[i].x, -pts[i].y, 0)).normalized()
                d = (d - t * t.dot(d)).normalized()
                self.lathe([(1.15 * r, 1.4 * r), (1.6 * r, 1.4 * r), (1.6 * r, -1.4 * r), (1.15 * r, -1.4 * r)], BOLT, 8, pts[i], t, 0)
                Rm = Matrix((t, d.cross(t), d)).transposed()
                self.box(pts[i] + d * (r + gap / 2 + 0.2 * r), (2.6 * r, 1.4 * r, gap + 0.4 * r), BOLT, Rm, bevel=0.2 * r)
                run = 0.0
        return pts

    # -- finish
    def to_object(self, name, mats, sharp_deg=50):
        bm = self.bm
        bm.normal_update()
        for e in bm.edges:
            if len(e.link_faces) == 2 and e.calc_face_angle(0) > math.radians(sharp_deg):
                e.smooth = False
        me = bpy.data.meshes.new(name)
        bm.to_mesh(me)
        bm.free()
        for m in mats: me.materials.append(m)
        me.uv_layers.active = me.uv_layers["atlas"]
        me.uv_layers["atlas"].active_render = True
        ob = bpy.data.objects.new(name, me)
        bpy.context.scene.collection.objects.link(ob)
        add_modifiers(ob)
        return ob


def add_modifiers(ob):
    """Export stack: n-gons (caps) to triangles, then hard-surface normals for the bevels."""
    if "pe_ngons" not in ob.modifiers:
        tri = ob.modifiers.new("pe_ngons", "TRIANGULATE")
        tri.min_vertices, tri.ngon_method = 5, "BEAUTY"
        ob.modifiers.move(ob.modifiers.find(tri.name), 0)
    if "pe_weighted" not in ob.modifiers:
        wn = ob.modifiers.new("pe_weighted", "WEIGHTED_NORMAL")
        wn.mode, wn.weight, wn.keep_sharp = "FACE_AREA", 50, True


def anchor(ob, name, pos, direction):
    e = bpy.data.objects.new(ob.name + "." + name, None)
    e.empty_display_type = "SINGLE_ARROW"
    e.empty_display_size = 0.3
    z = Vector(direction).normalized()
    x = z.orthogonal().normalized()
    y = z.cross(x)
    m = Matrix((x, y, z)).transposed().to_4x4()
    m.translation = Vector(pos)
    e.parent = ob
    e.matrix_parent_inverse = Matrix.Identity(4)
    e.matrix_basis = m
    bpy.context.scene.collection.objects.link(e)
    return e


# ================================================================================================ pieces

def volute(b, z, base_r, r0, r1, sign, housing_r, zlim):
    """Scroll casing around the pump: cross-section grows r0 -> r1 over a turn, then a straight tangential
    discharge with a welded, bolted flange. Cast fillets where the scroll meets the housing wall (between
    zlim[0] and zlim[1]). sign = +1 discharges toward +Y, -1 toward -Y. Returns (anchor point, direction)."""
    N = 72
    pts, radii = [], []
    for i in range(N + 1):
        th = 2 * math.pi * i / N
        rc = r0 + (r1 - r0) * i / N
        R = base_r + rc * 0.8
        pts.append(Vector((R * math.cos(th), sign * R * math.sin(th), z)))
        radii.append(rc)
    # discharge direction: the chord of the last 1/56 turn, as in v1, so the anchors keep their v1 values
    rc = r0 + (r1 - r0) * 55 / 56
    R = base_r + rc * 0.8
    th = 2 * math.pi * 55 / 56
    d = (pts[N] - Vector((R * math.cos(th), sign * R * math.sin(th), z))).normalized()
    for k in (1, 6):
        pts.append(pts[N] + d * (0.06 * k))
        radii.append(r1)
    b.sweep(pts, radii, CAST, sides=20, cap0=True)
    # cast parting line around the scroll's outer equator, running out along the discharge
    rib = [pts[i] + Vector((math.cos(2 * math.pi * i / N), sign * math.sin(2 * math.pi * i / N), 0)) * radii[i] for i in range(2, N + 1)]
    rib.append(pts[N + 1] + Vector((1, 0, 0)) * r1)
    b.sweep(rib, 0.009, CAST, 5, cap0=True, cap1=True, record=False)
    # cast fillets along the two lines where the scroll breaks through the housing cylinder
    for up in (1, -1):
        run = []
        for i in range(3, N + 1):
            th = 2 * math.pi * i / N
            rc = r0 + (r1 - r0) * i / N
            R = base_r + rc * 0.8
            dz2 = rc * rc - (housing_r - R) ** 2
            zz = z + up * math.sqrt(max(dz2, 0))
            if dz2 > 0 and zlim[0] < zz < zlim[1]:
                run.append(Vector((housing_r * math.cos(th), sign * housing_r * math.sin(th), zz)))
            elif len(run) > 3:
                b.sweep(run, 0.011, CAST, 5, cap0=True, cap1=True, record=False); run = []
            else: run = []
        if len(run) > 3: b.sweep(run, 0.011, CAST, 5, cap0=True, cap1=True, record=False)
    end = pts[-1]
    fl = r1 * 1.38
    b.lathe([(0.0, 0.07, MACH), (fl * 0.82, 0.07), (fl * 0.82, 0.074), (fl, 0.074), (fl, 0.0), (r1 * 1.2, 0.0, CAST, 0.006),
             (r1 * 1.04, -0.04, None, 0.01), (r1 * 1.0, -0.055)], MACH, 40, end - d * 0.024, d, 0.005)
    b.weld_ring(end - d * 0.079, d, r1, 0.009, 40, 5)
    b.bolt_ring(end - d * 0.024, -d, (fl + r1 * 1.2) / 2, 8, 0.018, phase=0.5)
    return end + d * 0.05, d


def turbopump(dual=True):
    """Single-shaft turbopump: oxidizer pump on top (axial inlet), fuel pump below it (dual only), bearing
    section, turbine with its toroidal hot-gas inlet manifold, and the turbine exhaust at the bottom."""
    b = Builder(segs=64, rnd=0.008, seed=11 if dual else 12, wrap_r=0.15)
    A = {}
    # ---- oxidizer inlet flange, inducer neck, cast flare with ribs, pump housing and its flange
    b.lathe([(0.0, 1.116, MACH), (0.272, 1.116), (0.272, 1.13), (0.40, 1.13), (0.40, 1.074), (0.338, 1.074, CAST),
             (0.312, 1.045, None, 0.02), (0.312, 0.992, MACH), (0.327, 0.986), (0.327, 0.958), (0.312, 0.952, CAST),
             (0.312, 0.905, None, 0.05), (0.37, 0.852), (0.46, 0.797), (0.55, 0.745), (0.603, 0.695), (0.622, 0.64),
             (0.622, 0.372, MACH, 0.022), (0.68, 0.372), (0.68, 0.30), (0.40, 0.30)], CAST)
    b.bolt_ring((0, 0, 1.13), (0, 0, 1), 0.36, 12, 0.019, phase=0.5)
    A["inlet"] = ((0, 0, 1.16), (0, 0, 1))
    for k in range(8):   # cast ribs on the flare
        th = 2 * math.pi * (k + 0.5) / 8
        radial = Vector((math.cos(th), math.sin(th), 0))
        b.prism([(0.30, 1.03), (0.338, 1.03), (0.40, 0.95), (0.62, 0.72), (0.62, 0.66), (0.50, 0.70), (0.30, 0.86)],
                (0, 0, 0), radial, (0, 0, 1), 0.022, CAST, bevel=0.005)
    end, d = volute(b, 0.5, 0.55, 0.07, 0.17, +1, 0.622, (0.372, 0.64))
    A["ox_out" if dual else "out"] = (tuple(end), tuple(d))
    # flange bolts, except those buried under the scroll's discharge
    b.bolt_ring((0, 0, 0.372), (0, 0, 1), 0.651, 18, 0.017,
                skip=lambda p: math.atan2(p.y, p.x) % (2 * math.pi) > 5.2 or math.atan2(p.y, p.x) % (2 * math.pi) < 0.35)
    # discharge pressure transducer on top of the ox scroll, above the junction box
    th_s = math.radians(-40)
    rc_s = 0.07 + 0.1 * (2 * math.pi + th_s) / (2 * math.pi)
    R_s = 0.55 + 0.8 * rc_s
    ox_sensor = b.sensor(Vector((R_s * math.cos(th_s), R_s * math.sin(th_s), 0.5 + rc_s - 0.004)), (0, 0, 1), 0.026)

    zb = 0.30
    if dual:
        # inter-stage seal housing with gussets, bolted to the fuel pump
        b.lathe([(0.40, zb, DARK), (0.335, zb - 0.01), (0.322, zb - 0.03), (0.322, -0.02, MACH, 0.02), (0.58, -0.02),
                 (0.58, -0.07), (0.552, -0.07, CAST, 0.015), (0.552, -0.42), (0.49, -0.475), (0.30, -0.50)], DARK)
        for k in range(8):
            th = 2 * math.pi * k / 8 + math.pi / 8
            radial = Vector((math.cos(th), math.sin(th), 0))
            b.prism([(0.31, -0.02), (0.50, -0.02), (0.50, 0.03), (0.47, zb), (0.31, zb)], (0, 0, 0), radial, (0, 0, 1), 0.024, DARK, bevel=0.005)
        b.bolt_ring((0, 0, -0.02), (0, 0, 1), 0.53, 16, 0.017, phase=0.5,
                    skip=lambda p: any(abs((math.atan2(p.y, p.x) - (2 * math.pi * k / 8 + math.pi / 8) + math.pi) % (2 * math.pi) - math.pi) < 0.07 for k in range(8)))
        b.plate_curved(0.322, -0.24, 0.24, 0.07, 0.19, 0.006)
        # fuel pump scroll
        end, d = volute(b, -0.25, 0.5, 0.05, 0.13, -1, 0.552, (-0.42, -0.07))
        A["fuel_out"] = (tuple(end), tuple(d))
        # fuel inlet: an elbow from the pump side turning up, ending in a flange
        th = math.radians(125)
        c, s = math.cos(th), math.sin(th)
        radial = Vector((c, s, 0))
        path = b.pipe([radial * 0.45 + Vector((0, 0, -0.2)), radial * 0.66 + Vector((0, 0, -0.2)), radial * 0.79 + Vector((0, 0, -0.13)),
                       radial * 0.82 + Vector((0, 0, -0.0)), radial * 0.82 + Vector((0, 0, 0.12))], 0.1, CAST, 20, per=4)
        b.weld_junction(radial * 0.45 + Vector((0, 0, -0.2)), radial, 0.1, sdf_cyl(0.552), 0.011, n=36, sides=5)
        topf = radial * 0.82 + Vector((0, 0, 0.12))
        b.lathe([(0, 0.05, MACH), (0.142, 0.05), (0.142, 0.0), (0.118, 0.0, CAST, 0.005), (0.104, -0.025, None, 0.006), (0.1, -0.035)],
                MACH, 32, topf, (0, 0, 1), 0.004)
        b.bolt_ring(topf, (0, 0, -1), 0.128, 8, 0.012, phase=0.5)
        A["fuel_in"] = (tuple(topf + Vector((0, 0, 0.07))), (0, 0, 1))
        zt = -0.50
    else:
        b.lathe([(0.40, zb, DARK), (0.315, zb - 0.01), (0.30, zb - 0.03), (0.30, -0.10), (0.30, -0.12)], DARK)
        for k in range(8):
            th = 2 * math.pi * k / 8 + math.pi / 8
            radial = Vector((math.cos(th), math.sin(th), 0))
            b.prism([(0.29, -0.12), (0.37, -0.12), (0.47, zb), (0.29, zb)], (0, 0, 0), radial, (0, 0, 1), 0.024, DARK, bevel=0.005)
        b.plate_curved(0.30, -0.24, 0.24, 0.0, 0.12, 0.006)
        zt = -0.12

    # ---- bearing neck, turbine flange, turbine housing, exhaust cone and flange
    b.lathe([(0.30, zt, DARK), (0.272, zt - 0.025), (0.272, zt - 0.06, MACH), (0.262, zt - 0.065), (0.262, zt - 0.08),
             (0.272, zt - 0.085), (0.272, zt - 0.20, MACH, 0.02), (0.62, zt - 0.20), (0.62, zt - 0.25, TURB),
             (0.80, zt - 0.262, None, 0.02), (0.86, zt - 0.30), (0.86, zt - 0.56), (0.80, zt - 0.582), (0.53, zt - 0.59, None, 0.03),
             (0.46, zt - 0.70), (0.44, zt - 0.775, MACH, 0.02), (0.50, zt - 0.78), (0.50, zt - 0.84), (0.0, zt - 0.84)], DARK)
    b.bolt_ring((0, 0, zt - 0.20), (0, 0, 1), 0.585, 20, 0.017, phase=0.25, stud=True)
    b.bolt_ring((0, 0, zt - 0.84), (0, 0, -1), 0.465, 12, 0.016, phase=0.5)
    for k in range(12):   # gussets between the exhaust flange and the turbine housing
        th = 2 * math.pi * (k + 0.5) / 12
        radial = Vector((math.cos(th), math.sin(th), 0))
        b.prism([(0.44, zt - 0.78), (0.50, zt - 0.78), (0.79, zt - 0.585), (0.52, zt - 0.585)], (0, 0, 0), radial, (0, 0, 1), 0.02, TURB, bevel=0.004)
    # toroidal hot-gas manifold, welded to the housing, fed from the side
    zm = zt - 0.43
    b.torus((0, 0, zm), 0.9, 0.11, HOT, 96, 16)
    dz = math.sqrt(0.11 ** 2 - 0.04 ** 2)
    for sgn in (1, -1): b.weld_ring((0, 0, zm + sgn * dz), (0, 0, 1), 0.86, 0.012, 72, 5)
    th = math.radians(30)
    c, s = math.cos(th), math.sin(th)
    radial = Vector((c, s, 0))
    b.sweep([radial * 0.9 + Vector((0, 0, zm)), radial * 1.25 + Vector((0, 0, zm))], 0.1, HOT, 24)
    b.weld_junction(radial * 0.9 + Vector((0, 0, zm)), radial, 0.1, sdf_torus((0, 0, zm), 0.9, 0.11), 0.012, n=36, sides=5)
    p = radial * 1.25 + Vector((0, 0, zm))
    b.lathe([(0, 0.05, MACH), (0.142, 0.05), (0.142, 0.0), (0.118, 0.0, HOT, 0.005), (0.104, -0.025, None, 0.006), (0.1, -0.04)],
            MACH, 32, p, radial, 0.004)
    b.bolt_ring(p, -radial, 0.128, 8, 0.012, phase=0.5)
    A["turbine_in"] = (tuple(radial * 1.32 + Vector((0, 0, zm))), (c, s, 0))
    A["turbine_exhaust"] = ((0, 0, zt - 0.86), (0, 0, -1))

    # ---- instrumentation: a junction box on the seal housing (between two gussets, on the +X side) collects
    # the harness from the ox discharge pressure, the bearing speed pickup and the turbine inlet temperature
    th_h = math.radians(-45)
    rad_h = Vector((math.cos(th_h), math.sin(th_h), 0))
    zj, rj = (0.13, 0.36) if dual else (0.10, 0.34)
    box_c = rad_h * rj + Vector((0, 0, zj))
    b.box(box_c, (0.07, 0.15, 0.13), PAINT, th_h, bevel=0.01)
    b.bolt_ring(box_c + rad_h * 0.035, rad_h, 0.062, 4, 0.006, phase=0.5, washer=False)
    con_up = b.connector(box_c + rad_h * 0.035 + Vector((0, 0, 0.032)), rad_h, 0.016)
    con_dn = b.connector(box_c + rad_h * 0.035 + Vector((0, 0, -0.032)), rad_h, 0.016)
    spd = b.sensor(rad_h * 0.272 + Vector((0, 0, zt - 0.12)), rad_h, 0.022)
    th_t = math.radians(-20)
    tsens = b.sensor(Vector((0.9 * math.cos(th_t), 0.9 * math.sin(th_t), zm + 0.107)), (0, 0, 1), 0.02)

    def cyl_pt(th, r, z): return Vector((r * math.cos(th), r * math.sin(th), z))

    # main harness: down over the seal flange, the fuel scroll and the pump flanges to the bearing neck
    trunk = b.harness([(th_h + 0.04, zj - 0.075), (th_h + 0.04, zt - 0.03)], 0.02,
                      lead_in=[con_dn, con_dn + rad_h * 0.03 + Vector((0, 0, -0.035))],
                      lead_out=[cyl_pt(th_h + 0.03, 0.33, zt - 0.06), spd + rad_h * 0.03 + Vector((0, 0, 0.012)), spd])
    # turbine temperature: up from the manifold, across the turbine flange top, joining the trunk at the neck
    zf = zt - 0.155
    b.cable([tsens, tsens + Vector((0, 0, 0.035)), cyl_pt(th_t, 0.84, zf + 0.01), cyl_pt(th_t - 0.08, 0.66, zf),
             cyl_pt(th_h + 0.12, 0.45, zf), cyl_pt(th_h + 0.06, 0.33, zt - 0.075)], 0.012, per=5)
    # ox discharge pressure: up over the ox flange to the transducer on top of the scroll
    b.harness([(th_h + 0.03, zj + 0.085), (th_s + 0.03, 0.44), (th_s + 0.05, 0.60)], 0.013,
              lead_in=[con_up, con_up + rad_h * 0.03 + Vector((0, 0, 0.03))],
              lead_out=[ox_sensor + Vector((0.0, 0.0, 0.05)) + rad_h * 0.03, ox_sensor])
    return b, A


def preburner():
    """Preburner (unit = pump reference radius) standing on the turbine: bolted base flange at z = 0, jacketed
    body, bolted injector head with an igniter and two feed inlets, and the hot-gas outlet toward the engine
    axis (-X)."""
    b = Builder(segs=56, rnd=0.008, seed=21, wrap_r=0.15)
    A = {}
    dome = [(0.0, 1.074, MACH), (0.07, 1.074), (0.07, 1.052, HOT, 0.01)]
    t0 = math.asin(0.075 / 0.43)
    for i in range(1, 7):
        t = t0 + (math.pi / 2 - t0) * i / 6
        dome.append((0.43 * math.sin(t), 0.79 + 0.27 * math.cos(t)))
    b.lathe(dome + [(0.48, 0.79, MACH), (0.48, 0.758), (0.466, 0.758, DARK, 0.0), (0.466, 0.752, MACH, 0.0), (0.48, 0.752),
                    (0.48, 0.72), (0.425, 0.72, TURB, 0.02), (0.425, 0.27), (0.44, 0.265, MACH), (0.44, 0.225), (0.425, 0.22, TURB),
                    (0.425, 0.14, MACH, 0.03), (0.53, 0.14), (0.53, 0.0), (0.0, 0.0)], HOT)
    b.bolt_ring((0, 0, 0.79), (0, 0, 1), 0.456, 14, 0.013)
    b.bolt_ring((0, 0, 0.72), (0, 0, -1), 0.456, 14, 0.013, washer=False, stud=True)
    b.bolt_ring((0, 0, 0.14), (0, 0, 1), 0.49, 12, 0.018, stud=True)
    dome_sdf = sdf_ellipsoid((0, 0, 0.79), 0.43, 0.27)
    # feed inlets on the head: oxidizer and fuel, each a welded stub with a small bolted flange
    for name, x in (("ox_feed", 0.16), ("fuel_feed", -0.16)):
        p0 = Vector((x, 0.12, 0.96))
        b.cyl(p0, p0 + Vector((0, 0, 0.20)), 0.06, HOT, 24, rnd=0.0)
        b.weld_junction(p0, (0, 0, 1), 0.06, dome_sdf, 0.009, n=24, sides=5, reach=0.2)
        p1 = p0 + Vector((0, 0, 0.20))
        b.lathe([(0, 0.035, MACH), (0.09, 0.035), (0.09, 0.0), (0.072, 0.0, None, 0.004), (0.062, -0.015)], MACH, 24, p1, (0, 0, 1), 0.004)
        b.bolt_ring(p1, (0, 0, -1), 0.078, 4, 0.0095, phase=0.5, washer=False)
        A[name] = (tuple(p1 + Vector((0, 0, 0.04))), (0, 0, 1))
    # hot-gas outlet toward the engine axis
    b.sweep([(-0.3, 0, 0.5), (-0.72, 0, 0.5)], 0.19, HOT, 32)
    b.weld_junction((-0.3, 0, 0.5), (-1, 0, 0), 0.19, sdf_cyl(0.425), 0.014, n=32, sides=5)
    b.lathe([(0.0, 0.07, MACH), (0.262, 0.07), (0.262, 0.0), (0.225, 0.0, HOT, 0.006), (0.2, -0.04, None, 0.01), (0.19, -0.06)],
            MACH, 48, (-0.72, 0, 0.5), (-1, 0, 0), 0.005)
    b.bolt_ring((-0.72, 0, 0.5), (1, 0, 0), 0.243, 10, 0.015, phase=0.5)
    A["hot_out"] = ((-0.8, 0, 0.5), (-1, 0, 0))
    # igniter on top, chamber pressure and wall temperature on the +X side, cables to a connector bracket
    # standing on the base flange
    ign = b.sensor((0, 0, 1.074), (0, 0, 1), 0.03)
    th = math.radians(28)
    rad = Vector((math.cos(th), math.sin(th), 0))
    pc = b.sensor(rad * 0.425 + Vector((0, 0, 0.47)), rad, 0.022)
    th2 = math.radians(-40)
    rad2 = Vector((math.cos(th2), math.sin(th2), 0))
    tw = b.sensor(rad2 * 0.425 + Vector((0, 0, 0.40)), rad2, 0.02)
    thb = math.radians(-11.25)
    radb = Vector((math.cos(thb), math.sin(thb), 0))
    br = radb * 0.50 + Vector((0, 0, 0.14))
    b.box(br + Vector((0, 0, 0.085)), (0.04, 0.09, 0.17), DARK, thb, bevel=0.008)
    jc = [b.connector(br + radb * 0.02 + Vector((0, 0, zc)), radb, 0.014) for zc in (0.20, 0.14, 0.08)]
    b.harness([(math.radians(-6), 1.05), (math.radians(-8), 0.80), (math.radians(-9), 0.60), (thb, 0.36)], 0.011,
              lead_in=[ign, ign + Vector((0, 0, 0.05)), Vector((0.12, -0.01, 1.16))],
              lead_out=[jc[0] + radb * 0.05 + Vector((0, 0, 0.05)), jc[0]])
    b.harness([(th, 0.53), (math.radians(5), 0.42), (thb + 0.05, 0.30)], 0.009,
              lead_in=[pc, pc + rad * 0.03 + Vector((0, 0, 0.02))], lead_out=[jc[1] + radb * 0.04 + Vector((0, 0, 0.03)), jc[1]])
    b.harness([(th2, 0.34), (math.radians(-25), 0.28), (thb - 0.05, 0.20)], 0.009,
              lead_in=[tw, tw + rad2 * 0.03 + Vector((0, 0, -0.02))], lead_out=[jc[2] + radb * 0.04 + Vector((0, 0, 0.02)), jc[2]])
    b.plate_curved(0.425, math.radians(60), math.radians(90), 0.36, 0.46, 0.006)
    return b, A


def gas_generator():
    """Gas generator (unit = pump reference radius): its outlet flange at the origin bolts onto the turbine inlet,
    the body runs outward along +X, two propellant valves sit on top of the injector end."""
    b = Builder(segs=48, rnd=0.006, seed=31, wrap_r=0.1)
    A = {}
    X = (1, 0, 0)
    prof = [(0.0, 0.708, MACH), (0.045, 0.708), (0.045, 0.692, HOT, 0.006)]
    t0 = math.asin(0.05 / 0.2)
    for i in range(1, 6):
        t = t0 + (math.pi / 2 - t0) * i / 5
        prof.append((0.2 * math.sin(t), 0.58 + 0.11 * math.cos(t)))
    prof += [(0.245, 0.58, MACH), (0.245, 0.563), (0.236, 0.563, DARK, 0.0), (0.236, 0.559, MACH, 0.0), (0.245, 0.559),
             (0.245, 0.54), (0.2, 0.54, TURB, 0.015), (0.2, 0.40), (0.208, 0.396, MACH), (0.208, 0.374), (0.2, 0.37, TURB),
             (0.2, 0.22, None, 0.04), (0.115, 0.13), (0.115, 0.055, MACH, 0.012), (0.175, 0.05), (0.175, 0.0), (0.0, 0.0)]
    b.lathe(prof, HOT, 48, (0, 0, 0), X, ref=(0, 0, 1))
    b.bolt_ring((0.58, 0, 0), X, 0.224, 12, 0.0095)
    b.bolt_ring((0.05, 0, 0), X, 0.147, 10, 0.0105, phase=0.5)
    dome_sdf = lambda p: math.sqrt((p.y ** 2 + p.z ** 2) / 0.2 ** 2 + (p.x - 0.58) ** 2 / 0.11 ** 2) - 1.0
    # propellant valves: welded stub, ball-valve body, bolted top flange, actuator box with connector
    for name, y in (("ox_feed", 0.09), ("fuel_feed", -0.09)):
        base = Vector((0.62, y, 0.10))
        b.cyl(base, base + Vector((0, 0, 0.21)), 0.036, HOT, 16, rnd=0.0)
        b.weld_junction(base, (0, 0, 1), 0.036, dome_sdf, 0.007, n=20, sides=4, reach=0.15)
        b.lathe([(0.0, 0.405, MACH), (0.058, 0.405), (0.058, 0.392), (0.046, 0.388, CAST, 0.004), (0.064, 0.36), (0.066, 0.345),
                 (0.06, 0.322), (0.046, 0.31, MACH, 0.004), (0.058, 0.306), (0.058, 0.295), (0.036, 0.295)], CAST, 24,
                (base.x, y, 0.0), (0, 0, 1), 0.004)
        b.bolt_ring((base.x, y, 0.392), (0, 0, 1), 0.051, 4, 0.0065, phase=0.5, washer=False)
        ab = Vector((base.x + 0.095, y, 0.35))
        b.cyl(Vector((base.x + 0.05, y, 0.35)), ab - Vector((0.03, 0, 0)), 0.018, MACH, 12)
        b.box(ab, (0.06, 0.05, 0.075), PAINT, None, bevel=0.008)
        co = b.connector(ab + Vector((0.03, 0, -0.012)), (1, 0, 0), 0.011)
        jb = Vector((0.345, y * 0.3, 0.225))
        b.cable([co, co + Vector((0.025, 0, -0.01)), Vector((0.79, y, 0.25)), Vector((0.73, y * 0.85, 0.185)), Vector((0.64, y * 0.7, 0.175)),
                 Vector((0.52, y * 0.55, 0.215)), Vector((0.42, y * 0.4, 0.222)), jb + Vector((0.025, 0, 0)), jb], 0.007, per=5,
                clips=(0.62,), clip_dir=lambda p: (0, -p.y, -p.z))
        A[name] = ((0.62, y, 0.41), (0, 0, 1))
    # junction box on top of the body, igniter on the injector end, mounting lug below, data plate on the side
    b.box((0.30, 0, 0.222), (0.09, 0.10, 0.05), DARK, None, bevel=0.008)
    b.sensor((0.708, 0, 0), X, 0.014)
    b.prism(b.rounded(0.0, -0.232, 0.03, 0.028, -0.16), Vector((0.30, 0, 0)), (1, 0, 0), (0, 0, 1), 0.026, CAST, bevel=0.005)
    b.lathe([(0, 0.019), (0.012, 0.019), (0.012, -0.019), (0, -0.019)], BOLT, 12, (0.30, 0, -0.232), (0, 1, 0), 0.002)
    b.plate_curved(0.2, -1.95, -1.25, 0.24, 0.35, 0.004, axis=X, ref=(0, 0, 1))
    A["out"] = ((0.0, 0, 0), (-1, 0, 0))
    return b, A


def valve():
    """In-line main propellant valve (unit = line radius), flow along +Z: flanges, cast ball-valve body, bonnet,
    pneumatic actuator with tie rods on +X, solenoid box with connector and cable."""
    b = Builder(segs=40, rnd=0.04, seed=41, wrap_r=0.3)
    prof = [(0.0, 2.2, MACH), (1.15, 2.2), (1.15, 2.17), (1.45, 2.17), (1.45, 1.92), (1.2, 1.92, CAST, 0.03), (1.08, 1.82, None, 0.1),
            (1.08, 1.32, None, 0.12)]
    for i in range(1, 10):
        t = math.pi * i / 10
        prof.append((1.08 + 0.72 * math.sin(t), 1.3 - 2.6 * i / 10))
    prof += [(1.08, -1.32, None, 0.12), (1.08, -1.82, None, 0.1), (1.2, -1.92, MACH, 0.03), (1.45, -1.92), (1.45, -2.17), (1.15, -2.17),
             (1.15, -2.2), (0.0, -2.2)]
    b.lathe(prof, CAST)
    b.bolt_ring((0, 0, 1.92), (0, 0, -1), 1.32, 8, 0.11, phase=0.5)
    b.bolt_ring((0, 0, -1.92), (0, 0, 1), 1.32, 8, 0.11, phase=0.5)
    # bonnet and actuator along +X
    X = (1, 0, 0)
    b.lathe([(0.95, 1.85, MACH), (0.95, 1.72), (0.55, 1.72, CAST, 0.04), (0.5, 1.55), (0.5, 1.2)], CAST, 32, (0, 0, 0), X, 0.03)
    b.bolt_ring((1.72, 0, 0), (-1, 0, 0), 0.78, 6, 0.08, phase=0.5)
    b.box((1.93, 0, 0), (0.16, 1.62, 1.62), PAINT, None, bevel=0.05)
    b.box((3.42, 0, 0), (0.16, 1.62, 1.62), PAINT, None, bevel=0.05)
    b.lathe([(0.8, 3.34, PAINT), (0.8, 2.01)], PAINT, 48, (0, 0, 0), X, 0.0)
    for sy in (-1, 1):
        for sz in (-1, 1):
            q = Vector((0, sy * 0.66, sz * 0.66))
            b.cyl(Vector((1.85, 0, 0)) + q, Vector((3.5, 0, 0)) + q, 0.055, MACH, 8, rnd=0.0)
            b.bolt(Vector((3.5, 0, 0)) + q, X, 0.09, stud=True)
    # position indicator on the end cap, solenoid box, connector and cable down to the body flange
    b.lathe([(0, 0.12, CHROME), (0.18, 0.1), (0.22, 0.0)], MACH, 32, (3.5, 0, 0), X, 0.02)
    b.box((2.55, 0, 1.0), (1.0, 0.7, 0.38), DARK, None, bevel=0.06)
    for sx in (2.15, 2.95):
        for sy in (-0.27, 0.27):
            b.lathe([(0, 0.07), (0.05, 0.07), (0.065, 0.05), (0.065, 0.0)], BOLT, 8, (sx, sy, 1.19), (0, 0, 1), 0.0)
    co = b.connector(Vector((2.8, 0, 1.19)), (0, 0, 1), 0.15)
    # junction box hung under the top flange, between two bolts; the cable runs to its connector
    b.box((0, 1.30, 1.74), (0.30, 0.30, 0.36), DARK, None, bevel=0.04)
    jc = b.connector(Vector((0.15, 1.30, 1.70)), (1, 0, 0), 0.1)
    b.cable([co, co + Vector((0, 0, 0.16)), Vector((2.6, 0.12, 1.68)), Vector((2.0, 0.42, 1.70)), Vector((1.25, 0.98, 1.66)),
             Vector((0.62, 1.27, 1.68)), jc + Vector((0.08, 0, 0)), jc], 0.065, per=6)
    b.plate_curved(0.8, math.radians(65), math.radians(115), 2.25, 3.0, 0.025, axis=X, ref=(0, 0, 1))
    return b, {"in": ((0, 0, -2.2), (0, 0, -1)), "out": ((0, 0, 2.2), (0, 0, 1))}


def actuator():
    """Gimbal actuator (unit = barrel radius), from the top clevis at z = 0 down to the rod-end eye at z = -12.
    Two stretch zones (barrel -2.6..-6.1, rod -7.3..-10.9) let the plugin fit any length: only plain barrel or
    rod surface inside them. Clevis, servo block, gland and eye keep their shape."""
    b = Builder(segs=36, rnd=0.05, seed=51, wrap_r=0.62)
    Y = (0, 1, 0)
    # top clevis: lug around the pin, spherical bearing, pin with head and nut
    b.prism(b.rounded(0.0, 0.0, 0.55, 0.5, -0.86, 12), (0, 0, 0), (1, 0, 0), (0, 0, 1), 0.6, MACH, bevel=0.06)
    b.lathe([(0.44, 0.35, CHROME), (0.44, -0.35)], CHROME, 32, (0, 0, 0), Y, 0.0)
    b.lathe([(0.0, 0.62, MACH), (0.3, 0.62), (0.33, 0.58), (0.33, 0.45), (0.2, 0.45, CHROME), (0.2, -0.45), (0.33, -0.45, MACH),
             (0.33, -0.58), (0.3, -0.62), (0, -0.62)], MACH, 24, (0, 0, 0), Y, 0.02)
    b.lathe([(0.0, 0.62), (0.3, 0.62), (0.38, 0.58), (0.38, 0.45)], BOLT, 6, (0, 0, 0), (0, -1, 0), 0.0, phase=0.3)
    # barrel: head cap, plain barrel (zone 0), gland nut, chrome rod (zone 1)
    b.lathe([(0.0, -0.86, MACH), (0.86, -0.86), (0.95, -0.9), (1.06, -0.96), (1.06, -1.12), (1.0, -1.16, PAINT), (1.0, -6.12),
             (1.0, -6.3, MACH), (1.06, -6.34), (1.2, -6.4), (1.2, -6.95), (0.6, -6.95, DARK), (0.6, -7.06), (0.42, -7.06, CHROME),
             (0.42, -11.05), (0.0, -11.05)], PAINT, 40)
    for k in range(4):   # spanner slots on the gland nut
        th = 2 * math.pi * k / 4 + math.pi / 4
        rad = Vector((math.cos(th), math.sin(th), 0))
        b.box(rad * 1.17 + Vector((0, 0, -6.68)), (0.08, 0.18, 0.3), DARK, th, bevel=0.02)
    # rod end: lock nut and eye with its spherical bearing
    b.lathe([(0.0, -11.02), (0.48, -11.02), (0.6, -11.08), (0.6, -11.28), (0.48, -11.34), (0.0, -11.34)], BOLT, 6, rnd=0.0, phase=0.2)
    b.prism(b.rounded(0.0, -12.0, 0.56, 0.5, -11.3, 12), (0, 0, 0), (1, 0, 0), (0, 0, 1), 0.6, MACH, bevel=0.06)
    b.lathe([(0.44, 0.34, CHROME), (0.44, -0.34)], CHROME, 32, (0, 0, -12.0), Y, 0.0)
    b.lathe([(0.0, 0.37, DARK), (0.2, 0.37), (0.2, 0.30, CHROME), (0.34, 0.3), (0.34, -0.30), (0.2, -0.30, DARK), (0.2, -0.37), (0, -0.37)],
            CHROME, 24, (0, 0, -12.0), Y, 0.02)
    # servo-valve block with cap screws, hydraulic lines with fittings and B-nuts, electrical connector
    b.box((1.25, 0, -1.8), (0.7, 1.1, 1.2), DARK, None, bevel=0.07)
    b.box((1.62, 0, -1.55), (0.12, 0.7, 0.55), PAINT, None, bevel=0.04)
    for sy in (-0.42, 0.42):
        for sz in (-1.3, -2.3):
            b.lathe([(0, 0.12), (0.1, 0.12), (0.12, 0.1), (0.12, 0.0)], BOLT, 12, (1.6, sy, sz), (1, 0, 0), 0.0)
    for y in (-0.3, 0.3):
        b.lathe([(0, 0.13), (0.15, 0.13), (0.17, 0.11), (0.17, 0.0)], BOLT, 6, (1.3, y, -1.2), (0, 0, 1), 0.0, phase=0.1)
        b.lathe([(0, 0.32), (0.17, 0.32), (0.21, 0.28), (0.21, 0.17), (0.17, 0.13)], BOLT, 6, (1.3, y, -1.2), (0, 0, 1), 0.0, phase=0.4)
        path = b.pipe([(1.3, y, -0.9), (1.3, y, -0.7), (1.08, y, -0.47), (0.78, y * 1.35, -0.36)], 0.11, MACH, 12, per=5)
        t = (path[-1] - path[-2]).normalized()
        b.lathe([(0, 0.0), (0.21, 0.0), (0.21, -0.2), (0.17, -0.24), (0.0, -0.24)], BOLT, 6, path[-1] + t * 0.18, t, 0.0, phase=0.2)
    co = b.connector(Vector((1.68, 0, -2.15)), (1, 0, 0), 0.16)
    b.cable([co, co + Vector((0.2, 0, 0.0)), Vector((2.12, 0.42, -1.85)), Vector((1.95, 0.66, -1.1)), Vector((1.4, 0.68, -0.62)),
             Vector((0.85, 0.66, -0.4))], 0.075, per=6)
    b.lathe([(0, 0.0), (0.12, 0.0), (0.12, -0.18), (0.0, -0.18)], CONN, 16, (0.67, 0.66, -0.4), (-1, 0, 0), 0.02)
    b.plate_curved(1.0, math.radians(150), math.radians(210), -2.45, -1.3, 0.02)
    return b, {"zone0_hi": ((0, 0, -2.6), (0, 0, 1)), "zone0_lo": ((0, 0, -6.1), (0, 0, 1)),
               "zone1_hi": ((0, 0, -7.3), (0, 0, 1)), "zone1_lo": ((0, 0, -10.9), (0, 0, 1)),
               "top": ((0, 0, 0), (0, 0, 1)), "end": ((0, 0, -12.0), (0, 0, -1))}


def dome_inlet():
    """Oxidizer inlet on the injector dome (unit = line radius): a reinforcing boss welded into the dome (local +Z =
    dome normal), bolted cover, and a flanged elbow turning outward along +X."""
    b = Builder(segs=56, rnd=0.04, seed=61, wrap_r=0.3)
    b.lathe([(0.0, 0.42, MACH), (1.74, 0.42), (1.82, 0.36, CAST, 0.04), (2.0, 0.18), (2.05, -0.9), (0.0, -0.9)], CAST)
    b.bolt_ring((0, 0, 0.42), (0, 0, 1), 1.5, 10, 0.12, phase=0.5)
    path = b.pipe([(0, 0, 0.3), (0, 0, 1.0), (0.25, 0, 1.75), (1.0, 0, 2.15), (2.0, 0, 2.15)], 1.0, CAST, 28, per=6)
    b.weld_ring((0, 0, 0.42), (0, 0, 1), 1.0, 0.09, 48, 5)
    b.lathe([(0.0, 0.3, MACH), (1.4, 0.3), (1.4, 0.0), (1.16, 0.0, CAST, 0.03), (1.04, -0.2, None, 0.05), (1.0, -0.32)], MACH, 56,
            (2.0, 0, 2.15), (1, 0, 0), 0.03)
    b.weld_ring((1.69, 0, 2.15), (1, 0, 0), 1.0, 0.08, 48, 5)
    b.bolt_ring((2.0, 0, 2.15), (-1, 0, 0), 1.25, 8, 0.11, phase=0.5)
    out = b.sensor((-0.9, 0.9, 0.42), (0, 0, 1), 0.17)
    b.cable([out, out + Vector((0, 0, 0.25)), Vector((-1.35, 1.35, 0.85)), Vector((-1.75, 1.6, 0.45)), Vector((-1.95, 1.7, -0.3))], 0.07,
            clips=(0.82,), clip_dir=lambda p: (-p.x, -p.y, 0))
    return b, {"in": ((2.35, 0, 2.15), (1, 0, 0))}


def injector_ring():
    """Regen return manifold under the injector flange (unit = chamber radius, injector plane at z = 0): a torus
    with eight welded feeders into the chamber, two flanged ports, hanger brackets, and instrumentation bosses on
    the chamber wall."""
    b = Builder(segs=96, rnd=0.004, seed=71, wrap_r=0.08)
    z = -0.17
    b.torus((0, 0, z), 1.12, 0.05, HOT, 96, 16)
    tor = sdf_torus((0, 0, z), 1.12, 0.05)
    for k in range(8):
        th = 2 * math.pi * (k + 0.5) / 8
        rad = Vector((math.cos(th), math.sin(th), 0))
        b.sweep([rad * 0.97 + Vector((0, 0, z)), rad * 1.1 + Vector((0, 0, z))], 0.025, HOT, 16)
        b.weld_junction(rad * 1.1 + Vector((0, 0, z)), -rad, 0.025, tor, 0.006, n=20, sides=4, reach=0.08)
        b.weld_ring(rad * 1.0 + Vector((0, 0, z)), rad, 0.025, 0.006, 16, 4)
        # hanger between feeders: a clamp band around the ring and a strap up to the injector flange
        th2 = 2 * math.pi * k / 8
        r2 = Vector((math.cos(th2), math.sin(th2), 0))
        t2 = Vector((-math.sin(th2), math.cos(th2), 0))
        b.lathe([(0.053, 0.012, MACH), (0.061, 0.012), (0.061, -0.012), (0.053, -0.012)], MACH, 16, r2 * 1.12 + Vector((0, 0, z)), t2, 0.002)
        b.prism([(1.10, z + 0.055), (1.14, z + 0.055), (1.145, -0.0), (1.095, -0.0)], (0, 0, 0), r2, (0, 0, 1), 0.02, MACH, bevel=0.004)
        b.bolt(r2 * 1.12 + Vector((0, 0, -0.035)), t2, 0.008, washer=False)
    for th in (math.radians(100), math.radians(280)):
        rad = Vector((math.cos(th), math.sin(th), 0))
        b.pipe([rad * 1.12 + Vector((0, 0, z)), rad * 1.17 + Vector((0, 0, z - 0.05)), rad * 1.2 + Vector((0, 0, z - 0.16))], 0.045, HOT, 16, per=5)
        p = rad * 1.2 + Vector((0, 0, z - 0.16))
        b.lathe([(0, 0.0, MACH), (0.075, 0.0), (0.075, -0.03), (0.0, -0.03)], MACH, 32, p, (0, 0, 1), 0.004)
        b.bolt_ring(p, (0, 0, 1), 0.062, 4, 0.008, phase=0.5, washer=False)
    for th in (math.radians(60), math.radians(160), math.radians(240), math.radians(320)):
        rad = Vector((math.cos(th), math.sin(th), 0))
        b.sensor(rad * 1.0 + Vector((0, 0, -0.42)), rad, 0.022)
    return b, {}


# name, builder, texel weight (atlas share relative to model units: about the piece's unit size in game / 0.3 m)
PIECES = [
    ("turbopump_dual", lambda: turbopump(True), 1.0),
    ("turbopump_single", lambda: turbopump(False), 1.0),
    ("preburner", preburner, 1.0),
    ("gas_generator", gas_generator, 1.0),
    ("valve", valve, 0.22),
    ("actuator", actuator, 0.13),
    ("dome_inlet", dome_inlet, 0.22),
    ("injector_ring", injector_ring, 1.4),
]
BUDGET = {"turbopump_dual": 45000, "turbopump_single": 45000, "preburner": 15000, "gas_generator": 15000,
          "valve": 8000, "actuator": 8000, "dome_inlet": 8000, "injector_ring": 12000}
LOD1_RATIO = 0.28


# ================================================================================================ atlas + bake

def select_only(obs):
    bpy.ops.object.select_all(action="DESELECT")
    for o in obs: o.select_set(True)
    bpy.context.view_layer.objects.active = obs[0]


def unwrap(obs):
    """Smart UV Project the faces that were not unrolled while modelling (face attribute pe_wrap = 0), weight
    every piece's islands by its texel share, and pack everything into the atlas."""
    for o in obs: o.data.uv_layers.active = o.data.uv_layers["atlas"]
    select_only(obs)
    bpy.ops.object.mode_set(mode="EDIT")
    for o in obs:
        bm = bmesh.from_edit_mesh(o.data)
        W = bm.faces.layers.int.get("pe_wrap")
        for f in bm.faces: f.select_set(W is None or f[W] == 0)
        bmesh.update_edit_mesh(o.data)
    bpy.ops.uv.smart_project(angle_limit=math.radians(66), island_margin=0.002, scale_to_bounds=False)
    bpy.ops.object.mode_set(mode="OBJECT")
    # Smart UV Project normalises its islands into the unit square: scale them back to object units, like the
    # unrolled ones, so every face gets its fair share of the atlas
    for o in obs:
        me = o.data
        me.calc_loop_triangles()
        tri = np.empty(len(me.loop_triangles) * 3, np.int32); me.loop_triangles.foreach_get("loops", tri); tri = tri.reshape(-1, 3)
        poly = np.empty(len(me.loop_triangles), np.int32); me.loop_triangles.foreach_get("polygon_index", poly)
        W = me.attributes.get("pe_wrap")
        wf = np.zeros(len(me.polygons), np.int32)
        if W: W.data.foreach_get("value", wf)
        smart = wf[poly] == 0
        if not smart.any(): continue
        uv = np.empty(len(me.loops) * 2, np.float32); me.uv_layers["atlas"].data.foreach_get("uv", uv); uv = uv.reshape(-1, 2)
        vi = np.empty(len(me.loops), np.int32); me.loops.foreach_get("vertex_index", vi)
        co = np.empty(len(me.vertices) * 3, np.float32); me.vertices.foreach_get("co", co); co = co.reshape(-1, 3)
        t3 = co[vi[tri[smart]]]
        a3 = 0.5 * np.linalg.norm(np.cross(t3[:, 1] - t3[:, 0], t3[:, 2] - t3[:, 0]), axis=1).sum()
        t2 = uv[tri[smart]]
        a2 = 0.5 * np.abs((t2[:, 1, 0] - t2[:, 0, 0]) * (t2[:, 2, 1] - t2[:, 0, 1]) - (t2[:, 2, 0] - t2[:, 0, 0]) * (t2[:, 1, 1] - t2[:, 0, 1])).sum()
        lstart = np.empty(len(me.polygons), np.int32); me.polygons.foreach_get("loop_start", lstart)
        ltotal = np.empty(len(me.polygons), np.int32); me.polygons.foreach_get("loop_total", ltotal)
        loop_smart = np.repeat(wf == 0, ltotal)
        uv[loop_smart] *= math.sqrt(a3 / max(a2, 1e-12))
        me.uv_layers["atlas"].data.foreach_set("uv", uv.ravel())
    # texel weighting: a piece's share of the atlas follows its typical size in game, not its model units
    for o in obs:
        f = o.get("pe_texel", 1.0)
        uv = np.empty(len(o.data.loops) * 2, np.float32)
        o.data.uv_layers["atlas"].data.foreach_get("uv", uv)
        o.data.uv_layers["atlas"].data.foreach_set("uv", uv * f)
    bpy.ops.object.mode_set(mode="EDIT")
    bpy.ops.mesh.select_all(action="SELECT")
    bpy.ops.uv.pack_islands(rotate=True, scale=True, margin_method="FRACTION", margin=0.0022, shape_method="CONCAVE")
    bpy.ops.object.mode_set(mode="OBJECT")


def bake(obs, size, quality, out_dir, debug=False):
    sc = bpy.context.scene
    sc.render.engine = "CYCLES"
    sc.cycles.device = "CPU"
    sc.cycles.use_denoising = False
    sc.render.bake.margin = max(4, size // 256)
    mats = {m for o in obs for m in o.data.materials if m and m.node_tree.nodes.get("pe_emit")}
    imgs = {}
    for k in ("mask", "color", "rough", "normal"):
        old = bpy.data.images.get("pe_" + k)
        if old: bpy.data.images.remove(old)
        imgs[k] = bpy.data.images.new("pe_" + k, size, size, alpha=True, float_buffer=True)
        imgs[k].colorspace_settings.name = "Non-Color"
    for m in mats:
        m.node_tree.nodes["pe_mask"].image = imgs["mask"]
    select_only(obs)
    samples = {"mask": 32, "color": 16, "rough": 12, "normal": 16}
    for k in ("mask", "color", "rough", "normal"):
        for m in mats:
            nt = m.node_tree
            nd = nt.nodes
            nd["pe_bake"].image = imgs[k]
            nt.nodes.active = nd["pe_bake"]
            if k == "normal":
                nt.links.new(nd["pe_normal"].outputs["Normal"], nd["pe_bsdf"].inputs["Normal"])
                nt.links.new(nd["pe_bsdf"].outputs[0], nd["pe_output"].inputs["Surface"])
            else:
                src = {"mask": nd["pe_mask_out"].outputs[0], "color": sock(nd["pe_color"].outputs, "Result_Color"),
                       "rough": nd["pe_rough"].outputs[0]}[k]
                nt.links.new(src, nd["pe_emit"].inputs["Color"])
                nt.links.new(nd["pe_emit"].outputs[0], nd["pe_output"].inputs["Surface"])
        sc.cycles.samples = max(1, int(round(samples[k] * quality)))
        log(f"bake {k} ({sc.cycles.samples} samples, {size}px)")
        if k == "normal":
            bpy.ops.object.bake(type="NORMAL", normal_space="TANGENT", normal_r="POS_X", normal_g="POS_Y", normal_b="POS_Z",
                                uv_layer="atlas", use_clear=True)
        else:
            bpy.ops.object.bake(type="EMIT", uv_layer="atlas", use_clear=True)
    # leave the materials showing their colour
    for m in mats:
        nd = m.node_tree.nodes
        m.node_tree.links.new(sock(nd["pe_color"].outputs, "Result_Color"), nd["pe_emit"].inputs["Color"])
        m.node_tree.links.new(nd["pe_emit"].outputs[0], nd["pe_output"].inputs["Surface"])
        nd["pe_bake"].image = None

    def px(img):
        a = np.empty(size * size * 4, np.float32)
        img.pixels.foreach_get(a)
        return a.reshape(size, size, 4)

    mask, col, rough, nrm = px(imgs["mask"]), px(imgs["color"]), px(imgs["rough"]), px(imgs["normal"])
    if debug:   # the intermediate maps, for tuning the materials
        save_png(os.path.join(out_dir, "debug_mask.png"), np.clip(mask[..., :3], 0, 1))
        lin_c = np.clip(col[..., :3], 0, 1)
        save_png(os.path.join(out_dir, "debug_color.png"), np.where(lin_c <= 0.0031308, lin_c * 12.92, 1.055 * np.power(lin_c, 1 / 2.4) - 0.055))
        save_png(os.path.join(out_dir, "debug_rough.png"), np.repeat(np.clip(rough[..., 0:1], 0, 1), 3, axis=2))
    ao = np.clip(mask[..., 0:1], 0, 1)
    shade = 0.38 + 0.62 * ao ** 1.3                         # baked ambient occlusion
    lin = np.clip(col[..., :3] * shade, 0, 1)
    srgb = np.where(lin <= 0.0031308, lin * 12.92, 1.055 * np.power(lin, 1 / 2.4) - 0.055)
    r = np.clip(rough[..., 0:1], 0, 1)
    gloss = np.clip((0.9 - r) / 0.78, 0, 1) ** 1.25            # bare metal glossy, grime and soot matte
    save_png(os.path.join(out_dir, "kit_atlas.png"), np.concatenate([srgb, gloss], axis=2))
    n = np.clip(nrm[..., :3], 0, 1)
    empty = (nrm[..., 3] <= 0) | (np.abs(nrm[..., :3]).sum(axis=2) < 1e-6)
    n[empty] = (0.5, 0.5, 1.0)
    save_png(os.path.join(out_dir, "kit_atlas_n.png"), n)


def save_png(path, arr):
    """Write an (H, W, 3|4) float array (rows bottom-up, as Blender stores them) to an 8-bit PNG, values as-is."""
    h, w, c = arr.shape
    img = bpy.data.images.new("pe_save", w, h, alpha=(c == 4))
    img.colorspace_settings.name = "Non-Color"
    if c == 3: arr = np.concatenate([arr, np.ones((h, w, 1), np.float32)], axis=2)
    img.pixels.foreach_set(np.ascontiguousarray(arr, dtype=np.float32).ravel())
    img.filepath_raw = path
    img.file_format = "PNG"
    sc = bpy.context.scene
    sc.render.image_settings.color_mode = "RGBA" if c == 4 else "RGB"
    img.save()
    bpy.data.images.remove(img)
    log(f"wrote {path}")


# ================================================================================================ export

def mesh_streams(me):
    """Deduplicated vertex streams (Unity axes) and triangle indices (Unity winding) of an evaluated mesh,
    with MikkTSpace tangents on the atlas UV map."""
    me.calc_loop_triangles()
    me.calc_tangents(uvmap="atlas")
    nl = len(me.loops)
    vi = np.empty(nl, np.int32); me.loops.foreach_get("vertex_index", vi)
    co = np.empty(len(me.vertices) * 3, np.float32); me.vertices.foreach_get("co", co); co = co.reshape(-1, 3)
    nor = np.empty(nl * 3, np.float32); me.corner_normals.foreach_get("vector", nor); nor = nor.reshape(-1, 3)
    tan = np.empty(nl * 3, np.float32); me.loops.foreach_get("tangent", tan); tan = tan.reshape(-1, 3)
    sgn = np.empty(nl, np.float32); me.loops.foreach_get("bitangent_sign", sgn)
    uv = np.empty(nl * 2, np.float32); me.uv_layers["atlas"].data.foreach_get("uv", uv); uv = uv.reshape(-1, 2)
    tri = np.empty(len(me.loop_triangles) * 3, np.int32); me.loop_triangles.foreach_get("loops", tri)
    # exact unit normals and tangents orthogonal to them (in double precision: a tangent almost along the
    # normal, at a degenerate UV corner, loses its orthogonality in float32)
    nor = nor.astype(np.float64)
    nor = nor / np.maximum(np.linalg.norm(nor, axis=1, keepdims=True), 1e-12)
    tan = tan.astype(np.float64)
    tan = tan - nor * np.sum(tan * nor, axis=1, keepdims=True)
    bad = np.linalg.norm(tan, axis=1) < 1e-3
    if bad.any():   # degenerate UVs: any perpendicular will do
        alt = np.where(np.abs(nor[bad, 0:1]) < 0.9, [[1.0, 0, 0]], [[0, 1.0, 0]])
        tan[bad] = alt - nor[bad] * np.sum(alt * nor[bad], axis=1, keepdims=True)
    tan = tan / np.linalg.norm(tan, axis=1, keepdims=True)
    nor, tan = nor.astype(np.float32), tan.astype(np.float32)
    sgn = np.where(sgn < 0, -1.0, 1.0).astype(np.float32)
    key = np.concatenate([vi[:, None].astype(np.float64), np.round(nor, 4), np.round(uv, 5), np.round(tan, 3), sgn[:, None]], axis=1)
    _, first, inv = np.unique(key, axis=0, return_index=True, return_inverse=True)
    inv = inv.ravel()
    pos = co[vi[first]][:, [0, 2, 1]]                 # Blender (x, y, z) -> Unity (x, z, y)
    n_u = nor[first][:, [0, 2, 1]]
    t_u = np.concatenate([tan[first][:, [0, 2, 1]], -sgn[first][:, None]], axis=1)   # the swap mirrors: w = -sign
    idx = inv[tri].reshape(-1, 3)[:, [0, 2, 1]]       # and reverses the winding
    return pos, n_u, t_u, uv[first], idx.ravel().astype(np.int32)


def piece_anchors(ob):
    out = []
    inv = ob.matrix_world.inverted()
    for ch in sorted(ob.children, key=lambda c: c.name):
        if ch.type != "EMPTY" or not ch.name.startswith(ob.name + "."): continue
        m = inv @ ch.matrix_world
        p = m.translation
        d = (m.to_3x3() @ Vector((0, 0, 1))).normalized()
        out.append((ch.name[len(ob.name) + 1:], (p.x, p.z, p.y), (d.x, d.z, d.y)))
    return out


def write_pem(path, streams, anchors):
    pos, nor, tan, uv, idx = streams
    with open(path, "wb") as f:
        f.write(b"PEM1")
        f.write(struct.pack("<iii", 2, len(pos), len(idx)))
        for arr in (pos, nor, tan, uv):
            f.write(np.ascontiguousarray(arr, dtype="<f4").tobytes())
        f.write(np.ascontiguousarray(idx, dtype="<i4").tobytes())
        f.write(struct.pack("<i", len(anchors)))
        for name, p, d in anchors:
            nb = name.encode("utf-8")
            f.write(struct.pack("<i", len(nb))); f.write(nb)
            f.write(struct.pack("<6f", *p, *d))


def export_piece(ob, out_dir):
    dg = bpy.context.evaluated_depsgraph_get()
    eo = ob.evaluated_get(dg)
    me = eo.to_mesh()
    s0 = mesh_streams(me)
    eo.to_mesh_clear()
    anchors = piece_anchors(ob)
    write_pem(os.path.join(out_dir, ob.name + ".pem"), s0, anchors)
    tris0 = len(s0[4]) // 3
    budget = BUDGET.get(ob.name)
    log(f"{ob.name}: {len(s0[0])} verts, {tris0} tris (budget {budget}), anchors {[a[0] for a in anchors]}")
    if budget and tris0 > budget: log(f"WARNING {ob.name} is over its triangle budget")
    # LOD1: Decimate (collapse) a copy of the final LOD0 mesh; UVs follow, tangents are recomputed
    lod_me = bpy.data.meshes.new_from_object(eo, preserve_all_data_layers=True, depsgraph=dg)
    tmp = bpy.data.objects.new(ob.name + "_lod1", lod_me)
    bpy.context.scene.collection.objects.link(tmp)
    dec = tmp.modifiers.new("pe_decimate", "DECIMATE")
    dec.decimate_type, dec.use_collapse_triangulate = "COLLAPSE", True
    wn = tmp.modifiers.new("pe_weighted", "WEIGHTED_NORMAL")
    wn.mode, wn.weight, wn.keep_sharp = "FACE_AREA", 50, True
    ratio = LOD1_RATIO
    for _ in range(4):
        dec.ratio = ratio
        dg = bpy.context.evaluated_depsgraph_get()
        et = tmp.evaluated_get(dg)
        m1 = et.to_mesh()
        s1 = mesh_streams(m1)
        et.to_mesh_clear()
        if len(s1[4]) // 3 <= 0.30 * tris0: break
        ratio *= 0.9
    write_pem(os.path.join(out_dir, ob.name + ".lod1.pem"), s1, anchors)
    log(f"{ob.name}.lod1: {len(s1[0])} verts, {len(s1[4]) // 3} tris ({100.0 * len(s1[4]) / max(1, len(s0[4])):.0f} %)")
    bpy.data.objects.remove(tmp)
    bpy.data.meshes.remove(lod_me)


# ================================================================================================ main

def main():
    o = args()
    os.makedirs(o["out"], exist_ok=True)
    pieces = [p for p in PIECES if not o["only"] or p[0] in o["only"]]
    if o["export_only"]:
        obs = [bpy.data.objects[n] for n, _, _ in pieces if n in bpy.data.objects]
        for ob in obs: add_modifiers(ob)
        bpy.context.view_layer.update()
        if o["unwrap"]: unwrap(obs)
    else:
        bpy.ops.wm.read_factory_settings(use_empty=True)
        mats = make_materials()
        obs = []
        for i, (n, fn, texel) in enumerate(pieces):
            b, anchors = fn()
            ob = b.to_object(n, mats)
            ob["pe_texel"] = texel
            for k, (pos, d) in anchors.items():
                anchor(ob, k, pos, d)
            ob.location = (16.0 * (i % 4), 16.0 * (i // 4), 0)   # apart, so pieces don't occlude each other in the AO bake
            obs.append(ob)
            log(f"modelled {n}: {len(ob.data.polygons)} faces")
        bpy.context.view_layer.update()
        unwrap(obs)
        log("unwrapped")
    if o["bake"]:
        bake(obs, o["size"], o["quality"], o["out"], o["debug"])
    for ob in obs:
        export_piece(ob, o["out"])
    if not o["export_only"] and not o["only"]:
        bpy.ops.wm.save_as_mainfile(filepath=os.path.join(HERE, "kit.blend"), compress=True)
    log("done")


if __name__ == "__main__":
    main()
