using System;
using System.Collections.Generic;

namespace ProcEngines.Model
{
    /// <summary>A point of the nozzle wall in units of the throat radius: x along the flow (throat at 0), r radius.</summary>
    public struct WallPoint
    {
        public double X, R;
        public WallPoint(double x, double r) { X = x; R = r; }
    }

    /// <summary>
    /// Rao thrust-optimised parabola (TOP) bell, or a 15-degree cone. Normalised to Rt = 1.
    /// Inflection/exit angles vs area ratio and length fraction: digitised Rao curves from
    /// ferram4/ProcEngines BellNozzleParams.cfg (MIT, Copyright (c) 2017 ferram4).
    /// </summary>
    public sealed class NozzleContour
    {
        static readonly double[] LengthFractions = { 0.6, 0.7, 0.8, 0.9, 1.0 };
        static readonly double[] AreaRatios = { 5, 7.5, 10, 15, 20, 25, 30, 35, 40, 45, 50, 75, 100, 150, 250 };
        static readonly double[,] ExitAngle =
        {
            { 20, 16.58, 13.55263158, 11.31578947, 8.815789474 },
            { 18.42, 15, 11.97368421, 9.736842105, 7.5 },
            { 17.24, 13.95, 10.92105263, 8.684210526, 6.578947368 },
            { 15.79, 12.63157895, 9.736842105, 7.763157895, 5.921052632 },
            { 14.74, 11.97368421, 9.210526316, 7.5, 5.657894737 },
            { 14.08, 11.31578947, 8.947368421, 7.368421053, 5.526315789 },
            { 13.55, 10.78947368, 8.684210526, 7.171052632, 5.394736842 },
            { 13.29, 10.46052632, 8.421052632, 6.973684211, 5.131578947 },
            { 13, 10.26315789, 8.157894737, 6.710526316, 5 },
            { 12.89, 10.06578947, 7.894736842, 6.578947368, 4.868421053 },
            { 12.89, 10, 7.763157895, 6.447368421, 4.736842105 },
            { 12.89, 9.671052632, 7.105263158, 5.789473684, 4.078947368 },
            { 12.89, 9.473684211, 6.710526316, 5.394736842, 3.684210526 },
            { 12.89, 9.276315789, 6.315789474, 5, 3.289473684 },
            { 12.89, 9.078947368, 5.921052632, 4.605263158, 2.894736842 },
        };
        static readonly double[,] InflectionAngle =
        {
            { 28.42105263, 25.52631579, 22.76315789, 21.18421053, 20 },
            { 31.05263158, 26.97368421, 24.21052632, 22.76315789, 21.31578947 },
            { 32.36842105, 28.28947368, 25.52631579, 23.81578947, 22.5 },
            { 34.34210526, 30.39473684, 27.76315789, 26.05263158, 24.34210526 },
            { 35.52631579, 31.97368421, 29.34210526, 27.63157895, 25.78947368 },
            { 36.44736842, 33.02631579, 30.26315789, 28.68421053, 26.97368421 },
            { 37.10526316, 33.68421053, 30.92105263, 29.34210526, 27.76315789 },
            { 37.5, 34.07894737, 31.44736842, 29.86842105, 28.42105263 },
            { 37.89473684, 34.47368421, 31.71052632, 30.13157895, 28.94736842 },
            { 38.15789474, 34.73684211, 31.90789474, 30.39473684, 29.21052632 },
            { 38.42105263, 34.86842105, 32.23684211, 30.65789474, 29.47368421 },
            { 39.47368421, 35.39473684, 33.55263158, 31.71052632, 30.52631579 },
            { 40.52631579, 35.92105263, 34.86842105, 32.76315789, 31.57894737 },
            { 41.57894737, 36.44736842, 36.18421053, 33.81578947, 32.63157895 },
            { 42.63157895, 36.97368421, 37.5, 34.86842105, 33.68421053 },
        };

        public readonly double Eps, LengthFraction;
        public readonly bool Conical;
        public readonly double ThetaN, ThetaE;          // deg
        public readonly double Length;                  // throat to exit, in Rt
        public readonly List<WallPoint> Wall = new List<WallPoint>();   // throat (x=0) to exit

        public NozzleContour(double eps, double lengthFraction, bool conical)
        {
            Eps = Math.Max(eps, 1.5);
            LengthFraction = Math.Max(0.6, Math.Min(1.0, lengthFraction));
            Conical = conical;
            double re = Math.Sqrt(Eps);
            double cone = (re - 1) / Math.Tan(15 * Math.PI / 180);   // 15-degree cone length (from Rt), the Rao reference
            if (conical)
            {
                ThetaN = ThetaE = 15;
                // 0.382 Rt arc to 15 degrees, then a straight cone
                double tn = 15 * Math.PI / 180;
                Arc(0.382, 1.382, -90, 15 - 90, 8);
                double xN = 0.382 * Math.Sin(tn), rN = 1.382 - 0.382 * Math.Cos(tn);
                Length = xN + (re - rN) / Math.Tan(tn);
                for (int i = 1; i <= 10; i++) { double t = i / 10.0; Wall.Add(new WallPoint(xN + t * (Length - xN), rN + t * (re - rN))); }
                return;
            }
            ThetaN = Table(InflectionAngle, Eps, LengthFraction);
            ThetaE = Table(ExitAngle, Eps, LengthFraction);
            Length = LengthFraction * cone;
            double n = ThetaN * Math.PI / 180, e = ThetaE * Math.PI / 180;
            Arc(0.382, 1.382, -90, ThetaN - 90, 10);
            double xn = 0.382 * Math.Sin(n), rn = 1.382 - 0.382 * Math.Cos(n);
            double m1 = Math.Tan(n), m2 = Math.Tan(e);
            double c1 = rn - m1 * xn, c2 = re - m2 * Length;
            double qx = (c2 - c1) / (m1 - m2), qr = (m1 * c2 - m2 * c1) / (m1 - m2);
            const int seg = 40;
            for (int i = 1; i <= seg; i++)
            {
                double t = (double)i / seg, u = 1 - t;
                Wall.Add(new WallPoint(u * u * xn + 2 * u * t * qx + t * t * Length, u * u * rn + 2 * u * t * qr + t * t * re));
            }
        }

        void Arc(double R, double rCenter, double th0, double th1, int steps)
        {
            for (int i = 0; i <= steps; i++)
            {
                double th = (th0 + (th1 - th0) * i / steps) * Math.PI / 180;
                Wall.Add(new WallPoint(R * Math.Cos(th), rCenter + R * Math.Sin(th)));
            }
        }

        static double Table(double[,] tab, double eps, double lf)
        {
            double e = Math.Max(AreaRatios[0], Math.Min(AreaRatios[AreaRatios.Length - 1], eps));
            int i = 0;
            while (i < AreaRatios.Length - 2 && e > AreaRatios[i + 1]) i++;
            double u = (Math.Log(e) - Math.Log(AreaRatios[i])) / (Math.Log(AreaRatios[i + 1]) - Math.Log(AreaRatios[i]));
            int j = 0;
            while (j < LengthFractions.Length - 2 && lf > LengthFractions[j + 1]) j++;
            double v = (lf - LengthFractions[j]) / (LengthFractions[j + 1] - LengthFractions[j]);
            double a = (1 - v) * tab[i, j] + v * tab[i, j + 1], b = (1 - v) * tab[i + 1, j] + v * tab[i + 1, j + 1];
            return (1 - u) * a + u * b;
        }

        /// <summary>Divergence efficiency (1 + cos(exit angle)) / 2. For a bell the exit angle is the wall angle at the lip.</summary>
        public double DivergenceEfficiency => (1 + Math.Cos(ThetaE * Math.PI / 180)) / 2;

        /// <summary>Wetted wall area between two area ratios, in Rt^2.</summary>
        public double WallArea(double epsFrom, double epsTo)
        {
            double area = 0;
            for (int i = 1; i < Wall.Count; i++)
            {
                WallPoint a = Wall[i - 1], b = Wall[i];
                double ea = a.R * a.R, eb = b.R * b.R;
                if (eb <= epsFrom || ea >= epsTo) continue;
                double ds = Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.R - a.R) * (b.R - a.R));
                area += Math.PI * (a.R + b.R) * ds;   // frustum lateral area
            }
            return area;
        }
    }

    /// <summary>Boundary-layer skin-friction loss along the divergent wall (flat-plate turbulent friction, 1-D core flow).</summary>
    public static class NozzleFriction
    {
        const double R = 8314.46;   // J/(kmol K)

        /// <summary>Returns the fractional loss of vacuum thrust (e.g. 0.008 = 0.8 %).</summary>
        public static double ThrustLoss(NozzleContour c, Propellant p, double of, double pcBar, double rtMeters, double idealIspVac, double massFlow)
        {
            ChamberState ch = p.Chamber(of, pcBar);
            double g = ch.Gamma;
            double force = 0, x = 0.001 * rtMeters;
            for (int i = 1; i < c.Wall.Count; i++)
            {
                WallPoint a = c.Wall[i - 1], b = c.Wall[i];
                double ds = Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.R - a.R) * (b.R - a.R)) * rtMeters;
                x += ds;
                double rMid = 0.5 * (a.R + b.R);
                double eps = Math.Max(rMid * rMid, 1.0);
                double M = Math.Max(p.Mach(of, pcBar, eps), 1.0);
                double pStatic = p.PressureRatio(of, pcBar, eps) * pcBar * 1e5;
                double T = ch.Tc / (1 + 0.5 * (g - 1) * M * M);
                double rho = pStatic * ch.MolarMass / (R * T);
                double V = M * Math.Sqrt(g * R * T / ch.MolarMass);
                double mu = Viscosity(ch.MolarMass, T);
                double re = Math.Max(rho * V * x / mu, 1e3);
                // turbulent flat plate, with a reference-temperature style compressibility correction
                double cf = 0.074 * Math.Pow(re, -0.2) / Math.Pow(1 + 0.5 * (g - 1) * 0.9 * M * M, 0.5);
                double cosWall = (b.X - a.X) * rtMeters / Math.Max(ds, 1e-12);
                force += 0.5 * rho * V * V * cf * 2 * Math.PI * rMid * rtMeters * ds * cosWall;
            }
            double idealThrust = massFlow * idealIspVac * 9.80665;
            return Math.Min(force / Math.Max(idealThrust, 1e-9), 0.2);
        }

        /// <summary>Dynamic viscosity of combustion gas (Pa s), Huzel and Huang's correlation mu = 46.6e-10 M^0.5 T^0.6 (lbm/in s, T in R).</summary>
        public static double Viscosity(double molarMass, double tempK)
        {
            double tR = tempK * 1.8;
            double lbmPerInS = 46.6e-10 * Math.Sqrt(molarMass) * Math.Pow(tR, 0.6);
            return lbmPerInS * 17.858;   // lbm/(in s) -> kg/(m s)
        }
    }
}
