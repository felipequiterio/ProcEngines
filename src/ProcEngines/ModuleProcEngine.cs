using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ProcEngines.Model;
using PropellantData = ProcEngines.Model.Propellant;
using UnityEngine;

namespace ProcEngines
{
    /// <summary>
    /// The procedural engine (DESIGN.md 3). Listed BEFORE ModuleEnginesFX and ModuleGimbal in the part cfg so its
    /// OnLoad/OnStart create the transforms before they cache them. The design (inputs) and stats (outputs) live in
    /// this module's node: in the editor stats are recomputed from DESIGN, in flight the stored STATS are applied as-is.
    ///
    /// Render meshes come from <see cref="EngineMeshCache"/>: parts with the same DESIGN + STATS share them. Each part
    /// still builds its own EngineGeometry for its nodes, colliders (per part), transforms and glow.
    /// - Prefab (LOADING): the distant level only, with the LOD0 renderers pointing at the same meshes.
    /// - Editor: both levels at OnStart. A change stream (a drag or typing, changes under 0.5 s apart) rebuilds only
    ///   LOD1 at up to 10 Hz and forces it; LOD0 follows 0.5 s after the last change. A one-off change (a button, one
    ///   handle step) rebuilds both levels together once nothing follows it for 0.15 s, without forcing LOD1.
    /// - Flight: LOD1 at OnStart (both renderer sets show it), LOD0 from a queue, about one engine build per frame
    ///   (cache hits are instant); <see cref="Lod0Pending"/> is true until then.
    /// Each level has four renderers: the chamber, the bell (under the gimbal), the static kit, and the kit fixed to
    /// the bell (a child of the bell renderer, so it gimbals with it). Mods that read MeshFilter.mesh replace a shared
    /// mesh with a per-filter copy; the part puts its own back (see Show / RestoreMeshes).
    /// </summary>
    public class ModuleProcEngine : PartModule, IPartMassModifier, IPartCostModifier
    {
        public const string RootName = "peRoot", GimbalName = "peGimbal", ThrustName = "thrustTransform", ExhaustName = "peGGExhaust";
        // renderers: LOD0 keeps the original names (tests look them up), LOD1 is the distant level (ASSETS.md 6)
        public const string ChamberName = "peChamber", BellName = "peBell", KitName = "peKit",
            ChamberLodName = "peChamberLod1", BellLodName = "peBellLod1", KitLodName = "peKitLod1";
        /// <summary>The kit fixed to the gimballed bell (KitBuilder's bell mesh): a child of peGimbal/peBell and of
        /// peGimbal/peBellLod1, in the bell's frame (ASSETS.md 6).</summary>
        public const string KitBellName = "peKitBell", KitBellLodName = "peKitBellLod1";
        /// <summary>LODGroup screen-relative heights (raw LOD.screenRelativeTransitionHeight values: KSP's
        /// QualitySettings.lodBias = 2 halves them in effect, to 0.125 and 0.015): LOD0 above, LOD1 above, LOD2 (kit
        /// culled) down to 0.</summary>
        public const float Lod0Height = 0.25f, Lod1Height = 0.03f;

        /// <summary>Editor: changes closer together than this are a stream (LOD1 only, forced, until it ends).</summary>
        const float StreamGap = 0.5f;
        /// <summary>Editor: a one-off change rebuilds both levels once nothing has followed it for this long.</summary>
        const float SettleTime = 0.15f;
        /// <summary>Seconds an unreferenced mesh set is kept after its part goes away (undo, LMP vessel reloads).</summary>
        const float PartGrace = 5f;
        /// <summary>Flight: how often the render filters are checked for meshes another mod swapped in.</summary>
        const float MeshCheckInterval = 1f;

        // Prefab defaults (part cfg); the live design is held in `design`.
        [KSPField] public string propellant = "Kerolox";
        [KSPField] public string cycle = "GasGenerator";
        [KSPField] public float thrustVac = 200f;
        [KSPField] public float pc = 60f;
        [KSPField] public float eps = 20f;
        [KSPField] public float gimbalRange = 5f;

        [KSPField(isPersistant = true)] public string designName = "New engine";

        [KSPField(guiActive = true, guiActiveEditor = true, guiName = "Engine")] public string summary = "";
        [KSPField(guiActive = false, guiActiveEditor = true, guiName = "Status")] public string status = "";

        public EngineDesign Design { get; private set; }
        public EngineStats Stats { get; private set; }
        public TechCaps Caps { get; private set; }
        public List<string> Violations { get; private set; } = new List<string>();
        public bool StatsStale { get; private set; }
        public string WaterfallStatus = "";
        /// <summary>Flight: the design exceeds the researched tech (launched past the editor's pre-flight check); the engine won't run.</summary>
        public bool TechLocked { get; private set; }
        public List<string> FlightViolations { get; private set; } = new List<string>();
        /// <summary>Design-driven mesh rebuilds (at most 10 Hz while dragging; a one-off change counts once); the LOD0
        /// refinement 0.5 s after a stream is counted in SlowRebuilds only.</summary>
        public int MeshRebuilds, SlowRebuilds;
        public double LastBuildMs;
        /// <summary>Flight: the full-detail level is still queued; until it is built both renderer sets show LOD1.</summary>
        public bool Lod0Pending { get; private set; }
        public event Action DesignApplied;

        Transform root, gimbalT, thrustT, exhaustT;
        MeshFilter staticMF, bellMF, kitMF, kitBellMF, staticLodMF, bellLodMF, kitLodMF, kitBellLodMF;
        MeshRenderer bellR, bellLodR, kitR, kitLodR, kitBellR, kitBellLodR;
        // The eight render filters by slot (LOD0 chamber, bell, kit, bell kit, then the same at LOD1: slot + Lod1Slot)
        // and the mesh this part last gave each one. Not serialized, so a clone starts with none assigned.
        const int SlotChamber = 0, SlotBell = 1, SlotKit = 2, SlotKitBell = 3, Lod1Slot = 4, Slots = 8;
        readonly MeshFilter[] filters = new MeshFilter[Slots];
        readonly Mesh[] shown = new Mesh[Slots];
        // copies other mods made of our meshes (MeshFilter.mesh) that a conformal decal may still project onto
        readonly List<Mesh> keptCopies = new List<Mesh>();
        float nextMeshCheck;
        bool vesselEvents;
        static int copiesReplaced;
        LODGroup lodGroup;
        string radiativeTech, materialKey;
        float glow;
        bool glowApplied;
        MaterialPropertyBlock glowBlock;
        ModuleEnginesFX engineFX;
        static readonly int EmissiveColorId = Shader.PropertyToID("_EmissiveColor"), EmissiveSTId = Shader.PropertyToID("_Emissive_ST");
        public KitBuilder.Result Kit { get; private set; }
        public float GlowTemperature { get; private set; }
        public Color GlowColor { get; private set; }
        public Color DuctGlowColor { get; private set; }
        readonly List<MeshCollider> colliders = new List<MeshCollider>();
        EngineGeometry geometry;
        string geometryKey;   // EngineGeometry.MeshKey that `geometry` was built for
        // the shared render meshes this part holds a reference on (never destroyed or rebuilt by the part)
        EngineMeshCache.Entry lod0Entry, lod1Entry;
        bool designDirty, meshDirty, slowDirty, lod0Current, ownMeshes, nodesPending, shipModifiedPending;
        readonly List<Mesh> ownedMeshes = new List<Mesh>();   // colliders (and any mesh made outside LOADING): destroyed with the part
        float lastChange, prevChange, lastMesh, lastCapsCheck;

        ModelData Data => KspData.Data;

        // ------------------------------------------------------------------ persistence

        public override void OnLoad(ConfigNode node)
        {
            EngineDesign defaults = PrefabDesign();
            ConfigNode d = node.GetNode("DESIGN");
            Design = d != null ? Serialization.LoadDesign(KspData.ToCfg(d), defaults) : (Design ?? defaults);
            ConfigNode s = node.GetNode("STATS");
            if (s != null)
            {
                CfgNode sc = KspData.ToCfg(s);
                Stats = Serialization.LoadStats(sc);
                StatsStale = Serialization.StatsModelVersion(sc) != EngineStats.ModelVersion;
            }

            if (HighLogic.LoadedScene == GameScenes.LOADING)
            {
                // Prefab: strip the placeholder model and build the default design, so the parts list, the R&D
                // screen and the prefab drag cube show a real engine. Only the distant level: the prefab is seen as
                // icons and tooltips, and it would otherwise hold a full-detail engine for the whole session.
                StripPlaceholder();
                EnsureTransforms();
                Stats = EngineModel.Compute(Design, Data);
                BuildPrefab();
                RebuildColliders();
                SetNodes(false);
                // the surfaces the default design doesn't use (the channel-wall jacket, carbon-carbon) load now, not
                // as a main-thread stall the first time an engine in the editor or in flight needs them
                EngineMaterials.Warm();
            }
        }

        public override void OnSave(ConfigNode node)
        {
            node.AddValue("schemaVersion", Serialization.SchemaVersion);
            if (Design != null) node.AddNode(KspData.ToConfigNode(Serialization.SaveDesign(Design)));
            if (Stats != null) node.AddNode(KspData.ToConfigNode(Serialization.SaveStats(Stats)));
        }

        /// <summary>Editor copies and symmetry clones: the design isn't a Unity-serialized field, so copy it here.</summary>
        public override void OnCopy(PartModule fromModule)
        {
            if (fromModule is ModuleProcEngine src && src.Design != null)
            {
                Design = Serialization.LoadDesign(Serialization.SaveDesign(src.Design));
                Stats = src.Stats;
                designName = src.designName;
            }
        }

        EngineDesign PrefabDesign() => new EngineDesign
        {
            Propellant = propellant, Cycle = cycle, ThrustVac = thrustVac, Pc = pc, Eps = eps, GimbalRange = gimbalRange, Tech = "Mid",
        };

        public override string GetInfo()
        {
            if (Stats != null) ApplyEngine();   // prefab: make the stock engine info match the default design
            return Stats == null ? "" : string.Format(CultureInfo.InvariantCulture,
                "Procedural engine: design it in the editor.\nDefault: {0} {1}, {2:F0} kN, Isp {3:F0}/{4:F0} s",
                Design.Propellant, Design.Cycle, Stats.ThrustVac, Stats.IspVac, Stats.IspSL);
        }

        // ------------------------------------------------------------------ lifecycle

        public override void OnStart(StartState state)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            if (Design == null) Design = PrefabDesign();
            EnsureTransforms();
            if (HighLogic.LoadedSceneIsEditor)
            {
                Caps = KspData.CurrentCaps();
                Recompute();
            }
            else if (Stats == null)
            {
                Debug.LogWarning($"[ProcEngines] {part.partInfo.name}: no STATS in flight, recomputing from DESIGN");
                Stats = EngineModel.Compute(Design, Data);
            }
            if (HighLogic.LoadedSceneIsFlight) StartFlightMeshes();
            else lod0Current = RebuildMesh(true);
            RebuildColliders();
            // Editor: stats were recomputed (possibly at another tech level than when the craft was saved), so the
            // nodes may have moved: push the attached parts on the first Update, once the craft is fully linked.
            if (HighLogic.LoadedSceneIsEditor) nodesPending = true;
            else SetNodes(false);
            ApplyEngine();
            UpdateReadouts();
            LastBuildMs = sw.Elapsed.TotalMilliseconds;
            if (HighLogic.LoadedSceneIsFlight)
            {
                // spread the engines' mesh checks over the interval
                nextMeshCheck = Time.realtimeSinceStartup + MeshCheckInterval * (1 + (GetInstanceID() & 15) / 16f);
                if (!vesselEvents)
                {
                    vesselEvents = true;
                    GameEvents.onVesselWasModified.Add(OnVesselChanged);
                    GameEvents.onVesselGoOffRails.Add(OnVesselChanged);
                }
                StartCoroutine(FlightPostStart());
            }
        }

        void Update()
        {
            EngineMeshCache.Sweep();   // also driven by HarmonyLoader (LaunchGuard.cs), for when no engine is loaded
            if (HighLogic.LoadedSceneIsFlight)
            {
                float t = Time.realtimeSinceStartup;
                if (t >= nextMeshCheck) { nextMeshCheck = t + MeshCheckInterval; RestoreMeshes(); }
                PumpLod0Queue();
                if (engineFX == null) engineFX = part.FindModuleImplementing<ModuleEnginesFX>();
                ModuleEnginesFX e = engineFX;
                if (TechLocked && e != null && (e.EngineIgnited || e.getIgnitionState)) e.Shutdown();
                UpdateGlow(e);
                return;
            }
            if (!HighLogic.LoadedSceneIsEditor || Design == null) return;
            float now = Time.realtimeSinceStartup;
            if (nodesPending) { nodesPending = false; SetNodes(true); }
            // Tech can change while the editor is open (LMP co-op research, R&D retry): re-evaluate every 2 s. A caps
            // change is a design change like any other (debounced, one rebuild).
            if (now - lastCapsCheck > (Caps == null ? 1f : 2f))
            {
                lastCapsCheck = now;
                TechCaps c = KspData.CurrentCaps();
                if (c != null && CapsKey(c) != CapsKey(Caps)) { Caps = c; designDirty = true; MarkChange(now); }
            }
            if (designDirty)
            {
                designDirty = false;
                Recompute();
                ApplyEngine();
                UpdateReadouts();
                meshDirty = slowDirty = true;
                lod0Current = false;
            }
            if (meshDirty)
            {
                if (lastChange - prevChange < StreamGap)
                {
                    // a change stream (drag, slider, typing): the distant level only, at most 10 Hz, forced
                    if (now - lastMesh >= 0.1f)
                    {
                        meshDirty = false;
                        lastMesh = now;
                        RebuildMesh(false);
                    }
                }
                else if (now - lastChange >= SettleTime)
                {
                    // a one-off change that nothing followed: both levels at once, no forced LOD1; the attached parts
                    // follow right away (the slow path below repeats this, a no-op by then)
                    meshDirty = false;
                    lastMesh = now;
                    lod0Current = RebuildMesh(true);
                    RebuildColliders();
                    SetNodes(true);
                }
            }
            if (slowDirty && now - lastChange >= StreamGap)   // the change (or stream) ended 0.5 s ago: LOD0, nodes, aero, undo
            {
                slowDirty = false;
                if (meshDirty) { meshDirty = false; lod0Current = RebuildMesh(true); }
                else if (!lod0Current && BuildMeshes(false, true))   // LOD1 is current (the stream built it): LOD0 only
                {
                    lod0Current = true;
                    if (lodGroup != null) { lodGroup.ForceLOD(-1); lodGroup.RecalculateBounds(); }
                }
                RebuildColliders();
                SetNodes(true);
                Integrations.NotifyAero(part);
                SlowRebuilds++;
                shipModifiedPending = true;   // fired once per frame for the whole symmetry group (LateUpdate)
                DesignApplied?.Invoke();
            }
        }

        /// <summary>Editor: one ship-modified event and one undo snapshot per frame, after every symmetry counterpart has
        /// applied its slow path (they all run it in the same frame).</summary>
        void LateUpdate()
        {
            if (!shipModifiedPending) return;
            shipModifiedPending = false;
            if (shipModifiedFrame == Time.frameCount || !HighLogic.LoadedSceneIsEditor || EditorLogic.fetch == null) return;
            shipModifiedFrame = Time.frameCount;
            GameEvents.onEditorShipModified.Fire(EditorLogic.fetch.ship);
            EditorLogic.fetch.SetBackup();
        }

        static int shipModifiedFrame = -1;

        static string CapsKey(TechCaps c) => c == null ? "" : string.Join("|", c.Unlimited, c.TechLevel, c.MaxPc, c.MaxEps, c.MaxThrust, c.RaoBell, c.RadiativeExtension,
            string.Join(",", c.Cycles.OrderBy(x => x)), string.Join(",", c.Propellants.OrderBy(x => x)));

        void MarkChange(float now)
        {
            prevChange = lastChange;
            lastChange = now;
        }

        /// <summary>Editor: replace the design (clamped to the current caps), push it to symmetry counterparts.</summary>
        public void SetDesign(EngineDesign d, bool pushSymmetry = true)
        {
            Design = Clamp(d);
            designDirty = true;
            MarkChange(Time.realtimeSinceStartup);
            if (!pushSymmetry) return;
            foreach (Part p in part.symmetryCounterparts)
                p.FindModuleImplementing<ModuleProcEngine>()?.SetDesign(d, false);
        }

        EngineDesign Clamp(EngineDesign d)
        {
            ModelData data = Data;
            var c = Serialization.LoadDesign(Serialization.SaveDesign(d));   // copy
            if (!data.Propellants.ContainsKey(c.Propellant)) c.Propellant = "Kerolox";
            if (!data.Cycles.ContainsKey(c.Cycle)) c.Cycle = "GasGenerator";
            PropellantData p = data.Propellants[c.Propellant];
            c.Pc = Math.Max(p.PcMin, Math.Min(c.Pc, p.PcMax));
            c.Eps = Math.Max(1.5, Math.Min(c.Eps, p.EpsMax));
            c.ThrustVac = Math.Max(0.5, Math.Min(c.ThrustVac, 30000));
            c.LengthFraction = Math.Max(0.6, Math.Min(c.LengthFraction, 1.0));
            c.GimbalRange = Math.Max(0, Math.Min(c.GimbalRange, 10));
            if (!double.IsNaN(c.Of)) c.Of = Math.Max(p.OfMin, Math.Min(c.Of, p.OfMax));
            return c;
        }

        void Recompute()
        {
            if (Caps != null) Design.Tech = Caps.TechLevel;
            Stats = EngineModel.Compute(Design, Data);
            StatsStale = false;
            Violations = Caps?.Violations(Design) ?? new List<string> { "tech not evaluated yet" };
        }

        IEnumerator FlightPostStart()
        {
            while (vessel == null || vessel.packed || !FlightGlobals.ready) yield return null;
            yield return null;
            // Designs over the researched tech can reach flight through paths that skip the editor's pre-flight check
            // (KSC launch dialog, LMP, KCT): lock the engine. Tech only grows, so a legally launched design stays legal.
            TechCaps caps = null;
            for (float t0 = Time.realtimeSinceStartup; (caps = KspData.CurrentCaps()) == null && Time.realtimeSinceStartup - t0 < 10f;) yield return null;
            if (caps != null && !caps.Unlimited)
            {
                FlightViolations = caps.Violations(Design);
                if (FlightViolations.Count > 0)
                {
                    TechLocked = true;
                    var e = part.Modules.OfType<ModuleEnginesFX>().FirstOrDefault();
                    if (e != null) { e.Shutdown(); e.Events["Activate"].guiActive = false; }
                    summary = "<color=#ff6060>LOCKED: design exceeds researched tech</color>";
                    ScreenMessages.PostScreenMessage($"{designName}: engine locked, the design exceeds researched technology ({string.Join("; ", FlightViolations)})", 8f, ScreenMessageStyle.UPPER_CENTER);
                    Debug.Log($"[ProcEngines] {designName} locked in flight: {string.Join("; ", FlightViolations)}");
                }
            }
            Integrations.NotifyAero(part);
            var specs = new List<Integrations.PlumeSpec>();
            Plume plume = Data.PlumeFor(Design.Propellant, Plume.RoleFor(Stats));
            if (plume != null)
                specs.Add(new Integrations.PlumeSpec { Template = plume.Template, ParentTransform = ThrustName, Scale = (float)(Stats.ExitDiameter / plume.ReferenceDiameter), LengthScale = (float)plume.LengthScale });
            Plume exhaust = Data.Plumes.FirstOrDefault(p => p.Propellant == Design.Propellant && p.Role == "exhaust");
            if (exhaust != null && Kit != null && Kit.HasExhaust)
                specs.Add(new Integrations.PlumeSpec { Template = exhaust.Template, ParentTransform = ExhaustName, Scale = 2 * Kit.ExhaustRadius / (float)exhaust.ReferenceDiameter, LengthScale = (float)exhaust.LengthScale });
            if (specs.Count > 0)
                yield return StartCoroutine(Integrations.ApplyWaterfall(this, specs, s => WaterfallStatus = s));
        }

        /// <summary>Heat glow (DESIGN.md 8.4): a first-order lag on throttle drives the wall temperature where the
        /// radiatively cooled extension meets the regen section (niobium ~1700 K, carbon-carbon ~2000 K at full
        /// thrust). The extension's emissive ramp fades and reddens it toward the exit; its _Emissive_ST is set per
        /// engine so the ramp spans the extension, and as the metal cools the glowing band recedes toward the joint.
        /// The uncooled GG exhaust duct glows dull red (~1100 K).</summary>
        void UpdateGlow(ModuleEnginesFX e)
        {
            if (e == null || bellR == null) return;
            float target = e.EngineIgnited ? e.currentThrottle : 0;
            if (target == 0 && glow == 0)
            {
                if (glowApplied) ClearGlow();
                return;
            }
            // heats in ~2 s; cools like a radiator: fast while white-hot (T^4), a slow dull-red tail (~5 s in all)
            float rate = target > glow ? 0.5f : 0.12f + 0.5f * glow * glow * glow;
            glow = Mathf.MoveTowards(glow, target, rate * TimeWarp.deltaTime);
            bool cc = radiativeTech == "Late";
            GlowTemperature = 300 + glow * (cc ? 1700 : 1400);
            GlowColor = EngineMaterials.Glow(GlowTemperature);
            DuctGlowColor = EngineMaterials.Glow(300 + glow * 800);
            if (geometry != null && geometry.HasExtension)
            {
                // compress the ramp toward the joint as it cools (the ramp texture clamps beyond 1, dark there)
                Vector4 st = geometry.ExtensionEmissiveST;
                st.y /= Mathf.Lerp(0.35f, 1f, glow);
                SetGlowBlock(bellR, EngineGeometry.MatRadiative, GlowColor, st);
                SetGlowBlock(bellLodR, EngineGeometry.MatRadiative, GlowColor, st);
            }
            if (Kit != null && Kit.HasExhaust)
            {
                SetGlowBlock(kitR, KitBuilder.MatHot, DuctGlowColor, new Vector4(1, 1, 0, 0));
                SetGlowBlock(kitLodR, KitBuilder.MatHot, DuctGlowColor, new Vector4(1, 1, 0, 0));
                SetGlowBlock(kitBellR, KitBuilder.MatHot, DuctGlowColor, new Vector4(1, 1, 0, 0));
                SetGlowBlock(kitBellLodR, KitBuilder.MatHot, DuctGlowColor, new Vector4(1, 1, 0, 0));
            }
            glowApplied = true;
        }

        /// <summary>Start from KSP's renderer-wide block (highlight rim, opacity) so the per-material block keeps them;
        /// refreshed every frame while glowing.</summary>
        void SetGlowBlock(MeshRenderer mr, int index, Color c, Vector4 emissiveST)
        {
            if (mr == null) return;
            if (glowBlock == null) glowBlock = new MaterialPropertyBlock();
            mr.GetPropertyBlock(glowBlock);
            glowBlock.SetColor(EmissiveColorId, c);
            glowBlock.SetVector(EmissiveSTId, emissiveST);
            mr.SetPropertyBlock(glowBlock, index);
        }

        /// <summary>Cooled down: drop the per-material blocks so the renderer-wide one (highlighting) applies again.</summary>
        void ClearGlow()
        {
            glowApplied = false;
            GlowColor = DuctGlowColor = Color.black;
            foreach (MeshRenderer mr in new[] { bellR, bellLodR })
                if (mr != null) mr.SetPropertyBlock(null, EngineGeometry.MatRadiative);
            foreach (MeshRenderer mr in new[] { kitR, kitLodR, kitBellR, kitBellLodR })
                if (mr != null) mr.SetPropertyBlock(null, KitBuilder.MatHot);
        }

        // ------------------------------------------------------------------ engine

        public void ApplyEngine()
        {
            var engine = part.Modules.OfType<ModuleEnginesFX>().FirstOrDefault();
            if (engine == null || Stats == null) return;
            EngineStats s = Stats;
            float g = engine.g;
            float maxThrust = (float)s.ThrustVac, minThrust = (float)(s.ThrustVac * s.ThrottleMin);
            var n = new ConfigNode("MODULE");
            n.AddValue("name", engine.moduleName);
            n.AddValue("persistentId", engine.PersistentId);   // PartModule.Load would otherwise reset it
            n.AddValue("maxThrust", F(maxThrust));
            n.AddValue("minThrust", F(minThrust));
            n.AddValue("heatProduction", "100");
            n.AddValue("useThrustCurve", "False");
            n.AddValue("EngineType", "LiquidFuel");
            if (Data.Resources.TryGetValue(Design.Propellant, out ResourceSet rs))
                foreach (var r in rs.Resources)
                {
                    ConfigNode pn = n.AddNode("PROPELLANT");
                    pn.AddValue("name", r.Key);
                    pn.AddValue("ratio", F((float)r.Value));
                    if (r.Key == rs.GaugeResource) pn.AddValue("DrawGauge", "True");
                }
            ConfigNode c = n.AddNode("atmosphereCurve");
            foreach (CurveKey k in s.AtmosphereCurve)
                c.AddValue("key", F((float)k.Pressure) + " " + F((float)k.Isp) + " " + F((float)k.Tangent) + " " + F((float)k.Tangent));
            engine.Load(n);
            // Always set the fuel flows explicitly, whatever Load did (SPIKE_RESULTS.md 2).
            float ispVac = (float)s.IspVac;
            engine.maxFuelFlow = maxThrust / (ispVac * g);
            engine.minFuelFlow = minThrust / (ispVac * g);
            engine.throttleMin = engine.minFuelFlow / engine.maxFuelFlow;
            var gimbal = part.Modules.OfType<ModuleGimbal>().FirstOrDefault();
            if (gimbal != null) gimbal.gimbalRange = (float)Design.GimbalRange;
        }

        static string F(float v) => v.ToString("R", CultureInfo.InvariantCulture);

        void UpdateReadouts()
        {
            if (Stats == null) return;
            summary = string.Format(CultureInfo.InvariantCulture, "{0} {1}: {2:F0} kN, Isp {3:F0}/{4:F0} s, {5:F2} t, Ø{6:F2} m",
                Design.Propellant, Design.Cycle, Stats.ThrustVac, Stats.IspVac, Stats.IspSL, Stats.Mass, Stats.ExitDiameter);
            status = Violations.Count > 0 ? "<color=#ff6060>" + string.Join("; ", Violations) + "</color>"
                   : Stats.Warnings.Count > 0 ? "<color=#ffcc40>" + string.Join("; ", Stats.Warnings) + "</color>" : "OK";
        }

        [KSPEvent(guiActiveEditor = true, guiName = "Engine designer")]
        public void OpenDesigner() => DesignerWindow.Toggle(this);

        public float GetModuleMass(float defaultMass, ModifierStagingSituation sit) => Stats == null ? 0 : (float)Stats.Mass - defaultMass;
        public ModifierChangeWhen GetModuleMassChangeWhen() => ModifierChangeWhen.FIXED;
        public float GetModuleCost(float defaultCost, ModifierStagingSituation sit) => Stats == null ? 0 : (float)Stats.Cost - defaultCost;
        public ModifierChangeWhen GetModuleCostChangeWhen() => ModifierChangeWhen.FIXED;

        // ------------------------------------------------------------------ transforms and renderers

        void StripPlaceholder()
        {
            Transform model = part.transform.Find("model");
            if (model == null) return;
            foreach (Transform t in model.Cast<Transform>().ToList())
                if (t.name != RootName) DestroyImmediate(t.gameObject);
        }

        void EnsureTransforms()
        {
            Transform model = part.transform.Find("model");
            root = model.Find(RootName);
            bool chamberChannel = EngineGeometry.ChannelWall(Design), bellChannel = EngineGeometry.ChannelNozzle(Design);
            if (root == null)
            {
                root = new GameObject(RootName).transform;
                root.SetParent(model, false);
                staticMF = MakeRenderer(root, ChamberName, EngineMaterials.Static(Design?.Propellant, chamberChannel));
                kitMF = MakeRenderer(root, KitName, EngineMaterials.Kit(Design?.Propellant));
                exhaustT = new GameObject(ExhaustName).transform;
                exhaustT.SetParent(root, false);
                // Gimbal pivot oriented like a stock gimbal transform (+Z down the exhaust); the bell counter-rotates.
                gimbalT = new GameObject(GimbalName).transform;
                gimbalT.SetParent(root, false);
                gimbalT.localRotation = Quaternion.Euler(90f, 0f, 0f);
                bellMF = MakeRenderer(gimbalT, BellName, EngineMaterials.Bell("Mid", Design?.Propellant, bellChannel));
                bellMF.transform.localRotation = Quaternion.Euler(-90f, 0f, 0f);
                thrustT = new GameObject(ThrustName).transform;
                thrustT.SetParent(gimbalT, false);
                for (int i = 0; i < 3; i++)
                {
                    var go = new GameObject("peCollider" + i);
                    go.transform.SetParent(root, false);
                    var mc = go.AddComponent<MeshCollider>();
                    mc.convex = true;
                    mc.sharedMesh = new Mesh { name = "peCollider" + i };
                }
            }
            else
            {
                staticMF = root.Find(ChamberName).GetComponent<MeshFilter>();
                kitMF = root.Find(KitName).GetComponent<MeshFilter>();
                exhaustT = root.Find(ExhaustName);
                gimbalT = root.Find(GimbalName);
                bellMF = gimbalT.Find(BellName).GetComponent<MeshFilter>();
                thrustT = gimbalT.Find(ThrustName);
            }
            // the distant level: made with the prefab, so clones (and KSP's cached renderer lists) already have it;
            // created here as well if an older hierarchy lacks it
            staticLodMF = FindRenderer(root, ChamberLodName) ?? MakeRenderer(root, ChamberLodName, EngineMaterials.Static(Design?.Propellant, chamberChannel));
            kitLodMF = FindRenderer(root, KitLodName) ?? MakeRenderer(root, KitLodName, EngineMaterials.Kit(Design?.Propellant));
            bellLodMF = FindRenderer(gimbalT, BellLodName);
            if (bellLodMF == null)
            {
                bellLodMF = MakeRenderer(gimbalT, BellLodName, EngineMaterials.Bell("Mid", Design?.Propellant, bellChannel));
                bellLodMF.transform.localRotation = Quaternion.Euler(-90f, 0f, 0f);
            }
            // the kit fixed to the bell (actuator lugs, the bellows-to-manifold runs): children of the two bell
            // renderers with an identity transform, so they gimbal with the bell and share its frame (KitBuilder.Build)
            kitBellMF = FindRenderer(bellMF.transform, KitBellName) ?? MakeRenderer(bellMF.transform, KitBellName, EngineMaterials.Kit(Design?.Propellant));
            kitBellLodMF = FindRenderer(bellLodMF.transform, KitBellLodName) ?? MakeRenderer(bellLodMF.transform, KitBellLodName, EngineMaterials.Kit(Design?.Propellant));
            bellR = bellMF.GetComponent<MeshRenderer>(); bellLodR = bellLodMF.GetComponent<MeshRenderer>();
            kitR = kitMF.GetComponent<MeshRenderer>(); kitLodR = kitLodMF.GetComponent<MeshRenderer>();
            kitBellR = kitBellMF.GetComponent<MeshRenderer>(); kitBellLodR = kitBellLodMF.GetComponent<MeshRenderer>();
            filters[SlotChamber] = staticMF; filters[SlotBell] = bellMF; filters[SlotKit] = kitMF; filters[SlotKitBell] = kitBellMF;
            filters[Lod1Slot + SlotChamber] = staticLodMF; filters[Lod1Slot + SlotBell] = bellLodMF;
            filters[Lod1Slot + SlotKit] = kitLodMF; filters[Lod1Slot + SlotKitBell] = kitBellLodMF;
            // Instantiate() copies serialized state only: a clone (prefab -> part, or an editor copy) starts with
            // ownMeshes = false and still shares its source's collider meshes, so give it its own. Its renderers keep
            // showing the source's (prefab or cached) meshes until the first build assigns this part's cache entries;
            // nothing ever writes into those.
            bool fresh = HighLogic.LoadedScene != GameScenes.LOADING && !ownMeshes;
            colliders.Clear();
            for (int i = 0; i < 3; i++)
            {
                var mc = root.Find("peCollider" + i).GetComponent<MeshCollider>();
                if (fresh) mc.sharedMesh = Own("peCollider" + i);
                colliders.Add(mc);
            }
            if (fresh) ownMeshes = true;
            if (part.collider == null) part.collider = colliders[0];
            SetupLods();
        }

        static MeshFilter FindRenderer(Transform parent, string name)
        {
            Transform t = parent.Find(name);
            MeshFilter mf = t != null ? t.GetComponent<MeshFilter>() : null;
            return mf != null ? mf : null;   // Unity null (destroyed / missing) as a real null, for ??
        }

        /// <summary>One LODGroup on peRoot (ASSETS.md 6): LOD0 renderers, then the LOD1 set, then the LOD1 chamber
        /// and bell alone (both kits culled) down to a screen height of 0, so the engine never disappears. A clone's
        /// copied group already points at its own renderers; setting it again is harmless.</summary>
        void SetupLods()
        {
            lodGroup = root.GetComponent<LODGroup>();
            if (lodGroup == null) lodGroup = root.gameObject.AddComponent<LODGroup>();
            Renderer chamber0 = staticMF.GetComponent<MeshRenderer>(), chamber1 = staticLodMF.GetComponent<MeshRenderer>();
            lodGroup.fadeMode = LODFadeMode.None;
            lodGroup.SetLODs(new[]
            {
                new LOD(Lod0Height, new Renderer[] { chamber0, bellR, kitR, kitBellR }),
                new LOD(Lod1Height, new Renderer[] { chamber1, bellLodR, kitLodR, kitBellLodR }),
                new LOD(0f, new Renderer[] { chamber1, bellLodR }),
            });
        }

        Mesh Own(string name)
        {
            var m = new Mesh { name = name };
            ownedMeshes.Add(m);
            return m;
        }

        /// <summary>The part goes (editor delete, undo, vessel unload, scene change): leave the queue, give back the
        /// shared meshes (kept a few seconds for an undo or reload to pick up again), destroy what it made itself
        /// and the copies other mods made of its meshes on its filters (a conformal decal dies with its parent).</summary>
        void OnDestroy()
        {
            if (Lod0Pending) { Lod0Pending = false; lod0Queue.Remove(this); }
            if (vesselEvents)
            {
                vesselEvents = false;
                GameEvents.onVesselWasModified.Remove(OnVesselChanged);
                GameEvents.onVesselGoOffRails.Remove(OnVesselChanged);
            }
            for (int i = 0; i < Slots; i++)
            {
                MeshFilter f = filters[i];
                if (f == null) continue;
                Mesh cur = f.sharedMesh;
                if (IsForeignCopy(i, cur)) Destroy(cur);
            }
            for (int i = 0; i < keptCopies.Count; i++)
                if (keptCopies[i] != null) Destroy(keptCopies[i]);
            keptCopies.Clear();
            EngineMeshCache.Release(lod0Entry, PartGrace);
            EngineMeshCache.Release(lod1Entry, PartGrace);
            lod0Entry = lod1Entry = null;
            for (int i = 0; i < ownedMeshes.Count; i++)
                if (ownedMeshes[i] != null) Destroy(ownedMeshes[i]);
            ownedMeshes.Clear();
        }

        // ------------------------------------------------------------------ render filters and foreign copies
        // Reading MeshFilter.mesh (not sharedMesh) on a filter that doesn't own its mesh makes Unity copy the mesh
        // ("<name> Instance"), point the filter at the copy and leave the copy to the caller. Mods do this to our
        // shared meshes: KSPCommunityFixes' drag cubes (handled in Integrations.NotifyAero), Firefly's vessel bounds,
        // ConformalDecals' projection targets. Each part remembers what it gave every filter; in flight it checks
        // about once a second (and after vessel changes) and points the filter back at the shared mesh, so identical
        // engines keep sharing and the copies don't pile up until the next scene change.

        /// <summary>Point render filter `slot` at `mesh` (and remember it); a copy another mod left on the filter goes first.</summary>
        void Show(int slot, Mesh mesh)
        {
            MeshFilter f = filters[slot];
            if (f == null) return;
            DropCopy(slot, f.sharedMesh);
            f.sharedMesh = mesh;
            shown[slot] = mesh;
        }

        /// <summary>The meshes of a cache entry on the four filters of its level.</summary>
        void ShowLevel(int lod, EngineMeshCache.Entry e)
        {
            int b = lod <= 0 ? 0 : Lod1Slot;
            Show(b + SlotChamber, e.Chamber);
            Show(b + SlotBell, e.Bell);
            Show(b + SlotKit, e.Kit);
            Show(b + SlotKitBell, e.KitBell);
        }

        /// <summary>Flight: put back what this part gave each filter wherever a mod swapped in something else.</summary>
        void RestoreMeshes()
        {
            for (int i = 0; i < Slots; i++)
            {
                MeshFilter f = filters[i];
                Mesh want = shown[i];
                if (f == null || want == null) continue;   // never assigned by this part (or gone): nothing to put back
                Mesh cur = f.sharedMesh;
                if (cur == want) continue;
                if (copiesReplaced++ == 0)   // once per session; a name ending in " Instance" is a MeshFilter.mesh copy
                    Debug.Log($"[ProcEngines] {designName}: render mesh {want.name} was replaced by {(cur != null ? cur.name : "nothing")}; putting the shared mesh back (further cases not logged)");
                DropCopy(i, cur);
                f.sharedMesh = want;
            }
        }

        /// <summary>A mesh on filter `slot` that is a per-filter copy another mod made (MeshFilter.mesh) of what this
        /// part gave it: only once this part has assigned the filter (a clone's filters show its source's meshes until
        /// then, and those aren't ours to destroy), named as Unity names such copies, and none of ours.</summary>
        bool IsForeignCopy(int slot, Mesh m) =>
            !ReferenceEquals(shown[slot], null) && m != null && m != shown[slot] && m.name.EndsWith(" Instance", StringComparison.Ordinal)
            && !ownedMeshes.Contains(m) && !keptCopies.Contains(m) && !EngineMeshCache.Holds(m);

        /// <summary>The filter is about to show something else: destroy a foreign copy it holds, or keep it until the
        /// part goes when a conformal decal on a child part may still be projecting onto it.</summary>
        void DropCopy(int slot, Mesh cur)
        {
            if (!IsForeignCopy(slot, cur)) return;
            if (DecalOnChild()) keptCopies.Add(cur);
            else Destroy(cur);
        }

        /// <summary>ConformalDecals projects onto its parent part's meshes and holds the MeshFilter.mesh copies it made.</summary>
        bool DecalOnChild()
        {
            if (part == null || part.children == null) return false;
            foreach (Part c in part.children)
            {
                if (c == null || c.Modules == null) continue;
                for (int i = 0; i < c.Modules.Count; i++)
                {
                    string n = c.Modules[i] != null ? c.Modules[i].moduleName : null;
                    if (n != null && n.StartsWith("ModuleConformal", StringComparison.Ordinal)) return true;
                }
            }
            return false;
        }

        /// <summary>Firefly re-reads the vessel's bounds (through MeshFilter.mesh) about 20 frames after a vessel
        /// change or load: check now and again shortly after that.</summary>
        void OnVesselChanged(Vessel v)
        {
            if (v == null || v != vessel) return;
            RestoreMeshes();
            nextMeshCheck = Mathf.Min(nextMeshCheck, Time.realtimeSinceStartup + 0.5f);
        }

        /// <summary>The prefab's renderers get their own meshes (filled in LOADING); a renderer made anywhere else (an
        /// old hierarchy missing the LOD1 set) gets a placeholder owned by this part until a cache entry replaces it.</summary>
        MeshFilter MakeRenderer(Transform parent, string name, Material[] materials)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var mf = go.AddComponent<MeshFilter>();
            mf.sharedMesh = HighLogic.LoadedScene == GameScenes.LOADING ? new Mesh { name = name } : Own(name);
            go.AddComponent<MeshRenderer>().sharedMaterials = materials;
            return mf;
        }

        /// <summary>The extension material (niobium or carbon-carbon) depends on the tech level; the regen tube tint,
        /// the soot inside and the hot duct on the propellant; tube or channel wall on the cycle (and tech); the
        /// kerolox bell's sooted tube wall on whether the built bell has a radiative extension (its soot band belongs
        /// at the exit lip, not at the joint). Called once `geometry` is current.</summary>
        void UpdateMaterials()
        {
            string tech = Design.Tech ?? "Mid";
            bool chamberChannel = EngineGeometry.ChannelWall(Design), bellChannel = EngineGeometry.ChannelNozzle(Design);
            bool extension = geometry != null && geometry.HasExtension;
            string key = tech + "|" + Design.Propellant + "|" + chamberChannel + "|" + bellChannel + "|" + extension;
            if (materialKey == key) return;
            Material[] bell = EngineMaterials.Bell(tech, Design.Propellant, bellChannel, extension), chamber = EngineMaterials.Static(Design.Propellant, chamberChannel),
                kit = EngineMaterials.Kit(Design.Propellant);
            bellR.sharedMaterials = bell;
            bellLodR.sharedMaterials = bell;
            staticMF.GetComponent<MeshRenderer>().sharedMaterials = chamber;
            staticLodMF.GetComponent<MeshRenderer>().sharedMaterials = chamber;
            kitR.sharedMaterials = kit;
            kitLodR.sharedMaterials = kit;
            kitBellR.sharedMaterials = kit;
            kitBellLodR.sharedMaterials = kit;
            radiativeTech = tech;
            materialKey = key;
        }

        // ------------------------------------------------------------------ meshes

        /// <param name="full">true: both levels, then automatic LOD switching (OnStart in the editor, one-off changes,
        /// the slow path when the drag path didn't run). false: the drag path, which rebuilds only the distant level
        /// and shows it (ForceLOD) until LOD0 follows.</param>
        /// <returns>Whether the meshes were built (false: nothing to build, or the build failed and the part keeps its
        /// previous meshes; the error is logged).</returns>
        public bool RebuildMesh(bool full = true)
        {
            if (Stats == null) return false;
            MeshRebuilds++;
            if (!BuildMeshes(true, full)) return false;
            if (lodGroup != null)
            {
                if (full) { lodGroup.ForceLOD(-1); lodGroup.RecalculateBounds(); }
                else lodGroup.ForceLOD(1);
            }
            return true;
        }

        string MeshScope => HighLogic.LoadedSceneIsFlight ? "F" : "E";

        /// <summary>
        /// Points the requested levels' renderers at the cache entries for the current DESIGN + STATS, building an
        /// entry on a miss, and places the transforms. Every position the nodes, colliders, transforms and glow use
        /// is identical at both levels (EngineGeometry and KitBuilder take them from the full-resolution contour and
        /// the LOD0 pieces), so this part's own LOD1 geometry serves for all of them. The new entry is assigned before
        /// the old one is released (a superseded set nobody else holds goes at once). A level not requested keeps its
        /// current meshes (the drag path leaves LOD0 as it was, hidden by ForceLOD(1)).
        /// </summary>
        bool BuildMeshes(bool lod1, bool lod0)
        {
            if (Stats == null) return false;
            try
            {
                TechLevel tech = Data.TechLevels.TryGetValue(Design.Tech ?? "Mid", out TechLevel t) ? t : null;
                KitBuilder.LineRadii(Design, Stats, Data, out float fuelR, out float _);
                string key = EngineGeometry.MeshKey(Design, Stats, MeshScope);
                if (geometry == null || geometryKey != key)
                {
                    geometry = EngineGeometry.Build(Design, Stats, tech, fuelR, 1);
                    geometryKey = key;
                }
                if (lod1)
                {
                    EngineMeshCache.Entry e = Level(1, key, geometry, tech, fuelR);
                    ShowLevel(1, e);
                    if (e != lod1Entry) { EngineMeshCache.Release(lod1Entry, 0); lod1Entry = e; }
                    else EngineMeshCache.Release(e, 0);   // already held: drop the extra reference Level took
                    Kit = e.KitResult;
                }
                if (lod0)
                {
                    EngineMeshCache.Entry e = Level(0, key, null, tech, fuelR);
                    ShowLevel(0, e);
                    if (e != lod0Entry) { EngineMeshCache.Release(lod0Entry, 0); lod0Entry = e; }
                    else EngineMeshCache.Release(e, 0);
                    Kit = e.KitResult;
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ProcEngines] {designName}: mesh build failed for {Design?.Propellant}/{Design?.Cycle} eps {Design?.Eps}: {ex}");
                return false;
            }
            if (HighLogic.LoadedScene != GameScenes.LOADING) UpdateMaterials();
            PlaceTransforms();
            return true;
        }

        /// <summary>A cache entry for one level: a hit takes a reference; a miss builds the four meshes (the LOD0
        /// geometry is built here, only on a miss) and registers them with one reference.</summary>
        EngineMeshCache.Entry Level(int lod, string key, EngineGeometry g, TechLevel tech, float fuelR)
        {
            string levelKey = (lod <= 0 ? "L0|" : "L1|") + key;
            EngineMeshCache.Entry e = EngineMeshCache.Acquire(levelKey);
            if (e != null) return e;
            if (g == null || g.Lod != lod) g = EngineGeometry.Build(Design, Stats, tech, fuelR, lod);
            Mesh chamber = new Mesh { name = lod <= 0 ? ChamberName : ChamberLodName };
            Mesh bell = new Mesh { name = lod <= 0 ? BellName : BellLodName };
            Mesh kit = new Mesh { name = lod <= 0 ? KitName : KitLodName };
            Mesh kitBell = new Mesh { name = lod <= 0 ? KitBellName : KitBellLodName };
            try
            {
                EngineGeometry.Lathe(chamber, g.Static, EngineGeometry.StaticMaterials, EngineGeometry.RingSegments(g.StaticMaxRadius, lod), 0f);
                EngineGeometry.Lathe(bell, g.Bell, EngineGeometry.BellMaterials, EngineGeometry.RingSegments(g.MaxRadius, lod), g.YThroat);
                KitBuilder.Result kr = KitBuilder.Build(kit, kitBell, Design, Stats, g, Data, lod);
                return EngineMeshCache.Add(levelKey, chamber, bell, kit, kitBell, kr);
            }
            catch
            {
                Destroy(chamber); Destroy(bell); Destroy(kit); Destroy(kitBell);
                throw;
            }
        }

        void PlaceTransforms()
        {
            gimbalT.localPosition = new Vector3(0, geometry.YThroat, 0);
            thrustT.localPosition = new Vector3(0, 0, geometry.YThroat - geometry.YExit);
            exhaustT.localPosition = Kit != null && Kit.HasExhaust ? Kit.ExhaustPosition : new Vector3(0, geometry.YExit, 0);
            exhaustT.localRotation = Quaternion.LookRotation(Vector3.down, Vector3.forward);   // +Z down the exhaust, like thrustTransform
        }

        /// <summary>LOADING: the prefab's own meshes (made with its renderers), the distant level only; the LOD0
        /// renderers point at the same meshes (clones replace both sets with cache entries at OnStart).</summary>
        void BuildPrefab()
        {
            if (Stats == null) return;
            try
            {
                TechLevel tech = Data.TechLevels.TryGetValue(Design.Tech ?? "Mid", out TechLevel t) ? t : null;
                KitBuilder.LineRadii(Design, Stats, Data, out float fuelR, out float _);
                EngineGeometry g1 = EngineGeometry.Build(Design, Stats, tech, fuelR, 1);
                EngineGeometry.Lathe(staticLodMF.sharedMesh, g1.Static, EngineGeometry.StaticMaterials, EngineGeometry.RingSegments(g1.StaticMaxRadius, 1), 0f);
                EngineGeometry.Lathe(bellLodMF.sharedMesh, g1.Bell, EngineGeometry.BellMaterials, EngineGeometry.RingSegments(g1.MaxRadius, 1), g1.YThroat);
                Kit = KitBuilder.Build(kitLodMF.sharedMesh, kitBellLodMF.sharedMesh, Design, Stats, g1, Data, 1);
                geometry = g1;
                geometryKey = null;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ProcEngines] prefab mesh build failed: {ex}");   // the part still loads, without a model
                return;
            }
            // the LOD0 renderers show the LOD1 meshes; the empty LOD0 meshes they were made with go
            for (int i = 0; i < Lod1Slot; i++)
            {
                Mesh old = filters[i].sharedMesh, lod1 = filters[Lod1Slot + i].sharedMesh;
                shown[Lod1Slot + i] = lod1;
                Show(i, lod1);
                if (old != null && old != lod1) Destroy(old);
            }
            UpdateMaterials();   // the built bell decides the kerolox wall surface (extension or not)
            PlaceTransforms();
            if (lodGroup != null) { lodGroup.ForceLOD(-1); lodGroup.RecalculateBounds(); }
            MeshRebuilds++;
        }

        // ------------------------------------------------------------------ flight: LOD0 after OnStart

        static readonly List<ModuleProcEngine> lod0Queue = new List<ModuleProcEngine>();
        static int lod0QueueFrame = -1;

        /// <summary>Flight OnStart: LOD1 now, shown on both renderer sets (so anything that draws the LOD0 renderers
        /// directly shows the right shape), forced; LOD0 joins the queue.</summary>
        void StartFlightMeshes()
        {
            MeshRebuilds++;
            if (Stats == null || !BuildMeshes(true, false)) return;
            for (int i = 0; i < Lod1Slot; i++) Show(i, shown[Lod1Slot + i]);
            if (lodGroup != null) { lodGroup.ForceLOD(1); lodGroup.RecalculateBounds(); }
            if (!Lod0Pending) { Lod0Pending = true; lod0Queue.Add(this); }
        }

        /// <summary>Called from every flight engine's Update; the first call in a frame works the queue: cache hits
        /// complete at once, and it stops after the first engine that had to build. Parts destroyed while queued
        /// (vessel unloaded, part lost, scene change) are skipped.</summary>
        static void PumpLod0Queue()
        {
            if (lod0QueueFrame == Time.frameCount || lod0Queue.Count == 0) return;
            lod0QueueFrame = Time.frameCount;
            while (lod0Queue.Count > 0)
            {
                ModuleProcEngine m = lod0Queue[0];
                lod0Queue.RemoveAt(0);
                if (m == null || !m.Lod0Pending) continue;
                if (m.CompleteLod0()) break;
            }
        }

        /// <returns>Whether it had to build (a cache miss), which ends this frame's share of the queue.</returns>
        bool CompleteLod0()
        {
            Lod0Pending = false;
            int misses = EngineMeshCache.Misses;
            if (!BuildMeshes(false, true)) return true;   // logged; the part keeps LOD1 on both sets
            if (lodGroup != null) { lodGroup.ForceLOD(-1); lodGroup.RecalculateBounds(); }
            if (ReleaseCpuCopies)
            {
                lod0Entry?.ReleaseCpuCopy();
                lod1Entry?.ReleaseCpuCopy();
            }
            return EngineMeshCache.Misses != misses;
        }

        /// <summary>Assemblies (name prefixes) of mods known to read render-mesh vertices in flight. With any of them
        /// loaded the flight meshes stay readable. FAR is covered by Patches/FAR.cfg (it voxelises the colliders).
        /// Firefly copies the meshes through MeshFilter.mesh (RestoreMeshes puts ours back within a second), and a copy
        /// of a mesh without its CPU data could draw empty until then.</summary>
        static readonly string[] MeshReaders = { "KerbalJointReinforcement", "KJR", "KerbalFX", "AeroFX", "MechJeb", "ProceduralFairings", "ConformalDecals", "BDArmory", "KIS", "KAS", "Trajectories", "Firefly" };
        static bool? releaseCpu;

        /// <summary>Flight meshes may drop their CPU copy (UploadMeshData(true), about half their memory) once the
        /// part's LOD0 is in: nothing rebuilds in flight, the editor never sees these entries (scope "F"), and only
        /// when no installed mod is known to read the vertices.</summary>
        static bool ReleaseCpuCopies
        {
            get
            {
                if (releaseCpu == null)
                {
                    string reader = null;
                    foreach (AssemblyLoader.LoadedAssembly a in AssemblyLoader.loadedAssemblies)
                    {
                        string n = a.assembly?.GetName().Name ?? a.name ?? "";
                        if (MeshReaders.Any(r => n.StartsWith(r, StringComparison.OrdinalIgnoreCase))) { reader = n; break; }
                    }
                    releaseCpu = reader == null;
                    Debug.Log("[ProcEngines] flight meshes " + (reader == null ? "drop their CPU copy once built" : $"stay readable ({reader} reads mesh data)"));
                }
                return releaseCpu.Value;
            }
        }

        // ------------------------------------------------------------------ colliders and nodes

        public void RebuildColliders()
        {
            if (geometry == null) return;
            EngineGeometry.Frustum(colliders[0].sharedMesh, geometry.ColliderChamberTop, geometry.ColliderChamberBottom);
            EngineGeometry.Frustum(colliders[1].sharedMesh, geometry.ColliderThroat, geometry.ColliderMid);
            EngineGeometry.Frustum(colliders[2].sharedMesh, geometry.ColliderMid, geometry.ColliderExit);
            foreach (MeshCollider mc in colliders) { Mesh m = mc.sharedMesh; mc.sharedMesh = null; mc.sharedMesh = m; }
        }

        /// <summary>Absolute node positions from the geometry (never deltas); push or follow attached parts.</summary>
        public void SetNodes(bool push)
        {
            if (geometry == null) return;
            SetNode("top", Vector3.zero, Mathf.Clamp(Mathf.RoundToInt(2 * geometry.MountRadius / 1.25f), 0, 4), push);
            SetNode("bottom", new Vector3(0, geometry.YExit, 0), Mathf.Clamp(Mathf.RoundToInt((float)Stats.ExitDiameter / 1.25f), 0, 4), push);
        }

        void SetNode(string id, Vector3 pos, int size, bool push)
        {
            AttachNode n = part.FindAttachNode(id);
            if (n == null) return;
            Vector3 oldWorld = part.transform.TransformPoint(n.position);
            n.position = n.originalPosition = pos;
            n.size = size;
            if (!push || n.attachedPart == null) return;
            Vector3 delta = part.transform.TransformPoint(pos) - oldWorld;
            if (delta.sqrMagnitude < 1e-12f) return;
            // Attached to our parent through this node: move ourselves (with our subtree); otherwise push the child.
            if (n.attachedPart == part.parent) part.transform.position -= delta;
            else n.attachedPart.transform.position += delta;
        }
    }

    /// <summary>
    /// Render meshes shared by every part whose (level, DESIGN, STATS, mesh version, scene scope) key is identical:
    /// symmetry counterparts, clusters, undo re-spawns and LMP vessel reloads build each level once. Entries are
    /// reference counted. A part never destroys or rebuilds a shared mesh: a design change acquires another entry
    /// and releases the old one (non-symmetric copies start out sharing). An entry nobody holds is destroyed at
    /// once when a design change superseded it, after a short grace when its part went away (so an undo or a vessel
    /// reload a moment later still hits), and in any case on the next scene load. Unity main thread only.
    /// </summary>
    public static class EngineMeshCache
    {
        public sealed class Entry
        {
            public readonly string Key;
            /// <summary>Static chamber, gimballed bell, static kit, and the kit fixed to the bell (in the bell's frame).</summary>
            public readonly Mesh Chamber, Bell, Kit, KitBell;
            public readonly KitBuilder.Result KitResult;   // shared, read-only: exhaust placement
            public int Refs { get; internal set; }
            /// <summary>False once ReleaseCpuCopy ran (flight only).</summary>
            public bool Readable { get; private set; } = true;
            internal float IdleSince, Grace;

            internal Entry(string key, Mesh chamber, Mesh bell, Mesh kit, Mesh kitBell, KitBuilder.Result kitResult)
            {
                Key = key; Chamber = chamber; Bell = bell; Kit = kit; KitBell = kitBell; KitResult = kitResult;
            }

            /// <summary>Meshes still there (Unity null check: nothing else should ever destroy them).</summary>
            public bool Alive => Chamber != null && Bell != null && Kit != null && KitBell != null;

            /// <summary>One of this entry's meshes.</summary>
            internal bool Has(Mesh m) => m == Chamber || m == Bell || m == Kit || m == KitBell;

            /// <summary>Upload and drop the CPU copy (Mesh.UploadMeshData(true)); the meshes can't be read or written
            /// afterwards, and nothing in flight needs to.</summary>
            public void ReleaseCpuCopy()
            {
                if (!Readable || !Alive) return;
                Chamber.UploadMeshData(true);
                Bell.UploadMeshData(true);
                Kit.UploadMeshData(true);
                KitBell.UploadMeshData(true);
                Readable = false;
            }
        }

        static readonly Dictionary<string, Entry> entries = new Dictionary<string, Entry>();
        static readonly List<Entry> idle = new List<Entry>();
        static bool hooked;
        static float nextSweep;

        /// <summary>Diagnostics: live entries, unreferenced entries waiting for their grace to end, counters.</summary>
        public static int Count => entries.Count;
        public static int IdleCount => idle.Count;
        public static int Hits, Misses, Destroyed;

        /// <summary>The entry for `key` with one more reference, or null on a miss (the caller builds and Adds).</summary>
        public static Entry Acquire(string key)
        {
            Hook();
            if (!entries.TryGetValue(key, out Entry e)) { Misses++; return null; }
            if (!e.Alive) { entries.Remove(key); idle.Remove(e); Misses++; return null; }
            if (e.Refs == 0) idle.Remove(e);
            e.Refs++;
            Hits++;
            return e;
        }

        /// <summary>Register freshly built meshes under `key`, with one reference for the caller.</summary>
        public static Entry Add(string key, Mesh chamber, Mesh bell, Mesh kit, Mesh kitBell, KitBuilder.Result kitResult)
        {
            Hook();
            if (entries.TryGetValue(key, out Entry old))
            {
                // only after a miss, so this is a dead or unexpected duplicate: an unreferenced one goes now, a held
                // one leaves the table and is destroyed when its last holder releases it
                entries.Remove(key);
                if (old.Refs == 0) Destroy(old);
            }
            var e = new Entry(key, chamber, bell, kit, kitBell, kitResult) { Refs = 1 };
            entries[key] = e;
            return e;
        }

        /// <summary>Drop one reference. When none is left the entry is destroyed after `grace` seconds (0: now).</summary>
        public static void Release(Entry e, float grace)
        {
            if (e == null || e.Refs <= 0) return;
            if (--e.Refs > 0) return;
            if (grace <= 0) { Destroy(e); return; }
            e.IdleSince = Time.realtimeSinceStartup;
            e.Grace = grace;
            if (!idle.Contains(e)) idle.Add(e);
        }

        /// <summary>Destroy unreferenced entries whose grace is over (all of them with `all`). Cheap; runs at most
        /// twice a second unless `all`.</summary>
        public static void Sweep(bool all = false)
        {
            float now = Time.realtimeSinceStartup;
            if (!all && (now < nextSweep || idle.Count == 0)) return;
            nextSweep = now + 0.5f;
            for (int i = idle.Count - 1; i >= 0; i--)
            {
                Entry e = idle[i];
                if (e.Refs > 0) { idle.RemoveAt(i); continue; }
                if (all || now - e.IdleSince >= e.Grace) { idle.RemoveAt(i); Destroy(e); }
            }
        }

        static void Destroy(Entry e)
        {
            if (entries.TryGetValue(e.Key, out Entry cur) && cur == e) entries.Remove(e.Key);
            idle.Remove(e);
            if (e.Chamber != null) UnityEngine.Object.Destroy(e.Chamber);
            if (e.Bell != null) UnityEngine.Object.Destroy(e.Bell);
            if (e.Kit != null) UnityEngine.Object.Destroy(e.Kit);
            if (e.KitBell != null) UnityEngine.Object.Destroy(e.KitBell);
            Destroyed++;
        }

        /// <summary>Whether `m` is one of the cached meshes (a linear scan: for the rare foreign-copy checks only).</summary>
        internal static bool Holds(Mesh m)
        {
            if (m == null) return false;
            foreach (Entry e in entries.Values)
                if (e.Has(m)) return true;
            return false;
        }

        /// <summary>On every scene load the parts of the old scene are gone: free what they left behind, and the
        /// pooled mesh builders' buffers.</summary>
        static void Hook()
        {
            if (hooked) return;
            hooked = true;
            // KSP's EventData needs an instance target (EvtDelegate reads evt.Target), so a static method can't be
            // registered directly: it throws NullReferenceException.
            GameEvents.onLevelWasLoaded.Add(SceneHook.Instance.OnLevelWasLoaded);
        }

        sealed class SceneHook
        {
            public static readonly SceneHook Instance = new SceneHook();
            public void OnLevelWasLoaded(GameScenes scene)
            {
                Sweep(true);
                MeshBuilder.ClearPool();
            }
        }
    }
}
