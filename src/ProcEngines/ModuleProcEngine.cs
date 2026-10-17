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
    /// </summary>
    public class ModuleProcEngine : PartModule, IPartMassModifier, IPartCostModifier
    {
        public const string RootName = "peRoot", GimbalName = "peGimbal", ThrustName = "thrustTransform";

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
        public int MeshRebuilds, SlowRebuilds;
        public double LastBuildMs;
        public event Action DesignApplied;

        Transform root, gimbalT, thrustT;
        MeshFilter staticMF, bellMF, kitMF;
        readonly List<MeshCollider> colliders = new List<MeshCollider>();
        EngineGeometry geometry;
        bool designDirty, meshDirty, slowDirty, ownMeshes, nodesPending;
        float lastChange, lastMesh, lastCapsCheck;
        static Material steel, copper, niobium, soot;

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
                // screen and the prefab drag cube show a real engine.
                StripPlaceholder();
                EnsureTransforms();
                Stats = EngineModel.Compute(Design, Data);
                RebuildMesh();
                RebuildColliders();
                SetNodes(false);
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
            RebuildMesh();
            RebuildColliders();
            // Editor: stats were recomputed (possibly at another tech level than when the craft was saved), so the
            // nodes may have moved: push the attached parts on the first Update, once the craft is fully linked.
            if (HighLogic.LoadedSceneIsEditor) nodesPending = true;
            else SetNodes(false);
            ApplyEngine();
            UpdateReadouts();
            LastBuildMs = sw.Elapsed.TotalMilliseconds;
            if (HighLogic.LoadedSceneIsFlight) StartCoroutine(FlightPostStart());
        }

        void Update()
        {
            if (HighLogic.LoadedSceneIsFlight && TechLocked)
            {
                var e = part.Modules.OfType<ModuleEnginesFX>().FirstOrDefault();
                if (e != null && (e.EngineIgnited || e.getIgnitionState)) e.Shutdown();
                return;
            }
            if (!HighLogic.LoadedSceneIsEditor || Design == null) return;
            float now = Time.realtimeSinceStartup;
            if (nodesPending) { nodesPending = false; SetNodes(true); }
            // Tech can change while the editor is open (LMP co-op research, R&D retry): re-evaluate every 2 s.
            if (now - lastCapsCheck > (Caps == null ? 1f : 2f))
            {
                lastCapsCheck = now;
                TechCaps c = KspData.CurrentCaps();
                if (c != null && CapsKey(c) != CapsKey(Caps)) { Caps = c; designDirty = true; }
            }
            if (designDirty)
            {
                designDirty = false;
                Recompute();
                ApplyEngine();
                UpdateReadouts();
                meshDirty = slowDirty = true;
            }
            if (meshDirty && now - lastMesh >= 0.1f)   // at most 10 Hz while dragging
            {
                meshDirty = false;
                lastMesh = now;
                RebuildMesh();
            }
            if (slowDirty && now - lastChange >= 0.5f)   // on release + 0.5 s debounce
            {
                slowDirty = false;
                if (meshDirty) { meshDirty = false; RebuildMesh(); }
                RebuildColliders();
                SetNodes(true);
                Integrations.NotifyAero(part);
                SlowRebuilds++;
                if (EditorLogic.fetch != null)
                {
                    GameEvents.onEditorShipModified.Fire(EditorLogic.fetch.ship);
                    EditorLogic.fetch.SetBackup();
                }
                DesignApplied?.Invoke();
            }
        }

        static string CapsKey(TechCaps c) => c == null ? "" : string.Join("|", c.Unlimited, c.TechLevel, c.MaxPc, c.MaxEps, c.MaxThrust, c.RaoBell, c.RadiativeExtension,
            string.Join(",", c.Cycles.OrderBy(x => x)), string.Join(",", c.Propellants.OrderBy(x => x)));

        /// <summary>Editor: replace the design (clamped to the current caps), push it to symmetry counterparts.</summary>
        public void SetDesign(EngineDesign d, bool pushSymmetry = true)
        {
            Design = Clamp(d);
            designDirty = true;
            lastChange = Time.realtimeSinceStartup;
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
            Plume plume = Data.PlumeFor(Design.Propellant, Plume.RoleFor(Stats));
            if (plume != null)
                yield return StartCoroutine(Integrations.ApplyWaterfall(this, plume.Template, (float)(Stats.ExitDiameter / plume.ReferenceDiameter), (float)plume.LengthScale, s => WaterfallStatus = s));
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
        public void OpenDesigner() => DesignerWindow.Open(this);

        public float GetModuleMass(float defaultMass, ModifierStagingSituation sit) => Stats == null ? 0 : (float)Stats.Mass - defaultMass;
        public ModifierChangeWhen GetModuleMassChangeWhen() => ModifierChangeWhen.FIXED;
        public float GetModuleCost(float defaultCost, ModifierStagingSituation sit) => Stats == null ? 0 : (float)Stats.Cost - defaultCost;
        public ModifierChangeWhen GetModuleCostChangeWhen() => ModifierChangeWhen.FIXED;

        // ------------------------------------------------------------------ transforms and meshes

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
            if (root == null)
            {
                root = new GameObject(RootName).transform;
                root.SetParent(model, false);
                staticMF = MakeRenderer(root, "peChamber", 1);
                kitMF = MakeRenderer(root, "peKit", 1);
                // Gimbal pivot oriented like a stock gimbal transform (+Z down the exhaust); the bell counter-rotates.
                gimbalT = new GameObject(GimbalName).transform;
                gimbalT.SetParent(root, false);
                gimbalT.localRotation = Quaternion.Euler(90f, 0f, 0f);
                bellMF = MakeRenderer(gimbalT, "peBell", 3);
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
                staticMF = root.Find("peChamber").GetComponent<MeshFilter>();
                kitMF = root.Find("peKit").GetComponent<MeshFilter>();
                gimbalT = root.Find(GimbalName);
                bellMF = gimbalT.Find("peBell").GetComponent<MeshFilter>();
                thrustT = gimbalT.Find(ThrustName);
            }
            // Instantiate() copies serialized state only: a clone (prefab -> part, or an editor copy) starts with
            // ownMeshes = false and still shares its source's Mesh objects, so give it its own.
            bool fresh = HighLogic.LoadedScene != GameScenes.LOADING && !ownMeshes;
            if (fresh)
            {
                staticMF.sharedMesh = new Mesh { name = "peChamber" };
                kitMF.sharedMesh = new Mesh { name = "peKit" };
                bellMF.sharedMesh = new Mesh { name = "peBell" };
            }
            colliders.Clear();
            for (int i = 0; i < 3; i++)
            {
                var mc = root.Find("peCollider" + i).GetComponent<MeshCollider>();
                if (fresh) mc.sharedMesh = new Mesh { name = "peCollider" + i };
                colliders.Add(mc);
            }
            if (fresh) ownMeshes = true;
            if (part.collider == null) part.collider = colliders[0];
        }

        MeshFilter MakeRenderer(Transform parent, string name, int materials)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var mf = go.AddComponent<MeshFilter>();
            mf.sharedMesh = new Mesh { name = name };
            var mr = go.AddComponent<MeshRenderer>();
            if (steel == null)
            {
                Shader sh = Shader.Find("KSP/Specular") ?? Shader.Find("Standard");
                steel = new Material(sh) { color = new Color(0.56f, 0.57f, 0.6f) };
                copper = new Material(sh) { color = new Color(0.62f, 0.42f, 0.28f) };
                niobium = new Material(sh) { color = new Color(0.32f, 0.33f, 0.36f) };
                soot = new Material(sh) { color = new Color(0.07f, 0.06f, 0.06f) };
            }
            mr.sharedMaterials = materials == 3 ? new[] { copper, niobium, soot } : new[] { steel };
            return mf;
        }

        public void RebuildMesh()
        {
            if (Stats == null) return;
            TechLevel tech = Data.TechLevels.TryGetValue(Design.Tech ?? "Mid", out TechLevel t) ? t : null;
            geometry = EngineGeometry.Build(Design, Stats, tech);
            int seg = Mathf.Clamp(Mathf.CeilToInt(2 * Mathf.PI * geometry.MaxRadius / 0.06f), 16, 64);
            EngineGeometry.Lathe(staticMF.sharedMesh, geometry.StaticSegments, null, 1, seg, 0f);
            EngineGeometry.Lathe(bellMF.sharedMesh, geometry.BellSegments, geometry.BellSegmentMaterial, 3, seg, geometry.YThroat);
            EngineGeometry.BuildKit(kitMF.sharedMesh, Design, Stats, geometry);
            gimbalT.localPosition = new Vector3(0, geometry.YThroat, 0);
            thrustT.localPosition = new Vector3(0, 0, geometry.YThroat - geometry.YExit);
            MeshRebuilds++;
        }

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
}
