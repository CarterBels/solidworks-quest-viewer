using System;
using UnityEngine;

/// A placeable keypad (pinch it by the side grips to move it) with a small display and buttons, pressed with a
/// fingertip or controller tip. SolidWorks fork layout (lighting is always shaded):
///   main page                                   settings page (the gear button)
///   (gear)     RESET      INSPECT                (gear)   CONNECTION: address, pair code
///   CUT        ROTATE     LOCK                   FIXED    STYLE    HOLO
///   LOCK PART  ISOLATE    EXPLODE                BOX      SPAWN – x ft +
///   CONTROLS   FIX PART   MOVE                            BRIGHTNESS slider
/// LOCK PART, ISOLATE and FIX PART show only once what they depend on is on (EXPLODE -> ISOLATE -> LOCK PART;
/// MOVE -> FIX PART); ROTATE hides while LOCK is on.
/// A holo-screen above it shows the inspected or isolated part's specs (PartInfoPanel).
/// The display shows the scale and short status messages. Voice commands press the same buttons (Press).
[RequireComponent(typeof(Grabbable))]
public class Keypad : MonoBehaviour
{
    public ModelHost host;
    public Grabbable model;
    public Grabbable placeholder;

    internal static readonly Color Accent = new Color(0.35f, 0.9f, 1f);
    static readonly Color Green = new Color(0.45f, 1f, 0.55f);
    static readonly Color Red = new Color(1f, 0.45f, 0.4f);

    const float Pitch = 0.043f;
    const float DisplayY = 0.052f;
    const float GripWidth = 0.03f;

    KeypadButton holo, style, explode, turntable, cut, fixedScale, inspect, reset, lockButton, grabBox, isolate, lockPart, moveButton, fixPart, gear, controls;
    Transform mainPage, settingsPage;
    bool settingsOpen;
    KeypadSlider brightness;
    readonly System.Collections.Generic.Dictionary<string, KeypadButton> byLabel =
        new System.Collections.Generic.Dictionary<string, KeypadButton>();
    TextMesh scaleText, infoText, spawnText, connectionText;
    ModelServer server;
    AudioSource audioSource;
    AudioClip click;

    void Awake()
    {
        var baseMat = NewLit(new Color(0.07f, 0.08f, 0.1f), 0.6f);
        var capMat = NewLit(new Color(0.2f, 0.22f, 0.26f), 0.5f);

        // Plate covering the display and the 3 x 4 buttons (the last row holds the buttons that come and go).
        float top = DisplayY + 0.024f, bottom = -3 * Pitch - 0.024f;
        var plateSize = new Vector3(3 * Pitch + 0.012f, top - bottom, 0.012f);
        var plateCenter = new Vector3(0f, (top + bottom) * 0.5f, 0f);
        var plate = GameObject.CreatePrimitive(PrimitiveType.Cube);
        Destroy(plate.GetComponent<Collider>());
        plate.name = "Plate";
        plate.transform.SetParent(transform, false);
        plate.transform.localPosition = plateCenter;
        plate.transform.localScale = plateSize;
        plate.GetComponent<Renderer>().sharedMaterial = baseMat;

        // Grips on both sides (SolidWorks fork): the keypad is grabbed only by these, so reaching for it doesn't
        // press buttons. Ribbed, raised, and a little lighter than the plate.
        var gripMat = NewLit(new Color(0.16f, 0.2f, 0.24f), 0.3f);
        var ribMat = NewLit(new Color(0.35f, 0.9f, 1f) * 0.45f, 0.5f);
        for (int side = -1; side <= 1; side += 2)
        {
            float x = side * (plateSize.x * 0.5f + GripWidth * 0.5f);
            var grip = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Destroy(grip.GetComponent<Collider>());
            grip.name = side < 0 ? "GripRight" : "GripLeft";
            grip.transform.SetParent(transform, false);
            grip.transform.localPosition = new Vector3(x, plateCenter.y, 0.004f);
            grip.transform.localScale = new Vector3(GripWidth, plateSize.y, 0.02f);
            grip.GetComponent<Renderer>().sharedMaterial = gripMat;
            for (int i = 0; i < 9; i++)
            {
                var rib = GameObject.CreatePrimitive(PrimitiveType.Cube);
                Destroy(rib.GetComponent<Collider>());
                rib.name = "Rib";
                rib.transform.SetParent(grip.transform, false);
                rib.transform.localPosition = new Vector3(0f, (i - 4) * 0.075f, 0.5f);
                rib.transform.localScale = new Vector3(0.7f, 0.012f, 0.1f);
                rib.GetComponent<Renderer>().sharedMaterial = ribMat;
            }
            // A generous grab volume around the grip (a little wider and deeper than it looks).
            var grab = gameObject.AddComponent<BoxCollider>();
            grab.center = new Vector3(x + side * 0.004f, plateCenter.y, 0.004f);
            grab.size = new Vector3(GripWidth + 0.012f, plateSize.y, 0.04f);
        }

        // Display: a dark screen with the scale and a status line.
        var screen = GameObject.CreatePrimitive(PrimitiveType.Quad);
        Destroy(screen.GetComponent<Collider>());
        screen.name = "Screen";
        screen.transform.SetParent(transform, false);
        screen.transform.localPosition = new Vector3(0f, DisplayY, 0.0062f);
        screen.transform.localRotation = Quaternion.Euler(0f, 180f, 0f);
        screen.transform.localScale = new Vector3(3 * Pitch - 0.004f, 0.04f, 1f);
        screen.GetComponent<Renderer>().sharedMaterial = NewUnlit(new Color(0.01f, 0.03f, 0.04f));
        scaleText = KeypadButton.MakeText(transform, "100%", new Vector3(0f, DisplayY + 0.007f, 0.0066f), 0.0022f, Accent);
        scaleText.fontStyle = FontStyle.Bold;
        infoText = KeypadButton.MakeText(transform, "", new Vector3(0f, DisplayY - 0.011f, 0.0066f), 0.00075f, Color.white);

        mainPage = new GameObject("MainPage").transform;
        mainPage.SetParent(transform, false);
        settingsPage = new GameObject("SettingsPage").transform;
        settingsPage.SetParent(transform, false);

        // Main page: what you use while looking at the model.
        reset = Button(mainPage, 1, 0, "RESET", ResetObject, capMat);
        inspect = Button(mainPage, 2, 0, "INSPECT", ViewModes.ToggleInspect, capMat);
        cut = Button(mainPage, 0, 1, "CUT", ViewModes.ToggleCut, capMat);
        turntable = Button(mainPage, 1, 1, "ROTATE", ViewModes.ToggleTurntable, capMat);
        lockButton = Button(mainPage, 2, 1, "LOCK", ToggleLock, capMat);
        explode = Button(mainPage, 2, 2, "EXPLODE", ViewModes.ToggleExplode, capMat);
        moveButton = Button(mainPage, 2, 3, "MOVE", ToggleMove, capMat);
        // Buttons that only mean something once another mode is on: shown only then, beside what they depend on.
        lockPart = Button(mainPage, 0, 2, "LOCK PART", ToggleLockPart, capMat);
        isolate = Button(mainPage, 1, 2, "ISOLATE", ToggleIsolate, capMat);
        fixPart = Button(mainPage, 1, 3, "FIX PART", ToggleFixPart, capMat);
        controls = Button(mainPage, 0, 3, "CONTROLS", ControlsPanel.Toggle, capMat);

        // Settings page (gear): how things look and appear.
        fixedScale = Button(settingsPage, 0, 1, "FIXED", ToggleFixedScale, capMat);
        style = Button(settingsPage, 1, 1, "STYLE", ViewModes.CycleStyle, capMat);
        holo = Button(settingsPage, 2, 1, "HOLO", ViewModes.ToggleHologram, capMat);
        grabBox = Button(settingsPage, 0, 2, "BOX", ToggleBox, capMat);
        // SPAWN – 2 ft +: how far in front of you a model appears (1-5 ft), across the two right columns.
        float spawnX = -Pitch * 0.5f, spawnY = -2 * Pitch;
        foreach (var (label, step) in new[] { ("–", -1f), ("+", 1f) })
        {
            var b = Button(settingsPage, 1, 1, label, () => StepSpawn(step), capMat);
            b.transform.localPosition = new Vector3(spawnX - step * 0.031f, spawnY, 0f);
            b.transform.localScale = Vector3.one * 0.6f;
            b.UseBigSymbol();
        }
        spawnText = KeypadButton.MakeText(settingsPage, "", new Vector3(spawnX, spawnY, 0.0066f), 0.0008f, Color.white);
        // Connection: the address and pairing code to enter in SolidWorks (Quest settings), top row, right two columns.
        var conn = GameObject.CreatePrimitive(PrimitiveType.Quad);
        Destroy(conn.GetComponent<Collider>());
        conn.name = "ConnectionScreen";
        conn.transform.SetParent(settingsPage, false);
        conn.transform.localPosition = new Vector3(-Pitch * 0.5f, 0f, 0.0062f);
        conn.transform.localRotation = Quaternion.Euler(0f, 180f, 0f);
        conn.transform.localScale = new Vector3(2 * Pitch - 0.006f, Pitch - 0.006f, 1f);
        conn.GetComponent<Renderer>().sharedMaterial = NewUnlit(new Color(0.01f, 0.03f, 0.04f));
        connectionText = KeypadButton.MakeText(settingsPage, "", new Vector3(-Pitch * 0.5f, 0f, 0.0066f), 0.0008f, Color.white);

        // Brightness: a slider across the two right columns of the bottom row.
        brightness = KeypadSlider.Create(settingsPage, new Vector3(-Pitch * 0.5f, -3 * Pitch, 0f), 2 * Pitch - 0.01f, this,
            () => Mathf.InverseLerp(SceneLighting.MinBrightness, SceneLighting.MaxBrightness, SceneLighting.Brightness),
            v => SceneLighting.Brightness = Mathf.Lerp(SceneLighting.MinBrightness, SceneLighting.MaxBrightness, v),
            () => $"BRIGHTNESS  {SceneLighting.Brightness * 100f:0}%");

        // The gear: always there, switches pages.
        gear = Button(transform, 0, 0, "SETTINGS", ToggleSettings, capMat);
        gear.UseGearIcon();
        ShowPage();

        gameObject.AddComponent<PartInfoPanel>().keypadTop = top;
        gameObject.AddComponent<ControlsPanel>();

        audioSource = gameObject.AddComponent<AudioSource>();
        audioSource.spatialBlend = 1f;
        audioSource.minDistance = 0.3f;
        audioSource.playOnAwake = false;
        click = MakeClick();
    }

    KeypadButton Button(Transform parent, int col, int row, string label, Action action, Material capMat)
    {
        var b = KeypadButton.Create(parent, new Vector3((1 - col) * Pitch, -row * Pitch, 0f), label, capMat, action, this);
        byLabel[label] = b;
        return b;
    }

    /// The gear: main buttons <-> settings (HOLO, STYLE, BOX, FIXED, spawn distance, brightness).
    void ToggleSettings()
    {
        settingsOpen = !settingsOpen;
        ShowPage();
        if (settingsOpen) Status.Info("Settings\ngear again to go back", 2f);
    }

    void ShowPage()
    {
        mainPage.gameObject.SetActive(!settingsOpen);
        settingsPage.gameObject.SetActive(settingsOpen);
        gear.SetState(settingsOpen ? "BACK" : "SETTINGS", settingsOpen);
    }

    /// Shows a button only while it means something; it also stays while it is on itself, so it can be turned off.
    static void ShowIf(KeypadButton b, bool show)
    {
        if (b != null && b.gameObject.activeSelf != show) b.gameObject.SetActive(show);
    }

    /// Presses a button by its label (HOLO, STYLE, …, RESET), with the same click and animation as a touch.
    public bool Press(string label)
    {
        if (!byLabel.TryGetValue(label, out var b)) return false;
        b.Press();
        return true;
    }

    /// Whether a button's LED is lit (its mode is on). False for unknown labels.
    public bool IsOn(string label) => byLabel.TryGetValue(label, out var b) && b.IsOn;

    /// Back in front of you at 100%, with the hologram scan-in.
    public void Recenter() => ResetObject();

    /// Plays a sound from the keypad (Marvin's voice).
    public void Say(AudioClip clip, float volume = 0.7f)
    {
        if (clip == null) return;
        audioSource.pitch = 1f;
        audioSource.PlayOneShot(clip, volume);
    }

    /// FIXED scale on: back to exactly 100%, and nothing can scale it until it's turned off.
    void ToggleFixedScale()
    {
        ViewModes.ToggleFixedScale();
        if (!ViewModes.FixedScale) return;
        foreach (var g in new[] { model, placeholder })
            if (g != null && g.allowScale && !g.IsTrueSize) g.ResetScale();
        Status.Info("Fixed scale\n100%, scaling is off", 2f);
    }

    /// LOCK on: the model stays put (no grabbing, pushing or throwing), so a casual flick can't send it flying.
    void ToggleLock()
    {
        ViewModes.ToggleLock();
        Status.Info(ViewModes.Lock ? "Model locked\nit stays where it is" : "Model unlocked", 2f);
    }

    /// SPAWN - / +: models appear 1-5 ft in front of you (their near side), in half-foot steps. Takes effect at the
    /// next RESET (or the next first model), so the model doesn't jump while you adjust.
    public void StepSpawn(float direction)
    {
        SpawnInFront.SpawnFeet += direction * SpawnInFront.StepFeet;
        Status.Info($"Models appear {SpawnInFront.SpawnFeet:0.#} ft away\nRESET to move it there", 2f);
    }

    /// MOVE: pinch a part to turn or slide it as its SolidWorks mates allow (the model holds still); off puts
    /// every part back where the model has it.
    void ToggleMove()
    {
        ViewModes.ToggleMove();
        int movable = 0;
        if (host != null) foreach (var p in host.Parts) if (p != null && p.CanMove && !p.Held) movable++;
        int held = HeldMarks.CountHeld(host);
        Status.Info(ViewModes.Move ? $"Move on\n{movable} parts can turn or slide" + (held > 0 ? $", {held} held (orange)" : "")
                                   : "Move off\nparts go back", 2.5f);
    }

    float lastFixPress = -10f;

    /// FIX PART: while on, pinching a part holds it still for MOVE (or lets it go); held parts glow orange.
    /// Double-tap: free every held part. Starts with SolidWorks' fixed parts held.
    void ToggleFixPart()
    {
        if (Time.time - lastFixPress < 0.7f)
        {
            lastFixPress = -10f;
            ViewModes.ToggleFixPick(); // undo the first tap
            int n = HeldMarks.FreeAll(host);
            Status.Info(n > 0 ? $"Freed all\n{n} held part{(n == 1 ? "" : "s")} can move again" : "Nothing is held", 2.5f);
            return;
        }
        lastFixPress = Time.time;
        ViewModes.ToggleFixPick();
        int held = HeldMarks.CountHeld(host);
        Status.Info(ViewModes.FixPick ? $"Fix part\npinch parts to hold or free them\n{held} held (orange) · double-tap frees all"
                                      : "Fix part done", 2.5f);
    }

    /// Voice "free all": every held part can move again.
    public void FreeAllHeld()
    {
        int n = HeldMarks.FreeAll(host);
        Status.Info(n > 0 ? $"Freed all\n{n} held part{(n == 1 ? "" : "s")} can move again" : "Nothing is held", 2.5f);
    }

    /// ISOLATE: parts pulled out of the exploded view stay where you put them; off sends them all home.
    void ToggleIsolate()
    {
        if (!ViewModes.Isolate && !ViewModes.Explode) ViewModes.ToggleExplode(); // parts can only be pulled out exploded
        ViewModes.ToggleIsolate();
        Status.Info(ViewModes.Isolate ? "Isolate on\npull a part out, it stays put" : "Isolate off\nparts go back", 2f);
    }

    /// LOCK PART: the isolated part shown on the holo-screen can't be moved (again to release it).
    void ToggleLockPart()
    {
        var p = PartInfoPanel.Focus;
        if (p == null || !p.Isolated)
        {
            Status.Info("Isolate a part first\nISOLATE, then pull one out", 2.5f);
            return;
        }
        p.Pinned = !p.Pinned;
        Status.Info((p.Pinned ? "Part locked\n" : "Part unlocked\n") + p.PartName, 2f);
        ViewModes.Touch();
    }

    /// BOX on: the model can be pinched anywhere inside its outlined bounding box (fingers push it like a block).
    void ToggleBox()
    {
        ViewModes.ToggleBox();
        Status.Info(ViewModes.Box ? "Grab box on\npinch anywhere inside" : "Grab box off\ngrab the parts", 2f);
    }

    void OnEnable() { ViewModes.Changed += Refresh; Refresh(); }
    void OnDisable() => ViewModes.Changed -= Refresh;

    void Refresh()
    {
        if (holo == null) return;
        holo.SetState(ViewModes.Hologram ? "ON" : "OFF", ViewModes.Hologram);
        style.SetState(ViewModes.Style == LookStyle.XRay ? "X-RAY" : "SOLID", ViewModes.Style == LookStyle.XRay);
        explode.SetState(ViewModes.Explode ? "ON" : "OFF", ViewModes.Explode);
        turntable.SetState(ViewModes.Turntable ? "ON" : "OFF", ViewModes.Turntable);
        cut.SetState(ViewModes.Cut ? "ON" : "OFF", ViewModes.Cut);
        fixedScale.SetState(ViewModes.FixedScale ? "ON" : "OFF", ViewModes.FixedScale);
        inspect.SetState(ViewModes.Inspect ? "ON" : "OFF", ViewModes.Inspect);
        reset.SetState("", false);
        lockButton.SetState(ViewModes.Lock ? "ON" : "OFF", ViewModes.Lock);
        grabBox.SetState(ViewModes.Box ? "ON" : "OFF", ViewModes.Box);
        isolate.SetState(ViewModes.Isolate ? "ON" : "OFF", ViewModes.Isolate);
        moveButton.SetState(ViewModes.Move ? "ON" : "OFF", ViewModes.Move);
        fixPart.SetState(ViewModes.FixPick ? "PICK" : "OFF", ViewModes.FixPick);
        controls.SetState(ControlsPanel.Visible ? "ON" : "", ControlsPanel.Visible);
    }

    Grabbable Target => host != null && host.HasModel ? model : placeholder;

    void ResetObject()
    {
        var t = Target;
        if (t == null) return;
        var spawn = t.GetComponent<SpawnInFront>();
        if (spawn != null) spawn.Respawn(); // also releases it and resets the scale to 100%
        else
        {
            t.ReleaseAll();
            t.ResetScale();
        }
    }

    void LateUpdate()
    {
        // LOCK PART follows whichever part the holo-screen shows.
        var focus = PartInfoPanel.Focus;
        bool pinned = focus != null && focus.Pinned;
        if (lockPart != null) lockPart.SetState(pinned ? "ON" : "OFF", pinned);
        if (spawnText != null) spawnText.text = $"SPAWN\n{SpawnInFront.SpawnFeet:0.#} ft";
        // Dependent buttons appear once what they depend on is on (and stay while they are on themselves).
        ShowIf(isolate, ViewModes.Explode || ViewModes.Isolate);
        ShowIf(lockPart, ViewModes.Isolate || pinned);
        ShowIf(fixPart, ViewModes.Move || ViewModes.FixPick);
        ShowIf(turntable, !ViewModes.Lock || ViewModes.Turntable); // a locked model doesn't turn

        var t = Target;
        float scale = t != null ? t.Scale : 1f;
        bool trueSize = t == null || t.IsTrueSize;
        scaleText.text = $"{scale * 100f:0}%";
        scaleText.color = trueSize ? Green : Accent;

        string info;
        var color = new Color(1f, 1f, 1f, 0.8f);
        if (UploadProgress.Active)
        {
            // Receiving the model from SolidWorks: 45%  3.9 of 8.6 MB (then "Loading" while it's built).
            long total = UploadProgress.Total, got = UploadProgress.Received;
            int percent = (int)(100 * got / System.Math.Max(1, total));
            info = $"Receiving  {percent}%\n{got / 1048576f:0.0} of {total / 1048576f:0.0} MB";
            color = Accent;
        }
        else if (Status.Visible)
        {
            info = Status.Message;
            if (Status.Type == Status.Kind.Error) color = Red;
            else if (Status.Type == Status.Kind.Ok) color = Green;
            if (Status.Type == Status.Kind.Busy) info += new string('.', 1 + (int)(Time.unscaledTime * 3f) % 3);
        }
        else if (ViewModes.Inspect) info = "INSPECT\ntouch a part";
        else if (ViewModes.Cut) info = "CUT\npinch the frame to move it";
        else
        {
            if (server == null) server = FindAnyObjectByType<ModelServer>();
            // The model's name (the address and code are on the settings page's connection screen).
            info = host != null && host.HasModel ? host.CurrentName : "No model yet\naddress and code: gear";
        }
        if (connectionText != null)
        {
            if (server == null) server = FindAnyObjectByType<ModelServer>();
            connectionText.text = server != null ? "CONNECTION\n" + server.PairingInfo.Replace("pair code ", "code ") : "CONNECTION\nnot ready";
            connectionText.color = Accent;
        }
        infoText.text = Fit(info, 28);
        infoText.color = color;
    }

    /// Keeps the display tidy: at most two lines of 'width' characters.
    static string Fit(string s, int width)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var lines = s.Split('\n');
        string a = Trim(lines[0], width);
        string b = lines.Length > 1 ? Trim(lines[1], width) : "";
        return b.Length > 0 ? a + "\n" + b : a;
    }

    static string Trim(string s, int width) => s.Length <= width ? s : s.Substring(0, width - 1) + "…";

    internal void PlayClick(float pitch)
    {
        audioSource.pitch = pitch;
        audioSource.PlayOneShot(click, 0.6f);
    }

    internal static Material NewLit(Color c, float smoothness)
    {
        var m = new Material(Shader.Find("Universal Render Pipeline/Lit")) { color = c };
        m.SetFloat("_Smoothness", smoothness);
        return m;
    }

    internal static Material NewUnlit(Color c) => new Material(Shader.Find("Universal Render Pipeline/Unlit")) { color = c };

    /// A short synthetic "blip": two quick tones with a fast decay.
    static AudioClip MakeClick()
    {
        const int rate = 44100;
        int n = rate / 16;
        var data = new float[n];
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)rate;
            float env = Mathf.Exp(-t * 55f);
            float f = t < 0.018f ? 1900f : 2600f;
            data[i] = Mathf.Sin(2f * Mathf.PI * f * t) * env * 0.5f;
        }
        var clip = AudioClip.Create("KeypadClick", n, 1, rate, false);
        clip.SetData(data, 0);
        return clip;
    }
}

/// One keypad button: pressed by anything with a Poker collider.
public class KeypadButton : MonoBehaviour
{
    public string label;
    const float Size = 0.037f;
    const float Travel = 0.006f;
    const float Cooldown = 0.35f;

    Action action;
    Keypad pad;
    Transform cap;
    TextMesh stateText;
    Material ledMat;
    float pressedAt = -10f;
    bool on;

    /// A single big symbol (the SPAWN – and +), centred, instead of a word label.
    public void UseBigSymbol()
    {
        title.characterSize = 0.0024f;
        title.transform.localPosition = new Vector3(0f, -0.001f, title.transform.localPosition.z);
    }

    public static KeypadButton Create(Transform parent, Vector3 pos, string label, Material capMat, Action action, Keypad pad)
    {
        var go = new GameObject("Button_" + label);
        go.transform.SetParent(parent, false);
        go.transform.localPosition = pos;
        var b = go.AddComponent<KeypadButton>();
        b.action = action;
        b.label = label;
        b.pad = pad;

        var trigger = go.AddComponent<BoxCollider>();
        trigger.isTrigger = true;
        trigger.size = new Vector3(Size, Size, 0.03f);
        trigger.center = new Vector3(0f, 0f, 0.016f);

        var capGo = GameObject.CreatePrimitive(PrimitiveType.Cube);
        Destroy(capGo.GetComponent<Collider>());
        capGo.name = "Cap";
        b.cap = capGo.transform;
        b.cap.SetParent(go.transform, false);
        b.cap.localScale = new Vector3(Size, Size, 0.012f);
        b.cap.localPosition = new Vector3(0f, 0f, 0.012f);
        capGo.GetComponent<Renderer>().sharedMaterial = capMat;

        var face = new GameObject("Face").transform;
        face.SetParent(go.transform, false);
        b.faceRoot = face;
        // Long labels ("LOCK PART") shrink to fit the cap. SolidWorks fork: bigger, for the Quest 3S's coarser screen.
        var title = MakeText(face, label, new Vector3(0f, 0.0025f, 0.0185f), 0.00088f * Mathf.Min(1f, 8f / label.Length), Color.white);
        title.fontStyle = FontStyle.Bold;
        b.title = title;
        b.stateText = MakeText(face, "", new Vector3(0f, -0.0095f, 0.0185f), 0.00078f, new Color(1f, 1f, 1f, 0.92f));
        b.stateText.fontStyle = FontStyle.Bold;

        var ledGo = GameObject.CreatePrimitive(PrimitiveType.Quad);
        Destroy(ledGo.GetComponent<Collider>());
        ledGo.name = "Led";
        ledGo.transform.SetParent(face, false);
        ledGo.transform.localPosition = new Vector3(0f, 0.0125f, 0.0185f);
        ledGo.transform.localRotation = Quaternion.Euler(0f, 180f, 0f);
        ledGo.transform.localScale = new Vector3(0.018f, 0.0026f, 1f);
        b.ledMat = Keypad.NewUnlit(Color.black);
        ledGo.GetComponent<Renderer>().sharedMaterial = b.ledMat;
        return b;
    }

    Transform faceRoot;
    TextMesh title;
    Material iconMat;

    /// Replaces the label with a gear drawn from simple shapes (the font has no gear symbol).
    public void UseGearIcon()
    {
        title.gameObject.SetActive(false);
        iconMat = Keypad.NewUnlit(new Color(0.85f, 0.95f, 1f));
        var holeMat = Keypad.NewUnlit(new Color(0.2f, 0.22f, 0.26f));
        var center = new Vector3(0f, 0.0015f, 0.0186f);
        Shape(PrimitiveType.Cylinder, center, Quaternion.Euler(90f, 0f, 0f), new Vector3(0.0105f, 0.0004f, 0.0105f), iconMat);
        Shape(PrimitiveType.Cylinder, center + new Vector3(0f, 0f, 0.0003f), Quaternion.Euler(90f, 0f, 0f), new Vector3(0.0045f, 0.0004f, 0.0045f), holeMat);
        for (int i = 0; i < 8; i++)
        {
            var rot = Quaternion.Euler(0f, 0f, i * 45f);
            Shape(PrimitiveType.Cube, center + rot * new Vector3(0f, 0.0058f, 0f), rot, new Vector3(0.0028f, 0.0026f, 0.0008f), iconMat);
        }
    }

    void Shape(PrimitiveType type, Vector3 pos, Quaternion rot, Vector3 scale, Material mat)
    {
        var go = GameObject.CreatePrimitive(type);
        Destroy(go.GetComponent<Collider>());
        go.name = "Icon";
        go.transform.SetParent(faceRoot, false);
        go.transform.localPosition = pos;
        go.transform.localRotation = rot;
        go.transform.localScale = scale;
        go.GetComponent<Renderer>().sharedMaterial = mat;
    }

    internal static TextMesh MakeText(Transform parent, string text, Vector3 localPos, float size, Color color)
    {
        var go = new GameObject("Text");
        go.transform.SetParent(parent, false);
        go.transform.localPosition = localPos;
        go.transform.localRotation = Quaternion.Euler(0f, 180f, 0f); // face the user (local +Z)
        var t = go.AddComponent<TextMesh>();
        t.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        go.GetComponent<MeshRenderer>().sharedMaterial = t.font.material;
        t.text = text;
        t.anchor = TextAnchor.MiddleCenter;
        t.alignment = TextAlignment.Center;
        t.fontSize = 64;
        t.characterSize = size;
        t.color = color;
        return t;
    }

    public bool IsOn => on;

    public void SetState(string text, bool active)
    {
        stateText.text = text;
        on = active;
    }

    void OnTriggerEnter(Collider other)
    {
        if (other.GetComponent<Poker>() != null) Press(true);
    }

    /// Touch or voice: click, animate, run the action. With CONTROLS on, touching a button shows how to use it
    /// instead (the gear still switches pages, so the settings buttons can be looked up too).
    public void Press(bool touch = false)
    {
        if (Time.time - pressedAt < Cooldown) return;
        pressedAt = Time.time;
        pad.PlayClick(on ? 0.9f : 1.1f);
        if (touch && ControlsPanel.Visible && label != "CONTROLS" && label != "GESTURES")
        {
            ControlsPanel.ShowFor(label);
            if (label != "SETTINGS") return;
        }
        action?.Invoke();
    }

    void Update()
    {
        // Press animation (cap and its labels move together) and LED.
        float t = Time.time - pressedAt;
        float press = t < 0.08f ? t / 0.08f : Mathf.Clamp01(1f - (t - 0.08f) / 0.15f);
        cap.localPosition = new Vector3(0f, 0f, 0.012f - Travel * press);
        faceRoot.localPosition = new Vector3(0f, 0f, -Travel * press);

        var c = on ? Keypad.Accent : Keypad.Accent * 0.15f;
        if (t < 0.35f) c = Color.Lerp(Color.white, c, t / 0.35f);
        c.a = 1f;
        ledMat.color = c;
    }

    void OnDestroy()
    {
        if (ledMat != null) Destroy(ledMat);
        if (iconMat != null) Destroy(iconMat);
    }
}

/// A finger slider (SolidWorks fork: brightness): touch the track and slide along it; the knob follows the fingertip.
public class KeypadSlider : MonoBehaviour
{
    Func<float> get;
    Action<float> set;
    Func<string> label;
    Keypad pad;
    float width;
    Transform knob;
    TextMesh text;
    float lastClick = -10f;
    float lastValue = -1f;

    public static KeypadSlider Create(Transform parent, Vector3 pos, float width, Keypad pad,
                                      Func<float> get, Action<float> set, Func<string> label)
    {
        var go = new GameObject("Slider");
        go.transform.SetParent(parent, false);
        go.transform.localPosition = pos;
        var s = go.AddComponent<KeypadSlider>();
        s.get = get; s.set = set; s.label = label; s.pad = pad; s.width = width;

        var trigger = go.AddComponent<BoxCollider>();
        trigger.isTrigger = true;
        trigger.size = new Vector3(width + 0.01f, 0.03f, 0.03f);
        trigger.center = new Vector3(0f, -0.004f, 0.016f);

        var track = GameObject.CreatePrimitive(PrimitiveType.Cube);
        Destroy(track.GetComponent<Collider>());
        track.name = "Track";
        track.transform.SetParent(go.transform, false);
        track.transform.localPosition = new Vector3(0f, -0.006f, 0.008f);
        track.transform.localScale = new Vector3(width, 0.004f, 0.004f);
        track.GetComponent<Renderer>().sharedMaterial = Keypad.NewUnlit(Keypad.Accent * 0.35f);

        var k = GameObject.CreatePrimitive(PrimitiveType.Cube);
        Destroy(k.GetComponent<Collider>());
        k.name = "Knob";
        s.knob = k.transform;
        s.knob.SetParent(go.transform, false);
        s.knob.localScale = new Vector3(0.008f, 0.016f, 0.012f);
        k.GetComponent<Renderer>().sharedMaterial = Keypad.NewUnlit(Keypad.Accent);

        s.text = KeypadButton.MakeText(go.transform, "", new Vector3(0f, 0.01f, 0.0066f), 0.0008f, Color.white);
        return s;
    }

    void OnTriggerStay(Collider other)
    {
        if (other.GetComponent<Poker>() == null) return;
        if (ControlsPanel.Visible) ControlsPanel.ShowFor("BRIGHTNESS");
        // Text faces the user (local +Z, rotated 180° about Y), so the user's left is local +X: left = 0.
        float x = transform.InverseTransformPoint(other.transform.position).x;
        float v = Mathf.Clamp01(0.5f - x / width);
        set(v);
        if (Mathf.Abs(v - lastValue) > 0.05f && Time.time - lastClick > 0.08f)
        {
            lastClick = Time.time;
            lastValue = v;
            pad.PlayClick(0.8f + v * 0.6f);
        }
    }

    void Update()
    {
        float v = get();
        knob.localPosition = new Vector3((0.5f - v) * width, -0.006f, 0.012f);
        text.text = label();
    }
}
