using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace ProcEngines
{
    /// <summary>An authored kit piece (tools/blender/build_kit.py): mesh in piece units, Unity axes (+X radially
    /// outward from the engine axis, +Y up, +Z tangential), UVs into the shared kit atlas, named anchors.</summary>
    public sealed class KitMesh
    {
        public string Name;
        public int Version;
        public Vector3[] Positions, Normals;
        /// <summary>Per-vertex tangents from the file (.pem v2: Blender MikkTSpace, w = binormal sign, binormal
        /// toward +V); null for a version 1 file. <see cref="TangentsOrFallback"/> always returns a usable set.</summary>
        public Vector4[] Tangents;
        public Vector2[] Uvs;
        public int[] Indices;
        public readonly Dictionary<string, KeyValuePair<Vector3, Vector3>> Anchors = new Dictionary<string, KeyValuePair<Vector3, Vector3>>();
        Vector4[] generatedTangents;

        public bool TryAnchor(string name, out Vector3 position, out Vector3 direction)
        {
            if (Anchors.TryGetValue(name, out var a)) { position = a.Key; direction = a.Value; return true; }
            position = direction = Vector3.zero;
            return false;
        }

        /// <summary>The triangles whose three vertices all pass `inside` (`keepInside` true), or all the other
        /// triangles (false), as a piece of its own with the same anchors and only the vertices it uses (normals,
        /// tangents and UVs copied). Vertices on the cut go into both halves. Null when no triangle qualifies.</summary>
        public KitMesh Subset(Func<Vector3, bool> inside, bool keepInside, string suffix)
        {
            var map = new int[Positions.Length];
            for (int i = 0; i < map.Length; i++) map[i] = -1;
            bool[] at = new bool[Positions.Length];
            for (int i = 0; i < at.Length; i++) at[i] = inside(Positions[i]);
            var idx = new List<int>(Indices.Length);
            var used = new List<int>(Positions.Length);
            for (int k = 0; k + 2 < Indices.Length; k += 3)
            {
                bool all = at[Indices[k]] && at[Indices[k + 1]] && at[Indices[k + 2]];
                if (all != keepInside) continue;
                for (int j = 0; j < 3; j++)
                {
                    int v = Indices[k + j];
                    if (map[v] < 0) { map[v] = used.Count; used.Add(v); }
                    idx.Add(map[v]);
                }
            }
            if (idx.Count == 0) return null;
            Vector4[] T = TangentsOrFallback();
            var m = new KitMesh
            {
                Name = Name + suffix, Version = 2, Positions = new Vector3[used.Count], Normals = new Vector3[used.Count],
                Tangents = new Vector4[used.Count], Uvs = new Vector2[used.Count], Indices = idx.ToArray(),
            };
            for (int i = 0; i < used.Count; i++)
            {
                int v = used[i];
                m.Positions[i] = Positions[v]; m.Normals[i] = Normals[v]; m.Tangents[i] = T[v]; m.Uvs[i] = Uvs[v];
            }
            foreach (var a in Anchors) m.Anchors[a.Key] = a.Value;
            return m;
        }

        /// <summary>The file's tangents, or (version 1) tangents derived once from the UVs: per-triangle dP/dU,
        /// area-weighted, orthogonalised against the normal, with w chosen so the binormal follows +V (ASSETS.md 2).</summary>
        public Vector4[] TangentsOrFallback()
        {
            if (Tangents != null) return Tangents;
            if (generatedTangents != null) return generatedTangents;
            int nv = Positions.Length;
            var su = new Vector3[nv];
            var sv = new Vector3[nv];
            for (int k = 0; k + 2 < Indices.Length; k += 3)
            {
                int a = Indices[k], b = Indices[k + 1], c = Indices[k + 2];
                Vector3 e1 = Positions[b] - Positions[a], e2 = Positions[c] - Positions[a];
                float du1 = Uvs[b].x - Uvs[a].x, dv1 = Uvs[b].y - Uvs[a].y, du2 = Uvs[c].x - Uvs[a].x, dv2 = Uvs[c].y - Uvs[a].y;
                float det = du1 * dv2 - du2 * dv1;
                if (Mathf.Abs(det) < 1e-14f) continue;
                float area = Vector3.Cross(e1, e2).magnitude;
                Vector3 tu = (e1 * dv2 - e2 * dv1) / det, tv = (e2 * du1 - e1 * du2) / det;
                float lu = tu.magnitude, lv = tv.magnitude;
                if (lu < 1e-20f || lv < 1e-20f) continue;
                tu *= area / lu; tv *= area / lv;
                su[a] += tu; su[b] += tu; su[c] += tu;
                sv[a] += tv; sv[b] += tv; sv[c] += tv;
            }
            var res = new Vector4[nv];
            for (int i = 0; i < nv; i++)
            {
                Vector3 nn = Normals[i];
                Vector3 t = su[i] - nn * Vector3.Dot(nn, su[i]);
                if (t.sqrMagnitude < 1e-20f) t = Vector3.Cross(nn, Mathf.Abs(nn.y) < 0.95f ? Vector3.up : Vector3.right);
                t.Normalize();
                float w = Vector3.Dot(Vector3.Cross(nn, t), sv[i]) < 0 ? -1 : 1;
                res[i] = new Vector4(t.x, t.y, t.z, w);
            }
            return generatedTangents = res;
        }
    }

    /// <summary>
    /// Loads the authored kit pieces and their atlas from GameData/ProcEngines/PluginData/Kit (PluginData, so KSP's
    /// GameDatabase doesn't also load the textures), and the PNG texture loader shared with EngineMaterials. Missing
    /// or broken files are never errors: callers fall back to primitives or code-generated textures.
    /// </summary>
    public static class KitAssets
    {
        static readonly Dictionary<string, KitMesh> meshes = new Dictionary<string, KitMesh>();
        static Texture2D atlas, atlasNormal;
        static bool atlasTried, atlasNormalTried;
        public static string Status = "not loaded";

        /// <summary>GameData/ProcEngines/PluginData.</summary>
        public static string PluginData
        {
            get
            {
                string plugins = Path.GetDirectoryName(typeof(KitAssets).Assembly.Location);
                return Path.GetFullPath(Path.Combine(plugins, "..", "PluginData"));
            }
        }

        public static string Directory => Path.Combine(PluginData, "Kit");

        /// <summary>The tiling surface textures (ASSETS.md 5).</summary>
        public static string SurfaceDirectory => Path.Combine(PluginData, "Surfaces");

        public static KitMesh Get(string name)
        {
            if (meshes.TryGetValue(name, out KitMesh m)) return m;
            m = null;
            string path = Path.Combine(Directory, name + ".pem");
            try
            {
                if (File.Exists(path)) m = Read(name, File.ReadAllBytes(path));
                Status = m != null ? $"{name}: v{m.Version}, {m.Positions.Length} verts, {m.Indices.Length / 3} tris, {m.Anchors.Count} anchors" : $"{name}: missing ({path})";
            }
            catch (Exception e)
            {
                Status = $"{name}: {e.Message}";
                Debug.LogError($"[ProcEngines] kit piece {path}: {e}");
            }
            meshes[name] = m;
            return m;
        }

        /// <summary>A level of detail of a piece (`<name>.lod1.pem`), or the full piece when there's no such file.</summary>
        public static KitMesh GetLod(string name, int lod)
        {
            if (lod <= 0) return Get(name);
            string status = Status;
            KitMesh m = Get(name + ".lod" + lod);
            if (m != null) return m;
            Status = status;   // a missing LOD file isn't news: keep the last real status
            return Get(name);
        }

        /// <summary>Parse a .pem file (ASSETS.md 3), version 1 or 2. Throws InvalidDataException on a bad file.</summary>
        public static KitMesh Read(string name, byte[] data)
        {
            if (data == null || data.Length < 16 || data[0] != 'P' || data[1] != 'E' || data[2] != 'M' || data[3] != '1')
                throw new InvalidDataException("not a PEM1 file");
            int version = BitConverter.ToInt32(data, 4), nv = BitConverter.ToInt32(data, 8), ni = BitConverter.ToInt32(data, 12);
            if (version != 1 && version != 2) throw new InvalidDataException("unsupported version " + version);
            if (nv <= 0 || ni <= 0 || ni % 3 != 0) throw new InvalidDataException($"bad counts {nv} vertices / {ni} indices");
            int floatsPerVertex = 3 + 3 + (version >= 2 ? 4 : 0) + 2;
            long need = 16L + 4L * floatsPerVertex * nv + 4L * ni + 4;
            if (data.Length < need) throw new InvalidDataException($"truncated: {data.Length} bytes, {need} needed");

            var m = new KitMesh
            {
                Name = name, Version = version, Positions = new Vector3[nv], Normals = new Vector3[nv], Uvs = new Vector2[nv], Indices = new int[ni],
                Tangents = version >= 2 ? new Vector4[nv] : null,
            };
            // bulk copies (the file is little-endian, as is every platform KSP runs on)
            var f = new float[4 * nv];
            int off = 16;
            Buffer.BlockCopy(data, off, f, 0, 12 * nv); off += 12 * nv;
            for (int i = 0, k = 0; i < nv; i++, k += 3) m.Positions[i] = new Vector3(f[k], f[k + 1], f[k + 2]);
            Buffer.BlockCopy(data, off, f, 0, 12 * nv); off += 12 * nv;
            for (int i = 0, k = 0; i < nv; i++, k += 3)
            {
                var nn = new Vector3(f[k], f[k + 1], f[k + 2]);
                float l = nn.magnitude;
                m.Normals[i] = l > 1e-12f ? nn / l : Vector3.up;
            }
            if (version >= 2)
            {
                Buffer.BlockCopy(data, off, f, 0, 16 * nv); off += 16 * nv;
                for (int i = 0, k = 0; i < nv; i++, k += 4) m.Tangents[i] = new Vector4(f[k], f[k + 1], f[k + 2], f[k + 3] < 0 ? -1 : 1);
            }
            Buffer.BlockCopy(data, off, f, 0, 8 * nv); off += 8 * nv;
            for (int i = 0, k = 0; i < nv; i++, k += 2) m.Uvs[i] = new Vector2(f[k], f[k + 1]);
            Buffer.BlockCopy(data, off, m.Indices, 0, 4 * ni); off += 4 * ni;
            for (int i = 0; i < ni; i++)
                if ((uint)m.Indices[i] >= (uint)nv) throw new InvalidDataException("index out of range");

            int na = BitConverter.ToInt32(data, off); off += 4;
            if (na < 0 || na > 1000) throw new InvalidDataException("bad anchor count " + na);
            for (int i = 0; i < na; i++)
            {
                if (off + 4 > data.Length) throw new InvalidDataException("truncated anchors");
                int len = BitConverter.ToInt32(data, off); off += 4;
                if (len < 0 || off + len + 24 > data.Length) throw new InvalidDataException("truncated anchors");
                string an = Encoding.UTF8.GetString(data, off, len); off += len;
                var p = new Vector3(BitConverter.ToSingle(data, off), BitConverter.ToSingle(data, off + 4), BitConverter.ToSingle(data, off + 8));
                var d = new Vector3(BitConverter.ToSingle(data, off + 12), BitConverter.ToSingle(data, off + 16), BitConverter.ToSingle(data, off + 20));
                off += 24;
                m.Anchors[an] = new KeyValuePair<Vector3, Vector3>(p, d.normalized);
            }
            return m;
        }

        /// <summary>The kit atlas (Kit/kit_atlas.png): RGB = base colour with wear x AO (sRGB), A = gloss. DXT5,
        /// mipmapped, no CPU copy; null if missing. An atlas without alpha gets a uniform medium gloss.</summary>
        public static Texture2D Atlas()
        {
            if (atlas != null) return atlas;                                   // Unity check: reloads after a destroy
            if (atlasTried && ReferenceEquals(atlas, null)) return null;       // missing file: don't retry
            atlasTried = true;
            atlas = LoadTexture(Path.Combine(Directory, "kit_atlas.png"), false, TextureWrapMode.Clamp, TextureWrapMode.Clamp, 0.45f);
            return atlas;
        }

        /// <summary>The kit atlas normal map (Kit/kit_atlas_n.png, OpenGL tangent space), linear, swizzled to the
        /// DXT5nm layout and compressed; null if missing (the material then uses a flat normal).</summary>
        public static Texture2D AtlasNormal()
        {
            if (atlasNormal != null) return atlasNormal;
            if (atlasNormalTried && ReferenceEquals(atlasNormal, null)) return null;
            atlasNormalTried = true;
            atlasNormal = LoadTexture(Path.Combine(Directory, "kit_atlas_n.png"), true, TextureWrapMode.Clamp, TextureWrapMode.Clamp);
            return atlasNormal;
        }

        /// <summary>
        /// Read a PNG into a GPU-only texture: mipmapped, trilinear, DXT5-compressed when the size allows (multiples
        /// of 4), CPU copy dropped. Albedo (normalMap false) is sRGB with gloss in alpha; when the file has no
        /// alpha at all, every texel gets `defaultGloss` (if >= 0). A normal map (RGB = n * 0.5 + 0.5, OpenGL) is
        /// linear and swizzled to the DXT5nm layout (1, y, 1, x): x in alpha, y in green, which KSP's
        /// UnpackNormalDXT5nm reads and which Unity's UnpackNormal (x = r * a) also reads correctly. `mipBias` (sampler
        /// LOD bias, 0 = none) lets a texture with a fine regular pattern pick a blurrier mip earlier, against moire.
        /// Returns null for a missing or unreadable file; never throws.
        /// </summary>
        public static Texture2D LoadTexture(string path, bool normalMap, TextureWrapMode wrapU, TextureWrapMode wrapV, float defaultGloss = -1, float mipBias = 0)
        {
            Texture2D png = null;
            try
            {
                if (!File.Exists(path)) return null;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                png = new Texture2D(2, 2, TextureFormat.RGBA32, false, normalMap);
                if (!png.LoadImage(File.ReadAllBytes(path), false))
                {
                    Debug.LogWarning($"[ProcEngines] texture {path}: not a readable image");
                    return null;
                }
                int w = png.width, h = png.height;
                Color32[] px = png.GetPixels32();
                UnityEngine.Object.DestroyImmediate(png);
                png = null;
                if (normalMap)
                {
                    for (int i = 0; i < px.Length; i++) { Color32 c = px[i]; px[i] = new Color32(255, c.g, 255, c.r); }
                }
                else if (defaultGloss >= 0)
                {
                    bool opaque = true;
                    for (int i = 0; i < px.Length; i++) if (px[i].a != 255) { opaque = false; break; }
                    if (opaque)
                    {
                        byte g = (byte)Mathf.RoundToInt(Mathf.Clamp01(defaultGloss) * 255);
                        for (int i = 0; i < px.Length; i++) px[i].a = g;
                    }
                }
                var t = new Texture2D(w, h, TextureFormat.RGBA32, true, normalMap)
                {
                    name = "ProcEngines/" + Path.GetFileNameWithoutExtension(path),
                    wrapModeU = wrapU, wrapModeV = wrapV, filterMode = FilterMode.Trilinear, anisoLevel = 4, mipMapBias = mipBias,
                };
                t.SetPixels32(px);
                t.Apply(true, false);                           // mipmaps from the full-size image
                if ((w & 3) == 0 && (h & 3) == 0) t.Compress(true);   // RGBA32 -> DXT5 (1 byte per texel)
                t.Apply(false, true);                           // drop the CPU copy
                t.mipMapBias = mipBias;                         // (sampler state; set again in case compression reset it)
                Debug.Log($"[ProcEngines] texture {Path.GetFileName(path)}: {w}x{h} {t.format}{(normalMap ? " linear normal" : "")}{(mipBias != 0 ? $" mip bias {mipBias:F2}" : "")}, loaded in {sw.Elapsed.TotalMilliseconds:F0} ms");
                return t;
            }
            catch (Exception e)
            {
                Debug.LogError($"[ProcEngines] texture {path}: {e}");
                return null;
            }
            finally
            {
                if (png != null) UnityEngine.Object.DestroyImmediate(png);
            }
        }
    }
}
