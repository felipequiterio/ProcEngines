using System;
using System.Collections.Generic;
using System.Linq;
using ProcEngines.Model;

namespace ProcEngines.Model.Tests
{
    /// <summary>
    /// Fits the free Balance.cfg coefficients to the reference engines (Nelder-Mead on scaled errors:
    /// mass error / 10 %, Isp error / 3 %) and prints the values to paste into Balance.cfg.
    /// "cv" mode runs leave-one-out cross-validation to measure how well the model predicts an engine it was not fitted on.
    /// Efficiencies and the well-known structural constants are fixed by physics, not fitted.
    /// </summary>
    public static class Fit
    {
        sealed class Param
        {
            public string Name; public Func<ModelData, double> Get; public Action<ModelData, double> Set; public double Lo, Hi; public bool Linear;
        }

        public static bool Full;

        static List<Param> Params() => Full ? FullParams() : new List<Param>
        {
            P("chamberPressureVessel", d => d.Balance.ChamberPressureVessel, (d, v) => d.Balance.ChamberPressureVessel = v, 20, 2000),
            P("nozzleRegen", d => d.Balance.NozzleRegen, (d, v) => d.Balance.NozzleRegen = v, 5, 80),
            P("turbomachinery", d => d.Balance.Turbomachinery, (d, v) => d.Balance.Turbomachinery = v, 0.05, 10),
            P("turbomachinery.Hydrolox", d => d.Balance.TurbomachineryByPropellant.TryGetValue("Hydrolox", out double x) ? x : 1, (d, v) => d.Balance.TurbomachineryByPropellant["Hydrolox"] = v, 0.7, 3),
        };

        static List<Param> FullParams() => new List<Param>
        {
            P("chamberPressureVessel", d => d.Balance.ChamberPressureVessel, (d, v) => d.Balance.ChamberPressureVessel = v, 20, 2000),
            P("nozzleRegen", d => d.Balance.NozzleRegen, (d, v) => d.Balance.NozzleRegen = v, 5, 80),
            P("turbomachinery", d => d.Balance.Turbomachinery, (d, v) => d.Balance.Turbomachinery = v, 0.05, 10),
            P("turbomachineryExponent", d => d.Balance.TurbomachineryExponent, (d, v) => d.Balance.TurbomachineryExponent = v, 0.5, 1.0, true),
            P("turbomachinery.Hydrolox", d => d.Balance.TurbomachineryByPropellant.TryGetValue("Hydrolox", out double x) ? x : 1, (d, v) => d.Balance.TurbomachineryByPropellant["Hydrolox"] = v, 0.7, 3),
            P("separationK", d => d.Balance.SeparationK, (d, v) => d.Balance.SeparationK = v, 0.3, 0.7, true),
            P("Early.structureFactor", d => d.TechLevels["Early"].StructureFactor, (d, v) => d.TechLevels["Early"].StructureFactor = v, 0.8, 2, true),
            P("Late.structureFactor", d => d.TechLevels["Late"].StructureFactor, (d, v) => d.TechLevels["Late"].StructureFactor = v, 0.4, 1.0, true),
        };

        static Param P(string n, Func<ModelData, double> g, Action<ModelData, double> s, double lo, double hi, bool linear = false) =>
            new Param { Name = n, Get = g, Set = s, Lo = lo, Hi = hi, Linear = linear };

        public static double Cost(ModelData data, Func<ReferenceEngine, bool> include)
        {
            double sum = 0;
            foreach (ReferenceEngine r in ReferenceEngine.All.Where(include))
            {
                EngineStats s = EngineModel.Compute(r.Design(), data);
                double Sq(double model, double real, double tol) => double.IsNaN(real) ? 0 : Math.Pow((model / real - 1) / tol, 2);
                if (!r.SoftIsp) sum += Sq(s.IspVac, r.IspVac, Program.IspTolerance);
                if (!r.SoftIsp) sum += Sq(s.IspSL, r.IspSL, Program.IspTolerance);
                if (!double.IsNaN(r.MassKg) && !r.SoftMass) sum += Math.Pow(Math.Log(s.Mass * 1000 / r.MassKg) / Program.MassTolerance, 2);
            }
            return sum;
        }

        static double[] Optimise(ModelData data, List<Param> ps, double[] start, Func<ReferenceEngine, bool> include, int restarts)
        {
            int n = ps.Count;
            Func<int, double, double> toVal = (i, u) =>
            {
                u = Math.Max(0, Math.Min(1, u));
                Param p = ps[i];
                return p.Linear ? p.Lo + u * (p.Hi - p.Lo) : Math.Exp(Math.Log(p.Lo) + u * (Math.Log(p.Hi) - Math.Log(p.Lo)));
            };
            Func<double[], double> f = u =>
            {
                for (int i = 0; i < n; i++) ps[i].Set(data, toVal(i, u[i]));
                return Cost(data, include) + u.Sum(x => x < 0 ? -x : x > 1 ? x - 1 : 0) * 100;
            };
            double[] best = (double[])start.Clone();
            for (int restart = 0; restart < restarts; restart++) best = NelderMead(f, best, 0.15 / (restart + 1), 4000);
            f(best);
            return best;
        }

        static double[] StartPoint(ModelData data, List<Param> ps) => ps.Select(p => p.Linear
            ? (p.Get(data) - p.Lo) / (p.Hi - p.Lo)
            : (Math.Log(p.Get(data)) - Math.Log(p.Lo)) / (Math.Log(p.Hi) - Math.Log(p.Lo))).ToArray();

        static bool Reliable(ReferenceEngine e) => true;

        public static void Run(ModelData data)
        {
            List<Param> ps = Params();
            Console.WriteLine($"start cost {Cost(data, Reliable):F3}");
            Optimise(data, ps, StartPoint(data, ps), Reliable, 6);
            Console.WriteLine($"final cost {Cost(data, Reliable):F3}");
            foreach (Param p in ps) Console.WriteLine($"\t{p.Name} = {p.Get(data):G4}");
            Report.Print(data);
        }

        /// <summary>Leave-one-out: refit without engine i, predict engine i. Reports the predictive mass error.</summary>
        public static void CrossValidate(ModelData data)
        {
            List<Param> ps = Params();
            double[] start = StartPoint(data, ps);
            var errs = new List<double>();
            foreach (ReferenceEngine r in ReferenceEngine.All.Where(e => !double.IsNaN(e.MassKg) && !e.SoftMass))
            {
                Optimise(data, ps, start, e => e != r, 3);
                EngineStats s = EngineModel.Compute(r.Design(), data);
                double err = s.Mass * 1000 / r.MassKg - 1;
                errs.Add(err);
                Console.WriteLine($"{r.Name,-12} predicted {s.Mass * 1000,7:F0} kg vs {r.MassKg,6:F0}  {100 * err:+0.0;-0.0}%   Isp vac {Report.Pct(s.IspVac, r.IspVac)}");
            }
            double rms = Math.Sqrt(errs.Average(e => e * e));
            Console.WriteLine($"leave-one-out mass error: RMS {100 * rms:F1} %, median |err| {100 * errs.Select(Math.Abs).OrderBy(x => x).ElementAt(errs.Count / 2):F1} %, max |err| {100 * errs.Max(Math.Abs):F1} %, within 10 %: {errs.Count(e => Math.Abs(e) <= 0.1)}/{errs.Count}, within 20 %: {errs.Count(e => Math.Abs(e) <= 0.2)}/{errs.Count}");
        }

        static double[] NelderMead(Func<double[], double> f, double[] x0, double step, int maxIter)
        {
            int n = x0.Length;
            var pts = new double[n + 1][];
            var val = new double[n + 1];
            for (int i = 0; i <= n; i++)
            {
                pts[i] = (double[])x0.Clone();
                if (i > 0) pts[i][i - 1] += step;
                val[i] = f(pts[i]);
            }
            for (int it = 0; it < maxIter; it++)
            {
                int[] order = Enumerable.Range(0, n + 1).OrderBy(i => val[i]).ToArray();
                pts = order.Select(i => pts[i]).ToArray();
                val = order.Select(i => val[i]).ToArray();
                if (val[n] - val[0] < 1e-9) break;
                var c = new double[n];
                for (int i = 0; i < n; i++) for (int j = 0; j < n; j++) c[j] += pts[i][j] / n;
                double[] Lerp(double t) => c.Select((cj, j) => cj + t * (pts[n][j] - cj)).ToArray();
                double[] xr = Lerp(-1); double fr = f(xr);
                if (fr < val[0])
                {
                    double[] xe = Lerp(-2); double fe = f(xe);
                    if (fe < fr) { pts[n] = xe; val[n] = fe; } else { pts[n] = xr; val[n] = fr; }
                }
                else if (fr < val[n - 1]) { pts[n] = xr; val[n] = fr; }
                else
                {
                    double[] xc = Lerp(fr < val[n] ? -0.5 : 0.5); double fc = f(xc);
                    if (fc < Math.Min(fr, val[n])) { pts[n] = xc; val[n] = fc; }
                    else
                        for (int i = 1; i <= n; i++)
                        {
                            pts[i] = pts[i].Select((x, j) => pts[0][j] + 0.5 * (x - pts[0][j])).ToArray();
                            val[i] = f(pts[i]);
                        }
                }
            }
            int b = Enumerable.Range(0, n + 1).OrderBy(i => val[i]).First();
            return pts[b];
        }
    }
}
