using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ProcEngines.Model
{
    /// <summary>A PROCENG_CYCLE node. All balance lives in data.</summary>
    public sealed class Cycle
    {
        public string Name;
        public double PcMin, PcMax;            // bar, soft range (warning outside)
        public bool HasPumps = true;
        public bool DumpsTurbineGas;           // open cycle (gas generator): turbine flow leaves at low Isp
        public bool ExpanderHeatLimited;       // turbine power from regen-jacket heat pickup
        public double PumpDpFactor = 1.3;      // pump pressure rise / Pc
        public double PumpDpMin = 10;          // bar, injector + cooling-jacket drop on top
        public double TurbinePressureRatio = 20;
        public double TurbomachineryFactor = 1;   // turbopump mass multiplier (preburner, second pump, ...)
        public double PressurantFactor;        // pressure-fed: helium bottle mass coefficient multiplier
        public double ThrottleMin = 0.6;
        public int Ignitions = 1;
        public string[] Propellants = new string[0];

        public static Cycle Load(CfgNode n) => new Cycle
        {
            Name = n.Get("name"),
            PcMin = n.Double("pcMin", 1),
            PcMax = n.Double("pcMax", 400),
            HasPumps = n.Get("hasPumps") != "false",
            DumpsTurbineGas = n.Get("dumpsTurbineGas") == "true",
            ExpanderHeatLimited = n.Get("expanderHeatLimited") == "true",
            PumpDpFactor = n.Double("pumpDpFactor", 1.3),
            PumpDpMin = n.Double("pumpDpMin", 10),
            TurbinePressureRatio = n.Double("turbinePressureRatio", 20),
            TurbomachineryFactor = n.Double("turbomachineryFactor", 1),
            PressurantFactor = n.Double("pressurantFactor", 0),
            ThrottleMin = n.Double("throttleMin", 0.6),
            Ignitions = (int)n.Double("ignitions", 1),
            Propellants = (n.Get("propellants") ?? "").Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries),
        };
    }

    /// <summary>A PROCENG_TECHLEVEL node: technology-dependent efficiencies and structural factors.</summary>
    public sealed class TechLevel
    {
        public string Name;
        public double EtaCStarBonus;           // added to the propellant's base c* efficiency
        public double PumpEfficiency = 0.65;
        public double TurbineEfficiency = 0.6;
        public double StructureFactor = 1;     // chamber, nozzle and turbomachinery mass multiplier (materials, manufacturing)
        public double RegenEpsMax = 30;        // area ratio up to which the nozzle is regeneratively cooled

        public static TechLevel Load(CfgNode n) => new TechLevel
        {
            Name = n.Get("name"),
            EtaCStarBonus = n.Double("etaCStarBonus", 0),
            PumpEfficiency = n.Double("pumpEfficiency", 0.65),
            TurbineEfficiency = n.Double("turbineEfficiency", 0.6),
            StructureFactor = n.Double("structureFactor", 1),
            RegenEpsMax = n.Double("regenEpsMax", 30),
        };
    }

    /// <summary>Global constants (PROCENG_BALANCE): mass-model coefficients, separation criterion, game multipliers.</summary>
    public sealed class Balance
    {
        // c* efficiency per propellant (before the tech bonus)
        public Dictionary<string, double> EtaCStar = new Dictionary<string, double>();
        // Turbopump mass multiplier per propellant (overrides the propellant file when present)
        public Dictionary<string, double> TurbomachineryByPropellant = new Dictionary<string, double>();
        // Mass model, SI units (kg, m, MPa, kW, kN)
        public double ChamberPressureVessel = 180;   // kg per (MPa * m^3) of Pc * chamber volume
        public double ChamberWall = 30;              // kg/m^2 of chamber + convergent wall (minimum gauge, cooling jacket)
        public double Injector = 56;                 // kg per (m^3 * sqrt(bar)) of Dc^3 sqrt(Pc): plate thickness ~ Dc sqrt(Pc/sigma)
        public double NozzleRegen = 20;              // kg/m^2, regeneratively cooled nozzle wall
        public double NozzleRadiative = 7;           // kg/m^2, radiatively cooled extension
        public double NozzleSizeExponent = 0.3;      // areal density grows as (exit radius / 1 m)^exponent (thicker walls, stiffeners)
        public double Turbomachinery = 0.9;          // kg per kW^exponent
        public double TurbomachineryExponent = 0.65;
        public double Pressurant = 0.004;            // kg per (kg/s * s * bar), pressure-fed helium + bottles
        public double PressurantBurnTime = 300;      // s, rated burn used to size the pressurant
        public double GimbalMass = 0.004;            // kg per (kN * deg)
        public double MiscFraction = 0.08;           // valves, lines, structure
        public double MiscConstant = 5;              // kg
        public double ExpanderHeatFlux = 9.8;        // MW/m^2 usable turbine heat at Pc = 50 bar (scales Pc^0.8)
        // Separation (Kalt-Badal style): p_sep / pa = SeparationK * (Pc / pa)^SeparationExponent
        public double SeparationK = 0.667, SeparationExponent = -0.2;
        // Game multipliers
        public double MassMultiplier = 1, IspMultiplier = 1, CostMultiplier = 1;

        public static Balance Load(CfgNode n)
        {
            var b = new Balance();
            foreach (var kv in n.Values)
            {
                if (kv.Key.StartsWith("etaCStar.", StringComparison.Ordinal))
                {
                    b.EtaCStar[kv.Key.Substring(9)] = CfgNode.ParseDouble(kv.Value);
                    continue;
                }
                if (kv.Key.StartsWith("turbomachinery.", StringComparison.Ordinal))
                {
                    b.TurbomachineryByPropellant[kv.Key.Substring(15)] = CfgNode.ParseDouble(kv.Value);
                    continue;
                }
                var f = typeof(Balance).GetField(char.ToUpperInvariant(kv.Key[0]) + kv.Key.Substring(1));
                if (f != null && f.FieldType == typeof(double)) f.SetValue(b, CfgNode.ParseDouble(kv.Value));
            }
            return b;
        }
    }

    /// <summary>Everything the model needs, loaded once.</summary>
    public sealed class ModelData
    {
        public readonly Dictionary<string, Propellant> Propellants = new Dictionary<string, Propellant>();
        public readonly Dictionary<string, Cycle> Cycles = new Dictionary<string, Cycle>();
        public readonly Dictionary<string, TechLevel> TechLevels = new Dictionary<string, TechLevel>();
        public Balance Balance = new Balance();

        public void AddNodes(CfgNode root)
        {
            foreach (CfgNode n in root.NodesNamed("PROCENG_PROPELLANT")) { var p = Propellant.Load(n); Propellants[p.Name] = p; }
            foreach (CfgNode n in root.NodesNamed("PROCENG_CYCLE")) { var c = Cycle.Load(n); Cycles[c.Name] = c; }
            foreach (CfgNode n in root.NodesNamed("PROCENG_TECHLEVEL")) { var t = TechLevel.Load(n); TechLevels[t.Name] = t; }
            foreach (CfgNode n in root.NodesNamed("PROCENG_BALANCE")) Balance = Balance.Load(n);
        }

        /// <summary>Loads every .cfg in a folder (outside KSP: tests, tools).</summary>
        public static ModelData LoadFolder(string dir)
        {
            var d = new ModelData();
            foreach (string f in Directory.GetFiles(dir, "*.cfg").OrderBy(x => x, StringComparer.Ordinal))
                d.AddNodes(CfgNode.Parse(File.ReadAllText(f)));
            return d;
        }
    }
}
