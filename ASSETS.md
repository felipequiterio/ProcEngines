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
- `MeshBuilder.Lathe` and `MeshBuilder.Sweep`: tangent = +U direction, and w is chosen per segment or ring so that `cross(N, T) * w` points toward +V.
  - Lathe: w = sign(UTiles) × (Inward ? −1 : +1) × sign(VLength). That is +1 on an outward surface whose V grows along the profile, and −1 where V runs backwards (the convergent, VLength = −2·arc; the inner radiative wall) or on inward walls.
  - Sweep: w = sign(dot(a × b, path direction)). That is +1 for tubes and −1 for `Torus`, whose frame runs against its path.
  - Never force w = +1 everywhere: V relief would invert on the convergent, the inner extension and every torus.
  - The code-generated fallback normal maps use the same green = +V rule.
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
- **Export check:** `build_kit.py` fails the export when the tangent frames disagree with dP/du and dP/dv. Run `tools/blender/check_kit.py` on the shipped files after any rebuild.
- **Vertex budget at LOD0:**
  - turbopump, including the `_inv` variants: ≤ 45k triangles;
  - preburner, gas generator: ≤ 15k triangles;
  - valve, actuator, dome inlet: ≤ 8k triangles;
  - injector ring: ≤ 12k triangles.

## 4. Kit pieces and atlas

Two atlas files:
- `Kit/kit_atlas.png`: RGBA 2048². RGB = base colour with wear × ambient occlusion; A = gloss.
- `Kit/kit_atlas_n.png`: RGB 2048², tangent normal map (bevels, cast/machined texture, weld beads, bolt detail).

How the atlas is built:
- **Padding:** empty texels are filled by push-pull from the islands (colour, gloss and normal), so mips never darken toward black or full gloss. The bake margin is 1 px; islands are 3 px apart.
- **Partial builds:** `--only`, or a fresh build with `--no-bake`, refuses to write into the shipped directory; give `--out`. `--only` defaults to a scratch directory, which it clears first. An `--export-only --only X --no-bake` run into the shipped kit is allowed on purpose, because it keeps kit.blend's UVs.
- **Atomic writes:** every piece's streams are checked before anything is written, and the files are staged before they replace the shipped ones.
- **Reproducible tangents:** export with `blender -b -t 1 ...` (single-threaded MikkTSpace) so the tangents are byte-stable.
- **`_inv` variants:** they follow their base piece. An export-only run fails if a variant has drifted after hand edits; rerun with `--unwrap`.

Pieces, units and anchors. **These must not change meaning.** Positions may move a little with better geometry, but each envelope stays within ±10 % of v1, because `KitBuilder` places pieces relative to these numbers.

| Piece | Unit | Anchors | Envelope (v1, piece units) |
|---|---|---|---|
| `turbopump_dual` | pump reference radius | `inlet` (top, +Z), `fuel_in` (+Z), `ox_out` (tangent +Y side), `fuel_out` (−Y side), `turbine_in` (radial at 30°), `turbine_exhaust` (bottom, −Z) | z −1.36 … +1.16, r ≲ 1.35 |
| `turbopump_single` | pump radius | `inlet`, `out`, `turbine_in`, `turbine_exhaust` | z −0.98 … +1.16 |
| `turbopump_dual_inv`, `turbopump_single_inv` | as the base piece | identical to the base piece | Same triangles and anchors as the base, with their own UVs, baked hanging upside down so drips and dust fall the right way. `KitBuilder` uses them for the flipped staged-combustion and full-flow pumps, falling back to the base piece. |
| `preburner` | pump radius | `hot_out` (−X, toward the engine axis; outlet radius 0.30, flange r 0.40), `ox_feed`, `fuel_feed` | z 0 … 1.25 (base flange at z = 0), r ≤ 0.8 |
| `gas_generator` | pump radius | `out` (origin, −X), `ox_feed`, `fuel_feed` | body along +X, 0 … 0.70 |
| `valve` | line radius | `in` (−Z), `out` (+Z) | z −2.2 … 2.2, flow along Z, actuator on +X |
| `actuator` | barrel radius | `top` (0), `end` (z −12), `zone0_hi`/`zone0_lo` (barrel, −2.6/−6.1), `zone1_hi`/`zone1_lo` (rod, −7.3/−10.9) | z 0 … −12 |
| `dome_inlet` | line radius | `in` at (1.95, 1.6, 0) Unity, i.e. the elbow end pointing +X | local +Z = dome normal; boss r ≈ 1.52 with its top face at 0.42; top of the piece ≤ 2.9. `KitBuilder` moves it outward on the dome until nothing pokes above the mount plate. |
| `injector_ring` | chamber radius | (none) | torus R 1.12 at z −0.17 |

- **Stretch zones (actuator):** `MeshBuilder.AddMesh(..., length)` stretches the vertices between `zoneN_hi` and `zoneN_lo` along y. Keep only plain barrel or rod surface inside a zone: no bolts, rings or blocks, or they get stretched too.
- **Atlas share:** each piece's UVs are weighted by its `pe_texel` property before packing: 0.8 for pump-unit pieces and 0.9 for the ring.
- **Kit position rule:** the whole kit stays below the mount plate (y < 0) and above the exit plane.
- **Bell kit:** parts rigidly fixed to the gimballed bell go into a second kit mesh, rendered as `peKitBell` / `peKitBellLod1` under the bell, in the bell frame (part frame minus (0, YThroat, 0)). These are the actuator lugs and pins, the fuel line below its lower bellows, and the expander hot line from the manifold to its bellows.
- **Static clearance:** static kit keeps the bell's 10° swept envelope plus 4 mm + 1.5 % rc.
- **Splines:** lines use centripetal Catmull-Rom with straight legs and 2.5-radius bends, so they never overshoot.
- **Thrust frame:** 4 struts, or 3 if the dome is crowded, laid out from the free arcs.

## 5. Surface textures

Directory: `GameData/ProcEngines/PluginData/Surfaces/`. Each surface has `<name>.png` (RGBA albedo + gloss) and `<name>_n.png` (normal).
- `build_surfaces.py` fails the build on a seam-check breach. The check compares the wrap edge against interior neighbours and also catches height steps shared by both edge columns.
- Every texture tiles seamlessly in U.
- V wraps unless marked clamp.
- How the geometry maps UVs is fixed (`EngineGeometry` / `MeshBuilder`), as listed below.

| Name | Size (albedo / normal) | U | V | Notes |
|---|---|---|---|---|
| `regen` | **2048×1024** / 2048×1024 | **32 cooling tubes per tile** (`EngineMaterials.TubesPerTile = 32`, 64 px per tube); each tube centred in its 1/32 of U, braze fillets at the boundaries | **Heat coordinate, clamp.** 0 = throat (hottest), 1 = coolest. The chamber runs 1.0 at the injector flange → 0.5 at the convergent start, the convergent 0.5 → 0, the bell 0 → 1 at the end of the regen section. | Tube-wall engines. The propellant tint multiplies it (kerolox dark bronze, methalox warm grey, hydrolox near neutral). Heat tint (straw → bronze → purple → blue → grey) in a streaky band near V ≈ 0. Per-tube shade variation, dents, braze discoloration, grime and streaks along the tubes. Gloss: tube crowns shiny, grooves and fillets matte. A tube mip bias of 0.5 is applied at load. |
| `regen_k` | 2048×1024 / (uses `regen_n`) | as `regen` | as `regen` | Kerolox tube-wall bells **without** a radiative extension (V = 1 is the real exit there): `regen` plus fired exterior soot from V ≈ 0.8, dense above ≈ 0.9. Falls back to `regen`. |
| `jacket` | 2048×1024 / 2048×1024 | 32 lands per tile, as `regen` | as `regen` | Channel-wall engines (`EngineGeometry.ChannelWall`: staged combustion, full flow, Late-tech gas generators; for hydrolox, `ChannelNozzle` keeps a tube bell). Smooth brazed jacket, about 0.43 sRGB neutral grey before the tint. Faint land ribbing, a few longitudinal seam welds, the same heat gradient, gloss 0.3–0.45. |
| `steel` | 1024² / 512² | Tile = `EngineMaterials.SteelTile` = 0.6 m square | Same, wrap | Chamber jacket, mount, flange faces, bands, gimbal block. Worn stainless/Inconel: fine brushing, mottling, handling marks, faint grime streaks running +V (down the profile on outer walls), a few scratches. No strong periodic features, because it tiles. |
| `bolts` | 256×160 / 256×160 | One bolt per tile | 0 = top edge of the rim → 1 = bottom (clamp) | Hex head with washer, centred. The tile is 1.6× as wide as high. Flange metal background with edge wear. |
| `soot` | 512² / 512² | 8 tiles around | 1 tile = max(0.3, exit radius) m, running from the exit toward the throat | Inside of the nozzle, near greyscale (a per-propellant tint is applied). Streaks along V, flaking deposits, gloss low. |
| `niobium` | 1024² / 1024² | Tile = `EngineMaterials.RadiativeTile` = **0.8 m** | Same | Radiative extension at the Early/Mid tech level: C103 with silicide coating. Matte grey-violet, mottled, faint flow streaks along V, small coating chips. |
| `carbon` | 1024² / 1024² | Tile 0.8 m | Same | Carbon-carbon (Late tech): 2/2 twill, **64 tows per tile** (`EngineMaterials.CarbonTowsPerTile`). The relief lives in the normal map, with muted albedo and gloss contrast, resin-rich patches and an oxidised haze. |
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
| LOD0 | LOD0 renderers | 0.25 |
| LOD1 | LOD1 renderers | 0.03 |
| LOD2 | `peChamberLod1` + `peBellLod1` only (the kit is culled) | 0, so the engine never disappears |

These are raw `LOD.screenRelativeTransitionHeight` values. KSP's `QualitySettings.lodBias` of 2 halves them in effect.

**Rebuilds:**
- **Editor, during a change stream** (changes less than 0.5 s apart): LOD1 is rebuilt at most at 10 Hz and forced (`ForceLOD(1)`). The slow path then builds only LOD0, followed by `ForceLOD(-1)` and `RecalculateBounds()`.
- **Editor, one-off change:** both levels are built together 0.15 s later, with no forced LOD.
- **Flight:** `OnStart` builds LOD1 and points the LOD0 filters at it. `Lod0Pending` is true until a static queue builds LOD0, about one engine build per frame (cache hits are instant). It then forces `ForceLOD(-1)`.
- **Prefab:** builds LOD1 only; the LOD0 filters show the LOD1 meshes.

**Shared mesh cache:**
- Render meshes are shared and reference-counted. The key is the serialized DESIGN + STATS, the LOD level, a mesh version and the scope (editor or flight).
- Identical engines (symmetry, clusters, LMP clients) build once. A design change takes a new entry; a shared mesh is never rebuilt in place.
- Entries are destroyed when superseded, 5 s after their last part is destroyed, or on scene load.
- Collider meshes stay per part.
- **Flight CPU copies:** in flight, meshes drop their CPU copy (`UploadMeshData(true)`) unless a mod that reads part meshes is loaded. The decision is logged.

**FAR:** `Patches/FAR.cfg` sets `forceUseColliders = True`, so FAR voxelises the collider hulls and never the render meshes.
- **Glow blocks:** applied to both bell renderers (`MatRadiative`) and both kit renderers (`MatHot`).
- **Materials:** applied to both levels.
- **Renderer creation:** the LOD1 renderers are created in `EnsureTransforms`, so the prefab and its clones have them. Clones take their meshes from the cache.
- **Bell kit renderers:** `peKitBell` (LOD0) and `peKitBellLod1` (LOD1) live under the bell renderers. They are part of every cache entry and get the kit materials and the `MatHot` glow.
- **Foreign mesh copies:** another mod's `MeshFilter.mesh` read (KSPCF's drag-cube patch, Firefly, ConformalDecals) silently copies a shared mesh.
  - `NotifyAero` restores the filters right after the stock or KSPCF drag-cube render.
  - In flight, each part restores its filters about every second and on vessel modification.
  - Copies named " Instance" are destroyed.
- **Texture warm-up:** all surface textures load during LOADING, so the first channel-wall or Late engine doesn't stall a frame.
