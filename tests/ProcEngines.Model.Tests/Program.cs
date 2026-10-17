using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using ProcEngines.Model;

namespace ProcEngines.Model.Tests
{
    public static class Program
    {
        public const double IspTolerance = 0.03, MassTolerance = 0.10;

        public static string DataDir()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "GameData", "ProcEngines", "Data"))) dir = dir.Parent;
            if (dir == null) throw new DirectoryNotFoundException("GameData/ProcEngines/Data not found above " + AppContext.BaseDirectory);
            return Path.Combine(dir.FullName, "GameData", "ProcEngines", "Data");
        }

        public static int Main(string[] args)
        {
            CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
            ModelData data = ModelData.LoadFolder(DataDir());
            string mode = args.Length > 0 ? args[0] : "test";
            Fit.Full = args.Contains("full");
            switch (mode)
            {
                case "report": Report.Print(data); return 0;
                case "fit": Fit.Run(data); return 0;
                case "cv": Fit.CrossValidate(data); return 0;
                case "perf":
                {
                    var d = new EngineDesign { Propellant = "Hydrolox", Cycle = "StagedCombustion", ThrustVac = 2000, Pc = 200, Eps = 69 };
                    for (int i = 0; i < 200; i++) EngineModel.Compute(d, data);
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    for (int i = 0; i < 2000; i++) { d.ThrustVac = 1000 + i; EngineModel.Compute(d, data); }
                    Console.WriteLine($"EngineModel.Compute: {sw.Elapsed.TotalMilliseconds / 2000 * 1000:F0} us per design");
                    sw.Restart(); ModelData.LoadFolder(DataDir()); Console.WriteLine($"load data: {sw.Elapsed.TotalMilliseconds:F0} ms");
                    return 0;
                }
                case "pressurefed":
                    foreach (var (f, pc) in new[] { (30.0, 10.0), (78.0, 21.0), (250.0, 25.0) })
                        foreach (string prop in new[] { "Kerolox", "Hydrolox" })
                        {
                            EngineStats s = EngineModel.Compute(new EngineDesign { Propellant = prop, Cycle = "PressureFed", ThrustVac = f, Pc = pc, Eps = 40 }, data);
                            Console.WriteLine($"{prop} {f} kN Pc {pc}: mass {s.Mass * 1000:F0} kg (pressurant {s.MassBreakdown["pressurant"]:F0}), TWR {s.TwrVac:F1}, Isp {s.IspVac:F0}");
                        }
                    return 0;
                case "expander":
                    foreach (string prop in new[] { "Hydrolox", "Methalox" })
                        foreach (double f in new[] { 50.0, 100, 180, 300, 500, 1000 })
                            Console.WriteLine($"{prop} {f} kN Pc 60 eps 80: margin {EngineModel.Compute(new EngineDesign { Propellant = prop, Cycle = "Expander", ThrustVac = f, Pc = 60, Eps = 80 }, data).ExpanderPowerMargin:F2}");
                    return 0;
                default: return Tests.RunAll(data);
            }
        }
    }

    /// <summary>Reference-engine comparison table.</summary>
    public static class Report
    {
        public static void Print(ModelData data)
        {
            Console.WriteLine($"{"engine",-14} {"cycle",-16} {"Isp vac",15} {"Isp SL",15} {"mass kg",18} {"TWR",5} {"De m",5} {"turb%",5} {"fric%",5} sep");
            foreach (ReferenceEngine r in ReferenceEngine.All)
            {
                EngineStats s = EngineModel.Compute(r.Design(), data);
                string Cmp(double model, double real, string f) => double.IsNaN(real) ? model.ToString(f) + " (-)" : $"{model.ToString(f)} {Pct(model, real)}";
                Console.WriteLine($"{r.Name + (r.SoftIsp || r.SoftMass ? "~" : ""),-14} {r.Cycle,-16} {Cmp(s.IspVac, r.IspVac, "F1"),15} {Cmp(s.IspSL, r.IspSL, "F1"),15} {Cmp(s.Mass * 1000, r.MassKg, "F0"),18} {s.TwrVac,5:F0} {s.ExitDiameter,5:F2} {100 * s.TurbineFlowFraction,5:F1} {100 * s.FrictionLoss,5:F2} {(s.SeparatedAtSeaLevel ? "yes" : "")}");
                Console.WriteLine("    mass: " + string.Join(", ", s.MassBreakdown.Select(kv => $"{kv.Key} {kv.Value:F0}")) +
                                  (double.IsNaN(s.ExpanderPowerMargin) ? "" : $"; expander margin {s.ExpanderPowerMargin:F2}") +
                                  (s.Warnings.Count > 0 ? "; " + string.Join("; ", s.Warnings) : ""));
            }
            Console.WriteLine("~ = soft reference (Isp and/or mass reported only, not fitted or tested)");
        }

        public static string Pct(double model, double real) => $"({100 * (model / real - 1):+0.0;-0.0}%)";
    }
}
