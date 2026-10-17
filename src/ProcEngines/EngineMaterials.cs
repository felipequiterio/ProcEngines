using UnityEngine;

namespace ProcEngines
{
    /// <summary>
    /// Procedural textures and shared materials (DESIGN.md 8.4). Everything is generated in code: no texture
    /// assets, nothing borrowed from other mods. Tube tiling is baked into the UVs, so one shared material serves
    /// every engine and identical engines batch. The heat glow is a per-material property block on the extension
    /// slot (KSP instantiates part materials and keeps its own renderer-wide block, so writing a material won't show).
    /// </summary>
    public static class EngineMaterials
    {
        static Material steel, copperTubes, soot, machine, lines, niobium, carbon;
        static Texture2D tubeNormal, tubeAlbedo, steelAlbedo, carbonAlbedo, carbonNormal, flatNormal, white;
        public static string ShaderReport = "";

        static Shader Find(params string[] names)
        {
            foreach (string n in names) { Shader s = Shader.Find(n); if (s != null) return s; }
            return Shader.Find("Standard");
        }

        static Texture2D Tex(int w, int h, bool linear, System.Func<float, float, Color> f, TextureWrapMode wrap = TextureWrapMode.Repeat)
        {
            var t = new Texture2D(w, h, TextureFormat.RGBA32, true, linear) { wrapMode = wrap, filterMode = FilterMode.Trilinear, anisoLevel = 4 };
            var px = new Color[w * h];
            for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) px[y * w + x] = f((x + 0.5f) / w, (y + 0.5f) / h);
            t.SetPixels(px);
            t.Apply(true, true);
            return t;
        }

        /// <summary>Unity's UnpackNormal reads x from alpha and y from green (DXT5nm layout); fill both layouts.</summary>
        static Color Normal(float nx, float ny)
        {
            float x = nx * 0.5f + 0.5f, y = ny * 0.5f + 0.5f;
            return new Color(x, y, 1, x);
        }

        static void Init()
        {
            if (steel != null) return;
            Shader bumped = Find("KSP/Bumped Specular", "KSP/Specular");
            Shader emissive = Find("KSP/Emissive/Bumped Specular", "KSP/Emissive/Specular", "KSP/Bumped Specular");
            var props = new System.Collections.Generic.List<string>();
            for (int i = 0; i < emissive.GetPropertyCount(); i++) props.Add(emissive.GetPropertyName(i) + ":" + emissive.GetPropertyType(i));
            ShaderReport = $"bumped={bumped.name} emissive={emissive.name} [{string.Join(", ", props)}]";

            // one cooling tube per U repeat: a round tube profile across U, constant along V (tubes run with the flow)
            tubeNormal = Tex(64, 4, true, (u, v) =>
            {
                float x = 2 * u - 1;                         // -1..1 across the tube
                float slope = -x / Mathf.Sqrt(Mathf.Max(1 - x * x, 0.04f));
                Vector2 n = new Vector2(slope * 0.6f, 0);
                float z = 1;
                float len = Mathf.Sqrt(n.x * n.x + z * z);
                return Normal(n.x / len, 0);
            });
            tubeAlbedo = Tex(64, 4, false, (u, v) =>
            {
                float x = 2 * u - 1;
                float h = Mathf.Sqrt(Mathf.Max(1 - x * x, 0));
                float k = 0.55f + 0.45f * h;                 // darker in the grooves between tubes
                return new Color(0.78f * k, 0.5f * k, 0.32f * k, 1);
            });
            steelAlbedo = Tex(64, 64, false, (u, v) =>
            {
                float streak = 0.93f + 0.07f * Mathf.Sin(u * 97f + Mathf.Sin(v * 13f) * 2f);
                return new Color(0.6f * streak, 0.61f * streak, 0.64f * streak, 1);
            });
            // carbon-carbon: a 2x2 twill weave
            carbonAlbedo = Tex(64, 64, false, (u, v) =>
            {
                bool warp = ((int)(u * 8) + (int)(v * 8)) % 2 == 0;
                float k = warp ? 0.16f : 0.11f;
                return new Color(k, k, k * 1.05f, 1);
            });
            carbonNormal = Tex(64, 64, true, (u, v) =>
            {
                bool warp = ((int)(u * 8) + (int)(v * 8)) % 2 == 0;
                float f = Mathf.Sin((warp ? u : v) * 8 * Mathf.PI * 2) * 0.25f;
                return warp ? Normal(f, 0) : Normal(0, f);
            });
            flatNormal = Tex(4, 4, true, (u, v) => Normal(0, 0));
            white = Tex(4, 4, false, (u, v) => Color.white);

            Material Make(Shader sh, Texture2D albedo, Texture2D normal, Color tint, float shininess, Color spec)
            {
                var m = new Material(sh);
                m.SetTexture("_MainTex", albedo);
                if (m.HasProperty("_BumpMap")) m.SetTexture("_BumpMap", normal);
                m.SetColor("_Color", tint);
                if (m.HasProperty("_SpecColor")) m.SetColor("_SpecColor", spec);
                if (m.HasProperty("_Shininess")) m.SetFloat("_Shininess", shininess);
                return m;
            }
            steel = Make(bumped, steelAlbedo, flatNormal, Color.white, 0.25f, new Color(0.45f, 0.45f, 0.45f));
            copperTubes = Make(bumped, tubeAlbedo, tubeNormal, Color.white, 0.18f, new Color(0.6f, 0.45f, 0.35f));
            soot = Make(bumped, white, flatNormal, new Color(0.06f, 0.055f, 0.05f), 0.6f, new Color(0.05f, 0.05f, 0.05f));
            machine = Make(bumped, steelAlbedo, flatNormal, new Color(0.42f, 0.43f, 0.46f), 0.3f, new Color(0.5f, 0.5f, 0.5f));
            lines = Make(bumped, steelAlbedo, flatNormal, new Color(0.75f, 0.74f, 0.7f), 0.35f, new Color(0.6f, 0.6f, 0.6f));
            niobium = Make(emissive, steelAlbedo, flatNormal, new Color(0.7f, 0.71f, 0.74f), 0.4f, new Color(0.7f, 0.7f, 0.7f));
            carbon = Make(emissive, carbonAlbedo, carbonNormal, Color.white, 0.08f, new Color(0.15f, 0.15f, 0.15f));
            foreach (Material m in new[] { niobium, carbon })
            {
                if (m.HasProperty("_Emissive")) m.SetTexture("_Emissive", white);
                if (m.HasProperty("_EmissiveColor")) m.SetColor("_EmissiveColor", Color.black);
            }
            Debug.Log("[ProcEngines] materials: " + ShaderReport);
        }

        public static Material[] Static() { Init(); return new[] { steel, copperTubes }; }
        public static Material[] Kit() { Init(); return new[] { steel, machine, lines }; }

        /// <summary>Bell materials: regen tubes, the extension material (niobium, or carbon-carbon at the Late tech level), soot inside.</summary>
        public static Material[] Bell(string techLevel)
        {
            Init();
            return new[] { copperTubes, techLevel == "Late" ? carbon : niobium, soot };
        }

        /// <summary>Approximate blackbody colour (K), scaled to KSP emissive brightness.</summary>
        public static Color Glow(float tempK, bool carbonCarbon)
        {
            if (tempK < 800) return Color.black;
            float t = Mathf.Clamp01((tempK - 800) / 1600f);   // 800 K dull red .. 2400 K yellow-white
            Color c = Color.Lerp(new Color(0.45f, 0.04f, 0), new Color(1f, 0.55f, 0.18f), Mathf.Clamp01(t * 1.6f));
            c = Color.Lerp(c, new Color(1f, 0.9f, 0.7f), Mathf.Clamp01(t * 2 - 1));
            float brightness = Mathf.Pow(t, 1.5f) * (carbonCarbon ? 0.8f : 1.2f);
            return c * brightness;
        }
    }
}
