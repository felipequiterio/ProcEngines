using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ProcEngines.Model;
using PropellantData = ProcEngines.Model.Propellant;
using UnityEngine;

namespace ProcEngines
{
    /// <summary>
    /// Designer window (IMGUI, editor only; DESIGN.md 7.1). Talks to the part only through ModuleProcEngine.SetDesign,
    /// so the two-speed update pipeline, symmetry and undo apply unchanged.
    /// Fixed size clamped to the screen (content scrolls); closes with the ✕, the Close button, Esc, or the part-menu
    /// button (which toggles).
    /// </summary>
    [KSPAddon(KSPAddon.Startup.EditorAny, false)]
    public class DesignerWindow : MonoBehaviour
    {
        static DesignerWindow instance;
        static Rect rect = new Rect(-1, 70, Width, 0);   // remembered across opens; x < 0 = place on first open
        const float Width = 470;
        const int WindowId = 0x50E1;
        const string LockId = "ProcEnginesDesigner";
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        ModuleProcEngine target;
        Vector2 scroll;
        Texture2D plot;
        EngineStats plotStats;
        string nameEdit = "", lastSaved = "";
        readonly Dictionary<string, string> editBuffers = new Dictionary<string, string>();
        GUIStyle section, small, valueStyle, warn, bad, okStyle, closeButton, centered;
        static bool finishOpen = true;   // the Appearance section, remembered across opens

        public static bool IsOpen => instance != null && instance.target != null;
        public static ModuleProcEngine Target => instance?.target;
        public static Rect WindowRect => rect;

        /// <summary>Opens on <paramref name="m"/>; null closes.</summary>
        public static void Open(ModuleProcEngine m)
        {
            if (instance == null) return;
            instance.target = m;
            instance.nameEdit = m?.designName ?? "";
            instance.lastSaved = "";
            instance.editBuffers.Clear();
        }

        public static void Close() => Open(null);

        /// <summary>Test hook: scroll the content (pixels from the top).</summary>
        public static void ScrollTo(float y) { if (instance != null) instance.scroll = new Vector2(0, y); }

        /// <summary>Part-menu button: open on this engine, or close if it's already open on it.</summary>
        public static void Toggle(ModuleProcEngine m)
        {
            if (instance == null) return;
            if (instance.target == m) Close(); else Open(m);
        }

        void Awake() { instance = this; }

        void OnDestroy()
        {
            InputLockManager.RemoveControlLock(LockId);
            if (plot != null) Destroy(plot);
            if (instance == this) instance = null;
        }

        void Update()
        {
            if (target != null && (target.part == null || EditorLogic.fetch == null || !EditorLogic.fetch.ship.parts.Contains(target.part))) Close();
            bool over = target != null && rect.Contains(new Vector2(Input.mousePosition.x, Screen.height - Input.mousePosition.y));
            if (over) InputLockManager.SetControlLock(ControlTypes.EDITOR_LOCK, LockId);
            else InputLockManager.RemoveControlLock(LockId);
        }

        void OnGUI()
        {
            if (target == null || target.Design == null) return;
            GUI.skin = HighLogic.Skin;
            InitStyles();
            // Esc closes (unless a text field is being edited)
            if (Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.Escape && GUIUtility.keyboardControl == 0)
            {
                Close();
                Event.current.Use();
                return;
            }
            float height = Mathf.Min(760, Screen.height - 100);
            if (rect.x < 0) rect.x = Screen.width - Width - 20;
            rect.width = Width;
            rect.height = height;
            rect = GUI.Window(WindowId, rect, Draw, "Engine designer");
            // keep the title bar reachable
            rect.x = Mathf.Clamp(rect.x, 0, Screen.width - 60);
            rect.y = Mathf.Clamp(rect.y, 0, Screen.height - 40);
        }

        void InitStyles()
        {
            if (section != null) return;
            section = new GUIStyle(HighLogic.Skin.label) { fontStyle = FontStyle.Bold, fontSize = 13 };
            section.normal.textColor = new Color(1f, 0.82f, 0.35f);
            small = new GUIStyle(HighLogic.Skin.label) { fontSize = 11, wordWrap = true };
            small.normal.textColor = new Color(0.75f, 0.78f, 0.8f);
            valueStyle = new GUIStyle(HighLogic.Skin.label) { alignment = TextAnchor.MiddleRight };
            valueStyle.normal.textColor = Color.white;
            warn = new GUIStyle(small) { fontSize = 12 }; warn.normal.textColor = new Color(1f, 0.8f, 0.25f);
            bad = new GUIStyle(small) { fontSize = 12 }; bad.normal.textColor = new Color(1f, 0.42f, 0.42f);
            okStyle = new GUIStyle(small) { fontSize = 12 }; okStyle.normal.textColor = new Color(0.45f, 1f, 0.5f);
            closeButton = new GUIStyle(HighLogic.Skin.button) { fontSize = 14, fontStyle = FontStyle.Bold, padding = new RectOffset(0, 0, 0, 2) };
            centered = new GUIStyle(HighLogic.Skin.label) { alignment = TextAnchor.MiddleCenter };
            centered.normal.textColor = Color.white;
        }

        void Draw(int id)
        {
            if (GUI.Button(new Rect(rect.width - 28, 4, 22, 20), new GUIContent("✕", "Close (Esc)"), closeButton)) { Close(); return; }

            ModuleProcEngine m = target;
            EngineDesign d = m.Design;
            ModelData data = KspData.Data;
            TechCaps caps = m.Caps;
            EngineStats s = m.Stats;
            EngineDesign next = Serialization.LoadDesign(Serialization.SaveDesign(d));
            bool changed = false;

            GUILayout.Space(6);
            // ---- name + status
            GUILayout.BeginHorizontal();
            GUILayout.Label("Name", GUILayout.Width(50));
            nameEdit = GUILayout.TextField(nameEdit, 48, GUILayout.Width(250));
            if (nameEdit != m.designName) m.designName = nameEdit;
            GUILayout.FlexibleSpace();
            if (m.Violations.Count > 0) GUILayout.Label(new GUIContent("Not launchable", "Uses technology not researched yet"), bad);
            else if (s != null && s.Warnings.Count > 0) GUILayout.Label(s.Warnings.Count + " warning" + (s.Warnings.Count > 1 ? "s" : ""), warn);
            else GUILayout.Label("OK", okStyle);
            GUILayout.EndHorizontal();

            scroll = GUILayout.BeginScrollView(scroll, false, true, GUIStyle.none, HighLogic.Skin.verticalScrollbar, GUILayout.ExpandHeight(true));

            // ---- propellant & cycle
            Section("Propellant and cycle");
            GUILayout.BeginHorizontal();
            foreach (string p in new[] { "Kerolox", "Methalox", "Hydrolox" }.Where(data.Propellants.ContainsKey))
            {
                bool allowed = caps == null || caps.Unlimited || caps.Propellants.Contains(p);
                GUI.enabled = allowed;
                var c = new GUIContent(p, allowed ? PropellantTip(p) : "Not researched yet");
                if (GUILayout.Toggle(d.Propellant == p, c, HighLogic.Skin.button) && d.Propellant != p) { next.Propellant = p; next.Of = double.NaN; changed = true; }
            }
            GUI.enabled = true;
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            foreach (Cycle c in data.Cycles.Values)
            {
                bool researched = caps == null || caps.Unlimited || caps.Cycles.Contains(c.Name);
                bool fits = c.Propellants.Length == 0 || c.Propellants.Contains(next.Propellant);
                GUI.enabled = researched && fits;
                string tip = !researched ? "Not researched yet" : !fits ? $"Not available with {next.Propellant}" : CycleTip(c.Name);
                if (GUILayout.Toggle(d.Cycle == c.Name, new GUIContent(Short(c.Name), tip), HighLogic.Skin.button) && d.Cycle != c.Name) { next.Cycle = c.Name; changed = true; }
            }
            GUI.enabled = true;
            GUILayout.EndHorizontal();

            PropellantData prop = data.Propellants[next.Propellant];
            Cycle cyc = data.Cycles[next.Cycle];
            double pcHi = Math.Min(Math.Min(cyc.PcMax, prop.PcMax), caps?.MaxPc ?? double.PositiveInfinity);
            double pcLo = Math.Min(Math.Max(cyc.PcMin, prop.PcMin), pcHi);

            // ---- chamber
            Section("Chamber");
            changed |= Row("thrust", "Thrust (vacuum)", "kN", ref next.ThrustVac, 1, Math.Min(25000, caps?.MaxThrust ?? 25000), true, 0, "Vacuum thrust. Exit size grows with √thrust.");
            changed |= Row("pc", "Chamber pressure", "bar", ref next.Pc, pcLo, pcHi, false, 0, $"{cyc.Name}: {cyc.PcMin:F0}–{cyc.PcMax:F0} bar. Higher = smaller, higher Isp, heavier pumps.");
            bool defaultOf = double.IsNaN(next.Of);
            GUILayout.BeginHorizontal();
            bool useDefault = GUILayout.Toggle(defaultOf, new GUIContent($" Default mixture ratio ({prop.OfDefault:F2})", "Off: set O/F yourself. Tanks still drain at the stock ratio."));
            GUILayout.EndHorizontal();
            if (useDefault != defaultOf) { next.Of = useDefault ? double.NaN : prop.OfDefault; changed = true; }
            if (!double.IsNaN(next.Of)) changed |= Row("of", "Mixture ratio O/F", "", ref next.Of, prop.OfMin, prop.OfMax, false, 2, "Oxidizer/fuel mass ratio for the combustion model.");

            // ---- nozzle
            Section("Nozzle");
            changed |= Row("eps", "Area ratio", "", ref next.Eps, 2, Math.Min(prop.EpsMax, caps?.MaxEps ?? 400), true, 1, "Exit area / throat area. High = vacuum engine (separates at sea level).");
            bool raoAllowed = caps == null || caps.Unlimited || caps.RaoBell;
            GUI.enabled = raoAllowed;
            bool bell = GUILayout.Toggle(!next.Conical, new GUIContent(" Rao bell nozzle (off: 15° cone)", raoAllowed ? "Shorter and more efficient than a 15° cone" : "Not researched yet: conical only"));
            GUI.enabled = true;
            if (bell == next.Conical) { next.Conical = !bell; changed = true; }
            bool radAllowed = caps == null || caps.Unlimited || caps.RadiativeExtension;
            GUI.enabled = radAllowed;
            bool rad = GUILayout.Toggle(next.RadiativeExtension, new GUIContent(" Radiatively cooled extension (vacuum)", radAllowed ? "Light uncooled nozzle extension for vacuum engines (glows when running)" : "Not researched yet"));
            GUI.enabled = true;
            if (rad != next.RadiativeExtension) { next.RadiativeExtension = rad; changed = true; }
            if (!next.Conical) changed |= Row("lf", "Bell length", "% cone", ref next.LengthFraction, 0.6, 1.0, false, 0, "Length as a fraction of an equivalent 15° cone. Longer = slightly more Isp, more mass.", 100);
            changed |= Row("gimbal", "Gimbal range", "°", ref next.GimbalRange, 0, 8, false, 1, "Thrust vectoring range.");

            if (changed) m.SetDesign(next);

            // ---- appearance: materials only
            GUILayout.Space(4);
            finishOpen = GUILayout.Toggle(finishOpen, new GUIContent((finishOpen ? "▾ " : "▸ ") + "Appearance (visual only)", "Materials and paint. No effect on performance, mass or cost."), section);
            if (finishOpen) DrawFinish(m);

            // ---- performance
            if (s != null)
            {
                Section("Performance");
                Grid("Isp vacuum", $"{s.IspVac:F1} s", "Isp sea level", $"{s.IspSL:F1} s");
                Grid("Thrust vacuum", $"{s.ThrustVac:F1} kN", "Thrust sea level", $"{s.ThrustSL:F1} kN");
                Grid("Mass", $"{s.Mass * 1000:F0} kg", "TWR vac / SL", $"{s.TwrVac:F0} / {s.ThrustSL / (s.Mass * 9.80665):F0}");
                Grid("Exit diameter", $"{s.ExitDiameter:F2} m", "Length", $"{s.TotalLength:F2} m");
                Grid("Mass flow", $"{s.MassFlow:F1} kg/s", "Exit pressure", $"{s.ExitPressure:F2} bar");
                Grid("Throttle", $"{s.ThrottleMin:P0}–100 %", "Ignitions", s.Ignitions.ToString(Inv));
                if (s.PumpPower > 0) Grid("Pump power", $"{s.PumpPower / 1000:F2} MW", "Turbine dump", s.TurbineFlowFraction > 0 ? $"{s.TurbineFlowFraction:P1}" : "none");
                Grid("Tech level", d.Tech, "Cost", $"{s.Cost:F0}");
                if (caps == null || caps.Unlimited) GUILayout.Label(caps == null ? "Tech limits: waiting for R&D" : "Tech limits: none (sandbox)", small);
                GUILayout.Label("Mass: " + string.Join(", ", s.MassBreakdown.Where(kv => kv.Value > 0.5).OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key} {kv.Value:F0}")) + " kg", small);

                Section("Isp vs ambient pressure");
                DrawPlot(s);

                if (s.Warnings.Count > 0 || m.Violations.Count > 0) Section("Messages");
                foreach (string w in s.Warnings) GUILayout.Label("Warning: " + w, warn);
                foreach (string v in m.Violations) GUILayout.Label("Not researched: " + v, bad);
            }
            GUILayout.EndScrollView();

            // ---- footer (always visible)
            GUILayout.Label(string.IsNullOrEmpty(GUI.tooltip) ? "Tip: drag the coloured handles on the engine to resize it." : GUI.tooltip, small, GUILayout.Height(30));
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(new GUIContent("Save to library", "Saves a one-part subassembly (VAB ▸ Subassemblies)"))) lastSaved = DesignLibrary.SaveAsSubassembly(m);
            if (GUILayout.Button("Close", GUILayout.Width(90))) Close();
            GUILayout.EndHorizontal();
            if (!string.IsNullOrEmpty(lastSaved)) GUILayout.Label(lastSaved, small);
            GUI.DragWindow(new Rect(0, 0, rect.width - 30, 24));
        }

        /// <summary>Preset buttons (the one the finish matches is down; none for a custom finish), then one row per
        /// part group that steps through its options. Changes go through ModuleProcEngine.SetFinish (symmetry, undo).</summary>
        void DrawFinish(ModuleProcEngine m)
        {
            EngineFinish f = m.Finish;
            string current = f.PresetName;
            GUILayout.BeginHorizontal();
            GUILayout.Label(new GUIContent("Preset", "Sets every part at once; change single parts below"), small, GUILayout.Width(44));
            foreach (EngineFinish.Preset p in EngineFinish.Presets)
                if (GUILayout.Toggle(current == p.Name, new GUIContent(p.Name, p.Tip), HighLogic.Skin.button) && current != p.Name)
                    m.SetFinish(EngineFinish.FromPreset(p.Name));
            GUILayout.EndHorizontal();
            if (current == EngineFinish.Custom) GUILayout.Label("Custom finish (matches no preset)", small);
            bool extension = m.Geometry != null && m.Geometry.HasExtension;
            foreach (EngineFinish.Slot s in EngineFinish.Slots)
            {
                int n = s.Options.Length, i = Math.Max(0, Array.FindIndex(s.Options, o => o.Id == f.Get(s.Id)));
                bool usable = s.Id != "extension" || extension;
                GUI.enabled = usable;
                GUILayout.BeginHorizontal();
                GUILayout.Label(new GUIContent(s.Label, usable ? s.Tip : "This engine has no radiative extension"), GUILayout.Width(96));
                int step = 0;
                if (GUILayout.Button(new GUIContent("<", "Previous"), GUILayout.Width(26))) step = -1;
                EngineFinish.Option o = s.Options[i];
                GUILayout.Label(new GUIContent(o.Label, o.Tip), centered, GUILayout.Width(150));
                if (GUILayout.Button(new GUIContent(">", "Next"), GUILayout.Width(26))) step = 1;
                GUILayout.EndHorizontal();
                GUI.enabled = true;
                if (step != 0)
                {
                    EngineFinish next = f.Copy();
                    next.Set(s.Id, s.Options[(i + step + n) % n].Id);
                    m.SetFinish(next);
                }
            }
        }

        // ------------------------------------------------------------------ widgets

        void Section(string title)
        {
            GUILayout.Space(4);
            GUILayout.Label(title, section);
        }

        void Grid(string l1, string v1, string l2, string v2)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(l1, small, GUILayout.Width(90));
            GUILayout.Label(v1, valueStyle, GUILayout.Width(92));
            GUILayout.Space(14);
            GUILayout.Label(l2, small, GUILayout.Width(90));
            GUILayout.Label(v2, valueStyle, GUILayout.Width(92));
            GUILayout.EndHorizontal();
        }

        /// <summary>Label, slider (linear or log) and a typed number field. Returns true when the player changed the value.
        /// Out-of-range designs are drawn clamped but never rewritten just by drawing.</summary>
        bool Row(string id, string label, string unit, ref double v, double lo, double hi, bool log, int decimals, string tip, double display = 1)
        {
            hi = Math.Max(hi, lo + 1e-6);
            bool changed = false;
            GUILayout.BeginHorizontal();
            GUILayout.Label(new GUIContent(label, tip), GUILayout.Width(118));
            double shownV = Math.Max(lo, Math.Min(hi, v));
            float shown = log ? (float)Math.Log(shownV) : (float)shownV;
            float nv = GUILayout.HorizontalSlider(shown, log ? (float)Math.Log(lo) : (float)lo, log ? (float)Math.Log(hi) : (float)hi, GUILayout.Width(150));
            if (nv != shown)
            {
                double x = log ? Math.Exp(nv) : nv;
                double mag = log ? Math.Pow(10, Math.Floor(Math.Log10(x)) - 2) : Math.Pow(10, -decimals) / display;
                x = Math.Max(lo, Math.Min(hi, Math.Round(x / mag) * mag));
                if (Math.Abs(x - v) > 1e-9) { v = x; changed = true; }
            }
            // typed entry: edits are kept in a buffer while focused; applied when they parse
            string ctrl = "pe_" + id;
            string text = (v * display).ToString("F" + decimals, Inv);
            bool focused = GUI.GetNameOfFocusedControl() == ctrl;
            if (!focused) editBuffers.Remove(id);
            else if (editBuffers.TryGetValue(id, out string buf)) text = buf;
            GUI.SetNextControlName(ctrl);
            string edited = GUILayout.TextField(text, 10, GUILayout.Width(58));
            if (edited != text)
            {
                editBuffers[id] = edited;
                if (double.TryParse(edited, NumberStyles.Float, Inv, out double typed))
                {
                    typed = Math.Max(lo, Math.Min(hi, typed / display));
                    if (Math.Abs(typed - v) > 1e-9) { v = typed; changed = true; editBuffers[id] = edited; }
                }
            }
            GUILayout.Label(unit, small, GUILayout.Width(44));
            GUILayout.EndHorizontal();
            return changed;
        }

        static string Short(string cycle) => cycle switch
        {
            "PressureFed" => "Press.", "GasGenerator" => "GG", "Expander" => "Exp.", "StagedCombustion" => "SC", "FullFlowStaged" => "FFSC", _ => cycle,
        };

        static string CycleTip(string cycle) => cycle switch
        {
            "PressureFed" => "Pressure-fed: no pumps, low Pc, helium bottles; many restarts, deep throttling",
            "GasGenerator" => "Gas generator: open cycle, dumps 2–5 % of flow through the turbine",
            "Expander" => "Expander: turbine driven by heated fuel; light and reliable, thrust-limited",
            "StagedCombustion" => "Staged combustion: no dump, high Pc, heavy turbomachinery",
            "FullFlowStaged" => "Full-flow staged combustion: two preburners, highest Pc",
            _ => cycle,
        };

        static string PropellantTip(string p) => p switch
        {
            "Kerolox" => "RP-1 / LOX: dense, LiquidFuel + Oxidizer",
            "Methalox" => "Methane / LOX: LqdMethane + Oxidizer (3:1)",
            "Hydrolox" => "Hydrogen / LOX: highest Isp, bulky, LqdHydrogen + Oxidizer (15:1)",
            _ => p,
        };

        /// <summary>Isp vs ambient pressure 0–1.5 atm, drawn with KSP's own Hermite evaluation of the curve keys and
        /// scaled to the curve's range; redrawn only when the stats change.</summary>
        void DrawPlot(EngineStats s)
        {
            const int w = 360, h = 120;
            const double pMax = 1.5;
            List<CurveKey> c = s.AtmosphereCurve;
            double Isp(double p)
            {
                for (int i = 0; i < c.Count - 1; i++)
                    if (p >= c[i].Pressure && p <= c[i + 1].Pressure) return CurveKey.Hermite(c[i], c[i + 1], p);
                return 0;
            }
            double top = s.IspVac, bottom = Math.Max(0, Isp(pMax));
            double span = Math.Max(top - bottom, 1);
            double yHi = top + 0.06 * span, yLo = Math.Max(0, bottom - 0.12 * span);
            if (plot == null) plot = new Texture2D(w, h, TextureFormat.RGBA32, false);
            if (!ReferenceEquals(plotStats, s))
            {
                plotStats = s;
                var px = new Color[w * h];
                var bg = new Color(0.07f, 0.08f, 0.09f, 1);
                for (int i = 0; i < px.Length; i++) px[i] = bg;
                var grid = new Color(0.2f, 0.21f, 0.23f, 1);
                for (int k = 1; k < 6; k++) { int x = k * w / 6; for (int y = 0; y < h; y++) px[y * w + x] = grid; }
                for (int k = 1; k < 4; k++) { int y = k * h / 4; for (int x = 0; x < w; x++) px[y * w + x] = grid; }
                int sl = (int)(w * 1.0 / pMax);
                for (int y = 0; y < h; y++) px[y * w + Math.Min(sl, w - 1)] = new Color(0.3f, 0.6f, 1f, 1);
                int prev = -1;
                for (int x = 0; x < w; x++)
                {
                    double isp = Isp(pMax * x / (w - 1));
                    int y = Mathf.Clamp((int)((isp - yLo) / (yHi - yLo) * (h - 1)), 0, h - 1);
                    int a = prev < 0 ? y : Math.Min(prev, y), b = prev < 0 ? y : Math.Max(prev, y);
                    for (int yy = a; yy <= b; yy++)
                        for (int t = -1; t <= 1; t++) px[Mathf.Clamp(yy + t, 0, h - 1) * w + x] = new Color(1f, 0.72f, 0.25f, 1);
                    prev = y;
                }
                plot.SetPixels(px);
                plot.Apply();
            }
            GUILayout.BeginHorizontal();
            GUILayout.BeginVertical(GUILayout.Width(44), GUILayout.Height(h));
            GUILayout.Label(yHi.ToString("F0", Inv) + " s", small);
            GUILayout.FlexibleSpace();
            GUILayout.Label(yLo.ToString("F0", Inv) + " s", small);
            GUILayout.EndVertical();
            GUILayout.Box(plot, GUILayout.Width(w), GUILayout.Height(h));
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            GUILayout.Space(48);
            GUILayout.Label("0 atm (vacuum)", small, GUILayout.Width(w * 0.62f));
            GUILayout.Label("1 atm", small, GUILayout.Width(w * 0.22f));
            GUILayout.Label("1.5", small);
            GUILayout.EndHorizontal();
            GUILayout.Label($"Isp (s); blue line = sea level{(s.SeparatedAtSeaLevel ? "; flow separates in the nozzle at sea level" : "")}", small);
        }
    }
}
