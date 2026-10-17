using System;
using System.Collections.Generic;
using ProcEngines.Model;
using UnityEngine;

namespace ProcEngines
{
    /// <summary>Accumulates primitive pieces into one mesh with a few submeshes.</summary>
    public sealed class MeshBuilder
    {
        readonly List<Vector3> v = new List<Vector3>(), n = new List<Vector3>();
        readonly List<Vector2> uv = new List<Vector2>();
        readonly List<int>[] tris;

        public MeshBuilder(int materials) { tris = new List<int>[materials]; for (int i = 0; i < materials; i++) tris[i] = new List<int>(); }

        static void Basis(Vector3 dir, out Vector3 a, out Vector3 b)
        {
            dir.Normalize();
            a = Vector3.Cross(dir, Mathf.Abs(dir.y) < 0.95f ? Vector3.up : Vector3.right).normalized;
            b = Vector3.Cross(dir, a);
        }

        /// <summary>Generic swept ring strip: rings[i] centre, radius, frame axes.</summary>
        void Sweep(IList<Vector3> centres, IList<float> radii, IList<Vector3> ax, IList<Vector3> bx, int sides, int mat, bool capStart, bool capEnd)
        {
            int start = v.Count, ring = sides + 1;
            for (int i = 0; i < centres.Count; i++)
                for (int j = 0; j <= sides; j++)
                {
                    float phi = 2 * Mathf.PI * j / sides;
                    Vector3 dir = ax[i] * Mathf.Cos(phi) + bx[i] * Mathf.Sin(phi);
                    v.Add(centres[i] + dir * radii[i]);
                    n.Add(dir);
                    uv.Add(new Vector2((float)j / sides, i));
                }
            var t = new List<int>();
            for (int i = 0; i < centres.Count - 1; i++)
                for (int j = 0; j < sides; j++)
                {
                    int a = start + i * ring + j, b = a + 1, c = a + ring, d = c + 1;
                    t.Add(a); t.Add(c); t.Add(b); t.Add(b); t.Add(c); t.Add(d);
                }
            EngineGeometry.FixWinding(v, n, t);
            tris[mat].AddRange(t);
            if (capStart) Cap(centres[0], centres[0] - centres[1], radii[0], ax[0], bx[0], sides, mat);
            if (capEnd) Cap(centres[centres.Count - 1], centres[centres.Count - 1] - centres[centres.Count - 2], radii[radii.Count - 1], ax[ax.Count - 1], bx[bx.Count - 1], sides, mat);
        }

        void Cap(Vector3 c, Vector3 outward, float r, Vector3 a, Vector3 b, int sides, int mat)
        {
            outward.Normalize();
            int start = v.Count;
            v.Add(c); n.Add(outward); uv.Add(new Vector2(0.5f, 0.5f));
            for (int j = 0; j <= sides; j++)
            {
                float phi = 2 * Mathf.PI * j / sides;
                v.Add(c + (a * Mathf.Cos(phi) + b * Mathf.Sin(phi)) * r); n.Add(outward); uv.Add(new Vector2(Mathf.Cos(phi), Mathf.Sin(phi)));
            }
            var t = new List<int>();
            for (int j = 0; j < sides; j++) { t.Add(start); t.Add(start + 1 + j); t.Add(start + 2 + j); }
            EngineGeometry.FixWinding(v, n, t);
            tris[mat].AddRange(t);
        }

        public void Frustum(Vector3 p0, Vector3 p1, float r0, float r1, int mat, int sides = 14, bool caps = true)
        {
            Basis(p1 - p0, out Vector3 a, out Vector3 b);
            Sweep(new[] { p0, p1 }, new[] { r0, r1 }, new[] { a, a }, new[] { b, b }, sides, mat, caps, caps);
        }

        public void Cylinder(Vector3 p0, Vector3 p1, float r, int mat, int sides = 14) => Frustum(p0, p1, r, r, mat, sides);

        public void Sphere(Vector3 c, float r, int mat, int sides = 14)
        {
            var cs = new List<Vector3>(); var rs = new List<float>(); var ax = new List<Vector3>(); var bx = new List<Vector3>();
            for (int i = 0; i <= 8; i++)
            {
                float th = Mathf.PI * i / 8;
                cs.Add(c + Vector3.up * (r * Mathf.Cos(th))); rs.Add(Mathf.Max(r * Mathf.Sin(th), 1e-4f)); ax.Add(Vector3.right); bx.Add(Vector3.forward);
            }
            // normals of a sphere are radial, not the ring direction: fix after the sweep
            int start = v.Count;
            Sweep(cs, rs, ax, bx, sides, mat, false, false);
            for (int i = start; i < v.Count; i++) n[i] = (v[i] - c).normalized;
        }

        public void Torus(Vector3 c, Vector3 axis, float R, float r, int mat, int segs = 20, int sides = 8)
        {
            Basis(axis, out Vector3 a, out Vector3 b);
            var cs = new List<Vector3>(); var rs = new List<float>(); var ax = new List<Vector3>(); var bx = new List<Vector3>();
            for (int i = 0; i <= segs; i++)
            {
                float th = 2 * Mathf.PI * i / segs;
                Vector3 radial = a * Mathf.Cos(th) + b * Mathf.Sin(th);
                cs.Add(c + radial * R); rs.Add(r); ax.Add(radial); bx.Add(axis.normalized);
            }
            int start = v.Count;
            Sweep(cs, rs, ax, bx, sides, mat, false, false);
        }

        /// <summary>Tube along a smoothed path (Catmull-Rom through the points), parallel-transported frames.</summary>
        public void Tube(IList<Vector3> pts, float r, int mat, int perSpan = 6, int sides = 10)
        {
            var path = new List<Vector3>();
            for (int i = 0; i < pts.Count - 1; i++)
            {
                Vector3 p0 = pts[Math.Max(i - 1, 0)], p1 = pts[i], p2 = pts[i + 1], p3 = pts[Math.Min(i + 2, pts.Count - 1)];
                for (int k = 0; k < perSpan; k++)
                {
                    float t = (float)k / perSpan, t2 = t * t, t3 = t2 * t;
                    path.Add(0.5f * (2 * p1 + (-p0 + p2) * t + (2 * p0 - 5 * p1 + 4 * p2 - p3) * t2 + (-p0 + 3 * p1 - 3 * p2 + p3) * t3));
                }
            }
            path.Add(pts[pts.Count - 1]);
            var ax = new List<Vector3>(); var bx = new List<Vector3>(); var rs = new List<float>();
            Basis(path[1] - path[0], out Vector3 a, out Vector3 b);
            for (int i = 0; i < path.Count; i++)
            {
                Vector3 tan = (path[Math.Min(i + 1, path.Count - 1)] - path[Math.Max(i - 1, 0)]).normalized;
                a = (a - Vector3.Dot(a, tan) * tan).normalized;   // parallel transport
                b = Vector3.Cross(tan, a);
                ax.Add(a); bx.Add(b); rs.Add(r);
            }
            Sweep(path, rs, ax, bx, sides, mat, true, true);
        }

        public void Box(Vector3 c, Vector3 size, int mat)
        {
            Vector3 h = size * 0.5f;
            Cylinder(c - Vector3.up * h.y, c + Vector3.up * h.y, Mathf.Max(h.x, h.z) * 1.2f, mat, 4);
        }

        public void Build(Mesh mesh)
        {
            mesh.Clear();
            mesh.indexFormat = v.Count > 65000 ? UnityEngine.Rendering.IndexFormat.UInt32 : UnityEngine.Rendering.IndexFormat.UInt16;
            mesh.SetVertices(v);
            mesh.SetNormals(n);
            mesh.SetUVs(0, uv);
            mesh.subMeshCount = tris.Length;
            for (int i = 0; i < tris.Length; i++) mesh.SetTriangles(tris[i], i);
            mesh.RecalculateBounds();
            mesh.RecalculateTangents();
        }
    }

    /// <summary>
    /// Kit-bashed turbomachinery, plumbing and structure per cycle (DESIGN.md 8.5), from primitives (visual only, no
    /// colliders). Pieces scale with the turbopump / pressurant mass and the propellant flow; positions avoid the bell.
    /// </summary>
    public static class KitBuilder
    {
        public const int MatStructure = 0, MatMachine = 1, MatLines = 2;

        public sealed class Result
        {
            public bool HasExhaust;
            public Vector3 ExhaustPosition;   // part frame, GG turbine exhaust nozzle exit
            public float ExhaustRadius;
        }

        static Vector3 Around(float deg, float radius, float y) => new Vector3(radius * Mathf.Cos(deg * Mathf.Deg2Rad), y, radius * Mathf.Sin(deg * Mathf.Deg2Rad));

        public static Result Build(Mesh mesh, EngineDesign d, EngineStats s, EngineGeometry g, ModelData data)
        {
            var mb = new MeshBuilder(3);
            var res = new Result();
            float rc = g.ChamberRadius, rt = g.ThroatRadius;
            double turboKg = s.MassBreakdown.TryGetValue("turbomachinery", out double tk) ? tk : 0;
            double pressKg = s.MassBreakdown.TryGetValue("pressurant", out double pk) ? pk : 0;
            float yUnder = -g.MountHeight;
            float bellLen = g.YThroat - g.YExit;

            // propellant line sizes: constant flow velocity, d ~ sqrt(mdot / rho)
            double of = s.Of;
            data.Propellants.TryGetValue(d.Propellant, out ProcEngines.Model.Propellant prop);
            double fuelDensity = prop?.FuelDensity ?? 800, oxDensity = prop?.OxDensity ?? 1141;
            double mdotFuel = s.MassFlow / (1 + of), mdotOx = s.MassFlow - mdotFuel;
            float LineR(double mdot, double rho) => Mathf.Clamp(Mathf.Sqrt((float)(mdot / (rho * 15) / Math.PI)), 0.006f, 0.25f * rc + 0.01f);
            float fuelR = LineR(mdotFuel, fuelDensity), oxR = LineR(mdotOx, oxDensity);

            // thrust frame: four struts from the mount plate rim to the top of the chamber
            for (int k = 0; k < 4; k++)
            {
                float ang = 45 + 90 * k;
                mb.Cylinder(Around(ang, g.MountRadius * 0.92f, yUnder), Around(ang, rc * 0.95f, g.YInjector + 0.15f * g.DomeHeight), Mathf.Max(0.035f * rc, 0.004f), MatStructure, 8);
            }
            // gimbal actuators (piston + rod) from the mount to the bell, 90 degrees apart
            float yAct = g.YThroat - 0.3f * bellLen;
            foreach (float ang in new[] { 135f, 225f })
            {
                Vector3 top = Around(ang, g.MountRadius * 0.85f, yUnder);
                Vector3 bot = Around(ang, g.BellRadiusAt(yAct) + 0.02f * rc + 0.005f, yAct);
                Vector3 mid = Vector3.Lerp(top, bot, 0.55f);
                float ra = Mathf.Max(0.06f * rc, 0.006f);
                mb.Cylinder(top, mid, ra, MatMachine, 10);
                mb.Cylinder(mid, bot, ra * 0.45f, MatStructure, 8);
            }

            // turbopump envelope from its mass (dense machinery, ~5 t/m^3 including the housing), never wider than the chamber
            float rp = Mathf.Clamp(Mathf.Pow((float)(turboKg / (5000 * 2 * Mathf.PI)), 1f / 3f), 0.02f, Mathf.Max(0.9f * rc, 0.03f));
            Vector3 Dome(float ang, float frac) => Around(ang, rc * frac, g.YInjector + 0.4f * g.DomeHeight);
            float yMan = Mathf.Max(g.RegenEndY, g.YExit) + 0.02f * bellLen;
            float manR = g.BellRadiusAt(yMan) + fuelR;

            void Pump(float ang, float scale, bool preburner, bool fuelToManifold, bool oxToDome)
            {
                float r = rp * scale;
                float pr = rc + r * 1.25f;
                // everything stays below the mount plate, so nothing pokes into the part stacked on top
                Vector3 c = Around(ang, pr, yUnder - r * (preburner ? 2.35f : 1.0f));
                mb.Cylinder(c - Vector3.up * r, c + Vector3.up * r * 0.9f, r * 0.75f, MatMachine);            // housing
                mb.Torus(c + Vector3.up * r * 0.2f, Vector3.up, r * 0.78f, r * 0.32f, MatMachine);            // volute
                mb.Cylinder(c - Vector3.up * r * 1.35f, c - Vector3.up * r * 0.95f, r * 0.9f, MatStructure);  // turbine
                if (preburner)
                {
                    Vector3 pb = c + Vector3.up * (r * 0.9f);
                    mb.Cylinder(pb, pb + Vector3.up * r * 0.9f, r * 0.45f, MatMachine);
                    mb.Sphere(pb + Vector3.up * r * 0.9f, r * 0.45f, MatMachine);
                    // hot-gas duct into the injector dome
                    mb.Tube(new[] { pb + Vector3.up * r * 1.2f, Vector3.Lerp(pb, Dome(ang, 0.2f), 0.5f) + Vector3.up * r * 0.6f, Dome(ang, 0.2f) }, r * 0.28f, MatMachine);
                }
                if (oxToDome)
                    mb.Tube(new[] { c + Around(ang + 90, r * 0.75f, r * 0.2f), Around(ang + 30, rc * 1.05f, c.y + r * 0.2f), Dome(ang + 30, 0.7f) }, oxR, MatLines);
                if (fuelToManifold)
                {
                    Vector3 start = c + Around(ang - 90, r * 0.75f, r * 0.2f);
                    float yMid = g.YChamberBottom;
                    mb.Tube(new[] { start, Around(ang - 25, pr, yMid), Around(ang - 25, g.BellRadiusAt(g.YThroat - 0.5f * (g.YThroat - yMan)) + fuelR * 2, g.YThroat - 0.5f * (g.YThroat - yMan)), Around(ang - 25, manR, yMan) }, fuelR, MatLines);
                }
            }

            switch (d.Cycle)
            {
                case "PressureFed":
                {
                    // composite helium bottles (COPV), sized from the pressurant mass at ~400 kg/m^3 effective, plus valves
                    int n = pressKg > 300 ? 4 : 2;
                    float rb = Mathf.Clamp(Mathf.Pow((float)(pressKg / n / (400 * 4.19f)), 1f / 3f), 0.02f, Mathf.Max(1.2f * rc, 0.03f));
                    for (int k = 0; k < n; k++)
                    {
                        float ang = 360f * k / n;
                        mb.Sphere(Around(ang, rc + rb * 1.15f, yUnder - rb * 1.1f), rb, MatMachine);
                    }
                    foreach (float ang in new[] { 90f, 270f })
                    {
                        Vector3 valve = Around(ang, rc * 0.85f, yUnder - 0.3f * g.DomeHeight);
                        mb.Box(valve, Vector3.one * Mathf.Max(oxR * 2.5f, 0.01f), MatMachine);
                        mb.Tube(new[] { valve + Vector3.up * 0.01f, Dome(ang, 0.6f) }, ang < 180 ? fuelR : oxR, MatLines);
                    }
                    break;
                }
                case "GasGenerator":
                {
                    Pump(0, 1, false, true, true);
                    // gas generator beside the pump, exhaust duct down past the bell ending in a small nozzle
                    float r = rp;
                    Vector3 pumpC = Around(0, rc + r * 1.25f, yUnder - r * 1.0f);
                    mb.Sphere(Around(28, rc + r * 0.9f, yUnder - r * 0.45f), r * 0.38f, MatMachine);
                    float yEx = g.YThroat - 0.45f * bellLen;
                    float ductR = Mathf.Max(r * 0.32f, 0.01f);
                    float clear = g.BellRadiusAt(yEx) + ductR * 1.6f;
                    Vector3 exitTop = Around(-35, clear, yEx);
                    Vector3 exitBottom = exitTop - Vector3.up * ductR * 2.2f;
                    mb.Tube(new[] { pumpC - Vector3.up * r * 1.35f, Around(-20, rc + r * 1.25f, g.YChamberBottom), Around(-30, g.BellRadiusAt(g.YThroat - 0.2f * bellLen) + ductR * 1.6f, g.YThroat - 0.2f * bellLen), exitTop }, ductR, MatMachine);
                    mb.Frustum(exitTop, exitBottom, ductR, ductR * 1.7f, MatMachine, 12, false);
                    res.HasExhaust = true;
                    res.ExhaustPosition = exitBottom;
                    res.ExhaustRadius = ductR * 1.7f;
                    break;
                }
                case "Expander":
                {
                    Pump(0, 1, false, false, true);
                    // heated hydrogen: regen manifold up to the turbine
                    float r = rp;
                    Vector3 pumpC = Around(0, rc + r * 1.25f, yUnder - r * 1.0f);
                    mb.Tube(new[] { Around(-25, manR, yMan), Around(-25, g.BellRadiusAt(g.YThroat - 0.5f * (g.YThroat - yMan)) + fuelR * 2, g.YThroat - 0.5f * (g.YThroat - yMan)), Around(-15, rc + r * 1.25f, g.YChamberBottom), pumpC - Vector3.up * r * 1.35f }, fuelR, MatLines);
                    break;
                }
                case "StagedCombustion":
                    Pump(0, 1, true, true, true);
                    break;
                case "FullFlowStaged":
                    Pump(0, 0.8f, true, true, false);
                    Pump(180, 0.8f, true, false, true);
                    break;
            }
            // regen coolant inlet manifold around the bell (all pumped/pressure-fed regen engines)
            mb.Torus(new Vector3(0, yMan, 0), Vector3.up, manR, fuelR * 1.1f, MatLines, 32, 8);

            mb.Build(mesh);
            return res;
        }
    }
}
