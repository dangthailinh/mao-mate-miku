#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using MaoIntegration;
using Newtonsoft.Json;
using UniGLTF;
using UnityEditor;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;
using VRM;
using Object = UnityEngine.Object;

// Offline read-only avatar rendering. Never added to a player or its live scene.
public static partial class MaoCodexPetExporter
{
    private static GameObject model;
    private static Animator animator;
    private static Camera camera;
    private static RenderTexture target;
    private static PlayableGraph graph;
    private static AnimationClipPlayable playable;
    private static readonly List<Object> owned = new List<Object>();
    private static string output;

    public static async void Run()
    {
        try
        {
            string project = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            output = Argument("--mao-pet-output=") ?? Path.Combine(project, "Logs", "CodexPet-20261029");
            output = Path.GetFullPath(output);
            Directory.CreateDirectory(output);
            string source = Path.Combine(project, "AvatarAssets", "hatsune-miku.vrm");
            var instance = await VrmUtility.LoadAsync(source, new ImmediateCaller());
            model = instance.gameObject;
            instance.ShowMeshes();
            foreach (var component in model.GetComponentsInChildren<MonoBehaviour>(true)) component.enabled = false;
            foreach (var transform in model.GetComponentsInChildren<Transform>(true)) transform.gameObject.layer = 31;
            animator = model.GetComponent<Animator>() ?? model.GetComponentInChildren<Animator>(true);
            if (animator == null || !animator.isHuman || animator.avatar == null) throw new InvalidOperationException("Humanoid avatar required.");
            animator.applyRootMotion = false;
            animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            ApplySavedAppearance();
            AnimationClip idle = Clip("Assets/MaoIntegration/Motions/FemaleLocomotion/idle.fbx");
            graph = PlayableGraph.Create("Miku offline pet export");
            graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
            playable = AnimationClipPlayable.Create(graph, idle);
            playable.SetApplyFootIK(false);
            AnimationPlayableOutput.Create(graph, "Miku", animator).SetSourcePlayable(playable);
            graph.Play();
            Sample(0.2);
            Transform head = animator.GetBoneTransform(HumanBodyBones.Head);
            Bounds visualBounds = PosedBounds();
            float feet = visualBounds.min.y;
            float height = visualBounds.size.y;
            camera = new GameObject("Offline pet camera").AddComponent<Camera>(); owned.Add(camera.gameObject);
            camera.orthographic = true; camera.orthographicSize = height * 0.66f;
            camera.transform.position = new Vector3(0, feet + height * 0.61f, -4);
            camera.transform.rotation = Quaternion.identity;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = Color.clear;
            camera.cullingMask = 1 << 31;
            camera.allowHDR = false; camera.allowMSAA = true;
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.8f, 0.8f, 0.8f);
            var light = new GameObject("Offline pet light").AddComponent<Light>(); owned.Add(light.gameObject);
            light.type = LightType.Directional; light.intensity = 1.1f; light.color = Color.white;
            light.transform.rotation = Quaternion.Euler(35, -25, 0); light.cullingMask = 1 << 31;
            target = new RenderTexture(384, 416, 24, RenderTextureFormat.ARGB32) { antiAliasing = 4 };
            target.Create(); camera.targetTexture = target;
            foreach (var renderer in model.GetComponentsInChildren<SkinnedMeshRenderer>(true)) renderer.updateWhenOffscreen = true;
            Capture(Path.Combine(output, "preview.png"));
            File.WriteAllText(Path.Combine(output, "source-report.json"), JsonConvert.SerializeObject(new {
                source, height, feet, humanoid = animator.isHuman,
                head = head.position.ToString(), idle = idle.name,
                bones = Enum.GetValues(typeof(HumanBodyBones)).Cast<HumanBodyBones>().Where(b => b != HumanBodyBones.LastBone && animator.GetBoneTransform(b) != null).Select(b => b.ToString()).ToArray(),
                muscles = HumanTrait.MuscleName,
                shapes = model.GetComponentsInChildren<SkinnedMeshRenderer>(true).SelectMany(r => Enumerable.Range(0, r.sharedMesh.blendShapeCount).Select(r.sharedMesh.GetBlendShapeName)).Distinct().ToArray()
            }, Formatting.Indented));
            Debug.Log("[Miku Codex Pet] preview_rendered=" + Path.Combine(output, "preview.png"));
            if (!Environment.GetCommandLineArgs().Contains("--mao-pet-preview")) ExportAnimations(idle);
            Cleanup(); EditorApplication.Exit(0);
        }
        catch (Exception exception) { Debug.LogException(exception); Cleanup(); EditorApplication.Exit(1); }
    }

    private static string Argument(string prefix) => Environment.GetCommandLineArgs().FirstOrDefault(s => s.StartsWith(prefix, StringComparison.Ordinal))?.Substring(prefix.Length);
    private static AnimationClip Clip(string path) => AssetDatabase.LoadAllAssetsAtPath(path).OfType<AnimationClip>().First(c => !c.name.StartsWith("__preview__", StringComparison.Ordinal));
    private static void Sample(double time)
    {
        model.transform.SetPositionAndRotation(Vector3.zero, Quaternion.Euler(0, 180, 0));
        playable.SetTime(time); graph.Evaluate(0);
        model.transform.SetPositionAndRotation(Vector3.zero, Quaternion.Euler(0, 180, 0));
    }
    private static void ChangeClip(AnimationClip clip)
    {
        if (playable.GetAnimationClip() == clip) return;
        graph.Destroy();
        graph = PlayableGraph.Create("Miku offline pet pose"); graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
        playable = AnimationClipPlayable.Create(graph, clip); playable.SetApplyFootIK(false);
        AnimationPlayableOutput.Create(graph, "Miku", animator).SetSourcePlayable(playable); graph.Play();
    }
    private static Bounds PosedBounds()
    {
        Bounds bounds = new Bounds(); bool first = true;
        foreach (var renderer in model.GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            var baked = new Mesh();
            try
            {
                renderer.BakeMesh(baked);
                foreach (var vertex in baked.vertices)
                {
                    Vector3 point = renderer.transform.TransformPoint(vertex);
                    if (first) { bounds = new Bounds(point, Vector3.zero); first = false; } else bounds.Encapsulate(point);
                }
            }
            finally { Object.DestroyImmediate(baked); }
        }
        if (first || bounds.size.y < 0.5f) throw new InvalidOperationException("Invalid posed avatar bounds.");
        return bounds;
    }
    private static void ApplySavedAppearance()
    {
        string profile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "LocalLow", "Linh", "Mao Mate Engine", "mao-appearance.json");
        var saved = File.Exists(profile) ? JsonUtility.FromJson<MaoAppearanceProfile>(File.ReadAllText(profile)) : new MaoAppearanceProfile();
        saved.Normalize();
        var body = model.GetComponentsInChildren<SkinnedMeshRenderer>(true).First(r => r.sharedMaterials.Any(m => m != null && m.name.StartsWith("Costume", StringComparison.Ordinal)));
        var head = animator.GetBoneTransform(HumanBodyBones.Head);
        if (saved.hair != 0 || saved.face != 0) { var mesh = AppearanceCall<Mesh>("MaoAppearanceMeshBuilder", "Build", body, head, saved.hair, saved.face); owned.Add(mesh); body.sharedMesh = mesh; }
        var materials = body.sharedMaterials;
        for (int index = 0; index < materials.Length; index++)
        {
            var source = materials[index]; if (source == null || source.mainTexture == null) continue;
            int kind = source.name.StartsWith("Hair", StringComparison.Ordinal) ? 0 : source.name.StartsWith("Costume", StringComparison.Ordinal) ? 1 : source.name.StartsWith("Eye", StringComparison.Ordinal) ? 2 : -1;
            int id = kind == 0 ? saved.hairColor : kind == 1 ? saved.outfit : kind == 2 ? saved.eyeColor : 0;
            if (kind < 0 || id == 0) continue;
            if (kind == 1 && id == 3) throw new NotSupportedException("Jacket export needs the live accessory mesh.");
            var material = new Material(source); var texture = AppearanceCall<Texture2D>("MaoAppearancePalette", "Recolor", source.mainTexture, kind, id);
            owned.Add(material); owned.Add(texture); material.mainTexture = texture;
            if (material.HasProperty("_ShadeTexture")) material.SetTexture("_ShadeTexture", texture);
            materials[index] = material;
        }
        body.sharedMaterials = materials;
        if (saved.catEars != 0 || saved.catTail != 0) throw new NotSupportedException("Selected cat accessories require an explicit export binding.");
        File.WriteAllText(Path.Combine(output, "appearance-snapshot.json"), JsonUtility.ToJson(saved, true));
    }
    private static T AppearanceCall<T>(string typeName, string method, params object[] arguments)
    {
        // Offline tooling reuses the runtime's internal pure mesh/palette helpers
        // without changing their public surface or creating a live controller.
        var type = typeof(MaoAppearanceController).Assembly.GetTypes().Single(t => t.Name == typeName);
        var function = type.GetMethod(method, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        if (function == null) throw new MissingMethodException(typeName, method);
        return (T)function.Invoke(null, arguments);
    }
    private static void Capture(string path)
    {
        var previous = RenderTexture.active; Texture2D pixels = null;
        var snapshots = new List<GameObject>(); var meshes = new List<Mesh>();
        var renderers = model.GetComponentsInChildren<SkinnedMeshRenderer>(true);
        var enabled = renderers.Select(r => r.enabled).ToArray();
        try
        {
            // In batch edit mode, camera.Render can reuse the previous GPU skin
            // matrices after manual FK changes. Bake the exact current bone and
            // morph pose, then render temporary static copies in the same space.
            foreach (var renderer in renderers)
            {
                var baked = new Mesh(); renderer.BakeMesh(baked); meshes.Add(baked);
                var snapshot = new GameObject("Offline pose snapshot", typeof(MeshFilter), typeof(MeshRenderer));
                snapshot.layer = 31; snapshot.transform.SetParent(renderer.transform, false);
                snapshot.GetComponent<MeshFilter>().sharedMesh = baked;
                snapshot.GetComponent<MeshRenderer>().sharedMaterials = renderer.sharedMaterials;
                snapshots.Add(snapshot); renderer.enabled = false;
            }
            camera.Render(); RenderTexture.active = target;
            pixels = new Texture2D(target.width, target.height, TextureFormat.RGBA32, false);
            pixels.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0); pixels.Apply(); File.WriteAllBytes(path, pixels.EncodeToPNG());
        }
        finally
        {
            RenderTexture.active = previous; if (pixels != null) Object.DestroyImmediate(pixels);
            foreach (var snapshot in snapshots) Object.DestroyImmediate(snapshot);
            foreach (var mesh in meshes) Object.DestroyImmediate(mesh);
            for (int i = 0; i < renderers.Length; i++) renderers[i].enabled = enabled[i];
        }
    }
    private static void Cleanup()
    {
        if (graph.IsValid()) graph.Destroy();
        if (camera != null) camera.targetTexture = null;
        if (target != null) { target.Release(); Object.DestroyImmediate(target); }
        if (model != null) Object.DestroyImmediate(model);
        foreach (var item in owned) if (item != null) Object.DestroyImmediate(item);
        owned.Clear();
    }
}
#endif
