# ProcEngines Phase 2: MVP in game, status

Date: 2026-10-17. Phase 2 of [DESIGN.md](DESIGN.md) §14 ("MVP in game"). Repository: https://github.com/felipequiterio/ProcEngines (private).

**Status: the MVP loop works in KSP 1.12.5, and every automated check passes.** Design a kerolox, methalox or hydrolox engine (pressure-fed, gas generator, expander, staged combustion or full-flow) in the editor, see its curve, save it to the library, fly it, and have the other LunaMultiplayer player get the identical engine.

What's not finished is listed at the end. The biggest gap: tech violations show in red but don't yet block launch.

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
- **Career, ksp_test_B: 3/3 pass.**
  - Start node only: pressure-fed kerolox, Pc ≤ 25 bar, ε ≤ 30, F ≤ 250 kN, Early tech. The default design shows its three violations.
  - Opening the designer doesn't silently rewrite an out-of-caps design. This was a bug, caught by a screenshot and fixed.
  - After researching basicRocketry, generalRocketry, advRocketry and propulsionSystems, the caps update to GG + expander, hydrolox, bell nozzles, Pc ≤ 90 and Mid tech.
- **LunaMultiplayer, two clients on the local server: pass.**
  - TesterA designed and launched a pressure-fed kerolox engine.
  - TesterB's ProtoVessel carries identical DESIGN/STATS.
  - TesterB's loaded engine is identical: thrust, Isp, fuel flow, exit diameter, node position, bell and kit vertex counts and bounds, mass and propellants.
  - The server universe was backed up and restored.

Screenshots: `shots/A_sandbox_designer.png` (designer window), `A_sandbox_editor_*.png` (one per combination), `A_sandbox_stack.png`, `A_sandbox_flight_burn.png`, `B_career_designer_start.png`, `B_lmpB_lmpB_flight.png`.

## Not done yet (Phase 2 follow-ups)

1. **Tech violations don't block launch.** They show in red in the part menu and the designer, but a violating design can still be launched. Options: a stock pre-flight check, or locking the engine in flight when the design is over the caps.
2. **Balance: pressure-fed engines are very heavy.** The on-engine pressurant term sizes helium for a 300 s burn: a 78 kN, 21 bar engine weighs 0.85 t, and its helium spheres are 2.6 m across. Decide on burn time, or on putting pressurant in the tanks (DESIGN §5.5, PHASE1_RESULTS change 5).
3. **Visuals.** Kit pieces are plain primitives and can hide under a tank from some angles. No GG exhaust plume, no heat glow, no TexturesUnlimited path (Phase 3). The plume `referenceDiameter` values are first guesses.
4. **Untested in game.** Symmetry counterparts (the code path exists), stock undo after a design change, surface-attached children on the chamber (not handled), KER and MechJeb with the real module (checked with the spike), and the parts-list icon.
5. **Not installed in ksp_mp.** The real pack needs its ~9-minute ModuleManager patch. GPTT node IDs are mapped in `Tech.cfg` but untested in a GPTT career.
6. **Designer.** IMGUI MVP. The UGUI window and in-scene drag handles are Phase 3 (§7.2).

## Test-copy and server changes

All listed in `spike/run/TEST_COPY_CHANGES.txt`. `spike/run/cleanup.sh` removes ProcEngines, the test driver, the spike and the extra mods from both test copies. The test driver is inactive unless `GameData/ProcEnginesTests/PluginData/autotest.txt` exists, and none does now.
