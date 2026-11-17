using System;
using UnityEngine;

public enum LookStyle { Solid, Clay, XRay }
public enum LightingMode { Shaded, Flat }

/// The user-switchable modes (keypad, controller buttons, gestures, voice).
/// Look settings (hologram, style, lighting) and fixed scale are remembered between sessions; tool modes start off.
/// The hologram look is on by default.
public static class ViewModes
{
    public static bool Hologram { get; private set; }
    public static LookStyle Style { get; private set; }
    public static LightingMode Lighting { get; private set; }
    public static bool Explode { get; private set; }
    public static bool Turntable { get; private set; }
    /// Spin direction about the vertical axis: +1 or -1 (same sense as Unity's rotation about +Y).
    public static float TurntableDirection { get; private set; } = 1f;
    public static bool Cut { get; private set; }
    /// Nothing can change the model's scale (two-hand grab only moves and rotates, air pinch does nothing).
    public static bool FixedScale { get; private set; }
    public static bool Inspect { get; private set; }
    /// Grab box: pinch anywhere inside the model's bounding box to grab it, and fingers don't push it away.
    public static bool Box { get; private set; }
    /// Lock: the model stays where it is (no grabbing, pushing or throwing); parts can still be pulled out when
    /// exploded. Starts off, like the other tool modes.
    public static bool Lock { get; private set; }
    /// Isolate (SolidWorks fork): a part pulled out of the exploded view stays where you put it until this is turned
    /// off. Starts off.
    public static bool Isolate { get; private set; }
    /// Move (SolidWorks fork): pinch a part to turn or slide it as its SolidWorks mates allow; off puts them back.
    public static bool Move { get; private set; }
    /// Fix part (SolidWorks fork): pinch parts to hold them still (or let them go) for MOVE.
    public static bool FixPick { get; private set; }

    public static event Action Changed;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void Load()
    {
        Hologram = PlayerPrefs.GetInt("mode.hologram", 1) == 1;
        // SolidWorks fork: solid or X-ray only (no clay), and always shaded lighting.
        Style = PlayerPrefs.GetInt("mode.style", 0) == (int)LookStyle.XRay ? LookStyle.XRay : LookStyle.Solid;
        Lighting = LightingMode.Shaded;
        FixedScale = PlayerPrefs.GetInt("mode.fixedscale", 0) == 1;
        Box = PlayerPrefs.GetInt("mode.box", 0) == 1;
        // SolidWorks fork: FIXED is the only mode on by default. Applied once to existing installs too.
        if (PlayerPrefs.GetInt("defaults.v2", 0) == 0)
        {
            Hologram = Box = false;
            FixedScale = true;
            Style = LookStyle.Solid;
            PlayerPrefs.SetInt("defaults.v2", 1);
            Save();
        }
        Explode = Turntable = Cut = Inspect = Lock = Isolate = Move = FixPick = false;
    }

    public static void ToggleHologram() { Hologram = !Hologram; Save(); }
    public static void CycleStyle() { Style = Style == LookStyle.XRay ? LookStyle.Solid : LookStyle.XRay; Save(); }
    public static void ToggleLighting() { Lighting = Lighting == LightingMode.Shaded ? LightingMode.Flat : LightingMode.Shaded; Save(); }
    public static void ToggleExplode() { Explode = !Explode; Save(); }
    public static void ToggleTurntable() { Turntable = !Turntable; Save(); }
    public static void SetTurntable(bool on, float direction)
    {
        Turntable = on;
        if (direction != 0f) TurntableDirection = Mathf.Sign(direction);
        Save();
    }
    public static void ToggleCut() { Cut = !Cut; Save(); }
    public static void ToggleFixedScale() { FixedScale = !FixedScale; Save(); }
    public static void ToggleInspect() { Inspect = !Inspect; Save(); }
    public static void ToggleBox() { Box = !Box; Save(); }
    public static void ToggleLock() { Lock = !Lock; Save(); }
    public static void ToggleIsolate() { Isolate = !Isolate; Save(); }
    public static void ToggleMove() { Move = !Move; if (!Move) FixPick = false; Save(); }
    public static void ToggleFixPick() { FixPick = !FixPick; if (FixPick) Move = true; Save(); }
    /// Something changed that only the keypad shows (e.g. a part was locked): refresh it.
    public static void Touch() => Changed?.Invoke();

    static void Save()
    {
        PlayerPrefs.SetInt("mode.hologram", Hologram ? 1 : 0);
        PlayerPrefs.SetInt("mode.style", (int)Style);
        PlayerPrefs.SetInt("mode.lighting", (int)Lighting);
        PlayerPrefs.SetInt("mode.fixedscale", FixedScale ? 1 : 0);
        PlayerPrefs.SetInt("mode.box", Box ? 1 : 0);
        PlayerPrefs.Save();
        Changed?.Invoke();
    }
}
