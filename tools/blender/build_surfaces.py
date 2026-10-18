"""
ProcEngines surface textures: the seamless albedo+gloss and normal maps of the code-built engine surfaces
(ASSETS.md section 5). Deterministic (fixed seeds), numpy only.

    <blender>/5.2/python/bin/python.exe tools/blender/build_surfaces.py [options]
    blender -b --factory-startup -P tools/blender/build_surfaces.py -- [options]

Options:
    --out DIR       output directory (default GameData/ProcEngines/PluginData/Surfaces)
    --only a,b      build only these surfaces (regen, steel, bolts, soot, niobium, carbon, foil, hotduct, lines)
    --sheets DIR    also write review sheets: 2x2 tiled albedo / normal / gloss / lit, plus a full-resolution
                    crop across the tile corner, so seams can be checked by eye
    --render DIR    (inside Blender only) render lit Cycles previews of the written textures on engine-like
                    shapes mapped the way EngineGeometry / MeshBuilder map them; skips building if combined
                    with --no-build
    --no-build      do not regenerate the textures (use with --render)
    --samples N     Cycles samples for --render (default 48)

Output per surface: <name>.png (RGBA, 8-bit: sRGB albedo, A = gloss, 0 matte .. 1 full specular) and
<name>_n.png (RGB tangent-space normal, n * 0.5 + 0.5, OpenGL convention: green = +V). The PNGs are written
here with zlib + struct and carry no gamma or colour-profile chunks, so the bytes are exactly the values below.

Array convention in this file: images are float arrays indexed [j, i]; i is the U texel, j the V texel with
j = 0 at v = 0 (the bottom row as Unity's Texture2D.LoadImage places it). The PNG writer flips the rows, since a
PNG stores its top row (v = 1) first.

Every random field is periodic over the tile (FFT-filtered noise on the tile's own lattice, stamps written with
wrap-around indices), so every texture tiles in U and in V; regen and bolts clamp V in game, so their V content
is unique instead (heat coordinate; rim top -> bottom).
"""
import math, os, struct, sys, time, zlib
import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.abspath(os.path.join(HERE, "..", ".."))
SURFACES = ["regen", "steel", "bolts", "soot", "niobium", "carbon", "foil", "hotduct", "lines"]

try:
    import bpy  # noqa: F401  (only for --render)
    IN_BLENDER = True
except ImportError:
    IN_BLENDER = False


def args():
    a = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else sys.argv[1:]
    o = {"out": os.path.join(REPO, "GameData", "ProcEngines", "PluginData", "Surfaces"), "only": SURFACES,
         "sheets": None, "render": None, "build": True, "samples": 48}
    i = 0
    while i < len(a):
        if a[i] == "--out": o["out"] = a[i + 1]; i += 1
        elif a[i] == "--only": o["only"] = [s for s in a[i + 1].split(",") if s]; i += 1
        elif a[i] == "--sheets": o["sheets"] = a[i + 1]; i += 1
        elif a[i] == "--render": o["render"] = a[i + 1]; i += 1
        elif a[i] == "--samples": o["samples"] = int(a[i + 1]); i += 1
        elif a[i] == "--no-build": o["build"] = False
        i += 1
    for s in o["only"]:
        if s not in SURFACES: raise SystemExit("unknown surface " + s)
    return o


# ============================================================================================== noise toolkit

def rng(key):
    return np.random.default_rng(zlib.crc32(key.encode()))


def fnoise(shape, key, lmax, lmin=1.5, beta=1.0, eu=1.0, ev=1.0):
    """Seamless band-limited noise, zero mean, unit std. Wavelengths between lmax and lmin texels (measured
    along U), amplitude ~ f^-beta. ev > 1 stretches the features along V by that factor (eu: along U)."""
    h, w = shape
    F = np.fft.rfft2(rng(key).standard_normal((h, w)))
    fv = np.fft.fftfreq(h)[:, None] * ev
    fu = np.fft.rfftfreq(w)[None, :] * eu
    r = np.sqrt(fu * fu + fv * fv)
    r[0, 0] = 1.0
    amp = r ** -beta * (1 - np.exp(-(r * lmax) ** 4)) * np.exp(-(r * lmin) ** 2)
    amp[0, 0] = 0
    out = np.fft.irfft2(F * amp, s=(h, w))
    s = out.std()
    return out / s if s > 0 else out


def fnoise1(count, n, key, lmax, lmin=1.5, beta=1.0):
    """`count` independent seamless 1-D noises of length n (rows), each zero mean, unit std."""
    F = np.fft.rfft(rng(key).standard_normal((count, n)), axis=1)
    f = np.fft.rfftfreq(n)[None, :].copy()
    f[0, 0] = 1.0
    amp = f ** -beta * (1 - np.exp(-(f * lmax) ** 4)) * np.exp(-(f * lmin) ** 2)
    amp[0, 0] = 0
    out = np.fft.irfft(F * amp, n=n, axis=1)
    return out / np.maximum(out.std(axis=1, keepdims=True), 1e-12)


def blur(img, su, sv=None):
    """Periodic Gaussian blur (sigma in texels)."""
    sv = su if sv is None else sv
    h, w = img.shape
    fv = np.fft.fftfreq(h)[:, None]
    fu = np.fft.rfftfreq(w)[None, :]
    g = np.exp(-2 * math.pi ** 2 * ((fu * su) ** 2 + (fv * sv) ** 2))
    return np.fft.irfft2(np.fft.rfft2(img) * g, s=(h, w))


def sample(img, x, y):
    """Periodic bilinear lookup at texel coordinates (x, y) (texel centres at integers)."""
    h, w = img.shape
    x0 = np.floor(x).astype(int); y0 = np.floor(y).astype(int)
    fx = x - x0; fy = y - y0
    x0 %= w; y0 %= h
    x1 = (x0 + 1) % w; y1 = (y0 + 1) % h
    return (img[y0, x0] * (1 - fx) + img[y0, x1] * fx) * (1 - fy) + (img[y1, x0] * (1 - fx) + img[y1, x1] * fx) * fy


def smoothstep(e0, e1, x):
    t = np.clip((x - e0) / (e1 - e0), 0.0, 1.0)
    return t * t * (3 - 2 * t)


def lerp(a, b, t):
    """a + (b - a) t; a scalar field t blends colour arrays (trailing axis of 3) per texel."""
    a = np.asarray(a, float); b = np.asarray(b, float); t = np.asarray(t, float)
    if t.ndim and (a.shape[-1:] == (3,) or b.shape[-1:] == (3,)) and t.shape[-1:] != (3,): t = t[..., None]
    return a + (b - a) * t


def smax(a, b, k):
    """Polynomial smooth maximum (blend radius k): a concave fillet where two surfaces meet."""
    h = np.clip(0.5 + 0.5 * (a - b) / k, 0, 1)
    return b + (a - b) * h + k * h * (1 - h)


def ramp(t, stops):
    pos = np.array([s[0] for s in stops])
    cols = np.array([s[1] for s in stops], float)
    return np.stack([np.interp(t, pos, cols[:, c]) for c in range(3)], -1)


def rgb(*c):
    return np.array(c, float)


def stamp(canvas, cx, cy, rx, ry, fn, op="add", ext=3.0, mask=None):
    """Evaluate fn(dx, dy) on the texels around (cx, cy) (within ext * radius) and add/max it into the canvas
    with wrap-around. mask = (idmap, id) limits it to texels where idmap == id."""
    h, w = canvas.shape
    ex = min(ext * rx, w / 2 - 1); ey = min(ext * ry, h / 2 - 1)
    xs = np.arange(int(math.floor(cx - ex)), int(math.ceil(cx + ex)) + 1)
    ys = np.arange(int(math.floor(cy - ey)), int(math.ceil(cy + ey)) + 1)
    dx = xs[None, :] + 0.5 - cx
    dy = ys[:, None] + 0.5 - cy
    val = fn(dx, dy)
    ix = np.ix_(ys % h, xs % w)
    if mask is not None: val = val * (mask[0][ix] == mask[1])
    if op == "add": canvas[ix] += val
    elif op == "max": canvas[ix] = np.maximum(canvas[ix], val)
    elif op == "min": canvas[ix] = np.minimum(canvas[ix], val)


def polyline(canvas, pts, width, value=1.0, taper=True, soft=1.0):
    """Anti-aliased line of half-width `width` texels along pts (texel coordinates), max-blended, wrapped."""
    pts = np.asarray(pts, float)
    h, w = canvas.shape
    pad = width * 2 + 2
    x0, y0 = np.floor(pts.min(0) - pad).astype(int)
    x1, y1 = np.ceil(pts.max(0) + pad).astype(int)
    if x1 - x0 >= w or y1 - y0 >= h: return
    X, Y = np.meshgrid(np.arange(x0, x1 + 1) + 0.5, np.arange(y0, y1 + 1) + 0.5)
    d = np.full(X.shape, 1e9)
    tg = np.zeros(X.shape)
    seglen = np.hypot(*np.diff(pts, axis=0).T)
    total = max(seglen.sum(), 1e-6)
    acc = 0.0
    for (a, b), L in zip(zip(pts[:-1], pts[1:]), seglen):
        ab = b - a
        t = np.clip(((X - a[0]) * ab[0] + (Y - a[1]) * ab[1]) / max(ab @ ab, 1e-9), 0, 1)
        dd = np.hypot(X - a[0] - t * ab[0], Y - a[1] - t * ab[1])
        closer = dd < d
        d = np.where(closer, dd, d)
        tg = np.where(closer, (acc + t * L) / total, tg)
        acc += L
    prof = np.clip(1 - d / width, 0, 1) ** soft
    if taper: prof = prof * np.sin(np.pi * np.clip(tg, 0, 1)) ** 0.5
    ix = np.ix_(np.arange(y0, y1 + 1) % h, np.arange(x0, x1 + 1) % w)
    canvas[ix] = np.maximum(canvas[ix], prof * value)


def scratches(shape, key, count, lmin, lmax, wmin=0.45, wmax=1.0, angle=None, spread=math.pi, curve=0.15):
    """Field of fine scratches (0..1)."""
    R = rng(key)
    h, w = shape
    out = np.zeros(shape)
    for _ in range(count):
        L = R.uniform(lmin, lmax)
        th = (R.uniform(0, 2 * math.pi) if angle is None else angle + R.uniform(-spread, spread))
        c = np.array([R.uniform(0, w), R.uniform(0, h)])
        d = np.array([math.cos(th), math.sin(th)])
        nrm = np.array([-d[1], d[0]])
        bend = R.uniform(-curve, curve) * L
        pts = [c - d * L / 2, c + nrm * bend * 0.75, c + d * L / 2]
        pts = [pts[0], 0.5 * (pts[0] + pts[1]) + nrm * bend * 0.1, pts[1], 0.5 * (pts[1] + pts[2]), pts[2]]
        polyline(out, pts, R.uniform(wmin, wmax), R.uniform(0.35, 1.0))
    return out


def smudges(shape, key, count, rmin, rmax, aspect=2.0):
    """Soft blotchy handling marks (0..1): rotated ellipses broken up by fine noise."""
    R = rng(key)
    h, w = shape
    out = np.zeros(shape)
    for _ in range(count):
        r = R.uniform(rmin, rmax)
        a = R.uniform(1.0, aspect)
        th = R.uniform(0, math.pi)
        c, s = math.cos(th), math.sin(th)
        strength = R.uniform(0.4, 1.0)
        stamp(out, R.uniform(0, w), R.uniform(0, h), r * a, r * a,
              lambda dx, dy: strength * np.exp(-(((dx * c + dy * s) / (r * a)) ** 2 + ((-dx * s + dy * c) / r) ** 2) * 1.5),
              op="max", ext=2.0)
    brk = smoothstep(-0.8, 0.8, fnoise(shape, key + ".brk", max(rmin, 6), 1.5, 0.3))
    return out * (0.35 + 0.65 * brk)


def normal_map(hgt, vs=1.0, wrap_v=True, strength=1.0):
    """Tangent-space normals from a height field in texel units (OpenGL: +x = +U, +y = +V). vs = physical length
    of a V texel in U texels."""
    dx = (np.roll(hgt, -1, 1) - np.roll(hgt, 1, 1)) * 0.5
    dy = (np.roll(hgt, -1, 0) - np.roll(hgt, 1, 0)) * 0.5 if wrap_v else np.gradient(hgt, axis=0)
    dy = dy / vs
    n = np.stack([-dx * strength, -dy * strength, np.ones_like(hgt)], -1)
    return n / np.linalg.norm(n, axis=-1, keepdims=True)


def down2(img):
    """2x2 box downsample (keeps periodicity)."""
    h, w = img.shape[:2]
    return img.reshape(h // 2, 2, w // 2, 2, *img.shape[2:]).mean(axis=(1, 3))


def down_normals(n, f=2):
    while f > 1:
        n = down2(n); f //= 2
    return n / np.linalg.norm(n, axis=-1, keepdims=True)


def cavity(hgt, sigma):
    """> 0 in hollows, < 0 on ridges (local height relative to the blurred neighbourhood)."""
    return blur(hgt, sigma) - hgt


# ============================================================================================== heat tint

# Oxide colours of stainless / Inconel by how hot it ran: pale straw, straw, bronze, purple, blue, light blue,
# grey, dark scale. The first stop is blended in from the bare metal by the caller.
HEAT_STOPS = [
    (0.05, (0.66, 0.62, 0.52)),
    (0.20, (0.68, 0.58, 0.42)),
    (0.34, (0.57, 0.44, 0.32)),
    (0.47, (0.46, 0.36, 0.36)),
    (0.58, (0.41, 0.35, 0.43)),
    (0.68, (0.35, 0.38, 0.48)),
    (0.79, (0.42, 0.46, 0.52)),
    (0.90, (0.44, 0.44, 0.46)),
    (1.00, (0.34, 0.34, 0.35)),
]


# ============================================================================================== surfaces

def tex_regen():
    """Brazed tube wall. U: 16 tubes per tile, each centred in its 1/16, braze fillets at the boundaries.
    V: heat coordinate (clamped), 0 = throat (hottest) .. 1 = end of the regen section (coolest)."""
    N, T = 1024, 16
    tw = N / T
    A = 3.0                 # a V texel is ~3 U texels long on a typical bell (V spans the whole regen section)
    sh = (N, N)
    R = rng("regen")
    jj, ii = np.indices(sh)
    ui = ii + 0.5
    v = (jj + 0.5) / N

    # tubes are hand-fitted: the boundaries wander a texel or so along the length
    wob = fnoise1(T, N, "regen.wob", 300, 30, 1.2) * 0.5
    b = np.arange(-1, T + 2)
    bpos = b[:, None] * tw + wob[b % T]                 # boundary k at row k + 1
    k = (ui // tw).astype(int)
    k = k - (ui < bpos[k + 1, jj]).astype(int) + (ui >= bpos[k + 2, jj]).astype(int)
    left, right = bpos[k + 1, jj], bpos[k + 2, jj]
    tid = k % T
    half = 0.5 * (right - left)
    x = (ui - left) / half - 1.0                        # -1 .. 1 across the tube

    t_shade = R.normal(0, 1, T); t_heat = R.normal(0, 1, T); t_hue = R.normal(0, 1, T)
    t_h = 0.80 + 0.03 * R.normal(0, 1, T)               # slightly oval tubes
    kink = fnoise1(T, N, "regen.kink", 160, 16, 1.0)
    heatn = fnoise1(T, N, "regen.heatn", 300, 8, 1.3)
    shaden = fnoise1(T, N, "regen.shaden", 200, 6, 0.8)
    filn = fnoise1(T, N, "regen.filn", 120, 6, 0.9)

    # ---- height (texels)
    crown = np.sqrt(np.clip(1 - x * x, 0, 1))
    ht = half * t_h[tid] * crown * (1 + 0.025 * kink[tid, jj])
    bid = np.where(x < 0, k, k + 1) % T
    fl = half * (0.30 + 0.04 * filn[bid, jj])
    prof = smax(ht, fl, 0.2 * half)                     # round crown, concave braze fillet into the seam
    hn = prof / half
    d_seam = np.minimum(ui - left, right - ui)
    seam = np.exp(-(d_seam / 1.2) ** 2)
    fillet = 1 - smoothstep(0.33, 0.48, hn)
    lumps = fnoise(sh, "regen.lumps", 80, 3, 0.9, ev=5 / A)          # braze runs along the seams
    hgt = prof - 0.35 * seam + fillet * 0.35 * lumps

    dent = np.zeros(sh)
    for _ in range(110):                                # handling dings on the crowns
        kt = int(R.integers(T)); x0 = R.uniform(-0.55, 0.55); cy = R.uniform(0, N)
        ru = R.uniform(2.5, 8.0); rv = ru / A * R.uniform(0.9, 1.8); depth = R.uniform(0.2, 0.7) * ru / 5
        stamp(dent, kt * tw + tw / 2 + x0 * tw / 2, cy, ru, rv,
              lambda dx, dy: -depth * np.exp(-(dx / ru) ** 2 - (dy / rv) ** 2), mask=(tid, kt))
    for kt in range(T):                                 # kinks: shallow creases across a tube
        for _ in range(int(R.integers(0, 3))):
            cy = R.uniform(0, N); rv = R.uniform(1.5, 3.0); depth = R.uniform(0.2, 0.55)
            ru = tw * 0.45
            stamp(dent, kt * tw + tw / 2, cy, ru, rv,
                  lambda dx, dy: -depth * np.exp(-(dy / rv) ** 2) * np.exp(-(dx / ru) ** 4), mask=(tid, kt), ext=1.2)
    dent *= smoothstep(0.35, 0.7, hn)
    micro = fnoise(sh, "regen.micro", 200, 1.5, 0.5, ev=30 / A)       # drawing marks along the tubes
    grain = fnoise(sh, "regen.grain", 6, 1.2, 0.0, ev=1 / A)
    hgt = hgt + dent + 0.05 * micro + 0.02 * grain
    nrm = normal_map(hgt, vs=A, wrap_v=False, strength=0.85)

    # ---- albedo: warm metallic grey (the propellant tint multiplies it)
    mott = fnoise(sh, "regen.mott", 500, 25, 1.2, ev=1 / A)
    streak = fnoise(sh, "regen.streak", 160, 3, 0.8, ev=14 / A)
    crownness = smoothstep(0.4, 0.75, hn)
    shade = 1 + 0.055 * t_shade[tid] + 0.015 * shaden[tid, jj] + 0.03 * mott + 0.008 * streak
    col = rgb(0.575, 0.565, 0.545) * shade[..., None] + (t_hue[tid] * 0.01)[..., None] * rgb(1, 0.4, -0.8)

    # heat discoloration near the throat: irregular per tube and in patches, a faint warm haze further down
    hp = (1 - smoothstep(0.0, 0.30, v)) ** 1.2
    blot = fnoise(sh, "regen.blot", 260, 16, 1.2, ev=5 / A)
    mfine = fnoise(sh, "regen.mfine", 80, 6, 1.0, ev=4 / A)
    haze = 0.13 * (1 - smoothstep(0.10, 0.55, v)) * (0.55 + 0.45 * blot)
    env = 0.95 * hp + haze                              # zero beyond the haze, so no stray tint further down
    heat = env * (1 + 0.32 * t_heat[tid] + 0.26 * heatn[tid, jj] + 0.30 * blot + 0.10 * mfine) + 0.05 * fillet * hp
    tint_w = smoothstep(0.04, 0.18, heat) * 0.9
    col = lerp(col, ramp(heat, HEAT_STOPS) * shade[..., None], tint_w)

    braze = rgb(0.55, 0.52, 0.46) * (0.96 + 0.05 * lumps)[..., None]
    col = lerp(col, braze, fillet * 0.4)
    flux = fillet * smoothstep(1.7, 2.5, fnoise(sh, "regen.flux", 40, 3, 0.8, ev=4 / A))
    col = lerp(col, rgb(0.30, 0.25, 0.20), flux * 0.5)

    ao = 0.6 + 0.4 * smoothstep(0.12, 0.8, hn) ** 0.8
    col = col * (ao * (1 + 0.2 * np.clip(dent, -1, 0)))[..., None]

    cover = smoothstep(-0.4, 1.4, fnoise(sh, "regen.cover", 420, 30, 1.2, ev=3 / A))
    gstreak = smoothstep(0.2, 2.2, fnoise(sh, "regen.gstreak", 260, 12, 1.0, ev=20 / A))
    gv = 0.08 + 0.6 * smoothstep(0.35, 1.0, v)
    grime = np.clip(gv * cover * (0.55 * (1 - crownness) + 0.45 * gstreak), 0, 0.75)
    col = lerp(col, rgb(0.22, 0.20, 0.18), grime * 0.75)
    speck = smoothstep(2.6, 3.3, fnoise(sh, "regen.speck", 8, 1.5, 0.3, ev=1 / A)) * smoothstep(0.6, 1.0, v)
    col = lerp(col, rgb(0.12, 0.11, 0.10), speck * 0.6)

    # ---- gloss: crowns shiny, fillets matte, oxide scale and grime dull
    g = 0.15 + 0.68 * smoothstep(0.35, 0.85, hn)
    g = g * (1 + 0.10 * micro + 0.05 * streak)
    g = g * (1 - 0.35 * tint_w * smoothstep(0.85, 1.0, heat))
    g = lerp(g, 0.22, fillet * 0.4)
    g = g * (1 - 0.85 * grime) * (1 - 0.6 * speck)
    return col, g, nrm, {"wrap_v": False, "vs": A, "phase_u": 64}


def tex_steel():
    """Worn stainless / Inconel, 0.6 m square tile (0.6 mm per texel). Turning marks run around (along U),
    grime streaks run +V (down the profile on outer walls). Also used, darkened by a tint, for machinery."""
    N = 1024
    sh = (N, N)
    turn = fnoise(sh, "steel.turn", 400, 1.2, 0.5, eu=40)
    turn2 = fnoise(sh, "steel.turn2", 120, 1.0, 0.2, eu=12)
    mott = fnoise(sh, "steel.mott", 700, 25, 1.25)
    mott2 = fnoise(sh, "steel.mott2", 120, 8, 1.0)
    hue = fnoise(sh, "steel.hue", 800, 80, 1.5)
    grain = fnoise(sh, "steel.grain", 6, 1.0, 0.0)
    peel = fnoise(sh, "steel.peel", 60, 8, 1.0)
    scr = scratches(sh, "steel.scr", 150, 12, 120, 0.4, 0.85)
    scuff = np.zeros(sh)
    R = rng("steel.scuff")
    for _ in range(9):                                   # scuffed patches: bundles of short parallel scratches
        cx, cy, th = R.uniform(0, N), R.uniform(0, N), R.uniform(0, math.pi)
        d = np.array([math.cos(th), math.sin(th)]); nn = np.array([-d[1], d[0]])
        for _ in range(int(R.integers(8, 20))):
            o = nn * R.normal(0, 10) + d * R.normal(0, 8)
            L = R.uniform(8, 30)
            c = np.array([cx, cy]) + o
            polyline(scuff, [c - d * L / 2, c + d * L / 2], R.uniform(0.4, 0.65), R.uniform(0.3, 0.7))
    smud = smudges(sh, "steel.smudge", 28, 12, 45, 2.5)
    dings = np.zeros(sh)
    for _ in range(60):
        r = R.uniform(1.2, 3.5); dpt = R.uniform(0.1, 0.3) * r
        stamp(dings, R.uniform(0, N), R.uniform(0, N), r, r, lambda dx, dy: -dpt * np.exp(-(dx * dx + dy * dy) / (r * r)))
    # grime: soft runs streaking +V, patchy, plus faint general dirt in the mottling lows
    runs = fnoise(sh, "steel.runs", 300, 5.0, 0.9, ev=10)
    cover = smoothstep(0.2, 1.6, fnoise(sh, "steel.cover", 500, 40, 1.2, ev=2))
    grime = np.clip(smoothstep(0.9, 2.6, runs) * cover * 0.32 + 0.06 * smoothstep(0.5, 2.0, mott2), 0, 0.4)

    hgt = 0.08 * turn + 0.04 * turn2 + 0.025 * grain + 0.06 * peel + dings - 0.3 * scr - 0.18 * scuff
    nrm = down_normals(normal_map(hgt), 2)              # 512^2

    k = 1 + 0.035 * mott + 0.02 * mott2 + 0.03 * turn + 0.01 * grain
    col = rgb(0.55, 0.553, 0.56) * k[..., None] + hue[..., None] * rgb(0.008, 0.003, -0.006)
    col = col * (1 - 0.06 * smud)[..., None]
    col = lerp(col, rgb(0.30, 0.285, 0.265), grime)
    col = col + (0.08 * np.maximum(scr, scuff))[..., None]

    g = 0.50 + 0.12 * turn + 0.04 * mott - 0.04 * mott2
    g = g - 0.16 * smud + 0.28 * np.maximum(scr, scuff)
    g = g * (1 - 0.7 * grime)
    return col, g, nrm, {"wrap_v": True, "vs": 1.0}


def tex_bolts():
    """One hex bolt with washer per tile (256 x 160, square texels). V: 0 = top edge of the rim .. 1 = bottom."""
    W, H, S = 256, 160, 2
    w, h = W * S, H * S
    jj, ii = np.indices((h, w))
    v = (jj + 0.5) / h
    x = (ii + 0.5) / h - 0.8                            # tile-height units, bolt centre at (0, 0)
    y = v - 0.5                                         # +y = +V = down the rim
    r = np.hypot(x, y)
    th0 = math.radians(14)
    hexd = np.max([np.abs(x * math.cos(th0 + q * math.pi / 3) + y * math.sin(th0 + q * math.pi / 3)) for q in range(3)], axis=0)
    a, rw, tw_, hh, bev = 0.232, 0.355, 0.035, 0.075, 0.024
    sh = (h, w)

    brush = fnoise(sh, "bolts.brush", 300, 1.5, 0.5, eu=30)
    mott = fnoise(sh, "bolts.mott", 400, 20, 1.2)
    grain = fnoise(sh, "bolts.grain", 8, 1.2, 0.0)
    # heights in tile-height units
    washer = tw_ * smoothstep(rw, rw - 0.028, r)
    rc = 0.93 * a
    top = tw_ + hh
    head_top = top - np.maximum(r - rc, 0) * 0.55                    # chamfered corners
    side = tw_ + (a + bev - hexd) / bev * hh
    head = np.minimum(head_top, side)
    turn_marks = 0.0005 * np.sin(2 * math.pi * r / 0.02) * smoothstep(rc, rc - 0.02, r)   # turned top face
    hgt = np.maximum(np.maximum(washer, head), 0)
    head_m = smoothstep(a + bev, a, hexd)                           # 1 on the head
    face_m = (r < rc) * (hexd < a)                                  # flat top face
    wash_m = smoothstep(rw, rw - 0.01, r) * (1 - head_m)
    edge = smoothstep(0.075, 0.0, v) + smoothstep(0.925, 1.0, v)    # rim edges, worn
    edge_n = smoothstep(-0.3, 0.9, fnoise(sh, "bolts.edge", 40, 2, 0.6))
    wear = np.clip(edge * edge_n, 0, 1)
    nicks = smoothstep(1.6, 2.4, fnoise(sh, "bolts.nick", 10, 1.5, 0.3)) * edge
    hgt = hgt + (1 - head_m) * (1 - wash_m) * (0.0015 * brush + 0.0008 * grain) - 0.004 * nicks
    nrm = down_normals(normal_map((hgt + turn_marks) * h), S)

    # albedo
    flange = rgb(0.50, 0.51, 0.53) * (1 + 0.05 * mott + 0.03 * brush + 0.015 * grain)[..., None]
    col = flange
    col = lerp(col, rgb(0.69, 0.69, 0.70), wear * 0.8)
    col = lerp(col, rgb(0.60, 0.60, 0.615) * (1 + 0.03 * grain)[..., None], wash_m)
    headc = rgb(0.55, 0.555, 0.57) * (1 + 0.04 * mott + 0.02 * grain)[..., None]
    headc = lerp(headc, rgb(0.64, 0.64, 0.65), face_m * 0.6)
    col = lerp(col, headc, head_m)
    corner = smoothstep(rc, a / math.cos(math.pi / 6), r) * head_m * smoothstep(a - 0.03, a, hexd)
    col = col + (0.08 * corner)[..., None]                         # worn hex corners
    # torque stripe: lacquer across the head, down onto the washer and the flange
    sd = np.array([math.cos(math.radians(62)), math.sin(math.radians(62))])
    along = x * sd[0] + y * sd[1]
    across = -x * sd[1] + y * sd[0]
    stripe_n = fnoise(sh, "bolts.stripe", 30, 2, 0.5)
    stripe_e = fnoise(sh, "bolts.stripe_e", 240, 40, 1.0)
    stripe = smoothstep(0.029, 0.021, np.abs(across + 0.003 * stripe_e)) * smoothstep(-0.11, -0.07, along + 0.01 * stripe_e) \
        * smoothstep(0.50, 0.45, along + 0.015 * stripe_e)
    stripe *= 1 - 0.6 * smoothstep(1.4, 2.2, stripe_n)              # thin, worn spots
    col = lerp(col, rgb(0.68, 0.29, 0.12) * (1 + 0.05 * stripe_n)[..., None], stripe * 0.9)
    # contact shadow and grime under the bolt (+V)
    cav = np.clip(cavity(hgt, 0.03 * h) * 18, 0, 1)
    col = col * (1 - 0.45 * cav)[..., None]
    run = np.exp(-(x / 0.16) ** 2) * smoothstep(rw - 0.02, rw + 0.06, r) * (y > 0) * np.exp(-np.maximum(y - rw, 0) / 0.18)
    gr = np.clip(run * 0.45 * smoothstep(-0.5, 1.0, fnoise(sh, "bolts.grime", 60, 3, 0.8, ev=4)) + 0.12 * smoothstep(0.4, 1.8, mott), 0, 0.6)
    col = lerp(col, rgb(0.24, 0.22, 0.20), gr)
    col = col * (1 - 0.3 * nicks)[..., None]

    g = 0.40 + 0.06 * brush + 0.04 * mott
    g = lerp(g, 0.62, wash_m)
    g = lerp(g, 0.42 + 0.2 * face_m, head_m)
    g = g + 0.3 * wear + 0.2 * corner
    g = lerp(g, 0.35, stripe * 0.9)
    g = g * (1 - 0.7 * gr) * (1 - 0.5 * cav)
    col, g = down2(col), down2(g)
    return col, g, nrm, {"wrap_v": False, "vs": 1.0}


def tex_soot():
    """Inside of the nozzle, near greyscale (a per-propellant tint multiplies it). 8 tiles around; V runs from
    the exit toward the throat (flow direction is -V). Streaks along V, flaking deposits, low gloss."""
    N = 512
    sh = (N, N)
    A = 1.2
    streak = fnoise(sh, "soot.streak", 300, 1.5, 0.6, ev=14 / A)
    streak2 = fnoise(sh, "soot.streak2", 120, 1.2, 0.4, ev=30 / A)
    thick = fnoise(sh, "soot.thick", 250, 10, 1.1, ev=2.5 / A)
    grain = fnoise(sh, "soot.grain", 5, 1.0, 0.0)
    # flakes: the deposit has spalled off in places, showing the metal under it
    fl_n = fnoise(sh, "soot.flake", 50, 4, 0.9, ev=1.5 / A)
    flake = smoothstep(1.75, 1.95, fl_n + 0.5 * thick + 0.25 * streak)
    flake = np.clip(flake, 0, 1)
    dep = 0.9 * thick + 0.4 * streak + 0.15 * grain       # deposit thickness (texels)
    hgt = dep * (1 - flake) - 1.2 * flake
    nrm = normal_map(hgt, vs=A, strength=0.8)

    k = 0.76 + 0.035 * streak + 0.02 * streak2 - 0.05 * thick + 0.02 * grain
    col = np.repeat(k[..., None], 3, -1)
    metal = rgb(1.0, 0.97, 0.93) * (0.97 + 0.04 * grain)[..., None]
    col = lerp(col, metal, flake * 0.9)
    edge = np.clip(smoothstep(0.05, 0.5, flake) - smoothstep(0.5, 0.95, flake), 0, 1)
    col = col * (1 - 0.25 * edge)[..., None]
    g = 0.10 + 0.03 * streak + 0.02 * grain
    g = lerp(g, 0.42, flake)
    return col, g, nrm, {"wrap_v": True, "vs": A}


def tex_niobium():
    """C103 niobium with its silicide coating, 0.8 m tile. Matte grey-violet, finely mottled, faint flow streaks
    along V (joint -> exit), spinning marks around, small coating chips showing bright metal."""
    N = 1024
    sh = (N, N)
    m1 = fnoise(sh, "nb.m1", 650, 40, 1.3)
    m2 = fnoise(sh, "nb.m2", 120, 6, 1.0)
    m3 = fnoise(sh, "nb.m3", 30, 3, 0.8)
    flow = fnoise(sh, "nb.flow", 300, 3, 0.7, ev=18)
    hue = fnoise(sh, "nb.hue", 700, 80, 1.5)
    grain = fnoise(sh, "nb.grain", 5, 1.0, 0.0)
    spin = fnoise(sh, "nb.spin", 200, 3, 0.8, eu=60)
    sand = fnoise(sh, "nb.sand", 12, 1.5, 0.3)
    cl = fnoise(sh, "nb.cl", 400, 60, 1.0)
    fl = fnoise(sh, "nb.fl", 12, 2.0, 0.5)
    chip = smoothstep(2.45, 2.7, fl + 0.6 * cl)
    hgt = 0.18 * m3 + 0.08 * grain + 0.18 * sand + 0.14 * spin - 0.9 * chip
    nrm = normal_map(hgt, strength=0.9)

    violet, neutral = rgb(0.415, 0.395, 0.435), rgb(0.43, 0.425, 0.42)
    base = lerp(violet, neutral, smoothstep(-1.6, 1.6, hue))
    k = 1 + 0.035 * m1 + 0.03 * m2 + 0.025 * m3 + 0.03 * flow + 0.025 * grain + 0.025 * sand
    col = base * k[..., None]
    col = lerp(col, rgb(0.35, 0.33, 0.36), 0.22 * smoothstep(1.0, 2.4, m1 + 0.6 * flow))   # darker heat-mottled areas
    metal = rgb(0.62, 0.62, 0.63) * (1 + 0.04 * grain)[..., None]
    rim = np.clip(smoothstep(0.0, 0.4, chip) - smoothstep(0.4, 1.0, chip), 0, 1)
    col = lerp(col, metal, chip)
    col = col * (1 - 0.2 * rim)[..., None]
    g = 0.14 + 0.04 * m2 + 0.03 * flow + 0.02 * sand
    g = lerp(g, 0.6, chip)
    return col, g, nrm, {"wrap_v": True, "vs": 1.0}


def tex_carbon():
    """Carbon-carbon, 2/2 twill, 32 tows per 0.8 m tile: warp tows along V, weft along U. Flat, tightly packed
    tows; the warp and weft catch the light differently (fibre sheen); resin-rich glossy patches."""
    N, tows = 1024, 32
    c = N / tows
    sh = (N, N)
    jj, ii = np.indices(sh)
    X = (ii + 0.5) / c; Y = (jj + 0.5) / c
    ic = np.floor(X).astype(int); jc = np.floor(Y).astype(int)
    xl = (X - ic) * 2 - 1; yl = (Y - jc) * 2 - 1
    und_w = np.cos(math.pi / 2 * (Y + ic - 1))      # +1 where the warp tow is over, -1 under
    und_f = np.cos(math.pi / 2 * (X + jc - 3))
    R = rng("carbon")
    tw_shade = fnoise1(tows, N, "cc.tw", 200, 6, 1.0)
    tf_shade = fnoise1(tows, N, "cc.tf", 200, 6, 1.0)
    w_off = R.normal(0, 1, tows); f_off = R.normal(0, 1, tows)
    edge_w = 0.985 + 0.01 * fnoise1(tows, N, "cc.ew", 60, 3, 0.8)[ic, jj]
    edge_f = 0.985 + 0.01 * fnoise1(tows, N, "cc.ef", 60, 3, 0.8)[jc, ii]
    cw = np.clip(1 - (xl / edge_w) ** 2, 0, 1) ** 0.35
    cf = np.clip(1 - (yl / edge_f) ** 2, 0, 1) ** 0.35
    Hw = 0.9 * und_w + 0.8 * cw - 0.25 * (1 - cw)
    Hf = 0.9 * und_f + 0.8 * cf - 0.25 * (1 - cf)
    warp = Hw > Hf
    hgt = smax(Hw, Hf, 0.35)
    fib_w = fnoise(sh, "cc.fw", 80, 1.1, 0.3, ev=25)
    fib_f = fnoise(sh, "cc.ff", 80, 1.1, 0.3, eu=25)
    fib = np.where(warp, fib_w, fib_f)
    resin = smoothstep(0.5, 1.6, fnoise(sh, "cc.resin", 320, 30, 1.2))
    haze = smoothstep(1.0, 2.2, fnoise(sh, "cc.haze", 260, 20, 1.1))
    big = fnoise(sh, "cc.big", 600, 60, 1.3)
    hgt = hgt + 0.04 * fib
    hgt = lerp(hgt, smax(blur(hgt, 2.5), 0.4, 0.8), resin * 0.6)
    nrm = normal_map(hgt, strength=0.5)

    top = np.where(warp, cw, cf)
    shade = np.where(warp, 1.0 + 0.05 * tw_shade[ic, jj] + 0.03 * w_off[ic], 0.74 + 0.05 * tf_shade[jc, ii] + 0.03 * f_off[jc])
    k = 0.19 * shade * (1 + 0.04 * fib + 0.05 * big) * (0.8 + 0.2 * top)
    col = np.stack([k, k * 1.0, k * 1.04], -1)
    col = lerp(col, rgb(0.13, 0.12, 0.115), resin * 0.45)
    col = lerp(col, rgb(0.27, 0.27, 0.28), haze * 0.4)
    ao = np.clip(cavity(hgt, 4) * 0.18, 0, 0.3)
    col = col * (1 - ao)[..., None]
    g = np.where(warp, 0.46, 0.30) * (1 + 0.12 * fib) * (0.55 + 0.45 * top)
    g = lerp(g, 0.58, resin * 0.7)
    g = g * (1 - 0.6 * haze)
    return col, g, nrm, {"wrap_v": True, "vs": 1.0, "phase_u": 32, "phase_v": 32}


def tex_foil():
    """Gold MLI on hydrogen lines. U = around the pipe, V = arc length / circumference (square texels).
    Crinkle, a lapped seam along V, a tape wrap across U."""
    N = 512
    sh = (N, N)
    jj, ii = np.indices(sh)
    wu = 6 * fnoise(sh, "foil.wu", 200, 30, 1.2); wv = 6 * fnoise(sh, "foil.wv", 200, 30, 1.2)
    n1 = sample(fnoise(sh, "foil.c1", 180, 22, 1.0, ev=1.4), ii + wu, jj + wv)
    n2 = sample(fnoise(sh, "foil.c2", 70, 10, 1.0), ii + wu * 0.5, jj + wv * 0.5)
    n3 = fnoise(sh, "foil.c3", 20, 3, 0.8)
    crinkle = 3.6 * np.abs(n1) - 1.6 * np.abs(n2) + 0.3 * np.abs(n3)
    # lapped seam along V: the outer layer (u < seam) lies on the inner one
    su = 0.37 * N + 2.5 * fnoise1(1, N, "foil.seam", 120, 6, 1.0)[0][jj]
    du = ii + 0.5 - su
    outer = smoothstep(1.5, -1.5, du)
    under = np.roll(crinkle, (N // 3, N // 5), (0, 1))
    hgt = lerp(under, crinkle, outer) + 1.6 * outer + 0.8 * np.exp(-(du / 2.5) ** 2) * (du < 0)
    # tape wrap across U (constant V band)
    vt0, vt1 = 0.58 * N, 0.665 * N
    tv = jj + 0.5 + 1.2 * fnoise1(1, N, "foil.tape", 80, 4, 1.0)[0][ii]
    tape = smoothstep(vt0 - 1, vt0 + 1, tv) * smoothstep(vt1 + 1, vt1 - 1, tv)
    twr = fnoise(sh, "foil.twr", 40, 3, 0.8, ev=4)
    hgt = lerp(hgt, 0.25 * hgt + 2.4 + 0.35 * twr, tape)
    nrm = normal_map(hgt, strength=1.0)

    cvar = fnoise(sh, "foil.cvar", 200, 10, 1.0)
    gold = lerp(rgb(0.82, 0.62, 0.27), rgb(0.76, 0.52, 0.20), smoothstep(-1.5, 1.5, cvar))
    col = gold * (1 + 0.06 * n2 + 0.04 * n3)[..., None]
    shadow = np.exp(-(du / 3.0) ** 2) * (du > 0)
    col = col * (1 - 0.35 * shadow)[..., None]
    kapton = rgb(0.72, 0.47, 0.18) * (1 + 0.05 * twr)[..., None]
    col = lerp(col, kapton, tape)
    dirt = smudges(sh, "foil.dirt", 10, 8, 30, 2.0)
    col = lerp(col, rgb(0.40, 0.32, 0.18), dirt * 0.45)
    g = 0.80 - 0.10 * np.abs(n3) + 0.05 * n2
    g = lerp(g, 0.62, tape)
    g = g * (1 - 0.5 * dirt) * (1 - 0.3 * shadow)
    return col, g, nrm, {"wrap_v": True, "vs": 1.0}


def tex_hotduct():
    """Uncooled turbine exhaust duct: heat-tinted Inconel with a longitudinal seam weld (heat-affected zone
    bands), soft mottling, flow streaks along V and diffuse soot. Mapped like foil."""
    N = 512
    sh = (N, N)
    jj, ii = np.indices(sh)
    big = fnoise(sh, "hd.big", 420, 40, 1.5)
    mid = fnoise(sh, "hd.mid", 120, 8, 1.0)
    streak = fnoise(sh, "hd.streak", 200, 3, 0.7, ev=8)
    grain = fnoise(sh, "hd.grain", 5, 1.0, 0.0)
    wave = fnoise(sh, "hd.wave", 300, 40, 1.5)
    u0 = 0.21 * N + 1.0 * fnoise1(1, N, "hd.weld", 100, 8, 1.0)[0][jj]
    du = ii + 0.5 - u0
    duw = (du + N / 2) % N - N / 2
    bead_r = 4.2
    bead = np.sqrt(np.clip(1 - (duw / bead_r) ** 2, 0, 1))
    ripple = np.sin(2 * math.pi * (jj + 0.25 * duw ** 2 / bead_r) / 4.0)
    haz = np.exp(-(np.abs(duw) / 20) ** 1.3)
    hgt = 1.6 * wave + 0.12 * mid + 0.04 * grain + 1.7 * bead + 0.18 * ripple * bead
    nrm = normal_map(hgt, strength=1.0)

    heat = 0.30 + 0.07 * big + 0.05 * mid + 0.04 * streak + 0.12 * smoothstep(0.0, 2.4, big + 0.3 * mid)
    heat = np.maximum(heat, 0.16 + 0.80 * haz + 0.03 * mid)
    tint = ramp(np.clip(heat, 0, 1), HEAT_STOPS)
    bare = rgb(0.56, 0.56, 0.57) * (1 + 0.03 * grain)[..., None]
    col = lerp(bare, tint, smoothstep(0.02, 0.14, heat)) * (1 + 0.035 * mid + 0.025 * streak + 0.02 * grain)[..., None]
    col = lerp(col, rgb(0.37, 0.36, 0.37) * (1 + 0.1 * ripple)[..., None], bead * 0.75)
    soot = 0.35 * smoothstep(0.3, 2.4, fnoise(sh, "hd.soot", 200, 12, 1.1, ev=2.5) + 0.3 * streak)
    col = lerp(col, rgb(0.16, 0.15, 0.14), soot)
    g = 0.46 - 0.12 * smoothstep(0.75, 1.0, heat) + 0.05 * mid
    g = lerp(g, 0.32, bead)
    g = g * (1 - 0.75 * soot)
    return col, g, nrm, {"wrap_v": True, "vs": 1.0}


def tex_lines():
    """Stainless propellant lines and kit structure: drawn tubing brushed along V, handling marks, light grime."""
    N = 512
    sh = (N, N)
    brush = fnoise(sh, "ln.brush", 300, 1.0, 0.5, ev=40)
    brush2 = fnoise(sh, "ln.brush2", 80, 1.0, 0.2, ev=10)
    mott = fnoise(sh, "ln.mott", 400, 20, 1.25)
    hue = fnoise(sh, "ln.hue", 500, 60, 1.5)
    grain = fnoise(sh, "ln.grain", 5, 1.0, 0.0)
    scr = scratches(sh, "ln.scr", 40, 8, 70, 0.4, 0.8)
    scr2 = scratches(sh, "ln.scr2", 25, 20, 120, 0.4, 0.8, angle=math.pi / 2, spread=0.12)
    scr = np.maximum(scr, scr2)
    smud = smudges(sh, "ln.smudge", 9, 8, 26, 2.5)
    R = rng("ln.dings")
    dings = np.zeros(sh)
    for _ in range(12):
        r = R.uniform(1.0, 3.0); dpt = R.uniform(0.08, 0.2) * r
        stamp(dings, R.uniform(0, N), R.uniform(0, N), r, r, lambda dx, dy: -dpt * np.exp(-(dx * dx + dy * dy) / (r * r)))
    grime = np.clip(0.25 * smoothstep(0.8, 2.2, fnoise(sh, "ln.grime", 260, 12, 1.1, ev=3)), 0, 0.3)
    hgt = 0.09 * brush + 0.04 * brush2 + 0.02 * grain + dings - 0.25 * scr
    nrm = normal_map(hgt)
    k = 1 + 0.03 * mott + 0.06 * brush + 0.03 * brush2 + 0.01 * grain
    col = rgb(0.50, 0.505, 0.515) * k[..., None] + hue[..., None] * rgb(0.008, 0.003, -0.008)
    col = col * (1 - 0.05 * smud)[..., None]
    col = lerp(col, rgb(0.32, 0.30, 0.28), grime)
    col = col + (0.07 * scr)[..., None]
    g = 0.56 + 0.15 * brush + 0.05 * brush2 + 0.04 * mott - 0.14 * smud + 0.22 * scr
    g = g * (1 - 0.7 * grime)
    return col, g, nrm, {"wrap_v": True, "vs": 1.0}


BUILDERS = {"regen": tex_regen, "steel": tex_steel, "bolts": tex_bolts, "soot": tex_soot, "niobium": tex_niobium,
            "carbon": tex_carbon, "foil": tex_foil, "hotduct": tex_hotduct, "lines": tex_lines}


# ============================================================================================== PNG output

def png_bytes(arr):
    """8-bit RGB/RGBA PNG; arr is uint8 [rows, cols, channels] with row 0 at the top. One filter type for the
    whole image (chosen on a sample of rows), no colour chunks."""
    arr = np.ascontiguousarray(arr, dtype=np.uint8)
    h, w, c = arr.shape
    x = arr.reshape(h, w * c).astype(np.int16)
    up = np.vstack([np.zeros((1, w * c), np.int16), x[:-1]])
    left = np.hstack([np.zeros((h, c), np.int16), x[:, :-c]])
    cands = {0: x, 1: x - left, 2: x - up}            # none / sub / up; whole-image Paeth or average came within ~4 %
    probe = slice(0, h, max(1, h // 64))
    best = min(cands, key=lambda f: len(zlib.compress((cands[f][probe] & 0xFF).astype(np.uint8).tobytes(), 6)))
    rows = np.hstack([np.full((h, 1), best, np.uint8), (cands[best] & 0xFF).astype(np.uint8)])

    def chunk(tag, data):
        return struct.pack(">I", len(data)) + tag + data + struct.pack(">I", zlib.crc32(tag + data) & 0xFFFFFFFF)
    ihdr = struct.pack(">IIBBBBB", w, h, 8, {3: 2, 4: 6}[c], 0, 0, 0)
    return b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", ihdr) + chunk(b"IDAT", zlib.compress(rows.tobytes(), 9)) + chunk(b"IEND", b"")


def write_png(path, arr):
    with open(path, "wb") as f: f.write(png_bytes(arr))


def read_png(path):
    """Minimal reader for the files this script writes (8-bit RGB/RGBA, non-interlaced): rows top first."""
    with open(path, "rb") as f: data = f.read()
    pos, idat, w = 8, b"", 0
    while pos < len(data):
        n, tag = struct.unpack(">I4s", data[pos:pos + 8])
        body = data[pos + 8:pos + 8 + n]
        if tag == b"IHDR":
            w, h, depth, ct = struct.unpack(">IIBB", body[:10])
            c = {2: 3, 6: 4}[ct]
        elif tag == b"IDAT": idat += body
        pos += 12 + n
    raw = np.frombuffer(zlib.decompress(idat), np.uint8).reshape(h, 1 + w * c)
    ft = set(raw[:, 0].tolist())
    if len(ft) != 1 or not ft <= {0, 1, 2}: raise ValueError("read_png handles one filter of none/sub/up only")
    line = raw[:, 1:].astype(np.int64).reshape(h, w, c)
    f = ft.pop()
    if f == 1: line = np.cumsum(line, axis=1)
    elif f == 2: line = np.cumsum(line, axis=0)
    return (line & 0xFF).astype(np.uint8)


def to8(x):
    return np.clip(np.round(x * 255), 0, 255).astype(np.uint8)


def encode(col, gloss, nrm):
    """Float texture-space arrays -> PNG-ordered uint8 arrays (albedo RGBA, normal RGB)."""
    alb = np.dstack([to8(np.clip(col, 0, 1)), to8(np.clip(gloss, 0, 1))])
    nm = to8(nrm * 0.5 + 0.5)
    return np.flipud(alb), np.flipud(nm)


# ============================================================================================== checks, sheets

def seam_stats(img, axis, phase=0):
    """Mean |difference| across the wrap edge vs across interior neighbour pairs along the same axis: the
    edge value, the interior mean, and the ratio edge / interior. With a structural period (tubes, tows), the
    interior pairs are those at the same phase (the other tube/tow boundaries), since the edge is one of them."""
    a = img.astype(np.float64)
    if axis == 0: a = a.transpose(1, 0, 2)
    inner = np.abs(np.diff(a, axis=1)).mean(axis=(0, 2))            # pair (i, i+1) at index i
    edge = np.abs(a[:, 0] - a[:, -1]).mean()
    if phase: inner = inner[phase - 1::phase]
    ref = inner.mean()
    return edge, ref, edge / max(ref, 1e-9)


def check(name, alb, nm, meta, worst):
    lines = []
    for label, img in (("albedo", alb), ("normal", nm)):
        sc = img.shape[1] / alb.shape[1]
        pu = int(meta.get("phase_u", 0) * sc); pv = int(meta.get("phase_v", 0) * sc)
        eu, iu, ru = seam_stats(img, 1, pu)
        s = f"  {name:8s} {label:6s} {img.shape[1]}x{img.shape[0]}  U edge {eu:5.2f} / interior {iu:5.2f} = {ru:4.2f}"
        worst.append(ru)
        if meta["wrap_v"]:
            ev, iv, rv = seam_stats(img, 0, pv)
            s += f"   V edge {ev:5.2f} / interior {iv:5.2f} = {rv:4.2f}"
            worst.append(rv)
        else: s += "   V clamp (not checked)"
        lines.append(s)
    n = nm.astype(float) / 255 * 2 - 1
    lines.append(f"  {name:8s} albedo mean RGB {alb[..., :3].reshape(-1, 3).mean(0).round(1)}  gloss mean {alb[..., 3].mean():.1f}"
                 f" [p5 {np.percentile(alb[..., 3], 5):.0f}, p95 {np.percentile(alb[..., 3], 95):.0f}]"
                 f"  normal |xy| mean {np.hypot(n[..., 0], n[..., 1]).mean():.3f}")
    return lines


def lit(alb, nm, light=(-0.5, 0.55, 0.67), spec=0.55, power=40.0):
    """Quick Blinn-Phong look at the maps (PNG-ordered arrays, so +V is up and the light comes from upper left)."""
    n = nm.astype(float) / 255 * 2 - 1
    n = n / np.linalg.norm(n, axis=-1, keepdims=True)
    L = np.array(light, float); L /= np.linalg.norm(L)
    Hv = L + np.array([0, 0, 1.0]); Hv /= np.linalg.norm(Hv)
    a = alb[..., :3].astype(float) / 255
    g = alb[..., 3].astype(float) / 255
    nl = np.clip(n @ L, 0, 1)
    nh = np.clip(n @ Hv, 0, 1)
    out = a * (0.28 + 0.95 * nl)[..., None] + (spec * g * nh ** power)[..., None]
    return to8(np.clip(out, 0, 1))


def resize_nn(img, h, w):
    """Box-ish downsample by integer factors, else nearest."""
    H, W = img.shape[:2]
    if H % h == 0 and W % w == 0:
        fy, fx = H // h, W // w
        return img.reshape(h, fy, w, fx, -1).mean(axis=(1, 3)).astype(np.uint8)
    yi = (np.arange(h) * H // h); xi = (np.arange(w) * W // w)
    return img[yi][:, xi]


def write_sheet(path, name, alb, nm, vs=1.0):
    """2x2 grid of panels (albedo, normal, gloss, lit), each the texture tiled 2x2, plus a full-resolution crop
    centred on the tile corner (path with _crop); with vs > 1 the crop is also stretched in V by vs to show the
    in-game proportions."""
    ah, aw = alb.shape[:2]
    if nm.shape[:2] != alb.shape[:2]:
        f = alb.shape[0] // nm.shape[0]
        nm_full = np.repeat(np.repeat(nm, f, 0), f, 1)
    else: nm_full = nm
    lt = lit(alb, nm_full)
    panels = [alb[..., :3], nm_full, np.repeat(alb[..., 3:4], 3, -1), lt]
    tiled = [np.tile(p, (2, 2, 1)) for p in panels]
    ph = 512 if aw >= ah else int(512 * ah / aw)
    pw = int(round(ph * aw / ah))
    small = [resize_nn(t, ph, pw) for t in tiled]
    gap = 6
    sheet = np.full((2 * ph + gap, 2 * pw + gap, 3), 40, np.uint8)
    for k, s in enumerate(small):
        y, x = (k // 2) * (ph + gap), (k % 2) * (pw + gap)
        sheet[y:y + ph, x:x + pw] = s
    write_png(path, sheet)
    ch, cw = min(ah, 512), min(aw, 512)
    lt_t = np.tile(lt, (2, 2, 1))
    al_t = np.tile(alb[..., :3], (2, 2, 1))
    y0, x0 = ah - ch // 2, aw - cw // 2
    crop = np.hstack([al_t[y0:y0 + ch, x0:x0 + cw], np.full((ch, gap, 3), 40, np.uint8), lt_t[y0:y0 + ch, x0:x0 + cw]])
    write_png(path.replace(".png", "_crop.png"), crop)
    if vs > 1:
        rows = int(ch / vs)
        y1 = ah - rows // 2
        part = np.hstack([al_t[y1:y1 + rows, x0:x0 + cw], np.full((rows, gap, 3), 40, np.uint8), lt_t[y1:y1 + rows, x0:x0 + cw]])
        write_png(path.replace(".png", "_crop_ingame.png"), np.repeat(part, int(round(vs)), axis=0))


# ============================================================================================== main

def build(o):
    os.makedirs(o["out"], exist_ok=True)
    if o["sheets"]: os.makedirs(o["sheets"], exist_ok=True)
    report, worst = [], []
    t_all = time.time()
    for name in o["only"]:
        t0 = time.time()
        col, g, nrm, meta = BUILDERS[name]()
        alb, nm = encode(col, g, nrm)
        for suffix, img in (("", alb), ("_n", nm)):
            path = os.path.join(o["out"], name + suffix + ".png")
            write_png(path, img)
            if not np.array_equal(read_png(path), img): raise SystemExit("PNG round trip failed: " + path)
        report += check(name, alb, nm, meta, worst)
        if o["sheets"]: write_sheet(os.path.join(o["sheets"], name + "_sheet.png"), name, alb, nm, meta.get("vs", 1.0))
        kb = sum(os.path.getsize(os.path.join(o["out"], name + s)) for s in (".png", "_n.png")) / 1024
        print(f"[surfaces] {name:8s} {alb.shape[1]}x{alb.shape[0]} / {nm.shape[1]}x{nm.shape[0]}  {kb:7.0f} KB  {time.time() - t0:5.1f} s", flush=True)
    print("[surfaces] seam check, mean |diff| of 8-bit values across the wrap edge / between interior neighbours"
          " (same phase for tubes and tows); ~1 = seamless:")
    for line in report: print(line)
    print(f"[surfaces] worst seam ratio {max(worst):.2f}; done in {time.time() - t_all:.1f} s -> {o['out']}")


def main():
    o = args()
    if o["build"]: build(o)
    if o["render"]:
        if not IN_BLENDER: raise SystemExit("--render needs Blender: blender -b --factory-startup -P build_surfaces.py -- --render DIR")
        render_previews(o)


# ============================================================================================== Blender previews

# Material settings the way the plugin used them before this pass (EngineMaterials.Make: tint, _Shininess,
# _SpecColor), so the preview approximates KSP/Bumped Specular: Lambert diffuse of albedo x tint plus a
# Blinn-Phong lobe of specColor x gloss (alpha) with exponent shininess x 128.
PREVIEW_MATS = {
    "steel": ((1, 1, 1), 0.3, (0.66, 0.66, 0.68)),
    "machine": ((0.5, 0.51, 0.55), 0.3, (0.55, 0.55, 0.57)),
    "bolts": ((1, 1, 1), 0.36, (0.72, 0.72, 0.74)),
    "regen": ((1, 1, 1), 0.32, (0.86, 0.76, 0.64)),
    "regen_kerolox": ((1, 0.88, 0.76), 0.32, (0.86, 0.669, 0.486)),
    "soot_hydrolox": ((0.42, 0.32, 0.26), 0.3, (0.16, 0.16, 0.16)),
    "soot_kerolox": ((0.07, 0.065, 0.06), 0.3, (0.16, 0.16, 0.16)),
    "niobium": ((1, 1, 1), 0.13, (0.42, 0.41, 0.45)),
    "carbon": ((1, 1, 1), 0.2, (0.4, 0.4, 0.42)),
    "foil": ((1, 1, 1), 0.2, (1.0, 0.86, 0.56)),
    "hotduct": ((1, 1, 1), 0.26, (0.58, 0.56, 0.55)),
    "lines": ((1, 1, 1), 0.45, (0.85, 0.85, 0.86)),
}


def render_previews(o):
    import bpy
    from mathutils import Vector
    surf, out = o["out"], o["render"]
    os.makedirs(out, exist_ok=True)
    bpy.ops.wm.read_factory_settings(use_empty=True)
    sc = bpy.context.scene
    sc.render.engine = "CYCLES"
    sc.cycles.device = "CPU"
    sc.cycles.samples = o["samples"]
    sc.cycles.use_denoising = True
    sc.render.resolution_x = sc.render.resolution_y = 1000
    sc.view_settings.view_transform = "Standard"
    sc.view_settings.look = "None"
    world = bpy.data.worlds.new("w")
    sc.world = world
    world.use_nodes = True
    world.node_tree.nodes["Background"].inputs["Color"].default_value = (0.24, 0.27, 0.32, 1)
    world.node_tree.nodes["Background"].inputs["Strength"].default_value = 1.0
    sun = bpy.data.objects.new("sun", bpy.data.lights.new("sun", "SUN"))
    sun.data.energy = 3.3
    sun.data.angle = math.radians(0.6)
    sc.collection.objects.link(sun)
    cam = bpy.data.objects.new("cam", bpy.data.cameras.new("cam"))
    sc.collection.objects.link(cam)
    sc.camera = cam

    images = {}

    def image(name, non_color):
        if name not in images:
            im = bpy.data.images.load(os.path.join(surf, name + ".png"))
            if non_color: im.colorspace_settings.name = "Non-Color"
            else: im.alpha_mode = "CHANNEL_PACKED"
            images[name] = im
        return images[name]

    def material(key, clamp_v=False):
        tex = key.split("_")[0]
        tint, shin, spec = PREVIEW_MATS[key]
        m = bpy.data.materials.new(key)
        m.use_nodes = True
        nt = m.node_tree
        nt.nodes.clear()
        N, L = nt.nodes.new, nt.links.new
        uvn = N("ShaderNodeUVMap"); uvn.uv_map = "UVMap"
        vec = uvn.outputs["UV"]
        if clamp_v:   # U repeats, V clamps (regen heat coordinate, bolt rim)
            sep = N("ShaderNodeSeparateXYZ"); L(vec, sep.inputs[0])
            fr = N("ShaderNodeMath"); fr.operation = "FRACT"; L(sep.outputs[0], fr.inputs[0])
            cl = N("ShaderNodeMath"); cl.operation = "MULTIPLY"; cl.use_clamp = True; L(sep.outputs[1], cl.inputs[0]); cl.inputs[1].default_value = 1.0
            comb = N("ShaderNodeCombineXYZ"); L(fr.outputs[0], comb.inputs[0]); L(cl.outputs[0], comb.inputs[1])
            vec = comb.outputs[0]
        ti = N("ShaderNodeTexImage"); ti.image = image(tex, False); L(vec, ti.inputs[0])
        tn = N("ShaderNodeTexImage"); tn.image = image(tex + "_n", True); L(vec, tn.inputs[0])
        if clamp_v: ti.extension = tn.extension = "EXTEND"
        nm = N("ShaderNodeNormalMap"); nm.space = "TANGENT"; nm.uv_map = "UVMap"; L(tn.outputs["Color"], nm.inputs["Color"])
        mul = N("ShaderNodeMix"); mul.data_type = "RGBA"; mul.blend_type = "MULTIPLY"; mul.inputs["Factor"].default_value = 1
        L(ti.outputs["Color"], mul.inputs["A"]); mul.inputs["B"].default_value = (*tint, 1)
        dif = N("ShaderNodeBsdfDiffuse"); L(mul.outputs["Result"], dif.inputs["Color"]); L(nm.outputs[0], dif.inputs["Normal"])
        try: glo = N("ShaderNodeBsdfGlossy")
        except RuntimeError: glo = N("ShaderNodeBsdfAnisotropic")
        # Blinn exponent n = 128 s  ->  GGX alpha ~ sqrt(2 / (n + 2)); scale the lobe so its peak matches the
        # unnormalised Blinn-Phong peak (specColor x gloss) relative to the Lambert term.
        alpha = math.sqrt(2 / (128 * shin + 2))
        glo.inputs["Roughness"].default_value = math.sqrt(alpha)
        k = 4 * alpha * alpha * 0.8
        gm = N("ShaderNodeMix"); gm.data_type = "RGBA"; gm.blend_type = "MIX"
        L(ti.outputs["Alpha"], gm.inputs["Factor"]); gm.inputs["A"].default_value = (0, 0, 0, 1)
        gm.inputs["B"].default_value = (spec[0] * k, spec[1] * k, spec[2] * k, 1)
        L(gm.outputs["Result"], glo.inputs["Color"]); L(nm.outputs[0], glo.inputs["Normal"])
        add = N("ShaderNodeAddShader"); L(dif.outputs[0], add.inputs[0]); L(glo.outputs[0], add.inputs[1])
        outn = N("ShaderNodeOutputMaterial"); L(add.outputs[0], outn.inputs["Surface"])
        return m

    mats = {}

    def mat(key, clamp_v=False):
        if key not in mats: mats[key] = material(key, clamp_v)
        return mats[key]

    def mesh_object(name, verts, faces, uvs, material_):
        me = bpy.data.meshes.new(name)
        me.from_pydata(verts, [], faces)
        uv = me.uv_layers.new(name="UVMap")
        vi = np.zeros(len(me.loops), int)
        me.loops.foreach_get("vertex_index", vi)
        uv.data.foreach_set("uv", np.asarray(uvs, float)[vi].ravel())
        me.polygons.foreach_set("use_smooth", [True] * len(me.polygons))
        me.materials.append(material_)
        me.update()
        ob = bpy.data.objects.new(name, me)
        sc.collection.objects.link(ob)
        return ob

    def lathe(name, prof, material_, u_tiles=1.0, v_len=1.0, v_off=0.0, inward=False, ring=192, sweep=1.0):
        """EngineGeometry/MeshBuilder.Lathe mapping: U = u_tiles around, V = v_off + arc length / v_len.
        sweep < 1 revolves only that fraction of a turn (cutaways)."""
        prof = np.asarray(prof, float)
        s = np.concatenate([[0], np.cumsum(np.hypot(*np.diff(prof, axis=0).T))])
        verts, uvs, faces = [], [], []
        for i, (r, z) in enumerate(prof):
            for j in range(ring + 1):
                ph = 2 * math.pi * sweep * j / ring - math.pi * (1 - sweep)
                verts.append((r * math.cos(ph), r * math.sin(ph), z))
                uvs.append((u_tiles * sweep * j / ring, v_off + s[i] / v_len))
        w = ring + 1
        for i in range(len(prof) - 1):
            for j in range(ring):
                a, b, c, d = i * w + j, i * w + j + 1, (i + 1) * w + j + 1, (i + 1) * w + j
                faces.append((a, b, c, d) if inward else (a, d, c, b))
        return mesh_object(name, verts, faces, uvs, material_)

    def pipe(name, path, r, material_, sides=48):
        """MeshBuilder.Sweep mapping: U = 0..1 around, V = arc length / circumference."""
        path = [Vector(p) for p in path]
        circ = 2 * math.pi * r
        t0 = (path[1] - path[0]).normalized()
        a = t0.orthogonal().normalized()
        verts, uvs, faces, s = [], [], [], 0.0
        for i, p in enumerate(path):
            if i: s += (p - path[i - 1]).length
            t = (path[min(i + 1, len(path) - 1)] - path[max(i - 1, 0)]).normalized()
            a = (a - a.dot(t) * t).normalized()
            b = t.cross(a)
            for j in range(sides + 1):
                ph = 2 * math.pi * j / sides
                verts.append(tuple(p + (a * math.cos(ph) + b * math.sin(ph)) * r))
                uvs.append((j / sides, s / circ))
        w = sides + 1
        for i in range(len(path) - 1):
            for j in range(sides):
                q = (i * w + j, i * w + j + 1, (i + 1) * w + j + 1, (i + 1) * w + j)
                faces.append(q)
        return mesh_object(name, verts, faces, uvs, material_)

    def arc_len(p):
        p = np.asarray(p, float)
        return float(np.hypot(*np.diff(p, axis=0).T).sum())

    def line(r0, z0, r1, z1, n=1):
        return [(r0 + (r1 - r0) * t, z0 + (z1 - z0) * t) for t in np.linspace(0, 1, n + 1)]

    def bezier_bell(rt, zt, re, length, a0=32.0, a1=8.0, n=64):
        p0, p2 = np.array([rt, zt]), np.array([re, zt - length])
        d0 = np.array([math.sin(math.radians(a0)), -math.cos(math.radians(a0))])
        d2 = np.array([math.sin(math.radians(a1)), -math.cos(math.radians(a1))])
        # intersection of p0 + s d0 and p2 - u d2
        M = np.array([d0, d2]).T
        s_, u_ = np.linalg.solve(M, p2 - p0)
        p1 = p0 + s_ * d0
        t = np.linspace(0, 1, n)[:, None]
        return [tuple(x) for x in (1 - t) ** 2 * p0 + 2 * (1 - t) * t * p1 + t ** 2 * p2]

    def flange(name, r, z_top, z_bot, t, steel_key="steel"):
        h = z_top - z_bot
        bolts = max(8, round(2 * math.pi * (r + t) / (1.6 * h)))
        lathe(name + "_top", line(r * 0.995, z_top, r + t, z_top), mat(steel_key), 8, 0.6)
        lathe(name + "_rim", line(r + t, z_top, r + t, z_bot), mat("bolts", True), bolts, h)
        lathe(name + "_bot", line(r + t, z_bot, r * 0.995, z_bot), mat(steel_key), 8, 0.6)

    def build_engine(ext_key, regen_key, soot_key):
        for ob in list(bpy.data.objects):
            if ob.type == "MESH": bpy.data.objects.remove(ob)
        rt, rc, lc = 0.12, 0.21, 0.30
        tubes = min(max(round(2 * math.pi * rc / 0.035 * 2 / 16) * 16, 24), 240)
        tiles = tubes / 16
        lathe("dome", [(rc * 0.45 + (rc - rc * 0.45) * math.sin(t), lc + 0.07 + 0.08 * math.cos(t)) for t in np.linspace(0, math.pi / 2, 13)], mat("steel"), 4, 0.6)
        flange("inj", rc, lc + 0.07, lc + 0.025, 0.02)
        lathe("chamber", line(rc, lc + 0.025, rc, 0.0, 4), mat("steel"), max(1, round(2 * math.pi * rc / 0.6)), 0.6)
        r_arc = 2.5 * rt - 1.5 * rt * math.sin(math.radians(60))
        cone = max(rc - r_arc, 0) / math.tan(math.radians(30))
        zt = -cone - 0.75 * rt
        conv = [(rc, 0.0)] + [(2.5 * rt + 1.5 * rt * math.sin(th), zt - 1.5 * rt * math.cos(th))
                              for th in np.radians(np.linspace(-120, -90, 13))]
        lathe("conv", conv, mat(regen_key, True), tiles, -2 * arc_len(conv), 0.5)
        re = rt * math.sqrt(10)
        bell = bezier_bell(rt, zt, re, 0.8 * (re - rt) / math.tan(math.radians(15)))
        lathe("regen", bell, mat(regen_key, True), tiles, arc_len(bell))
        z_end = bell[-1][1]
        inner = [(r - 0.004, z) for r, z in bell][::-1]
        lathe("soot", inner, mat(soot_key), 8, max(0.3, re), inward=True)
        lathe("throat_cap", line(rt - 0.004, zt, 0.001, zt), mat(soot_key), 1, 1)   # faces down the bell
        # the joint flange sits on the regen end; the extension continues to eps 35
        flange("joint", re, z_end + 0.022, z_end - 0.012, 0.016)
        r2 = rt * math.sqrt(35)
        ext = bezier_bell(re, z_end, r2, (r2 - re) / math.tan(math.radians(9)), 9.5, 6.0, 40)
        circ = 2 * math.pi * r2
        lathe("ext", ext, mat(ext_key), max(1, round(circ / 0.8)), 0.8)
        lathe("ext_in", [(r - 0.004, z) for r, z in ext], mat(ext_key), max(1, round(circ / 0.8)), 0.8, inward=True)
        return zt, z_end, ext[-1][1]

    def shoot(path, loc, target, lens=50, sun_rot=(52, 0, 35)):
        cam.location = Vector(loc)
        cam.data.lens = lens
        cam.rotation_euler = (Vector(target) - cam.location).to_track_quat("-Z", "Y").to_euler()
        sun.rotation_euler = tuple(math.radians(a) for a in sun_rot)
        sc.render.filepath = os.path.join(out, path)
        t0 = time.time()
        bpy.ops.render.render(write_still=True)
        print(f"[render] {path} {time.time() - t0:.1f} s", flush=True)

    zt, z_end, z_exit = build_engine("niobium", "regen", "soot_hydrolox")
    shoot("engine_niobium.png", (2.6, -2.2, -0.2), (0, 0, -0.75), 40)
    shoot("throat_close.png", (0.55, -0.45, zt + 0.05), (0, 0, zt - 0.05), 50)
    shoot("bell_close.png", (0.75, -0.55, z_end + 0.25), (0.15, 0, z_end + 0.05), 50, (40, 0, 60))
    shoot("flange_close.png", (0.42, -0.3, 0.42), (0.1, 0, 0.3), 50)
    shoot("inside.png", (0.25, -0.2, z_exit - 1.6), (0, 0, zt), 35, (150, 0, 30))   # sun shining up into the bell
    for soot_key, regen_key in (("soot_hydrolox", "regen"), ("soot_kerolox", "regen_kerolox")):
        # cutaway of an all-regen bell (half a turn) to show the inside wall, lit from the open side
        for ob in list(bpy.data.objects):
            if ob.type == "MESH": bpy.data.objects.remove(ob)
        rt = 0.12
        re = rt * math.sqrt(16)
        bell = bezier_bell(rt, 0.0, re, 0.8 * (re - rt) / math.tan(math.radians(15)))
        tiles = 5
        lathe("regen_half", bell, mat(regen_key, True), tiles, arc_len(bell), sweep=0.5)
        inner = [(r - 0.006, z) for r, z in bell][::-1]
        lathe("soot_half", inner, mat(soot_key), 8, max(0.3, re), inward=True, sweep=0.5)
        shoot(f"cutaway_{soot_key}.png", (-1.5, -0.9, -0.35), (0.12, 0, -0.62), 45, (60, 0, -110))
    build_engine("carbon", "regen_kerolox", "soot_kerolox")
    shoot("engine_carbon_kerolox.png", (2.6, -2.2, -0.2), (0, 0, -0.75), 40)
    shoot("ext_carbon_close.png", (1.05, -0.8, z_exit + 0.45), (0.35, 0, z_exit + 0.15), 50, (40, 0, 60))
    shoot("ext_close_regenjoint.png", (0.9, -0.7, z_end + 0.05), (0.25, 0, z_end - 0.15), 50, (40, 0, 60))
    for ob in list(bpy.data.objects):
        if ob.type == "MESH": bpy.data.objects.remove(ob)
    for k, (key, r) in enumerate((("foil", 0.035), ("hotduct", 0.05), ("lines", 0.03))):
        y = (k - 1) * 0.22
        pts = [(-0.55 + 0.02 * i, y, 0.2) for i in range(20)]
        cx, cz, R = -0.17, -0.05, 0.25
        pts += [(cx + R * math.sin(a), y, cz + R * math.cos(a)) for a in np.linspace(0, math.pi / 2, 32)][1:]
        pts += [(cx + R, y, cz - 0.02 * i) for i in range(1, 16)]
        pipe(key, pts, r, mat(key))
    shoot("pipes.png", (0.9, -1.2, 0.55), (-0.05, 0, -0.02), 50, (45, 0, 25))
    shoot("pipes_close.png", (0.35, -0.55, 0.32), (0.0, 0, 0.1), 50, (45, 0, 25))
    print("[render] done ->", out)


if __name__ == "__main__":
    main()
