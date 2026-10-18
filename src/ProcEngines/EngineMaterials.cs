using System;
using System.Collections.Generic;
using System.IO;
using ProcEngines.Model;
using UnityEngine;

namespace ProcEngines
{
    /// <summary>
    /// Shared materials and their textures (DESIGN.md 8.4, ASSETS.md 5). Every surface loads from
    /// GameData/ProcEngines/PluginData/Surfaces (`name.png` = sRGB albedo + gloss in alpha -> DXT5,
    /// `name_n.png` = OpenGL normal map -> linear, swizzled, DXT5); a missing file falls back, per file, to a texture
    /// generated here (regen_k falls back to the regen textures first). Every surface texture is loaded during LOADING
    /// (<see cref="Warm"/>, after the first prefab build), so none stalls a scene later; materials are created on
    /// first use (carbon-carbon only when a Late-tech bell asks for it, the jacket only for a channel-wall engine).
    /// Tiling is baked into the UVs, so one shared material serves every engine and
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
        public const int TubesPerTile = 32;        // cooling tubes (jacket: lands) per U repeat of regen / regen_k / jacket
        public const float SteelTile = 0.6f;       // metres per texture repeat on steel surfaces
        public const int CarbonTowsPerTile = 64;   // carbon-carbon twill tows per tile (12.5 mm at the 0.8 m tile)

        /// <summary>The ASSETS.md 5 surfaces. regen_k (kerolox tube wall: regen plus exterior soot toward the exit)
        /// falls back to regen when its files are missing; jacket (channel wall) has its own generated fallback.</summary>
        public static readonly string[] Surfaces = { "regen", "regen_k", "jacket", "steel", "bolts", "soot", "niobium", "carbon", "foil", "hotduct", "lines" };

        /// <summary>Positive mip bias on the tube-wall textures: with ~20 mm tubes the 64-texel tube pitch reaches 2-4
        /// screen pixels at mid range, where the groove / crown pattern sits at the sampling limit and shimmers
        /// (moire, worst near the silhouettes where anisotropic sampling runs out). Half a mip level blends in the
        /// next coarser level there; up close the texture is magnified and the bias changes nothing.</summary>
        public const float TubeMipBias = 0.5f;

        public static string ShaderReport = "";

        /// <summary>One entry per surface texture created so far: file or generated, size, format.</summary>
        public static string TextureReport => string.Join("; ", report.ToArray());

        static Shader bumped, emissive;
        static Material steel, bolts, machine, lines, structure, foil, niobium, carbon, authored;
        static readonly Dictionary<string, Material> walls = new Dictionary<string, Material>(), soot = new Dictionary<string, Material>(),
            bellSteel = new Dictionary<string, Material>(), hotDuct = new Dictionary<string, Material>(),
            variants = new Dictionary<string, Material>();   // finish options, by material name
        static readonly Dictionary<string, Texture2D> textures = new Dictionary<string, Texture2D>();
        static readonly List<string> report = new List<string>();
        static Texture2D flatNormal, white, glowRamp;

        // ------------------------------------------------------------------ public API

        /// <summary>Chamber, mount and convergent: EngineGeometry.MatSteel, MatRegenStatic, MatBolts. The wall slot is
        /// the tube wall (`regen`; never the sooted kerolox variant: the chamber's heat coordinate is 1 at the
        /// injector, where no plume soot reaches) or, with `channel`, the brazed channel-wall jacket. `finish` picks
        /// the chamber wall's and the mount's material (null: all Auto).</summary>
        public static Material[] Static(string propellant, bool channel = false, EngineFinish finish = null)
        {
            EnsureShaders();
            return new[] { SteelPart("steel", Opt(finish, "mount"), Steel), Wall(WallSurface(false, propellant, channel), propellant, Opt(finish, "chamber")), Bolts() };
        }

        /// <summary>The surface of the cooled wall: the channel-wall `jacket`, else the tube wall, which on a kerolox
        /// bell that ends in its own exit lip is the sooted `regen_k` and everywhere else (the chamber, and a bell with
        /// a radiative extension, whose regen section ends at the joint flange, where `regen_k`'s dense band at V = 1
        /// would sit) the plain `regen`.</summary>
        public static string WallSurface(bool bell, string propellant, bool channel, bool extension = false) =>
            channel ? "jacket" : bell && !extension && propellant == "Kerolox" ? "regen_k" : "regen";

        /// <summary>Bell: the regen section (tubes, sooted toward the exit for kerolox without an extension; or the
        /// channel-wall jacket), the extension (niobium, or carbon-carbon at the Late tech level), the inside, the
        /// propellant-tinted steel of the hat bands / manifold / joint flange, bolts. `extension`: the built bell
        /// has a radiative extension (EngineGeometry.HasExtension). `finish` overrides the nozzle wall, the
        /// extension, the inside and the bands.</summary>
        public static Material[] Bell(string techLevel, string propellant, bool channel = false, bool extension = false, EngineFinish finish = null)
        {
            EnsureShaders();
            string hardware = Opt(finish, "hardware");
            return new[]
            {
                Wall(WallSurface(true, propellant, channel, extension), propellant, Opt(finish, "nozzle")),
                Extension(CarbonExtension(techLevel, finish), Opt(finish, "extension")),
                Soot(InteriorPropellant(propellant, Opt(finish, "interior"))),
                hardware == EngineFinish.Auto ? BellSteel(propellant) : SteelPart("steel", hardware, Steel),
                Bolts(),
            };
        }

        /// <summary>Kit: KitBuilder.MatStructure, MatMachine, MatLines, MatHot, MatFoil, MatAuthored (the baked
        /// atlas of the Blender pieces with its normal map; plain machine steel if the atlas is missing). The hot
        /// duct is tinted per propellant (sooty for kerosene). `finish` overrides the frame and the lines (the plain
        /// and the foil-wrapped slot alike); the machinery and the hot duct keep their look.</summary>
        public static Material[] Kit(string propellant = null, EngineFinish finish = null)
        {
            EnsureShaders();
            string lines = Opt(finish, "lines");
            return new[] { SteelPart("lines", Opt(finish, "frame"), Structure), Machine(), LineMaterial(lines, false), HotDuct(propellant), LineMaterial(lines, true), Authored() };
        }

        /// <summary>Whether the extension shows carbon-carbon, whose glow runs hotter than niobium's: chosen in the
        /// finish, or Auto at the Late tech level.</summary>
        public static bool CarbonExtension(string techLevel, EngineFinish finish)
        {
            string o = Opt(finish, "extension");
            return o == "Carbon" || (o == EngineFinish.Auto && techLevel == "Late");
        }

        static string Opt(EngineFinish finish, string slot) => finish == null ? EngineFinish.Auto : finish.Get(slot);

        static readonly Color[] glowStops = { new Color(0.45f, 0.02f, 0f), new Color(0.95f, 0.2f, 0.02f), new Color(1f, 0.4f, 0.08f), new Color(1f, 0.58f, 0.22f) };

        /// <summary>Approximate blackbody colour of hot metal (K) for _EmissiveColor: dull red at 750 K to orange-yellow
        /// at ~2000 K. No channel exceeds 1 and alpha is 1 (the shader multiplies by it), so the glow added over
        /// sunlit metal doesn't wash out to white.</summary>
        public static Color Glow(float tempK)
        {
            if (tempK < 750) return Color.black;
            float t = (tempK - 750) / 1450f;   // 750 K .. 2200 K
            Color c = Ramp(t, glowStops);
            c *= Mathf.Pow(Mathf.Clamp01(t * 1.25f), 1.6f);
            c.a = 1f;
            return c;
        }

        // ------------------------------------------------------------------ materials (created on first use)
        // Gloss maps scale the specular per texel (mostly 0.2-0.7). In-game (gamma colour space) the tint multiplies
        // the sRGB albedo directly. Metals take a dim, slightly tinted highlight; the tube walls keep a tight lobe so
        // the highlight follows each tube instead of spreading over several.

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

        /// <summary>Propellant lines: worn, dull stainless (not polished chrome).</summary>
        static Material Lines() { if (lines == null) lines = MakeSurface("lines", bumped, "lines", new Color(0.9f, 0.9f, 0.91f), 0.45f, new Color(0.52f, 0.52f, 0.53f)); return lines; }

        /// <summary>Kit structure (struts and their end balls, tube flanges, bolt heads, collars, bands): the `lines`
        /// texture, which is authored for the kit's per-circumference mapping (ASSETS.md 5; the 0.6 m steel tile
        /// would be squeezed around every strut), tinted a little darker and cooler than the lines, with a duller,
        /// broader highlight, so structure and plumbing read as different steel.</summary>
        static Material Structure() { if (structure == null) structure = MakeSurface("structure", bumped, "lines", new Color(0.8f, 0.81f, 0.84f), 0.32f, new Color(0.44f, 0.44f, 0.46f)); return structure; }

        /// <summary>Gold MLI on hydrogen lines: large planar facets with pinpoint glints (tight lobe).</summary>
        static Material Foil() { if (foil == null) foil = MakeSurface("foil", bumped, "foil", Color.white, 0.6f, new Color(1f, 0.86f, 0.56f)); return foil; }

        /// <summary>Uncooled turbine exhaust / hot-gas duct: heat-tinted Inconel, darkened by soot for kerosene.</summary>
        static Material HotDuct(string propellant)
        {
            propellant = propellant ?? "";
            if (hotDuct.TryGetValue(propellant, out Material m) && m != null) return m;   // Unity null check
            Color tint = propellant == "Kerolox" ? new Color(0.62f, 0.58f, 0.55f) : propellant == "Methalox" ? new Color(0.85f, 0.83f, 0.81f) : Color.white;
            m = MakeSurface("hotduct-" + PropName(propellant), emissive, "hotduct", tint, 0.26f, new Color(0.4f, 0.39f, 0.38f));
            Glowing(m, White());
            hotDuct[propellant] = m;
            return m;
        }

        /// <summary>Silicide-coated niobium: dark charcoal with a slight sheen.</summary>
        static Material Niobium()
        {
            if (niobium != null) return niobium;
            niobium = MakeSurface("niobium", emissive, "niobium", Color.white, 0.25f, new Color(0.35f, 0.35f, 0.37f));
            Glowing(niobium, GlowRamp());
            return niobium;
        }

        /// <summary>Carbon-carbon: matte charcoal; the weave lives mostly in the normal map.</summary>
        static Material Carbon()
        {
            if (carbon != null) return carbon;
            carbon = MakeSurface("carbon", emissive, "carbon", Color.white, 0.2f, new Color(0.3f, 0.3f, 0.31f));
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

        static string PropName(string propellant) => string.IsNullOrEmpty(propellant) ? "default" : propellant;

        /// <summary>
        /// The cooled wall: `regen` / `regen_k` (brazed tubes) or `jacket` (smooth channel wall), tinted per propellant.
        /// Kerosene runs the wall to dark oxidised bronze, methane to a warm grey, hydrogen leaves it near neutral.
        /// The specular is dim and only half tinted (a fired wall has no polished copper left), with a tight lobe on
        /// the tubes so the highlight runs along each crown; the smooth jacket takes a dimmer, broader sheen.
        /// </summary>
        static Material Wall(string surface, string propellant, string option = EngineFinish.Auto)
        {
            if (option != EngineFinish.Auto) return WallVariant(surface, option);
            propellant = propellant ?? "";
            string key = surface + "-" + PropName(propellant);
            if (walls.TryGetValue(key, out Material m) && m != null) return m;
            bool jacket = surface == "jacket";
            Color tint = jacket
                ? (propellant == "Kerolox" ? new Color(0.68f, 0.6f, 0.53f) : propellant == "Methalox" ? new Color(0.84f, 0.81f, 0.78f) : new Color(0.92f, 0.92f, 0.93f))
                : (propellant == "Kerolox" ? new Color(0.78f, 0.66f, 0.55f) : propellant == "Methalox" ? new Color(0.86f, 0.83f, 0.8f) : new Color(0.92f, 0.92f, 0.92f));
            Color spec = Color.Lerp(Color.white, tint, 0.5f) * (jacket ? 0.4f : 0.48f);
            spec.a = 1;
            m = MakeSurface(key, bumped, surface, tint, jacket ? 0.35f : 0.45f, spec);
            walls[key] = m;
            return m;
        }

        /// <summary>Hat bands, the coolant manifold and the joint flange: steel that shares the bell's firing history
        /// (bronzed and grimy for kerosene), duller than the clean chamber steel.</summary>
        static Material BellSteel(string propellant)
        {
            propellant = propellant ?? "";
            if (bellSteel.TryGetValue(propellant, out Material m) && m != null) return m;
            Color tint = propellant == "Kerolox" ? new Color(0.72f, 0.62f, 0.54f) : propellant == "Methalox" ? new Color(0.78f, 0.76f, 0.74f) : new Color(0.85f, 0.85f, 0.86f);
            m = MakeSurface("bellsteel-" + PropName(propellant), bumped, "steel", tint, 0.3f, new Color(0.45f, 0.45f, 0.46f));
            bellSteel[propellant] = m;
            return m;
        }

        /// <summary>Inside the nozzle: heavy soot for kerosene, light for methane, clean heat-tinted metal for hydrogen.</summary>
        static Material Soot(string propellant)
        {
            propellant = propellant ?? "";
            if (soot.TryGetValue(propellant, out Material m) && m != null) return m;
            Color tint = propellant == "Kerolox" ? new Color(0.07f, 0.065f, 0.06f) : propellant == "Methalox" ? new Color(0.2f, 0.18f, 0.16f) : new Color(0.42f, 0.32f, 0.26f);
            m = MakeSurface("soot-" + PropName(propellant), bumped, "soot", tint, 0.3f, new Color(0.16f, 0.16f, 0.16f));
            soot[propellant] = m;
            return m;
        }

        // ------------------------------------------------------------------ finish options (EngineFinish)
        // Each option re-tints a surface the engine already uses, so it costs no texture memory. The textures are
        // near-neutral grey with their wear baked in; an option names the albedo it should average to, and the tint
        // is that over the texture's own mean (gamma space: the shader multiplies the sRGB albedo by the tint, which
        // may exceed 1 to lighten a dark texture). Metals get a highlight tinted like the metal; paint and
        // insulation a dim, broad one.

        /// <summary>Mean albedo of each surface texture as shipped (build_surfaces.py output; regen_k as its clean
        /// regen part). Close enough for a generated fallback.</summary>
        static float SurfaceMean(string surface)
        {
            switch (surface)
            {
                case "regen": case "regen_k": return 0.515f;
                case "jacket": return 0.432f;
                default: return 0.5f;   // steel, lines
            }
        }

        struct Look
        {
            public Color Albedo, Spec;
            public float Shininess;
            public Look(Color albedo, Color spec, float shininess) { Albedo = albedo; Spec = spec; Shininess = shininess; }
        }

        static Color C(float r, float g, float b) => new Color(r, g, b, 1);

        /// <summary>The colour times k, alpha left at 1 (Color * float would scale it too).</summary>
        static Color Lighten(Color c, float k) => new Color(c.r * k, c.g * k, c.b * k, 1);

        /// <summary>The cooled-wall options (EngineFinish wall slots). Tube walls keep their tight lobe along each
        /// crown; the smooth jacket takes a broader one.</summary>
        static Look WallLook(string option, bool jacket)
        {
            float tight = jacket ? 0.35f : 0.45f;
            switch (option)
            {
                case "Copper": return new Look(C(0.64f, 0.4f, 0.29f), C(0.6f, 0.4f, 0.3f), tight);
                case "Bronze": return new Look(C(0.44f, 0.29f, 0.2f), C(0.34f, 0.24f, 0.18f), tight);
                case "Stainless": return new Look(C(0.5f, 0.5f, 0.52f), C(0.55f, 0.55f, 0.57f), tight);
                case "Inconel": return new Look(C(0.5f, 0.45f, 0.36f), C(0.44f, 0.4f, 0.33f), tight);
                case "Nickel": return new Look(C(0.42f, 0.42f, 0.4f), C(0.34f, 0.34f, 0.32f), tight * 0.8f);
                case "Black": return new Look(C(0.075f, 0.072f, 0.07f), C(0.2f, 0.2f, 0.2f), 0.25f);
                default: return new Look(C(0.5f, 0.5f, 0.5f), C(0.4f, 0.4f, 0.4f), tight);
            }
        }

        /// <summary>Steel parts (EngineFinish steel slots): bare, darkened, bronzed, or painted.</summary>
        static Look SteelLook(string option)
        {
            switch (option)
            {
                case "Steel": return new Look(C(0.55f, 0.55f, 0.57f), C(0.6f, 0.6f, 0.62f), 0.3f);
                case "DarkSteel": return new Look(C(0.3f, 0.3f, 0.31f), C(0.42f, 0.42f, 0.43f), 0.3f);
                case "Bronzed": return new Look(C(0.5f, 0.42f, 0.34f), C(0.38f, 0.33f, 0.28f), 0.3f);
                case "Primer": return new Look(C(0.5f, 0.51f, 0.52f), C(0.14f, 0.14f, 0.14f), 0.15f);
                case "Black": return new Look(C(0.07f, 0.07f, 0.075f), C(0.16f, 0.16f, 0.16f), 0.2f);
                case "White": return new Look(C(0.82f, 0.82f, 0.8f), C(0.2f, 0.2f, 0.2f), 0.2f);
                default: return new Look(C(0.5f, 0.5f, 0.5f), C(0.4f, 0.4f, 0.4f), 0.3f);
            }
        }

        static Material Variant(string key, Func<Material> make)
        {
            if (variants.TryGetValue(key, out Material m) && m != null) return m;   // Unity null check
            m = make();
            variants[key] = m;
            return m;
        }

        static Material Tinted(string key, Shader sh, string surface, Look look) =>
            Variant(key, () => MakeSurface(key, sh, surface, Lighten(look.Albedo, 1f / SurfaceMean(surface)), look.Shininess, look.Spec));

        static Material WallVariant(string surface, string option) =>
            Tinted("wall-" + surface + "-" + option, bumped, surface, WallLook(option, surface == "jacket"));

        /// <summary>A steel-slot option on `surface` (steel for the mount and bands, the kit's lines texture for the
        /// frame); Auto keeps the slot's own material.</summary>
        static Material SteelPart(string surface, string option, Func<Material> auto) =>
            option == EngineFinish.Auto ? auto() : Tinted("paint-" + surface + "-" + option, bumped, surface, SteelLook(option));

        /// <summary>The radiative extension: coated niobium, bare niobium or carbon-carbon, all with the glow ramp.
        /// Bare niobium is the brushed steel texture tinted lavender grey (the coated niobium texture, lightened that
        /// far, turns its coating chips into blotches).</summary>
        static Material Extension(bool carbon, string option)
        {
            if (carbon) return Carbon();
            if (option != "BareNiobium") return Niobium();
            return Variant("niobium-bare", () =>
            {
                var look = new Look(C(0.54f, 0.53f, 0.57f), C(0.5f, 0.5f, 0.55f), 0.35f);
                Material m = MakeSurface("niobium-bare", emissive, "steel", Lighten(look.Albedo, 1f / SurfaceMean("steel")), look.Shininess, look.Spec);
                Glowing(m, GlowRamp());
                return m;
            });
        }

        /// <summary>The inside of the nozzle: the soot level of a propellant (Clean = hydrogen's heat-tinted metal,
        /// Light = methane's film, Heavy = kerosene's).</summary>
        static string InteriorPropellant(string propellant, string option)
        {
            switch (option)
            {
                case "Clean": return "Hydrolox";
                case "Light": return "Methalox";
                case "Heavy": return "Kerolox";
                default: return propellant;
            }
        }

        /// <summary>Lines (KitBuilder.MatLines) and foil-wrapped lines (MatFoil): Auto keeps bare stainless and gold
        /// foil on the hydrogen lines; the other options give every line the same skin.</summary>
        static Material LineMaterial(string option, bool foilSlot)
        {
            switch (option)
            {
                case "Stainless": return Lines();
                case "Gold": return Foil();
                case "Silver":
                    return Variant("foil-silver", () =>
                    {
                        // the gold foil texture's mean is (0.746, 0.63, 0.391): per-channel gains turn it silver
                        var tint = new Color(0.7f / 0.746f, 0.7f / 0.63f, 0.72f / 0.391f, 1);
                        return MakeSurface("foil-silver", bumped, "foil", tint, 0.6f, C(0.8f, 0.8f, 0.82f));
                    });
                case "White":
                    return Tinted("insulation-White", bumped, "lines", new Look(C(0.84f, 0.84f, 0.82f), C(0.12f, 0.12f, 0.12f), 0.12f));
                case "Black":
                    return Tinted("insulation-Black", bumped, "lines", new Look(C(0.06f, 0.06f, 0.062f), C(0.12f, 0.12f, 0.12f), 0.12f));
                default: return foilSlot ? Foil() : Lines();
            }
        }

        static bool warmed;

        /// <summary>LOADING, after the first prefab build: load every surface texture now (decode, mipmaps, DXT5:
        /// ~125 ms per 2048x1024 file), so a design that first needs one (the channel-wall jacket, carbon-carbon) doesn't
        /// stall the editor or a vessel load. The default design already loaded most of them; about 8 MiB of VRAM for
        /// the rest. Materials stay created on first use (cheap once their textures exist).</summary>
        internal static void Warm()
        {
            if (warmed) return;
            warmed = true;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            int before = textures.Count;
            foreach (string s in Surfaces) { Surface(s, false); Surface(s, true); }
            Debug.Log($"[ProcEngines] surfaces warmed: {textures.Count - before} more textures in {sw.Elapsed.TotalMilliseconds:F0} ms");
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

        /// <summary>Surfaces whose V is clamped: the tube walls' V is the heat coordinate, the bolts tile spans the rim.</summary>
        public static bool ClampsV(string surface) => surface == "regen" || surface == "regen_k" || surface == "jacket" || surface == "bolts";

        /// <summary>The surface whose textures stand in when `surface`'s own file is missing (regen_k -> regen); null
        /// when the code-generated fallback is used instead.</summary>
        public static string FallbackSurface(string surface) => surface == "regen_k" ? "regen" : null;

        /// <summary>Mip bias for a surface's textures (see <see cref="TubeMipBias"/>). The jacket's lands are too faint
        /// to alias, so it keeps full sharpness.</summary>
        public static float MipBias(string surface) => surface == "regen" || surface == "regen_k" ? TubeMipBias : 0f;

        /// <summary>The file that would provide a surface's albedo (normal = false) or normal map.</summary>
        public static string SurfacePath(string surface, bool normal) => Path.Combine(KitAssets.SurfaceDirectory, (normal ? surface + "_n" : surface) + ".png");

        /// <summary>Where <see cref="Surface"/> takes a texture from, judged by which files exist (for tools and tests;
        /// a file that exists but fails to decode also falls through in game): the path of the surface's own file
        /// or of its fallback surface's (regen_k -> regen), or null for the code-generated texture of
        /// `GeneratedSurface(surface)`.</summary>
        public static string TextureSource(string surface, bool normal)
        {
            for (string s = surface; s != null; s = FallbackSurface(s))
            {
                string p = SurfacePath(s, normal);
                if (File.Exists(p)) return p;
            }
            return null;
        }

        /// <summary>The surface whose generated pixels stand in when no file exists along the fallback chain.</summary>
        public static string GeneratedSurface(string surface)
        {
            string s = surface;
            while (FallbackSurface(s) != null) s = FallbackSurface(s);
            return s;
        }

        /// <summary>A surface's albedo (normal = false) or normal map: the file if it loads, else the fallback
        /// surface's texture (regen_k -> regen), else generated. Each file falls back on its own.</summary>
        static Texture2D Surface(string name, bool normal)
        {
            string key = normal ? name + "_n" : name;
            if (textures.TryGetValue(key, out Texture2D t) && t != null) return t;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            TextureWrapMode wv = ClampsV(name) ? TextureWrapMode.Clamp : TextureWrapMode.Repeat;
            float bias = MipBias(name);
            t = KitAssets.LoadTexture(SurfacePath(name, normal), normal, TextureWrapMode.Repeat, wv, normal ? -1 : DefaultGloss(name), bias);
            string source = "file";
            string other = t == null ? FallbackSurface(name) : null;
            if (other != null)
            {
                t = Surface(other, normal);
                source = "-> " + other;
            }
            else if (t == null)
            {
                Color[] px = FallbackPixels(name, normal, out int w, out int h);
                t = Upload("ProcEngines/" + key + " (generated)", px, w, h, normal, TextureWrapMode.Repeat, wv, true, bias);
                source = "generated";
            }
            report.Add($"{key} {source} {t.width}x{t.height} {t.format}{(t.mipMapBias != 0 ? $" bias {t.mipMapBias:F2}" : "")} {sw.Elapsed.TotalMilliseconds:F0} ms");
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
                case "jacket": return 0.38f;
                default: return 0.45f;
            }
        }

        static Texture2D Upload(string name, Color[] px, int w, int h, bool linear, TextureWrapMode wrapU, TextureWrapMode wrapV, bool compress, float mipBias = 0)
        {
            var t = new Texture2D(w, h, TextureFormat.RGBA32, true, linear) { name = name, wrapModeU = wrapU, wrapModeV = wrapV, filterMode = FilterMode.Trilinear, anisoLevel = 4, mipMapBias = mipBias };
            t.SetPixels(px);
            t.Apply(true, false);
            if (compress && (w & 3) == 0 && (h & 3) == 0) t.Compress(true);
            t.Apply(false, true);
            t.mipMapBias = mipBias;
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

        /// <summary>Emissive ramp along the radiative extension (V = 0 at the joint, 1 at the exit; U = the extension's
        /// 0.8 m tiles around, wrapping): the wall is hottest where it meets the regen section and falls off
        /// exponentially toward the exit (0.32 at v = 0.3, 0.15 at v = 0.5), reddening as it goes, with faint axial hot
        /// streaks (8 per tile, wandering a little). Uncompressed (smooth).</summary>
        static Texture2D GlowRamp()
        {
            if (glowRamp == null)
                glowRamp = Upload("ProcEngines/glow_ramp", Fill(64, 64, GlowRampTexel), 64, 64, false, TextureWrapMode.Repeat, TextureWrapMode.Clamp, false);
            return glowRamp;
        }

        /// <summary>One texel of the emissive ramp (public for tools; touches no engine state).</summary>
        public static Color GlowRampTexel(float u, float v)
        {
            float b = Mathf.Exp(-3.8f * v);
            float phase = 8 * u + 0.6f * (Noise(u, v, 16, 3, 71) - 0.5f);   // periodic in u: the streaks tile around
            float streak = 1 + 0.12f * Mathf.Sin(2 * Mathf.PI * phase) * (0.75f + 0.5f * Noise(u, 0.5f, 8, 1, 73));
            b *= streak;
            return new Color(Mathf.Clamp01(b), Mathf.Clamp01(b * (1 - 0.7f * v)), Mathf.Clamp01(b * (1 - 0.92f * v)), 1);
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
                case "regen_k":   // in game regen_k uses the regen textures when its own files are missing
                    // 32 texels per tube (the files carry 64)
                    if (normal) { w = 32 * TubesPerTile; h = 128; return RegenNormal(w, h); }
                    w = 32 * TubesPerTile; h = 256; return Fill(w, h, RegenAlbedo);
                case "jacket":
                    if (normal) { w = 16 * TubesPerTile; h = 128; return JacketNormal(w, h); }
                    w = 16 * TubesPerTile; h = 256; return Fill(w, h, JacketAlbedo);
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
                    w = h = 8 * CarbonTowsPerTile;   // 8 texels per tow
                    return Fill(w, h, normal ? (Func<float, float, Color>)CarbonNormal : CarbonAlbedo);
                case "foil":
                    w = 256; h = 256;
                    return normal ? FromHeight(w, h, FoilHeight, 10f, true) : Fill(w, h, FoilAlbedo);
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

        /// <summary>Piecewise-linear ramp through `stops` (no allocation: pass a cached array).</summary>
        static Color Ramp(float t, Color[] stops)
        {
            t = Mathf.Clamp01(t) * (stops.Length - 1);
            int i = Mathf.Min(Mathf.FloorToInt(t), stops.Length - 2);
            return Color.Lerp(stops[i], stops[i + 1], t - i);
        }

        // heat tint stops after the bare metal: straw, bronze, purple, blue, grey
        static readonly Color[] heatStops = { Color.clear, new Color(0.74f, 0.63f, 0.42f), new Color(0.56f, 0.4f, 0.26f),
            new Color(0.42f, 0.3f, 0.36f), new Color(0.33f, 0.36f, 0.48f), new Color(0.36f, 0.36f, 0.38f) };

        /// <summary>Heat tint of stainless/Inconel by how hot it ran: bare, straw, bronze, purple, blue, grey.</summary>
        static Color HeatTint(Color bare, float heat)
        {
            float t = Mathf.Clamp01(heat) * (heatStops.Length - 1);
            if (t < 1) return Color.Lerp(bare, heatStops[1], t);
            return Ramp(heat, heatStops);
        }

        static Color WithGloss(Color c, float gloss) { c.a = Mathf.Clamp01(gloss); return c; }

        // ---- regen tubes: TubesPerTile per U repeat; V is the heat coordinate (0 = throat, 1 = coolest), clamped

        static Color RegenAlbedo(float u, float v)
        {
            float tu = u * TubesPerTile;
            int tube = Mathf.FloorToInt(tu);
            float x = 2 * (tu - tube) - 1;
            float crown = Mathf.Sqrt(Mathf.Max(1 - x * x, 0));
            float streak = Noise(tube / (float)TubesPerTile, v, TubesPerTile, 10, 7);
            // a graded, streaky heat band near the throat, irregular per tube
            float heat = Mathf.Pow(1 - v, 2.6f) * 0.95f + (streak - 0.5f) * 0.22f + (Hash(tube, 0, 3) - 0.5f) * 0.1f;
            Color bare = new Color(0.66f, 0.62f, 0.56f) * (0.95f + 0.07f * Hash(tube, 1, 5));
            Color c = HeatTint(bare, heat);
            float grime = Fbm(u, v, 2 * TubesPerTile, 4, 9, 3);              // streaks running along the tubes
            float dent = Smooth(0.72f, 0.8f, Fbm(u, v, TubesPerTile, 8, 13, 3));   // small dents and handling marks
            c *= (0.88f + 0.16f * grime) * (1 - 0.1f * dent);
            c *= 0.7f + 0.3f * crown;                                         // grooves between tubes (soft AO)
            bool fillet = Mathf.Abs(x) > 0.86f;
            if (fillet) c = Color.Lerp(c, new Color(0.56f, 0.5f, 0.4f), 0.45f) * (0.85f + 0.15f * grime);   // braze fillet
            float gloss = fillet ? 0.18f : 0.2f + 0.4f * crown * crown;
            gloss *= (0.75f + 0.4f * grime) * (1 - 0.3f * Mathf.Clamp01(heat)) * (1 - 0.3f * dent);
            return WithGloss(c, gloss);
        }

        static Color[] RegenNormal(int w, int h)
        {
            // dents: a small height field; the tube profile itself is analytic (convex: tilts toward +U on the +U side)
            var px = FromHeight(w, h, (u, v) => -Smooth(0.72f, 0.8f, Fbm(u, v, TubesPerTile, 8, 13, 3)) * 0.5f, 1.5f, false);
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

        // ---- jacket: channel-wall jacket (staged combustion, full flow, Late gas generator). Same UV contract as
        // regen: TubesPerTile milled lands per U repeat, V = heat coordinate (clamped). A smooth brazed sheet, neutral
        // grey (the propellant tint multiplies it), faint pillowing over the lands, one seam weld per tile, a
        // desaturated heat band near the throat at about half the tubes' strength, grime streaks toward the exit.

        const float JacketSeamU = 12.5f / TubesPerTile;   // the seam runs along the middle of one land per tile

        static float JacketSeam(float u) => Smooth(0.006f, 0.0015f, Mathf.Abs(u - JacketSeamU));

        static Color JacketAlbedo(float u, float v)
        {
            float mottle = Fbm(u, v, 4, 4, 81);
            float streak = Noise(u, v, 3 * TubesPerTile, 3, 82);
            float heat = 0.5f * (Mathf.Pow(1 - v, 3.5f) * 0.95f + (Noise(u, v, TubesPerTile, 8, 83) - 0.5f) * 0.16f);
            Color hot = HeatTint(new Color(0.6f, 0.6f, 0.6f), heat) * (0.44f / 0.6f);   // heat colours at the sheet's brightness
            float lum = (hot.r + hot.g + hot.b) / 3;
            Color c = Color.Lerp(hot, new Color(lum, lum, lum), 0.45f) * (0.92f + 0.16f * mottle);   // desaturated heat
            float tu = u * TubesPerTile, x = 2 * (tu - Mathf.Floor(tu)) - 1;
            c *= 0.975f + 0.03f * (1 - x * x);                                                       // faint pillowing
            float grime = streak * Smooth(0.3f, 1f, v) * Smooth(0.35f, 0.7f, Fbm(u, v, 6, 2, 84, 2));   // toward the exit
            c *= 1 - 0.3f * grime;
            float seam = JacketSeam(u);
            c = Color.Lerp(c, new Color(0.38f, 0.33f, 0.27f), 0.65f * seam);                          // weld bead
            float gloss = 0.4f + 0.08f * (mottle - 0.5f) - 0.12f * grime - 0.1f * Mathf.Clamp01(2 * heat) - 0.12f * seam;
            return WithGloss(c, gloss);
        }

        static Color[] JacketNormal(int w, int h)
        {
            // small dents (height field), then the analytic land pillowing (about a tenth of a tube crown's slope) and
            // the raised seam bead
            var px = FromHeight(w, h, (u, v) => -0.5f * Smooth(0.74f, 0.8f, Fbm(u, v, TubesPerTile / 2, 8, 85, 3)), 1f, false);
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    float u = (x + 0.5f) / w, tu = u * TubesPerTile, xt = 2 * (tu - Mathf.Floor(tu)) - 1;
                    Color c = px[y * w + x];
                    float nz = Mathf.Sqrt(Mathf.Max(1 - Sq(c.a * 2 - 1) - Sq(c.g * 2 - 1), 1e-4f));
                    float nx = (c.a * 2 - 1) / nz + 0.06f * xt, ny = (c.g * 2 - 1) / nz;
                    float d = u - JacketSeamU;
                    if (Mathf.Abs(d) < 0.006f) nx += 0.45f * d / 0.006f;   // rounded bead: tilts away from its centre
                    px[y * w + x] = Normal(nx, ny);
                }
            return px;
        }

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

        // ---- niobium: C103 with silicide coating; dark charcoal with a violet cast, mottled, flow streaks along V,
        // coating chips

        static float NiobiumChips(float u, float v) => Smooth(0.76f, 0.8f, Fbm(u, v, 12, 12, 27, 3)) * Smooth(0.4f, 0.6f, Noise(u, v, 4, 4, 28));

        static float NiobiumHeight(float u, float v) => 0.5f * Fbm(u, v, 16, 16, 31, 3) - 0.7f * NiobiumChips(u, v);

        static Color NiobiumAlbedo(float u, float v)
        {
            float m = Fbm(u, v, 4, 4, 23);
            float streak = Noise(u, v, 32, 2, 29);
            float k = 0.84f + 0.16f * m + 0.06f * streak;
            Color c = new Color(0.26f * k, 0.255f * k, 0.275f * k);
            float chip = NiobiumChips(u, v);
            c = Color.Lerp(c, new Color(0.58f, 0.57f, 0.56f), chip);   // bare metal where the coating chipped
            return WithGloss(c, 0.2f + 0.06f * (m - 0.5f) + 0.3f * chip);
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
            float big = Fbm(u, v, 3, 3, 38, 3);   // large-scale mottling and coating haze
            float k = 0.16f * (warp ? 1f : 0.88f) * (0.9f + 0.1f * fibre) * (0.94f + 0.08f * lines) * (0.88f + 0.24f * big) * (1 - 0.2f * resin);
            return WithGloss(new Color(k, k, k * 1.05f), (warp ? 0.34f : 0.28f) * (0.85f + 0.15f * fibre * lines) + 0.12f * resin);
        }

        static Color CarbonNormal(float u, float v)
        {
            const int tows = CarbonTowsPerTile;
            int i = Mathf.FloorToInt(u * tows), j = Mathf.FloorToInt(v * tows);
            bool warp = ((i + j) & 3) < 2;
            float across = 2 * (warp ? u * tows - i : v * tows - j) - 1;   // -1..1 across the tow, convex
            return warp ? Normal(across * 0.8f, 0) : Normal(0, across * 0.8f);
        }

        // ---- foil: gold MLI on hydrogen lines; crinkle, tape wraps across U (bands of constant V), a seam along V

        static float FoilTape(float v)
        {
            float f = v * 3 - Mathf.Floor(v * 3);   // three wraps per tile
            return Smooth(0.0f, 0.015f, f) * Smooth(0.13f, 0.115f, f);
        }

        static float FoilHeight(float u, float v)
        {
            float crinkle = 1 - Mathf.Abs(2 * Fbm(u, v, 5, 5, 43, 3) - 1);   // ridged: sharp creases between large facets
            float seam = Smooth(0.012f, 0f, Mathf.Abs(u - 0.5f));            // overlap edge running along V
            return 0.6f * crinkle * (1 - 0.7f * FoilTape(v)) + 0.4f * FoilTape(v) + 0.25f * seam;
        }

        static Color FoilAlbedo(float u, float v)
        {
            float crinkle = 1 - Mathf.Abs(2 * Fbm(u, v, 5, 5, 43, 3) - 1);
            float k = 0.8f + 0.24f * crinkle;
            float tape = FoilTape(v);
            Color c = Color.Lerp(new Color(0.8f * k, 0.7f * k, 0.46f * k), new Color(0.7f, 0.68f, 0.62f) * (0.9f + 0.1f * crinkle), tape);   // pale gold; tape: duller, silvery
            float grime = Smooth(0.6f, 0.85f, Fbm(u, v, 4, 4, 45, 3));
            c *= 1 - 0.18f * grime;
            return WithGloss(c, Mathf.Lerp(0.55f + 0.35f * crinkle, 0.35f, tape) - 0.2f * grime);
        }

        // ---- hot duct: uncooled turbine exhaust duct, dark heat-tinted Inconel (muted straw / bronze / blue), soot

        static float HotDuctHeight(float u, float v) => 0.5f * Fbm(u, v, 6, 6, 51, 4);

        static Color HotDuctAlbedo(float u, float v)
        {
            float heat = 0.2f + 0.8f * Fbm(u, v, 3, 3, 47);
            Color c = HeatTint(new Color(0.46f, 0.46f, 0.47f), heat);
            float lum = (c.r + c.g + c.b) / 3;
            c = Color.Lerp(c, new Color(lum, lum, lum), 0.35f) * 0.85f;   // muted, darker heat colours
            float sootMask = Smooth(0.4f, 0.75f, Fbm(u, v, 4, 2, 49, 3)) * (0.6f + 0.4f * Noise(u, v, 32, 2, 50));
            c = Color.Lerp(c, new Color(0.1f, 0.09f, 0.08f), 0.75f * sootMask);
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
            return WithGloss(c, 0.45f + 0.08f * (brush - 0.5f) - 0.15f * smudge - 0.25f * grime + 0.2f * scratch);
        }
    }
}
