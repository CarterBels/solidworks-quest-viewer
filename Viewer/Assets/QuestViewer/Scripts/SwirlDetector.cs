using UnityEngine;
using UnityEngine.XR.Hands;

/// Point your index finger straight up (the other fingers curled) and draw a flat circle with it, like stirring, to
/// start the turntable, spinning the way you went. Again to stop it.
/// SolidWorks fork:
/// - Starting needs the pose clearly (index straight and nearly vertical, other fingers curled); once started, a
///   looser pose keeps it going, so it doesn't reset halfway round.
/// - Where the finger starts is the top of the circle. The circle's centre is set a little towards you from there,
///   and the ring fills to the finger's direction from that centre (anywhere within 15 cm / 6 in of it), so it
///   follows the finger forwards and back. A full circle toggles ROTATE.
/// - The ring is drawn flat at that spot, just above finger height: an arrow filling it the way you're going, turning
///   green when ROTATE starts. While ROTATE is on the ring is red, and a full circle strikes it through and stops it.
public class SwirlDetector : MonoBehaviour
{
    public TrackedHand left;
    public TrackedHand right;
    [Tooltip("Turn needed to toggle, in degrees (a full circle is 360).")]
    public float turnDegrees = 345f;
    [Tooltip("The circle's centre is this far towards you from where the finger starts (m).")]
    public float centreOffset = 0.03f;
    [Tooltip("The finger may be this far from the centre (m); farther gives up.")]
    public float maxDistance = 0.15f;
    [Tooltip("Pose or tracking may drop out this long without giving up (s).")]
    public float graceSeconds = 0.6f;
    [Tooltip("Giving up when the finger hasn't gone further round for this long (s).")]
    public float stallSeconds = 3f;
    public float cooldown = 1.2f;

    class HandState
    {
        public bool active;
        public Vector3 centre, top, tip;   // centre: of the circle (flat); top: direction to where it started
        public float progress, lastAngle;  // signed degrees (+ = clockwise seen from above)
        public float lastProgressAt, lostSince = -1f;
        public SwirlRing ring;

        public void Stop() { active = false; progress = 0f; ring?.Hide(); }
    }

    readonly HandState leftState = new HandState(), rightState = new HandState();
    float lastTrigger = -10f;

    void Start()
    {
        leftState.ring = SwirlRing.Create(transform, "SwirlRingLeft");
        rightState.ring = SwirlRing.Create(transform, "SwirlRingRight");
    }

    void Update()
    {
        Track(left, leftState);
        Track(right, rightState);
    }

    void Track(TrackedHand hand, HandState s)
    {
        float now = Time.time;
        if (hand == null || now - lastTrigger < cooldown) { if (s.active) s.Stop(); return; }

        if (!s.active)
        {
            if (!Pose(hand, true, out var start)) return;
            // Start: this point is the top of the circle; the centre is a little towards you.
            var cam = Camera.main != null ? Camera.main.transform : null;
            var away = cam != null ? Vector3.ProjectOnPlane(cam.forward, Vector3.up) : Vector3.forward;
            away = away.sqrMagnitude > 1e-4f ? away.normalized : Vector3.forward;
            s.active = true;
            s.centre = start - away * centreOffset;
            s.top = away;
            s.tip = start;
            s.progress = s.lastAngle = 0f;
            s.lastProgressAt = now;
            s.lostSince = -1f;
            s.ring?.Begin(s.centre, s.top, start.y);
            return;
        }

        if (!Pose(hand, false, out var tip))
        {
            if (s.lostSince < 0f) s.lostSince = now;
            if (now - s.lostSince > graceSeconds) s.Stop();
            return;
        }
        s.lostSince = -1f;
        s.tip = tip;

        var d = Vector3.ProjectOnPlane(tip - s.centre, Vector3.up);
        if (d.magnitude > maxDistance) { s.Stop(); return; }
        if (d.magnitude > 0.006f) // right at the centre the direction is meaningless
        {
            float angle = Vector3.SignedAngle(s.top, d, Vector3.up);
            float step = Mathf.DeltaAngle(s.lastAngle, angle);
            s.lastAngle = angle;
            float before = Mathf.Abs(s.progress);
            s.progress += step;
            if (Mathf.Abs(s.progress) > before + 1f) s.lastProgressAt = now;
        }
        if (now - s.lastProgressAt > stallSeconds) { s.Stop(); return; }

        if (Mathf.Abs(s.progress) >= turnDegrees)
        {
            lastTrigger = now;
            bool starting = !ViewModes.Turntable;
            ViewModes.SetTurntable(starting, Mathf.Sign(s.progress));
            Status.Info(ViewModes.Turntable ? "Rotate on" : "Rotate off", 1.5f);
            s.ring?.Done(Mathf.Sign(s.progress), starting);
            s.active = false;
            s.progress = 0f;
            return;
        }
        s.ring?.Show(s.progress / 360f, ViewModes.Turntable);
    }

    /// Index finger out and pointing up, the other fingers curled. Starting asks for it clearly (straight, nearly
    /// vertical, fingers well curled); keeping it going is looser.
    static bool Pose(TrackedHand hand, bool starting, out Vector3 tip)
    {
        tip = default;
        if (!hand.IsTracked) return false;
        if (hand.IndexCurl < (starting ? 0.085f : 0.07f) || hand.GripCurl > (starting ? 0.065f : 0.095f)) return false;
        if (!hand.TryGetJoint(XRHandJointID.IndexTip, out var t) || !hand.TryGetJoint(XRHandJointID.IndexProximal, out var b)) return false;
        var dir = t.position - b.position;
        if (dir.magnitude < 0.035f || Vector3.Dot(dir.normalized, Vector3.up) < (starting ? 0.85f : 0.3f)) return false;
        if (starting && hand.TryGetJoint(XRHandJointID.IndexIntermediate, out var m))
        {
            // Straight: the finger's two halves line up.
            if (Vector3.Angle(m.position - b.position, t.position - m.position) > 30f) return false;
        }
        tip = t.position;
        return true;
    }
}

/// The ring shown while swirling, flat at the circle's centre: a faint full circle whose top is where the finger
/// started, an arc with an arrowhead filling it towards the finger, and (to stop ROTATE) a strike through it.
public class SwirlRing : MonoBehaviour
{
    const float Radius = 0.03f;
    const float Above = 0.02f;      // above the finger's starting height
    const float Width = 0.0024f;
    const float TiltDegrees = 8f;   // far edge raised a little towards you so it reads
    const int Segments = 64;
    const float ShowFrom = 0.03f;   // fraction of a circle before the arc shows
    const float DoneSeconds = 0.7f;

    static readonly Color Fill = new Color(0.85f, 0.95f, 1f);
    static readonly Color Go = new Color(0.35f, 1f, 0.45f);
    static readonly Color Stop = new Color(1f, 0.3f, 0.25f);

    LineRenderer track, arc, arrow, strike;
    Material trackMat, arcMat, strikeMat;
    Vector3 centre, top, right;
    float doneAt = -10f, doneSign;
    bool doneStarting;

    public static SwirlRing Create(Transform parent, string name)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        var r = go.AddComponent<SwirlRing>();
        r.trackMat = Keypad.NewUnlit(Color.white);
        r.arcMat = Keypad.NewUnlit(Color.white);
        r.strikeMat = Keypad.NewUnlit(Stop);
        r.track = r.Line("Track", Segments + 1, r.trackMat, Width * 0.6f, true);
        r.arc = r.Line("Arc", Segments + 1, r.arcMat, Width, false);
        r.arrow = r.Line("Arrow", 3, r.arcMat, Width, false);
        r.strike = r.Line("Strike", 2, r.strikeMat, Width * 1.2f, false);
        r.Hide();
        return r;
    }

    LineRenderer Line(string name, int count, Material mat, float width, bool loop)
    {
        var go = new GameObject(name);
        go.transform.SetParent(transform, false);
        var l = go.AddComponent<LineRenderer>();
        l.useWorldSpace = true;
        l.positionCount = count;
        l.loop = loop;
        l.widthMultiplier = width;
        l.numCapVertices = 2;
        l.numCornerVertices = 2;
        l.alignment = LineAlignment.View;
        l.sharedMaterial = mat;
        l.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        l.receiveShadows = false;
        return l;
    }

    void OnDestroy()
    {
        Destroy(trackMat); Destroy(arcMat); Destroy(strikeMat);
    }

    /// A new circle: centre (flat), direction to its top (where the finger started), the finger's height.
    public void Begin(Vector3 circleCentre, Vector3 topDirection, float fingerY)
    {
        if (Time.time - doneAt < DoneSeconds) doneAt = -10f;
        float tilt = TiltDegrees * Mathf.Deg2Rad;
        centre = new Vector3(circleCentre.x, fingerY + Above, circleCentre.z);
        right = Vector3.Cross(Vector3.up, topDirection).normalized;
        top = topDirection * Mathf.Cos(tilt) + Vector3.up * Mathf.Sin(tilt);
        Draw(1f, 0f, Fill, 1f, false);
    }

    /// While swirling: 'fraction' of a full circle so far (sign = direction), and whether ROTATE is on now.
    public void Show(float fraction, bool rotating)
    {
        if (Time.time - doneAt < DoneSeconds) return;
        float f = Mathf.Abs(fraction);
        Draw(Mathf.Sign(fraction), f < ShowFrom ? 0f : Mathf.Min(f, 1f), rotating ? Stop : Fill, 1f, false);
    }

    /// The circle is complete: green and full (ROTATE starts), or red and struck through (ROTATE stops).
    public void Done(float sign, bool starting)
    {
        doneAt = Time.time;
        doneSign = sign;
        doneStarting = starting;
    }

    void LateUpdate()
    {
        float t = Time.time - doneAt;
        if (t >= DoneSeconds) return;
        float alpha = 1f - Mathf.Clamp01((t - DoneSeconds * 0.5f) / (DoneSeconds * 0.5f));
        Draw(doneSign, 1f, doneStarting ? Go : Stop, alpha, !doneStarting);
        if (t + Time.deltaTime >= DoneSeconds) Hide(true); // gone, not left as a few faint pixels
    }

    public void Hide(bool force = false)
    {
        if (!force && Time.time - doneAt < DoneSeconds) return; // let the green / struck-through ring finish
        if (force) doneAt = -10f;
        if (!track.enabled) return;
        track.enabled = arc.enabled = arrow.enabled = strike.enabled = false;
    }

    Vector3 At(float a) => centre + Radius * (Mathf.Sin(a) * right + Mathf.Cos(a) * top);

    void Draw(float sign, float fraction, Color color, float alpha, bool struck)
    {
        if (sign == 0f) sign = 1f;
        // Opaque materials over passthrough: fade by thinning the lines, not by darkening them.
        track.enabled = true;
        trackMat.color = color * 0.45f;
        track.widthMultiplier = Width * 0.6f * alpha;
        for (int i = 0; i <= Segments; i++) track.SetPosition(i, At(i * 2f * Mathf.PI / Segments));

        bool showArc = fraction > 0f;
        arc.enabled = arrow.enabled = showArc;
        if (showArc)
        {
            arcMat.color = color;
            arc.widthMultiplier = arrow.widthMultiplier = Width * alpha;
            // Clockwise seen from above for sign > 0, from the top (where the finger started).
            float end = sign * fraction * 2f * Mathf.PI;
            for (int i = 0; i <= Segments; i++) arc.SetPosition(i, At(end * i / Segments));

            // Arrowhead at the arc's end, pointing the way you're going.
            var p = At(end);
            var radial = (p - centre).normalized;
            var along = (At(end + sign * 0.01f) - p).normalized;
            const float head = 0.007f;
            arrow.SetPosition(0, p - along * head + radial * head * 0.7f);
            arrow.SetPosition(1, p);
            arrow.SetPosition(2, p - along * head - radial * head * 0.7f);
        }

        strike.enabled = struck;
        if (struck)
        {
            strike.widthMultiplier = Width * 1.2f * alpha;
            var d = (top + right).normalized * Radius * 1.35f;
            strike.SetPosition(0, centre + d);
            strike.SetPosition(1, centre - d);
        }
    }
}
