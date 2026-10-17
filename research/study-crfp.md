# Study: CRFP (CarnationRED Flexible Parts) for ProcEngines

Source: SpaceDock mod 2380 (v0.7.0.5, KSP 1.12.5, CC BY-SA 4.0), maintained by linuxgurugamer. Repo: https://github.com/linuxgurugamer/CarnationREDFlexibleParts. Shallow clone at `scratchpad/src_crfp`. Read only, nothing built. Paths below are relative to the repo; `MCVP` = `Main/ModuleCarnationVariablePart.cs`, `Tool` = `Main/CVSPEditorTool.cs`.

**Verdict:** CRFP is a handle-based mod but not a free-vertex one. Each part is a lofted solid between two rounded rectangles ("sections"). Every section has a width, a height and four corners, and each corner has a signed radius and a shape type. On top of that come length, twist, two tilts and run/raise offsets. You edit it with in-scene handles on the section corners and edges. There is no arbitrary vertex editing. The only "vertex" feature is the Creator, which builds a panel from 3–4 points you pick in the scene. It is still the best KSP match for "direct manipulation of procedural geometry". Nothing mainstream does true per-vertex editing, so I didn't look further.

## 1. User-facing mechanics

**Shape parameters.** All of these are persistent KSPFields:
- `Section0Radius` and `Section1Radius` (Vector4, one value per corner, −1..1). A negative radius ignores aspect ratio (MCVP:50-56, 58-94).
- `SectionCorners`: a string of 8 corner-type names (MCVP:98-124). Corner shapes are 7-point unit curves loaded from `Config/CRFPSectionCornerDefinitions.cfg` (fillet, chamfer, and custom ones). Each carries a `cornerArea` and a `cornerPerimeter` used for volume.
- `SectionSizes` (w0, h0, w1, h1), `offsets` (twist, length, run, raise), `tilts`, and `scale` (mirror) (MCVP:127-145).
- Clamps live in the property setters: twist and tilt to ±45°, size to a tech-limited `MaxSize` (MCVP:209-266).

**Handles.**
- The prefab `handles` comes from the asset bundle `AssetBundles/cvsp_x64.handles`. It has 14 children, each given a `HandleGizmo` and a type (Tool:60-74, 632-668):
  - 0–3: corner size (XAndY)
  - 4: length (Stretch)
  - 5: twist (Rotation)
  - 6–9: corner radius
  - 10–13: edge width or height
- Two copies are reparented under the part's `section0node` and `section1node` transforms, so they ride the geometry for free (Tool:1188-1208). They are rescaled to the section size (Tool:1213-1235). They fade when that end faces away from the camera (Tool:1082-1100) or while the right mouse button is held (`HandleGizmo.cs:95`).
- Handles sit on layer 2 (`GizmosLayer = 0b10`, "Ignore Raycast", Tool:614). KSP's own picking skips that layer. CRFP raycasts only that layer on LMB down (Tool:1104-1118), and a click on empty space closes the tool (Tool:1119-1122).
- Dragging works in screen space. On click, the handle projects the section's local right, forward and up axes to screen vectors. Each frame the mouse delta is dotted with those vectors, which turns pixel motion into world units. Twist is the signed screen angle around the section centre × 0.4 (`HandleGizmo.cs:101-174`). This is simple and robust, with no 3D plane intersection.
- Snapping reuses the stock VAB angle-snap toggle. Offsets snap to 0.25 m (0.125 m with Shift) and angles to `EditorLogic.srfAttachAngleSnap` or its fine variant (Tool:1050-1072). Pressing C while the mouse is over the panel toggles stock snap and updates its button (MCVP:1525-1538).
- Modifiers:
  - Ctrl on a size handle forces a uniform width and height (Tool:954-955).
  - On a radius handle: Shift changes all 8 corners, Ctrl the 4 at the same end, Alt the matching corner at the other end (Tool:1020-1045).
  - Numpad 1/7/3/9 nudge run and raise (Tool:1126-1152).
  - Esc exits (Tool:1125).
- Corner index labels are drawn with IMGUI `OnGUI` plus `Drawing.DrawLine` while the mouse hovers a radius slider (MCVP:1988-2010).

**Coexisting with the stock editor.**
- On activation, CRFP forces `EditorLogic.fetch.toolsUI.SetMode(ConstructionMode.Place)` so the stock offset and rotate gizmos disappear (MCVP:1667). It also dims the part highlighter (Tool:700-701).
- It calls `EditorLogic.fetch.Lock(false,false,false,"CVSPEditorTool")` only while the mouse is over its panel (Tool:309-325).
- Any stock part event other than dragging deactivates the tool (MCVP:1241-1244).
- Hotkeys: F on a hovered part opens handles and panel (MCVP:1522-1523). A right-click without dragging opens the panel only (MCVP:1580-1581). Ctrl+F on another part while active copies the shape to it (Tool:833-842).

**UI.**
- A UGUI Canvas prefab is loaded from `AssetBundles/cvsp_x64.gui` (Tool:276-292). Its code lives in a separate `UI` assembly that knows nothing about KSP. The `Main` assembly injects KSP behaviour through static events: localisation, snap state, camera, input lock, part spawning (Tool:294-388). That decoupling is clean.
- The panel holds sliders with numeric fields (`UI/CVSPAxisField.cs`), corner-type cycle buttons, texture browsers, a tank-type switcher, and Link toggles. Link copies the section shape onto the stacked neighbour CRFP part (MCVP:268-371).

**Copy/paste and presets.**
- Ctrl+C/V copies every `Fields[i]` value through a static `clipboard` (MCVP:1544-1564).
- Section presets are XML `SectionInfo` files in `GameData/.../Presets` (`UI/CVSPPresetManager.cs:270-330`). Their thumbnails are rendered by an off-screen camera into a 128² RenderTexture (`Main/SectionPreviewRenderer.cs`).

**Undo.** There is no custom undo stack. With `FullUndoAndRedo = True` (`CRFPSettings.cfg`), CRFP calls `EditorLogic.fetch.SetBackup()` when the key or mouse is released after a change (MCVP:1583-1593), and once when UI editing starts (MCVP:1686). Because all state is in persistent KSPFields, stock undo restores it and the part's `Start` rebuilds the mesh.

**Creator.** It picks 3–4 points with a trigger capsule cast along the mouse ray, parented to the camera (`UI/CapsuleRay.cs:60-90`). Snap mode follows the held key: vertex by default, Ctrl for surface, Alt for edge with percentage snapping (C toggles it, Shift for fine) (CapsuleRay:161-214). It then spawns the part with `EditorLogic.fetch.SpawnPart`, sets its parameters in an `onEditorPartEvent` capture, and fixes the transform on `onPartAttach` (Tool:1265-1306, 179-243). This is exactly the spawn-and-apply pattern in DESIGN §10.1 Phase 5.

## 2. Mesh generation

- **Topology.** One singleton `CVSPMeshBuilder` (`Main/CVSPMeshBuilder.cs:233`) with a fixed, hand-made topology: 58 section verts, 7 per corner, plus a centre fan per end cap (lines 12-128).
- **Per update** (lines 777-813): place corner curves → collapse zero-radius corners → scale, tilt and twist, then offset → seam normals → build 4 side strips (`Main/CVSPBodyEdge.cs` `MakeStrip`) → end-cap UVs → merge → delete hidden end caps → strip degenerate triangles (`Optimize`) → write the mesh with 2 submeshes (ends, sides).
- **Normals and tangents.** Angle-weighted normals and tangents are computed by hand (lines 930-980). Corner seam normals are averaged between strips (lines 597-606). Tangent `w` is always 1 (line 740), which is wrong for mirrored parts. The subdivision code exists but is unfinished and disabled (MCVP:2044-2071).
- **UVs.** Options are `RealWorldMapping`, a perimeter-corrected U (`CornerUVCorrection`), offsets and scales (BodyEdge:102-131).
- **Rebuild trigger.** A reflection-based `CVSPField.ValueChanged` compares every tagged field once per frame (`Main/CVSPField.cs:30-60`). It rebuilds every frame while a change is happening, with no throttle. `mesh.MarkDynamic()` is called while editing (CMB:912).
- **Collider.** A single `MeshCollider` from the .mu. Its `sharedMesh` is set to the render mesh on every rebuild (MCVP:686-690), and symmetry counterparts get it on mouse release (MCVP:714-727). Non-mirrored counterparts share the mesh (MCVP:1457).
- **Drag cubes.** `DragCubeSystem.Instance.RenderProceduralDragCube(part)` runs only on release (MCVP:1072-1075). In flight it runs again via a coroutine (MCVP:1190-1200).
- **FAR.** Reflection on `GeometryPartModule.RebuildAllMeshData` (`Main/FARAPI.cs`).
- **Attach nodes and moving attached parts.** The node position, orientation and `nodeTransform` are set to the section transforms, and the node size follows the section size (MCVP:1024-1032, 1632-1641). Moving attached parts uses a neat trick:
  1. Reparent the attached children under the section transforms (MCVP:943-969).
  2. Move the section transforms.
  3. Shift the part itself so the end attached to its parent stays put in world space (MCVP:973-1003).
  4. Reparent the children back (MCVP:1007-1020).

## 3. Persistence and sync

- **What's saved.** Only `[KSPField(isPersistant)]` fields: shape, texture file names, tint, mapping flags, misc flags, UV offsets and scales, `isSectionVisible`, `tankType`, `scale`, `CoMOffset`. There is no `OnSave`/`OnLoad` override and no schema version. v0.6 broke old saves ("NOT COMPATIBLE WITH OLD SAVEGAME", `Changelog.md`).
- **Load.** `Start` → `LoadPart` rebuilds the mesh, transforms, nodes and tank (MCVP:1104-1188).
- **Flight.** End caps covered by a matching neighbour are hidden (MCVP:1706-1782, flight only), and the section helper objects are destroyed (MCVP:1176-1179).
- **Symmetry.** In `Update`, the editing part pushes its values to its counterparts with `CopyParamsFrom`, swapping corners for mirror symmetry. Mirroring itself is a negative `localScale.z` on the model (MCVP:1428-1458, 1613-1631).
- **Multiplayer.** Nothing specific. Because everything is in the MODULE node, ProtoVessel sync carries the shape. Textures, however, are referenced by file name under `PluginData/Texture`. A client without the same files falls back to defaults.

## 4. Resources, mass and materials

- **Volume.** Frustum formula `L/3·(A0+A1+√(A0A1))`, which ignores twist, run and raise (MCVP:1897-1901). Area is `w·h/4 · Σ(1−(1−cornerArea)·|r|)` (MCVP:1902-1913).
- **Dry mass, cost and resources.** Dry mass is `lerp(volume, surfaceArea, coeff)·dryMassPerVolume`, and cost is `dryMass·dryCostPerMass` (MCVP:809-810). Resources are rebuilt with `RemoveResource`/`AddResource(ConfigNode)`, keeping the fill fraction (MCVP:895-925). RealFuels gets `ModuleFuelTanks.ChangeTotalVolume` (`Main/RFAPI.cs`).
- **Cost widget.** Refreshed by reflection on the private `CostWidget.onShipModified` (MCVP:526, 1284-1293). This is a hack. Firing `onEditorShipModified` is the better route.
- **Tech limits.** `CRFPTechLimits.cfg` maps a tech node to maximum size and length. CRFP rewrites `partInfo.TechRequired` globally and starts a background thread to poll R&D (`Main/CVSPConfigs.cs:76-150`).
- **Materials.** Two materials on the stock `KSP/Bumped Specular (Mapped)` shader (ends and sides). Textures are DDS files loaded at runtime from `PluginData/Texture` with sarbian's MIT DDS loader into a static cache. A 2×2 texture provides the plain tint (MCVP:1849-1892). No TexturesUnlimited.

## 5. Licence

The code is CC BY-SA 4.0. Copying or adapting any code, the handle or UI asset bundles, or the section and corner tables makes ProcEngines a derivative work. A derivative must be released as BY-SA 4.0 or a CC-approved compatible licence (GPLv3 only). It cannot be MIT. Some bundled textures are CC BY-NC-SA, which is incompatible with MIT as well. The ideas, UX patterns and algorithms aren't copyrightable, so a clean-room reimplementation from this description is fine. The DDS loader is sarbian's MIT code and can be reused with attribution. Recommendation: copy no code or assets from CRFP. Write our own handles, which are only ~150 lines.

## 6. Code quality and pitfalls

- Known issues in `README-WEB.md`:
  - parts end up in the wrong position when editing mirror symmetry
  - custom textures lost on load
  - bad movement when Tilt and Twist change together
  - handle gizmos sometimes copied along with the part
- A background thread touches Unity and KSP APIs (CVSPConfigs:88, 130-150), which isn't thread-safe.
- The singleton mesh builder holds per-part state. Its `lock` is meaningless.
- Per-frame reflection and boxing. New `Material`s per part that are never destroyed. Static texture caches.
- Hard `using` references to FAR and RealFuels types. This only works because the JIT is lazy.
- Mirroring with negative scale plus tangent w = 1 gives broken normal maps on mirrored parts.
- Rebuilding the render mesh into a non-convex collider every frame is costly. Drag cubes are rendered on release.
- No format versioning. Copy/paste copies all fields by index.

## Useful for ProcEngines (ranked)

1. **Screen-space axis-projected drag handles** (`HandleGizmo` approach). Parent the handle GameObjects under our generated transforms: the exit plane, throat and chamber top.
   - Exit-rim handle: drag radially to change exit diameter, which sets ε at fixed throat, or Rt at fixed ε with Ctrl.
   - Exit-plane axial handle: drag along the axis for the nozzle length fraction Lf (0.6–0.9).
   - Chamber-top axial handle: chamber length or L*.
   - Ring handle on the gimbal pivot: gimbal range.
   - Convert pixel delta to world units with the projected axis, as CRFP does, and feed it through the same clamps as the sliders.
2. **Put handles on layer 2 and raycast only that layer.** Fade handles that face away. Scale them with part size. Switch the stock editor to Place mode on activation, call `EditorLogic.Lock` only while the cursor is over our window, and close on any stock part event or a click on empty space.
3. **Reuse stock snap.** Snap only when the VAB angle-snap toggle is on, with Shift for fine steps. Snap ε to a 1-2-5 log ladder, Pc to 0.5 MPa and diameter to 0.125 m, so it feels native.
4. **Undo with `SetBackup()` on drag release only**, never per frame. The design lives in the MODULE node, so stock undo, LMP and craft files all work unchanged.
5. **Attached-part follow trick.** Reparent children under the moving node transform, move it, then restore. That keeps the stack intact when the nozzle length changes. Combine it with Procedural Parts' approach from DESIGN §8.1. This is our own reimplementation.
6. **Live gizmo overlays.** A translucent gimbal cone drawn from the pivot, an exit-plane disc coloured by separation risk at sea level, and a ghost of the previous shape while dragging.
7. **Off-screen camera thumbnails** for design cards, at 256² instead of 128².
8. **Spawn-and-apply** via `SpawnPart` plus a one-shot `onEditorPartEvent` capture (Tool:1265-1306) for the library panel.
9. **Shape copy hotkey** (Ctrl+F-like, "apply hovered design to this engine"), plus Ctrl+C/V producing our `PE1:` share code instead of a static array.
10. **Split UI and KSP assemblies with injected delegates.** This keeps a UGUI prefab project testable outside KSP.

**What to avoid:**
- per-frame reflection change detection (use explicit dirty flags and the 10 Hz throttle from DESIGN §7)
- render mesh used as the collider
- negative-scale mirroring
- singleton builder state
- background threads touching KSP
- rewriting `partInfo.TechRequired`
- reflection into `CostWidget`
- unversioned persisted fields (we have `schemaVersion`)
- texture references by loose file name
- any copied CRFP code or assets (BY-SA)
