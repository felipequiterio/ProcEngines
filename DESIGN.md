# Procedural Engines for KSP 1.12.5: design document

Status: design only, no code yet. Written 2026-10-17 for the `ksp_mp` LunaMultiplayer pack (Sol at real scale, Mirage, FAR, SMURFF, Kerbalism, Waterfall + StockWaterfallEffects, CryoTanks/CryoEngines, B9PartSwitch, Procedural Parts, Near Future, Bluedog DB, Tantares, SystemHeat, GPTT tech tree).

Working name: **ProcEngines**. Code namespace `ProcEngines`, part names `procEngine-liquid` and `procEngine-nuclear`.

---

## 0. Goals and non-goals

**Goals**

- Design a liquid-fuel or nuclear-thermal engine in the VAB/SPH by choosing: propellant combination, engine cycle, chamber pressure (Pc), nozzle expansion ratio (ε), and size (thrust or exit diameter).
- Physically sound performance: Isp vs ambient pressure, flow separation, cycle penalties and mass scaling, calibrated so a "Merlin-like" or "RL10-like" design lands within about 5–10 % of the real engine.
- Tech-tree-gated upgrades (which cycles, propellants, Pc and ε limits, efficiencies, nozzle materials, NTR fuel temperatures).
- A procedurally generated bell nozzle and chamber, with kit-bashed turbopumps and plumbing that change with the cycle.
- Waterfall plumes picked and scaled to fit the generated nozzle.
- A design library: save designs and spawn them from a VAB tab. Every design is the same base part with a different saved configuration. A design never becomes its own PART config, so there is no restart and no ModuleManager re-patch (about 9 minutes in this pack).
- Works in LunaMultiplayer: other players see the right engine without having the designer's library.

**Non-goals (for the MVP)**

- Full RealFuels-style simulation (ullage, pressure-fed tank pressurisation, residuals, boil-off coupling). The pack doesn't use RealFuels, and the tanks use stock-style fixed mixtures.
- Solid rockets (Procedural Parts already covers them), electric propulsion, air-breathers, aerospikes (possible later).
- Generating PART configs at runtime or at load time.

---

## 1. Facts about the target install that shape the design

Checked against `E:\Kerbal Space Program Instances\ksp_mp\GameData` on 2026-10-17:

| Area | What is there | Consequence |
|---|---|---|
| Propellants in tanks | Stock `LiquidFuel`/`Oxidizer` (0.9:1.1 by units). CryoTanks B9 tank types `LH2`, `LH2O`, `LM`, `LMOx` (+ Cryo ZBO variants), also wired into Procedural Parts tanks via `CryoTanksProceduralFuelTanks.cfg`. `MonoPropellant`. CRP defines Kerosene, LqdOxygen, MMH, NTO, etc., but **no installed tank carries them**. | The supported set is LF/Ox (as kerolox), LqdMethane/Ox (methalox), LqdHydrogen/Ox (hydrolox), MonoPropellant, LqdHydrogen (NTR). Each pair is burned in the ratio its tank preset uses (see §4). |
| SMURFF | `enginelever = 1` → LFO engines get `maxThrust × 1.5`, mass `× 0.375` (TWR × 4), Isp untouched. Excludable per part via `SMURFFExclude = true`. | SMURFF'd stock engines land close to real-world TWR, so ProcEngines is calibrated directly to real engines. The base parts set `SMURFFExclude = true` so SMURFF doesn't patch them a second time. |
| Scale | Sol `SystemScale = Real` (~9.4 km/s to LEO, 1 atm at sea level). | Real-world engine numbers are the right target. |
| Tech tree | GPTT nodes like `gptt_liquid0..4`, `gptt_liquidVac0..4`, `gptt_hydrolox1..4`, `gptt_hydroloxVac`, `gptt_hydroloxVac2`, `gptt_methalox`, `gptt_methaloxVac`, `gptt_fuelPump`, `gptt_ntr1..4`, `gptt_precision`, `gptt_aerospikeRockets`, `gptt_metox`, `gptt_mono1..3`. | Gate upgrades on these node IDs, with fallback to stock node IDs when GPTT is absent (§6). |
| Waterfall | Waterfall core + 57 templates (`waterfall-kerolox-lower-1..5`, `-upper-*`, `waterfall-kerolox-exhaust-1` (GG exhaust), `waterfall-hydrolox-*`, `waterfall-methalox-*` incl. raptor/BE4, `waterfall-ntr-lh2-1`, `waterfall-hydrazine-monopropellant-upper-1`, ...). | Every cycle/propellant can map to an existing template. No new plume art is needed for the MVP (§9). |
| Kerbalism reliability | `KerbalismConfig/System/Reliability.cfg` adds no engine reliability; it notes that engine ignition/burn failures live in the optional **KerbalismEngineFailures** companion mod, which is **not installed**. Kerbalism.dll does contain `rel_ignitions` / `rel_duration` / `QualityManagement_ignitionlimit`. No TestFlight. | Engines in this pack currently never fail. ProcEngines carries its own ignitions and rated burn time, and adds an optional adapter if KerbalismEngineFailures or TestFlight is ever installed (§11). |
| SystemHeat | Installed (Near Future reactors use it). | An optional SystemHeat loop for NTR decay heat (§5.4). |
| LunaMultiplayer | Has `PartSync/*.xml` definitions (fields and methods synced live per module, e.g. `ModuleB9PartSwitch.OnPartModelChanged`). LmpClient knows `CraftType.Subassembly` (craft library sharing includes subassemblies). | Add a PartSync XML for `ModuleProcEngine`. Subassembly-based designs travel through LMP's craft sharing for free (§10). |
| Other relevant mods | ProceduralParts, ROUtils, TexturesUnlimited, ClickThroughBlocker, ToolbarControl, KSPCommunityFixes, Harmony, FAR, KER, MechJeb. | ClickThroughBlocker + ToolbarControl for the designer window. FAR re-voxelization hook (§8.6). |

---

## 2. Prior art

Full survey with URLs and verified/unverified tags: [research/prior-art.md](research/prior-art.md). Code studies of Procedural Parts, CRFP and B9 Procedural Wings are in `research/` and summarised in §15. The KSP forum blocked automated fetches, so forum-only facts there are unverified. Summary:

### 2.1 Direct prior art: nothing complete exists

| Project | Status | Licence | What it has | What it lacks |
|---|---|---|---|---|
| **ferram4/ProcEngines** (github.com/ferram4/ProcEngines) | Abandoned 2017 (KSP ~1.3), never released | **MIT** | The physics half. Cycle calculators (pressure-fed, gas generator, staged combustion), a turbopump model, and propellant tables (LH2/LOX, kerolox, methalox, UDMH/NTO) indexed by O/F and Pc 0.25–100 MPa, generated offline with RPA plus a Python parser. Shifting→frozen flow switch. `BellNozzleParams.cfg` holds the **Rao θn/θe tables** vs ε (5–250) and length fraction (60–100 %), and `NozzleCalculator` builds the bell contour plus divergence and friction losses. | Never writes to `ModuleEngines`, no mesh (uses the stock Skipper model), no persistence, no tech, no plumes. Not compiled against 1.12. |
| **ec429/SPEngine** "Simple Procedural Engines" (github.com/ec429/SPEngine) | Dormant 2023, RO/RP-1 on 1.12 | **MIT** | The gameplay half. Engine "families" with tech levels, a power-law scaling of mass and cost with thrust and ignitions, tooling costs, upgrading a design to a new tech level, and a **GUID-keyed design library saved in a scenario**. One part per family: `ModuleSPEngine` builds a RealFuels `CONFIG` and calls `ModuleEngineConfigs.SetConfiguration`. | No physics. Hard dependency on RealFuels + RO models. |
| **Ignition!** / **Chemical Propulsion** (CharleRoger) | Active, 1.12.5 (2026) | GPL-3.0 | Propellant-pair performance multipliers, PARTUPGRADE-gated B9PS propellant subtypes, an NTR add-on | Not procedural geometry or cycles. A likely compatibility target. Code reusable only under GPL. |
| Jbaltus0/ProceduralEnginesKSP, "E4 Jet Propulsions Laboratory", several forum threads | Dead / discussion only | – | Shows long-standing demand | – |

### 2.2 Frameworks and references

- **RealFuels** (CC-BY-SA, active). `ModuleEngineConfigs` (configs, tech levels, entry costs) and `ModuleEnginesRF` (ignitions, ullage, variance, residuals). Isp still comes from a hand-authored `atmosphereCurve`: there's no Pc/ε physics. Not installed in this pack and not needed. It *is* the reference for "rewrite an engine module's config at runtime".
- **SolverEngines** (LGPL-2.1). `ModuleEnginesSolver`, a `ModuleEnginesFX` subclass with a pluggable solver. A legal dependency option if we ever want per-frame physics. The MVP doesn't need it: a precomputed atmosphereCurve is exact for a fixed-Pc engine (§5.1).
- **KSP Interstellar Extended** (custom licence: derivatives allowed with credit). `ThermalNozzleController` rebuilds `atmosphereCurve` and `maxFuelFlow` of a stock engine at runtime from reactor temperature and power. This is the NTR reference.
- **Kerbalism `EngineFailures`** (KerbalismEngineFailures companion, **Unlicense / public domain**). Has `rated_ignitions` and `rated_operation_duration` (−2 = automatic from propellant family). A procedural engine can set these from the design (§11).
- **TestFlight** (CC-BY-NC-SA-4.0): integrate only, never copy. **Engine Ignitor** (MIT core): ignitions and ullage reference.
- **SMURFF** (this pack ships `LICENSE-GPL-3.0.txt`; the survey found Apache-2.0 on GitHub/CKAN, so check before quoting): a balance target only.
- **TweakScale**: thrust and mass scale with size exponent 2.5. A sanity prior for our mass model at constant Pc.
- **Weldatron** (MIT, 2026). Proves that design → real runtime `AvailablePart` works in 1.12 (writes a cfg, `PartLoader.ParsePart`, registers it, thumbnail, drag cubes). **We deliberately don't do this**, for multiplayer reasons (§10.1).

### 2.3 Mesh, plumes and switching

- **Procedural Parts** (CC-BY-SA-3.0). The SRB bell is a pre-made `SRBBell.mu`, scaled rather than generated, with one `atmosphereCurve` per bell option. Its surface-of-revolution shape classes and its attach-node/attached-part moving logic are the best behavioural reference. Read them, don't copy them (share-alike).
- **Procedural Fairings** (KSP-RO fork, CC-BY-4.0) and **Decoupler Shroud** (MIT): the most permissively licensed runtime-mesh + collider + drag-cube examples.
- **SSTU** (code GPL-3.0, assets NC-SA; abandoned 2020): mesh generator classes and engine-cluster mounts (prefab mounts, not procedural).
- **Waterfall** (CC-BY-NC-SA-4.0). Templates with position/rotation/scale. Runtime API: `AddEffect`/`CopyEffect`/`RemoveEffect`, `FindEffect`, `SetControllerValue`, custom push controllers. Soft dependency through its API; never copy code.
- **B9PartSwitch** (LGPL-3.0): `upgradeRequired` subtypes and engine-module DATA patching. Not needed, since we drive our own module.
- **Stock PARTUPGRADE**: forum reports say field patching through upgrades is flaky (e.g. `maxThrust`). Use it only for R&D display and query unlock state ourselves (`PartUpgradeManager.Handler.IsUnlocked`/`IsAvailableToUse`, or the tech node state).

### 2.4 Offline thermochemistry

**NASA CEA** (github.com/nasa/cea, **Apache-2.0**, `pip install cea`) is the canonical source. Use it for the tables. RocketCEA / RocketIsp / RocketProps are GPL-3.0 tools (their numeric output is just data), proptools is MIT, Cantera is BSD-3. ferram's existing RPA tables (MIT) can bootstrap the MVP before our own CEA script exists.

### 2.5 Continue or start fresh?

**Start a new mod, and port the MIT pieces of both abandoned projects into it.** There's no single project worth "continuing":

- ferram4/ProcEngines is a 2017 physics prototype with no KSP integration. Port its `NozzleCalculator` + `BellNozzleParams.cfg` (Rao contour, losses), its cycle/turbopump calculators as a starting point for §5, and optionally its propellant tables.
- SPEngine depends on RealFuels/RO, which this pack doesn't have. Borrow its design-library and tooling/upgrade **patterns** (GUID designs, scenario storage, tech-level upgrades of an existing design).

Licence the new mod **MIT**, so MIT and public-domain code can be included with attribution (keep their LICENSE notices in `THIRD_PARTY.md`). Interoperate with Waterfall, Kerbalism, SystemHeat, FAR and LMP through their APIs and configs only.

---

## 3. Architecture overview

```
GameData/ProcEngines/
  Parts/procEngine-liquid.cfg      one PART (placeholder model, ModuleProcEngine, ModuleEnginesFX, ModuleGimbal,
                                   ModuleWaterfallFX (NEEDS[Waterfall]), ModuleSurfaceFX, ModuleTestSubject)
  Parts/procEngine-nuclear.cfg     one PART (same, plus NTR modules: SystemHeat / Kerbalism Emitter via NEEDS)
  Data/Propellants.cfg             PROCENG_PROPELLANT nodes (thermochem tables, see §4)
  Data/Cycles.cfg                  PROCENG_CYCLE nodes (limits, penalties, mesh kit, see §5)
  Data/Tech.cfg                    PROCENG_TECH nodes (node id → caps, see §6)
  Data/Waterfall.cfg               PROCENG_PLUME nodes (template, reference diameter, see §9)
  Data/Balance.cfg                 global multipliers (TWR, Isp, cost), like SMURFF's lever
  Assets/procengines.bundle        kit-bash meshes and textures (pumps, valves, motor, reactor dome)
  Plugins/ProcEngines.dll
  PluginData/Designs/*.cfg         the player's global design library (not synced by Steam/CKAN, user data)
  LunaMultiplayer/PartSync/ProcEngines/ModuleProcEngine.xml
```

Code layers, kept separate so the physics can be unit-tested outside KSP:

1. **ProcEngines.Model** (plain C#, no Unity references): propellant tables, nozzle gas dynamics, cycle and mass model, NTR model. Input: `EngineDesign` (a small immutable record). Output: `EngineStats` (thrust vac/SL, Isp curve keys, mass flow, propellant ratios, mass, cost, dimensions, throttle range, ignitions, burn time, EC draw, thermal power).
2. **ProcEngines.Geometry** (Unity Mesh, no KSP): bell contour, chamber, revolve/sweep mesh builders, kit-bash layout rules.
3. **ProcEngines.UI** (Unity UGUI only, no KSP types): the designer window and library panel. They talk to the game through injected delegates/interfaces, CRFP's split, so the UI can be iterated in a plain Unity scene.
4. **ProcEngines.KSP**: `ModuleProcEngine : PartModule, IPartMassModifier, IPartCostModifier`, the editor controller (one instance: window, handles, input locks), the library, integrations (Waterfall, FAR, Kerbalism, SystemHeat, LMP).

### 3.1 Applying stats to a stock engine module at runtime

Don't subclass `ModuleEngines`. Keep a stock `ModuleEnginesFX` on the part, and on every design change build a `ConfigNode` (maxThrust, minThrust, heatProduction, PROPELLANT nodes, atmosphereCurve keys, `engineAccelerationSpeed`/`engineDecelerationSpeed`, `useThrustCurve=false`) and call `engine.Load(node)`. RealFuels' `ModuleEngineConfigs` uses the same proven approach. Then:

- set `maxFuelFlow` **and** `minFuelFlow` explicitly afterwards (`F / (Isp_vac·g0)`), whatever `Load` did. Procedural Parts' SRB skips `Load` entirely: it assigns `atmosphereCurve` and `maxThrust` and writes `maxFuelFlow` directly, a lighter fallback if `Load` misbehaves. Test both in the spike,
- call `engine.SetupPropellant()` if needed, and fire `GameEvents.onEditorShipModified` so the stock ΔV app, KER and MechJeb refresh,
- set `ModuleGimbal.gimbalRange` and gimbal speed, and move `thrustTransform` to the nozzle exit plane.

KER, MechJeb, RCSBuildAid and the stock ΔV app all read the live `ModuleEngines` fields, so they work unchanged.

### 3.2 Persistence: the design travels with the part

`ModuleProcEngine` persists the **whole design** (inputs) and the **derived stats** (outputs) in the part's MODULE node:

```
MODULE
{
  name = ModuleProcEngine
  designName = Kestrel-2 Vac
  designId = 6f1c...            // GUID of the library entry it came from (informational only)
  modelVersion = 3              // performance-model version that produced the stats
  DESIGN { propellant = Methalox  cycle = GasGenerator  pc = 95  eps = 60  thrustVac = 450  gimbal = 4 ... }
  STATS  { maxThrust = 450  mass = 0.61  cost = 2400  exitDiameter = 1.42 ... atmosphereCurve { key = 0 352 ... } }
  ignitionsLeft = 4
  burnTimeUsed = 112.4
}
```

Rules:

- `DESIGN` and `STATS` are written and read in explicit `OnSave`/`OnLoad` overrides. Don't rely on KSPCommunityFixes' `PersistentIConfigNode` patch, which Procedural Parts needs for its `FloatCurve` fields. Static data (propellant, cycle and plume tables) is never saved per part: it's read from the global config nodes, or copied from the prefab in `OnAwake`.
- `schemaVersion` + `modelVersion` from day one. Neither CRFP, B9 PWings nor VBPP versions its fields, and CRFP v0.6 broke old saves.
- Write every float with `CultureInfo.InvariantCulture` and round-trip format (`"R"` / `"G9"`), and parse the same way. VBPP saves culture-sensitive strings, so a craft saved on a comma-decimal Windows locale breaks for a player on a dot-decimal one. In LMP co-op across countries this matters.
- **In the editor**, the module recomputes `STATS` from `DESIGN` with the current model.
- **In flight**, it only applies the stored `STATS`, never recomputes them. A balance patch, or a different floating-point result on another player's PC, can't change an engine that is already flying. Every LMP client applies byte-identical numbers.
- The mesh **is** regenerated from `DESIGN` + `STATS` geometry fields on every client. It's deterministic and doesn't affect physics: colliders are built from the stored dimensions.
- If `modelVersion` of a vessel in the editor (loaded .craft) is older than the current one, the module shows "stats will update to model v3" and recomputes them. Designs aren't silently changed in flight.

---

## 4. Propellants

### 4.1 Supported combinations (MVP)

| Key | KSP resources (unit ratio) | Real-world analogue | Game O/F (mass) | Notes |
|---|---|---|---|---|
| `Kerolox` | LiquidFuel 0.9 : Oxidizer 1.1 | RP-1/LOX | 1.22 (stock LFO density 5 kg/unit) | Thermochem uses RP-1/LOX at O/F ≈ 2.3–2.6, but propellant is drawn at the tank preset ratio. Same compromise every stock engine makes. |
| `Methalox` | LqdMethane 3 : Oxidizer 1 (CryoTanks `LMOx` 1.875 : 0.625 units/volume; CryoEngines use the same) | CH4/LOX | ≈ 3.9 | Read the ratio from the `LMOx` B9 tank type at load, never hard-code it. |
| `Hydrolox` | LqdHydrogen 15 : Oxidizer 1 (CryoTanks `LH2O` 4.995 : 0.333; CryoEngines 1.5 : 0.1) | LH2/LOX | ≈ 4.7 | Hydrogen's low density makes pumps big, which the mass model has to capture (§5.3). |
| `Monoprop` | MonoPropellant | N2H4 catalytic | – | Pressure-fed only, low Isp (~230 s vac). For landers and RCS-scale verniers. Phase 4. |
| `NTR-H2` | LqdHydrogen | H2 NTR | – | Nuclear cycle only. |
| `NTR-CH4`, `NTR-H2O` (optional) | LqdMethane, Water (Kerbalism tanks carry Water) | – | – | Lower Isp, denser. Late, for fun/ISRU. |

**Mixture ratio.** The chemistry tables are evaluated at the real optimum O/F, while the engine draws propellant in the tank preset's unit ratio. That keeps every existing tank (CryoTanks, Procedural Parts, B9 tanks in Bluedog/Tantares) correctly balanced. An optional "mixture ratio" slider (±15 %) can come later. It would trade Isp for density and leave tanks slightly unbalanced, which is realistic.

**Unit ratios.** KSP PROPELLANT ratios are in units, not mass. For a target mass split `w_i` and resource density `ρ_i` (t/unit): `ratio_i = (w_i/ρ_i) / Σ(w_j/ρ_j)`. All densities come from `PartResourceLibrary` at runtime, so CRP changes are respected.

### 4.2 Thermochemistry tables (precomputed offline)

Don't run combustion equilibrium in-game. Precompute with **NASA CEA** (`pip install cea`, Apache-2.0; RocketCEA works too but is GPL) in an offline Python script in the repo's `tools/` folder, and ship the result as config. ferram4/ProcEngines' MIT RPA tables can bootstrap the MVP:

```
PROCENG_PROPELLANT
{
  name = Methalox
  fuel = CH4(L)   oxidizer = O2(L)   ofOptimal = 3.4
  // per chamber pressure (bar): characteristic velocity c* (m/s), effective gamma for nozzle flow, Tc (K), mean molar mass
  TABLE { pc = 10  cstar = 1795 gamma = 1.20 tc = 3420 mw = 21.0 }
  TABLE { pc = 50  cstar = 1835 gamma = 1.20 tc = 3530 mw = 21.4 }
  ...
  TABLE { pc = 300 cstar = 1868 gamma = 1.21 tc = 3640 mw = 21.8 }
  densityBulk = 0.83          // t/m³ at game O/F, for pump power
  coolantQuality = 0.7        // regen cooling capacity (H2 1.0, CH4 0.7, RP-1 0.45); limits Pc and thrust density
  cokingLimit = ...           // RP-1 only: restricts ox-rich staged combustion until a tech upgrade
  ignition = Spark            // Spark / Hypergolic / Pyrotechnic (TEA-TEB) → ignition count rules
}
```

One subtlety: a single "effective γ" per Pc (a frozen-flow fit) under-predicts hydrolox vacuum Isp by about 3 % (see §5.2 check). Better: tabulate **CEA Isp_vac and Cf_vac vs ε** directly per Pc (shifting equilibrium, 2D table Pc × ε, ~10 × 15 floats per propellant), and use γ only for the area–Mach/pressure relations and separation. Bilinear interpolation in log(Pc), log(ε).

---

## 5. Performance model

### 5.1 Nozzle and thrust (all cycles)

Definitions: `At` throat area, `ε = Ae/At`, `Pc` chamber pressure, `pa` ambient pressure, `pe` exit pressure.

- Exit Mach from ε by inverting the area–Mach relation (Newton or bisection, γ from the table):
  `ε = (1/M)·[(2/(γ+1))·(1 + (γ−1)/2·M²)]^((γ+1)/(2(γ−1)))`
- `pe/Pc = (1 + (γ−1)/2·M²)^(−γ/(γ−1))`
- Vacuum thrust coefficient:
  `Cf_vac = √( 2γ²/(γ−1) · (2/(γ+1))^((γ+1)/(γ−1)) · (1 − (pe/Pc)^((γ−1)/γ)) ) + ε·pe/Pc`
- Thrust at ambient pressure: `F(pa) = (Cf_vac − ε·pa/Pc)·Pc·At·η_Cf`
- `Isp(pa) = η_c*·c*·η_Cf·(Cf_vac − ε·pa/Pc) / g0`
- Mass flow `ṁ = Pc·At/(η_c*·c*)` is independent of `pa`. This matches stock `ModuleEngines` with `atmChangeFlow = false`: fuel flow stays constant and thrust scales with Isp. So `maxThrust` = vacuum thrust and the atmosphereCurve does the rest.

Efficiencies (tech-dependent, §6):

- `η_c*` combustion efficiency: 0.94 (early) → 0.985 (late). Pintle or coaxial-swirl injector upgrades.
- `η_Cf` nozzle efficiency = divergence factor × boundary-layer factor. Divergence for an 80 % Rao bell is about 0.985–0.99, for a 15° cone 0.983. Boundary-layer loss is 1–2 %, lower at high Pc and large size.

**Sizing.** The player picks three of {vacuum thrust (or sea-level thrust), Pc, ε, exit diameter}, and the fourth is derived. Default lock: thrust + Pc + ε → exit diameter.
`At = F_vac / (η_Cf·Cf_vac·Pc)`, `De = 2·√(ε·At/π)`.

### 5.2 Flow separation and the atmosphereCurve

Overexpanded nozzles don't follow the linear `−ε·pa/Pc` line forever. When `pe` drops below roughly 0.25–0.4 × `pa`, the flow separates inside the bell (Summerfield criterion; Schmucker's `p_sep/pa = (1.88·M − 1)^−0.64` is a refinement worth using).

Model: if `pe < k_sep·pa`, find the effective exit ratio `ε_sep` where the local wall pressure equals `k_sep·pa`, and evaluate `Cf` with `ε_sep` instead of ε. That's higher than the linear extrapolation, which matches reality. Separation also:

- adds a **side-load penalty**: a reliability hit when ignited or run while separated (§11) and a UI warning "will separate at sea level",
- for large ε at sea level, makes thrust collapse rather than go negative.

**atmosphereCurve generation.** Sample `Isp(pa)` at pa = 0, 0.05, 0.1, 0.2, 0.35, 0.5, 0.75, 1.0, 1.5, 2, 3, 5, 10 atm (Venus-capable). Stop early once Isp falls below ~5 % of vacuum, and add a final key at ~0. Store keys with tangents computed from finite differences so KSP's Hermite curve doesn't overshoot.

**Sanity check.** A quick script (frozen γ, η_c* 0.975, η_Cf 0.98, k_sep 0.35) gave:

| Case | Model vac / SL Isp (s) | Real engine |
|---|---|---|
| Kerolox, Pc 97 bar, ε 16 | 313 / 283 | Merlin 1D 311 / 282 |
| Kerolox, Pc 97 bar, ε 165 | 343 / (separated) | MVac 348 |
| Methalox, Pc 300 bar, ε 34 | 339 / 318 | Raptor 2 ~350 / 327 |
| Hydrolox, Pc 206 bar, ε 69 | 437 / 379 | RS-25 452 / 366 |
| Hydrolox, Pc 44 bar, ε 84 | 439 / – | RL10A-4 451 |

Kerolox lands within 1 %. Hydrolox and methalox come out 2–3 % low with a frozen γ, which is why §4.2 tabulates CEA Isp directly. The RS-25 sea-level number shows the separation model is slightly optimistic for big ε at sea level (the real engine runs pe ≈ 0.2 bar thanks to its high Pc). Tune `k_sep` against RS-25, Vulcain 2 and RL10 data.

### 5.3 Engine cycles

Each cycle is a `PROCENG_CYCLE` config node, so balance lives in data, not code.

| Cycle | Pc range (bar), tech-capped | Isp effect | Pump Δp / Pc | Mass and other effects | Ignitions / restart | Propellants |
|---|---|---|---|---|---|---|
| Pressure-fed | 5–25 | none (but low Pc limits ε at sea level and makes the engine big) | – (no pumps) | Light, simplest, no turbomachinery. Can't affect tank mass, so it gets a **pressurant mass term** (helium bottles on the engine, ∝ propellant flow × assumed burn time, §5.5). | Many (hypergolic-style: 30+), deep throttle (to 10 %) | all chemical, Monoprop |
| Electric pump-fed | 10–130 | none | 1.25 | Pump + motor + inverter mass; draws **ElectricCharge** as a third PROPELLANT, `P = ṁ·Δp/(ρ·η_pump·η_motor)`, 1 EC = 1 kJ (NF/Kerbalism convention). With stock-density batteries this only pays off for small engines, which is realistic (Rutherford). | Many, good throttle | Kerolox, Methalox |
| Gas generator (open) | 30–110 | **−1.5 to −3 %**: 2–5 % of flow is dumped through the turbine exhaust at low Isp | 1.3 | Moderate pump mass. Turbine exhaust duct + its own small plume. | 1–3 (TEA-TEB) or spark for cryo | all |
| Expander (closed) | 20–70 | none | 1.5 | Thrust **capped** (heat pickup scales with chamber area, ∝ F^(2/3), while needed turbine power ∝ F): about 300 kN for H2, 100 kN for CH4, not allowed for RP-1. Very light, very reliable. | Excellent (10+) | Hydrolox, Methalox |
| Expander (bleed / open), later | 30–90 | −1 % | 1.4 | Lifts the expander thrust cap about 3× (LE-5B, LE-9 style). | Good | Hydrolox, Methalox |
| Staged combustion (ox-rich or fuel-rich) | 120–270 | none (no dump) | 1.8–2.2 | Heavy turbomachinery and preburner, high Pc → small, dense engine with high TWR. Ox-rich kerolox needs a metallurgy upgrade (`gptt_metox`-type node). Lower early reliability. | 1–3 | all (ORSC for kerolox/methalox, FRSC for hydrolox) |
| Full-flow staged combustion | 200–330 | none | 1.6 | Two preburners, two turbopumps. Highest Pc, best TWR, latest tech. Gentler on turbines → reliability better than ORSC at equal tech. | 2–5 | Methalox (Hydrolox possible later) |
| Nuclear thermal | 20–90 | see §5.4 | 1.4 (bleed/expander-style pump) | Reactor-dominated mass. | 5–20, slow spool-up | NTR-H2 (+ optional CH4/H2O) |

**Pump power.** `P_pump = ṁ·Δp_pump/(ρ_bulk·η_pump)`, with `Δp_pump = (Δp/Pc)_cycle × Pc` plus a minimum injector/cooling Δp, and `η_pump` 0.55 (early) to 0.75 (late). Hydrolox's ρ_bulk ≈ 0.36 t/m³ against kerolox ≈ 1.03 gives ~3× the pump power per kg/s, which is why hydrolox engines have lower TWR. The model gets this for free.

### 5.4 Nuclear thermal model

No combustion. The reactor heats hydrogen to a fuel-element-limited chamber temperature `Tc`.

- **Tc by tech tier** (fuel element): graphite composite (NERVA/Rover) 2350–2500 K → CERMET / composite 2700–2900 K → advanced carbide 3000–3200 K.
- **c*** from ideal gas: `c* = √(R·Tc/M) / Γ`, with `Γ = √γ·(2/(γ+1))^((γ+1)/(2(γ−1)))`. For H2 (M = 2.016, γ_eff ≈ 1.35–1.40) at 2700 K, c* ≈ 4900 m/s → Isp_vac ≈ 850–900 s at ε 100–300. Better: tabulate H2 Isp vs (Tc, Pc, ε) offline with CEA, since low-Pc dissociation adds a few %.
- **Thermal power** `P_th = ṁ·Δh(Tc)`, with Δh(H2) ≈ 33–40 MJ/kg over the Tc range (NERVA: ~1100 MW for ~33 kg/s).
- **Mass** = `m_core0 + k_th·P_th + m_shield + m_pump + m_nozzle`:
  - `m_core0` minimum critical mass: 0.8 t graphite / 1.0 t CERMET (denser, but higher power density),
  - `k_th` 4–5 kg/MW (graphite) → 2–3 kg/MW (advanced),
  - optional internal shield (toggle, ~10 % mass) that cuts the radiation emitted toward the vessel.
  - Calibration: SNRE (367 MW, 73 kN, 875 s, ~2.5 t), NERVA-1 flight design (~1100 MW, ~250 kN, ~840 s, ~10 t with shield).
- **Pc** 20–90 bar, ε up to 300 (vacuum engine; sea-level use is legal but separates).
- **Throttle and spool**: min throttle 30 %, `engineAccelerationSpeed` ~0.2 (seconds-scale spool-up/down).
- **Heat (SystemHeat soft dependency)**: while running, propellant carries the heat away. After shutdown, decay heat (~6 % P_th dropping as t^−0.2) has to go into a SystemHeat loop or the engine overheats. Without SystemHeat, use stock part heat with a post-shutdown heat source.
- **Radiation (Kerbalism soft dependency)**: add a Kerbalism `Emitter` scaled by P_th while running (residual after shutdown), reduced by the shield option. The `Emitter` lives in the nuclear base part config under `NEEDS[Kerbalism]`. Its strength is set at runtime by `ModuleProcEngine`; verify that Emitter exposes a writable radiation field.
- Later: **LANTR** (LOX afterburning mode via `MultiModeEngine`: thrust ×2.5–3, Isp ~650 s), **bimodal** (electric generation when idle, NF-style).

### 5.5 Mass model (chemical)

Sum of components. Each one scales with a physically sensible exponent and is calibrated against real engines (Merlin 1D 470 kg, RL10A-4 168 kg, RS-25 3.18 t, RD-180 5.48 t, Raptor 2 ~1.6 t, Rutherford 35 kg, F-1 8.4 t, J-2 1.44 t, RS-68 6.6 t, Vinci 0.28 t, LR-87, Aestus).

1. **Thrust chamber** (pressure vessel + injector). Wall mass ∝ ρ_mat·Pc·V_c/σ, and `V_c = L*·At` with `At ∝ F/Pc`, so `m ∝ L*·F`, independent of Pc. A **minimum gauge** term ∝ chamber surface area dominates small/low-Pc engines. Injector plate ∝ D³·√Pc. `L*` per propellant (RP-1 1.0–1.3 m, CH4 0.9–1.1, H2 0.75–1.0). Contraction ratio `εc = 8·Dt^−0.6 + 1.25` (Dt in cm), the usual empirical fit.
2. **Nozzle**: wetted area × areal density. Bell area ≈ k·At·(ε − 1) for a fixed length fraction. Regeneratively cooled section (up to a tech-dependent ε_regen ≈ 5–30) at 15–25 kg/m²; radiatively cooled extension (niobium → carbon-carbon tech) at 5–10 kg/m². Radiative extensions are only allowed in vacuum-optimised designs (ε > 40) and add a max-temperature constraint.
3. **Turbomachinery**: `m_tp = a_cycle·P_pump^0.65` (kg, P in kW), with `a_cycle` from the cycle node (staged combustion ~1.5× GG, plus the preburner). Electric: pump `a·P^0.65` + motor/inverter `P/(5 kW/kg)` (tech: 3 → 8 kW/kg).
4. **Pressure-fed pressurant**: `m_press = c·ṁ·t_ref·Pc` (helium bottles sized for `t_ref` = rated burn time). This is what keeps pressure-fed from being free.
5. **Gimbal, valves, structure**: 6–10 % of the sum + a constant. Gimbal range adds mass ∝ range × thrust.

Then `mass = Σ × Balance.massMultiplier`. Report TWR (vacuum and SL) in the designer so the player sees what they're trading.

**Cost**: `cost = base + c_mass·mass + c_Pc·Pc^1.2·At + c_cycle + c_tech`, scaled to sit near SMURFF'd stock and Bluedog engines of similar thrust. Optional career **design certification fee**: a one-time charge the first time a new design is launched, waived for designs already flown.

### 5.6 Throttle, gimbal, other outputs

- Min throttle by cycle and injector tech: pressure-fed / electric / pintle 10–20 %, GG 60 %, SC 50 %, FFSC 40 %, expander 30 %, NTR 30 %. Upgrades lower these.
- Gimbal range: player-set 0–8°, mass cost as above.
- `heatProduction` ∝ (1 − regen efficiency) × thrust / exit area.
- Ignitions and rated burn time: §11.

---

## 6. Tech progression and upgrades

**Mechanism.** One data file maps tech nodes to capability caps:

```
PROCENG_TECH
{
  node = gptt_liquid1           // fallback: generalRocketry
  UNLOCK { cycle = GasGenerator }
  CAP    { propellant = Kerolox  pcMax = 70  epsMax = 30  thrustMax = 1200  etaCstar = 0.95  etaPump = 0.6  regenEps = 8 }
}
PROCENG_TECH
{
  node = gptt_metox
  UNLOCK { cycle = StagedCombustion  propellant = Kerolox  variant = OxRich }
}
```

At design time the module evaluates every researched node (`ResearchAndDevelopment.GetTechnologyState(node) == Available`) and takes the **max of each cap**. Sandbox ignores caps. In science/career, a design that exceeds the current caps (for example an imported one) shows the offending fields in red and can't be launched until they're fixed.

**Evaluation rules** (from Procedural Parts' old TECHLIMIT system, which worked well; §15.1):

- Read `PROCENG_TECH` once at load.
- Merge the caps of every researched node as a union: smallest minimum, largest maximum.
- 0 or missing means "no limit from this node".
- Sandbox skips caps entirely.
- If `ResearchAndDevelopment.Instance` isn't ready yet (early `OnStart`), retry from `Update()` and keep the design unvalidated until then.

**Don't gate on PARTUPGRADE.** Current Procedural Parts uses PARTUPGRADE + module `UPGRADES` blocks for its limits. That forces it to flip `PartUpgradesInSandbox` in the save settings and can require R&D purchases in career. The B9 PWings and forum reports agree that upgrade field patching is fragile.

**Optional R&D visibility:** emit display-only `PARTUPGRADE` nodes (`entryCost = 0`) so the tech tree shows "Staged combustion engines" icons. ProcEngines never reads them. If the Phase 0 spike shows they need purchasing or clutter the R&D screen, drop them and add a "ProcEngines tech" page to the designer window instead.

**Suggested progression in GPTT** (tuned in playtests):

| Tier | Nodes (GPTT) | Unlocks |
|---|---|---|
| 0 | `start`, `gptt_liquidFueledRockets`, `gptt_liquid0` | Pressure-fed + GG kerolox, Pc ≤ 50, ε ≤ 25, η_c* 0.94, conical-ish 15° nozzles only |
| 1 | `gptt_liquid1`, `gptt_liquidVac0`, `gptt_fuelPump` | Rao bell nozzles, ε ≤ 60, Pc ≤ 70 |
| 2 | `gptt_hydrolox1`, `gptt_liquidVac1` | Hydrolox, expander cycle, regen ε 15 |
| 3 | `gptt_liquid2..3`, `gptt_liquidVac2`, `gptt_precision` | Pc ≤ 110, η_c* 0.97, niobium radiative extensions, ε ≤ 150 |
| 3 | `gptt_methalox`, `gptt_methaloxVac` | Methalox |
| 3 | `gptt_ntr1..2` | NTR graphite (Tc 2450 K) |
| 4 | `gptt_metox`, `gptt_liquid4`, `gptt_hydrolox3..4` | Staged combustion (ORSC/FRSC), Pc ≤ 270 |
| 4 | electric-motor node (e.g. `gptt_power` / NF electric nodes) | Electric pump-fed |
| 5 | `gptt_liquidVac4`, `gptt_hydroloxVac2` | C-C extensions, ε ≤ 300, pintle deep throttle |
| 5 | `gptt_ntr3..4` | NTR CERMET / carbide (Tc 2800 → 3100 K), LANTR |
| 6 | late propulsion node | Full-flow staged combustion, Pc ≤ 330, η_pump 0.75 |

Two upgrade flavours:

- **Cap upgrades**: new limits for new designs.
- **Retrofit upgrades** (optional, off by default): when a node raises η_c*/η_pump, existing designs can be re-certified in the editor and get a "Mk2" stats bump. That's the "upgrade" fantasy without silently buffing flying vessels.

---

## 7. Designer UI

Three ways in, so nobody has to discover a hidden hotkey. B9 PWings' hover+J as the only entry is its most common complaint.

- PAW button **"Open engine designer"** (editor only).
- **F** while hovering the engine (CRFP-style).
- Toolbar button (ToolbarControl).

The PAW itself shows a compact summary (design name, Isp vac/SL, thrust, mass, TWR) plus the propellant and cycle choosers as stock `UI_ChooseOption`. Players who never open the window can still swap cycles.

### 7.1 Designer window

UGUI, built in code or from a small bundle. Wrapped by ClickThroughBlocker. **One window instance** driven by a singleton editor controller: not per-part `OnGUI`, which B9 PWings does and pays for in performance and static-state bugs. The window contains:

- **Propellant and cycle dropdowns.** Locked entries are greyed out with the node that unlocks them.
- **Sliders for Pc, ε, thrust (vac/SL toggle), gimbal and nozzle length fraction**, using B9 PWings' slider pattern:
  - left-drag snaps to the coarse step, right-drag is a fine vernier,
  - side buttons step coarse (left click) or fine (right click),
  - a **#** toggle turns every slider into a numeric text field.
  - ε is on a log scale (1-2-5 ladder when snapping). Pc snaps to 5 bar coarse / 0.5 bar fine.
  - Bounds come live from the tech caps, as Procedural Parts' `AdjustDimensionBounds()` rewrites stock slider min/max before each rebuild.
- **Size-lock toggles** (which of thrust / Pc / ε / exit diameter is derived).
- **Live readout:** Isp vac/SL, thrust vac/SL, mass, TWR, exit diameter, length, ṁ, EC draw (electric), P_th (NTR), min throttle, ignitions, rated burn time, cost.
- **Isp vs altitude plot** for the launch body (pressure from `FlightGlobals.GetHomeBody()`), with the separation zone shaded.
- **"Why?" box** (B9 PWings' "last edited field" panel). It explains the field just changed in two lines, e.g. "Higher expansion ratio → more vacuum Isp, but the flow separates below 12 km on Earth."
- **Warnings:** "separates below X km", "exceeds tech cap", "expander thrust limit", "zero-margin design".
- **Inherit buttons** (B9 PWings "inherit base/shape"):
  - "Match mount to part above": top diameter from the parent's attach-node size or bounds.
  - "Fit exit inside interstage/fairing below": cap the exit diameter from the child or fairing size.
  - "Copy design from engine…": pick another ProcEngine on the craft.
  - These read attach-node sizes and bounds, so the neighbour doesn't need our module.

### 7.2 In-scene drag handles (Phase 3; CRFP-style, clean-room)

Handles are parented to the generated transforms, so they follow the mesh:

| Handle | Where | Drag changes | Modifiers |
|---|---|---|---|
| Exit rim (radial arrows ×4) | nozzle exit lip | ε at fixed throat | Ctrl: exit diameter at fixed ε (changes thrust) |
| Exit plane (axial arrow) | below exit centre | nozzle length fraction (0.6–0.9) | – |
| Chamber top (axial arrow) | injector dome | chamber length (L*) | – |
| Gimbal ring | throat plane | gimbal range, drawn as a translucent cone | – |
| Thrust ring | around the chamber | thrust, scaling the whole engine | Shift: fine |

Mechanics taken from CRFP:

- **Picking:** handles live on layer 2 ("Ignore Raycast"), so KSP's own part picking ignores them, and our controller raycasts only that layer.
- **Dragging is screen-space:** project the handle's axis to screen and take the dot product with the mouse delta.
- **Display:** handles fade when facing away and scale with engine size.
- **Snapping:** respects the stock VAB angle-snap toggle; Shift = fine.
- **Editor coexistence:**
  - activating switches the editor to Place mode,
  - lock editor input (`EditorLogic.fetch.Lock`) only while the cursor is over our window or a handle,
  - lock `KEYBOARDINPUT` through `InputLockManager` while a text field has focus (B9 PWings doesn't, and typing "3" moves the camera),
  - close on any stock part pick/drop event, on Esc, or on a click on empty space.
- **Undo:** call `EditorLogic.fetch.SetBackup()` on drag or field **release** only. Stock Ctrl+Z then restores designs for free, because the design is stored on the part.
- **Live overlays while dragging:**
  - a ghost outline of the previous shape,
  - the exit disc tinted green/amber/red by sea-level separation margin,
  - the gimbal sweep cone.

### 7.3 Advanced profile editor (Phase 5+, optional; idea from VertexBasedProceduralParts)

VBPP's real novelty is direct per-vertex editing of a cross-section: a 2D window with draggable points, multi-select, group drag, grid snap and in-ring mirror symmetry. For engines, an "Advanced" tab of the designer gives the same feel, but split by what the point controls:

- **Nozzle contour: constrained points.** Show the Rao bell as its Bézier control points: the throat-arc end N with angle θn, the middle control point Q, and the exit E with angle θe. Dragging Q or the angles reshapes the bell. The model then recomputes divergence loss (from θe) and length/mass (from the contour). Expansion ratio and throat stay pinned to the main sliders. This turns "bell length fraction" into something players can sculpt, with immediate Isp/mass feedback, without breaking the physics.
- **Cosmetic profiles: free vertices.** The mount/thrust-frame shroud, chamber jacket and injector dome profiles can be fully free polylines (VBPP-style), since they don't feed the performance model, only mass via area.
- **Editor mechanics to copy:**
  - Ctrl+click toggles a point in the selection; dragging moves the whole selection together.
  - Box-select (VBPP started it and never finished).
  - A snap key for a grid. Use **Alt**, because Shift is already "fine" in §7.2.
  - Mirror-in-profile toggle.
  - Zoom.
  - Numeric fields for the selected point.
- **Profile presets** ("F-1 style short bell", "RL10 long bell", "conical", "Merlin-like shroud") as `PROCENG_PROFILE` ConfigNodes loaded once at game load. Don't do what VBPP does: it reads JSON files from disk in every part's `OnStart`, so ModuleManager can't patch them and a malformed file throws per part.
- **Copy/paste of sub-parts**: "copy nozzle", "copy mount", "copy whole design" through the system clipboard (`GUIUtility.systemCopyBuffer`), so it survives scene changes and works across players. VBPP's copy buffer lives only in a static field for the session.
- **Scarfed nozzle** (VBPP's per-vertex axial offset gives slanted ends): a single "scarf angle" slider for clustered or side-mounted vacuum engines. It's cosmetic plus a small thrust-vector offset.
- **Designer-only "cutaway / hide kit" toggle** (VBPP can hide faces): shows the chamber and nozzle section while editing.

### 7.4 Update pipeline (two speeds)

Learned from all three mods: none throttles properly, and Procedural Parts rebuilds the whole part and its symmetry copies on every slider tick.

1. **Immediately** on a value change (dirty flag, no per-frame reflection polling as CRFP does, and no mesh rebuild per mouse-drag event as VBPP does):
   - recompute stats, which is pure math and microseconds,
   - update the readouts and the engine module.
2. **At most 10 Hz while dragging:** rebuild the render mesh and move the handles.
3. **On release, plus a 0.5 s debounce** (B9 PWings' coroutine, but *without* its extra immediate FAR call):
   - colliders,
   - attach nodes and attached parts (§8.1),
   - drag cube,
   - FAR `GeometryPartModuleRebuildMeshData`,
   - Waterfall rescale,
   - `onEditorShipModified` once (Procedural Parts fires it twice per change),
   - `SetBackup()`.

**Symmetry:** use only the primary part's field callback. Skip it if the value is unchanged, then push the DESIGN to the counterparts by hand. That's Procedural Parts' approach: since KSP 1.6 the symmetry callback fires first with a wrong "old value". Never mirror with negative scale (CRFP), because engines are axisymmetric anyway.

---

## 8. Procedural mesh

### 8.1 Coordinate frame and attach nodes

The placeholder model only holds transforms: `model` root, `gimbal` (pivot at the throat plane), `thrustTransform` (at the exit plane, +Z down the exhaust), and a `pumps` anchor. Attach nodes:

- `top` at the injector/mount top,
- `bottom` at the nozzle exit,
- surface attach on the chamber.

After a resize, move nodes and shift attached parts. This is Procedural Parts' recipe, reimplemented rather than copied (BY-SA):

1. Compute every node position **absolutely** from the design. Never add deltas, which drift over many edits.
2. For each node: set `node.position` and `node.originalPosition`, then fire `GameEvents.onPartNodeMoved` (or `OnPartNodeMoved` via `SendMessage`, as Procedural Parts does).
3. **Stack neighbours:**
   - push children attached to `bottom` by the change in exit-plane position;
   - if the part on `top` is our *parent*, move ourselves instead and shift the root accordingly, divided by the symmetry count.
   - CRFP's alternative also works: temporarily reparent the children under the moving end transform, move it, then restore.
4. **Surface-attached children** (e.g. a camera on the chamber): convert to cylindrical coordinates (angle, height fraction, radius), rescale, and convert back.
5. Then fire `onPartModelChanged` / `onPartColliderChanged` (Procedural Parts sends these, and LMP PartSync hooks can listen) and the debounced steps in §7.4.

**Build in `OnStart`, every scene, synchronously.** Vessel placement on the pad and LMP vessel loads need real geometry and nodes before the first physics frame. Procedural Parts once had to add a temporary box collider in flight for exactly this reason.

**Part icon:** in `GetInfo()` during loading (it runs on the prefab), build a default design's mesh so the parts list and library show a real engine instead of an empty placeholder. That's Procedural Parts' trick for its icons.

**Module order in the PART cfg:** `ModuleProcEngine` *before* `ModuleGimbal` and `ModuleEnginesFX`, so our `OnStart` has moved the gimbal pivot and `thrustTransform` before they cache them. Re-parent `thrustTransform` under the gimbal pivot so thrust follows the gimbal (Procedural Parts' SRB does both).

### 8.2 Bell contour (Rao thrust-optimised parabola approximation)

Standard engineering approximation (Rao 1958; widely published as the "TOP" bell):

1. **Converging section**: arc radius 1.5·Rt from the chamber-to-throat entrance angle (−135° → −90°), joined to the chamber cylinder with a 0.5·Rc fillet and a straight 30–45° cone.
2. **Throat downstream arc**: radius 0.382·Rt from −90° to (θn − 90°).
3. **Bell**: quadratic Bézier from point N (end of throat arc, wall angle θn) to exit E (radius Re = √ε·Rt, wall angle θe), with the middle control point at the intersection of the tangents. Length `Ln = Lf·(√ε − 1)·Rt / tan 15°`, with length fraction Lf = 0.8 by default (0.6–0.9 with an "nozzle length" option).
4. **θn, θe** from a small table vs ε and Lf (Rao's curves digitised: e.g. at Lf = 0.8, θn ≈ 21° at ε 4 → 35° at ε 100; θe ≈ 14° → 7°). Interpolate in log ε.
5. **Early tech**: a 15° cone (θn = θe = 15°) with a matching divergence loss.

Sample about 40 profile points, denser near the throat.

### 8.3 Chamber and injector

- Cylinder radius `Rc = √εc·Rt`, length from `V_c = L*·At` minus the convergent volume.
- Injector dome: a spherical cap. Its height and the manifold torus radius scale with Rc.
- Regenerative section: a coolant manifold torus at `ε_regen` (where the radiative extension starts, if any).

### 8.4 Mesh construction

- **Revolve** (lathe) the profile with 48 segments (24 on low-detail setting). Two shells: outer (hot-metal/cooling-tube material) and inner (dark, soot gradient), with a lip ring at the exit. About 4–8k triangles per engine.
- **UVs**: U around (0–1, with a seam), V along arc length, normalised per section, so tileable "cooling tube" normal maps keep a constant density.
- **Materials**: stock `KSP/Bumped Specular` (no dependency). An optional TexturesUnlimited PBR path when TU is present (it's installed here). Section materials: regen copper/steel, niobium (light grey, glows orange), carbon-carbon (near black, glows dull red).
- **Heat glow**: drive `_EmissiveColor` / `_TemperatureColor` on the nozzle from throttle and heat each frame, through a per-renderer **`MaterialPropertyBlock`**. Never write it to the shared material: Procedural Parts' SRB does, so every SRB glows together. A property block is also simpler than an `FXModuleAnimateThrottle` clip, since a procedural mesh has no clip.
- **Colliders**: 2–4 convex `MeshCollider`s built from a coarse (12-segment) hull, split at the concave points: chamber + convergent, throat → mid-bell, mid-bell → exit. Keep each under 255 triangles (Unity's convex limit), and reuse the `Mesh` objects between rebuilds. Never use the render mesh as a collider; CRFP and B9 PWings both do, and both have click/collision bugs from it.
- **Drag cubes** (non-FAR players):
  - Wait until the editor or vessel is ready, then call `DragCubeSystem.Instance.RenderProceduralDragCube(part)`. It's singular and returns one `DragCube`.
  - Then: `part.DragCubes.ClearCubes()`, add the cube, `ResetCubeWeights()`, `ForceUpdate(true, true, false)`, `SetDragWeights()`.
  - Cache the cube by a hash of the design so 9 identical engines render it once. ROUtils' `DragCubeTool` does exactly this, but it's NC-SA, so reimplement (~40 lines).
  - In the editor, skip it unless FAR is absent; on drag release only (§7).
- **Exact normals**: compute normals and tangents analytically from the 2D profile (Procedural Parts' lathe does this). Make creases (injector face edge, exit lip, regen/radiative joint) with two coincident profile points with different normals. Don't use `RecalculateNormals`. Duplicate the seam vertex for the UV wrap.
- **Ring density** by size: segments chosen so the chord error stays under ~1 cm (min 16, max 64), plus extra rings around the throat where curvature is highest.
- **Cooling-tube UVs**: a whole number of tubes around the circumference, constant along the length (real tubes taper with the bell), and V by arc length. Bake the tiling into the UVs, not a material scale, so one shared material (and TU) works for every engine and identical engines batch.

### 8.5 Kit-bashed turbomachinery and plumbing

Two kinds of pieces:

- **Kit pieces** (small library in `procengines.bundle`, or built from primitives in code for the MVP): turbopump (housing + volute + turbine), gas generator / preburner (small sphere + dome), main valves, gimbal actuators (piston pairs), thrust frame / gimbal block, electric motor + inverter box, helium bottles (pressure-fed), reactor vessel + shield disc + control-drum ring (NTR), heat exchanger.
- **Generated plumbing**: swept tubes (circle swept along a Catmull–Rom spline) from pump outlets to injector or manifold, sized by `ṁ/ρ` (constant flow velocity → `d ∝ √(ṁ/ρ)`). The GG cycle adds a turbine **exhaust duct** that ends in a small nozzle beside the bell (Merlin/F-1 style). That's where the GG Waterfall effect anchors.

Layout rules per cycle (in `PROCENG_CYCLE { KIT { ... } }`):

| Cycle | Kit |
|---|---|
| Pressure-fed | valves + 2 helium bottles, no pumps |
| Electric | 2 pumps + 2 motor cans + inverter box |
| Gas generator | 1 turbopump (single shaft) + GG + exhaust duct |
| Expander | 1–2 small pumps, regen jacket manifolds visible |
| Staged combustion | 1 big turbopump + preburner on top, hot-gas duct into the injector |
| FFSC | 2 turbopumps + 2 preburners, symmetric |
| NTR | reactor cylinder above the throat, shield disc, drum ring, 1 pump |

Pieces scale by `(P_pump)^(1/3)` (pump volume ∝ power) and sit on a ring at `Rc + pump radius`, at angles chosen so pieces don't overlap (simple greedy placement on a circle). They're visual only and don't get colliders (or only a coarse one). Their mass is already in §5.5.

**Template-vertex deformation** (B9 PWings' technique) is a good middle ground for kit pieces: author a pump, duct elbow or thrust frame once in Blender, then stretch it at runtime instead of scaling it uniformly. For example, a turbopump housing gets longer without its flanges getting fatter.

- B9 PWings classifies vertices by position thresholds. That's fragile, so **tag stretch zones in a vertex-colour channel** in Blender instead (R = "moves with length", G = "moves with radius").
- Rewrite UVs in world units after stretching.
- Recalculate normals for non-uniformly scaled zones (B9 PWings doesn't).

**One shared material, per-part data in vertices** (B9 PWings' shader trick): material layer (steel, copper, niobium, C-C, painted) goes in UV2, tint in vertex colour, and per-part heat glow in a `MaterialPropertyBlock`. All engines then share one material and batch.

**Art source.** MVP: pure primitives plus tileable textures. That's fully procedural, needs no asset licence questions, and still looks "rocket engine". Later: author proper kit pieces in Blender under a licence the project controls, or reuse compatible assets (see §2 for licences).

### 8.6 FAR, physics and editor hooks after a rebuild

- FAR: `part.SendMessage("GeometryPartModuleRebuildMeshData")` (the hook Procedural Parts and B9 use) so FAR re-voxelizes.
- `part.UpdateMass()`, `GameEvents.onEditorShipModified.Fire(EditorLogic.fetch.ship)`, `GameEvents.onPartModelChanged` (for KJR, FAR and LMP PartSync method hooks).
- `ModuleSurfaceFX` (ground dust): set its `thrustProviderModuleIndex` and its transform to the exit plane.

---

## 9. Waterfall integration

Waterfall is a soft dependency. Without it, fall back to stock/SmokeScreen effects with stock FX prefabs scaled by exit diameter.

1. The base part ships with an empty `ModuleWaterfallFX` (`moduleID = procEngine`, standard controllers: `throttle`, `atmosphereDepth`, `random`) under `NEEDS[Waterfall]`.
2. `PROCENG_PLUME` table: `(propellant, cycle, altitude role) → template name + reference exit diameter + reference length + offsets`. Altitude role comes from the design's expansion: "lower" if pe ≳ 0.4 atm, else "upper". Examples:
   - Kerolox GG → `waterfall-kerolox-lower-2` (sea level) / `waterfall-kerolox-upper-*`, plus `waterfall-kerolox-exhaust-1` on the GG exhaust duct,
   - Methalox FFSC → `waterfall-methalox-lower-raptor-1` / `-upper-raptor-1`,
   - Hydrolox SC → `waterfall-hydrolox-rs25-1`, expander → `waterfall-hydrolox-upper-1`,
   - NTR → `waterfall-ntr-lh2-1`, Monoprop → `waterfall-hydrazine-monopropellant-upper-1`.
3. On design change (editor) and on load (flight), through reflection or a thin adapter against Waterfall's public classes:
   - remove previous ProcEngines effects from the module,
   - for each plume entry, instantiate the template with `scale = De / referenceDiameter` and `position = exit plane`, then let the module re-initialize its effects.

   Waterfall's own B9PartSwitch integration (template swapping per subtype) and ROEngines' rescaling show this is supported. Confirm the exact runtime API (template lookup, `AddEffect`/`RemoveEffect` or reload) in the Phase 0 spike.
4. Calibrate `referenceDiameter` once per template by viewing it in the Waterfall editor on a 1 m test nozzle. Stored in data, so it doesn't need code changes.
5. Plume brightness and length can also scale gently with Pc and expansion. Waterfall's atmosphere controller already handles plume expansion with altitude.

---

## 10. Design library, saving, sharing, multiplayer

### 10.1 Two-step plan: subassemblies first, custom tab later

**MVP: stock Subassemblies.** "Save to library" writes a one-part `ShipConstruct` (the base part with its `ModuleProcEngine` DESIGN/STATS) as a **subassembly** `.craft` into `saves/<save>/Subassemblies/ProcEngine - <name>.craft`. KSP renders the thumbnail and lists it in the VAB's stock Subassemblies category, and drag-to-spawn just works.

Pros: zero custom spawning code, robust, and LMP's craft library already understands `CraftType.Subassembly`, so designs can be shared through LMP's existing craft sharing UI.
Cons: mixed in with the player's other subassemblies, and one folder per save.

**Phase 5: dedicated "Engine Designs" tab.**

- Add a custom category through `PartCategorizer` (a custom filter with an icon) that shows the base part(s) only.
- Next to it, a ProcEngines library panel with design cards: thumbnail, name, Isp, thrust, mass, tech status.
- Clicking a card calls `EditorLogic.fetch.SpawnPart(basePartInfo)` and immediately applies the design to the new part on the cursor (hook `onEditorPartEvent(PartCreated)` for that one part). That gives part-like behaviour without an `AvailablePart` per design.
- Thumbnails: render the generated mesh with an offscreen camera to a 256² PNG in `PluginData/Thumbnails/<designId>.png`.

**Why not one `AvailablePart` per design.** You *can* clone `AvailablePart` objects at runtime and register them in `PartLoader`. But .craft files and ProtoVessels would then reference part names that only exist on the creator's machine: LMP vessel sync breaks, other players get "missing part", and save files depend on the library. Every vessel must only ever reference `procEngine-liquid` / `procEngine-nuclear`.

### 10.2 Design file format and sharing

```
PROC_ENGINE_DESIGN
{
  id = 6f1c2a...      name = Kestrel-2 Vac     author = Felipe     created = 2026-10-17
  modelVersion = 3    schemaVersion = 1
  DESIGN { ... same block as in the part module ... }
  NOTES  { text = Upper stage for the Kestrel launcher }
}
```

- **Global library**: `GameData/ProcEngines/PluginData/Designs/*.cfg`, shared across saves. A per-save "favourites" list.
- **Share codes**: "Copy design code" puts `PE1:` + base64(deflate(ConfigNode text)) on the clipboard, and "Paste design code" imports it. Discord-friendly.
- **Validation on import**: unknown propellant/cycle → reject with a message. Over tech caps → import as "locked".

### 10.3 LunaMultiplayer

- **Vessel sync**: the design lives inside the part's MODULE node (§3.2), so LMP's ProtoVessel sync carries it. Other clients rebuild mesh, plume and stats from it without needing the library. This is the key requirement, and the subassembly/single-base-part design satisfies it.
- **Mod control**: only two part names to whitelist. All clients must run the same ProcEngines.dll (LMP mod control can enforce the DLL). Version mismatch → stored STATS still guarantee identical physics, and only cosmetics could differ.
- **PartSync XML** (`LunaMultiplayer/PartSync/ProcEngines/ModuleProcEngine.xml`): sync `ignitionsLeft` and `burnTimeUsed` (they change in flight) and the `OnDesignApplied` method. Follow the shape of the existing `ModuleB9PartSwitch.xml` and the `PartSyncSchema.xsd` in LMP.
- **Library sharing between players** (Phase 5+): either LMP craft sharing of the subassembly (free), or a "Send design to players" button over LMP's ModApi messaging (the same mechanism KerbalismLunaSync v0.2 already uses for health sync).
- **Shared tech**: LMP shares the R&D state, so caps are the same for everybody in co-op.

---

## 11. Reliability (Kerbalism-friendly)

Today the pack has **no engine reliability** (KerbalismEngineFailures and TestFlight are both absent). Proposal: ProcEngines computes reliability-relevant numbers from the design and enforces only the light, fun parts itself. Anything heavier is left to an adapter.

**Computed per design**

- **Ignitions**: by cycle and ignition type. Pyrotechnic/TEA-TEB GG: 1–3. Spark-ignited expander: 10+. Pressure-fed/hypergolic-style: 30+. NTR: 10–20. Tech can add more.
- **Rated burn time**: cycle base (GG 300 s, SC 400 s, expander 1500 s, NTR 3600 s) × tech factor × (thrust-density factor: higher Pc shortens it early on).
- **Ignition reliability** and **burn reliability** (per rated burn): from cycle complexity, Pc relative to the tech cap ("pushing it" costs reliability), and side-load events (sea-level ignition of a separated nozzle).
- **Design maturity (optional, TestFlight-like)**: every design ID accumulates flight seconds and ignitions in a scenario module (per save, synced in LMP via the scenario system). Reliability rises from "new" to "mature" over e.g. 5 flights. Copying a design keeps its ID, and editing parameters forks a new ID.

**Enforcement, MVP**

- Ignition limit: `ModuleProcEngine` counts activations and blocks ignition at 0, like EngineIgnitor.
- Burning past rated time: a growing chance per second of a "shutdown + thrust cap" failure, repairable by an Engineer on EVA (Kerbalism-style), or not repairable for NTR.
- Everything can be toggled off in difficulty settings.

**Adapters (later)**

- If `KerbalismEngineFailures` is installed (Kerbalism repo, public domain): put its `EngineFailures` module on the base part under `NEEDS[KerbalismEngineFailures]` and **set `rated_ignitions` and `rated_operation_duration` at runtime** from the design, then disable our own ignition logic. Those fields are confirmed in the Kerbalism source by the prior-art survey; −2 means "auto from propellant family", which we override. Installing it in ksp_mp would also give every other engine in the pack failures, so that's a group decision.
- If TestFlight is installed: provide `TESTFLIGHT` config for the base part, with the reliability curve driven at runtime through TestFlight's API (`TestFlightInterface`).
- **Kerbalism Planner**: electric pump engines put `ElectricCharge` in PROPELLANT. Check that the planner counts it, and if not, add a Kerbalism `Process`/consumer hint.
- **Kerbalism radiation**: NTR `Emitter` as in §5.4.
- Kerbalism's background simulation doesn't run engines, so nothing extra is needed. KerbalismLunaSync is unaffected.

---

## 12. Balance against the rest of the pack

- Calibration target = real engines (§5.5), because SMURFF already pulls stock-derived engines (CryoEngines, Bluedog, Tantares, NFLV) to about real TWR. Validate by comparing a few designs against the SMURFF'd stats of Bluedog's H-1/F-1/J-2/RL10 analogues in the pack (read from `ModuleManager.ConfigCache`) and keep within ±10 %.
- `Balance.cfg` exposes `twrMultiplier`, `ispMultiplier`, `costMultiplier`, `techCapMultiplier`, so the group can tune without code.
- Procedural engines shouldn't simply dominate. They pay for flexibility with: tech caps a tier behind the best fixed parts, a certification fee in career, and "new design" reliability.

---

## 13. Risks and open questions

| Risk | Mitigation |
|---|---|
| `ModuleEnginesFX.Load()` at runtime might not fully reinitialize (fuel flow, propellant gauges, effects) | Phase 0 spike. RealFuels does this successfully, so read its `ModuleEngineConfigs` for the exact call sequence. |
| Waterfall runtime template API (the survey confirms `AddEffect`/`CopyEffect`/`RemoveEffect`/`SetControllerValue` exist) | Spike. Fall back to declaring several effects in the base part config and enabling/scaling one through Waterfall's controllers. |
| Attach-node moves upsetting KJR / FAR / LMP | Copy the order of operations Procedural Parts uses, and test with the 2-client LMP rig (`ksp_test_A` / `ksp_test_B`). |
| Mesh rebuild cost in big craft | Cache meshes by design hash and share `Mesh` instances between identical engines (clusters of 9–33 engines). |
| PARTUPGRADE display without real upgrades | Spike. Fallback: our own "ProcEngines" R&D info panel. |
| Bottom attach node at the nozzle exit moves when ε / length / gimbal change (Procedural Parts has a still-open bug for an SRB attached through its nozzle) | Set node positions absolutely from the design (never incremental deltas), and test "engine attached by its nozzle to a decoupler below" explicitly in the spike. |
| Adaptive mesh/collider loops freezing on part duplicate (Procedural Parts #375) | Fixed or hard-capped iteration counts everywhere in Geometry. |
| Zero/degenerate dimensions summoning the kraken (B9 PWings #14) | Hard minimums on every geometric output (throat radius, wall thickness, lengths), checked in the model layer. |
| Mixture-ratio compromise (stock LF/Ox 0.9:1.1) | Documented. Optional MR slider later. |
| Electric pumps useless with stock battery density | Realistic. Balance knob `ecPerKJ` in Balance.cfg. |
| Players without Waterfall | Stock FX fallback (scaled `fx_exhaustFlame_*`). |

---

## 14. Phased plan (rough effort; one developer working with Claude, evenings/weekends)

| Phase | Scope | Effort |
|---|---|---|
| **0. Spike** | Base part with a placeholder model. Runtime `ModuleEnginesFX.Load` with synthesized nodes, checked by KER, MechJeb and the stock ΔV app. Move attach nodes. FAR re-voxelize. Waterfall add/scale a template at runtime. Two-client LMP test that the MODULE node round-trips. PARTUPGRADE display test. Drag-handle prototype (one radial handle on the exit rim, §7.2). **FAR whole-craft smoke test**: attach the generated engine to a winged test craft and confirm the *other* parts' lift and drag in the FAR flight data are unchanged (VBPP open issue #10: one generated part zeroes the whole craft's aero). Engine attached by its nozzle to a decoupler below. | 3–5 days |
| **1. Model library** | Port ferram4/ProcEngines' NozzleCalculator + Rao tables (MIT). Offline NASA CEA script → `Propellants.cfg`. Nozzle maths, separation, cycles (pressure-fed, GG, staged combustion), mass and cost model. Unit tests calibrated against ~10 real engines (assert ±10 % mass, ±3 % Isp). Pure C#, no KSP needed to run. | 4–6 days |
| **2. MVP in game** | `ModuleProcEngine` with DESIGN/STATS persistence. Designer window (dropdowns, sliders, readouts, Isp plot). Procedural bell + chamber mesh (revolve) + simple primitive pump/GG blocks. Colliders, drag cubes, mass/cost modifiers. Tech caps from `Tech.cfg` (GPTT + stock fallback). **Save as subassembly** library. Kerolox / methalox / hydrolox. One Waterfall template per propellant, scaled. | 1.5–2.5 weeks |
| **3. Visual pass** | In-scene drag handles (§7.2). Kit-bash layout rules for every cycle (template-vertex deformation for authored pieces), swept plumbing, GG exhaust duct + exhaust plume, materials (regen / niobium / C-C), heat glow, TU path, LOD. Full Waterfall template table with calibrated reference sizes. | 1–2 weeks |
| **4. More cycles + NTR** | Expander (+ bleed), FFSC, electric pump (EC draw), monoprop. NTR with Tc tiers, reactor mass, spool-up, SystemHeat decay heat, Kerbalism radiation Emitter. | 1–1.5 weeks |
| **5. Library tab + sharing** | "Engine Designs" category, design cards with rendered thumbnails, spawn-on-click, global library, share codes, LMP PartSync XML, optional ModApi "send design". | 1 week |
| **5b. Advanced profile editor** (optional) | §7.3: Bézier nozzle-contour editing with live loss and mass feedback, free-vertex cosmetic profiles, `PROCENG_PROFILE` presets, clipboard copy of sub-parts, scarf angle, cutaway toggle. | 4–6 days |
| **5c. Components + clusters** (optional) | §16.3: component slots with per-component presets and compatibility checks, kit-piece placement handles, in-part engine clusters (1–9 engines, one module). | 1–1.5 weeks |
| **6. Reliability + career** | Ignitions, rated burn time, failures, design maturity scenario, certification fee, KerbalismEngineFailures/TestFlight adapters, difficulty toggles. | 1 week |
| **7. Polish** | Balance pass against SMURFF'd pack engines, localisation, docs, CKAN/SpaceDock release. | 3–5 days |

**MVP = Phases 0–2**: about 3–4 weeks of part-time work. That gets a playable "design a kerolox/methalox/hydrolox GG or staged-combustion engine, see its curve, save it, and fly it in multiplayer" loop. Phases 3–4 make it look and feel like the full vision. 5–6 are quality-of-life and career depth.

**Development setup**: reuse the KerbalismLunaSync toolchain (`dotnet build` against `ksp_mp\KSP_x64_Data\Managed`). Develop in a small test instance (the `ksp_test_A/B` copies, without Mirage/Parallax) to avoid the ~9-minute ModuleManager patch. Only the base parts need an MM pass, once per install. Iterate on the DLL, the data configs and the mesh code without touching ksp_mp until a release build.

---

## 15. Lessons from Procedural Parts, CRFP, B9 Procedural Wings and VBPP

Code studies with file:line citations: [research/study-procedural-parts.md](research/study-procedural-parts.md), [research/study-crfp.md](research/study-crfp.md), [research/study-b9-pwings.md](research/study-b9-pwings.md), [research/study-vbpp.md](research/study-vbpp.md). Sources read: Procedural Parts at HEAD 9380d94 (configs identical to the installed 2.8.0) plus v2.0.6 for the old TECHLIMIT code; CRFP v0.7.0.5 (linuxgurugamer/CarnationREDFlexibleParts); B9 PWings fork v0.46.1 (B9-Procedural-Wings/B9-PWings-Modified, HEAD de9108a). What each mod is:

- **Procedural Parts** (installed): parametric surface-of-revolution shapes (cylinder, cone, Bezier cone, pill, polygon) edited with stock PAW sliders. Its procedural SRB swaps and scales *pre-made* bell models.
- **CRFP** (not installed): a "SimplePlanes fuselage". Each part is a solid lofted between two rounded rectangles, with 8 corner radii, twist, tilt and offsets. It's edited with **3D drag handles in the scene** and a UGUI panel, and has a "Creator" that builds a panel from 3–4 picked points with vertex/edge/surface snapping. It's handle-based rather than true per-vertex editing. No mainstream KSP mod does free vertex editing.
- **VertexBasedProceduralParts (VBPP)** (KerbalHub, GitHub only; in ksp_mirage, not ksp_mp). The installed DLL is v0.3.7 (2026-03-16), but GitHub's source is the older v0.2.0, so the study decompiled the installed DLL. The part is two 24-vertex rings (bottom/top) joined by straight sides. You edit **individual vertices**: X/Y in an 800×800 2D window with multi-select, group drag and grid snap, plus a per-vertex Z offset so ends can be slanted. "Cap" collapses a ring to a point, duplicate vertices make sharp corners, and any side face can be hidden. JSON cross-section presets. No in-scene handles, no smoothing, no end caps.
- **B9 Procedural Wings** (installed): **vertex-based**. The .mu ships template meshes (surface, section, 4 edge styles), and the plugin moves their vertices to the requested span/chord/thickness. Edited in an IMGUI window opened with J over the wing.

### 15.1 What we adopt (and where it landed in this doc)

| From | Mechanic | Where |
|---|---|---|
| Procedural Parts | Compute mass/cost/stats in the editor, save them, and return the saved values in flight | §3.2 (STATS) |
| Procedural Parts | Rebuild synchronously in `OnStart` in every scene; default mesh for the part icon via `GetInfo()` | §8.1 |
| Procedural Parts | Lathe builder: analytic normals, duplicate seam vertex, chord-error ring count, extra throat rings, collider split at concave points into ≤255-tri convex pieces | §8.4 |
| Procedural Parts | Attach-node recipe (`originalPosition`, `OnPartNodeMoved`, push stack neighbours, cylindrical coordinates for surface children); module order before `ModuleGimbal`; `thrustTransform` under the gimbal | §8.1 |
| Procedural Parts | Old TECHLIMIT semantics (union of researched nodes, 0 = unlimited, sandbox unlimited, retry if R&D isn't ready) instead of PARTUPGRADE | §6 |
| Procedural Parts | Symmetry: primary callback only, push to counterparts by hand | §7.3 |
| Procedural Parts | Drag-cube call sequence (`RenderProceduralDragCube` + `ClearCubes`/`ResetCubeWeights`/`ForceUpdate`/`SetDragWeights`), cached by design hash | §8.4 |
| Procedural Parts | Whole-number circumferential tiling for cooling-tube textures | §8.4 |
| CRFP | Screen-space drag handles on layer 2 with our own raycast; fading and size scaling; stock snap toggle; Place mode; input lock only over UI; close on part events | §7.2 |
| CRFP | `SetBackup()` on release → stock undo works for designs | §7.2 |
| CRFP | Spawn-and-apply for library entries (`SpawnPart` + one-shot part-event capture) — validates our §10.1 Phase 5 plan; off-screen camera thumbnails; Ctrl+C/V-style copy → our `PE1:` codes, "apply hovered design" hotkey | §10 |
| CRFP | UI assembly separate from the KSP assembly (delegates injected), so the window can be developed and tested without KSP | §3 (layering) |
| B9 PWings | Coarse/fine paged sliders with a # numeric toggle | §7.1 |
| B9 PWings | "Last edited field" explanation box | §7.1 |
| B9 PWings | Inherit-from-neighbour buttons (match mount above / fit exit below / copy design) | §7.1 |
| B9 PWings | Two-speed updates: immediate stats, 0.5 s debounced FAR/drag/ship-modified | §7.4 |
| VBPP | 2D point editor (multi-select, group drag, grid snap, mirror) → Advanced nozzle-contour and cosmetic-profile editor | §7.3 |
| VBPP | Cross-section presets → `PROCENG_PROFILE` presets (as ConfigNodes, not JSON) | §7.3 |
| VBPP | Per-vertex axial offset (slanted ends) → scarfed-nozzle angle; face hiding → designer cutaway | §7.3 |
| B9 PWings | Template-vertex deformation for kit pieces; material layer in UV2 + tint in vertex colour, one shared material | §8.5 |
| B9 PWings + Procedural Parts | Design state lives in the part module, and each LMP client rebuilds the shape on load. Neither needs special multiplayer code: Procedural Parts already has LMP PartSync XMLs installed, and B9 PWings doesn't even need one. | §10.3 (confirms the approach) |

### 15.2 What we avoid

- **Per-frame reflection or field polling** to detect changes (CRFP, B9 PWings). Use dirty flags.
- **Unthrottled full rebuilds on every slider tick**, symmetry copies included, and firing `onEditorShipModified` twice (Procedural Parts).
- **The render mesh as collider** (CRFP, B9 PWings), and pieces with no collider at all (B9 PWings edges can't be clicked).
- **Per-part `OnGUI`, static UI state, or a hidden hotkey as the only entry** (B9 PWings).
- **Writing heat glow to the shared material** (Procedural Parts SRB: every SRB glows at once).
- **Negative-scale mirroring** (CRFP); **background threads touching KSP objects** (CRFP rewrites `TechRequired` from a thread); **reflection into private stock UI** like `CostWidget`.
- **Unversioned save fields** (CRFP, B9 PWings).
- **Leaking meshes and colliders** on rebuild (Procedural Parts Bezier/hollow shapes), and **uncapped adaptive loops** (Procedural Parts #375 freeze on duplicate).
- **Degenerate dimensions** (B9 PWings "inherit shape" zeroes tip thickness → kraken).
- **Attach-node-size events without checking which part owns the node** (Procedural Parts, probably the cause of its open #340).
- **Texture references by loose file name** (CRFP loses custom textures on other machines), or quietly borrowing another mod's textures (VBPP's texture config reuses Procedural Parts' files). Everything we reference ships in our own bundle.
- **Building the first mesh a frame late** (VBPP). The placeholder's collider is live meanwhile, and on reload the collider reverts. Commenters on VBPP issue #6 traced its ConformalDecals bug to this. Build synchronously in `OnStart` (§8.1), and after each rebuild notify surface-attached ConformalDecals children so decals re-project (ConformalDecals is installed in ksp_mp).
- **Symmetry by copying vertex lists without mirroring them** (VBPP, likely open issue #9), and face/vertex indices that can go to −1.
- **Culture-sensitive float serialisation** (VBPP; see §3.2).
- **Hiding the editor behind one obscure PAW toggle** (VBPP issue #13, "can't find the editor"). That's why §7 has three entry points.

### 15.3 Licences: reimplement, don't copy

| Mod | Licence | What it means for MIT ProcEngines |
|---|---|---|
| Procedural Parts | CC BY-SA 3.0 | Read for behaviour and API call sequences, then reimplement. Pasted code would make those files BY-SA. Credit it in the README. |
| ROUtils (`DragCubeTool`) | CC BY-NC-SA 4.0 | Reimplement (≈40 lines). |
| CRFP | CC BY-SA 4.0 (some textures BY-NC-SA) | Ideas only; no code, bundles or shape tables. |
| VBPP | MIT, but the copyright line was never filled in ("[year] [fullname]") | Reuse allowed with attribution to KerbalHub/KH759. Nothing worth copying, since reimplementing is simpler. |
| B9 PWings fork | Repo LICENSE says **MIT**; CKAN lists **CC-BY-NC-SA-4.0** | Patterns are safe. Ask the maintainer (linuxgurugamer) before copying any code verbatim. Never copy the .mu/textures (mixed provenance from bac9). |

---

## 16. Building an engine from separate parts + Weldatron?

The question: could players build an engine from separate parts (nozzle part, pump part, mount part, …) placed in the VAB, then fuse them into one part with **Weldatron** (Phantomical/Weldatron, MIT, last commit cb6ee2b 2026-07-28)? Answer: **no as the core mechanic, but three of its ideas are worth taking.**

### 16.1 How Weldatron works (from its source and DESIGN.md)

1. You pick up a sub-assembly and drop it on a "Weld drop zone" button (a clone of the stock `SubassemblyDropZone`) in its own "Welded Parts" parts category, then give it a name.
2. It writes a real `PART:NEEDS[Weldatron&<every source mod>]` config to `GameData/zzzWeldedParts/Parts/weld-<name>-<hash>.cfg`. The config holds the source part names and their poses, the merged mass/cost/resources/attach nodes, and the baked drag cubes.
3. It makes the part available in the same session: registers a `UrlConfig`, calls `PartLoader.ParsePart`, patches `loadedParts`/`APFinderByName`/`APFinderByIcon`, renders a palette icon with `CraftThumbnail.TakePartSnapshot`, and bakes drag cubes with `DragCubeSystem.SetupDragCubeCoroutine`.
4. At every game load, `ModuleWeldedPart` rebuilds the geometry by cloning each source **prefab's** `model` subtree (`ModuleWeldedPart.cs:120`). No meshes are copied to disk.
5. Modules are only carried over through per-module "welder" adapters. Unknown modules are dropped. There's an `EnginesWelderBase` that merges identical engines into one `ModuleEngines` with thrust × N and every source's thrust transforms (engine clusters).

### 16.2 Why welding separate component parts doesn't fit ProcEngines

| Problem | Detail |
|---|---|
| **It creates PART configs**, exactly what §0 rules out | Each weld adds a `.cfg` under GameData. ModuleManager hashes GameData configs, so the **next game start should do a full re-patch (~9 min in ksp_mp)**, for every player who receives the file. |
| **Multiplayer** | Every LMP client needs the identical weld `.cfg` and all its source mods before a vessel using it can load. Otherwise the vessel has a missing part. A design stored inside one shared base part (§3.2) has no such problem. |
| **Procedural meshes don't survive welding** | Weldatron clones the source *prefab's* model, not the live part. Our engine's mesh is generated at runtime from `DESIGN`, so a weld would capture the default placeholder, and `ModuleProcEngine` would be dropped unless we wrote a Weldatron adapter. |
| **The physics is coupled** | A nozzle's performance depends on chamber pressure and throat size; a pump has to supply ṁ·Δp for that chamber; the mount carries that thrust. Separate parts would still need a "combine and validate" step, i.e. the designer, plus a way to reject mismatched pieces. |
| **Separate parts before welding are fragile** | A 4–6-part engine wobbles on joints (KJR), multiplies part count, and complicates symmetry, staging and FAR voxels. |

### 16.3 What we take instead

1. **Component slots inside the single part: the "LEGO feel" without parts.** The design becomes a set of components, each with its own preset library and drop-down:

   ```
   DESIGN
   {
     CHAMBER  { preset = ...  pc = ...  lStar = ... }            // + injector type
     NOZZLE   { preset = RL10-long-bell  eps = ...  lengthFrac = ...  extension = CC }
     TURBO    { preset = SingleShaftGG  count = 1  layoutAngle = 90 }   // per cycle
     MOUNT    { preset = ConeFrame  topDiameter = 2.5  gimbal = 5 }
     EXTRAS   { heatShield = false  nozzleCover = false  roll = VerniersX2 }
   }
   ```

   - Each component can be saved and shared on its own ("my favourite vacuum nozzle"), alongside whole designs (§10). This extends the §7.3 sub-part copy/paste.
   - The model checks compatibility live: pump power vs need, mount rating vs thrust, nozzle throat vs chamber. Incompatible combinations show red with the reason, rather than being forbidden silently.
   - Kit pieces (pumps, GG, ducts) get **placement handles** around the chamber (angle and height, §7.2). That gives the free-placement fun of separate parts. Placement is cosmetic and stored in `TURBO { layoutAngle … }`.

2. **Engine clusters inside one part** (Weldatron's engine-merge idea, done procedurally). A `CLUSTER { count = 1|2|3|4|5|7|9  pattern = ring|ring+centre  spacing = … }` block:
   - the mesh is built once and drawn N times, with a generated thrust plate,
   - one `ModuleEnginesFX` with `maxThrust × N` and N `thrustTransform`s with the same name (stock splits thrust across them),
   - `ModuleGimbal` drives all N gimbal transforms.
   - A 9-engine first stage becomes one part instead of 10–20, a big physics, part-count and LMP-sync win.
   - Trade-off: engine-out becomes "lose 1/N thrust" handled by our reliability logic (§11), not a separate part failing.

3. **UI and technical patterns from Weldatron** (MIT, so even code may be reused with attribution, unlike §15's BY-SA mods):
   - **Drop zone cloned from stock `SubassemblyDropZone`** for the library tab. Drop an engine on "Save to Engine Library" to store its design, with styling identical to stock.
   - **`CraftThumbnail.TakePartSnapshot(…)`** for palette-style library icons (shader-swapped, bounds-fitted, transparent), instead of our own off-screen camera.
   - **A `PartCategorizer` filter category with a live filter delegate** for the "Engine Designs" tab (§10.1 Phase 5).
   - **Delete flow** that refuses deletion while the item is on the current craft and lists saved vessels using it. The same applies to library designs referenced by subassemblies.
   - **Attach-node ids must be a single token with no `_`**: `ParsePart` splits on `_`. This matters if we ever generate node ids, for example per cluster engine.

### 16.4 Compatibility with Weldatron itself (low priority)

If someone in the group installs Weldatron and welds a stack containing a ProcEngine, the weld would lose the engine (wrong model and dropped module). Options, in order of effort:

1. Do nothing, and document "don't weld ProcEngines".
2. Ship a `WELDATRON_MODULE` adapter that refuses or warns on welding a ProcEngine.
3. A real adapter that carries `ModuleProcEngine` + `DESIGN` into the weld with a transform offset, so the engine regenerates inside the welded part.

The real adapter is only worth it if Weldatron becomes part of the pack. Even then, every weld still costs a ModuleManager re-patch and needs distributing to all players.
