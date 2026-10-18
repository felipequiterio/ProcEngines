using System;
using System.Collections.Generic;
using ProcEngines.Model;
using UnityEngine;

namespace ProcEngines
{
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
