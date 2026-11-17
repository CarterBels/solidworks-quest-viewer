using System.Text.Json;
using SolidWorksQuestSender;

/// Re-runs simplification + GLB writing on a GLB this add-in exported earlier (no SolidWorks needed).
static class Shrink
{
    public static void Run(string input, string output, int budget)
    {
        var buf = File.ReadAllBytes(input);
        int jsonLen = BitConverter.ToInt32(buf, 12);
        using var doc = JsonDocument.Parse(new ReadOnlyMemory<byte>(buf, 20, jsonLen));
        var g = doc.RootElement;
        int binStart = 20 + jsonLen + 8;
        var views = g.GetProperty("bufferViews").EnumerateArray().ToArray();
        var accs = g.GetProperty("accessors").EnumerateArray().ToArray();
        var mats = g.GetProperty("materials").EnumerateArray().ToArray();

        float[] Floats(int a)
        {
            var acc = accs[a]; var v = views[acc.GetProperty("bufferView").GetInt32()];
            int n = acc.GetProperty("count").GetInt32() * 3, off = binStart + v.GetProperty("byteOffset").GetInt32();
            var r = new float[n];
            if (acc.GetProperty("componentType").GetInt32() == 5126) Buffer.BlockCopy(buf, off, r, 0, n * 4);
            else for (int i = 0; i < n / 3; i++) for (int k = 0; k < 3; k++) r[i * 3 + k] = (sbyte)buf[off + i * 4 + k] / 127f;
            return r;
        }
        uint[] Ints(int a)
        {
            var acc = accs[a]; var v = views[acc.GetProperty("bufferView").GetInt32()];
            int n = acc.GetProperty("count").GetInt32(), off = binStart + v.GetProperty("byteOffset").GetInt32();
            var r = new uint[n];
            bool shorts = acc.GetProperty("componentType").GetInt32() == 5123;
            for (int i = 0; i < n; i++) r[i] = shorts ? BitConverter.ToUInt16(buf, off + i * 2) : BitConverter.ToUInt32(buf, off + i * 4);
            return r;
        }

        var meshes = g.GetProperty("meshes").EnumerateArray().Select(m =>
        {
            var md = new MeshData { Name = m.GetProperty("name").GetString() };
            foreach (var p in m.GetProperty("primitives").EnumerateArray())
            {
                var mat = mats[p.GetProperty("material").GetInt32()].GetProperty("pbrMetallicRoughness");
                var prim = new Primitive
                {
                    Color = mat.GetProperty("baseColorFactor").EnumerateArray().Select(e => e.GetSingle()).ToArray(),
                    Roughness = mat.GetProperty("roughnessFactor").GetSingle(),
                };
                var pos = Floats(p.GetProperty("attributes").GetProperty("POSITION").GetInt32());
                var nrm = Floats(p.GetProperty("attributes").GetProperty("NORMAL").GetInt32());
                foreach (var i in Ints(p.GetProperty("indices").GetInt32()))
                    prim.AddVertex(pos[i * 3], pos[i * 3 + 1], pos[i * 3 + 2], nrm[i * 3], nrm[i * 3 + 1], nrm[i * 3 + 2]);
                md.Primitives.Add(prim);
            }
            return md;
        }).ToArray();

        var parts = g.GetProperty("nodes").EnumerateArray().Select(n => new MeshPart
        {
            Name = n.GetProperty("name").GetString(),
            Id = n.GetProperty("extensions").GetProperty("PTC_onshape_metadata").GetProperty("id")[0].GetString(),
            Mesh = meshes[n.GetProperty("mesh").GetInt32()],
            Matrix = n.TryGetProperty("matrix", out var mx) ? mx.EnumerateArray().Select(e => e.GetSingle()).ToArray() : null,
        }).ToList();

        long before = parts.Sum(p => (long)p.TriangleCount);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        double tol = Decimator.FitBudget(parts, budget);
        var simplifyTime = clock.Elapsed.TotalSeconds;
        // Node names are SolidWorks component names ("Sub-1/Part-2", maybe " / body"): rebuild the component paths.
        foreach (var p in parts)
        {
            var segs = p.Name.Split(" / ")[0].Split('/');
            p.Path = Enumerable.Range(1, segs.Length).Select(n => string.Join("/", segs.Take(n))).ToArray();
        }
        int bodies = parts.Count;
        var mass = parts.Select(p => (p.Id, (double?)1.0, 1e-6)).ToList();
        clock.Restart();
        var grouped = Grouping.Group(parts, Grouping.MaxParts, ref mass);
        var groupTime = clock.Elapsed.TotalSeconds;
        var glb = GlbWriter.Write("shrunk", grouped);
        File.WriteAllBytes(output, glb);
        Console.WriteLine($"drawn {before:N0} -> {parts.Sum(p => (long)p.TriangleCount):N0} at {tol} mm in {simplifyTime:0.0}s; " +
                          $"unique {meshes.Sum(m => m.TriangleCount):N0}; {buf.Length / 1e6:0.0} MB -> {glb.Length / 1e6:0.0} MB");
        Console.WriteLine(Grouping.LastNote); Console.WriteLine($"{bodies} bodies -> {grouped.Count} parts in {groupTime:0.0}s; mass entries {mass.Count}, total v {mass.Sum(x => x.Item3) * 1e6:0}");
        foreach (var gp in grouped.OrderByDescending(x => x.TriangleCount).Take(12))
            Console.WriteLine($"  {gp.Name}: {gp.TriangleCount:N0} triangles, {gp.Mesh.Primitives.Sum(pr => pr.Positions.Count / 3):N0} vertices");
    }
}
