using System.Collections;
using System.IO;
using System.Linq;
using MaoIntegration;
using UnityEngine;

public sealed class MaoAppearanceSmoke : MonoBehaviour
{
    private IEnumerator Start()
    {
        yield return new WaitForSecondsRealtime(4f);
        var controller = GetComponent<MaoAppearanceController>();
        if (controller == null || !controller.Supported) { Debug.LogError("[Mao Appearance Smoke] unsupported"); yield break; }
        var saved = controller.Profile.Copy();
        var body = GetComponentsInChildren<SkinnedMeshRenderer>(true).First(r => r.sharedMesh != null &&
            r.sharedMaterials.Any(m => m != null && m.name.StartsWith("Costume")));
        controller.ResetAppearance(false);
        Mesh original = body.sharedMesh;
        string[] shapes = Enumerable.Range(0, original.blendShapeCount).Select(original.GetBlendShapeName).ToArray();
        int failures = 0;
        Capture("original", body);
        var parts = new[] { MaoAppearancePart.Hair, MaoAppearancePart.Hair, MaoAppearancePart.Outfit,
            MaoAppearancePart.Outfit, MaoAppearancePart.Face, MaoAppearancePart.EyeColor, MaoAppearancePart.HairColor };
        var ids = new[] { 1, 2, 2, 3, 1, 1, 1 };
        var labels = new[] { "short-hair", "side-hair", "pastel", "jacket", "soft-face", "purple-eyes", "pink-hair" };
        for (int i = 0; i < parts.Length; i++)
        {
            controller.ResetAppearance(false);
            if (!controller.Select(parts[i], ids[i], false)) failures++;
            yield return new WaitForSecondsRealtime(0.6f);
            Mesh current = body.sharedMesh;
            bool valid = current.blendShapeCount == shapes.Length && Enumerable.Range(0, shapes.Length)
                .All(s => current.GetBlendShapeName(s) == shapes[s]) && current.vertices.All(Finite);
            if (!valid) failures++;
            Capture(labels[i], body);
            Debug.Log("[Mao Appearance Smoke] preset=" + labels[i] + " valid=" + valid + " resources=" + controller.OwnedResources);
        }
        controller.ResetAppearance(false);
        if (body.sharedMesh != original || controller.OwnedResources != 0) failures++;
        foreach (MaoAppearancePart part in System.Enum.GetValues(typeof(MaoAppearancePart)))
            if (saved.Get(part) != 0) controller.Select(part, saved.Get(part), false);
        Debug.Log("[Mao Appearance Smoke] failures=" + failures + " reset_original=" + (body.sharedMesh == original));
    }

    private static bool Finite(Vector3 v) => !float.IsNaN(v.x) && !float.IsInfinity(v.x) &&
        !float.IsNaN(v.y) && !float.IsInfinity(v.y) && !float.IsNaN(v.z) && !float.IsInfinity(v.z);

    private static void Capture(string label, SkinnedMeshRenderer body)
    {
        string folder = Path.Combine(Application.persistentDataPath, "MaoAppearancePreview");
        Directory.CreateDirectory(folder);
        var cameraObject = new GameObject("Appearance verification camera");
        var camera = cameraObject.AddComponent<Camera>();
        var target = new RenderTexture(768, 768, 24);
        var previous = RenderTexture.active;
        Texture2D image = null;
        try
        {
            Bounds bounds = body.bounds;
            camera.orthographic = true; camera.orthographicSize = Mathf.Max(bounds.extents.y * 1.12f, 0.65f);
            camera.transform.position = bounds.center + Vector3.back * 3f;
            camera.transform.LookAt(bounds.center);
            camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = new Color(0.11f, 0.14f, 0.18f);
            camera.targetTexture = target; camera.Render(); RenderTexture.active = target;
            image = new Texture2D(768, 768, TextureFormat.RGB24, false);
            image.ReadPixels(new Rect(0, 0, 768, 768), 0, 0); image.Apply();
            File.WriteAllBytes(Path.Combine(folder, label + ".png"), image.EncodeToPNG());
        }
        finally
        {
            RenderTexture.active = previous; camera.targetTexture = null;
            if (image != null) Object.Destroy(image);
            target.Release(); Object.Destroy(target); Object.Destroy(cameraObject);
        }
    }
}
