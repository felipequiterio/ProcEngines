# ProcEngines Phase 0 spike: results

Date: 2026-10-17. KSP 1.12.5 (build 03190), test copies `ksp_test_A` (stock + Kerbalism + LMP + FAR 0.16.1.2 + Waterfall 0.11.0 + KER 1.1.9.5 + MechJeb2 + MechJebForAll) and `ksp_test_B` (stock + Kerbalism + LMP + Waterfall, **no FAR**), local LMPServer on port 5525 (career universe). `ksp_mp` and `ksp_mirage` were not modified: mods were copied *from* `ksp_mp` into the test copies.

**Verdict: all 7 checks pass.** Every risky integration works in KSP 1.12.5 with the approach in DESIGN.md. 42 graded assertions, 0 failures in the final runs. Several details of DESIGN.md need correcting (see the end).

| # | Check | Result |
|---|---|---|
| 1 | Revolved bell + chamber mesh, analytic normals, 3 convex colliders, sync build in `OnStart`, editor throttling | **PASS** |
| 2 | Runtime `ModuleEnginesFX` rewrite (`Load(ConfigNode)` and direct fields), checked by the stock ΔV app, KER and MechJeb | **PASS** |
| 3 | Absolute attach nodes, push attached parts, engine attached by its nozzle to a decoupler | **PASS** |
| 4 | FAR re-voxelize + whole-craft smoke test (VBPP #10); non-FAR drag cube | **PASS** |
| 5 | Waterfall template added and rescaled to the exit diameter at runtime | **PASS** |
| 6 | DESIGN/STATS persistence with invariant culture; two-client LMP round trip | **PASS** |
| 7 | Display-only PARTUPGRADE in the R&D screen | **PASS** |

## How it was tested

The code is in [spike/](spike/) (throwaway quality):

- `ProcEngineSpike.csproj`: SDK-style, net472, references `KSP_x64_Data\Managed` of `ksp_test_A`. `dotnet build -c Release` → `spike/bin/ProcEngineSpike.dll`. FAR, Waterfall, KER, MechJeb and LMP are reached by reflection only.
- `src/SpikeModel.cs`: a crude ideal-rocket model (γ = 1.2, c* = 1750 m/s), just enough to produce thrust/Isp/atmosphereCurve/geometry. Not the Phase 1 model.
- `src/NozzleMesh.cs`: profile (mount, injector, chamber, 45° cone, 1.5 Rt / 0.382 Rt throat arcs, Rao Bézier bell, exit lip, inner wall, throat plug), lathe with analytic normals and creases, convex frustum colliders.
- `src/ModuleProcEngineSpike.cs`: the PartModule. Sliders `thrustVac`, `pc`, `eps` and a toggle "Apply engine via Load(ConfigNode) / direct fields".
- `GameData/ProcEngineSpike/Parts/procEngineSpike.cfg`: the part (`SMURFFExclude = true`; `ModuleProcEngineSpike` listed before `ModuleEnginesFX` and `ModuleGimbal`), plus the PARTUPGRADE. `Patches/Waterfall.cfg` adds an empty `ModuleWaterfallFX` under `NEEDS[Waterfall]`.
- `src/SpikeDriver.cs`: a **self-driving test harness**. If `GameData/ProcEngineSpike/PluginData/autotest.txt` exists, it creates a save, writes .craft files from prefab node positions, loads them in the VAB, simulates slider drags, launches, connects to LMP, takes screenshots, writes `results_<script>.txt` and quits the game. Scripts: `local`, `rnd`, `lmpA`, `lmpB`. Run with `spike/run/run.sh <A|B> <script>`.

Evidence (final runs): [spike/run/evidence/](spike/run/evidence/): `A_results_local.txt`, `B_results_local.txt`, `B_results_rnd.txt`, `A_results_lmpA.txt`, `B_results_lmpB.txt`, the KSP.logs of the LMP runs, the server-side vessel file, and 17 screenshots (`shots/A_*.png`, `shots/B_*.png`).

---

## 1. Mesh, colliders, sync build, throttling: PASS

- Built synchronously in `OnStart` in every scene: **1.5–3.0 ms** per part (3.1k verts / 5.5k tris at the 200 kN default; 6–7k verts / 13k tris at 1000 kN with ε 45).
- Segment count from the exit circumference (16–64), seam vertex duplicated.
- Analytic normals agree with `RecalculateNormals` to a median of 0.06–0.13°. The max of 3.6–7° is at the coarse arc/Bézier samples, where `RecalculateNormals` is the approximate one. Creases are coincident points with different normals.
- 3 convex `MeshCollider`s (chamber + cone, throat → mid-bell, mid-bell → exit) of 48 triangles each, all separate from the render mesh. A ray up the axis hits `peCollider2` exactly at the exit plane (0.0000 m), in the editor and in flight.
- `ModuleEnginesFX.thrustTransforms[0]` is our `thrustTransform`, under `peGimbal`, exactly at the exit plane, forward pointing down. `ModuleGimbal.gimbalTransforms` = `peGimbal`. Module order before the engine and gimbal works as designed.
- Throttling: 40 slider changes in 0.26 s gave **40 engine applies, 4 render-mesh rebuilds (≤ 10 Hz) and 1 debounced slow rebuild** (colliders, nodes, drag cube or FAR message, `onEditorShipModified`, `SetBackup`).
- The parts-list and R&D icons show the generated default engine (mesh built in the prefab's `OnLoad` during part compilation). See `shots/B_rnd_*.png`.

```
PASS  [1.editorDefault] mesh+colliders built synchronously; raycast hit peCollider2 at 0.0000 m from exit plane
INFO  [1.editorDefault] render meshes=2 verts=3108 tris=5508 OnStart build=1.65ms; analytic vs RecalculateNormals deviation median=0.13deg max=7.03deg
INFO  [1.editorDefault] engine thrustTransforms=1 first=thrustTransform parent=peGimbal worldPos-exit=(0.0000,0.0000,0.0000) fwd·down=1.000; gimbalTransforms=1 (peGimbal)
PASS  [1.throttle] 40 changes over 0.26s -> engine applies=40, render rebuilds=4 (<=10 Hz), slow rebuilds=1 (debounced)
PASS  [1.flight] mesh+colliders built synchronously; raycast hit peCollider2 at 0.0000 m from exit plane
```

## 2. Runtime engine rewrite, KER / MechJeb / stock ΔV: PASS

Both approaches work, in the editor and in flight. `maxFuelFlow`, `minFuelFlow` (and `throttleMin`) were always set explicitly afterwards. All three readouts agree with the model to the displayed precision:

| Path | Engine fields | Stock ΔV app | KER (editor) | MechJeb |
|---|---|---|---|---|
| `Load(ConfigNode)`, 1000 kN | 1000.00 kN, Isp 321.84 / 256.07 s | 1000.00 kN, 321.84 / 256.07 s, 2575 m/s | 1000.00 kN, 321.84 s, 2575 m/s | 1000.00 kN, 321.84 s, 2575 m/s; atmo 256.07 s |
| direct fields, 650 kN | 650.00 kN, 305.43 / 258.66 s | 650.00 kN, 305.43 / 258.66 s, 3039 m/s | 650.00 kN, 305.43 s, 3039 m/s | 650.00 kN, 305.43 s, 3039 m/s; atmo 258.66 s |
| flight (stored STATS), 200 kN | 200.00 kN, 315.12 / 256.65 s | 200.00 kN, 315.12 / 256.65 s | not tested in flight | 200.00 kN, 315.12 s |

```
PASS  [2.editorLoad.fields] mode=Load maxThrust=1000.00 (exp 1000.00) minThrust=400.00 Isp0=321.84 (exp 321.84) Isp1=256.07 (exp 256.07) maxFuelFlow=0.31684 minFuelFlow=0.12674 throttleMin=0.400 props=LiquidFuel:0.9+Oxidizer:1.1
PASS  [2.editorLoad.KER] stages: #0 thrust=1000.00 isp=321.84 dV=2575.00 (atm=0)
PASS  [2.editorDirect.MechJeb] vac: stg0 T=650.00 Isp=305.43 dV=3038.8; atmo: T=550.47 Isp=258.66
PASS  [2.flight.stockDV] stage 0: thrustVac=200.00 ispVac=315.12 ispASL=256.65 dVvac=3356.8 TWRvac=6.75
```

Notes:

- KER and MechJeb refresh from our single `onEditorShipModified` per slow rebuild. MechJeb keeps showing its previous result until its re-simulation lands (about 0.5 s). The first harness run read that stale value, which was a harness bug, not a mod bug.
- MechJeb's editor simulation only starts once `MechJebModuleStageStats.EditorBody` is set. Its own VAB window does that. The harness sets it by hand.
- KER in flight needs a KER part or the flight engineer enabled, so only the editor was checked.
- `ModuleEngines.OnLoad` parses `maxThrust`/`minThrust` with culture-sensitive `float.Parse`. Our ConfigNode is written with invariant culture. The process culture was en-US here.
- `PartModule.Load(node)` resets the module's private `persistentId` to the node's value (0 if absent). The spike passes `PersistentId` through. On these parts it was 0 anyway.

## 3. Attach nodes and attached parts: PASS

The part origin is at the mount top, so the **top node never moves**: `top = (0,0,0)` and `bottom = (0, y_exit, 0)`, both set absolutely, together with `originalPosition`. Tested both hierarchy directions with "engine attached by its nozzle to a decoupler below":

- **Root above** (probe → tank → engine → decoupler → tank): ε 30 → 120 moves the bottom node from −2.008 to −3.653 m, and the decoupler plus tank below are pushed by −1.6456 m.
- **Root below** (tank → decoupler → engine → tank → probe; the engine's *parent* is on its bottom node): the engine moves itself and its subtree up by +1.6456 m.

Gaps stayed at 0.00000 m, and after 6 more big design toggles back to the first design, drift was 0.000000 m (no accumulation).

```
PASS  [3.rootAbove] nozzle-decoupler gap before=0.00000 after eps30->120=0.00000 (bottom node moved -2.008->-3.653; decoupler pushed -1.6456 m); top gap=0.00000/0.00000; after 6 toggles back to eps30: gap=0.00000 drift=0.000000 m
PASS  [3.rootBelow] nozzle-decoupler gap before=0.00000 after eps30->120=0.00000 (bottom node moved -2.008->-3.653; engine moved itself 1.6456 m); top gap=0.00000/0.00000; after 6 toggles back to eps30: gap=0.00000 drift=0.000000 m
```

Craft save/load round-trips the node position exactly (`bottomNode.y=-2.3252`). Not tested: surface-attached children on the chamber, and symmetry counterparts.

## 4. FAR and drag cubes: PASS

**Whole-craft smoke test (VBPP #10).** The same winged test craft (probe, FL-T400, two wing connectors) was built twice, once with the stock LV-T30 and once with the generated engine. Forces came from `FARAPI.CalculateVesselAeroForces` at 150 m/s, 2000 m, 5° AoA, after `VesselVoxelizationCompletedAndValid`. The aero is essentially identical, so the generated part does not zero or corrupt the craft:

```
INFO  [4.far.ref]   reference craft with liquidEngine: pitch5: L=10.88 D=2.69 kN; yaw5: L=1.34 D=1.91 kN
INFO  [4.far.spike] spike craft (De=0.68): pitch5: L=10.59 D=2.69 kN; yaw5: L=1.09 D=1.90 kN
PASS  [4.far.wholeCraft] generated engine vs stock liquidEngine on the same winged craft at 150 m/s, 5deg: pitch L 10.64/10.95 D 2.69/2.69; yaw L 1.09/1.34 kN
```

**`GeometryPartModuleRebuildMeshData` is required and sufficient.** The control experiment ran in flight with the craft frozen:

1. Change the mesh *without* the message: voxel-derived forces are bit-identical (stale).
2. Send the message: FAR logs `Updating vessel voxel for SpikeWing` and the forces change.
3. Revert the design, with the message: the forces return exactly.

```
PASS  [4.far.rebuild] pitch5 D/L, yaw5 L (kN): base 2.687/10.645, 1.086 | mesh changed, no message 2.687/17.464, 1.086 (stale=True) | after message 2.239/18.472, 2.137 (changed=True) | reverted 2.687/15.354, 1.084 (back=True)
```

Two observations:

- **Pitch-plane lift** (wings) moved without any geometry message (10.6 → 17.5 kN) and did not come back on revert. That is FAR's wing model, not voxelization, so it was logged but not graded. It looks like state in the wing model (stall/interaction), driven by the craft having been frozen or disturbed. Phase 2 should repeat the whole-craft comparison on a real flying craft, with the engine designed in the editor.
- **Never change geometry in flight.** The first run grew the bell in flight without freezing the craft: the new colliders intersected the pad and threw the craft into the air (KSP logged altitude/speed records). DESIGN.md already applies only stored STATS in flight. Keep it that way, and if a flight-time shape change ever appears (deploying nozzle extension), it needs its own handling.

**Non-FAR (ksp_test_B):** `DragCubeSystem.Instance.RenderProceduralDragCube(part)` + `ClearCubes / Add / ResetCubeWeights / ForceUpdate(true,true,false) / SetDragWeights` works in the editor and in flight, synchronously:

```
PASS  [4.dragcube.editor] cubes=1 De 0.68->3.63 m; area before=[0.460,0.460,0.363,0.047,0.459,0.459] after=[12.284,12.284,10.309,0.563,12.284,12.284]
PASS  [4.dragcube.flight] in-flight re-render: YN area 0.047->0.563 (De 3.63); AreaDrag=3.2932
```

Test setup note: FAR 0.16.1.2 also needs `000_KSPBurst` (Unity.Mathematics/Burst) and `999_Scale_Redist.dll`. Without them it fails to load silently, apart from a KSPCF warning.

## 5. Waterfall runtime template: PASS

API that works with Waterfall 0.11.0: once the module has started (its protected `started` flag, flight only), call `ModuleWaterfallFX.OnLoad(node)` with a node holding one `TEMPLATE { templateName, overrideParentTransform = thrustTransform, position, rotation, scale }`. `OnLoad` replaces the TEMPLATE nodes in the module config and re-runs `Initialize()`, which cleans up the old effects and builds the new ones. Controllers stay those from the part config. Re-initialization takes 1.6–1.8 ms (10–12 ms the first time in a scene). Calling it again on a redesign replaces the effects.

```
INFO  [5.waterfall] template=waterfall-kerolox-lower-1 scale=0.683 effects=11 reinit=1.6ms
INFO  [5.waterfall] template=waterfall-kerolox-lower-1 scale=3.626 effects=11 reinit=1.6ms
PASS  [5.waterfall] plume renderer bounds (13.2465,1.7214,10.8746) -> (71.8635,38.4579,45.5903) (width ratio 5.43) for De 0.68 -> 3.63 (ratio 5.31); finalThrust=551.9 kN
```

See `shots/*_plume_small.png` / `*_plume_big.png`.

- The bounds ratio is a world-space AABB of a moving craft, so it is noisy (2.1–6.5 across runs). The applied scale (`De / referenceDiameter`) is the reliable evidence. Calibrating `referenceDiameter` per template is still to do (§9.4).
- Waterfall does nothing outside flight (`Start` and `LateUpdate` return unless `LoadedSceneIsFlight`), so there is no plume preview in the VAB.
- `WaterfallEffectTemplate.Load` defaults `position` to (1,1,1), so always write `position` explicitly.

## 6. Persistence and LunaMultiplayer: PASS

**Local.** `OnSave` writes `schemaVersion`, `modelVersion`, `DESIGN{}` and `STATS{ … atmosphereCurve{} }` with `ToString("R", InvariantCulture)`. Saving and re-parsing with the thread culture set to **de-DE** gives no comma decimals and a bit-exact round trip. Craft save → load restores the design, engine thrust and node position exactly.

```
PASS  [6.culture] saved under de-DE: no comma decimals=True, bit-exact round trip=True
PASS  [6.craft] craft save/load: design F=777.7kN Pc=123.4bar eps=45.6 (exp F=777.7 Pc=123.4 eps=45.6) maxThrust=777.7 bottomNode.y=-2.3252 (exp -2.3252) savedModelVersion=1
```

**Two clients, LMP 0.29.2, career server.**

1. TesterA (ksp_test_A) joined, built mk1pod + engine (777.7 kN, Pc 123.4, ε 45.6) in the VAB and launched.
2. TesterB (ksp_test_B, no FAR) joined afterwards.
3. B's ProtoVessel of A's vessel carried an identical `DESIGN`/`STATS`, and the server's vessel file contains the DESIGN block (`evidence/lmp_server_vessel_SpikeLMP.txt`).
4. B switched to the vessel (spectating TesterA, `shots/B_lmpB_flight.png`). Its loaded engine is identical to A's: thrust, Isp curve, fuel flow, exit diameter, node position, bell vertex count, mesh bounds and part mass.

No PartSync XML or LMP code was needed for this.

```
PASS  [6.lmp.proto] B's ProtoVessel MODULE DESIGN/STATS identical to A's: True
INFO  [6.lmpB] design=F=777.7kN Pc=123.4bar eps=45.6 maxThrust=777.7 ispVac=328.022675 isp1=263.210663 maxFuelFlow=0.241761729 De=1.389564 bottomNode=-2.32522464 bellVerts=4480 bellBounds=(1.3884,1.7672,1.3890) mass=0.9569739
PASS  [6.lmp.loaded] B's loaded engine matches A's exactly: True
```

- B logged `[KSPCF:ModuleIndexingMismatch] Part "procEngineSpike" configuration has changed`, because A has FAR (which adds modules) and B doesn't. KSPCF resynchronised the modules by name and our module loaded fine. In the real group everyone runs the same pack.
- The LMP harness reads the local test server's password from each client's own `LunaMultiplayer/Data/settings.xml`. The server has "Master server registration" on (pre-existing setting).

## 7. Display-only PARTUPGRADE: PASS

```
PARTUPGRADE { name = procEngineSpike-StagedCombustion  partIcon = procEngineSpike  techRequired = basicRocketry  entryCost = 0  cost = 0 ... }
```

No part module references the upgrade.

- In a new career game (Normal preset, `PartUpgradesInCareer = True`), the R&D "Basic Rocketry" panel lists it next to the stock parts, with the generated engine as its icon.
- Before research it shows no price. After research it shows **"owned" with no purchase step**, while the stock parts show their entry costs before research.
- `PartUpgradeManager.Handler.IsUnlocked(name)` is true right after `ResearchTech()`.
- No clutter: one icon per upgrade. See `shots/B_rnd_locked.png` and `shots/B_rnd_researched.png`.

```
INFO  [7.handler] upgrade registered=True forTech(basicRocketry)=procEngineSpike-StagedCombustion unlocked=False availableToUnlock=False cost=0 allowed=True PartUpgradesInCareer=True
INFO  [7.researched] R&D part list items=4: UPGRADE procEngineSpike-StagedCombustion, liquidEngine2.v2, solidBooster.v2, fuelTankSmallFlat
PASS  [7] after research: unlocked=True (no purchase needed=True) enabled=True availableToUnlock=False cost=0
```

Not tested: the Hard preset (`bypassEntryPurchaseAfterResearch = false`) and `PartUpgradesInCareer = false`. Check those before relying on it. Sandbox has no R&D screen, so it doesn't matter there.

---

## Changes needed in DESIGN.md

1. **§3.1**
   - Both paths are proven. Prefer `engine.Load(node)`: it rebuilds propellants, `consumedResources`, `thrustTransforms` and `throttleMin`.
   - The direct path must also set `throttleMin = minFuelFlow / maxFuelFlow`, which only `OnLoad` computes, and call `SetupPropellant()`.
   - With `Load`, pass `persistentId` through: `PartModule.Load` resets it.
   - Write every value in the node with invariant culture, because `ModuleEngines.OnLoad` uses plain `float.Parse`.
2. **§3.1 / §7.4.** One `onEditorShipModified` per debounced rebuild is enough for the stock ΔV app, KER and MechJeb. MechJeb lags about 0.5 s behind (its own re-simulation).
3. **§8.1, frame.** Put the part origin at the top node. The top node then never moves, and "if the part on top is our parent" can't happen. The case that remains, and was verified, is the parent on the **bottom** node: move ourselves (with the subtree) by −Δ.
4. **§8.1, events.** KSP 1.12.5 `GameEvents` has no `onPartNodeMoved`, `onPartModelChanged` or `onPartColliderChanged`. These are Unity `SendMessage` names (`part.SendMessage("OnPartNodeMoved")`, etc.) as Procedural Parts uses them. Reword "fire `GameEvents.onPartNodeMoved`" and §8.6 accordingly.
5. **§8.1, gimbal.** The gimbal pivot transform must be oriented like a stock gimbal transform, with +Z down the exhaust, because `ModuleGimbal` actuates about the pivot's local X and Y. Counter-rotate the bell mesh child (−90° about X). `thrustTransform` sits under the pivot, at local `(0,0,L)`.
6. **§8.1 / §8.4, placeholder model and meshes.**
   - The base part can use any stock model as a placeholder. Strip its children in the prefab's `OnLoad` (scene LOADING), create our transforms, and build the default design there. That gives correct parts-list and R&D icons and prefab drag cubes with no `GetInfo()` trick. `GetInfo()` is still useful to push default stats into the prefab engine.
   - Parts cloned with `Instantiate` (prefab → part, and editor copies) share the source's `Mesh` objects. Keep a **non-serialized** "owns meshes" flag and allocate new meshes when it's false.
7. **§8.4 drag cubes.** The call sequence works synchronously in both the editor and flight. The waiting in §8.4 is only needed in flight until the vessel is unpacked.
8. **§8.6 / §13 FAR.**
   - Confirmed: without `GeometryPartModuleRebuildMeshData` FAR keeps stale voxels. With it, FAR re-voxelizes the craft.
   - Add a Phase 2 test comparing wing lift near a generated engine against a similar stock engine on a real flight, because FAR's wing-model lift varied independently of geometry here.
   - Add the dependency note: FAR needs KSPBurst + Scale_Redist (present in ksp_mp).
9. **§3.2 / §8.** Make "no geometry change in flight" explicit. Growing colliders in flight launched the test craft off the pad.
10. **§9 Waterfall.**
    - Resolve the open API question: `ModuleWaterfallFX.OnLoad(node with TEMPLATE)` after the module has started, in flight only. Always write `position`.
    - No editor preview, because Waterfall is flight-only.
    - Expect about 1.6 ms per re-init (about 11 ms the first time in a scene).
11. **§10.3 LMP.**
    - Confirmed with two clients: ProtoVessel sync carries DESIGN/STATS, and other clients rebuild an identical engine with no PartSync XML.
    - The PartSync XML is only needed later for in-flight fields (`ignitionsLeft`, `burnTimeUsed`).
    - Mixed mod sets (FAR on one client only) trigger KSPCF module resync, which is harmless.
12. **§6 PARTUPGRADE.**
    - Keep the display-only upgrades: with `entryCost = 0` they are auto-owned on research, need no purchase, and show the base part's generated icon.
    - Re-check on the Hard preset before release.
13. **§14 Phase 0.** The drag-handle prototype (§7.2) listed in the Phase 0 row was **not** part of this spike's 7 checks and is still open. Also untested: surface-attached children on the chamber, symmetry counterparts, KER in flight, and Kerbalism-specific interactions beyond loading.

## Test-copy changes (all documented in `spike/run/TEST_COPY_CHANGES.txt`)

- `ksp_test_A/GameData`: added `000_KSPBurst`, `999_Scale_Redist.dll`, `FerramAerospaceResearch`, `ModularFlightIntegrator`, `Waterfall`, `KerbalEngineer`, `MechJeb2`, `MechJebForAll` (copied from ksp_mp) and `ProcEngineSpike`.
- `ksp_test_B/GameData`: added `Waterfall` and `ProcEngineSpike`.
- Saves were created: `pespike` in both copies, `pespike_career` in ksp_test_B. The harness trigger file was removed, so both copies start normally.
- `settings.cfg` (FULLSCREEN) is restored.
- LMPServer `Universe` was restored from `_backup_2026-10-17_procengines`. The post-test universe is kept in `_universe_after_procengines_test_2026-10-17`.
- `spike/run/cleanup.sh` removes all of the above from the test copies. The extra mods change the KerbalismLunaSync test rig, so run it before the next Kerbalism/LMP test, or keep FAR etc. if the spike will be re-run.
