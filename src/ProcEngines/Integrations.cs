using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace ProcEngines
{
    /// <summary>Soft integrations, all verified in the Phase 0 spike (SPIKE_RESULTS.md 4, 5).</summary>
    public static class Integrations
    {
        /// <summary>FAR re-voxelizes on this message; without FAR, render a fresh stock drag cube.</summary>
        public static void NotifyAero(Part part)
        {
            if (KspData.FarPresent)
            {
                part.SendMessage("GeometryPartModuleRebuildMeshData", SendMessageOptions.DontRequireReceiver);
                return;
            }
            if (DragCubeSystem.Instance == null) return;
            DragCube cube = DragCubeSystem.Instance.RenderProceduralDragCube(part);
            part.DragCubes.ClearCubes();
            part.DragCubes.Cubes.Add(cube);
            part.DragCubes.ResetCubeWeights();
            part.DragCubes.ForceUpdate(true, true, false);
            part.DragCubes.SetDragWeights();
        }

        /// <summary>
        /// Waterfall (flight only): once ModuleWaterfallFX has started, OnLoad with a TEMPLATE node replaces the
        /// templates and re-initializes the effects. Position is always written (Waterfall defaults it to 1,1,1).
        /// </summary>
        public struct PlumeSpec
        {
            public string Template, ParentTransform;
            public float Scale, LengthScale;
        }

        public static IEnumerator ApplyWaterfall(PartModule host, System.Collections.Generic.IList<PlumeSpec> plumes, Action<string> report)
        {
            PartModule wf = host.part.Modules.Cast<PartModule>().FirstOrDefault(m => m.moduleName == "ModuleWaterfallFX");
            if (wf == null || plumes.Count == 0) { report?.Invoke("no Waterfall"); yield break; }
            FieldInfo started = wf.GetType().GetField("started", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            for (int i = 0; i < 300 && started != null && !(bool)started.GetValue(wf); i++) yield return null;
            var n = new ConfigNode("MODULE");
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            foreach (PlumeSpec p in plumes)
            {
                ConfigNode t = n.AddNode("TEMPLATE");
                t.AddValue("templateName", p.Template);
                t.AddValue("overrideParentTransform", p.ParentTransform);
                t.AddValue("position", "0,0,0");
                t.AddValue("rotation", "0,0,0");
                string sc = p.Scale.ToString("R", inv), sl = (p.Scale * p.LengthScale).ToString("R", inv);
                t.AddValue("scale", sc + "," + sc + "," + sl);
            }
            try { wf.OnLoad(n); }
            catch (Exception e) { report?.Invoke("Waterfall OnLoad threw " + e.Message); yield break; }
            var fx = wf.GetType().GetProperty("FX")?.GetValue(wf) as IList;
            report?.Invoke(string.Join(" + ", plumes.Select(p => $"{p.Template} x{p.Scale:F2}")) + $" ({fx?.Count ?? -1} effects)");
        }
    }
}
