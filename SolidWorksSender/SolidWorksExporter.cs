using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace SolidWorksQuestSender;

public sealed class ExportResult
{
    public string Name;
    public string Key;          // fingerprint of the exported model, to skip unchanged sends
    public byte[] Glb;
    public string PartInfo;     // base64 JSON {"id": {"m": kg, "v": m3}} or ""
    public int Hidden;          // hidden components / bodies skipped
    public int Lightweight;     // lightweight components skipped (no geometry loaded)
    public int Parts;
    public int Instanced;       // parts that reuse another part's mesh
    public int Groups;          // parts sent to the viewer after grouping by sub-assembly, 0 if not grouped
    public int Triangles;       // unique triangles actually sent
    public long Drawn, DrawnBefore; // triangles drawn in the headset (every instance), after / before simplifying
    public double ToleranceMm;  // simplification tolerance used, 0 if none was needed
    public double Seconds;

    public string Summary =>
        $"{Name}: {Parts} parts" + (Instanced > 0 ? $" ({Instanced} reused)" : "") +
        (Groups > 0 ? $" grouped into {Groups}" : "") +
        $", {Drawn:N0} triangles drawn" +
        (ToleranceMm > 0 ? $" (simplified from {DrawnBefore:N0} at {ToleranceMm:0.##} mm)" : "") +
        $", {Glb.Length / 1024:N0} KB in {Seconds:0.0} s" +
        (Hidden > 0 ? $", {Hidden} hidden skipped" : "") + (Lightweight > 0 ? $", {Lightweight} lightweight skipped" : "");

    public string Timings;      // where the time went, for the log
}

/// Accumulates time per step of an export.
sealed class Timings
{
    readonly Dictionary<string, Stopwatch> watches = new Dictionary<string, Stopwatch>();
    public Step Measure(string name)
    {
        if (!watches.TryGetValue(name, out var w)) watches[name] = w = new Stopwatch();
        return new Step(w);
    }
    public override string ToString() =>
        string.Join(", ", watches.OrderByDescending(kv => kv.Value.Elapsed).Select(kv => $"{kv.Key} {kv.Value.Elapsed.TotalSeconds:0.00}s"));

    public readonly struct Step : IDisposable
    {
        readonly Stopwatch w;
        public Step(Stopwatch w) { this.w = w; w.Start(); }
        public void Dispose() => w.Stop();
    }
}

/// Turns the active SolidWorks part or assembly into a GLB. Must run on SolidWorks' main thread (add-in callbacks do).
public static class SolidWorksExporter
{
    public static ExportResult ExportActive(ISldWorks sw)
    {
        var clock = Stopwatch.StartNew();
        var doc = (ModelDoc2)sw.ActiveDoc;
        if (doc == null) throw new InvalidOperationException("No document is open in SolidWorks.");

        var type = (swDocumentTypes_e)doc.GetType();
        var config = (Configuration)doc.GetActiveConfiguration();
        string title = Path.GetFileNameWithoutExtension(doc.GetTitle() ?? "SolidWorks model");
        string configName = config?.Name;
        string displayName = string.IsNullOrEmpty(configName) || configName == "Default" ? title : $"{title} ({configName})";

        var ctx = new Context();
        if (type == swDocumentTypes_e.swDocPART) ExportPart(ctx, doc, title);
        else if (type == swDocumentTypes_e.swDocASSEMBLY) ExportAssembly(ctx, (AssemblyDoc)doc);
        else throw new InvalidOperationException("Open a part or an assembly (drawings can't be sent).");

        if (ctx.Parts.Count == 0)
        {
            string why = ctx.Lightweight > 0 ? $" {ctx.Lightweight} lightweight component(s) need to be resolved first." : "";
            throw new InvalidOperationException("Nothing visible to send." + why);
        }

        long drawnBefore = Drawn(ctx.Parts);
        double tolerance;
        using (ctx.T.Measure("simplify")) tolerance = Decimator.FitBudget(ctx.Parts, Settings.Load().MaxTriangles);

        // The viewer gives parts their own colliders (so hands can grab them) only up to 96 parts.
        // How many times each body of each part file + configuration appears (for the viewer's info panel).
        foreach (var same in ctx.Parts.Where(p => p.File != null).GroupBy(p => p.File + "|" + p.Config + "|" + p.Body))
        {
            int n = same.Count();
            foreach (var p in same) p.Instances = n;
        }

        List<MeshPart> parts;
        var mass = ctx.Mass;
        using (ctx.T.Measure("group")) parts = Grouping.Group(ctx.Parts, Grouping.MaxParts, ref mass);

        byte[] glb;
        using (ctx.T.Measure("glb")) glb = GlbWriter.Write(displayName, parts, ctx.Motions != null ? Motion.PathsJson(ctx.Motions) : null);
        string key;
        using (var sha = SHA256.Create())
            key = BitConverter.ToString(sha.ComputeHash(glb), 0, 16).Replace("-", "");

        return new ExportResult
        {
            Name = displayName,
            Key = key,
            Glb = glb,
            PartInfo = EncodePartInfo(mass),
            Hidden = ctx.Hidden,
            Lightweight = ctx.Lightweight,
            Parts = ctx.Parts.Count,
            Groups = parts.Count < ctx.Parts.Count ? parts.Count : 0,
            Instanced = ctx.Instanced,
            Triangles = ctx.Parts.Select(p => p.Mesh).Distinct().Sum(m => m.TriangleCount),
            Drawn = Drawn(ctx.Parts),
            DrawnBefore = drawnBefore,
            ToleranceMm = tolerance,
            Seconds = clock.Elapsed.TotalSeconds,
            Timings = ctx.T.ToString() + (Grouping.LastNote.Length > 0 ? " | " + Grouping.LastNote : "") + (ctx.Skipped.Count > 0 ? " | skipped: " + ctx.SkipReport() : ""),
        };
    }

    sealed class Context
    {
        public readonly List<MeshPart> Parts = new List<MeshPart>();
        public readonly List<(string id, double? m, double v)> Mass = new List<(string, double?, double)>();
        public readonly Dictionary<string, double?> Density = new Dictionary<string, double?>(); // per part file + config
        public readonly Dictionary<string, string> MaterialName = new Dictionary<string, string>(); // same keys
        public string File, Config, Material; // of the bodies being added
        public PartMotion Motion;            // of the component being added (null for a part document)
        public double[] MotionXf;            // its transform to the top level
        public int MotionGroup;               // parts with the same number move together
        public Dictionary<string, (PartMotion motion, int group)> Motions; // per top-level component
        public readonly Dictionary<string, Dictionary<string, double[]>> FeatureColors = new Dictionary<string, Dictionary<string, double[]>>();
        public readonly Dictionary<string, (MeshData mesh, double volume, double area)> Meshes = new Dictionary<string, (MeshData, double, double)>();
        public int Hidden, Lightweight, Instanced;
        public readonly Dictionary<string, (int count, List<string> examples)> Skipped = new Dictionary<string, (int, List<string>)>();

        public void Skip(string reason, string example)
        {
            Hidden++;
            if (!Skipped.TryGetValue(reason, out var s)) Skipped[reason] = s = (0, new List<string>());
            if (s.examples.Count < 5) s.examples.Add(example);
            Skipped[reason] = (s.count + 1, s.examples);
        }

        public string SkipReport() => string.Join("; ", Skipped.Select(kv => $"{kv.Value.count} {kv.Key}, e.g. {string.Join(", ", kv.Value.examples)}"));
        public readonly Timings T = new Timings();
        public string NextId() => "p" + Parts.Count;
        public string[] Path; // component path of the bodies being added
    }

    static void ExportPart(Context ctx, ModelDoc2 doc, string title)
    {
        var part = (PartDoc)doc;
        var all = AsArray<Body2>(part.GetBodies2((int)swBodyType_e.swSolidBody, false));
        var visible = AsArray<Body2>(part.GetBodies2((int)swBodyType_e.swSolidBody, true));
        ctx.Hidden += Math.Max(0, all.Length - visible.Length);
        double? density = Density(ctx, doc, "");
        SetSource(ctx, doc, "");
        double[] docColor = AsDoubles(doc.MaterialPropertyValues);

        var featureColors = FeatureColors(ctx, doc);
        foreach (var body in visible)
        {
            string name = visible.Length == 1 ? title : body.Name;
            AddBody(ctx, body, name, null, docColor, density, null, featureColors);
        }
    }

    static void ExportAssembly(Context ctx, AssemblyDoc asm)
    {
        foreach (var comp in AsArray<Component2>(asm.GetComponents(false)))
        {
            Body2[] bodies;
            using (ctx.T.Measure("components"))
            {
                // Not IsSuppressed(): it is also true for lightweight components, which we want (resolved) instead.
                if (comp.GetSuppression2() == (int)swComponentSuppressionState_e.swComponentSuppressed) continue;
                bool leaf = AsArray<Component2>(comp.GetChildren()).Length == 0;
                string hiddenBy = HiddenBy(comp);
                if (hiddenBy != null)
                {
                    if (leaf) ctx.Skip(hiddenBy == comp.Name2 ? "component hidden" : "parent hidden", comp.Name2 + (hiddenBy == comp.Name2 ? "" : " (by " + hiddenBy + ")"));
                    continue;
                }
                try { if (comp.IsEnvelope()) continue; } catch (Exception) { }
                bodies = AsArray<Body2>(comp.GetBodies2((int)swBodyType_e.swSolidBody));
            }
            if (bodies.Length == 0)
            {
                var suppression = (swComponentSuppressionState_e)comp.GetSuppression2();
                if (suppression == swComponentSuppressionState_e.swComponentLightweight
                    || suppression == swComponentSuppressionState_e.swComponentFullyLightweight) ctx.Lightweight++;
                continue; // sub-assemblies have no bodies of their own; their parts are listed separately
            }

            ModelDoc2 model;
            double[] partColor, compColor, xf = null;
            double? density;
            Dictionary<string, double[]> featureColors;
            using (ctx.T.Measure("part info"))
            {
                model = comp.GetModelDoc2() as ModelDoc2;
                partColor = model != null ? AsDoubles(model.MaterialPropertyValues) : null;
                compColor = ComponentAppearance(comp);
                density = model != null ? Density(ctx, model, comp.ReferencedConfiguration ?? "") : null;
                featureColors = model != null ? FeatureColors(ctx, model) : null;
                // Component bodies are tessellated in the part's own space; place them with the component transform
                // (to the top-level assembly, including any sub-assemblies).
                try { xf = ((MathTransform)comp.Transform2)?.ArrayData as double[]; } catch (Exception) { }
            }

            // Instances of the same part file + configuration (with the same assembly appearance) share their meshes.
            string meshKey = model != null
                ? (model.GetPathName() ?? model.GetTitle()) + "|" + comp.ReferencedConfiguration + "|" + ColorKey(compColor)
                : null;

            string baseName = comp.Name2 ?? "Component";
            ctx.Path = ComponentPath(comp);
            if (model != null) SetSource(ctx, model, comp.ReferencedConfiguration ?? "");
            else ctx.File = ctx.Config = ctx.Material = null;
            using (ctx.T.Measure("mates"))
            {
                // The motion of its top-level component's group (sub-assemblies move as one piece).
                if (ctx.Motions == null) ctx.Motions = Motion.Analyze((ModelDoc2)asm, Log.Write);
                if (ctx.Motions.TryGetValue(Motion.Top(comp.Name2) ?? "", out var mg)) { ctx.Motion = mg.motion; ctx.MotionGroup = mg.group; }
                else { ctx.Motion = null; ctx.MotionGroup = 0; }
                ctx.MotionXf = xf;
            }
            foreach (var body in bodies)
            {
                if (!body.Visible) { ctx.Skip("body hidden", $"{baseName} / {body.Name}"); continue; }
                string name = bodies.Length == 1 ? baseName : $"{baseName} / {body.Name}";
                AddBody(ctx, body, name, compColor, partColor, density, xf, featureColors, meshKey);
            }
        }
    }

    /// File name, configuration and material of the bodies about to be added (call after Density, which caches it).
    static void SetSource(Context ctx, ModelDoc2 model, string config)
    {
        string path = model.GetPathName();
        ctx.File = string.IsNullOrEmpty(path) ? model.GetTitle() : System.IO.Path.GetFileName(path);
        ctx.Config = string.IsNullOrEmpty(config) ? null : config;
        ctx.MaterialName.TryGetValue((path ?? model.GetTitle()) + "|" + config, out ctx.Material);
    }

    /// kg/m³ of the part's material, or null if no material is assigned (then the viewer shows volume only).
    static double? Density(Context ctx, ModelDoc2 model, string config)
    {
        string key = (model.GetPathName() ?? model.GetTitle()) + "|" + config;
        if (ctx.Density.TryGetValue(key, out var cached)) return cached;
        double? density = null;
        try
        {
            string material = model is PartDoc pd ? pd.GetMaterialPropertyName2(config, out string _) : null;
            ctx.MaterialName[key] = string.IsNullOrEmpty(material) ? null : material;
            if (!string.IsNullOrEmpty(material))
            {
                var mp = (IMassProperty2)model.Extension.CreateMassProperty2();
                if (mp != null && mp.Volume > 0) density = mp.Mass / mp.Volume;
            }
        }
        catch (Exception e) { Log.Write($"No density for {key}: {e.Message}"); }
        ctx.Density[key] = density;
        return density;
    }

    /// Appearances applied to features of a part (feature name -> colour), or null if there are none.
    static Dictionary<string, double[]> FeatureColors(Context ctx, ModelDoc2 model)
    {
        string key = model.GetPathName() ?? model.GetTitle();
        if (ctx.FeatureColors.TryGetValue(key, out var cached)) return cached;
        Dictionary<string, double[]> map = null;
        try
        {
            for (var f = model.FirstFeature() as Feature; f != null; f = f.GetNextFeature() as Feature)
            {
                if (f.GetMaterialPropertyValues2((int)swInConfigurationOpts_e.swThisConfiguration, null) is double[] v && v.Length >= 3 && v[0] >= 0)
                    (map = map ?? new Dictionary<string, double[]>())[f.Name] = v;
            }
        }
        catch (Exception e) { Log.Write($"Feature colours of {key}: {e.Message}"); }
        ctx.FeatureColors[key] = map;
        return map;
    }

    static double[] FeatureColor(Face2 face, Dictionary<string, double[]> featureColors)
    {
        if (featureColors == null) return null;
        try
        {
            if (face.GetFeature() is Feature f && featureColors.TryGetValue(f.Name, out var v)) return v;
        }
        catch (Exception) { }
        return null;
    }

    /// Name of the component that hides this one (itself or a parent sub-assembly), or null if visible.
    /// IsHidden(false): with true, SolidWorks also counts suppressed and lightweight components as hidden.
    static string HiddenBy(Component2 comp)
    {
        for (var c = comp; c != null; c = c.GetParent() as Component2)
            if (c.IsHidden(false)) return c.Name2;
        return null;
    }

    /// Names of the component and its parent sub-assemblies, top level first.
    static string[] ComponentPath(Component2 comp)
    {
        var names = new List<string>();
        for (var c = comp; c != null; c = c.GetParent() as Component2) names.Add(c.Name2 ?? "?");
        names.Reverse();
        return names.ToArray();
    }

    /// Appearance applied to the component in the assembly (overrides the part's own), or null.
    static double[] ComponentAppearance(Component2 comp)
    {
        try
        {
            var v = AsDoubles(comp.GetMaterialPropertyValues2((int)swInConfigurationOpts_e.swThisConfiguration, null));
            if (v != null && v.Length >= 3 && v[0] >= 0) return v;
        }
        catch (Exception) { }
        return null;
    }

    /// xf: SolidWorks MathTransform.ArrayData (rotation 0-8, translation 9-11, scale 12), or null.
    /// Points are row vectors: p' = (p · R) · scale + T.
    static void AddBody(Context ctx, Body2 body, string name, double[] overrideColor, double[] fallbackColor, double? density,
                        double[] xf = null, Dictionary<string, double[]> featureColors = null, string meshKey = null)
    {
        var node = new MeshPart { Name = name, Id = ctx.NextId(), Matrix = NodeMatrix(xf), Path = ctx.Path,
                              File = ctx.File, Config = ctx.Config, Material = ctx.Material, Body = body.Name, DensityKgM3 = density };
        SetMotion(node, ctx.Motion, ctx.MotionXf);
        node.MotionGroup = ctx.MotionGroup;
        string key = meshKey != null ? meshKey + "|" + body.Name : null;

        if (key != null && ctx.Meshes.TryGetValue(key, out var shared))
        {
            node.Mesh = shared.mesh;
            ctx.Parts.Add(node);
            ctx.Instanced++;
            SetSpecs(node, density, shared.volume, shared.area);
            if (shared.volume > 0) ctx.Mass.Add((node.Id, density.HasValue ? density * shared.volume : null, shared.volume));
            return;
        }

        node.Mesh = BuildMesh(ctx, body, name, overrideColor, fallbackColor, featureColors);
        double volume = 0, area = 0;
        using (ctx.T.Measure("mass"))
        {
            try
            {
                // [CoMx, CoMy, CoMz, volume, area, mass, ...]; density 1 so only the volume matters.
                if (body.GetMassProperties(1.0) is double[] mp && mp.Length > 4 && mp[3] > 0) { volume = mp[3]; area = mp[4]; }
            }
            catch (Exception) { /* mass is optional */ }
        }
        if (key != null) ctx.Meshes[key] = (node.Mesh, volume, area);
        SetSpecs(node, density, volume, area);
        if (node.Mesh.TriangleCount == 0) return;
        ctx.Parts.Add(node);
        if (volume > 0) ctx.Mass.Add((node.Id, density.HasValue ? density * volume : null, volume));
    }

    /// Motion axis from top-level space into the node's own space (the viewer applies the node's transform).
    static void SetMotion(MeshPart node, PartMotion m, double[] xf)
    {
        if (m == null) return;
        node.MotionKind = m.Kind;
        node.MotionNote = m.Note;
        node.Freedoms = m.Freedoms;
        node.MateCount = m.Mates;
        node.SolidWorksFixed = m.SolidWorksFixed;
        if (m.Point != null && m.Direction != null)
        {
            node.MotionPoint = Motion.XfInvPoint(xf, m.Point);
            node.MotionDirection = Motion.XfInvDir(xf, m.Direction);
        }
        // Every mode it moves in, as a screw in the node's own space: [id, rad/t, m/t, point xyz, direction xyz, tMin, tMax].
        node.Modes = new List<double[]>();
        foreach (var (mode, twist) in m.Modes)
        {
            Motion.Screw(twist, out var p, out var d, out double angleRate, out double slideRate);
            var pl = Motion.XfInvPoint(xf, p);
            var dl = Motion.XfInvDir(xf, d);
            node.Modes.Add(new[] { mode.Id, angleRate, slideRate, pl[0], pl[1], pl[2], dl[0], dl[1], dl[2], mode.Min, mode.Max });
        }
        node.Drive = m.Drive.Select(x => x.Id).ToArray();
    }

    static void SetSpecs(MeshPart node, double? density, double volume, double area)
    {
        node.VolumeM3 = volume;
        node.AreaM2 = area;
        node.MassKg = density.HasValue && volume > 0 ? density * volume : null;
    }

    /// SolidWorks transform (row vectors: p' = (p · R) · s + T) as a glTF column-major matrix; null for identity.
    static float[] NodeMatrix(double[] xf)
    {
        if (xf == null || xf.Length < 13 || IsIdentity(xf)) return null;
        float s = (float)xf[12];
        return new[]
        {
            s * (float)xf[0], s * (float)xf[1], s * (float)xf[2], 0f,
            s * (float)xf[3], s * (float)xf[4], s * (float)xf[5], 0f,
            s * (float)xf[6], s * (float)xf[7], s * (float)xf[8], 0f,
            (float)xf[9], (float)xf[10], (float)xf[11], 1f,
        };
    }

    static string ColorKey(double[] c) =>
        c == null ? "" : string.Join(",", c.Take(8).Select(v => v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)));

    /// Tessellates one body in its part's own space.
    static MeshData BuildMesh(Context ctx, Body2 body, string name, double[] overrideColor, double[] fallbackColor,
                              Dictionary<string, double[]> featureColors)
    {
        var bodyColor = AsDoubles(body.MaterialPropertyValues2) ?? fallbackColor;
        var mesh = new MeshData { Name = name };
        var byColor = new Dictionary<string, Primitive>();

        Face2[] faces;
        using (ctx.T.Measure("faces")) faces = AsArray<Face2>(body.GetFaces());
        foreach (var face in faces)
        {
            float[] tris, norms;
            using (ctx.T.Measure("tessellation")) tris = face.GetTessTriangles(true) as float[];
            if (tris == null || tris.Length < 9) continue;
            using (ctx.T.Measure("normals")) norms = face.GetTessNorms() as float[];
            bool haveNormals = norms != null && norms.Length == tris.Length;

            double[] props;
            using (ctx.T.Measure("colours"))
                props = overrideColor ?? AsDoubles(face.MaterialPropertyValues) ?? FeatureColor(face, featureColors) ?? bodyColor;
            var prim = PrimitiveFor(byColor, mesh, props);

            using var mesher = ctx.T.Measure("meshing");
            for (int i = 0; i + 8 < tris.Length; i += 9)
            {
                float nx = 0, ny = 0, nz = 0;
                if (!haveNormals) FaceNormal(tris, i, out nx, out ny, out nz);
                for (int v = 0; v < 3; v++)
                {
                    int k = i + v * 3;
                    if (haveNormals) { nx = norms[k]; ny = norms[k + 1]; nz = norms[k + 2]; }
                    prim.AddVertex(tris[k], tris[k + 1], tris[k + 2], nx, ny, nz);
                }
            }
        }
        return mesh;
    }

    static Primitive PrimitiveFor(Dictionary<string, Primitive> byColor, MeshData mesh, double[] props)
    {
        // SolidWorks material values: R, G, B, ambient, diffuse, specular, shininess, transparency, emission.
        float r = 0.8f, g = 0.8f, b = 0.8f, a = 1f, rough = 0.5f;
        if (props != null && props.Length >= 3 && props[0] >= 0)
        {
            r = (float)props[0]; g = (float)props[1]; b = (float)props[2];
            if (props.Length >= 8) a = Clamp(1 - (float)props[7], 0.05f, 1f);
            if (props.Length >= 7) rough = Clamp(1 - (float)props[6], 0.15f, 0.9f);
        }
        string key = $"{r:0.000},{g:0.000},{b:0.000},{a:0.00},{rough:0.00}";
        if (!byColor.TryGetValue(key, out var prim))
        {
            prim = new Primitive { Color = new[] { Linear(r), Linear(g), Linear(b), a }, Roughness = rough };
            byColor[key] = prim;
            mesh.Primitives.Add(prim);
        }
        return prim;
    }

    static bool IsIdentity(double[] x) =>
        Math.Abs(x[0] - 1) < 1e-9 && Math.Abs(x[4] - 1) < 1e-9 && Math.Abs(x[8] - 1) < 1e-9
        && Math.Abs(x[1]) < 1e-9 && Math.Abs(x[2]) < 1e-9 && Math.Abs(x[3]) < 1e-9
        && Math.Abs(x[5]) < 1e-9 && Math.Abs(x[6]) < 1e-9 && Math.Abs(x[7]) < 1e-9
        && Math.Abs(x[9]) < 1e-12 && Math.Abs(x[10]) < 1e-12 && Math.Abs(x[11]) < 1e-12 && Math.Abs(x[12] - 1) < 1e-9;

    static float Clamp(float v, float lo, float hi) => v < lo ? lo : v > hi ? hi : v;
    static float Linear(float c) => c <= 0.04045f ? c / 12.92f : (float)Math.Pow((c + 0.055f) / 1.055f, 2.4f);

    static void FaceNormal(float[] t, int i, out float nx, out float ny, out float nz)
    {
        float ax = t[i + 3] - t[i], ay = t[i + 4] - t[i + 1], az = t[i + 5] - t[i + 2];
        float bx = t[i + 6] - t[i], by = t[i + 7] - t[i + 1], bz = t[i + 8] - t[i + 2];
        nx = ay * bz - az * by; ny = az * bx - ax * bz; nz = ax * by - ay * bx;
        float len = (float)Math.Sqrt(nx * nx + ny * ny + nz * nz);
        if (len > 0) { nx /= len; ny /= len; nz /= len; }
    }

    static string EncodePartInfo(List<(string id, double? m, double v)> mass)
    {
        if (mass.Count == 0) return "";
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var sb = new StringBuilder("{");
        foreach (var (id, m, v) in mass)
        {
            if (sb.Length > 1) sb.Append(',');
            sb.Append('"').Append(id).Append("\":{");
            if (m.HasValue) sb.Append("\"m\":").Append(m.Value.ToString("G6", inv)).Append(',');
            sb.Append("\"v\":").Append(v.ToString("G6", inv)).Append('}');
        }
        sb.Append('}');
        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(sb.ToString()));
        // The viewer accepts 32 KB of headers in total; drop the (optional) mass info rather than fail.
        return b64.Length > 24 * 1024 ? "" : b64;
    }

    public static string DecodePartInfo(string b64) =>
        string.IsNullOrEmpty(b64) ? "" : Encoding.UTF8.GetString(Convert.FromBase64String(b64));

    static long Drawn(IEnumerable<MeshPart> parts) => parts.Sum(p => (long)p.TriangleCount);

    static T[] AsArray<T>(object o) where T : class =>
        o is object[] arr ? arr.OfType<T>().ToArray() : new T[0];

    static double[] AsDoubles(object o) => o as double[];
}
