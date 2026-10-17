using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ProcEngines.Model;
using UnityEngine;

namespace ProcEngines
{
    /// <summary>
    /// In-scene drag handles (DESIGN.md 7.2, CRFP-style, clean-room): shown on the engine open in the designer.
    /// Each handle moves along one axis in the part frame; the mouse ray is projected onto that axis and the distance
    /// along it is mapped to a design value. Changes go through ModuleProcEngine.SetDesign, so the two-speed update
    /// pipeline (and undo via SetBackup) applies. Drawn in screen space; editor input is locked while hovering/dragging.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.EditorAny, false)]
    public class EngineHandles : MonoBehaviour
    {
        public sealed class Handle
        {
            public string Id, Label;
            public Vector3 Origin, Axis;     // part frame: the line the handle slides along
            public float Value;              // current distance along the axis
            public Color Color;
            public Vector3 WorldPosition(Part p) => p.transform.TransformPoint(Origin + Axis * Value);
        }

        public static EngineHandles Instance { get; private set; }
        const string LockId = "ProcEnginesHandles";
        const float PickRadius = 14f;
        Handle dragging, hovered;
        Handle dragStart;                    // frozen copy of the handle at drag start (axis must not move under the mouse)
        EngineDesign designAtStart;
        EngineStats statsAtStart;
        Texture2D dot;

        void Awake() { Instance = this; }

        void OnDestroy()
        {
            InputLockManager.RemoveControlLock(LockId);
            if (dot != null) Destroy(dot);
            if (Instance == this) Instance = null;
        }

        static ModuleProcEngine Target => DesignerWindow.Target;

        /// <summary>The handles for the current engine, from its live stats (part frame, top node at the origin).</summary>
        public static List<Handle> HandlesFor(ModuleProcEngine m)
        {
            var l = new List<Handle>();
            if (m?.Stats == null) return l;
            EngineStats s = m.Stats;
            float yThroat = -(float)(EngineYThroatOffset(m));
            float yExit = m.part.FindAttachNode("bottom").position.y;
            l.Add(new Handle { Id = "exit", Label = "Area ratio", Origin = new Vector3(0, yExit, 0), Axis = Vector3.right, Value = (float)s.ExitRadius, Color = new Color(1f, 0.75f, 0.2f) });
            if (!m.Design.Conical)
                l.Add(new Handle { Id = "length", Label = "Bell length", Origin = new Vector3(0, yThroat, 0), Axis = Vector3.down, Value = yThroat - yExit, Color = new Color(0.3f, 0.8f, 1f) });
            float yChamber = yThroat + (float)s.ConvergentLength + 0.5f * (float)s.ChamberLength;
            l.Add(new Handle { Id = "thrust", Label = "Thrust", Origin = new Vector3(0, yChamber, 0), Axis = Vector3.forward, Value = (float)s.ChamberRadius, Color = new Color(0.4f, 1f, 0.45f) });
            return l;
        }

        // the throat sits below the mount and dome by the chamber, convergent; keep in step with EngineGeometry
        static double EngineYThroatOffset(ModuleProcEngine m)
        {
            Transform g = m.part.transform.Find("model/" + ModuleProcEngine.RootName + "/" + ModuleProcEngine.GimbalName);
            return g != null ? -g.localPosition.y : m.Stats.ChamberLength + m.Stats.ConvergentLength;
        }

        /// <summary>Maps a new distance along a handle's axis to a design, relative to the design at drag start.</summary>
        public static EngineDesign DesignFor(string id, float value, EngineDesign start, EngineStats startStats)
        {
            EngineDesign d = Serialization.LoadDesign(Serialization.SaveDesign(start));
            switch (id)
            {
                case "exit":     // Re = sqrt(eps) * Rt with Rt fixed (thrust and Pc unchanged)
                    d.Eps = Math.Max(2, Math.Pow(Math.Max(value, 0.001) / startStats.ThroatRadius, 2));
                    break;
                case "length":   // nozzle length = fraction * 15-degree cone length
                    double cone = (Math.Sqrt(start.Eps) - 1) * startStats.ThroatRadius / Math.Tan(15 * Math.PI / 180);
                    d.LengthFraction = Math.Max(0.6, Math.Min(1.0, value / Math.Max(cone, 1e-6)));
                    break;
                case "thrust":   // chamber radius ~ sqrt(F) at fixed Pc
                    d.ThrustVac = Math.Max(1, start.ThrustVac * Math.Pow(Math.Max(value, 0.001) / startStats.ChamberRadius, 2));
                    break;
            }
            return d;
        }

        /// <summary>Closest point between the mouse ray and the handle's axis line, as a distance along the axis.</summary>
        public static float Project(Part p, Handle h, Ray ray)
        {
            Vector3 o = p.transform.TransformPoint(h.Origin), u = p.transform.TransformDirection(h.Axis).normalized;
            Vector3 w = ray.origin - o, v = ray.direction.normalized;
            float b = Vector3.Dot(u, v), d = Vector3.Dot(u, w), e = Vector3.Dot(v, w);
            float denom = 1 - b * b;
            if (denom < 1e-6f) return h.Value;    // looking straight down the axis
            return (d - b * e) / denom;
        }

        /// <summary>Drag a handle along the given ray (also used by the in-game tests).</summary>
        public void DragTo(string id, Ray ray)
        {
            ModuleProcEngine m = Target;
            if (m == null) return;
            if (dragging == null || dragging.Id != id) BeginDrag(HandlesFor(m).FirstOrDefault(h => h.Id == id));
            if (dragStart == null) return;
            float value = Mathf.Max(0.001f, Project(m.part, dragStart, ray));
            dragging.Value = value;
            m.SetDesign(DesignFor(id, value, designAtStart, statsAtStart));
        }

        public void EndDrag()
        {
            dragging = null;
            dragStart = null;
        }

        void BeginDrag(Handle h)
        {
            if (h == null || Target == null) return;
            dragging = h;
            dragStart = new Handle { Id = h.Id, Label = h.Label, Origin = h.Origin, Axis = h.Axis, Value = h.Value, Color = h.Color };
            designAtStart = Serialization.LoadDesign(Serialization.SaveDesign(Target.Design));
            statsAtStart = Target.Stats;
        }

        void Update()
        {
            ModuleProcEngine m = Target;
            Camera cam = EditorLogic.fetch != null ? EditorLogic.fetch.editorCamera : null;
            if (m == null || cam == null) { hovered = null; EndDrag(); InputLockManager.RemoveControlLock(LockId); return; }
            Vector2 mouse = new Vector2(Input.mousePosition.x, Input.mousePosition.y);
            if (dragging == null)
            {
                hovered = null;
                foreach (Handle h in HandlesFor(m))
                {
                    Vector3 sp = cam.WorldToScreenPoint(h.WorldPosition(m.part));
                    if (sp.z > 0 && (new Vector2(sp.x, sp.y) - mouse).magnitude < PickRadius) { hovered = h; break; }
                }
                if (hovered != null && Input.GetMouseButtonDown(0)) BeginDrag(hovered);
            }
            else
            {
                if (Input.GetMouseButton(0)) DragTo(dragging.Id, cam.ScreenPointToRay(Input.mousePosition));
                else EndDrag();
            }
            if (hovered != null || dragging != null) InputLockManager.SetControlLock(ControlTypes.EDITOR_LOCK, LockId);
            else InputLockManager.RemoveControlLock(LockId);
        }

        void OnGUI()
        {
            ModuleProcEngine m = Target;
            Camera cam = EditorLogic.fetch != null ? EditorLogic.fetch.editorCamera : null;
            if (m == null || cam == null || Event.current.type != EventType.Repaint) return;
            if (dot == null)
            {
                dot = new Texture2D(32, 32, TextureFormat.RGBA32, false);
                for (int y = 0; y < 32; y++) for (int x = 0; x < 32; x++)
                {
                    float r = new Vector2(x - 15.5f, y - 15.5f).magnitude;
                    dot.SetPixel(x, y, new Color(1, 1, 1, Mathf.Clamp01(15.5f - r) * (r > 11 ? 1f : 0.85f)));
                }
                dot.Apply();
            }
            foreach (Handle h in HandlesFor(m))
            {
                Vector3 sp = cam.WorldToScreenPoint(h.WorldPosition(m.part));
                if (sp.z <= 0) continue;
                bool active = (dragging?.Id ?? hovered?.Id) == h.Id;
                float size = active ? 22 : 16;
                var r = new Rect(sp.x - size / 2, Screen.height - sp.y - size / 2, size, size);
                Color old = GUI.color;
                GUI.color = h.Color;
                GUI.DrawTexture(r, dot);
                GUI.color = old;
                if (active)
                    GUI.Label(new Rect(r.xMax + 4, r.y - 2, 220, 24), $"<b>{h.Label}</b>: {ValueText(h.Id, m)}");
            }
        }

        static string ValueText(string id, ModuleProcEngine m) => id switch
        {
            "exit" => m.Design.Eps.ToString("0.#", CultureInfo.InvariantCulture) + $" (Ø{m.Stats.ExitDiameter:F2} m)",
            "length" => (m.Design.LengthFraction * 100).ToString("0", CultureInfo.InvariantCulture) + " % of cone",
            "thrust" => m.Design.ThrustVac.ToString("0", CultureInfo.InvariantCulture) + " kN",
            _ => "",
        };
    }
}
