using System;
using System.Collections.Generic;
using ProcEngines.Model;
using UnityEngine;

namespace ProcEngines
{
    /// <summary>One smooth surface of revolution: an (r, y) polyline with its material and UV tiling.</summary>
    public sealed class ProfileSegment
    {
        public List<Vector2> Points;
        public int Material;
        public float UTiles = 1;     // texture repeats around the circumference: whole number of cooling tubes
        public float VLength = 1;    // metres of arc length per texture repeat along the profile

        public ProfileSegment(List<Vector2> pts, int material, float uTiles = 1, float vLength = 1)
        {
            Points = pts; Material = material; UTiles = uTiles; VLength = Mathf.Max(vLength, 1e-3f);
        }
    }

    /// <summary>
    /// Builds the engine's surfaces from DESIGN + STATS. Part frame: +Y up, origin at the mount top (= top node,
    /// which therefore never moves). Deterministic: every LMP client builds the same mesh from the same node.
    /// </summary>
    public sealed class EngineGeometry
    {
        // static mesh materials
        public const int MatSteel = 0, MatCopper = 1;
        // bell mesh materials
        public const int MatRegen = 0, MatRadiative = 1, MatSoot = 2;

        public float YInjector, YThroat, YExit, YChamberBottom, MountRadius, MountHeight, DomeHeight, MaxRadius, ChamberRadius, ThroatRadius;
        public readonly List<ProfileSegment> Static = new List<ProfileSegment>();
        public readonly List<ProfileSegment> Bell = new List<ProfileSegment>();
        public readonly List<Vector2> BellOuter = new List<Vector2>();   // throat to exit, outer wall (r, y), for kit clearance
        public float RegenEndY, RegenEndRadius;
        public int TubeCount;
        public Vector2 ColliderChamberTop, ColliderChamberBottom, ColliderMid, ColliderExit, ColliderThroat;   // (r, y)

        /// <summary>Outer bell radius at height y (0 above the throat).</summary>
        public float BellRadiusAt(float y)
        {
            for (int i = 1; i < BellOuter.Count; i++)
                if (BellOuter[i].y <= y)
                {
                    Vector2 a = BellOuter[i - 1], b = BellOuter[i];
                    float t = Mathf.Approximately(a.y, b.y) ? 1 : (y - a.y) / (b.y - a.y);
                    return Mathf.Lerp(a.x, b.x, Mathf.Clamp01(t));
                }
            return BellOuter.Count > 0 ? BellOuter[BellOuter.Count - 1].x : 0;
        }

        public static EngineGeometry Build(EngineDesign d, EngineStats s, TechLevel tech)
        {
            var g = new EngineGeometry();
            float rt = (float)s.ThroatRadius, rc = (float)s.ChamberRadius, lc = (float)s.ChamberLength;
            float mountR = Mathf.Max(0.7f * rc, 1.2f * rt);
            float mountH = 0.12f * rc + 0.02f;
            float domeH = 0.35f * rc;
            g.MountRadius = mountR; g.MountHeight = mountH; g.DomeHeight = domeH; g.ChamberRadius = rc; g.ThroatRadius = rt;
            float yInj = -(mountH + domeH);
            g.YInjector = yInj;
            float steelV = Mathf.Max(0.25f, 2 * Mathf.PI * rc / 4);

            // mount plate (top face, side, underside) and injector dome down to the chamber
            g.Static.Add(new ProfileSegment(Line(0, 0, mountR, 0, 4), MatSteel, 4, steelV));
            g.Static.Add(new ProfileSegment(Line(mountR, 0, mountR, -mountH, 2), MatSteel, 8, steelV));
            float r0 = Mathf.Max(mountR, 0.5f * rc);
            g.Static.Add(new ProfileSegment(Line(mountR, -mountH, r0, -mountH, 2), MatSteel, 8, steelV));
            var dome = new List<Vector2>();
            for (int i = 0; i <= 10; i++)
            {
                float t = i / 10f * Mathf.PI / 2;
                dome.Add(new Vector2(r0 + (rc - r0) * Mathf.Sin(t), -mountH - domeH * (1 - Mathf.Cos(t))));
            }
            g.Static.Add(new ProfileSegment(dome, MatSteel, 8, steelV));

            // whole number of cooling tubes, constant along the length (they taper with the wall), DESIGN.md 8.4
            g.TubeCount = Mathf.Clamp(Mathf.RoundToInt(2 * Mathf.PI * rc / 0.035f) * 2, 24, 240);
            float tubeV = 1000f;   // tubes run along the flow: no repeat along V

            // chamber cylinder: steel jacket over the regen liner
            float yConvStart = yInj - lc;
            g.YChamberBottom = yConvStart;
            g.Static.Add(new ProfileSegment(Line(rc, yInj, rc, yConvStart, 4), MatSteel, 8, steelV));
            // convergent: 30-degree cone, then a 1.5 Rt arc into the throat (copper regen section)
            float rArc = 2.5f * rt - 1.5f * rt * Mathf.Sin(60 * Mathf.Deg2Rad);
            float coneLen = Mathf.Max(rc - rArc, 0) / Mathf.Tan(30 * Mathf.Deg2Rad);
            float yThroat = yConvStart - coneLen - 0.75f * rt;
            g.YThroat = yThroat;
            var conv = new List<Vector2> { new Vector2(rc, yConvStart) };
            for (int i = 0; i <= 12; i++)
            {
                float th = (-120f + 30f * i / 12f) * Mathf.Deg2Rad;
                conv.Add(new Vector2(2.5f * rt + 1.5f * rt * Mathf.Sin(th), yThroat - 1.5f * rt * Mathf.Cos(th)));
            }
            g.Static.Add(new ProfileSegment(conv, MatCopper, g.TubeCount, tubeV));

            // bell: Rao contour in Rt units (x from the throat), split at the regen limit
            var contour = new NozzleContour(d.Eps, d.LengthFraction, d.Conical);
            double regenEps = d.RadiativeExtension ? Math.Min(d.Eps, tech?.RegenEpsMax ?? 30) : d.Eps;
            var regen = new List<Vector2>();
            var rad = new List<Vector2>();
            foreach (WallPoint w in contour.Wall)
            {
                var p = new Vector2((float)w.R * rt, yThroat - (float)w.X * rt);
                g.BellOuter.Add(p);
                if (w.R * w.R <= regenEps + 1e-6) regen.Add(p);
                else { if (rad.Count == 0 && regen.Count > 0) rad.Add(regen[regen.Count - 1]); rad.Add(p); }
            }
            g.Bell.Add(new ProfileSegment(regen, MatRegen, g.TubeCount, tubeV));
            float radV = Mathf.Max(0.3f, (float)(2 * Math.PI * rt * Math.Sqrt(d.Eps)) / 6);
            if (rad.Count > 1) g.Bell.Add(new ProfileSegment(rad, MatRadiative, 6, radV));
            g.RegenEndY = regen[regen.Count - 1].y;
            g.RegenEndRadius = regen[regen.Count - 1].x;
            Vector2 exit = g.BellOuter[g.BellOuter.Count - 1];
            g.YExit = exit.y;
            float wall = Mathf.Max(0.012f * exit.x, 0.004f);
            // lip and inner wall (soot), back to the throat, then a plug across the throat
            g.Bell.Add(new ProfileSegment(Line(exit.x, exit.y, exit.x - wall, exit.y, 1), MatSoot, 8, 1));
            var inner = new List<Vector2>();
            for (int i = g.BellOuter.Count - 1; i >= 0; i--) inner.Add(new Vector2(Mathf.Max(g.BellOuter[i].x - wall, 0.001f), g.BellOuter[i].y));
            g.Bell.Add(new ProfileSegment(inner, MatSoot, 8, Mathf.Max(0.3f, exit.x)));
            g.Bell.Add(new ProfileSegment(Line(inner[inner.Count - 1].x, yThroat, 0, yThroat, 1), MatSoot, 1, 1));

            g.MaxRadius = Mathf.Max(exit.x, rc, mountR);
            int mid = g.BellOuter.Count / 2;
            g.ColliderChamberTop = new Vector2(Mathf.Max(rc, mountR), 0);
            g.ColliderChamberBottom = conv[conv.Count / 2];
            g.ColliderThroat = new Vector2(rt, yThroat);
            g.ColliderMid = g.BellOuter[mid];
            g.ColliderExit = exit;
            return g;
        }

        static List<Vector2> Line(float r0, float y0, float r1, float y1, int steps)
        {
            var l = new List<Vector2>();
            for (int i = 0; i <= steps; i++) { float t = (float)i / steps; l.Add(new Vector2(r0 + (r1 - r0) * t, y0 + (y1 - y0) * t)); }
            return l;
        }

        // ------------------------------------------------------------------ lathe

        /// <summary>Revolve profile segments around Y into one mesh, one submesh per material. Normals come from the
        /// profile tangent (central differences along each smooth segment); segment joints are creases. U repeats
        /// UTiles times around (seam vertex duplicated), V runs along the arc length.</summary>
        public static void Lathe(Mesh mesh, List<ProfileSegment> segments, int materialCount, int ringSegments, float yOffset)
        {
            var verts = new List<Vector3>();
            var norms = new List<Vector3>();
            var tans = new List<Vector4>();
            var uvs = new List<Vector2>();
            var tris = new List<int>[materialCount];
            for (int m = 0; m < materialCount; m++) tris[m] = new List<int>();
            int ring = ringSegments + 1;
            foreach (ProfileSegment segment in segments)
            {
                List<Vector2> seg = segment.Points;
                if (seg.Count < 2) continue;
                float acc = 0;
                int baseIndex = verts.Count;
                for (int i = 0; i < seg.Count; i++)
                {
                    if (i > 0) acc += (seg[i] - seg[i - 1]).magnitude;
                    Vector2 tan = seg[Math.Min(i + 1, seg.Count - 1)] - seg[Math.Max(i - 1, 0)];
                    if (tan.sqrMagnitude < 1e-12f) tan = new Vector2(0, -1);
                    tan.Normalize();
                    var n2 = new Vector2(-tan.y, tan.x);   // outward for a profile running outward/downward
                    for (int j = 0; j < ring; j++)
                    {
                        float phi = 2 * Mathf.PI * j / ringSegments, c = Mathf.Cos(phi), sn = Mathf.Sin(phi);
                        verts.Add(new Vector3(seg[i].x * c, seg[i].y - yOffset, seg[i].x * sn));
                        norms.Add(new Vector3(n2.x * c, n2.y, n2.x * sn));
                        tans.Add(new Vector4(-sn, 0, c, -1));
                        uvs.Add(new Vector2(segment.UTiles * j / ringSegments, acc / segment.VLength));
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
                FixWinding(verts, norms, t);
                tris[Math.Min(segment.Material, materialCount - 1)].AddRange(t);
            }
            mesh.Clear();
            mesh.indexFormat = verts.Count > 65000 ? UnityEngine.Rendering.IndexFormat.UInt32 : UnityEngine.Rendering.IndexFormat.UInt16;
            mesh.SetVertices(verts);
            mesh.SetNormals(norms);
            mesh.SetTangents(tans);
            mesh.SetUVs(0, uvs);
            mesh.subMeshCount = materialCount;
            for (int m = 0; m < materialCount; m++) mesh.SetTriangles(tris[m], m);
            mesh.RecalculateBounds();
        }

        public static void FixWinding(List<Vector3> v, List<Vector3> n, List<int> tris)
        {
            int agree = 0, disagree = 0;
            for (int t = 0; t < tris.Count; t += 3)
            {
                Vector3 fn = Vector3.Cross(v[tris[t + 1]] - v[tris[t]], v[tris[t + 2]] - v[tris[t]]);
                if (fn.sqrMagnitude < 1e-14f) continue;
                if (Vector3.Dot(fn, n[tris[t]] + n[tris[t + 1]] + n[tris[t + 2]]) >= 0) agree++; else disagree++;
            }
            if (disagree > agree)
                for (int t = 0; t < tris.Count; t += 3) { int x = tris[t + 1]; tris[t + 1] = tris[t + 2]; tris[t + 2] = x; }
        }

        /// <summary>Convex frustum hull (12 sides, capped) between two (r, y) stations; under Unity's 255-triangle limit.</summary>
        public static void Frustum(Mesh mesh, Vector2 a, Vector2 b)
        {
            const int seg = 12;
            var v = new Vector3[seg * 2 + 2];
            for (int j = 0; j < seg; j++)
            {
                float phi = 2 * Mathf.PI * j / seg;
                v[j] = new Vector3(a.x * Mathf.Cos(phi), a.y, a.x * Mathf.Sin(phi));
                v[seg + j] = new Vector3(b.x * Mathf.Cos(phi), b.y, b.x * Mathf.Sin(phi));
            }
            v[2 * seg] = new Vector3(0, a.y, 0);
            v[2 * seg + 1] = new Vector3(0, b.y, 0);
            var t = new List<int>();
            for (int j = 0; j < seg; j++)
            {
                int i0 = j, i1 = (j + 1) % seg, i2 = seg + j, i3 = seg + (j + 1) % seg;
                t.AddRange(new[] { i0, i2, i1, i1, i2, i3, 2 * seg, i0, i1, 2 * seg + 1, i3, i2 });
            }
            mesh.Clear();
            mesh.vertices = v;
            mesh.triangles = t.ToArray();
            mesh.RecalculateBounds();
        }
    }
}
