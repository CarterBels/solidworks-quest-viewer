using System.Collections;
using UnityEngine;
using UnityEngine.InputSystem;

/// Places this object in front of the head (default 40 cm ahead, 10 cm below the eyes), facing the user,
/// once at start. After that it only moves back on request: if recallable, a double-pinch in empty air,
/// the A/X controller button, R on the PC keyboard, the keypad's RESET button, or a voice command.
/// Recentring always ends at exactly 100%: the object grows in from almost nothing while the hologram scan
/// sweeps over it (scalable objects with a ModelLook, i.e. the model and the test cube).
public class SpawnInFront : MonoBehaviour
{
    [Tooltip("Length of the grow-in and hologram scan when recentred.")]
    public float recenterSeconds = 2f;
    public Transform head;
    public float distance = 0.40f;
    public float belowEyes = 0.10f;
    [Tooltip("Positive = to the right.")]
    public float sideways = 0f;
    [Tooltip("Tilt the object back by this many degrees (e.g. a keypad lying like a desk panel).")]
    public float tiltDegrees = 0f;
    public bool recallable = true;
    /// Models (SolidWorks fork): the nearest side of the bounding box appears this far from the eyes, 1-5 ft,
    /// set with the keypad's SPAWN - / + or by voice; remembered between sessions.
    public const float MinFeet = 1f, MaxFeet = 5f, StepFeet = 0.5f;
    public static float SpawnFeet
    {
        get => Mathf.Clamp(PlayerPrefs.GetFloat("spawn.ft", 2f), MinFeet, MaxFeet);
        set { PlayerPrefs.SetFloat("spawn.ft", Mathf.Clamp(Mathf.Round(value / StepFeet) * StepFeet, MinFeet, MaxFeet)); PlayerPrefs.Save(); }
    }

    Grabbable grabbable;

    void Start()
    {
        grabbable = GetComponent<Grabbable>();
        if (head == null && Camera.main != null) head = Camera.main.transform;
        StartCoroutine(SpawnWhenTracked());
    }

    void OnEnable()
    {
        if (!recallable) return;
        // The model only comes back on purpose (RESET, voice, a controller button): an air double-pinch is too easy
        // to do by accident, e.g. pinching at a locked model. (SolidWorks fork.) The keypad still comes back with it.
        if (GetComponent<ModelHost>() == null) PinchGrabber.AirDoublePinch += Respawn;
        ControllerGrabber.RecallPressed += Respawn;
    }

    void OnDisable()
    {
        PinchGrabber.AirDoublePinch -= Respawn;
        ControllerGrabber.RecallPressed -= Respawn;
    }

    /// The head sits at the tracking origin (floor) until tracking data arrives, so wait for it.
    IEnumerator SpawnWhenTracked()
    {
        float until = Time.realtimeSinceStartup + 10f;
        while (head != null && head.localPosition.y < 0.3f && Time.realtimeSinceStartup < until) yield return null;
        yield return null;
        Respawn();
    }

    void Update()
    {
        if (recallable && Keyboard.current != null && Keyboard.current.rKey.wasPressedThisFrame) Respawn();
    }

    public void Respawn()
    {
        if (head == null || !isActiveAndEnabled) return;
        var look = GetComponent<ModelLook>();
        if (grabbable != null)
        {
            grabbable.ReleaseAll();
            if (grabbable.allowScale)
            {
                if (look != null) grabbable.GrowIn(recenterSeconds);
                else grabbable.ResetScale();
            }
        }

        var forward = Vector3.ProjectOnPlane(head.forward, Vector3.up);
        if (forward.sqrMagnitude < 1e-4f) forward = Vector3.forward;
        forward.Normalize();

        var right = Vector3.Cross(Vector3.up, forward);
        transform.rotation = Quaternion.LookRotation(-forward, Vector3.up) * Quaternion.Euler(-tiltDegrees, 0f, 0f);
        float d = distance;
        // A big model: move it back so its near side is SpawnFeet from your eyes (at 100%),
        // instead of appearing around your head. (SolidWorks fork.)
        var box = GetComponent<BoxCollider>();
        if (box != null && GetComponent<ModelHost>() != null) // the box keeps the model's size even when disabled
        {
            var s = transform.lossyScale;
            var half = Vector3.Scale(box.size, s) * 0.5f;
            float depth = Mathf.Abs(Vector3.Dot(transform.right, forward)) * half.x
                        + Mathf.Abs(Vector3.Dot(transform.up, forward)) * half.y
                        + Mathf.Abs(Vector3.Dot(transform.forward, forward)) * half.z;
            var centre = transform.rotation * Vector3.Scale(box.center, s);
            d = SpawnFeet * 0.3048f + depth - Vector3.Dot(centre, forward); // exactly, small or big
        }
        transform.position = head.position + forward * d + right * sideways + Vector3.down * belowEyes;
        if (grabbable != null) grabbable.Stop();
        if (look != null) look.ScanIn(recenterSeconds);
    }
}
