using System;
using System.Collections.Generic;
using ProcEngines.Model;
using UnityEngine;

namespace ProcEngines
{
    /// <summary>
    /// Builds the engine's meshes from DESIGN + STATS. Part frame: +Y up, origin at the mount top (= top node,
    /// which therefore never moves). Deterministic: every LMP client builds the same mesh from the same node.
    /// </summary>
    public sealed class EngineGeometry
    {
        public float YInjector, YThroat, YExit, MountRadius, MaxRadius;
        public readonly List<List<Vector2>> StaticSegments = new List<List<Vector2>>();   // (r, y) polylines; each one a smooth surface
        public readonly List<List<Vector2>> BellSegments = new List<List<Vector2>>();
        public readonly List<int> BellSegmentMaterial = new List<int>();   // 0 regen, 1 radiative extension, 2 inner/soot
        public float RegenEndY;
        public Vector2 ColliderChamberTop, ColliderChamberBottom, ColliderMid, ColliderExit, ColliderThroat;   // (r, y)

        public static EngineGeometry Build(EngineDesign d, EngineStats s, TechLevel tech)
        {
            var g = new EngineGeometry();
            float rt = (float)s.ThroatRadius, rc = (float)s.ChamberRadius, lc = (float)s.ChamberLength;
            float mountR = Mathf.Max(0.7f * rc, 1.2f * rt);
            float mountH = 0.12f * rc + 0.02f;
            float domeH = 0.35f * rc;
            g.MountRadius = mountR;
            float yInj = -(mountH + domeH);
            g.YInjector = yInj;

            // mount plate (top face, side) and injector dome down to the chamber
            g.StaticSegments.Add(Line(0, 0, mountR, 0, 4));
            g.StaticSegments.Add(Line(mountR, 0, mountR, -mountH, 2));
            g.StaticSegments.Add(Line(mountR, -mountH, Mathf.Max(mountR, 0.5f * rc), -mountH, 2));
            var dome = new List<Vector2>();
            float r0 = Mathf.Max(mountR, 0.5f * rc);
            for (int i = 0; i <= 10; i++)
            {
                float t = i / 10f * Mathf.PI / 2;
                dome.Add(new Vector2(r0 + (rc - r0) * Mathf.Sin(t), -mountH - domeH * (1 - Mathf.Cos(t))));
            }
            g.StaticSegments.Add(dome);
            // chamber cylinder
            float yConvStart = yInj - lc;
            g.StaticSegments.Add(Line(rc, yInj, rc, yConvStart, 4));
            // convergent: 30-degree cone, then a 1.5 Rt arc into the throat (nozzle frame x along the flow)
            float xArc = -0.75f * rt, rArc = 2.5f * rt - 1.5f * rt * Mathf.Sin(60 * Mathf.Deg2Rad);
            float coneLen = Mathf.Max(rc - rArc, 0) / Mathf.Tan(30 * Mathf.Deg2Rad);
            float yThroat = yConvStart - coneLen - 0.75f * rt;
            g.YThroat = yThroat;
            var conv = new List<Vector2> { new Vector2(rc, yConvStart) };
            for (int i = 0; i <= 12; i++)
            {
                float th = (-120f + 30f * i / 12f) * Mathf.Deg2Rad;
                conv.Add(new Vector2(2.5f * rt + 1.5f * rt * Mathf.Sin(th), yThroat - 1.5f * rt * Mathf.Cos(th)));
            }
            g.StaticSegments.Add(conv);

            // bell: Rao contour in Rt units (x from the throat), split at the regen limit
            var contour = new NozzleContour(d.Eps, d.LengthFraction, d.Conical);
            double regenEps = d.RadiativeExtension ? Math.Min(d.Eps, tech?.RegenEpsMax ?? 30) : d.Eps;
            var regen = new List<Vector2>();
            var rad = new List<Vector2>();
            foreach (WallPoint w in contour.Wall)
            {
                var p = new Vector2((float)w.R * rt, yThroat - (float)w.X * rt);
                if (w.R * w.R <= regenEps + 1e-6) regen.Add(p);
                else { if (rad.Count == 0 && regen.Count > 0) rad.Add(regen[regen.Count - 1]); rad.Add(p); }
            }
            g.BellSegments.Add(regen); g.BellSegmentMaterial.Add(0);
            if (rad.Count > 1) { g.BellSegments.Add(rad); g.BellSegmentMaterial.Add(1); }
            g.RegenEndY = regen[regen.Count - 1].y;
            Vector2 exit = (rad.Count > 1 ? rad : regen)[(rad.Count > 1 ? rad : regen).Count - 1];
            g.YExit = exit.y;
            float wall = Mathf.Max(0.012f * exit.x, 0.004f);
            // lip and inner wall (soot), back to the throat, then a plug across the throat
            g.BellSegments.Add(Line(exit.x, exit.y, exit.x - wall, exit.y, 1)); g.BellSegmentMaterial.Add(2);
            var inner = new List<Vector2>();
            var all = new List<Vector2>(regen);
            if (rad.Count > 1) all.AddRange(rad.GetRange(1, rad.Count - 1));
            for (int i = all.Count - 1; i >= 0; i--) inner.Add(new Vector2(Mathf.Max(all[i].x - wall, 0.001f), all[i].y));
            g.BellSegments.Add(inner); g.BellSegmentMaterial.Add(2);
            g.BellSegments.Add(Line(inner[inner.Count - 1].x, yThroat, 0, yThroat, 1)); g.BellSegmentMaterial.Add(2);

            g.MaxRadius = Mathf.Max(exit.x, rc, mountR);
            int mid = all.Count / 2;
            g.ColliderChamberTop = new Vector2(Mathf.Max(rc, mountR), 0);
            g.ColliderChamberBottom = conv[conv.Count / 2];
            g.ColliderThroat = new Vector2(rt, yThroat);
            g.ColliderMid = all[mid];
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

        /// <summary>Revolve polylines around Y into one mesh; one submesh per material index. Normals come from the
        /// profile tangent (central differences along each smooth segment); segment joints are creases.</summary>
        public static void Lathe(Mesh mesh, List<List<Vector2>> segments, List<int> materials, int materialCount, int ringSegments, float yOffset)
        {
            var verts = new List<Vector3>();
            var norms = new List<Vector3>();
            var tans = new List<Vector4>();
            var uvs = new List<Vector2>();
            var tris = new List<int>[materialCount];
            for (int m = 0; m < materialCount; m++) tris[m] = new List<int>();
            int ring = ringSegments + 1;
            for (int s = 0; s < segments.Count; s++)
            {
                List<Vector2> seg = segments[s];
                if (seg.Count < 2) continue;
                int mat = materials == null ? 0 : materials[s];
                float total = 0;
                for (int i = 1; i < seg.Count; i++) total += (seg[i] - seg[i - 1]).magnitude;
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
                        uvs.Add(new Vector2((float)j / ringSegments, total > 0 ? acc / total : 0));
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
                tris[mat].AddRange(t);
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

        static void FixWinding(List<Vector3> v, List<Vector3> n, List<int> tris)
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

        // ------------------------------------------------------------------ kit pieces (visual only)

        /// <summary>Primitive turbomachinery / pressurant pieces per cycle (DESIGN.md 8.5), combined into one mesh.</summary>
        public static void BuildKit(Mesh mesh, EngineDesign d, EngineStats s, EngineGeometry g)
        {
            var parts = new List<CombineInstance>();
            float rc = (float)s.ChamberRadius;
            double turboKg = s.MassBreakdown.TryGetValue("turbomachinery", out double tk) ? tk : 0;
            double pressKg = s.MassBreakdown.TryGetValue("pressurant", out double pk) ? pk : 0;
            float yTop = -0.02f;
            // pump size from turbomachinery mass: a cylinder of length 2r at ~3000 kg/m^3 effective density
            float Pump(double kg) => Mathf.Clamp(Mathf.Pow((float)(kg / (3000 * 2 * Mathf.PI)), 1f / 3f), 0.03f, 1.5f);
            void Cyl(float r, float h, Vector3 pos, Quaternion rot) => parts.Add(new CombineInstance { mesh = Cylinder(r, h), transform = Matrix4x4.TRS(pos, rot, Vector3.one) });
            void Sph(float r, Vector3 pos) => parts.Add(new CombineInstance { mesh = Sphere(r), transform = Matrix4x4.TRS(pos, Quaternion.identity, Vector3.one) });
            Vector3 Around(float angleDeg, float radius, float y) => new Vector3(radius * Mathf.Cos(angleDeg * Mathf.Deg2Rad), y, radius * Mathf.Sin(angleDeg * Mathf.Deg2Rad));

            switch (d.Cycle)
            {
                case "PressureFed":
                {
                    float rb = Mathf.Clamp(Mathf.Pow((float)(pressKg / (800 * 4.19f)), 1f / 3f), 0.03f, 1.2f);
                    Sph(rb, Around(0, rc + rb + 0.01f, yTop - rb));
                    Sph(rb, Around(180, rc + rb + 0.01f, yTop - rb));
                    break;
                }
                case "GasGenerator":
                {
                    float rp = Pump(turboKg);
                    Vector3 pump = Around(0, rc + rp * 1.1f, yTop - rp * 1.2f);
                    Cyl(rp, 2 * rp, pump, Quaternion.identity);
                    Sph(rp * 0.45f, Around(60, rc + rp * 0.5f, yTop - rp * 0.6f));   // gas generator
                    // turbine exhaust duct down beside the bell (its own small plume lives at its end later)
                    float ductR = rp * 0.25f, ductLen = Mathf.Max(0.6f * (pump.y - g.YThroat), 0.05f);
                    Cyl(ductR, ductLen, new Vector3(pump.x + rp * 0.3f, pump.y - rp - ductLen / 2, pump.z), Quaternion.identity);
                    break;
                }
                case "Expander":
                {
                    float rp = Pump(turboKg);
                    Cyl(rp, 2 * rp, Around(0, rc + rp * 1.1f, yTop - rp * 1.2f), Quaternion.identity);
                    break;
                }
                case "StagedCombustion":
                {
                    float rp = Pump(turboKg);
                    Vector3 pump = Around(0, rc + rp * 1.1f, yTop - rp * 1.3f);
                    Cyl(rp, 2.2f * rp, pump, Quaternion.identity);
                    Sph(rp * 0.6f, pump + new Vector3(0, rp * 0.9f, 0));          // preburner on top
                    Cyl(rp * 0.3f, rc + rp, new Vector3(pump.x * 0.5f, yTop - rp * 0.3f, 0), Quaternion.Euler(0, 0, 90));   // hot-gas duct
                    break;
                }
                case "FullFlowStaged":
                {
                    float rp = Pump(turboKg / 2);
                    foreach (float a in new[] { 0f, 180f })
                    {
                        Vector3 pump = Around(a, rc + rp * 1.1f, yTop - rp * 1.3f);
                        Cyl(rp, 2.2f * rp, pump, Quaternion.identity);
                        Sph(rp * 0.6f, pump + new Vector3(0, rp * 0.9f, 0));
                    }
                    break;
                }
            }
            mesh.Clear();
            if (parts.Count > 0) mesh.CombineMeshes(parts.ToArray(), true, true);
            foreach (CombineInstance ci in parts) UnityEngine.Object.Destroy(ci.mesh);
            mesh.RecalculateBounds();
        }

        static Mesh Cylinder(float r, float h)
        {
            var m = new Mesh();
            var segs = new List<List<Vector2>>
            {
                new List<Vector2> { new Vector2(0, h / 2), new Vector2(r, h / 2) },
                new List<Vector2> { new Vector2(r, h / 2), new Vector2(r, -h / 2) },
                new List<Vector2> { new Vector2(r, -h / 2), new Vector2(0, -h / 2) },
            };
            Lathe(m, segs, null, 1, 16, 0);
            return m;
        }

        static Mesh Sphere(float r)
        {
            var m = new Mesh();
            var arc = new List<Vector2>();
            for (int i = 0; i <= 10; i++) { float t = Mathf.PI * i / 10; arc.Add(new Vector2(r * Mathf.Sin(t), r * Mathf.Cos(t))); }
            Lathe(m, new List<List<Vector2>> { arc }, null, 1, 16, 0);
            return m;
        }
    }
}
