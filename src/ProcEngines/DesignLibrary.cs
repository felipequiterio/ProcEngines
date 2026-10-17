using System;
using System.IO;
using System.Linq;
using KSP.UI.Screens;
using UnityEngine;

namespace ProcEngines
{
    /// <summary>
    /// MVP library (DESIGN.md 10.1): a design is saved as a one-part stock subassembly, so it shows up in the VAB's
    /// Subassemblies category and LMP craft sharing understands it. The part node is taken from a save of the whole
    /// ship and detached (links removed), so the live editor part is never touched.
    /// </summary>
    public static class DesignLibrary
    {
        public static string FileNameFor(string designName)
        {
            string clean = new string((designName ?? "engine").Select(c => Path.GetInvalidFileNameChars().Contains(c) || "{}*?<>:".Contains(c) ? ' ' : c).ToArray()).Trim();
            return "ProcEngine - " + (clean.Length == 0 ? "engine" : clean);
        }

        public static string SaveAsSubassembly(ModuleProcEngine m)
        {
            try
            {
                ShipConstruct ship = EditorLogic.fetch.ship;
                ConfigNode shipNode = ship.SaveShip();
                string id = m.part.craftID.ToString();
                ConfigNode partNode = shipNode.GetNodes("PART").FirstOrDefault(p => (p.GetValue("part") ?? "").EndsWith("_" + id));
                if (partNode == null) return "save failed: part not found in the ship";
                partNode = partNode.CreateCopy();
                foreach (string key in new[] { "link", "attN", "srfN", "sym" }) partNode.RemoveValues(key);
                partNode.SetValue("pos", "0,0,0");
                partNode.SetValue("attPos", "0,0,0");
                partNode.SetValue("attPos0", "0,0,0");
                partNode.SetValue("rot", "0,0,0,1");
                partNode.SetValue("attRot", "0,0,0,1");
                partNode.SetValue("attRot0", "0,0,0,1");
                partNode.SetValue("istg", "0");
                partNode.SetValue("dstg", "0");
                partNode.SetValue("sidx", "0");

                string name = FileNameFor(m.designName);
                var craft = new ConfigNode();
                craft.AddValue("ship", name);
                craft.AddValue("version", shipNode.GetValue("version"));
                craft.AddValue("description", $"{m.Design.Propellant} {m.Design.Cycle}, {m.Stats.ThrustVac:F0} kN, Isp {m.Stats.IspVac:F0}/{m.Stats.IspSL:F0} s");
                craft.AddValue("type", "VAB");
                craft.AddValue("size", "1,1,1");
                craft.AddValue("steamPublishedFileId", "0");
                craft.AddValue("persistentId", "0");
                craft.AddValue("rot", "0,0,0,1");
                craft.AddValue("missionFlag", shipNode.GetValue("missionFlag") ?? "Squad/Flags/default");
                craft.AddValue("vesselType", "Debris");
                craft.AddNode(partNode);

                string dir = Path.Combine(KSPUtil.ApplicationRootPath, "saves", HighLogic.SaveFolder, "Subassemblies");
                Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, name + ".craft");
                craft.Save(path);
                EditorPartList.Instance?.RefreshSubassemblies();
                Debug.Log("[ProcEngines] saved design to " + path);
                return "Saved: " + name;
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                return "save failed: " + e.Message;
            }
        }
    }
}
