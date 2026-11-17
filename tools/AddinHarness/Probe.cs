using SolidWorksQuestSender;

/// Writes a GLB with `parts` bumpy spheres of about `verts` vertices each, to time how the viewer copes with
/// big per-part meshes (it cooks a convex collider per part when there are at most 96).
static class Probe
{
    public static void Run(string output, int verts, int parts)
    {
        int n = Math.Max(8, (int)Math.Sqrt(verts / 2.0));
        var list = new List<MeshPart>();
        for (int k = 0; k < parts; k++)
        {
            var prim = new Primitive { Color = new[] { 0.2f + 0.6f * k / Math.Max(1, parts), 0.4f, 0.8f, 1f } };
            var rnd = new Random(k);
            var pts = new (float x, float y, float z)[(n + 1) * (2 * n + 1)];
            for (int i = 0; i <= n; i++)
                for (int j = 0; j <= 2 * n; j++)
                {
                    double th = Math.PI * i / n, ph = Math.PI * j / n, r = 0.05 * (1 + 0.02 * rnd.NextDouble());
                    pts[i * (2 * n + 1) + j] = ((float)(r * Math.Sin(th) * Math.Cos(ph)), (float)(r * Math.Cos(th)), (float)(r * Math.Sin(th) * Math.Sin(ph)));
                }
            void V(int i, int j) { var p = pts[i * (2 * n + 1) + j]; float l = MathF.Sqrt(p.x * p.x + p.y * p.y + p.z * p.z); prim.AddVertex(p.x + 0.12f * k, p.y, p.z, p.x / l, p.y / l, p.z / l); }
            for (int i = 0; i < n; i++)
                for (int j = 0; j < 2 * n; j++)
                {
                    V(i, j); V(i + 1, j); V(i + 1, j + 1);
                    V(i, j); V(i + 1, j + 1); V(i, j + 1);
                }
            var mesh = new MeshData { Name = "probe" + k };
            mesh.Primitives.Add(prim);
            list.Add(new MeshPart { Name = "probe" + k, Id = "p" + k, Mesh = mesh, File = "Probe.SLDPRT", Config = "Default", Material = "AISI 1020", Body = "Boss-Extrude1", Path = new[] { "Sub-1", "probe" + k }, MassKg = 0.152, DensityKgM3 = 7900, VolumeM3 = 1.9e-5, AreaM2 = 0.0031, Instances = 4 });
        }
        var glb = GlbWriter.Write("probe", list);
        File.WriteAllBytes(output, glb);
        Console.WriteLine($"{parts} part(s), {list[0].Mesh.Primitives[0].Positions.Count / 3:N0} vertices each, {glb.Length / 1e6:0.0} MB");
    }
}
