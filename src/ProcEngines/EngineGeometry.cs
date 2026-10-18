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
    /// flange, chamber (the cooled wall, tubes or channel jacket, from the flange down) with steel stiffener bands,
    /// regen convergent, then the gimballed bell: regen wall with hat-section bands, the coolant manifold, and either
    /// a bolted joint flange plus a radiative extension or a plain exit.
    ///
    /// Every profile is a polyline of control points with a fillet radius per corner (ProfileBuilder): outer edges
    /// are rounded and joints carry weld fillets, so edges catch highlights instead of reading as CAD creases. The
    /// level of detail only changes how densely curves and fillets are sampled: every position, radius and the UV
    /// layout that other code relies on (nodes, colliders, BellRadiusAt, DomePoint, manifold, emissive ST, bolt
    /// counts) is computed from the full-resolution contour and is identical at every level.
    /// </summary>
    public sealed class EngineGeometry
    {
        // static mesh materials
        public const int MatSteel = 0, MatRegenStatic = 1, MatBolts = 2, StaticMaterials = 3;
        // bell mesh materials
        public const int MatRegen = 0, MatRadiative = 1, MatSoot = 2, MatBellSteel = 3, MatBellBolts = 4, BellMaterials = 5;

        public float YInjector, YThroat, YExit, YChamberBottom, YDomeTop, MountRadius, MountHeight, DomeHeight, GimbalRadius, MaxRadius, ChamberRadius, ThroatRadius;
        /// <summary>Largest radius of the static (chamber) mesh: sets its ring segments.</summary>
        public float StaticMaxRadius;
        /// <summary>Level of detail the profile lists were sampled for (0 = full).</summary>
        public int Lod;
        public readonly List<ProfileSegment> Static = new List<ProfileSegment>();
        public readonly List<ProfileSegment> Bell = new List<ProfileSegment>();
        public readonly List<Vector2> BellOuter = new List<Vector2>();   // throat to exit, outer wall (r, y), for kit clearance (full resolution at every LOD)
        public float RegenEndY, RegenEndRadius;
        public bool HasExtension;
        public float ManifoldY, ManifoldRadius, ManifoldTubeRadius;
        /// <summary>_Emissive_ST for the extension slot: maps its physical V onto 0..1 from the joint to the exit.</summary>
        public Vector4 ExtensionEmissiveST = new Vector4(1, 1, 0, 0);
        public int TubeCount;
        public Vector2 ColliderChamberTop, ColliderChamberBottom, ColliderMid, ColliderExit, ColliderThroat;   // (r, y)

        public float DomeTopRadius;

        /// <summary>Cooling tube width at the end of the regen section, and the most tubes a wall gets.</summary>
        public const float TubeWidth = 0.020f;
        public const int TubesMax = 512;

        /// <summary>Ring segments around the axis: ~2 cm facets at LOD0 (64..192), about a third of that at LOD1.</summary>
        public static int RingSegments(float maxRadius, int lod)
        {
            int s0 = Mathf.Clamp(Mathf.CeilToInt(2 * Mathf.PI * Mathf.Max(maxRadius, 1e-3f) / 0.02f), 64, 192);
            return lod <= 0 ? s0 : Mathf.Clamp(Mathf.RoundToInt(s0 / 3f), 24, 64);
        }

        /// <summary>Wall construction (ASSETS.md 5, round 2): a milled-channel / brazed-jacket chamber instead of brazed
        /// tubes, as on the RD-180, NK-33, Raptor and late Merlin: staged combustion, full flow, and gas generators at
        /// the Late tech level. Only the material changes (the `jacket` surface shares the tube UV contract).</summary>
        public static bool ChannelWall(EngineDesign d) =>
            d != null && (d.Cycle == "StagedCombustion" || d.Cycle == "FullFlowStaged" || (d.Cycle == "GasGenerator" && d.Tech == "Late"));

        /// <summary>Channel wall on the nozzle as well. Hydrogen engines keep a tube-wall nozzle under a channel-wall
        /// chamber (RS-25, J-2X, Vulcain 2), so a hydrolox engine only gets the jacket on the static (chamber) mesh.</summary>
        public static bool ChannelNozzle(EngineDesign d) => ChannelWall(d) && d.Propellant != "Hydrolox";

        /// <summary>Bumped when the meshes built from the same DESIGN + STATS change (code or asset contract).</summary>
        public const string MeshVersion = "geo-r2";

        /// <summary>
        /// Identity of the render meshes: every input of Build, KitBuilder and the lathes. The serialized DESIGN (it
        /// carries the tech level and propellant) plus the serialized STATS (in flight they are the stored node, which
        /// may differ from what DESIGN would compute today), the mesh version, and a caller scope (for example the
        /// scene class, so meshes that may lose their CPU copy in flight never serve the editor). Model data and the
        /// kit assets are fixed for the session. The level of detail is prefixed by the caller.
        /// </summary>
        public static string MeshKey(EngineDesign d, EngineStats s, string scope)
        {
            var sb = new System.Text.StringBuilder(2048);
            sb.Append(MeshVersion).Append('|').Append(scope).Append('|');
            AppendNode(sb, Serialization.SaveDesign(d));
            sb.Append('|');
            AppendNode(sb, Serialization.SaveStats(s));
            return sb.ToString();
        }

        static void AppendNode(System.Text.StringBuilder sb, CfgNode n)
        {
            sb.Append(n.Name).Append('{');
            foreach (KeyValuePair<string, string> kv in n.Values) sb.Append(kv.Key).Append('=').Append(kv.Value).Append(';');
            foreach (CfgNode c in n.Nodes) AppendNode(sb, c);
            sb.Append('}');
        }

        /// <summary>Point on the injector dome at radius r (clamped onto the dome), with its outward (r, y) normal.</summary>
        public void DomePoint(float r, out Vector2 point, out Vector2 normal)
        {
            float span = Mathf.Max(ChamberRadius - DomeTopRadius, 1e-4f);
            float t = Mathf.Asin(Mathf.Clamp((r - DomeTopRadius) / span, 0.15f, 0.97f));
            point = new Vector2(DomeTopRadius + span * Mathf.Sin(t), YDomeTop - DomeHeight * (1 - Mathf.Cos(t)));
            normal = new Vector2(DomeHeight * Mathf.Sin(t), span * Mathf.Cos(t)).normalized;
        }

        /// <summary>Outer bell radius at height y (the throat radius above the throat).</summary>
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

        // ------------------------------------------------------------------ level of detail

        /// <summary>Sampling densities per level: curve points and fillet steps.</summary>
        struct Detail
        {
            public int Convex, Concave;          // arc steps of rounded outer edges and of weld fillets
            public int BellPoints, InnerPoints;  // Rao bell outer contour and inner wall
            public int Dome, Ball, Conv, Torus;
            public bool Supports;                // extra points next to fillets on long faces (keeps flat faces flat-shaded)
        }

        static Detail DetailFor(int lod) => lod <= 0
            ? new Detail { Convex = 3, Concave = 2, BellPoints = 96, InnerPoints = 64, Dome = 24, Ball = 20, Conv = 20, Torus = 20, Supports = true }
            : new Detail { Convex = 1, Concave = 1, BellPoints = 32, InnerPoints = 16, Dome = 8, Ball = 8, Conv = 7, Torus = 10, Supports = false };

        // ------------------------------------------------------------------ build

        /// <param name="manifoldTubeRadius">Coolant manifold pipe radius (the fuel line radius, KitBuilder.LineRadii).</param>
        /// <param name="lod">0 = full detail, 1 = the distant level (fewer curve points, chamfers instead of fillets).</param>
        public static EngineGeometry Build(EngineDesign d, EngineStats s, TechLevel tech, float manifoldTubeRadius = 0.02f, int lod = 0)
        {
            var g = new EngineGeometry { Lod = Mathf.Clamp(lod, 0, 1) };
            Detail det = DetailFor(g.Lod);
            float rt = (float)s.ThroatRadius, rc = (float)s.ChamberRadius, lc = (float)s.ChamberLength;
            float mountR = Mathf.Max(0.7f * rc, 1.2f * rt);
            float mountH = 0.12f * rc + 0.02f;
            float domeH = 0.35f * rc;
            float rg = Mathf.Max(0.28f * rc, 0.6f * rt);
            float gimbalH = 0.24f * rc + 0.01f;
            g.MountRadius = mountR; g.MountHeight = mountH; g.DomeHeight = domeH; g.ChamberRadius = rc; g.ThroatRadius = rt; g.GimbalRadius = rg;

            // Cooling tubes: a whole number of texture tiles (EngineMaterials.TubesPerTile tubes each), constant along
            // the length, so they taper with the wall (DESIGN.md 8.4). Sized where they are widest, at the end of the
            // regen section: ~20 mm there, as on real tube walls (H-1 ~290, RL10 ~180, F-1 178/356 tubes), 64..TubesMax
            // tubes, and no narrower than 3.5 mm at the throat as long as one whole tile (32 tubes) still fits there,
            // which can drop small engines to 32. Below a throat radius of about 17.8 mm (32 x 3.5 mm / 2 pi, roughly
            // under 10 kN) the one-tile floor wins and the throat tubes get narrower (about 2 mm at a 10 mm throat).
            double regenEps = d.RadiativeExtension ? Math.Min(d.Eps, tech?.RegenEpsMax ?? 30) : d.Eps;
            int tpt = EngineMaterials.TubesPerTile;
            float rEnd = rt * Mathf.Sqrt((float)regenEps);
            int tubeTileCount = Mathf.Clamp(Mathf.RoundToInt(2 * Mathf.PI * rEnd / TubeWidth / tpt), Mathf.Max(1, 64 / tpt), Mathf.Max(1, TubesMax / tpt));
            tubeTileCount = Mathf.Max(Mathf.Max(1, 32 / tpt), Mathf.Min(tubeTileCount, Mathf.FloorToInt(2 * Mathf.PI * rt / 0.0035f / tpt)));
            g.TubeCount = tubeTileCount * tpt;
            float tubeTiles = tubeTileCount;

            Style steel = Style.Steel(MatSteel), bolts = Style.Bolts(MatBolts);
            var pb = new ProfileBuilder(det);

            // mount plate: top face (the stack interface: only a small edge radius), bolted rim, underside (open to
            // the thrust-frame bay below); the underside ends inside the gimbal block's top flange
            float y = -mountH, fl = 0.2f * gimbalH, fr = rg * 1.12f, rn = rg * 0.62f;
            pb.Move(new Vector2(0, 0));
            pb.Line(new Vector2(mountR, 0), steel, 0.12f * mountH);
            pb.Line(new Vector2(mountR, -mountH), bolts, 0.2f * mountH);
            pb.Line(new Vector2(fr * 0.96f, -mountH), steel);

            // gimbal block: bolted flange welded under the plate, neck, spherical bearing, neck, base flange
            // welded onto the dome
            float neck = 0.12f * gimbalH, ball = gimbalH - 2 * fl - 2 * neck;
            // small radii on the bolted rims (thin flanges: the bolt heads need the straight part)
            float weldG = 0.18f * fl, edgeG = 0.15f * fl, neckR = 0.35f * neck;
            pb.Move(new Vector2(fr + 1.4f * weldG, y + 0.12f * weldG));   // weld leg, inside the plate
            pb.Line(new Vector2(fr, y), steel, weldG);
            pb.Line(new Vector2(fr, y - fl), bolts, edgeG);
            y -= fl;
            pb.Line(new Vector2(rn, y), steel, neckR);
            y -= neck;
            pb.Line(new Vector2(rn, y), steel, neckR);
            // the bearing: an oblate ellipse through the neck radius (semi-axes rg and b)
            float psi0 = Mathf.Acos(0.62f), bAxis = ball * 0.5f / Mathf.Sin(psi0), yBall = y - ball * 0.5f;
            for (int i = 1; i < det.Ball; i++)
            {
                float psi = psi0 - 2 * psi0 * i / det.Ball;
                pb.Line(new Vector2(rg * Mathf.Cos(psi), yBall + bAxis * Mathf.Sin(psi)), steel, -1);
            }
            y -= ball;
            pb.Line(new Vector2(rn, y), steel, neckR);
            y -= neck;
            pb.Line(new Vector2(rn, y), steel, neckR);
            pb.Line(new Vector2(fr, y), steel, edgeG);
            y -= fl;
            g.YDomeTop = y;

            // injector dome: a quarter ellipse from under the gimbal block out to the chamber (DomePoint is the same
            // analytic surface); it runs into the injector flange's top face through a weld fillet
            float r0 = fr * 0.95f, yDome = y;
            g.DomeTopRadius = r0;
            float yInj = y - domeH;
            g.YInjector = yInj;
            float ft = 0.06f * rc + 0.004f, fh = 0.1f * rc + 0.008f;
            Vector2 DomeAt(float t) => new Vector2(r0 + (rc - r0) * Mathf.Sin(t), yDome - domeH * (1 - Mathf.Cos(t)));
            float tFrom = Mathf.Asin(Mathf.Clamp01((fr - r0) / Mathf.Max(rc - r0, 1e-4f)));
            float tTo = Mathf.Max(Mathf.Acos(Mathf.Clamp01(fh * 0.5f / Mathf.Max(domeH, 1e-4f))), tFrom + 0.05f);
            // the weld fillets at both ends of the dome get a guard point at a fixed distance, so their size (and the
            // bolt rim above) never depends on how densely this level samples the dome
            float weldI = 0.5f * ft, edgeI = 0.3f * Mathf.Min(ft, fh);
            float Speed(float t) => Mathf.Max(new Vector2((rc - r0) * Mathf.Cos(t), domeH * Mathf.Sin(t)).magnitude, 1e-5f);
            float tA = tFrom + 3 * weldG / Speed(tFrom), tB = tTo - 3 * weldI / Speed(tTo);
            if (tB - tA < 0.1f * (tTo - tFrom)) { tA = tFrom + 0.3f * (tTo - tFrom); tB = tTo - 0.3f * (tTo - tFrom); }
            pb.Line(DomeAt(tFrom), bolts, weldG);
            for (int i = 0; i <= det.Dome - 2; i++) pb.Line(DomeAt(tA + (tB - tA) * i / (det.Dome - 2)), steel, -1);

            // injector flange where the dome meets the chamber
            pb.Line(DomeAt(tTo), steel, weldI);
            pb.Line(new Vector2(rc + ft, yInj + fh * 0.5f), steel, edgeI);
            pb.Line(new Vector2(rc + ft, yInj - fh * 0.5f), bolts, edgeI);
            pb.Line(new Vector2(rc, yInj - fh * 0.5f), steel, weldI);

            // chamber: the cooled wall (tubes, or the channel jacket) runs up to the injector flange, as on the H-1,
            // F-1, J-2 and RL10 tube walls; steel stiffener bands on long chambers. Heat coordinate 1 at the flange
            // down to 0.5 where the convergent starts (set by height below, so it runs on across the bands).
            float yConvStart = yInj - lc;
            g.YChamberBottom = yConvStart;
            var chamberWall = new Style { Mat = MatRegenStatic, Kind = Uv.Fixed, U = tubeTiles, V = -2f * lc, VOffset = 1f, Priority = 2, Made = new List<ProfileSegment>() };
            if (lc > 1.1f * rc)
                for (int k = 1; k <= 2; k++)
                {
                    float yb = yInj - lc * k / 3f, bh = 0.06f * rc + 0.005f, bt = 0.025f * rc + 0.002f;
                    pb.Line(new Vector2(rc, yb + bh * 0.5f), chamberWall, 0.6f * bt);
                    pb.Line(new Vector2(rc + bt, yb + bh * 0.5f), steel, 0.35f * bt);
                    pb.Line(new Vector2(rc + bt, yb - bh * 0.5f), steel, 0.35f * bt);
                    pb.Line(new Vector2(rc, yb - bh * 0.5f), steel, 0.6f * bt);
                }

            // convergent: a rounded shoulder, 30-degree cone, then a 1.5 Rt arc into the throat (regen tubes; heat
            // coordinate 0.5 -> 0 along its own arc length). The shoulder fillet belongs to the chamber wall.
            float rArc = 2.5f * rt - 1.5f * rt * Mathf.Sin(60 * Mathf.Deg2Rad);
            float coneLen = Mathf.Max(rc - rArc, 0) / Mathf.Tan(30 * Mathf.Deg2Rad);
            float yThroat = yConvStart - coneLen - 0.75f * rt;
            g.YThroat = yThroat;
            var conv = new Style { Mat = MatRegenStatic, Kind = Uv.ArcScaled, U = tubeTiles, V = -2, VOffset = 0.5f, Priority = 2 };
            pb.Line(new Vector2(rc, yConvStart), chamberWall, 0.8f * rc);
            pb.Line(new Vector2(rArc, yThroat + 0.75f * rt), conv, -1);
            for (int i = 1; i <= det.Conv; i++)
            {
                float th = (-120f + 30f * i / det.Conv) * Mathf.Deg2Rad;
                pb.Line(new Vector2(2.5f * rt + 1.5f * rt * Mathf.Sin(th), yThroat - 1.5f * rt * Mathf.Cos(th)), conv, -1);
            }
            pb.Emit(g.Static);
            HeatByHeight(chamberWall.Made, yInj - fh * 0.5f, 1f, 0.5f);

            // ---------------------------------------------------------------- bell (gimballed)
            var contour = new NozzleContour(d.Eps, d.LengthFraction, d.Conical);
            var raw = new List<Vector2>(contour.Wall.Count);
            bool ext = false;
            foreach (WallPoint w in contour.Wall)
            {
                raw.Add(new Vector2((float)w.R * rt, yThroat - (float)w.X * rt));
                if (w.R * w.R > regenEps + 1e-6) ext = true;
            }
            Vector2 exit = raw[raw.Count - 1];
            g.YExit = exit.y;

            // smooth the Rao contour (centripetal Catmull-Rom) and resample it evenly in arc length plus turning, so
            // the throat gets its share of points; the regen / extension joint is placed exactly at regenEps
            var dense = new List<Vector2>(raw.Count * 8 + 1);
            var denseTan = new List<Vector2>(raw.Count * 8 + 1);
            var cost = new List<float>(raw.Count * 8 + 1);
            Densify(raw, 8, dense, denseTan, cost);
            float total = cost[cost.Count - 1];
            float cSplit = ext ? CostAtRadius(dense, cost, Mathf.Sqrt((float)regenEps) * rt) : total;
            // The extension is decided once, from the split itself: for Eps a hair above regenEps the wall test above
            // says "extension" while the split lands on the exit (SplitSample then puts every point in the regen part
            // and the extension lists would be empty). HasExtension, the branch below, the joint flange, the
            // manifold and the glow all follow this one flag.
            if (ext && cSplit >= total - 1e-6f) { ext = false; cSplit = total; }
            g.HasExtension = ext;
            int n0 = d.Conical ? 56 : DetailFor(0).BellPoints;
            var regen0 = new List<Vector2>(n0);
            var rad0 = new List<Vector2>(n0);
            SplitSample(dense, denseTan, cost, cSplit, total, n0, regen0, null, rad0, null);
            g.BellOuter.AddRange(regen0);
            for (int i = 1; i < rad0.Count; i++) g.BellOuter.Add(rad0[i]);
            g.RegenEndY = regen0[regen0.Count - 1].y;
            g.RegenEndRadius = regen0[regen0.Count - 1].x;
            float extArc0 = ArcLength(rad0);

            List<Vector2> regen = regen0, rad = rad0;
            if (g.Lod > 0)
            {
                int n1 = d.Conical ? 20 : det.BellPoints;
                regen = new List<Vector2>(n1); rad = new List<Vector2>(n1);
                SplitSample(dense, denseTan, cost, cSplit, total, n1, regen, null, rad, null);
            }
            // inner wall: the outer contour offset along its normal by the wall thickness
            float wall = Mathf.Max(0.012f * exit.x, 0.004f);
            int ni = d.Conical ? Mathf.Max(12, det.InnerPoints / 2) : det.InnerPoints;
            var innerRegen = new List<Vector2>(ni); var innerRegenTan = new List<Vector2>(ni);
            var innerRad = new List<Vector2>(ni); var innerRadTan = new List<Vector2>(ni);
            SplitSample(dense, denseTan, cost, cSplit, total, ni, innerRegen, innerRegenTan, innerRad, innerRadTan);
            Offset(innerRegen, innerRegenTan, wall);
            Offset(innerRad, innerRadTan, wall);
            Vector2 tExit = denseTan[denseTan.Count - 1], nExit = new Vector2(-tExit.y, tExit.x);

            var bellSteel = Style.Steel(MatBellSteel);
            var bellBolts = Style.Bolts(MatBellBolts);
            var regenStyle = new Style { Mat = MatRegen, Kind = Uv.ArcScaled, U = tubeTiles, V = 1 };   // heat coordinate 0 (throat) -> 1
            var soot = new Style { Mat = MatSoot, Kind = Uv.Fixed, U = 8, V = Mathf.Max(0.3f, exit.x) };
            var throatCap = new Style { Mat = MatSoot, Kind = Uv.Fixed, U = 1, V = 1 };
            var bb = new ProfileBuilder(det);

            // outer wall from the throat: regen tubes, then the extension (if any) up to its exit stiffening ring,
            // around the rolled exit lip and back up the inside to the throat
            bb.Move(regen[0]);
            for (int i = 1; i < regen.Count; i++) bb.Line(regen[i], regenStyle, -1);
            if (ext)
            {
                // radiative extension: physical tiling for the weave; the emissive ramp is remapped per engine. The
                // inside runs back up to the joint with V counting down to 0 there, so both walls glow from the joint.
                float extTiles = Mathf.Max(1, Mathf.Round(2 * Mathf.PI * exit.x / EngineMaterials.RadiativeTile));
                var radOut = new Style { Mat = MatRadiative, Kind = Uv.Fixed, U = extTiles, V = EngineMaterials.RadiativeTile };
                var radIn = new Style { Mat = MatRadiative, Kind = Uv.EndAtZero, U = extTiles, V = -EngineMaterials.RadiativeTile };
                g.ExtensionEmissiveST = new Vector4(1, EngineMaterials.RadiativeTile / Mathf.Max(extArc0, 1e-3f), 0, 0);
                float lh = Mathf.Min(wall * 3, 0.45f * (g.RegenEndY - exit.y)), lt = wall * 1.2f, yTop = exit.y + lh, weldR = Mathf.Min(0.5f * lt, 0.4f * lh);
                for (int i = 1; i < rad.Count && rad[i].y > yTop + 1.3f * weldR; i++) bb.Line(rad[i], radOut, -1);
                float rTop = g.BellRadiusAt(yTop);
                bb.Line(new Vector2(rTop, yTop), radOut, weldR);
                bb.Line(new Vector2(rTop + lt, yTop), radIn, 0.35f * lt);
                bb.Line(new Vector2(exit.x + lt, exit.y), radIn, 0.45f * lt);
                bb.Line(innerRad[innerRad.Count - 1], radIn, 0.45f * wall);
                for (int i = innerRad.Count - 2; i >= 0; i--) bb.Line(innerRad[i], radIn, -1);
            }
            else
            {
                // rolled lip: the outer wall turns over into the inner wall through two quarter rounds
                Vector2 q1 = exit + tExit * (wall * 0.5f), q2 = q1 - nExit * wall;
                bb.Line(q1, soot, wall * 0.5f);
                bb.Line(q2, soot, wall * 0.5f);
                bb.Line(innerRegen[innerRegen.Count - 1], soot, -1);
            }
            // regen inside: sooty, up to the throat, closed by a cap
            for (int i = innerRegen.Count - 2; i >= 0; i--) bb.Line(innerRegen[i], soot, -1);
            bb.Line(new Vector2(0, yThroat), throatCap);

            // hat bands: a few wide, brazed hat-section stiffeners on long regen bells, only a little proud of the tubes
            float regenLen = yThroat - g.RegenEndY;
            int bands = regenLen > 5 * rt ? Mathf.Clamp(Mathf.RoundToInt(regenLen / (7f * rt)), 1, 3) : 0;
            float bandW = 0.22f * rt + 0.008f, bandT = 0.05f * rt + 0.0035f;
            for (int k = 0; k < bands; k++)
            {
                float yb = yThroat - regenLen * (0.35f + 0.5f * (k + 0.5f) / bands);
                HatBand(bb, g, yb + bandW * 0.5f, yb - bandW * 0.5f, bandT, bellSteel);
            }

            // coolant manifold: at the joint above the extension flange, or just above the exit of an all-regen bell
            float mr = Mathf.Max(manifoldTubeRadius * 1.15f, 0.004f);
            g.ManifoldTubeRadius = mr;
            if (ext)
            {
                float rj = g.RegenEndRadius, jh = 0.05f * rj + 0.008f, jt = 0.025f * rj + 0.004f;
                Ring(bb, g, g.RegenEndY + jh * 0.5f, g.RegenEndY - jh * 0.5f, jt, bellSteel, bellBolts);
                g.ManifoldY = g.RegenEndY + jh * 0.5f + mr * 1.3f;
            }
            else g.ManifoldY = exit.y + mr * 1.6f;
            g.ManifoldRadius = g.BellRadiusAt(g.ManifoldY) + mr * 0.85f;
            // closed circle run clockwise (outward normal), starting and ending against the bell wall where the seam hides
            bb.Move(new Vector2(g.ManifoldRadius - mr, g.ManifoldY));
            for (int i = 1; i <= det.Torus; i++)
            {
                float th = Mathf.PI - 2 * Mathf.PI * i / det.Torus;
                bb.Line(new Vector2(g.ManifoldRadius + mr * Mathf.Cos(th), g.ManifoldY + mr * Mathf.Sin(th)), bellSteel, -1);
            }
            bb.Emit(g.Bell);

            g.MaxRadius = Mathf.Max(exit.x + wall * 1.2f, rc + ft, mountR, g.ManifoldRadius + mr);
            g.StaticMaxRadius = Mathf.Max(rc + ft, mountR, fr);
            // colliders: the same stations as the original coarse profile (the convergent arc at -105 degrees, the
            // middle wall point of the Rao contour), so the physics shape doesn't depend on the render detail
            float thC = -105f * Mathf.Deg2Rad;
            g.ColliderChamberTop = new Vector2(Mathf.Max(rc, mountR), 0);
            g.ColliderChamberBottom = new Vector2(2.5f * rt + 1.5f * rt * Mathf.Sin(thC), yThroat - 1.5f * rt * Mathf.Cos(thC));
            g.ColliderThroat = new Vector2(rt, yThroat);
            g.ColliderMid = raw[raw.Count / 2];
            g.ColliderExit = exit;
            return g;
        }

        /// <summary>A ring standing proud of the bell wall by t between yTop and yBottom: weld fillets where it meets
        /// the wall (the legs start just inside the wall, which passes underneath), rounded outer edges.</summary>
        static void Ring(ProfileBuilder pb, EngineGeometry g, float yTop, float yBottom, float t, Style face, Style side)
        {
            float h = Mathf.Max(yTop - yBottom, 1e-4f);
            float rT = g.BellRadiusAt(yTop), rB = g.BellRadiusAt(yBottom);
            float weld = 0.5f * t, edge = 0.3f * Mathf.Min(t, h), leg = 1.4f * weld, inset = 0.12f * weld;
            pb.Move(new Vector2(g.BellRadiusAt(yTop + leg) - inset, yTop + leg));
            pb.Line(new Vector2(rT, yTop), face, weld);
            pb.Line(new Vector2(rT + t, yTop), face, edge);
            pb.Line(new Vector2(rB + t, yBottom), side, edge);
            pb.Line(new Vector2(rB, yBottom), face, weld);
            pb.Line(new Vector2(g.BellRadiusAt(yBottom - leg) - inset, yBottom - leg), face);
        }

        /// <summary>A hat-section stiffener on the bell wall, crown from yTop to yBottom standing t proud. Its brims
        /// (0.35 t thick) reach 0.2 of the crown width beyond it on each side, so the foot is 1.4x the crown: a weld
        /// fillet where each brim meets the tubes, a shallow brim, a fillet into the web, a rounded crown.</summary>
        static void HatBand(ProfileBuilder pb, EngineGeometry g, float yTop, float yBottom, float t, Style s)
        {
            float h = Mathf.Max(yTop - yBottom, 1e-4f);
            float foot = 0.2f * h, web = 0.05f * h, tb = 0.35f * t;
            float yA = yTop + foot, yB = yBottom - foot;
            float weld = 0.6f * tb, inner = 0.4f * tb, edge = 0.3f * Mathf.Min(t, h);
            float leg = 1.4f * weld, inset = 0.12f * weld;
            pb.Move(new Vector2(g.BellRadiusAt(yA + leg) - inset, yA + leg));
            pb.Line(new Vector2(g.BellRadiusAt(yA), yA), s, weld);
            pb.Line(new Vector2(g.BellRadiusAt(yTop + web) + tb, yTop + web), s, inner);
            pb.Line(new Vector2(g.BellRadiusAt(yTop) + t, yTop), s, edge);
            pb.Line(new Vector2(g.BellRadiusAt(yBottom) + t, yBottom), s, edge);
            pb.Line(new Vector2(g.BellRadiusAt(yBottom - web) + tb, yBottom - web), s, inner);
            pb.Line(new Vector2(g.BellRadiusAt(yB), yB), s, weld);
            pb.Line(new Vector2(g.BellRadiusAt(yB - leg) - inset, yB - leg), s);
        }

        // ------------------------------------------------------------------ contour sampling

        /// <summary>Centripetal Catmull-Rom through the points, `sub` samples per span, with unit tangents and a
        /// cumulative sampling cost: arc length plus turning angle, each normalised to 1.</summary>
        static void Densify(List<Vector2> raw, int sub, List<Vector2> dense, List<Vector2> tan, List<float> cost)
        {
            int n = raw.Count;
            if (n < 2) { dense.Add(raw[0]); tan.Add(new Vector2(0, -1)); cost.Add(0); return; }
            for (int i = 0; i < n - 1; i++)
            {
                Vector2 p0 = i > 0 ? raw[i - 1] : 2 * raw[0] - raw[1], p1 = raw[i], p2 = raw[i + 1], p3 = i + 2 < n ? raw[i + 2] : 2 * raw[n - 1] - raw[n - 2];
                float t0 = 0, t1 = t0 + Mathf.Max(Mathf.Sqrt((p1 - p0).magnitude), 1e-5f);
                float t2 = t1 + Mathf.Max(Mathf.Sqrt((p2 - p1).magnitude), 1e-5f), t3 = t2 + Mathf.Max(Mathf.Sqrt((p3 - p2).magnitude), 1e-5f);
                for (int k = 0; k < sub; k++)
                {
                    float t = t1 + (t2 - t1) * k / sub;
                    Vector2 a1 = (t1 - t) / (t1 - t0) * p0 + (t - t0) / (t1 - t0) * p1;
                    Vector2 a2 = (t2 - t) / (t2 - t1) * p1 + (t - t1) / (t2 - t1) * p2;
                    Vector2 a3 = (t3 - t) / (t3 - t2) * p2 + (t - t2) / (t3 - t2) * p3;
                    Vector2 b1 = (t2 - t) / (t2 - t0) * a1 + (t - t0) / (t2 - t0) * a2;
                    Vector2 b2 = (t3 - t) / (t3 - t1) * a2 + (t - t1) / (t3 - t1) * a3;
                    dense.Add((t2 - t) / (t2 - t1) * b1 + (t - t1) / (t2 - t1) * b2);
                }
            }
            dense.Add(raw[n - 1]);
            int m = dense.Count;
            for (int i = 0; i < m; i++)
            {
                Vector2 tv = dense[Math.Min(i + 1, m - 1)] - dense[Math.Max(i - 1, 0)];
                tan.Add(tv.sqrMagnitude > 1e-14f ? tv.normalized : new Vector2(0, -1));
            }
            float len = 0, turn = 0;
            for (int i = 1; i < m; i++) { len += (dense[i] - dense[i - 1]).magnitude; turn += Vector2.Angle(tan[i - 1], tan[i]); }
            float acc = 0;
            cost.Add(0);
            for (int i = 1; i < m; i++)
            {
                acc += (dense[i] - dense[i - 1]).magnitude / Mathf.Max(len, 1e-6f) + (turn > 1e-3f ? Vector2.Angle(tan[i - 1], tan[i]) / turn : 0);
                cost.Add(acc);
            }
        }

        /// <summary>Sampling cost where the (monotonic) contour reaches radius r.</summary>
        static float CostAtRadius(List<Vector2> dense, List<float> cost, float r)
        {
            if (r <= dense[0].x) return 0;
            for (int i = 0; i < dense.Count - 1; i++)
                if (dense[i + 1].x >= r)
                {
                    float span = dense[i + 1].x - dense[i].x;
                    return Mathf.Lerp(cost[i], cost[i + 1], span > 1e-9f ? (r - dense[i].x) / span : 1);
                }
            return cost[cost.Count - 1];
        }

        /// <summary>`count` points evenly in cost between c0 and c1 (both included), with tangents if asked.</summary>
        static void Sample(List<Vector2> dense, List<Vector2> tan, List<float> cost, float c0, float c1, int count, List<Vector2> pts, List<Vector2> tans)
        {
            int j = 0, last = dense.Count - 2;
            count = Math.Max(count, 2);
            for (int k = 0; k < count; k++)
            {
                float c = k == count - 1 ? c1 : c0 + (c1 - c0) * k / (count - 1);
                while (j < last && cost[j + 1] < c) j++;
                float span = cost[j + 1] - cost[j];
                float f = span > 1e-9f ? Mathf.Clamp01((c - cost[j]) / span) : 0;
                pts.Add(Vector2.Lerp(dense[j], dense[j + 1], f));
                if (tans != null) tans.Add(Vector2.Lerp(tan[j], tan[j + 1], f).normalized);
            }
        }

        /// <summary>Sample the contour into the regen part (throat .. split) and the extension (split .. exit),
        /// sharing the split point; without a split everything goes to the regen part.</summary>
        static void SplitSample(List<Vector2> dense, List<Vector2> tan, List<float> cost, float cSplit, float total, int n, List<Vector2> a, List<Vector2> aTan, List<Vector2> b, List<Vector2> bTan)
        {
            if (cSplit >= total - 1e-6f) { Sample(dense, tan, cost, 0, total, n, a, aTan); return; }
            int na = Mathf.Clamp(Mathf.RoundToInt((n - 1) * cSplit / Mathf.Max(total, 1e-6f)) + 1, 4, Math.Max(n - 3, 4));
            Sample(dense, tan, cost, 0, cSplit, na, a, aTan);
            Sample(dense, tan, cost, cSplit, total, Math.Max(n - na + 1, 4), b, bTan);
        }

        /// <summary>Move points inward along the profile normal (the inner wall of the bell).</summary>
        static void Offset(List<Vector2> pts, List<Vector2> tans, float wall)
        {
            for (int i = 0; i < pts.Count; i++)
            {
                Vector2 t = tans[i], nrm = new Vector2(-t.y, t.x);
                Vector2 p = pts[i] - nrm * wall;
                pts[i] = new Vector2(Mathf.Max(p.x, 0.001f), p.y);
            }
        }

        /// <summary>Re-map V of segments that share one Style to a heat coordinate by height: vTop at yTop, vBottom at
        /// the last point of the last segment, linear in y in between. Each segment's V spans exactly its own share, so
        /// V runs on continuously across the runs a band interrupts and meets the next section's V where it ends.</summary>
        static void HeatByHeight(List<ProfileSegment> segs, float yTop, float vTop, float vBottom)
        {
            if (segs == null || segs.Count == 0) return;
            List<Vector2> last = segs[segs.Count - 1].Points;
            float yBottom = last[last.Count - 1].y, span = Mathf.Max(yTop - yBottom, 1e-4f);
            float V(float y) => Mathf.Lerp(vTop, vBottom, Mathf.Clamp01((yTop - y) / span));
            foreach (ProfileSegment s in segs)
            {
                float v0 = V(s.Points[0].y), v1 = V(s.Points[s.Points.Count - 1].y), arc = Mathf.Max(ArcLength(s.Points), 1e-4f);
                s.VOffset = v0;
                s.VLength = Mathf.Abs(v1 - v0) > 1e-4f ? arc / (v1 - v0) : span / (vBottom - vTop);
            }
        }

        static float ArcLength(List<Vector2> pts)
        {
            float a = 0;
            for (int i = 1; i < pts.Count; i++) a += (pts[i] - pts[i - 1]).magnitude;
            return a;
        }

        // ------------------------------------------------------------------ profile builder

        enum Uv { Steel, Bolts, Fixed, ArcScaled, EndAtZero }

        /// <summary>Material and UV mapping of a run of profile edges. Edges sharing one Style object (and joined by
        /// fillets or smooth points) become one smooth-shaded ProfileSegment.</summary>
        sealed class Style
        {
            public int Mat;
            public Uv Kind;
            public float U = 1, V = 1, VOffset;
            /// <summary>Where styles meet at a rounded corner, the fillet goes to the higher priority side.</summary>
            public int Priority = 1;
            /// <summary>When set, collects the segments made with this style (for a mapping fixed up after Emit).</summary>
            public List<ProfileSegment> Made;

            public ProfileSegment Make(List<Vector2> pts)
            {
                ProfileSegment s = MakeSegment(pts);
                Made?.Add(s);
                return s;
            }

            /// <summary>Physical tiling (ASSETS.md 5): EngineMaterials.SteelTile square, whole tiles around the
            /// segment's largest radius.</summary>
            public static Style Steel(int mat) => new Style { Mat = mat, Kind = Uv.Steel };
            /// <summary>One bolt head per U repeat, ~1.6x as wide as the straight rim is high; V 0 at the top, 1 at the bottom.</summary>
            public static Style Bolts(int mat) => new Style { Mat = mat, Kind = Uv.Bolts, Priority = 0 };

            ProfileSegment MakeSegment(List<Vector2> pts)
            {
                float rmax = 0;
                for (int i = 0; i < pts.Count; i++) rmax = Mathf.Max(rmax, pts[i].x);
                switch (Kind)
                {
                    case Uv.Steel:
                        return new ProfileSegment(pts, Mat, Mathf.Max(1, Mathf.Round(2 * Mathf.PI * rmax / EngineMaterials.SteelTile)), EngineMaterials.SteelTile, VOffset);
                    case Uv.Bolts:
                    {
                        float h = Mathf.Max(ArcLength(pts), 1e-3f);
                        return new ProfileSegment(pts, Mat, Mathf.Clamp(Mathf.Round(2 * Mathf.PI * rmax / (1.6f * h)), 8, 180), h);
                    }
                    case Uv.ArcScaled:
                        return new ProfileSegment(pts, Mat, U, V * Mathf.Max(ArcLength(pts), 1e-3f), VOffset);
                    case Uv.EndAtZero:
                        return new ProfileSegment(pts, Mat, U, V, ArcLength(pts) / Mathf.Max(Mathf.Abs(V), 1e-3f));
                    default:
                        return new ProfileSegment(pts, Mat, U, V, VOffset);
                }
            }
        }

        /// <summary>
        /// Polylines of control points (r, y) with a radius per corner: &gt; 0 rounds the corner with a circular
        /// fillet of that radius (clamped to the edges), 0 keeps it sharp (a crease when it turns more than 25
        /// degrees), &lt; 0 marks a point of a smooth curve. Edges carry a Style; a change of style splits the
        /// segment. Fillet tangent points depend only on radii and angles, not on the step count, so every LOD has
        /// its straight runs (and bolt rims) in the same places.
        /// </summary>
        sealed class ProfileBuilder
        {
            const float CreaseDeg = 25f;
            readonly Detail det;
            readonly List<List<Vector2>> pts = new List<List<Vector2>>();
            readonly List<List<float>> radii = new List<List<float>>();
            readonly List<List<Style>> styles = new List<List<Style>>();
            List<Vector2> cp;
            List<float> cr;
            List<Style> cs;

            public ProfileBuilder(Detail d) { det = d; }

            public void Move(Vector2 p)
            {
                cp = new List<Vector2>(64) { p }; cr = new List<float>(64) { 0 }; cs = new List<Style>(64);
                pts.Add(cp); radii.Add(cr); styles.Add(cs);
            }

            /// <summary>Edge to p in style s; `radius` is the corner at p (see the class summary).</summary>
            public void Line(Vector2 p, Style s, float radius = 0)
            {
                if ((p - cp[cp.Count - 1]).sqrMagnitude < 1e-14f) { cr[cr.Count - 1] = radius; return; }
                cp.Add(p); cr.Add(radius); cs.Add(s);
            }

            public void Emit(List<ProfileSegment> into)
            {
                for (int k = 0; k < pts.Count; k++) EmitPath(pts[k], radii[k], styles[k], into);
            }

            void EmitPath(List<Vector2> P, List<float> R, List<Style> S, List<ProfileSegment> into)
            {
                int n = P.Count;
                if (n < 2) return;
                var dir = new Vector2[n - 1];
                var len = new float[n - 1];
                for (int i = 0; i < n - 1; i++) { Vector2 e = P[i + 1] - P[i]; len[i] = e.magnitude; dir[i] = len[i] > 0 ? e / len[i] : new Vector2(0, -1); }
                // requested tangent lengths, then clamped so neighbouring fillets share an edge
                var want = new float[n];
                var phi = new float[n];
                var cross = new float[n];
                for (int i = 1; i < n - 1; i++)
                {
                    cross[i] = dir[i - 1].x * dir[i].y - dir[i - 1].y * dir[i].x;
                    phi[i] = Mathf.Atan2(Mathf.Abs(cross[i]), Vector2.Dot(dir[i - 1], dir[i]));
                    if (R[i] > 0 && phi[i] > 1e-3f && phi[i] < Mathf.PI - 1e-3f) want[i] = R[i] * Mathf.Tan(phi[i] * 0.5f);
                }
                var tl = new float[n];
                for (int i = 1; i < n - 1; i++)
                {
                    if (want[i] <= 0) continue;
                    float inAvail = len[i - 1] * (i - 1 > 0 && want[i - 1] > 0 ? 0.49f : 0.95f);
                    float outAvail = len[i] * (i + 1 < n - 1 && want[i + 1] > 0 ? 0.49f : 0.95f);
                    tl[i] = Mathf.Min(want[i], Mathf.Min(inAvail, outAvail));
                }

                var seg = new List<Vector2>(32) { P[0] };
                for (int i = 1; i < n; i++)
                {
                    Style sIn = S[i - 1];
                    if (i == n - 1) { seg.Add(P[i]); Flush(seg, sIn, into); break; }
                    Style sOut = S[i];
                    bool change = !ReferenceEquals(sIn, sOut);
                    if (tl[i] <= 1e-6f)
                    {
                        seg.Add(P[i]);
                        if (change || (R[i] >= 0 && phi[i] > CreaseDeg * Mathf.Deg2Rad)) { Flush(seg, sIn, into); seg = new List<Vector2>(32) { P[i] }; }
                        continue;
                    }
                    Vector2 dIn = dir[i - 1], dOut = dir[i];
                    float t = tl[i], rEff = t / Mathf.Tan(phi[i] * 0.5f);
                    Vector2 T0 = P[i] - dIn * t, T1 = P[i] + dOut * t;
                    // straight run left on each side, for the support points
                    float restIn = len[i - 1] - t - (i - 1 > 0 ? tl[i - 1] : 0), restOut = len[i] - t - (i + 1 < n - 1 ? tl[i + 1] : 0);
                    float epsIn = Mathf.Min(0.15f * t, 0.3f * restIn), epsOut = Mathf.Min(0.15f * t, 0.3f * restOut);
                    bool toOut = change && sOut.Priority > sIn.Priority;
                    if (change && toOut)
                    {
                        Vector2 s0 = T0 - dIn * epsIn;
                        seg.Add(s0);
                        Flush(seg, sIn, into);
                        seg = new List<Vector2>(32) { s0 };
                    }
                    else if (det.Supports && restIn > 2 * t && epsIn > 1e-6f) seg.Add(T0 - dIn * epsIn);
                    // every profile here runs with the surface on its outward (counter-clockwise normal) side, so a
                    // counter-clockwise turn is an inside corner: a weld fillet
                    bool ccw = cross[i] > 0;
                    int steps = Math.Max(1, ccw ? det.Concave : det.Convex);
                    seg.Add(T0);
                    if (steps > 1)
                    {
                        Vector2 nTurn = ccw ? new Vector2(-dIn.y, dIn.x) : new Vector2(dIn.y, -dIn.x);
                        Vector2 c = T0 + nTurn * rEff, a = T0 - c;
                        float sweep = (ccw ? 1 : -1) * phi[i];
                        for (int k = 1; k < steps; k++)
                        {
                            float ang = sweep * k / steps, ca = Mathf.Cos(ang), sa = Mathf.Sin(ang);
                            seg.Add(c + new Vector2(a.x * ca - a.y * sa, a.x * sa + a.y * ca));
                        }
                    }
                    seg.Add(T1);
                    if (change && !toOut)
                    {
                        Vector2 s1 = T1 + dOut * epsOut;
                        seg.Add(s1);
                        Flush(seg, sIn, into);
                        seg = new List<Vector2>(32) { s1 };
                    }
                    else if (det.Supports && restOut > 2 * t && epsOut > 1e-6f) seg.Add(T1 + dOut * epsOut);
                }
            }

            static void Flush(List<Vector2> seg, Style s, List<ProfileSegment> into)
            {
                // drop coincident points (they would make zero-length lathe rows)
                for (int i = seg.Count - 1; i > 0; i--)
                    if ((seg[i] - seg[i - 1]).sqrMagnitude < 1e-14f) seg.RemoveAt(i);
                if (seg.Count >= 2) into.Add(s.Make(seg));
            }
        }

        // ------------------------------------------------------------------ mesh

        /// <summary>Revolve profile segments around Y into one mesh, one submesh per material (MeshBuilder.Lathe), in a
        /// pooled builder (slot 2 for the static mesh, 3 for the bell; main thread only, Build copies the data).</summary>
        public static void Lathe(Mesh mesh, List<ProfileSegment> segments, int materialCount, int ringSegments, float yOffset)
        {
            int rows = 0;
            for (int i = 0; i < segments.Count; i++) rows += segments[i].Points.Count;
            MeshBuilder mb = MeshBuilder.Pooled(materialCount, materialCount == BellMaterials ? 3 : 2, rows * (ringSegments + 1));
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
