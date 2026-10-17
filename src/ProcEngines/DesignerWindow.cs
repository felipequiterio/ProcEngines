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
    /// MVP designer window (IMGUI, editor only; DESIGN.md 7.1). Talks to the part only through
    /// ModuleProcEngine.SetDesign, so the two-speed update pipeline applies unchanged.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.EditorAny, false)]
    public class DesignerWindow : MonoBehaviour
    {
        static DesignerWindow instance;
        ModuleProcEngine target;
        Rect rect = new Rect(Screen.width - 470, 80, 440, 640);
        Vector2 scroll;
        Texture2D plot;
        string nameEdit = "", lastSaved = "";
        const int WindowId = 0x50E1;
        const string LockId = "ProcEnginesDesigner";

        public static void Open(ModuleProcEngine m)
        {
            if (instance == null) return;
            instance.target = m;
            instance.nameEdit = m?.designName ?? "";
            instance.lastSaved = "";
        }

        public static bool IsOpen => instance != null && instance.target != null;
        public static ModuleProcEngine Target => instance?.target;

        void Awake() { instance = this; }

        void OnDestroy()
        {
            InputLockManager.RemoveControlLock(LockId);
            if (plot != null) Destroy(plot);
            if (instance == this) instance = null;
        }

        void Update()
        {
            if (target != null && (target.part == null || EditorLogic.fetch == null || !EditorLogic.fetch.ship.parts.Contains(target.part))) target = null;
            bool over = target != null && rect.Contains(new Vector2(Input.mousePosition.x, Screen.height - Input.mousePosition.y));
            if (over) InputLockManager.SetControlLock(ControlTypes.EDITOR_LOCK, LockId);
            else InputLockManager.RemoveControlLock(LockId);
        }

        void OnGUI()
        {
            if (target == null || target.Design == null) return;
            GUI.skin = HighLogic.Skin;
            rect = GUILayout.Window(WindowId, rect, Draw, "ProcEngines designer");
        }

        void Draw(int id)
        {
            ModuleProcEngine m = target;
            EngineDesign d = m.Design;
            ModelData data = KspData.Data;
            TechCaps caps = m.Caps;
            EngineDesign next = Serialization.LoadDesign(Serialization.SaveDesign(d));
            bool changed = false;

            scroll = GUILayout.BeginScrollView(scroll, GUILayout.Height(rect.height - 40));
            GUILayout.BeginHorizontal();
            GUILayout.Label("Name", GUILayout.Width(80));
            nameEdit = GUILayout.TextField(nameEdit, 48);
            GUILayout.EndHorizontal();
            if (nameEdit != m.designName) m.designName = nameEdit;

            // propellant and cycle pickers; locked choices are shown but disabled
            GUILayout.Label("Propellant");
            GUILayout.BeginHorizontal();
            foreach (string p in new[] { "Kerolox", "Methalox", "Hydrolox" }.Where(data.Propellants.ContainsKey))
            {
                bool allowed = caps == null || caps.Unlimited || caps.Propellants.Contains(p);
                GUI.enabled = allowed;
                if (GUILayout.Toggle(d.Propellant == p, p, HighLogic.Skin.button) && d.Propellant != p) { next.Propellant = p; next.Of = double.NaN; changed = true; }
            }
            GUI.enabled = true;
            GUILayout.EndHorizontal();
            GUILayout.Label("Cycle");
            GUILayout.BeginHorizontal();
            foreach (Cycle c in data.Cycles.Values)
            {
                bool allowed = (caps == null || caps.Unlimited || caps.Cycles.Contains(c.Name)) && (c.Propellants.Length == 0 || c.Propellants.Contains(next.Propellant));
                GUI.enabled = allowed;
                if (GUILayout.Toggle(d.Cycle == c.Name, Short(c.Name), HighLogic.Skin.button) && d.Cycle != c.Name) { next.Cycle = c.Name; changed = true; }
            }
            GUI.enabled = true;
            GUILayout.EndHorizontal();

            PropellantData prop = data.Propellants[next.Propellant];
            Cycle cyc = data.Cycles[next.Cycle];
            double pcHi = Math.Min(Math.Min(cyc.PcMax, prop.PcMax), caps?.MaxPc ?? double.PositiveInfinity);
            double pcLo = Math.Min(Math.Max(cyc.PcMin, prop.PcMin), pcHi);
            changed |= LogSlider("Thrust (vac)", "kN", ref next.ThrustVac, 1, Math.Min(25000, caps?.MaxThrust ?? 25000));
            changed |= Slider("Chamber pressure", "bar", ref next.Pc, pcLo, pcHi, 1);
            changed |= LogSlider("Area ratio", "", ref next.Eps, 2, Math.Min(prop.EpsMax, caps?.MaxEps ?? 400));
            bool defaultOf = double.IsNaN(next.Of);
            GUILayout.BeginHorizontal();
            bool newDefault = GUILayout.Toggle(defaultOf, defaultOf ? $"Mixture ratio: default ({prop.OfDefault:F2})" : "Mixture ratio: custom", GUILayout.Width(250));
            GUILayout.EndHorizontal();
            if (newDefault != defaultOf) { next.Of = newDefault ? double.NaN : prop.OfDefault; changed = true; }
            if (!double.IsNaN(next.Of)) changed |= Slider("O/F", "", ref next.Of, prop.OfMin, prop.OfMax, 0.05);
            bool raoAllowed = caps == null || caps.Unlimited || caps.RaoBell;
            GUI.enabled = raoAllowed;
            bool bell = GUILayout.Toggle(!next.Conical, "Rao bell nozzle (off: 15° cone)");
            GUI.enabled = true;
            if (bell == next.Conical) { next.Conical = !bell; changed = true; }
            if (!next.Conical) changed |= Slider("Bell length", "% of cone", ref next.LengthFraction, 0.6, 1.0, 0.01, 100);
            bool radAllowed = caps == null || caps.Unlimited || caps.RadiativeExtension;
            GUI.enabled = radAllowed;
            bool rad = GUILayout.Toggle(next.RadiativeExtension, "Radiatively cooled nozzle extension (vacuum)");
            GUI.enabled = true;
            if (rad != next.RadiativeExtension) { next.RadiativeExtension = rad; changed = true; }
            changed |= Slider("Gimbal", "°", ref next.GimbalRange, 0, 8, 0.5);

            if (changed) m.SetDesign(next);

            // ---- readouts
            EngineStats s = m.Stats;
            if (s != null)
            {
                GUILayout.Space(6);
                Row("Isp vac / SL", $"{s.IspVac:F1} / {s.IspSL:F1} s" + (s.SeparatedAtSeaLevel ? "  (separates at SL)" : ""));
                Row("Thrust vac / SL", $"{s.ThrustVac:F1} / {s.ThrustSL:F1} kN");
                Row("Mass", $"{s.Mass * 1000:F0} kg   TWR vac {s.TwrVac:F0}");
                Row("Exit Ø / length", $"{s.ExitDiameter:F2} m / {s.TotalLength:F2} m");
                Row("Mass flow", $"{s.MassFlow:F2} kg/s  (O/F {s.Of:F2})");
                if (s.PumpPower > 0) Row("Pump power", $"{s.PumpPower / 1000:F2} MW" + (s.TurbineFlowFraction > 0 ? $", turbine dump {s.TurbineFlowFraction:P1}" : ""));
                Row("Throttle / ignitions", $"{s.ThrottleMin:P0}–100 %, {s.Ignitions}");
                Row("Tech level", d.Tech + (caps == null ? " (R&D not ready)" : caps.Unlimited ? " (sandbox)" : ""));
                Row("Mass split", string.Join(", ", s.MassBreakdown.Where(kv => kv.Value > 0.5).Select(kv => $"{kv.Key} {kv.Value:F0}")));
                DrawPlot(s);
                foreach (string w in s.Warnings) GUILayout.Label("<color=#ffcc40>⚠ " + w + "</color>");
                foreach (string v in m.Violations) GUILayout.Label("<color=#ff6060>✖ " + v + "</color>");
            }
            GUILayout.EndScrollView();

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Save to library (subassembly)")) lastSaved = DesignLibrary.SaveAsSubassembly(m);
            if (GUILayout.Button("Close", GUILayout.Width(80))) target = null;
            GUILayout.EndHorizontal();
            if (!string.IsNullOrEmpty(lastSaved)) GUILayout.Label(lastSaved);
            GUI.DragWindow();
        }

        static string Short(string cycle) => cycle switch
        {
            "PressureFed" => "Press.", "GasGenerator" => "GG", "Expander" => "Exp.", "StagedCombustion" => "SC", "FullFlowStaged" => "FFSC", _ => cycle,
        };

        static void Row(string label, string value)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, GUILayout.Width(140));
            GUILayout.Label(value);
            GUILayout.EndHorizontal();
        }

        static bool Slider(string label, string unit, ref double v, double lo, double hi, double step, double display = 1)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label($"{label}: {(v * display).ToString("0.##", CultureInfo.InvariantCulture)} {unit}", GUILayout.Width(190));
            // Draw at the clamped value but only report a change when the player moves the knob: an out-of-range
            // design (e.g. imported, or tech not researched) must not be rewritten just by opening the window.
            float shown = (float)Math.Max(lo, Math.Min(hi, v));
            float nv = GUILayout.HorizontalSlider(shown, (float)lo, (float)Math.Max(hi, lo + 1e-3));
            GUILayout.EndHorizontal();
            if (nv == shown) return false;
            double snapped = Math.Round(nv / step) * step;
            snapped = Math.Max(lo, Math.Min(hi, snapped));
            if (Math.Abs(snapped - v) < 1e-9) return false;
            v = snapped;
            return true;
        }

        static bool LogSlider(string label, string unit, ref double v, double lo, double hi)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label($"{label}: {v.ToString(v < 10 ? "0.0" : "0", CultureInfo.InvariantCulture)} {unit}", GUILayout.Width(190));
            float shown = (float)Math.Log(Math.Max(lo, Math.Min(hi, v)));
            float lv = GUILayout.HorizontalSlider(shown, (float)Math.Log(lo), (float)Math.Log(Math.Max(hi, lo * 1.01)));
            GUILayout.EndHorizontal();
            if (lv == shown) return false;
            double nv = Math.Exp(lv);
            double mag = Math.Pow(10, Math.Floor(Math.Log10(nv)) - 2);   // 3 significant digits
            nv = Math.Max(lo, Math.Min(hi, Math.Round(nv / mag) * mag));
            if (Math.Abs(nv - v) / Math.Max(v, 1e-9) < 1e-6) return false;
            v = nv;
            return true;
        }

        /// <summary>Isp vs ambient pressure (0-1.5 atm) drawn with KSP's own Hermite evaluation of the keys.</summary>
        void DrawPlot(EngineStats s)
        {
            const int w = 400, h = 110;
            if (plot == null) plot = new Texture2D(w, h, TextureFormat.RGBA32, false);
            var px = new Color[w * h];
            for (int i = 0; i < px.Length; i++) px[i] = new Color(0.08f, 0.09f, 0.1f, 1);
            for (int x = 0; x < w; x += w / 6) for (int y = 0; y < h; y++) px[y * w + x] = new Color(0.2f, 0.2f, 0.22f, 1);
            double top = s.IspVac * 1.05;
            List<CurveKey> c = s.AtmosphereCurve;
            for (int x = 0; x < w; x++)
            {
                double p = 1.5 * x / (w - 1);
                double isp = 0;
                for (int i = 0; i < c.Count - 1; i++)
                    if (p >= c[i].Pressure && p <= c[i + 1].Pressure) { isp = CurveKey.Hermite(c[i], c[i + 1], p); break; }
                int y = Mathf.Clamp((int)(isp / top * (h - 1)), 0, h - 1);
                px[y * w + x] = new Color(1f, 0.75f, 0.3f, 1);
                if (y > 0) px[(y - 1) * w + x] = new Color(1f, 0.75f, 0.3f, 1);
            }
            int sl = (int)(w / 1.5);
            for (int y = 0; y < h; y++) px[y * w + sl] = new Color(0.3f, 0.6f, 1f, 1);
            plot.SetPixels(px);
            plot.Apply();
            GUILayout.Label("Isp vs ambient pressure, 0–1.5 atm (blue: sea level)");
            GUILayout.Box(plot, GUILayout.Width(w), GUILayout.Height(h));
        }
    }
}
