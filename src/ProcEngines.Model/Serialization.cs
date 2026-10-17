using System;
using System.Globalization;

namespace ProcEngines.Model
{
    /// <summary>
    /// DESIGN and STATS nodes (DESIGN.md 3.2). Invariant culture, round-trip formatting: a craft saved on a
    /// comma-decimal locale loads identically everywhere, and every LMP client applies byte-identical stats.
    /// </summary>
    public static class Serialization
    {
        public const int SchemaVersion = 1;

        public static string F(double v) => v.ToString("R", CultureInfo.InvariantCulture);
        static double D(CfgNode n, string key, double fallback) => n.Has(key) ? CfgNode.ParseDouble(n.Get(key)) : fallback;
        static bool B(CfgNode n, string key, bool fallback) => n.Has(key) ? string.Equals(n.Get(key), "true", StringComparison.OrdinalIgnoreCase) : fallback;

        public static CfgNode SaveDesign(EngineDesign d)
        {
            var n = new CfgNode("DESIGN");
            n.Add("propellant", d.Propellant);
            n.Add("cycle", d.Cycle);
            n.Add("thrustVac", F(d.ThrustVac));
            n.Add("pc", F(d.Pc));
            n.Add("eps", F(d.Eps));
            n.Add("mixtureRatio", double.IsNaN(d.Of) ? "default" : F(d.Of));
            n.Add("lengthFraction", F(d.LengthFraction));
            n.Add("conical", d.Conical ? "True" : "False");
            n.Add("radiativeExtension", d.RadiativeExtension ? "True" : "False");
            n.Add("gimbalRange", F(d.GimbalRange));
            n.Add("tech", d.Tech);
            return n;
        }

        public static EngineDesign LoadDesign(CfgNode n, EngineDesign defaults = null)
        {
            EngineDesign b = defaults ?? new EngineDesign();
            string of = n.Get("mixtureRatio");
            return new EngineDesign
            {
                Propellant = n.Get("propellant") ?? b.Propellant,
                Cycle = n.Get("cycle") ?? b.Cycle,
                ThrustVac = D(n, "thrustVac", b.ThrustVac),
                Pc = D(n, "pc", b.Pc),
                Eps = D(n, "eps", b.Eps),
                Of = of == null ? b.Of : of == "default" ? double.NaN : CfgNode.ParseDouble(of),
                LengthFraction = D(n, "lengthFraction", b.LengthFraction),
                Conical = B(n, "conical", b.Conical),
                RadiativeExtension = B(n, "radiativeExtension", b.RadiativeExtension),
                GimbalRange = D(n, "gimbalRange", b.GimbalRange),
                Tech = n.Get("tech") ?? b.Tech,
            };
        }

        static readonly string[] StatFields =
        {
            "ThrustVac", "ThrustSL", "IspVac", "IspSL", "MassFlow", "Mass", "Cost", "Of", "Pc", "Eps",
            "ThroatRadius", "ExitRadius", "ChamberRadius", "ChamberLength", "ConvergentLength", "NozzleLength",
            "ThetaN", "ThetaE", "EtaCStar", "EtaDivergence", "FrictionLoss", "TurbineFlowFraction", "TurbineExhaustIsp",
            "PumpPower", "ExpanderPowerMargin", "ExitPressure", "ThrottleMin",
        };

        public static CfgNode SaveStats(EngineStats s)
        {
            var n = new CfgNode("STATS");
            n.Add("modelVersion", EngineStats.ModelVersion.ToString(CultureInfo.InvariantCulture));
            foreach (string f in StatFields)
            {
                double v = (double)typeof(EngineStats).GetField(f).GetValue(s);
                n.Add(char.ToLowerInvariant(f[0]) + f.Substring(1), F(v));
            }
            n.Add("separatedAtSeaLevel", s.SeparatedAtSeaLevel ? "True" : "False");
            n.Add("ignitions", s.Ignitions.ToString(CultureInfo.InvariantCulture));
            var c = new CfgNode("atmosphereCurve");
            foreach (CurveKey k in s.AtmosphereCurve)
                c.Add("key", F(k.Pressure) + " " + F(k.Isp) + " " + F(k.Tangent) + " " + F(k.Tangent));
            n.Nodes.Add(c);
            var m = new CfgNode("MASS");
            foreach (var kv in s.MassBreakdown) m.Add(kv.Key, F(kv.Value));
            n.Nodes.Add(m);
            return n;
        }

        /// <summary>Returns null if the node is incomplete (the caller recomputes from DESIGN).</summary>
        public static EngineStats LoadStats(CfgNode n)
        {
            var s = new EngineStats();
            foreach (string f in StatFields)
            {
                string key = char.ToLowerInvariant(f[0]) + f.Substring(1);
                if (!n.Has(key)) return null;
                typeof(EngineStats).GetField(f).SetValue(s, CfgNode.ParseDouble(n.Get(key)));
            }
            s.SeparatedAtSeaLevel = B(n, "separatedAtSeaLevel", false);
            s.Ignitions = (int)D(n, "ignitions", 1);
            CfgNode c = n.Node("atmosphereCurve");
            if (c == null) return null;
            foreach (string key in c.GetAll("key"))
            {
                string[] p = key.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                var k = new CurveKey(CfgNode.ParseDouble(p[0]), CfgNode.ParseDouble(p[1]));
                if (p.Length > 2) k.Tangent = CfgNode.ParseDouble(p[2]);
                s.AtmosphereCurve.Add(k);
            }
            CfgNode mass = n.Node("MASS");
            if (mass != null) foreach (var kv in mass.Values) s.MassBreakdown[kv.Key] = CfgNode.ParseDouble(kv.Value);
            return s.AtmosphereCurve.Count >= 2 ? s : null;
        }

        public static int StatsModelVersion(CfgNode n) => n != null && n.Has("modelVersion") ? (int)CfgNode.ParseDouble(n.Get("modelVersion")) : 0;
    }
}
