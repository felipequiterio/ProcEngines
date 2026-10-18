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
    ///
    /// Garbage: a builder from <see cref="Pooled"/> keeps its buffers between builds (Reset keeps the capacity), and
    /// the primitives work in per-builder scratch buffers and shared, immutable sine tables, so a rebuild through a
    /// pooled builder allocates nothing once the buffers have grown to size. Build copies the data into the Unity
    /// mesh (Mesh.Set* copy), so a builder can be reset and refilled right after.
    /// </summary>
    public sealed class MeshBuilder
    {
        readonly List<Vector3> v, n;
        readonly List<Vector4> tg;
        readonly List<Vector2> uv;
        readonly List<int>[] tris;

        // scratch, reused by the primitives (never alive across two public calls)
        readonly List<Vector3> sweepC = new List<Vector3>(64), sweepA = new List<Vector3>(64), sweepB = new List<Vector3>(64);
        readonly List<float> sweepR = new List<float>(64), sweepSlope = new List<float>(64), tubeS = new List<float>(64);
        readonly float[] turnAx = new float[6], turnRa = new float[6], turnNa = new float[6], turnNr = new float[6];
        readonly float[] zHi = new float[10], zLo = new float[10], zDelta = new float[10], zK = new float[10];
        Vector3[] tubeDense = new Vector3[64];
        float[] tubeCum = new float[64], tubeKappa = new float[64];

        public MeshBuilder(int materials) : this(materials, 1024) { }

        /// <param name="vertexCapacity">Expected vertex count, to preallocate the vertex buffers (they still grow). The
        /// triangle lists start small and grow (doubling) to what each material needs, so a submesh that stays small
        /// never holds a share of the whole mesh.</param>
        public MeshBuilder(int materials, int vertexCapacity)
        {
            int cap = Math.Max(vertexCapacity, 16);
            v = new List<Vector3>(cap); n = new List<Vector3>(cap); tg = new List<Vector4>(cap); uv = new List<Vector2>(cap);
            tris = new List<int>[Math.Max(materials, 1)];
            for (int i = 0; i < tris.Length; i++) tris[i] = new List<int>(Math.Min(cap * 6 / tris.Length, 4096) + 16);
        }

        public int VertexCount => v.Count;

        /// <summary>Number of submeshes (materials) this builder writes.</summary>
        public int MaterialCount => tris.Length;

        /// <summary>Empty every buffer but keep its capacity, so the next fill reuses the memory.</summary>
        public void Reset()
        {
            v.Clear(); n.Clear(); tg.Clear(); uv.Clear();
            for (int i = 0; i < tris.Length; i++) tris[i].Clear();
        }

        static readonly Dictionary<long, MeshBuilder> pool = new Dictionary<long, MeshBuilder>();

        /// <summary>
        /// A reset builder that is kept and reused across calls (Unity main thread only), so rebuilding a mesh doesn't
        /// reallocate its vertex and index buffers every time. One builder per (slot, materials): it stays valid until
        /// the next Pooled call with the same pair, so callers that fill two meshes at once use two slots (KitBuilder:
        /// 0 = LOD0, 1 = LOD1, 4 / 5 = their bell kits; EngineGeometry.Lathe: 2 and 3). `vertexCapacity` (optional)
        /// pre-sizes the buffers; keep it modest, since a pooled builder holds its largest size until ClearPool.
        /// </summary>
        public static MeshBuilder Pooled(int materials, int slot, int vertexCapacity = 0)
        {
            materials = Math.Max(materials, 1);
            long key = ((long)slot << 32) | (uint)materials;
            if (pool.TryGetValue(key, out MeshBuilder mb))
            {
                mb.Reset();
                if (vertexCapacity > 0) mb.Reserve(vertexCapacity);
                return mb;
            }
            mb = new MeshBuilder(materials, Math.Max(vertexCapacity, 1024));
            pool[key] = mb;
            return mb;
        }

        /// <summary>Drop every pooled builder (their memory goes to the GC), e.g. on a scene change.</summary>
        public static void ClearPool() => pool.Clear();

        List<int> Tris(int mat) => tris[mat < 0 ? 0 : Math.Min(mat, tris.Length - 1)];

        /// <summary>Move the vertices from index `from` on by `d` (normals and tangents are unchanged), e.g. into the
        /// frame of the transform the mesh hangs under.</summary>
        public void Translate(Vector3 d, int from = 0)
        {
            for (int i = Math.Max(from, 0); i < v.Count; i++) v[i] += d;
        }

        void Reserve(int moreVertices)
        {
            int need = v.Count + moreVertices;
            if (v.Capacity >= need) return;
            int cap = Math.Max(need, v.Capacity * 2);
            v.Capacity = cap; n.Capacity = cap; tg.Capacity = cap; uv.Capacity = cap;
        }

        static T[] Grow<T>(ref T[] a, int size)
        {
            if (a.Length < size) a = new T[Math.Max(size, a.Length * 2)];
            return a;
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

        sealed class RingTable
        {
            public readonly float[] Cs, Sn;
            public RingTable(int sides)
            {
                Cs = new float[sides + 1]; Sn = new float[sides + 1];
                for (int j = 0; j < sides; j++)
                {
                    float phi = 2 * Mathf.PI * j / sides;
                    Cs[j] = Mathf.Cos(phi); Sn[j] = Mathf.Sin(phi);
                }
                Cs[sides] = 1; Sn[sides] = 0;   // the seam column repeats column 0 exactly
            }
        }

        // shared and never written after creation (a race could only build an equal table twice)
        static readonly RingTable[] ringTables = new RingTable[1025];

        /// <summary>cos/sin of the ring angles (sides + 1 entries, the last repeating the first). The arrays are shared:
        /// read only.</summary>
        static void Ring(int sides, out float[] cs, out float[] sn)
        {
            RingTable r = sides < ringTables.Length ? ringTables[sides] : null;
            if (r == null)
            {
                r = new RingTable(sides);
                if (sides < ringTables.Length) ringTables[sides] = r;
            }
            cs = r.Cs; sn = r.Sn;
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
        /// tilts the normals of a rippled surface (bellows). V is the centre-line length over the first ring's
        /// circumference (square texels on a tube; Sphere sets its own V).</summary>
        void Sweep(List<Vector3> centres, List<float> radii, List<Vector3> ax, List<Vector3> bx, int sides, int mat, bool capStart, bool capEnd, List<float> slopes = null)
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
            Reserve(t, sides * 3);
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
            float[] ax = turnAx, ra = turnRa, na = turnNa, nr = turnNr;
            ax[0] = 0; ax[1] = 0; ax[2] = b; ax[3] = h - b; ax[4] = h; ax[5] = h;
            ra[0] = rIn; ra[1] = R - b; ra[2] = R; ra[3] = R; ra[4] = R - b; ra[5] = rIn;
            na[0] = -1; na[1] = -1; na[2] = 0; na[3] = 0; na[4] = 1; na[5] = 1;
            nr[0] = 0; nr[1] = 0; nr[2] = 1; nr[3] = 1; nr[4] = 0; nr[5] = 0;
            Turned(o, dir, ax, ra, na, nr, 6, sides, mat);
        }

        /// <summary>Small hex-ish bolt heads standing on a face: `count` heads on a circle of radius rc around `o`,
        /// heads of radius rb and height hb along `dir`.</summary>
        void BoltCircle(Vector3 o, Vector3 dir, float rc, float rb, float hb, int count, int mat)
        {
            Basis(dir, out Vector3 a, out Vector3 b);
            dir.Normalize();
            // side, rounded top edge, centre: 3 rings of 7 vertices per head
            float[] ax = turnAx, ra = turnRa, na = turnNa, nr = turnNr;
            ax[0] = -0.2f * hb; ax[1] = hb; ax[2] = hb;
            ra[0] = rb; ra[1] = rb * 0.85f; ra[2] = 0;
            na[0] = 0; na[1] = 0.8f; na[2] = 1;
            nr[0] = 1; nr[1] = 0.6f; nr[2] = 0;
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
                ClearSweep();
                sweepC.Add(p0); sweepC.Add(p1); sweepR.Add(r0); sweepR.Add(r1);
                sweepA.Add(a); sweepA.Add(a); sweepB.Add(b); sweepB.Add(b);
                Sweep(sweepC, sweepR, sweepA, sweepB, sides, mat, false, false);
                return;
            }
            float bev = Mathf.Min(0.07f * Mathf.Max(r0, r1), 0.2f * L);
            float k = (r1 - r0) / L;   // dr/da along the side
            float sl = 1 / Mathf.Sqrt(1 + k * k);
            float sideNa = -k * sl, sideNr = sl;   // side normal (axial, radial)
            float[] ax = turnAx, ra = turnRa, na = turnNa, nr = turnNr;
            ax[0] = 0; ax[1] = 0; ax[2] = bev; ax[3] = L - bev; ax[4] = L; ax[5] = L;
            ra[0] = 0; ra[1] = Mathf.Max(r0 - bev, 0); ra[2] = r0 + k * bev; ra[3] = r1 - k * bev; ra[4] = Mathf.Max(r1 - bev, 0); ra[5] = 0;
            na[0] = -1; na[1] = -1; na[2] = sideNa; na[3] = sideNa; na[4] = 1; na[5] = 1;
            nr[0] = 0; nr[1] = 0; nr[2] = sideNr; nr[3] = sideNr; nr[4] = 0; nr[5] = 0;
            Turned(p0, d, ax, ra, na, nr, 6, sides, mat);
        }

        void ClearSweep()
        {
            sweepC.Clear(); sweepR.Clear(); sweepA.Clear(); sweepB.Clear(); sweepSlope.Clear();
        }

        public void Cylinder(Vector3 p0, Vector3 p1, float r, int mat, int sides = 14) => Frustum(p0, p1, r, r, mat, sides);

        /// <summary>UV sphere around Y. U = 0..1 around; V = 0.5 * (polar angle / pi), i.e. surface arc length over the
        /// equator's circumference, 0 at the top pole, so the texels are square at the equator.</summary>
        public void Sphere(Vector3 c, float r, int mat, int sides = 14)
        {
            int rings = Math.Max(8, sides / 2);
            ClearSweep();
            for (int i = 0; i <= rings; i++)
            {
                float th = Mathf.PI * i / rings;
                sweepC.Add(c + Vector3.up * (r * Mathf.Cos(th))); sweepR.Add(Mathf.Max(r * Mathf.Sin(th), 1e-4f));
                sweepA.Add(Vector3.right); sweepB.Add(Vector3.forward);
            }
            int start = v.Count, ring = sides + 1;
            Sweep(sweepC, sweepR, sweepA, sweepB, sides, mat, false, false);
            // normals of a sphere are radial, not the ring direction; and Sweep's V (centre spacing over the pole
            // ring's tiny circumference) would repeat the texture hundreds of times pole to pole. V still grows
            // with the ring index, so Sweep's tangents (w) stay valid.
            for (int i = 0; i <= rings; i++)
            {
                float vv = 0.5f * i / rings;
                for (int j = 0; j <= sides; j++)
                {
                    int k = start + i * ring + j;
                    n[k] = (v[k] - c).normalized;
                    uv[k] = new Vector2((float)j / sides, vv);
                }
            }
        }

        public void Torus(Vector3 c, Vector3 axis, float R, float r, int mat, int segs = 20, int sides = 8)
        {
            Basis(axis, out Vector3 a, out Vector3 b);
            Vector3 up = axis.normalized;
            ClearSweep();
            for (int i = 0; i <= segs; i++)
            {
                float th = 2 * Mathf.PI * i / segs;
                Vector3 radial = i == segs ? a : a * Mathf.Cos(th) + b * Mathf.Sin(th);
                sweepC.Add(c + radial * R); sweepR.Add(r); sweepA.Add(radial); sweepB.Add(up);
            }
            Sweep(sweepC, sweepR, sweepA, sweepB, sides, mat, false, false);
        }

        // ------------------------------------------------------------------ tubes

        /// <summary>Samples per span of the tube centre line (Tube and anything that follows its path, e.g.
        /// KitBuilder's clamps, sample <see cref="SplinePoint"/> at k / SplineSamples, k = 0 .. SplineSamples - 1).</summary>
        public const int SplineSamples = 16;

        /// <summary>
        /// Point at parameter t (0..1) on span i (pts[i] to pts[i + 1]) of the centripetal Catmull-Rom spline through
        /// `pts` (knot steps sqrt(chord), Barry-Goldman, as Hermite tangents). Unlike the uniform spline it never
        /// overshoots a short span next to a long one, never cusps or loops inside a span, and collinear points give an
        /// exact straight run. The end spans reflect their neighbour (a repeated point counts as missing), so a tube
        /// leaves its first point along the first chord.
        /// </summary>
        public static Vector3 SplinePoint(IList<Vector3> pts, int i, float t)
        {
            int n = pts.Count;
            Vector3 p1 = pts[i], p2 = pts[i + 1];
            float c1 = (p2 - p1).magnitude;
            if (c1 < 1e-7f) return p1;
            Vector3 p0 = i > 0 ? pts[i - 1] : p1, p3 = i + 2 < n ? pts[i + 2] : p2;
            float c0 = (p1 - p0).magnitude, c2 = (p3 - p2).magnitude;
            if (c0 < 1e-7f) { p0 = 2 * p1 - p2; c0 = c1; }
            if (c2 < 1e-7f) { p3 = 2 * p2 - p1; c2 = c1; }
            float d0 = Mathf.Sqrt(c0), d1 = Mathf.Sqrt(c1), d2 = Mathf.Sqrt(c2);
            // tangents at p1 and p2 per knot unit, scaled to the span's parameter (d1)
            Vector3 m1 = ((p1 - p0) / d0 - (p2 - p0) / (d0 + d1) + (p2 - p1) / d1) * d1;
            Vector3 m2 = ((p2 - p1) / d1 - (p3 - p1) / (d1 + d2) + (p3 - p2) / d2) * d1;
            float t2 = t * t, t3 = t2 * t;
            return (2 * t3 - 3 * t2 + 1) * p1 + (t3 - 2 * t2 + t) * m1 + (3 * t2 - 2 * t3) * p2 + (t3 - t2) * m2;
        }

        /// <summary>Pipe along a smoothed path (centripetal Catmull-Rom through the points, <see cref="SplinePoint"/>,
        /// resampled by arc length with extra rings in tight bends, parallel-transported frames). Optional bolted,
        /// bevelled flanges at both ends and a convoluted bellows section (fractions of the length) with collars at its
        /// ends.</summary>
        public void Tube(IList<Vector3> pts, float r, int mat, int sides = 10, int flangeMat = -1, float bellowsFrom = -1, float bellowsTo = -1)
        {
            if (pts == null || pts.Count < 2 || sides < 3) return;
            const int perSpan = SplineSamples;
            int nd = (pts.Count - 1) * perSpan + 1;
            Vector3[] dense = Grow(ref tubeDense, nd);
            for (int i = 0; i < pts.Count - 1; i++)
                for (int k = 0; k < perSpan; k++)
                    dense[i * perSpan + k] = SplinePoint(pts, i, k / (float)perSpan);
            dense[nd - 1] = pts[pts.Count - 1];
            float[] cum = Grow(ref tubeCum, nd);
            cum[0] = 0;
            for (int i = 1; i < nd; i++) cum[i] = cum[i - 1] + (dense[i] - dense[i - 1]).magnitude;
            float len = cum[nd - 1];
            if (len < 1e-4f) return;
            // curvature (turn angle per length) at each dense point, for the adaptive ring spacing
            float[] kappa = Grow(ref tubeKappa, nd);
            Array.Clear(kappa, 0, nd);
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
            List<float> ss = tubeS;
            ss.Clear();
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
            ClearSweep();
            List<Vector3> path = sweepC;
            int seg = 0;
            for (int i = 0; i < count; i++)
            {
                float s = ss[i];
                while (seg < nd - 2 && cum[seg + 1] < s) seg++;
                float span = cum[seg + 1] - cum[seg];
                float f = span < 1e-9f ? 0 : (s - cum[seg]) / span;
                path.Add(Vector3.Lerp(dense[seg], dense[seg + 1], Mathf.Clamp01(f)));
            }

            List<Vector3> ax = sweepA, bx = sweepB;
            List<float> rs = sweepR, slopes = sweepSlope;
            Basis(path[1] - path[0], out Vector3 a, out Vector3 b);
            for (int i = 0; i < count; i++)
            {
                Vector3 tan = (path[Math.Min(i + 1, count - 1)] - path[Math.Max(i - 1, 0)]).normalized;
                Vector3 ap = a - Vector3.Dot(a, tan) * tan;   // parallel transport
                if (ap.sqrMagnitude > 1e-12f) a = ap.normalized;
                b = Vector3.Cross(tan, a);
                ax.Add(a); bx.Add(b);
                if (bellows && ss[i] > b0 && ss[i] < b1)
                {
                    // convolutions r (1.16 + 0.08 cos) under a smooth envelope rising from the pipe radius at each end
                    float ph = 2 * Mathf.PI * (ss[i] - b0) / pitch, k = 2 * Mathf.PI / pitch;
                    float ta = Mathf.Clamp01((ss[i] - b0) / neck), tb = Mathf.Clamp01((b1 - ss[i]) / neck);
                    float ea = ta * ta * (3 - 2 * ta), eb = tb * tb * (3 - 2 * tb);
                    float dea = ta < 1 ? 6 * ta * (1 - ta) / neck : 0, deb = tb < 1 ? -6 * tb * (1 - tb) / neck : 0;
                    float env = ea * eb, denv = dea * eb + ea * deb, wave = 0.16f + 0.08f * Mathf.Cos(ph);
                    rs.Add(r * (1 + env * wave));
                    slopes.Add(r * (denv * wave - env * 0.08f * k * Mathf.Sin(ph)));
                }
                else { rs.Add(r); slopes.Add(0); }
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
            float[] zHi = this.zHi, zLo = this.zLo, zDelta = this.zDelta, zK = this.zK;
            if (length > 0 && m.TryAnchor("end", out Vector3 endP, out _))
            {
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
            Emit(m, origin, X, Y, Z, scale, mat, zc);
        }

        /// <summary>Place an authored piece with given stretches: zone N (zoneN_hi above zoneN_lo) grows by
        /// `zoneDelta[N]` piece units (negative shrinks it, to at least 30 % of its length), and everything below it
        /// moves down with it. Zones without both anchors or a delta are left alone. KitBuilder's gimbal actuators use
        /// it to give the barrel and the rod their own lengths.</summary>
        public void AddMesh(KitMesh m, Vector3 origin, Vector3 X, Vector3 Y, Vector3 Z, float scale, int mat, float[] zoneDelta)
        {
            if (m == null || m.Positions == null || m.Positions.Length == 0) return;
            int zc = 0;
            for (int z = 0; z < 10 && zoneDelta != null && z < zoneDelta.Length; z++)
                if (m.TryAnchor(ZoneHi[z], out Vector3 hi, out _) && m.TryAnchor(ZoneLo[z], out Vector3 lo, out _) && hi.y > lo.y)
                {
                    float zl = hi.y - lo.y;
                    zHi[zc] = hi.y; zLo[zc] = lo.y;
                    zDelta[zc] = Mathf.Max(zoneDelta[z], -0.7f * zl);
                    zK[zc] = 1 + zDelta[zc] / zl;
                    zc++;
                }
            Emit(m, origin, X, Y, Z, scale, mat, zc);
        }

        /// <summary>AddMesh's vertex and index copy, with the first `zc` stretch zones in zHi / zLo / zDelta / zK.</summary>
        void Emit(KitMesh m, Vector3 origin, Vector3 X, Vector3 Y, Vector3 Z, float scale, int mat, int zc)
        {
            float[] zHi = this.zHi, zLo = this.zLo, zDelta = this.zDelta, zK = this.zK;
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
