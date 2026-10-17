using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace ProcEngineSpike
{
    /// <summary>
    /// Phase 0 spike: one base part whose module generates the engine. Listed BEFORE ModuleEnginesFX
    /// and ModuleGimbal in the cfg, so OnLoad/OnStart here run first and the transforms exist when they cache them.
    /// </summary>
    public class ModuleProcEngineSpike : PartModule, IPartMassModifier, IPartCostModifier
    {
        public const string RootName = "peRoot", GimbalName = "peGimbal", ThrustName = "thrustTransform";
        public const int SchemaVersion = 1;

        [KSPField(guiActiveEditor = true, guiName = "Thrust (vac)", guiUnits = " kN")]
        [UI_FloatRange(minValue = 10f, maxValue = 2000f, stepIncrement = 5f, scene = UI_Scene.Editor)]
        public float thrustVac = 200f;

        [KSPField(guiActiveEditor = true, guiName = "Chamber pressure", guiUnits = " bar")]
        [UI_FloatRange(minValue = 10f, maxValue = 250f, stepIncrement = 1f, scene = UI_Scene.Editor)]
        public float pc = 60f;

        [KSPField(guiActiveEditor = true, guiName = "Expansion ratio")]
        [UI_FloatRange(minValue = 3f, maxValue = 200f, stepIncrement = 0.5f, scene = UI_Scene.Editor)]
        public float eps = 20f;

        [KSPField(guiActiveEditor = true, guiName = "Apply engine via")]
        [UI_Toggle(enabledText = "Load(ConfigNode)", disabledText = "direct fields", scene = UI_Scene.Editor)]
        public bool useLoad = true;

        [KSPField(guiActive = true, guiActiveEditor = true, guiName = "Isp vac / SL")]
        public string ispReadout = "";

        [KSPField(guiActive = true, guiActiveEditor = true, guiName = "Exit diameter")]
        public string sizeReadout = "";

        [KSPField] public string waterfallTemplate = "waterfall-kerolox-lower-1";
        [KSPField] public float waterfallReferenceDiameter = 1f;

        public SpikeStats stats;
        public int loadedModelVersion = -1;

        // Instrumentation read by the test driver.
        public int renderRebuilds, slowRebuilds, engineApplies;
        public double lastBuildMs;
        public string lastApplyMode = "";
        public string waterfallStatus = "not run";

        Transform root, gimbalT, thrustT;
        MeshFilter staticMF, bellMF;
        readonly List<MeshCollider> colliders = new List<MeshCollider>();
        bool designDirty, meshDirty, slowDirty, ownMeshes;
        float lastChange, lastMesh;
        static Material outerMat, innerMat, chamberMat;

        public SpikeDesign Design => new SpikeDesign { thrustVac = thrustVac, pc = pc, eps = eps };
        public static bool FarPresent => farPresent ?? (bool)(farPresent = AssemblyLoader.loadedAssemblies.Any(a => a.assembly.GetName().Name == "FerramAerospaceResearch"
            && a.assembly.GetType("FerramAerospaceResearch.FARAPI", false) != null));
        static bool? farPresent;
        static void Log(string s) => Debug.Log("[PESpike] " + s);

        // ---------------------------------------------------------------- persistence

        public override void OnLoad(ConfigNode node)
        {
            ConfigNode d = node.GetNode("DESIGN");
            if (d != null)
            {
                thrustVac = SpikeStats.P(d.GetValue("thrustVac"));
                pc = SpikeStats.P(d.GetValue("pc"));
                eps = SpikeStats.P(d.GetValue("eps"));
                if (d.HasValue("useLoad")) useLoad = bool.Parse(d.GetValue("useLoad"));
            }
            ConfigNode st = node.GetNode("STATS");
            if (st != null) stats = SpikeStats.Load(st);
            if (node.HasValue("modelVersion")) loadedModelVersion = int.Parse(node.GetValue("modelVersion"), CultureInfo.InvariantCulture);

            if (HighLogic.LoadedScene == GameScenes.LOADING)
            {
                // Prefab compile: strip the placeholder model, create our transforms, build the default design
                // so the parts-list icon and the prefab drag cube show a real engine.
                StripPlaceholder();
                EnsureTransforms();
                stats = SpikeModel.Compute(Design);
                RebuildMesh();
                RebuildColliders();
                SetNodes(false);
            }
        }

        public override void OnSave(ConfigNode node)
        {
            node.AddValue("schemaVersion", SchemaVersion);
            node.AddValue("modelVersion", SpikeModel.Version);
            ConfigNode d = node.AddNode("DESIGN");
            d.AddValue("thrustVac", SpikeStats.F(thrustVac));
            d.AddValue("pc", SpikeStats.F(pc));
            d.AddValue("eps", SpikeStats.F(eps));
            d.AddValue("useLoad", useLoad);
            if (stats != null) stats.Save(node.AddNode("STATS"));
        }

        public override string GetInfo()
        {
            // Runs on the prefab after every module has loaded: push default stats into the prefab engine
            // so the part tooltip and the stock engine info agree with the generated design.
            if (stats != null) ApplyEngine(true);
            return stats == null ? "" : string.Format(CultureInfo.InvariantCulture,
                "Procedural engine (spike)\nDefault: {0:F0} kN, Isp {1:F0}/{2:F0} s", stats.maxThrust, stats.ispVac, stats.ispSL);
        }

        // ---------------------------------------------------------------- lifecycle

        public override void OnStart(StartState state)
        {
            var sw = Stopwatch.StartNew();
            EnsureTransforms();
            if (HighLogic.LoadedSceneIsEditor || stats == null)
            {
                if (!HighLogic.LoadedSceneIsEditor) Log("WARN: no STATS in flight, recomputing from DESIGN");
                stats = SpikeModel.Compute(Design);   // editor: always recompute from DESIGN with the current model
            }
            // Synchronous: geometry, colliders and nodes exist before the first physics frame.
            RebuildMesh();
            RebuildColliders();
            SetNodes(false);
            ApplyEngine(useLoad);
            UpdateReadouts();
            sw.Stop();
            lastBuildMs = sw.Elapsed.TotalMilliseconds;
            Log($"OnStart {state} part={part.persistentId} {Design} build={lastBuildMs:F2}ms verts={staticMF.sharedMesh.vertexCount + bellMF.sharedMesh.vertexCount}");

            if (HighLogic.LoadedSceneIsEditor)
            {
                foreach (string f in new[] { "thrustVac", "pc", "eps", "useLoad" })
                    Fields[f].uiControlEditor.onFieldChanged += OnDesignFieldChanged;
            }
            if (HighLogic.LoadedSceneIsFlight) StartCoroutine(FlightPostStart());
        }

        void OnDesignFieldChanged(BaseField field, object oldValue)
        {
            if (Equals(field.GetValue(this), oldValue)) return;
            MarkDesignChanged();
        }

        /// <summary>The PAW path, also used by the test driver to simulate slider drags.</summary>
        public void MarkDesignChanged()
        {
            designDirty = true;
            lastChange = Time.realtimeSinceStartup;
        }

        void Update()
        {
            if (!HighLogic.LoadedSceneIsEditor) return;
            float now = Time.realtimeSinceStartup;
            if (designDirty)
            {
                // Fast path (every change): pure maths + engine module + readouts
                designDirty = false;
                stats = SpikeModel.Compute(Design);
                ApplyEngine(useLoad);
                UpdateReadouts();
                meshDirty = slowDirty = true;
            }
            if (meshDirty && now - lastMesh >= 0.1f)
            {
                // Render mesh at most 10 Hz while dragging
                meshDirty = false;
                lastMesh = now;
                RebuildMesh();
            }
            if (slowDirty && now - lastChange >= 0.5f)
            {
                slowDirty = false;
                SlowRebuild();
            }
        }

        void SlowRebuild()
        {
            if (meshDirty) { meshDirty = false; RebuildMesh(); }
            RebuildColliders();
            SetNodes(true);
            if (FarPresent) part.SendMessage("GeometryPartModuleRebuildMeshData", SendMessageOptions.DontRequireReceiver);
            else RenderDragCube();
            slowRebuilds++;
            if (HighLogic.LoadedSceneIsEditor && EditorLogic.fetch != null)
            {
                GameEvents.onEditorShipModified.Fire(EditorLogic.fetch.ship);
                EditorLogic.fetch.SetBackup();
            }
            Log($"SlowRebuild #{slowRebuilds} {Design} De={stats.ExitDiameter:F3} far={FarPresent}");
        }

        IEnumerator FlightPostStart()
        {
            // Wait for the vessel to be ready (off rails) before rendering drag cubes / touching Waterfall.
            while (vessel == null || vessel.packed || !FlightGlobals.ready) yield return null;
            yield return null;
            if (FarPresent) part.SendMessage("GeometryPartModuleRebuildMeshData", SendMessageOptions.DontRequireReceiver);
            else RenderDragCube();
            yield return StartCoroutine(ApplyWaterfall());
        }

        /// <summary>Test hook: change the design in flight and run the full rebuild (FAR / drag cube / Waterfall).</summary>
        public IEnumerator FlightRedesign(SpikeDesign d, bool notifyAero = true)
        {
            thrustVac = d.thrustVac; pc = d.pc; eps = d.eps;
            stats = SpikeModel.Compute(Design);
            RebuildMesh();
            RebuildColliders();
            SetNodes(false);
            ApplyEngine(useLoad);
            UpdateReadouts();
            if (notifyAero)
            {
                if (FarPresent) part.SendMessage("GeometryPartModuleRebuildMeshData", SendMessageOptions.DontRequireReceiver);
                else RenderDragCube();
            }
            yield return StartCoroutine(ApplyWaterfall());
        }

        // ---------------------------------------------------------------- engine

        public void ApplyEngine(bool viaLoad)
        {
            var engine = part.Modules.OfType<ModuleEnginesFX>().FirstOrDefault();
            if (engine == null || stats == null) return;
            float g = engine.g;
            if (viaLoad)
            {
                var n = new ConfigNode("MODULE");
                n.AddValue("name", engine.moduleName);
                n.AddValue("persistentId", engine.PersistentId);   // PartModule.Load would otherwise zero it
                n.AddValue("maxThrust", SpikeStats.F(stats.maxThrust));
                n.AddValue("minThrust", SpikeStats.F(stats.minThrust));
                n.AddValue("heatProduction", "200");
                n.AddValue("useThrustCurve", "False");
                foreach (Propellant p in engine.propellants.ToList())
                {
                    ConfigNode pn = n.AddNode("PROPELLANT");
                    pn.AddValue("name", p.name);
                    pn.AddValue("ratio", SpikeStats.F(p.ratio));
                    pn.AddValue("DrawGauge", p.drawStackGauge);
                }
                ConfigNode c = n.AddNode("atmosphereCurve");
                for (int i = 0; i < stats.curveP.Length; i++)
                    c.AddValue("key", SpikeStats.F(stats.curveP[i]) + " " + SpikeStats.F(stats.curveIsp[i]));
                engine.Load(n);
            }
            else
            {
                engine.maxThrust = stats.maxThrust;
                engine.minThrust = stats.minThrust;
                var curve = new FloatCurve();
                for (int i = 0; i < stats.curveP.Length; i++) curve.Add(stats.curveP[i], stats.curveIsp[i]);
                engine.atmosphereCurve = curve;
                engine.SetupPropellant();
            }
            // Always set the fuel flows explicitly, whatever Load did.
            engine.maxFuelFlow = stats.maxThrust / (stats.ispVac * g);
            engine.minFuelFlow = stats.minThrust / (stats.ispVac * g);
            engine.throttleMin = engine.minFuelFlow / engine.maxFuelFlow;

            var gimbal = part.Modules.OfType<ModuleGimbal>().FirstOrDefault();
            if (gimbal != null) gimbal.gimbalRange = 5f;
            engineApplies++;
            lastApplyMode = viaLoad ? "Load" : "Direct";
        }

        void UpdateReadouts()
        {
            ispReadout = string.Format(CultureInfo.InvariantCulture, "{0:F1} / {1:F1} s", stats.ispVac, stats.ispSL);
            sizeReadout = string.Format(CultureInfo.InvariantCulture, "{0:F2} m, L {1:F2} m", stats.ExitDiameter, -stats.YExit);
        }

        public float GetModuleMass(float defaultMass, ModifierStagingSituation sit) => stats == null ? 0 : stats.mass - defaultMass;
        public ModifierChangeWhen GetModuleMassChangeWhen() => ModifierChangeWhen.FIXED;
        public float GetModuleCost(float defaultCost, ModifierStagingSituation sit) => stats == null ? 0 : stats.cost - defaultCost;
        public ModifierChangeWhen GetModuleCostChangeWhen() => ModifierChangeWhen.FIXED;

        // ---------------------------------------------------------------- geometry

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
                staticMF = MakeRenderer(root, "peChamber");
                // Gimbal pivot oriented like a stock gimbal transform (+Z down the exhaust): ModuleGimbal
                // actuates about its local X and Y. The bell mesh counter-rotates back to the part frame.
                gimbalT = new GameObject(GimbalName).transform;
                gimbalT.SetParent(root, false);
                gimbalT.localRotation = Quaternion.Euler(90f, 0f, 0f);
                bellMF = MakeRenderer(gimbalT, "peBell");
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
                gimbalT = root.Find(GimbalName);
                bellMF = gimbalT.Find("peBell").GetComponent<MeshFilter>();
                thrustT = gimbalT.Find(ThrustName);
            }
            // Instantiate() copies serialized state only, so a clone (prefab -> part, or part -> editor copy)
            // starts with ownMeshes = false and still points at its source's Mesh objects: give it its own.
            bool fresh = HighLogic.LoadedScene != GameScenes.LOADING && !ownMeshes;
            if (fresh)
            {
                staticMF.sharedMesh = new Mesh { name = "peChamber" };
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

        MeshFilter MakeRenderer(Transform parent, string name)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var mf = go.AddComponent<MeshFilter>();
            mf.sharedMesh = new Mesh { name = name };
            var mr = go.AddComponent<MeshRenderer>();
            if (outerMat == null)
            {
                Shader sh = Shader.Find("KSP/Specular") ?? Shader.Find("Standard");
                outerMat = new Material(sh) { color = new Color(0.62f, 0.45f, 0.32f) };   // copper-ish
                innerMat = new Material(sh) { color = new Color(0.08f, 0.07f, 0.07f) };   // soot
                chamberMat = new Material(sh) { color = new Color(0.55f, 0.56f, 0.58f) };  // steel
            }
            mr.sharedMaterials = name == "peBell" ? new[] { outerMat, innerMat } : new[] { chamberMat };
            return mf;
        }

        public void RebuildMesh()
        {
            int seg = Mathf.Clamp(Mathf.CeilToInt(2 * Mathf.PI * stats.re / 0.08f), 16, 64);
            NozzleMesh.Lathe(staticMF.sharedMesh, NozzleMesh.StaticProfile(stats), seg, 0f);
            List<ProfilePoint> bell = NozzleMesh.BellProfile(stats, out _, out int innerStart);
            NozzleMesh.Lathe(bellMF.sharedMesh, bell, seg, stats.YThroat, innerStart);
            gimbalT.localPosition = new Vector3(0, stats.YThroat, 0);   // pivot at the throat plane
            thrustT.localPosition = new Vector3(0, 0, stats.YThroat - stats.YExit);   // exit plane, along gimbal +Z
            renderRebuilds++;
        }

        public void RebuildColliders()
        {
            float midT = 0.5f, u = 1 - midT;
            float xMid = u * u * stats.xN + 2 * u * midT * stats.qx + midT * midT * stats.ln;
            float rMid = u * u * stats.rN + 2 * u * midT * stats.qr + midT * midT * stats.re;
            float rConv = 1.4393f * stats.rt;
            NozzleMesh.Frustum(colliders[0].sharedMesh, stats.rc, 0f, rConv, stats.YOf(-1.0607f * stats.rt));
            NozzleMesh.Frustum(colliders[1].sharedMesh, stats.rt, stats.YThroat, rMid, stats.YOf(xMid));
            NozzleMesh.Frustum(colliders[2].sharedMesh, rMid, stats.YOf(xMid), stats.re, stats.YExit);
            foreach (MeshCollider mc in colliders)
            {
                Mesh m = mc.sharedMesh;
                mc.sharedMesh = null;   // force PhysX to re-cook the convex hull
                mc.sharedMesh = m;
            }
        }

        /// <summary>Absolute node positions from the design; push (or follow) the attached parts by the change.</summary>
        public void SetNodes(bool push)
        {
            SetNode("top", Vector3.zero, push);
            SetNode("bottom", new Vector3(0, stats.YExit, 0), push);
            if (part.srfAttachNode != null)
            {
                part.srfAttachNode.position = part.srfAttachNode.originalPosition = new Vector3(0, stats.YOf(stats.xCone - 0.5f * stats.lc), stats.rc);
            }
        }

        void SetNode(string id, Vector3 pos, bool push)
        {
            AttachNode n = part.FindAttachNode(id);
            if (n == null) return;
            Vector3 oldWorld = part.transform.TransformPoint(n.position);
            n.position = pos;
            n.originalPosition = pos;
            if (!push || n.attachedPart == null) return;
            Vector3 delta = part.transform.TransformPoint(pos) - oldWorld;
            if (delta.sqrMagnitude < 1e-12f) return;
            if (n.attachedPart == part.parent)
            {
                // Attached to our parent through this node: we move (with our subtree) so the joint stays closed.
                part.transform.position -= delta;
                Log($"node {id}: moved self by {-delta.y:F4}");
            }
            else
            {
                n.attachedPart.transform.position += delta;
                Log($"node {id}: pushed {n.attachedPart.partInfo.name} by {delta.y:F4}");
            }
        }

        // ---------------------------------------------------------------- integrations

        public void RenderDragCube()
        {
            if (DragCubeSystem.Instance == null) return;
            DragCube cube = DragCubeSystem.Instance.RenderProceduralDragCube(part);
            part.DragCubes.ClearCubes();
            part.DragCubes.Cubes.Add(cube);
            part.DragCubes.ResetCubeWeights();
            part.DragCubes.ForceUpdate(true, true, false);
            part.DragCubes.SetDragWeights();
            Log("drag cube: area=[" + string.Join(",", cube.Area.Select(a => a.ToString("F3", CultureInfo.InvariantCulture))) + "]");
        }

        IEnumerator ApplyWaterfall()
        {
            PartModule wf = part.Modules.Cast<PartModule>().FirstOrDefault(m => m.moduleName == "ModuleWaterfallFX");
            if (wf == null) { waterfallStatus = "Waterfall absent"; yield break; }
            var startedF = wf.GetType().GetField("started", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
            for (int i = 0; i < 300 && startedF != null && !(bool)startedF.GetValue(wf); i++) yield return null;
            float scale = stats.ExitDiameter / waterfallReferenceDiameter;
            var n = new ConfigNode("MODULE");
            ConfigNode t = n.AddNode("TEMPLATE");
            t.AddValue("templateName", waterfallTemplate);
            t.AddValue("overrideParentTransform", ThrustName);
            t.AddValue("position", "0,0,0");
            t.AddValue("rotation", "0,0,0");
            string sc = SpikeStats.F(scale);
            t.AddValue("scale", sc + "," + sc + "," + sc);
            var sw = Stopwatch.StartNew();
            try
            {
                wf.OnLoad(n);   // replaces TEMPLATE nodes in the module config and re-initializes effects
            }
            catch (Exception e)
            {
                waterfallStatus = "OnLoad threw " + e.GetType().Name + ": " + e.Message;
                Log("Waterfall: " + waterfallStatus);
                yield break;
            }
            sw.Stop();
            var fx = wf.GetType().GetProperty("FX")?.GetValue(wf) as IList;
            waterfallStatus = $"template={waterfallTemplate} scale={scale:F3} effects={fx?.Count ?? -1} reinit={sw.Elapsed.TotalMilliseconds:F1}ms";
            Log("Waterfall: " + waterfallStatus);
        }
    }
}
