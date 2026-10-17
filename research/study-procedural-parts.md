# Study: Procedural Parts (KSP-RO) for ProcEngines

Source: github.com/KSP-RO/ProceduralParts at HEAD 9380d94 (2026-03). Its configs match the installed 2.8.0 exactly; only the DLL differs. Read-only clone: `scratchpad/src_pp`. Paths below are relative to the repo root. Helper code lives in **ROUtils** (`scratchpad/src_routils`, also installed in `GameData/ROUtils`). The old TECHLIMIT code was read from tag v2.0.6.

## 1. Architecture and mesh building

- **`ProceduralPart`** (`Source/ProceduralPart.cs:12`) is the hub. It implements `IPartCostModifier` and `IPartMassModifier`, owns the shape modules, and finds the `sides`, `ends` and `colliderHolder` transforms in a stock `.mu` (`:269-311`). Calling `MeshFilter.mesh` gives each part its own copy of the mesh. It also grabs the matching meshes on `partInfo.iconPrefab`.
- **Shapes** are sibling PartModules: `ProceduralAbstractShape` (`Source/ProceduralAbstractShape.cs:20`). Only one is enabled at a time (`ProceduralPart.cs:418-436`). Each shape has `techRequired` and `techObsolete` (`ProceduralAbstractShape.cs:26-27`).
  - Surface-of-revolution (SoR) shapes: `ProceduralAbstractSoRShape`, with Cylinder, Cone and Pill on top of it.
  - Shapes that build their own mesh: BezierCone, Polygon, and the Hollow* shapes.
- **SoR pipeline** (`Source/ProceduralAbstractSoRShape.cs:95-281`):
  - Input is a list of `ProfilePoint` (diameter, y, v, 2D normal, inRender, inCollider). Each point can carry its own ring for the render mesh and a separate, coarser ring for the collider (`:29-64`).
  - `SubdivHorizontal` inserts extra rings when the diameter changes by more than 5 % between points. Normals between them are slerped (`:288-331`).
  - Ring resolution: `CirclePoints.ForDiameter` picks the vertex count so the chord error stays ≤ 1 cm, with at least 12 vertices. The tables are cached in a static list (`:15-16, 356-384`).
  - Rings are written as 4 quadrants with odd and even rows offset by half a step. A duplicate seam vertex sits at u+1 (`:572-611`). `WriteTriangles` can stitch two rings with different vertex counts (`:683-745`).
  - Normals and tangents are analytic: the 3D normal comes from the 2D profile normal (`:583-584`).
  - End caps are a separate planar-UV disc mesh (`:208-218, 495-529`).
  - The collider is one convex `MeshCollider`, and its `Mesh` object is reused (`:267-276`, `ProceduralPart.cs:333-348`). It logs a warning above 255 triangles (`:272`).
- **BezierCone**:
  - Adaptive sampling: it inserts points where the curve deviates most from the chord (`Source/ProceduralShapeBezierCone.cs:793-909`).
  - V is arc length normalised to 0–1 (`:678-697`).
  - Concave points split the collider into several convex `MeshCollider`s (`:732-790, 962-1007`).
  - It uses `RecalculateNormals/Tangents`, with a TODO noting this is wrong (`:1082, 1180-1186`).
- **When it rebuilds:**
  - On `OnStart` in every scene, synchronously (`ProceduralPart.cs:128-129`).
  - In `GetInfo` during LOADING, where it writes the icon meshes and rescales the icon (`:84-108`, `ProceduralAbstractShape.cs:409-423`).
  - On every `onFieldChanged`.
  - There is **no throttling**. Dragging a slider rebuilds the mesh and colliders synchronously for the part and each symmetry counterpart. It also sends the FAR message and fires `onEditorShipModified`, once from the Volume setter (`ProceduralAbstractShape.cs:72-91`) and again from TankContentSwitcher (`Source/TankContentSwitcher.cs:257-258`).

## 2. PAW / UI and tech limits

- **Controls:** only stock controls.
  - `UI_FloatEdit` with `incrementSlide = 0.001`, `sigFigs = 5`, `useSI` (`Source/ProceduralShapeCylinder.cs:11-17`). `incrementLarge` and `incrementSmall` come from config fields such as `diameterLargeStep` (`ProceduralPart.cs:44-47`, applied in `Cylinder.cs:36-51`).
  - `UI_ChooseOption`, with options filled at runtime (`ProceduralPart.cs:143-158`, `BezierCone.cs:168-172`).
  - `UI_Toggle` and `UI_FloatRange`.
  - Custom pieces: one IMGUI `GUILayout.Window` for the TU texture picker, without ClickThroughBlocker (`Source/TUTexturePickerGUI.cs:100-111`). `KSPFieldTool.SetField` calls the PAW item's private `SetFieldValue` through reflection (`Source/ProceduralTools/KSPFieldTool.cs`).
- **Ranges:** `AdjustDimensionBounds()` rewrites `UI_FloatEdit.minValue/maxValue` live from `PPart.diameterMin/Max`, `lengthMin/Max` and `volumeMin/Max`. Volume is combined with the other dimension (`Cylinder.cs:75-98`). It runs before every rebuild.
- **Field changes:** `OnShapeDimensionChanged` (`ProceduralAbstractShape.cs:181-204`) works like this:
  1. Return early if the value didn't change.
  2. AdjustDimensionBounds, then UpdateShape, then UpdateInterops (FAR, TestFlight), then TranslateAttachmentsAndNodes.
  3. Repeat step 2 by hand for each counterpart in `part.symmetryCounterparts`.

  The comment at `:174-180` explains why: in KSP ≥1.6 the symmetry callback fires before the primary one and passes the wrong `obj`. Shape switching has the same workaround (`ProceduralPart.cs:156-158, 206-217`). ProceduralSRB, by contrast, hooks both `onFieldChanged` and `onSymmetryFieldChanged` (`Source/ProceduralSRB.cs:67-77`).
- **Tech limits today:** the stock **PARTUPGRADE** system. The changelog line "Replace custom TECHLIMITS with stock PARTUPGRADE" is at `GameData/ProceduralParts/ChangeLog.txt:61`.
  - `PARTUPGRADE` definitions are in `Parts/TechTree/Upgrades.cfg`. Each module carries `UPGRADES { UPGRADE { name__ = ProceduralPartsSRB3m  lengthMax = 3 ... } }`, which overwrites KSPFields (`Parts/Tanks/4SRB.cfg:105-151`).
  - **Sandbox:** PP silently forces `PartUpgradesInSandbox = true` in the save's difficulty settings (`ProceduralPart.cs:60-62`).
  - **Career:** the changelog says players may need to buy the upgrades again.
- **Old TECHLIMIT (v2.0.6):**
  - `TECHLIMIT { name = <techId> diameterMin/Max lengthMin/Max volumeMin/Max allowCurveTweaking }` nodes are parsed only at LOADING and copied from the prefab (`v2.0.6 Source/ProceduralPart.cs:392-405`).
  - In career or science mode, every node whose tech is `Available` is merged as a union: min of the mins, max of the maxes (`TechLimit.cs:51-60`).
  - `Validate()` maps 0 or Infinity to unlimited defaults (`:35-49`).
  - Sandbox skips limits entirely (`ProceduralPart.cs:419-437`).
  - If `ResearchAndDevelopment.Instance` is null, it sets `needsTechInit` and retries from `Update()` (`:54-57, 422-426`).
  - Results are pushed into each shape's `UpdateTechConstraints()`.
  - Tank types filter on `techRequired`, and treat anything outside career or science as unlocked (`TankContentSwitcher.cs:168-171`).

## 3. Attach nodes and attached parts

- `MoveNode` moves `nodeTransform` if one exists, otherwise sets `position`. It always sets `originalPosition` and sends `OnPartNodeMoved` (`ProceduralAbstractShape.cs:524-544`). Initial placement uses absolute ±length/2 (`:496-522`).
- **Length change** (`:210-239`):
  - Stack nodes are translated by ±Δ/2.
  - Attached parts are pushed by `TranslatePart`. If the target is our parent, we move ourselves and push the eldest root, scaled by 1/(symmetry count + 1) (`:241-256`).
  - Surface children: their position is converted to cylindrical (u, y, r) coordinates, y is scaled by the ratio, and they are moved back.
- **Diameter change** (`:258-287`): our own `srfAttachNode` is moved, and we translate ourselves if we're surface-attached. Children's r is scaled the same way.
- **Shape switch:** coordinates are normalised and un-normalised between the two shapes, then `FixStackAttachments(true)` realigns any stack peer that is more than 1 cm off (`ProceduralPart.cs:230-260, 456-477`). The same thing runs on `onVariantApplied`.
- **Events:**
  - `OnPartModelChanged` and `OnPartColliderChanged` via `part.SendEvent` (`ProceduralAbstractShape.cs:372-390`).
  - `OnPartVolumeChanged`, `OnPartAttachNodeSizeChanged` and `OnChangeTextureScale` (`:342-368`).
  - FAR `GeometryPartModuleRebuildMeshData` (`:396-405`).
  - Node `size` and breaking force/torque are derived from the end diameter (`SoRShape.cs:333-348`).
- **Drag cubes:** `OnPartColliderChanged` calls `ROUtils.DragCubeTool.UpdateDragCubes(part, ShapeKey)`. In the editor this only happens when `updateDragCubesInEditor` is set, which is on with FAR (`ProceduralPart.cs:592-602`, `4SRB.cfg:103`). DragCubeTool (`src_routils/Source/ROUtils/ProceduralTools/DragCubeTool.cs`):
  - defers the work to `FixedUpdate` until `FlightGlobals.ready`, or in the editor until the part is attached to the root (`:83-105`),
  - caches cubes by `ShapeKey` (`:315-358`),
  - uses stock `DragCubeSystem.Instance.RenderProceduralDragCube(p)` (singular, it returns a `DragCube`), then `ClearCubes / Add / ResetCubeWeights / ForceUpdate(true,true,false) / SetDragWeights` (`:391-408`),
  - fixes up the PAW aero display (`:443-451`).
- **KSPCommunityFixes:** `KSPCommunityFixes.cfg` turns on `ModUpgradePipeline`, and `ModuleManager/KSPCF.cfg` turns on `PersistentIConfigNode`. The second is needed so `[Persistent] FloatCurve` inside `SRBBellConfig` loads.
- **LMP:** the installed `LunaMultiplayer/PartSync/LunaCompat/ProceduralParts/*.xml` syncs the shape fields plus `OnPartColliderChanged` and `OnChangeTextureScale`.

## 4. Persistence and loading

- **What is saved:** only the KSPFields marked `isPersistant`:
  - shapeName, texture fields, density, `moduleCost`, `moduleMass` (`ProceduralPart.cs:50,356-364,405,507,615`),
  - the shape dimensions,
  - SRB: `selectedBellName`, `bellScale`, `thrust`, `burnTimeME`, `thrustDeflection`, `attachedEndSize`, `heatProduction`,
  - TCS: `tankVolume`, `mass`, `tankType`.
- **Static config subnodes** (`SRB_BELL`, `TANK_TYPE_OPTION`) are parsed only at LOADING and copied from the prefab in `OnAwake` (`ProceduralSRB.cs:30-45`, `TankContentSwitcher.cs:210-217`). Craft files don't carry them.
- **Flight vs editor:** the mesh is rebuilt from the saved fields in `OnStart` in both scenes. UI hooks and bounds are editor-only. Cost and mass are computed in the editor, saved, and the saved value is returned in flight (`ProceduralPart.cs:541-589, 620-637`).
- v2.0.6 added a temporary `BoxCollider` in flight `OnLoad` so vessel placement worked before `OnStart` (`v2.0.6 ProceduralPart.cs:68-75`). That's a hint that placement needs geometry early.
- **Legacy and upgrades:**
  - Old craft without `forceLegacyTextures` are forced to legacy textures (`ProceduralPart.cs:76-82`).
  - A SaveUpgradePipeline `UpgradeScript` converts old Bezier presets, including KCT ship nodes (`Source/UpgradePipeline.cs`).
  - Texture paths `StretchyTanks/` → `ProceduralParts/` (`Source/TextureSet.cs:139-154`).
  - `OnSave` forces `isEnabled=True` (`ProceduralSRB.cs:47`).
  - `GetInfo` re-enables the module (`ProceduralPart.cs:100-105`).

## 5. ProceduralSRB (closest analogue)

- **Bells:** `SRB_BELL` → `SRBBellConfig : IConfigNode` (`ProceduralSRB.cs:158-191`). Each bell has a model transform (from extra MODEL nodes in the cfg), `atmosphereCurve`, `gimbalRange`, `bellChokeDiameter` and `chokeEndRatio`. Unused bell models are deactivated, and their colliders are destroyed in the editor (`:307-333`).
- **Applying a bell** (`InitModulesFromBell`, `:335-350`):
  - assigns `Engine.atmosphereCurve` and sets `ModuleGimbal.gimbalRange` plus the X/Y ranges,
  - points `gimbalTransformName` at the bell model. This works because ProceduralSRB comes before ModuleGimbal in the cfg, so its `OnStart` runs first.
- **thrustTransform:** in flight it is re-parented under the bell's `srbAttach` so thrust follows the gimbal (`:235-243`; changelog 2.4.1).
- **Thrust and burn time:**
  - `MaxThrust = max(attachedEndSize² · thrust1m, 10)` (`:379`).
  - `fuelRate = thrust/(Isp_vac·g)`, and `maxFuelFlow` is written directly (`:428, 444-450`). They set fields directly rather than calling `Load(ConfigNode)`.
  - Thrust and burn time are linked through propellant mass (`:544-562`).
  - `heatProduction = heatPerThrust·√thrust/(1+mass)` (`:437`).
  - The thrust limiter is forced to 100 % and hidden, including from a 0.1 s coroutine, because "Uncertain why this keeps getting turned on" (`:409, 490-501`).
- **Bell scale:** `bellScale = (chokeEndRatio/bellChokeDiameter)·√(thrust/thrust1m)`, a uniform `localScale` (`:440-449`).
- **Placement:** the bell is placed at −length/2, inset by the sweep of the gimbal and deflection angle. The bottom node goes to `srbAttach`, and attached parts are rotated and shifted (`:249-286`).
- **Heat glow:** sets `_EmissiveColor` on `sharedMaterial` (`:576-583`).

## 6. Textures

- **Legacy STRETCHYTANKTEXTURES** (`STTextures.cfg`, `TextureSet.cs`):
  - `uScale/vScale`, `autoScale(U/V)`, `autoWidthDivide`, `autoHeightSteps`.
  - The shape reports `(π·mean diameter, slant length)` through `OnChangeTextureScale` (`SoRShape.cs:193-199`).
  - `GetScaleUv` rounds the U tile count to an integer so the seam stays clean, and steps V (`TextureSet.cs:159-192`). The result is applied as `material.mainTextureScale` on `_MainTex` and `_BumpMap` (`LegacyTextureHandler.cs:88-98`).
  - Shaders are `KSP/Bumped Specular` or `KSP/Specular`, and `KSP/ScreenSpaceMask` for icons (`TextureSet.cs:194-204`).
- **TU path** (`ProceduralsUnlimited*.cfg`): `KSPTextureSwitch` with `transformName = sides/ends` and `TU/Metallic` with recolour masks. There is no size-based tiling: UVs stretch with the part.

## 7. Mass, cost and performance

- **TankContentSwitcher** sets:
  - `mass = dryDensity·kL + massConstant`, scaled by shape and density, and returns `mass − defaultMass` (`TankContentSwitcher.cs:28, 301-314`),
  - resources = `unitsConst + kL·unitsPerKL + mass·unitsPerT`, rescaled in place and with the PAW slider patched (`:330-359`).
- **Cost:** `ICostMultiplier` chain (`ProceduralPart.cs:550-556`). SRB cost is `thrust·0.5·costMultiplier` (`ProceduralSRB.cs:142`).
- **Garbage:**
  - `UncheckedMesh` allocates new arrays on every rebuild and calls `Mesh.Clear` before reassigning them (`Source/UnityUtils.cs:66-95`).
  - Bezier and Hollow shapes create a new GameObject and a new `Mesh` per collider on every rebuild and never destroy the meshes (`BezierCone.cs:992-1004`, `ProceduralPart.cs:322-328`), so they leak until `UnloadUnusedAssets`.
  - Bezier also calls `LinkedList.ElementAt` inside a loop, which is O(n²) (`:985`).

## 8. Licence

- **ProceduralParts:** `GameData/ProceduralParts/Licence.txt` is the full **CC BY-SA 3.0 Unported** legal code (359 lines). The CKAN metadata says `CC-BY-SA`, and the forum post says "Remains as CC-BY-SA 3.0 Unported". Section 4(b) says adaptations may only be distributed under BY-SA. So copying code, `.mu` files or textures makes those files BY-SA, which conflicts with ProcEngines' MIT plan. BY-SA 3.0 also isn't GPL-compatible.
- **ROUtils** (DragCubeTool) is **CC BY-NC-SA 4.0** (`src_routils/LICENSE.md`), which is stricter still. `UncheckedMesh` is PP's own code (`Source/UnityUtils.cs`), so it is BY-SA.
- **Conclusion:** reimplement the algorithms from this description. Ideas and stock API call sequences aren't copyrightable. Don't paste code. Reference PP in the credits.

## Useful for ProcEngines (ranked)

1. **Compute in the editor, save, return the saved value in flight** for mass and cost (`ProceduralPart.cs:541-637`). This is exactly what §3.2 needs: use `ModifierChangeWhen.FIXED` and keep the values in the module.
2. **Synchronous rebuild in `OnStart` in every scene, from saved data.** The mesh and colliders must exist before the vessel is placed (the v2.0.6 temp collider is the lesson). Also generate a default mesh into `iconPrefab` during LOADING via `GetInfo` and rescale the icon (`ProceduralAbstractShape.cs:409-423`). Our single base part needs this for its VAB icon.
3. **A profile → lathe builder like SoRShape:**
   - per-point 2D normals, giving analytic normals and tangents,
   - a duplicated seam vertex,
   - diameter-adaptive ring count with a minimum of 12,
   - subdivision where the radius changes quickly (the throat),
   - V by arc length,
   - separate, coarser collider rings.

   For creases (the injector face, the bell lip) use two coincident profile points with different normals. Don't use `RecalculateNormals` (Bezier TODO, seam artefacts).
4. **Convex collider splitting at concave profile points** (Bezier `:732-790`). The bell is concave, so split it into chamber/convergent, throat-to-mid-bell and mid-to-exit hulls. Stay under 255 triangles each. Reuse the `Mesh` objects; avoid the leak described in §7.
5. **Node and attachment recipe:**
   - `MoveNode` sets `position` (or `nodeTransform`) and `originalPosition`, then fires `OnPartNodeMoved`.
   - Push stack peers by the delta, moving self and the root if the peer is the parent, scaled by 1/(symmetry count + 1).
   - Move surface children in normalised cylindrical coordinates.
   - Order: bounds, mesh/collider, FAR message, nodes/parts, counterparts.
   - Set node positions absolutely from the design, not incrementally as `HandleLengthChange` does, which drifts.
6. **Symmetry:** hook `onFieldChanged` only, return early if the value didn't change, and update counterparts yourself. Don't trust `onSymmetryFieldChanged`'s `obj`.
7. **Drag cubes:**
   - defer to `FixedUpdate`, gated on `FlightGlobals.ready` or the part being attached to the root in the editor,
   - cache by a geometry key, for example a hash of DESIGN,
   - call stock `RenderProceduralDragCube` and then the ClearCubes / ResetCubeWeights / ForceUpdate / SetDragWeights sequence,
   - skip this in the editor unless FAR is installed.

   Write it yourself, about 40 lines: ROUtils is NC-SA. Note that §8.4's `RenderProceduralDragCubes` should be `RenderProceduralDragCube`.
8. **Engine hookup lessons from the SRB:**
   - put our module before `ModuleGimbal` in the cfg so `gimbalTransformName` is set before gimbal `OnStart`,
   - re-parent `thrustTransform` under the gimbal pivot at the exit plane,
   - set `maxFuelFlow` explicitly after any change (and also `minFuelFlow`, which PP ignores),
   - use an absolute exit-diameter model; PP derives thrust from the attach-end size.
9. **Tech caps:** prefer a custom `PROCENG_TECH` evaluated the TECHLIMIT way:
   - union over Available nodes,
   - `Validate` defaults,
   - unlimited in sandbox,
   - retry when R&D is null.

   Avoid PARTUPGRADE: it forces a player-facing purchase and the `PartUpgradesInSandbox` hack. Shape-style `techRequired`/`techObsolete` maps well to cycle availability.
10. **Prefab-held static data:** copy prefab subnodes in `OnAwake`. Save DESIGN/STATS with explicit `OnSave/OnLoad` rather than depending on KSPCF `PersistentIConfigNode`.
11. **Texture tiling for cooling tubes:** keep the U tube count an integer and constant along the bell (tubes taper), and tile V by arc length. Bake the tiling into the UVs rather than `mainTextureScale`, so one material works for every engine and for TU.
12. **LMP:** PP needs nothing beyond PartSync XMLs (already shipped by LunaCompat). Our design-in-a-MODULE-node approach is consistent with that.

**Avoid (pitfalls seen):**

- No rebuild throttling. We need a 10 Hz cap or rebuild on slider release.
- `AnimateHeat` writes to `sharedMaterial`, so every engine glows together (`ProceduralSRB.cs:581`). Use `MaterialPropertyBlock`.
- `OnPartAttachNodeSizeChanged` doesn't check that `node.owner == part`, so other mods' events can clobber `attachedEndSize`. This is the likely cause of issue #340, "SRB stuck at 10 kN" with small B9 fairing bases (`:117-127`). Filter events by owner.
- Thrust-limiter hack via coroutine.
- The redundant `EngineWrapper` (`ModuleEnginesFX` already inherits from `ModuleEngines`).
- The `GetLength()` type switch (`:587-598`).
- Open bugs:
  - #375: Bezier sampling freezes on duplicate. Cap adaptive loops.
  - #298: wrong concave collider.
  - #352: burn time reset on reload.
  - #363: surface-attached parts slightly angled.
  - "SRB attached through nozzle mispositions on deflection" (ChangeLog:22). That's directly relevant to our `bottom` node at the nozzle exit.
