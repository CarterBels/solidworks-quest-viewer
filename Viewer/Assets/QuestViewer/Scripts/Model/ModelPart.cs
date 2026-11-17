using System.Collections.Generic;
using UnityEngine;

/// One Onshape part inside the loaded model: name, part ID, true size, mass (when Onshape knows it),
/// and its home position for the exploded view.
public class ModelPart : MonoBehaviour
{
    public string PartName;
    public string PartId;
    /// True size in mm, in Onshape's axes (X, Y, Z).
    public Vector3 SizeMm;
    /// Kilograms, or NaN when the part has no material in Onshape.
    public float MassKg = float.NaN;
    /// Cubic metres, or NaN when unknown.
    public float VolumeM3 = float.NaN;
    /// Local position and rotation in the model before exploding.
    public Vector3 Home;
    public Quaternion HomeRotation = Quaternion.identity;
    /// Who is pulling this part out (exploded view), or null.
    public Grabber Dragger;
    /// Offset at full explosion (model-local).
    public Vector3 ExplodeOffset;
    public Renderer Renderer;

    // SolidWorks fork: specs from the add-in (glTF node extras "sw"), shown on the info panel. Null/NaN/0 = unknown.
    public string File, Config, Material, Body, Path;
    public float DensityKgM3 = float.NaN, AreaM2 = float.NaN;
    public int Count, Members, Triangles;

    // SolidWorks fork: isolated in the exploded view = stays where it was put (doesn't glide back) until ISOLATE
    // is turned off; Pinned = can't be grabbed either (LOCK PART).
    public bool Isolated, Pinned;
    /// Where an isolated part was let go (world space): it stays there even when the model is moved.
    public bool Placed;
    public Vector3 PlacedPosition;
    public Quaternion PlacedRotation;

    // ---- SolidWorks fork: moving by mates (MOVE mode) ----
    /// "rotate", "slide", "cylinder" (both, one axis), "fixed" (mates leave no motion), "other" (planar, ball...),
    /// "unmated"; null = no mate info (e.g. a part document or the upstream sender).
    public string MotionKind, MotionNote;
    public int Freedoms = -1, MateCount;
    /// Parts with the same number (> 0) move together: rigidly mated parts, and everything in a sub-assembly.
    public int MotionGroup;
    public bool SolidWorksFixed;
    /// Held still in MOVE mode (FIX PART); starts as SolidWorks' fixed parts.
    public bool Held;
    /// Being moved by a hand right now (follow exactly, no easing).
    public bool Driving;

    /// How this part moves in one mechanism mode, per unit of the mode's t: turns AngleRate radians about Axis and
    /// slides SlideRate metres along it. Point/Direction in the part's own space, Unity axes (X mirrored from
    /// SolidWorks, which turns a +angle into a −angle: see Target).
    public struct ModeMotion
    {
        public int Id;
        public float AngleRate, SlideRate;
        public Vector3 Point, Direction;
    }
    public readonly List<ModeMotion> Modes = new List<ModeMotion>();
    /// The modes grabbing this part drives: one (turn or slide), or two (turn, then slide, on one axis).
    public int[] Drive = System.Array.Empty<int>();

    public bool CanMove => Drive.Length > 0 && (MotionKind == "rotate" || MotionKind == "slide" || MotionKind == "cylinder");

    public bool TryMode(int id, out ModeMotion m)
    {
        foreach (var x in Modes) if (x.Id == id) { m = x; return true; }
        m = default;
        return false;
    }

    /// A mode's axis in the parent's space, at the part's home pose.
    public void AxisInParent(ModeMotion m, out Vector3 point, out Vector3 direction)
    {
        var tr = Matrix4x4.TRS(Home, HomeRotation, transform.localScale);
        point = tr.MultiplyPoint3x4(m.Point);
        direction = tr.MultiplyVector(m.Direction).normalized;
    }

    /// Where it belongs (parent space) for an explosion amount, with every mode it moves in applied.
    public void Target(float explode, out Vector3 position, out Quaternion rotation)
    {
        position = Home;
        rotation = HomeRotation;
        foreach (var m in Modes)
        {
            float t = Mechanism.Get(m.Id);
            if (t == 0f || Mechanism.TryPath(m.Id, MotionGroup, out _)) continue;
            AxisInParent(m, out var pivot, out var dir);
            // Mirrored from SolidWorks' right-handed space: a turn of +a about d there is −a about the mirrored d here.
            var q = Quaternion.AngleAxis(-m.AngleRate * t * Mathf.Rad2Deg, dir);
            position = pivot + q * (position - pivot) + dir * (m.SlideRate * t);
            rotation = q * rotation;
        }
        // Linkages: the traced displacement of its group (after its own spins, which are about its own axes).
        foreach (var m in Modes)
        {
            float t = Mechanism.Get(m.Id);
            if (t == 0f || !Mechanism.TryPath(m.Id, MotionGroup, out var path) || !path.Pose(MotionGroup, t, out var dq, out var dp)) continue;
            position = dq * position + dp;
            rotation = dq * rotation;
        }
        position += ExplodeOffset * explode;
    }


    /// For the info panel: how far its driven modes have turned/slid it.
    public string PositionText()
    {
        string s = null;
        foreach (var id in Drive)
        {
            if (!TryMode(id, out var m)) continue;
            float t = Mechanism.Get(id);
            if (Mechanism.TryPath(id, MotionGroup, out var path) && path.TryLimitValue(t, out float lv))
            {
                // Show the limit mate's own value, as SolidWorks measures it.
                string u = path.LimitIsAngle ? "°" : " mm";
                float k = path.LimitIsAngle ? Mathf.Rad2Deg : 1000f;
                string lt = $"{path.LimitName} {lv * k:0.#}{u}  (limits {path.LimitMin * k:0.#} to {path.LimitMax * k:0.#}{u})";
                s = s == null ? lt : s + ", " + lt;
                continue;
            }
            string part =m.AngleRate > 1e-6f ? $"{-m.AngleRate * t * Mathf.Rad2Deg:0}°" : $"{m.SlideRate * t * 1000f:0.#} mm";
            if (Mechanism.TryLimits(id, out var l))
                part += m.AngleRate > 1e-6f
                    ? $"  (limits {Lim(-m.AngleRate * l.y * Mathf.Rad2Deg, "0")}° to {Lim(-m.AngleRate * l.x * Mathf.Rad2Deg, "0")}°)"
                    : $"  (limits {Lim(m.SlideRate * l.x * 1000f, "0.#")} to {Lim(m.SlideRate * l.y * 1000f, "0.#")} mm)";
            s = s == null ? part : s + ", " + part;
        }
        return s;
    }

    static string Lim(float v, string f) => float.IsInfinity(v) ? (v < 0 ? "−∞" : "∞") : v.ToString(f);

    public string MotionText()
    {
        switch (MotionKind)
        {
            case "rotate": return "rotates on its axis";
            case "slide": return "slides along its axis";
            case "cylinder": return "rotates and slides on its axis";
            case "fixed": return "held in place by its mates";
            case "unmated": return SolidWorksFixed ? "fixed in SolidWorks" : "not mated";
            case "other": return $"moves {Freedoms} ways (not supported yet)";
            default: return null;
        }
    }

    public void Place()
    {
        Placed = true;
        PlacedPosition = transform.position;
        PlacedRotation = transform.rotation;
    }

    public string Describe()
    {
        var s = $"{PartName}\n{SizeMm.x:0.#} x {SizeMm.y:0.#} x {SizeMm.z:0.#} mm";
        if (!float.IsNaN(MassKg) && MassKg > 0f) s += MassKg >= 1f ? $"   {MassKg:0.00} kg" : $"   {MassKg * 1000f:0.#} g";
        else if (!float.IsNaN(VolumeM3) && VolumeM3 > 0f) s += $"   {VolumeM3 * 1e6f:0.#} cm3";
        return s;
    }
}
