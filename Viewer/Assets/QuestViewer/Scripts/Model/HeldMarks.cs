using System.Collections.Generic;
using UnityEngine;

/// SolidWorks fork: in MOVE mode, parts held still (FIX PART) glow orange so it's clear what is locked. Parts that
/// just stopped a move pulse brighter (Flash). Also frees held parts (all of them, or a given list).
[RequireComponent(typeof(ModelHost))]
public class HeldMarks : MonoBehaviour
{
    static readonly Color HeldColor = new Color(1f, 0.45f, 0.05f, 0.45f);
    static readonly Color FlashColor = new Color(1f, 0.25f, 0.05f, 0.8f);
    const float FlashSeconds = 3f;

    static readonly List<ModelPart> flashing = new List<ModelPart>();
    static float flashStart = -10f;

    ModelHost host;
    ModelLook look;
    bool wasShowing;

    void Awake()
    {
        host = GetComponent<ModelHost>();
        look = GetComponent<ModelLook>();
    }

    /// Makes these parts pulse for a few seconds (they blocked a move).
    public static void Flash(IEnumerable<ModelPart> parts)
    {
        flashing.Clear();
        flashing.AddRange(parts);
        flashStart = Time.time;
    }

    /// Frees every held part in the model; returns how many were held.
    public static int FreeAll(ModelHost host)
    {
        int n = 0;
        if (host == null) return 0;
        foreach (var p in host.Parts) if (p != null && p.Held) { p.Held = false; n++; }
        return n;
    }

    public static int CountHeld(ModelHost host)
    {
        int n = 0;
        if (host != null) foreach (var p in host.Parts) if (p != null && p.Held) n++;
        return n;
    }

    void LateUpdate()
    {
        if (look == null) return;
        bool show = ViewModes.Move;
        if (!show)
        {
            if (wasShowing) look.ClearTints();
            wasShowing = false;
            return;
        }
        wasShowing = true;
        float since = Time.time - flashStart;
        bool flash = since < FlashSeconds;
        float pulse = 0.5f + 0.5f * Mathf.Sin(since * Mathf.PI * 4f);
        foreach (var p in host.Parts)
        {
            if (p == null || p.Renderer == null) continue;
            Color? c = null;
            if (flash && flashing.Contains(p)) c = Color.Lerp(HeldColor, FlashColor, pulse);
            else if (p.Held) c = HeldColor;
            look.SetTint(p.Renderer, c);
        }
    }
}
