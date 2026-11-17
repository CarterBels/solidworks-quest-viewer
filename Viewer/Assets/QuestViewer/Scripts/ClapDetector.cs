using System;
using UnityEngine;

/// Clap your hands to toggle the exploded view: palms meet quickly while facing each other.
/// SolidWorks fork: a shockwave shows it. Turning EXPLODE on, rings burst out from your hands; turning it off,
/// rings rush back in to them.
public class ClapDetector : MonoBehaviour
{
    public TrackedHand left;
    public TrackedHand right;
    [Tooltip("Palm distance (m) that counts as a clap.")]
    public float contactDistance = 0.08f;
    [Tooltip("Minimum closing speed (m/s) just before contact.")]
    public float minSpeed = 0.5f;
    public float cooldown = 1.2f;

    public static event Action Clapped;

    float lastDist = 1f;
    float closingSpeed;
    float lastClap = -10f;

    void Update()
    {
        if (left == null || right == null || !left.IsTracked || !right.IsTracked) { lastDist = 1f; return; }

        var l = left.GripPose;
        var r = right.GripPose;
        float d = Vector3.Distance(l.position, r.position);
        float dt = Mathf.Max(Time.deltaTime, 1e-4f);
        closingSpeed = Mathf.Lerp(closingSpeed, (lastDist - d) / dt, 0.5f);

        // Palms facing each other: their palm normals point in opposite directions.
        bool facing = Vector3.Dot(l.rotation * Vector3.up, r.rotation * Vector3.up) < -0.4f;
        bool open = !left.IsGripping && !right.IsGripping && !left.IsPinching && !right.IsPinching;

        if (d < contactDistance && lastDist >= contactDistance && closingSpeed > minSpeed && facing && open
            && Time.time - lastClap > cooldown)
        {
            lastClap = Time.time;
            Clapped?.Invoke();
            ViewModes.ToggleExplode();
            var axis = r.position - l.position;
            if (axis.sqrMagnitude < 1e-4f) axis = l.rotation * Vector3.up;
            ClapShockwave.Play((l.position + r.position) * 0.5f, Vector3.up, ViewModes.Explode); // always flat
        }
        lastDist = d;
    }
}

/// Shockwave rings for a clap: a few flat (horizontal) rings around the hands, bursting out (outward) or rushing in
/// to the hands (inward), one after another.
public class ClapShockwave : MonoBehaviour
{
    const int Rings = 3;
    const float Stagger = 0.09f;
    const float Duration = 0.55f;
    const float MaxRadius = 0.2f;
    const float MinRadius = 0.015f;
    const float Width = 0.006f;
    const int Segments = 64;

    static ClapShockwave instance;

    readonly LineRenderer[] rings = new LineRenderer[Rings];
    Material mat;
    Vector3 centre, u, v;
    bool outward;
    float startedAt = -10f;

    public static void Play(Vector3 centre, Vector3 axis, bool outward)
    {
        if (instance == null)
        {
            instance = new GameObject("ClapShockwave").AddComponent<ClapShockwave>();
            instance.Build();
        }
        instance.centre = centre;
        instance.u = Vector3.Cross(axis, Mathf.Abs(Vector3.Dot(axis, Vector3.up)) > 0.9f ? Vector3.forward : Vector3.up).normalized;
        instance.v = Vector3.Cross(axis, instance.u).normalized;
        instance.outward = outward;
        instance.startedAt = Time.time;
    }

    void Build()
    {
        mat = Keypad.NewUnlit(Keypad.Accent);
        for (int i = 0; i < Rings; i++)
        {
            var go = new GameObject("Ring" + i);
            go.transform.SetParent(transform, false);
            var l = go.AddComponent<LineRenderer>();
            l.useWorldSpace = true;
            l.loop = true;
            l.positionCount = Segments;
            l.alignment = LineAlignment.View;
            l.sharedMaterial = mat;
            l.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            l.receiveShadows = false;
            l.enabled = false;
            rings[i] = l;
        }
    }

    void OnDestroy() { if (mat != null) Destroy(mat); }

    void LateUpdate()
    {
        float t0 = Time.time - startedAt;
        for (int i = 0; i < Rings; i++)
        {
            float t = (t0 - i * Stagger) / Duration;
            var l = rings[i];
            if (t < 0f || t > 1f) { l.enabled = false; continue; }
            // Fast at first and easing out (out), or speeding up into the hands (in).
            float k = outward ? 1f - (1f - t) * (1f - t) : 1f - t * t;
            float radius = Mathf.Lerp(MinRadius, MaxRadius, k);
            // Thick near the hands, thin far away; fades by thinning (opaque over passthrough).
            float fade = outward ? 1f - t : Mathf.Min(1f, t * 4f) * (1f - Mathf.Max(0f, t - 0.85f) / 0.15f);
            l.widthMultiplier = Width * Mathf.Lerp(0.35f, 1f, 1f - k) * Mathf.Clamp01(fade) * (1f - i * 0.2f);
            for (int s = 0; s < Segments; s++)
            {
                float a = s * 2f * Mathf.PI / Segments;
                l.SetPosition(s, centre + radius * (Mathf.Cos(a) * u + Mathf.Sin(a) * v));
            }
            l.enabled = true;
        }
    }
}
