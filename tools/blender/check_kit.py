"""Check the exported kit (ASSETS.md sections 2-4) without Blender. Needs numpy (Blender's bundled Python has it).

    python tools/blender/check_kit.py [KIT_DIR] [V1_REFERENCE_DIR]
    blender -b --factory-startup -P tools/blender/check_kit.py [-- KIT_DIR [V1_REFERENCE_DIR]]   (faster PNG reading)

KIT_DIR defaults to GameData/ProcEngines/PluginData/Kit. Per piece and LOD:
  * format: version 2, indices in range, unit normals, unit tangents orthogonal to the normals, w = +-1, UVs in 0..1;
  * as Unity sees the mesh: winding agrees with the normals; tangent along +U and binormal = cross(N, T) * w along +V
    (OpenGL normal maps), area-weighted >= 97 %;
  * the anchors of ASSETS.md 4 (LOD1: the same anchors), the LOD0 triangle budget, LOD1 <= 30 % of the LOD0 triangles;
  * informational: LOD1 / LOD0 UV area, triangles with zero UV area.
Kit-wide:
  * actuator stretch zones hold only plain barrel / rod surface;
  * D8 (round 2): dome_inlet boss radius ~1.52 and anchor "in" at (1.95, 1.6, 0); preburner hot-gas outlet radius 0.30
    with hot_out at (-0.8, 0.5, 0) (Unity axes, piece units);
  * inverted variants (<pump>_inv): the same triangles (positions, normals) and anchors as their base, their own UVs;
  * no two pieces share atlas texels (LOD0 UVs rasterised at the atlas size);
  * atlas files: 2048 x 2048, no black / fully glossy padding left (the push-pull fill), unit normals in the normal map;
  * with a v1 reference directory: each envelope stays within +-10 % of v1 (dome_inlet: D8 instead).
Exit code 1 on any error.
"""
import math, os, struct, sys, zlib
import numpy as np

try:
    import bpy  # noqa: F401  (only for faster PNG reading)
    IN_BLENDER = True
except ImportError:
    IN_BLENDER = False

HERE = os.path.dirname(os.path.abspath(__file__))
SHIPPED = os.path.join(HERE, "..", "..", "GameData", "ProcEngines", "PluginData", "Kit")
PUMP_DUAL = ["inlet", "fuel_in", "ox_out", "fuel_out", "turbine_in", "turbine_exhaust"]
PUMP_SINGLE = ["inlet", "out", "turbine_in", "turbine_exhaust"]
ANCHORS = {
    "turbopump_dual": PUMP_DUAL,
    "turbopump_single": PUMP_SINGLE,
    "preburner": ["hot_out", "ox_feed", "fuel_feed"],
    "gas_generator": ["out", "ox_feed", "fuel_feed"],
    "valve": ["in", "out"],
    "actuator": ["top", "end", "zone0_hi", "zone0_lo", "zone1_hi", "zone1_lo"],
    "dome_inlet": ["in"],
    "injector_ring": [],
    "turbopump_dual_inv": PUMP_DUAL,
    "turbopump_single_inv": PUMP_SINGLE,
}
VARIANTS = {"turbopump_dual_inv": "turbopump_dual", "turbopump_single_inv": "turbopump_single"}
BUDGET = {"turbopump_dual": 45000, "turbopump_single": 45000, "preburner": 15000, "gas_generator": 15000,
          "valve": 8000, "actuator": 8000, "dome_inlet": 8000, "injector_ring": 12000,
          "turbopump_dual_inv": 45000, "turbopump_single_inv": 45000}


def read(path):
    d = open(path, "rb").read()
    if d[:4] != b"PEM1": raise ValueError("bad magic")
    ver, nv, ni = struct.unpack_from("<iii", d, 4)
    o = 16

    def take(n, dt="<f4"):
        nonlocal o
        a = np.frombuffer(d, dtype=dt, count=n, offset=o)
        o += a.nbytes
        return a.astype(np.float64) if dt == "<f4" else a.astype(np.int64)

    pos = take(3 * nv).reshape(-1, 3); nor = take(3 * nv).reshape(-1, 3)
    tan = take(4 * nv).reshape(-1, 4) if ver >= 2 else None
    uv = take(2 * nv).reshape(-1, 2); idx = take(ni, "<i4")
    (na,) = struct.unpack_from("<i", d, o); o += 4
    anchors = {}
    for _ in range(na):
        (ln,) = struct.unpack_from("<i", d, o); o += 4
        name = d[o:o + ln].decode("utf-8"); o += ln
        anchors[name] = struct.unpack_from("<6f", d, o); o += 24
    if o != len(d): raise ValueError("%d trailing bytes" % (len(d) - o))
    return dict(ver=ver, nv=nv, ni=ni, pos=pos, nor=nor, tan=tan, uv=uv, idx=idx, anchors=anchors)


def tri_data(m):
    t = m["idx"].reshape(-1, 3)
    p, u = m["pos"], m["uv"]
    e1, e2 = p[t[:, 1]] - p[t[:, 0]], p[t[:, 2]] - p[t[:, 0]]
    d1, d2 = u[t[:, 1]] - u[t[:, 0]], u[t[:, 2]] - u[t[:, 0]]
    fn = np.cross(e1, e2)
    a3 = 0.5 * np.linalg.norm(fn, axis=1)
    det = d1[:, 0] * d2[:, 1] - d2[:, 0] * d1[:, 1]
    return t, e1, e2, d1, d2, fn, a3, det


def check(m, name, lod):
    errs, info = [], []
    nv, ni = m["nv"], m["ni"]
    if m["ver"] != 2: errs.append("version %d" % m["ver"])
    if ni % 3: errs.append("index count not a multiple of 3"); return errs, info
    if ((m["idx"] < 0) | (m["idx"] >= nv)).any(): errs.append("indices out of range"); return errs, info
    worst_n = np.abs(np.linalg.norm(m["nor"], axis=1) - 1).max()
    if worst_n > 1e-3: errs.append("normal length off by %.2g" % worst_n)
    if m["tan"] is not None:
        T, w = m["tan"][:, :3], m["tan"][:, 3]
        worst_t = np.abs(np.linalg.norm(T, axis=1) - 1).max()
        worst_o = np.abs(np.sum(T * m["nor"], axis=1)).max()
        if worst_t > 1e-3: errs.append("tangent length off by %.2g" % worst_t)
        if worst_o > 1e-3: errs.append("tangent not orthogonal to normal (|n.t| up to %.2g)" % worst_o)
        bad_w = int((~np.isin(w, (-1.0, 1.0))).sum())
        if bad_w: errs.append("%d tangents with w not +-1" % bad_w)
    if m["uv"].min() < -1e-4 or m["uv"].max() > 1 + 1e-4:
        errs.append("UVs outside 0..1 (%.4f..%.4f)" % (m["uv"].min(), m["uv"].max()))
    t, e1, e2, d1, d2, fn, a3, det = tri_data(m)
    nsum = m["nor"][t].sum(axis=1)
    area = a3.sum()
    wind = (a3 * (np.sum(fn * nsum, axis=1) > 0)).sum() / max(area, 1e-30)
    info.append("winding %.2f%%" % (100 * wind))
    if wind < 0.97: errs.append("winding disagrees with normals on %.1f%% of the area" % (100 - 100 * wind))
    if m["tan"] is not None:
        ok = np.abs(det) > 1e-14
        dpdu = (d2[ok, 1:2] * e1[ok] - d1[ok, 1:2] * e2[ok]) / det[ok, None]
        dpdv = (d1[ok, 0:1] * e2[ok] - d2[ok, 0:1] * e1[ok]) / det[ok, None]
        tt, aa = t[ok], a3[ok]
        fu = fv = 0.0
        for k in range(3):
            T, w, N = m["tan"][tt[:, k], :3], m["tan"][tt[:, k], 3], m["nor"][tt[:, k]]
            fu += (aa * (np.sum(T * dpdu, axis=1) > 0)).sum()
            fv += (aa * (np.sum(np.cross(N, T) * w[:, None] * dpdv, axis=1) > 0)).sum()
        fu, fv = fu / (3 * aa.sum()), fv / (3 * aa.sum())
        info.append("T along +U %.2f%%, B along +V %.2f%%" % (100 * fu, 100 * fv))
        if fu < 0.97 or fv < 0.97: errs.append("tangent frame does not follow the UVs")
    a2 = 0.5 * np.abs(det)
    info.append("UV area %.5f, zero-UV tris %d" % (a2.sum(), int((a2 < 1e-12).sum())))
    want = ANCHORS.get(name, [])
    missing = [a for a in want if a not in m["anchors"]]
    if missing: errs.append("missing anchors %s" % missing)
    for an, (px, py, pz, dx, dy, dz) in m["anchors"].items():
        if abs(math.sqrt(dx * dx + dy * dy + dz * dz) - 1) > 1e-3: errs.append("anchor %s direction not unit" % an)
    if lod == 0 and ni // 3 > BUDGET.get(name, 1 << 30): errs.append("%d triangles over the budget %d" % (ni // 3, BUDGET[name]))
    return errs, info


def bbox(m):
    p = m["pos"]
    return (p[:, 0].min(), p[:, 0].max()), (p[:, 1].min(), p[:, 1].max()), (p[:, 2].min(), p[:, 2].max()), np.hypot(p[:, 0], p[:, 2]).max()


def zone_check(m):
    """Vertices strictly inside an actuator stretch zone must be plain barrel (r 1.0) or rod (r 0.42) surface."""
    a = m["anchors"]
    bad = 0
    p = m["pos"]
    r = np.hypot(p[:, 0], p[:, 2])
    for z in (0, 1):
        hi, lo = a["zone%d_hi" % z][1], a["zone%d_lo" % z][1]
        inside = (p[:, 1] > lo + 1e-4) & (p[:, 1] < hi - 1e-4)
        bad += int((inside & ~((np.abs(r - 1.0) < 0.02) | (np.abs(r - 0.42) < 0.02))).sum())
    return bad


def d8_checks(name, m):
    """Round-2 contract delta D8. Returns error strings."""
    errs = []
    p = m["pos"]
    if name == "dome_inlet":
        boss = p[p[:, 1] < -0.85]            # the boss's bottom ring (the cable ends higher, at -0.5)
        r = np.hypot(boss[:, 0], boss[:, 2]).max()
        a = m["anchors"].get("in")
        print("%-17s      D8: boss radius %.3f (want ~1.52), anchor in at (%.2f, %.2f, %.2f) dir (%.2f, %.2f, %.2f)"
              % ("", r, *(a or (0,) * 6)))
        if abs(r - 1.52) > 0.05: errs.append("D8 boss radius %.3f" % r)
        if not a or max(abs(a[0] - 1.95), abs(a[1] - 1.6), abs(a[2])) > 1e-3 or a[3] < 0.999: errs.append("D8 anchor in")
        if p[:, 1].max() > 3.0: errs.append("D8 elbow top %.2f above 3.0 line radii" % p[:, 1].max())
    if name == "preburner":
        a = m["anchors"].get("hot_out")
        duct = p[(p[:, 0] < -0.40) & (p[:, 0] > -0.70) & (p[:, 1] > 0.15) & (p[:, 1] < 0.85)]
        rr = np.hypot(duct[:, 1] - 0.5, duct[:, 2])
        rr = np.round(rr[(rr > 0.2) & (rr < 0.36)], 3)    # the duct wall, its weld bead and flange hub
        vals, counts = np.unique(rr, return_counts=True)
        r = float(vals[np.argmax(counts)]) if rr.size else 0.0  # the duct's own rings are the most common radius
        print("%-17s      D8: hot-gas outlet radius %.3f (want 0.30), hot_out at (%.2f, %.2f, %.2f)" % ("", r, *(a or (0,) * 3)[:3]))
        if abs(r - 0.30) > 0.01: errs.append("D8 outlet radius %.3f" % r)
        if not a or max(abs(a[0] + 0.8), abs(a[1] - 0.5), abs(a[2])) > 1e-3: errs.append("D8 hot_out moved")
    return errs


def variant_check(mv, mb):
    """An inverted variant: the same triangles and anchors as its base, its own UVs."""
    errs = []
    if mv["ni"] != mb["ni"]: return ["triangle count differs from the base"]
    pv, pb = mv["pos"][mv["idx"]], mb["pos"][mb["idx"]]
    if np.abs(pv - pb).max() > 1e-5: errs.append("positions differ from the base (%.2g)" % np.abs(pv - pb).max())
    nvv, nvb = mv["nor"][mv["idx"]], mb["nor"][mb["idx"]]
    if np.abs(nvv - nvb).max() > 2e-3: errs.append("normals differ from the base (%.2g)" % np.abs(nvv - nvb).max())
    if mv["anchors"].keys() != mb["anchors"].keys() or max(
            (max(abs(x - y) for x, y in zip(mv["anchors"][k], mb["anchors"][k])) for k in mb["anchors"]), default=0) > 1e-5:
        errs.append("anchors differ from the base")
    if np.abs(mv["uv"][mv["idx"]] - mb["uv"][mb["idx"]]).max() < 1e-4: errs.append("shares the base's UVs")
    return errs


def raster(m, size):
    """Atlas texels covered by the triangles (texel centres inside), as a set of flat indices."""
    uv = m["uv"] * size - 0.5
    t = m["idx"].reshape(-1, 3)
    out = []
    A, B, C = uv[t[:, 0]], uv[t[:, 1]], uv[t[:, 2]]
    lo = np.floor(np.minimum(np.minimum(A, B), C)).astype(int)
    hi = np.ceil(np.maximum(np.maximum(A, B), C)).astype(int)
    span = (hi - lo + 1)
    for (sx, sy) in {tuple(s) for s in span}:
        sel = np.where((span[:, 0] == sx) & (span[:, 1] == sy))[0]
        gx, gy = np.meshgrid(np.arange(sx), np.arange(sy), indexing="ij")
        px = lo[sel, 0][:, None] + gx.ravel()[None]
        py = lo[sel, 1][:, None] + gy.ravel()[None]
        a, b, c = A[sel][:, None], B[sel][:, None], C[sel][:, None]

        def edge(p, q, x, y): return (q[..., 0] - p[..., 0]) * (y - p[..., 1]) - (q[..., 1] - p[..., 1]) * (x - p[..., 0])

        w0, w1, w2 = edge(b, c, px, py), edge(c, a, px, py), edge(a, b, px, py)
        inside = ((w0 >= 0) & (w1 >= 0) & (w2 >= 0)) | ((w0 <= 0) & (w1 <= 0) & (w2 <= 0))
        ok = inside & (px >= 0) & (px < size) & (py >= 0) & (py < size)
        out.append((py[ok] * size + px[ok]).ravel())
    return np.unique(np.concatenate(out)) if out else np.zeros(0, int)


def read_png(path):
    """8-bit RGB / RGBA non-interlaced PNG -> uint8 array (rows top-down), in plain Python (about 20 s for a 2048
    atlas: the Paeth rows are decoded byte by byte)."""
    d = open(path, "rb").read()
    assert d[:8] == bytes([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]), path
    o, idat = 8, []
    while o < len(d):
        ln, kind = struct.unpack(">I4s", d[o:o + 8])
        body = d[o + 8:o + 8 + ln]
        if kind == b"IHDR": w, h, bd, ct, _, _, il = struct.unpack(">IIBBBBB", body)
        elif kind == b"IDAT": idat.append(body)
        o += 12 + ln
    assert bd == 8 and il == 0 and ct in (2, 6), "unsupported PNG " + path
    ch = 4 if ct == 6 else 3
    stride = w * ch
    raw = zlib.decompress(b"".join(idat))
    out = bytearray(h * stride)
    prev = bytearray(stride)
    pos = 0
    for y in range(h):
        ft = raw[pos]
        line = bytearray(raw[pos + 1:pos + 1 + stride])
        pos += 1 + stride
        if ft == 1:
            for i in range(ch, stride): line[i] = (line[i] + line[i - ch]) & 255
        elif ft == 2:
            line = bytearray(((np.frombuffer(line, np.uint8).astype(np.uint16) + np.frombuffer(prev, np.uint8)) & 255).astype(np.uint8).tobytes())
        elif ft == 3:
            for i in range(stride):
                a = line[i - ch] if i >= ch else 0
                line[i] = (line[i] + ((a + prev[i]) >> 1)) & 255
        elif ft == 4:
            for i in range(stride):
                b = prev[i]
                if i >= ch:
                    a, c = line[i - ch], prev[i - ch]
                    pa, pb, pc = abs(b - c), abs(a - c), abs(a + b - 2 * c)
                    pred = a if pa <= pb and pa <= pc else (b if pb <= pc else c)
                else:
                    pred = b
                line[i] = (line[i] + pred) & 255
        out[y * stride:(y + 1) * stride] = line
        prev = line
    return np.frombuffer(bytes(out), np.uint8).reshape(h, w, ch)


def load_png(path):
    """PNG -> uint8 array (rows top-down): through Blender when run inside it (fast), else read_png."""
    if not IN_BLENDER: return read_png(path)
    img = bpy.data.images.load(path)
    img.colorspace_settings.name = "Non-Color"
    w, h = img.size
    a = np.empty(w * h * 4, np.float32)
    img.pixels.foreach_get(a)
    ch = 4 if img.depth == 32 else 3
    bpy.data.images.remove(img)
    return np.round(a.reshape(h, w, 4)[::-1, :, :ch] * 255).astype(np.uint8)


def atlas_checks(kit):
    errs = []
    for name, want_ch in (("kit_atlas.png", 4), ("kit_atlas_n.png", 3)):
        path = os.path.join(kit, name)
        if not os.path.exists(path): errs.append(name + " missing"); continue
        a = load_png(path)
        h, w = a.shape[:2]
        if (w, h) != (2048, 2048): errs.append("%s is %dx%d, not 2048x2048" % (name, w, h))
        if want_ch == 4:
            if a.shape[2] != 4: errs.append("kit_atlas.png has no alpha (gloss)"); continue
            pad = ((a[..., :3].astype(int).sum(-1) == 0) & (a[..., 3] == 255)).mean()
            lum = (0.2126 * a[..., 0] + 0.7152 * a[..., 1] + 0.0722 * a[..., 2]) / 255
            print("atlas %s: %dx%d, black fully glossy texels %.2f%%, luminance p10/50/90 %.2f/%.2f/%.2f, gloss mean %.2f"
                  % (name, w, h, 100 * pad, np.percentile(lum, 10), np.median(lum), np.percentile(lum, 90), a[..., 3].mean() / 255))
            if pad > 0.01: errs.append("kit_atlas.png still has %.1f%% black, fully glossy padding" % (100 * pad))
        else:
            n = a[..., :3].astype(np.float64) / 255 * 2 - 1
            ln = np.linalg.norm(n, axis=-1)
            flat = (np.abs(a[..., :3].astype(int) - (128, 128, 255)).sum(-1) <= 1).mean()
            print("atlas %s: %dx%d, |n| p1/p50/p99 %.2f/%.2f/%.2f, flat texels %.1f%%"
                  % (name, w, h, np.percentile(ln, 1), np.median(ln), np.percentile(ln, 99), 100 * flat))
            back = (n[..., 2] < -0.05).mean()
            print("atlas %s: back-facing texels %.2f%% (multi-sample bake averaging; the loader rebuilds z)" % (name, 100 * back))
            if np.median(ln) < 0.95 or back > 0.01: errs.append("normal map not unit / facing back")
    return errs


def main():
    # inside Blender, sys.argv is Blender's own command line: only what follows "--" is ours
    if "--" in sys.argv: argv = sys.argv[sys.argv.index("--") + 1:]
    else: argv = [] if IN_BLENDER else sys.argv[1:]
    if any(a.startswith("-") for a in argv[:2]):
        print("usage: check_kit.py [KIT_DIR] [V1_REFERENCE_DIR]   (inside Blender: ... -P check_kit.py -- [KIT_DIR] ...)")
        sys.exit(2)
    kit = os.path.abspath(argv[0] if argv else SHIPPED)
    ref = argv[1] if len(argv) > 1 else None
    print("kit:", kit + (", v1 reference: " + os.path.abspath(ref) if ref else ""))
    total_err = 0
    meshes = {}
    print("%-21s %-4s %6s %6s %7s  %s" % ("piece", "lod", "verts", "tris", "budget", "result"))
    for name in ANCHORS:
        p0 = os.path.join(kit, name + ".pem")
        p1 = os.path.join(kit, name + ".lod1.pem")
        if not os.path.exists(p0):
            print("%-21s missing" % name); total_err += 1; continue
        m0 = read(p0)
        meshes[name] = m0
        e0, i0 = check(m0, name, 0)
        e0 += d8_checks(name, m0)
        print("%-21s %-4s %6d %6d %7d  %s  [%s]" % (name, "0", m0["nv"], m0["ni"] // 3, BUDGET[name], "OK" if not e0 else "; ".join(e0), ", ".join(i0)))
        total_err += len(e0)
        if os.path.exists(p1):
            m1 = read(p1)
            e1, i1 = check(m1, name, 1)
            ratio = m1["ni"] / max(1, m0["ni"])
            if ratio > 0.30: e1.append("LOD1 has %.0f%% of the LOD0 triangles" % (100 * ratio))
            if m1["anchors"] != m0["anchors"]: e1.append("LOD1 anchors differ from LOD0")
            print("%-21s %-4s %6d %6d %6.0f%%  %s  [%s]" % ("", "1", m1["nv"], m1["ni"] // 3, 100 * ratio, "OK" if not e1 else "; ".join(e1), ", ".join(i1)))
            total_err += len(e1)
        else:
            print("%-21s %-4s missing" % ("", "1")); total_err += 1
        if name == "actuator":
            bad = zone_check(m0)
            print("%-21s      stretch zones: %s" % ("", "plain surface only" if not bad else "%d detail vertices inside a zone" % bad))
            total_err += bool(bad)
        if name in VARIANTS and VARIANTS[name] in meshes:
            ev = variant_check(m0, meshes[VARIANTS[name]])
            print("%-21s      inverted variant of %s: %s" % ("", VARIANTS[name], "same triangles and anchors, own UVs" if not ev else "; ".join(ev)))
            total_err += len(ev)
        base = VARIANTS.get(name, name)
        if ref and name != "dome_inlet" and os.path.exists(os.path.join(ref, base + ".pem")):
            mr = read(os.path.join(ref, base + ".pem"))
            b0, br = bbox(m0), bbox(mr)
            size_r = max(br[0][1] - br[0][0], br[1][1] - br[1][0], br[2][1] - br[2][0])
            dev = max(max(abs(b0[k][j] - br[k][j]) for k in range(3) for j in range(2)), abs(b0[3] - br[3])) / size_r
            moved = [a for a in mr["anchors"] if a in m0["anchors"] and
                     max(abs(m0["anchors"][a][j] - mr["anchors"][a][j]) for j in range(6)) > 1e-4]
            print("%-21s      envelope vs v1: x %.2f..%.2f (v1 %.2f..%.2f)  y %.2f..%.2f (v1 %.2f..%.2f)  z %.2f..%.2f (v1 %.2f..%.2f)  r %.2f (v1 %.2f): max shift %.1f%% of size; anchors changed: %s"
                  % ("", b0[0][0], b0[0][1], br[0][0], br[0][1], b0[1][0], b0[1][1], br[1][0], br[1][1], b0[2][0], b0[2][1], br[2][0], br[2][1],
                     b0[3], br[3], 100 * dev, moved or "none"))
            if dev > 0.10: total_err += 1; print("%-21s      ENVELOPE OUTSIDE +-10 %%" % "")
    # atlas islands: no texel shared by two pieces
    cover = {n: raster(m, 2048) for n, m in meshes.items()}
    names = list(cover)
    shared = []
    for i in range(len(names)):
        for j in range(i + 1, len(names)):
            k = np.intersect1d(cover[names[i]], cover[names[j]], assume_unique=True).size
            if k: shared.append("%s/%s %d" % (names[i], names[j], k))
    allc = np.unique(np.concatenate(list(cover.values()))) if cover else np.zeros(0)
    print("atlas islands: %.1f%% of the texels; shared between pieces: %s" % (100 * allc.size / 2048 ** 2, ", ".join(shared) or "none"))
    total_err += len(shared)
    errs = atlas_checks(kit)
    for e in errs: print("ERROR", e)
    total_err += len(errs)
    print("errors:", total_err)
    sys.stdout.flush()
    sys.exit(1 if total_err else 0)


main()
