using System.Collections.Generic;
using System.Text;
using UnityEngine;

/// CONTROLS (SolidWorks fork): a holo-screen beside the keypad that explains one thing at a time. While it is on,
/// touching a keypad button shows that button's card (what it does, where it is, what to say to Marvin, the hand
/// gesture and the controller button) instead of pressing it; the gear still switches pages, so the settings
/// buttons can be looked up too. Before any button is touched it shows the things that need no button.
public class ControlsPanel : MonoBehaviour
{
    public static bool Visible { get; private set; }
    static string current;
    static int version;

    public static void Toggle()
    {
        Visible = !Visible;
        current = null;
        version++;
        ViewModes.Touch();
    }

    /// Show the card for a keypad button label (CONTROLS must be on).
    public static void ShowFor(string label)
    {
        if (label == "–" || label == "+") label = "SPAWN";
        if (label == current) return;
        current = label;
        version++;
    }

    struct Card
    {
        public string title, what, keypad, say, hands, controller;
        public Card(string title, string what, string keypad, string say = "", string hands = "", string controller = "")
        { this.title = title; this.what = what; this.keypad = keypad; this.say = say; this.hands = hands; this.controller = controller; }
    }

    static readonly Dictionary<string, Card> Cards = new Dictionary<string, Card>
    {
        ["RESET"] = new Card("RESET", "Brings the model back in front of you at 100%.", "main page", "reset, recenter", "", "A or X"),
        ["INSPECT"] = new Card("INSPECT", "Touch a part with a fingertip to see its specs on the holo-screen. Touch it twice quickly to isolate it (the rest turns X-ray).", "main page", "inspect", "touch a part; twice to isolate"),
        ["CUT"] = new Card("CUT", "Section cut: a frame cuts away everything on one side of it.", "main page", "cut, section cut", "pinch the frame to move or tilt it"),
        ["ROTATE"] = new Card("ROTATE", "Spins the model slowly. Hidden while LOCK is on.", "main page", "rotate", "point your index finger up and circle it flat; a ring above the finger fills in (green: on, red struck through: off)"),
        ["LOCK"] = new Card("LOCK", "The model stays where it is: it can't be grabbed, pushed or thrown. Exploded parts can still be pulled out.", "main page", "lock, unlock"),
        ["EXPLODE"] = new Card("EXPLODE", "Pulls the parts apart. Pinch a part to pull it out; it glides back when you let go.", "main page", "explode", "clap; pinch a part to pull it out", "B or Y"),
        ["ISOLATE"] = new Card("ISOLATE", "Parts you pull out stay where you let go (or settle onto a table). Off sends them home. Turns EXPLODE on too.", "main page, once EXPLODE is on", "isolate", "pull a part out and let go"),
        ["LOCK PART"] = new Card("LOCK PART", "Fixes the isolated part shown on the holo-screen in place. Again to release it.", "main page, once ISOLATE is on", "lock part"),
        ["MOVE"] = new Card("MOVE", "Pinch a part to turn or slide it as its SolidWorks mates allow; mated parts follow. Off puts them back.", "main page", "move", "pinch a part and turn or slide it"),
        ["FIX PART"] = new Card("FIX PART", "Pinch parts to hold them still for MOVE (they glow orange). Double-tap the button to free them all.", "main page, once MOVE is on", "fix part; free all", "pinch a part to hold or free it"),
        ["SETTINGS"] = new Card("THE GEAR", "Switches between the main page and the settings page.", "both pages", "settings, options"),
        ["HOLO"] = new Card("HOLO", "Hologram look: a blue see-through scan effect.", "settings page", "hologram", "", "thumbstick click"),
        ["STYLE"] = new Card("STYLE", "Solid or X-ray look.", "settings page", "style, x ray"),
        ["BOX"] = new Card("BOX", "Outlines the model's bounding box: pinch anywhere inside it to grab the whole model. Part boxes show too.", "settings page", "box, grab box", "pinch inside the box"),
        ["FIXED"] = new Card("FIXED", "Fixed scale: back to exactly 100%, and nothing can scale it until it's off. On by default.", "settings page", "fixed, fixed scale"),
        ["SPAWN"] = new Card("SPAWN  – +", "How far in front of you models appear, 1 to 5 ft. Takes effect at the next RESET.", "settings page", "closer, farther"),
        ["BRIGHTNESS"] = new Card("BRIGHTNESS", "How bright the model's lighting is.", "settings page (slider)", "", "slide a fingertip along it"),
        ["CONTROLS"] = new Card("CONTROLS", "This help. Touch a button to see how to use it.", "main page", "controls, help"),
    };

    const string General =
        "Touch any button to see how to use it\n(it won't be pressed). CONTROLS again to close.\n\n" +
        "Lost the keypad?  \"Marvin, tablet\" brings it back\n" +
        "Send from SolidWorks:  Send to Quest there,\n   or \"Marvin, send this to the Quest\"\n" +
        "Controllers:  grip grabs, the tip presses keys,\n   A / X brings the model back, B / Y explodes\n\n" +
        "<color=#9fe8ffcc>Based on Onshape Quest Viewer by almostinventor\ngithub.com/almostinventor/onshape-quest-viewer · MIT</color>";

    // The GESTURES dropdown on the general page: every hand gesture.
    const string Gestures =
        "<b>ANY TIME</b>\n" +
        "Pinch it:  grab the model\n" +
        "Close your hand on it:  whole-hand grab\n" +
        "Pinch it with both hands:  turn and scale it\n" +
        "Pinch the air with both hands:  scale in place\n" +
        "Double-pinch the model:  back to 100%\n" +
        "Push it with your fingers:  nudge it\n" +
        "Clap:  EXPLODE on / off\n" +
        "Index finger up, circle it flat:  ROTATE\n" +
        "Pinch the keypad's side grips:  move the keypad\n" +
        "(\"Marvin, tablet\" brings the keypad back)\n" +
        "\n<b>IN A MODE</b>\n" +
        "EXPLODE:  pinch a part to pull it out\n" +
        "ISOLATE:  let go of a part, it stays there\n" +
        "INSPECT:  touch a part; twice to isolate it\n" +
        "MOVE:  pinch a part, turn or slide it\n" +
        "FIX PART:  pinch a part to hold or free it\n" +
        "CUT:  pinch the frame to move or tilt it\n" +
        "BOX:  pinch anywhere inside the box\n" +
        "BRIGHTNESS:  slide a fingertip along it";

    static bool gesturesOpen;
    KeypadButton gesturesButton;
    TextMesh gestureList;

    const float Width = 0.24f;
    const float Margin = 0.012f;
    const float Size = 0.001f;
    const float Gap = 0.02f;

    Transform root, pane;
    TextMesh title, body;
    int shownVersion = -1;

    void Build()
    {
        root = new GameObject("ControlsPanel").transform;
        root.SetParent(transform, false);
        var quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
        Destroy(quad.GetComponent<Collider>());
        quad.name = "Pane";
        pane = quad.transform;
        pane.SetParent(root, false);
        pane.localRotation = Quaternion.Euler(0f, 180f, 0f);
        var shader = Resources.Load<Shader>("HoloPanel");
        quad.GetComponent<Renderer>().sharedMaterial =
            shader != null ? new Material(shader) : Keypad.NewUnlit(new Color(0.02f, 0.08f, 0.1f));

        title = Text(Size * 1.6f, Keypad.Accent);
        title.fontStyle = FontStyle.Bold;
        body = Text(Size, Color.white);
        gestureList = Text(Size, Color.white);
        gesturesButton = KeypadButton.Create(root, Vector3.zero, "GESTURES", Keypad.NewLit(new Color(0.2f, 0.22f, 0.26f), 0.5f),
            () => { gesturesOpen = !gesturesOpen; version++; }, GetComponent<Keypad>());
        gesturesButton.transform.localScale = Vector3.one * 0.85f;
    }

    TextMesh Text(float size, Color color)
    {
        var t = KeypadButton.MakeText(root, "", Vector3.zero, size, color);
        t.anchor = TextAnchor.UpperLeft;
        t.alignment = TextAlignment.Left;
        t.richText = true;
        return t;
    }

    void LateUpdate()
    {
        if (Visible && root == null) Build();
        if (root == null) return;
        if (root.gameObject.activeSelf != Visible) root.gameObject.SetActive(Visible);
        if (!Visible || shownVersion == version) return;
        shownVersion = version;

        string accent = "#" + ColorUtility.ToHtmlStringRGB(Keypad.Accent);
        int lines;
        if (current != null && Cards.TryGetValue(current, out var c))
        {
            title.text = c.title;
            var sb = new StringBuilder();
            sb.Append(Wrap(c.what, 44)).Append("\n\n");
            Row(sb, accent, "KEYPAD", c.keypad);
            Row(sb, accent, "SAY", c.say.Length > 0 ? "\"Marvin, " + c.say + "\"" : "");
            Row(sb, accent, "HANDS", c.hands);
            Row(sb, accent, "CONTROLLER", c.controller);
            sb.Append("\n<color=#ffffff99>touch another button · CONTROLS to close</color>");
            body.text = sb.ToString();
        }
        else
        {
            title.text = "CONTROLS";
            body.text = General;
            gestureList.text = Gestures.Replace("<b>", $"<color={accent}><b>").Replace("</b>", "</b></color>");
        }
        bool general = current == null || !Cards.ContainsKey(current);
        gesturesButton.gameObject.SetActive(general);
        gestureList.gameObject.SetActive(general && gesturesOpen);
        gesturesButton.SetState(gesturesOpen ? "HIDE" : "SHOW", gesturesOpen);
        lines = body.text.Split('\n').Length;

        // Size the pane to the text: title, then the body below it.
        float line = 7.5f * Size, titleLine = 7.5f * Size * 1.6f;
        const float buttonRow = 0.042f;
        int listLines = general && gesturesOpen ? gestureList.text.Split('\n').Length : 0;
        float height = Margin * 2f + titleLine + line * 0.6f + lines * line
                       + (general ? buttonRow : 0f) + listLines * line;
        float x = Width * 0.5f - Margin, top = height * 0.5f - Margin;
        title.transform.localPosition = new Vector3(x, top, 0.002f);
        body.transform.localPosition = new Vector3(x, top - titleLine - line * 0.6f, 0.002f);
        float below = top - titleLine - line * 0.6f - lines * line;
        gesturesButton.transform.localPosition = new Vector3(x - 0.016f, below - buttonRow * 0.5f, 0f);
        gestureList.transform.localPosition = new Vector3(x, below - buttonRow, 0.002f);
        pane.localScale = new Vector3(Width, height, 1f);

        // Beside the keypad on the user's left (local +X), top edges level.
        var plate = transform.Find("Plate");
        float half = plate != null ? plate.localScale.x * 0.5f + 0.03f : 0.1f;
        float keypadTop = plate != null ? plate.localPosition.y + plate.localScale.y * 0.5f : 0.08f;
        root.localPosition = new Vector3(half + Gap + Width * 0.5f, keypadTop - height * 0.5f, 0f);
    }

    static void Row(StringBuilder sb, string accent, string label, string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        sb.Append($"<color={accent}><b>{label}</b></color>\n").Append(Wrap(text, 44)).Append("\n\n");
    }

    /// Breaks a line into lines of at most 'width' characters, at spaces.
    static string Wrap(string s, int width)
    {
        var sb = new StringBuilder();
        int lineLen = 0;
        foreach (var word in s.Split(' '))
        {
            if (lineLen > 0 && lineLen + 1 + word.Length > width) { sb.Append('\n'); lineLen = 0; }
            else if (lineLen > 0) { sb.Append(' '); lineLen++; }
            sb.Append(word);
            lineLen += word.Length;
        }
        return sb.ToString();
    }
}
