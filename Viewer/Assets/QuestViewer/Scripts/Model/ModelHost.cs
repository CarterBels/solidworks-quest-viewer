using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using GLTFast;
using UnityEngine;

/// The grabbable "Model" object. Loads a GLB at runtime and hot-swaps its content: the Model object itself is kept,
/// so the new version appears where you left it (same pose and scale); the very first model appears in front of you.
/// The previous version fades out as a ghost so changes are easy to spot.
[RequireComponent(typeof(Grabbable), typeof(BoxCollider), typeof(SpawnInFront))]
public class ModelHost : MonoBehaviour
{
    [Tooltip("Hidden once a model is loaded (the 100 mm test cube).")]
    public GameObject placeholder;
    [Tooltip("How far parts move apart in the exploded view, relative to their distance from the centre.")]
    public float explodeAmount = 0.9f;

    public string CurrentName { get; private set; }
    /// True size in mm, in Onshape's axes (X, Y, Z = width, depth, height).
    public Vector3 SizeMm { get; private set; }
    public bool HasModel => content != null;
    public IReadOnlyList<ModelPart> Parts => parts;
    public Transform Content => content;

    /// Raised on the main thread after a new model is in the scene.
    public static event Action<ModelHost> ModelLoaded;

    // Convex colliders are cooked from at most this many points per part: cooking time grows with the square of the
    // point count (a 200k-vertex part froze the app for 39 s on a Quest 3S), and grabbing doesn't need the detail.
    const int HullPoints = 600;
    // Beyond this many parts, each part gets its bounding box instead (instant, and still grabbable part by part).
    const int MaxConvexParts = 600;

    Grabbable grabbable;
    LineRenderer outline;
    bool hasPartColliders;
    readonly List<Mesh> hulls = new List<Mesh>(); // point clouds made for the current model's colliders
    BoxCollider box;
    SpawnInFront spawn;
    ModelLook look;
    Transform content;
    GltfImport current;
    readonly List<ModelPart> parts = new List<ModelPart>();
    bool busy;
    bool everLoaded;

    void Awake()
    {
        grabbable = GetComponent<Grabbable>();
        box = GetComponent<BoxCollider>();
        spawn = GetComponent<SpawnInFront>();
        look = GetComponent<ModelLook>();
        if (placeholder != null && placeholder.GetComponent<CreditsPanel>() == null) placeholder.AddComponent<CreditsPanel>();
        if (GetComponent<HeldMarks>() == null) gameObject.AddComponent<HeldMarks>();
        box.enabled = false; // nothing to grab until a model is loaded
    }

    void OnEnable() => ViewModes.Changed += ApplyModes;
    void OnDisable() => ViewModes.Changed -= ApplyModes;

    void AddPartColliders()
    {
        foreach (var h in hulls) if (h != null) Destroy(h); // the previous model's (its colliders are off by now)
        hulls.Clear();
        bool convex = parts.Count <= MaxConvexParts;
        int made = 0;
        foreach (var p in parts)
        {
            if (!(p.GetComponent<MeshFilter>() is { } f) || f.sharedMesh == null) continue;
            var mesh = f.sharedMesh;
            var hull = convex && mesh.isReadable ? HullSource(mesh) : null;
            if (hull != null)
            {
                var mc = p.gameObject.AddComponent<MeshCollider>();
                if (hull != mesh) hulls.Add(hull);
                mc.sharedMesh = hull;
                mc.convex = true;
            }
            else
            {
                var bc = p.gameObject.AddComponent<BoxCollider>();
                var b = mesh.bounds; // in the part's own space, like the collider
                bc.center = b.center;
                bc.size = Vector3.Max(b.size, Vector3.one * 0.002f);
            }
            made++;
        }
        hasPartColliders = made > 0;
    }

    /// A point cloud for convex cooking: the mesh's extreme points in 26 directions plus an even sample, at most
    /// HullPoints. Small meshes are used as they are.
    static Mesh HullSource(Mesh mesh)
    {
        var v = mesh.vertices;
        if (v.Length < 4) return null;
        if (v.Length <= HullPoints) return mesh;
        var pts = new List<Vector3>(HullPoints + 26);
        for (int x = -1; x <= 1; x++)
            for (int y = -1; y <= 1; y++)
                for (int z = -1; z <= 1; z++)
                {
                    if (x == 0 && y == 0 && z == 0) continue;
                    var d = new Vector3(x, y, z);
                    int best = 0; float bestDot = float.MinValue;
                    for (int i = 0; i < v.Length; i++)
                    {
                        float dot = Vector3.Dot(v[i], d);
                        if (dot > bestDot) { bestDot = dot; best = i; }
                    }
                    pts.Add(v[best]);
                }
        int step = Mathf.Max(1, v.Length / HullPoints);
        for (int i = 0; i < v.Length && pts.Count < HullPoints + 26; i += step) pts.Add(v[i]);
        var tris = new int[(pts.Count - 2) * 3]; // a fan over all points; cooking only uses the points
        for (int i = 0; i < pts.Count - 2; i++) { tris[i * 3] = 0; tris[i * 3 + 1] = i + 1; tris[i * 3 + 2] = i + 2; }
        var hull = new Mesh { name = mesh.name + " hull" };
        hull.SetVertices(pts);
        hull.SetTriangles(tris, 0, false);
        return hull;
    }

    /// Grab box (keypad BOX): the model's bounding box becomes grabbable everywhere and is outlined, each part's box
    /// is outlined too, and fingers push the box like a solid block. LOCK: the model can't be grabbed, pushed or thrown.
    /// Without part colliders (nothing loaded yet) the box is always used.
    void ApplyModes()
    {
        if (box == null) return;
        bool on = ViewModes.Box && HasModel;
        box.enabled = HasModel && (on || !hasPartColliders);
        if (grabbable != null)
        {
            grabbable.SetLocked(ViewModes.Lock);
        }
        if (on && outline == null) outline = MakeOutline();
        if (outline != null)
        {
            outline.gameObject.SetActive(on);
            if (on) SetOutline(box.center, box.size);
        }
        if (on && partBoxesFor != content) MakePartBoxes();
        foreach (var b in partBoxes) if (b != null) b.SetActive(on);
    }

    // ---- Part boxes: a thin outline of each part's bounding box, as a child of the part (so it follows the part in
    // the exploded view). One shared line mesh and material; made the first time BOX is on for a model. ----

    static Mesh unitBoxLines;
    static Material partBoxMaterial;
    readonly List<GameObject> partBoxes = new List<GameObject>();
    Transform partBoxesFor;

    void MakePartBoxes()
    {
        partBoxes.Clear(); // the previous model's went with it
        partBoxesFor = content;
        if (unitBoxLines == null)
        {
            unitBoxLines = new Mesh { name = "Unit box lines" };
            var v = new Vector3[8];
            for (int i = 0; i < 8; i++) v[i] = new Vector3((i & 1) - 0.5f, ((i >> 1) & 1) - 0.5f, ((i >> 2) & 1) - 0.5f);
            unitBoxLines.vertices = v;
            unitBoxLines.SetIndices(new[] { 0,1, 2,3, 4,5, 6,7, 0,2, 1,3, 4,6, 5,7, 0,4, 1,5, 2,6, 3,7 }, MeshTopology.Lines, 0);
            var c = Keypad.Accent * 0.7f;
            partBoxMaterial = new Material(Shader.Find("Universal Render Pipeline/Unlit")) { color = new Color(c.r, c.g, c.b, 1f) };
        }
        foreach (var p in parts)
        {
            if (!(p.GetComponent<MeshFilter>() is { } f) || f.sharedMesh == null) continue;
            var bounds = f.sharedMesh.bounds; // in the part's own space
            var go = new GameObject("PartBox");
            go.AddComponent<PartBoxMarker>();
            go.transform.SetParent(p.transform, false);
            go.transform.localPosition = bounds.center;
            go.transform.localScale = Vector3.Max(bounds.size, Vector3.one * 0.001f);
            go.AddComponent<MeshFilter>().sharedMesh = unitBoxLines;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = partBoxMaterial;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            partBoxes.Add(go);
        }
    }

    LineRenderer MakeOutline()
    {
        // Not a child of the model: the look, cut and table code treat every renderer under it as part of the model.
        var go = new GameObject("GrabBox");
        var lr = go.AddComponent<LineRenderer>();
        lr.useWorldSpace = false;
        lr.loop = false;
        lr.widthMultiplier = 0.0015f;
        lr.numCapVertices = 2;
        lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        lr.receiveShadows = false;
        var mat = new Material(Shader.Find("Universal Render Pipeline/Unlit"));
        var c = Keypad.Accent;
        mat.color = new Color(c.r, c.g, c.b, 1f);
        lr.sharedMaterial = mat;
        return lr;
    }

    /// The 12 edges of the box as one line (some edges are drawn twice).
    void SetOutline(Vector3 c, Vector3 s)
    {
        var h = s * 0.5f;
        Vector3 P(int x, int y, int z) => c + new Vector3(x * h.x, y * h.y, z * h.z);
        var path = new[]
        {
            P(-1,-1,-1), P(1,-1,-1), P(1,-1,1), P(-1,-1,1), P(-1,-1,-1),
            P(-1,1,-1), P(1,1,-1), P(1,1,1), P(-1,1,1), P(-1,1,-1),
            P(-1,1,1), P(-1,-1,1), P(1,-1,1), P(1,1,1), P(1,1,-1), P(1,-1,-1),
        };
        outline.positionCount = path.Length;
        outline.SetPositions(path);
    }

    /// Main thread only. partInfoJson: {"partId": {"m": kg, "v": m3}, ...} from Onshape (optional).
    /// Returns a one-line summary; throws on failure (the old model stays).
    public async Task<string> LoadGlbAsync(byte[] glb, string displayName, string partInfoJson = null)
    {
        if (busy) throw new InvalidOperationException("Already loading a model.");
        busy = true;
        try
        {
            var import = new GltfImport();
            if (!await import.Load(glb))
            {
                import.Dispose();
                throw new Exception("Not a valid glTF.");
            }

            // Root (keeps pose and scale) -> Content (recentred) -> glTF scene.
            var newContent = new GameObject("Content").transform;
            newContent.SetParent(transform, false);
            if (!await import.InstantiateMainSceneAsync(newContent))
            {
                Destroy(newContent.gameObject);
                import.Dispose();
                throw new Exception("The model could not be created.");
            }

            // Old version becomes a fading ghost; then swap in the new one.
            var old = content;
            var oldImport = current;
            content = newContent;
            current = import;
            if (old != null)
            {
                if (look != null) look.Ghost(old, () => oldImport?.Dispose());
                else { Destroy(old.gameObject); oldImport?.Dispose(); }
            }

            // Recentre on the bounding-box centre.
            var bounds = LocalBounds(content);
            content.localPosition -= bounds.center;
            box.center = Vector3.zero;
            box.size = Vector3.Max(bounds.size, Vector3.one * 0.01f);

            Mechanism.Clear(); // a new model: its own modes, all at t = 0
            ReadPaths(glb);
            BuildParts(glb, partInfoJson);

            // Per-part colliders so fingers push the real shape and parts can be grabbed one by one.
            AddPartColliders();
            ApplyModes();
            var rb = GetComponent<Rigidbody>();
            if (rb != null) rb.ResetCenterOfMass();

            if (look != null)
            {
                look.Capture();
                look.ScanIn();
            }

            if (placeholder != null && placeholder.activeSelf)
            {
                var g = placeholder.GetComponent<Grabbable>();
                if (g != null) g.ReleaseAll();
                placeholder.SetActive(false);
            }
            if (!everLoaded) spawn.Respawn();
            everLoaded = true;

            CurrentName = displayName;
            SizeMm = new Vector3(bounds.size.x, bounds.size.z, bounds.size.y) * 1000f;
            var summary = $"{displayName}: {SizeMm.x:0.#} x {SizeMm.y:0.#} x {SizeMm.z:0.#} mm";
            Status.Ok("Loaded\n" + displayName, 2f);
            ModelLoaded?.Invoke(this);
            return summary;
        }
        finally
        {
            busy = false;
        }
    }

    /// Creates a ModelPart per mesh object, with Onshape names/IDs from the GLB and mass/volume from partInfo.
    void BuildParts(byte[] glb, string partInfoJson)
    {
        parts.Clear();
        var nodeIds = ReadNodePartIds(glb);         // node name -> part IDs in node order
        var info = partInfoJson != null ? SafeParse(partInfoJson) : null;
        var used = new Dictionary<string, int>();
        var centre = Vector3.zero;                    // model centre in Model-local space after recentring

        foreach (var r in content.GetComponentsInChildren<MeshRenderer>(true))
        {
            var part = r.gameObject.AddComponent<ModelPart>();
            part.Renderer = r;
            part.PartName = r.gameObject.name;
            used.TryGetValue(part.PartName, out int n);
            used[part.PartName] = n + 1;
            object sw = null;
            if (nodeIds.TryGetValue(part.PartName, out var ids) && n < ids.Count) (part.PartId, sw) = ids[n];

            if (part.PartId != null && MiniJson.Get(info, part.PartId) is { } pi)
            {
                part.MassKg = (float)MiniJson.Num(MiniJson.Get(pi, "m"));
                part.VolumeM3 = (float)MiniJson.Num(MiniJson.Get(pi, "v"));
            }
            if (sw != null) ReadSpecs(part, sw);
            var mf = r.GetComponent<MeshFilter>();
            if (mf != null && mf.sharedMesh != null)
            {
                long idx = 0;
                for (int s = 0; s < mf.sharedMesh.subMeshCount; s++) idx += mf.sharedMesh.GetIndexCount(s);
                part.Triangles = (int)(idx / 3);
            }

            var b = RendererBoundsIn(r, transform);
            part.SizeMm = new Vector3(b.size.x, b.size.z, b.size.y) * 1000f;
            part.Home = r.transform.localPosition;
            part.HomeRotation = r.transform.localRotation;
            // Push outwards from the model centre (in the part's parent space).
            var dirModel = (b.center - centre) * explodeAmount;
            var parent = r.transform.parent;
            part.ExplodeOffset = parent.InverseTransformVector(transform.TransformVector(dirModel));
            parts.Add(part);
        }
    }

    /// Specs the SolidWorks add-in writes per node: {"file","config","material","body","path","mass","density",
    /// "volume","area","count","members"}; all optional. Mass and volume here win over X-Part-Info (which big models
    /// can't fit in a header).
    static void ReadSpecs(ModelPart part, object sw)
    {
        string S(string k) => MiniJson.Get(sw, k) as string;
        float N(string k) => (float)MiniJson.Num(MiniJson.Get(sw, k));
        part.File = S("file");
        part.Config = S("config");
        part.Material = S("material");
        part.Body = S("body");
        part.Path = S("path");
        if (!float.IsNaN(N("mass"))) part.MassKg = N("mass");
        if (!float.IsNaN(N("volume"))) part.VolumeM3 = N("volume");
        part.DensityKgM3 = N("density");
        part.AreaM2 = N("area");
        part.Count = float.IsNaN(N("count")) ? 0 : (int)N("count");
        part.Members = float.IsNaN(N("members")) ? 0 : (int)N("members");

        // Motion by mates: glTF axes -> Unity (glTFast mirrors X).
        part.MotionKind = S("motion");
        part.MotionNote = S("motionNote");
        part.Freedoms = float.IsNaN(N("dof")) ? -1 : (int)N("dof");
        part.MateCount = float.IsNaN(N("mates")) ? 0 : (int)N("mates");
        part.MotionGroup = float.IsNaN(N("group")) ? 0 : (int)N("group");
        part.SolidWorksFixed = MiniJson.Get(sw, "fixed") is bool f && f;
        part.Held = part.SolidWorksFixed;

        // Mechanism modes it moves in: [id, rad/t, m/t, point xyz, direction xyz, tMin, tMax] (glTF axes).
        part.Modes.Clear();
        if (MiniJson.AsList(MiniJson.Get(sw, "modes")) is { } modes)
            foreach (var o in modes)
            {
                var a = MiniJson.AsList(o);
                if (a == null || a.Count < 11) continue;
                float F(int i) => (float)MiniJson.Num(a[i], 0);
                var m = new ModelPart.ModeMotion
                {
                    Id = (int)F(0),
                    AngleRate = F(1),
                    SlideRate = F(2),
                    Point = new Vector3(-F(3), F(4), F(5)),                // glTFast mirrors X
                    Direction = new Vector3(-F(6), F(7), F(8)).normalized,
                };
                part.Modes.Add(m);
                Mechanism.SetLimits(m.Id, F(9), F(10));
            }
        var drive = new List<int>();
        if (MiniJson.AsList(MiniJson.Get(sw, "drive")) is { } d)
            foreach (var o in d) if (part.TryMode((int)MiniJson.Num(o, -1), out var dm)) drive.Add(dm.Id);
        part.Drive = drive.ToArray();
    }

    /// SolidWorks fork: linkage paths traced by the add-in (scene extras "sw.paths"), into Mechanism. glTF -> Unity
    /// mirrors X: a point (x, y, z) becomes (−x, y, z), a rotation (x, y, z, w) becomes (x, −y, −z, w).
    static void ReadPaths(byte[] glb)
    {
        try
        {
            int len = BitConverter.ToInt32(glb, 12);
            var root = MiniJson.Parse(Encoding.UTF8.GetString(glb, 20, len));
            var scene = MiniJson.AsList(MiniJson.Get(root, "scenes"))?[0];
            var list = MiniJson.AsList(MiniJson.Get(MiniJson.Get(MiniJson.Get(scene, "extras"), "sw"), "paths"));
            if (list == null) return;
            foreach (var o in list)
            {
                var ts = MiniJson.AsList(MiniJson.Get(o, "t"));
                if (ts == null || ts.Count == 0) continue;
                var p = new Mechanism.Path
                {
                    T = ts.ConvertAll(x => (float)MiniJson.Num(x, 0)).ToArray(),
                    Period = (float)MiniJson.Num(MiniJson.Get(o, "period"), 0),
                };
                if (MiniJson.Get(o, "limit") is { } lim && MiniJson.AsList(MiniJson.Get(lim, "v")) is { } lv && lv.Count == ts.Count)
                {
                    p.LimitName = MiniJson.Get(lim, "name") as string ?? "Limit";
                    p.LimitIsAngle = MiniJson.Get(lim, "angle") is bool b && b;
                    p.LimitMin = (float)MiniJson.Num(MiniJson.Get(lim, "min"), 0);
                    p.LimitMax = (float)MiniJson.Num(MiniJson.Get(lim, "max"), 0);
                    p.Limit = lv.ConvertAll(x => (float)MiniJson.Num(x, 0)).ToArray();
                }
                if (MiniJson.Get(o, "groups") is Dictionary<string, object> groups)
                    foreach (var kv in groups)
                    {
                        if (!int.TryParse(kv.Key, out int g) || !(MiniJson.AsList(kv.Value) is { } samples) || samples.Count != ts.Count) continue;
                        var arr = new (Quaternion, Vector3)[samples.Count];
                        for (int i = 0; i < samples.Count; i++)
                        {
                            var a = MiniJson.AsList(samples[i]);
                            float F(int j) => a != null && a.Count > j ? (float)MiniJson.Num(a[j], 0) : 0f;
                            arr[i] = (new Quaternion(F(0), -F(1), -F(2), F(3)), new Vector3(-F(4), F(5), F(6)));
                        }
                        p.Groups[g] = arr;
                    }
                Mechanism.SetPath((int)MiniJson.Num(MiniJson.Get(o, "id"), 0), p);
            }
        }
        catch (Exception e) { Debug.LogWarning("[ModelHost] Could not read motion paths: " + e.Message); }
    }

    /// glTF node name -> PTC_onshape_metadata part IDs (and SolidWorks specs), in node order (names can repeat).
    static Dictionary<string, List<(string id, object sw)>> ReadNodePartIds(byte[] glb)
    {
        var map = new Dictionary<string, List<(string id, object sw)>>();
        try
        {
            int len = BitConverter.ToInt32(glb, 12);
            var root = MiniJson.Parse(Encoding.UTF8.GetString(glb, 20, len));
            if (MiniJson.AsList(MiniJson.Get(root, "nodes")) is { } nodes)
                foreach (var node in nodes)
                {
                    if (!(MiniJson.Get(node, "name") is string name)) continue;
                    var ids = MiniJson.AsList(MiniJson.Get(MiniJson.Get(MiniJson.Get(node, "extensions"), "PTC_onshape_metadata"), "id"));
                    if (ids == null || ids.Count == 0 || !(ids[0] is string id)) continue;
                    if (!map.TryGetValue(name, out var list)) map[name] = list = new List<(string, object)>();
                    list.Add((id, MiniJson.Get(MiniJson.Get(node, "extras"), "sw"))); // specs from the SolidWorks add-in
                }
        }
        catch (Exception e) { Debug.LogWarning("[ModelHost] Could not read part IDs: " + e.Message); }
        return map;
    }

    static object SafeParse(string json)
    {
        try { return MiniJson.Parse(json); }
        catch (Exception) { return null; }
    }

    Bounds LocalBounds(Transform root)
    {
        bool any = false;
        var b = new Bounds();
        foreach (var r in root.GetComponentsInChildren<Renderer>())
        {
            var rb = RendererBoundsIn(r, transform);
            if (!any) { b = rb; any = true; }
            else b.Encapsulate(rb);
        }
        return b;
    }

    /// Bounds of one renderer in 'space' (local, unscaled).
    static Bounds RendererBoundsIn(Renderer r, Transform space)
    {
        var lb = r.localBounds;
        var m = space.worldToLocalMatrix * r.transform.localToWorldMatrix;
        var b = new Bounds(m.MultiplyPoint3x4(lb.center), Vector3.zero);
        for (int i = 0; i < 8; i++)
        {
            var corner = lb.center + Vector3.Scale(lb.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
            b.Encapsulate(m.MultiplyPoint3x4(corner));
        }
        return b;
    }

    void LateUpdate()
    {
        if (outline == null || !outline.gameObject.activeSelf) return;
        var t = outline.transform;
        t.SetPositionAndRotation(transform.position, transform.rotation);
        t.localScale = transform.lossyScale;
    }

    void OnDestroy()
    {
        current?.Dispose();
        if (outline != null) Destroy(outline.gameObject);
    }
}

/// Marks the part box outlines (BOX mode), which are not part of the model's look.
public class PartBoxMarker : MonoBehaviour { }
