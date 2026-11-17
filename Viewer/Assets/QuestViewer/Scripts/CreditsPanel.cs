using UnityEngine;

/// Credit to the project this viewer is built on (SolidWorks fork): a small holo-panel under the starting cube,
/// shown while no model is loaded (it goes away with the cube). Follows the cube and faces you.
public class CreditsPanel : MonoBehaviour
{
    public const string Credit = "Based on Onshape Quest Viewer by almostinventor\ngithub.com/almostinventor/onshape-quest-viewer · MIT License";

    const float Width = 0.2f, Height = 0.036f;

    Transform root;
    Material mat;

    void Awake()
    {
        root = new GameObject("CreditsPanel").transform;
        var quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
        Destroy(quad.GetComponent<Collider>());
        quad.name = "Pane";
        quad.transform.SetParent(root, false);
        quad.transform.localRotation = Quaternion.Euler(0f, 180f, 0f);
        quad.transform.localScale = new Vector3(Width, Height, 1f);
        var shader = Resources.Load<Shader>("HoloPanel");
        mat = shader != null ? new Material(shader) : Keypad.NewUnlit(new Color(0.02f, 0.08f, 0.1f));
        if (shader != null) mat.SetFloat("_Aspect", Width / Height);
        quad.GetComponent<Renderer>().sharedMaterial = mat;
        var t = KeypadButton.MakeText(root, Credit, new Vector3(0f, 0f, 0.002f), 0.0008f, Color.white);
        t.richText = true;
        t.text = "Based on <b>Onshape Quest Viewer</b> by almostinventor\n<color=#9fe8ffcc>github.com/almostinventor/onshape-quest-viewer · MIT License</color>";
    }

    void OnEnable() { if (root != null) root.gameObject.SetActive(true); }
    void OnDisable() { if (root != null) root.gameObject.SetActive(false); }
    void OnDestroy() { if (root != null) Destroy(root.gameObject); if (mat != null) Destroy(mat); }

    void LateUpdate()
    {
        var cam = Camera.main;
        if (cam == null) return;
        var r = GetComponent<Renderer>();
        float below = r != null ? r.bounds.extents.y + 0.04f : 0.09f;
        root.position = transform.position + Vector3.down * below;
        var toHead = Vector3.ProjectOnPlane(cam.transform.position - root.position, Vector3.up);
        if (toHead.sqrMagnitude > 1e-4f) root.rotation = Quaternion.LookRotation(toHead.normalized, Vector3.up); // +Z towards you
    }
}
