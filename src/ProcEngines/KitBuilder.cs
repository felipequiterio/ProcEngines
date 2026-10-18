using System;
using System.Collections.Generic;
using ProcEngines.Model;
using UnityEngine;

namespace ProcEngines
{
    /// <summary>Accumulates lathe surfaces and primitive pieces into one mesh with a few submeshes. Tangents follow
    /// increasing U with w = -1 everywhere (see EngineMaterials for the normal-map convention).</summary>
    public sealed class MeshBuilder
    {
        readonly List<Vector3> v = new List<Vector3>(), n = new List<Vector3>();
        readonly List<Vector4> tg = new List<Vector4>();
        readonly List<Vector2> uv = new List<Vector2>();
        readonly List<int>[] tris;

        public MeshBuilder(int materials) { tris = new List<int>[materials]; for (int i = 0; i < materials; i++) tris[i] = new List<int>(); }

        static void Basis(Vector3 dir, out Vector3 a, out Vector3 b)
        {
            dir.Normalize();
            a = Vector3.Cross(dir, Mathf.Abs(dir.y) < 0.95f ? Vector3.up : Vector3.right).normalized;
            b = Vector3.Cross(dir, a);
        }

        void AddTris(List<int> t, int mat)
        {
            EngineGeometry.FixWinding(v, n, t);
            tris[Math.Min(mat, tris.Length - 1)].AddRange(t);
        }

        /// <summary>Revolve profile segments around Y. Normals come from the profile tangent (central differences
        /// along each smooth segment; joints between segments are creases). U repeats UTiles times around (seam
        /// vertex duplicated), V runs along the arc length.</summary>
        public void Lathe(List<ProfileSegment> segments, int ringSegments, float yOffset)
        {
            int ring = ringSegments + 1;
            foreach (ProfileSegment segment in segments)
            {
                List<Vector2> seg = segment.Points;
                if (seg.Count < 2) continue;
                float acc = 0;
                int baseIndex = v.Count;
                for (int i = 0; i < seg.Count; i++)
                {
                    if (i > 0) acc += (seg[i] - seg[i - 1]).magnitude;
                    Vector2 tan = seg[Math.Min(i + 1, seg.Count - 1)] - seg[Math.Max(i - 1, 0)];
                    if (tan.sqrMagnitude < 1e-12f) tan = new Vector2(0, -1);
                    tan.Normalize();
                    var n2 = new Vector2(-tan.y, tan.x);   // outward for a profile running outward/downward
                    if (segment.Inward) n2 = -n2;
                    for (int j = 0; j < ring; j++)
                    {
                        float phi = 2 * Mathf.PI * j / ringSegments, c = Mathf.Cos(phi), sn = Mathf.Sin(phi);
                        v.Add(new Vector3(seg[i].x * c, seg[i].y - yOffset, seg[i].x * sn));
                        n.Add(new Vector3(n2.x * c, n2.y, n2.x * sn));
                        tg.Add(new Vector4(-sn, 0, c, -1));
                        uv.Add(new Vector2(segment.UTiles * j / ringSegments, segment.VOffset + acc / segment.VLength));
                    }
                }
                var t = new List<int>();
                for (int i = 0; i < seg.Count - 1; i++)
                {
                    if ((seg[i + 1] - seg[i]).sqrMagnitude < 1e-12f) continue;
                    for (int j = 0; j < ringSegments; j++)
                    {
                        int a = baseIndex + i * ring + j, b = a + 1, cI = a + ring, dI = cI + 1;
                        t.Add(a); t.Add(b); t.Add(cI);
                        t.Add(b); t.Add(dI); t.Add(cI);
                    }
                }
                AddTris(t, segment.Material);
            }
        }

        /// <summary>Generic swept ring strip: rings[i] centre, radius, frame axes (a x b = forward). `slopes` (dr/ds)
        /// tilts the normals of a rippled surface (bellows). V is arc length in circumferences (square texels).</summary>
        void Sweep(IList<Vector3> centres, IList<float> radii, IList<Vector3> ax, IList<Vector3> bx, int sides, int mat, bool capStart, bool capEnd, IList<float> slopes = null)
        {
            int start = v.Count, ring = sides + 1;
            float circ = 2 * Mathf.PI * Mathf.Max(radii[0], 1e-3f), s = 0;
            for (int i = 0; i < centres.Count; i++)
            {
                if (i > 0) s += (centres[i] - centres[i - 1]).magnitude;
                Vector3 fwd = Vector3.Cross(ax[i], bx[i]);
                for (int j = 0; j <= sides; j++)
                {
                    float phi = 2 * Mathf.PI * j / sides, c = Mathf.Cos(phi), sn = Mathf.Sin(phi);
                    Vector3 dir = ax[i] * c + bx[i] * sn;
                    Vector3 tu = -ax[i] * sn + bx[i] * c;
                    v.Add(centres[i] + dir * radii[i]);
                    n.Add(slopes == null ? dir : (dir - fwd * slopes[i]).normalized);
                    tg.Add(new Vector4(tu.x, tu.y, tu.z, -1));
                    uv.Add(new Vector2((float)j / sides, s / circ));
                }
            }
            var t = new List<int>();
            for (int i = 0; i < centres.Count - 1; i++)
                for (int j = 0; j < sides; j++)
                {
                    int a = start + i * ring + j, b = a + 1, c = a + ring, d = c + 1;
                    t.Add(a); t.Add(c); t.Add(b); t.Add(b); t.Add(c); t.Add(d);
                }
            AddTris(t, mat);
            if (capStart) Cap(centres[0], centres[0] - centres[1], radii[0], ax[0], bx[0], sides, mat);
            if (capEnd) Cap(centres[centres.Count - 1], centres[centres.Count - 1] - centres[centres.Count - 2], radii[radii.Count - 1], ax[ax.Count - 1], bx[bx.Count - 1], sides, mat);
        }

        void Cap(Vector3 c, Vector3 outward, float r, Vector3 a, Vector3 b, int sides, int mat)
        {
            outward.Normalize();
            int start = v.Count;
            v.Add(c); n.Add(outward); tg.Add(new Vector4(a.x, a.y, a.z, -1)); uv.Add(new Vector2(0.5f, 0.5f));
            for (int j = 0; j <= sides; j++)
            {
                float phi = 2 * Mathf.PI * j / sides;
                v.Add(c + (a * Mathf.Cos(phi) + b * Mathf.Sin(phi)) * r); n.Add(outward); tg.Add(new Vector4(a.x, a.y, a.z, -1));
                uv.Add(new Vector2(0.5f + 0.5f * Mathf.Cos(phi), 0.5f + 0.5f * Mathf.Sin(phi)));
            }
            var t = new List<int>();
            for (int j = 0; j < sides; j++) { t.Add(start); t.Add(start + 1 + j); t.Add(start + 2 + j); }
            AddTris(t, mat);
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
            Sweep(cs, rs, ax, bx, sides, mat, false, false);
        }

        /// <summary>Pipe along a smoothed path (Catmull-Rom through the points, resampled by arc length, parallel-
        /// transported frames). Optional bolted flanges at both ends and a convoluted bellows section (fractions of
        /// the length) with collars at its ends.</summary>
        public void Tube(IList<Vector3> pts, float r, int mat, int sides = 10, int flangeMat = -1, float bellowsFrom = -1, float bellowsTo = -1)
        {
            var dense = new List<Vector3>();
            for (int i = 0; i < pts.Count - 1; i++)
            {
                Vector3 p0 = pts[Math.Max(i - 1, 0)], p1 = pts[i], p2 = pts[i + 1], p3 = pts[Math.Min(i + 2, pts.Count - 1)];
                for (int k = 0; k < 12; k++)
                {
                    float t = k / 12f, t2 = t * t, t3 = t2 * t;
                    dense.Add(0.5f * (2 * p1 + (-p0 + p2) * t + (2 * p0 - 5 * p1 + 4 * p2 - p3) * t2 + (-p0 + 3 * p1 - 3 * p2 + p3) * t3));
                }
            }
            dense.Add(pts[pts.Count - 1]);
            var cum = new List<float> { 0 };
            for (int i = 1; i < dense.Count; i++) cum.Add(cum[i - 1] + (dense[i] - dense[i - 1]).magnitude);
            float len = cum[cum.Count - 1];
            if (len < 1e-4f) return;
            bool bellows = bellowsTo > bellowsFrom && bellowsFrom >= 0;
            float b0 = bellowsFrom * len, b1 = bellowsTo * len;
            float coarse = Mathf.Max(r * 1.2f, len / 48), fine = r * 0.15f, pitch = r * 0.6f;

            // resample: coarse along plain pipe, fine through the bellows
            var path = new List<Vector3>(); var ss = new List<float>();
            float s = 0; int seg = 0;
            while (true)
            {
                while (seg < cum.Count - 2 && cum[seg + 1] < s) seg++;
                float f = Mathf.Approximately(cum[seg + 1], cum[seg]) ? 0 : (s - cum[seg]) / (cum[seg + 1] - cum[seg]);
                path.Add(Vector3.Lerp(dense[seg], dense[seg + 1], Mathf.Clamp01(f)));
                ss.Add(s);
                if (s >= len) break;
                bool inBellows = bellows && s >= b0 - fine && s < b1;
                float step = inBellows ? fine : coarse;
                if (bellows && s < b0 && s + step > b0) step = b0 - s;   // land exactly on the bellows start
                s = Mathf.Min(s + Mathf.Max(step, 1e-4f), len);
            }
            if (path.Count < 2) return;

            var ax = new List<Vector3>(); var bx = new List<Vector3>(); var rs = new List<float>(); var slopes = new List<float>();
            Basis(path[1] - path[0], out Vector3 a, out Vector3 b);
            for (int i = 0; i < path.Count; i++)
            {
                Vector3 tan = (path[Math.Min(i + 1, path.Count - 1)] - path[Math.Max(i - 1, 0)]).normalized;
                a = (a - Vector3.Dot(a, tan) * tan).normalized;   // parallel transport
                b = Vector3.Cross(tan, a);
                ax.Add(a); bx.Add(b);
                if (bellows && ss[i] >= b0 && ss[i] <= b1)
                {
                    float ph = 2 * Mathf.PI * (ss[i] - b0) / pitch;
                    rs.Add(r * (1.16f + 0.08f * Mathf.Cos(ph)));
                    slopes.Add(-0.08f * r * Mathf.Sin(ph) * 2 * Mathf.PI / pitch);
                }
                else { rs.Add(r); slopes.Add(0); }
            }
            Sweep(path, rs, ax, bx, sides, mat, true, true, slopes);

            if (flangeMat >= 0)
            {
                Vector3 t0 = (path[1] - path[0]).normalized, t1 = (path[path.Count - 1] - path[path.Count - 2]).normalized;
                Cylinder(path[0], path[0] + t0 * r * 0.45f, r * 1.45f, flangeMat, sides + 2);
                Cylinder(path[path.Count - 1] - t1 * r * 0.45f, path[path.Count - 1], r * 1.45f, flangeMat, sides + 2);
                if (bellows)
                    foreach (float sc in new[] { b0, b1 })
                    {
                        int i = ss.FindIndex(x => x >= sc - 1e-5f);
                        if (i < 0) continue;
                        Vector3 tc = (path[Math.Min(i + 1, path.Count - 1)] - path[Math.Max(i - 1, 0)]).normalized;
                        Cylinder(path[i] - tc * r * 0.18f, path[i] + tc * r * 0.18f, r * 1.3f, flangeMat, sides + 2);
                    }
            }
        }

        /// <summary>Place an authored piece: piece-space (x, y, z) maps to origin + (X x + Y y + Z z) * scale. The
        /// basis must be a rotation (det +1), so the file's winding holds.</summary>
        public void AddMesh(KitMesh m, Vector3 origin, Vector3 X, Vector3 Y, Vector3 Z, float scale, int mat, float length = 0)
        {
            // template-vertex deformation (DESIGN.md 8.5): to reach `length` (piece units, measured from y = 0 down
            // to the "end" anchor) the stretch zones (zoneN_hi above zoneN_lo, on y) grow in proportion to their
            // own length; everything below a zone moves down with it, so flanges and eyes keep their shape.
            var zones = new List<Vector3>();   // (hi, lo, delta)
            if (length > 0 && m.TryAnchor("end", out Vector3 endP, out _))
            {
                float total = 0;
                for (int z = 0; z < 10; z++)
                    if (m.TryAnchor("zone" + z + "_hi", out Vector3 hi, out _) && m.TryAnchor("zone" + z + "_lo", out Vector3 lo, out _) && hi.y > lo.y)
                    {
                        zones.Add(new Vector3(hi.y, lo.y, 0));
                        total += hi.y - lo.y;
                    }
                float delta = length - (-endP.y);
                // shrinking a zone below 30 % of its length would fold it: scale the whole piece along y instead
                if (total <= 0 || delta < -0.7f * total) { zones.Clear(); Y *= length / Mathf.Max(-endP.y, 1e-3f); }
                else for (int z = 0; z < zones.Count; z++) zones[z] = new Vector3(zones[z].x, zones[z].y, delta * (zones[z].x - zones[z].y) / total);
            }
            int start = v.Count;
            for (int i = 0; i < m.Positions.Length; i++)
            {
                Vector3 p = m.Positions[i], nn = m.Normals[i];
                foreach (Vector3 z in zones) p.y -= z.z * Mathf.Clamp01((z.x - m.Positions[i].y) / (z.x - z.y));
                v.Add(origin + (X * p.x + Y * p.y + Z * p.z) * scale);
                Vector3 wn = (X * nn.x + Y * nn.y + Z * nn.z).normalized;
                n.Add(wn);
                Vector3 t = Vector3.Cross(wn, Mathf.Abs(wn.y) < 0.95f ? Vector3.up : Vector3.right).normalized;
                tg.Add(new Vector4(t.x, t.y, t.z, -1));
                uv.Add(m.Uvs[i]);
            }
            var tl = new List<int>(m.Indices.Length);
            foreach (int i in m.Indices) tl.Add(start + i);
            tris[Math.Min(mat, tris.Length - 1)].AddRange(tl);
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
            mesh.SetTangents(tg);
            mesh.SetUVs(0, uv);
            mesh.subMeshCount = tris.Length;
            for (int i = 0; i < tris.Length; i++) mesh.SetTriangles(tris[i], i);
            mesh.RecalculateBounds();
        }
    }

    /// <summary>
    /// Kit-bashed turbomachinery, plumbing and structure per cycle (DESIGN.md 8.5), from primitives (visual only, no
    /// colliders). Pieces scale with the turbopump / pressurant mass and the propellant flow; positions avoid the bell.
    /// The regen coolant manifold is part of the gimballed bell (EngineGeometry); the fuel line ends at it.
    /// </summary>
    public static class KitBuilder
    {
        public const int MatStructure = 0, MatMachine = 1, MatLines = 2, MatHot = 3, MatFoil = 4, MatAuthored = 5, Materials = 6;

        /// <summary>Where a turbopump's lines attach (part frame): positions and outward directions.</summary>
        sealed class PumpPorts
        {
            public Vector3 OxOut, OxDir, FuelOut, FuelDir, TurbineIn, TurbineInDir, TurbineExhaust, TurbineExhaustDir, Inlet;
            public bool HasInlet, HasFuelIn, Authored;
            public Vector3 FuelIn;
        }

        public sealed class Result
        {
            public bool HasExhaust;
            public Vector3 ExhaustPosition;   // part frame, GG turbine exhaust nozzle exit
            public float ExhaustRadius;
        }

        static Vector3 Around(float deg, float radius, float y) => new Vector3(radius * Mathf.Cos(deg * Mathf.Deg2Rad), y, radius * Mathf.Sin(deg * Mathf.Deg2Rad));

        /// <summary>Propellant line sizes: constant flow velocity (15 m/s), d ~ sqrt(mdot / rho).</summary>
        public static void LineRadii(EngineDesign d, EngineStats s, ModelData data, out float fuelR, out float oxR)
        {
            float rc = (float)s.ChamberRadius;
            double of = s.Of;
            ProcEngines.Model.Propellant prop = null;
            data?.Propellants.TryGetValue(d.Propellant, out prop);
            double fuelDensity = prop?.FuelDensity ?? 800, oxDensity = prop?.OxDensity ?? 1141;
            double mdotFuel = s.MassFlow / (1 + of), mdotOx = s.MassFlow - mdotFuel;
            float LineR(double mdot, double rho) => Mathf.Clamp(Mathf.Sqrt((float)(mdot / (rho * 15) / Math.PI)), 0.006f, 0.25f * rc + 0.01f);
            fuelR = LineR(mdotFuel, fuelDensity);
            oxR = LineR(mdotOx, oxDensity);
        }

        public static Result Build(Mesh mesh, EngineDesign d, EngineStats s, EngineGeometry g, ModelData data)
        {
            var mb = new MeshBuilder(Materials);
            var res = new Result();
            float rc = g.ChamberRadius;
            double turboKg = s.MassBreakdown.TryGetValue("turbomachinery", out double tk) ? tk : 0;
            double pressKg = s.MassBreakdown.TryGetValue("pressurant", out double pk) ? pk : 0;
            float yUnder = -g.MountHeight;
            float bellLen = g.YThroat - g.YExit;
            LineRadii(d, s, data, out float fuelR, out float oxR);
            // liquid hydrogen lines wear multi-layer insulation
            int fuelMat = d.Propellant == "Hydrolox" ? MatFoil : MatLines;

            // thrust frame: four struts from the mount plate rim to the shoulder of the injector dome
            for (int k = 0; k < 4; k++)
            {
                float ang = 45 + 90 * k;
                Vector3 top = Around(ang, g.MountRadius * 0.92f, yUnder), bot = Around(ang, rc * 0.95f, g.YInjector + 0.15f * g.DomeHeight);
                float sr = Mathf.Max(0.035f * rc, 0.004f);
                mb.Cylinder(top, bot, sr, MatStructure, 8);
                mb.Sphere(bot, sr * 1.6f, MatStructure, 8);
            }
            // gimbal actuators, 90 degrees apart: the authored piece stretched to length (barrel and rod zones), or
            // primitives (clevis, cylinder with a gland, rod, rod-end bearing)
            float yAct = g.YThroat - 0.3f * bellLen;
            KitMesh actuator = KitAssets.Get("actuator");
            foreach (float ang in new[] { 135f, 225f })
            {
                Vector3 top = Around(ang, g.MountRadius * 0.85f, yUnder);
                Vector3 bot = Around(ang, g.BellRadiusAt(yAct) + 0.02f * rc + 0.005f, yAct);
                Vector3 dir = (bot - top).normalized;
                float ra = Mathf.Max(0.06f * rc, 0.006f);
                if (actuator != null)
                {
                    Vector3 Y = -dir, X = Vector3.Cross(Y, Around(ang, 1, 0)).normalized, Z = Vector3.Cross(X, Y);
                    mb.AddMesh(actuator, top, X, Y, Z, ra, MatAuthored, (bot - top).magnitude / ra);
                    continue;
                }
                Vector3 mid = Vector3.Lerp(top, bot, 0.55f);
                mb.Sphere(top + dir * ra * 0.6f, ra * 1.1f, MatStructure, 10);
                mb.Cylinder(top + dir * ra * 0.6f, mid, ra, MatMachine, 12);
                mb.Cylinder(mid - dir * ra * 0.25f, mid + dir * ra * 0.05f, ra * 1.18f, MatStructure, 12);
                mb.Cylinder(mid, bot, ra * 0.42f, MatLines, 8);
                mb.Sphere(bot, ra * 0.7f, MatStructure, 10);
            }

            // regen return manifold under the injector flange, with feeders and instrumentation bosses
            KitMesh ring = KitAssets.Get("injector_ring");
            if (ring != null) mb.AddMesh(ring, new Vector3(0, g.YInjector, 0), Vector3.right, Vector3.up, Vector3.forward, rc, MatAuthored);

            // in-line main valve at a path point, flow along `along`, actuator on the upper side
            KitMesh valveMesh = KitAssets.Get("valve");
            void Valve(Vector3 at, Vector3 along, float lineR)
            {
                if (valveMesh == null || along.sqrMagnitude < 1e-8f) return;
                Vector3 Y = along.normalized;
                Vector3 X = Vector3.up - Y * Vector3.Dot(Vector3.up, Y);
                if (X.sqrMagnitude < 1e-4f) X = Vector3.Cross(Y, Vector3.forward);
                X.Normalize();
                mb.AddMesh(valveMesh, at, X, Y, Vector3.Cross(X, Y), lineR, MatAuthored);
            }

            // a line entering the injector dome: the authored inlet (boss and flanged elbow) at radius `frac` of the
            // chamber, elbow pointing outward; returns where the line attaches and its outward direction
            KitMesh inletMesh = KitAssets.Get("dome_inlet");
            Vector3 DomeInlet(float ang, float frac, float lineR, out Vector3 inDir)
            {
                g.DomePoint(rc * frac, out Vector2 pt, out Vector2 nrm);
                Vector3 radial = Around(ang, 1, 0);
                Vector3 at = Around(ang, pt.x, pt.y), N = radial * nrm.x + Vector3.up * nrm.y;
                if (inletMesh == null || !inletMesh.TryAnchor("in", out Vector3 la, out Vector3 ld)) { inDir = N; return at; }
                Vector3 X = (radial - N * Vector3.Dot(radial, N)).normalized, Y = N, Z = Vector3.Cross(X, Y);
                mb.AddMesh(inletMesh, at, X, Y, Z, lineR, MatAuthored);
                inDir = X * ld.x + Y * ld.y + Z * ld.z;
                return at + (X * la.x + Y * la.y + Z * la.z) * lineR;
            }

            // turbopump envelope from its mass (dense machinery, ~5 t/m^3 including the housing), never wider than the chamber
            float rp = Mathf.Clamp(Mathf.Pow((float)(turboKg / (5000 * 2 * Mathf.PI)), 1f / 3f), 0.02f, Mathf.Max(0.9f * rc, 0.03f));
            Vector3 Dome(float ang, float frac) => Around(ang, rc * frac, g.YInjector + 0.4f * g.DomeHeight);
            float yMan = g.ManifoldY, manR = g.ManifoldRadius;

            // Turbopumps: the authored Blender piece when it's installed, else primitives. Everything stays below the
            // mount plate, so nothing pokes into the part stacked on top. A plain pump hangs below its inlet duct;
            // a staged-combustion pump hangs upside down, turbine end up, with its preburner on top of the turbine.
            PumpPorts Pump(float ang, float scale, bool preburner, bool dual)
            {
                float r = rp * scale;
                Vector3 radial = Around(ang, 1, 0), tang = Around(ang + 90, 1, 0);
                KitMesh km = KitAssets.Get(dual ? "turbopump_dual" : "turbopump_single");
                var p = new PumpPorts();
                if (km != null)
                {
                    Vector3 c = Around(ang, rc + r * 1.25f, yUnder - r * (preburner ? 2.8f : 1.7f));
                    Vector3 Y = preburner ? Vector3.down : Vector3.up, Z = preburner ? -tang : tang;
                    mb.AddMesh(km, c, radial, Y, Z, r, MatAuthored);
                    bool A(string name, out Vector3 pos, out Vector3 dir)
                    {
                        bool ok = km.TryAnchor(name, out Vector3 lp, out Vector3 ld);
                        pos = c + (radial * lp.x + Y * lp.y + Z * lp.z) * r;
                        dir = radial * ld.x + Y * ld.y + Z * ld.z;
                        return ok;
                    }
                    if (!A("ox_out", out p.OxOut, out p.OxDir)) A("out", out p.OxOut, out p.OxDir);
                    if (!A("fuel_out", out p.FuelOut, out p.FuelDir)) A("out", out p.FuelOut, out p.FuelDir);
                    A("turbine_in", out p.TurbineIn, out p.TurbineInDir);
                    A("turbine_exhaust", out p.TurbineExhaust, out p.TurbineExhaustDir);
                    p.HasInlet = !preburner && A("inlet", out p.Inlet, out _);
                    p.HasFuelIn = !preburner && A("fuel_in", out p.FuelIn, out _);
                    p.Authored = true;
                }
                else
                {
                    Vector3 c = Around(ang, rc + r * 1.25f, yUnder - r * (preburner ? 2.35f : 1.7f));
                    mb.Cylinder(c - Vector3.up * r, c + Vector3.up * r * 0.9f, r * 0.75f, MatMachine);            // housing
                    mb.Torus(c + Vector3.up * r * 0.2f, Vector3.up, r * 0.78f, r * 0.32f, MatMachine);            // volute
                    mb.Torus(c - Vector3.up * r * 0.55f, Vector3.up, r * 0.76f, r * 0.06f, MatStructure, 20, 6);  // housing flange
                    mb.Cylinder(c - Vector3.up * r * 1.35f, c - Vector3.up * r * 0.95f, r * 0.9f, MatStructure);  // turbine
                    p.OxOut = c + Around(ang + 90, r * 0.75f, r * 0.2f); p.OxDir = tang;
                    p.FuelOut = c + Around(ang - 90, r * 0.75f, r * 0.2f); p.FuelDir = -tang;
                    p.TurbineIn = c + Around(ang + 28, r * 0.9f, -r * 1.15f); p.TurbineInDir = Around(ang + 28, 1, 0);
                    // the primitive preburner sits on the housing; the turbine exhaust is at the bottom
                    p.TurbineExhaust = preburner ? c + Vector3.up * r * 0.9f : c - Vector3.up * r * 1.35f;
                    p.TurbineExhaustDir = preburner ? Vector3.up : Vector3.down;
                    p.Inlet = c + Vector3.up * r * 0.9f;
                    p.HasInlet = !preburner;
                }
                // inlet ducts up to the tank interface, with bellows to take up gimbal and thermal motion
                float inR = Mathf.Max(fuelR, oxR) * 1.15f;
                if (p.HasInlet) mb.Tube(new[] { p.Inlet, new Vector3(p.Inlet.x, yUnder - 0.004f, p.Inlet.z) }, inR, MatLines, 12, MatStructure, 0.3f, 0.7f);
                if (p.HasFuelIn) mb.Tube(new[] { p.FuelIn, new Vector3(p.FuelIn.x, yUnder - 0.004f, p.FuelIn.z) }, fuelR * 1.1f, fuelMat, 12, MatStructure, 0.3f, 0.7f);
                if (preburner)
                {
                    Vector3 pb = p.TurbineExhaust;
                    KitMesh pre = KitAssets.Get("preburner");
                    if (pre != null && pre.TryAnchor("hot_out", out Vector3 ha, out Vector3 hd))
                    {
                        mb.AddMesh(pre, pb, radial, Vector3.up, tang, r, MatAuthored);
                        // hot-gas duct from the preburner outlet into the injector dome
                        Vector3 hot = pb + (radial * ha.x + Vector3.up * ha.y + tang * ha.z) * r, hdir = radial * hd.x + Vector3.up * hd.y + tang * hd.z;
                        Vector3 into = Dome(ang, 0.25f);
                        mb.Tube(new[] { hot, hot + hdir * r * 0.35f, Vector3.Lerp(hot, into, 0.6f) + Vector3.up * r * 0.25f, into }, r * 0.19f, MatHot, 14, MatStructure);
                    }
                    else
                    {
                        mb.Cylinder(pb, pb + Vector3.up * r * 0.9f, r * 0.45f, MatMachine);
                        mb.Sphere(pb + Vector3.up * r * 0.9f, r * 0.45f, MatMachine);
                        mb.Torus(pb + Vector3.up * r * 0.1f, Vector3.up, r * 0.46f, r * 0.05f, MatStructure, 20, 6);
                        // hot-gas duct into the injector dome
                        mb.Tube(new[] { pb + Vector3.up * r * 1.2f, Vector3.Lerp(pb, Dome(ang, 0.2f), 0.5f) + Vector3.up * r * 0.6f, Dome(ang, 0.2f) }, r * 0.28f, MatMachine, 12, MatStructure);
                    }
                }
                return p;
            }

            // main oxidizer valve on the way, into the dome through an inlet elbow
            void OxToDome(PumpPorts p, float ang, float r)
            {
                Vector3 inlet = DomeInlet(ang + 30, 0.72f, oxR, out Vector3 inDir);
                Vector3 p1 = p.OxOut + p.OxDir * r * 0.35f, p2 = Around(ang + 30, Mathf.Max(rc * 1.05f, (inlet - Vector3.up * inlet.y).magnitude + oxR * 4), Mathf.Min(p.OxOut.y, inlet.y + oxR * 3)), p3 = inlet + inDir * oxR * 2.5f;
                mb.Tube(new[] { p.OxOut, p1, p2, p3, inlet }, oxR, MatLines, 10, MatStructure);
                Valve(p2, p3 - p1, oxR);
            }

            // the fuel line crosses onto the gimballed bell: a bellows section takes up the motion
            void FuelToManifold(PumpPorts p, float ang, float r, int mat, float sweep = -25)
            {
                float yMid = Mathf.Min(g.YChamberBottom, p.FuelOut.y - r * 0.3f);
                float yHalf = g.YThroat - 0.5f * (g.YThroat - yMan);
                Vector3 p1 = p.FuelOut + p.FuelDir * r * 0.35f, p2 = Around(ang + sweep, rc + r * 1.3f, yMid), p3 = Around(ang + sweep, g.BellRadiusAt(yHalf) + fuelR * 2, yHalf);
                mb.Tube(new[] { p.FuelOut, p1, p2, p3, Around(ang + sweep, manR, yMan) }, fuelR, mat, 10, MatStructure, 0.28f, 0.38f);
                Valve(p2, p3 - p1, fuelR);   // main fuel valve
            }

            switch (d.Cycle)
            {
                case "PressureFed":
                {
                    // composite helium bottles (COPV), sized from the pressurant mass at ~400 kg/m^3 effective, plus valves
                    int nb = pressKg > 300 ? 4 : 2;
                    float rb = Mathf.Clamp(Mathf.Pow((float)(pressKg / nb / (400 * 4.19f)), 1f / 3f), 0.02f, Mathf.Max(1.2f * rc, 0.03f));
                    for (int k = 0; k < nb; k++)
                    {
                        float ang = 360f * k / nb;
                        Vector3 bc = Around(ang, rc + rb * 1.15f, yUnder - rb * 1.1f);
                        mb.Sphere(bc, rb, MatMachine, 18);
                        mb.Torus(bc, Vector3.up, rb * 1.0f, rb * 0.04f, MatStructure, 24, 6);   // mounting band
                    }
                    foreach (float ang in new[] { 90f, 270f })
                    {
                        float lr = ang < 180 ? fuelR : oxR;
                        if (valveMesh != null)
                        {
                            // tank feed down through the main valve into the dome inlet
                            Vector3 inlet = DomeInlet(ang, 0.7f, lr, out Vector3 inDir);
                            Vector3 top = Around(ang, rc * 1.1f, yUnder - 0.004f), mid = Around(ang, rc * 1.1f, (yUnder + inlet.y) * 0.5f), near = inlet + inDir * lr * 2.5f;
                            mb.Tube(new[] { top, mid, near, inlet }, lr, MatLines, 10, MatStructure);
                            Valve(mid, near - top, lr);
                            continue;
                        }
                        Vector3 valve = Around(ang, rc * 0.85f, yUnder - 0.3f * g.DomeHeight);
                        float vr = Mathf.Max(oxR * 1.25f, 0.005f);
                        mb.Sphere(valve, vr, MatMachine, 12);
                        mb.Cylinder(valve, valve + Around(ang, vr * 1.6f, 0), vr * 0.45f, MatStructure, 8);   // actuator boss
                        mb.Tube(new[] { valve + Vector3.up * 0.01f, Dome(ang, 0.6f) }, lr, MatLines, 10, MatStructure);
                    }
                    break;
                }
                case "GasGenerator":
                {
                    float r = rp;
                    PumpPorts p = Pump(0, 1, false, true);
                    OxToDome(p, 0, r);
                    FuelToManifold(p, 0, r, fuelMat);
                    // gas generator bolted onto the turbine inlet
                    KitMesh ggMesh = KitAssets.Get("gas_generator");
                    Vector3 gx = (p.TurbineInDir - Vector3.up * Vector3.Dot(p.TurbineInDir, Vector3.up)).normalized;
                    if (ggMesh != null && gx.sqrMagnitude > 0.5f)
                        mb.AddMesh(ggMesh, p.TurbineIn, gx, Vector3.up, Vector3.Cross(gx, Vector3.up), r, MatAuthored);
                    else
                    {
                        Vector3 gg = p.TurbineIn + p.TurbineInDir * r * 0.45f + Vector3.up * r * 0.1f;
                        mb.Sphere(gg, r * 0.3f, MatMachine);
                        mb.Cylinder(gg, p.TurbineIn, r * 0.12f, MatHot, 10);
                        mb.Cylinder(gg + Vector3.up * r * 0.25f, gg + Vector3.up * r * 0.45f, r * 0.14f, MatMachine, 10);
                    }
                    // exhaust duct down past the bell ending in a small nozzle
                    float yEx = g.YThroat - 0.45f * bellLen;
                    float ductR = Mathf.Max(r * 0.32f, 0.01f);
                    float clear = g.BellRadiusAt(yEx) + ductR * 1.6f;
                    Vector3 exitTop = Around(-35, clear, yEx);
                    Vector3 exitBottom = exitTop - Vector3.up * ductR * 2.2f;
                    Vector3 te = p.TurbineExhaust;
                    float yMid = Mathf.Min(g.YChamberBottom, te.y - r * 0.4f);
                    float yLow = Mathf.Min(g.YThroat - 0.2f * bellLen, yMid - r * 0.2f);
                    // uncooled turbine exhaust: heat-tinted, and it glows dull red while running (MatHot)
                    mb.Tube(new[] { te, te + p.TurbineExhaustDir * r * 0.3f, Around(-20, rc + r * 1.25f, yMid), Around(-30, g.BellRadiusAt(yLow) + ductR * 1.6f, yLow), exitTop }, ductR, MatHot, 12, MatStructure);
                    mb.Frustum(exitTop, exitBottom, ductR, ductR * 1.7f, MatHot, 14, false);
                    res.HasExhaust = true;
                    res.ExhaustPosition = exitBottom;
                    res.ExhaustRadius = ductR * 1.7f;
                    break;
                }
                case "Expander":
                {
                    float r = rp;
                    PumpPorts p = Pump(0, 1, false, true);
                    OxToDome(p, 0, r);
                    // cold hydrogen from the pump to the manifold, insulated
                    FuelToManifold(p, 0, r, MatFoil, -55);
                    // heated hydrogen: regen manifold up to the turbine inlet (warm gas: plain line, no insulation)
                    float yHalf = g.YThroat - 0.5f * (g.YThroat - yMan);
                    mb.Tube(new[] { Around(-25, manR, yMan), Around(-25, g.BellRadiusAt(yHalf) + fuelR * 2, yHalf), Around(-15, rc + r * 1.3f, Mathf.Min(g.YChamberBottom, p.TurbineIn.y - r * 0.3f)), p.TurbineIn + p.TurbineInDir * r * 0.4f, p.TurbineIn }, fuelR, MatLines, 10, MatStructure, 0.7f, 0.82f);
                    break;
                }
                case "StagedCombustion":
                {
                    PumpPorts p = Pump(0, 1, true, true);
                    OxToDome(p, 0, rp);
                    FuelToManifold(p, 0, rp, fuelMat);
                    break;
                }
                case "FullFlowStaged":
                {
                    // one single-propellant pump per side, each with its own preburner
                    PumpPorts fp = Pump(0, 0.8f, true, false);
                    FuelToManifold(fp, 0, rp * 0.8f, fuelMat);
                    PumpPorts op = Pump(180, 0.8f, true, false);
                    OxToDome(op, 180, rp * 0.8f);
                    break;
                }
            }

            mb.Build(mesh);
            return res;
        }
    }
}
