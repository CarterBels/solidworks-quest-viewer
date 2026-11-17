using System.Collections.Generic;
using UnityEngine;

/// SolidWorks fork: the model as a mechanism (MOVE mode). The add-in works out every way the assembly can move by
/// its mates ("modes": a carriage sliding on its rails with everything on it, a roller spinning...). Each mode has
/// one number, t; each part that moves in a mode turns and/or slides about its own axis in proportion to t
/// (ModelPart.Modes). Limit mates bound t. t = 0 everywhere is the model as assembled.
public static class Mechanism
{
    static readonly Dictionary<int, float> t = new Dictionary<int, float>();
    static readonly Dictionary<int, Vector2> limits = new Dictionary<int, Vector2>();

    public static float Get(int mode) => t.TryGetValue(mode, out var v) ? v : 0f;

    /// Sets a mode's t, kept within its limits. Returns true if it stopped at a limit.
    public static bool Set(int mode, float value)
    {
        bool stopped = false;
        if (limits.TryGetValue(mode, out var l))
        {
            float c = Mathf.Clamp(value, l.x, l.y);
            stopped = c != value;
            value = c;
        }
        t[mode] = value;
        return stopped;
    }

    public static bool AnyMoved
    {
        get { foreach (var v in t.Values) if (v != 0f) return true; return false; }
    }

    /// Everything back as assembled (MOVE off).
    public static void Reset() => t.Clear();

    /// A new model: forget the old modes.
    public static void Clear()
    {
        t.Clear();
        limits.Clear();
        paths.Clear();
    }

    /// A linkage mode traced by the add-in: each moving group's displacement from the assembled pose at sampled t
    /// (parent space, Unity axes), and the limit mate's own value along it.
    public sealed class Path
    {
        public float[] T;
        public float Period;                 // > 0: goes round and round, samples cover one turn each way
        public string LimitName;
        public bool LimitIsAngle;
        public float LimitMin, LimitMax;
        public float[] Limit;                // per sample, or null
        public readonly Dictionary<int, (Quaternion q, Vector3 p)[]> Groups = new Dictionary<int, (Quaternion, Vector3)[]>();

        /// Sample index i and blend f (0..1 towards i+1) for t.
        public void Locate(float time, out int i, out float f)
        {
            if (Period > 0f) time -= Period * Mathf.Round(time / Period);
            if (time <= T[0]) { i = 0; f = 0f; return; }
            if (time >= T[T.Length - 1]) { i = Mathf.Max(0, T.Length - 2); f = T.Length > 1 ? 1f : 0f; return; }
            int lo = 0, hi = T.Length - 1;
            while (hi - lo > 1) { int mid = (lo + hi) / 2; if (T[mid] <= time) lo = mid; else hi = mid; }
            i = lo;
            f = (time - T[lo]) / Mathf.Max(1e-9f, T[hi] - T[lo]);
        }

        public bool Pose(int group, float time, out Quaternion q, out Vector3 p)
        {
            q = Quaternion.identity; p = Vector3.zero;
            if (!Groups.TryGetValue(group, out var s) || s.Length == 0) return false;
            Locate(time, out int i, out float f);
            if (s.Length == 1) { (q, p) = s[0]; return true; }
            q = Quaternion.Slerp(s[i].q, s[i + 1].q, f);
            p = Vector3.Lerp(s[i].p, s[i + 1].p, f);
            return true;
        }

        public bool TryLimitValue(float time, out float v)
        {
            v = 0f;
            if (Limit == null) return false;
            Locate(time, out int i, out float f);
            v = Limit.Length == 1 ? Limit[0] : Mathf.Lerp(Limit[i], Limit[i + 1], f);
            return true;
        }
    }

    static readonly Dictionary<int, Path> paths = new Dictionary<int, Path>();

    public static void SetPath(int mode, Path p) => paths[mode] = p;

    /// The traced path of a mode that moves this group, if the add-in traced it.
    public static bool TryPath(int mode, int group, out Path p) => paths.TryGetValue(mode, out p) && p.Groups.ContainsKey(group);

    public static void SetLimits(int mode, float min, float max)
    {
        // Unbounded sides arrive as ±1e9.
        limits[mode] = new Vector2(min <= -1e8f ? float.NegativeInfinity : min, max >= 1e8f ? float.PositiveInfinity : max);
    }

    public static bool TryLimits(int mode, out Vector2 range)
    {
        if (limits.TryGetValue(mode, out range) && (!float.IsInfinity(range.x) || !float.IsInfinity(range.y))) return true;
        return false;
    }
}
