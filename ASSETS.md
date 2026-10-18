# ProcEngines asset contract (v2)

This file defines how the art pipeline and the plugin fit together:
- `tools/blender/build_kit.py` writes the kit pieces and their atlas.
- `tools/blender/build_surfaces.py` writes the tiling surface textures.
- The plugin reads both: `KitAssets`, `MeshBuilder`, `EngineMaterials`, `EngineGeometry` and `KitBuilder`.

Anything written by one side and read by the other is specified here. If code and this file disagree, fix one of them.

Visual target: the realism of current KSP part mods (Bluedog Design Bureau, Tantares, Artemis Construction Kit). That means hardware that has been **built, handled and test-fired**: it has bevelled edges that catch highlights, cast and machined surface texture, heat discoloration, soot, grime in crevices, worn edges and gloss variation. It should not look like clean CAD.

## 1. Who owns what (parallel work)

| Owner | Files |
|---|---|
| KIT (Blender pieces) | `tools/blender/build_kit.py`, `tools/blender/preview.py`, `tools/blender/kit.blend`, `GameData/ProcEngines/PluginData/Kit/*` |
| SURF (surface textures) | `tools/blender/build_surfaces.py` (new), `GameData/ProcEngines/PluginData/Surfaces/*` |
| GEO (C# geometry, LOD) | `src/ProcEngines/EngineGeometry.cs`, `src/ProcEngines/KitBuilder.cs`, `src/ProcEngines/ModuleProcEngine.cs` |
| MAT (C# loading, materials, mesh building) | `src/ProcEngines/MeshBuilder.cs`, `src/ProcEngines/KitAssets.cs`, `src/ProcEngines/EngineMaterials.cs` |

Nobody else edits tests, docs or the model project.

**C# builds:** GEO and MAT build the same project. Serialise builds with an atomic lock directory:
1. `mkdir` the lock `C:/Users/Felipe/AppData/Local/Temp/claude/E--Kerbal-Space-Program-Instances-ksp-mp/4f69e8c5-8331-47ff-9795-6f2e0e2043a9/scratchpad/build.lock`. If it exists, wait and retry.
2. Run `dotnet build -c Release tests/ProcEngines.KspTests`.
3. `rmdir` the lock.

If errors appear only in the other owner's files, they are mid-edit: wait and rebuild.

## 2. Conventions

**Tangent frame and normal maps:**
- Unity computes `binormal = cross(normal, tangent.xyz) * tangent.w`.
- Every normal map uses the **OpenGL convention: green = +V**, the direction of increasing texture v. Every mesh must provide tangents whose binormal points toward +V.
- `MeshBuilder.Lathe` and `MeshBuilder.Sweep`: tangent = +U direction, **w = +1** (it was −1). There, `cross(N, T)` is the +V direction (along the profile or along the path). MAT also flips the green sign of every code-generated fallback normal map, so bumps stay bumps.
- Authored `.pem` v2: Blender MikkTSpace tangents (`mesh.calc_tangents()` on the atlas UV map), converted to Unity axes `(x, z, y)` with **w = −bitangent_sign**. The y/z swap mirrors the space, so the bitangent sign flips.
- A bump in a normal map: `nx = +(u − uc)`, `ny = +(v − vc)` toward its rim (both channels point away from the bump centre).

**Gloss:**
- `KSP/Bumped Specular` takes its specular mask from `_MainTex` **alpha**. Every albedo texture carries **gloss in A**: 0 = matte, 1 = full specular.
- RGB is sRGB albedo.

**Normal-map PNGs:**
- RGB = n·0.5 + 0.5 (tangent space, OpenGL), 8-bit, no alpha needed.
- The loader swizzles to Unity's DXT5nm layout (x into alpha, y in green) and compresses.

**Loading:**
- Everything lives under `GameData/ProcEngines/PluginData/`, which KSP's GameDatabase never touches. The plugin reads the files itself.
- Albedo+gloss is compressed to DXT5 and normals to DXT5 (after the swizzle). Both drop their CPU copy.
- **A missing file is never an error:** the code-generated fallback is used.

**Units and axes of authored pieces (unchanged from v1):**
- Blender +Z = Unity +Y = up; Blender +X = radially outward from the engine axis; Blender +Y = Unity +Z = tangential.
- Each piece has its own unit (table in §4). Anchors are empties named `<piece>.<anchor>`; their local +Z is the anchor direction.

## 3. `.pem` mesh files

All values little-endian:

```
bytes[4]  "PEM1"
int32     version            1 or 2
int32     vertexCount V
int32     indexCount I
float32[3V] positions        Unity axes (x, z_blender, y_blender), piece units
float32[3V] normals
float32[4V] tangents         VERSION 2 ONLY: xyz in Unity axes, w = -blender_bitangent_sign (±1)
float32[2V] uvs              atlas UVs, v up (Blender convention, same as Unity)
int32[I]  indices            triangles, Unity winding (Blender order reversed)
int32     anchorCount
  per anchor: int32 nameLength, utf-8 name, float32[3] position, float32[3] direction (Unity axes)
```

- Version 1 files (no tangents) must still load; the plugin then makes its own tangents.
- **LOD files:** `<piece>.lod1.pem` has the same format, **the same atlas UVs**, the same anchors (copied from LOD0), and at most ~30 % of the LOD0 triangles (Blender Decimate collapse on the final LOD0 mesh).
- **Vertex budget at LOD0:**
  - turbopump: ≤ 45k triangles;
  - preburner, gas generator: ≤ 15k triangles;
  - valve, actuator, dome inlet: ≤ 8k triangles;
  - injector ring: ≤ 12k triangles.

## 4. Kit pieces and atlas

Two atlas files:
- `Kit/kit_atlas.png`: RGBA 2048². RGB = base colour with wear × ambient occlusion; A = gloss.
- `Kit/kit_atlas_n.png`: RGB 2048², tangent normal map (bevels, cast/machined texture, weld beads, bolt detail).

Pieces, units and anchors. **These must not change meaning.** Positions may move a little with better geometry, but each envelope stays within ±10 % of v1, because `KitBuilder` places pieces relative to these numbers.

| Piece | Unit | Anchors | Envelope (v1, piece units) |
|---|---|---|---|
| `turbopump_dual` | pump reference radius | `inlet` (top, +Z), `fuel_in` (+Z), `ox_out` (tangent +Y side), `fuel_out` (−Y side), `turbine_in` (radial at 30°), `turbine_exhaust` (bottom, −Z) | z −1.36 … +1.16, r ≲ 1.35 |
| `turbopump_single` | pump radius | `inlet`, `out`, `turbine_in`, `turbine_exhaust` | z −0.98 … +1.16 |
| `preburner` | pump radius | `hot_out` (−X, toward the engine axis), `ox_feed`, `fuel_feed` | z 0 … 1.06 (base flange at z = 0), r ≤ 0.8 |
| `gas_generator` | pump radius | `out` (origin, −X), `ox_feed`, `fuel_feed` | body along +X, 0 … 0.70 |
| `valve` | line radius | `in` (−Z), `out` (+Z) | z −2.2 … 2.2, flow along Z, actuator on +X |
| `actuator` | barrel radius | `top` (0), `end` (z −12), `zone0_hi`/`zone0_lo` (barrel, −2.6/−6.1), `zone1_hi`/`zone1_lo` (rod, −7.3/−10.9) | z 0 … −12 |
| `dome_inlet` | line radius | `in` (elbow end, +X) | local +Z = dome normal; boss ≈ r 2.05 |
| `injector_ring` | chamber radius | (none) | torus R 1.12 at z −0.17 |

- **Stretch zones (actuator):** `MeshBuilder.AddMesh(..., length)` stretches the vertices between `zoneN_hi` and `zoneN_lo` along y. Keep only plain barrel or rod surface inside a zone: no bolts, rings or blocks, or they get stretched too.
- **Atlas share:** each piece's UVs are weighted by its `pe_texel` property before packing.

## 5. Surface textures

Directory: `GameData/ProcEngines/PluginData/Surfaces/`. Each surface has `<name>.png` (RGBA albedo + gloss) and `<name>_n.png` (normal).
- Every texture tiles seamlessly in U.
- V wraps unless marked clamp.
- How the geometry maps UVs is fixed (`EngineGeometry` / `MeshBuilder`), as listed below.

| Name | Size (albedo / normal) | U | V | Notes |
|---|---|---|---|---|
| `regen` | 1024² / 1024² | **16 cooling tubes per tile** (`EngineMaterials.TubesPerTile = 16`); each tube centred in its 1/16 of U, braze fillets at the boundaries | **Heat coordinate, clamp.** 0 = throat (hottest), 1 = coolest, at the end of the regen section. The convergent runs 0.5 → 0. | The propellant tint multiplies it (kerolox copper, methalox warm, hydrolox none), so the base is warm metallic grey. Heat tint (straw → bronze → purple → blue → grey) near V ≈ 0, irregular per tube. Per-tube shade variation, small dents, braze discoloration, grime and streaks along the tubes. Gloss: tube crowns shiny, grooves and fillets matte. |
| `steel` | 1024² / 512² | Tile = `EngineMaterials.SteelTile` = 0.6 m square | Same, wrap | Chamber jacket, mount, flange faces, bands, gimbal block. Worn stainless/Inconel: fine brushing, mottling, handling marks, faint grime streaks running +V (down the profile on outer walls), a few scratches. No strong periodic features, because it tiles. |
| `bolts` | 256×160 / 256×160 | One bolt per tile | 0 = top edge of the rim → 1 = bottom (clamp) | Hex head with washer, centred. The tile is 1.6× as wide as high. Flange metal background with edge wear. |
| `soot` | 512² / 512² | 8 tiles around | 1 tile = max(0.3, exit radius) m, running from the exit toward the throat | Inside of the nozzle, near greyscale (a per-propellant tint is applied). Streaks along V, flaking deposits, gloss low. |
| `niobium` | 1024² / 1024² | Tile = `EngineMaterials.RadiativeTile` = **0.8 m** | Same | Radiative extension at the Early/Mid tech level: C103 with silicide coating. Matte grey-violet, mottled, faint flow streaks along V, small coating chips. |
| `carbon` | 1024² / 1024² | Tile 0.8 m | Same | Carbon-carbon (Late tech): 2/2 twill, **32 tows per tile**, fibre sheen, resin-rich patches, slight gloss. |
| `foil` | 512² / 512² | Kit pipes: U = 0…1 around | v = arc length / circumference (square texels) | Gold MLI on liquid-hydrogen lines: crinkle, overlaps, tape wraps across U (bands of constant V), seams along V. |
| `hotduct` | 512² / 512² | As foil | As foil | Uncooled turbine exhaust duct: heat-tinted Inconel, mottled straw/blue/purple/brown, soot. |
| `lines` | 512² / 512² | As foil | As foil | Stainless propellant lines and kit structure: longitudinal brushing along V, handling marks, grime. |

The emissive glow ramp stays generated in code.

## 6. Levels of detail (GEO)

Each engine has two sets of renderers under one `LODGroup` on `peRoot`:
- **LOD0** keeps the old names: `peChamber`, `peGimbal/peBell`, `peKit`. These are full resolution with the LOD0 pieces.
- **LOD1** is new: `peChamberLod1`, `peGimbal/peBellLod1`, `peKitLod1`. These use about a third of the ring segments, decimated profiles, `.lod1.pem` pieces and fewer primitive sides.

The LOD levels and their thresholds:

| Level | Shows | Applies above this screen-relative height |
|---|---|---|
| LOD0 | LOD0 renderers | 0.12 |
| LOD1 | LOD1 renderers | 0.015 |
| LOD2 | `peChamberLod1` + `peBellLod1` only (the kit is culled) | 0, so the engine never disappears |

- **Rebuilds:**
  - The 10 Hz drag path rebuilds only LOD1 and forces it (`ForceLOD(1)`).
  - The slow path (on release + 0.5 s), `OnStart` and the prefab build everything, then `ForceLOD(-1)` and `RecalculateBounds()`.
- **Glow blocks:** applied to both bell renderers (`MatRadiative`) and both kit renderers (`MatHot`).
- **Materials:** applied to both levels.
- **Renderer creation:** the LOD1 renderers are created in `EnsureTransforms`, so the prefab and its clones have them. Clones get fresh meshes (`ownMeshes`).
