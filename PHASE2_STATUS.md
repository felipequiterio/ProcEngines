# ProcEngines Phase 2: MVP in game, status

Date: 2026-10-17. Phase 2 of [DESIGN.md](DESIGN.md) §14 ("MVP in game"). Repository: https://github.com/felipequiterio/ProcEngines (private).

**Status: the MVP loop works in KSP 1.12.5, and every automated check passes.** Design a kerolox, methalox or hydrolox engine (pressure-fed, gas generator, expander, staged combustion or full-flow) in the editor, see its curve, save it to the library, fly it, and have the other LunaMultiplayer player get the identical engine.

What's not finished is listed at the end. The two gaps found at first (launching over-cap designs, too-heavy pressure-fed engines) are fixed; see "Follow-ups done".

## What was built

| Path | What |
|---|---|
| `src/ProcEngines/ModuleProcEngine.cs` | The part module. DESIGN/STATS persistence via the model's serializer (invariant culture; flight applies the stored STATS). Two-speed editor pipeline: on every change, stats + engine module + readouts; mesh at most 10 Hz; colliders, nodes, FAR/drag cube, `onEditorShipModified` and `SetBackup` after 0.5 s. Also: tech caps re-checked every 2 s, symmetry push, `OnCopy` for editor copies, mass/cost modifiers, `ModuleEnginesFX.Load` with explicit fuel flows, gimbal range, absolute attach nodes (pushes attached parts, including right after a craft loads at a different tech level), and Waterfall in flight. |
| `src/ProcEngines/EngineGeometry.cs` | Profile from the model's stats: mount, injector dome, chamber, 30° convergent with a 1.5 Rt arc, then the Rao contour from the model. Regen and radiative sections get different materials, plus a soot-coloured inner wall and throat plug. Normals come from the profile tangent. Kit primitives per cycle are sized from turbopump and pressurant mass (helium spheres; GG pump + gas generator + exhaust duct; SC pump + preburner + hot-gas duct; FFSC 2×). 3 convex colliders. |
| `src/ProcEngines/DesignerWindow.cs` | IMGUI designer, opened from "Engine designer" in the part menu. Propellant and cycle pickers with locked choices greyed out; log sliders for thrust and area ratio; Pc limited to the cycle and tech range; mixture ratio (default or custom); bell or cone; bell length; radiative extension; gimbal. Readouts, an Isp-vs-pressure plot (drawn with KSP's own Hermite evaluation), warnings, tech violations, and "Save to library". |
| `src/ProcEngines/DesignLibrary.cs` | Saves the design as a one-part stock subassembly (`saves/<save>/Subassemblies/ProcEngine - <name>.craft`). The part node is taken from a ship save and detached, so the live part is never touched. Refreshes the VAB list. |
| `src/ProcEngines/KspData.cs`, `Integrations.cs` | GameDatabase ↔ model data; tech caps from R&D (sandbox unlimited, retry while R&D isn't ready); FAR message or drag cube; Waterfall template application. |
| `src/ProcEngines.Model/Serialization.cs`, `Tech.cs` | DESIGN/STATS nodes, including the mass breakdown, so kit pieces rebuild identically on every client. Tech caps: union over researched nodes. |
| `GameData/ProcEngines/Parts/procEngine-liquid.cfg`, `Patches/Waterfall.cfg` | The base part (`SMURFFExclude`; module order before the engine and gimbal) and the empty Waterfall module. |
| `GameData/ProcEngines/Data/Tech.cfg`, `Resources.cfg`, `Plumes.cfg` | Stock tree + GPTT node mapping; KSP resources per propellant (LF/Ox 0.9:1.1, LqdMethane/Ox 3:1, LqdHydrogen/Ox 15:1); one Waterfall template per propellant and role (lower/upper). |
| `tests/ProcEngines.KspTests/`, `tests/ksp/run.sh` | In-game test driver (from the Phase 0 harness). Scripts: `sandbox`, `career`, `lmpA`, `lmpB`. `run.sh <A|B> <script>` builds, deploys, runs windowed and restores the fullscreen setting. |

## Verification

**Unit tests:** `dotnet run -c Release --project tests/ProcEngines.Model.Tests` passes 24/24. That covers Phase 1 plus serialization round-trips under de-DE, tech-cap union, one value per cfg line, and resource/plume coverage.

**In game:** evidence in [tests/ksp/evidence/phase2/](tests/ksp/evidence/phase2/).

- **Sandbox, ksp_test_A with FAR, Waterfall, KER and MechJeb: 21/21 pass.**
  - Six propellant/cycle combinations: kerolox GG, SC and pressure-fed; methalox FFSC; hydrolox SC and expander. For each, the engine fields equal STATS (thrust, Isp at 0 and 1 atm, fuel flow, min thrust), the propellant resources and ratios are correct, part mass = model mass, the thrust transform sits at the exit plane, and the stock ΔV app shows the same thrust and Isp.
  - Throttling: 40 changes in 0.26 s gave 4 mesh rebuilds and 1 slow rebuild.
  - The designer opens.
  - The library saves a one-part subassembly with the design inside, and stock `LoadSubassembly` reads it back.
  - Nozzle-to-decoupler gap stays 0 while the nozzle grows 2 m, with zero drift after going back.
  - Flight STATS are bit-identical to the editor's.
  - In flight: engine fields correct, Waterfall `waterfall-kerolox-lower-1` scaled to the exit diameter (11 effects), and the live burn gives 273 kN against 272 kN predicted at sea level.
- **Career, ksp_test_B: 6/6 pass.**
  - Start node only: pressure-fed kerolox, Pc ≤ 25 bar, ε ≤ 30, F ≤ 250 kN, Early tech. The default design shows its three violations.
  - Opening the designer doesn't silently rewrite an out-of-caps design. This was a bug, caught by a screenshot and fixed.
  - Launching that design from the VAB shows the blocking pre-flight dialog and stays in the editor.
  - Launching it by a path that skips the editor checks (`FlightDriver.StartWithNewLaunch`, like the KSC launch dialog or LMP) locks the engine: it won't ignite and produces 0 kN.
  - After researching basicRocketry, generalRocketry, advRocketry and propulsionSystems, the caps update to GG + expander, hydrolox, bell nozzles, Pc ≤ 90 and Mid tech. The same default design then passes the pre-flight test.
- **LunaMultiplayer, two clients on the local server: pass.**
  - TesterA designed and launched a pressure-fed kerolox engine.
  - TesterB's ProtoVessel carries identical DESIGN/STATS.
  - TesterB's loaded engine is identical: thrust, Isp, fuel flow, exit diameter, node position, bell and kit vertex counts and bounds, mass and propellants.
  - The server universe was backed up and restored.

Screenshots: `shots/A_sandbox_designer.png` (designer window), `A_sandbox_editor_*.png` (one per combination), `A_sandbox_stack.png`, `A_sandbox_flight_burn.png`, `B_career_designer_start.png`, `B_career_launch_blocked.png`, `B_career_flight_locked.png`, `B_lmpB_lmpB_flight.png`.

## Follow-ups done

1. **Over-cap designs can't be launched.**
   - `src/ProcEngines/LaunchGuard.cs` adds a blocking stock pre-flight test, `ProcEngineTechTest`. A Harmony postfix on `EditorLogic.GetStockPreFlightCheck` puts it first, so it shows before the stock warnings. It has no "launch anyway" option, and the dialog lists each engine with its violations.
   - The KSC launch dialog, LMP and KCT never run the stock checks, so `ModuleProcEngine` also checks in flight. An over-cap design (career or science) is locked: the engine shuts down, can't be activated, and a screen message says why. Tech only grows, so an engine that was legal at launch stays legal.
   - Harmony (`000_Harmony`, already in the pack) is now a dependency.
2. **Pressure-fed rebalanced.**
   - The on-engine pressurant is now sized physically: composite helium bottles that expel one rated burn (150 s) of propellant at tank pressure 1.5·Pc + 2 bar, using a COPV p·V/m of 120 kJ/kg, plus the helium itself.
   - A 78 kN, 21 bar kerolox engine drops from 849 kg (TWR 9) to **354 kg (TWR 22.5)**. 30 kN: TWR 18.6; 250 kN: TWR 21.3.
   - Hydrolox needs about 2.5× the helium for the same flow, because of its low density (TWR 13–15). That is realistic.
   - New unit test: pressure-fed TWR between 12 and 60, and pressurant between 10 % and 70 % of engine mass. 25/25 unit tests pass.

## Not done yet (Phase 2 follow-ups)

1. **Visuals.** Kit pieces are plain primitives and can hide under a tank from some angles. No GG exhaust plume, no heat glow, no TexturesUnlimited path (Phase 3). The plume `referenceDiameter` values are first guesses.
2. **Untested in game.** Symmetry counterparts (the code path exists), stock undo after a design change, surface-attached children on the chamber (not handled), KER and MechJeb with the real module (checked with the spike), and the parts-list icon.
3. **Not installed in ksp_mp.** The real pack needs its ~9-minute ModuleManager patch. GPTT node IDs are mapped in `Tech.cfg` but untested in a GPTT career.
4. **Designer.** IMGUI MVP. The UGUI window and in-scene drag handles are Phase 3 (§7.2).

## Test-copy and server changes

All listed in `spike/run/TEST_COPY_CHANGES.txt`. `spike/run/cleanup.sh` removes ProcEngines, the test driver, the spike and the extra mods from both test copies. The test driver is inactive unless `GameData/ProcEnginesTests/PluginData/autotest.txt` exists, and none does now.
