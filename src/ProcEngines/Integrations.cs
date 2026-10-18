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
        /// <summary>
        /// FAR re-voxelizes on this message; without FAR, render a fresh stock drag cube. KSPCommunityFixes' drag-cube
        /// patch renders the live part (stock renders a clone) and takes each renderer's bounds through
        /// MeshFilter.mesh, which makes Unity copy every shared render mesh into a per-filter "&lt;name&gt; Instance" and
        /// point the filter at it. The filters under the model are put back afterwards, and the copies (used only for
        /// their bounds, which equal the originals') destroyed, also when the render throws.
        /// </summary>
        public static void NotifyAero(Part part)
        {
            if (KspData.FarPresent)
            {
                part.SendMessage("GeometryPartModuleRebuildMeshData", SendMessageOptions.DontRequireReceiver);
                return;
            }
            if (DragCubeSystem.Instance == null) return;
            Transform model = part.transform.Find("model");
            MeshFilter[] filters = model != null ? model.GetComponentsInChildren<MeshFilter>(true) : new MeshFilter[0];
            var meshes = new Mesh[filters.Length];
            for (int i = 0; i < filters.Length; i++) meshes[i] = filters[i].sharedMesh;
            DragCube cube;
            try { cube = DragCubeSystem.Instance.RenderProceduralDragCube(part); }
            finally { PutBack(filters, meshes); }
            part.DragCubes.ClearCubes();
            part.DragCubes.Cubes.Add(cube);
            part.DragCubes.ResetCubeWeights();
            part.DragCubes.ForceUpdate(true, true, false);
            part.DragCubes.SetDragWeights();
        }

        /// <summary>Every filter whose mesh changed gets its own back; the mesh it had instead is destroyed unless it is
        /// one of the snapshot's (only a copy made during the call can be new).</summary>
        static void PutBack(MeshFilter[] filters, Mesh[] meshes)
        {
            for (int i = 0; i < filters.Length; i++)
            {
                MeshFilter f = filters[i];
                if (f == null) continue;
                Mesh now = f.sharedMesh;
                if (now == meshes[i]) continue;
                if (now != null && Array.IndexOf(meshes, now) < 0) UnityEngine.Object.Destroy(now);
                f.sharedMesh = meshes[i];
            }
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
