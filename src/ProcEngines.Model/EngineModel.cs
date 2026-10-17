using System;
using System.Collections.Generic;

namespace ProcEngines.Model
{
    /// <summary>Player inputs. Immutable once built.</summary>
    public sealed class EngineDesign
    {
        public string Propellant = "Kerolox";
        public string Cycle = "GasGenerator";
        public string Tech = "Mid";
        public double ThrustVac = 500;        // kN
        public double Pc = 70;                // bar
        public double Eps = 20;
        public double Of = double.NaN;        // NaN = the propellant's default
        public double LengthFraction = 0.8;   // of a 15-degree cone
        public bool Conical;
        public double GimbalRange = 5;        // deg
        // Radiatively cooled nozzle extension beyond the tech level's regen limit (vacuum engines). Without it the
        // whole nozzle is regeneratively (or dump) cooled, like sea-level engines.
        public bool RadiativeExtension;
    }

    public struct CurveKey
    {
        public double Pressure, Isp;          // atm, s
        public double Tangent;                // dIsp/dp (s/atm), used as both in- and out-tangent of KSP's Hermite FloatCurve
        public CurveKey(double p, double isp) { Pressure = p; Isp = isp; Tangent = 0; }

        /// <summary>KSP FloatCurve evaluation between two keys (cubic Hermite with the keys' tangents).</summary>
        public static double Hermite(CurveKey a, CurveKey b, double p)
        {
            double h = b.Pressure - a.Pressure, t = (p - a.Pressure) / h, t2 = t * t, t3 = t2 * t;
            return (2 * t3 - 3 * t2 + 1) * a.Isp + (t3 - 2 * t2 + t) * h * a.Tangent + (-2 * t3 + 3 * t2) * b.Isp + (t3 - t2) * h * b.Tangent;
        }
    }

    /// <summary>Outputs. Saved with the part (STATS) and applied as-is in flight.</summary>
    public sealed class EngineStats
    {
        public const int ModelVersion = 1;
        public double ThrustVac, ThrustSL;              // kN
        public double IspVac, IspSL;                    // s, whole engine (turbine exhaust included)
        public double MassFlow;                         // kg/s
        public double Mass;                             // t
        public double Cost;
        public double Of, Pc, Eps;
        public double ThroatRadius, ExitRadius, ChamberRadius, ChamberLength, ConvergentLength, NozzleLength;   // m
        public double TotalLength => ChamberLength + ConvergentLength + NozzleLength;
        public double ExitDiameter => 2 * ExitRadius;
        public double ThetaN, ThetaE;
        public double EtaCStar, EtaDivergence, FrictionLoss, TurbineFlowFraction, TurbineExhaustIsp;
        public double PumpPower;                        // kW
        public double ExpanderPowerMargin = double.NaN;  // available / needed
        public double ExitPressure;                     // bar
        public bool SeparatedAtSeaLevel;
        public double ThrottleMin;
        public int Ignitions;
        public readonly List<CurveKey> AtmosphereCurve = new List<CurveKey>();
        public readonly Dictionary<string, double> MassBreakdown = new Dictionary<string, double>();   // kg
        public readonly List<string> Warnings = new List<string>();

        public double TwrVac => ThrustVac / (Mass * 9.80665);
    }

    /// <summary>The engine model: thermochemistry tables + nozzle losses + cycle + mass model.</summary>
    public static class EngineModel
    {
        const double G0 = 9.80665;
        static readonly double[] CurvePressures = { 0, 0.05, 0.1, 0.2, 0.35, 0.5, 0.75, 1.0, 1.5, 2, 3, 5, 10 };

        public static EngineStats Compute(EngineDesign d, ModelData data)
        {
            if (!data.Propellants.TryGetValue(d.Propellant, out Propellant prop)) throw new ArgumentException("unknown propellant " + d.Propellant);
            if (!data.Cycles.TryGetValue(d.Cycle, out Cycle cyc)) throw new ArgumentException("unknown cycle " + d.Cycle);
            if (!data.TechLevels.TryGetValue(d.Tech, out TechLevel tech)) throw new ArgumentException("unknown tech level " + d.Tech);
            Balance bal = data.Balance;
            var s = new EngineStats();

            // ---- inputs, clamped to the tables (hard minimums: no degenerate geometry)
            double of = double.IsNaN(d.Of) ? prop.OfDefault : Clamp(d.Of, prop.OfMin, prop.OfMax);
            double pc = Clamp(d.Pc, prop.PcMin, prop.PcMax);
            double eps = Clamp(d.Eps, 1.5, prop.EpsMax);
            double thrust = Clamp(d.ThrustVac, 0.05, 50000) * 1000;   // N
            s.Of = of; s.Pc = pc; s.Eps = eps;
            if (pc < cyc.PcMin || pc > cyc.PcMax) s.Warnings.Add($"Pc {pc:F0} bar outside the {cyc.Name} range {cyc.PcMin:F0}-{cyc.PcMax:F0}");
            if (cyc.Propellants.Length > 0 && Array.IndexOf(cyc.Propellants, prop.Name) < 0) s.Warnings.Add($"{cyc.Name} does not support {prop.Name}");

            ChamberState ch = prop.Chamber(of, pc);
            var nozzle = new NozzleContour(eps, d.LengthFraction, d.Conical);
            s.ThetaN = nozzle.ThetaN; s.ThetaE = nozzle.ThetaE;

            double etaC = bal.EtaCStar.TryGetValue(prop.Name, out double e0) ? e0 : 0.97;
            etaC = Math.Min(etaC + tech.EtaCStarBonus, 0.995);
            double etaDiv = nozzle.DivergenceEfficiency;
            double ispIdeal = prop.IdealIspVac(of, pc, eps);
            s.EtaCStar = etaC; s.EtaDivergence = etaDiv;

            // ---- cycle: pump work per kg of propellant pumped, turbine flow fraction (open cycles)
            double pumpWork = 0;   // J/kg
            if (cyc.HasPumps)
            {
                double dp = (cyc.PumpDpFactor * pc + cyc.PumpDpMin) * 1e5;
                double fuelFrac = prop.IsNuclear ? 1 : 1 / (1 + of);
                double volPerKg = fuelFrac / prop.FuelDensity + (1 - fuelFrac) / (prop.IsNuclear ? 1e9 : prop.OxDensity);
                pumpWork = dp * volPerKg / tech.PumpEfficiency;
            }
            double turbineFrac = 0, ispExhaust = 0;
            if (cyc.DumpsTurbineGas && pumpWork > 0)
            {
                double gk = (prop.TurbineGasGamma - 1) / prop.TurbineGasGamma;
                double tOut = prop.TurbineGasTemp * Math.Pow(cyc.TurbinePressureRatio, -gk);
                double dh = prop.TurbineGasCp * (prop.TurbineGasTemp - tOut) * tech.TurbineEfficiency;
                turbineFrac = Math.Min(pumpWork / dh, 0.3);
                ispExhaust = 0.9 * Math.Sqrt(2 * prop.TurbineGasCp * tOut) / G0;
            }
            s.TurbineFlowFraction = turbineFrac; s.TurbineExhaustIsp = ispExhaust;

            // ---- size the engine; the friction loss depends on size, so iterate a fixed number of times
            double friction = 0.01, ispMain = 0, mdot = 0, mdotC = 0, at = 0, rt = 0;
            for (int it = 0; it < 4; it++)
            {
                ispMain = ispIdeal * etaC * etaDiv * (1 - friction) * bal.IspMultiplier;
                double ispVac = (1 - turbineFrac) * ispMain + turbineFrac * ispExhaust;
                mdot = thrust / (ispVac * G0);
                mdotC = mdot * (1 - turbineFrac);
                at = mdotC * etaC * ch.CStar / (pc * 1e5);
                rt = Math.Max(Math.Sqrt(at / Math.PI), 0.002);
                friction = NozzleFriction.ThrustLoss(nozzle, prop, of, pc, rt, ispIdeal, mdotC);
            }
            s.FrictionLoss = friction;
            s.MassFlow = mdot;
            s.ThrustVac = thrust / 1000;
            s.IspVac = thrust / (mdot * G0);
            s.PumpPower = pumpWork * mdot / 1000;

            // ---- geometry
            double dtCm = 200 * rt;
            double contraction = 8 * Math.Pow(dtCm, -0.6) + 1.25;   // Huzel and Huang's empirical fit
            double rc = rt * Math.Sqrt(contraction);
            double convLen = (rc - rt) / Math.Tan(30 * Math.PI / 180) + 0.3 * rt;
            double vConv = Math.PI / 3 * convLen * (rc * rc + rc * rt + rt * rt);
            double vc = Math.Max(prop.LStar * at, 1.5 * vConv);
            double lc = Math.Max((vc - vConv) / (Math.PI * rc * rc), 0.5 * rc);
            s.ThroatRadius = rt; s.ExitRadius = rt * Math.Sqrt(eps); s.ChamberRadius = rc;
            s.ChamberLength = lc; s.ConvergentLength = convLen; s.NozzleLength = nozzle.Length * rt;
            double ae = at * eps;
            s.ExitPressure = prop.PressureRatio(of, pc, eps) * pc;

            // ---- atmosphere curve, with flow separation
            foreach (double pAtm in CurvePressures)
            {
                double pa = pAtm * 1.01325;   // bar
                double f = MainThrust(prop, of, pc, eps, pa, mdotC, at, etaC, etaDiv, friction, bal, out bool sep);
                if (pAtm == 1.0) s.SeparatedAtSeaLevel = sep;
                f += mdot * turbineFrac * ispExhaust * G0 * Math.Max(0, 1 - 0.3 * pAtm);
                double isp = f / (mdot * G0);
                if (pAtm > 0 && isp < 0.05 * s.IspVac) { s.AtmosphereCurve.Add(new CurveKey(pAtm, 0.001)); break; }
                s.AtmosphereCurve.Add(new CurveKey(pAtm, isp));
                if (pAtm == 1.0) { s.IspSL = isp; s.ThrustSL = f / 1000; }
            }
            SetMonotoneTangents(s.AtmosphereCurve);
            if (s.SeparatedAtSeaLevel) s.Warnings.Add("flow separates in the nozzle at sea level");

            // ---- expander: turbine power from heat picked up in the chamber and throat region
            if (cyc.ExpanderHeatLimited)
            {
                double aHot = 2 * Math.PI * rc * lc + Math.PI * (rc + rt) * Math.Sqrt(convLen * convLen + (rc - rt) * (rc - rt))
                              + nozzle.WallArea(1, Math.Min(eps, 2)) * rt * rt;   // chamber + throat region: grows ~ sqrt(F), so the cycle caps out
                double available = bal.ExpanderHeatFlux * 1e6 * Math.Pow(pc / 50, 0.8) * aHot * tech.TurbineEfficiency * prop.CoolantQuality;
                s.ExpanderPowerMargin = available / Math.Max(pumpWork * mdot, 1);
                if (s.ExpanderPowerMargin < 1) s.Warnings.Add($"expander cycle: jacket heat covers only {s.ExpanderPowerMargin:P0} of the pump power");
            }

            // ---- mass (kg)
            double pcMPa = pc / 10;
            double aChamber = 2 * Math.PI * rc * lc + Math.PI * (rc + rt) * Math.Sqrt(convLen * convLen + (rc - rt) * (rc - rt));
            double regenTo = d.RadiativeExtension ? Math.Min(eps, tech.RegenEpsMax) : eps;
            double aRegen = nozzle.WallArea(1, regenTo) * rt * rt, aRad = nozzle.WallArea(regenTo, eps) * rt * rt;
            double sf = tech.StructureFactor;
            var mb = s.MassBreakdown;
            mb["chamber"] = sf * (bal.ChamberPressureVessel * pcMPa * vc + bal.ChamberWall * aChamber);
            mb["injector"] = sf * bal.Injector * Math.Pow(2 * rc, 3) * Math.Sqrt(pc);
            double sizeFactor = Math.Pow(Math.Max(s.ExitRadius, 0.05), bal.NozzleSizeExponent);
            mb["nozzle"] = sf * sizeFactor * (bal.NozzleRegen * aRegen + bal.NozzleRadiative * aRad);
            double propTurbo = bal.TurbomachineryByPropellant.TryGetValue(prop.Name, out double pt) ? pt : prop.TurbomachineryFactor;
            mb["turbomachinery"] = cyc.HasPumps ? sf * cyc.TurbomachineryFactor * propTurbo * bal.Turbomachinery * Math.Pow(Math.Max(s.PumpPower, 1), bal.TurbomachineryExponent) : 0;
            mb["pressurant"] = cyc.PressurantFactor * sf * PressurantMass(prop, of, pc, mdot, bal);
            mb["gimbal"] = bal.GimbalMass * s.ThrustVac * d.GimbalRange;
            double sum = 0;
            foreach (double v in mb.Values) sum += v;
            mb["misc"] = bal.MiscFraction * sum + bal.MiscConstant;
            sum += mb["misc"];
            s.Mass = Math.Max(sum * bal.MassMultiplier, 1) / 1000;

            // ---- cost (placeholder until the Phase 7 balance pass)
            s.Cost = bal.CostMultiplier * (150 + 0.9 * sum + 0.02 * s.ThrustVac * Math.Pow(pc / 50, 1.2));

            s.ThrottleMin = cyc.ThrottleMin;
            s.Ignitions = cyc.Ignitions;
            return s;
        }

        /// <summary>Helium + composite bottles (kg) to expel one rated burn's propellant volume at tank pressure.</summary>
        public static double PressurantMass(Propellant prop, double of, double pcBar, double mdot, Balance bal)
        {
            double volume = mdot * bal.PressurantBurnTime / prop.BulkDensity(of);                    // m^3 of propellant
            double pTank = (bal.PressurantTankFactor * pcBar + bal.PressurantTankMargin) * 1e5;      // Pa
            double pv = bal.PressurantExpansion * pTank * volume;                                     // J of stored p*V
            double bottles = pv / bal.PressurantBottlePerformance;
            double helium = pv / (2077.1 * bal.PressurantGasTemp);
            return bottles + helium;
        }

        /// <summary>Main-chamber thrust (N) at ambient pressure pa (bar). If the wall pressure drops below the separation
        /// pressure, the flow leaves the wall at eps_sep and the nozzle behaves as if it ended there.</summary>
        static double MainThrust(Propellant prop, double of, double pc, double eps, double pa, double mdotC, double at,
            double etaC, double etaDiv, double friction, Balance bal, out bool separated)
        {
            separated = false;
            double effEps = eps;
            if (pa > 0)
            {
                double pSep = bal.SeparationK * pa * Math.Pow(pc / pa, bal.SeparationExponent);
                if (prop.PressureRatio(of, pc, eps) * pc < pSep)
                {
                    separated = true;
                    effEps = Math.Max(prop.AreaRatioAtPressure(of, pc, pSep / pc, eps), 1.0);
                }
            }
            double isp = prop.IdealIspVac(of, pc, effEps) * etaC * etaDiv * (1 - friction) * bal.IspMultiplier;
            return mdotC * isp * G0 - at * effEps * pa * 1e5;
        }

        /// <summary>Fritsch-Carlson tangents: the Hermite curve through the keys never overshoots them.</summary>
        static void SetMonotoneTangents(List<CurveKey> k)
        {
            int n = k.Count;
            if (n < 2) return;
            var delta = new double[n - 1];
            for (int i = 0; i < n - 1; i++) delta[i] = (k[i + 1].Isp - k[i].Isp) / (k[i + 1].Pressure - k[i].Pressure);
            var m = new double[n];
            m[0] = delta[0]; m[n - 1] = delta[n - 2];
            for (int i = 1; i < n - 1; i++) m[i] = delta[i - 1] * delta[i] <= 0 ? 0 : (delta[i - 1] + delta[i]) / 2;
            for (int i = 0; i < n - 1; i++)
            {
                if (delta[i] == 0) { m[i] = m[i + 1] = 0; continue; }
                double a = m[i] / delta[i], b = m[i + 1] / delta[i], r = a * a + b * b;
                if (r > 9) { double t = 3 / Math.Sqrt(r); m[i] = t * a * delta[i]; m[i + 1] = t * b * delta[i]; }
            }
            for (int i = 0; i < n; i++) { CurveKey c = k[i]; c.Tangent = m[i]; k[i] = c; }
        }

        static double Clamp(double v, double lo, double hi) => v < lo ? lo : v > hi ? hi : v;
    }
}
