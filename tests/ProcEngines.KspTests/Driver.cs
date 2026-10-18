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
            IEnumerator body = script == "career" ? Career() : script == "lmpA" ? LmpA() : script == "lmpB" ? LmpB() : script == "visual" ? Visual() : script == "plumes" ? Plumes() : Sandbox();
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

            // flight: hydrolox upper-ish engine on a small stack, Waterfall + readouts from STATS
            yield return LoadCraft(WriteCraft("PE_Flight", Stack(false)));
            m = Engine(EditorLogic.fetch.ship.parts);
            m.SetDesign(D("Kerolox", "GasGenerator", 300, 90, 14));
            yield return new WaitForSecondsRealtime(1.5f);
            string flightPath = Path.Combine(KSPUtil.ApplicationRootPath, "saves", HighLogic.SaveFolder, "Ships", "VAB", "PE_Flight.craft");
            EditorLogic.fetch.ship.SaveShip().Save(flightPath);
            EngineStats editorStats = m.Stats;
            FlightDriver.StartWithNewLaunch(flightPath, "Squad/Flags/default", "LaunchPad", new VesselCrewManifest());
            yield return WaitScene(GameScenes.FLIGHT);
            float tf = Time.realtimeSinceStartup;
            while ((FlightGlobals.ActiveVessel == null || FlightGlobals.ActiveVessel.packed) && Time.realtimeSinceStartup - tf < 60) yield return null;
            yield return new WaitForSecondsRealtime(4f);
            Vessel v = FlightGlobals.ActiveVessel;
            m = Engine(v.parts);
            Check("flight.stats", m.Stats.IspVac == editorStats.IspVac && m.Stats.Mass == editorStats.Mass, $"flight STATS identical to editor: Isp {m.Stats.IspVac}/{editorStats.IspVac}");
            yield return CheckApplied(m, "flight", false);
            Info("waterfall", m.WaterfallStatus);
            var e = m.part.FindModuleImplementing<ModuleEnginesFX>();
            FlightInputHandler.state.mainThrottle = 1f;
            e.Activate();
            yield return new WaitForSecondsRealtime(2f);
            Check("flight.burn", e.finalThrust > 0.5f * (float)m.Stats.ThrustSL && m.WaterfallStatus.Contains("effects"), $"finalThrust {e.finalThrust:F1} kN (SL {m.Stats.ThrustSL:F1}); waterfall {m.WaterfallStatus}");
            yield return Shot("flight_burn");
            FlightInputHandler.state.mainThrottle = 0f;
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
            float t0 = Time.realtimeSinceStartup;
            while ((FlightGlobals.ActiveVessel == null || FlightGlobals.ActiveVessel.packed) && Time.realtimeSinceStartup - t0 < 60) yield return null;
            yield return new WaitForSecondsRealtime(3f);
            // lift the craft clear of the pad and its flame trench, then freeze it
            Vessel v = FlightGlobals.ActiveVessel;
            v.SetPosition(v.transform.position + (v.transform.position - v.mainBody.position).normalized * 15f);
            foreach (Part p in v.parts) if (p.Rigidbody != null) p.Rigidbody.isKinematic = true;
            CheatOptions.InfinitePropellant = true;   // stock tanks hold no LqdMethane/LqdHydrogen
            yield return new WaitForSecondsRealtime(1f);
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
            foreach (var (tag, d) in combos)
            {
                if (HighLogic.LoadedScene != GameScenes.EDITOR) yield return EnterEditor();
                string path = WriteCraft("PE_Vis_" + tag, BigTankCraft(probe));
                yield return LoadCraft(path);
                ModuleProcEngine m = Engine(EditorLogic.fetch.ship.parts);
                m.SetDesign(d);
                yield return new WaitForSecondsRealtime(1.5f);
                Info("visual." + tag, $"kit verts {m.part.transform.Find("model/peRoot/peKit").GetComponent<MeshFilter>().sharedMesh.vertexCount}, exhaust {m.Kit?.HasExhaust}, tubes {m.Stats.ExitDiameter:F2} m exit, materials {EngineMaterials.ShaderReport}");
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
                Camera(size * 2.0f, -6, 35, m.part);
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
