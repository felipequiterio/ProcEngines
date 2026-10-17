# ProcEngines

A procedural liquid-rocket-engine mod for Kerbal Space Program 1.12.5: one base part whose module generates the engine (propellant, cycle, chamber pressure, expansion ratio) with a procedural mesh, Waterfall plumes, tech caps and a design library. Built for a LunaMultiplayer co-op pack (Sol, FAR, Kerbalism, Waterfall).

| Document | What |
|---|---|
| [DESIGN.md](DESIGN.md) | Full design |
| [SPIKE_RESULTS.md](SPIKE_RESULTS.md) | Phase 0: integration spike in KSP (all 7 checks pass) |
| [PHASE1_RESULTS.md](PHASE1_RESULTS.md) | Phase 1: performance and mass model, calibrated against real engines |
| [PHASE2_STATUS.md](PHASE2_STATUS.md) | Phase 2: MVP in game (module, mesh, designer, tech caps, library, Waterfall, LMP) |
| [PHASE3_STATUS.md](PHASE3_STATUS.md) | Phase 3: visual pass (materials, heat glow, kit pieces, GG exhaust, plume calibration, drag handles) |
| [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) | ferram4 Rao table (MIT), NASA CEA provenance |

Layout:

- `src/ProcEngines.Model` – pure C# model (no Unity/KSP), `dotnet build -c Release`
- `src/ProcEngines` – the KSP plugin (part module, geometry, designer, library), `dotnet build -c Release`
- `tests/ProcEngines.KspTests` + `tests/ksp/run.sh` – self-driving in-game tests (`run.sh <A|B> <sandbox|career|lmpA|lmpB|visual|plumes>`)
- `tests/ProcEngines.Model.Tests` – `dotnet run -c Release` (add `report`, `fit` or `cv` after `--`)
- `tools/gen_propellants.py` – regenerates `GameData/ProcEngines/Data/Propellants.cfg` with NASA CEA
- `GameData/ProcEngines` – the mod's data and part (the plugin DLLs are copied into `Plugins/` on deploy)
- `spike/` – Phase 0 throwaway spike and its self-driving in-game test harness
- `research/`, `visualization/` – prior-art studies and a browser mock-up of the designer

Builds reference DLLs from a KSP install (`KSPRoot` property, default `..\..\ksp_test_A`).
