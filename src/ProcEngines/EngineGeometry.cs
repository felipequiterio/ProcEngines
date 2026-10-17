using System;
using System.Collections.Generic;
using ProcEngines.Model;
using UnityEngine;

namespace ProcEngines
{
    /// <summary>One smooth surface of revolution: an (r, y) polyline with its material and UV mapping.</summary>
    public sealed class ProfileSegment
    {
        public List<Vector2> Points;
        public int Material;
        public float UTiles = 1;     // texture repeats around the circumference (whole tubes, whole bolts)
        public float VLength = 1;    // metres of arc length per texture repeat along the profile (negative runs V backwards)
        public float VOffset;        // V at the first point
        public bool Inward;          // the surface faces the axis side of the profile direction (inside of a bell running down)

        public ProfileSegment(List<Vector2> pts, int material, float uTiles = 1, float vLength = 1, float vOffset = 0, bool inward = false)
        {
            Points = pts; Material = material; UTiles = uTiles; VOffset = vOffset; Inward = inward;
            VLength = Mathf.Abs(vLength) < 1e-3f ? Mathf.Sign(vLength + 1e-9f) * 1e-3f : vLength;
        }
    }

    /// <summary>
    /// Builds the engine's surfaces from DESIGN + STATS. Part frame: +Y up, origin at the mount top (= top node,
    /// which therefore never moves). Deterministic: every LMP client builds the same mesh from the same node.
    ///
    /// Top to bottom: mount plate (bolted rim), gimbal block in an open thrust-frame bay, injector dome, injector
    /// flange, chamber with stiffener bands, regen convergent, then the gimballed bell: regen tubes with hat bands,
    /// the coolant manifold, and either a bolted joint flange plus a radiative extension or a plain exit.
    /// </summary>
    public sealed class EngineGeometry
    {
        // static mesh materials
        public const int MatSteel = 0, MatRegenStatic = 1, MatBolts = 2, StaticMaterials = 3;
        // bell mesh materials
        public const int MatRegen = 0, MatRadiative = 1, MatSoot = 2, MatBellSteel = 3, MatBellBolts = 4, BellMaterials = 5;

        public float YInjector, YThroat, YExit, YChamberBottom, YDomeTop, MountRadius, MountHeight, DomeHeight, GimbalRadius, MaxRadius, ChamberRadius, ThroatRadius;
        public readonly List<ProfileSegment> Static = new List<ProfileSegment>();
        public readonly List<ProfileSegment> Bell = new List<ProfileSegment>();
        public readonly List<Vector2> BellOuter = new List<Vector2>();   // throat to exit, outer wall (r, y), for kit clearance
        public float RegenEndY, RegenEndRadius;
        public bool HasExtension;
        public float ManifoldY, ManifoldRadius, ManifoldTubeRadius;
        /// <summary>_Emissive_ST for the extension slot: maps its physical V onto 0..1 from the joint to the exit.</summary>
        public Vector4 ExtensionEmissiveST = new Vector4(1, 1, 0, 0);
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

        /// <param name="manifoldTubeRadius">Coolant manifold pipe radius (the fuel line radius, KitBuilder.LineRadii).</param>
        public static EngineGeometry Build(EngineDesign d, EngineStats s, TechLevel tech, float manifoldTubeRadius = 0.02f)
        {
            var g = new EngineGeometry();
            float rt = (float)s.ThroatRadius, rc = (float)s.ChamberRadius, lc = (float)s.ChamberLength;
            float mountR = Mathf.Max(0.7f * rc, 1.2f * rt);
            float mountH = 0.12f * rc + 0.02f;
            float domeH = 0.35f * rc;
            float rg = Mathf.Max(0.28f * rc, 0.6f * rt);
            float gimbalH = 0.24f * rc + 0.01f;
            g.MountRadius = mountR; g.MountHeight = mountH; g.DomeHeight = domeH; g.ChamberRadius = rc; g.ThroatRadius = rt; g.GimbalRadius = rg;
            float steelV = Mathf.Max(0.25f, 2 * Mathf.PI * rc / 4);
            List<ProfileSegment> st = g.Static;

            // mount plate: top face, bolted rim, underside (open to the thrust-frame bay below)
            st.Add(new ProfileSegment(Line(0, 0, mountR, 0, 4), MatSteel, 4, steelV));
            BoltedRim(st, mountR, 0, -mountH, MatBolts);
            st.Add(new ProfileSegment(Line(mountR, -mountH, 0.001f, -mountH, 3), MatSteel, 6, steelV));

            // gimbal block: bolted flange, neck, spherical bearing, neck, base flange onto the dome
            float y = -mountH, fl = 0.16f * gimbalH, fr = rg * 1.12f;
            BoltedRim(st, fr, y, y - fl, MatBolts);
            st.Add(new ProfileSegment(Line(fr, y - fl, rg * 0.62f, y - fl, 1), MatSteel, 4, steelV));
            y -= fl;
            float neck = 0.14f * gimbalH, ball = gimbalH - 2 * fl - 2 * neck;
            st.Add(new ProfileSegment(Line(rg * 0.62f, y, rg * 0.62f, y - neck, 1), MatSteel, 4, steelV));
            y -= neck;
            var arc = new List<Vector2>();
            for (int i = 0; i <= 10; i++)
            {
                float th = Mathf.PI * i / 10;
                arc.Add(new Vector2(rg * 0.62f + (rg - rg * 0.62f) * Mathf.Sin(th), y - ball * 0.5f * (1 - Mathf.Cos(th))));
            }
            st.Add(new ProfileSegment(arc, MatSteel, 4, steelV));
            y -= ball;
            st.Add(new ProfileSegment(Line(rg * 0.62f, y, rg * 0.62f, y - neck, 1), MatSteel, 4, steelV));
            y -= neck;
            st.Add(new ProfileSegment(Line(rg * 0.62f, y, fr, y, 1), MatSteel, 4, steelV));
            BoltedRim(st, fr, y, y - fl, MatBolts);
            y -= fl;
            g.YDomeTop = y;

            // injector dome: flat top under the gimbal block, then a quarter ellipse out to the chamber
            float r0 = fr * 0.95f;
            st.Add(new ProfileSegment(Line(fr, y, r0, y, 1), MatSteel, 4, steelV));
            var dome = new List<Vector2>();
            for (int i = 0; i <= 12; i++)
            {
                float t = i / 12f * Mathf.PI / 2;
                dome.Add(new Vector2(r0 + (rc - r0) * Mathf.Sin(t), y - domeH * (1 - Mathf.Cos(t))));
            }
            st.Add(new ProfileSegment(dome, MatSteel, 8, steelV));
            float yInj = y - domeH;
            g.YInjector = yInj;

            // injector flange where the dome meets the chamber
            float ft = 0.06f * rc + 0.004f, fh = 0.1f * rc + 0.008f;
            Flange(st, rc, rc, yInj + fh * 0.5f, yInj - fh * 0.5f, ft, MatSteel, MatBolts, steelV);

            // whole number of cooling tubes, a multiple of the texture's tubes per tile, constant along the length
            // (they taper with the wall), DESIGN.md 8.4
            g.TubeCount = Mathf.Clamp(Mathf.RoundToInt(2 * Mathf.PI * rc / 0.035f * 2 / EngineMaterials.TubesPerTile) * EngineMaterials.TubesPerTile, 24, 240);
            float tubeTiles = g.TubeCount / (float)EngineMaterials.TubesPerTile;

            // chamber cylinder: steel jacket over the regen liner, with stiffener bands on long chambers
            float yConvStart = yInj - lc;
            g.YChamberBottom = yConvStart;
            st.Add(new ProfileSegment(Line(rc, yInj - fh * 0.5f, rc, yConvStart, 4), MatSteel, 8, steelV));
            if (lc > 1.1f * rc)
                for (int k = 1; k <= 2; k++)
                {
                    float yb = yInj - lc * k / 3f, bh = 0.06f * rc + 0.005f;
                    Band(st, rc, rc, yb + bh * 0.5f, yb - bh * 0.5f, 0.025f * rc + 0.002f, MatSteel, steelV);
                }

            // convergent: 30-degree cone, then a 1.5 Rt arc into the throat (regen tubes; heat coordinate 0.5 -> 0)
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
            st.Add(new ProfileSegment(conv, MatRegenStatic, tubeTiles, -2 * ArcLength(conv), 0.5f));

            // ---------------------------------------------------------------- bell (gimballed)
            List<ProfileSegment> bell = g.Bell;
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
            g.HasExtension = rad.Count > 1;
            float regenArc = ArcLength(regen);
            bell.Add(new ProfileSegment(regen, MatRegen, tubeTiles, regenArc));
            g.RegenEndY = regen[regen.Count - 1].y;
            g.RegenEndRadius = regen[regen.Count - 1].x;
            Vector2 exit = g.BellOuter[g.BellOuter.Count - 1];
            g.YExit = exit.y;
            float bellSteelV = Mathf.Max(0.25f, 2 * Mathf.PI * exit.x / 6);

            // hat bands: stiffening rings on long regen bells
            float regenLen = yThroat - g.RegenEndY;
            int bands = regenLen > 5 * rt ? Mathf.Clamp(Mathf.RoundToInt(regenLen / (4.5f * rt)), 1, 4) : 0;
            float bandW = 0.12f * rt + 0.005f, bandT = 0.03f * rt + 0.002f;
            for (int k = 0; k < bands; k++)
            {
                float yb = yThroat - regenLen * (0.35f + 0.5f * (k + 0.5f) / bands);
                Band(bell, g.BellRadiusAt(yb + bandW * 0.5f), g.BellRadiusAt(yb - bandW * 0.5f), yb + bandW * 0.5f, yb - bandW * 0.5f, bandT, MatBellSteel, bellSteelV);
            }

            // coolant manifold: at the joint above the extension flange, or just above the exit of an all-regen bell
            float mr = Mathf.Max(manifoldTubeRadius * 1.15f, 0.004f);
            g.ManifoldTubeRadius = mr;
            float wall = Mathf.Max(0.012f * exit.x, 0.004f);
            if (g.HasExtension)
            {
                float rj = g.RegenEndRadius, jh = 0.05f * rj + 0.008f, jt = 0.025f * rj + 0.004f;
                Flange(bell, g.BellRadiusAt(g.RegenEndY + jh * 0.5f), g.BellRadiusAt(g.RegenEndY - jh * 0.5f), g.RegenEndY + jh * 0.5f, g.RegenEndY - jh * 0.5f, jt, MatBellSteel, MatBellBolts, bellSteelV);
                g.ManifoldY = g.RegenEndY + jh * 0.5f + mr * 1.3f;
            }
            else g.ManifoldY = exit.y + mr * 1.6f;
            g.ManifoldRadius = g.BellRadiusAt(g.ManifoldY) + mr * 0.85f;
            bell.Add(new ProfileSegment(Circle(g.ManifoldRadius, g.ManifoldY, mr, 12), MatBellSteel, 6, bellSteelV));

            // radiative extension: physical tiling for the weave; the emissive ramp is remapped per engine
            if (g.HasExtension)
            {
                float extArc = ArcLength(rad);
                float circ = 2 * Mathf.PI * exit.x;
                bell.Add(new ProfileSegment(rad, MatRadiative, Mathf.Max(1, Mathf.Round(circ / EngineMaterials.RadiativeTile)), EngineMaterials.RadiativeTile));
                g.ExtensionEmissiveST = new Vector4(1, EngineMaterials.RadiativeTile / Mathf.Max(extArc, 1e-3f), 0, 0);
                // a thin stiffening ring at the exit (its underside is the exit lip below)
                float lh = wall * 3, lt = wall * 1.2f;
                Band(bell, g.BellRadiusAt(exit.y + lh), exit.x, exit.y + lh, exit.y, lt, MatRadiative, EngineMaterials.RadiativeTile, false);
            }

            // exit lip and inside wall: the extension's inside glows with it (joint -> exit), the regen inside is sooty
            bell.Add(new ProfileSegment(Line(exit.x + (g.HasExtension ? wall * 1.2f : 0), exit.y, exit.x - wall, exit.y, 1), g.HasExtension ? MatRadiative : MatSoot, 8, 1));
            var innerAll = new List<Vector2>();
            foreach (Vector2 p in g.BellOuter) innerAll.Add(new Vector2(Mathf.Max(p.x - wall, 0.001f), p.y));
            int split = regen.Count - 1;
            if (g.HasExtension)
            {
                var innerExt = innerAll.GetRange(split, innerAll.Count - split);
                bell.Add(new ProfileSegment(innerExt, MatRadiative, Mathf.Max(1, Mathf.Round(2 * Mathf.PI * exit.x / EngineMaterials.RadiativeTile)), EngineMaterials.RadiativeTile, 0, true));
            }
            var innerRegen = innerAll.GetRange(0, (g.HasExtension ? split : innerAll.Count - 1) + 1);
            innerRegen.Reverse();   // up toward the throat: the profile normal then faces the axis
            bell.Add(new ProfileSegment(innerRegen, MatSoot, 8, Mathf.Max(0.3f, exit.x)));
            bell.Add(new ProfileSegment(Line(innerAll[0].x, yThroat, 0, yThroat, 1), MatSoot, 1, 1));

            g.MaxRadius = Mathf.Max(exit.x + wall * 1.2f, rc + ft, mountR, g.ManifoldRadius + mr);
            int mid = g.BellOuter.Count / 2;
            g.ColliderChamberTop = new Vector2(Mathf.Max(rc, mountR), 0);
            g.ColliderChamberBottom = conv[conv.Count / 2];
            g.ColliderThroat = new Vector2(rt, yThroat);
            g.ColliderMid = g.BellOuter[mid];
            g.ColliderExit = exit;
            return g;
        }

        // ------------------------------------------------------------------ profile pieces

        static List<Vector2> Line(float r0, float y0, float r1, float y1, int steps)
        {
            var l = new List<Vector2>();
            for (int i = 0; i <= steps; i++) { float t = (float)i / steps; l.Add(new Vector2(r0 + (r1 - r0) * t, y0 + (y1 - y0) * t)); }
            return l;
        }

        static float ArcLength(List<Vector2> pts)
        {
            float a = 0;
            for (int i = 1; i < pts.Count; i++) a += (pts[i] - pts[i - 1]).magnitude;
            return a;
        }

        /// <summary>Closed circle in the profile plane, run clockwise so the lathe's profile normal faces outward (a torus).</summary>
        static List<Vector2> Circle(float r, float y, float radius, int n)
        {
            var c = new List<Vector2>();
            for (int i = 0; i <= n; i++)
            {
                float th = -2 * Mathf.PI * i / n;
                c.Add(new Vector2(r + radius * Mathf.Cos(th), y + radius * Mathf.Sin(th)));
            }
            return c;
        }

        /// <summary>Vertical rim from y0 down to y1 at radius r with one bolt head per texture repeat (~1.6x as wide as high).</summary>
        static void BoltedRim(List<ProfileSegment> into, float r, float y0, float y1, int boltMat)
        {
            float h = Mathf.Max(y0 - y1, 1e-3f);
            float bolts = Mathf.Clamp(Mathf.Round(2 * Mathf.PI * r / (1.6f * h)), 8, 180);
            into.Add(new ProfileSegment(Line(r, y0, r, y1, 1), boltMat, bolts, h));
        }

        /// <summary>A ring standing proud of a wall by `t`: top face, side, bottom face. The wall radius is rTop at
        /// yTop and rBottom at yBottom (it may flare).</summary>
        static void Band(List<ProfileSegment> into, float rTop, float rBottom, float yTop, float yBottom, float t, int mat, float vLen, bool bottomFace = true)
        {
            into.Add(new ProfileSegment(Line(rTop * 0.995f, yTop, rTop + t, yTop, 1), mat, 8, vLen));
            into.Add(new ProfileSegment(Line(rTop + t, yTop, rBottom + t, yBottom, 1), mat, 8, vLen));
            if (bottomFace) into.Add(new ProfileSegment(Line(rBottom + t, yBottom, rBottom * 0.995f, yBottom, 1), mat, 8, vLen));
        }

        /// <summary>A bolted flange: like a band, with bolt heads on its rim.</summary>
        static void Flange(List<ProfileSegment> into, float rTop, float rBottom, float yTop, float yBottom, float t, int mat, int boltMat, float vLen)
        {
            into.Add(new ProfileSegment(Line(rTop * 0.995f, yTop, rTop + t, yTop, 1), mat, 8, vLen));
            float h = Mathf.Max(yTop - yBottom, 1e-3f);
            float bolts = Mathf.Clamp(Mathf.Round(2 * Mathf.PI * (rTop + t) / (1.6f * h)), 8, 180);
            into.Add(new ProfileSegment(Line(rTop + t, yTop, rBottom + t, yBottom, 1), boltMat, bolts, h));
            into.Add(new ProfileSegment(Line(rBottom + t, yBottom, rBottom * 0.995f, yBottom, 1), mat, 8, vLen));
        }

        // ------------------------------------------------------------------ mesh

        /// <summary>Revolve profile segments around Y into one mesh, one submesh per material (MeshBuilder.Lathe).</summary>
        public static void Lathe(Mesh mesh, List<ProfileSegment> segments, int materialCount, int ringSegments, float yOffset)
        {
            var mb = new MeshBuilder(materialCount);
            mb.Lathe(segments, ringSegments, yOffset);
            mb.Build(mesh);
        }

        public static void FixWinding(List<Vector3> v, List<Vector3> n, List<int> tris, int start = 0)
        {
            int agree = 0, disagree = 0;
            for (int t = start; t < tris.Count; t += 3)
            {
                Vector3 fn = Vector3.Cross(v[tris[t + 1]] - v[tris[t]], v[tris[t + 2]] - v[tris[t]]);
                if (fn.sqrMagnitude < 1e-14f) continue;
                if (Vector3.Dot(fn, n[tris[t]] + n[tris[t + 1]] + n[tris[t + 2]]) >= 0) agree++; else disagree++;
            }
            if (disagree > agree)
                for (int t = start; t < tris.Count; t += 3) { int x = tris[t + 1]; tris[t + 1] = tris[t + 2]; tris[t + 2] = x; }
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
