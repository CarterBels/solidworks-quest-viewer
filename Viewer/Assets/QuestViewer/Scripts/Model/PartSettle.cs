using System.Collections;
using UnityEngine;

/// SolidWorks fork: an isolated part let go just above a real table, desk or the floor settles down onto it,
/// like the whole model does (TableSnap). Uses the room's horizontal planes (PlaneTracker).
public class PartSettle : MonoBehaviour
{
    public const float MaxDrop = 0.15f;
    const float Seconds = 0.35f;

    ModelPart part;

    /// Call when 'part' (isolated) is let go.
    public static void Drop(ModelPart part)
    {
        if (part == null || part.Renderer == null) return;
        var b = part.Renderer.bounds;
        var bottom = new Vector3(b.center.x, b.min.y, b.center.z);
        if (!PlaneTracker.TryFindSurfaceBelow(bottom, MaxDrop, out float surfaceY)) return;
        var s = part.GetComponent<PartSettle>();
        if (s == null) s = part.gameObject.AddComponent<PartSettle>();
        s.part = part;
        s.StopAllCoroutines();
        s.StartCoroutine(s.Settle(surfaceY - bottom.y + 0.001f));
    }

    IEnumerator Settle(float dy)
    {
        var start = transform.position;
        var end = start + Vector3.up * dy;
        for (float t = 0; t < Seconds; t += Time.deltaTime)
        {
            if (part.Dragger != null || !part.Isolated) yield break; // picked up again, or sent home
            float k = 1f - Mathf.Pow(1f - t / Seconds, 3f);
            transform.position = Vector3.Lerp(start, end, k);
            part.Place();
            yield return null;
        }
        if (part.Dragger != null || !part.Isolated) yield break;
        transform.position = end;
        part.Place();
        Status.Info("On the table\n" + part.PartName, 1.5f);
    }
}
