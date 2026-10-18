using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace ProcEngines
{
    /// <summary>
    /// Shared materials and their textures (DESIGN.md 8.4, ASSETS.md 5). Every surface loads from
    /// GameData/ProcEngines/PluginData/Surfaces (`name.png` = sRGB albedo + gloss in alpha -> DXT5,
    /// `name_n.png` = OpenGL normal map -> linear, swizzled, DXT5); a missing file falls back, per file, to a texture
    /// generated here. Textures and materials are created on first use only (carbon-carbon, for example, only when a
    /// Late-tech bell asks for it). Tiling is baked into the UVs, so one shared material serves every engine and
    /// identical engines batch. The heat glow is a per-material property block on the glowing slots (KSP
    /// instantiates part materials and keeps its own renderer-wide block, so writing a material won't show).
    ///
    /// Conventions (ASSETS.md 2): albedo alpha is gloss, the specular mask of KSP/Bumped Specular. Normal maps are
    /// OpenGL tangent space, green = +V (MeshBuilder's tangents follow +U with the binormal on +V): a bump has
    /// nx = +(u - uc) and ny = +(v - vc) toward its rim, i.e. n = (-dH/du, -dH/dv, 1) for a height field H.
    /// </summary>
    public static class EngineMaterials
    {
        public const float RadiativeTile = 0.8f;   // metres per texture repeat on the radiative extension (ASSETS.md 5)
        public const int TubesPerTile = 16;        // cooling tubes per U repeat of the regen texture
        public const float SteelTile = 0.6f;       // metres per texture repeat on steel surfaces
        public const int CarbonTowsPerTile = 32;   // carbon-carbon twill tows per tile (2.5 cm at the 0.8 m tile)

        /// <summary>The ASSETS.md 5 surfaces.</summary>
        public static readonly string[] Surfaces = { "regen", "steel", "bolts", "soot", "niobium", "carbon", "foil", "hotduct", "lines" };

        public static string ShaderReport = "";

        /// <summary>One entry per surface texture created so far: file or generated, size, format.</summary>
        public static string TextureReport => string.Join("; ", report.ToArray());

        static Shader bumped, emissive;
        static Material steel, bolts, machine, lines, foil, hotDuct, niobium, carbon, authored;
        static readonly Dictionary<string, Material> regen = new Dictionary<string, Material>(), soot = new Dictionary<string, Material>();
        static readonly Dictionary<string, Texture2D> textures = new Dictionary<string, Texture2D>();
        static readonly List<string> report = new List<string>();
        static Texture2D flatNormal, white, glowRamp;

        // ------------------------------------------------------------------ public API

        /// <summary>Chamber, mount and convergent: EngineGeometry.MatSteel, MatRegenStatic, MatBolts.</summary>
        public static Material[] Static(string propellant)
        {
            EnsureShaders();
            return new[] { Steel(), Regen(propellant), Bolts() };
        }

        /// <summary>Bell: regen tubes, the extension (niobium, or carbon-carbon at the Late tech level), the inside, steel, bolts.</summary>
        public static Material[] Bell(string techLevel, string propellant)
        {
            EnsureShaders();
            return new[] { Regen(propellant), techLevel == "Late" ? Carbon() : Niobium(), Soot(propellant), Steel(), Bolts() };
        }

        /// <summary>Kit: KitBuilder.MatStructure, MatMachine, MatLines, MatHot, MatFoil, MatAuthored (the baked
        /// atlas of the Blender pieces with its normal map; plain machine steel if the atlas is missing).</summary>
        public static Material[] Kit()
        {
            EnsureShaders();
            return new[] { Steel(), Machine(), Lines(), HotDuct(), Foil(), Authored() };
        }

        /// <summary>Approximate blackbody colour of hot metal (K), scaled to read against daylit albedo. Above ~1 a
        /// channel saturates, so the hottest metal also goes whiter.</summary>
        public static Color Glow(float tempK)
        {
            if (tempK < 750) return Color.black;
            float t = (tempK - 750) / 1450f;   // 750 K dull red .. 2200 K yellow-white
            Color c = Ramp(t, new Color(0.5f, 0.04f, 0f), new Color(1f, 0.25f, 0.04f), new Color(1f, 0.52f, 0.18f), new Color(1f, 0.8f, 0.55f));
            float brightness = Mathf.Pow(Mathf.Clamp01(t * 1.5f), 1.5f) * 1.15f;
            return c * brightness;
        }

        // ------------------------------------------------------------------ materials (created on first use)
        // Gloss maps now scale the specular per texel (mostly 0.2-0.7), so _SpecColor sits higher than it did with
        // the old opaque albedos (gloss 1 everywhere); the average highlight is similar, with variation.

        static void EnsureShaders()
        {
            if (bumped != null) return;
            bumped = Find("KSP/Bumped Specular", "KSP/Specular");
            emissive = Find("KSP/Emissive/Bumped Specular", "KSP/Emissive/Specular", "KSP/Bumped Specular");
            var props = new List<string>();
            for (int i = 0; i < emissive.GetPropertyCount(); i++) props.Add(emissive.GetPropertyName(i) + ":" + emissive.GetPropertyType(i));
            ShaderReport = $"bumped={bumped.name} emissive={emissive.name} [{string.Join(", ", props.ToArray())}]";
            Debug.Log("[ProcEngines] materials: " + ShaderReport);
        }

        static Shader Find(params string[] names)
        {
            foreach (string n in names) { Shader s = Shader.Find(n); if (s != null) return s; }
            return Shader.Find("Standard");
        }

        // (explicit `== null`, not `??`: Unity's null check also catches a destroyed object, which is then rebuilt)
        static Material Steel() { if (steel == null) steel = MakeSurface("steel", bumped, "steel", Color.white, 0.3f, new Color(0.66f, 0.66f, 0.68f)); return steel; }

        static Material Bolts() { if (bolts == null) bolts = MakeSurface("bolts", bumped, "bolts", Color.white, 0.36f, new Color(0.72f, 0.72f, 0.74f)); return bolts; }

        /// <summary>Primitive machinery (pump housings, COPVs, valves when their pieces are missing): darker cast steel.</summary>
        static Material Machine() { if (machine == null) machine = MakeSurface("machine", bumped, "steel", new Color(0.5f, 0.51f, 0.55f), 0.3f, new Color(0.55f, 0.55f, 0.57f)); return machine; }

        static Material Lines() { if (lines == null) lines = MakeSurface("lines", bumped, "lines", Color.white, 0.45f, new Color(0.85f, 0.85f, 0.86f)); return lines; }

        static Material Foil() { if (foil == null) foil = MakeSurface("foil", bumped, "foil", Color.white, 0.2f, new Color(1f, 0.86f, 0.56f)); return foil; }

        static Material HotDuct()
        {
            if (hotDuct != null) return hotDuct;   // Unity null check
            hotDuct = MakeSurface("hotduct", emissive, "hotduct", Color.white, 0.26f, new Color(0.58f, 0.56f, 0.55f));
            Glowing(hotDuct, White());
            return hotDuct;
        }

        static Material Niobium()
        {
            if (niobium != null) return niobium;
            niobium = MakeSurface("niobium", emissive, "niobium", Color.white, 0.13f, new Color(0.42f, 0.41f, 0.45f));
            Glowing(niobium, GlowRamp());
            return niobium;
        }

        static Material Carbon()
        {
            if (carbon != null) return carbon;
            carbon = MakeSurface("carbon", emissive, "carbon", Color.white, 0.2f, new Color(0.4f, 0.4f, 0.42f));
            Glowing(carbon, GlowRamp());
            return carbon;
        }

        static Material Authored()
        {
            if (authored != null) return authored;
            Texture2D a = KitAssets.Atlas(), an = KitAssets.AtlasNormal();
            authored = a != null ? Make("authored", bumped, a, an != null ? an : FlatNormal(), Color.white, 0.3f, new Color(0.72f, 0.72f, 0.74f)) : Machine();
            return authored;
        }

        /// <summary>Regen tube tint: kerolox engines run their tubes darker and more copper-coloured.</summary>
        static Material Regen(string propellant)
        {
            propellant = propellant ?? "";
            if (regen.TryGetValue(propellant, out Material m) && m != null) return m;
            Color tint = propellant == "Kerolox" ? new Color(1f, 0.88f, 0.76f) : propellant == "Methalox" ? new Color(1f, 0.95f, 0.9f) : Color.white;
            Color spec = new Color(0.86f * tint.r, 0.76f * tint.g, 0.64f * tint.b);   // metal: the highlight takes its colour
            m = MakeSurface("regen-" + (propellant.Length > 0 ? propellant : "default"), bumped, "regen", tint, 0.32f, spec);
            regen[propellant] = m;
            return m;
        }

        /// <summary>Inside the nozzle: heavy soot for kerosene, light for methane, clean heat-tinted metal for hydrogen.</summary>
        static Material Soot(string propellant)
        {
            propellant = propellant ?? "";
            if (soot.TryGetValue(propellant, out Material m) && m != null) return m;
            Color tint = propellant == "Kerolox" ? new Color(0.07f, 0.065f, 0.06f) : propellant == "Methalox" ? new Color(0.2f, 0.18f, 0.16f) : new Color(0.42f, 0.32f, 0.26f);
            m = MakeSurface("soot-" + (propellant.Length > 0 ? propellant : "default"), bumped, "soot", tint, 0.3f, new Color(0.16f, 0.16f, 0.16f));
            soot[propellant] = m;
            return m;
        }

        static Material MakeSurface(string name, Shader sh, string surface, Color tint, float shininess, Color spec) =>
            Make(name, sh, Surface(surface, false), Surface(surface, true), tint, shininess, spec);

        static Material Make(string name, Shader sh, Texture2D albedo, Texture2D normal, Color tint, float shininess, Color spec)
        {
            var m = new Material(sh) { name = "ProcEngines/" + name };
            m.SetTexture("_MainTex", albedo);
            if (m.HasProperty("_BumpMap")) m.SetTexture("_BumpMap", normal);
            m.SetColor("_Color", tint);
            if (m.HasProperty("_SpecColor")) m.SetColor("_SpecColor", spec);
            if (m.HasProperty("_Shininess")) m.SetFloat("_Shininess", shininess);
            return m;
        }

        /// <summary>Emissive slot: the texture shapes the glow, the colour stays black until a property block heats it.</summary>
        static void Glowing(Material m, Texture2D emissiveTexture)
        {
            if (m.HasProperty("_Emissive")) m.SetTexture("_Emissive", emissiveTexture);
            if (m.HasProperty("_EmissiveColor")) m.SetColor("_EmissiveColor", Color.black);
        }

        // ------------------------------------------------------------------ textures

        /// <summary>A surface's albedo (normal = false) or normal map: the file if it loads, else generated.</summary>
        static Texture2D Surface(string name, bool normal)
        {
            string key = normal ? name + "_n" : name;
            if (textures.TryGetValue(key, out Texture2D t) && t != null) return t;
            bool clampV = name == "regen" || name == "bolts";   // regen V is the heat coordinate; bolts span the rim
            TextureWrapMode wv = clampV ? TextureWrapMode.Clamp : TextureWrapMode.Repeat;
            t = KitAssets.LoadTexture(Path.Combine(KitAssets.SurfaceDirectory, key + ".png"), normal, TextureWrapMode.Repeat, wv, normal ? -1 : DefaultGloss(name));
            string source = "file";
            if (t == null)
            {
                Color[] px = FallbackPixels(name, normal, out int w, out int h);
                t = Upload("ProcEngines/" + key + " (generated)", px, w, h, normal, TextureWrapMode.Repeat, wv, true);
                source = "generated";
            }
            report.Add($"{key} {source} {t.width}x{t.height} {t.format}");
            textures[key] = t;
            return t;
        }

        /// <summary>Gloss for an albedo file that has no alpha channel at all.</summary>
        static float DefaultGloss(string name)
        {
            switch (name)
            {
                case "soot": return 0.1f;
                case "niobium": return 0.2f;
                case "carbon": return 0.3f;
                case "hotduct": return 0.35f;
                case "foil": return 0.65f;
                case "lines": return 0.55f;
                default: return 0.45f;
            }
        }

        static Texture2D Upload(string name, Color[] px, int w, int h, bool linear, TextureWrapMode wrapU, TextureWrapMode wrapV, bool compress)
        {
            var t = new Texture2D(w, h, TextureFormat.RGBA32, true, linear) { name = name, wrapModeU = wrapU, wrapModeV = wrapV, filterMode = FilterMode.Trilinear, anisoLevel = 4 };
            t.SetPixels(px);
            t.Apply(true, false);
            if (compress && (w & 3) == 0 && (h & 3) == 0) t.Compress(true);
            t.Apply(false, true);
            return t;
        }

        static Texture2D FlatNormal()
        {
            if (flatNormal == null) flatNormal = Upload("ProcEngines/flat_n", Fill(4, 4, (u, v) => Normal(0, 0)), 4, 4, true, TextureWrapMode.Repeat, TextureWrapMode.Repeat, false);
            return flatNormal;
        }

        static Texture2D White()
        {
            if (white == null) white = Upload("ProcEngines/white", Fill(4, 4, (u, v) => Color.white), 4, 4, false, TextureWrapMode.Repeat, TextureWrapMode.Repeat, false);
            return white;
        }

        /// <summary>Emissive ramp along the radiative extension (V = 0 at the joint, 1 at the exit): the wall is hottest
        /// where it meets the regen section and cools toward the exit, reddening as it goes. Uncompressed (smooth).</summary>
        static Texture2D GlowRamp()
        {
            if (glowRamp == null)
                glowRamp = Upload("ProcEngines/glow_ramp", Fill(4, 64, (u, v) =>
                {
                    float b = Mathf.Pow(1 - 0.92f * v, 2.6f);
                    return new Color(b, b * (1 - 0.6f * v), b * (1 - 0.85f * v), 1);
                }), 4, 64, false, TextureWrapMode.Clamp, TextureWrapMode.Clamp, false);
            return glowRamp;
        }

        /// <summary>DXT5nm-style texel for a tangent-space normal (nx, ny, 1): x in alpha, y in green, R = B = 1 (see
        /// KitAssets.LoadTexture).</summary>
        static Color Normal(float nx, float ny)
        {
            float len = Mathf.Sqrt(nx * nx + ny * ny + 1);
            return new Color(1, ny / len * 0.5f + 0.5f, 1, nx / len * 0.5f + 0.5f);
        }

        // ------------------------------------------------------------------ generated fallbacks

        /// <summary>The code-generated stand-in for a surface texture (rows bottom to top, as Texture2D.SetPixels).
        /// Public so tools can render it outside the game; it touches no engine state.</summary>
        public static Color[] FallbackPixels(string name, bool normal, out int w, out int h)
        {
            switch (name)
            {
                case "regen":
                    if (normal) { w = 512; h = 64; return RegenNormal(w, h); }
                    w = 512; h = 256; return Fill(w, h, RegenAlbedo);
                case "steel":
                    w = 256; h = 256;
                    return normal ? FromHeight(w, h, SteelHeight, 2f, true) : Fill(w, h, SteelAlbedo);
                case "bolts":
                    w = 128; h = 80;
                    return Fill(w, h, normal ? (Func<float, float, Color>)BoltNormal : BoltAlbedo);
                case "soot":
                    w = 128; h = 128;
                    return normal ? FromHeight(w, h, SootHeight, 2.5f, true) : Fill(w, h, SootAlbedo);
                case "niobium":
                    w = 256; h = 256;
                    return normal ? FromHeight(w, h, NiobiumHeight, 3f, true) : Fill(w, h, NiobiumAlbedo);
                case "carbon":
                    w = 256; h = 256;
                    return Fill(w, h, normal ? (Func<float, float, Color>)CarbonNormal : CarbonAlbedo);
                case "foil":
                    w = 256; h = 256;
                    return normal ? FromHeight(w, h, FoilHeight, 14f, true) : Fill(w, h, FoilAlbedo);
                case "hotduct":
                    w = 256; h = 256;
                    return normal ? FromHeight(w, h, HotDuctHeight, 6f, true) : Fill(w, h, HotDuctAlbedo);
                case "lines":
                    w = 256; h = 256;
                    return normal ? FromHeight(w, h, LinesHeight, 1f, true) : Fill(w, h, LinesAlbedo);
                default:
                    w = h = 4;
                    return Fill(w, h, normal ? (Func<float, float, Color>)((u, v) => Normal(0, 0)) : (u, v) => new Color(0.6f, 0.6f, 0.6f, 0.4f));
            }
        }

        static Color[] Fill(int w, int h, Func<float, float, Color> f)
        {
            var px = new Color[w * h];
            for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) px[y * w + x] = f((x + 0.5f) / w, (y + 0.5f) / h);
            return px;
        }

        /// <summary>Normal map from a height field sampled once per texel; `strength` scales the per-texel slope.
        /// n = (-dH/du, -dH/dv, 1): green = +V. U always wraps; V wraps or clamps.</summary>
        static Color[] FromHeight(int w, int h, Func<float, float, float> height, float strength, bool wrapV)
        {
            var H = new float[w * h];
            for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) H[y * w + x] = height((x + 0.5f) / w, (y + 0.5f) / h);
            var px = new Color[w * h];
            for (int y = 0; y < h; y++)
            {
                int y0 = wrapV ? (y + h - 1) % h : Math.Max(y - 1, 0), y1 = wrapV ? (y + 1) % h : Math.Min(y + 1, h - 1);
                for (int x = 0; x < w; x++)
                {
                    int x0 = (x + w - 1) % w, x1 = (x + 1) % w;
                    float du = (H[y * w + x1] - H[y * w + x0]) * 0.5f, dv = (H[y1 * w + x] - H[y0 * w + x]) * (y1 - y0 == 2 || wrapV ? 0.5f : 1f);
                    px[y * w + x] = Normal(-du * strength, -dv * strength);
                }
            }
            return px;
        }

        // ---- noise: deterministic, periodic (period = frequency cells), so every texture tiles

        static float Hash(int x, int y, int seed)
        {
            unchecked
            {
                uint h = (uint)(x * 374761393 + y * 668265263 + seed * 1442695041);
                h = (h ^ (h >> 13)) * 1274126177u;
                return ((h ^ (h >> 16)) & 0xFFFFFF) / (float)0xFFFFFF;
            }
        }

        static int Wrap(int i, int n) => ((i % n) + n) % n;

        /// <summary>Value noise on a periodic lattice (fu x fv cells per tile).</summary>
        static float Noise(float u, float v, int fu, int fv, int seed)
        {
            float x = u * fu, y = v * fv;
            int x0 = Mathf.FloorToInt(x), y0 = Mathf.FloorToInt(y);
            float tx = x - x0, ty = y - y0;
            tx = tx * tx * (3 - 2 * tx); ty = ty * ty * (3 - 2 * ty);
            int ax = Wrap(x0, fu), bx = Wrap(x0 + 1, fu), ay = Wrap(y0, fv), by = Wrap(y0 + 1, fv);
            float a = Hash(ax, ay, seed), b = Hash(bx, ay, seed), c = Hash(ax, by, seed), d = Hash(bx, by, seed);
            return Mathf.Lerp(Mathf.Lerp(a, b, tx), Mathf.Lerp(c, d, tx), ty);
        }

        static float Fbm(float u, float v, int fu, int fv, int seed, int octaves = 4)
        {
            float sum = 0, amp = 0.5f, norm = 0;
            for (int o = 0; o < octaves; o++) { sum += amp * Noise(u, v, fu << o, fv << o, seed + o * 31); norm += amp; amp *= 0.5f; }
            return sum / norm;
        }

        static float Smooth(float e0, float e1, float x)
        {
            float t = Mathf.Clamp01((x - e0) / (e1 - e0));
            return t * t * (3 - 2 * t);
        }

        /// <summary>Sparse thin scratches (0..1): one random short segment in some of the cells x cells grid,
        /// periodic. `width` in tile units.</summary>
        static float Scratches(float u, float v, int cells, int seed, float width, float density = 0.45f)
        {
            int cx = Mathf.FloorToInt(u * cells), cy = Mathf.FloorToInt(v * cells);
            float best = 0;
            for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    int i = cx + dx, j = cy + dy, wi = Wrap(i, cells), wj = Wrap(j, cells);
                    if (Hash(wi, wj, seed) > density) continue;
                    float px = (i + Hash(wi, wj, seed + 1)) / cells, py = (j + Hash(wi, wj, seed + 2)) / cells;
                    float ang = Hash(wi, wj, seed + 3) * Mathf.PI, half = (0.35f + 0.65f * Hash(wi, wj, seed + 4)) * 0.6f / cells;
                    float ex = Mathf.Cos(ang), ey = Mathf.Sin(ang), rx = u - px, ry = v - py;
                    float along = rx * ex + ry * ey;
                    if (Mathf.Abs(along) > half) continue;
                    float d = Mathf.Abs(-rx * ey + ry * ex);
                    float s = Mathf.Clamp01(1 - d / width) * Mathf.Sqrt(1 - Mathf.Abs(along) / half) * (0.5f + 0.5f * Hash(wi, wj, seed + 5));
                    if (s > best) best = s;
                }
            return best;
        }

        static Color Ramp(float t, params Color[] stops)
        {
            t = Mathf.Clamp01(t) * (stops.Length - 1);
            int i = Mathf.Min(Mathf.FloorToInt(t), stops.Length - 2);
            return Color.Lerp(stops[i], stops[i + 1], t - i);
        }

        /// <summary>Heat tint of stainless/Inconel by how hot it ran: bare, straw, bronze, purple, blue, grey.</summary>
        static Color HeatTint(Color bare, float heat) => Ramp(heat, bare, new Color(0.74f, 0.63f, 0.42f), new Color(0.56f, 0.4f, 0.26f),
            new Color(0.42f, 0.3f, 0.36f), new Color(0.33f, 0.36f, 0.48f), new Color(0.36f, 0.36f, 0.38f));

        static Color WithGloss(Color c, float gloss) { c.a = Mathf.Clamp01(gloss); return c; }

        // ---- regen tubes: TubesPerTile per U repeat; V is the heat coordinate (0 = throat, 1 = coolest), clamped

        static Color RegenAlbedo(float u, float v)
        {
            float tu = u * TubesPerTile;
            int tube = Mathf.FloorToInt(tu);
            float x = 2 * (tu - tube) - 1;
            float crown = Mathf.Sqrt(Mathf.Max(1 - x * x, 0));
            float streak = Noise(tube / (float)TubesPerTile, v, TubesPerTile, 10, 7);
            float heat = Mathf.Pow(1 - v, 1.6f) * 0.95f + (streak - 0.5f) * 0.2f + (Hash(tube, 0, 3) - 0.5f) * 0.12f;
            Color bare = new Color(0.68f, 0.63f, 0.56f) * (0.92f + 0.12f * Hash(tube, 1, 5));
            Color c = HeatTint(bare, heat);
            float grime = Fbm(u, v, 64, 4, 9, 3);                           // streaks running along the tubes
            float dent = Smooth(0.72f, 0.8f, Fbm(u, v, 32, 8, 13, 3));       // small dents and handling marks
            c *= (0.86f + 0.2f * grime) * (1 - 0.12f * dent);
            c *= 0.55f + 0.45f * crown;                                      // dark grooves between tubes
            bool fillet = Mathf.Abs(x) > 0.86f;
            if (fillet) c = Color.Lerp(c, new Color(0.6f, 0.52f, 0.38f), 0.5f) * (0.8f + 0.2f * grime);   // braze fillet
            float gloss = fillet ? 0.2f : 0.22f + 0.5f * crown * crown;
            gloss *= (0.75f + 0.4f * grime) * (1 - 0.3f * Mathf.Clamp01(heat)) * (1 - 0.3f * dent);
            return WithGloss(c, gloss);
        }

        static Color[] RegenNormal(int w, int h)
        {
            // dents: a small height field; the tube profile itself is analytic (convex: tilts toward +U on the +U side)
            var px = FromHeight(w, h, (u, v) => -Smooth(0.72f, 0.8f, Fbm(u, v, 32, 8, 13, 3)) * 0.5f, 1.5f, false);
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    float u = (x + 0.5f) / w, tu = u * TubesPerTile, xt = 2 * (tu - Mathf.Floor(tu)) - 1;
                    Color c = px[y * w + x];
                    float nz = Mathf.Sqrt(Mathf.Max(1 - Sq(c.a * 2 - 1) - Sq(c.g * 2 - 1), 1e-4f));
                    float nx = (c.a * 2 - 1) / nz, ny = (c.g * 2 - 1) / nz;
                    if (Mathf.Abs(xt) <= 0.86f) nx += xt / Mathf.Sqrt(Mathf.Max(1 - xt * xt, 0.08f)) * 0.55f;   // flat braze fillet otherwise
                    px[y * w + x] = Normal(nx, ny);
                }
            return px;
        }

        static float Sq(float x) => x * x;

        // ---- steel: worn stainless / Inconel at SteelTile; circumferential turning marks, mottling, grime down the profile

        static float SteelHeight(float u, float v) => 0.35f * Noise(u, v, 2, 256, 11) + 0.25f * Fbm(u, v, 8, 8, 21, 3) - 0.6f * Scratches(u, v, 6, 17, 0.0035f);

        static Color SteelAlbedo(float u, float v)
        {
            float brush = Noise(u, v, 2, 256, 11);
            float mottle = Fbm(u, v, 4, 4, 13);
            float streak = Noise(u, v, 48, 3, 15) * Smooth(0.45f, 0.75f, Fbm(u, v, 3, 2, 16, 2));   // grime running +V
            float smudge = Smooth(0.6f, 0.8f, Fbm(u, v, 3, 3, 19, 3));                              // handling marks
            float scratch = Scratches(u, v, 6, 17, 0.0035f);
            float k = (0.95f + 0.06f * brush) * (0.9f + 0.16f * mottle) * (1 - 0.2f * streak) * (1 - 0.1f * smudge);
            Color c = new Color(0.6f, 0.61f, 0.63f) * k + new Color(0.1f, 0.1f, 0.1f) * scratch;
            float gloss = 0.44f + 0.1f * (brush - 0.5f) + 0.16f * (mottle - 0.5f) - 0.22f * streak - 0.15f * smudge + 0.25f * scratch;
            return WithGloss(c, gloss);
        }

        // ---- bolts: one hex head with washer per tile; the tile is 1.6x as wide as high; V spans the rim (clamped)

        const float BoltAspect = 1.6f, HeadApothem = 0.21f, WasherRadius = 0.31f;

        static float Hex(float x, float y) => Mathf.Max(Mathf.Abs(y), 0.866f * Mathf.Abs(x) + 0.5f * Mathf.Abs(y));

        static Color BoltAlbedo(float u, float v)
        {
            float x = (u - 0.5f) * BoltAspect, y = v - 0.5f, d = Mathf.Sqrt(x * x + y * y), hd = Hex(x, y);
            float grime = Fbm(u, v, 8, 4, 17, 3);
            float edge = Smooth(0.09f, 0.0f, Mathf.Min(v, 1 - v)) * Smooth(0.35f, 0.6f, Noise(u, v, 16, 2, 18));   // worn rim edges
            Color flange = new Color(0.52f, 0.53f, 0.55f) * (0.9f + 0.14f * grime);
            Color c; float gloss;
            if (hd < HeadApothem) { c = new Color(0.64f, 0.64f, 0.66f) * (0.9f + 0.12f * grime); gloss = hd > HeadApothem - 0.04f ? 0.7f : 0.55f; }
            else if (d < WasherRadius) { c = new Color(0.58f, 0.58f, 0.6f) * (0.92f + 0.1f * grime); gloss = 0.5f; }
            else if (d < WasherRadius + 0.05f) { c = flange * 0.6f; gloss = 0.15f; }                                  // contact shadow / grime ring
            else { c = Color.Lerp(flange, new Color(0.7f, 0.7f, 0.72f), edge); gloss = 0.36f + 0.3f * edge - 0.12f * grime; }
            return WithGloss(c, gloss);
        }

        static Color BoltNormal(float u, float v)
        {
            float x = (u - 0.5f) * BoltAspect, y = v - 0.5f, d = Mathf.Sqrt(x * x + y * y), hd = Hex(x, y);
            if (hd < HeadApothem)
            {
                if (hd < HeadApothem - 0.045f) return Normal(0, 0);   // flat top of the head
                // chamfer: tilt away from the centre, across the nearest hex face
                float fx, fy;
                if (Mathf.Abs(y) >= 0.866f * Mathf.Abs(x) + 0.5f * Mathf.Abs(y)) { fx = 0; fy = Mathf.Sign(y); }
                else { fx = 0.866f * Mathf.Sign(x); fy = 0.5f * Mathf.Sign(y); }
                return Normal(fx * 0.9f, fy * 0.9f);
            }
            if (d < WasherRadius && d > WasherRadius - 0.03f) return Normal(x / d * 0.5f, y / d * 0.5f);   // rounded washer edge
            return Normal(0, 0);
        }

        // ---- soot: inside of the nozzle, greyscale (tinted per propellant); streaks along V, flaking deposits

        static float SootFlakes(float u, float v) => Smooth(0.6f, 0.66f, Fbm(u, v, 8, 8, 21, 4));

        static float SootHeight(float u, float v) => 0.4f * Fbm(u, v, 16, 2, 19, 3) - 0.8f * SootFlakes(u, v);

        static Color SootAlbedo(float u, float v)
        {
            float k = 0.72f + 0.35f * Fbm(u, v, 16, 2, 19, 3);
            float flake = SootFlakes(u, v);
            k = Mathf.Lerp(k, 1.35f, 0.5f * flake);   // flaked off: the bare wall shows through
            return WithGloss(new Color(k, k, k), 0.08f + 0.18f * flake);
        }

        // ---- niobium: C103 with silicide coating; matte grey-violet, mottled, flow streaks along V, coating chips

        static float NiobiumChips(float u, float v) => Smooth(0.76f, 0.8f, Fbm(u, v, 12, 12, 27, 3)) * Smooth(0.4f, 0.6f, Noise(u, v, 4, 4, 28));

        static float NiobiumHeight(float u, float v) => 0.5f * Fbm(u, v, 16, 16, 31, 3) - 0.7f * NiobiumChips(u, v);

        static Color NiobiumAlbedo(float u, float v)
        {
            float m = Fbm(u, v, 4, 4, 23);
            float streak = Noise(u, v, 32, 2, 29);
            float k = 0.84f + 0.16f * m + 0.06f * streak;
            Color c = new Color(0.5f * k, 0.48f * k, 0.53f * k);
            float chip = NiobiumChips(u, v);
            c = Color.Lerp(c, new Color(0.64f, 0.63f, 0.62f), chip);   // bare metal where the coating chipped
            return WithGloss(c, 0.16f + 0.06f * (m - 0.5f) + 0.3f * chip);
        }

        // ---- carbon-carbon: 2/2 twill, CarbonTowsPerTile tows per tile; fibre sheen, resin-rich patches

        static Color CarbonAlbedo(float u, float v)
        {
            const int tows = CarbonTowsPerTile;
            int i = Mathf.FloorToInt(u * tows), j = Mathf.FloorToInt(v * tows);
            bool warp = ((i + j) & 3) < 2;   // warp tows run along V
            float across = warp ? u * tows - i : v * tows - j;
            float fibre = Mathf.Sin(across * Mathf.PI);
            float lines = warp ? Noise(u, v, 256, 8, 35) : Noise(u, v, 8, 256, 36);   // individual fibres along the tow
            float resin = Smooth(0.55f, 0.75f, Fbm(u, v, 4, 4, 39, 3));
            float k = (warp ? 0.165f : 0.13f) * (0.82f + 0.18f * fibre) * (0.9f + 0.14f * lines) * (0.92f + 0.12f * Noise(u, v, 8, 8, 37)) * (1 - 0.25f * resin);
            return WithGloss(new Color(k, k, k * 1.06f), 0.2f + 0.22f * fibre * (0.6f + 0.4f * lines) + 0.18f * resin);
        }

        static Color CarbonNormal(float u, float v)
        {
            const int tows = CarbonTowsPerTile;
            int i = Mathf.FloorToInt(u * tows), j = Mathf.FloorToInt(v * tows);
            bool warp = ((i + j) & 3) < 2;
            float across = 2 * (warp ? u * tows - i : v * tows - j) - 1;   // -1..1 across the tow, convex
            return warp ? Normal(across * 0.55f, 0) : Normal(0, across * 0.55f);
        }

        // ---- foil: gold MLI on hydrogen lines; crinkle, tape wraps across U (bands of constant V), a seam along V

        static float FoilTape(float v)
        {
            float f = v * 3 - Mathf.Floor(v * 3);   // three wraps per tile
            return Smooth(0.0f, 0.015f, f) * Smooth(0.13f, 0.115f, f);
        }

        static float FoilHeight(float u, float v)
        {
            float crinkle = 1 - Mathf.Abs(2 * Fbm(u, v, 8, 8, 43, 4) - 1);   // ridged: sharp creases
            float seam = Smooth(0.012f, 0f, Mathf.Abs(u - 0.5f));            // overlap edge running along V
            return 0.6f * crinkle * (1 - 0.7f * FoilTape(v)) + 0.4f * FoilTape(v) + 0.25f * seam;
        }

        static Color FoilAlbedo(float u, float v)
        {
            float crinkle = 1 - Mathf.Abs(2 * Fbm(u, v, 8, 8, 43, 4) - 1);
            float k = 0.72f + 0.38f * crinkle;
            float tape = FoilTape(v);
            Color c = Color.Lerp(new Color(0.86f * k, 0.66f * k, 0.3f * k), new Color(0.7f, 0.68f, 0.62f) * (0.9f + 0.1f * crinkle), tape);   // tape: duller, silvery
            float grime = Smooth(0.6f, 0.85f, Fbm(u, v, 4, 4, 45, 3));
            c *= 1 - 0.18f * grime;
            return WithGloss(c, Mathf.Lerp(0.55f + 0.35f * crinkle, 0.35f, tape) - 0.2f * grime);
        }

        // ---- hot duct: uncooled turbine exhaust duct, heat-tinted Inconel mottled straw / blue / purple / brown, soot

        static float HotDuctHeight(float u, float v) => 0.5f * Fbm(u, v, 6, 6, 51, 4);

        static Color HotDuctAlbedo(float u, float v)
        {
            float heat = 0.2f + 0.8f * Fbm(u, v, 3, 3, 47);
            Color c = HeatTint(new Color(0.6f, 0.6f, 0.62f), heat);
            float sootMask = Smooth(0.55f, 0.75f, Fbm(u, v, 4, 2, 49, 3)) * (0.6f + 0.4f * Noise(u, v, 32, 2, 50));
            c = Color.Lerp(c, new Color(0.12f, 0.11f, 0.1f), 0.7f * sootMask);
            return WithGloss(c, 0.34f + 0.1f * (heat - 0.5f) - 0.25f * sootMask);
        }

        // ---- lines: stainless propellant lines and kit structure; brushing along V, handling marks, grime

        static float LinesHeight(float u, float v) => 0.4f * Noise(u, v, 256, 2, 61) - 0.6f * Scratches(u, v, 5, 63, 0.004f);

        static Color LinesAlbedo(float u, float v)
        {
            float brush = Noise(u, v, 256, 2, 61);
            float mottle = Fbm(u, v, 4, 4, 62, 3);
            float smudge = Smooth(0.62f, 0.8f, Fbm(u, v, 3, 3, 64, 3));
            float grime = Smooth(0.5f, 0.8f, Fbm(u, v, 2, 6, 65, 3));
            float scratch = Scratches(u, v, 5, 63, 0.004f);
            float k = (0.94f + 0.07f * brush) * (0.92f + 0.12f * mottle) * (1 - 0.12f * smudge) * (1 - 0.15f * grime);
            Color c = new Color(0.7f, 0.7f, 0.71f) * k + new Color(0.08f, 0.08f, 0.08f) * scratch;
            return WithGloss(c, 0.56f + 0.12f * (brush - 0.5f) - 0.18f * smudge - 0.2f * grime + 0.2f * scratch);
        }
    }
}
