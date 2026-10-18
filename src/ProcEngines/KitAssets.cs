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
        public Vector3[] Positions, Normals;
        public Vector2[] Uvs;
        public int[] Indices;
        public readonly Dictionary<string, KeyValuePair<Vector3, Vector3>> Anchors = new Dictionary<string, KeyValuePair<Vector3, Vector3>>();

        public bool TryAnchor(string name, out Vector3 position, out Vector3 direction)
        {
            if (Anchors.TryGetValue(name, out var a)) { position = a.Key; direction = a.Value; return true; }
            position = direction = Vector3.zero;
            return false;
        }
    }

    /// <summary>
    /// Loads the authored kit pieces and their atlas from GameData/ProcEngines/PluginData/Kit (PluginData, so KSP's
    /// GameDatabase doesn't also load the atlas). Missing or broken files leave the primitive kit in place.
    /// </summary>
    public static class KitAssets
    {
        static readonly Dictionary<string, KitMesh> meshes = new Dictionary<string, KitMesh>();
        static Texture2D atlas;
        static bool atlasTried;
        public static string Status = "not loaded";

        public static string Directory
        {
            get
            {
                string plugins = Path.GetDirectoryName(typeof(KitAssets).Assembly.Location);
                return Path.GetFullPath(Path.Combine(plugins, "..", "PluginData", "Kit"));
            }
        }

        public static KitMesh Get(string name)
        {
            if (meshes.TryGetValue(name, out KitMesh m)) return m;
            m = null;
            string path = Path.Combine(Directory, name + ".pem");
            try
            {
                if (File.Exists(path)) m = Read(name, File.ReadAllBytes(path));
                Status = m != null ? $"{name}: {m.Positions.Length} verts, {m.Anchors.Count} anchors" : $"{name}: missing ({path})";
            }
            catch (Exception e)
            {
                Status = $"{name}: {e.Message}";
                Debug.LogError($"[ProcEngines] kit piece {path}: {e}");
            }
            meshes[name] = m;
            return m;
        }

        public static KitMesh Read(string name, byte[] data)
        {
            using (var r = new BinaryReader(new MemoryStream(data)))
            {
                if (Encoding.ASCII.GetString(r.ReadBytes(4)) != "PEM1") throw new InvalidDataException("not a PEM1 file");
                int version = r.ReadInt32();
                if (version != 1) throw new InvalidDataException("unsupported version " + version);
                int nv = r.ReadInt32(), ni = r.ReadInt32();
                var m = new KitMesh { Name = name, Positions = new Vector3[nv], Normals = new Vector3[nv], Uvs = new Vector2[nv], Indices = new int[ni] };
                for (int i = 0; i < nv; i++) m.Positions[i] = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                for (int i = 0; i < nv; i++) m.Normals[i] = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                for (int i = 0; i < nv; i++) m.Uvs[i] = new Vector2(r.ReadSingle(), r.ReadSingle());
                for (int i = 0; i < ni; i++)
                {
                    m.Indices[i] = r.ReadInt32();
                    if (m.Indices[i] < 0 || m.Indices[i] >= nv) throw new InvalidDataException("index out of range");
                }
                int na = r.ReadInt32();
                for (int i = 0; i < na; i++)
                {
                    string an = Encoding.UTF8.GetString(r.ReadBytes(r.ReadInt32()));
                    var p = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                    var d = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                    m.Anchors[an] = new KeyValuePair<Vector3, Vector3>(p, d.normalized);
                }
                return m;
            }
        }

        /// <summary>The baked atlas (base colour x ambient occlusion), DXT-compressed and non-readable; null if missing.</summary>
        public static Texture2D Atlas()
        {
            if (atlasTried) return atlas;
            atlasTried = true;
            string path = Path.Combine(Directory, "kit_atlas.png");
            try
            {
                if (!File.Exists(path)) return null;
                var png = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (!png.LoadImage(File.ReadAllBytes(path), false)) return null;
                // opaque: RGB24 compresses to DXT1 (half of DXT5)
                var t = new Texture2D(png.width, png.height, TextureFormat.RGB24, true);
                t.SetPixels32(png.GetPixels32());
                UnityEngine.Object.Destroy(png);
                t.wrapMode = TextureWrapMode.Clamp;
                t.filterMode = FilterMode.Trilinear;
                t.anisoLevel = 4;
                t.Apply(true, false);    // mipmaps from the full-size image
                t.Compress(true);        // DXT1: a quarter of RGBA32 with mips
                t.Apply(false, true);    // drop the CPU copy
                atlas = t;
            }
            catch (Exception e) { Debug.LogError($"[ProcEngines] kit atlas {path}: {e}"); }
            return atlas;
        }
    }
}
