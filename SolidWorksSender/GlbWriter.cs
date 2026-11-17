using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace SolidWorksQuestSender;

/// One triangle list with a single colour.
public sealed class Primitive
{
    public float[] Color = { 0.8f, 0.8f, 0.8f, 1f }; // linear RGBA
    public float Metallic = 0f;
    public float Roughness = 0.5f;
    public readonly List<float> Positions = new(); // xyz, metres, glTF axes (+Y up)
    public readonly List<float> Normals = new();
    public readonly List<uint> Indices = new();
    readonly Dictionary<(float, float, float, float, float, float), uint> weld = new();

    public void AddVertex(float px, float py, float pz, float nx, float ny, float nz)
    {
        var key = (px, py, pz, nx, ny, nz);
        if (!weld.TryGetValue(key, out uint i))
        {
            i = (uint)(Positions.Count / 3);
            Positions.Add(px); Positions.Add(py); Positions.Add(pz);
            Normals.Add(nx); Normals.Add(ny); Normals.Add(nz);
            weld[key] = i;
        }
        Indices.Add(i);
    }
}

/// The geometry of one solid body (one primitive per colour), in the part's own space. Shared by every instance.
public sealed class MeshData
{
    public string Name;
    public readonly List<Primitive> Primitives = new();
    public int TriangleCount => Primitives.Sum(p => p.Indices.Count / 3);
}

/// One placed body = one glTF node. The viewer makes each node a part.
public sealed class MeshPart
{
    public string Name;
    public string Id;       // short id, matched against X-Part-Info
    public MeshData Mesh;
    public float[] Matrix;  // glTF column-major 4x4, or null for identity
    public string[] Path;   // assembly components from the top level down to this body's component, or null
    // Specs for the viewer's part info panel (SolidWorks fork viewer; written as node extras, ignored by others).
    public string File, Config, Material, Body;
    public double? MassKg, DensityKgM3;
    public double VolumeM3, AreaM2;
    public int Instances, Members;
    // How the part may move by its SolidWorks mates (Motion.cs), axis in the node's own space; null = not known.
    public string MotionKind, MotionNote;
    public double[] MotionPoint, MotionDirection;
    public int Freedoms = -1, MateCount;
    public int MotionGroup;
    public List<double[]> Modes; // [id, rad/t, m/t, point xyz, direction xyz, tMin, tMax] per mechanism mode it moves in
    public int[] Drive;          // the modes grabbing it drives (1, or 2 for turn + slide)
    public bool SolidWorksFixed;
    public int TriangleCount => Mesh.TriangleCount;
}

/// Writes a minimal glTF 2.0 binary. Nodes carry the same "PTC_onshape_metadata" id extension Onshape uses, so the
/// unmodified Quest viewer can match parts to the mass/volume info it receives.
public static class GlbWriter
{
    const string IdExtension = "PTC_onshape_metadata";

    public static byte[] Write(string sceneName, IReadOnlyList<MeshPart> parts, string sceneExtras = null)
    {
        var bin = new MemoryStream();
        var views = new List<string>();
        var accessors = new List<string>();
        var meshes = new List<string>();
        var materials = new List<string>();
        var nodes = new List<string>();
        var materialIndex = new Dictionary<string, int>();

        int AddView(byte[] data, int target, int stride = 0)
        {
            while (bin.Length % 4 != 0) bin.WriteByte(0);
            long offset = bin.Length;
            bin.Write(data, 0, data.Length);
            views.Add($"{{\"buffer\":0,\"byteOffset\":{offset},\"byteLength\":{data.Length}" +
                      (stride > 0 ? $",\"byteStride\":{stride}" : "") + $",\"target\":{target}}}");
            return views.Count - 1;
        }

        var meshIndex = new Dictionary<MeshData, int>();
        foreach (var part in parts)
        {
            if (meshIndex.TryGetValue(part.Mesh, out int existing))
            {
                if (existing >= 0) nodes.Add(Node(part, existing));
                continue;
            }
            var prims = new List<string>();
            foreach (var p in part.Mesh.Primitives)
            {
                if (p.Indices.Count == 0) continue;
                var pos = p.Positions.ToArray();
                Bounds(pos, out var min, out var max);
                int vertexCount = pos.Length / 3;
                int pv = AddView(Bytes(pos), 34962);
                // Normals as normalized signed bytes (KHR_mesh_quantization), padded to 4 bytes per vertex.
                int nv = AddView(PackNormals(p.Normals), 34962, 4);
                bool shortIndices = vertexCount <= 65535;
                int iv = AddView(shortIndices ? Bytes16(p.Indices) : Bytes(p.Indices.ToArray()), 34963);
                accessors.Add($"{{\"bufferView\":{pv},\"componentType\":5126,\"count\":{vertexCount},\"type\":\"VEC3\",\"min\":[{F(min[0])},{F(min[1])},{F(min[2])}],\"max\":[{F(max[0])},{F(max[1])},{F(max[2])}]}}");
                int pa = accessors.Count - 1;
                accessors.Add($"{{\"bufferView\":{nv},\"componentType\":5120,\"normalized\":true,\"count\":{vertexCount},\"type\":\"VEC3\"}}");
                int na = accessors.Count - 1;
                accessors.Add($"{{\"bufferView\":{iv},\"componentType\":{(shortIndices ? 5123 : 5125)},\"count\":{p.Indices.Count},\"type\":\"SCALAR\"}}");
                int ia = accessors.Count - 1;

                var c = p.Color;
                string matJson = $"{{\"pbrMetallicRoughness\":{{\"baseColorFactor\":[{F(c[0])},{F(c[1])},{F(c[2])},{F(c[3])}],\"metallicFactor\":{F(p.Metallic)},\"roughnessFactor\":{F(p.Roughness)}}}"
                               + (c[3] < 0.999f ? ",\"alphaMode\":\"BLEND\"" : "") + ",\"doubleSided\":false}";
                if (!materialIndex.TryGetValue(matJson, out int mi))
                {
                    materials.Add(matJson);
                    mi = materialIndex[matJson] = materials.Count - 1;
                }
                prims.Add($"{{\"attributes\":{{\"POSITION\":{pa},\"NORMAL\":{na}}},\"indices\":{ia},\"material\":{mi},\"mode\":4}}");
            }
            if (prims.Count == 0) { meshIndex[part.Mesh] = -1; continue; }
            meshes.Add($"{{\"name\":{J(part.Mesh.Name ?? part.Name)},\"primitives\":[{string.Join(",", prims)}]}}");
            meshIndex[part.Mesh] = meshes.Count - 1;
            nodes.Add(Node(part, meshes.Count - 1));
        }
        if (nodes.Count == 0) throw new InvalidOperationException("Nothing to export: no visible solid bodies with geometry.");
        while (bin.Length % 4 != 0) bin.WriteByte(0);

        var rootNodes = string.Join(",", Enumerable.Range(0, nodes.Count));
        string sceneJson = sceneExtras != null ? ",\"extras\":" + sceneExtras : "";
        var json = "{\"asset\":{\"version\":\"2.0\",\"generator\":\"SolidWorks Quest Sender\"}"
                 + $",\"extensionsUsed\":[\"{IdExtension}\",\"KHR_mesh_quantization\"]"
                 + ",\"extensionsRequired\":[\"KHR_mesh_quantization\"]"
                 + $",\"scene\":0,\"scenes\":[{{\"name\":{J(sceneName)},\"nodes\":[{rootNodes}]{sceneJson}}}]"
                 + $",\"nodes\":[{string.Join(",", nodes)}]"
                 + $",\"meshes\":[{string.Join(",", meshes)}]"
                 + $",\"materials\":[{string.Join(",", materials)}]"
                 + $",\"accessors\":[{string.Join(",", accessors)}]"
                 + $",\"bufferViews\":[{string.Join(",", views)}]"
                 + $",\"buffers\":[{{\"byteLength\":{bin.Length}}}]}}";

        var jsonBytes = Encoding.UTF8.GetBytes(json);
        int jsonPadded = (jsonBytes.Length + 3) & ~3;
        int binLength = (int)bin.Length;
        int total = 12 + 8 + jsonPadded + 8 + binLength;

        var outStream = new MemoryStream(total);
        var w = new BinaryWriter(outStream);
        w.Write(0x46546C67u); // "glTF"
        w.Write(2u);
        w.Write((uint)total);
        w.Write((uint)jsonPadded);
        w.Write(0x4E4F534Au); // "JSON"
        w.Write(jsonBytes);
        for (int i = jsonBytes.Length; i < jsonPadded; i++) w.Write((byte)0x20);
        w.Write((uint)binLength);
        w.Write(0x004E4942u); // "BIN\0"
        w.Write(bin.GetBuffer(), 0, binLength);
        w.Flush();
        return outStream.ToArray();
    }

    static string Node(MeshPart part, int mesh) =>
        $"{{\"name\":{J(part.Name)},\"mesh\":{mesh}"
        + (part.Matrix != null ? $",\"matrix\":[{string.Join(",", part.Matrix.Select(F))}]" : "")
        + $",\"extensions\":{{\"{IdExtension}\":{{\"id\":[{J(part.Id)}]}}}}"
        + Extras(part) + "}";

    /// "extras":{"sw":{...}} with the part's specs, read by the SolidWorks fork of the viewer for its info panel.
    static string Extras(MeshPart p)
    {
        var f = new List<string>();
        void S(string k, string v) { if (!string.IsNullOrEmpty(v)) f.Add($"\"{k}\":{J(v)}"); }
        void N(string k, double? v) { if (v.HasValue && v.Value > 0 && !double.IsNaN(v.Value)) f.Add($"\"{k}\":{v.Value.ToString("G6", CultureInfo.InvariantCulture)}"); }
        S("file", p.File);
        S("config", p.Config);
        S("material", p.Material);
        S("body", p.Body);
        if (p.Path != null && p.Path.Length > 1) S("path", string.Join(" / ", p.Path.Take(p.Path.Length - 1)));
        N("mass", p.MassKg);
        N("density", p.DensityKgM3);
        N("volume", p.VolumeM3);
        N("area", p.AreaM2);
        N("count", p.Instances);
        N("members", p.Members);
        N("tris", p.TriangleCount);
        S("motion", p.MotionKind);
        S("motionNote", p.MotionNote);
        if (p.MotionPoint != null) f.Add("\"axisP\":[" + string.Join(",", p.MotionPoint.Select(x => x.ToString("G7", CultureInfo.InvariantCulture))) + "]");
        if (p.MotionDirection != null) f.Add("\"axisD\":[" + string.Join(",", p.MotionDirection.Select(x => x.ToString("G7", CultureInfo.InvariantCulture))) + "]");
        if (p.Freedoms >= 0) f.Add("\"dof\":" + p.Freedoms);
        if (p.MateCount > 0) f.Add("\"mates\":" + p.MateCount);
        if (p.MotionGroup > 0) f.Add("\"group\":" + p.MotionGroup);
        if (p.Modes != null && p.Modes.Count > 0)
        {
            // Unbounded limits as ±1e9 (JSON has no infinity).
            string V(double v) => (double.IsPositiveInfinity(v) ? 1e9 : double.IsNegativeInfinity(v) ? -1e9 : v).ToString("G7", CultureInfo.InvariantCulture);
            f.Add("\"modes\":[" + string.Join(",", p.Modes.Select(m => "[" + string.Join(",", m.Select(V)) + "]")) + "]");
        }
        if (p.Drive != null && p.Drive.Length > 0) f.Add("\"drive\":[" + string.Join(",", p.Drive) + "]");
        if (p.SolidWorksFixed) f.Add("\"fixed\":true");
        return f.Count == 0 ? "" : ",\"extras\":{\"sw\":{" + string.Join(",", f) + "}}";
    }

    static byte[] Bytes(float[] a) { var b = new byte[a.Length * 4]; Buffer.BlockCopy(a, 0, b, 0, b.Length); return b; }
    static byte[] Bytes(uint[] a) { var b = new byte[a.Length * 4]; Buffer.BlockCopy(a, 0, b, 0, b.Length); return b; }

    static byte[] Bytes16(List<uint> a)
    {
        var b = new byte[(a.Count * 2 + 3) & ~3];
        for (int i = 0; i < a.Count; i++) { b[i * 2] = (byte)a[i]; b[i * 2 + 1] = (byte)(a[i] >> 8); }
        return b;
    }

    static byte[] PackNormals(List<float> n)
    {
        int count = n.Count / 3;
        var b = new byte[count * 4];
        for (int i = 0; i < count; i++)
        {
            float x = n[i * 3], y = n[i * 3 + 1], z = n[i * 3 + 2];
            float len = (float)Math.Sqrt(x * x + y * y + z * z);
            if (len > 0) { x /= len; y /= len; z /= len; }
            b[i * 4] = (byte)(sbyte)Math.Round(x * 127);
            b[i * 4 + 1] = (byte)(sbyte)Math.Round(y * 127);
            b[i * 4 + 2] = (byte)(sbyte)Math.Round(z * 127);
        }
        return b;
    }

    static void Bounds(float[] p, out float[] min, out float[] max)
    {
        min = new[] { float.MaxValue, float.MaxValue, float.MaxValue };
        max = new[] { float.MinValue, float.MinValue, float.MinValue };
        for (int i = 0; i < p.Length; i += 3)
            for (int k = 0; k < 3; k++)
            {
                if (p[i + k] < min[k]) min[k] = p[i + k];
                if (p[i + k] > max[k]) max[k] = p[i + k];
            }
    }

    static string F(float v) => v.ToString("R", CultureInfo.InvariantCulture);

    public static string J(string s)
    {
        if (s == null) return "null";
        var sb = new StringBuilder("\"");
        foreach (char ch in s)
        {
            switch (ch)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (ch < 0x20) sb.Append("\\u").Append(((int)ch).ToString("x4"));
                    else sb.Append(ch);
                    break;
            }
        }
        return sb.Append('"').ToString();
    }
}
