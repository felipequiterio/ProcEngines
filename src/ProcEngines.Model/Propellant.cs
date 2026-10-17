using System;
using System.Collections.Generic;
using System.Linq;

namespace ProcEngines.Model
{
    /// <summary>Ideal (CEA shifting-equilibrium) state at one (O/F, Pc), interpolated.</summary>
    public struct ChamberState
    {
        public double Tc, CStar, MolarMass, Gamma;   // K, m/s, g/mol, chamber gamma_s
    }

    /// <summary>One PROCENG_PROPELLANT: CEA table indexed by O/F (or Tc for NTR), Pc and area ratio.</summary>
    public sealed class Propellant
    {
        public string Name;
        public bool IsNuclear;                // the O/F axis holds chamber temperature (K)
        public double OfDefault;
        public double FuelDensity, OxDensity;  // kg/m^3
        public double LStar;                   // m
        public double CoolantQuality;
        public string Ignition;
        // Gas-generator / preburner gas (fuel-rich), for turbine sizing and the turbine exhaust
        public double TurbineGasCp = 2400, TurbineGasGamma = 1.12, TurbineGasTemp = 1000;
        // Turbopump mass multiplier: low-density fuels need high-head, multi-stage pumps (LH2)
        public double TurbomachineryFactor = 1;

        double[] ofs, pcs, epss, logPcs, logEpss;
        // [of, pc] chamber values; [of, pc, eps] nozzle values
        double[,] tc, cstar, mw, gamma;
        double[,,] isp, pe, mach;

        public double OfMin => ofs[0];
        public double OfMax => ofs[ofs.Length - 1];
        public double PcMin => pcs[0];
        public double PcMax => pcs[pcs.Length - 1];
        public double EpsMax => epss[epss.Length - 1];

        /// <summary>Bulk density of the mixture at O/F (kg/m^3).</summary>
        public double BulkDensity(double of) => IsNuclear ? FuelDensity : (1 + of) / (1 / FuelDensity + of / OxDensity);

        public static Propellant Load(CfgNode n)
        {
            var p = new Propellant
            {
                Name = n.Get("name"),
                IsNuclear = n.Get("ofAxis") == "Tc",
                OfDefault = n.Double("ofDefault"),
                FuelDensity = n.Double("fuelDensity"),
                OxDensity = n.Double("oxDensity", 1),
                LStar = n.Double("lStar", 1),
                CoolantQuality = n.Double("coolantQuality", 0.5),
                Ignition = n.Get("ignition"),
                TurbineGasCp = n.Double("turbineGasCp", 2400),
                TurbineGasGamma = n.Double("turbineGasGamma", 1.12),
                TurbineGasTemp = n.Double("turbineGasTemp", 1000),
                TurbomachineryFactor = n.Double("turbomachineryFactor", 1),
            };
            p.ofs = n.Doubles("of");
            p.pcs = n.Doubles("pc");
            p.epss = n.Doubles("eps");
            p.logPcs = p.pcs.Select(x => Math.Log(x)).ToArray();
            p.logEpss = p.epss.Select(x => Math.Log(x)).ToArray();
            int no = p.ofs.Length, np = p.pcs.Length, ne = p.epss.Length;
            p.tc = new double[no, np]; p.cstar = new double[no, np]; p.mw = new double[no, np]; p.gamma = new double[no, np];
            p.isp = new double[no, np, ne]; p.pe = new double[no, np, ne]; p.mach = new double[no, np, ne];
            var filled = new bool[no, np];
            foreach (CfgNode r in n.NodesNamed("ROW"))
            {
                int i = Array.IndexOf(p.ofs, r.Double("of")), j = Array.IndexOf(p.pcs, r.Double("pc"));
                if (i < 0 || j < 0) throw new FormatException($"{p.Name}: ROW of={r.Get("of")} pc={r.Get("pc")} not on the grid");
                p.tc[i, j] = r.Double("tc"); p.cstar[i, j] = r.Double("cstar"); p.mw[i, j] = r.Double("mw"); p.gamma[i, j] = r.Double("gamma");
                double[] a = r.Doubles("isp"), b = r.Doubles("pe"), c = r.Doubles("mach");
                for (int k = 0; k < ne; k++) { p.isp[i, j, k] = a[k]; p.pe[i, j, k] = Math.Log(b[k]); p.mach[i, j, k] = c[k]; }
                filled[i, j] = true;
            }
            for (int i = 0; i < no; i++)
                for (int j = 0; j < np; j++)
                    if (!filled[i, j]) throw new FormatException($"{p.Name}: missing ROW of={p.ofs[i]} pc={p.pcs[j]}");
            return p;
        }

        // ---- interpolation: linear in O/F, linear in log(Pc), linear in log(eps); clamped to the grid

        static void Bracket(double[] axis, double x, out int i0, out double t)
        {
            if (x <= axis[0]) { i0 = 0; t = 0; return; }
            int n = axis.Length;
            if (x >= axis[n - 1]) { i0 = n - 2; t = 1; return; }
            int i = 0;
            while (i < n - 2 && x > axis[i + 1]) i++;
            i0 = i;
            t = (x - axis[i]) / (axis[i + 1] - axis[i]);
        }

        double Interp2(double[,] tab, double of, double pc)
        {
            Bracket(ofs, of, out int i, out double u);
            Bracket(logPcs, Math.Log(pc), out int j, out double v);
            return (1 - u) * ((1 - v) * tab[i, j] + v * tab[i, j + 1]) + u * ((1 - v) * tab[i + 1, j] + v * tab[i + 1, j + 1]);
        }

        double Interp3(double[,,] tab, double of, double pc, double eps)
        {
            Bracket(ofs, of, out int i, out double u);
            Bracket(logPcs, Math.Log(pc), out int j, out double v);
            Bracket(logEpss, Math.Log(eps), out int k, out double w);
            double At(int a, int b) => (1 - w) * tab[a, b, k] + w * tab[a, b, k + 1];
            return (1 - u) * ((1 - v) * At(i, j) + v * At(i, j + 1)) + u * ((1 - v) * At(i + 1, j) + v * At(i + 1, j + 1));
        }

        public ChamberState Chamber(double of, double pc) => new ChamberState
        {
            Tc = Interp2(tc, of, pc),
            CStar = Interp2(cstar, of, pc),
            MolarMass = Interp2(mw, of, pc),
            Gamma = Interp2(gamma, of, pc),
        };

        /// <summary>Ideal shifting-equilibrium vacuum Isp (s) at area ratio eps (1 = throat).</summary>
        public double IdealIspVac(double of, double pc, double eps) => Interp3(isp, of, pc, eps);

        /// <summary>Local static pressure / Pc at area ratio eps.</summary>
        public double PressureRatio(double of, double pc, double eps) => Math.Exp(Interp3(pe, of, pc, eps));

        public double Mach(double of, double pc, double eps) => Interp3(mach, of, pc, eps);

        /// <summary>Area ratio where p/Pc falls to <paramref name="ratio"/> (bisection in log eps, fixed iterations).</summary>
        public double AreaRatioAtPressure(double of, double pc, double ratio, double epsMax)
        {
            double lo = 0, hi = Math.Log(epsMax);
            if (PressureRatio(of, pc, epsMax) >= ratio) return epsMax;
            for (int it = 0; it < 60; it++)
            {
                double m = 0.5 * (lo + hi);
                if (PressureRatio(of, pc, Math.Exp(m)) > ratio) lo = m; else hi = m;
            }
            return Math.Exp(0.5 * (lo + hi));
        }
    }
}
