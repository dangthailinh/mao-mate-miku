using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using MaoIntegration;
using UnityEngine;
using Debug = UnityEngine.Debug;

public enum MaoDesktopActivity
{
    General,
    Coding,
    Video,
    Gaming,
    Browsing,
    Away
}

public enum MaoAttentionTargetType
{
    User,
    Screen,
    Cursor,
    Away,
    Interaction,
    Conversation
}

[Serializable]
public sealed class MaoAutonomyRuntimeSettings
{
    [Header("General")]
    public bool enabled = true;
    [Range(0.5f, 10f)] public float evaluationInterval = 2.5f;
    public KeyCode debugPanelKey = KeyCode.F10;

    [Header("Personality")]
    public MaoPersonalityProfile personality = MaoPersonalityProfile.Default;

    [Header("Initiative Thresholds")]
    [Range(0f, 1f)] public float physicalThreshold = 0.45f;
    [Range(0f, 1f)] public float speechThreshold = 0.68f;
    [Range(0f, 1f)] public float visionThreshold = 0.55f;
    [Min(10f)] public float physicalCooldown = 35f;
    [Min(60f)] public float speechCooldown = 300f;
    [Min(120f)] public float visionCooldown = 240f;

    [Header("Curiosity and Memory")]
    [Range(0f, 0.02f)] public float curiosityDecayPerSecond = 0.0015f;
    [Range(4, 64)] public int noveltyCapacity = 24;
    [Range(8, 128)] public int reactionHistoryCapacity = 32;

    [Header("Speech Budget")]
    [Range(1, 10)] public int speechBudgetCapacity = 5;
    [Min(30f)] public float speechTokenRegenerationSeconds = 180f;

    [Header("Attention")]
    [Range(0f, 1f)] public float eyeWeight = 0.85f;
    [Range(0f, 1f)] public float headWeight = 0.35f;
    [Range(0f, 1f)] public float bodyWeight = 0.08f;
    [Range(1f, 20f)] public float attentionSmoothness = 7f;
    [Range(0f, 1f)] public float headDelay = 0.18f;
    [Range(0f, 1.5f)] public float bodyDelay = 0.45f;

    [Header("Micro Motion")]
    public bool microMotionEnabled = true;
    [Range(0f, 1f)] public float breathingWeight = 0.22f;
    [Range(0f, 1f)] public float bodySwayWeight = 0.16f;
    [Range(2f, 12f)] public float microMotionPeriod = 4.8f;
    public bool suppressMicroMotionDuringTransitions = true;

    [Header("Blink and Idle")]
    [Min(1f)] public float blinkMinimumDelay = 2.8f;
    [Min(1f)] public float blinkMaximumDelay = 7.5f;
    [Range(0f, 0.5f)] public float doubleBlinkChance = 0.14f;
    [Min(8f)] public float minimumContextualIdleDuration = 18f;
    [Min(30f)] public float longIdlePoseSeconds = 210f;
    [Min(8f)] public float poseMinimumDuration = 24f;
    [Min(8f)] public float poseMaximumDuration = 48f;
}

public sealed class MaoInitiativeSystem
{
    private readonly MaoAutonomyRuntimeSettings settings;

    public MaoInitiativeSystem(MaoAutonomyRuntimeSettings settings)
    {
        this.settings = settings;
    }

    public MaoInitiativeDecision Evaluate(MaoInitiativeInput input)
    {
        MaoInitiativeDecision scored = MaoInitiativeLogic.Evaluate(input);
        if (input.ConversationBusy)
            return new MaoInitiativeDecision(MaoInitiativeChannel.None, scored.PhysicalScore, scored.SpeechScore, scored.VisionScore, "conversation-busy");
        if (scored.PhysicalScore >= settings.physicalThreshold && input.SecondsSinceReaction >= settings.physicalCooldown)
            return new MaoInitiativeDecision(MaoInitiativeChannel.Physical, scored.PhysicalScore, scored.SpeechScore, scored.VisionScore, "curiosity-physical-first");
        if (input.ScreenChanged && scored.VisionScore >= settings.visionThreshold && input.SecondsSinceVision >= settings.visionCooldown)
            return new MaoInitiativeDecision(MaoInitiativeChannel.Vision, scored.PhysicalScore, scored.SpeechScore, scored.VisionScore, "novel-screen-context");
        if (scored.SpeechScore >= settings.speechThreshold && input.SecondsSinceSpeech >= settings.speechCooldown)
            return new MaoInitiativeDecision(MaoInitiativeChannel.Speech, scored.PhysicalScore, scored.SpeechScore, scored.VisionScore, "rare-contextual-speech");
        return new MaoInitiativeDecision(MaoInitiativeChannel.None, scored.PhysicalScore, scored.SpeechScore, scored.VisionScore, scored.Reason);
    }
}

public sealed class MaoCuriositySystem
{
    private readonly MaoCuriosityState state;

    public MaoCuriositySystem(float initialValue = 0.25f)
    {
        state = new MaoCuriosityState(initialValue);
    }

    public float Value => state.Value;

    public void Advance(float deltaSeconds, float novelty, bool contextChanged, bool screenChanged, float decayPerSecond)
    {
        state.Advance(deltaSeconds, novelty, contextChanged, screenChanged, decayPerSecond);
    }

    public void Satisfy(float amount)
    {
        state.Satisfy(amount);
    }
}

public sealed class MaoDesktopContextSnapshot
{
    public string ProcessName = string.Empty;
    public string ContextHash = string.Empty;
    public MaoDesktopActivity Activity;
    public float IdleSeconds;
    public bool UserIsActive;
    public bool ContextChanged;
    public bool ScreenChanged;
    public bool UserReturned;
    public float FocusProtection;
}

public sealed class MaoDesktopContextObserver
{
    private string previousContextHash = string.Empty;
    private float previousIdleSeconds;

    public MaoDesktopContextSnapshot Poll()
    {
        string processName = string.Empty;
        string title = string.Empty;
        ReadForegroundWindow(out processName, out title);
        float idleSeconds = ReadIdleSeconds();
        MaoDesktopActivity activity = idleSeconds >= 90f
            ? MaoDesktopActivity.Away
            : Classify(processName, title);
        string contextHash = StableHash(processName + "|" + title + "|" + activity);
        bool changed = previousContextHash.Length > 0 && previousContextHash != contextHash;
        bool returned = previousIdleSeconds >= 60f && idleSeconds <= 5f;
        previousContextHash = contextHash;
        previousIdleSeconds = idleSeconds;

        return new MaoDesktopContextSnapshot
        {
            ProcessName = NormalizeProcess(processName),
            ContextHash = contextHash,
            Activity = activity,
            IdleSeconds = idleSeconds,
            UserIsActive = idleSeconds < 8f,
            ContextChanged = changed,
            ScreenChanged = changed,
            UserReturned = returned,
            FocusProtection = FocusFor(activity, idleSeconds)
        };
    }

    private static MaoDesktopActivity Classify(string processName, string title)
    {
        string value = ((processName ?? string.Empty) + " " + (title ?? string.Empty)).ToLowerInvariant();
        if (ContainsAny(value, "code", "devenv", "rider", "visual studio", "unity", "powershell", "terminal", "cmd.exe"))
            return MaoDesktopActivity.Coding;
        if (ContainsAny(value, "youtube", "netflix", "vlc", "mpv", "spotify", "media player", "anime"))
            return MaoDesktopActivity.Video;
        if (ContainsAny(value, "steam", "minecraft", "valorant", "genshin", "game", "roblox", "eldenring", "javaw"))
            return MaoDesktopActivity.Gaming;
        if (ContainsAny(value, "chrome", "msedge", "firefox", "opera", "brave"))
            return MaoDesktopActivity.Browsing;
        return MaoDesktopActivity.General;
    }

    private static bool ContainsAny(string value, params string[] needles)
    {
        for (int index = 0; index < needles.Length; index++)
            if (value.Contains(needles[index])) return true;
        return false;
    }

    private static float FocusFor(MaoDesktopActivity activity, float idleSeconds)
    {
        if (idleSeconds >= 45f) return 0.08f;
        switch (activity)
        {
            case MaoDesktopActivity.Coding: return 0.90f;
            case MaoDesktopActivity.Gaming: return 0.88f;
            case MaoDesktopActivity.Video: return 0.78f;
            case MaoDesktopActivity.Browsing: return 0.58f;
            case MaoDesktopActivity.Away: return 0.05f;
            default: return 0.42f;
        }
    }

    private static string NormalizeProcess(string value)
    {
        value = (value ?? string.Empty).Trim().ToLowerInvariant();
        return value.Length <= 48 ? value : value.Substring(0, 48);
    }

    private static string StableHash(string value)
    {
        unchecked
        {
            uint hash = 2166136261;
            string normalized = (value ?? string.Empty).ToLowerInvariant();
            for (int index = 0; index < normalized.Length; index++)
            {
                hash ^= normalized[index];
                hash *= 16777619;
            }
            return hash.ToString("x8");
        }
    }

    private static float ReadIdleSeconds()
    {
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
        var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref info)) return 0f;
        uint now = unchecked((uint)Environment.TickCount);
        return (now - info.dwTime) / 1000f;
#else
        return 0f;
#endif
    }

    private static void ReadForegroundWindow(out string processName, out string title)
    {
        processName = string.Empty;
        title = string.Empty;
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
        IntPtr window = GetForegroundWindow();
        if (window == IntPtr.Zero) return;
        var buffer = new StringBuilder(256);
        GetWindowText(window, buffer, buffer.Capacity);
        title = buffer.ToString();
        GetWindowThreadProcessId(window, out uint processId);
        if (processId == 0) return;
        try
        {
            using (Process process = Process.GetProcessById((int)processId))
                processName = process.ProcessName;
        }
        catch
        {
            processName = string.Empty;
        }
#endif
    }

#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll")] private static extern bool GetLastInputInfo(ref LASTINPUTINFO input);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr window, StringBuilder text, int count);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
#endif
}

[DefaultExecutionOrder(300)]
[DisallowMultipleComponent]
public sealed class MaoAttentionSystem : MonoBehaviour
{
    private Animator animator;
    private UniversalBlendshapes blendshapes;
    private MaoAutonomyRuntimeSettings settings;
    private Transform head;
    private Transform spine;
    private Transform chest;
    private Transform leftEye;
    private Transform rightEye;
    private MaoAnimationCoordinator animationCoordinator;
    private MaoAnimationRiggingController rigging;
    private MaoAttentionTargetType targetType = MaoAttentionTargetType.User;
    private Vector2 targetScreen;
    private float targetPriority;
    private float targetInterest;
    private float targetStartedAt;
    private float targetExpiresAt;
    private float yaw;
    private float pitch;
    private Quaternion lastHeadAdd = Quaternion.identity;
    private Quaternion lastSpineAdd = Quaternion.identity;
    private Quaternion lastLeftEyeAdd = Quaternion.identity;
    private Quaternion lastRightEyeAdd = Quaternion.identity;
    private float nextBlinkAt;
    private float blinkStartedAt = -1f;
    private bool queueSecondBlink;
    private bool suppressDoubleOnce;
    private bool ownsBlink;
    private float savedBlink;

    public MaoAttentionTargetType CurrentTargetType => targetType;
    public float CurrentInterest => Time.unscaledTime < targetExpiresAt ? targetInterest : 0f;

    public void Bind(Animator value, UniversalBlendshapes expressions, MaoAutonomyRuntimeSettings runtimeSettings)
    {
        animator = value;
        animationCoordinator = animator != null
            ? animator.GetComponentInParent<MaoAnimationCoordinator>()
            : null;
        rigging = animator != null
            ? animator.GetComponent<MaoAnimationRiggingController>()
            : null;
        blendshapes = expressions;
        settings = runtimeSettings;
        if (animator != null && animator.isHuman)
        {
            head = animator.GetBoneTransform(HumanBodyBones.Head);
            spine = animator.GetBoneTransform(HumanBodyBones.Spine);
            chest = animator.GetBoneTransform(HumanBodyBones.Chest);
            leftEye = animator.GetBoneTransform(HumanBodyBones.LeftEye);
            rightEye = animator.GetBoneTransform(HumanBodyBones.RightEye);
        }
        ScheduleBlink(Time.unscaledTime);
    }

    public bool RequestTarget(
        MaoAttentionTargetType type,
        Vector2 screenPosition,
        float priority,
        float interest,
        float duration)
    {
        float now = Time.unscaledTime;
        bool expired = now >= targetExpiresAt;
        if (!expired && priority < targetPriority && interest <= targetInterest) return false;
        targetType = type;
        targetScreen = screenPosition;
        targetPriority = Mathf.Clamp01(priority);
        targetInterest = Mathf.Clamp01(interest);
        targetStartedAt = now;
        targetExpiresAt = now + Mathf.Max(0.15f, duration);
        return true;
    }

    private void Update()
    {
        UpdateBlink();
    }

    private void LateUpdate()
    {
        RemovePreviousAdditives();
        if (animator == null || settings == null || head == null) return;
        if (rigging == null)
            rigging = animator.GetComponent<MaoAnimationRiggingController>();

        float now = Time.unscaledTime;
        float active = now < targetExpiresAt ? targetInterest : 0f;
        Vector2 screen = targetType == MaoAttentionTargetType.Cursor ? (Vector2)Input.mousePosition : targetScreen;
        if (screen == Vector2.zero) screen = new Vector2(Screen.width * 0.5f, Screen.height * 0.55f);
        float normalizedX = Screen.width > 0 ? (screen.x / Screen.width - 0.5f) * 2f : 0f;
        float normalizedY = Screen.height > 0 ? (screen.y / Screen.height - 0.5f) * 2f : 0f;
        float targetYaw = Mathf.Clamp(normalizedX * 22f, -24f, 24f) * active;
        float targetPitch = Mathf.Clamp(normalizedY * 14f, -15f, 15f) * active;
        float smoothing = 1f - Mathf.Exp(-Mathf.Max(1f, settings.attentionSmoothness) * Time.unscaledDeltaTime);
        yaw = Mathf.Lerp(yaw, targetYaw, smoothing);
        pitch = Mathf.Lerp(pitch, targetPitch, smoothing);

        float age = now - targetStartedAt;
        float eyeAmount = settings.eyeWeight;
        float headAmount = age >= settings.headDelay ? settings.headWeight : 0f;
        float bodyAmount = age >= settings.bodyDelay ? settings.bodyWeight : 0f;
        float microPhase = now / Mathf.Max(2f, settings.microMotionPeriod) * Mathf.PI * 2f;
        bool microMotion = settings.microMotionEnabled && !IsExclusiveAnimationActive();
        if (rigging != null && rigging.IsReady)
        {
            rigging.SetLookTarget(screen, headAmount, IsExclusiveAnimationActive());
            headAmount = 0f;
        }
        float breath = microMotion ? Mathf.Sin(microPhase) * settings.breathingWeight : 0f;
        float sway = microMotion ? Mathf.Sin(microPhase * 0.53f + 0.7f) * settings.bodySwayWeight : 0f;
        if (rigging == null || !rigging.IsReady)
        {
            lastHeadAdd = Quaternion.Euler(-pitch * headAmount + breath * 0.35f, yaw * headAmount, sway * 0.25f);
            head.localRotation *= lastHeadAdd;
        }
        Transform bodyTarget = chest != null ? chest : spine;
        if (bodyTarget != null && (bodyAmount > 0f || microMotion))
        {
            lastSpineAdd = Quaternion.Euler(breath, yaw * bodyAmount + sway, -yaw * bodyAmount * 0.08f + sway * 0.65f);
            bodyTarget.localRotation *= lastSpineAdd;
        }
        if (leftEye != null)
        {
            lastLeftEyeAdd = Quaternion.Euler(-pitch * eyeAmount, yaw * eyeAmount, 0f);
            leftEye.localRotation *= lastLeftEyeAdd;
        }
        if (rightEye != null)
        {
            lastRightEyeAdd = Quaternion.Euler(-pitch * eyeAmount, yaw * eyeAmount, 0f);
            rightEye.localRotation *= lastRightEyeAdd;
        }
    }

    private void RemovePreviousAdditives()
    {
        if (head != null && lastHeadAdd != Quaternion.identity) head.localRotation *= Quaternion.Inverse(lastHeadAdd);
        Transform bodyTarget = chest != null ? chest : spine;
        if (bodyTarget != null && lastSpineAdd != Quaternion.identity) bodyTarget.localRotation *= Quaternion.Inverse(lastSpineAdd);
        if (leftEye != null && lastLeftEyeAdd != Quaternion.identity) leftEye.localRotation *= Quaternion.Inverse(lastLeftEyeAdd);
        if (rightEye != null && lastRightEyeAdd != Quaternion.identity) rightEye.localRotation *= Quaternion.Inverse(lastRightEyeAdd);
        lastHeadAdd = lastSpineAdd = lastLeftEyeAdd = lastRightEyeAdd = Quaternion.identity;
    }

    private bool IsExclusiveAnimationActive()
    {
        if (animator == null) return true;
        if (animationCoordinator != null && animationCoordinator.SuppressAdditiveMotion) return true;
        for (int layer = 0; layer < animator.layerCount; layer++)
        {
            if (settings.suppressMicroMotionDuringTransitions && animator.IsInTransition(layer)) return true;
            string layerName = animator.GetLayerName(layer) ?? string.Empty;
            AnimatorStateInfo current = animator.GetCurrentAnimatorStateInfo(layer);
            string key = layerName.ToLowerInvariant();
            if (key.Contains("mao gravity") || key.Contains("mao guitar") || key.Contains("mao idle actions") ||
                current.IsName("Sitting") || current.IsName("WindowSit") || current.IsName("Lying") ||
                current.IsName("Lie") || current.IsName("Mounted") || current.IsName("Riding") ||
                current.IsName("Sleeping"))
                return true;
        }

        AnimatorControllerParameter[] parameters = animator.parameters;
        for (int index = 0; index < parameters.Length; index++)
        {
            AnimatorControllerParameter parameter = parameters[index];
            if (parameter.type != AnimatorControllerParameterType.Bool || !animator.GetBool(parameter.nameHash)) continue;
            string name = parameter.name.ToLowerInvariant();
            if (name.Contains("sit") || name.Contains("lie") || name.Contains("sleep") || name.Contains("gravity") ||
                name.Contains("guitar") || name.Contains("dragging")) return true;
        }
        return false;
    }

    private void UpdateBlink()
    {
        if (blendshapes == null || settings == null) return;
        float now = Time.unscaledTime;
        if (blinkStartedAt < 0f && now >= nextBlinkAt)
        {
            blinkStartedAt = now;
            savedBlink = blendshapes.Blink;
            ownsBlink = true;
            queueSecondBlink = !suppressDoubleOnce && UnityEngine.Random.value < settings.doubleBlinkChance;
            suppressDoubleOnce = false;
        }
        if (blinkStartedAt < 0f) return;

        float phase = (now - blinkStartedAt) / 0.14f;
        if (phase < 1f)
        {
            blendshapes.Blink = Mathf.Max(savedBlink, Mathf.Sin(phase * Mathf.PI));
            return;
        }

        blendshapes.Blink = savedBlink;
        ownsBlink = false;
        blinkStartedAt = -1f;
        if (queueSecondBlink)
        {
            queueSecondBlink = false;
            suppressDoubleOnce = true;
            nextBlinkAt = now + 0.16f;
        }
        else ScheduleBlink(now);
    }

    private void ScheduleBlink(float now)
    {
        if (settings == null) return;
        float maximum = Mathf.Max(settings.blinkMinimumDelay, settings.blinkMaximumDelay);
        nextBlinkAt = now + UnityEngine.Random.Range(settings.blinkMinimumDelay, maximum);
    }

    private void OnDisable()
    {
        RemovePreviousAdditives();
        if (blendshapes != null && ownsBlink) blendshapes.Blink = savedBlink;
        ownsBlink = false;
    }
}
