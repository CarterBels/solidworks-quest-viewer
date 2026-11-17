using System;
using System.Collections.Generic;
using System.Linq;
using SolidWorks.Interop.swconst;

namespace SolidWorksQuestSender
{
    /// Linkages move along curves: a mode's screw (one twist per group) is only right near the assembled pose, so a
    /// four-bar driven by its screw drifts apart. Here such a mode is traced step by step: at each pose the mates are
    /// re-linearised with the parts where they are now, and the motion continues the way it was going. The trace stops
    /// where the linkage locks up (no way to continue) or a limit mate's range ends.
    public static partial class Motion
    {
        const double StepDegrees = 1.0;
        const int MaxSteps = 360; // a whole turn of the anchor each way

        internal sealed class LimitInfo
        {
            public string Name;
            public bool Angle;
            public double Min, Max, Current;
            public Ent A, B;          // entities on the two sides
            public int GroupA, GroupB; // group numbers (0 = doesn't move)
            public string Key;         // assembly file | mate name, for LimitSigns
        }

        /// The traced paths for the viewer (glTF scene extras): {"sw":{"paths":[{"id", "t":[...], "period",
        /// "limit":{"name","angle","v":[...]}, "groups":{"g":[[qx,qy,qz,qw, tx,ty,tz] per sample]}}]}} in glTF space.
        public static string PathsJson(Dictionary<string, (PartMotion motion, int group)> motions)
        {
            var modes = motions.Values.SelectMany(v => v.motion.Modes.Select(x => x.mode)).Distinct().Where(m => m.Path != null).ToList();
            if (modes.Count == 0) return null;
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            string N(double v) => ((float)v).ToString("R", ci);
            var sb = new System.Text.StringBuilder("{\"sw\":{\"paths\":[");
            for (int mi = 0; mi < modes.Count; mi++)
            {
                var m = modes[mi];
                var p = m.Path;
                if (mi > 0) sb.Append(',');
                sb.Append("{\"id\":").Append(m.Id).Append(",\"t\":[").Append(string.Join(",", p.T.Select(N))).Append(']');
                if (p.Period > 0) sb.Append(",\"period\":").Append(N(p.Period));
                if (p.Limit != null)
                    sb.Append(",\"limit\":{\"name\":\"").Append((p.LimitName ?? "limit").Replace("\"", "'")).Append("\",\"angle\":")
                      .Append(p.LimitIsAngle ? "true" : "false").Append(",\"min\":").Append(N(p.LimitMin)).Append(",\"max\":").Append(N(p.LimitMax)).Append(",\"v\":[").Append(string.Join(",", p.Limit.Select(N))).Append("]}");
                sb.Append(",\"groups\":{");
                bool first = true;
                foreach (var kv in p.Poses)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append('"').Append(kv.Key).Append("\":[");
                    for (int i = 0; i < kv.Value.Count; i++)
                    {
                        var a = kv.Value[i];
                        var q = Quaternion(a);
                        if (i > 0) sb.Append(',');
                        sb.Append('[').Append(N(q[0])).Append(',').Append(N(q[1])).Append(',').Append(N(q[2])).Append(',').Append(N(q[3]))
                          .Append(',').Append(N(a[9])).Append(',').Append(N(a[10])).Append(',').Append(N(a[11])).Append(']');
                    }
                    sb.Append(']');
                }
                sb.Append("}}");
            }
            return sb.Append("]}}").ToString();
        }

        /// Row-major rotation (first 9 numbers) -> quaternion [x, y, z, w].
        static double[] Quaternion(double[] r)
        {
            double tr = r[0] + r[4] + r[8], x, y, z, w;
            if (tr > 0)
            {
                double s = Math.Sqrt(tr + 1) * 2;
                w = 0.25 * s; x = (r[7] - r[5]) / s; y = (r[2] - r[6]) / s; z = (r[3] - r[1]) / s;
            }
            else if (r[0] > r[4] && r[0] > r[8])
            {
                double s = Math.Sqrt(1 + r[0] - r[4] - r[8]) * 2;
                w = (r[7] - r[5]) / s; x = 0.25 * s; y = (r[1] + r[3]) / s; z = (r[2] + r[6]) / s;
            }
            else if (r[4] > r[8])
            {
                double s = Math.Sqrt(1 + r[4] - r[0] - r[8]) * 2;
                w = (r[2] - r[6]) / s; x = (r[1] + r[3]) / s; y = 0.25 * s; z = (r[5] + r[7]) / s;
            }
            else
            {
                double s = Math.Sqrt(1 + r[8] - r[0] - r[4]) * 2;
                w = (r[3] - r[1]) / s; x = (r[2] + r[6]) / s; y = (r[5] + r[7]) / s; z = 0.25 * s;
            }
            return new[] { x, y, z, w };
        }

        /// Whether a mode needs tracing: groups moving relative to each other with some turning.
        static bool NeedsPath(MotionMode m)
        {
            if (m.Twist.Count < 2) return false;
            // Mostly sliding (a carriage, perhaps with a bearing creeping round): the screw is right as it is.
            double maxTurn = 0, maxSlide = 0;
            foreach (var t in m.Twist.Values)
            {
                Screw(t, out _, out _, out double turn, out double slide);
                maxTurn = Math.Max(maxTurn, turn);
                maxSlide = Math.Max(maxSlide, Math.Abs(slide));
            }
            if (maxTurn * 0.1 < 0.2 * maxSlide || maxTurn < 1e-6) return false; // turning 0.1 m away vs sliding
            var first = m.Twist.Values.First();
            return m.Twist.Values.Any(t => Enumerable.Range(0, 6).Any(i => Math.Abs(t[i] - first[i]) > 1e-6));
        }

        /// The axis a limit's two sides turn about relative to each other in this mode, pointed a fixed way (its
        /// largest component positive) so a signed angle about it means the same every time; null if they only slide.
        static double[] HingeAxis(MotionMode m, LimitInfo l)
        {
            double[] W(int g) => m.Twist.TryGetValue(g, out var t) ? new[] { t[0], t[1], t[2] } : new double[3];
            var w = Sub(W(l.GroupA), W(l.GroupB));
            if (Len(w) < 1e-9) return null;
            w = Unit(w);
            int big = Math.Abs(w[0]) >= Math.Abs(w[1]) && Math.Abs(w[0]) >= Math.Abs(w[2]) ? 0 : Math.Abs(w[1]) >= Math.Abs(w[2]) ? 1 : 2;
            return w[big] < 0 ? Scale(w, -1) : w;
        }

        internal static MotionPath Trace(MotionMode m, List<(swMateType_e type, List<Ent> ents)> mates,
                                         Func<string, int> groupNo, List<LimitInfo> limits)
        {
            var S = m.Twist.Keys.OrderBy(g => g).ToList();
            var idx = S.Select((g, i) => (g, i)).ToDictionary(x => x.g, x => x.i);
            int k = S.Count, n = 6 * k;

            // Mates touching a moving group, with each entity's group number.
            var mine = new List<(swMateType_e type, List<Ent> ents, int[] gs)>();
            foreach (var (type, ents) in mates)
            {
                var gs = ents.Select(e => groupNo(e.Component)).ToArray();
                if (gs.Any(g => idx.ContainsKey(g)) && gs.Distinct().Count() == 2) mine.Add((type, ents, gs));
            }
            var lims = limits.Where(l => (idx.ContainsKey(l.GroupA) || idx.ContainsKey(l.GroupB)) && l.GroupA != l.GroupB).ToList();

            var x0 = new double[n];
            foreach (var g in S) Array.Copy(m.Twist[g], 0, x0, 6 * idx[g], 6);
            // t keeps the meaning it has near the assembled pose: the fastest-turning group turns at its starting rate.
            int anchor = 0;
            double best = -1;
            for (int i = 0; i < k; i++) { double w = Len3(x0, 6 * i); if (w > best) { best = w; anchor = i; } }
            double anchorRate = best;
            if (anchorRate < 1e-9) return null;
            double h = StepDegrees * Math.PI / 180 / anchorRate;

            var path = new MotionPath();
            var lim = lims.FirstOrDefault();
            if (lim != null) { path.LimitName = lim.Name; path.LimitIsAngle = lim.Angle; path.LimitMin = lim.Min; path.LimitMax = lim.Max; }

            var sides = new List<(double t, Pose[] pose, double limit)>();
            int fullSides = 0;
            foreach (int dir in new[] { 1, -1 })
            {
                int taken = 0;
                var pose = Enumerable.Range(0, k).Select(_ => Pose.Identity()).ToArray();
                var prev = x0.Select(x => x * dir).ToArray();
                var tracks = lims.Select(l => new LimitTrack(l, HingeAxis(m, l))).ToList();
                foreach (var tr in tracks) tr.Value(pose, idx);
                for (int step = 1; step <= MaxSteps; step++)
                {
                    var v1 = Velocity(pose, prev, mine, idx, n, anchor, anchorRate);
                    if (v1 == null) break;
                    var mid = Advance(pose, v1, h / 2);
                    var v2 = Velocity(mid, v1, mine, idx, n, anchor, anchorRate);
                    if (v2 == null) break;
                    var next = Advance(pose, v2, h);
                    bool inRange = true;
                    double shown = double.NaN;
                    for (int li = 0; li < tracks.Count; li++)
                    {
                        double val = tracks[li].Value(next, idx);
                        if (val < tracks[li].L.Min - 1e-4 || val > tracks[li].L.Max + 1e-4) inRange = false;
                        if (li == 0) shown = val;
                    }
                    if (!inRange) break;
                    pose = next;
                    prev = v2;
                    sides.Add((dir * step * h, pose, shown));
                    taken = step;
                }
                if (taken == MaxSteps) fullSides++;
            }
            double cur0 = lim != null ? new LimitTrack(lim, HingeAxis(m, lim)).Value(Enumerable.Range(0, k).Select(_ => Pose.Identity()).ToArray(), idx) : double.NaN;
            sides.Add((0, Enumerable.Range(0, k).Select(_ => Pose.Identity()).ToArray(), cur0));
            sides.Sort((a, b) => a.t.CompareTo(b.t));

            // How well the joints held: the furthest any pin (concentric mate) drifted off its hole along the path.
            foreach (var (type, ents, gs) in mine)
            {
                if (type != swMateType_e.swMateCONCENTRIC || ents.Count < 2) continue;
                foreach (var s in sides)
                {
                    var a = idx.TryGetValue(gs[0], out int ia) ? s.pose[ia].Apply(ents[0]) : ents[0];
                    var b = idx.TryGetValue(gs[1], out int ib) ? s.pose[ib].Apply(ents[1]) : ents[1];
                    path.Drift = Math.Max(path.Drift, DistanceToLine(a.P, b.P, b.D));
                }
            }
            path.T = sides.Select(s => s.t).ToArray();
            if (lim != null) path.Limit = sides.Select(s => s.limit).ToArray();
            foreach (var g in S) path.Poses[g] = sides.Select(s => s.pose[idx[g]].ToArray()).ToList();
            if (fullSides == 2)
            {
                // A whole turn each way without locking up: it goes round and round (a crank, a belt...).
                path.Period = MaxSteps * h;
            }
            else
            {
                m.Min = path.T.First();
                m.Max = path.T.Last();
            }
            return path;
        }

        /// The mechanism's velocity at this pose that continues 'prev', scaled so the anchor group turns at its rate.
        static double[] Velocity(Pose[] pose, double[] prev, List<(swMateType_e type, List<Ent> ents, int[] gs)> mates,
                                 Dictionary<int, int> idx, int n, int anchor, double anchorRate)
        {
            var rows = new List<double[]>();
            foreach (var (type, ents, gs) in mates)
            {
                var moved = ents.Select((e, i) => idx.TryGetValue(gs[i], out int pi) ? pose[pi].Apply(e) : e).ToList();
                int ga = gs.FirstOrDefault(g => idx.ContainsKey(g));
                int gb = gs.First(g => g != ga);
                var local = new List<double[]>();
                var tagged = moved.Select((e, i) => new Ent { Kind = e.Kind, P = e.P, D = e.D, Radius = e.Radius, Component = gs[i] == ga ? "A" : "B" }).ToList();
                AddMate(local, type, tagged, c => c == "A", null);
                foreach (var r in local)
                {
                    var row = new double[n];
                    for (int i = 0; i < 6; i++) row[6 * idx[ga] + i] += r[i];
                    if (idx.TryGetValue(gb, out int ib)) for (int i = 0; i < 6; i++) row[6 * ib + i] -= r[i];
                    rows.Add(row);
                }
            }
            var q = Orthonormal(NullSpace(rows, n), 1e-9);
            if (q.Count == 0) return null;
            var v = new double[n];
            foreach (var b in q)
            {
                double c = DotN(b, prev);
                for (int i = 0; i < n; i++) v[i] += c * b[i];
            }
            if (Norm(v) < 0.5 * Norm(prev)) return null; // locked up, or the motion turned into something else
            double w = Len3(v, 6 * anchor);
            if (w < 0.2 * anchorRate) return null;        // the anchor stalls: a dead point of the linkage
            double s = anchorRate / w;
            for (int i = 0; i < n; i++) v[i] *= s;
            return v;
        }

        static Pose[] Advance(Pose[] pose, double[] v, double h)
        {
            var next = new Pose[pose.Length];
            for (int i = 0; i < pose.Length; i++)
            {
                var w = new[] { v[6 * i], v[6 * i + 1], v[6 * i + 2] };
                var u = new[] { v[6 * i + 3], v[6 * i + 4], v[6 * i + 5] };
                next[i] = Pose.Step(w, u, h).Then(pose[i]);
            }
            return next;
        }

        static double Len3(double[] a, int o) => Math.Sqrt(a[o] * a[o] + a[o + 1] * a[o + 1] + a[o + 2] * a[o + 2]);

        /// A limit mate's value as the parts move; an unsigned angle or distance passing through zero changes sign.
        sealed class LimitTrack
        {
            public readonly LimitInfo L;
            readonly bool supplement;
            readonly double[] axis;
            readonly int sign;   // SolidWorks' value = sign · our signed value; 0 = not known (then unsigned)
            double lastRaw = double.NaN, lastDelta;
            bool negative;

            /// SolidWorks gives these limits no sign (an angle between faces is 0..180°, a distance ≥ 0), but they
            /// have one: turning the other way past 0 is below the minimum. Measured with a sign about the hinge
            /// (angles) or along the second face's normal (distances), SolidWorks' value is ±ours. Which of the two
            /// shows when the mate is away from 0 (its value says which way is positive); that is remembered for the
            /// mate, so it is still known when the part sits at exactly 0, where either way would look the same.
            public LimitTrack(LimitInfo l, double[] hinge)
            {
                L = l;
                axis = hinge;
                if (l.Angle)
                {
                    double g0 = Math.Acos(Clamp1(Dot(Unit(l.A.D), Unit(l.B.D))));
                    supplement = !double.IsNaN(l.Current) && Math.Abs(l.Current - (Math.PI - g0)) + 1e-6 < Math.Abs(l.Current - g0);
                }
                if (l.Angle && axis == null) return;
                double s0 = Signed(l.A, l.B);
                double near = l.Angle ? 0.5 * Math.PI / 180 : 0.2e-3;
                if (Math.Abs(s0) > near && !double.IsNaN(l.Current) && Math.Abs(l.Current) > near)
                {
                    sign = Math.Sign(l.Current) * Math.Sign(s0);
                    LimitSigns.Set(l.Key, sign);
                }
                else sign = LimitSigns.Get(l.Key);
            }

            /// The angle about the hinge (or the distance along the normal) with a sign, continuous through 0.
            double Signed(Ent a, Ent b)
            {
                if (!L.Angle) return Dot(Sub(a.P, b.P), Unit(b.D));
                var na = Unit(a.D);
                var nb = Unit(b.D);
                double g = Math.Acos(Clamp1(Dot(na, nb)));
                double v = supplement ? Math.PI - g : g;
                double s = Dot(axis, Cross(na, nb)) >= 0 ? 1 : -1;
                return supplement ? -s * v : s * v;
            }

            public double Value(Pose[] this_pose, Dictionary<int, int> idx)
            {
                var a = idx.TryGetValue(L.GroupA, out int ia) ? this_pose[ia].Apply(L.A) : L.A;
                var b = idx.TryGetValue(L.GroupB, out int ib) ? this_pose[ib].Apply(L.B) : L.B;
                if (sign != 0) return sign * Signed(a, b);
                // Sign unknown: unsigned, guessing a pass through 0 from the value coming down to it and going back up.
                double raw;
                if (L.Angle)
                {
                    double g = Math.Acos(Clamp1(Dot(Unit(a.D), Unit(b.D))));
                    raw = supplement ? Math.PI - g : g;
                }
                else raw = Math.Abs(Dot(Sub(a.P, b.P), Unit(b.D)));
                if (!double.IsNaN(lastRaw))
                {
                    double delta = raw - lastRaw;
                    // Came down towards zero and now goes back up: it went through zero.
                    double near = L.Angle ? 2 * Math.PI / 180 : 0.5e-3;
                    if (lastRaw < near && lastDelta < 0 && delta > 0) negative = !negative;
                    lastDelta = delta;
                }
                lastRaw = raw;
                return negative ? -raw : raw;
            }

            static double Clamp1(double x) => x < -1 ? -1 : x > 1 ? 1 : x;
        }

        /// A rigid displacement p' = R·p + T (R row-major).
        internal struct Pose
        {
            public double[] R, T;

            public static Pose Identity() => new Pose { R = new double[] { 1, 0, 0, 0, 1, 0, 0, 0, 1 }, T = new double[3] };

            public double[] Point(double[] p) => new[]
            {
                R[0] * p[0] + R[1] * p[1] + R[2] * p[2] + T[0],
                R[3] * p[0] + R[4] * p[1] + R[5] * p[2] + T[1],
                R[6] * p[0] + R[7] * p[1] + R[8] * p[2] + T[2],
            };

            public double[] Dir(double[] d) => new[]
            {
                R[0] * d[0] + R[1] * d[1] + R[2] * d[2],
                R[3] * d[0] + R[4] * d[1] + R[5] * d[2],
                R[6] * d[0] + R[7] * d[1] + R[8] * d[2],
            };

            internal Ent Apply(Ent e) => new Ent { Kind = e.Kind, Component = e.Component, Radius = e.Radius, P = Point(e.P), D = Dir(e.D) };

            /// This displacement after 'first'.
            public Pose Then(Pose first)
            {
                var r = new double[9];
                for (int i = 0; i < 3; i++)
                    for (int j = 0; j < 3; j++)
                        r[3 * i + j] = R[3 * i] * first.R[j] + R[3 * i + 1] * first.R[3 + j] + R[3 * i + 2] * first.R[6 + j];
                return new Pose { R = r, T = Point(first.T) };
            }

            /// Moving with twist (w, u) for time h: a screw about the axis through (w × u)/|w|².
            public static Pose Step(double[] w, double[] u, double h)
            {
                double wl = Len(w);
                if (wl < 1e-12) return new Pose { R = Identity().R, T = Scale(u, h) };
                var axis = Scale(w, 1 / wl);
                double a = wl * h, c = Math.Cos(a), s = Math.Sin(a), t = 1 - c;
                double x = axis[0], y = axis[1], z = axis[2];
                var r = new[]
                {
                    t * x * x + c,     t * x * y - s * z, t * x * z + s * y,
                    t * x * y + s * z, t * y * y + c,     t * y * z - s * x,
                    t * x * z - s * y, t * y * z + s * x, t * z * z + c,
                };
                var centre = Scale(Cross(w, u), 1 / (wl * wl));
                double slide = Dot(w, u) / wl;
                var rot = new Pose { R = r, T = new double[3] };
                var rc = rot.Dir(centre);
                // p' = centre + R(p − centre) + axis·slide·h
                return new Pose { R = r, T = Add(Sub(centre, rc), Scale(axis, slide * h)) };
            }

            public double[] ToArray() => R.Concat(T).ToArray();
        }
    }
}

namespace SolidWorksQuestSender
{
    /// Which way each limit mate counts as positive (see Motion.LimitTrack), remembered across sends and sessions in
    /// %APPDATA%\SolidWorksQuestSender\limit-signs.txt ("assembly path|mate name<TAB>±1" per line).
    static class LimitSigns
    {
        static Dictionary<string, int> map;
        static string File => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                                                      "SolidWorksQuestSender", "limit-signs.txt");

        static void Load()
        {
            if (map != null) return;
            map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (!System.IO.File.Exists(File)) return;
                foreach (var line in System.IO.File.ReadAllLines(File))
                {
                    int tab = line.LastIndexOf('\t');
                    if (tab > 0 && int.TryParse(line.Substring(tab + 1), out int s) && (s == 1 || s == -1)) map[line.Substring(0, tab)] = s;
                }
            }
            catch (Exception) { }
        }

        public static int Get(string key)
        {
            if (string.IsNullOrEmpty(key)) return 0;
            Load();
            return map.TryGetValue(key, out int s) ? s : 0;
        }

        public static void Set(string key, int sign)
        {
            if (string.IsNullOrEmpty(key) || sign == 0) return;
            Load();
            if (map.TryGetValue(key, out int old) && old == sign) return;
            map[key] = sign;
            try
            {
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(File));
                System.IO.File.WriteAllLines(File, map.Select(kv => kv.Key + "\t" + kv.Value));
            }
            catch (Exception) { }
        }
    }
}
