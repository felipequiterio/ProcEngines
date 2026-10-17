using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using UnityEngine;
using KSP.UI.Screens;
using Debug = UnityEngine.Debug;

namespace ProcEngineSpike
{
    /// <summary>
    /// Self-driving test harness. Active only if GameData/ProcEngineSpike/PluginData/autotest.txt exists;
    /// its first line names the script (local, rnd, lmpA, lmpB). Results go to PluginData/results_*.txt,
    /// screenshots to PluginData/shots/, then the game quits.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.Instantly, true)]
    public class SpikeDriver : MonoBehaviour
    {
        static string Dir => Path.Combine(KSPUtil.ApplicationRootPath, "GameData/ProcEngineSpike/PluginData");
        static string FlagDir => Path.GetFullPath(Path.Combine(KSPUtil.ApplicationRootPath, "../_dev/ProceduralEngines/spike/run/flags"));
        string script;
        StreamWriter results;
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        void Awake()
        {
            string f = Path.Combine(Dir, "autotest.txt");
            if (!File.Exists(f)) { Destroy(this); return; }
            DontDestroyOnLoad(gameObject);
            script = File.ReadAllLines(f)[0].Trim();
            Directory.CreateDirectory(Path.Combine(Dir, "shots"));
            results = new StreamWriter(Path.Combine(Dir, "results_" + script + ".txt"), false) { AutoFlush = true };
            Info("start", $"script={script} culture={CultureInfo.CurrentCulture.Name} ui={CultureInfo.CurrentUICulture.Name} far={ModuleProcEngineSpike.FarPresent}");
            StartCoroutine(Run());
        }

        // ------------------------------------------------------------------ reporting

        void Line(string s) { Debug.Log("[PESpike] " + s); results?.WriteLine(DateTime.Now.ToString("HH:mm:ss ", Inv) + s); }
        void Info(string tag, string msg) => Line($"INFO  [{tag}] {msg}");
        void Check(string id, bool pass, string msg) => Line($"{(pass ? "PASS" : "FAIL")}  [{id}] {msg}");
        static string V(Vector3 v) => string.Format(Inv, "({0:F4},{1:F4},{2:F4})", v.x, v.y, v.z);
        static bool Near(double a, double b, double rel = 0.005) => Math.Abs(a - b) <= rel * Math.Max(Math.Abs(b), 1e-6);

        IEnumerator Run()
        {
            while (HighLogic.LoadedScene != GameScenes.MAINMENU) yield return null;
            yield return new WaitForSecondsRealtime(4f);
            IEnumerator body;
            switch (script)
            {
                case "local": body = LocalScript(); break;
                case "rnd": body = RndScript(); break;
                case "lmpA": body = LmpAScript(); break;
                case "lmpB": body = LmpBScript(); break;
                default: Info("start", "unknown script"); body = null; break;
            }
            if (body != null) yield return StartCoroutine(Guard(body));
            Info("end", "quitting");
            results.Close();
            yield return new WaitForSecondsRealtime(1f);
            Application.Quit();
        }

        // Runs a coroutine, logging exceptions instead of silently dying.
        IEnumerator Guard(IEnumerator e)
        {
            var stack = new Stack<IEnumerator>();
            stack.Push(e);
            while (stack.Count > 0)
            {
                IEnumerator top = stack.Peek();
                bool moved;
                try { moved = top.MoveNext(); }
                catch (Exception ex) { Line("ERROR " + ex); yield break; }
                if (!moved) { stack.Pop(); continue; }
                if (top.Current is IEnumerator nested) stack.Push(nested);
                else yield return top.Current;
            }
        }

        // ------------------------------------------------------------------ scene helpers

        IEnumerator WaitScene(GameScenes s, float timeout = 600f)
        {
            float t0 = Time.realtimeSinceStartup;
            while (HighLogic.LoadedScene != s && Time.realtimeSinceStartup - t0 < timeout) yield return null;
            yield return new WaitForSecondsRealtime(2f);
        }

        IEnumerator Shot(string name)
        {
            yield return null;
            string p = Path.Combine(Dir, "shots", script + "_" + name + ".png");
            ScreenCapture.CaptureScreenshot(p);
            yield return null; yield return null;
            yield return new WaitForSecondsRealtime(0.5f);
            Info("shot", p);
        }

        IEnumerator NewGame(string name, Game.Modes mode)
        {
            GameParameters par = GameParameters.GetDefaultParameters(mode, GameParameters.Preset.Normal);
            Game g = GamePersistence.CreateNewGame(name, mode, par, "Squad/Flags/default", GameScenes.SPACECENTER, EditorFacility.VAB);
            HighLogic.CurrentGame = g;
            HighLogic.SaveFolder = name;
            GamePersistence.SaveGame(g, "persistent", name, SaveMode.OVERWRITE);
            g.Start();
            yield return WaitScene(GameScenes.SPACECENTER);
            yield return new WaitForSecondsRealtime(2f);
            Info("game", $"{name} {mode} started, scene={HighLogic.LoadedScene}");
        }

        IEnumerator EnterEditor()
        {
            EditorDriver.StartEditor(EditorFacility.VAB);
            yield return WaitScene(GameScenes.EDITOR);
            while (EditorLogic.fetch == null || EditorLogic.fetch.ship == null) yield return null;
            yield return new WaitForSecondsRealtime(2f);
        }

        IEnumerator LoadCraft(string path)
        {
            EditorLogic.LoadShipFromFile(path);
            yield return new WaitForSecondsRealtime(2.5f);
        }

        static ModuleProcEngineSpike Spike(IEnumerable<Part> parts) =>
            parts.Select(p => p.FindModuleImplementing<ModuleProcEngineSpike>()).FirstOrDefault(m => m != null);

        static void SetDesign(ModuleProcEngineSpike m, float F, float pc, float eps, bool? useLoad = null)
        {
            m.thrustVac = F; m.pc = pc; m.eps = eps;
            if (useLoad.HasValue) m.useLoad = useLoad.Value;
            m.MarkDesignChanged();
        }

        // ------------------------------------------------------------------ craft files

        class CP
        {
            public string name; public int parent = -1; public string pNode, myNode; public bool srf;
            public Vector3 srfPos; public Quaternion rot = Quaternion.identity; public int istg;
            public CP(string n) { name = n; }
        }

        static string FirstAvailable(params string[] names) => names.FirstOrDefault(n => PartLoader.getPartInfoByName(n) != null);

        string WriteCraft(string ship, List<CP> parts)
        {
            var pos = new Vector3[parts.Count];
            var cid = parts.Select((p, i) => (uint)(4200000000u + i * 17 + 3)).ToArray();
            string Id(int i) => parts[i].name + "_" + cid[i];
            for (int i = 0; i < parts.Count; i++)
            {
                CP c = parts[i];
                Part pf = PartLoader.getPartInfoByName(c.name).partPrefab;
                if (c.parent < 0) { pos[i] = new Vector3(0, 12, 0); continue; }
                Part pp = PartLoader.getPartInfoByName(parts[c.parent].name).partPrefab;
                if (c.srf) pos[i] = pos[c.parent] + c.srfPos - c.rot * pf.srfAttachNode.position;
                else pos[i] = pos[c.parent] + pp.FindAttachNode(c.pNode).position - pf.FindAttachNode(c.myNode).position;
            }
            var root = new ConfigNode();
            root.AddValue("ship", ship);
            root.AddValue("version", "1.12.5");
            root.AddValue("description", "ProcEngines spike test craft");
            root.AddValue("type", "VAB");
            root.AddValue("size", "2,6,2");
            root.AddValue("steamPublishedFileId", "0");
            root.AddValue("persistentId", "1234567");
            root.AddValue("rot", "0,0,0,1");
            root.AddValue("missionFlag", "Squad/Flags/default");
            root.AddValue("vesselType", "Probe");
            for (int i = 0; i < parts.Count; i++)
            {
                CP c = parts[i];
                Part pf = PartLoader.getPartInfoByName(c.name).partPrefab;
                ConfigNode n = root.AddNode("PART");
                n.AddValue("part", Id(i));
                n.AddValue("partName", "Part");
                n.AddValue("persistentId", (1000 + i).ToString(Inv));
                n.AddValue("pos", KSPUtil.WriteVector(pos[i]));
                n.AddValue("attPos", "0,0,0");
                n.AddValue("attPos0", KSPUtil.WriteVector(c.parent < 0 ? pos[i] : pos[i] - pos[c.parent]));
                n.AddValue("rot", KSPUtil.WriteQuaternion(c.rot));
                n.AddValue("attRot", "0,0,0,1");
                n.AddValue("attRot0", KSPUtil.WriteQuaternion(c.rot));
                n.AddValue("mir", "1,1,1");
                n.AddValue("symMethod", "Radial");
                n.AddValue("autostrutMode", "Off");
                n.AddValue("rigidAttachment", "False");
                n.AddValue("istg", c.istg.ToString(Inv));
                n.AddValue("resPri", "0");
                n.AddValue("dstg", "0");
                n.AddValue("sidx", "-1");
                n.AddValue("sqor", "-1");
                n.AddValue("sepI", "-1");
                n.AddValue("attm", c.srf ? "1" : "0");
                n.AddValue("modCost", "0");
                n.AddValue("modMass", "0");
                n.AddValue("modSize", "0,0,0");
                for (int k = 0; k < parts.Count; k++)
                    if (parts[k].parent == i) n.AddValue("link", Id(k));
                // attN on both ends of every stack joint
                for (int k = 0; k < parts.Count; k++)
                {
                    CP o = parts[k];
                    if (o.srf) continue;
                    if (k == i && o.parent >= 0)
                        n.AddValue("attN", o.myNode + "," + Id(o.parent) + "_0|" + NodeY(pf, o.myNode) + "|0");
                    if (o.parent == i)
                        n.AddValue("attN", o.pNode + "," + Id(k) + "_0|" + NodeY(pf, o.pNode) + "|0");
                }
                if (c.srf) n.AddValue("srfN", "srfAttach," + Id(c.parent));
                n.AddNode("EVENTS"); n.AddNode("ACTIONS"); n.AddNode("PARTDATA");
            }
            string dir = Path.Combine(KSPUtil.ApplicationRootPath, "saves", HighLogic.SaveFolder, "Ships", "VAB");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, ship + ".craft");
            root.Save(path);
            Info("craft", $"{ship}: {string.Join(" > ", parts.Select(p => p.name))}");
            return path;
        }

        static string NodeY(Part pf, string id) => pf.FindAttachNode(id).position.y.ToString("R", Inv);

        List<CP> StackCraft(bool rootAtBottom)
        {
            string probe = FirstAvailable("probeCoreOcto", "probeCoreOcto.v2", "mk1pod.v2");
            string tank = FirstAvailable("fuelTank", "fuelTank.long");
            string dec = FirstAvailable("Decoupler.1", "Decoupler_1");
            if (!rootAtBottom)
                return new List<CP>
                {
                    new CP(probe),
                    new CP(tank) { parent = 0, pNode = "bottom", myNode = "top" },
                    new CP("procEngineSpike") { parent = 1, pNode = "bottom", myNode = "top", istg = 1 },
                    new CP(dec) { parent = 2, pNode = "bottom", myNode = "top", istg = 0 },
                    new CP(tank) { parent = 3, pNode = "bottom", myNode = "top" },
                };
            return new List<CP>
            {
                new CP(tank),                                                               // root, bottom
                new CP(dec) { parent = 0, pNode = "top", myNode = "bottom" },
                new CP("procEngineSpike") { parent = 1, pNode = "top", myNode = "bottom" },   // attached by its nozzle to the parent
                new CP(tank) { parent = 2, pNode = "top", myNode = "bottom" },
                new CP(probe) { parent = 3, pNode = "top", myNode = "bottom" },
            };
        }

        List<CP> SingleStage(string engine, string probe = null)
        {
            probe = probe ?? FirstAvailable("probeCoreOcto", "probeCoreOcto.v2", "mk1pod.v2");
            string tank = FirstAvailable("fuelTank", "fuelTank.long");
            return new List<CP>
            {
                new CP(probe),
                new CP(tank) { parent = 0, pNode = "bottom", myNode = "top" },
                new CP(engine) { parent = 1, pNode = "bottom", myNode = "top" },
            };
        }

        List<CP> WingCraft(string engine)
        {
            var l = SingleStage(engine);
            string wing = FirstAvailable("wingConnector3", "wingConnector", "structuralWing", "deltaWing");
            Part tankPf = PartLoader.getPartInfoByName(l[1].name).partPrefab;
            float r = 0.625f;
            l.Add(new CP(wing) { parent = 1, srf = true, srfPos = new Vector3(r, 0, 0), rot = Quaternion.Euler(0, 0, 0) });
            l.Add(new CP(wing) { parent = 1, srf = true, srfPos = new Vector3(-r, 0, 0), rot = Quaternion.Euler(0, 180, 0) });
            return l;
        }

        // ------------------------------------------------------------------ check 1: mesh + colliders

        IEnumerator CheckMesh(ModuleProcEngineSpike m, string tag)
        {
            Transform root = m.part.transform.Find("model/" + ModuleProcEngineSpike.RootName);
            var mfs = root.GetComponentsInChildren<MeshFilter>().Where(f => f.name == "peChamber" || f.name == "peBell").ToArray();
            int verts = mfs.Sum(f => f.sharedMesh.vertexCount), tris = mfs.Sum(f => f.sharedMesh.triangles.Length / 3);
            float maxDev = 0, medDev = 0;
            foreach (MeshFilter mf in mfs)
            {
                float mx = NozzleMesh.NormalDeviation(mf.sharedMesh, out float med);
                maxDev = Math.Max(maxDev, mx); medDev = Math.Max(medDev, med);
            }
            Info("1." + tag, $"render meshes={mfs.Length} verts={verts} tris={tris} OnStart build={m.lastBuildMs:F2}ms; analytic vs RecalculateNormals deviation median={medDev:F2}deg max={maxDev:F2}deg (smooth vertices)");
            var cols = root.GetComponentsInChildren<MeshCollider>();
            bool colOk = cols.Length == 3 && cols.All(c => c.convex && c.sharedMesh.triangles.Length / 3 < 255 && c.sharedMesh != mfs[0].sharedMesh);
            Info("1." + tag, "colliders: " + string.Join("; ", cols.Select(c => $"{c.name} convex={c.convex} tris={c.sharedMesh.triangles.Length / 3} size={V(c.bounds.size)}")));
            // ray from below along the axis must hit our hull at the exit plane
            Vector3 exitW = m.part.transform.TransformPoint(new Vector3(0, m.stats.YExit, 0));
            Vector3 down = -m.part.transform.up;
            // ray from below along the axis must hit our hull at the exit plane (test our colliders only:
            // in flight the launch pad is below the nozzle)
            var ray = new Ray(exitW + down * 5f, -down);
            RaycastHit h = default; bool hit = false;
            foreach (MeshCollider c in root.GetComponentsInChildren<MeshCollider>())
                if (c.Raycast(ray, out RaycastHit hh, 10f) && (!hit || hh.distance < h.distance)) { h = hh; hit = true; }
            bool hitOurs = hit;
            Check("1." + tag, verts > 0 && colOk && hitOurs && maxDev < 30f,
                $"mesh+colliders built synchronously; raycast hit {(hit ? h.collider.name : "nothing")} at {(hit ? (5f - h.distance).ToString("F4", Inv) : "-")} m from exit plane");
            var engine = m.part.FindModuleImplementing<ModuleEnginesFX>();
            var gimbal = m.part.FindModuleImplementing<ModuleGimbal>();
            Transform tt = engine.thrustTransforms.FirstOrDefault();
            Info("1." + tag, $"engine thrustTransforms={engine.thrustTransforms.Count} first={tt?.name} parent={tt?.parent?.name} worldPos-exit={(tt == null ? "-" : V(tt.position - exitW))} fwd·down={(tt == null ? 0 : Vector3.Dot(tt.forward, down)):F3}; gimbalTransforms={gimbal?.gimbalTransforms?.Count} ({gimbal?.gimbalTransforms?.FirstOrDefault()?.name})");
            yield break;
        }

        // ------------------------------------------------------------------ check 2: readouts

        Type FindType(string full)
        {
            foreach (var a in AssemblyLoader.loadedAssemblies)
            {
                try { Type t = a.assembly.GetType(full); if (t != null) return t; } catch { }
            }
            return null;
        }

        IEnumerator CheckReadouts(ModuleProcEngineSpike m, IList<Part> parts, string tag, bool editor)
        {
            var engine = m.part.FindModuleImplementing<ModuleEnginesFX>();
            SpikeStats s = m.stats;
            float g = engine.g;
            bool fieldsOk = Near(engine.maxThrust, s.maxThrust, 1e-5) && Near(engine.minThrust, s.minThrust, 1e-5)
                && Near(engine.atmosphereCurve.Evaluate(0f), s.ispVac, 1e-4) && Near(engine.atmosphereCurve.Evaluate(1f), s.ispSL, 1e-4)
                && Near(engine.maxFuelFlow, s.maxThrust / (s.ispVac * g), 1e-5) && Near(engine.minFuelFlow, s.minThrust / (s.ispVac * g), 1e-5);
            Check("2." + tag + ".fields", fieldsOk,
                $"mode={m.lastApplyMode} maxThrust={engine.maxThrust:F2} (exp {s.maxThrust:F2}) minThrust={engine.minThrust:F2} Isp0={engine.atmosphereCurve.Evaluate(0f):F2} (exp {s.ispVac:F2}) Isp1={engine.atmosphereCurve.Evaluate(1f):F2} (exp {s.ispSL:F2}) maxFuelFlow={engine.maxFuelFlow:F5} minFuelFlow={engine.minFuelFlow:F5} throttleMin={engine.throttleMin:F3} props={string.Join("+", engine.propellants.Select(p => p.name + ":" + p.ratio))} ratioSum={engine.ratioSum:F2} mixDensity={engine.mixtureDensity:F5} pid={engine.PersistentId}");

            // stock dV app
            VesselDeltaV dv = editor ? EditorLogic.fetch.ship.vesselDeltaV : FlightGlobals.ActiveVessel?.VesselDeltaV;
            if (dv != null)
            {
                dv.SetCalcsDirty(true);
                float t0 = Time.realtimeSinceStartup;
                while (Time.realtimeSinceStartup - t0 < 3f) yield return null;
                var st = dv.OperatingStageInfo?.Where(x => x.thrustVac > 0).ToList() ?? new List<DeltaVStageInfo>();
                var best = st.OrderBy(x => Math.Abs(x.thrustVac - s.maxThrust)).FirstOrDefault();
                Check("2." + tag + ".stockDV", best != null && Near(best.thrustVac, s.maxThrust, 0.01) && Near(best.ispVac, s.ispVac, 0.01) && Near(best.ispASL, s.ispSL, 0.02),
                    best == null ? $"no stage with thrust (ready={dv.IsReady}, stages={dv.OperatingStageInfo?.Count})" :
                    $"stage {best.stage}: thrustVac={best.thrustVac:F2} ispVac={best.ispVac:F2} ispASL={best.ispASL:F2} dVvac={best.deltaVinVac:F1} TWRvac={best.TWRVac:F2}");
            }
            else Info("2." + tag + ".stockDV", "no VesselDeltaV");

            // KER
            Type sim = FindType("KerbalEngineer.VesselSimulator.SimManager");
            if (sim != null && editor)
            {
                sim.GetProperty("Gravity")?.SetValue(null, 9.81);
                sim.GetProperty("Atmosphere")?.SetValue(null, 0.0);
                object[] stages = null;
                float t0 = Time.realtimeSinceStartup;
                while (Time.realtimeSinceStartup - t0 < 6f)
                {
                    sim.GetMethod("RequestSimulation")?.Invoke(null, null);
                    sim.GetMethod("TryStartSimulation")?.Invoke(null, null);
                    yield return new WaitForSecondsRealtime(0.5f);
                    if ((bool)sim.GetMethod("ResultsReady").Invoke(null, null))
                    {
                        stages = (sim.GetProperty("Stages").GetValue(null) as Array)?.Cast<object>().ToArray();
                        if (stages != null && stages.Length > 0) break;
                    }
                }
                if (stages != null)
                {
                    string Fld(object o, string f) => Convert.ToDouble(o.GetType().GetField(f).GetValue(o)).ToString("F2", Inv);
                    var withThrust = stages.Where(o => Convert.ToDouble(o.GetType().GetField("thrust").GetValue(o)) > 0).ToList();
                    object bst = withThrust.OrderBy(o => Math.Abs(Convert.ToDouble(o.GetType().GetField("thrust").GetValue(o)) - s.maxThrust)).FirstOrDefault();
                    bool ok = bst != null && Near(Convert.ToDouble(bst.GetType().GetField("thrust").GetValue(bst)), s.maxThrust, 0.01)
                              && Near(Convert.ToDouble(bst.GetType().GetField("isp").GetValue(bst)), s.ispVac, 0.01);
                    Check("2." + tag + ".KER", ok, "stages: " + string.Join(" | ", stages.Select(o => $"#{o.GetType().GetField("number").GetValue(o)} thrust={Fld(o, "thrust")} isp={Fld(o, "isp")} dV={Fld(o, "deltaV")}")) + $" (atm={sim.GetProperty("Atmosphere").GetValue(null)})");
                }
                else Check("2." + tag + ".KER", false, "KER simulation produced no stages: " + sim.GetProperty("failMessage")?.GetValue(null));
            }
            else Info("2." + tag + ".KER", sim == null ? "KER not installed" : "skipped in flight (needs KER part/flight engineer)");

            // MechJeb
            PartModule core = parts.SelectMany(p => p.Modules.Cast<PartModule>()).FirstOrDefault(pm => pm.moduleName == "MechJebCore");
            if (core != null)
            {
                object ss = core.GetType().GetMethod("GetComputerModule", new[] { typeof(string) })?.Invoke(core, new object[] { "MechJebModuleStageStats" });
                if (ss != null)
                {
                    // MechJeb's own VAB dV window sets EditorBody; without it the editor simulation never starts.
                    FieldInfo eb = ss.GetType().GetField("EditorBody");
                    if (editor && eb != null && eb.GetValue(ss) == null) eb.SetValue(ss, FlightGlobals.GetHomeBody());
                    MethodInfo req = ss.GetType().GetMethod("RequestUpdate", Type.EmptyTypes);
                    IList vac = null;
                    float t0 = Time.realtimeSinceStartup;
                    while (Time.realtimeSinceStartup - t0 < 6f)
                    {
                        req?.Invoke(ss, null);
                        yield return new WaitForSecondsRealtime(0.5f);
                        vac = ss.GetType().GetField("VacStats")?.GetValue(ss) as IList;
                        // wait for a result for THIS design (MechJeb keeps its last result until the re-simulation lands)
                        if (vac != null && vac.Cast<object>().Any(o => Near(Convert.ToDouble(o.GetType().GetField("Thrust").GetValue(o)), s.maxThrust, 0.01))) break;
                    }
                    IList atmo = ss.GetType().GetField("AtmoStats")?.GetValue(ss) as IList;
                    double D(object o, string f) => Convert.ToDouble(o.GetType().GetField(f).GetValue(o));
                    var stats = vac?.Cast<object>().Where(o => D(o, "Thrust") > 0).ToList() ?? new List<object>();
                    object bst = stats.OrderBy(o => Math.Abs(D(o, "Thrust") - s.maxThrust)).FirstOrDefault();
                    Check("2." + tag + ".MechJeb", bst != null && Near(D(bst, "Thrust"), s.maxThrust, 0.01) && Near(D(bst, "Isp"), s.ispVac, 0.01),
                        "vac: " + string.Join(" | ", (vac?.Cast<object>() ?? new object[0]).Select(o => $"stg{o.GetType().GetField("KSPStage").GetValue(o)} T={D(o, "Thrust"):F2} Isp={D(o, "Isp"):F2} dV={D(o, "DeltaV"):F1}"))
                        + "; atmo: " + string.Join(" | ", (atmo?.Cast<object>() ?? new object[0]).Where(o => D(o, "Thrust") > 0).Select(o => $"T={D(o, "Thrust"):F2} Isp={D(o, "Isp"):F2}")));
                }
                else Info("2." + tag + ".MechJeb", "MechJebCore found but no MechJebModuleStageStats");
            }
            else Info("2." + tag + ".MechJeb", "no MechJebCore on the craft (MechJeb/MechJebForAll not installed)");
        }

        // ------------------------------------------------------------------ check 3: nodes

        static float Gap(Part a, string na, Part b, string nb) =>
            (a.transform.TransformPoint(a.FindAttachNode(na).position) - b.transform.TransformPoint(b.FindAttachNode(nb).position)).magnitude;

        IEnumerator CheckNodes(bool rootAtBottom)
        {
            string tag = rootAtBottom ? "rootBelow" : "rootAbove";
            yield return LoadCraft(WriteCraft("SpikeStack_" + tag, StackCraft(rootAtBottom)));
            List<Part> parts = EditorLogic.fetch.ship.parts;
            ModuleProcEngineSpike m = Spike(parts);
            Part eng = m.part;
            Part dec = eng.FindAttachNode("bottom").attachedPart;
            Part above = eng.FindAttachNode("top").attachedPart;
            if (dec == null || above == null) { Check("3." + tag, false, $"craft not attached as expected (bottom={dec?.name} top={above?.name})"); yield break; }
            string decNode = dec.FindAttachNode("top").attachedPart == eng ? "top" : "bottom";
            Info("3." + tag, $"engine parent={eng.parent?.partInfo.name} bottom->{dec.partInfo.name} top->{above.partInfo.name}; root={EditorLogic.RootPart.partInfo.name}");
            SetDesign(m, 400, 60, 30);
            yield return new WaitForSecondsRealtime(1.2f);
            Vector3 decPos0 = dec.transform.position, abovePos0 = above.transform.position, engPos0 = eng.transform.position;
            float gap0 = Gap(eng, "bottom", dec, decNode), gapTop0 = Gap(eng, "top", above, "bottom");
            float yExit0 = m.stats.YExit;
            SetDesign(m, 400, 60, 120);
            yield return new WaitForSecondsRealtime(1.2f);
            float yExit1 = m.stats.YExit;
            float gap1 = Gap(eng, "bottom", dec, decNode), gapTop1 = Gap(eng, "top", above, "bottom");
            float moved = rootAtBottom ? (eng.transform.position - engPos0).y : (dec.transform.position - decPos0).y;
            yield return Shot("stack_" + tag + "_eps120");
            // drift: toggle 6 times and return to the first design
            for (int i = 0; i < 6; i++)
            {
                SetDesign(m, i % 2 == 0 ? 900 : 150, 60 + 20 * i, i % 2 == 0 ? 15 : 150);
                yield return new WaitForSecondsRealtime(0.9f);
            }
            SetDesign(m, 400, 60, 30);
            yield return new WaitForSecondsRealtime(1.2f);
            float drift = rootAtBottom ? (above.transform.position - abovePos0).magnitude : (dec.transform.position - decPos0).magnitude;
            float gap2 = Gap(eng, "bottom", dec, decNode);
            Check("3." + tag, gap0 < 1e-3f && gap1 < 1e-3f && gapTop1 < 1e-3f && gap2 < 1e-3f && drift < 1e-3f && Math.Abs(Math.Abs(moved) - Math.Abs(yExit1 - yExit0)) < 1e-3f,
                $"nozzle-decoupler gap before={gap0:F5} after eps30->120={gap1:F5} (bottom node moved {yExit0:F3}->{yExit1:F3}; {(rootAtBottom ? "engine moved itself" : "decoupler pushed")} {moved:F4} m); top gap={gapTop0:F5}/{gapTop1:F5}; after 6 toggles back to eps30: gap={gap2:F5} drift={drift:F6} m");
        }

        // ------------------------------------------------------------------ check 6 (local part): invariant culture

        IEnumerator CheckPersistence(ModuleProcEngineSpike m, string path)
        {
            CultureInfo old = Thread.CurrentThread.CurrentCulture;
            var de = new CultureInfo("de-DE");
            // 1) our OnSave/OnLoad under a comma-decimal culture
            Thread.CurrentThread.CurrentCulture = de;
            var n = new ConfigNode("MODULE");
            m.Save(n);
            string text = n.ToString();
            ConfigNode design = n.GetNode("DESIGN"), statsN = n.GetNode("STATS");
            bool noComma = design != null && statsN != null && design.values.Cast<ConfigNode.Value>().Concat(statsN.values.Cast<ConfigNode.Value>())
                .All(v => !v.value.Contains(","));
            SpikeStats reread = SpikeStats.Load(statsN);
            float t = SpikeStats.P(design.GetValue("thrustVac"));
            Thread.CurrentThread.CurrentCulture = old;
            bool roundTrip = reread != null && reread.maxThrust == m.stats.maxThrust && reread.ispVac == m.stats.ispVac && reread.re == m.stats.re
                             && reread.curveIsp.SequenceEqual(m.stats.curveIsp) && t == m.thrustVac;
            Check("6.culture", noComma && roundTrip, $"saved under de-DE: no comma decimals={noComma}, bit-exact round trip={roundTrip}; DESIGN = {design}");
            Info("6.node", text.Replace("\n", " ").Replace("\t", ""));

            // 2) whole craft save -> load in the editor
            float thr = m.thrustVac, pcv = m.pc, epsv = m.eps, maxT = m.stats.maxThrust, yExit = m.stats.YExit;
            ConfigNode ship = EditorLogic.fetch.ship.SaveShip();
            ship.Save(path);
            yield return LoadCraft(path);
            ModuleProcEngineSpike m2 = Spike(EditorLogic.fetch.ship.parts);
            var e2 = m2.part.FindModuleImplementing<ModuleEnginesFX>();
            bool ok = m2.thrustVac == thr && m2.pc == pcv && m2.eps == epsv && e2.maxThrust == maxT && m2.part.FindAttachNode("bottom").position.y == yExit;
            Check("6.craft", ok, $"craft save/load: design {m2.Design} (exp F={thr} Pc={pcv} eps={epsv}) maxThrust={e2.maxThrust} bottomNode.y={m2.part.FindAttachNode("bottom").position.y:F4} (exp {yExit:F4}) savedModelVersion={m2.loadedModelVersion}");
        }

        // ------------------------------------------------------------------ check 4: aero

        bool FarValid(Vessel v)
        {
            Type api = FindType("FerramAerospaceResearch.FARAPI");
            return (bool)api.GetMethod("VesselVoxelizationCompletedAndValid").Invoke(null, new object[] { v });
        }

        (float lift, float drag) FarForces(Vessel v, Vector3 axis, float aoa)
        {
            Type api = FindType("FerramAerospaceResearch.FARAPI");
            Vector3 vel = Quaternion.AngleAxis(aoa, axis) * v.transform.up * 150f;
            object[] args = { v, Vector3.zero, Vector3.zero, vel, 2000.0 };
            api.GetMethod("CalculateVesselAeroForces").Invoke(null, args);
            Vector3 F = (Vector3)args[1];
            float drag = -Vector3.Dot(F, vel.normalized);
            float lift = (F + drag * vel.normalized).magnitude;
            return (lift, drag);
        }

        IEnumerator WaitFar(Vessel v, float timeout = 30f)
        {
            float t0 = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - t0 < timeout && !FarValid(v)) yield return null;
            yield return new WaitForSecondsRealtime(1f);
        }

        string FarSummary(Vessel v)
        {
            var parts = new List<string>();
            foreach (var (axis, an) in new[] { (v.transform.right, "pitch"), (v.transform.forward, "yaw") })
                foreach (float a in new[] { 0f, 5f })
                {
                    var f = FarForces(v, axis, a);
                    parts.Add($"{an}{a:F0}: L={f.lift:F2} D={f.drag:F2} kN");
                }
            return string.Join("; ", parts);
        }

        IEnumerator Launch(string craftPath, string site)
        {
            FlightDriver.StartWithNewLaunch(craftPath, "Squad/Flags/default", site, new VesselCrewManifest());
            yield return WaitScene(GameScenes.FLIGHT);
            float t0 = Time.realtimeSinceStartup;
            while ((FlightGlobals.ActiveVessel == null || FlightGlobals.ActiveVessel.packed || !FlightGlobals.ready) && Time.realtimeSinceStartup - t0 < 60) yield return null;
            yield return new WaitForSecondsRealtime(3f);
            Info("flight", $"launched {FlightGlobals.ActiveVessel?.vesselName} at {site}, parts={FlightGlobals.ActiveVessel?.parts.Count}");
        }

        // ------------------------------------------------------------------ check 5: waterfall

        static Bounds PlumeBounds(Part p)
        {
            Transform tt = p.FindModelTransform(ModuleProcEngineSpike.ThrustName);
            var rs = tt.GetComponentsInChildren<Renderer>(true).Where(r => r.enabled).ToArray();
            if (rs.Length == 0) return new Bounds(tt.position, Vector3.zero);
            Bounds b = rs[0].bounds;
            foreach (Renderer r in rs) b.Encapsulate(r.bounds);
            return b;
        }

        // ------------------------------------------------------------------ scripts

        (float lift, float drag) refPitch, refYaw;

        IEnumerator LocalScript()
        {
            yield return NewGame("pespike", Game.Modes.SANDBOX);
            yield return EnterEditor();

            // ---- 1 + 2: single-stage craft
            yield return LoadCraft(WriteCraft("SpikeDV", SingleStage("procEngineSpike")));
            List<Part> parts = EditorLogic.fetch.ship.parts;
            ModuleProcEngineSpike m = Spike(parts);
            if (m == null) { Check("load", false, "spike part not in the loaded craft"); yield break; }
            yield return CheckMesh(m, "editorDefault");
            yield return Shot("editor_default");

            // throttling: simulate a 40-frame slider drag
            int r0 = m.renderRebuilds, s0 = m.slowRebuilds, e0 = m.engineApplies;
            float t0 = Time.realtimeSinceStartup;
            for (int i = 0; i < 40; i++) { SetDesign(m, 200 + 10 * i, 60, 20 + i); yield return null; }
            float dragSecs = Time.realtimeSinceStartup - t0;
            yield return new WaitForSecondsRealtime(1.5f);
            int renders = m.renderRebuilds - r0, slows = m.slowRebuilds - s0, applies = m.engineApplies - e0;
            Check("1.throttle", renders <= Mathf.CeilToInt(dragSecs * 10) + 2 && slows == 1,
                $"40 changes over {dragSecs:F2}s -> engine applies={applies}, render rebuilds={renders} (<=10 Hz), slow rebuilds={slows} (debounced)");
            yield return CheckMesh(m, "editorDragged");

            foreach (bool viaLoad in new[] { true, false })
            {
                SetDesign(m, viaLoad ? 1000 : 650, viaLoad ? 80 : 45, viaLoad ? 30 : 12, viaLoad);
                yield return new WaitForSecondsRealtime(1.5f);
                yield return CheckReadouts(m, parts, "editor" + (viaLoad ? "Load" : "Direct"), true);
            }
            yield return Shot("editor_1000kN");

            // ---- 4 (editor side): drag cube or FAR message
            if (!ModuleProcEngineSpike.FarPresent)
            {
                SetDesign(m, 200, 60, 20, true); yield return new WaitForSecondsRealtime(1.5f);
                float[] a0 = m.part.DragCubes.Cubes.FirstOrDefault()?.Area?.ToArray();
                SetDesign(m, 1500, 60, 80, true); yield return new WaitForSecondsRealtime(1.5f);
                float[] a1 = m.part.DragCubes.Cubes.FirstOrDefault()?.Area?.ToArray();
                bool grew = a0 != null && a1 != null && a1[3] > a0[3] * 1.5f;   // index 3 = YN (looking up the nozzle)
                Check("4.dragcube.editor", grew, $"cubes={m.part.DragCubes.Cubes.Count} De {SpikeModel.Compute(new SpikeDesign { thrustVac = 200, pc = 60, eps = 20 }).ExitDiameter:F2}->{m.stats.ExitDiameter:F2} m; area before=[{(a0 == null ? "" : string.Join(",", a0.Select(x => x.ToString("F3", Inv))))}] after=[{(a1 == null ? "" : string.Join(",", a1.Select(x => x.ToString("F3", Inv))))}]");
            }

            // ---- 6 (local): persistence with invariant culture
            SetDesign(m, 777.7f, 123.4f, 45.6f, true);
            yield return new WaitForSecondsRealtime(1.5f);
            yield return CheckPersistence(m, Path.Combine(KSPUtil.ApplicationRootPath, "saves", HighLogic.SaveFolder, "Ships", "VAB", "SpikeSaved.craft"));

            // ---- 3: attach nodes, both hierarchy directions
            yield return CheckNodes(false);
            yield return CheckNodes(true);

            // ---- flight: 2 (flight readouts), 4 (aero), 5 (waterfall)
            string spikeWing = WriteCraft("SpikeWing", WingCraft("procEngineSpike"));
            if (ModuleProcEngineSpike.FarPresent)
            {
                string refEngine = FirstAvailable("liquidEngine", "liquidEngine2");
                string refWing = WriteCraft("RefWing", WingCraft(refEngine));
                yield return Launch(refWing, "Runway");
                yield return WaitFar(FlightGlobals.ActiveVessel);
                Info("4.far.ref", $"reference craft with {refEngine}: valid={FarValid(FlightGlobals.ActiveVessel)} {FarSummary(FlightGlobals.ActiveVessel)}");
                refPitch = FarForces(FlightGlobals.ActiveVessel, FlightGlobals.ActiveVessel.transform.right, 5f);
                refYaw = FarForces(FlightGlobals.ActiveVessel, FlightGlobals.ActiveVessel.transform.forward, 5f);
                yield return Shot("far_ref");
            }
            yield return Launch(spikeWing, "LaunchPad");
            Vessel v = FlightGlobals.ActiveVessel;
            m = Spike(v.parts);
            yield return CheckMesh(m, "flight");
            yield return CheckReadouts(m, v.parts, "flight", false);
            if (ModuleProcEngineSpike.FarPresent)
            {
                yield return WaitFar(v);
                Info("4.far.spike", $"spike craft (design {m.Design}, De={m.stats.ExitDiameter:F2}): valid={FarValid(v)} {FarSummary(v)}");
                var p0 = FarForces(v, v.transform.right, 5f); var y0 = FarForces(v, v.transform.forward, 5f);
                Check("4.far.wholeCraft", p0.lift > 1f && Near(p0.lift, refPitch.lift, 0.10) && Near(p0.drag, refPitch.drag, 0.20) && Near(y0.lift, refYaw.lift, 0.5),
                    $"generated engine vs stock {"liquidEngine"} on the same winged craft at 150 m/s, 5deg: pitch L {p0.lift:F2}/{refPitch.lift:F2} D {p0.drag:F2}/{refPitch.drag:F2}; yaw L {y0.lift:F2}/{refYaw.lift:F2} kN (VBPP #10 would zero the craft)");
                // Freeze the craft: in flight the grown bell's colliders would otherwise punch it off the pad and
                // the readings would include a tumbling, flexing craft. (In-flight redesign is a test-only path.)
                foreach (Part p in v.parts) if (p.Rigidbody != null) p.Rigidbody.isKinematic = true;
                // control: change the mesh WITHOUT the FAR message -> voxels must stay stale
                yield return m.FlightRedesign(new SpikeDesign { thrustVac = 1500, pc = 60, eps = 80 }, false);
                yield return new WaitForSecondsRealtime(4f);
                var p1 = FarForces(v, v.transform.right, 5f); var y1 = FarForces(v, v.transform.forward, 5f);
                // now send GeometryPartModuleRebuildMeshData
                m.part.SendMessage("GeometryPartModuleRebuildMeshData");
                yield return new WaitForSecondsRealtime(1f);
                yield return WaitFar(v);
                yield return new WaitForSecondsRealtime(2f);
                var p2 = FarForces(v, v.transform.right, 5f); var y2 = FarForces(v, v.transform.forward, 5f);
                Info("4.far.spikeBig", $"after redesign to De={m.stats.ExitDiameter:F2} + GeometryPartModuleRebuildMeshData: {FarSummary(v)}");
                // revert with the message
                yield return m.FlightRedesign(new SpikeDesign { thrustVac = 200, pc = 60, eps = 20 });
                yield return new WaitForSecondsRealtime(1f);
                yield return WaitFar(v);
                yield return new WaitForSecondsRealtime(2f);
                var p3 = FarForces(v, v.transform.right, 5f); var y3 = FarForces(v, v.transform.forward, 5f);
                // Graded on voxel-driven quantities (drag, body lift in the yaw plane). Pitch-plane lift is dominated by
                // FAR's wing model, whose state also moves without any geometry change; it is logged, not graded.
                bool stale = Near(p1.drag, p0.drag, 0.01) && Near(y1.lift, y0.lift, 0.01);
                bool changed = !Near(p2.drag, p0.drag, 0.03) || !Near(y2.lift, y0.lift, 0.03);
                bool back = Near(p3.drag, p0.drag, 0.01) && Near(y3.lift, y0.lift, 0.02);
                Check("4.far.rebuild", stale && changed && back,
                    $"pitch5 D/L, yaw5 L (kN): base {p0.drag:F3}/{p0.lift:F3}, {y0.lift:F3} | mesh changed, no message {p1.drag:F3}/{p1.lift:F3}, {y1.lift:F3} (stale={stale}) | after message {p2.drag:F3}/{p2.lift:F3}, {y2.lift:F3} (changed={changed}) | reverted {p3.drag:F3}/{p3.lift:F3}, {y3.lift:F3} (back={back})");
            }
            else
            {
                Info("4.dragcube.flight", $"De={m.stats.ExitDiameter:F2} cubes={m.part.DragCubes.Cubes.Count} area=[{string.Join(",", m.part.DragCubes.Cubes[0].Area.Select(x => x.ToString("F3", Inv)))}] AreaDrag={m.part.DragCubes.AreaDrag:F4}");
                float a0 = m.part.DragCubes.Cubes[0].Area[3];
                yield return m.FlightRedesign(new SpikeDesign { thrustVac = 1500, pc = 60, eps = 80 });
                yield return new WaitForSecondsRealtime(1f);
                float a1 = m.part.DragCubes.Cubes[0].Area[3];
                Check("4.dragcube.flight", a1 > a0 * 1.5f, $"in-flight re-render: YN area {a0:F3}->{a1:F3} (De {m.stats.ExitDiameter:F2}); AreaDrag={m.part.DragCubes.AreaDrag:F4}");
                yield return m.FlightRedesign(new SpikeDesign { thrustVac = 200, pc = 60, eps = 20 });
            }

            // ---- 5: waterfall
            foreach (Part p in v.parts) if (p.Rigidbody != null) p.Rigidbody.isKinematic = false;
            yield return new WaitForSecondsRealtime(1f);
            Info("5.waterfall", m.waterfallStatus);
            var engine = m.part.FindModuleImplementing<ModuleEnginesFX>();
            FlightInputHandler.state.mainThrottle = 1f;
            v.ctrlState.mainThrottle = 1f;
            engine.Activate();
            yield return new WaitForSecondsRealtime(2.5f);
            Bounds b0 = PlumeBounds(m.part);
            float de0 = m.stats.ExitDiameter;
            yield return Shot("plume_small");
            yield return m.FlightRedesign(new SpikeDesign { thrustVac = 1500, pc = 60, eps = 80 });
            Info("5.waterfall", m.waterfallStatus);
            yield return new WaitForSecondsRealtime(2.5f);
            Bounds b1 = PlumeBounds(m.part);
            yield return Shot("plume_big");
            float wRatio = Math.Max(b1.size.x, b1.size.z) / Math.Max(Math.Max(b0.size.x, b0.size.z), 1e-4f);
            bool wfOk = m.waterfallStatus.Contains("effects=") && !m.waterfallStatus.Contains("effects=0") && !m.waterfallStatus.Contains("effects=-1");
            Check("5.waterfall", wfOk && wRatio > 1.3f, $"plume renderer bounds {V(b0.size)} -> {V(b1.size)} (width ratio {wRatio:F2}) for De {de0:F2} -> {m.stats.ExitDiameter:F2} (ratio {m.stats.ExitDiameter / de0:F2}); finalThrust={engine.finalThrust:F1} kN; {m.waterfallStatus}");
            FlightInputHandler.state.mainThrottle = 0f;
            yield return new WaitForSecondsRealtime(1f);
        }

        IEnumerator RndScript()
        {
            yield return NewGame("pespike_career", Game.Modes.CAREER);
            const string up = "procEngineSpike-StagedCombustion";
            var h = PartUpgradeManager.Handler;
            Info("7.handler", $"upgrade registered={h.GetUpgrade(up) != null} forTech(basicRocketry)={string.Join(",", h.GetUpgradesForTech("basicRocketry").Select(u => u.name))} unlocked={h.IsUnlocked(up)} availableToUnlock={h.IsAvailableToUnlock(up)} cost={h.GetUpgradeCost(up)} allowed={h.UgpradesAllowed()} PartUpgradesInCareer={HighLogic.CurrentGame.Parameters.CustomParams<GameParameters.AdvancedParams>().PartUpgradesInCareer}");
            var rnd = FindObjectOfType<RnDBuilding>();
            if (rnd == null) { Check("7", false, "RnDBuilding not found"); yield break; }
            rnd.EnterBuilding();
            float t0 = Time.realtimeSinceStartup;
            while (RDController.Instance == null && Time.realtimeSinceStartup - t0 < 20) yield return null;
            yield return new WaitForSecondsRealtime(3f);
            RDNode node = RDController.Instance.nodes.FirstOrDefault(n => n.tech.techID == "basicRocketry");
            if (node == null) { Check("7", false, "basicRocketry node not found"); yield break; }
            // same path as a click on the node (RDNode input handler)
            RDController.Instance.node_selected = node;
            node.SelectNode();
            RDController.Instance.ShowNodePanel(node);
            yield return new WaitForSecondsRealtime(2f);
            yield return Shot("rnd_locked");
            Info("7.locked", PartListSummary());
            ResearchAndDevelopment.Instance.AddScience(1000, TransactionReasons.Cheating);
            node.tech.ResearchTech();   // what the panel's Research button calls
            yield return new WaitForSecondsRealtime(1f);
            node.UpdateGraphics();
            RDController.Instance.ShowNodePanel(node);
            yield return new WaitForSecondsRealtime(2f);
            yield return Shot("rnd_researched");
            Info("7.researched", PartListSummary());
            bool unlocked = h.IsUnlocked(up);
            Check("7", h.GetUpgrade(up) != null && unlocked,
                $"after research: unlocked={unlocked} (no purchase needed={unlocked}) enabled={h.IsEnabled(up)} availableToUnlock={h.IsAvailableToUnlock(up)} cost={h.GetUpgradeCost(up)}");
        }

        string PartListSummary()
        {
            var items = FindObjectsOfType<RDPartListItem>();
            return $"R&D part list items={items.Length}: " + string.Join(", ", items.Select(i =>
            {
                object upg = i.GetType().GetField("upgrade", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(i);
                object ap = i.GetType().GetField("myPart", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(i);
                string label = upg != null ? "UPGRADE " + upg.GetType().GetField("name")?.GetValue(upg) : (ap as AvailablePart)?.name ?? i.name;
                return label;
            }));
        }

        // ------------------------------------------------------------------ LMP

        IEnumerator LmpConnect()
        {
            // Use the password stored in this client's own LMP server list for 127.0.0.1 (local test server).
            string pw = "";
            try
            {
                var x = new System.Xml.XmlDocument();
                x.Load(Path.Combine(KSPUtil.ApplicationRootPath, "GameData/LunaMultiplayer/Data/settings.xml"));
                foreach (System.Xml.XmlNode e in x.GetElementsByTagName("ServerEntry"))
                    if (e["Address"]?.InnerText == "127.0.0.1") pw = e["Password"]?.InnerText ?? "";
            }
            catch (Exception ex) { Info("lmp", "could not read LMP settings: " + ex.Message); }
            Type nc = FindType("LmpClient.Network.NetworkConnection");
            nc.GetMethod("ConnectToServer", new[] { typeof(string), typeof(int), typeof(string) }).Invoke(null, new object[] { "127.0.0.1", 5525, pw });
            Info("lmp", "connecting to 127.0.0.1:5525");
            yield return WaitScene(GameScenes.SPACECENTER, 180);
            yield return new WaitForSecondsRealtime(8f);
            Info("lmp", $"in {HighLogic.LoadedScene}, mode={HighLogic.CurrentGame?.Mode}, save={HighLogic.SaveFolder}, vessels={FlightGlobals.Vessels.Count}");
        }

        static void Flag(string name, string content)
        {
            Directory.CreateDirectory(FlagDir);
            File.WriteAllText(Path.Combine(FlagDir, name), content);
        }

        static string Describe(ModuleProcEngineSpike m)
        {
            var e = m.part.FindModuleImplementing<ModuleEnginesFX>();
            Mesh bell = m.part.transform.Find("model/peRoot/peGimbal/peBell").GetComponent<MeshFilter>().sharedMesh;
            return string.Format(Inv, "design={0} maxThrust={1:R} ispVac={2:R} isp1={3:R} maxFuelFlow={4:R} De={5:R} bottomNode={6:R} bellVerts={7} bellBounds={8} mass={9:R}",
                m.Design, e.maxThrust, e.atmosphereCurve.Evaluate(0f), e.atmosphereCurve.Evaluate(1f), e.maxFuelFlow, m.stats.ExitDiameter,
                m.part.FindAttachNode("bottom").position.y, bell.vertexCount, V(bell.bounds.size), m.part.mass);
        }

        IEnumerator LmpAScript()
        {
            yield return LmpConnect();
            if (HighLogic.LoadedScene != GameScenes.SPACECENTER) { Check("6.lmp", false, "A could not join the server"); Flag("A_launched.txt", "FAILED"); yield break; }
            yield return EnterEditor();
            string probe = FirstAvailable("mk1pod.v2", "probeCoreOcto");
            string path = WriteCraft("SpikeLMP", new List<CP>
            {
                new CP(probe),
                new CP("procEngineSpike") { parent = 0, pNode = "bottom", myNode = "top" },
            });
            yield return LoadCraft(path);
            ModuleProcEngineSpike m = Spike(EditorLogic.fetch.ship.parts);
            SetDesign(m, 777.7f, 123.4f, 45.6f, true);
            yield return new WaitForSecondsRealtime(1.5f);
            EditorLogic.fetch.ship.shipName = "SpikeLMP";
            EditorLogic.fetch.ship.SaveShip().Save(path);
            yield return Launch(path, "LaunchPad");
            Vessel v = FlightGlobals.ActiveVessel;
            m = Spike(v.parts);
            string mine = Describe(m);
            Info("6.lmpA", mine);
            yield return Shot("lmpA_flight");
            yield return new WaitForSecondsRealtime(20f);   // let LMP upload the proto
            var pn = new ConfigNode(); v.BackupVessel().Save(pn);
            ConfigNode mod = pn.GetNodes("PART").SelectMany(p => p.GetNodes("MODULE")).FirstOrDefault(x => x.GetValue("name") == "ModuleProcEngineSpike");
            Flag("A_launched.txt", mine + "\n" + v.id + "\n" + mod);
            Info("6.lmpA", "flag written; waiting for B (max 12 min)");
            float t0 = Time.realtimeSinceStartup;
            while (!File.Exists(Path.Combine(FlagDir, "B_done.txt")) && Time.realtimeSinceStartup - t0 < 720) yield return new WaitForSecondsRealtime(1f);
            Info("6.lmpA", "B finished: " + File.Exists(Path.Combine(FlagDir, "B_done.txt")));
        }

        IEnumerator LmpBScript()
        {
            float t0 = Time.realtimeSinceStartup;
            while (!File.Exists(Path.Combine(FlagDir, "A_launched.txt")) && Time.realtimeSinceStartup - t0 < 900) yield return new WaitForSecondsRealtime(1f);
            string[] a = File.ReadAllText(Path.Combine(FlagDir, "A_launched.txt")).Split('\n');
            string aDesc = a[0];
            Guid id = new Guid(a[1].Trim());
            Info("6.lmpB", "A reports: " + aDesc);
            yield return LmpConnect();
            if (HighLogic.LoadedScene != GameScenes.SPACECENTER) { Check("6.lmp", false, "B could not join the server"); Flag("B_done.txt", "fail"); yield break; }
            Vessel v = null;
            t0 = Time.realtimeSinceStartup;
            while ((v = FlightGlobals.Vessels.FirstOrDefault(x => x.id == id)) == null && Time.realtimeSinceStartup - t0 < 120) yield return new WaitForSecondsRealtime(1f);
            if (v == null) { Check("6.lmp", false, "A's vessel never arrived"); Flag("B_done.txt", "fail"); yield break; }
            ConfigNode modNode = v.protoVessel.protoPartSnapshots.SelectMany(p => p.modules).FirstOrDefault(x => x.moduleName == "ModuleProcEngineSpike")?.moduleValues;
            Info("6.lmpB.proto", modNode == null ? "no module node in proto" : modNode.ToString().Replace("\n", " ").Replace("\t", ""));
            ConfigNode aMod = ConfigNode.Parse(string.Join("\n", a.Skip(2)));
            ConfigNode aModNode = aMod?.nodes.Count > 0 ? aMod.nodes[0] : aMod;
            bool sameNode = modNode != null && aModNode != null && modNode.GetNode("DESIGN")?.ToString() == aModNode.GetNode("DESIGN")?.ToString()
                            && modNode.GetNode("STATS")?.ToString() == aModNode.GetNode("STATS")?.ToString();
            Check("6.lmp.proto", sameNode, "B's ProtoVessel MODULE DESIGN/STATS identical to A's: " + sameNode);

            // load it: switch to the vessel (spectating, A holds control)
            int idx = HighLogic.CurrentGame.flightState.protoVessels.FindIndex(p => p.vesselID == id);
            GamePersistence.SaveGame("persistent", HighLogic.SaveFolder, SaveMode.OVERWRITE);
            idx = HighLogic.CurrentGame.flightState.protoVessels.FindIndex(p => p.vesselID == id);
            FlightDriver.StartAndFocusVessel("persistent", idx);
            yield return WaitScene(GameScenes.FLIGHT, 120);
            t0 = Time.realtimeSinceStartup;
            while ((FlightGlobals.ActiveVessel == null || FlightGlobals.ActiveVessel.packed) && Time.realtimeSinceStartup - t0 < 60) yield return null;
            yield return new WaitForSecondsRealtime(4f);
            v = FlightGlobals.ActiveVessel;
            ModuleProcEngineSpike m = v == null ? null : Spike(v.parts);
            if (m == null) { Check("6.lmp.loaded", false, $"active vessel={v?.vesselName}; no spike module"); }
            else
            {
                string mine = Describe(m);
                Info("6.lmpB", mine);
                Check("6.lmp.loaded", mine == aDesc, $"B's loaded engine matches A's exactly: {mine == aDesc}");
                yield return Shot("lmpB_flight");
            }
            Flag("B_done.txt", "ok");
            yield return new WaitForSecondsRealtime(3f);
        }
    }
}
