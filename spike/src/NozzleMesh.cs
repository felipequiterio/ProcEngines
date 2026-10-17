using System;
using System.Collections.Generic;
using UnityEngine;

namespace ProcEngineSpike
{
    /// <summary>A profile point in the part frame: radius, height, analytic 2D outward normal, arc length.</summary>
    public struct ProfilePoint
    {
        public float r, y, nr, ny, s;
        public ProfilePoint(float r, float y, float nr, float ny) { this.r = r; this.y = y; this.nr = nr; this.ny = ny; s = 0; }
    }

    /// <summary>
    /// Builds the revolved chamber + bell. Normals come from the analytic profile derivative
    /// (never RecalculateNormals); creases are two coincident points with different normals.
    /// </summary>
    public static class NozzleMesh
    {
        // Static part: mount, injector face, chamber, 45 deg cone, upstream throat arc. Ends at the throat.
        public static List<ProfilePoint> StaticProfile(SpikeStats s)
        {
            var p = new List<ProfilePoint>();
            float yInj = -s.mountH;
            // mount top cap (facing up), crease, mount side, crease, injector annulus, crease, chamber wall
            Seg(p, 0, 0, s.mountR, 0, 4);
            Seg(p, s.mountR, 0, s.mountR, yInj, 3);
            Seg(p, s.mountR, yInj, s.rc, yInj, 2);
            Seg(p, s.rc, yInj, s.rc, s.YOf(s.xCone), 4);
            // cone: crease at the chamber joint (no fillet in the spike)
            float xConv = -1.0607f * s.rt, rConv = 2.5f * s.rt - 1.0607f * s.rt;
            Seg(p, s.rc, s.YOf(s.xCone), rConv, s.YOf(xConv), 3);
            // upstream arc, radius 1.5 Rt, -135 -> -90 deg; tangent-continuous with the cone, so drop the duplicate
            p.RemoveAt(p.Count - 1);
            Arc(p, s, 1.5f * s.rt, 2.5f * s.rt, -135f, -90f, 10);
            return p;
        }

        // Gimballed part: downstream throat arc, bell, exit lip, inner wall, throat plug. Coordinates in the part frame.
        public static List<ProfilePoint> BellProfile(SpikeStats s, out float wall, out int innerStart)
        {
            var p = new List<ProfilePoint>();
            Arc(p, s, 0.382f * s.rt, 1.382f * s.rt, -90f, s.thetaN - 90f, 8);
            // Bezier N -> Q -> E, denser near the throat
            int n = 28;
            for (int i = 1; i <= n; i++)
            {
                float t = (float)i / n; t = t * t * 0.35f + t * 0.65f;
                float u = 1 - t;
                float x = u * u * s.xN + 2 * u * t * s.qx + t * t * s.ln;
                float r = u * u * s.rN + 2 * u * t * s.qr + t * t * s.re;
                float dx = 2 * u * (s.qx - s.xN) + 2 * t * (s.ln - s.qx);
                float dr = 2 * u * (s.qr - s.rN) + 2 * t * (s.re - s.qr);
                AddFromTangent(p, r, s.YOf(x), dr, -dx);
            }
            wall = Math.Max(0.015f * s.re, 0.004f);
            // exit lip (facing down), then the inner wall back to the throat with inward normals
            int outerCount = p.Count;   // arc + bezier points
            Seg(p, s.re, s.YExit, s.re - wall, s.YExit, 2);
            innerStart = p.Count - 1;
            var outerBell = p.GetRange(0, outerCount);
            for (int i = outerBell.Count - 1; i >= 0; i--)
            {
                ProfilePoint o = outerBell[i];
                p.Add(new ProfilePoint(Math.Max(o.r - wall, 0.001f), o.y, -o.nr, -o.ny));
            }
            // dark plug across the throat, facing down the nozzle
            ProfilePoint last = p[p.Count - 1];
            Seg(p, last.r, last.y, 0f, last.y, 2);
            return p;
        }

        static void Seg(List<ProfilePoint> p, float r0, float y0, float r1, float y1, int steps)
        {
            float dr = r1 - r0, dy = y1 - y0;
            for (int i = 0; i <= steps; i++)
            {
                float t = (float)i / steps;
                AddFromTangent(p, r0 + dr * t, y0 + dy * t, dr, dy);
            }
        }

        // Arc in the nozzle frame: x = R cos(th), r = rc + R sin(th)
        static void Arc(List<ProfilePoint> p, SpikeStats s, float R, float rCenter, float th0, float th1, int steps)
        {
            for (int i = 0; i <= steps; i++)
            {
                float th = (th0 + (th1 - th0) * i / steps) * Mathf.Deg2Rad;
                float x = R * Mathf.Cos(th), r = rCenter + R * Mathf.Sin(th);
                float dx = -R * Mathf.Sin(th), dr = R * Mathf.Cos(th);
                // y = -(x - xInj) - mountH, so dy = -dx
                AddFromTangent(p, r, s.YOf(x), dr, -dx);
            }
        }

        // Outward normal of a profile running in direction (dr, dy) is (-dy, dr).
        static void AddFromTangent(List<ProfilePoint> p, float r, float y, float dr, float dy)
        {
            float len = Mathf.Sqrt(dr * dr + dy * dy);
            if (len < 1e-9f) { dr = 0; dy = -1; len = 1; }
            p.Add(new ProfilePoint(r, y, -dy / len, dr / len));
        }

        /// <summary>Lathe the profile around Y. Reuses <paramref name="mesh"/>. yOffset shifts into a child frame.</summary>
        public static void Lathe(Mesh mesh, List<ProfilePoint> prof, int segments, float yOffset, int innerStart = -1)
        {
            // arc length for V
            float acc = 0;
            for (int i = 0; i < prof.Count; i++)
            {
                if (i > 0)
                {
                    float dr = prof[i].r - prof[i - 1].r, dy = prof[i].y - prof[i - 1].y;
                    acc += Mathf.Sqrt(dr * dr + dy * dy);
                }
                ProfilePoint q = prof[i]; q.s = acc; prof[i] = q;
            }
            float total = Math.Max(acc, 1e-6f);

            int ring = segments + 1;   // duplicated seam vertex for the UV wrap
            var verts = new Vector3[prof.Count * ring];
            var norms = new Vector3[verts.Length];
            var tans = new Vector4[verts.Length];
            var uvs = new Vector2[verts.Length];
            for (int i = 0; i < prof.Count; i++)
            {
                ProfilePoint q = prof[i];
                for (int j = 0; j < ring; j++)
                {
                    float phi = 2 * Mathf.PI * j / segments;
                    float c = Mathf.Cos(phi), sn = Mathf.Sin(phi);
                    int k = i * ring + j;
                    verts[k] = new Vector3(q.r * c, q.y - yOffset, q.r * sn);
                    norms[k] = new Vector3(q.nr * c, q.ny, q.nr * sn);
                    tans[k] = new Vector4(-sn, 0, c, -1);
                    uvs[k] = new Vector2((float)j / segments, q.s / total);
                }
            }

            var outer = new List<int>();
            var inner = new List<int>();
            for (int i = 0; i < prof.Count - 1; i++)
            {
                float dr = prof[i + 1].r - prof[i].r, dy = prof[i + 1].y - prof[i].y;
                if (dr * dr + dy * dy < 1e-12f) continue;   // crease duplicate: no zero-area strip
                List<int> tris = innerStart >= 0 && i >= innerStart ? inner : outer;
                for (int j = 0; j < segments; j++)
                {
                    int a = i * ring + j, b = a + 1, c = a + ring, d = c + 1;
                    // Winding chosen so the face normal agrees with the analytic normal (checked once below).
                    tris.Add(a); tris.Add(b); tris.Add(c);
                    tris.Add(b); tris.Add(d); tris.Add(c);
                }
            }
            FixWinding(verts, norms, outer);
            FixWinding(verts, norms, inner);

            mesh.Clear();
            mesh.vertices = verts;
            mesh.normals = norms;
            mesh.tangents = tans;
            mesh.uv = uvs;
            mesh.subMeshCount = inner.Count > 0 ? 2 : 1;
            mesh.SetTriangles(outer, 0);
            if (inner.Count > 0) mesh.SetTriangles(inner, 1);
            mesh.RecalculateBounds();
        }

        static void FixWinding(Vector3[] v, Vector3[] n, List<int> tris)
        {
            // Majority vote over triangles: flip the whole list if most faces disagree with the analytic normals.
            int agree = 0, disagree = 0;
            for (int t = 0; t < tris.Count; t += 3)
            {
                Vector3 fn = Vector3.Cross(v[tris[t + 1]] - v[tris[t]], v[tris[t + 2]] - v[tris[t]]);
                if (fn.sqrMagnitude < 1e-14f) continue;
                Vector3 an = n[tris[t]] + n[tris[t + 1]] + n[tris[t + 2]];
                if (Vector3.Dot(fn, an) >= 0) agree++; else disagree++;
            }
            if (disagree > agree)
                for (int t = 0; t < tris.Count; t += 3) { int x = tris[t + 1]; tris[t + 1] = tris[t + 2]; tris[t + 2] = x; }
        }

        /// <summary>Convex collider hull: a 12-segment frustum between two stations, capped.</summary>
        public static void Frustum(Mesh mesh, float r0, float y0, float r1, float y1)
        {
            const int seg = 12;
            var v = new Vector3[seg * 2 + 2];
            for (int j = 0; j < seg; j++)
            {
                float phi = 2 * Mathf.PI * j / seg;
                v[j] = new Vector3(r0 * Mathf.Cos(phi), y0, r0 * Mathf.Sin(phi));
                v[seg + j] = new Vector3(r1 * Mathf.Cos(phi), y1, r1 * Mathf.Sin(phi));
            }
            v[2 * seg] = new Vector3(0, y0, 0);
            v[2 * seg + 1] = new Vector3(0, y1, 0);
            var t = new List<int>();
            for (int j = 0; j < seg; j++)
            {
                int a = j, b = (j + 1) % seg, c = seg + j, d = seg + (j + 1) % seg;
                t.Add(a); t.Add(c); t.Add(b);
                t.Add(b); t.Add(c); t.Add(d);
                t.Add(2 * seg); t.Add(a); t.Add(b);
                t.Add(2 * seg + 1); t.Add(d); t.Add(c);
            }
            mesh.Clear();
            mesh.vertices = v;
            mesh.triangles = t.ToArray();
            mesh.RecalculateBounds();
        }

        /// <summary>Max angle (deg) between the analytic normals and Unity's RecalculateNormals, ignoring crease vertices.</summary>
        public static float NormalDeviation(Mesh m, out float median)
        {
            Vector3[] a = m.normals;
            Mesh copy = UnityEngine.Object.Instantiate(m);
            copy.RecalculateNormals();
            Vector3[] b = copy.normals;
            Vector3[] v = m.vertices;
            UnityEngine.Object.Destroy(copy);
            // vertices sharing a position with another vertex are creases or the seam: skip them
            var seen = new Dictionary<Vector3, int>();
            foreach (Vector3 p in v) { seen.TryGetValue(p, out int c); seen[p] = c + 1; }
            var devs = new List<float>();
            for (int i = 0; i < a.Length; i++)
            {
                if (seen[v[i]] > 1) continue;
                devs.Add(Vector3.Angle(a[i], b[i]));
            }
            devs.Sort();
            median = devs.Count > 0 ? devs[devs.Count / 2] : 0;
            return devs.Count > 0 ? devs[devs.Count - 1] : 0;
        }
    }
}
