using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using PreFlightTests;
using UnityEngine;

namespace ProcEngines
{
    /// <summary>
    /// Blocking pre-flight test: a craft whose procedural engines exceed the researched tech can't be launched from
    /// the editor (DESIGN.md 6). Inserted first so it shows before stock warnings. Other launch paths (KSC launch
    /// dialog, LMP, KCT) skip the stock checks; ModuleProcEngine locks the engine in flight for those.
    /// </summary>
    public class ProcEngineTechTest : IPreFlightTest
    {
        readonly List<string> problems = new List<string>();

        public ProcEngineTechTest(ShipConstruct ship)
        {
            if (ship == null) return;
            foreach (Part p in ship.parts)
            {
                ModuleProcEngine m = p.FindModuleImplementing<ModuleProcEngine>();
                if (m == null || m.Design == null) continue;
                List<string> v = KspData.CurrentCaps()?.Violations(m.Design);
                if (v != null && v.Count > 0) problems.Add($"{m.designName}: {string.Join("; ", v)}");
            }
        }

        public IReadOnlyList<string> Problems => problems;
        public bool Test() => problems.Count == 0;
        public string GetWarningTitle() => "Engine design exceeds researched technology";
        public string GetWarningDescription() =>
            "These procedural engines use technology you haven't researched yet. Change them in the Engine designer, or research the nodes first.\n\n" +
            string.Join("\n", problems);
        public string GetProceedOption() => null;   // blocking: no "launch anyway"
        public string GetAbortOption() => "Back to the editor";
    }

    [HarmonyPatch(typeof(EditorLogic), "GetStockPreFlightCheck")]
    static class EditorPreFlightPatch
    {
        static readonly FieldInfo Tests = typeof(PreFlightCheck).GetField("tests", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

        static void Postfix(PreFlightCheck __result)
        {
            if (__result == null || EditorLogic.fetch == null) return;
            var test = new ProcEngineTechTest(EditorLogic.fetch.ship);
            if (Tests?.GetValue(__result) is List<IPreFlightTest> list) list.Insert(0, test);
            else __result.AddTest(test);
        }
    }

    [KSPAddon(KSPAddon.Startup.Instantly, true)]
    public class HarmonyLoader : MonoBehaviour
    {
        void Awake()
        {
            new Harmony("procengines").PatchAll(typeof(HarmonyLoader).Assembly);
            Debug.Log("[ProcEngines] Harmony patches applied: " + string.Join(", ", Harmony.GetAllPatchedMethods().Where(m => Harmony.GetPatchInfo(m).Owners.Contains("procengines")).Select(m => m.Name)));
            DontDestroyOnLoad(this);
        }
    }
}
