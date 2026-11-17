// Test harness for the SolidWorks Quest Sender add-in. Note: SolidWorks answers API calls far more slowly while
// it is servicing a call from another process, so timings here are much worse than from the ribbon button.
//   AddinHarness export <file.glb>          installed add-in exports the active document
//   AddinHarness send [force]               installed add-in sends the active document to the viewer
//   AddinHarness dev <dll> <file.glb>       installed add-in loads a freshly built add-in DLL and runs its exporter
//   AddinHarness local <file.glb>           runs the exporter source in this process
//   AddinHarness open <file.sldasm|sldprt>  opens a document
using System.Runtime.InteropServices;
using SolidWorks.Interop.sldworks;

[DllImport("ole32.dll")] static extern int CLSIDFromProgID([MarshalAs(UnmanagedType.LPWStr)] string p, out Guid c);
[DllImport("oleaut32.dll")] static extern int GetActiveObject(ref Guid c, IntPtr r, [MarshalAs(UnmanagedType.IUnknown)] out object o);

if (args.Length == 0) { Console.WriteLine("Usage: export|send|dev|local|open|diag|shrink ..."); return 2; }
if (args[0] == "probe") { Probe.Run(args[1], int.Parse(args[2]), int.Parse(args[3])); return 0; }
if (args[0] == "shrink") { Shrink.Run(args[1], args[2], args.Length > 3 ? int.Parse(args[3]) : 1500000); return 0; }
CLSIDFromProgID("SldWorks.Application", out var clsid);
if (GetActiveObject(ref clsid, IntPtr.Zero, out var obj) != 0) { Console.WriteLine("SolidWorks not running"); return 1; }
var sw = (ISldWorks)obj;
var clock = System.Diagnostics.Stopwatch.StartNew();

switch (args[0])
{
    case "open":
    {
        int err = 0, warn = 0;
        int type = args[1].EndsWith(".SLDASM", StringComparison.OrdinalIgnoreCase) ? 2 : 1;
        var doc = sw.OpenDoc6(args[1], type, 1 /* silent */, "", ref err, ref warn);
        Console.WriteLine(doc != null ? "opened " + ((ModelDoc2)doc).GetTitle() : $"open failed err={err}");
        return doc != null ? 0 : 1;
    }
    case "diag":
    {
        // Why are components/bodies skipped as hidden? Counts per check, plus a few examples.
        var doc = (ModelDoc2)sw.ActiveDoc;
        Console.WriteLine($"{doc.GetTitle()} readOnly={doc.IsOpenedReadOnly()} viewOnly={doc.IsOpenedViewOnly()}");
        var comps = ((object[])((AssemblyDoc)doc).GetComponents(false)).Cast<Component2>().ToArray();
        int leaves = 0, suppressed = 0, hiddenSelf = 0, hiddenTree = 0, visProp0 = 0, bodiesTotal = 0, bodiesInvisible = 0, noBodies = 0;
        var examples = new List<string>();
        foreach (var c in comps)
        {
            bool leaf = ((object[])c.GetChildren() ?? Array.Empty<object>()).Length == 0;
            if (!leaf) continue;
            leaves++;
            if (c.IsSuppressed()) { suppressed++; continue; }
            bool hs = c.IsHidden(true);
            bool ht = false;
            for (var p = c; p != null; p = p.GetParent() as Component2) if (p.IsHidden(true)) { ht = true; break; }
            if (hs) hiddenSelf++;
            if (ht) hiddenTree++;
            if (c.Visible == 0) visProp0++;
            var bodies = (object[])c.GetBodies2(0) ?? Array.Empty<object>();
            if (bodies.Length == 0) noBodies++;
            foreach (Body2 b in bodies)
            {
                bodiesTotal++;
                if (!b.Visible) { bodiesInvisible++; if (examples.Count < 8 && !ht) examples.Add($"{c.Name2} / {b.Name} (comp hidden={hs}, comp.Visible={c.Visible})"); }
            }
        }
        foreach (var c in comps.Where(c => c.IsHidden(true) && ((object[])c.GetChildren() ?? Array.Empty<object>()).Length > 0))
            Console.WriteLine($"hidden sub-assembly: {c.Name2}  IsHidden(false)={c.IsHidden(false)} IsSuppressed={c.IsSuppressed()} Visible={c.Visible} " +
                              $"suppression={c.GetSuppression2()} envelope={c.IsEnvelope()} children={((object[])c.GetChildren()).Length} parent={(c.GetParent() as Component2)?.Name2 ?? "(top)"}");
        Console.WriteLine($"components {comps.Length}, leaf {leaves}, suppressed {suppressed}, IsHidden self {hiddenSelf}, hidden incl. parents {hiddenTree}, Visible==0 {visProp0}");
        Console.WriteLine($"bodies {bodiesTotal}, body.Visible=false {bodiesInvisible}, leaf comps without bodies {noBodies}");
        Console.WriteLine("invisible bodies in visible components, e.g.:\n  " + string.Join("\n  ", examples));
        return 0;
    }
    case "mates":
    {
        // Motion groups from the top-level mates: what moves, how, and which parts move together.
        var doc = (ModelDoc2)sw.ActiveDoc;
        Console.WriteLine(doc.GetTitle());
        var all = SolidWorksQuestSender.Motion.Analyze(doc, Console.WriteLine);
        foreach (var g in all.GroupBy(kv => kv.Value.group).OrderBy(g => g.Key))
        {
            var m = g.First().Value.motion;
            string axis = m.Direction != null ? $" d=({string.Join(",", m.Direction.Select(x => x.ToString("0.###")))}) p=({string.Join(",", m.Point.Select(x => (x * 1000).ToString("0.#")))})mm" : "";
            string drive = m.Drive.Count > 0 ? " drive=" + string.Join("+", m.Drive.Select(d => $"m{d.Id}[{d.Twist.Count} groups" + (double.IsInfinity(d.Min) && double.IsInfinity(d.Max) ? "" : $" t {d.Min:0.###}..{d.Max:0.###}") + "]")) : "";
            Console.WriteLine($"group {g.Key}: {m.Kind} dof={m.Freedoms} mates={m.Mates}{(m.Ground ? " GROUND" : "")}{(m.SolidWorksFixed ? " FIXED" : "")}{drive}{axis}{(m.Note != null ? "  " + m.Note : "")}");
            foreach (var kv in g) Console.WriteLine("    " + kv.Key);
            if (args.Length > 1 && g.Any(kv => kv.Key.Contains(args[1])))
                foreach (var c in ((object[])((AssemblyDoc)doc).GetComponents(true)).Cast<Component2>().Where(c => g.Any(kv => kv.Key == c.Name2)))
                    foreach (var o in (object[])c.GetMates() ?? Array.Empty<object>())
                        if (o is Mate2 mm)
                        {
                            var parts = new List<string>();
                            for (int i = 0; i < mm.GetMateEntityCount(); i++)
                            {
                                var e = mm.MateEntity(i);
                                var prm = e.EntityParams as double[] ?? Array.Empty<double>();
                                string nm = e.ReferenceComponent?.Name2 ?? "?";
                                parts.Add($"{(nm.Length > 24 ? nm.Substring(0, 24) : nm)}:{e.ReferenceType}[{string.Join(",", prm.Take(6).Select(x => x.ToString("0.###")))}]");
                            }
                            Console.WriteLine($"      {(SolidWorks.Interop.swconst.swMateType_e)mm.Type}: {string.Join("  ", parts)}");
                        }
        }
        foreach (var mode in all.Values.SelectMany(v => v.motion.Modes.Select(x => x.mode)).Distinct().Where(x => x.Path != null))
        {
            var p = mode.Path;
            double U(double v) => p.LimitIsAngle ? v * 180 / Math.PI : v * 1000;
            string lim = p.Limit != null ? $" {p.LimitName} {U(p.Limit.First()):0.#}..{U(p.Limit.Last()):0.#}{(p.LimitIsAngle ? "°" : "mm")}" : "";
            static double Ang(double[] r) => Math.Acos(Math.Max(-1, Math.Min(1, (r[0] + r[4] + r[8] - 1) / 2))) * 180 / Math.PI;
            var turns = p.Poses.Select(kv => $"g{kv.Key}:{Ang(kv.Value.First()):0}/{Ang(kv.Value.Last()):0}°");
            Console.WriteLine($"path m{mode.Id}: {p.T.Length} samples, t {p.T.First():0.###}..{p.T.Last():0.###}{lim}, pins drift {p.Drift * 1000:0.###} mm{(p.Period > 0 ? ", round and round" : "")}  turns {string.Join(" ", turns)}");
            if (p.Limit != null) Console.WriteLine("   limit: " + string.Join(" ", Enumerable.Range(0, p.T.Length).Where(i => i % 15 == 0 || p.T[i] == 0 || i < 8 || i >= p.T.Length - 8).Select(i => $"{p.T[i]:0.##}:{U(p.Limit[i]):0.##}")));
        }
        Console.WriteLine($"{clock.Elapsed.TotalSeconds:0.0} s");
        return 0;
    }
    case "comp":
    {
        // Every component (any level) whose name contains args[1]: state, parent, top-level or not.
        var doc = (ModelDoc2)sw.ActiveDoc;
        var tops = new HashSet<string>(((object[])((AssemblyDoc)doc).GetComponents(true)).Cast<Component2>().Select(c => c.Name2));
        foreach (var c in ((object[])((AssemblyDoc)doc).GetComponents(false)).Cast<Component2>().Where(c => c.Name2.Contains(args[1])))
            Console.WriteLine($"{c.Name2}  top={tops.Contains(c.Name2)} suppression={c.GetSuppression2()} hidden={c.IsHidden(true)} " +
                              $"parent={(c.GetParent() as Component2)?.Name2 ?? "-"} path={c.GetPathName()}");
        Console.WriteLine("top-level count " + tops.Count);
        return 0;
    }
    case "why":
    {
        // Which single top-level mate, left out, lets component args[1] move? Tries the mates of every component
        // whose name contains args[2] (default: args[1]).
        var doc = (ModelDoc2)sw.ActiveDoc;
        string target = args[1], near = args.Length > 2 ? args[2] : args[1];
        int Dof() => SolidWorksQuestSender.Motion.Analyze(doc).TryGetValue(target, out var r) ? r.motion.Freedoms : -99;
        Console.WriteLine($"{target}: dof {Dof()} with all mates");
        var names = new List<string>();
        for (var f = doc.FirstFeature() as Feature; f != null; f = f.GetNextFeature() as Feature)
        {
            if (f.GetTypeName2() != "MateGroup") continue;
            for (var s = f.GetFirstSubFeature() as Feature; s != null; s = s.GetNextSubFeature() as Feature)
                if (s.GetSpecificFeature2() is Mate2 m)
                    for (int i = 0; i < m.GetMateEntityCount(); i++)
                        if ((m.MateEntity(i).ReferenceComponent?.Name2 ?? "").Contains(near)) { names.Add(s.Name); break; }
        }
        foreach (var n in names)
        {
            SolidWorksQuestSender.Motion.DebugSkip.Clear();
            SolidWorksQuestSender.Motion.DebugSkip.Add(n);
            int d = Dof();
            Console.WriteLine($"  without {n}: dof {d}");
        }
        SolidWorksQuestSender.Motion.DebugSkip.Clear();
        return 0;
    }
    case "matefeat":
    {
        // Top-level mate features touching components whose name contains args[1]: name, error, entities.
        var doc = (ModelDoc2)sw.ActiveDoc;
        for (var f = doc.FirstFeature() as Feature; f != null; f = f.GetNextFeature() as Feature)
        {
            if (f.GetTypeName2() != "MateGroup") continue;
            for (var s = f.GetFirstSubFeature() as Feature; s != null; s = s.GetNextSubFeature() as Feature)
            {
                if (s.GetSpecificFeature2() is not Mate2 m) continue;
                var ents = new List<string>();
                bool hit = false;
                for (int i = 0; i < m.GetMateEntityCount(); i++)
                {
                    var e = m.MateEntity(i);
                    var rc = e.ReferenceComponent;
                    string nm = rc?.Name2 ?? "(null)";
                    if (nm.Contains(args[1])) hit = true;
                    string root = rc == null ? "" : rc.IsRoot() ? " ROOT" : "";
                    string refc = "";
                    try { if (e.Reference is Entity en) refc = " refComp=" + ((en.GetComponent() as Component2)?.Name2 ?? "(none)"); } catch (Exception ex) { refc = " ref:" + ex.Message; }
                    var prm = e.EntityParams as double[] ?? Array.Empty<double>();
                    ents.Add($"{nm}{root} t={e.ReferenceType} sel={e.ReferenceType2}{refc} [{string.Join(",", prm.Take(6).Select(x => x.ToString("0.###")))}]");
                }
                if (!hit) continue;
                bool w = false;
                int err = s.GetErrorCode2(out w);
                Console.WriteLine($"{s.Name} [{s.GetTypeName2()}] err={err}{(w ? " warn" : "")}\n    " + string.Join("\n    ", ents));
            }
        }
        return 0;
    }
    case "limits":
    {
        // Every distance/angle mate in the top-level Mates folder, with what the API says about limits.
        var doc = (ModelDoc2)sw.ActiveDoc;
        for (var f = doc.FirstFeature() as Feature; f != null; f = f.GetNextFeature() as Feature)
        {
            if (f.GetTypeName2() != "MateGroup") continue;
            for (var s = f.GetFirstSubFeature() as Feature; s != null; s = s.GetNextSubFeature() as Feature)
            {
                string tn = s.GetTypeName2();
                var m = s.GetSpecificFeature2() as Mate2;
                int type = m?.Type ?? -1;
                bool interesting = tn.IndexOf("Limit", StringComparison.OrdinalIgnoreCase) >= 0 || type == 5 || type == 6
                                   || (args.Length > 1 && s.Name.Contains(args[1]));
                if (!interesting) continue;
                string extra = "";
                try { extra += $" minVar={m?.MinimumVariation:0.####} maxVar={m?.MaximumVariation:0.####}"; } catch (Exception e) { extra += " var:" + e.Message; }
                try
                {
                    var def = s.GetDefinition();
                    extra += " def=" + (def?.GetType().Name ?? "null");
                    if (def is DistanceMateFeatureData dd) extra += $" d={dd.Distance:0.####} min={dd.MinimumDistance:0.####} max={dd.MaximumDistance:0.####}";
                    if (def is AngleMateFeatureData ad)
                    {
                        extra += $" a={ad.Angle:0.####} min={ad.MinimumAngle:0.####} max={ad.MaximumAngle:0.####} flip={ad.FlipDimension} align={ad.MateAlignment}";
                        try { s.Select2(false, -1); } catch (Exception) { }
                        try
                        {
                            var re = ad.ReferenceEntity;
                            extra += " ref=" + (re == null ? "null" : re.GetType().Name);
                            if (re is Edge edge && edge.GetCurve() is Curve cv && cv.IsLine()) extra += " line=[" + string.Join(",", ((double[])cv.LineParams).Select(x => x.ToString("0.###"))) + "]";
                            if (re is Face2 face && face.GetSurface() is Surface sf)
                            {
                                if (sf.IsPlane()) extra += " plane=[" + string.Join(",", ((double[])sf.PlaneParams).Select(x => x.ToString("0.###"))) + "]";
                                if (sf.IsCylinder()) extra += " cyl=[" + string.Join(",", ((double[])sf.CylinderParams).Select(x => x.ToString("0.###"))) + "]";
                            }
                            if (re is Entity en) extra += " refComp=" + ((en.GetComponent() as Component2)?.Name2 ?? "-");
                            if (re is Feature rf) extra += " refFeat=" + rf.Name + ":" + rf.GetTypeName2();
                        }
                        catch (Exception e) { extra += " ref:" + e.Message; }
                    }

                }
                catch (Exception e) { extra += " def:" + e.Message; }
                var who = new List<string>();
                if (m != null) for (int i = 0; i < m.GetMateEntityCount(); i++) { var n = m.MateEntity(i).ReferenceComponent?.Name2 ?? "(asm)"; who.Add(n.Length > 22 ? n.Substring(0, 22) : n); }
                Console.WriteLine($"{s.Name} [{tn}] type={(m != null ? ((SolidWorks.Interop.swconst.swMateType_e)type).ToString() : "-")}{extra}  {string.Join(" | ", who)}");
            }
        }
        return 0;
    }
    case "local":
    {
        SolidWorksQuestSender.Grouping.ViewerHandlesManyParts = true; // as the modified viewer reports
        var r = SolidWorksQuestSender.SolidWorksExporter.ExportActive(sw);
        File.WriteAllBytes(args[1], r.Glb);
        Console.WriteLine(r.Summary);
        Console.WriteLine(r.Timings);
        return 0;
    }
}

dynamic addin = sw.GetAddInObject("SolidWorksQuestSender.Addin");
if (addin == null) { Console.WriteLine("add-in not loaded"); return 1; }
string result = args[0] switch
{
    "export" => addin.ExportToFile(Path.GetFullPath(args[1])),
    "dev" => addin.DevExport(Path.GetFullPath(args[1]), Path.GetFullPath(args[2])),
    "send" => addin.SendNow(args.Length > 1 && args[1] == "force"),
    _ => "unknown command " + args[0],
};
Console.WriteLine(result);
Console.WriteLine($"wall {clock.Elapsed.TotalSeconds:0.0} s");
return 0;
