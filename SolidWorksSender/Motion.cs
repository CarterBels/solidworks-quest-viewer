using System;
using System.Collections.Generic;
using System.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace SolidWorksQuestSender
{
    /// One way the assembly can move (a mechanism mode): every group that moves in it, with its twist (ω, v) per unit
    /// of the mode's parameter t (world/glTF space; v = velocity of the point at the origin). E.g. a carriage sliding
    /// on its rails, with everything mounted on it; or a roller spinning on its own.
    public sealed class MotionMode
    {
        public int Id;
        internal double[] Vector;  // the full mode (all free groups' twists), unit length
        public readonly Dictionary<int, double[]> Twist = new Dictionary<int, double[]>(); // group -> 6-vector
        public double Min = double.NegativeInfinity, Max = double.PositiveInfinity;        // from limit mates
        /// For linkages: the real (curved) motion, traced step by step; null when the screw (Twist) is exact.
        public MotionPath Path;
    }

    /// A mode's motion sampled along t: each moving group's displacement from the assembled pose (top-level space,
    /// p' = R·p + T; R row-major 3x3), and the limit mate's own value at each sample if one bounds it.
    public sealed class MotionPath
    {
        public double[] T;
        public readonly Dictionary<int, List<double[]>> Poses = new Dictionary<int, List<double[]>>(); // group -> [R9, T3] per sample
        public double[] Limit;          // the limit mate's value per sample (radians or metres), or null
        public bool LimitIsAngle;
        public double LimitMin, LimitMax;
        public string LimitName;
        /// t for one whole turn when it goes round and round (samples cover one turn each way), else 0.
        public double Period;
        /// The furthest a pin drifted off its axis along the trace (metres), as a check.
        public double Drift;
    }

    /// How a group of parts moves: which modes move it (Modes), and which of them grabbing it drives (Drive).
    public sealed class PartMotion
    {
        public string Kind;         // "rotate", "slide", "cylinder" (rotate + slide on one axis), "fixed", "other", "unmated"
        public double[] Point;      // its own axis when driven (first drive mode), glTF space, metres
        public double[] Direction;  // unit axis
        public int Freedoms;        // how many independent ways it can move (0-6)
        public int Mates;           // mates counted
        public bool SolidWorksFixed;
        public bool Ground;         // held still as the base of the mechanism
        public string Note;         // e.g. unsupported mate types that were ignored
        public readonly List<(MotionMode mode, double[] twist)> Modes = new List<(MotionMode, double[])>();
        public readonly List<MotionMode> Drive = new List<MotionMode>();

        public bool Moves => Kind == "rotate" || Kind == "slide" || Kind == "cylinder";
    }

    public static partial class Motion
    {
        const double Eps = 1e-6;

        /// Test harness only: mate feature names to leave out (to find which mate locks a part).
        public static readonly HashSet<string> DebugSkip = new HashSet<string>();

        /// The top-level assembly's mates, worked out for the whole assembly at once:
        ///  1. Each top-level component (a sub-assembly moves as one rigid piece) starts as its own group.
        ///  2. Groups whose mates with each other leave no relative motion are merged (a wheel clamped to its shaft),
        ///     repeatedly, until nothing more merges.
        ///  3. Each group's motion comes from its mates with every other group, those held still.
        /// Returns top-level component name -> (motion, group number). Never throws; empty if mates can't be read.
        public static Dictionary<string, (PartMotion motion, int group)> Analyze(ModelDoc2 doc, Action<string> log = null)
        {
            var result = new Dictionary<string, (PartMotion, int)>();
            try
            {
                var asm = (AssemblyDoc)doc;
                var tops = (asm.GetComponents(true) as object[] ?? Array.Empty<object>()).OfType<Component2>()
                    .Where(c => c.GetSuppression2() != (int)swComponentSuppressionState_e.swComponentSuppressed).ToList();
                var fixedTop = new HashSet<string>();
                foreach (var c in tops) try { if (c.IsFixed()) fixedTop.Add(c.Name2); } catch (Exception) { }

                // Every active mate of the top-level assembly, with its entities (component = top-level name).
                var mates = new List<(swMateType_e type, List<Ent> ents)>();
                var ignored = new Dictionary<string, SortedSet<string>>();
                // Limit mates bound motion instead of fixing it: kept apart, applied after classifying (MotionLimits).
                var limits = new List<(Mate2 m, double min, double max, double cur, List<Ent> ents)>();
                int unknown = 0, suppressedMates = 0;
                var limitNames = new Dictionary<Mate2, string>();
                string docPath = "";
                try { docPath = doc.GetPathName() ?? ""; } catch (Exception) { }
                var topNames = new HashSet<string>(tops.Select(c => c.Name2));
                foreach (var (feat, m) in TopLevelMates(doc))
                {
                    if (DebugSkip.Count > 0 && DebugSkip.Contains(feat.Name)) continue;
                    var ents = Entities(m, p => p, d => d);
                    if (ents.Any(e => e.Unknown)) { unknown++; continue; }
                    foreach (var e in ents) e.Component = Top(e.Component);
                    // A mate to a suppressed component (SolidWorks can still report it as active) isn't in force.
                    if (ents.Any(e => e.Component != null && !topNames.Contains(e.Component))) { suppressedMates++; continue; }
                    var names = ents.Select(e => e.Component).Where(n => n != null).Distinct().ToList();
                    if (names.Count == 1 && ents.All(e => e.Component != null)) continue; // within one component
                    if (MotionLimits.IsLimit(m, feat, out var lmin, out var lmax, out var lcur)) { limits.Add((m, lmin, lmax, lcur, ents)); limitNames[m] = feat.Name; continue; }
                    mates.Add(((swMateType_e)m.Type, ents));
                }

                // Union-find over top-level components.
                var parent = tops.ToDictionary(c => c.Name2, c => c.Name2);
                string Find(string x) { while (parent[x] != x) x = parent[x] = parent[parent[x]]; return x; }
                IEnumerable<(swMateType_e, List<Ent>)> Between(string a, string b) =>
                    mates.Where(m => m.ents.Any(e => e.Component != null && parent.ContainsKey(e.Component) && Find(e.Component) == a)
                                  && m.ents.Any(e => e.Component != null && parent.ContainsKey(e.Component) && Find(e.Component) == b));

                for (bool merged = true; merged;)
                {
                    merged = false;
                    var pairs = mates.Select(m => m.ents.Where(e => e.Component != null && parent.ContainsKey(e.Component))
                                                        .Select(e => Find(e.Component)).Distinct().ToList())
                                     .Where(g => g.Count == 2).Select(g => string.CompareOrdinal(g[0], g[1]) < 0 ? (g[0], g[1]) : (g[1], g[0]))
                                     .Distinct().ToList();
                    foreach (var (a, b) in pairs)
                    {
                        if (Find(a) == Find(b)) continue;
                        var rows = new List<double[]>();
                        foreach (var (type, ents) in Between(Find(a), Find(b))) AddMate(rows, type, ents, e => parent.ContainsKey(e) && Find(e) == Find(a), null);
                        if (rows.Count > 0 && NullSpace(rows).Count == 0) { parent[Find(b)] = Find(a); merged = true; }
                    }
                }

                // ---- The whole mechanism at once ----
                var groups = tops.Select(c => Find(c.Name2)).Distinct().ToList();
                var numbers = groups.Select((g, i) => (g, i)).ToDictionary(x => x.g, x => x.i + 1);
                string GroupOf(string comp) => comp != null && parent.ContainsKey(comp) ? Find(comp) : null; // null = the assembly itself

                // Ground: groups with a SolidWorks-fixed component (plus the assembly's own planes). With none fixed,
                // the biggest group (most components) is the base.
                var ground = new HashSet<string>(groups.Where(g => tops.Any(c => Find(c.Name2) == g && fixedTop.Contains(c.Name2))));
                if (ground.Count == 0 && groups.Count > 0)
                    ground.Add(groups.OrderByDescending(g => tops.Count(c => Find(c.Name2) == g)).First());
                var free = groups.Where(g => !ground.Contains(g)).ToList();
                var column = free.Select((g, i) => (g, i)).ToDictionary(x => x.g, x => 6 * x.i);
                int n = 6 * free.Count;

                // Each mate constrains the relative twist of its two sides: r · (t_A − t_B) = 0.
                var info = groups.ToDictionary(g => g, g => new PartMotion());
                var skippedBy = groups.ToDictionary(g => g, g => new SortedSet<string>());
                var big = new List<double[]>();
                foreach (var (type, ents) in mates)
                {
                    var sides = ents.Select(e => GroupOf(e.Component)).Distinct().ToList();
                    if (sides.Count != 2) continue;
                    string a = sides.FirstOrDefault(s => s != null && !ground.Contains(s)) ?? sides.FirstOrDefault(s => s != null);
                    string b = sides.First(s => s != a);
                    foreach (var s in sides) if (s != null) info[s].Mates++;
                    if (a == null || (ground.Contains(a) && (b == null || ground.Contains(b)))) continue; // ground to ground
                    var local = new List<double[]>();
                    AddMate(local, type, ents, comp => GroupOf(comp) == a, skippedBy[a]);
                    foreach (var r in local)
                    {
                        var row = new double[n];
                        if (column.TryGetValue(a, out int ca)) for (int i = 0; i < 6; i++) row[ca + i] += r[i];
                        if (b != null && column.TryGetValue(b, out int cb)) for (int i = 0; i < 6; i++) row[cb + i] -= r[i];
                        big.Add(row);
                    }
                }

                // Every way the mechanism can move, made orthonormal.
                var q = Orthonormal(NullSpace(big, n), 1e-9);

                // Per group: the modes that move it, chosen so they move nothing they don't have to.
                var modes = new List<MotionMode>();
                MotionMode Register(double[] x)
                {
                    var unit = Normalized(x);
                    foreach (var m in modes)
                        if (Math.Abs(DotN(m.Vector, unit)) > 0.9999) return m;
                    var mode = new MotionMode { Id = modes.Count + 1 };
                    mode.Vector = unit;
                    foreach (var g in free)
                    {
                        var tw = Slice(unit, column[g]);
                        if (Norm(tw) > 1e-7) mode.Twist[numbers[g]] = tw;
                    }
                    modes.Add(mode);
                    return mode;
                }
                foreach (var g in groups)
                {
                    var motion = info[g];
                    motion.SolidWorksFixed = tops.Any(c => Find(c.Name2) == g && fixedTop.Contains(c.Name2));
                    motion.Ground = ground.Contains(g);
                    if (skippedBy[g].Count > 0) motion.Note = "ignored: " + string.Join(", ", skippedBy[g]);
                    if (motion.Ground) { motion.Kind = motion.Mates == 0 && !motion.SolidWorksFixed ? "unmated" : "fixed"; continue; }
                    if (motion.Mates == 0) { motion.Kind = "unmated"; motion.Freedoms = 6; continue; }

                    // The group's own twist in each mode = 6 vectors in mode space; their span = how it can move.
                    int c0 = column[g];
                    var rowsOfG = Enumerable.Range(0, 6).Select(i => q.Select(v => v[c0 + i]).ToArray()).ToList();
                    var dirs = Orthonormal(rowsOfG, 1e-7);
                    motion.Freedoms = dirs.Count;
                    var own = dirs.Select(cvec => Combine(q, cvec)).ToList(); // modes (full vectors) that move g
                    // A part riding on something that moves (a roller on the hood) moves both on its own and with
                    // its carrier: grabbing it drives what moves it alone (its spin); the carrier moves it too.
                    // (Only when its motions together aren't simply turn + slide on one axis, which grabbing handles.)
                    if (own.Count > 2 || (own.Count == 2 && !IsCylinder(own, c0)))
                    {
                        var solo = SoloModes(own, c0, n);
                        if (solo.Count > 0 && solo.Count < own.Count) own = solo;
                    }
                    ClassifyDrive(own, c0, motion, Register);
                }
                foreach (var m in modes)
                    foreach (var kv in m.Twist)
                        info[groups[kv.Key - 1]].Modes.Add((m, kv.Value));

                // Limit mates bound a mode's parameter: how far it can go before the limit distance/angle is reached.
                foreach (var (lm, lmin, lmax, lcur, lents) in limits)
                {
                    var sides = lents.Select(e => GroupOf(e.Component)).Distinct().ToList();
                    if (sides.Count != 2) continue;
                    var mineE = lents.First(e => GroupOf(e.Component) == sides[0]);
                    var otherE = lents.First(e => GroupOf(e.Component) == sides[1]);
                    foreach (var m in modes)
                    {
                        var ta = sides[0] != null && m.Twist.TryGetValue(numbers[sides[0]], out var t0) ? t0 : new double[6];
                        var tb = sides[1] != null && m.Twist.TryGetValue(numbers[sides[1]], out var t1) ? t1 : new double[6];
                        var rel = Enumerable.Range(0, 6).Select(i => ta[i] - tb[i]).ToArray();
                        if (Norm(rel) < 1e-7) continue;
                        if (MotionLimits.ModeBounds(MotionLimits.IsAngle(lm), lmin, lmax, lcur, mineE.P, mineE.D, mineE.Kind,
                                                    otherE.P, otherE.D, otherE.Kind, rel, out var lo, out var hi))
                        {
                            m.Min = Math.Max(m.Min, Math.Min(lo, 0));
                            m.Max = Math.Min(m.Max, Math.Max(hi, 0));
                        }
                    }
                }

                // Linkages: trace their real, curved motion (the screw alone would pull their joints apart).
                int GroupNo(string comp) => GroupOf(comp) is string gn && numbers.TryGetValue(gn, out int no) && !ground.Contains(gn) ? no : 0;
                var limitInfo = new List<LimitInfo>();
                foreach (var (lm, lmin, lmax, lcur, lents) in limits)
                {
                    var sides = lents.Select(e => GroupNo(e.Component)).Distinct().ToList();
                    if (sides.Count != 2) continue;
                    limitInfo.Add(new LimitInfo
                    {
                        Name = limitNames.TryGetValue(lm, out var ln) ? ln : "limit", Angle = MotionLimits.IsAngle(lm),
                        Min = lmin, Max = lmax, Current = lcur,
                        A = lents.First(e => GroupNo(e.Component) == sides[0]), B = lents.First(e => GroupNo(e.Component) == sides[1]),
                        GroupA = sides[0], GroupB = sides[1],
                        Key = docPath + "|" + (limitNames.TryGetValue(lm, out var lk) ? lk : ""),
                    });
                }
                int traced = 0;
                foreach (var m in modes)
                {
                    if (!NeedsPath(m)) continue;
                    try { m.Path = Trace(m, mates, GroupNo, limitInfo); if (m.Path != null) traced++; }
                    catch (Exception e) { log?.Invoke($"tracing motion {m.Id} failed: {e.Message}"); }
                }

                foreach (var g in groups)
                    foreach (var c in tops) if (Find(c.Name2) == g) result[c.Name2] = (info[g], numbers[g]);
                log?.Invoke($"mates: {mates.Count} (+{limits.Count} limits) between {tops.Count} components, {groups.Count} groups " +
                            $"({ground.Count} ground), {modes.Count} ways to move, {groups.Count(g => info[g].Moves)} groups can be moved by hand" + (traced > 0 ? $", {traced} linkage motions traced" : "") +
                            (unknown > 0 ? $", {unknown} mates to unnamed (hidden?) faces skipped" : "") +
                            (suppressedMates > 0 ? $", {suppressedMates} mates to suppressed parts skipped" : ""));
            }
            catch (Exception e) { log?.Invoke("mates unreadable: " + e.Message); }
            return result;
        }

        /// "Sub-1/Leaf-1" -> "Sub-1" (the top-level component).
        public static string Top(string name)
        {
            if (name == null) return null;
            int slash = name.IndexOf('/');
            return slash < 0 ? name : name.Substring(0, slash);
        }

        /// The mates in the assembly's Mates folder (not suppressed).
        static IEnumerable<(Feature feature, Mate2 mate)> TopLevelMates(ModelDoc2 doc)
        {
            for (var f = doc.FirstFeature() as Feature; f != null; f = f.GetNextFeature() as Feature)
            {
                if (f.GetTypeName2() != "MateGroup") continue;
                for (var s = f.GetFirstSubFeature() as Feature; s != null; s = s.GetNextSubFeature() as Feature)
                {
                    bool suppressed = false;
                    try { suppressed = ((bool[])s.IsSuppressed2((int)swInConfigurationOpts_e.swThisConfiguration, null))?.FirstOrDefault() ?? false; }
                    catch (Exception) { }
                    if (!suppressed && s.GetSpecificFeature2() is Mate2 m) yield return (s, m);
                }
            }
        }

        /// Adds one mate's constraint rows for the side picked by isMine (component name -> on our side).
        static void AddMate(List<double[]> rows, swMateType_e type, List<Ent> ents, Func<string, bool> isMine, SortedSet<string> ignored)
        {
            Ent MineOf(Ent a, Ent b) => a.Component != null && isMine(a.Component) ? a : b;
            void PairOf(swMateType_e t, Ent a, Ent b)
            {
                var mine = MineOf(a, b);
                Pair(rows, t, a, b, mine.Component);
            }
            switch (type)
            {
                case swMateType_e.swMateLOCK:
                case swMateType_e.swMatePROFILECENTER:
                case swMateType_e.swMateCOORDINATE:
                    AddLock(rows);
                    break;
                case swMateType_e.swMateHINGE:
                    if (ents.Count >= 2) PairOf(swMateType_e.swMateCONCENTRIC, ents[0], ents[1]);
                    if (ents.Count >= 4) PairOf(swMateType_e.swMateCOINCIDENT, ents[2], ents[3]);
                    break;
                case swMateType_e.swMateSLIDER:
                    if (ents.Count >= 2) Slider(rows, MineOf(ents[0], ents[1]));
                    break;
                case swMateType_e.swMateCOINCIDENT:
                case swMateType_e.swMateCONCENTRIC:
                case swMateType_e.swMateDISTANCE:
                case swMateType_e.swMatePARALLEL:
                case swMateType_e.swMatePERPENDICULAR:
                case swMateType_e.swMateANGLE:
                case swMateType_e.swMateTANGENT:
                case swMateType_e.swMateWIDTH:
                    if (ents.Count >= 2) PairOf(type, ents[0], ents[1]);
                    break;
                default:
                    ignored?.Add(type.ToString().Replace("swMate", "").ToLowerInvariant());
                    break;
            }
        }

        /// Motion of 'comp' from the mates in its parent assembly. 'toTop' maps the parent assembly's space to the
        /// top-level assembly (null = it is the top level). Never throws.
        public static PartMotion Of(Component2 comp, Func<double[], double[]> pointToTop, Func<double[], double[]> dirToTop)
        {
            var result = new PartMotion();
            try { result.SolidWorksFixed = comp.IsFixed(); } catch (Exception) { }
            var rows = new List<double[]>();
            var ignored = new SortedSet<string>();
            object[] mates;
            try { mates = comp.GetMates() as object[] ?? Array.Empty<object>(); }
            catch (Exception e) { result.Kind = "other"; result.Note = "mates unreadable: " + e.Message; return result; }

            string me = comp.Name2;
            foreach (var o in mates)
            {
                if (!(o is Mate2 mate)) continue; // mate references, folders
                result.Mates++;
                var type = (swMateType_e)mate.Type;
                var ents = Entities(mate, pointToTop, dirToTop);
                switch (type)
                {
                    case swMateType_e.swMateLOCK:
                        AddLock(rows);
                        break;
                    case swMateType_e.swMateHINGE:
                        // Entities: concentric pair, then coincident (or angle) pair.
                        if (ents.Count >= 2) Pair(rows, swMateType_e.swMateCONCENTRIC, ents[0], ents[1], me);
                        if (ents.Count >= 4) Pair(rows, swMateType_e.swMateCOINCIDENT, ents[2], ents[3], me);
                        break;
                    case swMateType_e.swMateSLIDER:
                        // Slides along the shared axis/line only.
                        if (ents.Count >= 2) Slider(rows, Mine(ents, me) ?? ents[0]);
                        break;
                    case swMateType_e.swMateCOINCIDENT:
                    case swMateType_e.swMateCONCENTRIC:
                    case swMateType_e.swMateDISTANCE:
                    case swMateType_e.swMatePARALLEL:
                    case swMateType_e.swMatePERPENDICULAR:
                    case swMateType_e.swMateANGLE:
                    case swMateType_e.swMateTANGENT:
                    case swMateType_e.swMateWIDTH:
                        if (ents.Count >= 2) Pair(rows, type, ents[0], ents[1], me);
                        break;
                    case swMateType_e.swMatePROFILECENTER:
                    case swMateType_e.swMateCOORDINATE:
                        AddLock(rows); // aligned profiles / coordinate systems: treat as fully held
                        break;
                    default:
                        // Gear, rack and pinion, screw, linear coupler, cam, path, slot, symmetric, universal joint...
                        // couple or limit motion rather than define it: ignored in this version.
                        ignored.Add(type.ToString().Replace("swMate", "").ToLowerInvariant());
                        break;
                }
            }
            if (ignored.Count > 0) result.Note = "ignored: " + string.Join(", ", ignored);
            if (result.Mates == 0) { result.Kind = "unmated"; result.Freedoms = 6; return result; }

            var basis = NullSpace(rows);
            result.Freedoms = basis.Count;
            Classify(basis, result);
            return result;
        }

        /// One mate entity: its kind, component name, and geometry (point, direction) in top-level space.
        internal sealed class Ent
        {
            public int Kind;           // swMateEntityTypes_e
            public string Component;
            public double[] P, D;
            public double Radius;
            /// A face/edge/vertex SolidWorks reports on the assembly itself: it really belongs to a part the API
            /// can't name (e.g. a hidden one) and its position is unreliable, so its mate is skipped.
            public bool Unknown;
        }

        static List<Ent> Entities(Mate2 mate, Func<double[], double[]> pointToTop, Func<double[], double[]> dirToTop)
        {
            var list = new List<Ent>();
            int n = mate.GetMateEntityCount();
            for (int i = 0; i < n; i++)
            {
                var e = mate.MateEntity(i);
                if (e == null) continue;
                // EntityParams: [x, y, z, i, j, k, radius1, radius2] (point, direction/normal, radii).
                var prm = e.EntityParams as double[] ?? Array.Empty<double>();
                var ent = new Ent
                {
                    Kind = e.ReferenceType, // geometry (swMateEntityTypes_e); ReferenceType2 is what was selected
                    Component = e.ReferenceComponent?.Name2,
                    P = prm.Length >= 3 ? pointToTop(new[] { prm[0], prm[1], prm[2] }) : new double[3],
                    D = prm.Length >= 6 ? Unit(dirToTop(new[] { prm[3], prm[4], prm[5] })) : new double[] { 0, 0, 1 },
                    Radius = prm.Length >= 7 ? prm[6] : 0,
                };
                try
                {
                    var rc = e.ReferenceComponent;
                    int sel = e.ReferenceType2; // 1 edge, 2 face, 3 vertex; planes/axes/points/sketches are real assembly geometry
                    ent.Unknown = (rc == null || rc.IsRoot()) && (sel == 1 || sel == 2 || sel == 3);
                    if (rc != null && rc.IsRoot()) ent.Component = null; // the assembly itself
                }
                catch (Exception) { }
                list.Add(ent);
            }
            return list;
        }

        static Ent Mine(List<Ent> ents, string me) => ents.FirstOrDefault(e => e.Component == me);

        const int Point = (int)swMateEntityTypes_e.swMatePoint;
        const int Line = (int)swMateEntityTypes_e.swMateLine;
        const int Circle = (int)swMateEntityTypes_e.swMateCircle;
        const int Plane = (int)swMateEntityTypes_e.swMatePlane;
        const int Cylinder = (int)swMateEntityTypes_e.swMateCylinder;
        const int Sphere = (int)swMateEntityTypes_e.swMateSphere;
        const int Cone = (int)swMateEntityTypes_e.swMateCone;

        static bool Axial(int k) => k == Line || k == Cylinder || k == Cone || k == Circle;

        /// Constraint rows for one two-entity mate. A row r means r · (ω, v) = 0 for the body twist
        /// (ω = angular velocity, v = velocity of the body point at the origin).
        static void Pair(List<double[]> rows, swMateType_e type, Ent a, Ent b, string me)
        {
            // Use our own entity's geometry where it matters which side is which.
            var mine = a.Component == me ? a : b;
            var other = mine == a ? b : a;
            int k1 = mine.Kind, k2 = other.Kind;

            switch (type)
            {
                case swMateType_e.swMateCONCENTRIC:
                    if (k1 == Sphere || k2 == Sphere || k1 == Point || k2 == Point) { PointFixed(rows, mine.P); return; }
                    if (k1 == Circle && k2 == Circle) { Axis(rows, mine.P, mine.D); PlaneContact(rows, mine.P, mine.D); return; }
                    Axis(rows, mine.P, mine.D);
                    return;

                case swMateType_e.swMatePARALLEL:
                    NoTiltAbout(rows, Axial(k1) ? null : mine.D, Axial(k1) ? mine.D : null);
                    return;

                case swMateType_e.swMatePERPENDICULAR:
                case swMateType_e.swMateANGLE:
                    rows.Add(Rot(Cross(mine.D, other.D)));
                    return;

                case swMateType_e.swMateWIDTH:
                    // Centred between two faces: no motion along their normal, no tilting out of it.
                    PlaneOn(rows, mine.P, mine.D);
                    return;
            }

            // Coincident, distance and tangent: what touches what.
            if (k1 == Plane && k2 == Plane) { PlaneOn(rows, mine.P, mine.D); return; }
            if (k1 == Point && k2 == Point) { PointFixed(rows, mine.P); return; }
            if (k1 == Point && k2 == Plane) { PointOnPlane(rows, mine.P, other.D); return; }
            if (k1 == Plane && k2 == Point) { PointOnPlane(rows, other.P, mine.D); return; }
            if (k1 == Point && Axial(k2)) { PointOnLine(rows, mine.P, other.D); return; }
            if (Axial(k1) && k2 == Point) { PointOnLine(rows, other.P, mine.D); return; }
            if (Axial(k1) && k2 == Plane) { LineOnPlane(rows, mine.P, mine.D, other.D); return; }
            if (k1 == Plane && Axial(k2)) { LineOnPlane(rows, other.P, other.D, mine.D); return; }
            if (Axial(k1) && Axial(k2))
            {
                // Coincident lines/axes: collinear. Tangent (or a distance) between cylinders on parallel but
                // separate axes: the axes stay parallel and keep their distance; it may roll around the other.
                var off = Sub(mine.P, other.P);
                var perp = Sub(off, Scale(other.D, Dot(off, other.D)));
                bool apart = Len(perp) > 1e-5 && Math.Abs(Dot(mine.D, other.D)) > 0.999;
                if (type != swMateType_e.swMateCOINCIDENT && apart)
                {
                    NoTiltAbout(rows, null, mine.D);
                    rows.Add(Vel(mine.P, Unit(perp)));
                    return;
                }
                Axis(rows, mine.P, mine.D);
                return;
            }
            if (k1 == Sphere || k2 == Sphere) { PointFixed(rows, mine.P); return; }
        }

        // ---- Constraint builders ----

        static double[] Rot(double[] w) => new[] { w[0], w[1], w[2], 0, 0, 0 };

        /// Velocity of point p along direction n is zero: (v + ω × p) · n = ω · (p × n) + v · n.
        static double[] Vel(double[] p, double[] n)
        {
            var pxn = Cross(p, n);
            return new[] { pxn[0], pxn[1], pxn[2], n[0], n[1], n[2] };
        }

        static void AddLock(List<double[]> rows)
        {
            for (int i = 0; i < 6; i++) { var r = new double[6]; r[i] = 1; rows.Add(r); }
        }

        static void PointFixed(List<double[]> rows, double[] p)
        {
            rows.Add(Vel(p, new double[] { 1, 0, 0 }));
            rows.Add(Vel(p, new double[] { 0, 1, 0 }));
            rows.Add(Vel(p, new double[] { 0, 0, 1 }));
        }

        static void PointOnPlane(List<double[]> rows, double[] p, double[] n) => rows.Add(Vel(p, n));

        static void PointOnLine(List<double[]> rows, double[] p, double[] d)
        {
            Perp(d, out var e1, out var e2);
            rows.Add(Vel(p, e1));
            rows.Add(Vel(p, e2));
        }

        /// The body's axis (p, d) stays on a fixed axis: may rotate about it and slide along it.
        static void Axis(List<double[]> rows, double[] p, double[] d)
        {
            Perp(d, out var e1, out var e2);
            rows.Add(Rot(e1));
            rows.Add(Rot(e2));
            rows.Add(Vel(p, e1));
            rows.Add(Vel(p, e2));
        }

        /// A plane of the body stays on a fixed plane: slides within it, turns about its normal.
        static void PlaneOn(List<double[]> rows, double[] p, double[] n)
        {
            Perp(n, out var e1, out var e2);
            rows.Add(Rot(e1));
            rows.Add(Rot(e2));
            rows.Add(Vel(p, n));
        }

        static void PlaneContact(List<double[]> rows, double[] p, double[] n) => rows.Add(Vel(p, n));

        /// A line of the body lies in a fixed plane (normal n): two of its points keep their height.
        static void LineOnPlane(List<double[]> rows, double[] p, double[] d, double[] n)
        {
            rows.Add(Vel(p, n));
            rows.Add(Vel(Add(p, d), n));
        }

        /// Parallel: a plane normal (or an axis direction) keeps its direction.
        static void NoTiltAbout(List<double[]> rows, double[] normal, double[] axis)
        {
            var d = normal ?? axis;
            Perp(d, out var e1, out var e2);
            rows.Add(Rot(e1));
            rows.Add(Rot(e2));
        }

        /// Slider: translation along d only.
        static void Slider(List<double[]> rows, Ent e)
        {
            rows.Add(Rot(new double[] { 1, 0, 0 }));
            rows.Add(Rot(new double[] { 0, 1, 0 }));
            rows.Add(Rot(new double[] { 0, 0, 1 }));
            Perp(e.D, out var e1, out var e2);
            rows.Add(new[] { 0, 0, 0, e1[0], e1[1], e1[2] });
            rows.Add(new[] { 0, 0, 0, e2[0], e2[1], e2[2] });
        }

        // ---- Solving ----

        /// Basis of the twists allowed by all rows (null space), by Gaussian elimination with partial pivoting.
        public static List<double[]> NullSpace(List<double[]> rows, int n = 6)
        {
            // Rows scaled to unit length, so the pivot tolerance means the same for every mate.
            var m = rows.Select(r =>
            {
                var c = (double[])r.Clone();
                double len = Math.Sqrt(c.Sum(x => x * x));
                if (len > Eps) for (int i = 0; i < n; i++) c[i] /= len;
                return c;
            }).Where(r => r.Any(x => Math.Abs(x) > Eps)).ToList();

            var pivotCols = new List<int>();
            var isPivot = new bool[n];
            int row = 0;
            for (int col = 0; col < n && row < m.Count; col++)
            {
                int best = row;
                for (int i = row + 1; i < m.Count; i++) if (Math.Abs(m[i][col]) > Math.Abs(m[best][col])) best = i;
                if (Math.Abs(m[best][col]) < 1e-7) continue;
                (m[row], m[best]) = (m[best], m[row]);
                var pr = m[row];
                double pv = pr[col];
                for (int j = 0; j < n; j++) pr[j] /= pv;
                for (int i = 0; i < m.Count; i++)
                {
                    if (i == row) continue;
                    var ri = m[i];
                    double f = ri[col];
                    if (Math.Abs(f) < 1e-15) continue;
                    for (int j = col; j < n; j++) ri[j] -= f * pr[j];
                }
                pivotCols.Add(col);
                isPivot[col] = true;
                row++;
            }

            var basis = new List<double[]>();
            for (int free = 0; free < n; free++)
            {
                if (isPivot[free]) continue;
                var x = new double[n];
                x[free] = 1;
                for (int r = 0; r < pivotCols.Count; r++) x[pivotCols[r]] = -m[r][free];
                basis.Add(x);
            }
            return basis;
        }

        // ---- Mechanism helpers ----

        /// Modified Gram-Schmidt: an orthonormal basis of the vectors' span (vectors shorter than tol dropped).
        static List<double[]> Orthonormal(List<double[]> vectors, double tol)
        {
            var result = new List<double[]>();
            foreach (var v0 in vectors)
            {
                var v = (double[])v0.Clone();
                for (int pass = 0; pass < 2; pass++)
                    foreach (var u in result)
                    {
                        double d = DotN(u, v);
                        for (int i = 0; i < v.Length; i++) v[i] -= d * u[i];
                    }
                double len = Math.Sqrt(DotN(v, v));
                if (len > tol) result.Add(v.Select(x => x / len).ToArray());
            }
            return result;
        }

        static double DotN(double[] a, double[] b) { double s = 0; for (int i = 0; i < a.Length; i++) s += a[i] * b[i]; return s; }
        static double Norm(double[] a) => Math.Sqrt(DotN(a, a));
        static double[] Slice(double[] a, int start) => new[] { a[start], a[start + 1], a[start + 2], a[start + 3], a[start + 4], a[start + 5] };

        /// Unit length, with the sign fixed (largest component positive) so the same mode always looks the same.
        static double[] Normalized(double[] a)
        {
            double len = Norm(a);
            int big = 0;
            for (int i = 1; i < a.Length; i++) if (Math.Abs(a[i]) > Math.Abs(a[big])) big = i;
            double s = (a[big] < 0 ? -1 : 1) / Math.Max(len, 1e-300);
            return a.Select(x => x * s).ToArray();
        }

        /// Sum of c[j] * basis[j]
        static double[] Combine(List<double[]> basis, double[] c)
        {
            var x = new double[basis[0].Length];
            for (int j = 0; j < basis.Count; j++)
                for (int i = 0; i < x.Length; i++) x[i] += c[j] * basis[j][i];
            return x;
        }

        static double[] Sub6(double[] a, double[] b, double s) => a.Select((x, i) => x - s * b[i]).ToArray();

        /// Whether two modes move the group as turn + slide on one axis (what ClassifyDrive calls "cylinder").
        static bool IsCylinder(List<double[]> own, int c0)
        {
            var probe = new PartMotion();
            ClassifyDrive(own, c0, probe, x => new MotionMode { Vector = x });
            return probe.Kind == "cylinder";
        }

        /// The combinations of 'own' (full mode vectors) that move no group but the one at columns c0..c0+5,
        /// orthonormal. Empty if every way it moves takes others along.
        static List<double[]> SoloModes(List<double[]> own, int c0, int n)
        {
            int k = own.Count;
            var rows = new List<double[]>();
            for (int col = 0; col < n; col++)
            {
                if (col >= c0 && col < c0 + 6) continue;
                var r = new double[k];
                double s = 0;
                for (int j = 0; j < k; j++) { r[j] = own[j][col]; s += r[j] * r[j]; }
                if (s > 1e-14) rows.Add(r);
            }
            var combos = rows.Count == 0 ? Enumerable.Range(0, k).Select(j => { var e = new double[k]; e[j] = 1; return e; }).ToList()
                                         : NullSpace(rows, k);
            var result = combos.Select(c => Combine(own, c)).Where(v => Norm(v) > 1e-9).ToList();
            return Orthonormal(result, 1e-7);
        }

        /// How grabbing this group moves it: one mode (turn or slide), two on one axis (turn + slide), or not by hand.
        /// 'own' = orthonormal full mode vectors that move the group; c0 = its columns.
        static void ClassifyDrive(List<double[]> own, int c0, PartMotion motion, Func<double[], MotionMode> register)
        {
            if (own.Count == 0) { motion.Kind = "fixed"; return; }
            if (own.Count == 1)
            {
                var (w, v) = Split(Slice(own[0], c0));
                motion.Drive.Add(register(own[0]));
                SetAxis(motion, w, v);
                return;
            }
            if (own.Count == 2)
            {
                // Turn + slide on one axis: the span holds a combination where this group doesn't turn.
                var (w1, _) = Split(Slice(own[0], c0));
                var (w2, _) = Split(Slice(own[1], c0));
                var (ma, mb, wa, wb) = Len(w1) >= Len(w2) ? (own[0], own[1], w1, w2) : (own[1], own[0], w2, w1);
                double wal = Len(wa);
                if (wal > 1e-9)
                {
                    double t = Dot(wb, wa) / (wal * wal);
                    if (Len(Sub(wb, Scale(wa, t))) < 1e-4 * Math.Max(1, Len(wb)))
                    {
                        var slide = Sub6(mb, ma, t);                    // this group only slides
                        var sn = Normalized(slide);
                        var turn = Sub6(ma, sn, DotN(ma, sn));          // the rest: turning
                        var (ws, vs) = Split(Slice(slide, c0));
                        var (wt, vt) = Split(Slice(turn, c0));
                        if (Len(vs) > 1e-9 && Len(wt) > 1e-9 && Math.Abs(Dot(Unit(vs), Unit(wt))) > 0.999)
                        {
                            motion.Drive.Add(register(turn));
                            motion.Drive.Add(register(slide));
                            SetAxis(motion, wt, vt);
                            motion.Kind = "cylinder";
                            return;
                        }
                    }
                }
            }
            motion.Kind = "other"; // planar, ball joint, two independent motions...: not by hand yet
        }

        static void SetAxis(PartMotion motion, double[] w, double[] v)
        {
            double wl = Len(w);
            if (wl < 1e-6 * Math.Max(1, Len(v) * 10))
            {
                motion.Kind = "slide";
                motion.Direction = Unit(v);
                motion.Point = new double[3];
            }
            else
            {
                motion.Kind = "rotate";
                motion.Direction = Unit(w);
                motion.Point = Scale(Cross(w, v), 1 / (wl * wl));
            }
        }

        /// A twist (w, v) per unit t as a screw: axis point and direction, radians and metres per unit t.
        public static void Screw(double[] twist, out double[] point, out double[] direction, out double angleRate, out double slideRate)
        {
            var (w, v) = Split(twist);
            double wl = Len(w);
            if (wl > 1e-9)
            {
                direction = Scale(w, 1 / wl);
                angleRate = wl;
                point = Scale(Cross(w, v), 1 / (wl * wl));
                slideRate = Dot(w, v) / wl;
            }
            else
            {
                double vl = Len(v);
                direction = vl > 1e-12 ? Scale(v, 1 / vl) : new double[] { 0, 0, 1 };
                angleRate = 0;
                point = new double[3];
                slideRate = vl;
            }
        }

        static void Classify(List<double[]> basis, PartMotion result)
        {
            switch (basis.Count)
            {
                case 0:
                    result.Kind = "fixed";
                    return;
                case 1:
                {
                    var (w, v) = Split(basis[0]);
                    double wl = Len(w);
                    if (wl < Eps)
                    {
                        result.Kind = "slide";
                        result.Direction = Unit(v);
                        result.Point = new double[3];
                        return;
                    }
                    // Rotation about the axis through p = (ω × v) / |ω|² (a screw pitch, if any, is ignored).
                    result.Kind = "rotate";
                    result.Direction = Unit(w);
                    result.Point = Scale(Cross(w, v), 1 / (wl * wl));
                    return;
                }
                case 2:
                {
                    // Cylindrical if the span holds a pure translation along the rotation axis.
                    var (w1, v1) = Split(basis[0]);
                    var (w2, v2) = Split(basis[1]);
                    // Combination a*b1 + b*b2 with ω = 0: pick the larger ω to eliminate.
                    var (wa, va, wb, vb) = Len(w1) >= Len(w2) ? (w1, v1, w2, v2) : (w2, v2, w1, v1);
                    double wal = Len(wa);
                    if (wal < Eps) { result.Kind = "other"; return; }
                    double t = Dot(wb, wa) / (wal * wal);
                    var wr = Sub(wb, Scale(wa, t));
                    if (Len(wr) > 1e-4 * Math.Max(1, Len(wb))) { result.Kind = "other"; return; } // two rotations
                    var slide = Sub(vb, Scale(va, t));
                    if (Len(slide) < Eps) { result.Kind = "other"; return; }
                    var axis = Unit(wa);
                    if (Math.Abs(Dot(Unit(slide), axis)) < 0.999) { result.Kind = "other"; return; } // e.g. planar
                    result.Kind = "cylinder";
                    result.Direction = axis;
                    result.Point = Scale(Cross(wa, va), 1 / (wal * wal));
                    return;
                }
                default:
                    result.Kind = "other"; // planar, ball joint, free...: not supported yet
                    return;
            }
        }

        // ---- SolidWorks transforms (MathTransform.ArrayData: rotation 0-8, translation 9-11, scale 12; p' = (p·R)·s + T) ----

        public static double[] TransformOf(Component2 c)
        {
            try { return ((MathTransform)c?.Transform2)?.ArrayData as double[]; } catch (Exception) { return null; }
        }

        public static double[] XfPoint(double[] xf, double[] p) =>
            xf == null ? p : Add(Scale(RowTimes(p, xf), xf[12]), new[] { xf[9], xf[10], xf[11] });

        public static double[] XfDir(double[] xf, double[] d) => xf == null ? d : RowTimes(d, xf);

        /// Inverse: top-level point -> the component's own space.
        public static double[] XfInvPoint(double[] xf, double[] p)
        {
            if (xf == null) return p;
            var q = Scale(Sub(p, new[] { xf[9], xf[10], xf[11] }), 1 / xf[12]);
            return TimesRows(q, xf);
        }

        public static double[] XfInvDir(double[] xf, double[] d) => xf == null ? d : Unit(TimesRows(d, xf));

        // p·R (row vector times R) and p·Rᵀ
        static double[] RowTimes(double[] p, double[] r) => new[]
        {
            p[0] * r[0] + p[1] * r[3] + p[2] * r[6],
            p[0] * r[1] + p[1] * r[4] + p[2] * r[7],
            p[0] * r[2] + p[1] * r[5] + p[2] * r[8],
        };
        static double[] TimesRows(double[] p, double[] r) => new[]
        {
            p[0] * r[0] + p[1] * r[1] + p[2] * r[2],
            p[0] * r[3] + p[1] * r[4] + p[2] * r[5],
            p[0] * r[6] + p[1] * r[7] + p[2] * r[8],
        };

        /// Distance from point c to the line (p, d).
        public static double DistanceToLine(double[] c, double[] p, double[] d) => Len(Cross(Sub(c, p), Unit(d)));

        // ---- Small vector helpers ----

        static (double[] w, double[] v) Split(double[] t) => (new[] { t[0], t[1], t[2] }, new[] { t[3], t[4], t[5] });
        static double[] Cross(double[] a, double[] b) => new[] { a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0] };
        static double Dot(double[] a, double[] b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];
        static double Len(double[] a) => Math.Sqrt(Dot(a, a));
        static double[] Add(double[] a, double[] b) => new[] { a[0] + b[0], a[1] + b[1], a[2] + b[2] };
        static double[] Sub(double[] a, double[] b) => new[] { a[0] - b[0], a[1] - b[1], a[2] - b[2] };
        static double[] Scale(double[] a, double s) => new[] { a[0] * s, a[1] * s, a[2] * s };
        static double[] Unit(double[] a) { double l = Len(a); return l < 1e-12 ? new double[] { 0, 0, 1 } : Scale(a, 1 / l); }

        static void Perp(double[] d, out double[] e1, out double[] e2)
        {
            var n = Unit(d);
            var t = Math.Abs(n[0]) < 0.9 ? new double[] { 1, 0, 0 } : new double[] { 0, 1, 0 };
            e1 = Unit(Cross(n, t));
            e2 = Cross(n, e1);
        }
    }
}
