using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using MaoIntegration;
using UnityEngine;

// Opt-in full-player verification. All artifacts and persistence use the controller's
// isolated temporary-cache profile; this component never changes PlayerPrefs.
[DisallowMultipleComponent]
public sealed class MaoCatAccessoriesSmoke : MonoBehaviour
{
    public static void Ensure()
    {
        if (FindFirstObjectByType<MaoCatAccessoriesSmoke>() != null) return;
        var runner = new GameObject("Miku accessory verification runner");
        DontDestroyOnLoad(runner);
        runner.AddComponent<MaoCatAccessoriesSmoke>();
    }
    private const float DeadlineSeconds = 90f;
    private readonly List<Check> checks = new List<Check>();
    private readonly List<string> captures = new List<string>();
    private readonly List<string> skippedPoses = new List<string>();
    private readonly HashSet<UnityEngine.Object> accessoryResources = new HashSet<UnityEngine.Object>();
    private readonly Dictionary<Transform, Vector3> originalScales = new Dictionary<Transform, Vector3>();
    private readonly int[] expectedResources = { 0, -1, -1, -1 };
    private MaoAppearanceController controller;
    private MaoCatAccessories accessories;
    private SkinnedMeshRenderer body;
    private Animator animator;
    private Mesh originalMesh;
    private Vector3[] originalVertices;
    private string outputFolder, profilePath, captureFolder, startedUtc;
    private int failures;
    private float startedAt;
    private bool reload, finished, completed, persisted, loaded;
    private AvatarPoseController smokePose;
    private MaoReactionDirector smokeDirector;
    private AvatarLocomotionController smokeLocomotion;
    private bool runtimeStateChanged, smokePoseStarted, previousFollow, previousRun, previousDebug, previousEnabled;

    [Serializable]
    private sealed class Check
    {
        public string name;
        public bool passed;
        public string detail;
    }

    [Serializable]
    private sealed class Summary
    {
        public int schemaVersion = 1;
        public string phase, unityVersion, startedUtc, profilePath, captureFolder;
        public bool completed, persisted, loaded;
        public int passed, failed;
        public string[] captures, skippedPoses;
        public Check[] checks;
    }

    private IEnumerator Start()
    {
        string[] arguments = Environment.GetCommandLineArgs();
        reload = Array.IndexOf(arguments, "--mao-smoke-cat-accessories-reload") >= 0;
        if (!reload && Array.IndexOf(arguments, "--mao-smoke-cat-accessories") < 0) yield break;
        Application.runInBackground = true;
        Debug.Log("[Mao Cat Smoke] runner_started phase=" + (reload ? "reload" : "initial"));
        startedAt = Time.realtimeSinceStartup;
        startedUtc = DateTime.UtcNow.ToString("o");
        outputFolder = Path.Combine(Application.temporaryCachePath, "MaoCatAccessoriesSmoke");
        profilePath = Path.Combine(outputFolder, "mao-appearance.json");
        captureFolder = Path.Combine(outputFolder, (reload ? "reload-" : "initial-") +
            DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffZ"));

        // Drive the iterator explicitly so an exception produces a failed summary and exit code.
        IEnumerator sequence = Run();
        while (!finished)
        {
            object yielded = null;
            bool hasNext = false;
            try { hasNext = sequence.MoveNext(); if (hasNext) yielded = sequence.Current; }
            catch (Exception exception) { Assert("uncaught-exception", false, exception.GetType().Name); }
            if (!hasNext) break;
            yield return yielded;
        }
        (sequence as IDisposable)?.Dispose();
        Finish();
    }

    private void Update()
    {
        if (startedAt > 0f && !finished && Time.realtimeSinceStartup - startedAt > DeadlineSeconds)
        {
            Assert("deadline", false, "bounded player smoke exceeded 90 seconds");
            StopAllCoroutines();
            Finish();
        }
    }

    private IEnumerator Run()
    {
        Directory.CreateDirectory(captureFolder);
        yield return new WaitForSecondsRealtime(4f);
        controller = MaoAppearanceController.Active;
        if (controller == null) { Assert("active-appearance-controller", false); yield break; }
        // Fresh isolated preferences can hide the whole avatar container. Activate the
        // test subject explicitly, never change normal-launch visibility preferences.
        var parents = new Stack<Transform>();
        for (Transform parent = controller.transform; parent != null; parent = parent.parent) parents.Push(parent);
        while (parents.Count > 0) parents.Pop().gameObject.SetActive(true);
        yield return null;
        accessories = controller.GetComponent<MaoCatAccessories>();
        animator = controller.GetComponentInChildren<Animator>(true);
        body = controller.GetComponentsInChildren<SkinnedMeshRenderer>(true).FirstOrDefault(renderer =>
            renderer.sharedMesh != null && renderer.sharedMaterials.Any(material => material != null &&
            material.name.StartsWith("Costume", StringComparison.Ordinal)));
        Assert("supported-avatar", controller != null && controller.Supported && accessories != null &&
            animator != null && animator.avatar != null && animator.avatar.isValid && animator.isHuman && body != null);
        if (failures != 0) yield break;
        string readiness = "controller=" + controller.isActiveAndEnabled + " accessory=" + accessories.isActiveAndEnabled +
            " ears=" + accessories.EarsAvailable + " tail=" + accessories.TailAvailable +
            " root=" + controller.gameObject.activeInHierarchy;
        Assert("ears-available", controller.CanSelect(MaoAppearancePart.CatEars) && accessories.EarsAvailable, readiness);
        Assert("tail-available", controller.CanSelect(MaoAppearancePart.CatTail) && accessories.TailAvailable, readiness);
        if (failures != 0) yield break;

        if (!reload) controller.ResetAppearance(false);
        yield return null;
        yield return null;
        originalMesh = body.sharedMesh;
        originalVertices = originalMesh.vertices;
        RememberScales(body.transform);
        RememberScales(animator.GetBoneTransform(HumanBodyBones.Head));
        RememberScales(animator.GetBoneTransform(HumanBodyBones.Hips));

        if (reload)
        {
            loaded = controller.Profile.catEars == 1 && controller.Profile.catTail == 1 &&
                controller.Profile.hairColor == 2 && accessories.EarsVisible && accessories.TailVisible;
            Assert("real-startup-profile-reloaded", loaded, "expected ears=1 tail=1 hairColor=2 before any selection");
            CheckState("reload", true, true);
            CaptureViews("reloaded");
            controller.ResetAppearance(true);
            yield return null;
            yield return null;
            CheckState("reload-reset", false, false);
            Assert("reload-reset-released-all", controller.OwnedResources == 0 && accessoryResources.All(resource => resource == null));
            MaoAppearanceProfile reset = ReadSavedProfile();
            Assert("reload-reset-persisted", reset != null && reset.catEars == 0 && reset.catTail == 0 && reset.hairColor == 0);
            completed = true;
            yield break;
        }

        CheckState("original", false, false);
        Capture("original-front", 0f, false);
        var menu = FindObjectsByType<Xamin.CircleSelector>(FindObjectsInactive.Include,
            FindObjectsSortMode.None).FirstOrDefault(candidate => candidate.Buttons.Any(definition =>
                definition != null && definition.GetComponent<Xamin.Button>()?.id == "appearance-entry"));
        Assert("radial-appearance-entry-present", menu != null);
        if (menu != null)
        {
            menu.gameObject.SetActive(true);
            menu.Open();
            yield return new WaitForSecondsRealtime(0.4f);
            var entry = menu.GetButtonWithId("appearance-entry");
            Assert("radial-visible-entry", entry != null && entry.unlocked);
            if (entry != null) entry.ExecuteAction();
            yield return new WaitForSecondsRealtime(0.4f);
            var accessoryPage = menu.GetButtonWithId("appearance-accessories");
            Assert("radial-accessory-page-present", accessoryPage != null && accessoryPage.unlocked);
            if (accessoryPage != null) accessoryPage.ExecuteAction();
            yield return new WaitForSecondsRealtime(0.4f);
            Assert("radial-accessory-page-five-choices", menu.Buttons.Count == 5);
            foreach (string id in new[] { "appearance-CatEars-1", "appearance-CatTail-1",
                "appearance-CatEars-0", "appearance-CatTail-0" })
            {
                var button = menu.GetButtonWithId(id);
                Assert("radial-visible-" + id, button != null && button.unlocked);
                if (button == null) continue;
                button.ExecuteAction();
                yield return new WaitForSecondsRealtime(0.4f);
                var visible = menu.GetButtonWithId(id);
                var label = visible != null ? visible.transform.Find("Label")?.GetComponent<TMPro.TextMeshProUGUI>() : null;
                Assert("radial-selection-" + id, visible != null && visible.useCustomColor &&
                    label != null && label.text.Contains("[ĐANG CHỌN]"));
                Assert("radial-model-sync-" + id, accessories.EarsVisible == (controller.Profile.catEars == 1) &&
                    accessories.TailVisible == (controller.Profile.catTail == 1));
            }
            yield return new WaitForEndOfFrame();
            Texture2D menuImage = ScreenCapture.CaptureScreenshotAsTexture();
            if (menuImage != null)
            {
                string menuPath = Path.Combine(captureFolder, "radial-accessories.png");
                File.WriteAllBytes(menuPath, menuImage.EncodeToPNG());
                captures.Add(menuPath);
                Destroy(menuImage);
            }
            var back = menu.GetButtonWithId("appearance-accessories-back");
            if (back != null) back.ExecuteAction();
            yield return new WaitForSecondsRealtime(0.4f);
            back = menu.GetButtonWithId("appearance-back");
            if (back != null) back.ExecuteAction();
            yield return new WaitForSecondsRealtime(0.4f);
            menu.Close();
            controller.ResetAppearance(false);
            yield return null;
        }
        Assert("enable-ears", controller.Select(MaoAppearancePart.CatEars, 1, false));
        yield return null;
        CheckState("ears-only", true, false);
        Capture("ears-only-head", 0f, true);
        Assert("enable-tail", controller.Select(MaoAppearancePart.CatTail, 1, false));
        yield return null;
        CheckState("both", true, true);
        CaptureViews("both");
        accessories.enabled = false;
        yield return null;
        yield return null;
        Assert("disabled-component-cleanup", !accessories.EarsVisible && !accessories.TailVisible &&
            accessories.OwnedResources == 0 && accessoryResources.All(resource => resource == null));
        Assert("disabled-component-choices-unavailable", !controller.CanSelect(MaoAppearancePart.CatEars) &&
            !controller.CanSelect(MaoAppearancePart.CatTail));
        Assert("disabled-component-preserves-profile", controller.Profile.catEars == 1 && controller.Profile.catTail == 1);
        accessories.enabled = true;
        yield return null;
        CheckState("component-reenabled", true, true);
        Assert("disable-ears-independently", controller.Select(MaoAppearancePart.CatEars, 0, false));
        yield return null;
        CheckState("tail-only", false, true);
        Capture("tail-only-side", 90f, false);
        Assert("disable-tail-independently", controller.Select(MaoAppearancePart.CatTail, 0, false));
        yield return null;
        yield return null;
        CheckState("off", false, false);
        Assert("all-off-resources-released", accessoryResources.All(resource => resource == null));

        for (int repetition = 0; repetition < 8; repetition++)
        {
            controller.Select(MaoAppearancePart.CatEars, 1, false);
            yield return null;
            CheckState("repeat-" + repetition + "-ears", true, false);
            controller.Select(MaoAppearancePart.CatEars, 0, false);
            controller.Select(MaoAppearancePart.CatTail, 1, false);
            yield return null;
            CheckState("repeat-" + repetition + "-tail", false, true);
            controller.Select(MaoAppearancePart.CatEars, 1, false);
            yield return null;
            CheckState("repeat-" + repetition + "-both", true, true);
            controller.ResetAppearance(false);
            yield return null;
            yield return null;
            CheckState("repeat-" + repetition + "-reset", false, false);
            Assert("repeat-" + repetition + "-released", controller.OwnedResources == 0 &&
                accessoryResources.All(resource => resource == null));
        }

        controller.Select(MaoAppearancePart.CatEars, 1, false);
        controller.Select(MaoAppearancePart.CatTail, 1, false);
        for (int color = 0; color <= 2; color++)
        {
            Assert("select-tint-" + color, controller.Select(MaoAppearancePart.HairColor, color, false));
            yield return null;
            yield return null;
            CheckState("tint-" + color, true, true);
            Color expected = color == 1 ? new Color(0.96f, 0.48f, 0.70f) :
                color == 2 ? new Color(0.30f, 0.52f, 0.95f) : new Color(0.16f, 0.83f, 0.79f);
            Material[] outerMaterials = CatFilters().SelectMany(filter => filter.GetComponent<MeshRenderer>().sharedMaterials)
                .Where(material => material != null && material.name == "Miku cat turquoise").Distinct().ToArray();
            Assert("accessory-tint-" + color, outerMaterials.Length == 1 && outerMaterials.All(material =>
                material.HasProperty("_Color") && ColorClose(material.GetColor("_Color"), expected)));
            Capture("tint-" + color + "-head", 0f, true);
        }

        smokePose = FindObjectsByType<AvatarPoseController>(FindObjectsInactive.Exclude,
            FindObjectsSortMode.None).FirstOrDefault(candidate => candidate.avatarAnimator == animator);
        smokeDirector = FindObjectsByType<MaoReactionDirector>(FindObjectsInactive.Exclude,
            FindObjectsSortMode.None).FirstOrDefault(candidate => candidate.BoundAvatarInstanceId == animator.GetInstanceID());
        smokeLocomotion = FindObjectsByType<AvatarLocomotionController>(FindObjectsInactive.Exclude,
            FindObjectsSortMode.None).FirstOrDefault(candidate => candidate.Animator == animator);
        bool safeMotion = smokeDirector != null && smokeLocomotion != null && smokeDirector.AdaptiveMotionReady &&
            !smokeDirector.GravityEnabled && !smokeDirector.GuitarPlaying && (smokePose == null || !smokePose.IsPoseActive);
        if (safeMotion)
        {
            previousFollow = smokeLocomotion.FollowCursorEnabled;
            previousRun = smokeLocomotion.PreferRunning;
            previousDebug = smokeLocomotion.EnableLocomotionDebug;
            previousEnabled = smokeLocomotion.EnableLocomotion;
            runtimeStateChanged = true;
            smokeDirector.SetCursorFollowEnabled(false);
        }
        int sit = smokePose == null || smokePose.IsPoseActive ? -1 : Enumerable.Range(0, smokePose.PoseCount).Where(index =>
            smokePose.GetPoseName(index).IndexOf("sit", StringComparison.OrdinalIgnoreCase) >= 0).DefaultIfEmpty(-1).First();
        if (sit >= 0 && (smokePoseStarted = smokePose.PlayPoseForDuration(sit, 2f)))
        {
            yield return new WaitForSecondsRealtime(0.25f);
            Assert("sit-pose-active", smokePose.IsPoseActive && smokePose.CurrentPoseIndex == sit);
            CheckState("sit", true, true);
            Capture("sit-front", 0f, false);
            Capture("sit-side", 90f, false);
            smokePose.StopPose();
            smokePoseStarted = false;
            yield return null;
        }
        else SkipPose("sit: no available named sitting pose or animation ownership denied");

        if (safeMotion)
        {
            smokeLocomotion.EnableLocomotion = true;
            if (smokeDirector.BeginAdaptiveWalkSmokeProbe(1))
            {
                yield return new WaitForSecondsRealtime(0.2f);
                Assert("walk-service-active", smokeLocomotion.CurrentCursorGait == MaoLocomotionGait.Walk);
                CheckState("walk", true, true);
                Capture("walk-three-quarter", 40f, false);
            }
            else SkipPose("walk: real locomotion service rejected the smoke probe");
            smokeDirector.SetCursorFollowEnabled(false);
            smokeLocomotion.EnableLocomotion = true;
            if (smokeDirector.BeginAdaptiveMotionSmokeProbe(-1))
            {
                yield return new WaitForSecondsRealtime(0.2f);
                Assert("run-service-active", smokeLocomotion.CurrentCursorGait == MaoLocomotionGait.Run);
                CheckState("run", true, true);
                Capture("run-three-quarter", 40f, false);
            }
            else SkipPose("run: real locomotion service rejected the smoke probe");
        }
        else SkipPose("walk/run: no safe available locomotion service (gravity or guitar may own animation)");
        RestoreRuntimeState();

        controller.ResetAppearance(false);
        yield return null;
        yield return null;
        CheckState("final-reset", false, false);
        Assert("reset-zero-owned-resources", controller.OwnedResources == 0 && accessoryResources.All(resource => resource == null));
        Assert("persist-blue-hair", controller.Select(MaoAppearancePart.HairColor, 2, false));
        Assert("persist-ears", controller.Select(MaoAppearancePart.CatEars, 1, false));
        Assert("persist-tail", controller.Select(MaoAppearancePart.CatTail, 1, true));
        yield return null;
        MaoAppearanceProfile saved = ReadSavedProfile();
        persisted = saved != null && saved.catEars == 1 && saved.catTail == 1 && saved.hairColor == 2;
        Assert("real-profile-save", persisted, "second player launch must use --mao-smoke-cat-accessories-reload");
        CheckState("persisted", true, true);
        Capture("persisted-front", 0f, false);
        completed = true;
    }

    private void CheckState(string label, bool ears, bool tail)
    {
        Assert(label + "-independent-state", accessories.EarsVisible == ears && accessories.TailVisible == tail &&
            controller.Profile.catEars == (ears ? 1 : 0) && controller.Profile.catTail == (tail ? 1 : 0));
        MeshFilter[] filters = CatFilters();
        foreach (MeshFilter filter in filters)
        {
            accessoryResources.Add(filter.sharedMesh);
            foreach (Material material in filter.GetComponent<MeshRenderer>().sharedMaterials) accessoryResources.Add(material);
        }
        int state = (ears ? 1 : 0) + (tail ? 2 : 0);
        if (expectedResources[state] < 0) expectedResources[state] = accessories.OwnedResources;
        Assert(label + "-resources-stable", accessories.OwnedResources == expectedResources[state],
            "owned=" + accessories.OwnedResources + " expected=" + expectedResources[state]);
        Assert(label + "-no-duplicate-children", filters.Length == (ears ? 1 : 0) + (tail ? 1 : 0));
        Assert(label + "-finite-meshes", filters.All(filter => filter.sharedMesh != null &&
            filter.sharedMesh.vertices.All(Finite) && filter.sharedMesh.normals.All(Finite) &&
            Finite(filter.sharedMesh.bounds.center) && Finite(filter.sharedMesh.bounds.extents) &&
            filter.sharedMesh.triangles.All(index => index >= 0 && index < filter.sharedMesh.vertexCount)));
        Assert(label + "-source-mesh-unchanged", body.sharedMesh == originalMesh && originalMesh.vertices.SequenceEqual(originalVertices));
        Assert(label + "-parent-scales-unchanged", originalScales.All(pair => pair.Key != null && pair.Key.localScale == pair.Value));
        Assert(label + "-bone-parenting", filters.All(filter => filter.transform.parent ==
            animator.GetBoneTransform(filter.name == "Miku cat ears" ? HumanBodyBones.Head : HumanBodyBones.Hips)));
    }

    private MeshFilter[] CatFilters() => controller.GetComponentsInChildren<MeshFilter>(true).Where(filter =>
        filter.name == "Miku cat ears" || filter.name == "Miku cat tail").ToArray();

    private void RememberScales(Transform current)
    {
        while (current != null)
        {
            if (!originalScales.ContainsKey(current)) originalScales.Add(current, current.localScale);
            current = current.parent;
        }
    }

    private MaoAppearanceProfile ReadSavedProfile()
    {
        try { return File.Exists(profilePath) ? JsonUtility.FromJson<MaoAppearanceProfile>(File.ReadAllText(profilePath)) : null; }
        catch (Exception exception) { Assert("read-saved-profile", false, exception.GetType().Name); return null; }
    }

    private void CaptureViews(string label)
    {
        Capture(label + "-front", 0f, false);
        Capture(label + "-three-quarter", 40f, false);
        Capture(label + "-side", 90f, false);
        Capture(label + "-rear", 180f, false);
        Capture(label + "-head", 0f, true);
    }

    private void Capture(string label, float angle, bool headOnly)
    {
        var cameraObject = new GameObject("Miku cat verification camera") { hideFlags = HideFlags.HideAndDontSave };
        var camera = cameraObject.AddComponent<Camera>();
        camera.enabled = false;
        var target = new RenderTexture(768, headOnly ? 768 : 960, 24);
        RenderTexture previous = RenderTexture.active;
        Texture2D image = null;
        try
        {
            Bounds bounds = body.bounds;
            foreach (MeshFilter filter in CatFilters()) bounds.Encapsulate(filter.GetComponent<MeshRenderer>().bounds);
            Vector3 forward = Vector3.ProjectOnPlane(animator.transform.forward, Vector3.up).normalized;
            if (forward.sqrMagnitude < 0.01f) forward = Vector3.forward;
            Vector3 center = headOnly ? animator.GetBoneTransform(HumanBodyBones.Head).position +
                Vector3.up * bounds.size.y * 0.04f : bounds.center;
            camera.orthographic = true;
            camera.aspect = (float)target.width / target.height;
            camera.orthographicSize = headOnly ? Mathf.Max(0.22f, bounds.size.y * 0.16f) :
                Mathf.Max(0.65f, Mathf.Max(bounds.extents.y, bounds.extents.x / camera.aspect) * 1.15f);
            camera.transform.position = center + Quaternion.AngleAxis(angle, Vector3.up) * forward * Mathf.Max(3f, bounds.size.magnitude * 2f);
            camera.transform.LookAt(center, Vector3.up);
            camera.nearClipPlane = 0.01f;
            camera.farClipPlane = 100f;
            camera.cullingMask = 1 << body.gameObject.layer;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.11f, 0.14f, 0.18f, 1f);
            camera.targetTexture = target;
            // Manual rendering of a disabled camera and active-target readback:
            // https://docs.unity3d.com/6000.2/Documentation/ScriptReference/Camera.Render.html
            // https://docs.unity3d.com/6000.2/Documentation/ScriptReference/Texture2D.ReadPixels.html
            camera.Render();
            RenderTexture.active = target;
            image = new Texture2D(target.width, target.height, TextureFormat.RGBA32, false);
            image.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0);
            image.Apply();
            Color32[] pixels = image.GetPixels32();
            var colors = new HashSet<Color32>();
            for (int index = 0; index < pixels.Length; index += 37) colors.Add(pixels[index]);
            Assert("capture-" + label + "-nonblank", colors.Count > 12, "sampled-colors=" + colors.Count);
            string path = Path.Combine(captureFolder, label + ".png");
            File.WriteAllBytes(path, image.EncodeToPNG());
            captures.Add(path);
            Debug.Log("[Mao Cat Smoke] capture=" + path);
        }
        catch (Exception exception) { Assert("capture-" + label, false, exception.GetType().Name); }
        finally
        {
            RenderTexture.active = previous;
            camera.targetTexture = null;
            if (image != null) Destroy(image);
            target.Release();
            Destroy(target);
            Destroy(cameraObject);
        }
    }

    private void Assert(string name, bool passed, string detail = "")
    {
        checks.Add(new Check { name = name, passed = passed, detail = detail });
        if (!passed) failures++;
        Debug.Log("[Mao Cat Smoke] check=" + name + " passed=" + passed.ToString().ToLowerInvariant() + " " + detail);
    }

    private void SkipPose(string reason)
    {
        skippedPoses.Add(reason);
        Debug.Log("[Mao Cat Smoke] pose_skipped=" + reason);
    }

    private void Finish()
    {
        if (finished) return;
        finished = true;
        if (!completed && failures == 0) Assert("sequence-completed", false);
        try { RestoreRuntimeState(); }
        catch (Exception exception) { Assert("restore-runtime-state", false, exception.GetType().Name); }
        try
        {
            var summary = new Summary
            {
                phase = reload ? "reload" : "initial", unityVersion = Application.unityVersion,
                startedUtc = startedUtc, profilePath = profilePath, captureFolder = captureFolder,
                completed = completed, persisted = persisted, loaded = loaded,
                passed = checks.Count - failures, failed = failures,
                captures = captures.ToArray(), skippedPoses = skippedPoses.ToArray(), checks = checks.ToArray()
            };
            Directory.CreateDirectory(outputFolder);
            string path = Path.Combine(outputFolder, reload ? "reload-summary.json" : "summary.json");
            File.WriteAllText(path, JsonUtility.ToJson(summary, true), Encoding.UTF8);
            Debug.Log("[Mao Cat Smoke] summary=" + path);
        }
        catch (Exception exception) { Assert("write-summary", false, exception.GetType().Name); }
        Debug.Log("[Mao Cat Smoke] completed=" + completed + " phase=" + (reload ? "reload" : "initial") + " failures=" + failures);
        if (Array.IndexOf(Environment.GetCommandLineArgs(), "--mao-smoke-exit") >= 0) Application.Quit(failures == 0 && completed ? 0 : 1);
    }

    private void RestoreRuntimeState()
    {
        if (smokePoseStarted && smokePose != null) smokePose.StopPose();
        smokePoseStarted = false;
        if (!runtimeStateChanged) return;
        if (smokeDirector != null) smokeDirector.SetCursorFollowEnabled(previousFollow, previousRun);
        if (smokeLocomotion != null) smokeLocomotion.EnableLocomotionDebug = previousDebug;
        if (smokeLocomotion != null) smokeLocomotion.EnableLocomotion = previousEnabled;
        runtimeStateChanged = false;
    }

    private static bool Finite(Vector3 value) => !float.IsNaN(value.x) && !float.IsInfinity(value.x) &&
        !float.IsNaN(value.y) && !float.IsInfinity(value.y) && !float.IsNaN(value.z) && !float.IsInfinity(value.z);

    private static bool ColorClose(Color left, Color right) => Mathf.Abs(left.r - right.r) < 0.025f &&
        Mathf.Abs(left.g - right.g) < 0.025f && Mathf.Abs(left.b - right.b) < 0.025f;
}
