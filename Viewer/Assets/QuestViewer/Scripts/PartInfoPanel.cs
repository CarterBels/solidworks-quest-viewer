using System.Collections.Generic;
using UnityEngine;

/// A holo-screen above the keypad with everything known about one part (SolidWorks fork): name, sub-assembly, file,
/// configuration, material, size, volume, surface area, mass, density, how many are in the model, triangles.
/// It shows the part touched in INSPECT mode, or the part last pulled out with ISOLATE on.
/// Specs beyond size/mass/volume come from the SolidWorks add-in (glTF node extras); unknown ones are left out.
public class PartInfoPanel : MonoBehaviour
{
    /// The part to describe (set by PartInspector and the isolate logic); null hides the panel.
    public static ModelPart Focus;

    const float Width = 0.235f;
    const float Gap = 0.012f;
    const float Margin = 0.012f;
    const float Line = 0.0083f;      // row height at characterSize 0.0011 (measured in the headset: ~7.5 * size)
    const float TitleLine = 0.0121f; // same ratio at 0.0016
    const float OpenSeconds = 0.18f;

    public float keypadTop = 0.074f;

    Transform root;
    Transform pane;
    Material paneMat;
    TextMesh title, sub, labels, values, footer;
    ModelPart shown;
    string shownKey;
    float openedAt = -10f;
    float height = 0.1f;

    void Awake()
    {
        root = new GameObject("PartInfoPanel").transform;
        root.SetParent(transform, false);

        var quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
        Destroy(quad.GetComponent<Collider>());
        quad.name = "Pane";
        pane = quad.transform;
        pane.SetParent(root, false);
        pane.localRotation = Quaternion.Euler(0f, 180f, 0f);
        var shader = Resources.Load<Shader>("HoloPanel");
        paneMat = shader != null ? new Material(shader) : Keypad.NewUnlit(new Color(0.02f, 0.08f, 0.1f));
        quad.GetComponent<Renderer>().sharedMaterial = paneMat;

        title = Text(0.0016f, Keypad.Accent, TextAnchor.UpperLeft);
        title.fontStyle = FontStyle.Bold;
        sub = Text(0.0009f, new Color(0.7f, 0.95f, 1f, 0.75f), TextAnchor.UpperLeft);
        labels = Text(0.0011f, new Color(0.6f, 0.9f, 1f, 0.8f), TextAnchor.UpperLeft);
        values = Text(0.0011f, Color.white, TextAnchor.UpperLeft);
        footer = Text(0.0009f, new Color(1f, 0.85f, 0.45f), TextAnchor.LowerLeft);
        root.gameObject.SetActive(false);
    }

    TextMesh Text(float size, Color color, TextAnchor anchor)
    {
        var t = KeypadButton.MakeText(root, "", Vector3.zero, size, color);
        t.anchor = anchor;
        t.alignment = TextAlignment.Left;
        return t;
    }

    void OnDestroy()
    {
        if (paneMat != null) Destroy(paneMat);
    }

    void LateUpdate()
    {
        var part = Focus;
        bool show = part != null && part.isActiveAndEnabled && (ViewModes.Inspect || part.Isolated || ViewModes.Move);
        if (!show)
        {
            if (root.gameObject.activeSelf) root.gameObject.SetActive(false);
            shown = null;
            return;
        }

        if (!root.gameObject.activeSelf)
        {
            root.gameObject.SetActive(true);
            openedAt = Time.unscaledTime;
        }
        string key = part.Isolated + "|" + part.Pinned + "|" + part.Held + "|" + part.PositionText();
        if (part != shown || key != shownKey)
        {
            if (part != shown) openedAt = Time.unscaledTime;
            shown = part;
            shownKey = key;
            Fill(part);
        }

        // Open by unfolding vertically from the keypad's top edge.
        float u = Mathf.Clamp01((Time.unscaledTime - openedAt) / OpenSeconds);
        u = 1f - (1f - u) * (1f - u);
        root.localScale = new Vector3(1f, Mathf.Max(u, 0.01f), 1f);
        paneMat.SetFloat("_Fade", u);
    }

    void Fill(ModelPart p)
    {
        var rows = new List<(string, string)>();
        if (p.Members > 1) rows.Add(("Group", $"{p.Members} parts merged"));
        if (!string.IsNullOrEmpty(p.File)) rows.Add(("File", p.File));
        if (!string.IsNullOrEmpty(p.Config)) rows.Add(("Configuration", p.Config));
        if (!string.IsNullOrEmpty(p.Body) && p.Body != p.PartName) rows.Add(("Body", p.Body));
        if (p.Members <= 1) rows.Add(("Material", string.IsNullOrEmpty(p.Material) ? "none assigned" : p.Material));
        var s = p.SizeMm;
        rows.Add(("Size", $"{s.x:0.#} × {s.y:0.#} × {s.z:0.#} mm"));
        rows.Add(("", $"{s.x / 25.4f:0.##} × {s.y / 25.4f:0.##} × {s.z / 25.4f:0.##} in"));
        if (Known(p.VolumeM3)) rows.Add(("Volume", $"{p.VolumeM3 * 1e6f:0.##} cm³   ({p.VolumeM3 * 61023.7f:0.###} in³)"));
        if (Known(p.AreaM2)) rows.Add(("Surface area", $"{p.AreaM2 * 1e4f:0.#} cm²   ({p.AreaM2 * 1550.0f:0.##} in²)"));
        if (Known(p.MassKg)) rows.Add(("Mass", Mass(p.MassKg)));
        if (Known(p.DensityKgM3)) rows.Add(("Density", $"{p.DensityKgM3 / 1000f:0.###} g/cm³"));
        if (p.Count > 1) rows.Add(("In this model", $"{p.Count} of these"));
        if (p.Triangles > 0) rows.Add(("Triangles", $"{p.Triangles:N0} (as shown)"));
        if (p.MotionText() is string moves) rows.Add(("Mates", (p.MateCount > 0 ? $"{p.MateCount}: " : "") + moves));
        if (!string.IsNullOrEmpty(p.MotionNote)) rows.Add(("", p.MotionNote));
        if (p.CanMove && p.PositionText() is string pos) rows.Add(("Position", pos));

        var l = new System.Text.StringBuilder();
        var v = new System.Text.StringBuilder();
        foreach (var (label, value) in rows)
        {
            if (l.Length > 0) { l.Append('\n'); v.Append('\n'); }
            l.Append(label);
            v.Append(Trim(value, 34));
        }

        title.text = Wrap(Leaf(p.PartName ?? "Part"), 28);
        int titleLines = title.text.Split('\n').Length;
        sub.text = string.IsNullOrEmpty(p.Path) ? "" : Trim("in " + p.Path, 52);
        labels.text = l.ToString();
        values.text = v.ToString();
        footer.text = p.Pinned ? "ISOLATED · LOCKED  (LOCK PART to release)"
                    : p.Isolated ? "ISOLATED  (stays where you put it)"
                    : ViewModes.Move && p.Held ? "HELD STILL  (FIX PART to free it)" : "";

        // Lay out from the top down; the panel sits on the keypad's top edge.
        float y = 0f;
        float top = y - Margin;
        float cursor = top;
        Place(title, cursor); cursor -= TitleLine * (titleLines + 0.25f);
        if (sub.text.Length > 0) { Place(sub, cursor); cursor -= Line * 1.1f; }
        cursor -= Line * 0.35f;
        Place(labels, cursor);
        values.transform.localPosition = new Vector3(XLeft - LabelColumn, cursor, TextZ);
        cursor -= Line * rows.Count;
        if (footer.text.Length > 0) cursor -= Line * 1.9f;
        height = -cursor + Margin;

        // Everything was laid out with the top at 0; move it so the bottom sits just above the keypad.
        float lift = height; // relative to root, which sits on the keypad top edge
        foreach (var t in new[] { title, sub, labels, values })
        {
            var lp = t.transform.localPosition;
            t.transform.localPosition = new Vector3(lp.x, lp.y + lift, lp.z);
        }
        footer.transform.localPosition = new Vector3(XLeft, Margin * 0.8f, TextZ);
        pane.localPosition = new Vector3(0f, height * 0.5f, 0f);
        pane.localScale = new Vector3(Width, height, 1f);
        root.localPosition = new Vector3(0f, keypadTop + Gap, 0f);
        paneMat.SetFloat("_Aspect", Width / height);
    }

    const float LabelColumn = 0.066f;
    const float TextZ = 0.002f; // the user is on the +Z side

    // Text faces the user (rotated 180° about Y), so the user's left is local +X.
    static float XLeft => Width * 0.5f - Margin;

    void Place(TextMesh t, float y) => t.transform.localPosition = new Vector3(XLeft, y, TextZ);

    static bool Known(float v) => !float.IsNaN(v) && v > 0f;

    static string Mass(float kg) =>
        (kg >= 1f ? $"{kg:0.###} kg" : $"{kg * 1000f:0.#} g") + $"   ({kg * 2.20462f:0.###} lb)";

    /// "Drive Train-1/CNC Plate Right-1" -> "CNC Plate Right-1" (the sub-assembly is on the line below).
    static string Leaf(string name)
    {
        int slash = name.LastIndexOf('/');
        return slash >= 0 && slash < name.Length - 1 ? name.Substring(slash + 1).Trim() : name;
    }

    /// Breaks a long name into lines of at most 'width' characters, at spaces where possible.
    static string Wrap(string s, int width)
    {
        var sb = new System.Text.StringBuilder();
        int lineStart = 0;
        while (s.Length - lineStart > width)
        {
            int cut = s.LastIndexOf(' ', lineStart + width, width);
            if (cut <= lineStart) cut = lineStart + width; // no space: hard break
            sb.Append(s, lineStart, cut - lineStart).Append('\n');
            lineStart = cut;
            while (lineStart < s.Length && s[lineStart] == ' ') lineStart++;
        }
        return sb.Append(s, lineStart, s.Length - lineStart).ToString();
    }

    static string Trim(string s, int width) => s.Length <= width ? s : s.Substring(0, width - 1) + "…";
}
