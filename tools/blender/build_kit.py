"""
ProcEngines kit pieces: modelled, UV-atlased, baked and exported headless from Blender (5.2).

    blender -b --factory-startup -P tools/blender/build_kit.py -- [--out DIR] [--size 2048] [--samples 64]
        Build every piece from this script, bake, export, and save tools/blender/kit.blend.
    blender -b tools/blender/kit.blend -P tools/blender/build_kit.py -- --export-only [--out DIR]
        Bake and export the scene as it is in kit.blend (after hand edits in Blender).

Conventions (one unit = the pump's reference radius, the plugin scales it from the turbopump mass):
  * Blender +Z = up (Unity +Y), +X = radially outward from the engine axis, +Y = tangential (the "+90 deg" side).
  * Anchors are empties parented to their piece and named "<piece>.<anchor>"; their local +Z is the direction.
  * Every piece shares one UV atlas; the baked texture is base colour x ambient occlusion.

Output (DIR, default GameData/ProcEngines/PluginData/Kit): <piece>.pem for each piece, kit_atlas.png.
.pem layout (little-endian): b"PEM1", int32 version (1), int32 vertexCount, int32 indexCount,
  float32[3*V] positions, float32[3*V] normals, float32[2*V] uvs, int32[I] indices (Unity winding),
  int32 anchorCount, then per anchor: int32 nameLength, utf-8 name, float32[3] position, float32[3] direction.
Positions/directions are already in Unity axes (x, z, y).
"""
import bpy, bmesh, math, os, struct, sys
from mathutils import Vector, Matrix
import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.abspath(os.path.join(HERE, "..", ".."))


def args():
    a = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    o = {"out": os.path.join(REPO, "GameData", "ProcEngines", "PluginData", "Kit"), "size": 2048, "samples": 64, "export_only": False}
    i = 0
    while i < len(a):
        if a[i] == "--out": o["out"] = a[i + 1]; i += 1
        elif a[i] == "--size": o["size"] = int(a[i + 1]); i += 1
        elif a[i] == "--samples": o["samples"] = int(a[i + 1]); i += 1
        elif a[i] == "--export-only": o["export_only"] = True
        i += 1
    return o


# ---------------------------------------------------------------------------------------------- materials

MATERIALS = [
    # name, base colour (linear), noise strength, roughness
    ("housing", (0.42, 0.43, 0.45), 0.10, 0.45),      # cast aluminium / Inconel housings
    ("turbine", (0.20, 0.16, 0.14), 0.25, 0.55),      # heat-tinted Inconel
    ("fitting", (0.60, 0.60, 0.62), 0.05, 0.3),       # flanges, bolts, bright steel
    ("dark", (0.10, 0.10, 0.11), 0.10, 0.6),          # seals, ribs, insulation
    ("tint", (0.30, 0.22, 0.14), 0.30, 0.5),          # straw/bronze heat tint near the turbine
]
M_HOUSING, M_TURBINE, M_FITTING, M_DARK, M_TINT = range(5)


def make_materials():
    mats = []
    for name, col, noise, rough in MATERIALS:
        m = bpy.data.materials.get("pe_" + name) or bpy.data.materials.new("pe_" + name)
        m.use_nodes = True
        nt = m.node_tree
        nt.nodes.clear()
        out = nt.nodes.new("ShaderNodeOutputMaterial")
        bsdf = nt.nodes.new("ShaderNodeBsdfPrincipled")
        bsdf.inputs["Roughness"].default_value = rough
        coord = nt.nodes.new("ShaderNodeTexCoord")
        tex = nt.nodes.new("ShaderNodeTexNoise")
        tex.inputs["Scale"].default_value = 6.0
        tex.inputs["Detail"].default_value = 6.0
        nt.links.new(coord.outputs["Object"], tex.inputs["Vector"])
        rng = nt.nodes.new("ShaderNodeMapRange")
        rng.inputs["To Min"].default_value = 1 - noise
        rng.inputs["To Max"].default_value = 1 + noise * 0.6
        nt.links.new(tex.outputs["Fac"], rng.inputs["Value"])
        mix = nt.nodes.new("ShaderNodeMix")
        mix.data_type = "RGBA"
        mix.blend_type = "MULTIPLY"
        mix.inputs["Factor"].default_value = 1.0
        sock = lambda socks, ident: next(s for s in socks if s.identifier == ident)
        sock(mix.inputs, "A_Color").default_value = (*col, 1)
        nt.links.new(rng.outputs["Result"], sock(mix.inputs, "B_Color"))   # the float becomes a grey colour
        nt.links.new(sock(mix.outputs, "Result_Color"), bsdf.inputs["Base Color"])
        nt.links.new(bsdf.outputs["BSDF"], out.inputs["Surface"])
        mats.append(m)
    return mats


# ---------------------------------------------------------------------------------------------- bmesh helpers

class Builder:
    def __init__(self):
        self.bm = bmesh.new()

    def face(self, verts, mat):
        f = self.bm.faces.new(verts)
        f.material_index = mat
        f.smooth = True
        return f

    def lathe(self, prof, mat, segs=32, origin=(0, 0, 0), mats=None):
        """Revolve an (r, z) profile about +Z. A profile running outward/downward faces outward.
        `mats` optionally gives a material per profile span."""
        o = Vector(origin)
        rings = []
        for r, z in prof:
            if r < 1e-6:
                rings.append([self.bm.verts.new(o + Vector((0, 0, z)))])
            else:
                rings.append([self.bm.verts.new(o + Vector((r * math.cos(2 * math.pi * j / segs), r * math.sin(2 * math.pi * j / segs), z))) for j in range(segs)])
        for i in range(len(rings) - 1):
            A, B = rings[i], rings[i + 1]
            m = mats[i] if mats else mat
            if len(A) == 1 and len(B) == 1:
                continue
            for j in range(segs):
                j2 = (j + 1) % segs
                if len(A) == 1: self.face([A[0], B[j], B[j2]], m)
                elif len(B) == 1: self.face([A[j], B[0], A[j2]], m)
                else: self.face([A[j], B[j], B[j2], A[j2]], m)

    def sweep(self, pts, radii, mat, segs=12, cap0=True, cap1=True, closed=False):
        """Tube through points (parallel-transported frames), outward normals, optional end caps."""
        pts = [Vector(p) for p in pts]
        n = len(pts)
        if isinstance(radii, (int, float)): radii = [radii] * n
        tans = []
        for i in range(n):
            if closed: t = pts[(i + 1) % n] - pts[(i - 1) % n]
            else: t = pts[min(i + 1, n - 1)] - pts[max(i - 1, 0)]
            tans.append(t.normalized())
        u = tans[0].orthogonal().normalized()
        rings = []
        for i in range(n):
            t = tans[i]
            u = (u - u.dot(t) * t).normalized()
            v = t.cross(u)
            rings.append([self.bm.verts.new(pts[i] + (u * math.cos(2 * math.pi * j / segs) + v * math.sin(2 * math.pi * j / segs)) * radii[i]) for j in range(segs)])
        spans = n if closed else n - 1
        for i in range(spans):
            A, B = rings[i], rings[(i + 1) % n]
            for j in range(segs):
                j2 = (j + 1) % segs
                self.face([A[j], A[j2], B[j2], B[j]], mat)
        if not closed:
            if cap0: self.face(list(reversed(rings[0])), mat)
            if cap1: self.face(rings[-1], mat)

    def cylinder(self, p0, p1, r, mat, segs=16, caps=True):
        self.sweep([p0, p1], r, mat, segs, caps, caps)

    def torus(self, centre, R, r, mat, segs=48, sides=12, z_axis=(0, 0, 1)):
        c = Vector(centre)
        w = Vector(z_axis).normalized()
        a = w.orthogonal().normalized()
        b = w.cross(a)
        pts = [c + (a * math.cos(2 * math.pi * i / segs) + b * math.sin(2 * math.pi * i / segs)) * R for i in range(segs)]
        self.sweep(pts, r, mat, sides, closed=True)

    def bolt_ring(self, centre, axis, R, count, r, h, mat=M_FITTING, phase=0.0):
        c = Vector(centre)
        w = Vector(axis).normalized()
        a = w.orthogonal().normalized()
        b = w.cross(a)
        for k in range(count):
            th = 2 * math.pi * (k + phase) / count
            p = c + (a * math.cos(th) + b * math.sin(th)) * R
            self.cylinder(p, p + w * h, r, mat, segs=6)

    def box(self, centre, size, rot_z, mat):
        M = Matrix.Translation(Vector(centre)) @ Matrix.Rotation(rot_z, 4, "Z") @ Matrix.Diagonal((*size, 1))
        res = bmesh.ops.create_cube(self.bm, size=1.0, matrix=M)
        for f in {f for v in res["verts"] for f in v.link_faces}:
            f.material_index = mat
            f.smooth = True

    def to_object(self, name, mats, sharp_deg=32):
        bm = self.bm
        bm.normal_update()
        for e in bm.edges:
            if len(e.link_faces) == 2 and e.calc_face_angle(0) > math.radians(sharp_deg):
                e.smooth = False
        me = bpy.data.meshes.new(name)
        bm.to_mesh(me)
        bm.free()
        for m in mats: me.materials.append(m)
        ob = bpy.data.objects.new(name, me)
        bpy.context.scene.collection.objects.link(ob)
        return ob


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


# ---------------------------------------------------------------------------------------------- pieces

def volute(b, z, base_r, r0, r1, sign, mat):
    """Scroll casing around the pump: cross-section grows r0 -> r1 over a turn, then a straight tangential
    discharge with a bolted flange. sign = +1 discharges toward +Y, -1 toward -Y. Returns (end, direction)."""
    pts, radii = [], []
    N = 56
    for i in range(N + 1):
        th = 2 * math.pi * i / N
        rc = r0 + (r1 - r0) * i / N
        R = base_r + rc * 0.8
        pts.append(Vector((R * math.cos(th), sign * R * math.sin(th), z)))
        radii.append(rc)
    d = (pts[-1] - pts[-2]).normalized()
    for k in (1, 2, 3):
        pts.append(pts[N] + d * (0.12 * k))
        radii.append(r1)
    b.sweep(pts, radii, mat, segs=14, cap0=True, cap1=False)
    end = pts[-1]
    b.cylinder(end - d * 0.02, end + d * 0.05, r1 * 1.35, M_FITTING, segs=16)
    b.bolt_ring(end - d * 0.02, -d, r1 * 1.2, 8, 0.018, 0.03)
    return end + d * 0.05, d


def turbopump(dual=True):
    """Single-shaft turbopump: oxidizer pump on top (axial inlet), fuel pump below it (dual only), bearing
    section, turbine with its toroidal hot-gas inlet manifold, and the turbine exhaust at the bottom."""
    b = Builder()
    anchors = {}
    # oxidizer inlet flange and inducer neck, flaring into the pump housing
    b.lathe([(0.0, 1.13), (0.40, 1.13), (0.40, 1.07), (0.31, 1.07), (0.31, 0.88), (0.45, 0.80), (0.60, 0.70),
             (0.62, 0.60), (0.62, 0.36), (0.68, 0.36), (0.68, 0.30), (0.40, 0.30)], M_HOUSING,
            mats=[M_FITTING, M_FITTING, M_FITTING, M_HOUSING, M_HOUSING, M_HOUSING, M_HOUSING, M_HOUSING, M_FITTING, M_FITTING, M_FITTING])
    b.bolt_ring((0, 0, 1.13), (0, 0, 1), 0.355, 12, 0.022, 0.025)
    b.bolt_ring((0, 0, 0.36), (0, 0, 1), 0.65, 16, 0.02, 0.025)
    anchors["inlet"] = ((0, 0, 1.16), (0, 0, 1))
    end, d = volute(b, 0.5, 0.55, 0.07, 0.17, +1, M_HOUSING)
    anchors["ox_out" if dual else "out"] = (tuple(end), tuple(d))
    # instrumentation bosses
    for th in (2.2, 3.6):
        p = Vector((0.62 * math.cos(th), 0.62 * math.sin(th), 0.68))
        b.cylinder(p, p + Vector((math.cos(th), math.sin(th), 0)) * 0.08, 0.035, M_FITTING, segs=8)

    # inter-stage seal / bearing section with ribs
    zb = 0.30
    if dual:
        b.lathe([(0.40, zb), (0.32, zb - 0.02), (0.32, -0.02), (0.58, -0.02), (0.58, -0.07)], M_DARK)
        for k in range(8):
            th = 2 * math.pi * k / 8 + math.pi / 8
            b.box((0.42 * math.cos(th), 0.42 * math.sin(th), 0.14), (0.2, 0.025, 0.3), th, M_DARK)
        b.bolt_ring((0, 0, -0.02), (0, 0, 1), 0.53, 16, 0.018, 0.022)
        # fuel pump
        b.lathe([(0.58, -0.07), (0.55, -0.07), (0.55, -0.42), (0.48, -0.48), (0.30, -0.50)], M_HOUSING,
                mats=[M_FITTING, M_HOUSING, M_HOUSING, M_HOUSING])
        end, d = volute(b, -0.25, 0.5, 0.05, 0.13, -1, M_HOUSING)
        anchors["fuel_out"] = (tuple(end), tuple(d))
        # fuel inlet: an elbow from the pump side turning up, ending in a flange
        th = math.radians(125)
        c, s = math.cos(th), math.sin(th)
        elbow = [Vector((0.45 * c, 0.45 * s, -0.2)), Vector((0.72 * c, 0.72 * s, -0.2)), Vector((0.82 * c, 0.82 * s, -0.08)), Vector((0.82 * c, 0.82 * s, 0.12))]
        b.sweep(elbow, 0.1, M_HOUSING, segs=14, cap0=True, cap1=False)
        top = elbow[-1]
        b.cylinder(top, top + Vector((0, 0, 0.05)), 0.14, M_FITTING, segs=16)
        b.bolt_ring(top + Vector((0, 0, 0.05)), (0, 0, 1), 0.12, 8, 0.016, 0.02)
        anchors["fuel_in"] = (tuple(top + Vector((0, 0, 0.07))), (0, 0, 1))
        zt = -0.50
    else:
        b.lathe([(0.40, zb), (0.30, zb - 0.02), (0.30, -0.10), (0.30, -0.12)], M_DARK)
        for k in range(8):
            th = 2 * math.pi * k / 8 + math.pi / 8
            b.box((0.38 * math.cos(th), 0.38 * math.sin(th), 0.1), (0.16, 0.025, 0.3), th, M_DARK)
        zt = -0.12

    # bearing neck and turbine
    b.lathe([(0.30, zt), (0.27, zt - 0.02), (0.27, zt - 0.20), (0.62, zt - 0.20), (0.62, zt - 0.25), (0.82, zt - 0.27),
             (0.86, zt - 0.36), (0.86, zt - 0.50), (0.80, zt - 0.57), (0.52, zt - 0.58), (0.44, zt - 0.78),
             (0.50, zt - 0.78), (0.50, zt - 0.84), (0.0, zt - 0.84)], M_TURBINE,
            mats=[M_DARK, M_DARK, M_FITTING, M_FITTING, M_TINT, M_TURBINE, M_TURBINE, M_TURBINE, M_TURBINE, M_TINT, M_FITTING, M_FITTING, M_FITTING])
    b.bolt_ring((0, 0, zt - 0.20), (0, 0, 1), 0.58, 20, 0.018, 0.022)
    b.bolt_ring((0, 0, zt - 0.84), (0, 0, -1), 0.46, 12, 0.018, 0.022)
    # toroidal hot-gas manifold around the turbine, fed from the side
    b.torus((0, 0, zt - 0.43), 0.9, 0.11, M_TINT, segs=48, sides=12)
    th = math.radians(30)
    c, s = math.cos(th), math.sin(th)
    b.sweep([(0.9 * c, 0.9 * s, zt - 0.43), (1.12 * c, 1.12 * s, zt - 0.43), (1.25 * c, 1.25 * s, zt - 0.43)], 0.1, M_TINT, segs=12, cap0=True, cap1=False)
    p = Vector((1.25 * c, 1.25 * s, zt - 0.43))
    b.cylinder(p, p + Vector((c, s, 0)) * 0.05, 0.14, M_FITTING, segs=16)
    anchors["turbine_in"] = ((1.32 * c, 1.32 * s, zt - 0.43), (c, s, 0))
    anchors["turbine_exhaust"] = ((0, 0, zt - 0.86), (0, 0, -1))

    return b, anchors


def preburner():
    """Preburner (unit = pump reference radius) standing on the turbine: bolted base flange at z = 0, jacketed
    body, injector dome with two feed inlets, and the hot-gas outlet elbow turning toward the engine axis (-X)."""
    b = Builder()
    A = {}
    prof = [(0.0, 1.06)]
    for i in range(1, 9):
        t = i / 8 * math.pi / 2
        prof.append((0.42 * math.sin(t), 0.78 + 0.28 * math.cos(t)))
    prof += [(0.46, 0.78), (0.46, 0.73), (0.42, 0.73), (0.42, 0.14), (0.52, 0.14), (0.52, 0.0), (0.0, 0.0)]
    mats = [M_TURBINE] * 8 + [M_FITTING, M_FITTING, M_FITTING, M_TINT, M_FITTING, M_FITTING, M_FITTING]
    b.lathe(prof, M_TINT, mats=mats)
    b.bolt_ring((0, 0, 0.14), (0, 0, 1), 0.48, 16, 0.02, 0.024)
    b.bolt_ring((0, 0, 0.78), (0, 0, 1), 0.44, 14, 0.016, 0.02)
    for z in (0.32, 0.55):
        b.torus((0, 0, z), 0.43, 0.025, M_FITTING, segs=40, sides=8)
    # feed inlets on the dome: oxidizer and fuel, each with a small flanged stub
    for name, x in (("ox_feed", 0.16), ("fuel_feed", -0.16)):
        p0 = Vector((x, 0.12, 0.98))
        p1 = p0 + Vector((0, 0, 0.18))
        b.cylinder(p0, p1, 0.06, M_HOUSING, segs=12)
        b.cylinder(p1, p1 + Vector((0, 0, 0.035)), 0.09, M_FITTING, segs=12)
        A[name] = (tuple(p1 + Vector((0, 0, 0.04))), (0, 0, 1))
    # hot-gas outlet toward the injector dome
    pts = [(-0.3, 0, 0.5), (-0.55, 0, 0.5), (-0.72, 0, 0.5)]
    b.sweep(pts, 0.19, M_TINT, segs=16, cap0=True, cap1=False)
    b.cylinder((-0.72, 0, 0.5), (-0.79, 0, 0.5), 0.26, M_FITTING, segs=18)
    b.bolt_ring((-0.72, 0, 0.5), (1, 0, 0), 0.23, 10, 0.016, 0.02)
    A["hot_out"] = ((-0.8, 0, 0.5), (-1, 0, 0))
    # instrumentation boss
    b.cylinder((0.3, -0.3, 0.45), (0.38, -0.38, 0.45), 0.035, M_FITTING, segs=8)
    return b, A


def gas_generator():
    """Gas generator (unit = pump reference radius): its outlet flange at the origin bolts onto the turbine inlet,
    the body runs outward along +X, two propellant valves sit on top of the injector end."""
    b = Builder()
    A = {}
    # built along +Z, then turned so +Z becomes +X (up becomes +Z)
    prof = [(0.0, 0.70)]
    for i in range(1, 7):
        t = i / 6 * math.pi / 2
        prof.append((0.2 * math.sin(t), 0.58 + 0.12 * math.cos(t)))
    prof += [(0.23, 0.58), (0.23, 0.54), (0.2, 0.54), (0.2, 0.2), (0.11, 0.12), (0.11, 0.05), (0.17, 0.05), (0.17, 0.0), (0.0, 0.0)]
    mats = [M_HOUSING] * 6 + [M_FITTING, M_FITTING, M_FITTING, M_TINT, M_TINT, M_TINT, M_FITTING, M_FITTING, M_FITTING]
    b.lathe(prof, M_HOUSING, segs=24, mats=mats)
    b.bolt_ring((0, 0, 0.58), (0, 0, 1), 0.215, 10, 0.012, 0.014)
    b.torus((0, 0, 0.37), 0.205, 0.018, M_FITTING, segs=24, sides=6)
    # valves on the injector end: pre-rotation "up" is -X
    for name, y in (("ox_feed", 0.09), ("fuel_feed", -0.09)):
        base = Vector((-0.17, y, 0.62))
        top = base + Vector((-0.16, 0, 0))
        b.cylinder(base, top, 0.045, M_HOUSING, segs=10)
        b.cylinder(top, top + Vector((-0.07, 0, 0)), 0.07, M_DARK, segs=10)          # valve body
        b.box(tuple(top + Vector((-0.035, 0, 0.09))), (0.06, 0.05, 0.08), 0.0, M_DARK)  # actuator
    for v in b.bm.verts:
        x, y, z = v.co
        v.co = Vector((z, y, -x))
    A["ox_feed"] = ((0.62, 0.09, 0.41), (0, 0, 1))
    A["fuel_feed"] = ((0.62, -0.09, 0.41), (0, 0, 1))
    A["out"] = ((0.0, 0, 0), (-1, 0, 0))
    return b, A


def valve():
    """In-line main propellant valve (unit = line radius), flow along +Z: flanges, ball-valve body, actuator on +X."""
    b = Builder()
    prof = [(0.0, 2.2), (1.45, 2.2), (1.45, 1.9), (1.05, 1.9), (1.05, 1.3)]
    for i in range(1, 12):
        t = math.pi * i / 12
        prof.append((1.05 + 0.75 * math.sin(t), 1.3 - 2.6 * i / 12))
    prof += [(1.05, -1.3), (1.05, -1.9), (1.45, -1.9), (1.45, -2.2), (0.0, -2.2)]
    mats = [M_FITTING, M_FITTING, M_FITTING, M_HOUSING] + [M_HOUSING] * 12 + [M_HOUSING, M_FITTING, M_FITTING, M_FITTING]
    b.lathe(prof, M_HOUSING, segs=20, mats=mats)
    b.bolt_ring((0, 0, 1.9), (0, 0, -1), 1.25, 8, 0.14, 0.18)
    b.bolt_ring((0, 0, -1.9), (0, 0, 1), 1.25, 8, 0.14, 0.18)
    b.cylinder((1.5, 0, 0), (3.3, 0, 0), 0.8, M_DARK, segs=16)
    b.cylinder((3.3, 0, 0), (3.45, 0, 0), 0.9, M_FITTING, segs=16)
    b.box((2.4, 0, 0.95), (1.0, 0.7, 0.4), 0.0, M_DARK)
    b.cylinder((2.4, 0, 1.15), (2.4, 0, 1.6), 0.22, M_FITTING, segs=10)            # electrical connector
    b.cylinder((1.05, 0, 1.0), (1.05, 0, 1.4), 0.18, M_FITTING, segs=8)            # position indicator
    return b, {"in": ((0, 0, -2.2), (0, 0, -1)), "out": ((0, 0, 2.2), (0, 0, 1))}


def actuator():
    """Gimbal actuator (unit = barrel radius), from the top clevis at z = 0 down to the rod-end eye at z = -12.
    Two stretch zones (barrel, rod) let the plugin fit any length; the clevis, gland, servo block and eye keep shape."""
    b = Builder()
    # top clevis: lug and pin boss (pin along Y)
    b.box((0, 0, -0.45), (0.9, 0.7, 0.9), 0.0, M_FITTING)
    b.cylinder((0, -0.5, 0), (0, 0.5, 0), 0.5, M_FITTING, segs=14)
    prof = [(0.0, -0.8), (0.85, -0.8), (1.0, -0.98), (1.0, -6.4), (1.2, -6.4), (1.2, -6.95), (0.42, -6.95), (0.42, -11.2), (0.0, -11.2)]
    mats = [M_DARK, M_DARK, M_HOUSING, M_FITTING, M_FITTING, M_FITTING, M_FITTING, M_FITTING]
    b.lathe(prof, M_HOUSING, segs=20, mats=mats)
    for z in (-1.0, -6.3):
        b.torus((0, 0, z), 1.0, 0.07, M_FITTING, segs=24, sides=6)
    # servo-valve block with hydraulic lines and an electrical connector
    b.box((1.25, 0, -1.8), (0.7, 1.1, 1.2), 0.0, M_DARK)
    for y in (-0.3, 0.3):
        b.sweep([(1.45, y, -1.3), (1.6, y, -0.9), (1.6, y, -0.4)], 0.12, M_FITTING, segs=8)
    b.cylinder((1.6, 0, -2.2), (1.9, 0, -2.2), 0.2, M_FITTING, segs=10)
    # rod-end eye
    b.box((0, 0, -11.55), (0.8, 0.6, 0.7), 0.0, M_FITTING)
    b.cylinder((0, -0.4, -12.0), (0, 0.4, -12.0), 0.55, M_FITTING, segs=14)
    return b, {"zone0_hi": ((0, 0, -2.6), (0, 0, 1)), "zone0_lo": ((0, 0, -6.1), (0, 0, 1)),
               "zone1_hi": ((0, 0, -7.3), (0, 0, 1)), "zone1_lo": ((0, 0, -10.9), (0, 0, 1)),
               "top": ((0, 0, 0), (0, 0, 1)), "end": ((0, 0, -12.0), (0, 0, -1))}


def dome_inlet():
    """Oxidizer inlet on the injector dome (unit = line radius): a reinforcing boss welded into the dome (local +Z =
    dome normal) and a flanged elbow turning outward along +X."""
    b = Builder()
    b.lathe([(0.0, 0.4), (1.7, 0.4), (2.0, 0.2), (2.05, -0.9), (0.0, -0.9)], M_HOUSING, segs=20,
            mats=[M_HOUSING, M_HOUSING, M_HOUSING, M_HOUSING])
    b.bolt_ring((0, 0, 0.4), (0, 0, 1), 1.55, 10, 0.13, 0.15)
    b.sweep([(0, 0, 0.35), (0, 0, 1.2), (0.35, 0, 1.85), (1.05, 0, 2.15), (2.0, 0, 2.15)], 1.0, M_HOUSING, segs=16, cap0=True, cap1=False)
    b.cylinder((2.0, 0, 2.15), (2.3, 0, 2.15), 1.4, M_FITTING, segs=18)
    b.bolt_ring((2.0, 0, 2.15), (-1, 0, 0), 1.22, 8, 0.12, 0.15)
    b.cylinder((-0.9, 0.9, 0.3), (-0.9, 0.9, 0.9), 0.22, M_FITTING, segs=8)   # sensor port
    return b, {"in": ((2.35, 0, 2.15), (1, 0, 0))}


def injector_ring():
    """Regen return manifold under the injector flange (unit = chamber radius, injector plane at z = 0): a torus
    with eight feeders into the chamber, two flanged ports, and instrumentation bosses on the chamber wall."""
    b = Builder()
    z = -0.17
    b.torus((0, 0, z), 1.12, 0.05, M_HOUSING, segs=64, sides=10)
    for k in range(8):
        th = 2 * math.pi * (k + 0.5) / 8
        c, s = math.cos(th), math.sin(th)
        b.cylinder((0.97 * c, 0.97 * s, z), (1.08 * c, 1.08 * s, z), 0.025, M_HOUSING, segs=8)
    for th in (math.radians(100), math.radians(280)):
        c, s = math.cos(th), math.sin(th)
        b.sweep([(1.12 * c, 1.12 * s, z), (1.18 * c, 1.18 * s, z - 0.06), (1.2 * c, 1.2 * s, z - 0.16)], 0.045, M_HOUSING, segs=10, cap0=True, cap1=False)
        b.cylinder((1.2 * c, 1.2 * s, z - 0.16), (1.2 * c, 1.2 * s, z - 0.19), 0.07, M_FITTING, segs=12)
    for th in (math.radians(60), math.radians(160), math.radians(240), math.radians(320)):
        c, s = math.cos(th), math.sin(th)
        b.cylinder((0.98 * c, 0.98 * s, -0.42), (1.06 * c, 1.06 * s, -0.42), 0.03, M_FITTING, segs=8)
        b.cylinder((1.06 * c, 1.06 * s, -0.42), (1.09 * c, 1.09 * s, -0.42), 0.022, M_DARK, segs=6)
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


# ---------------------------------------------------------------------------------------------- atlas + bake

def select_only(obs):
    bpy.ops.object.select_all(action="DESELECT")
    for o in obs: o.select_set(True)
    bpy.context.view_layer.objects.active = obs[0]


def unwrap(obs):
    select_only(obs)
    bpy.ops.object.mode_set(mode="EDIT")
    bpy.ops.mesh.select_all(action="SELECT")
    bpy.ops.uv.smart_project(angle_limit=math.radians(60), island_margin=0.004, scale_to_bounds=False)
    bpy.ops.object.mode_set(mode="OBJECT")
    # texel weighting: a piece's share of the atlas follows its typical size in game, not its model units
    for o in obs:
        f = o.get("pe_texel", 1.0)
        for d in o.data.uv_layers.active.data: d.uv = d.uv * f
    bpy.ops.object.mode_set(mode="EDIT")
    bpy.ops.mesh.select_all(action="SELECT")
    bpy.ops.uv.pack_islands(margin=0.004, rotate=True, scale=True)
    bpy.ops.object.mode_set(mode="OBJECT")


def bake(obs, size, samples):
    sc = bpy.context.scene
    sc.render.engine = "CYCLES"
    sc.cycles.device = "CPU"
    sc.cycles.samples = samples
    if sc.world is None:
        sc.world = bpy.data.worlds.new("World")
    sc.world.light_settings.distance = 0.6
    col = bpy.data.images.new("kit_color", size, size, alpha=False)
    ao = bpy.data.images.new("kit_ao", size, size, alpha=False)
    ao.colorspace_settings.name = "Non-Color"
    mats = {m for o in obs for m in o.data.materials}

    def target(img):
        for m in mats:
            nt = m.node_tree
            node = nt.nodes.get("pe_bake") or nt.nodes.new("ShaderNodeTexImage")
            node.name = "pe_bake"
            node.image = img
            nt.nodes.active = node

    select_only(obs)
    target(col)
    bpy.ops.object.bake(type="DIFFUSE", pass_filter={"COLOR"}, margin=8, use_clear=True)
    target(ao)
    bpy.ops.object.bake(type="AO", margin=8, use_clear=True)
    c = np.empty(size * size * 4, dtype=np.float32); col.pixels.foreach_get(c)
    a = np.empty(size * size * 4, dtype=np.float32); ao.pixels.foreach_get(a)
    c = c.reshape(-1, 4); a = a.reshape(-1, 4)
    occl = 0.3 + 0.7 * a[:, 0:1]
    out = np.concatenate([np.clip(c[:, :3] * occl, 0, 1), np.ones((c.shape[0], 1), np.float32)], axis=1)
    img = bpy.data.images.new("kit_atlas", size, size, alpha=False)
    img.pixels.foreach_set(out.ravel())
    return img


# ---------------------------------------------------------------------------------------------- export

def export_piece(ob, path):
    dg = bpy.context.evaluated_depsgraph_get()
    eo = ob.evaluated_get(dg)
    me = eo.to_mesh()
    me.calc_loop_triangles()
    uvl = me.uv_layers.active.data
    cn = me.corner_normals
    verts = {}
    pos, nor, uv, idx = [], [], [], []
    for tri in me.loop_triangles:
        tri_idx = []
        for li in tri.loops:
            vi = me.loops[li].vertex_index
            p = me.vertices[vi].co
            n = cn[li].vector
            t = uvl[li].uv
            key = (vi, round(n.x, 4), round(n.y, 4), round(n.z, 4), round(t.x, 5), round(t.y, 5))
            k = verts.get(key)
            if k is None:
                k = len(pos)
                verts[key] = k
                pos.append((p.x, p.z, p.y))          # Blender (x, y, z) -> Unity (x, z, y)
                nor.append((n.x, n.z, n.y))
                uv.append((t.x, t.y))
            tri_idx.append(k)
        idx.extend((tri_idx[0], tri_idx[2], tri_idx[1]))   # the axis swap mirrors: reverse the winding
    eo.to_mesh_clear()
    anchors = []
    inv = ob.matrix_world.inverted()
    for ch in ob.children:
        if ch.type != "EMPTY" or not ch.name.startswith(ob.name + "."): continue
        m = inv @ ch.matrix_world
        p = m.translation
        d = (m.to_3x3() @ Vector((0, 0, 1))).normalized()
        anchors.append((ch.name[len(ob.name) + 1:], (p.x, p.z, p.y), (d.x, d.z, d.y)))
    with open(path, "wb") as f:
        f.write(b"PEM1")
        f.write(struct.pack("<iii", 1, len(pos), len(idx)))
        for arr in (pos, nor):
            f.write(struct.pack("<%df" % (3 * len(arr)), *[c for v in arr for c in v]))
        f.write(struct.pack("<%df" % (2 * len(uv)), *[c for v in uv for c in v]))
        f.write(struct.pack("<%di" % len(idx), *idx))
        f.write(struct.pack("<i", len(anchors)))
        for name, p, d in anchors:
            nb = name.encode("utf-8")
            f.write(struct.pack("<i", len(nb))); f.write(nb)
            f.write(struct.pack("<6f", *p, *d))
    print(f"[kit] {ob.name}: {len(pos)} verts, {len(idx) // 3} tris, anchors {[a[0] for a in anchors]} -> {path}")


def main():
    o = args()
    os.makedirs(o["out"], exist_ok=True)
    if o["export_only"]:
        obs = [bpy.data.objects[n] for n, _, _ in PIECES if n in bpy.data.objects]
    else:
        bpy.ops.wm.read_factory_settings(use_empty=True)
        mats = make_materials()
        obs = []
        for i, (n, fn, texel) in enumerate(PIECES):
            b, anchors = fn()
            ob = b.to_object(n, mats)
            ob["pe_texel"] = texel
            for k, (pos, d) in anchors.items():
                anchor(ob, k, pos, d)
            ob.location = (16.0 * (i % 4), 16.0 * (i // 4), 0)   # apart, so pieces don't occlude each other in the AO bake
            obs.append(ob)
        bpy.context.view_layer.update()
        unwrap(obs)
    img = bake(obs, o["size"], o["samples"])
    img.filepath_raw = os.path.join(o["out"], "kit_atlas.png")
    img.file_format = "PNG"
    img.save()
    for ob in obs:
        export_piece(ob, os.path.join(o["out"], ob.name + ".pem"))
    if not o["export_only"]:
        bpy.ops.wm.save_as_mainfile(filepath=os.path.join(HERE, "kit.blend"), compress=True)
    print("[kit] done")


main()
