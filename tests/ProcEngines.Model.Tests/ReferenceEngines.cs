using System.Collections.Generic;
using ProcEngines.Model;

namespace ProcEngines.Model.Tests
{
    /// <summary>
    /// Real engines used to calibrate and check the model. Public figures (manufacturer data sheets,
    /// Wikipedia and Astronautix summaries), rounded; treat them as +-2 % for Isp and +-5 % for mass.
    /// NaN = not used.
    /// </summary>
    public sealed class ReferenceEngine
    {
        public string Name, Propellant, Cycle, Tech;
        public double Of, Pc, Eps, ThrustVac, IspVac, IspSL = double.NaN, MassKg = double.NaN;
        public double LengthFraction = 0.8;
        public bool RadiativeExtension;
        public double MassTolerance = double.NaN;   // per-engine override of the 10 % test tolerance, with a reason in Note
        public bool SoftIsp, SoftMass;   // reported only: data too uncertain, or a feature the model lacks
        public string Note = "";

        public EngineDesign Design() => new EngineDesign
        {
            Propellant = Propellant, Cycle = Cycle, Tech = Tech, ThrustVac = ThrustVac, Pc = Pc, Eps = Eps, Of = Of,
            LengthFraction = LengthFraction, GimbalRange = 0, RadiativeExtension = RadiativeExtension,
        };

        public static readonly List<ReferenceEngine> All = new List<ReferenceEngine>
        {
            // ---- kerolox
            new ReferenceEngine { Name = "Merlin 1D", Propellant = "Kerolox", Cycle = "GasGenerator", Tech = "Late",
                Of = 2.36, Pc = 97, Eps = 16, ThrustVac = 914, IspVac = 311, IspSL = 282, MassKg = 470 },
            new ReferenceEngine { Name = "Merlin 1D Vac", Propellant = "Kerolox", Cycle = "GasGenerator", Tech = "Late",
                Of = 2.36, Pc = 97, Eps = 165, ThrustVac = 981, IspVac = 348, RadiativeExtension = true, Note = "radiative niobium extension; mass not published" },
            new ReferenceEngine { Name = "F-1", Propellant = "Kerolox", Cycle = "GasGenerator", Tech = "Early",
                Of = 2.27, Pc = 70, Eps = 16, ThrustVac = 7770, IspVac = 304, IspSL = 263, MassKg = 8400 },
            new ReferenceEngine { Name = "RD-191", Propellant = "Kerolox", Cycle = "StagedCombustion", Tech = "Mid",
                Of = 2.63, Pc = 258, Eps = 37, ThrustVac = 2084, IspVac = 337.5, IspSL = 311.2, MassKg = 2290, MassTolerance = 0.15,
                Note = "same cycle, propellant and era as NK-33 but 1.6x its mass per kN: a smooth model splits the difference" },
            new ReferenceEngine { Name = "NK-33", Propellant = "Kerolox", Cycle = "StagedCombustion", Tech = "Mid",
                Of = 2.6, Pc = 145, Eps = 27, ThrustVac = 1638, IspVac = 331, IspSL = 297, MassKg = 1222, MassTolerance = 0.25,
                Note = "exceptionally light (TWR 137); see RD-191" },
            new ReferenceEngine { Name = "Kestrel", Propellant = "Kerolox", Cycle = "PressureFed", Tech = "Mid",
                Of = 2.2, Pc = 9.3, Eps = 60, ThrustVac = 31, IspVac = 317, MassKg = 52, SoftIsp = true, SoftMass = true, RadiativeExtension = true,
                Note = "pressure-fed, ablative; Pc and area ratio uncertain; the real engine has no pressurant of its own (tank-fed)" },
            // ---- hydrolox
            new ReferenceEngine { Name = "RS-25D", Propellant = "Hydrolox", Cycle = "StagedCombustion", Tech = "Mid",
                Of = 6.03, Pc = 206, Eps = 69, ThrustVac = 2279, IspVac = 452.3, IspSL = 366, MassKg = 3177 },
            new ReferenceEngine { Name = "RS-68A", Propellant = "Hydrolox", Cycle = "GasGenerator", Tech = "Mid",
                Of = 6.0, Pc = 102.6, Eps = 21.5, ThrustVac = 3560, IspVac = 414, IspSL = 362, MassKg = 6740, SoftMass = true, Note = "ablative nozzle: the model has no ablative option yet" },
            new ReferenceEngine { Name = "J-2", Propellant = "Hydrolox", Cycle = "GasGenerator", Tech = "Early",
                Of = 5.5, Pc = 52.6, Eps = 27.5, ThrustVac = 1033, IspVac = 421, MassKg = 1438, Note = "separates at sea level (about 200 s); separated performance is not modelled precisely" },
            new ReferenceEngine { Name = "Vulcain 2", Propellant = "Hydrolox", Cycle = "GasGenerator", Tech = "Mid",
                Of = 6.1, Pc = 115, Eps = 58.5, ThrustVac = 1359, IspVac = 434, IspSL = 318, MassKg = 1800 },
            new ReferenceEngine { Name = "LE-7A", Propellant = "Hydrolox", Cycle = "StagedCombustion", Tech = "Mid",
                Of = 5.9, Pc = 121, Eps = 51.9, ThrustVac = 1098, IspVac = 440, IspSL = 338, MassKg = 1832 },
            new ReferenceEngine { Name = "RL10A-4-2", Propellant = "Hydrolox", Cycle = "Expander", Tech = "Mid",
                Of = 5.5, Pc = 42, Eps = 84, ThrustVac = 99.1, IspVac = 451, MassKg = 168, RadiativeExtension = true, Note = "niobium nozzle extension" },
            new ReferenceEngine { Name = "RL10B-2", Propellant = "Hydrolox", Cycle = "Expander", Tech = "Late",
                Of = 5.88, Pc = 44.1, Eps = 280, ThrustVac = 110.1, IspVac = 465.5, MassKg = 277, RadiativeExtension = true, SoftMass = true, Note = "extendible carbon-carbon nozzle with deployment mechanism (not modelled)" },
            // ---- methalox
            new ReferenceEngine { Name = "Raptor 2", Propellant = "Methalox", Cycle = "FullFlowStaged", Tech = "Late",
                Of = 3.6, Pc = 300, Eps = 34, ThrustVac = 2394, IspVac = 347, IspSL = 327, MassKg = 1630, Note = "vacuum figures of the sea-level engine are estimates" },
        };
    }
}
