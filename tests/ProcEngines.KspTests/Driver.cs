using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using ProcEngines.Model;
using UnityEngine;

namespace ProcEngines.KspTests
{
    /// <summary>
    /// Self-driving in-game tests (pattern from the Phase 0 spike). Runs only if
    /// GameData/ProcEnginesTests/PluginData/autotest.txt exists; first line = script (sandbox | career).
    /// Writes results_&lt;script&gt;.txt and screenshots to PluginData, then quits the game.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.Instantly, true)]
    public class Driver : MonoBehaviour
    {
        static string Dir => Path.Combine(KSPUtil.ApplicationRootPath, "GameData/ProcEnginesTests/PluginData");
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        const string PartName = "procEngine-liquid";
        string script;
        StreamWriter results;
        int passes, fails;

        void Awake()
        {
            string f = Path.Combine(Dir, "autotest.txt");
            if (!File.Exists(f)) { Destroy(this); return; }
            DontDestroyOnLoad(gameObject);
            script = File.ReadAllLines(f)[0].Trim();
            Directory.CreateDirectory(Path.Combine(Dir, "shots"));
            results = new StreamWriter(Path.Combine(Dir, "results_" + script + ".txt"), false) { AutoFlush = true };
            Info("start", $"script={script} far={KspData.FarPresent}");
            StartCoroutine(Run());
        }

        // ------------------------------------------------------------------ reporting

        void Line(string s) { Debug.Log("[PETest] " + s); results?.WriteLine(DateTime.Now.ToString("HH:mm:ss ", Inv) + s); }
        void Info(string tag, string msg) => Line($"INFO  [{tag}] {msg}");
        void Check(string id, bool pass, string msg) { if (pass) passes++; else fails++; Line($"{(pass ? "PASS" : "FAIL")}  [{id}] {msg}"); }
        static bool Near(double a, double b, double rel = 0.005) => Math.Abs(a - b) <= rel * Math.Max(Math.Abs(b), 1e-6);

        IEnumerator Run()
        {
            while (HighLogic.LoadedScene != GameScenes.MAINMENU) yield return null;
            yield return new WaitForSecondsRealtime(4f);
            IEnumerator body = script == "career" ? Career() : script == "lmpA" ? LmpA() : script == "lmpB" ? LmpB() : script == "visual" ? Visual() : script == "plumes" ? Plumes() : script == "gimbal" ? GimbalOnly() : Sandbox();
            yield return StartCoroutine(Guard(body));
            Info("end", $"{passes} passed, {fails} failed");
            results.Close();
            yield return new WaitForSecondsRealtime(1f);
            Application.Quit();
        }

        IEnumerator Guard(IEnumerator e)
        {
            var stack = new Stack<IEnumerator>();
            stack.Push(e);
            while (stack.Count > 0)
            {
                IEnumerator top = stack.Peek();
                bool moved;
                try { moved = top.MoveNext(); }
                catch (Exception ex) { fails++; Line("ERROR " + ex); yield break; }
                if (!moved) { stack.Pop(); continue; }
                if (top.Current is IEnumerator nested) stack.Push(nested);
                else yield return top.Current;
            }
        }

        // ------------------------------------------------------------------ helpers

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

        static ModuleProcEngine Engine(IEnumerable<Part> parts) =>
            parts.Select(p => p.FindModuleImplementing<ModuleProcEngine>()).FirstOrDefault(m => m != null);

        static string FirstAvailable(params string[] names) => names.FirstOrDefault(n => PartLoader.getPartInfoByName(n) != null);

        class CP
        {
            public string name; public int parent = -1; public string pNode, myNode;
            public CP(string n) { name = n; }
        }

        string WriteCraft(string ship, List<CP> parts)
        {
            var pos = new Vector3[parts.Count];
            string Id(int i) => parts[i].name + "_" + (4100000000u + (uint)i * 13 + 7);
            for (int i = 0; i < parts.Count; i++)
            {
                CP c = parts[i];
                if (c.parent < 0) { pos[i] = new Vector3(0, 12, 0); continue; }
                Part pf = PartLoader.getPartInfoByName(c.name).partPrefab, pp = PartLoader.getPartInfoByName(parts[c.parent].name).partPrefab;
                pos[i] = pos[c.parent] + pp.FindAttachNode(c.pNode).position - pf.FindAttachNode(c.myNode).position;
            }
            var root = new ConfigNode();
            foreach (var kv in new[] { ("ship", ship), ("version", "1.12.5"), ("description", "ProcEngines test"), ("type", "VAB"), ("size", "2,6,2"),
                         ("steamPublishedFileId", "0"), ("persistentId", "7654321"), ("rot", "0,0,0,1"), ("missionFlag", "Squad/Flags/default"), ("vesselType", "Probe") })
                root.AddValue(kv.Item1, kv.Item2);
            for (int i = 0; i < parts.Count; i++)
            {
                CP c = parts[i];
                Part pf = PartLoader.getPartInfoByName(c.name).partPrefab;
                ConfigNode n = root.AddNode("PART");
                n.AddValue("part", Id(i));
                n.AddValue("partName", "Part");
                n.AddValue("persistentId", (2000 + i).ToString(Inv));
                n.AddValue("pos", KSPUtil.WriteVector(pos[i]));
                n.AddValue("attPos", "0,0,0");
                n.AddValue("attPos0", KSPUtil.WriteVector(c.parent < 0 ? pos[i] : pos[i] - pos[c.parent]));
                n.AddValue("rot", "0,0,0,1"); n.AddValue("attRot", "0,0,0,1"); n.AddValue("attRot0", "0,0,0,1");
                n.AddValue("mir", "1,1,1"); n.AddValue("symMethod", "Radial"); n.AddValue("autostrutMode", "Off"); n.AddValue("rigidAttachment", "False");
                n.AddValue("istg", "0"); n.AddValue("resPri", "0"); n.AddValue("dstg", "0"); n.AddValue("sidx", "-1"); n.AddValue("sqor", "-1");
                n.AddValue("sepI", "-1"); n.AddValue("attm", "0"); n.AddValue("modCost", "0"); n.AddValue("modMass", "0"); n.AddValue("modSize", "0,0,0");
                for (int k = 0; k < parts.Count; k++) if (parts[k].parent == i) n.AddValue("link", Id(k));
                for (int k = 0; k < parts.Count; k++)
                {
                    CP o = parts[k];
                    if (k == i && o.parent >= 0) n.AddValue("attN", o.myNode + "," + Id(o.parent) + "_0|" + pf.FindAttachNode(o.myNode).position.y.ToString("R", Inv) + "|0");
                    if (o.parent == i) n.AddValue("attN", o.pNode + "," + Id(k) + "_0|" + pf.FindAttachNode(o.pNode).position.y.ToString("R", Inv) + "|0");
                }
                n.AddNode("EVENTS"); n.AddNode("ACTIONS"); n.AddNode("PARTDATA");
            }
            string dir = Path.Combine(KSPUtil.ApplicationRootPath, "saves", HighLogic.SaveFolder, "Ships", "VAB");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, ship + ".craft");
            root.Save(path);
            return path;
        }

        List<CP> Stack(bool withDecoupler)
        {
            string probe = FirstAvailable("probeCoreOcto", "probeCoreOcto.v2", "mk1pod.v2");
            string tank = FirstAvailable("fuelTank", "fuelTank.long");
            var l = new List<CP> { new CP(probe), new CP(tank) { parent = 0, pNode = "bottom", myNode = "top" }, new CP(PartName) { parent = 1, pNode = "bottom", myNode = "top" } };
            if (withDecoupler)
            {
                l.Add(new CP(FirstAvailable("Decoupler.1", "Decoupler_1")) { parent = 2, pNode = "bottom", myNode = "top" });
                l.Add(new CP(tank) { parent = 3, pNode = "bottom", myNode = "top" });
            }
            return l;
        }

        static EngineDesign D(string prop, string cycle, double f, double pc, double eps, bool rad = false) =>
            new EngineDesign { Propellant = prop, Cycle = cycle, ThrustVac = f, Pc = pc, Eps = eps, RadiativeExtension = rad };

        // ------------------------------------------------------------------ checks

        IEnumerator CheckApplied(ModuleProcEngine m, string tag, bool editor)
        {
            EngineStats s = m.Stats;
            var e = m.part.FindModuleImplementing<ModuleEnginesFX>();
            ResourceSet rs = KspData.Data.Resources[m.Design.Propellant];
            bool props = e.propellants.Count == rs.Resources.Count && e.propellants.All(p => rs.Resources.Any(r => r.Key == p.name && Math.Abs(r.Value - p.ratio) < 1e-4));
            bool fields = Near(e.maxThrust, s.ThrustVac, 1e-5) && Near(e.atmosphereCurve.Evaluate(0f), s.IspVac, 1e-4) && Near(e.atmosphereCurve.Evaluate(1f), s.IspSL, 2e-3)
                          && Near(e.maxFuelFlow, s.ThrustVac / (s.IspVac * e.g), 1e-4) && Near(e.minThrust, s.ThrustVac * s.ThrottleMin, 1e-4);
            float partMass = m.part.mass;
            if (editor) partMass = m.part.partInfo.partPrefab.mass + m.GetModuleMass(m.part.partInfo.partPrefab.mass, ModifierStagingSituation.CURRENT);
            bool mass = Near(partMass, s.Mass, 1e-4);
            Transform tt = e.thrustTransforms.FirstOrDefault();
            Vector3 exitW = m.part.transform.TransformPoint(m.part.FindAttachNode("bottom").position);
            bool thrustAtExit = tt != null && (tt.position - exitW).magnitude < 1e-3 && Vector3.Dot(tt.forward, -m.part.transform.up) > 0.999f;
            Check("apply." + tag, props && fields && mass && thrustAtExit,
                $"{m.Design.Propellant}/{m.Design.Cycle}: maxThrust {e.maxThrust:F1}/{s.ThrustVac:F1} Isp0 {e.atmosphereCurve.Evaluate(0f):F1}/{s.IspVac:F1} Isp1 {e.atmosphereCurve.Evaluate(1f):F1}/{s.IspSL:F1} " +
                $"props {string.Join("+", e.propellants.Select(p => p.name + ":" + p.ratio))} ({props}) mass {partMass:F3}/{s.Mass:F3} t thrustTransform at exit {thrustAtExit} warnings [{string.Join("; ", s.Warnings)}]");
            if (editor)
            {
                VesselDeltaV dv = EditorLogic.fetch.ship.vesselDeltaV;
                if (dv != null)
                {
                    dv.SetCalcsDirty(true);
                    yield return new WaitForSecondsRealtime(2f);
                    var st = dv.OperatingStageInfo?.Where(x => x.thrustVac > 0).OrderBy(x => Math.Abs(x.thrustVac - s.ThrustVac)).FirstOrDefault();
                    Check("dv." + tag, st != null && Near(st.thrustVac, s.ThrustVac, 0.01) && Near(st.ispVac, s.IspVac, 0.01),
                        st == null ? "no stage with thrust" : $"stock dV app: thrust {st.thrustVac:F1} kN Isp {st.ispVac:F1}/{st.ispASL:F1} s dV {st.deltaVinVac:F0} m/s");
                }
            }
            var mfs = m.part.transform.Find("model/" + ModuleProcEngine.RootName).GetComponentsInChildren<MeshFilter>().Where(f => f.name.StartsWith("pe")).ToArray();
            var cols = m.part.transform.Find("model/" + ModuleProcEngine.RootName).GetComponentsInChildren<MeshCollider>();
            Info("mesh." + tag, $"meshes {string.Join(", ", mfs.Select(f => f.name + ":" + f.sharedMesh.vertexCount))}; colliders {cols.Length} convex {cols.All(c => c.convex)}; build {m.LastBuildMs:F1} ms; Ø{s.ExitDiameter:F2} L{s.TotalLength:F2} m");
        }

        static float Gap(Part a, string na, Part b, string nb) =>
            (a.transform.TransformPoint(a.FindAttachNode(na).position) - b.transform.TransformPoint(b.FindAttachNode(nb).position)).magnitude;

        // ------------------------------------------------------------------ scripts

        IEnumerator Sandbox()
        {
            yield return NewGame("procengines_sandbox", Game.Modes.SANDBOX);
            yield return EnterEditor();
            yield return LoadCraft(WriteCraft("PE_Single", Stack(false)));
            ModuleProcEngine m = Engine(EditorLogic.fetch.ship.parts);
            Check("load", m != null && m.Stats != null && m.Caps != null && m.Caps.Unlimited, $"module {(m != null)} stats {(m?.Stats != null)} caps unlimited {m?.Caps?.Unlimited}");
            if (m == null) yield break;

            var combos = new[]
            {
                ("KeroloxGG", D("Kerolox", "GasGenerator", 900, 97, 16)),
                ("KeroloxSC", D("Kerolox", "StagedCombustion", 2000, 250, 37)),
                ("KeroloxPF", D("Kerolox", "PressureFed", 30, 12, 40, true)),
                ("MethaloxFFSC", D("Methalox", "FullFlowStaged", 2300, 300, 34)),
                ("HydroloxSC", D("Hydrolox", "StagedCombustion", 2200, 206, 69)),
                ("HydroloxExp", D("Hydrolox", "Expander", 110, 44, 250, true)),
            };
            foreach (var (tag, d) in combos)
            {
                m.SetDesign(d);
                yield return new WaitForSecondsRealtime(1.5f);
                yield return CheckApplied(m, tag, true);
                yield return CheckActuators(m, tag);
                yield return Shot("editor_" + tag);
            }

            // throttle: 40 changes in a burst
            int r0 = m.MeshRebuilds, s0 = m.SlowRebuilds;
            float t0 = Time.realtimeSinceStartup;
            for (int i = 0; i < 40; i++) { m.SetDesign(D("Kerolox", "GasGenerator", 500 + 10 * i, 80, 20 + i)); yield return null; }
            float secs = Time.realtimeSinceStartup - t0;
            yield return new WaitForSecondsRealtime(1.5f);
            Check("throttle", m.MeshRebuilds - r0 <= Mathf.CeilToInt(secs * 10) + 2 && m.SlowRebuilds - s0 == 1, $"{secs:F2}s burst: {m.MeshRebuilds - r0} mesh rebuilds, {m.SlowRebuilds - s0} slow rebuilds");

            // designer window
            m.designName = "Test Kestrel";
            DesignerWindow.Open(m);
            yield return new WaitForSecondsRealtime(1f);
            Check("designer.open", DesignerWindow.IsOpen, "designer window open");
            float h0 = DesignerWindow.WindowRect.height;
            for (int i = 0; i < 60; i++) yield return null;   // the old window grew a little every frame
            float h1 = DesignerWindow.WindowRect.height;
            Check("designer.size", h1 == h0 && h1 > 200 && h1 <= Screen.height - 100 && DesignerWindow.WindowRect.yMax <= Screen.height,
                $"window {DesignerWindow.WindowRect} stays {h0} -> {h1} px high on a {Screen.width}x{Screen.height} screen");
            yield return Shot("designer");
            DesignerWindow.ScrollTo(10000);
            yield return null; yield return null;
            yield return Shot("designer_scrolled");
            DesignerWindow.ScrollTo(0);

            // drag handles: drag along rays through known points on each handle's axis
            EngineHandles hs = EngineHandles.Instance;
            Camera ecam = EditorLogic.fetch.editorCamera;
            Ray RayTo(EngineHandles.Handle h, float value) { Vector3 w = m.part.transform.TransformPoint(h.Origin + h.Axis * value); return new Ray(ecam.transform.position, w - ecam.transform.position); }
            var handles = EngineHandles.HandlesFor(m);
            Info("handles", string.Join("; ", handles.Select(h => $"{h.Id} value {h.Value:F3} screen {ecam.WorldToScreenPoint(h.WorldPosition(m.part))}")));
            EngineDesign before = Serialization.LoadDesign(Serialization.SaveDesign(m.Design));
            EngineHandles.Handle exitH = handles.First(h => h.Id == "exit");
            hs.DragTo("exit", RayTo(exitH, exitH.Value * 1.5f));
            hs.EndDrag();
            yield return new WaitForSecondsRealtime(1.2f);
            double epsRatio = m.Design.Eps / before.Eps;
            handles = EngineHandles.HandlesFor(m);
            EngineHandles.Handle thrustH = handles.First(h => h.Id == "thrust");
            EngineDesign mid = Serialization.LoadDesign(Serialization.SaveDesign(m.Design));
            hs.DragTo("thrust", RayTo(thrustH, thrustH.Value * 1.2f));
            hs.EndDrag();
            yield return new WaitForSecondsRealtime(1.2f);
            double thrustRatio = m.Design.ThrustVac / mid.ThrustVac;
            handles = EngineHandles.HandlesFor(m);
            EngineHandles.Handle lenH = handles.First(h => h.Id == "length");
            double cone = (Math.Sqrt(m.Design.Eps) - 1) * m.Stats.ThroatRadius / Math.Tan(15 * Math.PI / 180);
            hs.DragTo("length", RayTo(lenH, (float)(0.9 * cone)));
            hs.EndDrag();
            yield return new WaitForSecondsRealtime(1.2f);
            yield return Shot("handles");
            Check("handles", Math.Abs(epsRatio - 2.25) < 0.02 && Math.Abs(thrustRatio - 1.44) < 0.03 && Math.Abs(m.Design.LengthFraction - 0.9) < 0.01 && m.Design.Pc == before.Pc,
                $"exit x1.5 -> eps x{epsRatio:F3} (exp 2.25); chamber x1.2 -> thrust x{thrustRatio:F3} (exp 1.44); length -> {m.Design.LengthFraction:F3} (exp 0.90); Pc unchanged {m.Design.Pc}");

            // library: save as subassembly, read back
            string msg = DesignLibrary.SaveAsSubassembly(m);
            string path = Path.Combine(KSPUtil.ApplicationRootPath, "saves", HighLogic.SaveFolder, "Subassemblies", DesignLibrary.FileNameFor(m.designName) + ".craft");
            ConfigNode sub = File.Exists(path) ? ConfigNode.Load(path) : null;
            ConfigNode modNode = sub?.GetNodes("PART").SelectMany(p => p.GetNodes("MODULE")).FirstOrDefault(n => n.GetValue("name") == "ModuleProcEngine");
            EngineDesign back = modNode?.GetNode("DESIGN") == null ? null : Serialization.LoadDesign(KspData.ToCfg(modNode.GetNode("DESIGN")));
            Check("library.save", sub != null && sub.GetNodes("PART").Length == 1 && !sub.GetNodes("PART")[0].HasValue("link") && back != null && back.ThrustVac == m.Design.ThrustVac && back.Eps == m.Design.Eps,
                $"{msg}; parts {sub?.GetNodes("PART").Length}; design back {back?.ThrustVac}/{back?.Eps}");
            ShipConstruct loaded = File.Exists(path) ? ShipConstruction.LoadSubassembly(path) : null;
            Check("library.load", loaded != null && loaded.parts.Count == 1 && loaded.parts[0].FindModuleImplementing<ModuleProcEngine>()?.Design?.ThrustVac == m.Design.ThrustVac,
                $"stock LoadSubassembly: parts {loaded?.parts.Count}");
            if (loaded != null) foreach (Part p in loaded.parts) Destroy(p.gameObject);
            // close paths: the part-menu button toggles, Close() closes
            m.OpenDesigner();
            yield return null;
            bool closedByToggle = !DesignerWindow.IsOpen;
            m.OpenDesigner();
            yield return null;
            bool reopened = DesignerWindow.IsOpen;
            DesignerWindow.Close();
            yield return null;
            Check("designer.close", closedByToggle && reopened && !DesignerWindow.IsOpen, $"toggle closed {closedByToggle}, toggle reopened {reopened}, Close() closed {!DesignerWindow.IsOpen}");

            // nodes: engine above a decoupler, nozzle grows
            yield return LoadCraft(WriteCraft("PE_Stack", Stack(true)));
            m = Engine(EditorLogic.fetch.ship.parts);
            Part dec = m.part.FindAttachNode("bottom").attachedPart;
            m.SetDesign(D("Kerolox", "GasGenerator", 500, 80, 15));
            yield return new WaitForSecondsRealtime(1.5f);
            float g0 = Gap(m.part, "bottom", dec, "top");
            Vector3 p0 = dec.transform.position;
            m.SetDesign(D("Kerolox", "GasGenerator", 500, 80, 120));
            yield return new WaitForSecondsRealtime(1.5f);
            float g1 = Gap(m.part, "bottom", dec, "top");
            float moved = (dec.transform.position - p0).magnitude;
            m.SetDesign(D("Kerolox", "GasGenerator", 500, 80, 15));
            yield return new WaitForSecondsRealtime(1.5f);
            float drift = (dec.transform.position - p0).magnitude;
            Check("nodes", g0 < 1e-3f && g1 < 1e-3f && moved > 0.1f && drift < 1e-3f, $"gap {g0:F5} -> {g1:F5}, decoupler pushed {moved:F3} m, back to start drift {drift:F6} m");
            yield return Shot("stack");

            // mesh cache: two engines with the same design share their render meshes; changing one gives it its own
            {
                string tankName = FirstAvailable("fuelTankSmallFlat", "fuelTankSmall", "fuelTank");
                string probeName = FirstAvailable("probeCoreOcto", "probeCoreOcto.v2");
                yield return LoadCraft(WriteCraft("PE_Pair", new List<CP>
                {
                    new CP(probeName),
                    new CP(PartName) { parent = 0, pNode = "bottom", myNode = "top" },
                    new CP(tankName) { parent = 1, pNode = "bottom", myNode = "top" },
                    new CP(PartName) { parent = 2, pNode = "bottom", myNode = "top" },
                }));
                var pair = EditorLogic.fetch.ship.parts.SelectMany(pp => pp.FindModulesImplementing<ModuleProcEngine>()).ToList();
                EngineDesign same = D("Kerolox", "GasGenerator", 400, 90, 18);
                foreach (ModuleProcEngine pe in pair) pe.SetDesign(same);
                yield return new WaitForSecondsRealtime(2f);
                Mesh Mesh0(ModuleProcEngine pe, string path) => pe.part.transform.Find("model/peRoot/" + path).GetComponent<MeshFilter>().sharedMesh;
                bool shared = pair.Count == 2 && Mesh0(pair[0], "peKit") == Mesh0(pair[1], "peKit") && Mesh0(pair[0], "peGimbal/peBell") == Mesh0(pair[1], "peGimbal/peBell")
                              && Mesh0(pair[0], "peChamberLod1") == Mesh0(pair[1], "peChamberLod1");
                pair[1].SetDesign(D("Kerolox", "GasGenerator", 400, 90, 30));
                yield return new WaitForSecondsRealtime(2f);
                bool split = pair.Count == 2 && Mesh0(pair[0], "peGimbal/peBell") != Mesh0(pair[1], "peGimbal/peBell") && Mesh0(pair[0], "peGimbal/peBell") != null
                             && Mesh0(pair[0], "peGimbal/peBell").vertexCount > 0 && Mesh0(pair[1], "peGimbal/peBell").vertexCount > 0;
                // nothing (e.g. KSPCF's drag-cube patch reading MeshFilter.mesh) may have replaced our meshes with copies
                var copies = pair.SelectMany(pe => pe.part.transform.Find("model/peRoot").GetComponentsInChildren<MeshFilter>(true))
                    .Where(f => f.sharedMesh != null && f.sharedMesh.name.Contains("Instance")).Select(f => f.name).ToList();
                Check("mesh.cache", shared && split && copies.Count == 0, $"{pair.Count} engines; identical designs share meshes {shared}; after changing one, separate and intact {split}; mesh copies on [{string.Join(", ", copies)}]; far {KspData.FarPresent}");
            }

            yield return FlightChecks();
        }

        /// <summary>The flight half of the sandbox script: a small stack launched, STATS and the engine applied, a burn.</summary>
        IEnumerator FlightChecks()
        {
            // flight: kerolox engine on a small stack, Waterfall + readouts from STATS
            yield return LoadCraft(WriteCraft("PE_Flight", Stack(false)));
            ModuleProcEngine m = Engine(EditorLogic.fetch.ship.parts);
            m.SetDesign(D("Kerolox", "GasGenerator", 300, 90, 14));
            yield return new WaitForSecondsRealtime(1.5f);
            string flightPath = Path.Combine(KSPUtil.ApplicationRootPath, "saves", HighLogic.SaveFolder, "Ships", "VAB", "PE_Flight.craft");
            EditorLogic.fetch.ship.SaveShip().Save(flightPath);
            EngineStats editorStats = m.Stats;
            FlightDriver.StartWithNewLaunch(flightPath, "Squad/Flags/default", "LaunchPad", new VesselCrewManifest());
            yield return WaitScene(GameScenes.FLIGHT);
            yield return WaitMeshesReady();
            float tf = Time.realtimeSinceStartup;
            while ((FlightGlobals.ActiveVessel == null || FlightGlobals.ActiveVessel.packed) && Time.realtimeSinceStartup - tf < 60) yield return null;
            yield return new WaitForSecondsRealtime(4f);
            Vessel v = FlightGlobals.ActiveVessel;
            m = Engine(v.parts);
            {
                PartJoint j = m.part.attachJoint;
                AttachNode top = m.part.FindAttachNode("top");
                Info("flight.joint", string.Format(Inv, "{0} parts; engine joint to {1}: breakForce {2} breakTorque {3}, part breakingForce {4} breakingTorque {5}, top node size {6}, parent node size {7}; engine mass {8:F3} t, vessel {9:F3} t",
                    v.parts.Count, m.part.parent?.partInfo?.name, j?.Joint != null ? j.Joint.breakForce : -1, j?.Joint != null ? j.Joint.breakTorque : -1, m.part.breakingForce, m.part.breakingTorque,
                    top?.size, m.part.parent?.FindAttachNodeByPart(m.part)?.size, m.part.mass + m.part.GetResourceMass(), v.totalMass));
            }
            float flightT0 = Time.realtimeSinceStartup;
            EventData<PartJoint, float>.OnEvent onBreak = (pj, f) => Info("flight.jointBreak", string.Format(Inv, "+{0:F2} s: {1} -> {2} broke at {3:F1}", Time.realtimeSinceStartup - flightT0, pj?.Child?.partInfo?.name, pj?.Parent?.partInfo?.name, f));
            GameEvents.onPartJointBreak.Add(onBreak);
            Check("flight.stats", m.Stats.IspVac == editorStats.IspVac && m.Stats.Mass == editorStats.Mass, $"flight STATS identical to editor: Isp {m.Stats.IspVac}/{editorStats.IspVac}");
            yield return CheckApplied(m, "flight", false);
            Info("waterfall", m.WaterfallStatus);
            var e = m.part.FindModuleImplementing<ModuleEnginesFX>();
            FlightInputHandler.state.mainThrottle = 1f;
            e.Activate();
            yield return new WaitForSecondsRealtime(2f);
            Check("flight.burn", e.finalThrust > 0.5f * (float)m.Stats.ThrustSL && m.WaterfallStatus.Contains("effects"), $"finalThrust {e.finalThrust:F1} kN (SL {m.Stats.ThrustSL:F1}); waterfall {m.WaterfallStatus}");
            yield return Shot("flight_burn");
            yield return FlightGimbal(m, v);
            FlightInputHandler.state.mainThrottle = 0f;
            GameEvents.onPartJointBreak.Remove(onBreak);
        }

        /// <summary>The flight checks alone (sandbox game), for quick runs.</summary>
        IEnumerator GimbalOnly()
        {
            yield return NewGame("procengines_gimbal", Game.Modes.SANDBOX);
            yield return EnterEditor();
            yield return FlightChecks();
        }

        /// <summary>
        /// The stock gimbal, driven by control input: full pitch and yaw make ModuleGimbal turn peGimbal by the
        /// design's range on both axes (ApplyEngine sets its per-direction ranges), the actuators follow, and the bell
        /// returns when the input goes. The engine is shut down first: on this small stack (about 11 g at full thrust)
        /// a full gimbal kick under thrust snaps the joints, and the engine, as debris, gets no input. The module
        /// deflects with or without thrust. It is activated as staging would (OnActive), since the test ignited the
        /// engine directly.
        /// </summary>
        IEnumerator FlightGimbal(ModuleProcEngine m, Vessel v)
        {
            Transform gT = m.part.transform.Find("model/peRoot/peGimbal");
            Quaternion rest = gT.localRotation;
            ModuleGimbal gm = m.part.FindModuleImplementing<ModuleGimbal>();
            var e = m.part.FindModuleImplementing<ModuleEnginesFX>();
            FlightInputHandler.state.mainThrottle = 0f;
            e?.Shutdown();
            yield return new WaitForSecondsRealtime(1f);
            ActuatorsFollow(m, out float _, out float _, out float len0, out float _, out string _);
            if (gm == null || m.part.vessel != v) { Check("flight.gimbal", false, $"ModuleGimbal {gm != null}; engine still on the active vessel {m.part.vessel == v}"); yield break; }
            gm.OnActive();
            FlightInputCallback fbw = st => { st.pitch = 1; st.yaw = 1; };
            v.OnFlyByWire += fbw;
            yield return new WaitForSecondsRealtime(1.5f);
            float defl = Quaternion.Angle(rest, gT.localRotation);
            bool ok = ActuatorsFollow(m, out float sep, out float stat, out float len, out float tilt, out string info);
            Vector3 act = gm.actuationLocal;
            yield return Shot("flight_gimbal");
            v.OnFlyByWire -= fbw;
            yield return new WaitForSecondsRealtime(1.5f);
            float back = Quaternion.Angle(rest, gT.localRotation);
            bool intact = m.part.vessel == v;
            // full pitch and yaw: each gimbal axis gets at most the range, so the bell turns by range .. range * sqrt(2)
            float range = (float)m.Design.GimbalRange;
            bool ranges = gm.gimbalRangeXP == range && gm.gimbalRangeXN == range && gm.gimbalRangeYP == range && gm.gimbalRangeYN == range;
            Check("flight.gimbal", ok && intact && ranges && defl > 0.95f * range && defl < 1.05f * range * Mathf.Sqrt(2) && back < 0.1f && Mathf.Abs(len - len0) > 1e-4f,
                string.Format(Inv, "ModuleGimbal with full pitch and yaw: {0:F2}° (actuation {1}; design range {2}°, module ranges {3}/{4}/{5}/{6}), {7:F2}° after release; craft intact {8}; eye-lug gap {9:F5} m (static {10:F4}), length {11:F4} -> {12:F4} m; {13}",
                    defl, act, range, gm.gimbalRangeXP, gm.gimbalRangeXN, gm.gimbalRangeYP, gm.gimbalRangeYN, back, intact, sep, stat, len0, len, info));
        }

        // ------------------------------------------------------------------ gimbal actuators

        /// <summary>
        /// How the actuators sit as their pivots stand now, in the peRoot frame: the rod eye against the lug pin it
        /// hangs on (the bell renderer turning the rest eye, independent of the module's own aim), what the gap would
        /// be with a static actuator, the barrel on its clevis and aimed through the eye, barrel and rod coaxial, the
        /// rod still in the gland and below the barrel head, and the eye's misalignment with the lug pin (the
        /// spherical bearing takes it). Returns whether all of it holds; sep / staticSep / length are the worst gap,
        /// the worst static gap and actuator 0's length.
        /// </summary>
        static bool ActuatorsFollow(ModuleProcEngine m, out float sep, out float staticSep, out float length, out float tilt, out string info)
        {
            sep = staticSep = length = tilt = 0;
            info = "";
            Transform root = m.part.transform.Find("model/peRoot");
            Transform bell = root.Find("peGimbal/peBell");
            KitBuilder.Result k = m.Kit;
            EngineGeometry g = m.Geometry;
            if (k == null || g == null || k.Actuators.Length != ModuleProcEngine.ActuatorCount) { info = $"no actuator poses ({k?.Actuators.Length})"; return false; }
            bool ok = true;
            float ra = k.ActuatorRadius;
            var parts = new List<string>();
            for (int i = 0; i < k.Actuators.Length; i++)
            {
                KitBuilder.ActuatorPose a = k.Actuators[i];
                Transform body = root.Find(ModuleProcEngine.ActuatorName + i), rod = root.Find(ModuleProcEngine.ActuatorRodName + i);
                Vector3 lug = root.InverseTransformPoint(bell.TransformPoint(a.Eye - new Vector3(0, g.YThroat, 0)));
                Vector3 top = root.InverseTransformPoint(body.position), eye = root.InverseTransformPoint(rod.position);
                Vector3 up = root.InverseTransformDirection(body.up), rodUp = root.InverseTransformDirection(rod.up);
                Vector3 pin = root.InverseTransformDirection(bell.TransformDirection(a.Pin));
                float s = (eye - lug).magnitude, st = (a.Eye - lug).magnitude, clevis = (top - a.Top).magnitude;
                float coax = Vector3.Angle(up, rodUp), aim = Vector3.Angle(up, top - eye), len = (top - eye).magnitude;
                float rodTop = len - k.ActuatorRodLength;   // top of the rod, from the clevis along the axis
                bool inserted = rodTop < k.ActuatorGland - 0.05f * ra && rodTop > 1.2f * ra;
                float tl = Mathf.Abs(90 - Vector3.Angle(rodUp, pin));
                ok &= s < 1e-3f && clevis < 1e-4f && coax < 0.01f && aim < 0.05f && inserted && body.gameObject.activeInHierarchy && rod.gameObject.activeInHierarchy;
                sep = Mathf.Max(sep, s); staticSep = Mathf.Max(staticSep, st); tilt = Mathf.Max(tilt, tl);
                if (i == 0) length = len;
                parts.Add(string.Format(Inv, "#{0} gap {1:F5} m (static {2:F4}), clevis {3:F5}, coax {4:F3}°, aim {5:F3}°, length {6:F4}/{7:F4} m, rod top {8:F3} m below the clevis (gland {9:F3}, head {10:F3}), eye/pin misalignment {11:F2}°",
                    i, s, st, clevis, coax, aim, len, (a.Top - a.Eye).magnitude, rodTop, k.ActuatorGland, 1.2f * ra, tl));
            }
            info = string.Join("; ", parts);
            return ok;
        }

        /// <summary>
        /// Smallest gaps (m) between the actuators as they stand and (bell) the bell wall and the coolant manifold,
        /// (kit) the static kit, (bellKit) the kit on the bell away from each actuator's own lug. An actuator is its
        /// axis from 1.8 barrel radii below the clevis (above that: the clevis on its outrigger beam, which doesn't
        /// move) to the eye: barrel 1.05 ra, gland 1.25 ra in the 0.9 ra above where the rod leaves it, rod 0.45 ra,
        /// rod end 0.65 ra in the last 0.7 ra. `kit` is the static kit's vertices (peRoot frame), `kitBell` the bell
        /// kit's (bell frame); `worst` names the static-kit vertex closest to an actuator.
        /// </summary>
        static void ActuatorClearance(ModuleProcEngine m, Vector3[] kit, Vector3[] kitBell, out float bell, out float stat, out float bellKit, out string worst)
        {
            bell = stat = bellKit = float.MaxValue;
            worst = "";
            Transform root = m.part.transform.Find("model/peRoot");
            Transform bellT = root.Find("peGimbal/peBell");
            KitBuilder.Result k = m.Kit;
            EngineGeometry g = m.Geometry;
            float ra = k.ActuatorRadius;
            Quaternion qb = Quaternion.Inverse(root.rotation) * bellT.rotation, qi = Quaternion.Inverse(qb);
            Vector3 pivot = root.InverseTransformPoint(bellT.position);
            for (int i = 0; i < k.Actuators.Length; i++)
            {
                KitBuilder.ActuatorPose a = k.Actuators[i];
                Vector3 top = root.InverseTransformPoint(root.Find(ModuleProcEngine.ActuatorName + i).position);
                Vector3 eye = root.InverseTransformPoint(root.Find(ModuleProcEngine.ActuatorRodName + i).position);
                float L = (eye - top).magnitude;
                Vector3 u = (eye - top) / L;
                float Need(float s) => s < 1.8f * ra ? -1 : s <= k.ActuatorGland - 0.9f * ra ? 1.05f * ra : s <= k.ActuatorGland ? 1.25f * ra : s <= L - 0.7f * ra ? 0.45f * ra : 0.65f * ra;
                float Gap(Vector3 p)
                {
                    Vector3 d = p - top;
                    float s = Vector3.Dot(d, u);
                    if (s < 1.8f * ra) return float.MaxValue;
                    if (s > L) return (p - eye).magnitude - 0.65f * ra;
                    return (d - u * s).magnitude - Need(s);
                }
                // the bell wall and manifold, in the bell's frame (heights as at rest)
                for (int n = 0; n <= 80; n++)
                {
                    float s = Mathf.Lerp(1.8f * ra, L, n / 80f);
                    Vector3 pb = qi * (top + u * s - pivot);
                    float y = pb.y + g.YThroat, r = Mathf.Sqrt(pb.x * pb.x + pb.z * pb.z);
                    if (y <= g.YThroat && y >= g.YExit) bell = Mathf.Min(bell, r - g.BellRadiusAt(y) - Need(s));
                    bell = Mathf.Min(bell, new Vector2(r - g.ManifoldRadius, y - g.ManifoldY).magnitude - g.ManifoldTubeRadius - Need(s));
                }
                foreach (Vector3 v in kit)
                {
                    float gp = Gap(v);
                    if (gp >= stat) continue;
                    stat = gp;
                    float sv = Vector3.Dot(v - top, u);
                    worst = string.Format(Inv, "actuator {0} at {1:F2} ra down the axis, vertex r {2:F3} y {3:F3} at {4:F0}°", i, sv / ra, Mathf.Sqrt(v.x * v.x + v.z * v.z), v.y, Mathf.Atan2(v.z, v.x) * Mathf.Rad2Deg);
                }
                // the bell kit, minus this actuator's lug (pad on the wall out to the pin, and a little past it)
                Vector3 eyeB = a.Eye - new Vector3(0, g.YThroat, 0), radial = new Vector3(a.Eye.x, 0, a.Eye.z).normalized;
                Vector3 padB = radial * g.BellRadiusAt(a.Eye.y) + new Vector3(0, eyeB.y, 0), lugOut = (eyeB - padB).normalized;
                Vector3 l0 = padB - lugOut * 0.3f * ra, l1 = eyeB + lugOut * 0.45f * ra;
                foreach (Vector3 vb in kitBell)
                {
                    Vector3 w = vb - l0, seg = l1 - l0;
                    float t = Mathf.Clamp01(Vector3.Dot(w, seg) / seg.sqrMagnitude);
                    if ((w - seg * t).magnitude < 1.05f * ra) continue;
                    bellKit = Mathf.Min(bellKit, Gap(pivot + qb * vb));
                }
            }
        }

        /// <summary>
        /// Editor: the bell turned GimbalClearanceDeg about eight horizontal axes (every 45 degrees, so both ways
        /// about the actuators' radial and pin axes). At each pose and at rest the actuators must follow
        /// (ActuatorsFollow), keep clear of the bell, and not run into the kit any more than they do at rest. Both
        /// levels have their barrel and rod renderers in the LODGroup.
        /// </summary>
        IEnumerator CheckActuators(ModuleProcEngine m, string tag)
        {
            Transform root = m.part.transform.Find("model/peRoot"), gT = root.Find("peGimbal");
            Quaternion rest = gT.localRotation;
            Vector3[] kit = root.Find("peKit").GetComponent<MeshFilter>().sharedMesh.vertices;
            Vector3[] kitBell = root.Find("peGimbal/peBell/peKitBell").GetComponent<MeshFilter>().sharedMesh.vertices;
            bool follow = ActuatorsFollow(m, out float sep, out float _, out float len0, out float _, out string info0);
            ActuatorClearance(m, kit, kitBell, out float bell0, out float kit0, out float bk0, out string where0);
            float worstSep = sep, worstStatic = 0, worstTilt = 0, bellMin = float.MaxValue, kitMin = float.MaxValue, bkMin = float.MaxValue, lenMin = len0, lenMax = len0;
            string bad = follow ? null : "rest: " + info0, whereMin = "";
            for (int n = 0; n < 8; n++)
            {
                Vector3 axis = Quaternion.Euler(0, 45 * n, 0) * Vector3.right;
                gT.localRotation = Quaternion.AngleAxis(KitBuilder.GimbalClearanceDeg, axis) * rest;
                yield return null;   // the module aims its actuators in LateUpdate
                bool ok = ActuatorsFollow(m, out sep, out float st, out float len, out float tilt, out string info);
                ActuatorClearance(m, kit, kitBell, out float b, out float kc, out float bk, out string where);
                if (kc < kitMin) whereMin = $"axis {45 * n}°: {where}";
                if (!ok && bad == null) bad = $"axis {45 * n}°: {info}";
                follow &= ok;
                worstSep = Mathf.Max(worstSep, sep); worstStatic = Mathf.Max(worstStatic, st); worstTilt = Mathf.Max(worstTilt, tilt);
                bellMin = Mathf.Min(bellMin, b); kitMin = Mathf.Min(kitMin, kc); bkMin = Mathf.Min(bkMin, bk);
                lenMin = Mathf.Min(lenMin, len); lenMax = Mathf.Max(lenMax, len);
            }
            gT.localRotation = rest;
            yield return null;
            follow &= ActuatorsFollow(m, out float sepBack, out float _, out float lenBack, out float _, out string _) && Mathf.Abs(lenBack - len0) < 1e-5f;
            var lg = root.GetComponent<LODGroup>().GetLODs();
            bool lods = true;
            for (int i = 0; i < ModuleProcEngine.ActuatorCount; i++)
                foreach (var (pivot, lod0, lod1) in new[] { (ModuleProcEngine.ActuatorName + i, ModuleProcEngine.ActBodyName, ModuleProcEngine.ActBodyLodName), (ModuleProcEngine.ActuatorRodName + i, ModuleProcEngine.ActRodName, ModuleProcEngine.ActRodLodName) })
                {
                    MeshRenderer r0 = root.Find(pivot + "/" + lod0)?.GetComponent<MeshRenderer>(), r1 = root.Find(pivot + "/" + lod1)?.GetComponent<MeshRenderer>();
                    Mesh m0 = r0?.GetComponent<MeshFilter>().sharedMesh, m1 = r1?.GetComponent<MeshFilter>().sharedMesh;
                    lods &= r0 != null && r1 != null && m0 != null && m1 != null && m0.vertexCount > m1.vertexCount && m1.vertexCount > 0
                            && lg[0].renderers.Contains(r0) && lg[1].renderers.Contains(r1) && !lg[2].renderers.Contains(r0) && !lg[2].renderers.Contains(r1);
                }
            bool clear = bellMin > 0 && kitMin >= Mathf.Min(kit0, 0) - 0.002f && bkMin >= Mathf.Min(bk0, 0) - 0.002f;
            Check("actuators." + tag, follow && clear && lods && lenMax - lenMin > 1e-3f,
                string.Format(Inv, "±{0}° about 8 axes: worst eye-lug gap {1:F5} m (a static actuator: {2:F4} m), length {3:F4} .. {4:F4} m (rest {5:F4}), eye misalignment ≤ {6:F2}°; " +
                    "clearance bell {7:F4} (rest {8:F4}), static kit {9:F4} [{15}] (rest {10:F4} [{16}]), bell kit {11:F4} (rest {12:F4}); LOD renderers {13}{14}",
                    KitBuilder.GimbalClearanceDeg, worstSep, worstStatic, lenMin, lenMax, len0, worstTilt, bellMin, bell0, kitMin, kit0, bkMin, bk0, lods, bad != null ? "; FIRST BAD " + bad : "", whereMin, where0));
        }

        /// <summary>Orbit `t` at `distance` from the heading (30-degree steps) that looks closest to along `worldDir`
        /// (from the target toward the camera).</summary>
        IEnumerator CameraToward(Transform t, Vector3 worldDir, float distance, float pitchDeg)
        {
            Vessel v = FlightGlobals.ActiveVessel;
            Vector3 up = (v.transform.position - v.mainBody.position).normalized, want = Vector3.ProjectOnPlane(worldDir, up);
            int best = 0;
            float bestErr = float.MaxValue;
            for (int h = 0; h < 360; h += 30)
            {
                CameraOn(t, distance, pitchDeg, h);
                for (int f = 0; f < 3; f++) yield return null;
                float err = Vector3.Angle(Vector3.ProjectOnPlane(FlightCamera.fetch.mainCamera.transform.position - t.position, up), want);
                if (err < bestErr) { bestErr = err; best = h; }
            }
            CameraOn(t, distance, pitchDeg, best);
            yield return new WaitForSecondsRealtime(1f);
        }

        /// <summary>Flight: the bell held at rest and at +-GimbalClearanceDeg about actuator 0's pin axis (the stock
        /// gimbal module paused), a close-up on its rod eye and lug from outside for each, and the actuators checked to
        /// follow at both deflections.</summary>
        IEnumerator GimbalShots(ModuleProcEngine m, string tag)
        {
            Transform root = m.part.transform.Find("model/peRoot"), gT = root.Find("peGimbal");
            ModuleGimbal gm = m.part.FindModuleImplementing<ModuleGimbal>();
            if (gm != null) gm.enabled = false;
            Quaternion rest = gT.localRotation;
            KitBuilder.ActuatorPose a = m.Kit.Actuators[0];
            Transform eye = root.Find(ModuleProcEngine.ActuatorRodName + "0");
            yield return CameraToward(eye, root.TransformDirection((a.Out + 0.7f * a.Pin).normalized), 0.6f * (a.Top - a.Eye).magnitude, 2);
            bool all = true;
            var lines = new List<string>();
            foreach (float deg in new[] { 0f, KitBuilder.GimbalClearanceDeg, -KitBuilder.GimbalClearanceDeg })
            {
                gT.localRotation = Quaternion.AngleAxis(deg, a.Pin) * rest;
                yield return new WaitForSecondsRealtime(0.8f);
                all &= ActuatorsFollow(m, out float sep, out float stat, out float len, out float _, out string info);
                lines.Add(string.Format(Inv, "{0:+0;-0;0}°: gap {1:F5} m (static {2:F4}), length {3:F4}", deg, sep, stat, len));
                yield return Shot(string.Format(Inv, "gimbal{0:+0;-0;0}_", deg) + tag);
            }
            gT.localRotation = rest;
            if (gm != null) gm.enabled = true;
            yield return null;
            Check("visual.gimbal." + tag, all, string.Join("; ", lines));
        }

        // ------------------------------------------------------------------ visual checks (Phase 3)

        List<CP> BigTankCraft(string probe)
        {
            string tank = FirstAvailable("Rockomax64.BW", "Rockomax64_BW", "Rockomax32.BW", "Rockomax32_BW", "fuelTank");
            return new List<CP>
            {
                new CP(probe),
                new CP(tank) { parent = 0, pNode = "bottom", myNode = "top" },
                new CP(PartName) { parent = 1, pNode = "bottom", myNode = "top" },
            };
        }

        IEnumerator LaunchFrozen(string craftPath)
        {
            FlightDriver.StartWithNewLaunch(craftPath, "Squad/Flags/default", "LaunchPad", new VesselCrewManifest());
            yield return WaitScene(GameScenes.FLIGHT);
            yield return WaitMeshesReady();
            float t0 = Time.realtimeSinceStartup;
            while ((FlightGlobals.ActiveVessel == null || FlightGlobals.ActiveVessel.packed) && Time.realtimeSinceStartup - t0 < 60) yield return null;
            yield return new WaitForSecondsRealtime(3f);
            // lift the craft clear of the pad and its flame trench, then freeze it
            Vessel v = FlightGlobals.ActiveVessel;
            v.SetPosition(v.transform.position + (v.transform.position - v.mainBody.position).normalized * 15f);
            foreach (Part p in v.parts) if (p.Rigidbody != null) p.Rigidbody.isKinematic = true;
            CheatOptions.InfinitePropellant = true;   // stock tanks hold no LqdMethane/LqdHydrogen
            yield return new WaitForSecondsRealtime(1f);
            // A high sun leaves the engine in the tank's shadow: move the clock to a low sun (~22 degrees) that
            // rakes in under the tank, so the shots show the materials in direct light.
            Vector3d up = (v.GetWorldPos3D() - v.mainBody.position).normalized;
            double ut0 = Planetarium.GetUniversalTime(), bestUT = ut0, bestErr = double.MaxValue;
            for (int k = 0; k < 36; k++)
            {
                double ut = ut0 + k * 600;
                Planetarium.SetUniversalTime(ut);
                yield return new WaitForFixedUpdate();
                yield return null;
                Vector3d toSun = (Planetarium.fetch.Sun.position - v.GetWorldPos3D()).normalized;
                double elev = Math.Asin(Vector3d.Dot(toSun, up)) * 180 / Math.PI;
                if (Math.Abs(elev - 22) < bestErr) { bestErr = Math.Abs(elev - 22); bestUT = ut; }
            }
            Planetarium.SetUniversalTime(bestUT);
            yield return new WaitForSecondsRealtime(0.5f);
            Vector3d sunNow = (Planetarium.fetch.Sun.position - v.GetWorldPos3D()).normalized;
            Info("visual.sun", $"sun elevation {Math.Asin(Vector3d.Dot(sunNow, up)) * 180 / Math.PI:F1} deg at UT {bestUT:F0} (+{bestUT - ut0:F0} s)");
        }

        /// <summary>Orbit a transform (the bell) instead of the part origin, which sits at the top of the engine.</summary>
        void CameraOn(Transform t, float distance, float pitchDeg, float hdgDeg)
        {
            var mi = typeof(FlightCamera).GetMethod("SetTargetTransform", new[] { typeof(Transform) });
            mi?.Invoke(mi.IsStatic ? null : FlightCamera.fetch, new object[] { t });
            Camera(distance, pitchDeg, hdgDeg);
        }

        void Camera(float distance, float pitchDeg, float hdgDeg, Part target = null)
        {
            FlightCamera c = FlightCamera.fetch;
            if (target != null) FlightCamera.SetTarget(target);
            c.SetDistance(distance);
            c.camPitch = pitchDeg * Mathf.Deg2Rad;
            c.camHdg = hdgDeg * Mathf.Deg2Rad;
        }

        IEnumerator Visual()
        {
            yield return NewGame("procengines_visual", Game.Modes.SANDBOX);
            yield return EnterEditor();
            string probe = FirstAvailable("probeCoreOcto", "probeCoreOcto.v2");
            var combos = new[]
            {
                ("KeroloxGG", D("Kerolox", "GasGenerator", 900, 97, 16)),
                ("KeroloxSC", D("Kerolox", "StagedCombustion", 2000, 250, 37)),
                ("MethaloxFFSC", D("Methalox", "FullFlowStaged", 2300, 300, 34)),
                ("HydroloxExpRad", D("Hydrolox", "Expander", 110, 44, 250, true)),
                ("KeroloxGGVacRad", D("Kerolox", "GasGenerator", 980, 97, 150, true)),
                ("KeroloxPF", D("Kerolox", "PressureFed", 78, 21, 40)),
            };
            var pieces = new[] { "turbopump_dual", "turbopump_single", "preburner", "gas_generator", "valve", "actuator", "dome_inlet", "injector_ring" };
            var loaded = pieces.Select(n => KitAssets.Get(n)).ToList();
            KitMesh dual = loaded[0], single = loaded[1], act = loaded[5];
            Check("visual.kit_assets", loaded.All(k => k != null) && KitAssets.Atlas() != null && dual.Anchors.ContainsKey("ox_out") && single.Anchors.ContainsKey("out") && act.Anchors.ContainsKey("zone1_lo"),
                $"{string.Join(", ", pieces.Zip(loaded, (n, k) => $"{n} {k?.Positions.Length}v/{k?.Anchors.Count}a"))}; atlas {KitAssets.Atlas()?.format} {KitAssets.Atlas()?.width}px");
            Info("visual.textures", EngineMaterials.TextureReport + "; atlas normal " + KitAssets.AtlasNormal()?.format);
            foreach (var (tag, d) in combos)
            {
                if (HighLogic.LoadedScene != GameScenes.EDITOR) yield return EnterEditor();
                string path = WriteCraft("PE_Vis_" + tag, BigTankCraft(probe));
                yield return LoadCraft(path);
                ModuleProcEngine m = Engine(EditorLogic.fetch.ship.parts);
                m.SetDesign(d);
                yield return new WaitForSecondsRealtime(1.5f);
                Info("visual." + tag, $"kit verts {m.part.transform.Find("model/peRoot/peKit").GetComponent<MeshFilter>().sharedMesh.vertexCount}, exhaust {m.Kit?.HasExhaust}, tubes {m.Stats.ExitDiameter:F2} m exit, materials {EngineMaterials.ShaderReport}");
                {
                    var lg = m.part.transform.Find("model/peRoot").GetComponent<LODGroup>();
                    var mfs = m.part.transform.Find("model/peRoot").GetComponentsInChildren<MeshFilter>(true);
                    Info("visual.lod." + tag, $"lodGroup {(lg != null ? lg.lodCount.ToString() : "none")}, lodBias {QualitySettings.lodBias}; meshes {string.Join(", ", mfs.Select(f => f.name + ":" + (f.sharedMesh != null ? f.sharedMesh.vertexCount : -1)))}; build {m.LastBuildMs:F1} ms");
                }
                yield return CheckActuators(m, tag);
                EditorLogic.fetch.ship.SaveShip().Save(path);
                yield return LaunchFrozen(path);
                m = Engine(FlightGlobals.ActiveVessel.parts);
                float size = (float)Math.Max(m.Stats.TotalLength, m.Stats.ExitDiameter);
                GameEvents.onHideUI.Fire();
                Camera(size * 2.0f, -6, 35, m.part);
                yield return new WaitForSecondsRealtime(1.5f);
                yield return Shot("cold_" + tag);
                Transform bellT = m.part.transform.Find("model/peRoot/peGimbal");
                CameraOn(bellT, size * 1.1f, 4, 60);
                yield return new WaitForSecondsRealtime(1f);
                yield return Shot("detail_" + tag);
                CameraOn(bellT, size * 1.1f, 4, 240);
                yield return new WaitForSecondsRealtime(1f);
                yield return Shot("detail2_" + tag);
                // orbit: four headings at a slight downward look, so at least one side is sunlit, aimed halfway
                // between the mount and the exit so the whole engine (lip, manifold, actuators) is in frame
                var mid = new GameObject("peCamTarget").transform;
                mid.SetParent(m.part.transform, false);
                AttachNode bottomNode = m.part.FindAttachNode("bottom");
                mid.localPosition = bottomNode != null ? bottomNode.position * 0.5f : Vector3.zero;
                foreach (int hdg in new[] { 0, 90, 180, 270 })
                {
                    CameraOn(mid, size * 1.3f, 10, hdg);
                    yield return new WaitForSecondsRealtime(0.6f);
                    yield return Shot($"orbit{hdg}_" + tag);
                }
                // a typical flight-camera view: further out, from below and to the side
                CameraOn(mid, size * 3f, -15, 35);
                yield return new WaitForSecondsRealtime(0.6f);
                yield return Shot("flight_" + tag);
                yield return GimbalShots(m, tag);
                Camera(size * 2.0f, -6, 35, m.part);
                Destroy(mid.gameObject);
                var e = m.part.FindModuleImplementing<ModuleEnginesFX>();
                FlightInputHandler.state.mainThrottle = 1f;
                e.Activate();
                yield return new WaitForSecondsRealtime(5f);
                Camera(size * 3.5f, -4, 35, m.part);
                yield return new WaitForSecondsRealtime(1f);
                yield return Shot("hot_" + tag);
                MeshRenderer bellR = m.part.transform.Find("model/peRoot/peGimbal/peBell").GetComponent<MeshRenderer>();
                var blk = new MaterialPropertyBlock();
                bellR.GetPropertyBlock(blk, EngineGeometry.MatRadiative);
                Color applied = blk.GetColor("_EmissiveColor");
                Vector4 st = blk.GetVector("_Emissive_ST");
                bool burning = e.EngineIgnited && e.finalThrust > 0;
                float thrust = e.finalThrust;
                // shut down: the plume goes out within a second, the metal stays hot for several
                e.Shutdown();
                FlightInputHandler.state.mainThrottle = 0f;
                CameraOn(bellT, size * 1.3f, -8, 35);
                yield return new WaitForSecondsRealtime(1.5f);
                yield return Shot("afterglow_" + tag);
                GameEvents.onShowUI.Fire();
                Check("visual." + tag, burning && m.WaterfallStatus.Contains("effects") && (!d.RadiativeExtension || (applied.maxColorComponent > 0.2f && st.y > 0 && st.y < 1)),
                    $"burning {thrust:F0} kN, glow {m.GlowTemperature:F0} K, extension block emissive {applied} ST {st} (extension {d.RadiativeExtension}), duct {m.DuctGlowColor}, waterfall {m.WaterfallStatus}");
            }
        }

        /// <summary>Calibration rig: each Waterfall template at scale 1 on a nozzle with a 1.000 m exit, side view.</summary>
        IEnumerator Plumes()
        {
            yield return NewGame("procengines_plumes", Game.Modes.SANDBOX);
            yield return EnterEditor();
            string probe = FirstAvailable("probeCoreOcto", "probeCoreOcto.v2");
            string path = WriteCraft("PE_Plumes", BigTankCraft(probe));
            yield return LoadCraft(path);
            ModuleProcEngine m = Engine(EditorLogic.fetch.ship.parts);
            // find the thrust that gives a 1.000 m exit at Pc 70, eps 12 (De ~ sqrt(F))
            double f = 300;
            for (int i = 0; i < 6; i++)
            {
                EngineStats s = EngineModel.Compute(D("Kerolox", "GasGenerator", f, 30, 12), KspData.Data);
                f *= Math.Pow(1.0 / s.ExitDiameter, 2);
            }
            m.SetDesign(D("Kerolox", "GasGenerator", f, 30, 12));
            yield return new WaitForSecondsRealtime(1.5f);
            Info("plumes", $"rig engine {f:F1} kN, exit {m.Stats.ExitDiameter:F4} m");
            EditorLogic.fetch.ship.SaveShip().Save(path);
            yield return LaunchFrozen(path);
            m = Engine(FlightGlobals.ActiveVessel.parts);
            var e = m.part.FindModuleImplementing<ModuleEnginesFX>();
            FlightInputHandler.state.mainThrottle = 1f;
            e.Activate();
            yield return new WaitForSecondsRealtime(2f);
            foreach (Plume p in KspData.Data.Plumes.Where(x => x.Role != "exhaust").Concat(KspData.Data.Plumes.Where(x => x.Role == "exhaust")))
            {
                string status = "";
                yield return StartCoroutine(Integrations.ApplyWaterfall(m, new List<Integrations.PlumeSpec> { new Integrations.PlumeSpec { Template = p.Template, ParentTransform = ModuleProcEngine.ThrustName, Scale = 1, LengthScale = 1 } }, s => status = s));
                yield return new WaitForSecondsRealtime(3f);
                GameEvents.onHideUI.Fire();
                Camera(5f, 0, 90, m.part);
                yield return new WaitForSecondsRealtime(1f);
                yield return Shot($"plume_{p.Propellant}_{p.Role}");
                GameEvents.onShowUI.Fire();
                Info("plumes." + p.Template, status);
            }
            FlightInputHandler.state.mainThrottle = 0f;
        }

        // ------------------------------------------------------------------ LunaMultiplayer, two clients

        static string FlagDir => Path.GetFullPath(Path.Combine(KSPUtil.ApplicationRootPath, "../_dev/ProceduralEngines/tests/ksp/flags"));
        static void Flag(string name, string content) { Directory.CreateDirectory(FlagDir); File.WriteAllText(Path.Combine(FlagDir, name), content); }

        Type FindType(string full)
        {
            foreach (var a in AssemblyLoader.loadedAssemblies) { try { Type t = a.assembly.GetType(full); if (t != null) return t; } catch { } }
            return null;
        }

        IEnumerator LmpConnect()
        {
            // the local test server's password, from this client's own LMP server list
            string pw = "";
            var x = new System.Xml.XmlDocument();
            x.Load(Path.Combine(KSPUtil.ApplicationRootPath, "GameData/LunaMultiplayer/Data/settings.xml"));
            foreach (System.Xml.XmlNode e in x.GetElementsByTagName("ServerEntry")) if (e["Address"]?.InnerText == "127.0.0.1") pw = e["Password"]?.InnerText ?? "";
            FindType("LmpClient.Network.NetworkConnection").GetMethod("ConnectToServer", new[] { typeof(string), typeof(int), typeof(string) }).Invoke(null, new object[] { "127.0.0.1", 5525, pw });
            yield return WaitScene(GameScenes.SPACECENTER, 180);
            yield return new WaitForSecondsRealtime(8f);
            Info("lmp", $"scene {HighLogic.LoadedScene}, mode {HighLogic.CurrentGame?.Mode}, vessels {FlightGlobals.Vessels.Count}");
        }

        /// <summary>Flight builds LOD0 a few frames after OnStart (ModuleProcEngine.Lod0Pending): wait until every
        /// loaded engine has its full meshes, so mesh readouts compare like with like.</summary>
        IEnumerator WaitMeshesReady(float timeout = 20f)
        {
            float t0 = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - t0 < timeout)
            {
                bool pending = false;
                foreach (Vessel v in FlightGlobals.VesselsLoaded)
                    foreach (Part p in v.parts)
                        foreach (ModuleProcEngine e in p.FindModulesImplementing<ModuleProcEngine>())
                            pending |= e.Lod0Pending;
                if (!pending) yield break;
                yield return null;
            }
            Info("meshes", $"LOD0 still pending after {timeout:F0} s");
        }

        static string Describe(ModuleProcEngine m)
        {
            var e = m.part.FindModuleImplementing<ModuleEnginesFX>();
            Mesh bell = m.part.transform.Find("model/peRoot/peGimbal/peBell").GetComponent<MeshFilter>().sharedMesh;
            Mesh kit = m.part.transform.Find("model/peRoot/peKit").GetComponent<MeshFilter>().sharedMesh;
            return string.Format(Inv, "{0}/{1} F={2:R} Isp0={3:R} Isp1={4:R} flow={5:R} De={6:R} node={7:R} bell={8} {9} kit={10} {11} mass={12:R} props={13}",
                m.Design.Propellant, m.Design.Cycle, e.maxThrust, e.atmosphereCurve.Evaluate(0f), e.atmosphereCurve.Evaluate(1f), e.maxFuelFlow, m.Stats.ExitDiameter,
                m.part.FindAttachNode("bottom").position.y, bell.vertexCount, bell.bounds.size.ToString("R"), kit.vertexCount, kit.bounds.size.ToString("R"), m.part.mass,
                string.Join("+", e.propellants.Select(p => p.name + ":" + p.ratio.ToString("R", Inv))));
        }

        IEnumerator LmpA()
        {
            yield return LmpConnect();
            if (HighLogic.LoadedScene != GameScenes.SPACECENTER) { Check("lmp", false, "A could not join"); Flag("A_launched.txt", "FAILED"); yield break; }
            yield return EnterEditor();
            string pod = FirstAvailable("mk1pod.v2", "probeCoreOcto");
            string path = WriteCraft("PE_LMP", new List<CP> { new CP(pod), new CP(PartName) { parent = 0, pNode = "bottom", myNode = "top" } });
            yield return LoadCraft(path);
            ModuleProcEngine m = Engine(EditorLogic.fetch.ship.parts);
            m.SetDesign(D("Kerolox", "PressureFed", 77.7, 21.3, 28.5));   // allowed by the start node in career
            m.designName = "LMP test";
            yield return new WaitForSecondsRealtime(1.5f);
            Info("lmpA.caps", $"violations [{string.Join("; ", m.Violations)}]");
            EditorLogic.fetch.ship.SaveShip().Save(path);
            FlightDriver.StartWithNewLaunch(path, "Squad/Flags/default", "LaunchPad", new VesselCrewManifest());
            yield return WaitScene(GameScenes.FLIGHT);
            yield return WaitMeshesReady();
            float t0 = Time.realtimeSinceStartup;
            while ((FlightGlobals.ActiveVessel == null || FlightGlobals.ActiveVessel.packed) && Time.realtimeSinceStartup - t0 < 60) yield return null;
            yield return new WaitForSecondsRealtime(4f);
            Vessel v = FlightGlobals.ActiveVessel;
            m = Engine(v.parts);
            string mine = Describe(m);
            Info("lmpA", mine);
            yield return Shot("lmpA_flight");
            yield return new WaitForSecondsRealtime(20f);   // let LMP upload the proto
            var pn = new ConfigNode();
            v.BackupVessel().Save(pn);
            ConfigNode mod = pn.GetNodes("PART").SelectMany(p => p.GetNodes("MODULE")).FirstOrDefault(n => n.GetValue("name") == "ModuleProcEngine");
            Flag("A_launched.txt", mine + "\n" + v.id + "\n" + mod);
            t0 = Time.realtimeSinceStartup;
            while (!File.Exists(Path.Combine(FlagDir, "B_done.txt")) && Time.realtimeSinceStartup - t0 < 720) yield return new WaitForSecondsRealtime(1f);
            Check("lmpA.done", File.Exists(Path.Combine(FlagDir, "B_done.txt")), "B finished");
        }

        IEnumerator LmpB()
        {
            float t0 = Time.realtimeSinceStartup;
            while (!File.Exists(Path.Combine(FlagDir, "A_launched.txt")) && Time.realtimeSinceStartup - t0 < 900) yield return new WaitForSecondsRealtime(1f);
            string[] a = File.ReadAllText(Path.Combine(FlagDir, "A_launched.txt")).Split('\n');
            if (a[0].StartsWith("FAILED")) { Check("lmp", false, "A failed"); yield break; }
            Guid id = new Guid(a[1].Trim());
            yield return LmpConnect();
            if (HighLogic.LoadedScene != GameScenes.SPACECENTER) { Check("lmp", false, "B could not join"); Flag("B_done.txt", "fail"); yield break; }
            Vessel v = null;
            t0 = Time.realtimeSinceStartup;
            while ((v = FlightGlobals.Vessels.FirstOrDefault(x => x.id == id)) == null && Time.realtimeSinceStartup - t0 < 120) yield return new WaitForSecondsRealtime(1f);
            if (v == null) { Check("lmp", false, "A's vessel never arrived"); Flag("B_done.txt", "fail"); yield break; }
            ConfigNode modNode = v.protoVessel.protoPartSnapshots.SelectMany(p => p.modules).FirstOrDefault(x => x.moduleName == "ModuleProcEngine")?.moduleValues;
            ConfigNode aMod = ConfigNode.Parse(string.Join("\n", a.Skip(2)));
            ConfigNode aNode = aMod?.nodes.Count > 0 ? aMod.nodes[0] : aMod;
            bool same = modNode != null && aNode != null && modNode.GetNode("DESIGN")?.ToString() == aNode.GetNode("DESIGN")?.ToString()
                        && modNode.GetNode("STATS")?.ToString() == aNode.GetNode("STATS")?.ToString();
            Check("lmp.proto", same, "B's ProtoVessel DESIGN/STATS identical to A's");
            GamePersistence.SaveGame("persistent", HighLogic.SaveFolder, SaveMode.OVERWRITE);
            int idx = HighLogic.CurrentGame.flightState.protoVessels.FindIndex(p => p.vesselID == id);
            FlightDriver.StartAndFocusVessel("persistent", idx);
            yield return WaitScene(GameScenes.FLIGHT, 120);
            yield return WaitMeshesReady();
            t0 = Time.realtimeSinceStartup;
            while ((FlightGlobals.ActiveVessel == null || FlightGlobals.ActiveVessel.packed) && Time.realtimeSinceStartup - t0 < 60) yield return null;
            yield return new WaitForSecondsRealtime(4f);
            ModuleProcEngine m = FlightGlobals.ActiveVessel == null ? null : Engine(FlightGlobals.ActiveVessel.parts);
            string mine = m == null ? "no module" : Describe(m);
            Info("lmpB", mine);
            Check("lmp.loaded", mine == a[0], "B's loaded engine (mesh, kit, stats, propellants) identical to A's\n  A: " + a[0] + "\n  B: " + mine);
            yield return Shot("lmpB_flight");
            Flag("B_done.txt", "ok");
        }

        IEnumerator Career()
        {
            yield return NewGame("procengines_career", Game.Modes.CAREER);
            yield return EnterEditor();
            yield return LoadCraft(WriteCraft("PE_Career", Stack(false)));
            ModuleProcEngine m = Engine(EditorLogic.fetch.ship.parts);
            yield return new WaitForSecondsRealtime(1.5f);
            TechCaps c = m?.Caps;
            Check("career.start", c != null && !c.Unlimited && c.Cycles.SetEquals(new[] { "PressureFed" }) && c.Propellants.SetEquals(new[] { "Kerolox" }) && m.Violations.Any(x => x.Contains("GasGenerator")),
                $"caps: cycles {string.Join(",", c?.Cycles ?? new HashSet<string>())} propellants {string.Join(",", c?.Propellants ?? new HashSet<string>())} Pc<={c?.MaxPc} eps<={c?.MaxEps} F<={c?.MaxThrust} level {c?.TechLevel}; violations [{string.Join("; ", m?.Violations ?? new List<string>())}]");
            double pcBefore = m.Design.Pc;
            string cycleBefore = m.Design.Cycle;
            DesignerWindow.Open(m);
            yield return new WaitForSecondsRealtime(1.5f);
            yield return Shot("designer_start");
            Check("career.designerNoSilentEdit", m.Design.Pc == pcBefore && m.Design.Cycle == cycleBefore, $"opening the designer left the out-of-caps design alone: Pc {m.Design.Pc}/{pcBefore}, cycle {m.Design.Cycle}");
            DesignerWindow.Open(null);

            // editor launch with a design over the caps: our blocking pre-flight test must stop it
            EditorLogic.fetch.launchVessel();
            yield return new WaitForSecondsRealtime(2f);
            PopupDialog block = FindObjectsOfType<PopupDialog>().FirstOrDefault(d => d.dialogToDisplay?.title == "Engine design exceeds researched technology");
            yield return Shot("launch_blocked");
            Check("career.launchBlocked", block != null && HighLogic.LoadedScene == GameScenes.EDITOR,
                $"pre-flight dialog '{block?.dialogToDisplay?.title}' shown, still in {HighLogic.LoadedScene}; message: {block?.dialogToDisplay?.message?.Replace("\n", " | ")}");
            if (block != null) block.Dismiss();
            yield return new WaitForSecondsRealtime(1f);

            // a path that skips the editor checks (FlightDriver = KSC launch / LMP / KCT): the engine must be locked in flight
            string lockPath = Path.Combine(KSPUtil.ApplicationRootPath, "saves", HighLogic.SaveFolder, "Ships", "VAB", "PE_Locked.craft");
            EditorLogic.fetch.ship.SaveShip().Save(lockPath);
            FlightDriver.StartWithNewLaunch(lockPath, "Squad/Flags/default", "LaunchPad", new VesselCrewManifest());
            yield return WaitScene(GameScenes.FLIGHT);
            yield return WaitMeshesReady();
            float tl = Time.realtimeSinceStartup;
            while ((FlightGlobals.ActiveVessel == null || FlightGlobals.ActiveVessel.packed) && Time.realtimeSinceStartup - tl < 60) yield return null;
            yield return new WaitForSecondsRealtime(4f);
            ModuleProcEngine fm = Engine(FlightGlobals.ActiveVessel.parts);
            var fe = fm.part.FindModuleImplementing<ModuleEnginesFX>();
            FlightInputHandler.state.mainThrottle = 1f;
            fe.Activate();
            yield return new WaitForSecondsRealtime(1.5f);
            yield return Shot("flight_locked");
            Check("career.flightLocked", fm.TechLocked && !fe.EngineIgnited && fe.finalThrust == 0,
                $"locked {fm.TechLocked} [{string.Join("; ", fm.FlightViolations)}], ignited {fe.EngineIgnited}, thrust {fe.finalThrust:F1}");
            FlightInputHandler.state.mainThrottle = 0f;
            yield return EnterEditor();
            yield return LoadCraft(WriteCraft("PE_Career2", Stack(false)));
            m = Engine(EditorLogic.fetch.ship.parts);
            yield return new WaitForSecondsRealtime(1.5f);

            // research a few nodes, re-evaluate
            foreach (string node in new[] { "basicRocketry", "generalRocketry", "advRocketry", "propulsionSystems" })
                ResearchAndDevelopment.Instance.UnlockProtoTechNode(new ProtoTechNode { techID = node, state = RDTech.State.Available, scienceCost = 0, partsPurchased = new List<AvailablePart>() });
            TechCaps c2 = KspData.CurrentCaps();
            yield return new WaitForSecondsRealtime(3f);   // module re-checks caps every 2 s
            var passTest = new ProcEngineTechTest(EditorLogic.fetch.ship);
            Check("career.nowLaunchable", passTest.Test() && m.Violations.Count == 0, $"default design after research: pre-flight test passes {passTest.Test()}, module violations [{string.Join("; ", m.Violations)}]");
            Check("career.researched", c2.Cycles.SetEquals(new[] { "PressureFed", "GasGenerator", "Expander" }) && c2.Propellants.Contains("Hydrolox") && c2.RaoBell && c2.TechLevel == "Mid" && c2.MaxPc == 90,
                $"after research: cycles {string.Join(",", c2.Cycles)} propellants {string.Join(",", c2.Propellants)} Pc<={c2.MaxPc} eps<={c2.MaxEps} F<={c2.MaxThrust} level {c2.TechLevel} bell {c2.RaoBell}");
        }
    }
}
