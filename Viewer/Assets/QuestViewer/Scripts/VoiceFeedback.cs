using UnityEngine;

/// How Marvin shows he hears you (SolidWorks fork; driven by VoiceCommands): a glow around the edges of your view.
///   blue, bouncing with your voice   he heard his name and is listening (say the command, or try again)
///   green flash                      he understood; the command shows briefly at the top of your view
///   orange flash                     he didn't catch that (he keeps listening a moment for another try)
///   red flash                        he understood but couldn't do it (e.g. SolidWorks isn't connected)
public class VoiceFeedback : MonoBehaviour
{
    [Tooltip("Seconds of quiet after the name (or a miss) before he stops listening.")]
    public float listenTimeout = 3.5f;

    static readonly Color Listening = new Color(0.35f, 0.9f, 1f);
    static readonly Color Heard = new Color(0.45f, 1f, 0.55f);
    static readonly Color Missed = new Color(1f, 0.8f, 0.45f);
    static readonly Color Failed = new Color(1f, 0.45f, 0.4f);

    const float GlowDistance = 0.3f;
    const float GlowSize = 1.1f;        // quad side at GlowDistance: wider than the view, so the band sits at its edges
    const float FlashSeconds = 0.9f;

    Material glowMat;
    Transform glow, caption;
    TextMesh captionText;
    Camera cam;

    bool listening;
    float lastHeardAt = -10f;     // name or speech while listening
    float bounce, bounceVel;      // the listening glow's bounce (springs back after each kick)
    Color flashColor;
    float flashAt = -10f;
    float captionAt = -10f, captionSeconds;
    float lastCommandAt = -10f;

    /// He heard his name recently and is waiting for a command (bare commands count then too).
    public bool IsListening => listening;

    void Awake()
    {
        var shader = Resources.Load<Shader>("EdgeGlow");
        glowMat = shader != null ? new Material(shader) : null;

        caption = new GameObject("VoiceCaption").transform;
        var go = new GameObject("Text");
        go.transform.SetParent(caption, false);
        captionText = go.AddComponent<TextMesh>();
        captionText.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        go.GetComponent<MeshRenderer>().sharedMaterial = captionText.font.material;
        captionText.anchor = TextAnchor.MiddleCenter;
        captionText.alignment = TextAlignment.Center;
        captionText.fontSize = 64;
        captionText.characterSize = 0.0012f;
        captionText.fontStyle = FontStyle.Bold;
        caption.gameObject.SetActive(false);
    }

    void OnDestroy()
    {
        if (glow != null) Destroy(glow.gameObject);
        if (caption != null) Destroy(caption.gameObject);
        if (glowMat != null) Destroy(glowMat);
    }

    void EnsureGlow()
    {
        if (cam == null) cam = Camera.main;
        if (glow != null || cam == null || glowMat == null) return;
        var q = GameObject.CreatePrimitive(PrimitiveType.Quad);
        Destroy(q.GetComponent<Collider>());
        q.name = "MarvinGlow";
        glow = q.transform;
        glow.SetParent(cam.transform, false);
        glow.localPosition = new Vector3(0f, 0f, GlowDistance);
        glow.localRotation = Quaternion.identity;
        glow.localScale = new Vector3(GlowSize, GlowSize, 1f);
        var r = q.GetComponent<MeshRenderer>();
        r.sharedMaterial = glowMat;
        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        r.receiveShadows = false;
        glow.gameObject.SetActive(false);
    }

    /// He heard his name (alone, or with words he didn't know yet).
    public void OnName()
    {
        if (Time.unscaledTime - lastCommandAt < 1.2f) return; // the tail of the command just handled
        listening = true;
        Kick(1f);
    }

    /// More speech while he's listening: the glow bounces with it.
    public void OnSpeech()
    {
        if (!listening) return;
        Kick(0.6f);
    }

    /// The words after his name weren't a command. He keeps listening for another try.
    public void OnMissed()
    {
        Flash(Missed);
        lastHeardAt = Time.unscaledTime;
    }

    /// A command was recognised; 'what' is the phrase without "marvin", e.g. "explode".
    public void OnCommand(string what)
    {
        listening = false;
        lastCommandAt = Time.unscaledTime;
        Flash(Heard);
        ShowCaption("Marvin: " + what, 1.8f);
    }

    /// He understood but it didn't work (shown after OnCommand).
    public void OnFailed() => Flash(Failed);

    void Kick(float amount)
    {
        lastHeardAt = Time.unscaledTime;
        bounceVel += amount * 9f;
    }

    void Flash(Color c)
    {
        flashColor = c;
        flashAt = Time.unscaledTime;
    }

    void ShowCaption(string message, float seconds)
    {
        captionText.text = message;
        captionAt = Time.unscaledTime;
        captionSeconds = seconds;
        caption.gameObject.SetActive(true);
    }

    void LateUpdate()
    {
        EnsureGlow();
        float now = Time.unscaledTime, dt = Time.unscaledDeltaTime;

        if (listening && now - lastHeardAt > listenTimeout)
        {
            listening = false;
            Flash(Missed);
        }

        // A damped spring: each bit of speech kicks the bounce up; it settles back to a gentle breathing level.
        float rest = listening ? 0.35f + 0.08f * Mathf.Sin(now * 5f) : 0f;
        bounceVel += (rest - bounce) * 60f * dt;
        bounceVel *= Mathf.Exp(-9f * dt);
        bounce = Mathf.Clamp(bounce + bounceVel * dt, 0f, 1f);

        float flash = Mathf.Clamp01(1f - (now - flashAt) / FlashSeconds);
        flash = flash * flash;
        float intensity = Mathf.Max(bounce, flash);
        var color = flash > bounce ? flashColor : Listening;

        if (glow != null)
        {
            bool show = intensity > 0.01f;
            if (glow.gameObject.activeSelf != show) glow.gameObject.SetActive(show);
            if (show)
            {
                glowMat.SetColor("_Color", color);
                glowMat.SetFloat("_Intensity", intensity);
            }
        }

        if (caption.gameObject.activeSelf)
        {
            float age = now - captionAt;
            if (age > captionSeconds) caption.gameObject.SetActive(false);
            else
            {
                var c = Heard;
                c.a = Mathf.Clamp01((captionSeconds - age) / 0.4f);
                captionText.color = c;
                PlaceCaption();
            }
        }
    }

    /// Near the top of your view, following your head.
    void PlaceCaption()
    {
        if (cam == null) return;
        var head = cam.transform;
        caption.position = head.position + head.forward * 0.6f + head.up * 0.17f;
        caption.rotation = Quaternion.LookRotation(caption.position - head.position, head.up);
    }
}
