using System;
using System.Collections.Generic;
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
}
