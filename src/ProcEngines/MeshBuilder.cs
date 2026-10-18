using System;
using System.Collections.Generic;
using UnityEngine;

namespace ProcEngines
{
    /// <summary>
    /// Accumulates lathe surfaces, primitives and authored pieces into one mesh with a few submeshes.
    ///
    /// Tangent frame (ASSETS.md 2): tangent.xyz = direction of increasing U, and w is chosen so that Unity's
    /// binormal, cross(normal, tangent.xyz) * w, points toward increasing V. On an outward lathe surface or a swept
    /// tube (V growing along the profile or path) that is w = +1; surfaces whose V runs backwards or whose normal
    /// faces the other way get w = -1, so every normal map reads with the OpenGL convention (green = +V)
    /// everywhere. Authored pieces keep their file tangents (rotated with the piece).
    /// </summary>
    public sealed class MeshBuilder
    {
        readonly List<Vector3> v, n;
        readonly List<Vector4> tg;
        readonly List<Vector2> uv;
        readonly List<int>[] tris;

        public MeshBuilder(int materials) : this(materials, 1024) { }

        /// <param name="vertexCapacity">Expected vertex count, to preallocate the buffers (they still grow).</param>
        public MeshBuilder(int materials, int vertexCapacity)
        {
            int cap = Math.Max(vertexCapacity, 16);
            v = new List<Vector3>(cap); n = new List<Vector3>(cap); tg = new List<Vector4>(cap); uv = new List<Vector2>(cap);
            tris = new List<int>[Math.Max(materials, 1)];
            for (int i = 0; i < tris.Length; i++) tris[i] = new List<int>(cap * 6 / tris.Length + 16);
        }

        public int VertexCount => v.Count;

        List<int> Tris(int mat) => tris[mat < 0 ? 0 : Math.Min(mat, tris.Length - 1)];

        void Reserve(int moreVertices)
        {
            int need = v.Count + moreVertices;
            if (v.Capacity >= need) return;
            int cap = Math.Max(need, v.Capacity * 2);
            v.Capacity = cap; n.Capacity = cap; tg.Capacity = cap; uv.Capacity = cap;
        }

        static void Reserve(List<int> t, int more)
        {
            int need = t.Count + more;
            if (t.Capacity < need) t.Capacity = Math.Max(need, t.Capacity * 2);
        }

        static void Basis(Vector3 dir, out Vector3 a, out Vector3 b)
        {
            dir.Normalize();
            a = Vector3.Cross(dir, Mathf.Abs(dir.y) < 0.95f ? Vector3.up : Vector3.right).normalized;
            b = Vector3.Cross(dir, a);
        }

        /// <summary>Make the winding of triangles t[from..] agree with their vertex normals (all or none flipped).</summary>
        void FixWinding(List<int> t, int from)
        {
            int agree = 0, disagree = 0;
            for (int k = from; k + 2 < t.Count; k += 3)
            {
                int a = t[k], b = t[k + 1], c = t[k + 2];
                Vector3 fn = Vector3.Cross(v[b] - v[a], v[c] - v[a]);
                if (fn.sqrMagnitude < 1e-14f) continue;
                if (Vector3.Dot(fn, n[a] + n[b] + n[c]) >= 0) agree++; else disagree++;
            }
            if (disagree > agree)
                for (int k = from; k + 2 < t.Count; k += 3) { int x = t[k + 1]; t[k + 1] = t[k + 2]; t[k + 2] = x; }
        }

        static void Ring(int sides, out float[] cs, out float[] sn)
        {
            cs = new float[sides + 1]; sn = new float[sides + 1];
            for (int j = 0; j < sides; j++)
            {
                float phi = 2 * Mathf.PI * j / sides;
                cs[j] = Mathf.Cos(phi); sn[j] = Mathf.Sin(phi);
            }
            cs[sides] = 1; sn[sides] = 0;   // the seam column repeats column 0 exactly
        }

        // ------------------------------------------------------------------ lathe

        /// <summary>Revolve profile segments around Y. Normals come from the profile tangent (central differences
        /// along each smooth segment; joints between segments are creases). U repeats UTiles times around (seam
        /// vertex duplicated), V runs along the arc length.</summary>
        public void Lathe(List<ProfileSegment> segments, int ringSegments, float yOffset)
        {
            int ring = ringSegments + 1;
            Ring(ringSegments, out float[] cs, out float[] sn);
            for (int si = 0; si < segments.Count; si++)
            {
                ProfileSegment segment = segments[si];
                List<Vector2> seg = segment.Points;
                if (seg == null || seg.Count < 2) continue;
                // tangent = +U; binormal = cross(N, T) * w must follow +V. cross(N, T) runs along the profile for an
                // outward surface with U increasing, so w flips for an inward surface, U or V running backwards.
                float uSign = segment.UTiles < 0 ? -1 : 1;
                float w = uSign * (segment.Inward ? -1 : 1) * (segment.VLength < 0 ? -1 : 1);
                Reserve(seg.Count * ring);
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
                    float r = seg[i].x, y = seg[i].y - yOffset, vv = segment.VOffset + acc / segment.VLength;
                    for (int j = 0; j < ring; j++)
                    {
                        float c = cs[j], s = sn[j];
                        v.Add(new Vector3(r * c, y, r * s));
                        n.Add(new Vector3(n2.x * c, n2.y, n2.x * s));
                        tg.Add(new Vector4(-s * uSign, 0, c * uSign, w));
                        uv.Add(new Vector2(segment.UTiles * j / ringSegments, vv));
                    }
                }
                List<int> t = Tris(segment.Material);
                int from = t.Count;
                Reserve(t, (seg.Count - 1) * ringSegments * 6);
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
                FixWinding(t, from);
            }
        }

        // ------------------------------------------------------------------ swept primitives

        /// <summary>Generic swept ring strip: rings[i] centre, radius, frame axes (a x b = forward). `slopes` (dr/ds)
        /// tilts the normals of a rippled surface (bellows). V is arc length in circumferences (square texels).</summary>
        void Sweep(IList<Vector3> centres, IList<float> radii, IList<Vector3> ax, IList<Vector3> bx, int sides, int mat, bool capStart, bool capEnd, IList<float> slopes = null)
        {
            int count = centres.Count;
            if (count < 2) return;
            int start = v.Count, ring = sides + 1;
            Ring(sides, out float[] cs, out float[] sn);
            Reserve(count * ring);
            float circ = 2 * Mathf.PI * Mathf.Max(radii[0], 1e-3f), s = 0;
            for (int i = 0; i < count; i++)
            {
                if (i > 0) s += (centres[i] - centres[i - 1]).magnitude;
                Vector3 a = ax[i], b = bx[i], fwd = Vector3.Cross(a, b);
                // cross(N, T) = a x b; V grows along the path, so w = +1 when the frame's forward follows it
                Vector3 path = centres[Math.Min(i + 1, count - 1)] - centres[Math.Max(i - 1, 0)];
                float w = Vector3.Dot(fwd, path) < 0 ? -1 : 1;
                float slope = slopes == null ? 0 : slopes[i], r = radii[i], vv = s / circ;
                Vector3 c0 = centres[i];
                for (int j = 0; j <= sides; j++)
                {
                    float c = cs[j], sj = sn[j];
                    Vector3 dir = a * c + b * sj;
                    Vector3 tu = b * c - a * sj;
                    v.Add(c0 + dir * r);
                    n.Add(slope == 0 ? dir : (dir - fwd * slope).normalized);
                    tg.Add(new Vector4(tu.x, tu.y, tu.z, w));
                    uv.Add(new Vector2((float)j / sides, vv));
                }
            }
            List<int> t = Tris(mat);
            int from = t.Count;
            Reserve(t, (count - 1) * sides * 6);
            for (int i = 0; i < count - 1; i++)
                for (int j = 0; j < sides; j++)
                {
                    int a = start + i * ring + j, b = a + 1, c = a + ring, d = c + 1;
                    t.Add(a); t.Add(c); t.Add(b); t.Add(b); t.Add(c); t.Add(d);
                }
            FixWinding(t, from);
            if (capStart) Cap(centres[0], centres[0] - centres[1], radii[0], ax[0], bx[0], sides, mat);
            if (capEnd) Cap(centres[count - 1], centres[count - 1] - centres[count - 2], radii[radii.Count - 1], ax[ax.Count - 1], bx[bx.Count - 1], sides, mat);
        }

        /// <summary>Flat disc; planar UVs, U along a and V along b.</summary>
        void Cap(Vector3 c, Vector3 outward, float r, Vector3 a, Vector3 b, int sides, int mat)
        {
            outward.Normalize();
            float w = Vector3.Dot(Vector3.Cross(outward, a), b) < 0 ? -1 : 1;
            var tan = new Vector4(a.x, a.y, a.z, w);
            Ring(sides, out float[] cs, out float[] sn);
            Reserve(sides + 2);
            int start = v.Count;
            v.Add(c); n.Add(outward); tg.Add(tan); uv.Add(new Vector2(0.5f, 0.5f));
            for (int j = 0; j <= sides; j++)
            {
                v.Add(c + (a * cs[j] + b * sn[j]) * r); n.Add(outward); tg.Add(tan);
                uv.Add(new Vector2(0.5f + 0.5f * cs[j], 0.5f + 0.5f * sn[j]));
            }
            List<int> t = Tris(mat);
            int from = t.Count;
            for (int j = 0; j < sides; j++) { t.Add(start); t.Add(start + 1 + j); t.Add(start + 2 + j); }
            FixWinding(t, from);
        }

        /// <summary>
        /// A turned part around `dir` through `o`: rings at (axial offset, radius) with their own profile normal
        /// (axial, radial components). Consecutive rings are joined and share vertices, so a short band between a
        /// face normal and a side normal shades as a rounded edge (a bevel that catches the light). U goes around,
        /// V is the profile length over the largest circumference (square texels there).
        /// </summary>
        void Turned(Vector3 o, Vector3 dir, float[] axial, float[] radius, float[] nAxial, float[] nRadial, int count, int sides, int mat)
        {
            Basis(dir, out Vector3 a, out Vector3 b);
            dir.Normalize();
            int ring = sides + 1, start = v.Count;
            Ring(sides, out float[] cs, out float[] sn);
            Reserve(count * ring);
            float rmax = 1e-4f;
            for (int i = 0; i < count; i++) rmax = Mathf.Max(rmax, radius[i]);
            float circ = 2 * Mathf.PI * rmax, s = 0;
            for (int i = 0; i < count; i++)
            {
                if (i > 0) s += Mathf.Sqrt((axial[i] - axial[i - 1]) * (axial[i] - axial[i - 1]) + (radius[i] - radius[i - 1]) * (radius[i] - radius[i - 1]));
                int i0 = Math.Max(i - 1, 0), i1 = Math.Min(i + 1, count - 1);
                float pa = axial[i1] - axial[i0], pr = radius[i1] - radius[i0];   // profile direction (+V)
                Vector3 centre = o + dir * axial[i];
                for (int j = 0; j <= sides; j++)
                {
                    Vector3 radial = a * cs[j] + b * sn[j], tu = b * cs[j] - a * sn[j];
                    Vector3 nn = (radial * nRadial[i] + dir * nAxial[i]).normalized;
                    float w = Vector3.Dot(Vector3.Cross(nn, tu), dir * pa + radial * pr) < 0 ? -1 : 1;
                    v.Add(centre + radial * radius[i]);
                    n.Add(nn);
                    tg.Add(new Vector4(tu.x, tu.y, tu.z, w));
                    uv.Add(new Vector2((float)j / sides, s / circ));
                }
            }
            List<int> t = Tris(mat);
            int from = t.Count;
            Reserve(t, (count - 1) * sides * 6);
            for (int i = 0; i < count - 1; i++)
            {
                if (Mathf.Abs(axial[i + 1] - axial[i]) + Mathf.Abs(radius[i + 1] - radius[i]) < 1e-7f) continue;
                for (int j = 0; j < sides; j++)
                {
                    int p = start + i * ring + j, q = p + 1, r = p + ring, u = r + 1;
                    t.Add(p); t.Add(r); t.Add(q); t.Add(q); t.Add(r); t.Add(u);
                }
            }
            FixWinding(t, from);
        }

        /// <summary>A disc of radius R and thickness h along `dir` from `o`, bevelled on both outer edges, with
        /// faces from radius rIn (0 = solid disc) up to the bevel.</summary>
        void BevelledDisc(Vector3 o, Vector3 dir, float h, float R, float rIn, float bevel, int sides, int mat)
        {
            float b = Mathf.Min(bevel, 0.45f * h, 0.45f * (R - rIn));
            var ax = new[] { 0, 0, b, h - b, h, h };
            var ra = new[] { rIn, R - b, R, R, R - b, rIn };
            var na = new[] { -1f, -1f, 0, 0, 1, 1 };
            var nr = new[] { 0f, 0, 1, 1, 0, 0 };
            Turned(o, dir, ax, ra, na, nr, 6, sides, mat);
        }

        /// <summary>Small hex-ish bolt heads standing on a face: `count` heads on a circle of radius rc around `o`,
        /// heads of radius rb and height hb along `dir`.</summary>
        void BoltCircle(Vector3 o, Vector3 dir, float rc, float rb, float hb, int count, int mat)
        {
            Basis(dir, out Vector3 a, out Vector3 b);
            dir.Normalize();
            // side, rounded top edge, centre: 3 rings of 7 vertices per head
            var ax = new[] { -0.2f * hb, hb, hb };
            var ra = new[] { rb, rb * 0.85f, 0 };
            var na = new[] { 0f, 0.8f, 1 };
            var nr = new[] { 1f, 0.6f, 0 };
            for (int k = 0; k < count; k++)
            {
                float phi = 2 * Mathf.PI * (k + 0.5f) / count;
                Turned(o + (a * Mathf.Cos(phi) + b * Mathf.Sin(phi)) * rc, dir, ax, ra, na, nr, 3, 6, mat);
            }
        }

        /// <summary>Truncated cone between p0 (radius r0) and p1 (r1). With caps, the end faces close it and both
        /// rims are bevelled (rounded-edge shading); without caps it is an open sleeve.</summary>
        public void Frustum(Vector3 p0, Vector3 p1, float r0, float r1, int mat, int sides = 14, bool caps = true)
        {
            Vector3 d = p1 - p0;
            float L = d.magnitude;
            if (L < 1e-6f) return;
            if (!caps)
            {
                Basis(d, out Vector3 a, out Vector3 b);
                Sweep(new[] { p0, p1 }, new[] { r0, r1 }, new[] { a, a }, new[] { b, b }, sides, mat, false, false);
                return;
            }
            float bev = Mathf.Min(0.07f * Mathf.Max(r0, r1), 0.2f * L);
            float k = (r1 - r0) / L;   // dr/da along the side
            float sl = 1 / Mathf.Sqrt(1 + k * k);
            float sideNa = -k * sl, sideNr = sl;   // side normal (axial, radial)
            var ax = new[] { 0, 0, bev, L - bev, L, L };
            var ra = new[] { 0, Mathf.Max(r0 - bev, 0), r0 + k * bev, r1 - k * bev, Mathf.Max(r1 - bev, 0), 0 };
            var na = new[] { -1f, -1f, sideNa, sideNa, 1, 1 };
            var nr = new[] { 0f, 0, sideNr, sideNr, 0, 0 };
            Turned(p0, d, ax, ra, na, nr, 6, sides, mat);
        }

        public void Cylinder(Vector3 p0, Vector3 p1, float r, int mat, int sides = 14) => Frustum(p0, p1, r, r, mat, sides);

        public void Sphere(Vector3 c, float r, int mat, int sides = 14)
        {
            int rings = Math.Max(8, sides / 2);
            var cs = new Vector3[rings + 1]; var rs = new float[rings + 1]; var ax = new Vector3[rings + 1]; var bx = new Vector3[rings + 1];
            for (int i = 0; i <= rings; i++)
            {
                float th = Mathf.PI * i / rings;
                cs[i] = c + Vector3.up * (r * Mathf.Cos(th)); rs[i] = Mathf.Max(r * Mathf.Sin(th), 1e-4f); ax[i] = Vector3.right; bx[i] = Vector3.forward;
            }
            // normals of a sphere are radial, not the ring direction: fix after the sweep (the tangents stay valid)
            int start = v.Count;
            Sweep(cs, rs, ax, bx, sides, mat, false, false);
            for (int i = start; i < v.Count; i++) n[i] = (v[i] - c).normalized;
        }

        public void Torus(Vector3 c, Vector3 axis, float R, float r, int mat, int segs = 20, int sides = 8)
        {
            Basis(axis, out Vector3 a, out Vector3 b);
            Vector3 up = axis.normalized;
            var cs = new Vector3[segs + 1]; var rs = new float[segs + 1]; var ax = new Vector3[segs + 1]; var bx = new Vector3[segs + 1];
            for (int i = 0; i <= segs; i++)
            {
                float th = 2 * Mathf.PI * i / segs;
                Vector3 radial = i == segs ? a : a * Mathf.Cos(th) + b * Mathf.Sin(th);
                cs[i] = c + radial * R; rs[i] = r; ax[i] = radial; bx[i] = up;
            }
            Sweep(cs, rs, ax, bx, sides, mat, false, false);
        }

        // ------------------------------------------------------------------ tubes

        /// <summary>Pipe along a smoothed path (Catmull-Rom through the points, resampled by arc length with extra
        /// rings in tight bends, parallel-transported frames). Optional bolted, bevelled flanges at both ends and a
        /// convoluted bellows section (fractions of the length) with collars at its ends.</summary>
        public void Tube(IList<Vector3> pts, float r, int mat, int sides = 10, int flangeMat = -1, float bellowsFrom = -1, float bellowsTo = -1)
        {
            if (pts == null || pts.Count < 2 || sides < 3) return;
            const int perSpan = 16;
            int nd = (pts.Count - 1) * perSpan + 1;
            var dense = new Vector3[nd];
            for (int i = 0; i < pts.Count - 1; i++)
            {
                Vector3 p0 = pts[Math.Max(i - 1, 0)], p1 = pts[i], p2 = pts[i + 1], p3 = pts[Math.Min(i + 2, pts.Count - 1)];
                for (int k = 0; k < perSpan; k++)
                {
                    float t = k / (float)perSpan, t2 = t * t, t3 = t2 * t;
                    dense[i * perSpan + k] = 0.5f * (2 * p1 + (-p0 + p2) * t + (2 * p0 - 5 * p1 + 4 * p2 - p3) * t2 + (-p0 + 3 * p1 - 3 * p2 + p3) * t3);
                }
            }
            dense[nd - 1] = pts[pts.Count - 1];
            var cum = new float[nd];
            for (int i = 1; i < nd; i++) cum[i] = cum[i - 1] + (dense[i] - dense[i - 1]).magnitude;
            float len = cum[nd - 1];
            if (len < 1e-4f) return;
            // curvature (turn angle per length) at each dense point, for the adaptive ring spacing
            var kappa = new float[nd];
            for (int i = 1; i < nd - 1; i++)
            {
                Vector3 d0 = dense[i] - dense[i - 1], d1 = dense[i + 1] - dense[i];
                float l = 0.5f * (d0.magnitude + d1.magnitude);
                if (l > 1e-7f && d0.sqrMagnitude > 1e-14f && d1.sqrMagnitude > 1e-14f) kappa[i] = Vector3.Angle(d0, d1) * Mathf.Deg2Rad / l;
            }

            bool bellows = bellowsTo > bellowsFrom && bellowsFrom >= 0 && bellowsFrom < 1;
            float b0 = Mathf.Max(bellowsFrom, 0) * len, b1 = Mathf.Min(bellowsTo, 1) * len;
            float coarse = Mathf.Max(r * 1.2f, len / 48), maxTurn = 0.5f * 2 * Mathf.PI / sides, minStep = r * 0.2f;
            int convolutions = bellows ? Math.Max(1, Mathf.RoundToInt((b1 - b0) / (r * 0.6f))) : 0;
            float pitch = bellows ? (b1 - b0) / convolutions : 1;
            int ringsPerPitch = Mathf.Clamp(sides / 2, 4, 10);
            float neck = 0.5f * pitch;   // the bellows rise smoothly from the pipe radius over half a pitch at each end

            // stations along the path
            var ss = new List<float>(64);
            int ks = 0;
            void Plain(float from, float to, bool includeFrom)
            {
                float s = from;
                if (includeFrom) ss.Add(s);
                while (s < to - 1e-6f)
                {
                    while (ks < nd - 2 && cum[ks + 1] < s) ks++;
                    float kap = Mathf.Max(kappa[ks], Mathf.Max(kappa[Math.Min(ks + 1, nd - 1)], kappa[Math.Min(ks + 2, nd - 1)]));
                    float step = kap > 1e-6f ? Mathf.Clamp(maxTurn / kap, minStep, coarse) : coarse;
                    s = s + step > to - 0.3f * step ? to : s + step;
                    ss.Add(s);
                }
            }
            if (bellows && b1 - b0 > 1e-4f)
            {
                Plain(0, b0, true);
                int nb = convolutions * ringsPerPitch;
                for (int k = 1; k <= nb; k++) ss.Add(b0 + (b1 - b0) * k / nb);
                if (len - b1 > 1e-4f) Plain(b1, len, false);
            }
            else { bellows = false; Plain(0, len, true); }

            int count = ss.Count;
            var path = new Vector3[count];
            int seg = 0;
            for (int i = 0; i < count; i++)
            {
                float s = ss[i];
                while (seg < nd - 2 && cum[seg + 1] < s) seg++;
                float span = cum[seg + 1] - cum[seg];
                float f = span < 1e-9f ? 0 : (s - cum[seg]) / span;
                path[i] = Vector3.Lerp(dense[seg], dense[seg + 1], Mathf.Clamp01(f));
            }

            var ax = new Vector3[count]; var bx = new Vector3[count]; var rs = new float[count]; var slopes = new float[count];
            Basis(path[1] - path[0], out Vector3 a, out Vector3 b);
            for (int i = 0; i < count; i++)
            {
                Vector3 tan = (path[Math.Min(i + 1, count - 1)] - path[Math.Max(i - 1, 0)]).normalized;
                Vector3 ap = a - Vector3.Dot(a, tan) * tan;   // parallel transport
                if (ap.sqrMagnitude > 1e-12f) a = ap.normalized;
                b = Vector3.Cross(tan, a);
                ax[i] = a; bx[i] = b;
                if (bellows && ss[i] > b0 && ss[i] < b1)
                {
                    // convolutions r (1.16 + 0.08 cos) under a smooth envelope rising from the pipe radius at each end
                    float ph = 2 * Mathf.PI * (ss[i] - b0) / pitch, k = 2 * Mathf.PI / pitch;
                    float ta = Mathf.Clamp01((ss[i] - b0) / neck), tb = Mathf.Clamp01((b1 - ss[i]) / neck);
                    float ea = ta * ta * (3 - 2 * ta), eb = tb * tb * (3 - 2 * tb);
                    float dea = ta < 1 ? 6 * ta * (1 - ta) / neck : 0, deb = tb < 1 ? -6 * tb * (1 - tb) / neck : 0;
                    float env = ea * eb, denv = dea * eb + ea * deb, wave = 0.16f + 0.08f * Mathf.Cos(ph);
                    rs[i] = r * (1 + env * wave);
                    slopes[i] = r * (denv * wave - env * 0.08f * k * Mathf.Sin(ph));
                }
                else rs[i] = r;
            }
            bool flanges = flangeMat >= 0;
            Sweep(path, rs, ax, bx, sides, mat, !flanges, !flanges, bellows ? slopes : null);

            if (flanges)
            {
                int fs = sides + 2;
                float R = r * 1.45f, h = r * 0.45f, bev = r * 0.07f;
                Vector3 t0 = (path[1] - path[0]).normalized, t1 = (path[count - 2] - path[count - 1]).normalized;
                // end flanges: a solid disc closes the pipe end (no coplanar cap); at full detail (10+ sides, i.e.
                // not the LOD1 kit) eight bolt heads on the back face
                BevelledDisc(path[0], t0, h, R, 0, bev, fs, flangeMat);
                BevelledDisc(path[count - 1], t1, h, R, 0, bev, fs, flangeMat);
                if (sides >= 10)
                {
                    BoltCircle(path[0] + t0 * h, t0, r * 1.23f, r * 0.1f, r * 0.08f, 8, flangeMat);
                    BoltCircle(path[count - 1] + t1 * h, t1, r * 1.23f, r * 0.1f, r * 0.08f, 8, flangeMat);
                }
                if (bellows)
                    for (int e = 0; e < 2; e++)
                    {
                        // collars over the necks, a little inside the bellows so they cover the rise
                        float sc = e == 0 ? Mathf.Min(b0 + 0.1f * r, 0.5f * (b0 + b1)) : Mathf.Max(b1 - 0.1f * r, 0.5f * (b0 + b1));
                        int i = 1;
                        while (i < count - 1 && ss[i] < sc) i++;
                        float span = ss[i] - ss[i - 1], f = span > 1e-9f ? Mathf.Clamp01((sc - ss[i - 1]) / span) : 0;
                        Vector3 pc = Vector3.Lerp(path[i - 1], path[i], f), tc = (path[i] - path[i - 1]).normalized;
                        BevelledDisc(pc - tc * r * 0.18f, tc, r * 0.36f, r * 1.3f, r * 0.98f, r * 0.05f, fs, flangeMat);
                    }
            }
        }

        // ------------------------------------------------------------------ authored pieces

        static readonly string[] ZoneHi = { "zone0_hi", "zone1_hi", "zone2_hi", "zone3_hi", "zone4_hi", "zone5_hi", "zone6_hi", "zone7_hi", "zone8_hi", "zone9_hi" };
        static readonly string[] ZoneLo = { "zone0_lo", "zone1_lo", "zone2_lo", "zone3_lo", "zone4_lo", "zone5_lo", "zone6_lo", "zone7_lo", "zone8_lo", "zone9_lo" };

        /// <summary>Place an authored piece: piece-space (x, y, z) maps to origin + (X x + Y y + Z z) * scale. The
        /// basis should be a rotation; an orthogonal basis with other lengths or a mirror is handled (normals by the
        /// inverse transpose, tangent sign and winding flipped for a mirror). Tangents are the file's (rotated, w
        /// kept) or, for a version 1 file, derived from its UVs.</summary>
        public void AddMesh(KitMesh m, Vector3 origin, Vector3 X, Vector3 Y, Vector3 Z, float scale, int mat, float length = 0)
        {
            if (m == null || m.Positions == null || m.Positions.Length == 0) return;
            // template-vertex deformation (DESIGN.md 8.5): to reach `length` (piece units, measured from y = 0 down
            // to the "end" anchor) the stretch zones (zoneN_hi above zoneN_lo, on y) grow in proportion to their
            // own length; everything below a zone moves down with it, so flanges and eyes keep their shape.
            int zc = 0;
            float[] zHi = null, zLo = null, zDelta = null, zK = null;
            if (length > 0 && m.TryAnchor("end", out Vector3 endP, out _))
            {
                zHi = new float[10]; zLo = new float[10]; zDelta = new float[10]; zK = new float[10];
                float total = 0;
                for (int z = 0; z < 10; z++)
                    if (m.TryAnchor(ZoneHi[z], out Vector3 hi, out _) && m.TryAnchor(ZoneLo[z], out Vector3 lo, out _) && hi.y > lo.y)
                    {
                        zHi[zc] = hi.y; zLo[zc] = lo.y;
                        total += hi.y - lo.y;
                        zc++;
                    }
                float delta = length - (-endP.y);
                // shrinking a zone below 30 % of its length would fold it: scale the whole piece along y instead
                if (total <= 0 || delta < -0.7f * total) { zc = 0; Y *= length / Mathf.Max(-endP.y, 1e-3f); }
                else
                    for (int z = 0; z < zc; z++)
                    {
                        float zl = zHi[z] - zLo[z];
                        zDelta[z] = delta * zl / total;
                        zK[z] = 1 + zDelta[z] / zl;   // local stretch along y inside the zone
                    }
            }

            Vector3[] P = m.Positions, N = m.Normals;
            Vector2[] UV = m.Uvs;
            Vector4[] T = m.TangentsOrFallback();
            int nv = P.Length, start = v.Count;
            Reserve(nv);
            // positions: scaled basis; normals: inverse transpose of an orthogonal basis = columns / squared length
            float pxx = X.x * scale, pxy = X.y * scale, pxz = X.z * scale, pyx = Y.x * scale, pyy = Y.y * scale, pyz = Y.z * scale, pzx = Z.x * scale, pzy = Z.y * scale, pzz = Z.z * scale;
            float ix = 1 / Mathf.Max(X.sqrMagnitude, 1e-20f), iy = 1 / Mathf.Max(Y.sqrMagnitude, 1e-20f), iz = 1 / Mathf.Max(Z.sqrMagnitude, 1e-20f);
            float hand = Vector3.Dot(Vector3.Cross(X, Y), Z) < 0 ? -1 : 1;
            for (int i = 0; i < nv; i++)
            {
                Vector3 p = P[i];
                float y0 = p.y, ky = 1;
                for (int z = 0; z < zc; z++)
                {
                    float f = (zHi[z] - y0) / (zHi[z] - zLo[z]);
                    if (f <= 0) continue;
                    if (f < 1) ky *= zK[z]; else f = 1;
                    p.y -= zDelta[z] * f;
                }
                v.Add(new Vector3(origin.x + pxx * p.x + pyx * p.y + pzx * p.z, origin.y + pxy * p.x + pyy * p.y + pzy * p.z, origin.z + pxz * p.x + pyz * p.y + pzz * p.z));

                Vector3 q = N[i];
                float qx = q.x * ix, qy = q.y / ky * iy, qz = q.z * iz;
                float nx = X.x * qx + Y.x * qy + Z.x * qz, ny = X.y * qx + Y.y * qy + Z.y * qz, nz = X.z * qx + Y.z * qy + Z.z * qz;
                float nl = Mathf.Sqrt(nx * nx + ny * ny + nz * nz);
                if (nl > 1e-20f) { nl = 1 / nl; nx *= nl; ny *= nl; nz *= nl; } else { nx = 0; ny = 1; nz = 0; }
                n.Add(new Vector3(nx, ny, nz));

                Vector4 t4 = T[i];
                float sy = t4.y * ky;
                float tx = X.x * t4.x + Y.x * sy + Z.x * t4.z, ty = X.y * t4.x + Y.y * sy + Z.y * t4.z, tz = X.z * t4.x + Y.z * sy + Z.z * t4.z;
                float dn = tx * nx + ty * ny + tz * nz;
                tx -= dn * nx; ty -= dn * ny; tz -= dn * nz;   // keep the frame orthogonal after the stretch
                float tl = tx * tx + ty * ty + tz * tz;
                if (tl < 1e-20f)
                {
                    Vector3 alt = Vector3.Cross(new Vector3(nx, ny, nz), Mathf.Abs(ny) < 0.95f ? Vector3.up : Vector3.right);
                    tx = alt.x; ty = alt.y; tz = alt.z; tl = alt.sqrMagnitude;
                }
                tl = 1 / Mathf.Sqrt(tl);
                tg.Add(new Vector4(tx * tl, ty * tl, tz * tl, (t4.w < 0 ? -1 : 1) * hand));
                uv.Add(UV[i]);
            }
            int[] I = m.Indices;
            List<int> t = Tris(mat);
            Reserve(t, I.Length);
            if (hand > 0)
                for (int k = 0; k < I.Length; k++) t.Add(start + I[k]);
            else
                for (int k = 0; k + 2 < I.Length; k += 3) { t.Add(start + I[k]); t.Add(start + I[k + 2]); t.Add(start + I[k + 1]); }
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
            for (int i = 0; i < tris.Length; i++) mesh.SetTriangles(tris[i], i, false);
            mesh.RecalculateBounds();
        }
    }
}
