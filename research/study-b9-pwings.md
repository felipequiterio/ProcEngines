# Study: B9 Procedural Wings (Modified fork) v0.46.1, for ProcEngines

Source: clone of github.com/B9-Procedural-Wings/B9-PWings-Modified (HEAD de9108a) at `scratchpad/src_pwings`. Paths below are relative to `B9_PWings_Fork/` unless stated. Most of the logic is in one 4.6k-line file, `WingProcedural.cs` (WP).

## 1. Mesh approach (template deformation)
- **Templates.** The .mu ships child meshes `surface`, `section`, `edge_trailing_type0..3` and `edge_leading_type0..3` (control surfaces use `frame`, `surface` and `edge_type0..2`). They are found through a hard-coded hierarchy, `part.transform.GetChild(0).GetChild(0).GetChild(0).Find(name)` (WP:2168). The pristine vertices, normals and UVs are copied once into **static** `MeshReference`s shared by all instances (WP:91-114, 2119-2146, 2198-2218).
- **Vertex classification is by position, with normals as a secondary test.** Vertex colours are not used for this. `x < -0.05` means tip, otherwise root; `z < 0` means leading, otherwise trailing (WP:1346-1375, 1420-1449). Thickness scales y by `thickness/0.24`, the template's reference thickness (WP:1321-1322). Edges are classified the same way and scale z by `edgeWidth/0.24`. `nm.x == 0` separates the side faces from the end caps (WP:1538-1558). Control surfaces add a UV-based filter, `uv.y > 0.185` (WP:1684-1704).
- **Edge-shape switch.** All four edge variants live in the model, and `SetActive` shows only the chosen one (WP:1479-1501). All four are still deformed every time, because the bounds of inactive meshes otherwise corrupt craft-size reports (comment at WP:1514; GitHub issue #6).
- **UVs.** These are rewritten in world units, so texel density stays constant as the part stretches. Surfaces use `U = span/4` and `V = 0.5 ± width/8 − offset/4` (WP:1428-1446). The section uses `U = width` (WP:1354-1372) and the edge side faces use `U = length` (WP:1543-1546).
- **Normals and tangents are never recalculated.** The template normals are kept, which is only acceptable because the pieces are near-planar. `RecalculateBounds` is the only recalculation.
- **Colliders.** A convex `MeshCollider` is built on the render mesh of `section` (WP:1382-1391), or of `frame` for control surfaces (WP:1747). The trick of setting `sharedMesh = null` first forces a re-cook. The edges have no collider, which explains issue #38 (edges don't respond to hover or click).
- **Shader `KSP/Specular Layered`** (`shaders/SpecularLayered.shader`, shipped in `pwings_allplatforms.bundle` and loaded in `StaticWingGlobals.cs:59-78`):
  - `_MainTex` holds 4 grayscale diffuse layers in its RGBA channels, and `_Emissive` holds 4 colour masks.
  - Vertex colour RGB is the paint colour (HSB to RGB conversion at WP:4261-4317) and A is the paint opacity.
  - **UV2.x selects the material layer**, computed as `(mat−1)/3`. **UV2.y = 1 means a uniform fill**, used for material 0 (WP:4256-4259).
  - Gloss is derived from the mask, the normal is constant, there is no normal map, and deferred support was added in 0.46.
  - One shared static material is used for all wings (WP:2247-2336).
  - If TexturesUnlimited's `KSPTextureSwitch` is present, the legacy path is skipped (WP:1301-1304, `B9PWingsUnlimited.cfg`).

## 2. Editor UI
- **IMGUI.** One `GUILayout.Window` with a static rect, wrapped by a reflection proxy to ClickThroughBlocker (`ClickThroughBlockerWrapper.cs`, WP:3107-3114). **Every** wing runs `OnGUI`, and only the target instance draws (WP:3087-3131).
- **Opening.**
  - In `OnMouseOver` (it needs the part's collider), pressing **J** sets the static `uiInstanceIDTarget` (WP:3757-3784). This works only if `part.parent != null && isAttached`, so the root part can't be edited.
  - J again, or a left-click outside the window, exits edit mode (WP:3928-3951). Right-click while hovering also exits (WP:3768).
  - The hover-to-open option added in 0.43.0.2 was removed again in 0.43.0.5 (Changelog).
- **Other hotkeys.**
  - Hover, then hold **G**, **T** or **B** and move the mouse to drag-deform length/offset, tip width and root width. Modifiers: **LeftAlt** for the leading edge, **LeftCtrl** for the trailing edge (WP:3786-3900).
  - In-scene handle gizmos come from the asset bundle (`EditorHandle.cs`, WP:4070-4212). Dragging auto-locks an axis, and LeftCtrl/X/Y change the lock (hint text at WP:3160).
  - These hotkeys are undocumented in the UI (issue #18).
- **Sliders** (`UIUtility.cs`):
  - `FieldSlider` (135-247) is an unbounded "paged" slider: the bar covers one `range` window and `delta` is the page index. **Left-click** on a side button jumps to the end of the bar or to the next page. **Right-click** on a side button applies a fine increment. **Left-drag** snaps to the increment, and **right-drag** is a fine vernier around the current value (211).
  - `LimitedSlider` (362) is used for bounded fields and `IntegerSlider` (476) for enums.
  - The **#** button toggles a `GUI.TextField` numeric entry on every slider (WP:3183-3186, UIUtility:232).
  - The slider fill colour is set per group (WP:3133-3139).
- **Tooltips.** There is a fixed "Last: <field>" header plus a hint box with the explanation for the last-edited field: `UpdateTooltipText(fieldID)` (WP:3617-3755), shown at WP:3149-3165. All text is localised.
- **Inheritance.** Buttons appear only when the parent has `WingProcedural` (WP:616-637, 3354-3390).
  - **Base** copies the parent's tip values (width, thickness, edges) onto the child's root, and mirrors the offset (WP:698-713).
  - **Shape** extrapolates the parent's taper and sweep over the child's length (WP:680-696).
  - **Edges** (WP:715-727) and **Colour** (WP:729-754) copy those properties. Control surfaces get "Align with back/fore edges" (WP:756-771).
  - "Save as default" and "Restore default" change the session-static defaults (WP:459-562). Initial defaults come from MM `ProceduralWingDefaults` (`WingDefaults.cs`, `StaticWingGlobals.cs:47-51`).
- **Angle mode.** An optional "define by sweep angles" mode has lock toggles (WP:938-1074, 3203-3208). There is also a "scale edges with thickness" option (WP:3568-3581).
- **PAW.** The stock PAW is not suppressed. All shape `KSPField`s are simply `guiActiveEditor=false`. The PAW shows only mass, lift area, MAC, semispan, sweep and taper (WP:2525-2547), plus the events "Surface Config" and "Next configuration". `displayDirty` refreshes it (WP:3079-3085).
- **Input locking.** `EditorLogic.fetch.Lock(false,false,false,"WingProceduralWindow")` is applied while the cursor is inside the window rect, and unlocked otherwise (WP:3120-3129). There is no keyboard lock for text entry, so the J handler still fires while you type.

## 3. Derived stats
- `CalculateAerodynamicValues` (WP:2566-2873) works from the dimensions (edges added in):
  - It computes semispan, MAC, taper and mid-chord sweep, then area, AR and a sweep-scaled AR factor (2661-2666).
  - Mass is `0.015·S·f(AR)·(1+taper)/2` (2668), and Cl and Cd are computed alongside it.
  - Joint strength is `√(Cl + childrenCl)·150` (2672). `GatherChildrenCl` recurses up the parent chain (2918-2945).
  - Cost is `mass·(1+ARSS/4)·5300` (2683).
  - CoM, CoL and CoP are placed at the equal-area chord (2607-2635, 2685).
- **Stock aero.** `deflectionLiftCoeff = S/3.52`, and mass = `0.1×` that (2707-2721). After a debounce, a procedural drag cube is rendered (2904-2907).
- **FAR integration** is reflection-only. The code sets `b_2`, `b_2_actual`, `MAC`, `MAC_actual`, `MidChordSweep`, `TaperRatio`, and either `ctrlSurfFrac` or `rootMidChordOffsetFromOrig`, then invokes `StartInitialization` immediately (2751-2857). After a 0.5 s **debounce**, `UpdateAeroDelayed` calls `StartInitialization` again, sends `SendMessage("GeometryPartModuleRebuildMeshData")` and fires `onEditorShipModified` (2876-2916). `FARVoxPatch.cfg` forces `GeometryPartModule.forceUseMeshes = True`. Under FAR, mass is left to FAR (`GetModuleMass`, 4587-4590).
- **Fuel.**
  - Volume is `0.7·L·avgWidth·avgThickness` (4371).
  - `FuelConfigs.cfg` `ProceduralWingFuelSetups/FuelSet` entries (STR/LF/LFO/Mono) give units per m³ as `ratio·1000/(resVolume·Σratio)` (`WingTankResource.cs:28-36`).
  - With RF, MFT or Configurable Containers, the code instead sends `OnPartVolumeChanged` with `volName="Tankage"`, the ProceduralParts convention (4496-4507).
  - Fuel volume adds no dry mass (issue #26).

## 4. Persistence and multiplayer
- **Saved fields.** About 40 `isPersistant` floats, plus `isAttached`, `isSetToDefaultValues`, `fuelSelectedTankSetup` and `aeroIsLiftingSurface`. **Derived stats are persisted too** (2525-2547). `mirrorTexturing` is written by hand in `OnSave`/`OnLoad` (897-930).
- **Defaults.** `isSetToDefaultValues` decides between applying defaults and clamping the saved values (3468-3471).
- **No versioning or legacy migration** exists in this fork (grep finds none).
- **Editor load.** `Start()` calls `Setup()` (867-893), and per-frame `Update` polls about 40 "Cached" twin fields to detect changes (3955-4020).
- **Flight load.** `OnStart` starts `SetupReorderedForFlight` (2401-2491). Only one wing per vessel runs it, gated by a static `vesselList`. That wing runs `Setup()` on all wings, waits 2 FixedUpdates, then does all the aero calculations. Shape never changes in flight.
- **Symmetry.**
  - `UpdateCounterparts` copies every field and its cache to the counterparts and calls `RefreshGeometry` (1970-2020).
  - The fuel and lift toggles are propagated separately (4404-4417, 4437-4450).
  - `isMirrored` comes from the Mirror symmetry method and which side of the root part the wing is on (1146-1150). It flips the top/bottom paint (1451) and swaps root and tip on control surfaces (1644-1654).
- **LMP.** The `LunaMultiplayer/PartSync` folder has **no B9 PWings definition** (only `LunaCompat/*` and `Squad`; no file mentions wings). None is needed: the shape is editor-only, it travels in the ProtoPartModule fields, and each client rebuilds the mesh in `OnStart`. This is the same model DESIGN §10.3 assumes.

## 5. Licence
- The repo `LICENSE` is **MIT**, "Copyright (c) 2015" with no holder named. The same file ships in `GameData/` (identical; `diff` is clean).
- The **CKAN registry** for this install (`ksp_mp/CKAN/registry.json`, module `B9-PWings-Fork` 3:0.46.1) says **CC-BY-NC-SA-4.0**.
- The README credits many authors, from DYJ/bac9 through LGG.
- **Implications.** Under MIT, the code patterns, the slider widget and the shader may be reused if the MIT notice and credits are kept. The CKAN tag and the mixed art provenance (bac9's .mu files and textures) make copying the assets unsafe. Re-implement the patterns rather than copying files, and ask LGG to clarify if anything is copied verbatim.

## 6. Pitfalls and bugs
- **Performance.**
  - Every wing has `OnGUI` and an `Update` that polls fields each frame.
  - Each geometry change runs the full deformation, the counterpart `RefreshGeometry`, `GatherChildrenCl` up the parent chain, and an **immediate FAR `StartInitialization` via reflection** (2856). Only the revoxelization is debounced.
  - `SetupMeshFilters` `.Add`s the edge filters on **every** `RefreshGeometry` without clearing the list (2047-2054). The lists grow during every symmetric edit.
- **Bugs.**
  - `InheritShape` sets tip thickness to `Mathf.Min(…, 0)`, which zeroes it (695). That leads to issue #14: zero-dimension wings summon the kraken.
  - `IntegerSlider` increment is `1 / range`, an integer division (UIUtility ~486).
  - Cost subtracts `partInfo.cost` twice (2694, 4579); this is harmless only because the part's cost is 0.
  - `CheckAssemblies` doesn't guard `GetName()` (issue #42).
- **UX.**
  - The window reappears after every scene reload because of static state (issue #32).
  - J doesn't open the window for some users (#20).
  - Ctrl+Z destroys the handles; a reload workaround exists (#11, WP:4073-4078).
  - The edges can't be clicked (#38).
  - Mirrored control surfaces put the CoL off-centre (#29).
  - Shapes reset after FAR is removed (#12).
  - There is an `ArgumentNullException` on database reload (#37).

## Useful for ProcEngines (ranked)
1. **Two-speed update.** Recompute cheap stats immediately, and put the drag cube, FAR `GeometryPartModuleRebuildMeshData` and `onEditorShipModified` behind one restartable 0.5 s debounce (WP:2876-2916). This implements DESIGN §7/§8.6 throttling; unlike B9, don't also call FAR immediately.
2. **Coarse/fine slider widget** (UIUtility:135-247). Side buttons do coarse steps on left-click and fine steps on right-click, drag snaps to the increment, right-drag is a vernier, and **#** toggles numeric entry. Use it for Pc, ε and thrust; use a log scale for ε and bounded `LimitedSlider` semantics.
3. **"Last edited" explanation box** (WP:3149-3165, 3617). A localised help line for whichever field was just touched is a cheap way to explain separation, ε and Pc.
4. **Inherit-from-neighbour buttons** (WP:616-727). Our equivalents would be "Match mount to part above", "Match exit to interstage/fairing below" and "Copy design from parent engine". Drive them from attach-node size or bounds, not only from our own module, and show them only when applicable.
5. **Template-vertex deformation for kit pieces** (complements the generated bell). Author pumps, ducts and frames once, then stretch regions independently, like a 9-slice. Encode the zone in **UV2 or vertex colour in Blender** instead of B9's fragile position thresholds. Keep UVs in world units, swap variants with `SetActive`, and still update the bounds of inactive variants.
6. **Material and paint channels in vertex data** (UV2.x layer, colour RGBA paint) with one shared material, so draw calls batch. For heat glow, use a `MaterialPropertyBlock` per part, and prefer stock shaders unless we ship a bundle with deferred support.
7. **Persist derived stats alongside inputs** (WP:2525-2547), which matches DESIGN STATS. Use a **vessel-wide two-pass flight init**: geometry first, then aero/physics after 2 FixedUpdates (WP:2401-2491).
8. Later: in-scene **drag handles** (`EditorHandle.cs`) for nozzle length and exit diameter.

**Avoid:**
- About 40 flat KSPFields with "Cached" twins and per-frame polling. Use one DESIGN node with a dirty flag.
- Static UI state and per-part `OnGUI`. Use one KSPAddon window controller.
- Hover+J as the only entry point (it can't edit the root part and the keys are hidden). Use a PAW button and a toolbar button, as DESIGN §7 already plans.
- Render-mesh colliders, and visible parts with no collider at all.
- Leaving normals unrecalculated after non-uniform scaling.
- A hard-coded `GetChild(0)×3` transform path.
- Allowing zero dimensions.
- Having no schema version.
- Not locking the keyboard while a text field has focus. Use `InputLockManager` `KEYBOARDINPUT` when `GUI.GetNameOfFocusedControl()` is a field.
- Storing tank volume without any dry mass.
