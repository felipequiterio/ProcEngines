using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using ProcEngines.Model;

namespace ProcEngines.Model.Tests
{
    /// <summary>Tiny package-free test harness: every public static void method taking ModelData is a test.</summary>
    public static class Tests
    {
        static int failures;

        public static int RunAll(ModelData data)
        {
            var tests = typeof(Tests).GetMethods().Where(m => m.IsStatic && m.ReturnType == typeof(void)
                && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(ModelData)).ToList();
            int passed = 0;
            foreach (var t in tests)
            {
                int before = failures;
                try { t.Invoke(null, new object[] { data }); }
                catch (Exception e) { failures++; Console.WriteLine($"FAIL {t.Name}: {(e.InnerException ?? e).Message}"); }
                if (failures == before) { passed++; Console.WriteLine($"ok   {t.Name}"); }
            }
            Console.WriteLine($"{passed}/{tests.Count} tests passed, {failures} failed assertion(s)");
            return failures == 0 ? 0 : 1;
        }

        static void Assert(bool cond, string msg)
        {
            if (cond) return;
            failures++;
            Console.WriteLine("  FAIL: " + msg);
        }

        static EngineDesign D(string prop = "Kerolox", string cycle = "GasGenerator", double f = 500, double pc = 80, double eps = 20, string tech = "Mid") =>
            new EngineDesign { Propellant = prop, Cycle = cycle, ThrustVac = f, Pc = pc, Eps = eps, Tech = tech };

        // ------------------------------------------------------------------ data

        public static void CfgParserHandlesNestingCommentsAndInlineBraces(ModelData _)
        {
            CfgNode root = CfgNode.Parse("A // c\n{\n x = 1.5 // c\n B { y = 2 }\n}\nC\n{\n}\n");
            Assert(root.Nodes.Count == 2, "two top-level nodes");
            CfgNode a = root.Node("A");
            Assert(a.Double("x") == 1.5, "value with trailing comment");
            Assert(a.Node("B")?.Double("y") == 2, "inline braces");
        }

        public static void PropellantTablesLoaded(ModelData data)
        {
            foreach (string p in new[] { "Kerolox", "Methalox", "Hydrolox", "NTR-H2" })
                Assert(data.Propellants.ContainsKey(p), "missing propellant " + p);
            foreach (string c in new[] { "PressureFed", "GasGenerator", "Expander", "StagedCombustion", "FullFlowStaged" })
                Assert(data.Cycles.ContainsKey(c), "missing cycle " + c);
        }

        public static void TableReproducesCeaGridPoint(ModelData data)
        {
            // CEA (NASA cea 3.3.4) RP-1/LOX, O/F 2.4, Pc 100 bar: spot values straight from the generated table
            Propellant k = data.Propellants["Kerolox"];
            double a = k.IdealIspVac(2.4, 100, 16), b = k.IdealIspVac(2.4, 100, 16.0000001);
            Assert(Math.Abs(a - b) < 1e-3, "interpolation continuous at a grid point");
            Assert(a > 330 && a < 345, $"kerolox ideal Isp(eps 16, Pc 100) = {a:F1} s, expected ~337");
            double h = data.Propellants["Hydrolox"].IdealIspVac(6.0, 200, 69);
            Assert(h > 455 && h < 470, $"hydrolox ideal Isp(eps 69, Pc 200) = {h:F1} s, expected ~463");
        }

        public static void IdealIspIncreasesWithAreaRatioAndPressureDropsWithIt(ModelData data)
        {
            foreach (Propellant p in data.Propellants.Values)
            {
                double pc = 70, of = p.OfDefault, prevIsp = 0, prevP = 2;
                foreach (double eps in new[] { 1.0, 2, 5, 10, 20, 50, 100, 200, 400 })
                {
                    double isp = p.IdealIspVac(of, pc, eps), pr = p.PressureRatio(of, pc, eps);
                    Assert(isp > prevIsp, $"{p.Name}: Isp not increasing at eps {eps}");
                    Assert(pr < prevP, $"{p.Name}: p/Pc not decreasing at eps {eps}");
                    prevIsp = isp; prevP = pr;
                }
            }
        }

        public static void AreaRatioAtPressureInvertsPressureRatio(ModelData data)
        {
            Propellant p = data.Propellants["Methalox"];
            foreach (double eps in new[] { 3.0, 12, 40, 150 })
            {
                double pr = p.PressureRatio(3.6, 150, eps);
                double back = p.AreaRatioAtPressure(3.6, 150, pr, 400);
                Assert(Math.Abs(back / eps - 1) < 1e-3, $"eps {eps} -> p/Pc {pr:G4} -> eps {back:F3}");
            }
        }

        // ------------------------------------------------------------------ nozzle

        public static void RaoContourMatchesInputs(ModelData _)
        {
            foreach (double eps in new[] { 4.0, 16, 69, 250 })
                foreach (double lf in new[] { 0.6, 0.8, 1.0 })
                {
                    var c = new NozzleContour(eps, lf, false);
                    WallPoint exit = c.Wall[c.Wall.Count - 1];
                    Assert(Math.Abs(exit.R * exit.R / eps - 1) < 1e-9, $"exit radius for eps {eps}");
                    Assert(Math.Abs(exit.X - c.Length) < 1e-9, "exit at nozzle length");
                    Assert(c.ThetaN > c.ThetaE && c.ThetaE > 2 && c.ThetaN < 45, $"angles eps {eps} lf {lf}: {c.ThetaN:F1}/{c.ThetaE:F1}");
                    for (int i = 1; i < c.Wall.Count; i++) Assert(c.Wall[i].X > c.Wall[i - 1].X && c.Wall[i].R >= c.Wall[i - 1].R, "wall monotone");
                }
            var cone = new NozzleContour(20, 1, true);
            Assert(Math.Abs(cone.DivergenceEfficiency - 0.983) < 0.001, "15-degree cone divergence 0.983");
            var bell = new NozzleContour(20, 0.8, false);
            Assert(bell.DivergenceEfficiency > 0.99 && bell.Length < cone.Length, "80 % bell: shorter than the cone, better divergence");
        }

        public static void FrictionLossIsSmallAndShrinksWithSize(ModelData data)
        {
            double small = EngineModel.Compute(D(f: 5), data).FrictionLoss, big = EngineModel.Compute(D(f: 5000), data).FrictionLoss;
            Assert(small > 0.001 && small < 0.04, $"5 kN friction loss {small:P2}");
            Assert(big > 0.0005 && big < small, $"5000 kN friction loss {big:P2} < 5 kN {small:P2}");
        }

        // ------------------------------------------------------------------ performance

        public static void SizingClosesThrust(ModelData data)
        {
            foreach (string prop in new[] { "Kerolox", "Methalox", "Hydrolox" })
            {
                EngineStats s = EngineModel.Compute(D(prop, f: 800, pc: 100, eps: 30), data);
                Assert(Math.Abs(s.MassFlow * s.IspVac * 9.80665 / 1000 - 800) < 1e-6, $"{prop}: F = mdot Isp g0");
                Assert(Math.Abs(s.ExitRadius / s.ThroatRadius - Math.Sqrt(30)) < 1e-9, "Re/Rt = sqrt(eps)");
            }
        }

        public static void AtmosphereCurveIsMonotoneAndEndsNearZero(ModelData data)
        {
            foreach (double eps in new[] { 8.0, 30, 120 })
            {
                EngineStats s = EngineModel.Compute(D("Hydrolox", "StagedCombustion", 1000, 200, eps), data);
                List<CurveKey> c = s.AtmosphereCurve;
                Assert(c[0].Pressure == 0 && Math.Abs(c[0].Isp - s.IspVac) < 1e-6, "first key is vacuum");
                for (int i = 1; i < c.Count; i++)
                {
                    Assert(c[i].Pressure > c[i - 1].Pressure, "pressures increase");
                    Assert(c[i].Isp < c[i - 1].Isp, $"eps {eps}: Isp decreases ({c[i - 1].Isp:F1} -> {c[i].Isp:F1} at {c[i].Pressure} atm)");
                    Assert(c[i].Isp > 0, "Isp stays positive");
                }
            }
        }

        public static void HermiteCurveNeverOvershoots(ModelData data)
        {
            foreach (double eps in new[] { 8.0, 40, 165 })
            {
                EngineStats s = EngineModel.Compute(D("Kerolox", "GasGenerator", 900, 97, eps), data);
                List<CurveKey> c = s.AtmosphereCurve;
                for (int i = 0; i < c.Count - 1; i++)
                    for (int j = 1; j < 20; j++)
                    {
                        double p = c[i].Pressure + j / 20.0 * (c[i + 1].Pressure - c[i].Pressure);
                        double v = CurveKey.Hermite(c[i], c[i + 1], p);
                        Assert(v <= c[i].Isp + 1e-9 && v >= c[i + 1].Isp - 1e-9, $"eps {eps}: Hermite {v:F2} outside [{c[i + 1].Isp:F2}, {c[i].Isp:F2}] at {p:F3} atm");
                    }
            }
        }

        public static void SeparationKeepsHighExpansionThrustPositive(ModelData data)
        {
            EngineStats s = EngineModel.Compute(D("Kerolox", "GasGenerator", 900, 97, 165), data);
            Assert(s.SeparatedAtSeaLevel, "MVac-like nozzle separates at sea level");
            Assert(s.IspSL > 0, $"separated sea-level Isp {s.IspSL:F1} > 0 (linear extrapolation would be negative)");
            EngineStats t = EngineModel.Compute(D("Kerolox", "GasGenerator", 900, 97, 12), data);
            Assert(!t.SeparatedAtSeaLevel, "low-expansion nozzle does not separate at sea level");
        }

        public static void GasGeneratorCostsIspStagedCombustionDoesNot(ModelData data)
        {
            EngineStats gg = EngineModel.Compute(D(cycle: "GasGenerator", pc: 100), data);
            EngineStats sc = EngineModel.Compute(D(cycle: "StagedCombustion", pc: 100), data);
            double loss = 1 - gg.IspVac / sc.IspVac;
            Assert(loss > 0.01 && loss < 0.04, $"GG dump loss {loss:P1} (DESIGN 5.3: 1.5-3 %)");
            Assert(sc.TurbineFlowFraction == 0 && gg.TurbineFlowFraction > 0.01, "only open cycles dump turbine gas");
        }

        public static void HydrogenNeedsMorePumpPowerPerKg(ModelData data)
        {
            EngineStats h = EngineModel.Compute(D("Hydrolox", f: 1000, pc: 100), data);
            EngineStats k = EngineModel.Compute(D("Kerolox", f: 1000, pc: 100), data);
            Assert(h.PumpPower / h.MassFlow > 2 * k.PumpPower / k.MassFlow, "hydrolox pump work per kg >> kerolox");
        }

        public static void ExpanderIsHeatLimitedAtHighThrust(ModelData data)
        {
            EngineStats small = EngineModel.Compute(D("Hydrolox", "Expander", 100, 45, 80), data);
            EngineStats big = EngineModel.Compute(D("Hydrolox", "Expander", 1500, 45, 80), data);
            Assert(small.ExpanderPowerMargin > 1, $"RL10-size expander closes (margin {small.ExpanderPowerMargin:F2})");
            Assert(big.ExpanderPowerMargin < 1 && big.Warnings.Any(w => w.Contains("expander")), $"1500 kN expander does not close (margin {big.ExpanderPowerMargin:F2})");
        }

        public static void NuclearTableGivesNervaClassIsp(ModelData data)
        {
            var d = new EngineDesign { Propellant = "NTR-H2", Cycle = "Expander", Tech = "Mid", ThrustVac = 250, Pc = 40, Eps = 100, Of = 2500 };
            EngineStats s = EngineModel.Compute(d, data);
            Assert(s.IspVac > 800 && s.IspVac < 950, $"NTR Tc 2500 K eps 100 Isp {s.IspVac:F0} s (NERVA ~840)");
        }

        // ------------------------------------------------------------------ mass

        public static void MassScalesSensibly(ModelData data)
        {
            double m1 = EngineModel.Compute(D(f: 100), data).Mass, m10 = EngineModel.Compute(D(f: 1000), data).Mass;
            Assert(m10 > 5 * m1 && m10 < 12 * m1, $"10x thrust -> {m10 / m1:F1}x mass");
            double lowEps = EngineModel.Compute(D(eps: 10), data).Mass, highEps = EngineModel.Compute(D(eps: 100), data).Mass;
            Assert(highEps > lowEps, "bigger nozzle is heavier");
            double pf = EngineModel.Compute(D(cycle: "PressureFed", pc: 15, f: 50), data).Mass;
            Assert(pf > 0 && EngineModel.Compute(D(cycle: "PressureFed", pc: 15, f: 50), data).MassBreakdown["pressurant"] > 0, "pressure-fed carries pressurant mass");
        }

        // ------------------------------------------------------------------ robustness

        public static void RandomDesignsAreFiniteAndDeterministic(ModelData data)
        {
            var rng = new Random(12345);
            string[] props = { "Kerolox", "Methalox", "Hydrolox" };
            string[] cycles = data.Cycles.Keys.ToArray();
            string[] techs = data.TechLevels.Keys.ToArray();
            for (int i = 0; i < 400; i++)
            {
                var d = new EngineDesign
                {
                    Propellant = props[rng.Next(props.Length)], Cycle = cycles[rng.Next(cycles.Length)], Tech = techs[rng.Next(techs.Length)],
                    ThrustVac = Math.Exp(rng.NextDouble() * Math.Log(20000)) * 0.5, Pc = 1 + rng.NextDouble() * 400, Eps = 1 + rng.NextDouble() * 500,
                    Of = rng.NextDouble() < 0.3 ? double.NaN : rng.NextDouble() * 10, LengthFraction = 0.5 + rng.NextDouble() * 0.6, Conical = rng.NextDouble() < 0.2,
                };
                EngineStats a = EngineModel.Compute(d, data), b = EngineModel.Compute(d, data);
                double[] v = { a.IspVac, a.IspSL, a.Mass, a.MassFlow, a.ThroatRadius, a.ExitRadius, a.TotalLength, a.Cost };
                Assert(v.All(x => !double.IsNaN(x) && !double.IsInfinity(x)) && a.Mass > 0 && a.ThroatRadius > 0, $"design {i}: non-finite output");
                Assert(a.IspVac == b.IspVac && a.Mass == b.Mass && a.AtmosphereCurve.Count == b.AtmosphereCurve.Count, $"design {i}: not deterministic");
            }
        }

        public static void ResultsIndependentOfCulture(ModelData _)
        {
            CultureInfo old = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
                ModelData de = ModelData.LoadFolder(Program.DataDir());
                Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;
                ModelData inv = ModelData.LoadFolder(Program.DataDir());
                var d = D("Methalox", "FullFlowStaged", 2000, 300, 34);
                Assert(EngineModel.Compute(d, de).IspVac == EngineModel.Compute(d, inv).IspVac, "data loaded under de-DE gives identical results");
            }
            finally { Thread.CurrentThread.CurrentCulture = old; }
        }

        // ------------------------------------------------------------------ calibration against real engines

        public static void ReferenceEnginesIsp(ModelData data)
        {
            foreach (ReferenceEngine r in ReferenceEngine.All.Where(e => !e.SoftIsp))
            {
                EngineStats s = EngineModel.Compute(r.Design(), data);
                Assert(Math.Abs(s.IspVac / r.IspVac - 1) <= Program.IspTolerance, $"{r.Name}: Isp vac {s.IspVac:F1} vs {r.IspVac} {Report.Pct(s.IspVac, r.IspVac)}");
                if (!double.IsNaN(r.IspSL))
                    Assert(Math.Abs(s.IspSL / r.IspSL - 1) <= Program.IspTolerance, $"{r.Name}: Isp SL {s.IspSL:F1} vs {r.IspSL} {Report.Pct(s.IspSL, r.IspSL)}");
            }
        }

        public static void ReferenceEnginesMass(ModelData data)
        {
            foreach (ReferenceEngine r in ReferenceEngine.All.Where(e => !double.IsNaN(e.MassKg) && !e.SoftMass))
            {
                EngineStats s = EngineModel.Compute(r.Design(), data);
                double tol = double.IsNaN(r.MassTolerance) ? Program.MassTolerance : r.MassTolerance;
                Assert(Math.Abs(s.Mass * 1000 / r.MassKg - 1) <= tol, $"{r.Name}: mass {s.Mass * 1000:F0} kg vs {r.MassKg} {Report.Pct(s.Mass * 1000, r.MassKg)}");
            }
        }
    }
}
