# Prior-art survey: procedural liquid/nuclear rocket engines for KSP 1.12.5

Researched 2026-10-17. Sources: SpaceDock API, GitHub API (`gh`), CKAN-meta, and web search. The KSP forum returns HTTP 403 to automated fetches and archive.org was offline, so forum details come only from search-result snippets and are marked as such.

Legend: **[V]** means I checked it against the URL given (repo file, licence file, CKAN metadata or SpaceDock API). **[U]** means unverified (inference, memory or a search snippet only).

---

## 1. Direct prior art: mods that actually let you design engines

| Mod | URL | Status | Licence | Verdict |
|---|---|---|---|---|
| **ProcEngines** (ferram4) | https://github.com/ferram4/ProcEngines | Abandoned. Last push 2017-09-20, KSP ~1.3 era | **MIT** [V] | **Best code to reuse.** A physics calculator only, never finished |
| **SPEngine / Simple Procedural Engines** (ec429) | https://github.com/ec429/SPEngine | Dormant. Last push 2023-08-24, RO/RP-1 for 1.12 | **MIT** [V] | Best model for the **design library and tooling** |
| **Ignition!** (Charle_Roger) | https://github.com/CharleRoger/Ignition, https://spacedock.info/mod/3868 | Active. v1.2.3.2, 1.12.5, 2026-09 | **GPL-3.0** [V] | Simple propellant-pair performance model |
| **Chemical Propulsion** (Charle_Roger) | https://github.com/CharleRoger/ChemicalPropulsion, https://spacedock.info/mod/3866 | Active. v1.7.1, 1.12.5, 2026-10 | **GPL-3.0** [V] | Curated propellant set, PARTUPGRADE-gated subtypes |
| ProceduralEnginesKSP (Jbaltus0) | https://github.com/Jbaltus0/ProceduralEnginesKSP | Empty repo (2017) [V] | none | Dead |
| E4 Jet Propulsions Laboratory | https://forum.kerbalspaceprogram.com/topic/126473-development-e4-jet-propulsions-laboratory-create-your-own-engines | Discussion stage only, no release found [U] | n/a | Idea only |
| Forum threads: "Procedural Engines?" (2017), "Let's Talk About Procedural Engines", "Would you want procedural engine nozzles in KSP 2" | https://forum.kerbalspaceprogram.com/topic/162487-procedural-engines/ · https://forum.kerbalspaceprogram.com/topic/131613-lets-talk-about-procedural-engines/ · https://forum.kerbalspaceprogram.com/topic/208254-would-you-want-procedural-engine-nozzles-in-ksp-2 | Discussion only [U] | n/a | Useful for gauging demand and design ideas |

### 1.1 ferram4/ProcEngines [V]
- **Files:** `EngineCalculatorBase/GasGen/PresFed/Staged.cs` (engine cycles: pressure-fed, gas generator, staged combustion), `TurbopumpCalculator.cs`, `NozzleCalculator.cs`, `PropellantMixtureLibrary.cs`, plus an IMGUI `EngineGUI.cs`.
- **Data:** `PropellantMixtures/*.cfg` holds tables for LH2/LOX, Kerosene/LOX, CH4/LOX and UDMH/NTO. Each table is indexed by O/F ratio and chamber pressure Pc (0.25–100 MPa) and stores Tc, Te, Pe, exit molecular weight, gamma and exit Mach, all at area ratio 3. A `frozenAreaRatio` setting switches the nozzle from shifting to frozen flow at that point.
- **Data provenance:** `RPAEngineData.cfg` is a "EngineDataGatherer" settings file for **Rocket Propulsion Analysis (RPA)**, and `config_parser.py` turns RPA output into these tables. That is exactly the offline precompute approach you are planning.
- **Nozzle geometry:** `BellNozzleParams.cfg` stores Rao thrust-optimised-parabola exit and inflection angles against area ratio (5–250) and relative length (60–100 % of a 15° cone). `NozzleCalculator` builds the bell contour from that (points E, Q, N normalised to throat radius, plus a converging arc). It also computes divergence and skin-friction efficiency, and supports conical vs bell shapes. **This contour code can directly drive a procedural nozzle mesh.**
- **Gaps:** It never writes to `ModuleEngines`; nothing in the repo references it. The test part reuses the stock Skipper model, so there is no mesh generation. There is no persistence, no tech gating and no plumes.
- **Licence:** MIT, so you can fork it or copy code with only the copyright notice. Expect to port it to KSP 1.12 / .NET 4.x APIs. [U: it has not been compiled against 1.12]
- The forum thread for Simple Procedural Engines says ferram4 "tried it and burnt out" (search snippet) [U].

### 1.2 ec429/SPEngine ("Simple Procedural Engines", RO/RP-1) [V]
- **Forum thread:** https://forum.kerbalspaceprogram.com/topic/181186-181-simple-procedural-engines (could not fetch it; 403).
- **Design model:** It does no physics. Each "Family" (A, B, … ≈ historical engine lines) has TechLevels with `techRequired`, maxThrust, maxIgnitions, Isp, mass, cost, propellants, ullage/pressure-fed flags, entryCost and toolCost. A design scales from those: `mass *= (T/Tmax)^0.8` and `cost *= (T/Tmax)^1.2 * ((ign+1)/(maxIgn+1))^0.2`.
- **Tooling and upgrades:** An untooled design costs ×1000. Upgrading a design to a new TechLevel scales thrust linearly and gives 80 % of the old tooling as credit.
- **Persistence (the pattern worth copying):** `DesignLibrary` keeps designs in a dictionary keyed by GUID, saved through ConfigNode in a scenario. `ModuleSPEngine` on a cloned part stores which design it uses. At load it builds a RealFuels `CONFIG` node (maxThrust, atmosphereCurve, …) and calls `ModuleEngineConfigs.SetConfiguration()`. So there is one part per family, and designs live in the save, not in part cfgs.
- **Dependencies:** RealFuels is a hard dependency, and it clones RO/ROEngines parts for models. It also ships `TestFlight_techTransfer.cfg`.
- **Licence:** MIT, so it can be reused freely.

### 1.3 Ignition! / Chemical Propulsion [V]
- **Ignition!** takes per-resource `IgnitionPropellantConfig` values (`IsOxidizer`, `MixtureConstant`, `ThrustMultiplier`, …). From those it derives each fuel/oxidizer pair's mixture ratio, thrust multiplier, Isp multiplier and ignition potential. It works with B9PartSwitch and TweakScale.
- **Chemical Propulsion** adds a Nertea-style curated propellant set (Kerosene, LH2, Hydrazine, HTP, LOX, NTO, PBAN). With Chemical Tech Tree installed, each propellant gets a PARTUPGRADE that unlocks B9PS subtypes. It also has a nuclear add-on, `ChemicalPropulsionNuclear` (CKAN), which adds high-thrust, low-Isp NTR propellant options.
- **Licence:** GPL-3.0. You can reuse it only if your mod is GPL-3.0 too. Even without reusing code, it is a good design reference and a likely compatibility target.

---

## 2. Performance, engine-module and reliability frameworks

| Mod | URL | Status (latest) | Licence | Relevance |
|---|---|---|---|---|
| **RealFuels** | https://github.com/KSP-RO/RealFuels | Active. v15.16.1.0, 2026-10-05 [V] | **CC-BY-SA** (readme: "License remains CC-BY-SA as modified by ialdabaoth"; version not stated) [V] | `ModuleEngineConfigs` (multiple `CONFIG`s, TechLevels, entry costs, PARTUPGRADE/ECM support, B9PS integration); `ModuleEnginesRF` + `SolverRF` (ignitions, ullage via `UllageModule/UllageSimulator`, pressure-fed, throttle floor, per-run Isp/flow/MR variance, residuals, chamber-temperature heating). **Isp still comes from an `atmosphereCurve`.** There is no chamber-pressure or expansion physics [V, from source grep]. Its ullage code derives from HoneyFox's MIT EngineIgnitor. |
| **SolverEngines** | https://github.com/KSP-RO/SolverEngines | Last push 2023-11; CKAN v3.14.0, 1.12 [V] | **LGPL-2.1** (CKAN) [V] | `ModuleEnginesSolver`, a ModuleEnginesFX subclass that hands thrust and Isp to a pluggable `EngineSolver`. **The cleanest legal base for a physics-driven engine module.** Under the LGPL you can depend on its DLL without your mod becoming LGPL. |
| **AJE (Advanced Jet Engine)** | https://github.com/KSP-RO/AJE | CKAN v2.20.0, 1.12 [V] | LGPL-2.0 [V] | Thermodynamic jet solvers built on SolverEngines. Reference for gas-property/cycle code structure. |
| **TestFlight** | https://github.com/KSP-RO/TestFlight | Active. v2.12.1.0, 2026-09-27 [V] | **CC-BY-NC-SA-4.0** (readme) [V] | Flight-data ("du") driven reliability. Modules include `TestFlightReliability_EngineCycle`, `TestFlightFailure_IgnitionFail` and `FlightDataRecorder_Engine`. Its tech-transfer concept is something SPEngine uses. Because of NC-SA, integrate through its API or configs rather than copying code. |
| **Kerbalism reliability / KerbalismEngineFailures** | https://github.com/Kerbalism/Kerbalism (`GameData/KerbalismEngineFailures`) | Active. v3.43 [V] | **Unlicense** (public domain) [V] | `EngineFailures` module with `rated_operation_duration` / `rated_ignitions`. A value of `-2` means "auto", resolved from `KERBALISM_ENGINE_RELIABILITY` RESOURCE roles and FAMILY nodes (burn_min/max, base/min/max_ignitions). **A procedural engine can set rated_* at design time.** Public domain, so code can be copied freely. Your existing Kerbalism setup makes this the natural reliability target. |
| **Engine Ignitor (Reignited)** | https://github.com/linuxgurugamer/EngineIgnitor | v1.3.7.8, 1.12.5 (2025-03) [V] | Original code MIT (HoneyFox, LICENSE.txt). CKAN and SpaceDock list it as CC-BY-SA [V; the licences conflict] | Ignition counts, ignitor resources, ullage |
| **KSP Interstellar Extended** | https://github.com/sswelm/KSP-Interstellar-Extended | v1.29.6, 1.12.5 (CKAN) [V] | Custom "KSP Interstellar License": derivatives allowed if you keep the licence file and credit the KSPI team [V] | `FNPlugin/Propulsion/ThermalNozzleController.cs` (~2400 lines) computes `baseMaxIsp = sqrt(coreTemperature) * mult`, sets thrust from `reactorPower / Isp / g0`, and **rebuilds `AttachedEngine.atmosphereCurve` and `maxFuelFlow` at runtime**. It handles propellant decomposition and soot. **This is the NTR physics reference**, and it shows how to push computed values into a stock ModuleEngines. |
| **SystemHeat – FissionEngines** | https://github.com/KSPModStewards/SystemHeat | CKAN v0.9.1, 1.12 [V] | MIT [V] | Ties NTR operation to a reactor/heat loop. MIT, so it is reusable. |
| **SMURFF** | https://github.com/Kerbas-ad-astra/SMURFF | CKAN v1.9.1 (max 1.9.1), last push 2024-01 [V] | Apache-2.0 [V] | An MM patch: biprop engines get +50 % thrust and −62.5 % mass, SRBs −40 % dry mass and +40 s Isp. NTRs, jets, monoprop and ion engines are left alone. Use it as a **balance target** for "real-ish" TWR (stock biprop TWR 15–25 vs real 60–150). |

---

## 3. Mesh generation, models, switching and scaling

| Mod | URL | Status | Licence | Relevance |
|---|---|---|---|---|
| **Procedural Parts** | https://github.com/KSP-RO/ProceduralParts | Active. v2.8.0.0 (2025-07), commits 2026-10 [V] | **CC-BY-SA-3.0** (`GameData/ProceduralParts/Licence.txt`). CKAN says "CC-BY-SA" [V] | `ProceduralSRB.cs`: `SRB_BELL` nodes, each with its own `atmosphereCurve`, `bellChokeDiameter` and gimbal. You pick one through `UI_ChooseOption`, and the **pre-made `SRBBell.mu` is scaled (`bellScale`), not generated**. Thrust or burn time is tweakable, and cost is `thrust*0.5*mult`. Its **surface-of-revolution mesh code** (`ProceduralAbstractSoRShape`, `ProceduralShapeBezierCone`) is a good basis for a revolved bell mesh. CC-BY-SA does work for code, but its share-alike terms would apply to any derived module. |
| **SSTU (Shadowmage)** | https://github.com/shadowmage45/SSTULabs | Abandoned. Last release 0.12.50.162 (2020-02-09, KSP 1.8.x [U]) [V] | **Code GPL-3.0, assets CC-BY-NC-SA-4.0** [V] | `SSTUModularEngineCluster` handles cluster layouts, spacing and **selectable/scaled prefab mounts**; the mounts are not procedural. `MeshGenerator/` (`ProceduralModel`, `ArcMeshGenerator`, `CylinderMeshGenerator`) does procedural meshes for decouplers and fairings. Also includes `SSTUEngineThrustCurveGUI` and `SSTUDeployableEngine`. |
| **ROLib / ROTanks / ROEngines / ROUtils** | https://github.com/KSP-RO/ROLibrary · /ROTanks · /ROEngines · /ROUtils | Active (2026) [V] | ROLib **CC-BY-SA-4.0** (CKAN). ROTanks MIT (GitHub) vs CC-BY-SA (CKAN). **ROEngines CC-BY-NC-ND-4.0** (no derivatives). ROUtils CC-BY-NC-SA-4.0 [V] | ROLib's `ROLModelDefinition/Layout` is SSTU-derived model-definition and switching code. ROEngines uses B9PS and RealFuels for engine variants, and its models cannot be modified. |
| **Procedural Fairings** | https://github.com/KSP-RO/ProceduralFairings (fork of e-dog) | Active. v6.8.0.0, 2026-08-31 [V] | **CC-BY-4.0** (KSP-RO fork, GitHub). Original e-dog: CC-BY-3.0 [V] | Runtime mesh generation (`FairingSide.cs`, `ProcAdapter.cs`) plus **`FairingSideShapePreset.cs`, a shape-preset system**. Only attribution is required, making it **the most permissive procedural-mesh reference** in this list. |
| **ProceduralFairings-ForEverything** | https://github.com/KSP-RO/ProceduralFairings-ForEverything | v0.3.0, max 1.11 (CKAN) [V] | CC-BY-SA [V] | Configs only |
| **Decoupler Shroud** | https://github.com/linuxgurugamer/DecouplerShroud, https://spacedock.info/mod/1692 | 1.12.5, 2026-09 [V] | MIT [V] | Generates a procedural shroud mesh around engines. Small, MIT-licensed mesh-generation example. |
| **DeployableEngines** (Nertea) | https://github.com/post-kerbin-mining-corporation/DeployableEngines | CKAN v1.3.1, 1.12 [V] | MIT [V] | Animates nozzle extensions (useful if you offer extendable nozzles) |
| **B9PartSwitch** | https://github.com/blowfishpro/B9PartSwitch (now KSPModStewards) | CKAN v2.21.0.4 [V] | **LGPL-3.0** [V] | `SUBTYPE` supports `upgradeRequired` (tied to a **PARTUPGRADE**), `transform` and `TRANSFORM` (offset/scale), `addedMass`/`addedCost`, and `MODULE { IDENTIFIER { name, engineID } DATA {…} }` for patching engine modules. The wiki calls MODULE switching "highly experimental" (https://github.com/blowfishpro/B9PartSwitch/wiki/ModuleB9PartSwitch) [V]. Fine to use as a dependency. |
| **TweakScale (Lisias)** | https://github.com/TweakScale/TweakScale | v2.4.8.9, 1.12.5 [V] | GPL-2.0 or "restricted" [V] | `ModuleEngines`: thrust and fuel flow scale with exponent 2.5, `heatProduction` with −0.5, and `ModuleEngineConfigs` is ignored [V] |
| **TweakScale Rescaled (JonnyOThan)** | https://github.com/JonnyOThan/TweakScale | v3.3.2.1, 1.12.5 [V] | CC-BY-NC-SA-4.0 [V] | Same 2.5 exponents for engines. Its "Engine" behaviour gives mass ^2.5; "SRB" gives thrust and flow ^3 [V]. **Use 2.5 as the thrust and mass size-scaling prior.** |
| Kerbal Atomics / Cryo Engines / CryoTanks (Nertea) | https://github.com/post-kerbin-mining-corporation/KerbalAtomics · /CryoEngines · /CryoTanks | KA v1.3.4 (2024-08), CE 2025-02, 1.12 [V] | Readme: **code and configs MIT, art ARR**. CKAN says "restricted"; SpaceDock says CC-BY-NC-SA-4.0 [V; the licences conflict] | Fuel-mode switching for LH2 vs LF (`KerbalAtomics-NTRsUseLF` patch on CKAN [V]). `ModuleCryoTank` boil-off. You cannot reuse the art. |

---

## 4. Plumes / FX

| Mod | URL | Status | Licence | Relevance |
|---|---|---|---|---|
| **Waterfall** | https://github.com/KSPModStewards/Waterfall | Active. 0.11.x (2026-04 / 2026-09 push) [V] | **CC-BY-NC-SA-4.0** [V] | `ModuleWaterfallFX` declares `TEMPLATE { templateName, overrideParentTransform, position, rotation, scale }` (`WaterfallEffectTemplate.cs`). The runtime API includes `SetControllerValue(controllerID, value)`, `FindEffect`, `AddEffect/CopyEffect/RemoveEffect` and `AddController`. Controllers include `CustomPushController`, `atmosphere_density`, `throttle`, `thrust`, `mach` and `gimbal`. To scale a plume to a generated nozzle: set the template `scale`/`position` from the throat and exit radius, and push an expansion-state value through a custom push controller. Use it as a soft dependency instead of copying code (NC-SA). |
| RealPlume | https://github.com/KSP-RO/RealPlume | Active (2026-07 push) [V] | CC-BY-SA (CKAN, old entry). The asset folders have mixed per-author licences, some ARR (e.g. `Nertea_FX_New/License_ARR.txt`) [V] | Legacy SmokeScreen plumes. Superseded by Waterfall. |
| Hot Rockets | https://forum.kerbalspaceprogram.com/threads/65754 | Last CKAN version for KSP 0.25 [V] | CC-BY-NC [V] | Obsolete |
| Stock Waterfall Effects / Restock Waterfall Expansion | https://spacedock.info/mod/3149 | 1.12.5 [V] | CC-BY-NC-SA-4.0 [V] | Template sources |

---

## 5. Upgrades, presets, design libraries and runtime parts

- **Stock PARTUPGRADE / PartUpgradeHandler.** Global `PARTUPGRADE { name, partIcon, techRequired, entryCost, title, … }` nodes pair with per-module `UPGRADES { UPGRADE { name__ = X; … } }` (`IsAdditiveUpgrade__`). Forum reports say `maxThrust` changes through upgrades were ignored in some cases, and that upgrades can't reliably enable or disable modules. Sources: https://forum.kerbalspaceprogram.com/topic/155309-is-it-possible-to-add-modules-through-part-upgrade, https://forum.kerbalspaceprogram.com/topic/186285-passive-part-upgrades-in-rd, https://forum.kerbalspaceprogram.com/topic/159452-lets-talk-about-partmodules-upgrades [U: snippets only]. **Recommendation:** have your own module query `PartUpgradeManager.Handler.IsUnlocked(name)` / `IsAvailableToUse(name)` [U: from memory of the API] to unlock propellants, cycles and Pc/ε limits. Don't depend on stock field patching.
- **PartUpgrade (supermerill)** https://github.com/supermerill/KspPartUpgrade, CC-BY-4.0, KSP 1.0.4. Pre-stock prior art [V].
- **UpgradesUIExtensions** https://github.com/gotmachine/UpgradesUIExtensions. Unlicense, KSP 1.2.2. Shows upgraded stats in the VAB tooltips [V].
- **RealFuels TechLevels + ECMs** and **SPEngine families/TechLevels**: two proven tech-progression models (see above).
- **ModulePartVariants (stock):** switches meshes and textures and adds mass/cost per variant. It does not switch module data [U, general knowledge].
- **Runtime PART generation: Weldatron** https://github.com/Phantomical/Weldatron. **MIT**, active 2026-07 [V]. It writes a PART cfg to `GameData/zzzWeldedParts/Parts/<id>.cfg`, registers a `UrlConfig`, calls `PartLoader.Instance.ParsePart(...)`, and adds the result to `loadedParts`, `APFinderByName` and `APFinderByIcon`. It then renders a thumbnail with `CraftThumbnail.TakePartSnapshot` and bakes `DRAG_CUBE` through a `DragCubeSystem` coroutine. It also ships notes (`notes/part-prefab-structure.md`). **This is working, MIT-licensed proof that "design → real AvailablePart" is possible in 1.12.**
  - **Multiplayer caveat [U, inference]:** with LunaMP, every client would need the generated cfg. SPEngine's approach is safer for multiplayer: one generic part, with the design stored in module and scenario data that syncs through the ProtoPartModuleSnapshot.
- **Other runtime/library approaches:**
  - **RackMount** (MIT, https://github.com/pehvbot/RackMount) adds parts and modules from inventory at runtime [V; I only checked the repo metadata and the `AddPart.cs` hit].
  - **Craft Manager** https://github.com/Sujimichi/CraftManager, CC-BY-4.0, KSP 1.9 [V]. A craft and subassembly list UI.
  - The ProceduralFairings shape presets (above).
  - B9 Procedural Wings (https://github.com/B9-Procedural-Wings/B9-PWings-Modified, CC-BY-NC-SA-4.0 per CKAN [V]); its shape copy/paste and presets are [U].
  - **Not found:** "Merged Parts", "Engine Group Controller", "Nuclear Engines OSS NTR". None of them turned up on SpaceDock, in CKAN-meta, on GitHub or through web search. "Rescale!" is a planet-rescale mod and isn't relevant [U]. Part Commander (GPL-3.0) only gives access to action menus and isn't relevant.
- **Engine Analyzer** https://github.com/Iftn1/KSP-EngineAnalyzer, MIT, 1.12.5, 2026-06 [V]. A VAB engine-recommendation and Δv tool, useful as a UI reference only.

---

## 6. Offline performance libraries (to precompute c*, γ, Tc, MW tables)

| Tool | URL | Licence | Notes |
|---|---|---|---|
| **NASA CEA (new release)** | https://github.com/nasa/cea | **Apache-2.0** [V] | `pip install cea` (Python ≥3.11), plus Fortran/C builds. Rocket-performance mode, the canonical source. Apache-2.0 lets you ship the generated tables in any licence. |
| **RocketCEA** | https://github.com/sonofeft/RocketCEA | GPL-3.0 [V] | Python wrapper for the Fortran CEA: `get_Cstar`, `get_Isp`, `get_Chamber_MolWt_gamma`, frozen/equilibrium options. The tool is GPL, but its numeric output is data, so the tables aren't GPL [U, the usual reading]. |
| **RocketIsp** | https://github.com/sonofeft/RocketIsp | GPL-3.0 [V] | Delivered Isp: divergence, boundary layer, kinetics, mixing efficiencies. Good model for the efficiency stack. |
| **RocketProps** | https://github.com/sonofeft/RocketProps | GPL-3.0 [V] | Liquid propellant properties (density, vapour pressure), useful for pump power and tank pressure |
| **proptools** | https://github.com/mvernacc/proptools | MIT [V] | Nozzle, turbopump and electric-propulsion calculations, ideal-rocket equations. Last push 2022. |
| **Cantera** | https://github.com/Cantera/cantera | BSD-3-Clause (Caltech/Sandia text) [V] | Equilibrium solver alternative to CEA |
| **Rocket Propulsion Analysis (RPA)** | https://www.rocket-propulsion.com [U] | Commercial, with a free Lite edition [U] | ProcEngines used it (`RPAEngineData.cfg`). Its licence terms for redistributing output were not checked. |

---

## 7. Takeaways for your mod

1. **Nothing complete exists.** ProcEngines (physics only, no mesh, no gameplay) and SPEngine (gameplay only, no physics, RealFuels and RO only) are each half of what you want, and **both are MIT**. Combining their ideas — ferram's RPA/CEA tables, Rao contour and cycle calculators with ec429's GUID design library, tooling and tech-level upgrade flow — is legally clean.
2. **Engine module:** subclass `ModuleEnginesFX` yourself, or depend on SolverEngines (LGPL-2.1), and write `atmosphereCurve` and `maxFuelFlow` at runtime, the way KSPIE's ThermalNozzleController does. Make RealFuels an optional integration (SPEngine's `SetConfiguration` path) rather than a requirement.
3. **Mesh:** revolve the Rao contour from ProcEngines' NozzleCalculator. Use ProceduralFairings (CC-BY-4.0), Decoupler Shroud (MIT) or Procedural Parts' SoR shapes (CC-BY-SA-3.0) as references for mesh, collider and drag-cube updates. Weldatron shows the drag-cube baking calls.
4. **Plumes:** use Waterfall templates at runtime, scaled by exit diameter, as a soft dependency.
5. **Reliability:** set Kerbalism `EngineFailures` `rated_ignitions` / `rated_operation_duration` from the design (public domain). Add an optional TestFlight config.
6. **Tech:** gate propellants, cycles and Pc/ε ranges with stock PARTUPGRADE nodes queried from your module, as Chemical Propulsion and B9PS `upgradeRequired` do.
7. **Licences to avoid copying from:** Waterfall, TestFlight, TweakScale Rescaled, ROUtils and SSTU assets (NC-SA); ROEngines (NC-ND); Nertea art (ARR); RealFuels and Procedural Parts code (SA would apply). Reference them or interoperate through configs and APIs.
