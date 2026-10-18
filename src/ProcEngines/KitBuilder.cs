using System;
using System.Collections.Generic;
using ProcEngines.Model;
using UnityEngine;

namespace ProcEngines
{
    /// <summary>
    /// Kit-bashed turbomachinery, plumbing and structure per cycle (DESIGN.md 8.5), from authored pieces and primitives
    /// (visual only, no colliders). Pieces scale with the turbopump / pressurant mass and the propellant flow; positions
    /// avoid the chamber and the bell, and everything stays below the mount plate (y &lt; 0), so nothing pokes into the
    /// part stacked on top. Lines that feed from the stage above end just under y = 0 (the tank interface).
    /// The regen coolant manifold is part of the gimballed bell (EngineGeometry); the fuel line ends at it.
    ///
    /// Gimbal: the bell swings about the throat (up to <see cref="GimbalClearanceDeg"/>). What is fixed to it (the
    /// actuator lugs and pins, the fuel line below its bellows, the expander hot line up to its bellows, the brackets
    /// holding them) is built in a second mesh in the bell's frame (<see cref="Build(Mesh, Mesh, EngineDesign,
    /// EngineStats, EngineGeometry, ModelData, int)"/>); everything else is static and keeps out of the volume the bell
    /// sweeps, with a small gap. The lines cross from the static kit onto the bell at flanged joints under their
    /// bellows, where the bell's motion is small. The gimbal actuators span the two: given meshes for them, each
    /// becomes a barrel on its clevis and a rod on the bell lug that slides in it (<see cref="ActuatorPose"/>), which
    /// ModuleProcEngine aims at the gimballed eye every frame.
    ///
    /// Proportions follow the real hardware: fat gimbal actuators just outside the chamber on outriggers, landing on
    /// lugs on the regen bell; pump-suction ducts sized for 15 m/s and high-pressure discharge lines for 30 m/s with
    /// transition cones at the volutes; main valves bolted coaxially into straight runs (the oxidizer valve onto the
    /// dome inlet elbow, the fuel valve in the drop to the manifold); pumps braced to the chamber; P-clamps,
    /// brackets and instrumentation cables.
    ///
    /// Levels of detail (ASSETS.md 6): LOD0 uses the full authored pieces and primitives with many sides; LOD1 the
    /// `.lod1.pem` pieces and about half the sides (tubes under 10 sides, so MeshBuilder drops their flange bolt
    /// heads and halves the bellows rings) and leaves out what is too thin to see there (cables, P-clamp bands).
    /// Every placement decision (authored or primitive, anchors, stretch lengths, valve and inlet positions, fits
    /// under the mount plate, gimbal clearance) is taken from the LOD0 piece and the full-resolution bell contour, so
    /// the two levels line up exactly.
    /// </summary>
    public static class KitBuilder
    {
        public const int MatStructure = 0, MatMachine = 1, MatLines = 2, MatHot = 3, MatFoil = 4, MatAuthored = 5, Materials = 6;

        /// <summary>Highest point of anything that feeds from the stage above: just under the stack interface (y = 0).</summary>
        const float YTank = -0.004f;

        /// <summary>Gimbal deflection the static kit is laid out for (ModuleProcEngine clamps the range to this).</summary>
        public const float GimbalClearanceDeg = 10f;

        /// <summary>Where a turbopump's lines attach (part frame): positions and outward directions.</summary>
        sealed class PumpPorts
        {
            public Vector3 OxOut, OxDir, FuelOut, FuelDir, TurbineIn, TurbineInDir, TurbineExhaust, TurbineExhaustDir, Inlet, FuelIn, FuelInDir;
            public bool HasInlet, HasFuelIn, Authored, Flipped;
            public float Angle, R, CentreRadius, OxPortR, FuelPortR;
            public Vector3 Centre;
            /// <summary>Tangential side (+1 / -1, toward increasing / decreasing angle) the main discharge leaves on.</summary>
            public float MainSide;
            /// <summary>Brace struts to the chamber: pump end and chamber end of each.</summary>
            public readonly Vector3[] StrutPump = new Vector3[2], StrutChamber = new Vector3[2];
            public float StrutR;
        }

        public sealed class Result
        {
            public bool HasExhaust;
            public Vector3 ExhaustPosition;   // part frame, GG turbine exhaust nozzle exit
            public float ExhaustRadius;
            /// <summary>Thrust-frame struts placed (4, or 3 when the dome is too crowded; for tests).</summary>
            public int ThrustStruts;
            /// <summary>The gimbal actuators at rest (empty when they were drawn into the static kit).</summary>
            public ActuatorPose[] Actuators = new ActuatorPose[0];
            /// <summary>Barrel radius (m).</summary>
            public float ActuatorRadius;
            /// <summary>Along the actuator axis: from the clevis pin to where the rod leaves the gland, and from the
            /// rod-eye pin up to the top of the rod (hidden in the barrel), in metres. The rod is still in the barrel
            /// while length - RodLength &lt; Gland.</summary>
            public float ActuatorGland, ActuatorRodLength;
        }

        /// <summary>
        /// A gimbal actuator at rest, part frame: the top clevis pin centre (fixed to the thrust frame), the rod-eye
        /// pin centre (on the bell lug, so it gimbals with the bell), the direction its servo block faces (outward,
        /// square to the axis) and the pin axis of both clevises. Its barrel mesh (KitBuilder's actuator body) hangs
        /// from the clevis in the frame (X = Out, Y = up the actuator, Z = Pin); its rod mesh has the same axes with the
        /// rod-eye pin at the origin. ModuleProcEngine aims both at the gimballed eye every frame.
        /// </summary>
        public struct ActuatorPose
        {
            public Vector3 Top, Eye, Out, Pin;
        }

        static Vector3 Around(float deg, float radius, float y) => new Vector3(radius * Mathf.Cos(deg * Mathf.Deg2Rad), y, radius * Mathf.Sin(deg * Mathf.Deg2Rad));

        static float Horizontal(Vector3 p) => Mathf.Sqrt(p.x * p.x + p.z * p.z);

        /// <summary>
        /// Propellant line sizes: constant flow velocity, d ~ sqrt(mdot / rho), at most `capFrac` of the chamber radius
        /// (+1 cm). The defaults (15 m/s, 0.25) are the low-pressure size: pump suction ducts, tank feeds and the regen
        /// coolant manifold. High-pressure discharge lines run at about 30 m/s (KitBuilder uses 30 m/s, 0.18).
        /// </summary>
        public static void LineRadii(EngineDesign d, EngineStats s, ModelData data, out float fuelR, out float oxR, float velocity = 15f, float capFrac = 0.25f)
        {
            float rc = (float)s.ChamberRadius;
            double of = s.Of;
            ProcEngines.Model.Propellant prop = null;
            data?.Propellants.TryGetValue(d.Propellant, out prop);
            double fuelDensity = prop?.FuelDensity ?? 800, oxDensity = prop?.OxDensity ?? 1141;
            double mdotFuel = s.MassFlow / (1 + of), mdotOx = s.MassFlow - mdotFuel;
            double v = Math.Max(velocity, 0.1);
            float LineR(double mdot, double rho) => Mathf.Clamp(Mathf.Sqrt((float)(mdot / (rho * v) / Math.PI)), 0.006f, capFrac * rc + 0.01f);
            fuelR = LineR(mdotFuel, fuelDensity);
            oxR = LineR(mdotOx, oxDensity);
        }

        /// <summary>The kit in one mesh, as before the gimballed kit existed (bell-attached pieces included, part frame).</summary>
        public static Result Build(Mesh mesh, EngineDesign d, EngineStats s, EngineGeometry g, ModelData data, int lod = 0) =>
            Build(mesh, null, d, s, g, data, lod);

        /// <summary>
        /// Builds the static kit into `mesh` (part frame) and what is rigidly fixed to the gimballed bell (actuator lugs
        /// and pins, the fuel line below its bellows, the expander hot line up to its bellows) into `bellMesh`, in the
        /// BELL frame: part frame minus (0, YThroat, 0), the frame of EngineGeometry.Lathe(bell, ..., g.YThroat). With
        /// `bellMesh` null that geometry goes into `mesh`. Result positions are in the part frame.
        /// </summary>
        public static Result Build(Mesh mesh, Mesh bellMesh, EngineDesign d, EngineStats s, EngineGeometry g, ModelData data, int lod = 0) =>
            Build(mesh, bellMesh, null, null, d, s, g, data, lod);

        /// <summary>
        /// As above, and with `actBody` and `actRod` given the gimbal actuators are not in the static kit: one barrel
        /// (`actBody`, in the clevis frame) and one rod (`actRod`, in the rod-eye frame) that every actuator of this
        /// engine shares, placed per actuator from Result.Actuators (<see cref="ActuatorPose"/>).
        /// </summary>
        public static Result Build(Mesh mesh, Mesh bellMesh, Mesh actBody, Mesh actRod, EngineDesign d, EngineStats s, EngineGeometry g, ModelData data, int lod = 0)
        {
            // pooled builders (ASSETS round 2, D7): slot 0 = LOD0, 1 = LOD1, 4 / 5 = their bell kits, 6 / 7 the LOD0
            // actuator barrel and rod, 8 / 9 at LOD1; Build copies the data, so reuse is safe. Modest pre-sizes, a
            // quarter of a big kit: the buffers double to the largest kit built (MeshBuilder.Reserve), landing at
            // 147k / 49k vertices for the biggest test kits (142k / 47k).
            MeshBuilder mb = MeshBuilder.Pooled(Materials, lod <= 0 ? 0 : 1, lod <= 0 ? 36864 : 12288);
            MeshBuilder mbBell = bellMesh != null ? MeshBuilder.Pooled(Materials, lod <= 0 ? 4 : 5, lod <= 0 ? 6144 : 2048) : null;
            bool act = actBody != null && actRod != null;
            MeshBuilder mbBody = act ? MeshBuilder.Pooled(Materials, lod <= 0 ? 6 : 8, lod <= 0 ? 4096 : 1024) : null;
            MeshBuilder mbRod = act ? MeshBuilder.Pooled(Materials, lod <= 0 ? 7 : 9, lod <= 0 ? 1024 : 512) : null;
            Result res = Fill(mb, mbBell, mbBody, mbRod, d, s, g, data, lod);
            mb.Build(mesh);
            if (bellMesh != null) mbBell.Build(bellMesh);
            if (act) { mbBody.Build(actBody); mbRod.Build(actRod); }
            return res;
        }

        // ------------------------------------------------------------------ helpers

        /// <summary>Highest world y of an authored piece placed with this basis and scale (no stretch).</summary>
        static float TopY(KitMesh m, Vector3 origin, Vector3 X, Vector3 Y, Vector3 Z, float scale)
        {
            Vector3[] P = m.Positions;
            float best = float.NegativeInfinity;
            for (int i = 0; i < P.Length; i++)
            {
                float y = X.y * P[i].x + Y.y * P[i].y + Z.y * P[i].z;
                if (y > best) best = y;
            }
            return origin.y + best * scale;
        }

        static float TopY(KitMesh m) { float b = float.NegativeInfinity; foreach (Vector3 p in m.Positions) b = Mathf.Max(b, p.y); return b; }

        static readonly List<Vector3> pathDense = new List<Vector3>(256);
        static readonly List<float> pathCum = new List<float>(256);

        /// <summary>Point and unit tangent at fraction `frac` of the arc length of the path MeshBuilder.Tube draws
        /// through `pts` (the same centripetal Catmull-Rom samples, MeshBuilder.SplinePoint). Main thread only.</summary>
        static Vector3 PathPoint(IList<Vector3> pts, float frac, out Vector3 tangent)
        {
            const int perSpan = MeshBuilder.SplineSamples;
            pathDense.Clear(); pathCum.Clear();
            int n = pts.Count;
            for (int i = 0; i < n - 1; i++)
                for (int k = 0; k < perSpan; k++)
                    pathDense.Add(MeshBuilder.SplinePoint(pts, i, k / (float)perSpan));
            pathDense.Add(pts[n - 1]);
            pathCum.Add(0);
            for (int i = 1; i < pathDense.Count; i++) pathCum.Add(pathCum[i - 1] + (pathDense[i] - pathDense[i - 1]).magnitude);
            float target = Mathf.Clamp01(frac) * pathCum[pathCum.Count - 1];
            int j = 0;
            while (j < pathDense.Count - 2 && pathCum[j + 1] < target) j++;
            float span = pathCum[j + 1] - pathCum[j];
            float f = span > 1e-9f ? Mathf.Clamp01((target - pathCum[j]) / span) : 0;
            Vector3 d = pathDense[j + 1] - pathDense[j];
            tangent = d.sqrMagnitude > 1e-14f ? d.normalized : Vector3.down;
            return Vector3.Lerp(pathDense[j], pathDense[j + 1], f);
        }

        /// <summary>
        /// Control points for a line of straight legs through `corners`, bent at the inner corners with a radius of
        /// about `bend` (at most 0.45 of a leg). Each long leg gets guard points `bend` in from both ends, so between
        /// them the spline has collinear neighbours on both sides and comes out exactly straight: a long run next to a
        /// short one can't bow out (MeshBuilder.SplinePoint).
        /// </summary>
        static List<Vector3> Legs(IList<Vector3> corners, float bend)
        {
            var o = new List<Vector3>(corners.Count * 4);
            int n = corners.Count;
            for (int i = 0; i < n - 1; i++)
            {
                Vector3 a = corners[i], b = corners[i + 1], u = b - a;
                float L = u.magnitude;
                if (L < 1e-6f) continue;
                u /= L;
                float ba = i == 0 ? 0 : Mathf.Min(bend, 0.45f * L), bb = i == n - 2 ? 0 : Mathf.Min(bend, 0.45f * L);
                Vector3 s = a + u * ba, e = b - u * bb;
                float inner = L - ba - bb, gd = Mathf.Min(bend, 0.25f * inner);
                if (o.Count == 0 || (o[o.Count - 1] - s).sqrMagnitude > 1e-12f) o.Add(s);
                if (gd > 1e-5f && inner > 3f * gd) { o.Add(s + u * gd); o.Add(e - u * gd); }
                o.Add(e);
            }
            if (o.Count < 2) { o.Clear(); o.Add(corners[0]); o.Add(corners[n - 1]); }
            return o;
        }

        /// <summary>Length of the centre line MeshBuilder.Tube draws through `pts` (the same samples).</summary>
        static float PathLength(IList<Vector3> pts)
        {
            const int perSpan = MeshBuilder.SplineSamples;
            float len = 0;
            Vector3 prev = pts[0];
            for (int i = 0; i < pts.Count - 1; i++)
                for (int k = 1; k <= perSpan; k++)
                {
                    Vector3 q = k == perSpan ? pts[i + 1] : MeshBuilder.SplinePoint(pts, i, k / (float)perSpan);
                    len += (q - prev).magnitude;
                    prev = q;
                }
            return len;
        }

        /// <summary>
        /// The volume the gimballed bell sweeps, as the outermost radius per height (bins of `dy` from `y0` up): the
        /// bell's outer wall (padded for its hat bands, joint ring and lip ring) and the coolant manifold, over every
        /// deflection up to GimbalClearanceDeg about the throat. A point's polar angle about the pivot changes by at
        /// most the deflection, and the bell is a solid of revolution, so turning its profile in its own plane by
        /// -max .. +max covers every gimbal direction. Built from the full-resolution contour (the same at every LOD).
        /// </summary>
        static float[] SweptTable(EngineGeometry g, out float y0, out float dy)
        {
            const int bins = 400;
            List<Vector2> bo = g.BellOuter;
            float rt = g.ThroatRadius, exitR = bo.Count > 0 ? bo[bo.Count - 1].x : rt;
            float pad = Mathf.Max(0.05f * rt + 0.0035f, Mathf.Max(0.025f * g.RegenEndRadius + 0.004f, 0.015f * exitR + 0.002f));
            float sMax = Mathf.Sin(GimbalClearanceDeg * Mathf.Deg2Rad);
            float yTop = g.YThroat + (rt + pad) * sMax + 0.01f;
            float yBot = Mathf.Min(g.YExit, bo.Count > 0 ? bo[bo.Count - 1].y : g.YExit) - (exitR + pad) * sMax - 0.01f;
            y0 = yBot;
            dy = Mathf.Max((yTop - yBot) / bins, 1e-4f);
            var table = new float[bins];
            if (bo.Count < 2) return table;
            // profiles relative to the pivot: the padded outer wall, and the manifold as a closed loop
            int nm = 16, nb = bo.Count;
            var pr = new Vector2[nb + nm + 1];
            for (int i = 0; i < nb; i++) pr[i] = new Vector2(bo[i].x + pad, bo[i].y - g.YThroat);
            float mr = g.ManifoldTubeRadius + 0.5f * pad;
            for (int i = 0; i <= nm; i++)
            {
                float th = 2 * Mathf.PI * i / nm;
                pr[nb + i] = new Vector2(g.ManifoldRadius + mr * Mathf.Cos(th), g.ManifoldY - g.YThroat + mr * Mathf.Sin(th));
            }
            float yb0 = y0, step = dy;
            void Raster(Vector2 a, Vector2 b)
            {
                float lo = Mathf.Min(a.y, b.y), hi = Mathf.Max(a.y, b.y);
                int i0 = Mathf.Clamp(Mathf.FloorToInt((lo - yb0) / step), 0, bins - 1), i1 = Mathf.Clamp(Mathf.FloorToInt((hi - yb0) / step), 0, bins - 1);
                if (hi < yb0 || lo > yb0 + bins * step) return;
                for (int i = i0; i <= i1; i++)
                {
                    // the segment's largest radius inside this bin (it is linear, so at a clamped end)
                    float c0 = Mathf.Max(lo, yb0 + i * step), c1 = Mathf.Min(hi, yb0 + (i + 1) * step);
                    float r0, r1;
                    if (hi - lo < 1e-7f) { r0 = a.x; r1 = b.x; }
                    else
                    {
                        r0 = Mathf.Lerp(a.x, b.x, (c0 - a.y) / (b.y - a.y));
                        r1 = Mathf.Lerp(a.x, b.x, (c1 - a.y) / (b.y - a.y));
                    }
                    float r = Mathf.Max(r0, r1);
                    if (r > table[i]) table[i] = r;
                }
            }
            int steps = Mathf.CeilToInt(GimbalClearanceDeg);   // 1-degree steps, -max .. +max
            for (int k = -steps; k <= steps; k++)
            {
                float th = GimbalClearanceDeg * Mathf.Deg2Rad * k / steps, c = Mathf.Cos(th), s = Mathf.Sin(th);
                Vector2 Rot(Vector2 p) => new Vector2(p.x * c - p.y * s, p.x * s + p.y * c + g.YThroat);
                Vector2 prev = Rot(pr[0]);
                for (int i = 1; i < nb; i++) { Vector2 q = Rot(pr[i]); Raster(prev, q); prev = q; }
                prev = Rot(pr[nb]);
                for (int i = nb + 1; i <= nb + nm; i++) { Vector2 q = Rot(pr[i]); Raster(prev, q); prev = q; }
            }
            return table;
        }

        // ------------------------------------------------------------------ the kit

        /// <summary>Adds the kit for one level of detail to `mb` (Build without the Unity mesh, for tools), bell-attached
        /// pieces included (part frame).</summary>
        public static Result Fill(MeshBuilder mb, EngineDesign d, EngineStats s, EngineGeometry g, ModelData data, int lod = 0) =>
            Fill(mb, null, d, s, g, data, lod);

        /// <summary>Adds the kit for one level of detail: static pieces to `mb` (part frame), bell-attached ones to
        /// `bellMb` in the bell frame (part frame minus (0, YThroat, 0)), or to `mb` when `bellMb` is null.</summary>
        public static Result Fill(MeshBuilder mb, MeshBuilder bellMb, EngineDesign d, EngineStats s, EngineGeometry g, ModelData data, int lod = 0) =>
            Fill(mb, bellMb, null, null, d, s, g, data, lod);

        /// <summary>As above; with `bodyMb` and `rodMb` the gimbal actuators go there (one barrel and one rod in their
        /// own frames, see <see cref="ActuatorPose"/>) instead of into the static kit.</summary>
        public static Result Fill(MeshBuilder mb, MeshBuilder bellMb, MeshBuilder bodyMb, MeshBuilder rodMb, EngineDesign d, EngineStats s, EngineGeometry g, ModelData data, int lod = 0)
        {
            int bellStart = bellMb != null ? bellMb.VertexCount : 0;
            Result res = FillParts(mb, bellMb ?? mb, bodyMb != null && rodMb != null ? bodyMb : null, rodMb, d, s, g, data, lod);
            if (bellMb != null) bellMb.Translate(new Vector3(0, -g.YThroat, 0), bellStart);
            return res;
        }

        static readonly Dictionary<KitMesh, KitMesh[]> actuatorSplit = new Dictionary<KitMesh, KitMesh[]>();

        /// <summary>
        /// The authored actuator cut into its barrel (clevis, servo block, barrel, gland and the wiper fillet that
        /// closes onto the rod) and its rod (chrome rod, lock nut, rod-end eye), cached per loaded piece. The rod is
        /// what lies below the top of the rod stretch zone (zone1_hi + 0.25) within 2 % of the rod radius, plus
        /// everything below zone1_lo - 0.05; the rod radius is the smallest radius above 0.1 between zone1_lo and
        /// zone1_hi + 0.2 (0.42 barrel radii). Both levels of the shipped piece cut cleanly at the ring where the
        /// fillet meets the rod (y -7.11). Null when the piece has no rod zone or the cut leaves either half empty.
        /// </summary>
        static KitMesh[] SplitActuator(KitMesh m)
        {
            if (m == null) return null;
            if (actuatorSplit.TryGetValue(m, out KitMesh[] parts)) return parts;
            parts = null;
            if (m.TryAnchor("zone1_hi", out Vector3 hi, out _) && m.TryAnchor("zone1_lo", out Vector3 lo, out _) && m.TryAnchor("end", out _, out _))
            {
                float rodR = float.MaxValue;
                foreach (Vector3 p in m.Positions)
                {
                    float r = Mathf.Sqrt(p.x * p.x + p.z * p.z);
                    if (p.y >= lo.y && p.y <= hi.y + 0.2f && r > 0.1f && r < rodR) rodR = r;
                }
                if (rodR < 1f)
                {
                    float rodMax = 1.02f * rodR, yTop = hi.y + 0.25f, yEye = lo.y - 0.05f;
                    bool Rod(Vector3 p) => p.y < yEye || (p.y <= yTop && p.x * p.x + p.z * p.z <= rodMax * rodMax);
                    KitMesh body = m.Subset(Rod, false, ".body"), rod = m.Subset(Rod, true, ".rod");
                    if (body != null && rod != null) parts = new[] { body, rod };
                }
            }
            actuatorSplit[m] = parts;
            return parts;
        }

        static float ZoneLength(KitMesh m, int zone) =>
            m.TryAnchor("zone" + zone + "_hi", out Vector3 hi, out _) && m.TryAnchor("zone" + zone + "_lo", out Vector3 lo, out _) ? Mathf.Max(hi.y - lo.y, 0) : 0;

        /// <summary>
        /// One gimbal actuator as two meshes that slide on each other: the barrel in the clevis frame (clevis pin at
        /// the origin, the axis down -Y, the servo block toward +X, the pin along Z) and the rod in the rod-eye frame
        /// (eye pin at the origin, the rod up +Y). At the rest `length` (m) they line up exactly as the single
        /// stretched piece did: the barrel zone (zone0) and the rod zone (zone1) share the stretch in proportion to
        /// their lengths, and a length that would fold them scales the piece along its axis instead (as AddMesh).
        /// The rod is longer than that by a reserve hidden in the barrel: the eye, `eyeArm` from the gimbal pivot,
        /// moves at most 2 eyeArm sin(max / 2) when the bell swings GimbalClearanceDeg, which bounds how much longer
        /// the actuator gets, plus 0.35 barrel radii, so the rod never leaves the gland.
        /// `parts` are this level's (barrel, rod) halves and `parts0` the LOD0 piece's, which set the stations;
        /// null draws primitives.
        /// </summary>
        static void BuildActuator(MeshBuilder mbBody, MeshBuilder mbRod, KitMesh[] parts, KitMesh[] parts0, float length, float ra, float eyeArm, Result res, Func<int, int> S)
        {
            float reserve = 2 * eyeArm * Mathf.Sin(0.5f * GimbalClearanceDeg * Mathf.Deg2Rad) + 0.35f * ra;
            if (parts != null && parts0 != null && parts0[1].TryAnchor("end", out Vector3 end, out _))
            {
                float z0 = ZoneLength(parts0[0], 0), z1 = ZoneLength(parts0[0], 1), endL = Mathf.Max(-end.y, 1e-3f);
                float L = length / ra, D = L - endL, k = 1, d0 = 0, d1 = 0;
                if (z0 + z1 <= 0 || D < -0.7f * (z0 + z1)) k = L / endL;
                else { d0 = D * z0 / (z0 + z1); d1 = D * z1 / (z0 + z1); }
                float e = reserve / (ra * k);   // the reserve in piece units, before the axial scale
                float rodTop = float.NegativeInfinity;
                foreach (Vector3 p in parts0[1].Positions) rodTop = Mathf.Max(rodTop, p.y);
                Vector3 Y = Vector3.up * k;
                mbBody.AddMesh(parts[0], Vector3.zero, Vector3.right, Y, Vector3.forward, ra, MatAuthored, new[] { d0, d1 });
                // the rod: zone0 unstretched (the rod lies below it), zone1 grown by the reserve, the eye moved to the origin
                float eyeY = end.y - d1 - e;
                mbRod.AddMesh(parts[1], new Vector3(0, -eyeY * k * ra, 0), Vector3.right, Y, Vector3.forward, ra, MatAuthored, new[] { 0f, d1 + e });
                res.ActuatorGland = (d0 - rodTop) * k * ra;
                res.ActuatorRodLength = (rodTop - eyeY) * k * ra;
                return;
            }
            // primitives: clevis ball, barrel and gland on the clevis; rod and rod-end ball on the eye
            float lb = 0.55f * length;
            mbBody.Sphere(new Vector3(0, -0.6f * ra, 0), ra * 1.1f, MatStructure, S(20));
            mbBody.Cylinder(new Vector3(0, -0.6f * ra, 0), new Vector3(0, -lb, 0), ra, MatMachine, S(24));
            mbBody.Cylinder(new Vector3(0, -lb + 0.25f * ra, 0), new Vector3(0, -lb - 0.05f * ra, 0), ra * 1.18f, MatStructure, S(24));
            float rodLen = length - lb + reserve;
            mbRod.Cylinder(Vector3.zero, new Vector3(0, rodLen, 0), ra * 0.42f, MatLines, S(16));
            mbRod.Sphere(Vector3.zero, ra * 0.5f, MatStructure, S(20));
            res.ActuatorGland = lb + 0.05f * ra;
            res.ActuatorRodLength = rodLen;
        }

        /// <summary>The kit: static pieces into `mb`, bell-attached ones into `mbB`, both in the part frame; with
        /// `mbBody` non-null the actuator barrel and rod into `mbBody` / `mbRod` in their own frames.</summary>
        static Result FillParts(MeshBuilder mb, MeshBuilder mbB, MeshBuilder mbBody, MeshBuilder mbRod, EngineDesign d, EngineStats s, EngineGeometry g, ModelData data, int lod)
        {
            var res = new Result();
            bool full = lod <= 0;
            // primitive sides: the LOD0 value, about half at LOD1; tubes stay under 10 sides at LOD1
            int S(int sides0) => full ? sides0 : Math.Max(6, sides0 / 2);
            int T(int sides0) => full ? sides0 : Mathf.Clamp(sides0 / 2, 6, 8);
            // the LOD0 piece decides placement and anchors; the mesh drawn is this level's (when its file carries the
            // same anchors: the actuator's stretch zones are read from the drawn mesh)
            KitMesh Draw(KitMesh piece, string name)
            {
                if (piece == null || full) return piece;
                KitMesh m = KitAssets.GetLod(name, lod);
                return m != null && m.Anchors.Count >= piece.Anchors.Count ? m : piece;
            }
            float rc = g.ChamberRadius;
            double turboKg = s.MassBreakdown.TryGetValue("turbomachinery", out double tk) ? tk : 0;
            double pressKg = s.MassBreakdown.TryGetValue("pressurant", out double pk) ? pk : 0;
            float yUnder = -g.MountHeight;
            float bellLen = g.YThroat - g.YExit;
            // pump suction ducts and tank feeds: low pressure, 15 m/s; discharge lines: high pressure, 30 m/s
            LineRadii(d, s, data, out float fuelIn, out float oxIn);
            LineRadii(d, s, data, out float fuelR, out float oxR, 30f, 0.18f);
            // liquid hydrogen lines wear multi-layer insulation
            int fuelMat = d.Propellant == "Hydrolox" ? MatFoil : MatLines;
            bool staged = d.Cycle == "StagedCombustion" || d.Cycle == "FullFlowStaged";
            float ft = 0.06f * rc + 0.004f;   // injector flange overhang (EngineGeometry)
            float yMan = g.ManifoldY, manR = g.ManifoldRadius, mtr = g.ManifoldTubeRadius;
            // where a line of radius lr enters the coolant manifold from above: on its crown, a little outboard so the
            // line clears the bell wall it hugs
            float ManifoldEntry(float lr) => Mathf.Max(manR, g.BellRadiusAt(yMan + mtr) + 1.15f * lr);

            // ---------------------------------------------------------------- gimbal clearance
            // static pieces keep this far outside the volume the bell sweeps (SweptTable)
            float gap = 0.004f + 0.015f * rc;
            float[] swept = SweptTable(g, out float swY0, out float swDy);
            // outermost radius of the swept bell between two heights (0 where it never reaches)
            float SweptMax(float ya, float yb)
            {
                int i0 = Mathf.FloorToInt((Mathf.Min(ya, yb) - swY0) / swDy) - 1, i1 = Mathf.FloorToInt((Mathf.Max(ya, yb) - swY0) / swDy) + 1;
                float best = 0;
                for (int i = Math.Max(i0, 0); i <= i1 && i < swept.Length; i++) best = Mathf.Max(best, swept[i]);
                return best;
            }
            // highest height where the swept bell is at least r wide (-infinity if it never is)
            float SweptYAt(float r)
            {
                for (int i = swept.Length - 1; i >= 0; i--)
                    if (swept[i] >= r) return swY0 + (i + 0.5f) * swDy;
                return float.NegativeInfinity;
            }

            // outermost radius of the static chamber (bands and flanges included) between two heights
            float StaticR(float y0, float y1)
            {
                float lo = Mathf.Min(y0, y1), hi = Mathf.Max(y0, y1), best = 0;
                foreach (ProfileSegment ps in g.Static)
                {
                    List<Vector2> pts = ps.Points;
                    for (int i = 0; i < pts.Count; i++)
                    {
                        Vector2 p = pts[i];
                        if (p.y >= lo && p.y <= hi) best = Mathf.Max(best, p.x);
                        if (i == 0) continue;
                        Vector2 a = pts[i - 1];
                        if ((a.y - lo) * (p.y - lo) < 0) best = Mathf.Max(best, Mathf.Lerp(a.x, p.x, (lo - a.y) / (p.y - a.y)));
                        if ((a.y - hi) * (p.y - hi) < 0) best = Mathf.Max(best, Mathf.Lerp(a.x, p.x, (hi - a.y) / (p.y - a.y)));
                    }
                }
                return best;
            }
            // outward normal of the bell wall at height y, as (radial, up)
            Vector2 BellNormal(float y)
            {
                float dy = Mathf.Max(0.02f * rc, 0.002f);
                float slope = (g.BellRadiusAt(y - dy) - g.BellRadiusAt(y + dy)) / (2 * dy);   // dr per metre down
                return new Vector2(1, slope).normalized;
            }

            // ---------------------------------------------------------------- gimbal actuators (placement first: the
            // thrust frame and the plumbing keep clear of them)
            // Real barrels (Merlin, H-1, F-1) are a quarter to a third of the chamber diameter, standing nearly upright
            // just outside the chamber: the top clevis hangs from an outrigger under the stage above, clear of the
            // injector flange; the rod end lands on a lug on the regen bell, above the extension joint and the
            // manifold. Sized from its own length (L ~ 18 barrel radii), iterated since the stations depend on it.
            // The stations are laid out as if the actuator were static (its rod eye and rod outside the swept bell,
            // the lug cheeks on the bell reaching out to the eye); in the part it follows the bell (ActuatorPose). The
            // visual test checks it stays clear of the bell and the kit at full deflection in every direction.
            float[] actAngles = d.Cycle == "FullFlowStaged" ? new[] { 90f, 270f } : new[] { 135f, 225f };
            float regenLimit = g.YThroat - 0.6f * (g.YThroat - g.RegenEndY);
            float ra = 0.15f * rc, rTop = 0, yAct = 0, yBeam = 0, yPin = 0, rEye = 0;
            void ActStations()
            {
                rTop = rc + ft + 1.6f * ra;
                yAct = Mathf.Max(SweptYAt(rTop - 0.5f * ra), regenLimit);
                yBeam = YTank - 0.55f * ra;          // outrigger beam: its top at the stack interface
                yPin = yBeam - 0.55f * ra;           // the clevis lug reaches up into the beam
                // rod eye: outside the swept bell (eye about 0.6 barrel radii), and the whole lower actuator too (rod
                // 0.62, barrel and gland 1.25 barrel radii, taken along the straight line from the clevis)
                rEye = Mathf.Max(g.BellRadiusAt(yAct) + 1.2f * ra, SweptMax(yAct - 0.65f * ra, yAct + 0.65f * ra) + 0.62f * ra + gap);
                for (int k = 1; k < 20; k++)
                {
                    float f = k / 20f, y = Mathf.Lerp(yAct, yPin, f), sw = SweptMax(y - 0.3f * ra, y + 0.3f * ra);
                    if (sw <= 0) continue;   // no bell at this height
                    float need = sw + (f < 0.42f ? 0.62f : 1.25f) * ra + gap;
                    rEye = Mathf.Max(rEye, (need - f * rTop) / (1 - f));
                }
            }
            for (int it = 0; it < 3; it++)
            {
                ActStations();
                float L = new Vector2(rEye - rTop, yAct - yPin).magnitude;
                ra = Mathf.Clamp(L / 18f, 0.08f * rc, 0.22f * rc);
            }
            ActStations();

            // what sits on the injector dome (inlets, hot-gas ducts, the actuator outriggers): angle, linear half-width
            // and its radius, so the thrust frame struts (drawn last) keep clear of it
            var domeBusy = new List<Vector3>(8);
            float strutR = Mathf.Max(0.05f * rc, 0.005f);
            foreach (float ang in actAngles) domeBusy.Add(new Vector3(ang, 0.55f * ra, g.MountRadius * 0.92f));

            // ---------------------------------------------------------------- actuators
            KitMesh actuator = KitAssets.Get("actuator");
            bool moving = mbBody != null;
            KitMesh[] split0 = moving ? SplitActuator(actuator) : null, split = moving ? SplitActuator(Draw(actuator, "actuator")) : null;
            if (split == null) split = split0;   // a LOD1 file that doesn't cut: the full piece's halves
            if (moving)
            {
                res.Actuators = new ActuatorPose[actAngles.Length];
                res.ActuatorRadius = ra;
            }
            for (int ai = 0; ai < actAngles.Length; ai++)
            {
                float ang = actAngles[ai];
                Vector3 radial = Around(ang, 1, 0);
                Vector3 top = Around(ang, rTop, yPin);
                float rb = g.BellRadiusAt(yAct);
                Vector3 bot = Around(ang, rEye, yAct);
                Vector3 dir = (bot - top).normalized;
                // the piece's frame: Y up the actuator, X outward (servo-valve block on the outside, where it shows),
                // Z along the clevis pins (tangential: the actuator swings the bell in its own radial plane)
                Vector3 Y = -dir, X = (radial - Y * Vector3.Dot(radial, Y)).normalized, Z = Vector3.Cross(X, Y);

                // outrigger beam from under the mount plate out past the clevis
                float beamR = 0.55f * ra;
                mb.Cylinder(Around(ang, g.MountRadius * 0.85f, yBeam), Around(ang, rTop + 0.65f * ra, yBeam), beamR, MatStructure, S(16));
                // bell lug (on the bell): a pad welded on the wall, two cheeks either side of the rod eye, and the pin
                Vector2 bn = BellNormal(yAct);
                Vector3 N = radial * bn.x + Vector3.up * bn.y, pad = Around(ang, rb, yAct);
                mbB.Cylinder(pad - N * 0.12f * ra, pad + N * 0.22f * ra, 0.8f * ra, MatStructure, S(20));
                Vector3 lugOut = (bot - pad).normalized;
                for (int side = -1; side <= 1; side += 2)
                    mbB.Cylinder(pad + N * 0.1f * ra + Z * side * 0.6f * ra, bot + lugOut * 0.3f * ra + Z * side * 0.6f * ra, 0.22f * ra, MatStructure, S(12));
                mbB.Cylinder(bot - Z * 0.85f * ra, bot + Z * 0.85f * ra, 0.17f * ra, MatStructure, S(12));

                if (moving)
                {
                    res.Actuators[ai] = new ActuatorPose { Top = top, Eye = bot, Out = X, Pin = Z };
                    // every actuator has the same stations, so the first one's barrel and rod serve them all
                    if (ai == 0) BuildActuator(mbBody, mbRod, split0 != null ? split : null, split0, (bot - top).magnitude, ra, (bot - g.YThroat * Vector3.up).magnitude, res, S);
                    continue;
                }
                if (actuator != null)
                {
                    mb.AddMesh(Draw(actuator, "actuator"), top, X, Y, Z, ra, MatAuthored, (bot - top).magnitude / ra);
                    continue;
                }
                // primitives: clevis ball, barrel, gland, rod, rod-end ball
                Vector3 mid = Vector3.Lerp(top, bot, 0.55f);
                mb.Sphere(top + dir * ra * 0.6f, ra * 1.1f, MatStructure, S(20));
                mb.Cylinder(top + dir * ra * 0.6f, mid, ra, MatMachine, S(24));
                mb.Cylinder(mid - dir * ra * 0.25f, mid + dir * ra * 0.05f, ra * 1.18f, MatStructure, S(24));
                mb.Cylinder(mid, bot, ra * 0.42f, MatLines, S(16));
                mb.Sphere(bot, ra * 0.5f, MatStructure, S(20));
            }

            // regen return manifold under the injector flange, with feeders and instrumentation bosses
            KitMesh ring = KitAssets.Get("injector_ring");
            if (ring != null) mb.AddMesh(Draw(ring, "injector_ring"), new Vector3(0, g.YInjector, 0), Vector3.right, Vector3.up, Vector3.forward, rc, MatAuthored);

            // ---------------------------------------------------------------- valves, inlets, clamps

            // the whole piece under the stack: below the tank interface, and clear of the mount plate (with a margin)
            bool FitsUnder(KitMesh m, Vector3 origin, Vector3 X, Vector3 Y, Vector3 Z, float scale)
            {
                float plateR = g.MountRadius + 0.01f * rc + 0.002f, plateY = yUnder - 0.01f * rc - 0.002f;
                foreach (Vector3 q in m.Positions)
                {
                    Vector3 w = origin + (X * q.x + Y * q.y + Z * q.z) * scale;
                    if (w.y > YTank) return false;
                    if (w.y > plateY && w.x * w.x + w.z * w.z < plateR * plateR) return false;
                }
                return true;
            }

            // in-line main valve, flow along `axis` between its flanges at centre -/+ 2.2 line radii (its in / out
            // anchors); the actuator goes to the first side, starting from `prefer`, that keeps the valve under the
            // mount plate and doesn't point at the engine
            KitMesh valveMesh = KitAssets.Get("valve"), valveDraw = Draw(valveMesh, "valve");
            void Valve(Vector3 centre, Vector3 axis, float lineR, Vector3 prefer)
            {
                if (valveMesh == null || axis.sqrMagnitude < 1e-8f) return;
                Vector3 Y = axis.normalized;
                Vector3 x0 = prefer - Y * Vector3.Dot(prefer, Y);
                if (x0.sqrMagnitude < 1e-6f) x0 = Vector3.Cross(Y, Vector3.up);
                if (x0.sqrMagnitude < 1e-6f) x0 = Vector3.Cross(Y, Vector3.forward);
                x0.Normalize();
                Vector3 x1 = Vector3.Cross(Y, x0), outward = new Vector3(centre.x, 0, centre.z).normalized;
                Vector3 bestX = x0;
                float bestTop = float.PositiveInfinity;
                foreach (float a in new[] { 0f, 45f, -45f, 90f, -90f, 135f, -135f, 180f })
                {
                    Vector3 X = x0 * Mathf.Cos(a * Mathf.Deg2Rad) + x1 * Mathf.Sin(a * Mathf.Deg2Rad);
                    if (Vector3.Dot(X, outward) > -0.25f && FitsUnder(valveMesh, centre, X, Y, Vector3.Cross(X, Y), lineR)) { bestX = X; break; }
                    float top = TopY(valveMesh, centre, X, Y, Vector3.Cross(X, Y), lineR);
                    if (top < bestTop) { bestTop = top; bestX = X; }
                }
                mb.AddMesh(valveDraw, centre, bestX, Y, Vector3.Cross(bestX, Y), lineR, MatAuthored);
            }

            // a line entering the injector dome: the authored inlet (boss and flanged elbow) at radius `frac` of the
            // chamber, elbow pointing outward. It moves out toward the dome's edge (lower and steeper there) until the
            // whole piece is under the mount plate. Returns where the line attaches and its outward direction.
            KitMesh inletMesh = KitAssets.Get("dome_inlet"), inletDraw = Draw(inletMesh, "dome_inlet");
            Vector3 DomeInlet(float ang, float frac, float lineR, out Vector3 inDir)
            {
                Vector3 radial = Around(ang, 1, 0);
                Vector2 pt, nrm;
                if (inletMesh == null || !inletMesh.TryAnchor("in", out Vector3 la, out Vector3 ld))
                {
                    // no piece: the line comes in from outboard, low over the dome's edge
                    g.DomePoint(rc * Mathf.Max(frac, 0.9f), out pt, out nrm);
                    inDir = (radial + Vector3.up * 0.25f).normalized;
                    domeBusy.Add(new Vector3(ang, 1.6f * lineR, pt.x));
                    return Around(ang, pt.x, pt.y);
                }
                Vector3 at = Vector3.zero, X = Vector3.right, Y = Vector3.up, Z = Vector3.forward;
                float f0 = Mathf.Min(frac, 0.95f);
                pt = Vector2.zero;
                for (int k = 0; k <= 10; k++)
                {
                    g.DomePoint(rc * Mathf.Lerp(f0, 0.97f, k / 10f), out pt, out nrm);
                    at = Around(ang, pt.x, pt.y);
                    Y = radial * nrm.x + Vector3.up * nrm.y;
                    X = (radial - Y * Vector3.Dot(radial, Y)).normalized;
                    Z = Vector3.Cross(X, Y);
                    if (TopY(inletMesh, at, X, Y, Z, lineR) < -0.1f * lineR && FitsUnder(inletMesh, at, X, Y, Z, lineR)) break;
                }
                mb.AddMesh(inletDraw, at, X, Y, Z, lineR, MatAuthored);
                domeBusy.Add(new Vector3(ang, 2.3f * lineR, pt.x));
                inDir = X * ld.x + Y * ld.y + Z * ld.z;
                return at + (X * la.x + Y * la.y + Z * la.z) * lineR;
            }

            // the main valve bolted straight onto a dome inlet elbow (the main oxidizer valve at the injector, as on
            // the F-1, H-1 and J-2); returns the valve's inlet flange, where the line arrives, and the line's direction
            // out of it
            Vector3 DomeFeed(float ang, float frac, float lineR, out Vector3 backDir)
            {
                Vector3 e = DomeInlet(ang, frac, lineR, out Vector3 inDir);
                backDir = inDir;
                if (valveMesh == null) return e;
                Vector3 vOut = e - inDir * 0.04f * lineR, vc = vOut + inDir * 2.2f * lineR;
                Valve(vc, -inDir, lineR, Vector3.up * 0.6f + new Vector3(vc.x, 0, vc.z).normalized);
                return vc + inDir * 2.2f * lineR;
            }

            // P-clamp around a line, with a bracket to the static chamber wall or a pump housing when one is in reach
            List<PumpPorts> pumps = new List<PumpPorts>(2);
            void LineClamp(Vector3 at, Vector3 axis, float lineR)
            {
                if (full) mb.Torus(at, axis, 1.1f * lineR, 0.13f * lineR, MatStructure, S(20), 6);
                Vector3 target = Vector3.zero;
                float best = 9f * lineR + 0.02f;
                Vector3 hz = new Vector3(at.x, 0, at.z);
                // to the chamber wall above the throat (the bracket's foot clear of the bell's top edge as it swings)
                if (at.y < g.YInjector - 0.05f * rc && at.y > g.YThroat + 0.25f * g.ThroatRadius + 0.5f * lineR + gap)
                {
                    float rw = StaticR(at.y - 0.01f * rc, at.y + 0.01f * rc);
                    float dist = hz.magnitude - rw;
                    if (rw > 0 && dist < best) { best = dist; target = hz.normalized * (rw - Mathf.Min(0.004f, 0.3f * rw)) + Vector3.up * at.y; }
                }
                foreach (PumpPorts p in pumps)
                {
                    if (at.y < p.Centre.y - 0.9f * p.R || at.y > p.Centre.y + 0.9f * p.R) continue;
                    Vector3 off = new Vector3(at.x - p.Centre.x, 0, at.z - p.Centre.z);
                    float dist = off.magnitude - 0.6f * p.R;
                    if (dist < best) { best = dist; target = new Vector3(p.Centre.x, at.y, p.Centre.z) + off.normalized * 0.55f * p.R; }
                }
                if (best >= 9f * lineR + 0.02f) return;
                Vector3 from = at + (target - at).normalized * 1.15f * lineR;
                if ((target - from).magnitude < 0.5f * lineR) return;
                mb.Cylinder(from, target, 0.24f * lineR, MatStructure, S(12));
                mb.Sphere(target, 0.42f * lineR, MatStructure, S(12));
            }

            // P-clamp on a line fixed to the bell, with a bracket in to the bell wall (both on the bell)
            void BellClamp(Vector3 at, Vector3 axis, float lineR)
            {
                if (full) mbB.Torus(at, axis, 1.1f * lineR, 0.13f * lineR, MatStructure, S(20), 6);
                Vector3 hz = new Vector3(at.x, 0, at.z);
                float rw = g.BellRadiusAt(at.y), dist = hz.magnitude - rw;
                if (dist < 1.6f * lineR || dist > 16f * lineR + 0.03f) return;
                Vector3 target = hz.normalized * (rw - 0.003f) + Vector3.up * at.y;
                Vector3 from = at + (target - at).normalized * 1.15f * lineR;
                float br = Mathf.Max(0.24f * lineR, 0.02f * dist);
                mbB.Cylinder(from, target, br, MatStructure, S(12));
                mbB.Sphere(target, 1.7f * br, MatStructure, S(12));
            }

            // ---------------------------------------------------------------- turbopumps
            // turbopump envelope from its mass (dense machinery, ~5 t/m^3 including the housing), never wider than
            // the chamber; a staged-combustion powerhead is bigger (up to 1.3 chamber radii: RD-180, Raptor)
            float rp = Mathf.Clamp(Mathf.Pow((float)(turboKg / (5000 * 2 * Mathf.PI)), 1f / 3f), 0.02f, Mathf.Max((staged ? 1.3f : 0.9f) * rc, 0.03f));
            KitMesh preMesh = KitAssets.Get("preburner");
            float preTop = preMesh != null ? TopY(preMesh) : 1.25f;

            // Turbopumps: the authored Blender piece when it's installed, else primitives. A plain pump hangs below its
            // inlet duct; a staged-combustion pump hangs upside down, turbine end up, with its preburner on top just
            // under the mount plate. Braced to the chamber by two struts. `mainFuel`: a single pump that pumps fuel.
            PumpPorts Pump(float ang, float scale, bool preburner, bool dual, bool mainFuel)
            {
                float r = rp * scale;
                Vector3 radial = Around(ang, 1, 0), tang = Around(ang + 90, 1, 0);
                // an upside-down pump draws the inverted variant when it's installed (build_kit.py VARIANTS: the same
                // triangles and anchors, baked hanging that way, so its drips and dust fall the right way)
                string pumpName = dual ? "turbopump_dual" : "turbopump_single";
                KitMesh km = null;
                if (preburner)
                {
                    string status = KitAssets.Status;
                    km = KitAssets.Get(pumpName + "_inv");
                    if (km != null) pumpName += "_inv";
                    else KitAssets.Status = status;   // a missing variant isn't news
                }
                if (km == null) km = KitAssets.Get(pumpName);
                float teLocal = km != null && km.TryAnchor("turbine_exhaust", out Vector3 tea, out _) ? -tea.y : (dual ? 1.36f : 0.98f);
                if (preburner)
                {
                    // an upside-down powerhead hangs preburner, pump and suction U-duct from the mount: on a stubby bell
                    // it is made smaller (to 0.6 of its size) rather than hang below the exit plane
                    float inletLocal = km != null && km.TryAnchor("inlet", out Vector3 ila, out _) ? ila.y : 1.35f;
                    float stack = 0.05f + teLocal + preTop + inletLocal + 0.98f, room = yUnder - g.YExit - 0.002f;
                    if (r * stack > room) r = Mathf.Max(room / stack, 0.6f * r);
                }
                var p = new PumpPorts { Angle = ang, R = r, Flipped = preburner, Authored = km != null };
                float cy = preburner ? yUnder - 0.05f * r - (teLocal + preTop) * r : yUnder - 1.7f * r;
                // radial station: clear of the chamber, and of the swept bell beside the pump body and below it (the
                // suction U-duct of an upside-down pump hangs up to 2.2 pump radii below the centre, 0.33 wide)
                float maxBell = 0;
                for (int k = 0; k <= 8; k++) maxBell = Mathf.Max(maxBell, g.BellRadiusAt(cy - 1.4f * r + 2.8f * r * k / 8));
                float cR = Mathf.Max(Mathf.Max(rc + 1.25f * r, maxBell + 1.05f * r),
                    Mathf.Max(SweptMax(cy - 1.4f * r, cy + 1.4f * r) + 1.05f * r, SweptMax(cy - 2.2f * r, cy - 1.4f * r) + 0.4f * r) + gap);
                Vector3 c = Around(ang, cR, cy);
                p.Centre = c; p.CentreRadius = cR;
                Vector3 Y = preburner ? Vector3.down : Vector3.up, Z = preburner ? -tang : tang;
                if (km != null)
                {
                    mb.AddMesh(Draw(km, pumpName), c, radial, Y, Z, r, MatAuthored);
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
                    p.HasInlet = A("inlet", out p.Inlet, out _);
                    p.HasFuelIn = A("fuel_in", out p.FuelIn, out p.FuelInDir);
                    // volute discharge radii of the piece (ASSETS.md 4: ox scroll 0.17, fuel scroll 0.13 pump units)
                    p.OxPortR = 0.17f * r;
                    p.FuelPortR = (dual ? 0.13f : 0.17f) * r;
                }
                else
                {
                    // primitive pump: upright housing, volute, flange, turbine; a primitive preburner on the housing
                    mb.Cylinder(c - Vector3.up * r, c + Vector3.up * r * 0.9f, r * 0.75f, MatMachine, S(28));                // housing
                    mb.Torus(c + Vector3.up * r * 0.2f, Vector3.up, r * 0.78f, r * 0.32f, MatMachine, S(40), S(16));        // volute
                    mb.Torus(c - Vector3.up * r * 0.55f, Vector3.up, r * 0.76f, r * 0.06f, MatStructure, S(40), S(12));     // housing flange
                    mb.Cylinder(c - Vector3.up * r * 1.35f, c - Vector3.up * r * 0.95f, r * 0.9f, MatStructure, S(28));      // turbine
                    p.OxOut = c + Around(ang + 90, r * 0.75f, r * 0.2f); p.OxDir = tang;
                    p.FuelOut = c + Around(ang - 90, r * 0.75f, r * 0.2f); p.FuelDir = -tang;
                    p.TurbineIn = c + Around(ang + 28, r * 0.9f, -r * 1.15f); p.TurbineInDir = Around(ang + 28, 1, 0);
                    p.TurbineExhaust = preburner ? c + Vector3.up * r * 0.9f : c - Vector3.up * r * 1.35f;
                    p.TurbineExhaustDir = preburner ? Vector3.up : Vector3.down;
                    p.Inlet = preburner ? c - Vector3.up * r * 1.35f : c + Vector3.up * r * 0.9f;
                    p.HasInlet = true;
                    p.OxPortR = p.FuelPortR = 0.3f * r;
                    Y = Vector3.up;
                }
                p.MainSide = Vector3.Dot(p.OxDir, tang) >= 0 ? 1 : -1;

                // suction: ducts up to the tank interface with bellows (gimbal and thermal motion)
                float inR = Mathf.Min((dual ? oxIn : mainFuel ? fuelIn : oxIn) * 1.15f, 0.33f * r);
                int inMat = !dual && mainFuel ? fuelMat : MatLines;
                if (p.HasInlet && !preburner)
                    mb.Tube(new[] { p.Inlet, new Vector3(p.Inlet.x, YTank, p.Inlet.z) }, inR, inMat, T(24), MatStructure, 0.3f, 0.7f);
                else if (p.HasInlet)
                {
                    // upside-down pump: an RS-25-style U-duct down from the inlet, round under the pump and up beside
                    // it to the tank, on the side away from the main discharge
                    // (shallower when a stubby bell's exit plane is close under the pump: nothing hangs below it)
                    float uAng = ang - p.MainSide * 55;
                    float uDepth = Mathf.Clamp(p.Inlet.y - (g.YExit + 1.3f * inR + 0.002f), 0.3f * r, 0.65f * r);
                    Vector3 i1 = p.Inlet + Vector3.down * 0.54f * uDepth;
                    float yLow = p.Inlet.y - uDepth;
                    var u = new[] { p.Inlet, i1, Around(ang - p.MainSide * 22, cR, yLow), Around(uAng, cR, yLow + 0.35f * r), Around(uAng, cR, 0.5f * (yLow + YTank)), Around(uAng, cR, YTank) };
                    mb.Tube(u, inR, inMat, T(24), MatStructure, 0.72f, 0.86f);
                    if (full)
                    {
                        Vector3 cp = PathPoint(u, 0.62f, out Vector3 ct);
                        mb.Torus(cp, ct, 1.1f * inR, 0.12f * inR, MatStructure, S(24), 6);
                    }
                }
                if (p.HasFuelIn)
                {
                    float fr = Mathf.Min(fuelIn * 1.1f, 0.11f * r);
                    if (!preburner)
                        mb.Tube(new[] { p.FuelIn, new Vector3(p.FuelIn.x, YTank, p.FuelIn.z) }, fr, fuelMat, T(20), MatStructure, 0.3f, 0.7f);
                    else
                    {
                        // upside-down dual pump: the fuel boost inlet points down. A smaller duct: straight down past
                        // the pump's lower end, round under it on the discharge side and up to the tank outboard of the
                        // oxidizer line (which runs to the dome inlet 60 degrees along), the other way from the
                        // oxidizer U-duct; straight legs with bends of 3 line radii
                        float fAng = ang + p.MainSide * 72;
                        float bottomY = c.y - (km != null ? TopY(km) : 1.35f) * r;   // lowest point of the hanging pump
                        float yLowF = Mathf.Max(bottomY - 1.6f * fr - 0.04f * r, Mathf.Min(g.YExit + 1.3f * fr + 0.002f, p.FuelIn.y - 0.35f * r));
                        var fu = Legs(new[] { p.FuelIn, new Vector3(p.FuelIn.x, yLowF, p.FuelIn.z), Around(ang + p.MainSide * 36, cR + 0.1f * r, yLowF),
                            Around(fAng, cR, yLowF), Around(fAng, cR, YTank) }, 3f * fr);
                        // a short bellows (4 line radii) half way up the riser
                        float fl = PathLength(fu), bm = fl - 0.5f * (YTank - yLowF);
                        mb.Tube(fu, fr, fuelMat, T(20), MatStructure, Mathf.Clamp01((bm - 2f * fr) / fl), Mathf.Clamp01((bm + 2f * fr) / fl));
                        if (full)
                        {
                            Vector3 cp = PathPoint(fu, 0.6f, out Vector3 ct);
                            mb.Torus(cp, ct, 1.1f * fr, 0.12f * fr, MatStructure, S(20), 6);
                        }
                    }
                }

                // brace struts from the turbine end of the housing to the chamber wall, 20 degrees either side
                {
                    float lyStrut = km != null ? -teLocal + 0.62f : -1.0f;      // pump-local height of the turbine flange
                    float ys = c.y + Y.y * lyStrut * r;
                    float sr = Mathf.Max(0.06f * r, 0.003f);
                    // on the static chamber, the end ball clear of the bell's top edge as it swings
                    float yHi = g.YInjector - 0.2f * (g.YInjector - g.YChamberBottom);
                    float yLo = Mathf.Min(Mathf.Max(g.YThroat + 0.45f * (g.YChamberBottom - g.YThroat), g.YThroat + 0.25f * g.ThroatRadius + 1.6f * sr + gap), yHi);
                    float ysC = Mathf.Clamp(ys, yLo, yHi);
                    p.StrutR = sr;
                    for (int k = 0; k < 2; k++)
                    {
                        float sgn = k == 0 ? 1 : -1;
                        Vector3 pe = new Vector3(c.x, ys, c.z) + (-radial * 0.45f + tang * sgn * 0.45f) * r;
                        float rw = StaticR(ysC - 0.01f * rc, ysC + 0.01f * rc);
                        Vector3 ce = Around(ang + sgn * 20, Mathf.Max(rw, g.ThroatRadius) - 0.003f, ysC);
                        p.StrutPump[k] = pe; p.StrutChamber[k] = ce;
                        mb.Cylinder(pe, ce, sr, MatStructure, S(12));
                        mb.Sphere(pe, sr * 1.6f, MatStructure, S(12));
                        mb.Sphere(ce, sr * 1.6f, MatStructure, S(12));
                    }
                }

                if (preburner)
                {
                    Vector3 pb = p.TurbineExhaust;
                    float ductR = 0.30f * r;
                    if (preMesh != null && preMesh.TryAnchor("hot_out", out Vector3 ha, out Vector3 hd))
                    {
                        mb.AddMesh(Draw(preMesh, "preburner"), pb, radial, Vector3.up, tang, r, MatAuthored);
                        Vector3 hot = pb + (radial * ha.x + Vector3.up * ha.y + tang * ha.z) * r, hdir = radial * hd.x + Vector3.up * hd.y + tang * hd.z;
                        HotGasDuct(hot, hdir, ang, ductR, r);
                    }
                    else
                    {
                        mb.Cylinder(pb, pb + Vector3.up * r * 0.9f, r * 0.45f, MatMachine, S(28));
                        mb.Sphere(pb + Vector3.up * r * 0.9f, r * 0.45f, MatMachine, S(28));
                        mb.Torus(pb + Vector3.up * r * 0.1f, Vector3.up, r * 0.46f, r * 0.05f, MatStructure, S(40), S(12));
                        HotGasDuct(pb + Vector3.up * 0.5f * r - radial * 0.45f * r, -radial, ang, ductR, r);
                    }
                }
                pumps.Add(p);
                return p;
            }

            // hot-gas duct from a preburner outlet into the injector dome (staged combustion): fat (0.3 pump radii),
            // landing on the dome surface as high as the mount plate allows, with a welded socket there
            void HotGasDuct(Vector3 hot, Vector3 hdir, float ang, float ductR, float r)
            {
                Vector3 radial = Around(ang, 1, 0);
                float yMax = YTank - 1.08f * ductR;
                Vector2 pt = default, nrm = default;
                Vector3 into = Vector3.zero, N = Vector3.up;
                float k = 0;
                for (int i = 0; i <= 6; i++)
                {
                    g.DomePoint(rc * Mathf.Lerp(0.8f, 0.97f, i / 6f), out pt, out nrm);
                    into = Around(ang, pt.x, pt.y);
                    N = radial * nrm.x + Vector3.up * nrm.y;
                    k = Mathf.Min(0.6f * r, (yMax - into.y) / Mathf.Max(N.y, 0.05f));
                    if (k >= 0.35f * r) break;
                }
                k = Mathf.Max(k, 0.25f * ductR);
                domeBusy.Add(new Vector3(ang, 1.25f * ductR, pt.x));
                Vector3 lead = hot + hdir * 0.35f * r;
                var path = new[] { hot, lead, into + N * k, into - N * 0.3f * ductR };
                mb.Tube(path, ductR, MatHot, T(28));
                // flange against the preburner's outlet flange, socket on the dome
                mb.Cylinder(hot - hdir * 0.02f * r, hot + hdir * 0.12f * r, 1.34f * ductR, MatStructure, S(40));
                mb.Cylinder(into - N * 0.15f * ductR, into + N * 0.3f * ductR, 1.18f * ductR, MatStructure, S(40));
                if (full) mb.Torus(PathPoint(path, 0.45f, out Vector3 tn), tn, 1.03f * ductR, 0.1f * ductR, MatStructure, S(40), 6);
            }

            // ---------------------------------------------------------------- lines

            // pump discharge: a short transition cone from the volute's discharge to the line size
            Vector3 Discharge(Vector3 port, Vector3 dir, float portR, float lineR)
            {
                Vector3 end = port + dir * 2.5f * lineR;
                mb.Frustum(port, end, portR, lineR, MatLines, S(24), false);
                return end;
            }

            // main oxidizer line: transition cone, round to the dome inlet `off` degrees along the discharge side, into
            // the main valve bolted on the inlet elbow
            void OxToDome(PumpPorts p, float off)
            {
                float lr = oxR;
                Vector3 vIn = DomeFeed(p.Angle + p.MainSide * off, 0.72f, lr, out Vector3 back);
                Vector3 s0 = Discharge(p.OxOut, p.OxDir, p.OxPortR, lr);
                var path = new[] { s0, s0 + p.OxDir * 1.5f * lr, vIn + back * 1.6f * lr, vIn };
                mb.Tube(path, lr, MatLines, T(20), MatStructure);
                Vector3 cp = PathPoint(path, 0.5f, out Vector3 ct);
                LineClamp(cp, ct, lr);
            }

            // main fuel line: transition cone, a short turn into a straight drop beside the bell carrying the main fuel
            // valve and a bellows section, then a flanged joint onto the gimballed bell: the run below it is fixed to
            // the bell (mbB, braced to the wall) and drops into the coolant manifold. Everything above the joint is
            // static and stays out of the swept bell.
            void FuelToManifold(PumpPorts p, int mat, float sweep)
            {
                float lr = fuelR;
                Vector3 tangP = Around(p.Angle + 90, 1, 0);
                float side = Vector3.Dot(p.FuelDir, tangP) >= 0 ? 1 : -1;
                float a = p.Angle + side * sweep;
                Vector3 s0 = Discharge(p.FuelOut, p.FuelDir, p.FuelPortR, lr);
                Vector3 s1 = s0 + p.FuelDir * 1.5f * lr;
                float yB = yMan + 3.5f * lr + mtr;
                float yA = s1.y - 3f * lr;
                // valve a third of the way down the drop (but clear of the turn at its top), then 1 lr of pipe, 3 lr of
                // bellows and 0.5 lr of pipe down to the joint
                const float post = 4.5f;
                float yV = Mathf.Min(Mathf.Lerp(yA, yB, 0.3f), yA - 4.5f * lr);
                bool valve = valveMesh != null && yV - (2.2f + post) * lr - yB > 2f * lr;
                float yC = valve ? yV - (2.2f + post) * lr : Mathf.Max(yB + 1.5f * lr, Mathf.Min(Mathf.Lerp(yA, yB, 0.45f), yA - 4f * lr));
                // the drop: outside the pump's discharge, the manifold entry and the bell's foot; the static part (valve
                // 1.8, flanges 1.45 line radii) also clear of the swept bell down to the joint
                float rv = Mathf.Max(Mathf.Max(Horizontal(s1), ManifoldEntry(lr)), g.BellRadiusAt(yB) + 1.9f * lr);
                rv = Mathf.Max(rv, SweptMax(yC - 0.6f * lr, yA + lr) + (valve ? 1.8f : 1.45f) * lr + gap);
                Vector3 A = Around(a, rv, yA), B = Around(a, rv, yB), C = Around(a, rv, yC), M = Around(a, ManifoldEntry(lr), yMan);
                // straight legs with bends of 2.5 line radii (a long leg can't bow out past the plate or the exit)
                List<Vector3> pre;
                if (valve)
                {
                    Vector3 vc = Around(a, rv, yV), t = Vector3.down;
                    Vector3 vIn = vc - t * 2.2f * lr, vOut = vc + t * 2.2f * lr;
                    pre = Legs(new[] { s0, s1, A, vIn }, 2.5f * lr);
                    mb.Tube(pre, lr, mat, T(20), MatStructure);
                    Valve(vc, t, lr, Around(a, 1, 0));
                    mb.Tube(new[] { vOut, C }, lr, mat, T(20), MatStructure, 1f / post, 4f / post);
                }
                else
                {
                    pre = Legs(new[] { s0, s1, A, C }, 2.5f * lr);
                    float len = PathLength(pre);
                    mb.Tube(pre, lr, mat, T(20), MatStructure, Mathf.Max(0.05f, 1 - 3.6f * lr / len), Mathf.Max(0.1f, 1 - 0.6f * lr / len));
                }
                Vector3 cp = PathPoint(pre, 0.55f, out Vector3 ct);
                LineClamp(cp, ct, lr);
                // on the bell: the drop into the manifold, clamped to the wall half way down
                var down = Legs(new[] { C, B, M }, 2.5f * lr);
                mbB.Tube(down, lr, mat, T(20), MatStructure);
                if (yC - yB > 4f * lr)
                {
                    Vector3 bp = Vector3.Lerp(C, B, 0.5f);
                    BellClamp(bp, Vector3.down, lr);
                }
            }

            // instrumentation cable from an injector-ring transducer boss (chamber wall, 0.42 rc under the injector)
            // down the chamber and out along a pump brace to the pump, strapped to the wall every 0.3 rc
            float cabR = Mathf.Max(0.012f * rc, 0.0012f);
            void Cable(float sensorAng, PumpPorts p)
            {
                if (!full) return;
                float ySen = g.YInjector - 0.42f * rc;
                int k = Mathf.Abs(Mathf.DeltaAngle(sensorAng, p.Angle + 20)) < Mathf.Abs(Mathf.DeltaAngle(sensorAng, p.Angle - 20)) ? 0 : 1;
                Vector3 ce = p.StrutChamber[k], pe = p.StrutPump[k];
                float aEnd = Mathf.Atan2(ce.z, ce.x) * Mathf.Rad2Deg;
                float yEnd = ce.y + p.StrutR + 1.3f * cabR;
                float rw = StaticR(Mathf.Min(yEnd, ySen) - 0.02f * rc, ySen + 0.02f * rc) + 1.8f * cabR;
                var pts = new List<Vector3> { Around(sensorAng, rc * 1.03f, ySen), Around(sensorAng, rw + 0.6f * cabR, ySen - 0.05f * rc) };
                float da = Mathf.DeltaAngle(sensorAng, aEnd);
                int steps = Mathf.Max(2, Mathf.CeilToInt(Mathf.Abs(da) / 12f));
                for (int i = 1; i <= steps; i++)
                {
                    float f = i / (float)steps;
                    pts.Add(Around(sensorAng + da * f, rw, Mathf.Lerp(ySen - 0.05f * rc, yEnd, f)));
                }
                Vector3 up = Vector3.up * (p.StrutR + 1.3f * cabR);
                Vector3 along = (pe - ce).normalized;
                pts.Add(ce + along * 3 * p.StrutR + up);
                pts.Add(pe - along * 2 * p.StrutR + up);
                mb.Tube(pts, cabR, MatMachine, 8);
                // connector at the pump end, straps along the wall
                mb.Cylinder(pe - along * 2 * p.StrutR + up, pe - along * 0.5f * p.StrutR + up, 2.2f * cabR, MatMachine, 10);
                float len = 0;
                for (int i = 1; i < pts.Count; i++) len += (pts[i] - pts[i - 1]).magnitude;
                int clips = Mathf.FloorToInt(len / (0.3f * rc));
                for (int i = 1; i <= clips; i++)
                {
                    Vector3 cp = PathPoint(pts, i / (float)(clips + 1), out Vector3 ct);
                    mb.Torus(cp, ct, 1.5f * cabR, 0.45f * cabR, MatStructure, 10, 4);
                }
            }

            switch (d.Cycle)
            {
                case "PressureFed":
                {
                    // composite helium bottles (COPV), sized from the pressurant mass at ~400 kg/m^3 effective: bare
                    // metal liners with polar bosses, hung from the stage on a post, a band round the equator, a thin
                    // pressurant line to the injector; with four, two tiers either side
                    int nb = pressKg > 300 ? 4 : 2;
                    float rb = Mathf.Clamp(Mathf.Pow((float)(pressKg / nb / (400 * 4.19f)), 1f / 3f), 0.02f, Mathf.Max(0.75f * rc, 0.03f));
                    foreach (float ang in new[] { 0f, 180f })
                    {
                        Vector3 prevBottom = Vector3.zero;
                        for (int tier = 0; tier < nb / 2; tier++)
                        {
                            float y = yUnder - 1.45f * rb - tier * 2.3f * rb;
                            float rr = Mathf.Max(Mathf.Max(rc + 1.3f * rb, g.BellRadiusAt(y - rb) + 1.15f * rb), SweptMax(y - 1.1f * rb, y + 1.1f * rb) + 1.08f * rb + gap);
                            Vector3 bc = Around(ang, rr, y);
                            mb.Sphere(bc, rb, MatLines, S(36));
                            mb.Torus(bc, Vector3.up, rb * 1.0f, rb * 0.06f, MatStructure, S(48), S(10));   // mounting band
                            Vector3 bTop = bc + Vector3.up * 1.07f * rb, bBot = bc - Vector3.up * 1.07f * rb;
                            mb.Cylinder(bc + Vector3.up * 0.95f * rb, bTop, 0.18f * rb, MatStructure, S(16));
                            mb.Cylinder(bc - Vector3.up * 0.95f * rb, bBot, 0.18f * rb, MatStructure, S(16));
                            if (tier == 0)
                            {
                                mb.Cylinder(bTop, new Vector3(bTop.x, YTank, bTop.z), 0.2f * rb, MatStructure, S(16));   // hanger post
                                Vector3 l0 = bTop + Around(ang + 90, 0.12f * rb, 0);
                                Vector3 domeEnd = Around(ang, rc * 0.85f, g.YInjector + 0.4f * g.DomeHeight);
                                mb.Tube(new[] { l0, l0 + Vector3.up * 0.3f * rb, Vector3.Lerp(l0, domeEnd, 0.6f) + Vector3.up * 0.3f * rb, domeEnd }, Mathf.Max(0.04f * rb, 0.002f), MatLines, T(12), MatStructure);
                            }
                            else mb.Cylinder(prevBottom, bTop, 0.2f * rb, MatStructure, S(16));                              // link to the tier above
                            prevBottom = bBot;
                        }
                    }
                    // tank feeds: straight down from the stage into the main valve bolted on each dome inlet
                    foreach (float ang in new[] { 90f, 270f })
                    {
                        float lr = ang < 180 ? fuelR : oxR;
                        Vector3 vIn = DomeFeed(ang, 0.7f, lr, out Vector3 back);
                        Vector3 bend = vIn + back * 1.6f * lr;
                        Vector3 top = new Vector3(bend.x, YTank, bend.z);
                        var path = new[] { top, Vector3.Lerp(top, bend, 0.5f), bend, vIn };
                        mb.Tube(path, lr, ang < 180 ? fuelMat : MatLines, T(20), MatStructure);
                        if (full)
                        {
                            Vector3 cp = PathPoint(path, 0.35f, out Vector3 ct);
                            mb.Torus(cp, ct, 1.1f * lr, 0.13f * lr, MatStructure, S(20), 6);
                        }
                    }
                    // transducer cables up the dome to the stage harness
                    if (full)
                        foreach (float ang in new[] { 60f, 240f })
                        {
                            float ySen = g.YInjector - 0.42f * rc;
                            g.DomePoint(rc * 0.97f, out Vector2 dp, out _);
                            // out past the regen return ring (r 1.17 rc), up over the injector flange, across the dome
                            float rOut = Mathf.Max(1.24f * rc, rc + ft + 3 * cabR), yOver = g.YInjector + 0.05f * rc + 0.004f + 3 * cabR;
                            var pts = new[] { Around(ang, rc * 1.03f, ySen), Around(ang, rOut, ySen + 0.04f * rc), Around(ang, rOut, yOver),
                                Around(ang, dp.x + 2 * cabR, Mathf.Max(dp.y, yOver) + 2 * cabR), Around(ang, g.MountRadius * 0.9f, yUnder - 2 * cabR) };
                            mb.Tube(pts, cabR, MatMachine, 8);
                        }
                    break;
                }
                case "GasGenerator":
                {
                    PumpPorts p = Pump(0, 1, false, true, false);
                    float r = p.R;
                    OxToDome(p, 50);   // round past the fuel inlet duct
                    FuelToManifold(p, fuelMat, 25);
                    // gas generator bolted onto the turbine inlet
                    KitMesh ggMesh = KitAssets.Get("gas_generator");
                    Vector3 gx = (p.TurbineInDir - Vector3.up * Vector3.Dot(p.TurbineInDir, Vector3.up)).normalized;
                    if (ggMesh != null && gx.sqrMagnitude > 0.5f)
                        mb.AddMesh(Draw(ggMesh, "gas_generator"), p.TurbineIn, gx, Vector3.up, Vector3.Cross(gx, Vector3.up), r, MatAuthored);
                    else
                    {
                        Vector3 gg = p.TurbineIn + p.TurbineInDir * r * 0.45f + Vector3.up * r * 0.1f;
                        mb.Sphere(gg, r * 0.3f, MatMachine, S(24));
                        mb.Cylinder(gg, p.TurbineIn, r * 0.12f, MatHot, S(16));
                        mb.Cylinder(gg + Vector3.up * r * 0.25f, gg + Vector3.up * r * 0.45f, r * 0.14f, MatMachine, S(16));
                    }
                    // Turbine exhaust duct (Merlin 1C/1D, H-1, RS-27): down the free side, away from the fuel line, to a
                    // small nozzle a little over half way down the regen bell. Static, so it keeps out of the swept bell
                    // (the radius only grows on the way down). Uncooled: heat-tinted, and it glows dull red while
                    // running (MatHot).
                    float ductR = Mathf.Max(r * 0.32f, 0.01f);
                    float side = p.MainSide;   // the oxidizer side: the fuel line drops on the other
                    float Clear(float y) => Mathf.Max(g.BellRadiusAt(y) + 1.6f * ductR, SweptMax(y - ductR, y + ductR) + ductR + gap);
                    Vector3 te = p.TurbineExhaust;
                    float yMid = Mathf.Min(g.YChamberBottom, te.y - r * 0.4f);
                    // the nozzle (2.2 duct radii) ends above the bell's exit plane, even on a stubby bell, and above the
                    // reach of the swinging coolant manifold when there is room (a short regen bell ahead of an
                    // extension), rather than standing off round it
                    float yEx = Mathf.Min(g.YThroat - 0.55f * (g.YThroat - g.RegenEndY), yMid - 4f * ductR);
                    float manTop = yMan + mtr + (manR + mtr) * Mathf.Sin(GimbalClearanceDeg * Mathf.Deg2Rad) + 2.4f * ductR + gap;
                    if (yEx < manTop && yEx > yMan - mtr - 2f * ductR) yEx = Mathf.Min(manTop, yMid - 3f * ductR);
                    yEx = Mathf.Max(yEx, g.YExit + 2.4f * ductR + 0.002f);
                    yMid = Mathf.Max(yMid, Mathf.Min(yEx + 2f * ductR, te.y - r * 0.35f));
                    // straight down from the pump, swinging round to the free side, pushed out only where the bell
                    // flares into its way: the radius never shrinks on the way down (points close enough that the
                    // spline follows the swept bell's contour)
                    float rDuct = Mathf.Max(p.CentreRadius, Clear(yMid));
                    var duct = new List<Vector3> { te, te + p.TurbineExhaustDir * r * 0.3f, Around(p.Angle + side * 15, rDuct, yMid) };
                    int steps = Mathf.Clamp(Mathf.CeilToInt((yMid - yEx) / Mathf.Max(0.06f * bellLen, 3 * ductR)), 2, 24);
                    for (int i = 1; i <= steps; i++)
                    {
                        float f = i / (float)steps, y = Mathf.Lerp(yMid, yEx, f);
                        rDuct = Mathf.Max(rDuct, Clear(y));
                        duct.Add(Around(p.Angle + side * Mathf.Lerp(15, 40, Mathf.Sqrt(f)), rDuct, y));
                    }
                    // exit nozzle (2.2 duct radii, flaring to 1.7) clear of the swept bell too
                    float yExB = yEx - 2.2f * ductR;
                    float rEx = Mathf.Max(rDuct, Mathf.Max(g.BellRadiusAt(yExB) + 1.95f * ductR, SweptMax(yExB - 0.2f * ductR, yEx + 0.3f * ductR) + 1.75f * ductR + gap));
                    // into the nozzle straight down (the last stretch: any step out happens above it)
                    float yLead = yEx + Mathf.Min(1.6f * ductR, 0.5f * (duct[duct.Count - 2].y - yEx));
                    duct[duct.Count - 1] = Around(p.Angle + side * 40, rEx, yLead);
                    duct.Add(Around(p.Angle + side * 40, rEx, yEx));
                    Vector3 exitTop = duct[duct.Count - 1];
                    Vector3 exitBottom = exitTop - Vector3.up * ductR * 2.2f;
                    mb.Tube(duct, ductR, MatHot, T(24), MatStructure);
                    mb.Frustum(exitTop, exitBottom, ductR, ductR * 1.7f, MatHot, S(28), false);
                    mb.Torus(exitTop - Vector3.up * 0.1f * ductR, Vector3.up, 1.04f * ductR, 0.14f * ductR, MatStructure, S(32), S(8));
                    // clamp bands along the run
                    foreach (float f in new[] { 0.4f, 0.75f })
                    {
                        Vector3 cp = PathPoint(duct, f, out Vector3 ct);
                        mb.Torus(cp, ct, 1.05f * ductR, 0.12f * ductR, MatStructure, S(32), S(8));
                    }
                    res.HasExhaust = true;
                    res.ExhaustPosition = exitBottom;
                    res.ExhaustRadius = ductR * 1.7f;
                    Cable(60, p);
                    Cable(320, p);
                    break;
                }
                case "Expander":
                {
                    PumpPorts p = Pump(0, 1, false, true, false);
                    float r = p.R;
                    OxToDome(p, 50);
                    // cold fuel from the pump to the manifold (insulated when it is hydrogen)
                    FuelToManifold(p, fuelMat, 55);
                    // heated fuel: from the regen manifold up beside the bell (fixed to it), out of the bell's sweep to
                    // a flanged joint, then a bellows and the static run under the pump into the turbine inlet from
                    // outboard (warm gas: plain line, no insulation)
                    float aM = p.Angle - p.MainSide * 25;
                    float lr = fuelR;
                    float yUnderPump = p.Centre.y - 1.75f * r;
                    float yRise0 = yMan + 3.5f * lr + mtr;
                    // the joint as high as it goes (the bell moves least there): its 3.6 lr of bellows end 2 lr under
                    // the throat and 1.5 lr under the run beneath the pump
                    const float bel = 3.6f;
                    float yJ = Mathf.Max(Mathf.Min(g.YThroat - (bel + 2f) * lr, yUnderPump - (bel + 1.5f) * lr), yRise0 + 3f * lr);
                    float rJ = Mathf.Max(g.BellRadiusAt(yJ) + 2.2f * lr, SweptMax(yJ - 0.6f * lr, yJ + (bel + 0.6f) * lr) + 1.45f * lr + gap);
                    // on the bell: the rise hugging the wall (a point every 6 lr, so the spline follows the contour),
                    // easing out to the joint radius at the top, the last chord vertical
                    var rise = new List<Vector3> { Around(aM, ManifoldEntry(lr), yMan) };
                    float yR1 = yJ - 1.2f * lr;
                    int nr = Mathf.Clamp(Mathf.CeilToInt((yR1 - yRise0) / (6f * lr)), 3, 40);
                    for (int i = 0; i <= nr; i++)
                    {
                        float f = i / (float)nr, y = Mathf.Lerp(yRise0, yR1, f), rr = g.BellRadiusAt(y) + 2.2f * lr;
                        float e = Mathf.Clamp01((f - 0.6f) / 0.4f);
                        e = e * e * (3 - 2 * e);
                        rise.Add(Around(aM, Mathf.Max(rr, Mathf.Lerp(rr, rJ, e)), y));
                    }
                    Vector3 J = Around(aM, rJ, yJ);
                    rise.Add(J);
                    mbB.Tube(rise, lr, MatLines, T(20), MatStructure);
                    {
                        Vector3 bp = PathPoint(rise, 0.45f, out Vector3 bt);
                        BellClamp(bp, bt, lr);
                    }
                    // static: bellows up from the joint, out past the chamber and the swept bell if need be, up under
                    // the pump and into the turbine inlet
                    float rU = Mathf.Max(Mathf.Max(p.CentreRadius + 0.7f * r, g.BellRadiusAt(yUnderPump) + 2.5f * lr), SweptMax(yUnderPump - 1.5f * lr, yUnderPump + 1.5f * lr) + 1.45f * lr + gap);
                    Vector3 B1 = J + Vector3.up * bel * lr, U = Around(p.Angle + p.MainSide * 5, rU, yUnderPump);
                    float rNeed = 0;
                    for (int k = 0; k <= 12; k++)
                    {
                        float y = Mathf.Lerp(B1.y, yUnderPump, k / 12f);
                        float wall = y > g.YThroat ? StaticR(y - 1.5f * lr, y + 1.5f * lr) : 0;
                        rNeed = Mathf.Max(rNeed, Mathf.Max(wall, SweptMax(y - 1.5f * lr, y + 1.5f * lr)) + 1.45f * lr + gap);
                    }
                    var corners = new List<Vector3> { J, B1 };
                    if (rJ < rNeed) corners.Add(Around(Mathf.Lerp(aM, p.Angle + p.MainSide * 5, 0.3f), Mathf.Min(rNeed, rU), B1.y + 2f * lr));
                    corners.Add(U);
                    List<Vector3> hot = Legs(corners, 2.5f * lr);
                    hot.Add(p.TurbineIn + p.TurbineInDir * 0.6f * r - Vector3.up * 0.2f * r);
                    hot.Add(p.TurbineIn);
                    float hl = PathLength(hot);
                    mb.Tube(hot, lr, MatLines, T(20), MatStructure, 0.4f * lr / hl, (bel - 0.2f) * lr / hl);
                    Cable(60, p);
                    Cable(320, p);
                    break;
                }
                case "StagedCombustion":
                {
                    PumpPorts p = Pump(0, 1, true, true, false);
                    OxToDome(p, 60);   // clear of the hot-gas duct landing on the dome
                    FuelToManifold(p, fuelMat, 25);
                    Cable(60, p);
                    Cable(320, p);
                    break;
                }
                case "FullFlowStaged":
                {
                    // one single-propellant pump per side, each with its own preburner
                    PumpPorts fp = Pump(0, 0.8f, true, false, true);
                    FuelToManifold(fp, fuelMat, 25);
                    PumpPorts op = Pump(180, 0.8f, true, false, false);
                    OxToDome(op, 60);
                    Cable(60, fp);
                    Cable(240, op);
                    break;
                }
            }

            // thrust frame: struts from the mount plate rim down to the shoulder of the injector dome, ball joints at
            // both ends. Laid out from the free arcs: four at 90 degrees, else three at 120, each as near its station as
            // the inlets, hot-gas ducts and actuator outriggers allow, never two crowded on one side.
            res.ThrustStruts = ThrustFrame();
            int ThrustFrame()
            {
                const float stepDeg = 2.5f;
                int nFree = Mathf.RoundToInt(360 / stepDeg);
                var free = new bool[nFree];
                for (int i = 0; i < nFree; i++)
                {
                    float a = i * stepDeg;
                    bool ok = true;
                    foreach (Vector3 b in domeBusy)
                    {
                        float rho = Mathf.Clamp(b.z, 0.3f * rc, 0.95f * rc);
                        if (Mathf.Abs(Mathf.DeltaAngle(a, b.x)) < Mathf.Rad2Deg * Mathf.Atan2(b.y + 1.6f * strutR, rho) + 4) { ok = false; break; }
                    }
                    free[i] = ok;
                }
                // nearest free angle to `nominal` within +-window (NaN when none)
                float Nearest(float nominal, float window)
                {
                    for (float off = 0; off <= window + 1e-3f; off += stepDeg)
                        for (int sgn = 1; sgn >= -1; sgn -= 2)
                        {
                            float a = Mathf.Repeat(nominal + sgn * off, 360);
                            if (free[Mathf.RoundToInt(a / stepDeg) % nFree]) return a;
                            if (off == 0) break;
                        }
                    return float.NaN;
                }
                var best = new float[4];
                var cand = new float[4];
                int placed = 0;
                foreach (int n in new[] { 4, 3 })
                {
                    float spacing = 360f / n, window = 0.45f * spacing, bestWorst = float.PositiveInfinity;
                    for (float b0 = 0; b0 < spacing - 1e-3f; b0 += stepDeg)
                    {
                        float worst = 0;
                        bool ok = true;
                        for (int k = 0; k < n && ok; k++)
                        {
                            float nominal = 45 + b0 + spacing * k;   // 45, 135, ... when nothing is in the way
                            float a = Nearest(nominal, window);
                            if (float.IsNaN(a)) { ok = false; break; }
                            cand[k] = a;
                            worst = Mathf.Max(worst, Mathf.Abs(Mathf.DeltaAngle(a, nominal)));
                        }
                        if (!ok) continue;
                        // balanced: no two struts closer than 0.6 of the spacing, no gap wider than 1.35 of it, and the
                        // struts' directions summing to nearly nothing (not bunched on one side)
                        float sx = 0, sz = 0;
                        for (int k = 0; k < n && ok; k++)
                        {
                            float gapDeg = Mathf.Repeat(cand[(k + 1) % n] - cand[k], 360);
                            if (gapDeg < 0.6f * spacing || gapDeg > 1.35f * spacing) ok = false;
                            sx += Mathf.Cos(cand[k] * Mathf.Deg2Rad); sz += Mathf.Sin(cand[k] * Mathf.Deg2Rad);
                        }
                        if (ok && Mathf.Sqrt(sx * sx + sz * sz) > 0.15f * n) ok = false;
                        if (!ok || worst >= bestWorst - 1e-3f) continue;
                        bestWorst = worst;
                        Array.Copy(cand, best, n);
                        placed = n;
                    }
                    if (placed > 0) break;
                }
                if (placed == 0)
                {
                    // crowded dome (small engines: thick lines, fat ducts): the best-balanced three free angles at
                    // least 40 degrees apart, beside the obstacles rather than near any station
                    var freeIdx = new List<int>(nFree);
                    var ux = new float[nFree];
                    var uz = new float[nFree];
                    for (int i = 0; i < nFree; i++)
                    {
                        if (free[i]) freeIdx.Add(i);
                        ux[i] = Mathf.Cos(i * stepDeg * Mathf.Deg2Rad); uz[i] = Mathf.Sin(i * stepDeg * Mathf.Deg2Rad);
                    }
                    float bestImb = float.PositiveInfinity;
                    int minSep = Mathf.RoundToInt(40 / stepDeg);
                    for (int a = 0; a < freeIdx.Count; a++)
                        for (int b = a + 1; b < freeIdx.Count; b++)
                        {
                            int ia = freeIdx[a], ib = freeIdx[b];
                            if (ib - ia < minSep) continue;
                            for (int c = b + 1; c < freeIdx.Count; c++)
                            {
                                int ic = freeIdx[c];
                                if (ic - ib < minSep || nFree - ic + ia < minSep) continue;
                                float sx = ux[ia] + ux[ib] + ux[ic], sz = uz[ia] + uz[ib] + uz[ic];
                                float imb = sx * sx + sz * sz;
                                if (imb < bestImb - 1e-6f) { bestImb = imb; best[0] = ia * stepDeg; best[1] = ib * stepDeg; best[2] = ic * stepDeg; }
                            }
                        }
                    // accept up to a mildly uneven tripod (|sum| <= 0.4 x 3); past that, or with no three free angles,
                    // three at 120 degrees turned to sit least deep in what is in the way
                    if (bestImb <= (0.4f * 3) * (0.4f * 3)) placed = 3;
                    else
                    {
                        float bestDepth = float.PositiveInfinity;
                        for (float b0 = 0; b0 < 120 - 1e-3f; b0 += stepDeg)
                        {
                            float depth = 0;
                            for (int k = 0; k < 3; k++)
                                foreach (Vector3 bz in domeBusy)
                                {
                                    float rho = Mathf.Clamp(bz.z, 0.3f * rc, 0.95f * rc);
                                    float half = Mathf.Rad2Deg * Mathf.Atan2(bz.y + 1.6f * strutR, rho) + 4;
                                    depth = Mathf.Max(depth, half - Mathf.Abs(Mathf.DeltaAngle(b0 + 120 * k, bz.x)));
                                }
                            if (depth < bestDepth - 1e-3f) { bestDepth = depth; for (int k = 0; k < 3; k++) best[k] = b0 + 120 * k; }
                        }
                        placed = 3;
                    }
                }
                for (int k = 0; k < placed; k++)
                {
                    float ang = best[k];
                    Vector3 top = Around(ang, g.MountRadius * 0.92f, yUnder), bot = Around(ang, rc * 0.95f, g.YInjector + 0.15f * g.DomeHeight);
                    mb.Cylinder(top, bot, strutR, MatStructure, S(16));
                    mb.Sphere(top, strutR * 1.5f, MatStructure, S(16));
                    mb.Sphere(bot, strutR * 1.5f, MatStructure, S(16));
                }
                return placed;
            }

            return res;
        }
    }
}
