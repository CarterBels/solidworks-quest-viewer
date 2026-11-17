using UnityEngine;

/// Something that can hold a Grabbable: a hand (pinch or grip) or a controller.
/// In exploded view it can also pull out a single part (which glides back when released).
public abstract class Grabber : MonoBehaviour
{
    /// Layer of the finger/controller colliders; excluded from grab searches.
    public const string HandsLayerName = "Hands";

    /// Pose the held object follows.
    public abstract Pose GrabPose { get; }
    public Grabbable Held { get; protected set; }
    /// The single part being pulled out in exploded view, or null.
    public ModelPart DraggedPart { get; private set; }
    /// Holding anything (a whole object or a part).
    public bool IsHolding => Held != null || DraggedPart != null;
    /// When this grabber last let go of something (finger pushing waits a moment after a release).
    public float LastReleaseTime { get; private set; } = -10f;

    Vector3 partOffPos;
    Quaternion partOffRot;

    /// Called by Grabbable when it's taken away (respawned or replaced).
    internal virtual void Drop(Grabbable g)
    {
        if (Held != g) return;
        Held = null;
        LastReleaseTime = Time.time;
    }

    protected bool TryGrab(Vector3 at, float radius)
    {
        var target = FindTarget(at, radius);
        if (target == null) return false;
        Held = target;
        target.Grab(this);
        return true;
    }

    protected void ReleaseHeld()
    {
        if (Held == null) return;
        Held.Release(this);
        Held = null;
        LastReleaseTime = Time.time;
    }

    /// Exploded view (or an isolated part at any time): start pulling out the part at 'at'.
    /// With ISOLATE on, the part stays where it's let go (SolidWorks fork). A locked part (LOCK PART) can't be moved:
    /// the pinch is used up, so it doesn't grab the whole model instead.
    protected bool TryDragPart(Vector3 at, float radius)
    {
        var part = FindPart(at, radius);
        if (part == null || part.Dragger != null) return false;
        if (ViewModes.FixPick) { ToggleHeld(part); return true; }
        if (ViewModes.Move && !part.Isolated) return TryMoveByMates(part);
        if (!ViewModes.Explode && !part.Isolated) return false;
        if (part.Pinned)
        {
            PartInfoPanel.Focus = part;
            Status.Info("Part locked\npress LOCK PART to move it", 1.5f);
            return true;
        }
        if (ViewModes.Isolate)
        {
            part.Isolated = true;
            PartInfoPanel.Focus = part;
        }
        DraggedPart = part;
        part.Dragger = this;
        var p = GrabPose;
        var inv = Quaternion.Inverse(p.rotation);
        partOffPos = inv * (part.transform.position - p.position);
        partOffRot = inv * part.transform.rotation;
        return true;
    }

    // ---- MOVE mode (SolidWorks fork): turn or slide a part as its mates allow ----


    ModelPart movingPart;
    readonly System.Collections.Generic.List<ModelPart> movingParts = new System.Collections.Generic.List<ModelPart>();
    Vector3 moveStartHand;
    float moveStartT;
    Vector3 moveLastAround;
    float lastLimitNote = -10f;
    static ModelPart blockedPart;   // last part whose move held parts stopped (pinch it again to free them)
    static float blockedAt = -10f;

    static void ToggleHeld(ModelPart part)
    {
        bool held = !part.Held;
        foreach (var p in GroupOf(part)) p.Held = held; // a group is held or free as one
        PartInfoPanel.Focus = part;
        Status.Info((part.Held ? "Held still\n" : "Free to move\n") + part.PartName, 1.8f);
    }

    /// Always uses up the pinch (so it doesn't grab the whole model); moves the part only if its mates allow.
    /// Everything that moves with it (its mode: e.g. a whole carriage) moves too.
    bool TryMoveByMates(ModelPart part)
    {
        PartInfoPanel.Focus = part;
        if (!part.CanMove)
        {
            Status.Info(part.MotionText() is string why ? part.PartName + "\n" + why : "No mate info\nsend it again from SolidWorks", 2f);
            return true;
        }
        var host = part.GetComponentInParent<ModelHost>();
        movingParts.Clear();
        if (host != null)
            foreach (var p in host.Parts)
            {
                if (p == null) continue;
                foreach (var id in part.Drive)
                    if (p.TryMode(id, out _)) { movingParts.Add(p); break; }
            }
        var held = movingParts.FindAll(p => p.Held);
        if (held.Count > 0)
        {
            if (blockedPart == part && Time.time - blockedAt < 4f)
            {
                // Second pinch on the same part: free what was in the way, and move it.
                foreach (var h in held) foreach (var p in GroupOf(h)) p.Held = false;
                Status.Info($"Freed {held.Count} held part{(held.Count == 1 ? "" : "s")}", 1.5f);
                blockedPart = null;
            }
            else
            {
                HeldMarks.Flash(held);
                blockedPart = part;
                blockedAt = Time.time;
                string who = held.Count == 1 ? (held[0] == part ? "This part is" : held[0].PartName + " is") : $"{held.Count} parts that move with it are";
                Status.Info($"{who} held still (orange)\npinch again to free and move", 3f);
                movingParts.Clear();
                return true;
            }
        }
        blockedPart = null;
        movingPart = part;
        foreach (var p in movingParts) p.Driving = true;
        moveStartHand = GrabPose.position;
        moveStartT = SlideMode(part, out int sid) ? Mechanism.Get(sid) : 0f;
        moveLastAround = Vector3.zero;
        // A linkage (traced path, not one that goes round and round): the pinched spot follows the hand along it.
        pathPoints = null;
        foreach (var d in part.Drive)
            if (part.TryMode(d, out var dm) && dm.AngleRate > 1e-6f && Mechanism.TryPath(d, part.MotionGroup, out var path)
                && path.Period <= 0f)
            {
                // Where the pinched spot is at every sample of the path (parent space).
                var local = part.transform.InverseTransformPoint(GrabPose.position);
                var home = Matrix4x4.TRS(part.Home, part.HomeRotation, part.transform.localScale).MultiplyPoint3x4(local);
                var s = path.Groups[part.MotionGroup];
                pathPoints = new Vector3[s.Length];
                for (int i = 0; i < s.Length; i++) pathPoints[i] = s[i].q * home + s[i].p;
                pathMode = d;
                pathT = path.T;
                break;
            }
        return true;
    }

    Vector3[] pathPoints;
    float[] pathT;
    int pathMode;

    /// Moves a traced linkage so the pinched spot of the grabbed part comes as near the hand as the path allows:
    /// from where it is, walks along the path while that brings the spot closer. It stops at the ends of the path (a
    /// limit) and where the spot would have to come back (a dead point of the linkage). True if it stopped short.
    bool FollowHand(int id, Vector3 handParent)
    {
        var a = pathPoints;
        Mechanism.TryPath(id, movingPart.MotionGroup, out var path);
        path.Locate(Mechanism.Get(id), out int i, out float f);
        int j = f > 0.5f ? Mathf.Min(i + 1, a.Length - 1) : i;
        float D(int k) => (a[k] - handParent).sqrMagnitude;
        int dirIdx = j + 1 < a.Length && D(j + 1) < D(j) ? 1 : j > 0 && D(j - 1) < D(j) ? -1 : 0;
        if (dirIdx != 0)
            while (j + dirIdx >= 0 && j + dirIdx < a.Length && D(j + dirIdx) < D(j)) j += dirIdx;
        // Between j and its better neighbour: the nearest point on that segment.
        int n = j + 1 < a.Length && (j == 0 || D(j + 1) < D(j - 1)) ? j + 1 : j - 1;
        float u = 0f;
        if (n >= 0 && n < a.Length)
        {
            var seg = a[n] - a[j];
            u = seg.sqrMagnitude > 1e-12f ? Mathf.Clamp01(Vector3.Dot(handParent - a[j], seg) / seg.sqrMagnitude) : 0f;
            Mechanism.Set(id, Mathf.Lerp(pathT[j], pathT[n], u));
        }
        else Mechanism.Set(id, pathT[j]);
        bool atEnd = (j == 0 || j == a.Length - 1) && u < 0.01f;
        return atEnd && D(j) > 0.01f * 0.01f; // pulled on past the end of the path
    }

    /// The part's driven mode that slides it (the second of turn + slide, or the only one if it slides).
    static bool SlideMode(ModelPart part, out int id)
    {
        foreach (var d in part.Drive)
            if (part.TryMode(d, out var m) && m.AngleRate <= 1e-6f && m.SlideRate > 1e-9f) { id = d; return true; }
        id = 0;
        return false;
    }

    void UpdateMoveByMates()
    {
        if ((object)movingPart == null) return;
        var part = movingPart;
        if (part == null || !part.isActiveAndEnabled || !ViewModes.Move) { EndMoveByMates(); return; } // replaced, or MOVE off
        var hand = GrabPose.position;
        bool stopped = false;

        if (pathPoints != null)
        {
            var parentT = part.transform.parent;
            stopped = FollowHand(pathMode, parentT != null ? parentT.InverseTransformPoint(hand) : hand);
            if (stopped && Time.time - lastLimitNote > 1f)
            {
                lastLimitNote = Time.time;
                Status.Info("As far as it goes\n" + part.PositionText(), 1f);
            }
            return;
        }

        foreach (var id in part.Drive)
        {
            if (!part.TryMode(id, out var m)) continue;
            // The axis as the part is now (turning about its own axis or sliding along it doesn't move the axis).
            var pivot = part.transform.TransformPoint(m.Point);
            var dir = part.transform.TransformDirection(m.Direction).normalized;
            if (m.AngleRate > 1e-6f)
            {
                // Turn by the angle the hand sweeps around the axis, frame by frame, so whole turns add up
                // (needs the hand a centimetre or more off the axis). Target turns by −rate·t, hence the minus.
                var b = Vector3.ProjectOnPlane(hand - pivot, dir);
                if (b.sqrMagnitude > 1e-4f)
                {
                    if (moveLastAround.sqrMagnitude > 1e-4f)
                    {
                        float swept = Vector3.SignedAngle(moveLastAround, b, dir) * Mathf.Deg2Rad;
                        stopped |= Mechanism.Set(id, Mechanism.Get(id) - swept / m.AngleRate);
                    }
                    moveLastAround = b;
                }
            }
            else if (m.SlideRate > 1e-9f)
            {
                // Slide by how far the hand moved along the axis (world metres -> model metres -> t).
                var parent = part.transform.parent;
                var delta = parent != null ? parent.InverseTransformVector(hand - moveStartHand) : hand - moveStartHand;
                var dirParent = parent != null ? parent.InverseTransformDirection(dir).normalized : dir;
                stopped |= Mechanism.Set(id, moveStartT + Vector3.Dot(delta, dirParent) / m.SlideRate);
            }
        }
        if (stopped && Time.time - lastLimitNote > 1f)
        {
            lastLimitNote = Time.time;
            Status.Info("At its limit\n" + part.PositionText(), 1f);
        }
    }

    /// The part and every part that moves with it (same MotionGroup).
    static System.Collections.Generic.List<ModelPart> GroupOf(ModelPart part)
    {
        var list = new System.Collections.Generic.List<ModelPart> { part };
        var host = part.GetComponentInParent<ModelHost>();
        if (part.MotionGroup <= 0 || host == null) return list;
        foreach (var p in host.Parts)
            if (p != null && p != part && p.MotionGroup == part.MotionGroup) list.Add(p);
        return list;
    }

    void EndMoveByMates()
    {
        foreach (var p in movingParts) if (p != null) p.Driving = false;
        movingParts.Clear();
        movingPart = null;
    }


    protected void ReleasePart()
    {
        EndMoveByMates();
        if (DraggedPart == null) return;
        if (DraggedPart.Dragger == this) DraggedPart.Dragger = null;
        if (DraggedPart.Isolated)
        {
            DraggedPart.Place();                // stays here in the room, even if the model moves
            PartSettle.Drop(DraggedPart);       // or onto a table just below, if there is one
        }
        DraggedPart = null;
        LastReleaseTime = Time.time;
    }

    protected virtual void LateUpdate()
    {
        UpdateMoveByMates();
        if (DraggedPart == null) return;
        if (DraggedPart.Dragger != this) { DraggedPart = null; return; } // e.g. the model was replaced
        var p = GrabPose;
        DraggedPart.transform.SetPositionAndRotation(p.position + p.rotation * partOffPos, p.rotation * partOffRot);
    }

    protected static Grabbable FindTarget(Vector3 p, float radius)
    {
        Physics.SyncTransforms();
        Grabbable best = null;
        float bestDist = float.MaxValue;
        foreach (var col in Physics.OverlapSphere(p, radius, Mask, QueryTriggerInteraction.Ignore))
        {
            var g = col.GetComponentInParent<Grabbable>();
            if (g == null || !col.enabled) continue;
            float d = Vector3.Distance(col.ClosestPoint(p), p);
            if (d < bestDist) { best = g; bestDist = d; }
        }
        return best;
    }

    protected static ModelPart FindPart(Vector3 p, float radius)
    {
        Physics.SyncTransforms();
        ModelPart best = null;
        float bestDist = float.MaxValue;
        foreach (var col in Physics.OverlapSphere(p, radius, Mask, QueryTriggerInteraction.Ignore))
        {
            var part = col.GetComponent<ModelPart>();
            if (part == null || !col.enabled) continue;
            float d = Vector3.Distance(col.ClosestPoint(p), p);
            if (d < bestDist) { best = part; bestDist = d; }
        }
        return best;
    }

    static int Mask => ~LayerMask.GetMask(HandsLayerName);
}
