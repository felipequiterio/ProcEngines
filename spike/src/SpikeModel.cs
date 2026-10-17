using System;
using System.Globalization;

namespace ProcEngineSpike
{
    /// <summary>Inputs. Kept tiny for the spike: thrust, chamber pressure, expansion ratio.</summary>
    public struct SpikeDesign
    {
        public float thrustVac;   // kN
        public float pc;          // bar
        public float eps;         // Ae/At

        public override string ToString() =>
            string.Format(CultureInfo.InvariantCulture, "F={0}kN Pc={1}bar eps={2}", thrustVac, pc, eps);
    }

    /// <summary>Outputs. Saved in the STATS node and applied as-is in flight.</summary>
    public class SpikeStats
    {
        public float maxThrust, minThrust, ispVac, ispSL, mdot, mass, cost;
        public float rt, rc, re, mountR, mountH, lc;      // m
        public float xInj, xCone, xN, rN, qx, qr, ln;      // m, nozzle frame: x along flow, throat at 0
        public float thetaN, thetaE;                       // deg
        public float[] curveP, curveIsp;                   // atmosphereCurve keys

        public float ExitDiameter => 2f * re;
        public float YOf(float x) => -(x - xInj) - mountH;  // part frame: y up, origin at the mount top
        public float YThroat => YOf(0f);
        public float YExit => YOf(ln);

        public static readonly string[] Names =
        {
            "maxThrust", "minThrust", "ispVac", "ispSL", "mdot", "mass", "cost",
            "rt", "rc", "re", "mountR", "mountH", "lc", "xInj", "xCone", "xN", "rN", "qx", "qr", "ln", "thetaN", "thetaE",
        };

        float[] Pack() => new[]
        {
            maxThrust, minThrust, ispVac, ispSL, mdot, mass, cost,
            rt, rc, re, mountR, mountH, lc, xInj, xCone, xN, rN, qx, qr, ln, thetaN, thetaE,
        };

        void Unpack(float[] v)
        {
            int i = 0;
            maxThrust = v[i++]; minThrust = v[i++]; ispVac = v[i++]; ispSL = v[i++]; mdot = v[i++]; mass = v[i++]; cost = v[i++];
            rt = v[i++]; rc = v[i++]; re = v[i++]; mountR = v[i++]; mountH = v[i++]; lc = v[i++];
            xInj = v[i++]; xCone = v[i++]; xN = v[i++]; rN = v[i++]; qx = v[i++]; qr = v[i++]; ln = v[i++];
            thetaN = v[i++]; thetaE = v[i++];
        }

        public void Save(ConfigNode node)
        {
            float[] v = Pack();
            for (int i = 0; i < Names.Length; i++) node.AddValue(Names[i], F(v[i]));
            ConfigNode c = node.AddNode("atmosphereCurve");
            for (int i = 0; i < curveP.Length; i++) c.AddValue("key", F(curveP[i]) + " " + F(curveIsp[i]));
        }

        public static SpikeStats Load(ConfigNode node)
        {
            var s = new SpikeStats();
            float[] v = new float[Names.Length];
            for (int i = 0; i < Names.Length; i++)
            {
                string str = node.GetValue(Names[i]);
                if (str == null) return null;   // incomplete STATS: caller recomputes
                v[i] = P(str);
            }
            s.Unpack(v);
            ConfigNode c = node.GetNode("atmosphereCurve");
            string[] keys = c?.GetValues("key") ?? new string[0];
            s.curveP = new float[keys.Length];
            s.curveIsp = new float[keys.Length];
            for (int i = 0; i < keys.Length; i++)
            {
                string[] parts = keys[i].Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                s.curveP[i] = P(parts[0]);
                s.curveIsp[i] = P(parts[1]);
            }
            return s;
        }

        // Invariant culture, round-trip format: a craft saved on a comma-decimal locale must load anywhere.
        public static string F(float f) => f.ToString("R", CultureInfo.InvariantCulture);
        public static float P(string s) => float.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);
    }

    /// <summary>Crude ideal-rocket model, just enough to drive geometry and engine stats.</summary>
    public static class SpikeModel
    {
        public const int Version = 1;
        const float G0 = 9.80665f;
        const float Gamma = 1.2f;
        const float CStar = 1750f;   // m/s, kerolox-ish
        const float EtaC = 0.97f;
        const float Deg2Rad = (float)(Math.PI / 180.0);

        public static SpikeStats Compute(SpikeDesign d)
        {
            float eps = Clamp(d.eps, 3f, 400f);
            float pcPa = Clamp(d.pc, 5f, 400f) * 1e5f;
            float F = Clamp(d.thrustVac, 1f, 20000f) * 1000f;

            float me = ExitMach(eps);
            float g = Gamma;
            float pr = (float)Math.Pow(1 + 0.5 * (g - 1) * me * me, -g / (g - 1));   // pe/pc
            double a = 2 * g * g / (g - 1) * Math.Pow(2 / (g + 1), (g + 1) / (g - 1)) * (1 - Math.Pow(pr, (g - 1) / g));
            float cfVac = (float)Math.Sqrt(a) + eps * pr;

            var s = new SpikeStats();
            s.ispVac = EtaC * CStar * cfVac / G0;
            float at = F / (cfVac * pcPa);
            float ae = eps * at;
            s.mdot = F / (s.ispVac * G0);
            s.maxThrust = F / 1000f;
            s.minThrust = 0.4f * s.maxThrust;
            float fsl = F - ae * 101325f;
            s.ispSL = Math.Max(fsl / (s.mdot * G0), 0.01f * s.ispVac);
            float pZero = F / (ae * 101325f);   // ambient pressure (atm) where thrust reaches zero
            s.curveP = new[] { 0f, 1f, Clamp(pZero, 1.5f, 20f) };
            s.curveIsp = new[] { s.ispVac, s.ispSL, 0.001f };

            s.rt = Math.Max((float)Math.Sqrt(at / Math.PI), 0.01f);
            s.re = (float)Math.Sqrt(eps) * s.rt;
            s.rc = (float)Math.Sqrt(3.0) * s.rt;
            s.lc = 2.0f * s.rc;
            s.mountR = 0.6f * s.rc;
            s.mountH = 0.35f * s.rc;

            float f = Clamp01((float)((Math.Log(eps) - Math.Log(4)) / (Math.Log(100) - Math.Log(4))));
            s.thetaN = Lerp(21f, 35f, f);
            s.thetaE = Lerp(14f, 7f, f);
            s.ln = 0.8f * ((float)Math.Sqrt(eps) - 1f) * s.rt / (float)Math.Tan(15 * Math.PI / 180);

            float tn = s.thetaN * Deg2Rad, te = s.thetaE * Deg2Rad;
            s.xN = 0.382f * s.rt * (float)Math.Sin(tn);
            s.rN = s.rt * (1.382f - 0.382f * (float)Math.Cos(tn));
            float m1 = (float)Math.Tan(tn), m2 = (float)Math.Tan(te);
            float c1 = s.rN - m1 * s.xN, c2 = s.re - m2 * s.ln;
            s.qx = (c2 - c1) / (m1 - m2);
            s.qr = (m1 * c2 - m2 * c1) / (m1 - m2);

            float xConv = -1.0607f * s.rt;            // start of the 1.5 Rt upstream arc (-135 deg)
            float rConv = 2.5f * s.rt - 1.0607f * s.rt;
            s.xCone = xConv - (s.rc - rConv);         // 45 deg cone from the chamber
            s.xInj = s.xCone - s.lc;

            s.mass = Math.Max(s.maxThrust / (G0 * 90f) + 0.05f * ae, 0.05f);
            s.cost = 200f + 1.5f * s.maxThrust + 4f * d.pc;
            return s;
        }

        static float ExitMach(float eps)
        {
            double g = Gamma, lo = 1.0001, hi = 30;
            for (int i = 0; i < 80; i++)   // fixed iteration count: never an open-ended loop
            {
                double m = 0.5 * (lo + hi);
                double ar = 1 / m * Math.Pow(2 / (g + 1) * (1 + 0.5 * (g - 1) * m * m), (g + 1) / (2 * (g - 1)));
                if (ar > eps) hi = m; else lo = m;
            }
            return (float)(0.5 * (lo + hi));
        }

        static float Clamp(float v, float lo, float hi) => v < lo ? lo : v > hi ? hi : v;
        static float Clamp01(float v) => Clamp(v, 0f, 1f);
        static float Lerp(float a, float b, float t) => a + (b - a) * t;
    }
}
