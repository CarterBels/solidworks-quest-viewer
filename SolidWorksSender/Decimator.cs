using System;
using System.Collections.Generic;
using System.Linq;

namespace SolidWorksQuestSender;

/// Quadric-error mesh simplification (Garland & Heckbert), half-edge collapses: a vertex is merged into a neighbour,
/// so surviving vertices keep their exact positions. Open edges (colour borders, where neighbouring faces of a body
/// have different colours) are never moved, so primitives of one body stay crack-free. Normals are recomputed
/// afterwards with a crease angle.
public static class Decimator
{
    /// Simplifies 'tris' (indices into 'pos', xyz) until no collapse costs less than maxErrorÂ² (metresÂ²) or the
    /// triangle count reaches minTriangles. Returns the surviving triangles (indices into 'pos').
    public static int[] Simplify(float[] pos, int[] tris, double maxError, int minTriangles = 4)
    {
        int nv = pos.Length / 3, nt = tris.Length / 3;
        if (nt <= minTriangles) return tris;

        var t = (int[])tris.Clone();
        var alive = new bool[nt];
        for (int i = 0; i < nt; i++) alive[i] = true;
        int aliveCount = nt;

        // Vertex -> triangles
        var vt = new List<int>[nv];
        for (int i = 0; i < nv; i++) vt[i] = new List<int>(6);
        for (int i = 0; i < nt; i++) for (int k = 0; k < 3; k++) vt[t[i * 3 + k]].Add(i);

        // Plane quadrics (area weighted)
        var q = new double[nv * 10];
        var areaSum = new double[nv];
        for (int i = 0; i < nt; i++)
        {
            int a = t[i * 3], b = t[i * 3 + 1], c = t[i * 3 + 2];
            Normal(pos, a, b, c, out double nx, out double ny, out double nz, out double area);
            if (area <= 0) continue;
            double d = -(nx * pos[a * 3] + ny * pos[a * 3 + 1] + nz * pos[a * 3 + 2]);
            for (int k = 0; k < 3; k++) AddPlane(q, areaSum, t[i * 3 + k], nx, ny, nz, d, area);
        }

        // Border vertices: on an edge used by only one triangle. They stay put.
        var locked = new bool[nv];
        var edgeCount = new Dictionary<long, int>(nt * 3, EdgeKeyComparer.Instance);
        for (int i = 0; i < nt; i++)
            for (int k = 0; k < 3; k++)
            {
                long key = EdgeKey(t[i * 3 + k], t[i * 3 + (k + 1) % 3]);
                edgeCount.TryGetValue(key, out int n);
                edgeCount[key] = n + 1;
            }
        foreach (var kv in edgeCount)
            if (kv.Value != 2) { locked[(int)(kv.Key >> 32)] = true; locked[(int)(kv.Key & 0xffffffff)] = true; }

        // Every directed edge u -> v is a candidate (cost of moving u onto v). Costs only ever grow as quadrics
        // accumulate, so stale entries are re-checked when they reach the top instead of updating the whole
        // neighbourhood after each collapse (which is quadratic around high-valence vertices).
        var heap = new MinHeap(nt * 6);
        void Candidate(int u, int v)
        {
            if (!locked[u]) heap.Push(Cost(q, areaSum, u, v, pos), u, v, 0);
        }
        for (int i = 0; i < nt; i++)
            for (int k = 0; k < 3; k++)
            {
                int a = t[i * 3 + k], b = t[i * 3 + (k + 1) % 3];
                Candidate(a, b); Candidate(b, a);
            }

        double limit = maxError * maxError;
        var newNeighbours = new List<int>();
        var gone = new bool[nv];
        while (heap.Count > 0 && aliveCount > minTriangles)
        {
            var (cost, u, v, _) = heap.Pop();
            if (cost > limit) break;
            if (gone[u] || gone[v]) continue;                       // either end already collapsed away
            // Vertex -> triangle lists keep dead triangles (removing them is linear); prune now and then.
            if (vt[u].Count > 32) Prune(vt[u], alive);
            if (vt[u].Count > MaxValence) continue;                 // fan centres: moving them almost never works
            if (!Adjacent(t, vt, alive, u, v)) continue;
            double now = Cost(q, areaSum, u, v, pos);
            if (now > cost * (1 + 1e-9) + 1e-30) { heap.Push(now, u, v, 0); continue; } // stale: re-queue
            if (!CanCollapse(pos, t, vt, alive, u, v)) continue;

            // Collapse u into v.
            newNeighbours.Clear();
            foreach (int ti in vt[u])
            {
                if (!alive[ti]) continue;
                int i0 = ti * 3;
                if (t[i0] == v || t[i0 + 1] == v || t[i0 + 2] == v)
                {
                    alive[ti] = false; aliveCount--;
                }
                else
                {
                    for (int k = 0; k < 3; k++)
                    {
                        if (t[i0 + k] == u) t[i0 + k] = v;
                        else newNeighbours.Add(t[i0 + k]);
                    }
                    vt[v].Add(ti);
                }
            }
            vt[u].Clear();
            gone[u] = true;
            if (vt[v].Count > 64 && vt[v].Count % 64 == 0) Prune(vt[v], alive);
            for (int k = 0; k < 10; k++) q[v * 10 + k] += q[u * 10 + k];
            areaSum[v] += areaSum[u];

            // Edges that now exist between v and u's former neighbours.
            foreach (int w in newNeighbours) { Candidate(w, v); Candidate(v, w); }
        }
        var result = new List<int>(aliveCount * 3);
        for (int i = 0; i < nt; i++)
            if (alive[i]) { result.Add(t[i * 3]); result.Add(t[i * 3 + 1]); result.Add(t[i * 3 + 2]); }
        return result.ToArray();
    }

    /// Simplifies the model's meshes until the triangles drawn (counting every instance) fit 'budget', loosening the
    /// tolerance step by step. Returns the tolerance used (mm), or 0 if nothing needed simplifying.
    public static double FitBudget(IReadOnlyList<MeshPart> parts, int budget)
    {
        var uses = new Dictionary<MeshData, int>();
        foreach (var p in parts) { uses.TryGetValue(p.Mesh, out int n); uses[p.Mesh] = n + 1; }
        long Drawn() { long s = 0; foreach (var kv in uses) s += (long)kv.Key.TriangleCount * kv.Value; return s; }

        double tolMm = 0;
        var meshes = uses.Keys.OrderByDescending(m => m.TriangleCount).ToArray(); // biggest first, for load balance
        for (double mm = 0.05; mm <= 3.2;)
        {
            long drawn = Drawn();
            if (drawn <= budget) break;
            tolMm = mm;
            double tol = mm / 1000.0;
            System.Threading.Tasks.Parallel.ForEach(meshes, m => SimplifyMesh(m, tol));
            // Far over budget: loosen faster.
            mm *= Drawn() > budget * 4L ? 4 : 2;
        }
        return tolMm;
    }

    /// Simplifies each primitive (colour) of a mesh in place and recomputes its normals.
    static void SimplifyMesh(MeshData mesh, double tol)
    {
        for (int pi = 0; pi < mesh.Primitives.Count; pi++)
        {
            var prim = mesh.Primitives[pi];
            if (prim.Indices.Count < 3 * 8) continue;

            // Weld by position (vertices are split where normals differ; the simplifier needs connectivity).
            var weld = new Dictionary<(float, float, float), int>();
            var pos = new List<float>();
            var remap = new int[prim.Positions.Count / 3];
            for (int i = 0; i < remap.Length; i++)
            {
                var key = (prim.Positions[i * 3], prim.Positions[i * 3 + 1], prim.Positions[i * 3 + 2]);
                if (!weld.TryGetValue(key, out int w))
                {
                    w = weld[key] = pos.Count / 3;
                    pos.Add(key.Item1); pos.Add(key.Item2); pos.Add(key.Item3);
                }
                remap[i] = w;
            }
            var tris = new List<int>(prim.Indices.Count);
            for (int i = 0; i + 2 < prim.Indices.Count; i += 3)
            {
                int a = remap[prim.Indices[i]], b = remap[prim.Indices[i + 1]], c = remap[prim.Indices[i + 2]];
                if (a != b && b != c && a != c) { tris.Add(a); tris.Add(b); tris.Add(c); }
            }

            var p = pos.ToArray();
            var simplified = Simplify(p, tris.ToArray(), tol);
            if (simplified.Length >= tris.Count) continue;

            var rebuilt = new Primitive { Color = prim.Color, Metallic = prim.Metallic, Roughness = prim.Roughness };
            AddWithCreaseNormals(rebuilt, p, simplified, 40);
            mesh.Primitives[pi] = rebuilt;
        }
    }

    /// Smooth normals within 'creaseDeg' of each other, hard edges beyond (cylinders smooth, box edges sharp).
    static void AddWithCreaseNormals(Primitive prim, float[] p, int[] t, double creaseDeg)
    {
        int nt = t.Length / 3;
        var fn = new double[nt * 3];
        var fa = new double[nt];
        var vt = new Dictionary<int, List<int>>();
        for (int i = 0; i < nt; i++)
        {
            Normal(p, t[i * 3], t[i * 3 + 1], t[i * 3 + 2], out fn[i * 3], out fn[i * 3 + 1], out fn[i * 3 + 2], out fa[i]);
            for (int k = 0; k < 3; k++)
            {
                if (!vt.TryGetValue(t[i * 3 + k], out var l)) vt[t[i * 3 + k]] = l = new List<int>(6);
                l.Add(i);
            }
        }
        double cos = Math.Cos(creaseDeg * Math.PI / 180);
        for (int i = 0; i < nt; i++)
            for (int k = 0; k < 3; k++)
            {
                int v = t[i * 3 + k];
                double nx = 0, ny = 0, nz = 0;
                foreach (int j in vt[v])
                {
                    double dot = fn[i * 3] * fn[j * 3] + fn[i * 3 + 1] * fn[j * 3 + 1] + fn[i * 3 + 2] * fn[j * 3 + 2];
                    if (dot < cos) continue;
                    nx += fn[j * 3] * fa[j]; ny += fn[j * 3 + 1] * fa[j]; nz += fn[j * 3 + 2] * fa[j];
                }
                double len = Math.Sqrt(nx * nx + ny * ny + nz * nz);
                if (len > 0) { nx /= len; ny /= len; nz /= len; } else { nx = fn[i * 3]; ny = fn[i * 3 + 1]; nz = fn[i * 3 + 2]; }
                prim.AddVertex(p[v * 3], p[v * 3 + 1], p[v * 3 + 2], (float)nx, (float)ny, (float)nz);
            }
    }

    const int MaxValence = 48;

    static void Prune(List<int> list, bool[] alive) => list.RemoveAll(ti => !alive[ti]);

    static bool Adjacent(int[] t, List<int>[] vt, bool[] alive, int u, int v)
    {
        // Scan the shorter list.
        var list = vt[u].Count <= vt[v].Count ? vt[u] : vt[v];
        int other = list == vt[u] ? v : u;
        foreach (int ti in list)
            if (alive[ti] && (t[ti * 3] == other || t[ti * 3 + 1] == other || t[ti * 3 + 2] == other)) return true;
        return false;
    }

    /// Rejects collapses that would flip or squash a remaining triangle.
    static bool CanCollapse(float[] pos, int[] t, List<int>[] vt, bool[] alive, int u, int v)
    {
        foreach (int ti in vt[u])
        {
            if (!alive[ti]) continue;
            int i0 = ti * 3;
            int a = t[i0], b = t[i0 + 1], c = t[i0 + 2];
            if (a == v || b == v || c == v) continue; // removed by the collapse
            Normal(pos, a, b, c, out double ox, out double oy, out double oz, out double oldArea);
            int na = a == u ? v : a, nb = b == u ? v : b, nc = c == u ? v : c;
            Normal(pos, na, nb, nc, out double nx, out double ny, out double nz, out double newArea);
            if (newArea <= oldArea * 1e-3) return false;
            if (ox * nx + oy * ny + oz * nz < 0.3) return false;
        }
        return true;
    }

    /// [x y z 1] Q [x y z 1]áµ€ at v for Q(u)+Q(v), divided by the summed plane area: the area-weighted mean squared
    /// distance (mÂ²) from v to the planes of the triangles around u and v.
    static double Cost(double[] q, double[] area, int u, int v, float[] p)
    {
        double x = p[v * 3], y = p[v * 3 + 1], z = p[v * 3 + 2];
        int a = u * 10, b = v * 10;
        double q0 = q[a] + q[b], q1 = q[a + 1] + q[b + 1], q2 = q[a + 2] + q[b + 2], q3 = q[a + 3] + q[b + 3];
        double q4 = q[a + 4] + q[b + 4], q5 = q[a + 5] + q[b + 5], q6 = q[a + 6] + q[b + 6];
        double q7 = q[a + 7] + q[b + 7], q8 = q[a + 8] + q[b + 8], q9 = q[a + 9] + q[b + 9];
        double e = q0 * x * x + 2 * q1 * x * y + 2 * q2 * x * z + 2 * q3 * x
                 + q4 * y * y + 2 * q5 * y * z + 2 * q6 * y
                 + q7 * z * z + 2 * q8 * z + q9;
        double w = area[u];
        return w > 0 ? Math.Max(0, e) / w : double.MaxValue;
    }

    static void AddPlane(double[] q, double[] areaSum, int v, double a, double b, double c, double d, double w)
    {
        areaSum[v] += w;
        int o = v * 10;
        q[o] += w * a * a; q[o + 1] += w * a * b; q[o + 2] += w * a * c; q[o + 3] += w * a * d;
        q[o + 4] += w * b * b; q[o + 5] += w * b * c; q[o + 6] += w * b * d;
        q[o + 7] += w * c * c; q[o + 8] += w * c * d; q[o + 9] += w * d * d;
    }

    static void Normal(float[] p, int a, int b, int c, out double nx, out double ny, out double nz, out double area)
    {
        double ax = p[b * 3] - p[a * 3], ay = p[b * 3 + 1] - p[a * 3 + 1], az = p[b * 3 + 2] - p[a * 3 + 2];
        double bx = p[c * 3] - p[a * 3], by = p[c * 3 + 1] - p[a * 3 + 1], bz = p[c * 3 + 2] - p[a * 3 + 2];
        nx = ay * bz - az * by; ny = az * bx - ax * bz; nz = ax * by - ay * bx;
        double len = Math.Sqrt(nx * nx + ny * ny + nz * nz);
        area = len * 0.5;
        if (len > 0) { nx /= len; ny /= len; nz /= len; }
    }

    /// long.GetHashCode() is hi ^ lo, i.e. a ^ b for edge keys: neighbouring vertex pairs collide massively.
    sealed class EdgeKeyComparer : IEqualityComparer<long>
    {
        public static readonly EdgeKeyComparer Instance = new EdgeKeyComparer();
        public bool Equals(long x, long y) => x == y;
        public int GetHashCode(long k)
        {
            ulong h = (ulong)k * 0x9E3779B97F4A7C15UL;
            return (int)(h >> 32) ^ (int)h;
        }
    }

    static long EdgeKey(int a, int b) => a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;

    sealed class MinHeap
    {
        (double cost, int u, int v, int ver)[] items;
        public int Count { get; private set; }
        public MinHeap(int capacity) { items = new (double, int, int, int)[Math.Max(16, capacity)]; }

        public void Push(double cost, int u, int v, int ver)
        {
            if (Count == items.Length) Array.Resize(ref items, items.Length * 2);
            int i = Count++;
            items[i] = (cost, u, v, ver);
            while (i > 0)
            {
                int p = (i - 1) / 2;
                if (items[p].cost <= items[i].cost) break;
                (items[p], items[i]) = (items[i], items[p]);
                i = p;
            }
        }

        public (double cost, int u, int v, int ver) Pop()
        {
            var top = items[0];
            items[0] = items[--Count];
            int i = 0;
            while (true)
            {
                int l = i * 2 + 1, r = l + 1, m = i;
                if (l < Count && items[l].cost < items[m].cost) m = l;
                if (r < Count && items[r].cost < items[m].cost) m = r;
                if (m == i) break;
                (items[m], items[i]) = (items[i], items[m]);
                i = m;
            }
            return top;
        }
    }
}
