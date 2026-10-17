# Study: VertexBasedProceduralParts (VBPP)

**Sources.** GitHub `KerbalHub/VertexBasedProceduralParts` (MIT, 18 stars, created 2026-03-10, last push 2026-03-16; README is three lines, no wiki pages, no docs). **The repo source is stale:** every `.cs` file was last touched in `c582ee9` ("Update for v0.2.0"), and tags v0.3.0 through v0.3.7 all point at `3435f1d`, an RP-1 cfg merge. The installed DLL (`ksp_mirage/.../VertexBasedProceduralParts.dll`, 27,136 bytes, dated 2026-03-16) is the **v0.3.7 release**. It contains `ShoelaceFormula`, `CrossSectionPresets`, `sideMaterial` and ClickThroughBlocker, none of which are in HEAD (the repo DLL is 19,456 bytes). So this study is based on an ilspycmd decompile of the installed DLL, done statically, with the HEAD source used for comparison:

- `dec:` = `scratchpad/decomp_vbpp_installed/out/VertexBasedProceduralParts/*.cs` (v0.3.7, the behaviour players get)
- `src:` = `scratchpad/src_vbpp/VertexBasedProceduralParts/*.cs` (v0.2.0)

The whole mod is about 1,100 decompiled lines: one PartModule plus three helpers.

## 1. What it is, and how you edit it

**Shape model.** One part, `VBProceduralPart` (structural; placeholder cube .mu). Its shape is **exactly two cross-section rings** (bottom and top), each with a **fixed 24 vertices** (`dec:CrossSection.cs:27`, `dec:VBProceduralParts.cs:254-255`). The two rings are a fixed `l` apart (0.01–10 m). The part is a **ruled loft**: vertex *i* of the bottom ring is joined to vertex *i* of the top ring (`dec:Lofter.cs:87-163`). There are no intermediate sections, no smoothing and no rounding. Corners come from stacking duplicate vertices: the `Mk2Fuselage.json` preset repeats points to make hard corners, and users ask for a vertex-count slider instead (#8).

Each vertex is a `Vector3`:

- x and y are the in-plane position.
- **z is a per-vertex axial offset.** `RingCreator` swaps the coordinates to (x, z, y) and adds ±l/2 (`dec:RingCreator.cs:179-210`). So rings needn't be planar: you can get scarfed or slanted ends, and twists by vertex correspondence.

Built-in operations:

- **Cap Top/Bottom** collapses a ring to the origin, which gives a cone or wedge. A failsafe refuses to cap both ends (`dec:411-424`, `IsCap` `dec:219-233`).
- **Reset** restores a 1.25 m circle.
- **Scale Cross-Section** scales the ring about the origin by a PAW factor (`dec:795-813`).
- **Toggle Side Visibility** sets `sideMaterial[i] = -1` to drop the quad strip between vertices *i* and *i*+1 (`dec:192-211`). That makes open panels and plates out of a tube. It's novel.

**Editing mechanics.** Everything is in the PAW plus one IMGUI window. There are **no in-scene handles or gizmos**.

- **PAW** (`dec:35-81`, `140-217`):
  - Preset chooser: a `UI_ChooseOption` whose options are filled at runtime from JSON (`dec:249-253`).
  - Length slider.
  - "Selected Ring" (0/1) and "Selected Vertex" (0–23) sliders, then **Vertex X/Y/Z sliders** (−2…5, step 1e-4). This is the only numeric entry, and the only way to set z.
  - In-section "Mirror Symmetry" toggle.
  - Flat-shading toggle, Scale Factor, and buttons for Reset, Cap, Copy/Paste, Scale, Toggle Side and **Toggle 2D Editor**.
  - Users can't find the editor (#13 "How to use?"). That confirms our §7 rule that a hidden entry point isn't enough.
- **2D Cross-Section Editor** (`dec:874-1053`). It is per-part `OnGUI` through `ClickThruBlocker.GUIWindow(0, …)`, a fixed 800×800 window at (200,200). Its title tells you to "Hold Shift to Snap". It shows the active ring's 24 vertices as 16 px `GUI.Box` handles at `pos*zoom + (400,400)`, with screen y pointing down.
  - **Picking:** a rect hit-test on MouseDown.
  - **Multi-select** (`dec:978-1000`): Ctrl+click toggles a vertex in a `HashSet`, **Ctrl+Shift+click selects all**, and a plain click on an unselected vertex selects only that one. A plain click on an already-selected vertex starts a **group drag**.
  - **Group drag:** start positions are cached (`dragStartPositions`, `dec:1001-1005`). The dragged vertex follows the mouse, and every selected vertex moves by the same delta, so relative offsets are kept (`dec:1011-1030`).
  - **Snapping:** the dragged vertex's absolute position is rounded to 0.05 m with Shift held, else 0.001 m (`dec:954-961`, `1013-1014`).
  - **Mirror symmetry inside the section:** `GetMirroredIndex = (n/2 − i + n) % n` with x → −x, applied per vertex during the drag (`dec:445-459`, `1022-1026`).
  - A zoom slider runs from 50 to 400 px/m (`dec:1049`), plus X/Y readout labels.
  - `boxStart`/`boxEnd` are declared but unused, so box-select was probably planned (`dec:121-123`).
- **Copy/paste:** a `static List<Vector3>` clipboard, so it works across parts within the session only (`dec:13`, `369-381`). Paste replaces the active ring.
- **Undo:** none (no `SetBackup`).
- **Editor coexistence:**
  - Input locking is delegated entirely to ClickThroughBlocker, a new dependency in v0.3.7 that answered #7.
  - Shift and Ctrl state are tracked from `Input.GetKeyDown/Up` in `Update`, which returns early while dragging (`dec:492-511`).
  - No editor-mode switching.

**Compared with the three mods we studied.** Procedural Parts and CRFP are parametric, and B9 PWings deforms template meshes. VBPP is the only one with **free per-vertex editing**: direct 2D manipulation of an arbitrary polygon, with multi-select, relative group drag, grid snap and in-profile mirroring. The ideas worth noting:

- non-planar rings (the z offset),
- lofting by index correspondence,
- "collapse to a point" as a cap,
- duplicate vertices as creases,
- per-face hiding.

The 3D side is weak: no gizmos, and z can only be set with a PAW slider.

## 2. Mesh generation

- **Loft** (`dec:Lofter.cs`):
  - The vertex list is `bottom + top + bottom + top`, and the second copy carries reversed winding. So the mesh is **double-sided by duplication**, with no caps and no polygon triangulation; that's also why "Flip normals" was removed.
  - Diagonals alternate per quad (`i%2`).
  - Ring closure uses `(i+1)%n`, so there's **no duplicated seam vertex**.
  - Concave sections "work" only because nothing is triangulated.
- **Normals:** `RecalculateNormals`. "Flat shading" unwelds every triangle (`dec:855-872`). No tangents.
- **UVs:** cylindrical projection `u = (atan2(x,z)+180)/360`, `v = y + l` (`dec:832-853`). That means:
  - a texture-wrap artefact on the seam quad,
  - stretching on off-centre or flat shapes,
  - V shifts whenever the length changes,
  - no tiling control.
- **Mesh reuse:** the render mesh is reused (`mesh.Clear()` then refill, `dec:710-716`). But a brand-new loft `Mesh` is created on every rebuild and handed to the collider, and the previous one is never destroyed, so **one mesh leaks per drag event** (`dec:709`, `727`).
- **Collider:** that loft mesh, set to `convex = true` (`dec:717-728`). So a plate or a concave fuselage collides as its convex hull.
- **Drag and FAR:**
  - No drag cubes: stock aero uses the placeholder cube's drag.
  - FAR gets `SendMessage("GeometryPartModuleRebuildMeshData")` on every rebuild (`dec:743-749`).
  - Open #10: with FAR, **the whole craft loses all lift and drag** when one VBPP part is attached. The cause is unknown; a guess is an unreadable/degenerate mesh or the double-sided duplicate. Either way, FAR plus runtime meshes needs an early smoke test.
- **Throttling:** none.
  - Every MouseDrag event runs `GenerateMesh` and `UpdateMass` twice: `GenerateMesh` already calls `UpdateMass` (`dec:729`), and `UpdateMass` fires `onEditorShipModified` (`dec:344-348`).
  - `Update` polls the slider values every frame and calls `FindModelComponent` each frame (`dec:466-545`).
- **Deferred first build:** `OnStart` defers the first mesh by one frame via a coroutine (`dec:271-279`). For that frame the cube collider is live. That is the root cause of the ConformalDecals bug, where decals project onto the cube (#6, analysed in its comments). It's direct evidence for our §8.1 "build synchronously in OnStart" rule.
- **Children after a rebuild:** it sends `OnAttach` to every child `ModuleConformalDecal` so decals re-project (`dec:731-740`).
- **The placeholder** is `DestroyImmediate`d and the procedural mesh is parented under `model` (`dec:653-687`).

## 3. Attach nodes, symmetry

- **Nodes:** only `top`/`bottom` on the axis at ±l/2, whatever the ring shape or z offsets. Node size never changes, there's no `originalPosition` and no `onPartNodeMoved` (`dec:751-770`).
- **Moving neighbours on a length change:**
  - Stack neighbours move by a **delta** (`length − oldLength` from the field callback, `dec:436-443`, `772-793`).
  - If the neighbour is the parent, the part moves itself and shifts the root (`dec:815-830`), which is Procedural Parts' recipe.
  - Surface children are not moved.
- **Symmetry:**
  - `PropagateSymmetry` copies the vertex lists verbatim to the counterparts (`dec:547-584`). It doesn't reflect them for mirror symmetry, and it pushes neither `sideMaterial` nor `l`, which is left to KSP's field symmetry.
  - Open #9 "Uneven mirroring" reports exactly this inconsistency.
  - `ToggleSide` with in-section mirror writes `sideMaterial[mirroredIndex - 1]`, which is **index −1** when vertex 12 is selected (`dec:205-209`).

## 4. Persistence

- **Persisted fields:** `bottomVerticesData` and `topVerticesData` are strings of `x,y,z;x,y,z;…`, written by `StringBuilder.Append(float)` and read back with `float.Parse` (`dec:586-651`). `sideMaterialData` is a comma list. `l` and `isFlatShading` are also persisted.
- **Size:** about 1–1.5 KB per part.
- **No schema version.**
- **Culture-sensitive formatting:** a comma-decimal locale collides with the `,` separator. Precision is the default "G", not round-trip.
- **Re-serialised on every rebuild** (`dec:691-692`).
- **Presets:** `CrossSectionPresets/*.json` `{name, vertices:[{x,y}…]}`. They are read with `File.ReadAllText` + `JsonUtility` from a hard-coded GameData path **in every part's `OnStart`** (`dec:CrossSectionPresets.cs:66-78`, `dec:249`). Consequences:
  - ModuleManager can't patch them.
  - Applying a preset indexes by the ring's count, so a preset with fewer than 24 points throws (`dec:310-327`).
  - The preset is applied to the active ring only.
- **Flight:** the mesh is rebuilt from the persisted strings in `OnStart`, and `Update` exits outside the editor. That's fine for LMP ProtoVessel sync, and there's no PartSync XML (none needed). Open #16: the TU recolour reverts to plain metal on launch.

## 5. Mass/cost, TU, RP-1

- **Mass:** `V = l/3·(A1 + A2 + √(A1·A2))`, a frustum formula with shoelace areas (`dec:ShoelaceFormula.cs`, `dec:337-367`). It's a **solid** volume × 0.1 t/m³, and cost = 1000 × mass.
  - It's wrong for skewed or z-offset shapes and for open sides.
  - Players call it "rather heavy" and ask for wall thickness (#14).
- **No resources or tanks.**
- **TU** (`VBPP_TU*.cfg`):
  - Defines three `KSP_COLOR_PRESET`s and adds them to the SSTU preset group.
  - Adds `SSTURecolorGUI` and `KSPTextureSwitch` with two `KSP_TEXTURE_SET`s ("Plain Metal", "Digital Camo").
  - Both sets use the `TU/Metallic` shader with **Procedural Parts' textures** (an implicit dependency) plus its own camo mask.
- **RP-1** (`VBPP_RP-1_Support.cfg`): only `TechRequired = start`, `RSSROConfig`, `RP0conf`, `specLevel = operational`. No RealFuels tank types, no `ModuleTagList`, no tooling. PR #11 (open) adds `entryCost`/`cost`.

## 6. Licence

**MIT**, with the boilerplate unfilled: "Copyright (c) [year] [fullname]" (`LICENSE.txt:3`). The only author attribution is in the part cfg (`author = KH759`). Copying code into MIT ProcEngines is allowed if the notice is kept. Attribute "KerbalHub/KH759" and note that the published source lags the binary. Ideally, ask the author for a proper notice before copying verbatim. In practice nothing is worth copying: the patterns are trivial to reimplement.

## 7. Quality and pitfalls

Early hobby code (README: "Still WIP").

- Unused `using`s (`UnityEditor`, `CommNet`, `BitVector32`).
- Lofter instantiated twice.
- Both window ID and drag state are per part, with the window always using ID 0.
- `scaleFactor` has no initialiser, so it is 0 until the slider is touched, and "Scale" collapses the ring.
- Leaked meshes.
- Unthrottled rebuilds.
- The PAW single-selection (`selectedVertex`) and the window's multi-selection are separate states.

Open issues: #6/#16/#17 (decal, TU, TURD interactions), #7 (window too big), #8 (vertex count), #9 (symmetry), #10 (FAR kills craft aero), #12 (CKAN), #13 (discoverability), #14 (thickness).

## Useful for ProcEngines (ranked)

1. **A 2D profile editor as an advanced designer tab.** VBPP's real contribution is direct 2D manipulation of a polyline. Map it onto the half-profile (r vs x) of the engine, drawn from §8.2's contour:
   - **Nozzle:** show the Rao Bézier's three control points N, Q, E as handles. Dragging Q changes θn/θe and dragging E changes ε/Ln, with the throat locked. The model recomputes the divergence loss, so the drag stays physical. Free vertices are only allowed on **cosmetic** profiles: chamber jacket, injector dome, **thrust-structure/mount shroud**. These are revolved by the same lathe and add mass by Pappus shell area.
   - Draw it in our UGUI window, not IMGUI, and keep the profile clamped to valid ranges.
2. **Multi-select + relative group drag + snapping the dragged anchor to a grid:**
   - Ctrl toggles, Ctrl+Shift selects all, Shift snaps.
   - Shift is already "fine" in §7.2, so pick a non-conflicting key (Alt = snap).
   - Add box-select, which VBPP left unfinished.
3. **Shape presets as data.** Contour/mount/dome presets ("Bell 80%", "Conical 15°", "Truncated ideal", "Merlin-style mount") become `PROCENG_PROFILE` ConfigNodes in GameData, loaded once at startup and MM-patchable. Not JSON read per part. A preset carries its own point count (resample to ours).
4. **Partial copy/paste:** "Copy nozzle", "Copy mount" between engines, on top of whole-design `PE1:` codes. Use the system clipboard (`GUIUtility.systemCopyBuffer`), not a static field.
5. **A non-planar exit via per-vertex axial offset:** a **scarfed nozzle** option (exit-plane tilt for clustered or side-mounted engines). It needs only an angle parameter, not per-vertex z.
6. **Per-section visibility:** a designer-only **cutaway/"hide kit"** toggle (pumps, plumbing, outer shell) to inspect the chamber and regen. Editor visual only, never persisted.
7. **Re-notify children after a rebuild:** ConformalDecals and FAR are installed. After the debounced rebuild (§7.3), send `OnAttach` to child `ModuleConformalDecal`s, plus the planned `onPartModelChanged`.
8. **ClickThroughBlocker** for the window: VBPP needed it after #7. It confirms §7.1.

**Avoid** (each one already bit VBPP):

- **A deferred first build:** the cube collider and decal bug (#6).
- **The render mesh as a convex collider.**
- **No drag cubes.**
- **Untested FAR on runtime meshes:** #10 kills the whole craft's aero. Smoke-test FAR in Phase 1.
- **Culture-dependent float strings:** use `ToString("R", CultureInfo.InvariantCulture)` / ConfigNode values.
- **Unversioned fields.**
- **A fixed vertex count baked into the data.**
- **Copying lists verbatim to mirror counterparts.**
- **Per-part `OnGUI` with window ID 0.**
- **Per-frame polling and `FindModelComponent`.**
- **Rebuild + `onEditorShipModified` per mouse event.**
- **Leaked meshes.**
- **No seam vertex, and V tied to length.**
- **`RecalculateNormals` on duplicated geometry.**
- **Solid-volume mass for shells.**
- **Depending on another mod's textures** (VBPP's TU sets need Procedural Parts).
- **Discoverability through one PAW button** (#13).
