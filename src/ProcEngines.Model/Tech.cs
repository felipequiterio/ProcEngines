using System;
using System.Collections.Generic;
using System.Linq;

namespace ProcEngines.Model
{
    /// <summary>One PROCENG_TECH node: what a researched tech-tree node unlocks (DESIGN.md 6).</summary>
    public sealed class TechEntry
    {
        public string Node;
        public string TechLevel;                         // Early/Mid/Late; the highest researched wins
        public readonly HashSet<string> Cycles = new HashSet<string>();
        public readonly HashSet<string> Propellants = new HashSet<string>();
        public bool RaoBell, RadiativeExtension;
        public double PcMax, EpsMax, ThrustMax;          // 0 = no limit from this node

        public static TechEntry Load(CfgNode n)
        {
            var e = new TechEntry { Node = n.Get("node"), TechLevel = n.Get("techLevel") };
            CfgNode u = n.Node("UNLOCK");
            if (u != null)
            {
                foreach (string c in u.GetAll("cycle")) e.Cycles.Add(c.Trim());
                foreach (string p in u.GetAll("propellant")) e.Propellants.Add(p.Trim());
                e.RaoBell = u.Get("raoBell") == "true";
                e.RadiativeExtension = u.Get("radiativeExtension") == "true";
            }
            CfgNode c2 = n.Node("CAP");
            if (c2 != null)
            {
                e.PcMax = c2.Double("pcMax", 0);
                e.EpsMax = c2.Double("epsMax", 0);
                e.ThrustMax = c2.Double("thrustMax", 0);
            }
            return e;
        }
    }

    /// <summary>The union of everything researched: largest maximum wins, 0/missing means "no limit from this node".</summary>
    public sealed class TechCaps
    {
        public bool Unlimited;                           // sandbox
        public string TechLevel = "Early";
        public readonly HashSet<string> Cycles = new HashSet<string>();
        public readonly HashSet<string> Propellants = new HashSet<string>();
        public bool RaoBell, RadiativeExtension;
        public double PcMax, EpsMax, ThrustMax;          // 0 = nothing researched sets it -> use the hard defaults below

        public const double DefaultPcMax = 25, DefaultEpsMax = 30, DefaultThrustMax = 250;

        public static TechCaps Sandbox(IEnumerable<string> techLevelsLowToHigh)
        {
            var c = new TechCaps { Unlimited = true, RaoBell = true, RadiativeExtension = true, TechLevel = techLevelsLowToHigh.Last() };
            return c;
        }

        /// <summary>Union of the entries whose node is researched. Tech levels are ranked by <paramref name="levelOrder"/>.</summary>
        public static TechCaps Evaluate(IEnumerable<TechEntry> entries, Func<string, bool> researched, IList<string> levelOrder)
        {
            var c = new TechCaps { TechLevel = levelOrder[0] };
            foreach (TechEntry e in entries.Where(e => researched(e.Node)))
            {
                c.Cycles.UnionWith(e.Cycles);
                c.Propellants.UnionWith(e.Propellants);
                c.RaoBell |= e.RaoBell;
                c.RadiativeExtension |= e.RadiativeExtension;
                c.PcMax = Math.Max(c.PcMax, e.PcMax);
                c.EpsMax = Math.Max(c.EpsMax, e.EpsMax);
                c.ThrustMax = Math.Max(c.ThrustMax, e.ThrustMax);
                if (e.TechLevel != null && levelOrder.IndexOf(e.TechLevel) > levelOrder.IndexOf(c.TechLevel)) c.TechLevel = e.TechLevel;
            }
            return c;
        }

        public double MaxPc => Unlimited ? double.PositiveInfinity : PcMax > 0 ? PcMax : DefaultPcMax;
        public double MaxEps => Unlimited ? double.PositiveInfinity : EpsMax > 0 ? EpsMax : DefaultEpsMax;
        public double MaxThrust => Unlimited ? double.PositiveInfinity : ThrustMax > 0 ? ThrustMax : DefaultThrustMax;

        /// <summary>Everything in the design that the current tech doesn't allow (empty = launchable).</summary>
        public List<string> Violations(EngineDesign d)
        {
            var v = new List<string>();
            if (Unlimited) return v;
            if (!Propellants.Contains(d.Propellant)) v.Add($"propellant {d.Propellant} not researched");
            if (!Cycles.Contains(d.Cycle)) v.Add($"cycle {d.Cycle} not researched");
            if (d.Pc > MaxPc + 1e-6) v.Add($"Pc {d.Pc:F0} bar > {MaxPc:F0}");
            if (d.Eps > MaxEps + 1e-6) v.Add($"area ratio {d.Eps:F0} > {MaxEps:F0}");
            if (d.ThrustVac > MaxThrust + 1e-6) v.Add($"thrust {d.ThrustVac:F0} kN > {MaxThrust:F0}");
            if (!d.Conical && !RaoBell) v.Add("bell nozzles not researched (conical only)");
            if (d.RadiativeExtension && !RadiativeExtension) v.Add("radiative nozzle extensions not researched");
            return v;
        }
    }
}
