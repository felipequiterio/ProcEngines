using System.Collections.Generic;
using System.Linq;
using ProcEngines.Model;
using UnityEngine;

namespace ProcEngines
{
    /// <summary>Bridges KSP's GameDatabase/ConfigNode and the model's CfgNode/ModelData.</summary>
    public static class KspData
    {
        static ModelData data;
        static readonly string[] NodeTypes =
            { "PROCENG_PROPELLANT", "PROCENG_CYCLE", "PROCENG_TECHLEVEL", "PROCENG_BALANCE", "PROCENG_TECH", "PROCENG_RESOURCES", "PROCENG_PLUME" };

        /// <summary>Model data from the game database (loaded once, after ModuleManager has patched it).</summary>
        public static ModelData Data
        {
            get
            {
                if (data != null) return data;
                var root = new CfgNode("root");
                foreach (string t in NodeTypes)
                    foreach (ConfigNode n in GameDatabase.Instance.GetConfigNodes(t))
                        root.Nodes.Add(ToCfg(n));
                data = new ModelData();
                data.AddNodes(root);
                Debug.Log($"[ProcEngines] model data: {data.Propellants.Count} propellants, {data.Cycles.Count} cycles, {data.TechLevels.Count} tech levels, {data.Tech.Count} tech entries");
                return data;
            }
        }

        public static CfgNode ToCfg(ConfigNode n)
        {
            var c = new CfgNode(n.name);
            foreach (ConfigNode.Value v in n.values) c.Add(v.name, v.value);
            foreach (ConfigNode child in n.nodes) c.Nodes.Add(ToCfg(child));
            return c;
        }

        public static ConfigNode ToConfigNode(CfgNode c)
        {
            var n = new ConfigNode(c.Name);
            foreach (KeyValuePair<string, string> kv in c.Values) n.AddValue(kv.Key, kv.Value);
            foreach (CfgNode child in c.Nodes) n.AddNode(ToConfigNode(child));
            return n;
        }

        /// <summary>
        /// Current tech caps. Sandbox (or no R&D) = unlimited. Returns null while ResearchAndDevelopment isn't ready
        /// yet (early OnStart in career): callers retry later and keep the design unvalidated meanwhile.
        /// </summary>
        public static TechCaps CurrentCaps()
        {
            ModelData d = Data;
            Game g = HighLogic.CurrentGame;
            if (g == null || g.Mode == Game.Modes.SANDBOX || g.Mode == Game.Modes.MISSION_BUILDER)
                return TechCaps.Sandbox(d.TechLevelOrder);
            if (ResearchAndDevelopment.Instance == null) return null;
            return TechCaps.Evaluate(d.Tech, node => ResearchAndDevelopment.GetTechnologyState(node) == RDTech.State.Available, d.TechLevelOrder);
        }

        public static bool FarPresent => farPresent ?? (bool)(farPresent = AssemblyLoader.loadedAssemblies.Any(a =>
            a.assembly.GetName().Name == "FerramAerospaceResearch" && a.assembly.GetType("FerramAerospaceResearch.FARAPI", false) != null));
        static bool? farPresent;
    }
}
