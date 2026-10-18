using System;
using System.Collections.Generic;
using System.Linq;

namespace ProcEngines.Model
{
    /// <summary>
    /// The engine's visual finish: which material each part group shows. Purely cosmetic: it is kept out of DESIGN
    /// (its own FINISH node), so it never changes stats, mass, cost, tech checks or the shared render meshes; only
    /// the materials on the renderers change. Every slot defaults to "Auto", the propellant- and cycle-driven look
    /// (a test-fired engine). A preset sets all slots at once; a finish that matches no preset is "Custom".
    /// </summary>
    public sealed class EngineFinish
    {
        public const string Auto = "Auto", Custom = "Custom";

        public sealed class Slot
        {
            public string Id, Label, Tip;
            public Option[] Options;
        }

        public sealed class Option
        {
            public string Id, Label, Tip;
            public Option(string id, string label, string tip) { Id = id; Label = label; Tip = tip; }
        }

        static Option O(string id, string label, string tip) => new Option(id, label, tip);

        // the cooled wall: the same choices for the chamber and the nozzle (tube wall or channel-wall jacket, as the cycle builds it)
        static Option[] WallOptions() => new[]
        {
            O(Auto, "Fired", "As fired: bronzed by kerosene, warm grey from methane, near bare from hydrogen"),
            O("Copper", "Copper", "Bare copper alloy, fresh from the shop"),
            O("Bronze", "Bronze", "Heavily oxidised copper: dark reddish brown"),
            O("Stainless", "Stainless", "Stainless steel tubes or jacket: light silver grey"),
            O("Inconel", "Inconel", "Heat-tinted nickel alloy: straw gold"),
            O("Nickel", "Nickel", "Electroformed nickel: dull grey"),
            O("Black", "Black", "High-temperature black coating"),
        };

        // steel parts: bare, darkened or painted
        static Option[] SteelOptions(string autoTip) => new[]
        {
            O(Auto, "Auto", autoTip),
            O("Steel", "Steel", "Bare steel"),
            O("DarkSteel", "Dark steel", "Darkened, heat-dulled steel"),
            O("Bronzed", "Bronzed", "Steel bronzed by firing"),
            O("Primer", "Grey primer", "Matte grey primer"),
            O("Black", "Black", "Matte black paint"),
            O("White", "White", "White paint"),
        };

        public static readonly Slot[] Slots =
        {
            new Slot { Id = "chamber", Label = "Chamber", Tip = "The combustion chamber's cooled wall", Options = WallOptions() },
            new Slot { Id = "nozzle", Label = "Nozzle", Tip = "The cooled nozzle wall (tubes or jacket)", Options = WallOptions() },
            new Slot
            {
                Id = "extension", Label = "Extension", Tip = "The radiatively cooled nozzle extension (vacuum engines)",
                Options = new[]
                {
                    O(Auto, "Auto", "By tech level: coated niobium, carbon-carbon at the late tech level"),
                    O("Niobium", "Niobium", "Silicide-coated niobium: dark charcoal"),
                    O("BareNiobium", "Bare niobium", "Uncoated niobium alloy: light lavender grey"),
                    O("Carbon", "Carbon-carbon", "Carbon-carbon composite: black weave"),
                },
            },
            new Slot
            {
                Id = "interior", Label = "Inside", Tip = "Inside the nozzle",
                Options = new[]
                {
                    O(Auto, "Auto", "By propellant: heavy soot for kerosene, light for methane, clean for hydrogen"),
                    O("Clean", "Clean", "Clean, heat-tinted metal"),
                    O("Light", "Light soot", "A light film of soot"),
                    O("Heavy", "Heavy soot", "Thick kerosene soot"),
                },
            },
            new Slot { Id = "hardware", Label = "Bell bands", Tip = "Hat bands, coolant manifold and joint flange on the nozzle", Options = SteelOptions("Steel with the bell's firing history") },
            new Slot { Id = "mount", Label = "Mount", Tip = "Thrust mount, injector dome and chamber flanges", Options = SteelOptions("Bare steel") },
            new Slot { Id = "frame", Label = "Frame", Tip = "Struts, thrust frame, line flanges and collars", Options = SteelOptions("Worn steel") },
            new Slot
            {
                Id = "lines", Label = "Lines", Tip = "Propellant lines",
                Options = new[]
                {
                    O(Auto, "Auto", "Stainless lines; gold foil on hydrogen lines"),
                    O("Stainless", "Stainless", "Bare stainless everywhere"),
                    O("Gold", "Gold foil", "Every line wrapped in gold foil insulation"),
                    O("Silver", "Silver foil", "Every line wrapped in aluminised foil insulation"),
                    O("White", "White", "White insulation"),
                    O("Black", "Black", "Black insulation"),
                },
            },
        };

        public sealed class Preset
        {
            public string Name, Tip;
            public Dictionary<string, string> Values;
            public Preset(string name, string tip, Dictionary<string, string> values) { Name = name; Tip = tip; Values = values; }
        }

        /// <summary>Presets, in display order. Slots a preset leaves out stay Auto.</summary>
        public static readonly Preset[] Presets =
        {
            new Preset("Fired", "As test-fired: every part by propellant and cycle (the default)", new Dictionary<string, string>()),
            new Preset("Copper", "Fresh from the shop: bare copper walls, a clean nozzle, bare niobium, bare steel",
                new Dictionary<string, string> { ["chamber"] = "Copper", ["nozzle"] = "Copper", ["extension"] = "BareNiobium", ["interior"] = "Clean", ["hardware"] = "Steel", ["mount"] = "Steel", ["frame"] = "Steel", ["lines"] = "Stainless" }),
            new Preset("Stainless", "Stainless walls and bare steel all over",
                new Dictionary<string, string> { ["chamber"] = "Stainless", ["nozzle"] = "Stainless", ["interior"] = "Light", ["hardware"] = "Steel", ["mount"] = "Steel", ["frame"] = "Steel", ["lines"] = "Stainless" }),
            new Preset("Inconel", "Heat-tinted nickel alloy walls, bronzed bands, foil-wrapped lines",
                new Dictionary<string, string> { ["chamber"] = "Inconel", ["nozzle"] = "Inconel", ["hardware"] = "Bronzed", ["mount"] = "Steel", ["frame"] = "DarkSteel", ["lines"] = "Gold" }),
            new Preset("Black", "Black coating, black paint and insulation, a carbon-carbon extension",
                new Dictionary<string, string> { ["chamber"] = "Black", ["nozzle"] = "Black", ["extension"] = "Carbon", ["interior"] = "Heavy", ["hardware"] = "Black", ["mount"] = "DarkSteel", ["frame"] = "Black", ["lines"] = "Black" }),
            new Preset("White", "Flight look: stainless walls, white paint and insulation, grey primer",
                new Dictionary<string, string> { ["chamber"] = "Stainless", ["nozzle"] = "Stainless", ["hardware"] = "Primer", ["mount"] = "Primer", ["frame"] = "White", ["lines"] = "White" }),
        };

        readonly Dictionary<string, string> values = new Dictionary<string, string>();

        public static Slot SlotById(string id) => Slots.FirstOrDefault(s => s.Id == id);

        /// <summary>The option of a slot (Auto when unset or unknown).</summary>
        public string Get(string slot) => values.TryGetValue(slot, out string v) ? v : Auto;

        /// <summary>Sets a slot; an unknown slot or option is ignored, Auto clears it.</summary>
        public void Set(string slot, string option)
        {
            Slot s = SlotById(slot);
            if (s == null || option == null || !s.Options.Any(o => o.Id == option)) return;
            if (option == Auto) values.Remove(slot); else values[slot] = option;
        }

        public bool IsDefault => values.Count == 0;

        public EngineFinish Copy()
        {
            var f = new EngineFinish();
            foreach (var kv in values) f.values[kv.Key] = kv.Value;
            return f;
        }

        public static EngineFinish FromPreset(string name)
        {
            var f = new EngineFinish();
            Preset p = Presets.FirstOrDefault(x => x.Name == name);
            if (p != null) foreach (var kv in p.Values) f.Set(kv.Key, kv.Value);
            return f;
        }

        /// <summary>The preset this finish matches exactly, or Custom.</summary>
        public string PresetName
        {
            get
            {
                foreach (var p in Presets)
                    if (Slots.All(s => Get(s.Id) == (p.Values.TryGetValue(s.Id, out string v) ? v : Auto))) return p.Name;
                return Custom;
            }
        }

        /// <summary>Identifies the finish (materials are cached by it): the non-Auto slots in slot order.</summary>
        public string Key => string.Join(",", Slots.Where(s => values.ContainsKey(s.Id)).Select(s => s.Id + "=" + values[s.Id]));

        public override string ToString() => IsDefault ? Auto : Key;

        /// <summary>FINISH node: one value per non-Auto slot.</summary>
        public CfgNode Save()
        {
            var n = new CfgNode("FINISH");
            foreach (Slot s in Slots) if (values.TryGetValue(s.Id, out string v)) n.Add(s.Id, v);
            return n;
        }

        /// <summary>Reads a FINISH node; unknown slots and options (a newer or older version) are dropped.</summary>
        public static EngineFinish Load(CfgNode n)
        {
            var f = new EngineFinish();
            if (n == null) return f;
            foreach (var kv in n.Values) f.Set(kv.Key, kv.Value);
            return f;
        }

        public override bool Equals(object obj) => obj is EngineFinish f && f.Key == Key;
        public override int GetHashCode() => Key.GetHashCode();
    }
}
