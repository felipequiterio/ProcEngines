# ProcEngines Phase 3: visual pass, status

Date: 2026-10-17. Phase 3 of [DESIGN.md](DESIGN.md) §14. Repository: https://github.com/felipequiterio/ProcEngines (private).

**Status:** the visual pass is done for everything that doesn't need authored art:

- materials and the heat glow;
- turbomachinery and plumbing kit pieces for every cycle;
- GG turbine exhaust duct with its own plume;
- calibrated Waterfall plume sizes;
- in-scene drag handles.

All checks pass in game:

| Run | Result |
|---|---|
| sandbox (ksp_test_A) | 22/22, including the handles |
| visual (ksp_test_B) | 6/6 |
| career (ksp_test_B, regression) | 6/6 |
| unit tests | 25/25 |

Template-vertex-deformed kit pieces, the TexturesUnlimited path and LOD are not done (see the end).

## What changed

| Area | Implementation |
|---|---|
| Materials (`EngineMaterials.cs`) | All textures are generated in code: no texture assets, nothing borrowed from other mods. The materials are shared, so identical engines batch. KSP's stock `KSP/Bumped Specular` / `KSP/Emissive/Bumped Specular` shaders. <br>• **Regen copper:** a round-tube normal map plus darker grooves in the albedo, one tube per U repeat, with a **whole number of tubes** baked into the UVs. They run the length of the nozzle, tapering with the wall (DESIGN §8.4). <br>• **Chamber and mount:** brushed steel. <br>• **Radiative extension:** niobium (light grey) below the Late tech level, **carbon-carbon twill** at Late. <br>• **Inner wall:** soot. |
| Heat glow (`ModuleProcEngine.UpdateGlow`) | The extension's wall temperature follows throttle with a first-order lag: heats in ~2 s, cools in ~7 s; niobium up to ~1700 K, C-C ~2000 K. An approximate blackbody colour is applied through a **per-material property block** on the extension slot. That block starts from KSP's renderer-wide block, so the part highlight still works. <br>• **Finding:** KSP instantiates part materials and keeps its own renderer-wide property block, so writing `_EmissiveColor` on a material, or on a per-part material instance, never shows. The in-game diagnostic proved this; only the per-material block does. DESIGN §8.4 should say so. |
| Geometry (`EngineGeometry.cs`) | Profile segments now carry a material and UV tiling (`UTiles`/`VLength`). The convergent section is copper regen; the chamber jacket, mount and dome are steel. Ring segment count is scaled to the exit circumference (24–72). |
| Kit pieces (`KitBuilder.cs`) | Built from primitives (cylinder, frustum, sphere, torus, Catmull-Rom swept tubes with parallel-transported frames) into one mesh with 3 materials (structure, machinery, lines). Visual only, no colliders. <br>• **All cycles:** 4 thrust-frame struts, 2 gimbal actuators (piston + rod), and a regen coolant manifold torus around the bell. <br>• **Gas generator:** turbopump (housing, volute torus, turbine disc), gas generator, oxidizer line to the dome, fuel line down the bell to the manifold, and a **turbine exhaust duct** routed clear of the bell, ending in a small nozzle where the exhaust plume attaches (`peGGExhaust` transform). <br>• **Staged combustion:** pump plus a preburner on top and a hot-gas duct into the dome. <br>• **Full flow:** two pumps and two preburners, 180° apart. <br>• **Expander:** small pump; heated hydrogen goes from the manifold up to the turbine. <br>• **Pressure-fed:** 2 or 4 COPV spheres sized from the pressurant mass, plus valves. <br>• **Sizing:** the pump envelope comes from the turbopump mass at ~5 t/m³, capped at 0.9× chamber radius. Line diameters come from flow at 15 m/s. Everything stays below the mount plate, so nothing intersects the part stacked on top (an early build had preburner domes poking through). Everything derives from the persisted STATS, so all LMP clients build the same kit. |
| Waterfall (`Integrations.cs`, `Plumes.cfg`) | Several templates per engine: the main plume on `thrustTransform`, plus the GG exhaust plume on `peGGExhaust` (`waterfall-kerolox-exhaust-1`, `methalox-vernier-1`, `hydrolox-upper-1`). <br>• **Calibrated `referenceDiameter`** with the new `plumes` rig: each template at scale 1 on a 1.00 m exit, side view at sea level, measuring plume width at the exit plane against the nozzle. <br>• Results: kerolox-lower-1 **0.43**, kerolox-upper-1 **0.40**, methalox-lower-raptor-1 **1.18**, methalox-upper-1 **0.85**, hydrolox-lower-1 **1.07**, hydrolox-upper-1 **1.0**. The exhaust templates are tiny ducts: 0.12 / 0.08 / 1.0. <br>• Before this, kerolox plumes were drawn at ~40 % of the nozzle width. |
| Drag handles (`EngineHandles.cs`) | Three screen-space handles on the engine open in the designer: **exit rim** (radius → area ratio), **nozzle tip** (length → bell length fraction) and **chamber side** (radius → thrust, since the chamber radius scales as √F at fixed Pc). <br>• Dragging projects the mouse ray onto the handle's axis, frozen at drag start so the axis doesn't move under the cursor. <br>• Editor input is locked while hovering or dragging, and a value label shows while active. <br>• Changes go through `SetDesign`, so the two-speed pipeline, symmetry and undo apply. <br>• Tested by dragging along rays through known points: exit ×1.5 gives area ratio ×2.250, chamber ×1.2 gives thrust ×1.440, and the bell length lands at 0.900 exactly. |

## Designer window rework (after user feedback)

- **Fixed the window that grew forever.** The scroll view's height was derived from the window height, and `GUILayout.Window` grew the window to fit it every frame. The window is now a fixed height (min(760, screen − 100) px), kept on screen, and the content scrolls inside it. Test: the height is unchanged after 60 frames.
- **Closing:** a ✕ in the title bar, the Close button, Esc (when not typing), and the part-menu "Engine designer" button, which now toggles. The window also closes when the part is removed. Test: toggle closes, toggle reopens, Close() closes.
- **Layout:**
  - Sections: Propellant and cycle, Chamber, Nozzle, Performance, Isp plot, Messages.
  - Every slider has a typed number field next to it, applied as you type and clamped to the allowed range.
  - Tooltips on everything; locked choices explain why they're locked.
  - Two-column performance readout, adding sea-level TWR, exit pressure and cost.
  - A status badge: OK / warnings / not launchable.
  - Footer always visible: Save to library, Close, and the tooltip or tip line.
- **Isp plot:** scaled to the curve's range (it used to start at 0 Isp, so the curve looked flat), with Isp labels top and bottom, pressure labels, and a sea-level line. Only redrawn when the stats change.
- Screenshots: `shots/sandbox_designer.png`, `shots/sandbox_designer_scrolled.png`. Sandbox run: 24/24.

## Engine detail pass (code-only geometry, textures, glow)

First step of the model-improvement plan. Everything is still generated in code; authored Blender pieces come next.

- **Geometry (`EngineGeometry.cs`):**
  - **Top of the engine:** the mount plate now has a bolted rim and an open underside. Below it, an open thrust-frame bay holds a **gimbal block**: bolted flanges, neck and spherical bearing. The engine is about 0.24 × the chamber radius taller; stats lengths are unchanged.
  - **Injector dome:** now domed, with a **bolted injector flange** where it meets the chamber.
  - **Chamber:** two **stiffener bands** on long chambers.
- **Bell:**
  - **Hat bands** (stiffening rings) on long regen sections.
  - The **coolant manifold** is now part of the gimballed bell, so it moves with it. It sits at the joint, or just above the exit on an all-regen bell.
  - A **bolted joint flange** between the regen section and the radiative extension.
  - A thin **exit ring** on extensions.
  - The **inside of the extension** is now the extension material, so it glows with it. The regen inside stays sooty.
  - Bolts are a texture: one bolt head per U repeat on flange rims, ~1.6× as wide as high (bolt count from the rim circumference).
- **Mesh (`MeshBuilder`):**
  - Lathe and primitives share one builder with explicit tangents (+U, w = −1). The normal-map convention is documented in `EngineMaterials`: bump nx = +(u−uc), ny = −(v−vc).
  - By this convention the old tube normal map had nx reversed, which would light the tubes as grooves. Fixed by derivation, not measured in game.
- **Kit (`KitBuilder.cs`):**
  - **Lines:** bolted end flanges, and **bellows** with collars where lines cross onto the gimballed bell and on the pump inlet ducts, which now run up to the tank interface. Liquid-hydrogen lines wear gold **MLI foil**.
  - **Actuators** have a clevis, gland and rod-end bearing.
  - **Strut feet**, a pump housing flange, COPV mounting bands, and valves with actuator bosses.
  - The **GG exhaust duct** uses heat-tinted steel and glows dull red while running.
- **Textures (`EngineMaterials.cs`), still generated in code:**
  - **Regen tubes:** 8 tubes per tile with per-tube shade variation and braze fillets. Heat tint runs along the tubes: bare metal → straw → bronze → purple → blue toward the throat. Kerolox engines are tinted more copper.
  - **Steel:** brushed and mottled.
  - **Bolt heads:** separate albedo and normal maps for flange rims.
  - **Nozzle inside:** soot tinted by propellant (heavy for kerosene, light for methane, clean metal for hydrogen).
  - **Niobium:** with its silicide coating.
  - **Carbon-carbon:** 2/2 twill at a fixed 0.4 m tile, so the weave no longer scales with the engine.
  - **Foil and heat-tinted duct steel.**
  - Tileable value noise. All textures together take well under 1 MB.
- **Glow (`ModuleProcEngine.UpdateGlow`):**
  - **Along the extension:** the extension material's `_Emissive` is a ramp, brightest and yellowest at the joint, dimmer and redder toward the exit. A per-engine `_Emissive_ST` in the per-material block stretches the ramp over the extension's length (the KSP emissive shader honours it, checked in game).
  - **Brightness curve** retuned so hot metal reads in daylight without washing out.
  - **GG duct:** glows at ~1100 K at full throttle.
  - **When cooled:** the per-material blocks are cleared, so KSP's renderer-wide block (part highlight) applies again.
- **Tests:** the `visual` script adds a close-up (camera on the bell) and an **after-shutdown** shot: plume out, metal still hot. It also checks the emissive ST. Shots are `visual_detail_*` and `visual_afterglow_*`.

## Blender pipeline and the first authored piece (turbopumps)

Step 2 of the model-improvement plan.

- **`tools/blender/build_kit.py`** runs headless in Blender 5.2, in about 5 s at 32 samples:
  - Models each piece with bmesh, from lathe profiles, swept tubes, scroll volutes, bolt rings and ribs. Edges sharper than 32° are marked sharp, so the exported split normals keep hard edges.
  - Places **anchors** as empties named `<piece>.<anchor>`, with +Z as the direction.
  - Smart-UV-projects all pieces into **one shared atlas**.
  - Bakes it with Cycles, once for base colour (noise-mottled per material) and once for ambient occlusion. The atlas is base colour × (0.3 + 0.7·AO), 1024².
  - Exports each piece to a small binary **`.pem`** file: positions, normals, UVs, indices and anchors, already in Unity axes and winding.
  - Saves `kit.blend`. `--export-only` re-bakes and exports from a hand-edited `kit.blend`.
- **Pieces:**
  - **`turbopump_dual`** (GG, staged combustion, expander): single shaft. From top to bottom: bolted axial ox inlet, inducer flare, ox pump with scroll volute discharging tangentially, ribbed inter-stage seal, fuel pump with its own volute and an inlet elbow turning up, bearing neck, then the turbine with a bronze toroidal hot-gas manifold, a side inlet and a bolted exhaust flange. Anchors: `inlet`, `fuel_in`, `ox_out`, `fuel_out`, `turbine_in`, `turbine_exhaust`. 9.4k vertices.
  - **`turbopump_single`** (full-flow, one propellant per pump): the same without the fuel stage. 6.5k vertices.
- **Plugin (`KitAssets.cs`):** loads the `.pem` files and atlas from `GameData/ProcEngines/PluginData/Kit`. PluginData is outside KSP's GameDatabase, so the atlas isn't also loaded as a game texture.
  - The atlas is compressed to **DXT1** (~0.7 MB with mips) and drops its CPU copy.
  - Missing or broken files fall back to the primitive pump.
- **`KitBuilder`:**
  - Places the piece with a rotation basis: +X radially out, +Y up, +Z tangential.
  - All plumbing now routes from the pump's anchors: ox line to the dome, fuel line to the manifold, GG on the turbine inlet, GG exhaust duct from the turbine exhaust, expander hot hydrogen into the turbine inlet. Two inlet ducts (ox and fuel), each with bellows, run up to the tank interface.
  - **Staged-combustion pumps hang upside down**, turbine end up, with the preburner sitting on the turbine, as on the RS-25.
  - The kit's 6th material is the atlas.
- **Tests:** `visual.kit_assets` checks that both pieces and their anchors load and that the atlas is DXT1. A second close-up (`visual_detail2_*`) shows the pump side, which is in shadow in that view. Visual 7/7, sandbox 24/24.
- **Not done yet:**
  - The other authored pieces: preburner, gas generator, valves, actuators, injector head.
  - A baked normal map: the atlas uses a flat normal.
  - Levels of detail: the pumps add about 9–13k kit vertices per engine.

## Evidence

[tests/ksp/evidence/phase3/](tests/ksp/evidence/phase3/): results files, and screenshots:

- `visual_cold_*` / `visual_hot_*`: six designs cold and burning;
- `plumes_*`: the calibration rig at scale 1;
- `sandbox_handles.png`.

New driver scripts in `tests/ProcEngines.KspTests/Driver.cs`:

- **`visual`:** freezes the craft 15 m above the pad, hides the UI, aims the camera at the engine, and turns on infinite propellant, because stock tanks carry no methane or hydrogen.
- **`plumes`:** the calibration rig.

## Not done (Phase 3 leftovers)

1. **Authored kit pieces with template-vertex deformation** (DESIGN §8.5): this needs Blender assets. The primitive kit covers the MVP.
2. **TexturesUnlimited PBR path and LOD:** optional.
3. **The designer is still IMGUI.** The UGUI window from DESIGN §7.1 hasn't been started.
4. **Visual tuning:**
   - The C-C glow is fairly subtle in daylight. The brightness factors are in `EngineMaterials.Glow`.
   - The GG exhaust plume is small; its reference sizes were judged visually.
   - On large engines, the kit pieces sit just below the tank and can be hard to see from above.
5. **Not installed in ksp_mp yet**, and nothing changed there.
