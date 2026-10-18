using System.Collections.Generic;
using UnityEngine;

namespace ProcEngines
{
    /// <summary>
    /// Procedural textures and shared materials (DESIGN.md 8.4). Everything is generated in code: no texture
    /// assets, nothing borrowed from other mods. Tiling is baked into the UVs, so one shared material serves every
    /// engine and identical engines batch. The heat glow is a per-material property block on the glowing slots
    /// (KSP instantiates part materials and keeps its own renderer-wide block, so writing a material won't show).
    ///
    /// Normal-map convention (both the lathe and the swept primitives): tangent = direction of increasing U, w = -1,
    /// so the bitangent points against increasing V. A bump therefore has nx = +(u - uc) and ny = -(v - vc).
    /// </summary>
    public static class EngineMaterials
    {
        public const float RadiativeTile = 0.8f;   // metres per texture repeat on the radiative extension (ASSETS.md 5)
        public const int TubesPerTile = 16;        // cooling tubes per U repeat of the regen texture
        public const float SteelTile = 0.6f;       // metres per texture repeat on steel surfaces

        static Material steel, bolts, machine, lines, foil, hotDuct, niobium, carbon;
        static readonly Dictionary<string, Material> regen = new Dictionary<string, Material>(), soot = new Dictionary<string, Material>();
        static Texture2D tubeNormal, tubeAlbedo, steelAlbedo, boltAlbedo, boltNormal, sootAlbedo, niobiumAlbedo, niobiumNormal,
            carbonAlbedo, carbonNormal, foilAlbedo, foilNormal, heatTintAlbedo, flatNormal, white, glowRamp;
        static Shader bumped, emissive;
        public static string ShaderReport = "";

        static Shader Find(params string[] names)
        {
            foreach (string n in names) { Shader s = Shader.Find(n); if (s != null) return s; }
            return Shader.Find("Standard");
        }

        static Texture2D Tex(int w, int h, bool linear, System.Func<float, float, Color> f, TextureWrapMode wrapU = TextureWrapMode.Repeat, TextureWrapMode wrapV = TextureWrapMode.Repeat)
        {
            var t = new Texture2D(w, h, TextureFormat.RGBA32, true, linear) { wrapModeU = wrapU, wrapModeV = wrapV, filterMode = FilterMode.Trilinear, anisoLevel = 4 };
            var px = new Color[w * h];
            for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) px[y * w + x] = f((x + 0.5f) / w, (y + 0.5f) / h);
            t.SetPixels(px);
            t.Apply(true, true);
            return t;
        }

        /// <summary>Unity's UnpackNormal reads x from alpha and y from green (DXT5nm layout); fill both layouts.</summary>
        static Color Normal(float nx, float ny)
        {
            float len = Mathf.Sqrt(nx * nx + ny * ny + 1);
            float x = nx / len * 0.5f + 0.5f, y = ny / len * 0.5f + 0.5f;
            return new Color(x, y, 1, x);
        }

        // ------------------------------------------------------------------ deterministic tileable noise

        static float Hash(int x, int y, int seed)
        {
            unchecked
            {
                uint h = (uint)(x * 374761393 + y * 668265263 + seed * 1442695041);
                h = (h ^ (h >> 13)) * 1274126177u;
                return ((h ^ (h >> 16)) & 0xFFFFFF) / (float)0xFFFFFF;
            }
        }

        /// <summary>Value noise on a periodic lattice (period = frequency cells), so textures tile.</summary>
        static float Noise(float u, float v, int fu, int fv, int seed)
        {
            float x = u * fu, y = v * fv;
            int x0 = Mathf.FloorToInt(x), y0 = Mathf.FloorToInt(y);
            float tx = x - x0, ty = y - y0;
            tx = tx * tx * (3 - 2 * tx); ty = ty * ty * (3 - 2 * ty);
            int Wx(int i) => ((i % fu) + fu) % fu;
            int Wy(int i) => ((i % fv) + fv) % fv;
            float a = Hash(Wx(x0), Wy(y0), seed), b = Hash(Wx(x0 + 1), Wy(y0), seed), c = Hash(Wx(x0), Wy(y0 + 1), seed), d = Hash(Wx(x0 + 1), Wy(y0 + 1), seed);
            return Mathf.Lerp(Mathf.Lerp(a, b, tx), Mathf.Lerp(c, d, tx), ty);
        }

        static float Fbm(float u, float v, int fu, int fv, int seed, int octaves = 4)
        {
            float sum = 0, amp = 0.5f, norm = 0;
            for (int o = 0; o < octaves; o++) { sum += amp * Noise(u, v, fu << o, fv << o, seed + o * 31); norm += amp; amp *= 0.5f; }
            return sum / norm;
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

        // ------------------------------------------------------------------ textures

        static void Init()
        {
            if (steel != null) return;
            bumped = Find("KSP/Bumped Specular", "KSP/Specular");
            emissive = Find("KSP/Emissive/Bumped Specular", "KSP/Emissive/Specular", "KSP/Bumped Specular");
            var props = new List<string>();
            for (int i = 0; i < emissive.GetPropertyCount(); i++) props.Add(emissive.GetPropertyName(i) + ":" + emissive.GetPropertyType(i));
            ShaderReport = $"bumped={bumped.name} emissive={emissive.name} [{string.Join(", ", props)}]";

            // Regen tubes, TubesPerTile per U repeat. V is a heat coordinate (0 = throat, 1 = coolest), clamped:
            // the convergent runs 0.5 -> 0 into the throat, the bell 0 -> 1 down to the end of the regen section.
            tubeAlbedo = Tex(256, 128, false, (u, v) =>
            {
                float tu = u * TubesPerTile;
                int tube = Mathf.FloorToInt(tu);
                float x = 2 * (tu - tube) - 1;
                float h = Mathf.Sqrt(Mathf.Max(1 - x * x, 0));
                float streak = Noise(tube / (float)TubesPerTile, v, TubesPerTile, 10, 7);
                float heat = Mathf.Pow(1 - v, 1.6f) * 0.95f + (streak - 0.5f) * 0.18f + (Hash(tube, 0, 3) - 0.5f) * 0.1f;
                Color bare = new Color(0.7f, 0.66f, 0.6f) * (0.93f + 0.1f * Hash(tube, 1, 5));
                Color c = HeatTint(bare, heat);
                c *= 0.5f + 0.5f * h;                                       // dark grooves between tubes
                if (Mathf.Abs(x) > 0.86f) c = Color.Lerp(c, new Color(0.72f, 0.66f, 0.5f), 0.55f);   // braze fillet
                c.a = 1;
                return c;
            }, TextureWrapMode.Repeat, TextureWrapMode.Clamp);
            tubeNormal = Tex(256, 4, true, (u, v) =>
            {
                float tu = u * TubesPerTile;
                float x = 2 * (tu - Mathf.Floor(tu)) - 1;
                if (Mathf.Abs(x) > 0.86f) return Normal(0, 0);              // flat braze fillet
                float slope = x / Mathf.Sqrt(Mathf.Max(1 - x * x, 0.08f));   // convex tube: tilts toward +U on the +U side
                return Normal(slope * 0.55f, 0);
            });

            steelAlbedo = Tex(128, 128, false, (u, v) =>
            {
                float brush = 0.95f + 0.05f * Noise(u, v, 128, 4, 11);
                float mottle = 0.94f + 0.08f * Fbm(u, v, 4, 4, 13);
                float k = brush * mottle;
                return new Color(0.6f * k, 0.61f * k, 0.64f * k, 1);
            });

            // bolt heads on a flange rim: one bolt per U repeat, V spans the rim height. The tile is ~1.6x wider than high.
            const float aspect = 1.6f;
            boltAlbedo = Tex(64, 40, false, (u, v) =>
            {
                float x = (u - 0.5f) * aspect, y = v - 0.5f, d = Mathf.Sqrt(x * x + y * y);
                Color flange = new Color(0.5f, 0.51f, 0.53f) * (0.95f + 0.05f * Noise(u, v, 8, 4, 17));
                if (d < 0.27f) return new Color(0.7f, 0.7f, 0.72f, 1) * (0.9f + 0.1f * (1 - d / 0.27f));      // head
                if (d < 0.34f) return new Color(0.62f, 0.62f, 0.64f, 1);                                         // washer
                if (d < 0.4f) return flange * 0.7f;                                                              // shadow
                flange.a = 1;
                return flange;
            }, TextureWrapMode.Repeat, TextureWrapMode.Clamp);
            boltNormal = Tex(64, 40, true, (u, v) =>
            {
                float x = (u - 0.5f) * aspect, y = v - 0.5f, d = Mathf.Sqrt(x * x + y * y);
                if (d < 0.27f) return Normal(x / 0.27f * 0.9f, -y / 0.27f * 0.9f);
                if (d > 0.34f && d < 0.38f) return Normal(-x / d * 0.4f, y / d * 0.4f);
                return Normal(0, 0);
            }, TextureWrapMode.Repeat, TextureWrapMode.Clamp);

            // inside the nozzle: soot streaks with the flow; the colour comes from the per-propellant tint
            sootAlbedo = Tex(64, 64, false, (u, v) =>
            {
                float k = 0.75f + 0.35f * Fbm(u, v, 16, 2, 19, 3);
                return new Color(k, k, k, 1);
            });

            // C103 niobium with its silicide coating: matte grey with a faint purple cast and some mottling
            niobiumAlbedo = Tex(128, 128, false, (u, v) =>
            {
                float m = Fbm(u, v, 4, 4, 23);
                float streak = Noise(u, v, 32, 2, 29);
                float k = 0.86f + 0.14f * m + 0.05f * streak;
                return new Color(0.5f * k, 0.48f * k, 0.52f * k, 1);
            });
            niobiumNormal = Tex(128, 128, true, (u, v) =>
            {
                const float e = 1f / 128;
                float hx = Fbm(u + e, v, 16, 16, 31, 3) - Fbm(u - e, v, 16, 16, 31, 3);
                float hy = Fbm(u, v + e, 16, 16, 31, 3) - Fbm(u, v - e, 16, 16, 31, 3);
                return Normal(-hx * 3f, hy * 3f);
            });

            // carbon-carbon: 2/2 twill, 16 tows per tile (2.5 cm tows at the 0.4 m tile)
            const int tows = 16;
            carbonAlbedo = Tex(128, 128, false, (u, v) =>
            {
                int i = Mathf.FloorToInt(u * tows), j = Mathf.FloorToInt(v * tows);
                bool warp = ((i + j) & 3) < 2;
                float across = warp ? u * tows - i : v * tows - j;
                float fibre = 0.85f + 0.15f * Mathf.Sin(across * Mathf.PI);
                float k = (warp ? 0.17f : 0.13f) * fibre * (0.92f + 0.12f * Noise(u, v, 8, 8, 37));
                return new Color(k, k, k * 1.06f, 1);
            });
            carbonNormal = Tex(128, 128, true, (u, v) =>
            {
                int i = Mathf.FloorToInt(u * tows), j = Mathf.FloorToInt(v * tows);
                bool warp = ((i + j) & 3) < 2;
                float across = 2 * ((warp ? u * tows - i : v * tows - j)) - 1;
                return warp ? Normal(across * 0.5f, 0) : Normal(0, -across * 0.5f);
            });

            // multi-layer insulation foil on hydrogen lines: crinkled gold
            foilAlbedo = Tex(128, 128, false, (u, v) =>
            {
                float k = 0.75f + 0.4f * Fbm(u, v, 8, 8, 41);
                return new Color(0.86f * k, 0.66f * k, 0.3f * k, 1);
            });
            foilNormal = Tex(128, 128, true, (u, v) =>
            {
                const float e = 1f / 128;
                float hx = Fbm(u + e, v, 8, 8, 43) - Fbm(u - e, v, 8, 8, 43);
                float hy = Fbm(u, v + e, 8, 8, 43) - Fbm(u, v - e, 8, 8, 43);
                return Normal(-hx * 9f, hy * 9f);
            });

            // uncooled turbine exhaust duct: heat-tinted steel, mottled straw / bronze / blue
            heatTintAlbedo = Tex(128, 128, false, (u, v) =>
            {
                float heat = 0.25f + 0.75f * Fbm(u, v, 3, 3, 47);
                return HeatTint(new Color(0.6f, 0.6f, 0.62f), heat);
            });

            flatNormal = Tex(4, 4, true, (u, v) => Normal(0, 0));
            white = Tex(4, 4, false, (u, v) => Color.white);

            // emissive ramp along the radiative extension (V = 0 at the joint, 1 at the exit): the wall is hottest
            // where it meets the regen section and cools toward the exit, reddening as it goes.
            glowRamp = Tex(4, 64, false, (u, v) =>
            {
                float b = Mathf.Pow(1 - 0.92f * v, 2.6f);
                return new Color(b, b * (1 - 0.6f * v), b * (1 - 0.85f * v), 1);
            }, TextureWrapMode.Clamp, TextureWrapMode.Clamp);

            steel = Make(bumped, steelAlbedo, flatNormal, Color.white, 0.25f, new Color(0.45f, 0.45f, 0.45f));
            bolts = Make(bumped, boltAlbedo, boltNormal, Color.white, 0.3f, new Color(0.5f, 0.5f, 0.5f));
            machine = Make(bumped, steelAlbedo, flatNormal, new Color(0.42f, 0.43f, 0.46f), 0.3f, new Color(0.5f, 0.5f, 0.5f));
            lines = Make(bumped, steelAlbedo, flatNormal, new Color(0.78f, 0.77f, 0.74f), 0.4f, new Color(0.6f, 0.6f, 0.6f));
            foil = Make(bumped, foilAlbedo, foilNormal, Color.white, 0.12f, new Color(0.9f, 0.75f, 0.45f));
            hotDuct = Make(emissive, heatTintAlbedo, flatNormal, Color.white, 0.3f, new Color(0.45f, 0.45f, 0.45f));
            niobium = Make(emissive, niobiumAlbedo, niobiumNormal, Color.white, 0.15f, new Color(0.3f, 0.3f, 0.32f));
            carbon = Make(emissive, carbonAlbedo, carbonNormal, Color.white, 0.08f, new Color(0.15f, 0.15f, 0.15f));
            foreach (Material m in new[] { niobium, carbon })
            {
                if (m.HasProperty("_Emissive")) m.SetTexture("_Emissive", glowRamp);
                if (m.HasProperty("_EmissiveColor")) m.SetColor("_EmissiveColor", Color.black);
            }
            if (hotDuct.HasProperty("_Emissive")) hotDuct.SetTexture("_Emissive", white);
            if (hotDuct.HasProperty("_EmissiveColor")) hotDuct.SetColor("_EmissiveColor", Color.black);
            Debug.Log("[ProcEngines] materials: " + ShaderReport);
        }

        static Material Make(Shader sh, Texture2D albedo, Texture2D normal, Color tint, float shininess, Color spec)
        {
            var m = new Material(sh);
            m.SetTexture("_MainTex", albedo);
            if (m.HasProperty("_BumpMap")) m.SetTexture("_BumpMap", normal);
            m.SetColor("_Color", tint);
            if (m.HasProperty("_SpecColor")) m.SetColor("_SpecColor", spec);
            if (m.HasProperty("_Shininess")) m.SetFloat("_Shininess", shininess);
            return m;
        }

        /// <summary>Regen tube tint: kerolox engines run their tubes darker and more copper-coloured.</summary>
        static Material Regen(string propellant)
        {
            propellant = propellant ?? "";
            if (regen.TryGetValue(propellant, out Material m)) return m;
            Color tint = propellant == "Kerolox" ? new Color(1f, 0.88f, 0.76f) : propellant == "Methalox" ? new Color(1f, 0.95f, 0.9f) : Color.white;
            m = Make(bumped, tubeAlbedo, tubeNormal, tint, 0.2f, new Color(0.55f, 0.48f, 0.4f));
            regen[propellant] = m;
            return m;
        }

        /// <summary>Inside the nozzle: heavy soot for kerosene, light for methane, clean heat-tinted metal for hydrogen.</summary>
        static Material Soot(string propellant)
        {
            propellant = propellant ?? "";
            if (soot.TryGetValue(propellant, out Material m)) return m;
            Color tint = propellant == "Kerolox" ? new Color(0.07f, 0.065f, 0.06f) : propellant == "Methalox" ? new Color(0.2f, 0.18f, 0.16f) : new Color(0.42f, 0.32f, 0.26f);
            m = Make(bumped, sootAlbedo, flatNormal, tint, 0.5f, new Color(0.06f, 0.06f, 0.06f));
            soot[propellant] = m;
            return m;
        }

        /// <summary>Chamber, mount and convergent: EngineGeometry.MatSteel, MatRegenStatic, MatBolts.</summary>
        public static Material[] Static(string propellant) { Init(); return new[] { steel, Regen(propellant), bolts }; }

        /// <summary>Kit: KitBuilder.MatStructure, MatMachine, MatLines, MatHot, MatFoil, MatAuthored (the baked
        /// atlas of the Blender pieces; plain machine steel if it's missing).</summary>
        public static Material[] Kit()
        {
            Init();
            if (authored == null)
            {
                Texture2D a = KitAssets.Atlas();
                authored = a != null ? Make(bumped, a, flatNormal, Color.white, 0.3f, new Color(0.45f, 0.45f, 0.47f)) : machine;
            }
            return new[] { steel, machine, lines, hotDuct, foil, authored };
        }

        static Material authored;

        /// <summary>Bell: regen tubes, the extension (niobium, or carbon-carbon at the Late tech level), the inside, steel, bolts.</summary>
        public static Material[] Bell(string techLevel, string propellant)
        {
            Init();
            return new[] { Regen(propellant), techLevel == "Late" ? carbon : niobium, Soot(propellant), steel, bolts };
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
    }
}
