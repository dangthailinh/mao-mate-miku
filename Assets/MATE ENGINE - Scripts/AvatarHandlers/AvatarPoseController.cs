using System;
using System.Collections.Generic;
using System.IO;
using MaoIntegration;
using UnityEngine;

[DisallowMultipleComponent]
public sealed class AvatarPoseController : MonoBehaviour
{
    [Header("References")]
    public Animator avatarAnimator;

    [Header("Keyboard")]
    public bool enableKeyboardShortcuts;
    public KeyCode cyclePoseKey = KeyCode.F2;
    public KeyCode stopPoseKey = KeyCode.F3;

    public event Action<int, string> PoseChanged;

    public int CurrentPoseIndex { get; private set; } = -1;
    public int PoseCount => poses.Length;
    public bool IsPoseActive => CurrentPoseIndex >= 0;
    public string CurrentPoseName => CurrentPoseIndex >= 0 && CurrentPoseIndex < poses.Length
        ? poses[CurrentPoseIndex].name
        : string.Empty;

    public bool TogglePose(int poseIndex)
    {
        if (IsPoseActive && CurrentPoseIndex == poseIndex)
        {
            StopPose();
            return true;
        }
        return PlayPose(poseIndex);
    }

    public string GetPoseName(int poseIndex)
    {
        return poseIndex >= 0 && poseIndex < poses.Length && poses[poseIndex] != null
            ? poses[poseIndex].name
            : string.Empty;
    }

    static readonly int[] BusyParameters =
    {
        Animator.StringToHash("isCustomDancing"),
        Animator.StringToHash("isDancing"),
        Animator.StringToHash("isBigScreen"),
        Animator.StringToHash("isBigScreenSaver"),
        Animator.StringToHash("isBigScreenAlarm"),
        Animator.StringToHash("isWaitingForDancing"),
        Animator.StringToHash("IsSleeping")
    };

    readonly HashSet<int> boolParameters = new HashSet<int>();
    RuntimePoseFrame[] poses = Array.Empty<RuntimePoseFrame>();
    HumanPoseHandler humanPoseHandler;
    HumanPose humanPose;
    float poseStopAt;
    MaoAnimationCoordinator animationCoordinator;

    void Awake()
    {
        LoadPoseLibrary();
        if (avatarAnimator == null) avatarAnimator = GetComponentInChildren<Animator>(true);
        SetAnimator(avatarAnimator);
    }

    void Update()
    {
        if (IsPoseActive && (avatarAnimator == null || IsAnimatorBusy()))
        {
            StopPose();
            return;
        }
        if (AvatarPoseSelectionLogic.ShouldExpirePose(IsPoseActive, poseStopAt, Time.unscaledTime))
        {
            StopPose();
            return;
        }

        if (!enableKeyboardShortcuts) return;
        if (Input.GetKeyDown(stopPoseKey))
        {
            StopPose();
            return;
        }

        if (!Input.GetKeyDown(cyclePoseKey)) return;
        if (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift)) PreviousPose();
        else NextPose();
    }

    void LateUpdate()
    {
        if (IsPoseActive && !ApplyCurrentPose()) StopPose();
    }

    void OnDisable()
    {
        StopPose();
    }

    void OnDestroy()
    {
        StopPose();
        DisposeHumanPoseHandler();
    }

    public void SetAnimator(Animator animator)
    {
        if (avatarAnimator != animator) StopPose();
        DisposeHumanPoseHandler();
        avatarAnimator = animator;
        animationCoordinator = avatarAnimator != null
            ? avatarAnimator.GetComponentInParent<MaoAnimationCoordinator>()
            : null;
        CacheAnimatorParameters();
        if (avatarAnimator != null && avatarAnimator.isHuman && avatarAnimator.avatar != null)
            humanPoseHandler = new HumanPoseHandler(avatarAnimator.avatar, avatarAnimator.transform);
    }

    public bool NextPose()
    {
        return PlayPose(AvatarPoseSelectionLogic.NextIndex(CurrentPoseIndex, poses.Length));
    }

    public bool PreviousPose()
    {
        return PlayPose(AvatarPoseSelectionLogic.PreviousIndex(CurrentPoseIndex, poses.Length));
    }

    public bool CyclePoseOrStop()
    {
        int nextIndex = AvatarPoseSelectionLogic.ConsoleCycleIndex(CurrentPoseIndex, poses.Length);
        if (nextIndex >= 0) return PlayPose(nextIndex);
        if (!IsPoseActive) return false;
        StopPose();
        return true;
    }

    public bool PlayPose(int poseIndex)
    {
        return PlayPoseInternal(poseIndex, 0f);
    }

    public bool PlayPoseForDuration(int poseIndex, float durationSeconds)
    {
        return PlayPoseInternal(poseIndex, Mathf.Max(0f, durationSeconds));
    }

    bool PlayPoseInternal(int poseIndex, float durationSeconds)
    {
        bool indexValid = poseIndex >= 0 && poseIndex < poses.Length;
        bool hasAnimator = avatarAnimator != null;
        bool isHumanoid = hasAnimator && avatarAnimator.isHuman;
        bool hasValidMuscles = indexValid && poses[poseIndex] != null
            && AvatarPoseSelectionLogic.HasExpectedMuscleCount(poses[poseIndex].muscles, HumanTrait.MuscleCount);
        PosePlaybackBlockReason blockReason = AvatarPoseSelectionLogic.EvaluatePlayback(
            poseIndex,
            poses.Length,
            hasAnimator,
            isHumanoid,
            humanPoseHandler != null,
            hasValidMuscles,
            hasAnimator && IsAnimatorBusy());
        if (blockReason != PosePlaybackBlockReason.None)
        {
            Debug.LogWarning($"[MikuPose] event=blocked index={poseIndex} reason={blockReason}");
            return false;
        }

        if (animationCoordinator != null && !animationCoordinator.TrySetPersistent(MaoAnimationOwner.Pose, true))
        {
            Debug.LogWarning($"[MikuPose] event=blocked index={poseIndex} reason=AnimationOwner");
            return false;
        }

        CurrentPoseIndex = poseIndex;
        if (!ApplyCurrentPose())
        {
            CurrentPoseIndex = -1;
            poseStopAt = 0f;
            animationCoordinator?.SetPersistent(MaoAnimationOwner.Pose, false);
            Debug.LogWarning($"[MikuPose] event=blocked index={poseIndex} reason=ApplyFailed");
            return false;
        }
        poseStopAt = durationSeconds > 0f ? Time.unscaledTime + durationSeconds : 0f;
        Debug.Log($"[MikuPose] event=applied index={poseIndex} name={CurrentPoseName} timed={durationSeconds > 0f}");
        PoseChanged?.Invoke(CurrentPoseIndex, CurrentPoseName);
        return true;
    }

    public void StopPose()
    {
        bool wasActive = IsPoseActive;
        CurrentPoseIndex = -1;
        poseStopAt = 0f;
        animationCoordinator?.SetPersistent(MaoAnimationOwner.Pose, false);
        if (wasActive && avatarAnimator != null && avatarAnimator.isActiveAndEnabled)
            avatarAnimator.Update(0f);
        if (wasActive) PoseChanged?.Invoke(-1, string.Empty);
    }

    void LoadPoseLibrary()
    {
        string executableDirectory = Path.GetDirectoryName(Application.dataPath) ?? string.Empty;
        string libraryPath = Path.Combine(executableDirectory, "MaoAssets", "MikuPoses.json");
        if (!File.Exists(libraryPath))
        {
            string editorPath = Path.Combine(executableDirectory, "AvatarAssets", "MikuPoses.json");
            if (File.Exists(editorPath)) libraryPath = editorPath;
        }

        try
        {
            RuntimePoseLibrary library = File.Exists(libraryPath)
                ? JsonUtility.FromJson<RuntimePoseLibrary>(File.ReadAllText(libraryPath))
                : null;
            RuntimePoseFrame[] candidates = library?.poses ?? Array.Empty<RuntimePoseFrame>();
            var validPoses = new List<RuntimePoseFrame>(candidates.Length);
            for (int i = 0; i < candidates.Length; i++)
                if (candidates[i] != null && !string.IsNullOrEmpty(candidates[i].name)
                    && AvatarPoseSelectionLogic.HasExpectedMuscleCount(candidates[i].muscles, HumanTrait.MuscleCount))
                    validPoses.Add(candidates[i]);
            AppendRuntimeFbxPoses(validPoses);
            poses = OrderPoses(validPoses);
        }
        catch (Exception exception)
        {
            poses = Array.Empty<RuntimePoseFrame>();
            Debug.LogWarning($"[MikuPoseLibrary] load_failed={exception.GetType().Name}");
        }

        int expected = AvatarPoseSelectionLogic.PoseResourceNames().Length;
        string loadSummary = $"[MikuPoseLibrary] loaded={poses.Length} expected={expected}";
        if (poses.Length == expected) Debug.Log(loadSummary);
        else Debug.LogWarning(loadSummary);
    }

    void AppendRuntimeFbxPoses(List<RuntimePoseFrame> target)
    {
        string[] resourcePaths = AvatarPoseSelectionLogic.RuntimeFbxPoseResourcePaths();
        for (int index = 0; index < resourcePaths.Length; index++)
        {
            string resourcePath = resourcePaths[index];
            string poseName = resourcePath.Substring(resourcePath.LastIndexOf('/') + 1);
            if (target.Exists(frame => frame != null && frame.name == poseName)) continue;

            RuntimePoseFrame frame = BakeRuntimeFbxPose(resourcePath, poseName);
            if (frame != null) target.Add(frame);
        }
    }

    static RuntimePoseFrame BakeRuntimeFbxPose(string resourcePath, string poseName)
    {
        GameObject prefab = Resources.Load<GameObject>(resourcePath);
        AnimationClip[] clips = Resources.LoadAll<AnimationClip>(resourcePath);
        AnimationClip clip = null;
        for (int index = 0; index < clips.Length; index++)
            if (clips[index] != null && !clips[index].name.StartsWith("__preview__", StringComparison.OrdinalIgnoreCase))
            {
                clip = clips[index];
                break;
            }
        if (prefab == null || clip == null)
        {
            Debug.LogWarning($"[MikuPoseLibrary] fbx_missing={poseName}");
            return null;
        }

        GameObject instance = null;
        HumanPoseHandler sourceHandler = null;
        try
        {
            instance = Instantiate(prefab);
            instance.name = "Miku Pose Source - " + poseName;
            instance.hideFlags = HideFlags.HideAndDontSave;
            Renderer[] sourceRenderers = instance.GetComponentsInChildren<Renderer>(true);
            for (int index = 0; index < sourceRenderers.Length; index++) sourceRenderers[index].enabled = false;
            Animator sourceAnimator = instance.GetComponentInChildren<Animator>(true);
            if (sourceAnimator == null || sourceAnimator.avatar == null || !sourceAnimator.avatar.isHuman)
                throw new InvalidOperationException("The imported pose avatar is not Humanoid.");

            sourceAnimator.enabled = false;
            float sampleTime = clip.length > 0f ? clip.length * 0.5f : 0f;
            clip.SampleAnimation(instance, sampleTime);
            sourceHandler = new HumanPoseHandler(sourceAnimator.avatar, sourceAnimator.transform);
            var sourcePose = new HumanPose();
            sourceHandler.GetHumanPose(ref sourcePose);
            if (!AvatarPoseSelectionLogic.HasExpectedMuscleCount(sourcePose.muscles, HumanTrait.MuscleCount))
                throw new InvalidDataException("The imported pose did not produce a complete Humanoid muscle frame.");

            var muscles = new float[sourcePose.muscles.Length];
            Array.Copy(sourcePose.muscles, muscles, muscles.Length);
            Debug.Log($"[MikuPoseLibrary] fbx_loaded={poseName} clip={clip.name}");
            return new RuntimePoseFrame { name = poseName, muscles = muscles };
        }
        catch (Exception exception)
        {
            Debug.LogWarning($"[MikuPoseLibrary] fbx_failed={poseName} error={exception.GetType().Name}");
            return null;
        }
        finally
        {
            sourceHandler?.Dispose();
            if (instance != null)
            {
                if (Application.isPlaying) Destroy(instance);
                else DestroyImmediate(instance);
            }
        }
    }

    static RuntimePoseFrame[] OrderPoses(List<RuntimePoseFrame> source)
    {
        var remaining = new List<RuntimePoseFrame>(source);
        var ordered = new List<RuntimePoseFrame>(source.Count);
        string[] expectedNames = AvatarPoseSelectionLogic.PoseResourceNames();
        for (int index = 0; index < expectedNames.Length; index++)
        {
            string expectedName = expectedNames[index];
            int matchIndex = remaining.FindIndex(frame => frame != null && frame.name == expectedName);
            if (matchIndex < 0) continue;
            ordered.Add(remaining[matchIndex]);
            remaining.RemoveAt(matchIndex);
        }
        ordered.AddRange(remaining);
        return ordered.ToArray();
    }

    void CacheAnimatorParameters()
    {
        boolParameters.Clear();
        if (avatarAnimator == null) return;
        AnimatorControllerParameter[] parameters = avatarAnimator.parameters;
        for (int i = 0; i < parameters.Length; i++)
            if (parameters[i].type == AnimatorControllerParameterType.Bool)
                boolParameters.Add(parameters[i].nameHash);
    }

    bool IsAnimatorBusy()
    {
        for (int i = 0; i < BusyParameters.Length; i++)
            if (boolParameters.Contains(BusyParameters[i]) && avatarAnimator.GetBool(BusyParameters[i]))
                return true;
        return false;
    }

    bool ApplyCurrentPose()
    {
        if (humanPoseHandler == null || CurrentPoseIndex < 0 || CurrentPoseIndex >= poses.Length)
            return false;
        try
        {
            humanPoseHandler.GetHumanPose(ref humanPose);
            if (humanPose.muscles == null || humanPose.muscles.Length != poses[CurrentPoseIndex].muscles.Length)
                return false;
            Array.Copy(poses[CurrentPoseIndex].muscles, humanPose.muscles, humanPose.muscles.Length);
            humanPoseHandler.SetHumanPose(ref humanPose);
            return true;
        }
        catch (Exception exception)
        {
            Debug.LogWarning($"[MikuPoseLibrary] apply_failed={exception.GetType().Name}");
            return false;
        }
    }

    void DisposeHumanPoseHandler()
    {
        if (humanPoseHandler == null) return;
        humanPoseHandler.Dispose();
        humanPoseHandler = null;
    }

    [Serializable]
    sealed class RuntimePoseLibrary
    {
        public RuntimePoseFrame[] poses;
    }

    [Serializable]
    sealed class RuntimePoseFrame
    {
        public string name;
        public float[] muscles;
    }
}
