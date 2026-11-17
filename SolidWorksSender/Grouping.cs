using System;
using System.Collections.Generic;
using System.Linq;

namespace SolidWorksQuestSender;

/// The viewer gives each part a convex collider (so fingers can grab it) only when there are at most 96 parts; above
/// that the whole model gets one box, which fingers just push away. So big assemblies are sent as sub-assemblies:
/// the parts of each group are merged into one mesh.
///
/// The viewer builds those colliders from every vertex, on its main thread, and the time grows with the square of a
/// part's vertex count (measured on a Quest 3S: 20k vertices 0.3 s, 80k 4.9 s, 200k 39 s; the app is frozen
/// meanwhile). So groups are split by vertex count, and if the estimated time is still too long the model is sent
/// ungrouped (one box, but no freeze).
public static class Grouping
{
    public const int MaxParts = 96;
    public const double MaxLoadSeconds = 8;

    /// What grouping did, for the log.
    public static string LastNote = "";

    /// Set from the viewer's /status: our viewer build (Viewer/) gives every part a cheap collider, so nothing needs
    /// merging. Stays false for the upstream viewer.
    public static volatile bool ViewerHandlesManyParts;

    public static double EstimateSeconds(int vertices) => 0.33 * Math.Pow(vertices / 20000.0, 2);

    static int Vertices(List<MeshPart> g) => g.Sum(p => p.Mesh.Primitives.Sum(pr => pr.Positions.Count / 3));

    public static List<MeshPart> Group(List<MeshPart> parts, int max, ref List<(string id, double? m, double v)> mass)
    {
        LastNote = "";
        if (parts.Count <= max) return parts;
        if (ViewerHandlesManyParts) { LastNote = "not grouped: the viewer handles many parts"; return parts; }

        var chosen = Choose(parts, max);
        var verts = chosen.Select(Vertices).ToList();
        double seconds = verts.Sum(EstimateSeconds);
        if (seconds > MaxLoadSeconds)
        {
            LastNote = $"not grouped: {chosen.Count} groups would take the viewer ~{seconds:0} s to make grabbable " +
                       $"(largest {verts.Max():N0} vertices); lower maxTriangles to allow it";
            return parts;
        }
        LastNote = $"grouped into {chosen.Count}, largest {verts.Max():N0} vertices, ~{seconds:0.0} s for colliders";
        return Finish(chosen, ref mass);
    }

    static List<List<MeshPart>> Choose(List<MeshPart> parts, int max)
    {
        // Start from the top-level components, then keep opening the heaviest sub-assembly (most vertices) into its
        // children while the count still fits (bodies of one component always stay together).
        var groups = ByLevel(parts, 1).Select(g => (level: 1, parts: g, size: (double)Vertices(g))).ToList();
        if (groups.Count > max)
            return MergeSmallest(groups.Select(g => g.parts).ToList(), max);
        var closed = new HashSet<List<MeshPart>>();
        while (true)
        {
            int pick = -1;
            for (int i = 0; i < groups.Count; i++)
                if (!closed.Contains(groups[i].parts) && (pick < 0 || groups[i].size > groups[pick].size)) pick = i;
            if (pick < 0) break;
            var (level, members, _) = groups[pick];
            var children = ByLevel(members, level + 1);
            bool deeper = members.Any(p => p.Path != null && p.Path.Length > level);
            if (!deeper || groups.Count - 1 + children.Count > max) { closed.Add(members); continue; }
            groups.RemoveAt(pick);
            foreach (var c in children)
            {
                bool leaf = c.All(p => p.Path == null || p.Path.Length <= level + 1);
                var entry = (level + 1, c, (double)Vertices(c));
                groups.Add(entry);
                if (leaf && c.Select(p => p.Path == null ? p.Id : p.Path.Last()).Distinct().Count() == 1) closed.Add(c);
            }
        }
        return groups.Select(g => g.parts).ToList();
    }

    static List<MeshPart> Finish(List<List<MeshPart>> groups, ref List<(string id, double? m, double v)> mass)
    {
        var massById = mass.ToDictionary(x => x.id, x => (x.m, x.v));
        var newMass = new List<(string id, double? m, double v)>();
        var result = new List<MeshPart>();
        foreach (var g in groups)
        {
            var part = g.Count == 1 ? g[0] : Merge(g, "g" + result.Count, GroupName(g));
            result.Add(part);
            // Totals of the members that have them (mass only where a material is assigned).
            double v = 0, m = 0;
            bool anyV = false, anyM = false;
            foreach (var p in g)
                if (massById.TryGetValue(p.Id, out var mv))
                {
                    anyV = true; v += mv.v;
                    if (mv.m.HasValue) { anyM = true; m += mv.m.Value; }
                }
            if (anyV) newMass.Add((part.Id, anyM ? m : (double?)null, v));
            if (g.Count > 1)
            {
                part.Members = g.Count;
                part.VolumeM3 = v;
                part.MassKg = anyM ? m : (double?)null;
            }
        }
        mass = newMass;
        return result;
    }

    static List<List<MeshPart>> ByLevel(List<MeshPart> parts, int level)
    {
        var groups = new Dictionary<string, List<MeshPart>>();
        var order = new List<List<MeshPart>>();
        foreach (var p in parts)
        {
            string key = p.Path == null || p.Path.Length == 0 ? "\u0001" + p.Id
                : string.Join("\u0000", p.Path.Take(Math.Min(level, p.Path.Length)));
            if (!groups.TryGetValue(key, out var list)) { groups[key] = list = new List<MeshPart>(); order.Add(list); }
            list.Add(p);
        }
        return order;
    }

    /// Too many groups even at the top level (e.g. hundreds of loose screws): keep the biggest groups and add each
    /// smaller one to the nearest kept group.
    static List<List<MeshPart>> MergeSmallest(List<List<MeshPart>> groups, int max)
    {
        var boxes = groups.Select(Bounds).ToList();
        var order = Enumerable.Range(0, groups.Count).OrderByDescending(i => boxes[i].Size).ToList();
        var kept = order.Take(max).ToList();
        var result = kept.Select(i => new List<MeshPart>(groups[i])).ToList();
        foreach (int i in order.Skip(max))
        {
            int best = 0; double bestD = double.MaxValue;
            for (int k = 0; k < kept.Count; k++)
            {
                double d = boxes[i].DistanceTo(boxes[kept[k]]);
                if (d < bestD) { bestD = d; best = k; }
            }
            result[best].AddRange(groups[i]);
        }
        return result;
    }

    struct Box
    {
        public double X0, Y0, Z0, X1, Y1, Z1;
        public double Size => Math.Max(0, X1 - X0) + Math.Max(0, Y1 - Y0) + Math.Max(0, Z1 - Z0);
        public double DistanceTo(Box o)
        {
            double dx = Math.Max(0, Math.Max(o.X0 - X1, X0 - o.X1));
            double dy = Math.Max(0, Math.Max(o.Y0 - Y1, Y0 - o.Y1));
            double dz = Math.Max(0, Math.Max(o.Z0 - Z1, Z0 - o.Z1));
            return dx * dx + dy * dy + dz * dz;
        }
    }

    static Box Bounds(List<MeshPart> g)
    {
        var b = new Box { X0 = double.MaxValue, Y0 = double.MaxValue, Z0 = double.MaxValue, X1 = double.MinValue, Y1 = double.MinValue, Z1 = double.MinValue };
        foreach (var p in g)
            foreach (var prim in p.Mesh.Primitives)
            {
                var pos = prim.Positions;
                for (int i = 0; i < pos.Count; i += 3)
                {
                    Transform(p.Matrix, pos[i], pos[i + 1], pos[i + 2], out float x, out float y, out float z);
                    if (x < b.X0) b.X0 = x; if (x > b.X1) b.X1 = x;
                    if (y < b.Y0) b.Y0 = y; if (y > b.Y1) b.Y1 = y;
                    if (z < b.Z0) b.Z0 = z; if (z > b.Z1) b.Z1 = z;
                }
            }
        return b;
    }

    /// One mesh in assembly space holding every body of the group, one primitive per material.
    static MeshPart Merge(List<MeshPart> g, string id, string name)
    {
        var mesh = new MeshData { Name = name };
        var byMaterial = new Dictionary<string, Primitive>();
        foreach (var p in g)
            foreach (var src in p.Mesh.Primitives)
            {
                string key = string.Join(",", src.Color) + "|" + src.Metallic + "|" + src.Roughness;
                if (!byMaterial.TryGetValue(key, out var dst))
                {
                    dst = new Primitive { Color = src.Color, Metallic = src.Metallic, Roughness = src.Roughness };
                    byMaterial[key] = dst;
                    mesh.Primitives.Add(dst);
                }
                uint offset = (uint)(dst.Positions.Count / 3);
                var pos = src.Positions; var nrm = src.Normals;
                for (int i = 0; i < pos.Count; i += 3)
                {
                    Transform(p.Matrix, pos[i], pos[i + 1], pos[i + 2], out float x, out float y, out float z);
                    dst.Positions.Add(x); dst.Positions.Add(y); dst.Positions.Add(z);
                    Rotate(p.Matrix, nrm[i], nrm[i + 1], nrm[i + 2], out float nx, out float ny, out float nz);
                    dst.Normals.Add(nx); dst.Normals.Add(ny); dst.Normals.Add(nz);
                }
                var ind = src.Indices;
                bool mirrored = Determinant(p.Matrix) < 0; // glTF flips winding for mirrored nodes; baked, we must
                for (int i = 0; i + 2 < ind.Count; i += 3)
                {
                    dst.Indices.Add(ind[i] + offset);
                    dst.Indices.Add(ind[mirrored ? i + 2 : i + 1] + offset);
                    dst.Indices.Add(ind[mirrored ? i + 1 : i + 2] + offset);
                }
            }
        return new MeshPart { Name = name, Id = id, Mesh = mesh, Path = g[0].Path };
    }

    /// The deepest sub-assembly shared by every member, e.g. "Left side-1"; else the first member's name.
    static string GroupName(List<MeshPart> g)
    {
        var first = g[0].Path;
        if (first == null) return g[0].Name;
        int common = first.Length;
        foreach (var p in g)
        {
            int n = 0;
            while (p.Path != null && n < common && n < p.Path.Length && p.Path[n] == first[n]) n++;
            common = n;
        }
        if (common == 0) return g[0].Name + $" + {g.Count - 1} more";
        string full = first[common - 1];
        int slash = full.LastIndexOf('/');
        return slash >= 0 ? full.Substring(slash + 1) : full;
    }

    static void Transform(float[] m, float x, float y, float z, out float ox, out float oy, out float oz)
    {
        if (m == null) { ox = x; oy = y; oz = z; return; }
        ox = m[0] * x + m[4] * y + m[8] * z + m[12];
        oy = m[1] * x + m[5] * y + m[9] * z + m[13];
        oz = m[2] * x + m[6] * y + m[10] * z + m[14];
    }

    static float Determinant(float[] m) => m == null ? 1f :
        m[0] * (m[5] * m[10] - m[9] * m[6]) - m[4] * (m[1] * m[10] - m[9] * m[2]) + m[8] * (m[1] * m[6] - m[5] * m[2]);

    static void Rotate(float[] m, float x, float y, float z, out float ox, out float oy, out float oz)
    {
        if (m == null) { ox = x; oy = y; oz = z; return; }
        ox = m[0] * x + m[4] * y + m[8] * z;
        oy = m[1] * x + m[5] * y + m[9] * z;
        oz = m[2] * x + m[6] * y + m[10] * z;
        float len = (float)Math.Sqrt(ox * ox + oy * oy + oz * oz);
        if (len > 0) { ox /= len; oy /= len; oz /= len; }
    }
}
