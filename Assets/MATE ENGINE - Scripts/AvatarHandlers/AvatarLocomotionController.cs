using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using MaoIntegration;
using UnityEngine;

public enum MaoCursorFlightPhase
{
    Grounded,
    Flying,
    Returning
}

public sealed partial class AvatarLocomotionController : MonoBehaviour
{
    const string GroundLocomotionLayerName = "Locomotion Layer";
    const string GroundBlendState = "Mao Ground Blend";
    const string GroundTurnLeftState = "Mao Turn Left";
    const string GroundTurnRightState = "Mao Turn Right";
    const string GroundStrafeWalkLeftState = "Mao Strafe Walk Left";
    const string GroundStrafeWalkRightState = "Mao Strafe Walk Right";
    const string GroundStrafeLeftState = "Mao Strafe Left";
    const string GroundStrafeRightState = "Mao Strafe Right";
    const string GroundJumpState = "Mao Ground Jump";
    const string CursorFlightLayerName = "Mao Cursor Flight";
    const string MacaronFlightState = "Macaron Flight";
    [Header("Enable")]
    public bool EnableLocomotion = true;

    [Header("Animator (optional, will auto-find)")]
    public Animator Animator;

    [Header("Debug")]
    public bool DebugTriggerWalkNow = false;
    public bool EnableLocomotionDebug = false;

    [Header("Locomotion Timing")]
    [Range(0f, 60f)] public float Randomizer = 10f;
    [Range(10f, 4000f)] public float MinWalkCycle = 250f;
    [Range(10f, 4000f)] public float MaxWalkCycle = 550f;

    [Header("Window Movement")]
    [Range(0f, 10f)] public float WindowSpeed = 2f;

    [Header("Animator Wiring")]
    public string BaseLayerName = "Base Layer";
    public string BaseIdleStateName = "Idle";
    public string WalkLeftParam = "WalkLeft";
    public string WalkRightParam = "WalkRight";
    public string RunLeftParam = "RunLeft";
    public string RunRightParam = "RunRight";

    [Header("Optional")]
    public bool OnlyMoveWhenFocused = false;

    [Header("Cursor Follow")]
    [Min(40f)] public float CursorSideOffsetPixels = 140f;
    [Min(5f)] public float CursorStopDistancePixels = 35f;
    [Min(40f)] public float CursorMaximumSegmentPixels = 360f;
    [Range(0.05f, 1f)] public float CursorRetargetInterval = 0.15f;
    [Range(0f, 2f)] public float CursorFollowPauseMin = 0.22f;
    [Range(0f, 2f)] public float CursorFollowPauseMax = 0.65f;
    [Range(0f, 10f)] public float RunWindowSpeed = 4.2f;
    [Min(80f)] public float CursorRunDistancePixels = 240f;
    [Range(45f, 110f)] public float SideFacingAngleDegrees = 90f;
    [Range(90f, 1080f)] public float FacingTurnDegreesPerSecond = 540f;
    [Range(1f, 20f)] public float FacingAlignmentToleranceDegrees = 8f;

    [Header("Advanced Ground Follow")]
    public bool EnableAdvancedGroundFollow = true;
    [Min(0f)] public float StopEnterDistancePixels = 36f;
    [Min(0f)] public float StopExitDistancePixels = 58f;
    [Min(0f)] public float RunExitDistancePixels = 210f;
    [Min(0f)] public float RunEnterDistancePixels = 300f;
    [Min(10f)] public float AdvancedWalkSpeedPixelsPerSecond = 210f;
    [Min(10f)] public float AdvancedRunSpeedPixelsPerSecond = 430f;
    [Min(10f)] public float MovementAccelerationPixelsPerSecondSquared = 620f;
    [Min(10f)] public float MovementDecelerationPixelsPerSecondSquared = 920f;
    [Range(0.1f, 30f)] public float CursorTargetFilterSharpness = 9f;
    [Range(5f, 90f)] public float StrafeStartAngleDegrees = 32f;
    [Range(90f, 179f)] public float HardTurnAngleDegrees = 115f;
    [Range(0f, 2f)] public float PostInteractionResumeDelay = 0.38f;

    [Header("Advanced Ground Jump")]
    public bool EnableOccasionalGroundJump = true;
    [Min(1f)] public float GroundJumpCooldownMin = 8f;
    [Min(1f)] public float GroundJumpCooldownMax = 14f;
    [Min(0f)] public float GroundJumpMinimumDistancePixels = 340f;

    [Header("Cursor Flight")]
    public bool EnableCursorFlight = true;
    [Min(40f)] public float CursorFlightMinimumRisePixels = 180f;
    [Min(20f)] public float CursorFlightTaskbarLandingPixels = 110f;
    [Min(0f)] public float CursorFlightHeadOffsetPixels = 80f;
    [Min(10f)] public float CursorFlightSpeedPixelsPerSecond = 430f;
    [Min(10f)] public float CursorFlightLandingSpeedPixelsPerSecond = 560f;

    public bool FollowCursorEnabled { get; private set; }
    public bool PreferRunning { get; private set; }
    public MaoIntegration.MaoLocomotionGait CurrentCursorGait { get; private set; }
    public MaoCursorFlightPhase CurrentCursorFlightPhase { get; private set; }
    public MaoIntegration.MaoGroundLocomotionState CurrentGroundState { get; private set; }
    public MaoIntegration.MaoMovementAuthority CurrentMovementAuthority { get; private set; }
    public bool CursorFlightReady => Animator != null && _cursorFlightLayerIndex >= 0;
    public bool CursorFlightAnimationActive => CurrentCursorFlightPhase != MaoCursorFlightPhase.Grounded &&
                                               IsCursorFlightStateActive(StateForCursorFlightPhase());
    public bool MacaronFlightVisualRequested =>
        (_bubbleHandler != null && _bubbleHandler.ExternalActiveRequested) ||
        (_taskbarController != null && _taskbarController.ExternalMacaronFlightRequested);
    public float CurrentFacingYawDegrees => _visualFacing.CurrentYaw;
    internal bool IsTurningForMovement => _turningForMovement;
    internal bool FacingAgreesWithDirection(int direction) => VisualFacingAgreesWithMovement(direction);
    public bool AdaptiveMotionReady =>
        Animator != null &&
        HasAnimatorParameter(WalkLeftParam, AnimatorControllerParameterType.Bool) &&
        HasAnimatorParameter(WalkRightParam, AnimatorControllerParameterType.Bool) &&
        HasAnimatorParameter(RunLeftParam, AnimatorControllerParameterType.Bool) &&
        HasAnimatorParameter(RunRightParam, AnimatorControllerParameterType.Bool);
    public bool AdvancedGroundMotionReady =>
        Animator != null && _groundLocomotionLayerIndex >= 0 &&
        HasAnimatorParameter("MaoMoveSpeed", AnimatorControllerParameterType.Float) &&
        HasAnimatorParameter("MaoMoveX", AnimatorControllerParameterType.Float) &&
        HasAnimatorParameter("MaoTurn", AnimatorControllerParameterType.Float) &&
        HasAnimatorParameter("MaoStrafe", AnimatorControllerParameterType.Float) &&
        HasAnimatorParameter("MaoGrounded", AnimatorControllerParameterType.Bool) &&
        HasAnimatorParameter("MaoGroundJump", AnimatorControllerParameterType.Trigger);
    public bool AdvancedGroundAnimationActive => IsAdvancedGroundStateActive(CurrentGroundState);

    [Header("Avatar Bounds Based Blocking (Left/Right only)")]
    public bool UseAvatarBoundsBlocking = true;

    [Tooltip("Root transform whose child Renderers define the avatar bounds. If null, this GameObject is used.")]
    public Transform AvatarBoundsRoot;

    [Tooltip("Camera used to project avatar bounds into Unity pixels. If null, Camera.main is used.")]
    public Camera BoundsCamera;

    [Header("Blocking Box Fine Tuning (Unity pixels)")]
    [Min(0f)] public float BoundsInsetLeft = 0f;
    [Min(0f)] public float BoundsInsetRight = 0f;

    [Tooltip("If the avatar box is within this many Unity pixels to a screen edge, the next walk cycle is forced away from that edge.")]
    [Min(0f)] public float EdgeThresholdUnityPixels = 12f;

    [Header("Visual Debug (Game + Gizmos)")]
    public bool DrawBlockingDebug = false;
    [Range(0f, 1f)] public float DebugOverlayAlpha = 0.55f;

    const uint SWP_NOSIZE = 0x0001;
    const uint SWP_NOZORDER = 0x0004;
    const uint SWP_NOACTIVATE = 0x0010;

    const uint GW_OWNER = 4;

    const uint MONITOR_DEFAULTTONEAREST = 2;

    const int SM_XVIRTUALSCREEN = 76;
    const int SM_CXVIRTUALSCREEN = 78;

    [StructLayout(LayoutKind.Sequential)]
    struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll", SetLastError = true)] static extern int GetWindowTextLength(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);
    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)] static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    int _baseLayerIndex = 0;
    int _groundLocomotionLayerIndex = -1;
    int _cursorFlightLayerIndex = -1;
    IntPtr _hwnd = IntPtr.Zero;

    bool _walking;
    int _dir;
    float _remainingPixels;
    float _nextDecisionTime;
    float _pauseUntil;

    float _nextAnimatorResolveTime;
    float _nextCursorRetargetTime;
    float _cursorFollowReadyAt;
    float _movementSuspendedUntil;
    bool _cursorFollowStartedLogged;

    readonly MaoVisualFacingRuntime _visualFacing = new MaoVisualFacingRuntime();
    bool _turningForMovement;
    int _pendingDirection;
    float _lastTargetDistance;
    float _lastVelocityPixelsPerSecond;
    float _nextContradictionLogAt;
    int _groundWindowTop;
    float _cursorFlightPhaseStartedAt;
    float _cursorFlightDebugProbeUntil;
    Transform _headBone;
    float _rawCursorTargetX;
    float _filteredCursorTargetX;
    bool _cursorTargetReady;
    float _advancedSpeed;
    int _advancedMoveDirection;
    float _nextGroundJumpAt;
    float _groundJumpStartedAt;
    MaoIntegration.MaoGroundLocomotionState _lastPlayedGroundState =
        MaoIntegration.MaoGroundLocomotionState.InteractionLocked;
    string _lastPlayedGroundAnimatorState = string.Empty;
    bool _wasMovementLocked;
    bool _useExplicitTarget;
    float _explicitTargetX;
    bool _autonomousMode;
    AvatarAnimatorController _avatarAnimatorController;
    AvatarTaskbarController _taskbarController;
    AvatarBubbleHandler _bubbleHandler;
    AvatarPoseController _poseController;
    MikuGuitarController _guitarController;
    MikuIdleActionController _idleActionController;
    MikuDesktopGravityController _gravityController;
    MaoAnimationCoordinator _animationCoordinator;
    readonly HashSet<string> _externalMovementLocks = new HashSet<string>(StringComparer.Ordinal);

    Renderer[] _boundsRenderers;
    float _nextBoundsResolveTime;

    int _forcedNextDir;

    bool _wasBaseIdle;

    static readonly Vector3[] _boundsCorners = new Vector3[8];

    void OnEnable()
    {
        Application.runInBackground = true;
        ResolveAnimatorSmart(true);
        CacheWindowHandle();
        ResolveBoundsRenderersSmart(true);
        ScheduleNextDecision(true);
        ScheduleNextGroundJump();
        _wasBaseIdle = true;
    }

    void OnDisable()
    {
        _animationCoordinator?.SetPersistent(MaoAnimationOwner.Locomotion, false);
        _animationCoordinator?.SetPersistent(MaoAnimationOwner.CursorFlight, false);
        StopCursorFlight(true);
        StopWalking();
        StopAdvancedGroundAnimation(MaoIntegration.MaoGroundLocomotionState.Idle);
        ClearTaskbarVisualOverrides();
        RestoreFacingImmediately();
    }

    void LateUpdate()
    {
        if (Animator == null || !Animator.isActiveAndEnabled) return;
        EnsureFacingRoot();
        _visualFacing.Tick(Time.unscaledDeltaTime, FacingTurnDegreesPerSecond);
    }

    void Update()
    {
        if (_animationCoordinator == null && Animator != null)
            _animationCoordinator = Animator.GetComponentInParent<MaoAnimationCoordinator>();
#if !(UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN)
        return;
#else
        if (!EnableLocomotion)
        {
            StopCursorFlight(true);
            StopWalking();
            return;
        }

        ResolveAnimatorSmart(false);
        ResolveBoundsRenderersSmart(false);

        if (Animator == null) return;

        if (_hwnd == IntPtr.Zero) CacheWindowHandle();
        if (_hwnd == IntPtr.Zero) return;

        if (OnlyMoveWhenFocused)
        {
            IntPtr fg = GetForegroundWindow();
            if (fg != _hwnd)
            {
                if (_walking) StopWalking();
                return;
            }
        }

        if (DebugTriggerWalkNow)
        {
            DebugTriggerWalkNow = false;
            ForceStartWalk();
        }

        float t = Time.unscaledTime;

        if (FollowCursorEnabled && IsMovementInteractionLocked(out MaoIntegration.MaoGroundLocomotionState lockedState))
        {
            if (CurrentCursorFlightPhase != MaoCursorFlightPhase.Grounded) StopCursorFlight(false);
            StopWalking(false);
            StopAdvancedGroundAnimation(lockedState);
            CurrentMovementAuthority = MaoIntegration.MaoMovementAuthority.InteractionLock;
            _movementSuspendedUntil = Mathf.Max(_movementSuspendedUntil, t + PostInteractionResumeDelay);
            _wasMovementLocked = true;
            return;
        }
        if (_wasMovementLocked)
        {
            _wasMovementLocked = false;
            _movementSuspendedUntil = Mathf.Max(_movementSuspendedUntil, t + PostInteractionResumeDelay);
            _cursorTargetReady = false;
            CurrentMovementAuthority = _useExplicitTarget
                ? MaoIntegration.MaoMovementAuthority.UserCommand
                : MaoIntegration.MaoMovementAuthority.MouseFollow;
        }

        if (FollowCursorEnabled && CurrentCursorFlightPhase != MaoCursorFlightPhase.Grounded)
        {
            if (t < _cursorFlightDebugProbeUntil) return;
            UpdateCursorFlight(t);
            return;
        }

        if (t < _movementSuspendedUntil)
        {
            if (_walking) StopWalking();
            return;
        }

        bool baseIdle = IsBaseIdle();
        if (!MaoIntegration.MaoAdvancedLocomotionLogic.CanUpdateMovement(
                FollowCursorEnabled, false, baseIdle))
        {
            if (_walking) StopWalking();
            if (_wasBaseIdle)
            {
                _pauseUntil = t + UnityEngine.Random.Range(0.08f, 0.22f);
                _nextDecisionTime = t + UnityEngine.Random.Range(0.25f, 0.75f);
            }
            _wasBaseIdle = false;
            return;
        }

        if (!_wasBaseIdle)
        {
            _pauseUntil = t + UnityEngine.Random.Range(0.08f, 0.22f);
            _nextDecisionTime = Mathf.Max(_nextDecisionTime, t + UnityEngine.Random.Range(0.25f, 0.75f));
            _wasBaseIdle = true;
        }

        if (t < _pauseUntil)
            return;

        if (!_walking)
        {
            if (FollowCursorEnabled)
            {
                UpdateCursorFollow(t);
                return;
            }
            if (Randomizer <= 0f) return;

            if (t >= _nextDecisionTime)
                StartWalk(false);

            return;
        }

        if (FollowCursorEnabled) UpdateCursorFollow(t);
        else StepWalk();
#endif
    }

    void OnGUI()
    {
        if (EnableLocomotionDebug) DrawLocomotionDiagnostics();
        if (!DrawBlockingDebug || !UseAvatarBoundsBlocking || !EnableLocomotion) return;

        if (!TryGetBlockingInfo(out BlockingInfo bi))
            return;

        Color prev = GUI.color;
        GUI.color = new Color(1f, 1f, 1f, Mathf.Clamp01(DebugOverlayAlpha));

        float xL = bi.effectiveMinXUnity;
        float xR = bi.effectiveMaxXUnity;

        DrawVLine(xL, 0f, Screen.height, 2f);
        DrawVLine(xR, 0f, Screen.height, 2f);

        float thr = bi.edgeThresholdUnity;
        float xLT = Mathf.Clamp(thr, 0f, Screen.width);
        float xRT = Mathf.Clamp(Screen.width - thr, 0f, Screen.width);

        DrawVLine(xLT, 0f, Screen.height, 1f);
        DrawVLine(xRT, 0f, Screen.height, 1f);

        GUI.color = prev;
    }

    void DrawVLine(float x, float yTop, float height, float width)
    {
        Rect r = new Rect(x - (width * 0.5f), yTop, width, height);
        GUI.DrawTexture(r, Texture2D.whiteTexture);
    }

    void DrawLocomotionDiagnostics()
    {
        Camera cam = BoundsCamera != null ? BoundsCamera : Camera.main;
        Vector3 visualForward = GetVisualForwardWorld();
        float visualScreenX = cam != null ? Vector3.Dot(visualForward, cam.transform.right) : 0f;
        string state = _turningForMovement
            ? "Turn"
            : Time.unscaledTime < _movementSuspendedUntil
                ? "Paused"
                : CurrentCursorGait.ToString();
        string text =
            "Miku locomotion: " + state + "\n" +
            "movementDirection: " + _dir + "\n" +
            "facingDirection: " + FacingDirectionFromScreenX(visualScreenX) + "\n" +
            "visualForward: " + visualForward.ToString("F2") + "\n" +
            "velocity: " + _lastVelocityPixelsPerSecond.ToString("0.0") + " px/s\n" +
            "deltaToTarget: " + _lastTargetDistance.ToString("0.0") + " px\n" +
            "yaw: " + _visualFacing.CurrentYaw.ToString("0.0") +
            " -> " + _visualFacing.DesiredYaw.ToString("0.0") + "\n" +
            "rootMotion: " + (Animator != null && Animator.applyRootMotion);

        Color previous = GUI.color;
        GUI.color = new Color(1f, 1f, 1f, 0.9f);
        GUI.Box(new Rect(12f, 12f, 310f, 166f), text);
        GUI.color = previous;
    }

    void OnDrawGizmos()
    {
        if (!DrawBlockingDebug) return;
        if (!UseAvatarBoundsBlocking) return;

        Camera cam = BoundsCamera != null ? BoundsCamera : Camera.main;
        if (cam == null) return;

        if (!TryGetBlockingInfo(out BlockingInfo bi))
            return;

        float z = cam.nearClipPlane + 0.05f;

        Vector3 p0 = cam.ScreenToWorldPoint(new Vector3(bi.effectiveMinXUnity, 0f, z));
        Vector3 p1 = cam.ScreenToWorldPoint(new Vector3(bi.effectiveMinXUnity, Screen.height, z));
        Vector3 p2 = cam.ScreenToWorldPoint(new Vector3(bi.effectiveMaxXUnity, 0f, z));
        Vector3 p3 = cam.ScreenToWorldPoint(new Vector3(bi.effectiveMaxXUnity, Screen.height, z));

        Gizmos.color = new Color(1f, 1f, 1f, 0.75f);
        Gizmos.DrawLine(p0, p1);
        Gizmos.DrawLine(p2, p3);

        float thr = bi.edgeThresholdUnity;
        Vector3 t0 = cam.ScreenToWorldPoint(new Vector3(thr, 0f, z));
        Vector3 t1 = cam.ScreenToWorldPoint(new Vector3(thr, Screen.height, z));
        Vector3 t2 = cam.ScreenToWorldPoint(new Vector3(Screen.width - thr, 0f, z));
        Vector3 t3 = cam.ScreenToWorldPoint(new Vector3(Screen.width - thr, Screen.height, z));

        Gizmos.color = new Color(1f, 1f, 1f, 0.35f);
        Gizmos.DrawLine(t0, t1);
        Gizmos.DrawLine(t2, t3);
    }

    public void SetAnimator(Animator a)
    {
        _animationCoordinator?.SetPersistent(MaoAnimationOwner.Locomotion, false);
        _animationCoordinator?.SetPersistent(MaoAnimationOwner.CursorFlight, false);
        if (Animator != a)
        {
            StopCursorFlight(false);
            RestoreFacingImmediately();
        }
        Animator = a;
        _animationCoordinator = Animator != null
            ? Animator.GetComponentInParent<MaoAnimationCoordinator>()
            : null;
        if (Animator != null) Animator.applyRootMotion = false;
        RefreshLayerIndex();
        _headBone = Animator != null && Animator.isHuman
            ? Animator.GetBoneTransform(HumanBodyBones.Head)
            : null;
        CacheMovementBlockers();
        EnsureFacingRoot();
    }

    public void SetCursorFollowEnabled(bool enabled)
    {
        SetCursorFollowEnabled(enabled, false);
    }

    public void SetCursorFollowEnabled(bool enabled, bool preferRunning)
    {
        _animationCoordinator?.SetPersistent(MaoAnimationOwner.Locomotion, enabled);
        _animationCoordinator?.SetPersistent(MaoAnimationOwner.CursorFlight, false);
        if (!enabled) StopCursorFlight(true);
        FollowCursorEnabled = enabled;
        PreferRunning = enabled && preferRunning;
        EnableLocomotion = enabled;
        StopWalking();
        _pauseUntil = 0f;
        _nextCursorRetargetTime = 0f;
        _cursorFollowStartedLogged = false;
        _cursorFollowReadyAt = Time.unscaledTime + (enabled ? RandomFollowPause() : 0f);
        _cursorTargetReady = false;
        _advancedSpeed = 0f;
        _advancedMoveDirection = 0;
        _useExplicitTarget = false;
        ScheduleNextGroundJump();
        CurrentCursorGait = MaoIntegration.MaoLocomotionGait.Idle;
        CurrentGroundState = MaoIntegration.MaoGroundLocomotionState.Idle;
        CurrentMovementAuthority = enabled
            ? MaoIntegration.MaoMovementAuthority.MouseFollow
            : MaoIntegration.MaoMovementAuthority.Idle;
        SyncTaskbarVisualOverrides();
        string mode = !enabled ? "stopped" : "cursor_adaptive";
        UnityEngine.Debug.Log("[Mao Movement] mode=" + mode);
    }

    public void SetTarget(Vector2 desktopTarget)
    {
        SetCursorFollowEnabled(true, false);
        _useExplicitTarget = true;
        _explicitTargetX = desktopTarget.x;
        _cursorTargetReady = false;
        CurrentMovementAuthority = MaoIntegration.MaoMovementAuthority.UserCommand;
    }

    public void ClearTarget()
    {
        _useExplicitTarget = false;
        _cursorTargetReady = false;
        CurrentMovementAuthority = FollowCursorEnabled
            ? MaoIntegration.MaoMovementAuthority.MouseFollow
            : MaoIntegration.MaoMovementAuthority.Idle;
    }

    public void SetFollowMouse(bool enabled)
    {
        SetCursorFollowEnabled(enabled, PreferRunning);
    }

    public void SuspendMovement(string reason)
    {
        if (!string.IsNullOrWhiteSpace(reason)) _externalMovementLocks.Add(reason);
        _movementSuspendedUntil = Mathf.Max(
            _movementSuspendedUntil,
            Time.unscaledTime + PostInteractionResumeDelay);
    }

    public void ResumeMovement(string reason)
    {
        if (!string.IsNullOrWhiteSpace(reason)) _externalMovementLocks.Remove(reason);
        _movementSuspendedUntil = Mathf.Max(
            _movementSuspendedUntil,
            Time.unscaledTime + PostInteractionResumeDelay);
    }

    public bool Jump()
    {
        if (!AdvancedGroundMotionReady || !FollowCursorEnabled ||
            CurrentCursorFlightPhase != MaoCursorFlightPhase.Grounded ||
            IsMovementInteractionLocked(out _))
            return false;
        StartGroundJump(Time.unscaledTime);
        return true;
    }

    public void Stop()
    {
        _animationCoordinator?.SetPersistent(MaoAnimationOwner.Locomotion, false);
        _animationCoordinator?.SetPersistent(MaoAnimationOwner.CursorFlight, false);
        SetCursorFollowEnabled(false, false);
        _autonomousMode = false;
        EnableLocomotion = false;
    }

    public void SetAutonomousMode(bool enabled)
    {
        _autonomousMode = enabled;
        if (enabled)
        {
            SetCursorFollowEnabled(false, false);
            EnableLocomotion = true;
            CurrentMovementAuthority = MaoIntegration.MaoMovementAuthority.Autonomy;
            ScheduleNextDecision(true);
        }
        else if (!FollowCursorEnabled)
        {
            CurrentMovementAuthority = MaoIntegration.MaoMovementAuthority.Idle;
        }
    }

    void UpdateCursorFollow(float now)
    {
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
        if (EnableAdvancedGroundFollow && AdvancedGroundMotionReady)
        {
            UpdateAdvancedCursorFollow(now);
            return;
        }
        if (now < _nextCursorRetargetTime)
        {
            if (_walking) StepWalk();
            return;
        }
        _nextCursorRetargetTime = now + Mathf.Max(0.05f, CursorRetargetInterval);
        if (!GetCursorPos(out POINT cursor) || !TryGetBlockingInfo(out BlockingInfo blocking))
        {
            if (_walking) StepWalk();
            return;
        }

        float avatarCenterX = (blocking.effectiveMinGlobalX + blocking.effectiveMaxGlobalX) * 0.5f;
        if (TryStartCursorFlight(cursor, avatarCenterX, now)) return;
        MaoIntegration.MaoCursorFollowDecision decision = MaoIntegration.MaoCursorFollowLogic.Decide(
            avatarCenterX,
            cursor.X,
            CursorSideOffsetPixels,
            CursorStopDistancePixels,
            CursorMaximumSegmentPixels);
        if (decision.Direction == 0)
        {
            if (_walking) StopWalking();
            _cursorFollowReadyAt = Mathf.Max(_cursorFollowReadyAt, now + RandomFollowPause());
            return;
        }
        if (!_walking && now < _cursorFollowReadyAt) return;

        MaoIntegration.MaoAdaptiveLocomotionDecision locomotionDecision =
            MaoIntegration.MaoAdaptiveLocomotionLogic.Decide(
                decision.Direction,
                decision.TargetDistance,
                CursorRunDistancePixels,
                _visualFacing.RightYaw,
                _visualFacing.LeftYaw);
        _lastTargetDistance = decision.TargetDistance;

        bool gaitChanged = CurrentCursorGait != locomotionDecision.Gait;
        if (!_walking || _dir != decision.Direction || gaitChanged)
        {
            bool movementStarted = BeginAdaptiveMovement(
                locomotionDecision,
                decision.Direction,
                decision.TargetDistance,
                true);
            if (!_cursorFollowStartedLogged)
            {
                _cursorFollowStartedLogged = true;
                UnityEngine.Debug.Log("[Mao Movement] event=cursor_follow_adaptive_started");
            }
            if (!movementStarted) return;
        }
        _remainingPixels = Mathf.Max(1f, decision.Distance);
        StepWalk();
#endif
    }

    bool TryStartCursorFlight(POINT cursor, float avatarCenterX, float now)
    {
        if (!EnableCursorFlight || !CursorFlightReady ||
            Animator.GetComponent<MikuDesktopGravityController>()?.GravityEnabled == true ||
            !TryGetAvatarHeadDesktop(out Vector2 head) ||
            !TryGetMonitorWorkArea(out _, out int taskbarTop) ||
            !GetWindowRect(_hwnd, out RECT windowRect))
            return false;

        MaoIntegration.MaoCursorFlightDecision decision = MaoIntegration.MaoCursorFlightLogic.Decide(
            false,
            head.y,
            cursor.Y,
            taskbarTop,
            CursorFlightMinimumRisePixels,
            CursorFlightTaskbarLandingPixels);
        if (decision.Intent != MaoIntegration.MaoCursorFlightIntent.Fly) return false;

        StopWalking(false);
        _groundWindowTop = windowRect.Top;
        CurrentCursorFlightPhase = MaoCursorFlightPhase.Flying;
        _animationCoordinator?.SetPersistent(MaoAnimationOwner.CursorFlight, true);
        _cursorFlightPhaseStartedAt = now;
        _visualFacing.FaceDirection(0);
        SyncTaskbarVisualOverrides();
        PlayCursorFlightState(MacaronFlightState, 0.12f);
        UnityEngine.Debug.Log("[Mao Macaron Flight] event=started horizontal=" +
                              Mathf.Abs(cursor.X - avatarCenterX).ToString("0") +
                              " rise=" + (head.y - cursor.Y).ToString("0"));
        return true;
    }

    void UpdateCursorFlight(float now)
    {
        if (!GetCursorPos(out POINT cursor) ||
            !TryGetAvatarHeadDesktop(out Vector2 head) ||
            !TryGetMonitorWorkArea(out _, out int taskbarTop))
        {
            StartCursorLanding(now);
            return;
        }

        MaoIntegration.MaoCursorFlightDecision decision = MaoIntegration.MaoCursorFlightLogic.Decide(
            true,
            head.y,
            cursor.Y,
            taskbarTop,
            CursorFlightMinimumRisePixels,
            CursorFlightTaskbarLandingPixels);
        if (decision.Intent == MaoIntegration.MaoCursorFlightIntent.Land &&
            CurrentCursorFlightPhase != MaoCursorFlightPhase.Returning)
            StartCursorLanding(now);

        switch (CurrentCursorFlightPhase)
        {
            case MaoCursorFlightPhase.Flying:
                StepCursorFlight(cursor, head, CursorFlightSpeedPixelsPerSecond);
                break;
            case MaoCursorFlightPhase.Returning:
                StepCursorLanding(now);
                break;
        }
    }

    void StepCursorFlight(POINT cursor, Vector2 head, float speed)
    {
        if (!GetWindowRect(_hwnd, out RECT windowRect)) return;
        MaoIntegration.MaoCursorFlightPosition next = MaoIntegration.MaoCursorFlightLogic.StepWindowPosition(
            windowRect.Left,
            windowRect.Top,
            head.x,
            head.y,
            cursor.X,
            cursor.Y,
            0f,
            CursorFlightHeadOffsetPixels,
            _groundWindowTop,
            speed,
            Time.unscaledDeltaTime);
        int roundedLeft = Mathf.RoundToInt(next.Left);
        int roundedTop = Mathf.RoundToInt(next.Top);
        if (roundedLeft != windowRect.Left || roundedTop != windowRect.Top)
            SetWindowPos(_hwnd, IntPtr.Zero, roundedLeft, roundedTop, 0, 0,
                SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
    }

    void StartCursorLanding(float now)
    {
        if (CurrentCursorFlightPhase == MaoCursorFlightPhase.Grounded ||
            CurrentCursorFlightPhase == MaoCursorFlightPhase.Returning)
            return;
        CurrentCursorFlightPhase = MaoCursorFlightPhase.Returning;
        _animationCoordinator?.SetPersistent(MaoAnimationOwner.CursorFlight, true);
        _cursorFlightPhaseStartedAt = now;
        UnityEngine.Debug.Log("[Mao Macaron Flight] event=returning");
    }

    void StepCursorLanding(float now)
    {
        if (!GetCursorPos(out POINT cursor) || !TryGetAvatarHeadDesktop(out Vector2 head)) return;
        StepCursorFlight(cursor, head, CursorFlightLandingSpeedPixelsPerSecond);
        if (!GetWindowRect(_hwnd, out RECT windowRect) || Mathf.Abs(windowRect.Top - _groundWindowTop) > 2) return;

        StopCursorFlight(false);
        _cursorFollowReadyAt = now + RandomFollowPause();
        UnityEngine.Debug.Log("[Mao Macaron Flight] event=grounded");
    }

    void StopCursorFlight(bool restoreGround)
    {
        if (CurrentCursorFlightPhase == MaoCursorFlightPhase.Grounded) return;
        if (restoreGround && _hwnd != IntPtr.Zero && GetWindowRect(_hwnd, out RECT windowRect))
            SetWindowPos(_hwnd, IntPtr.Zero, windowRect.Left, _groundWindowTop, 0, 0,
                SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
        if (Animator != null && _cursorFlightLayerIndex >= 0)
            Animator.CrossFadeInFixedTime(CursorFlightLayerName + ".Idle", 0.12f, _cursorFlightLayerIndex, 0f);
        CurrentCursorFlightPhase = MaoCursorFlightPhase.Grounded;
        _animationCoordinator?.SetPersistent(MaoAnimationOwner.CursorFlight, false);
        _cursorFlightPhaseStartedAt = 0f;
        _cursorFlightDebugProbeUntil = 0f;
        SyncTaskbarVisualOverrides();
    }

    void PlayCursorFlightState(string stateName, float transitionSeconds)
    {
        if (!CursorFlightReady) return;
        Animator.SetLayerWeight(_cursorFlightLayerIndex, 1f);
        Animator.CrossFadeInFixedTime(
            CursorFlightLayerName + "." + stateName,
            transitionSeconds,
            _cursorFlightLayerIndex,
            0f);
    }

    bool IsCursorFlightStateActive(string stateName)
    {
        if (!CursorFlightReady) return false;
        AnimatorStateInfo current = Animator.GetCurrentAnimatorStateInfo(_cursorFlightLayerIndex);
        AnimatorStateInfo next = Animator.GetNextAnimatorStateInfo(_cursorFlightLayerIndex);
        string fullName = CursorFlightLayerName + "." + stateName;
        return current.IsName(fullName) || current.IsName(stateName) ||
               (Animator.IsInTransition(_cursorFlightLayerIndex) &&
                (next.IsName(fullName) || next.IsName(stateName)));
    }

    string StateForCursorFlightPhase()
    {
        return CurrentCursorFlightPhase == MaoCursorFlightPhase.Grounded ? "Idle" : MacaronFlightState;
    }

    bool TryGetAvatarHeadDesktop(out Vector2 desktop)
    {
        desktop = default;
        Camera camera = BoundsCamera != null ? BoundsCamera : Camera.main;
        if (_headBone == null || camera == null || _hwnd == IntPtr.Zero ||
            !GetClientRect(_hwnd, out RECT clientRect))
            return false;
        POINT clientOrigin = default;
        if (!ClientToScreen(_hwnd, ref clientOrigin)) return false;
        Vector3 screen = camera.WorldToScreenPoint(_headBone.position);
        if (screen.z <= 0.001f) return false;
        float widthScale = (clientRect.Right - clientRect.Left) / Mathf.Max(1f, camera.pixelWidth);
        float heightScale = (clientRect.Bottom - clientRect.Top) / Mathf.Max(1f, camera.pixelHeight);
        desktop.x = clientOrigin.X + screen.x * widthScale;
        desktop.y = clientOrigin.Y + (camera.pixelHeight - screen.y) * heightScale;
        return true;
    }

    bool TryGetMonitorWorkArea(out int top, out int bottom)
    {
        top = bottom = 0;
        IntPtr monitor = MonitorFromWindow(_hwnd, MONITOR_DEFAULTTONEAREST);
        if (monitor == IntPtr.Zero) return false;
        MONITORINFO info = new MONITORINFO { cbSize = Marshal.SizeOf(typeof(MONITORINFO)) };
        if (!GetMonitorInfo(monitor, ref info)) return false;
        top = info.rcWork.Top;
        bottom = info.rcWork.Bottom;
        return bottom > top;
    }

    bool BeginAdaptiveMovement(
        MaoIntegration.MaoAdaptiveLocomotionDecision decision,
        int direction,
        float targetDistance,
        bool moveWindow)
    {
        StopWalking(false);
        _pendingDirection = direction;
        _visualFacing.FaceDirection(direction);
        _lastTargetDistance = targetDistance;
        if (!_visualFacing.IsAligned(FacingAlignmentToleranceDegrees))
        {
            _turningForMovement = true;
            if (EnableLocomotionDebug)
                UnityEngine.Debug.Log("[Mao Movement] state=turn direction=" +
                                      (direction < 0 ? "left" : "right") +
                                      " current_yaw=" + _visualFacing.CurrentYaw.ToString("0.0") +
                                      " target_yaw=" + _visualFacing.DesiredYaw.ToString("0.0"));
            return false;
        }

        _turningForMovement = false;
        _pendingDirection = 0;
        _dir = direction;
        _walking = moveWindow;
        CurrentCursorGait = decision.Gait;
        SetMovementAnimatorParameters(CurrentCursorGait, _dir);
        UnityEngine.Debug.Log("[Mao Movement] gait=" + CurrentCursorGait.ToString().ToLowerInvariant() +
                              " direction=" + (_dir < 0 ? "left" : "right") +
                              " distance=" + targetDistance.ToString("0") +
                              " facing_yaw=" + _visualFacing.DesiredYaw.ToString("0.0"));
        return true;
    }

    internal bool BeginAdaptiveMotionSmokeProbe(int direction, float targetDistance)
    {
        if (!AdaptiveMotionReady || direction == 0) return false;
        StopCursorFlight(true);
        EnableLocomotionDebug = true;
        EnsureFacingRoot();
        MaoIntegration.MaoAdaptiveLocomotionDecision decision =
            MaoIntegration.MaoAdaptiveLocomotionLogic.Decide(
                direction,
                targetDistance,
                CursorRunDistancePixels,
                _visualFacing.RightYaw,
                _visualFacing.LeftYaw);
        _visualFacing.FaceDirection(direction);
        _visualFacing.Tick(1f, 1080f);
        if (!BeginAdaptiveMovement(decision, direction, targetDistance, false)) return false;
        _movementSuspendedUntil = Time.unscaledTime + 0.75f;
        UnityEngine.Debug.Log("[Mao Movement] event=adaptive_smoke_probe");
        return true;
    }

    internal bool BeginAdvancedGroundMotionSmokeProbe(int direction, bool running)
    {
        if (!AdvancedGroundMotionReady || direction == 0) return false;
        StopCursorFlight(true);
        EnsureFacingRoot();
        _visualFacing.FaceDirection(direction);
        _visualFacing.Tick(1f, 1080f);
        _advancedMoveDirection = direction;
        _dir = direction;
        _advancedSpeed = running
            ? AdvancedRunSpeedPixelsPerSecond
            : AdvancedWalkSpeedPixelsPerSecond;
        CurrentCursorGait = running
            ? MaoIntegration.MaoLocomotionGait.Run
            : MaoIntegration.MaoLocomotionGait.Walk;
        CurrentGroundState = running
            ? MaoIntegration.MaoGroundLocomotionState.Running
            : MaoIntegration.MaoGroundLocomotionState.Walking;
        _lastPlayedGroundState = MaoIntegration.MaoGroundLocomotionState.InteractionLocked;
        _lastPlayedGroundAnimatorState = string.Empty;
        PlayAdvancedGroundState(CurrentGroundState, direction);
        _movementSuspendedUntil = Time.unscaledTime + 0.75f;
        UnityEngine.Debug.Log("[Mao Movement] event=advanced_ground_smoke_probe state=" + CurrentGroundState);
        return true;
    }

    internal bool BeginCursorFlightSmokeProbe(MaoCursorFlightPhase phase)
    {
        if (!CursorFlightReady || phase == MaoCursorFlightPhase.Grounded) return false;
        StopWalking(false);
        CurrentCursorFlightPhase = phase;
        _animationCoordinator?.SetPersistent(MaoAnimationOwner.CursorFlight, true);
        _cursorFlightPhaseStartedAt = Time.unscaledTime;
        _cursorFlightDebugProbeUntil = Time.unscaledTime + 1f;
        SyncTaskbarVisualOverrides();
        PlayCursorFlightState(StateForCursorFlightPhase(), 0.04f);
        return true;
    }

    internal void EndCursorFlightSmokeProbe()
    {
        _cursorFlightDebugProbeUntil = 0f;
        StopCursorFlight(false);
    }

    bool IsAdvancedGroundStateActive(MaoIntegration.MaoGroundLocomotionState state)
    {
        if (!AdvancedGroundMotionReady) return false;
        string stateName = GroundBlendState;
        switch (state)
        {
            case MaoIntegration.MaoGroundLocomotionState.TurningLeft: stateName = GroundTurnLeftState; break;
            case MaoIntegration.MaoGroundLocomotionState.TurningRight: stateName = GroundTurnRightState; break;
            case MaoIntegration.MaoGroundLocomotionState.StrafeWalkLeft: stateName = GroundStrafeWalkLeftState; break;
            case MaoIntegration.MaoGroundLocomotionState.StrafeWalkRight: stateName = GroundStrafeWalkRightState; break;
            case MaoIntegration.MaoGroundLocomotionState.StrafeLeft: stateName = GroundStrafeLeftState; break;
            case MaoIntegration.MaoGroundLocomotionState.StrafeRight: stateName = GroundStrafeRightState; break;
            case MaoIntegration.MaoGroundLocomotionState.Jumping: stateName = GroundJumpState; break;
        }
        AnimatorStateInfo current = Animator.GetCurrentAnimatorStateInfo(_groundLocomotionLayerIndex);
        AnimatorStateInfo next = Animator.GetNextAnimatorStateInfo(_groundLocomotionLayerIndex);
        string fullName = GroundLocomotionLayerName + "." + stateName;
        return current.IsName(fullName) || current.IsName(stateName) ||
               (Animator.IsInTransition(_groundLocomotionLayerIndex) &&
                (next.IsName(fullName) || next.IsName(stateName)));
    }

    float RandomFollowPause()
    {
        float minimum = Mathf.Max(0f, CursorFollowPauseMin);
        return UnityEngine.Random.Range(minimum, Mathf.Max(minimum, CursorFollowPauseMax));
    }

    void ResolveAnimatorSmart(bool immediate)
    {
        float t = Time.unscaledTime;
        if (!immediate && t < _nextAnimatorResolveTime) return;
        _nextAnimatorResolveTime = t + 0.75f;

        if (Animator != null && Animator.isActiveAndEnabled) return;

        Animator found = null;

        found = GetComponent<Animator>();
        if (found != null && found.isActiveAndEnabled)
        {
            SetAnimator(found);
            return;
        }

        var controller = FindFirstObjectByType<AvatarAnimatorController>();
        if (controller != null && controller.animator != null && controller.animator.isActiveAndEnabled)
        {
            SetAnimator(controller.animator);
            return;
        }

        var voice = FindFirstObjectByType<PetVoiceReactionHandler>();
        if (voice != null && voice.avatarAnimator != null && voice.avatarAnimator.isActiveAndEnabled)
        {
            SetAnimator(voice.avatarAnimator);
            return;
        }

        var bubble = FindFirstObjectByType<AvatarBubbleHandler>();
        if (bubble != null && bubble.avatarAnimator != null && bubble.avatarAnimator.isActiveAndEnabled)
        {
            SetAnimator(bubble.avatarAnimator);
            return;
        }

        var all = Resources.FindObjectsOfTypeAll<Animator>();
        for (int i = 0; i < all.Length; i++)
        {
            var a = all[i];
            if (a == null) continue;
            if (!a.isActiveAndEnabled) continue;
            if (!a.gameObject.activeInHierarchy) continue;
            if (a.runtimeAnimatorController == null) continue;
            found = a;
            break;
        }

        SetAnimator(found);
    }

    void ResolveBoundsRenderersSmart(bool immediate)
    {
        float t = Time.unscaledTime;
        if (!immediate && t < _nextBoundsResolveTime) return;
        _nextBoundsResolveTime = t + 1.0f;

        Transform root = AvatarBoundsRoot != null ? AvatarBoundsRoot : transform;
        if (root == null)
        {
            _boundsRenderers = null;
            return;
        }

        _boundsRenderers = root.GetComponentsInChildren<Renderer>(true);
    }

    void RefreshLayerIndex()
    {
        if (Animator == null) return;
        int idx = Animator.GetLayerIndex(BaseLayerName);
        _baseLayerIndex = idx >= 0 ? idx : 0;
        _groundLocomotionLayerIndex = Animator.GetLayerIndex(GroundLocomotionLayerName);
        _cursorFlightLayerIndex = Animator.GetLayerIndex(CursorFlightLayerName);
        if (_groundLocomotionLayerIndex >= 0) Animator.SetLayerWeight(_groundLocomotionLayerIndex, 1f);
        if (_cursorFlightLayerIndex >= 0) Animator.SetLayerWeight(_cursorFlightLayerIndex, 1f);
    }

    bool IsBaseIdle()
    {
        AnimatorStateInfo s = Animator.GetCurrentAnimatorStateInfo(_baseLayerIndex);
        return s.IsName(BaseIdleStateName);
    }

    void ForceStartWalk()
    {
        StopWalking();
        _pauseUntil = 0f;
        _nextDecisionTime = 0f;
        StartWalk(true);
    }

    void StartWalk(bool forced)
    {
        float min = Mathf.Max(0f, MinWalkCycle);
        float max = Mathf.Max(min, MaxWalkCycle);
        if (max <= 0.01f)
        {
            StopWalking();
            ScheduleNextDecision(false);
            return;
        }

        int chosenDir = _turningForMovement && _pendingDirection != 0
            ? _pendingDirection
            : 0;

        if (chosenDir == 0 && _forcedNextDir != 0)
        {
            chosenDir = _forcedNextDir;
            _forcedNextDir = 0;
        }
        else if (chosenDir == 0)
        {
            chosenDir = PickDirectionByEdges();
        }

        if (chosenDir == 0)
            chosenDir = UnityEngine.Random.value < 0.5f ? -1 : 1;

        _visualFacing.FaceDirection(chosenDir);
        if (!_visualFacing.IsAligned(FacingAlignmentToleranceDegrees))
        {
            _turningForMovement = true;
            _pendingDirection = chosenDir;
            return;
        }

        _turningForMovement = false;
        _pendingDirection = 0;
        _dir = chosenDir;
        _remainingPixels = UnityEngine.Random.Range(min, max);
        _walking = true;
        CurrentCursorGait = MaoIntegration.MaoLocomotionGait.Walk;
        SetMovementAnimatorParameters(CurrentCursorGait, _dir);
    }

    int PickDirectionByEdges()
    {
        if (!UseAvatarBoundsBlocking) return 0;

        if (!TryGetBlockingInfo(out BlockingInfo bi))
            return 0;

        float leftDist = bi.effectiveMinGlobalX - bi.monitorLeft;
        float rightDist = bi.monitorRight - bi.effectiveMaxGlobalX;

        if (leftDist <= bi.edgeThresholdGlobal && rightDist <= bi.edgeThresholdGlobal)
        {
            if (leftDist < rightDist) return 1;
            if (rightDist < leftDist) return -1;
            return UnityEngine.Random.value < 0.5f ? -1 : 1;
        }

        if (rightDist <= bi.edgeThresholdGlobal) return -1;
        if (leftDist <= bi.edgeThresholdGlobal) return 1;

        return 0;
    }

    void StepWalk()
    {
        if (!_walking) return;
        if (!_visualFacing.IsAligned(FacingAlignmentToleranceDegrees))
        {
            StopWalking(false);
            _turningForMovement = true;
            return;
        }
        if (!VisualFacingAgreesWithMovement(_dir))
        {
            ReportFacingContradiction(_dir);
            StopWalking(false);
            _turningForMovement = true;
            return;
        }

        if (!GetWindowRect(_hwnd, out RECT r))
        {
            StopWalking();
            ScheduleNextDecision(false);
            return;
        }

        int w = r.Right - r.Left;

        if (!TryGetMonitorBounds(_hwnd, out int monitorLeft, out int monitorRight))
        {
            int vx = GetSystemMetrics(SM_XVIRTUALSCREEN);
            int vw = GetSystemMetrics(SM_CXVIRTUALSCREEN);
            monitorLeft = vx;
            monitorRight = vx + vw;
        }

        int minX;
        int maxX;

        if (UseAvatarBoundsBlocking && TryGetBlockingInfo(out BlockingInfo bi))
        {
            minX = bi.minWindowX;
            maxX = bi.maxWindowX;
        }
        else
        {
            int minWinX = monitorLeft;
            int maxWinX = monitorRight - w;
            if (maxWinX < minWinX) maxWinX = minWinX;
            minX = minWinX;
            maxX = maxWinX;
        }

        float configuredSpeed = CurrentCursorGait == MaoIntegration.MaoLocomotionGait.Run
            ? RunWindowSpeed
            : WindowSpeed;
        float speedPxPerSecond = Mathf.Max(0f, configuredSpeed) * 100f;
        if (speedPxPerSecond <= 0.01f)
        {
            EndWalk();
            return;
        }

        float step = speedPxPerSecond * Time.unscaledDeltaTime;
        float move = Mathf.Min(step, _remainingPixels);

        int targetX = r.Left + Mathf.RoundToInt(move * _dir);
        int clampedX = Mathf.Clamp(targetX, minX, maxX);

        int actualMoved = Mathf.Abs(clampedX - r.Left);
        _remainingPixels -= actualMoved;
        _lastVelocityPixelsPerSecond = Time.unscaledDeltaTime > 0.0001f
            ? (actualMoved * _dir) / Time.unscaledDeltaTime
            : 0f;

        int yKeep = r.Top;

        if (!SetWindowPos(_hwnd, IntPtr.Zero, clampedX, yKeep, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE))
        {
            StopWalking();
            ScheduleNextDecision(false);
            return;
        }

        ValidateFacingInvariant();

        if (actualMoved <= 0)
        {
            _remainingPixels = 0f;
            _forcedNextDir = -_dir;
        }

        if (_remainingPixels <= 0.01f)
            EndWalk();
    }

    void EndWalk()
    {
        int finishedDirection = _dir;
        StopWalking(!FollowCursorEnabled);
        if (FollowCursorEnabled)
        {
            _visualFacing.FaceDirection(finishedDirection);
            _cursorFollowReadyAt = Time.unscaledTime + RandomFollowPause();
        }
        else
        {
            _pauseUntil = Time.unscaledTime + UnityEngine.Random.Range(0.4f, 1.2f);
            ScheduleNextDecision(false);
        }
    }

    void StopWalking(bool faceForward = true)
    {
        if (Animator != null)
        {
            SetBoolIfPresent(WalkLeftParam, false);
            SetBoolIfPresent(WalkRightParam, false);
            SetBoolIfPresent(RunLeftParam, false);
            SetBoolIfPresent(RunRightParam, false);
        }

        _walking = false;
        _advancedSpeed = 0f;
        _advancedMoveDirection = 0;
        _lastVelocityPixelsPerSecond = 0f;
        _remainingPixels = 0f;
        _dir = 0;
        _pendingDirection = 0;
        CurrentCursorGait = MaoIntegration.MaoLocomotionGait.Idle;
        if (CurrentGroundState != MaoIntegration.MaoGroundLocomotionState.Dragged &&
            CurrentGroundState != MaoIntegration.MaoGroundLocomotionState.InteractionLocked)
            CurrentGroundState = MaoIntegration.MaoGroundLocomotionState.Idle;
        if (faceForward)
        {
            _turningForMovement = false;
            _visualFacing.FaceDirection(0);
        }
    }

    void SetMovementAnimatorParameters(MaoIntegration.MaoLocomotionGait gait, int direction)
    {
        bool walking = gait == MaoIntegration.MaoLocomotionGait.Walk;
        bool running = gait == MaoIntegration.MaoLocomotionGait.Run;
        SetBoolIfPresent(WalkLeftParam, walking && direction < 0);
        SetBoolIfPresent(WalkRightParam, walking && direction > 0);
        SetBoolIfPresent(RunLeftParam, running && direction < 0);
        SetBoolIfPresent(RunRightParam, running && direction > 0);
    }

    void SetBoolIfPresent(string parameterName, bool value)
    {
        if (HasAnimatorParameter(parameterName, AnimatorControllerParameterType.Bool))
            Animator.SetBool(parameterName, value);
    }

    void SetFloatIfPresent(string parameterName, float value)
    {
        if (HasAnimatorParameter(parameterName, AnimatorControllerParameterType.Float))
            Animator.SetFloat(parameterName, value);
    }

    void StopAdvancedGroundAnimation(MaoIntegration.MaoGroundLocomotionState stoppedState)
    {
        CurrentGroundState = stoppedState;
        _advancedSpeed = 0f;
        _advancedMoveDirection = 0;
        if (Animator == null || _groundLocomotionLayerIndex < 0) return;
        SetFloatIfPresent("MaoMoveSpeed", 0f);
        SetFloatIfPresent("MaoMoveX", 0f);
        SetFloatIfPresent("MaoTurn", 0f);
        SetFloatIfPresent("MaoStrafe", 0f);
        SetBoolIfPresent("MaoGrounded", true);
        Animator.CrossFadeInFixedTime(
            GroundLocomotionLayerName + ".Idle",
            0.1f,
            _groundLocomotionLayerIndex,
            0f);
        _lastPlayedGroundState = MaoIntegration.MaoGroundLocomotionState.InteractionLocked;
        _lastPlayedGroundAnimatorState = string.Empty;
    }

    void CacheMovementBlockers()
    {
        if (Animator == null)
        {
            _avatarAnimatorController = null;
            _taskbarController = null;
            _bubbleHandler = null;
            _poseController = null;
            _guitarController = null;
            _idleActionController = null;
            _gravityController = null;
            return;
        }
        _avatarAnimatorController = Animator.GetComponentInParent<AvatarAnimatorController>();
        _taskbarController = ResolveTaskbarController();
        _bubbleHandler = ResolveBubbleHandler();
        _poseController = Animator.GetComponentInParent<AvatarPoseController>();
        _guitarController = Animator.GetComponent<MikuGuitarController>();
        _idleActionController = Animator.GetComponent<MikuIdleActionController>();
        _gravityController = Animator.GetComponent<MikuDesktopGravityController>();
    }

    void SyncTaskbarVisualOverrides()
    {
        if (_taskbarController == null && Animator != null)
            _taskbarController = ResolveTaskbarController();
        if (_bubbleHandler == null && Animator != null)
            _bubbleHandler = ResolveBubbleHandler();
        bool flying = CurrentCursorFlightPhase != MaoCursorFlightPhase.Grounded;
        if (_taskbarController != null)
        {
            _taskbarController.ExternalMacaronFlightRequested = flying;
            _taskbarController.ExternalGroundMovementRequested = FollowCursorEnabled && !flying;
        }
        if (_bubbleHandler != null) _bubbleHandler.ExternalActiveRequested = flying;
    }

    void ClearTaskbarVisualOverrides()
    {
        if (_taskbarController != null)
        {
            _taskbarController.ExternalMacaronFlightRequested = false;
            _taskbarController.ExternalGroundMovementRequested = false;
        }
        if (_bubbleHandler != null) _bubbleHandler.ExternalActiveRequested = false;
    }

    AvatarTaskbarController ResolveTaskbarController()
    {
        if (Animator == null) return null;
        AvatarTaskbarController parent = Animator.GetComponentInParent<AvatarTaskbarController>();
        if (parent != null) return parent;
        AvatarTaskbarController[] controllers = FindObjectsByType<AvatarTaskbarController>(
            FindObjectsInactive.Include, FindObjectsSortMode.None);
        for (int index = 0; index < controllers.Length; index++)
        {
            if (controllers[index] != null && controllers[index].avatarAnimator == Animator)
                return controllers[index];
        }
        for (int index = 0; index < controllers.Length; index++)
        {
            AvatarTaskbarController controller = controllers[index];
            if (controller == null || !controller.gameObject.activeInHierarchy) continue;
            if (controller.avatarAnimator != null && controller.avatarAnimator.gameObject.activeInHierarchy) continue;
            controller.SetAnimator(Animator);
            return controller;
        }
        return null;
    }

    AvatarBubbleHandler ResolveBubbleHandler()
    {
        if (Animator == null) return null;
        AvatarBubbleHandler parent = Animator.GetComponentInParent<AvatarBubbleHandler>();
        if (parent != null) return parent;
        for (int index = 0; index < AvatarBubbleHandler.ActiveHandlers.Count; index++)
        {
            AvatarBubbleHandler handler = AvatarBubbleHandler.ActiveHandlers[index];
            if (handler != null && handler.avatarAnimator == Animator) return handler;
        }
        for (int index = 0; index < AvatarBubbleHandler.ActiveHandlers.Count; index++)
        {
            AvatarBubbleHandler handler = AvatarBubbleHandler.ActiveHandlers[index];
            if (handler == null) continue;
            if (handler.avatarAnimator != null && handler.avatarAnimator.gameObject.activeInHierarchy) continue;
            handler.SetAnimator(Animator);
            return handler;
        }
        return null;
    }

    bool IsMovementInteractionLocked(out MaoIntegration.MaoGroundLocomotionState lockedState)
    {
        if (_avatarAnimatorController == null && Animator != null) CacheMovementBlockers();
        if (_avatarAnimatorController != null && _avatarAnimatorController.isDragging)
        {
            lockedState = MaoIntegration.MaoGroundLocomotionState.Dragged;
            return true;
        }
        bool interactionLocked = _externalMovementLocks.Count > 0 ||
                                 MenuActions.IsMovementBlocked() ||
                                 (_poseController != null && _poseController.IsPoseActive) ||
                                 (_guitarController != null && _guitarController.IsPlayingGuitar) ||
                                 (_idleActionController != null && _idleActionController.IsPlaying) ||
                                 (_gravityController != null && _gravityController.IsBusy);
        lockedState = interactionLocked
            ? MaoIntegration.MaoGroundLocomotionState.InteractionLocked
            : MaoIntegration.MaoGroundLocomotionState.Idle;
        return interactionLocked;
    }

    void EnsureFacingRoot()
    {
        _visualFacing.Bind(
            Animator,
            BoundsCamera != null ? BoundsCamera : Camera.main,
            SideFacingAngleDegrees,
            EnableLocomotionDebug);
    }

    void RestoreFacingImmediately()
    {
        _visualFacing.ResetRotation();
    }

    Vector3 GetVisualForwardWorld()
    {
        return _visualFacing.TryGetObservedVisualForwardWorld(out Vector3 observed)
            ? observed
            : _visualFacing.PredictedVisualForwardWorld();
    }

    bool VisualFacingAgreesWithMovement(int movementDirection)
    {
        Camera cam = BoundsCamera != null ? BoundsCamera : Camera.main;
        if (cam == null || movementDirection == 0) return true;
        float screenX = Vector3.Dot(GetVisualForwardWorld(), cam.transform.right);
        return MaoIntegration.MaoFacingLogic.DirectionsAgree(movementDirection, screenX, 0.15f);
    }

    void ValidateFacingInvariant()
    {
        if (_dir == 0 || Mathf.Abs(_lastVelocityPixelsPerSecond) < 1f) return;
        if (!VisualFacingAgreesWithMovement(_dir)) ReportFacingContradiction(_dir);
    }

    void ReportFacingContradiction(int movementDirection)
    {
        if (Time.unscaledTime < _nextContradictionLogAt) return;
        _nextContradictionLogAt = Time.unscaledTime + 1f;
        Camera cam = BoundsCamera != null ? BoundsCamera : Camera.main;
        Vector3 visualForward = GetVisualForwardWorld();
        float screenX = cam != null ? Vector3.Dot(visualForward, cam.transform.right) : 0f;
        UnityEngine.Debug.LogError(
            "[Mao Locomotion Contradiction] movementDirection=" + movementDirection +
            " facingDirection=" + FacingDirectionFromScreenX(screenX) +
            " visualForward=" + visualForward.ToString("F3") +
            " velocity=" + _lastVelocityPixelsPerSecond.ToString("0.0") +
            " deltaToTarget=" + _lastTargetDistance.ToString("0.0") +
            " state=" + CurrentCursorGait);
    }

    static int FacingDirectionFromScreenX(float screenX)
    {
        if (screenX > 0.15f) return 1;
        if (screenX < -0.15f) return -1;
        return 0;
    }

    bool HasAnimatorParameter(string parameterName, AnimatorControllerParameterType expectedType)
    {
        if (Animator == null || string.IsNullOrWhiteSpace(parameterName)) return false;
        AnimatorControllerParameter[] parameters = Animator.parameters;
        for (int index = 0; index < parameters.Length; index++)
            if (parameters[index].name == parameterName && parameters[index].type == expectedType) return true;
        return false;
    }

    void ScheduleNextDecision(bool immediate)
    {
        float t = Time.unscaledTime;

        if (immediate)
        {
            _nextDecisionTime = t + UnityEngine.Random.Range(0.2f, 0.8f);
            return;
        }

        float baseDelay = Mathf.Max(0.1f, Randomizer);
        _nextDecisionTime = t + UnityEngine.Random.Range(baseDelay, baseDelay * 2f);
    }

    void CacheWindowHandle()
    {
#if !(UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN)
        _hwnd = IntPtr.Zero;
#else
        uint pid = (uint)Process.GetCurrentProcess().Id;
        IntPtr best = IntPtr.Zero;
        long bestArea = -1;

        EnumWindows((hWnd, lParam) =>
        {
            if (!IsWindowVisible(hWnd)) return true;
            if (GetWindow(hWnd, GW_OWNER) != IntPtr.Zero) return true;

            GetWindowThreadProcessId(hWnd, out uint wp);
            if (wp != pid) return true;

            int len = GetWindowTextLength(hWnd);
            if (len <= 0) return true;

            if (!GetWindowRect(hWnd, out RECT rr)) return true;

            long area = (long)(rr.Right - rr.Left) * (long)(rr.Bottom - rr.Top);
            if (area > bestArea)
            {
                bestArea = area;
                best = hWnd;
            }

            return true;
        }, IntPtr.Zero);

        _hwnd = best;
#endif
    }

    bool TryGetMonitorBounds(IntPtr hwnd, out int left, out int right)
    {
        left = 0;
        right = 0;

        IntPtr mon = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        if (mon == IntPtr.Zero) return false;

        MONITORINFO mi = new MONITORINFO();
        mi.cbSize = Marshal.SizeOf(typeof(MONITORINFO));

        if (!GetMonitorInfo(mon, ref mi))
            return false;

        left = mi.rcMonitor.Left;
        right = mi.rcMonitor.Right;
        return true;
    }

    struct BlockingInfo
    {
        public int monitorLeft;
        public int monitorRight;
        public float effectiveMinXUnity;
        public float effectiveMaxXUnity;
        public int effectiveMinGlobalX;
        public int effectiveMaxGlobalX;
        public float edgeThresholdUnity;
        public int edgeThresholdGlobal;
        public int minWindowX;
        public int maxWindowX;
    }

    bool TryGetBlockingInfo(out BlockingInfo bi)
    {
        bi = default;

        if (_hwnd == IntPtr.Zero) return false;

        Camera cam = BoundsCamera != null ? BoundsCamera : Camera.main;
        if (cam == null) return false;

        if (!GetWindowRect(_hwnd, out RECT winRect))
            return false;

        if (!GetClientRect(_hwnd, out RECT clientRect))
            return false;

        POINT pt = new POINT { X = 0, Y = 0 };
        if (!ClientToScreen(_hwnd, ref pt))
            return false;

        int clientX = pt.X;

        int clientW = clientRect.Right - clientRect.Left;
        if (clientW <= 0) return false;
        if (Screen.width <= 0 || Screen.height <= 0) return false;

        float scaleX = clientW / (float)Screen.width;

        if (!TryGetMonitorBounds(_hwnd, out int monitorLeft, out int monitorRight))
        {
            int vx = GetSystemMetrics(SM_XVIRTUALSCREEN);
            int vw = GetSystemMetrics(SM_CXVIRTUALSCREEN);
            monitorLeft = vx;
            monitorRight = vx + vw;
        }

        if (!TryGetAvatarScreenBoundsUnity(cam, out float minXU, out float maxXU))
        {
            minXU = 0f;
            maxXU = Screen.width;
        }

        float effMinXU = Mathf.Clamp(minXU + BoundsInsetLeft, 0f, Screen.width);
        float effMaxXU = Mathf.Clamp(maxXU - BoundsInsetRight, 0f, Screen.width);

        if (effMaxXU < effMinXU)
        {
            float mid = (effMinXU + effMaxXU) * 0.5f;
            effMinXU = mid;
            effMaxXU = mid;
        }

        int borderLeft = clientX - winRect.Left;

        int effMinGlobalX = clientX + Mathf.RoundToInt(effMinXU * scaleX);
        int effMaxGlobalX = clientX + Mathf.RoundToInt(effMaxXU * scaleX);

        int thrGlobal = Mathf.RoundToInt(EdgeThresholdUnityPixels * scaleX);

        int minWindowX = monitorLeft - borderLeft - Mathf.RoundToInt(effMinXU * scaleX);
        int maxWindowX = monitorRight - borderLeft - Mathf.RoundToInt(effMaxXU * scaleX);

        if (maxWindowX < minWindowX) maxWindowX = minWindowX;

        bi.monitorLeft = monitorLeft;
        bi.monitorRight = monitorRight;
        bi.effectiveMinXUnity = effMinXU;
        bi.effectiveMaxXUnity = effMaxXU;
        bi.effectiveMinGlobalX = effMinGlobalX;
        bi.effectiveMaxGlobalX = effMaxGlobalX;
        bi.edgeThresholdUnity = EdgeThresholdUnityPixels;
        bi.edgeThresholdGlobal = Mathf.Max(0, thrGlobal);
        bi.minWindowX = minWindowX;
        bi.maxWindowX = maxWindowX;

        return true;
    }

    bool TryGetAvatarScreenBoundsUnity(Camera cam, out float minX, out float maxX)
    {
        minX = float.PositiveInfinity;
        maxX = float.NegativeInfinity;

        if (_boundsRenderers == null || _boundsRenderers.Length == 0)
            return false;

        bool any = false;

        for (int i = 0; i < _boundsRenderers.Length; i++)
        {
            Renderer rr = _boundsRenderers[i];
            if (rr == null || !MaoIntegration.MaoAdvancedLocomotionLogic.ShouldIncludeRenderer(
                    rr.enabled, rr.gameObject.activeInHierarchy)) continue;

            Bounds b = rr.bounds;
            Vector3 c = b.center;
            Vector3 e = b.extents;

            _boundsCorners[0] = new Vector3(c.x - e.x, c.y - e.y, c.z - e.z);
            _boundsCorners[1] = new Vector3(c.x - e.x, c.y - e.y, c.z + e.z);
            _boundsCorners[2] = new Vector3(c.x - e.x, c.y + e.y, c.z - e.z);
            _boundsCorners[3] = new Vector3(c.x - e.x, c.y + e.y, c.z + e.z);
            _boundsCorners[4] = new Vector3(c.x + e.x, c.y - e.y, c.z - e.z);
            _boundsCorners[5] = new Vector3(c.x + e.x, c.y - e.y, c.z + e.z);
            _boundsCorners[6] = new Vector3(c.x + e.x, c.y + e.y, c.z - e.z);
            _boundsCorners[7] = new Vector3(c.x + e.x, c.y + e.y, c.z + e.z);

            for (int k = 0; k < 8; k++)
            {
                Vector3 sp = cam.WorldToScreenPoint(_boundsCorners[k]);
                if (sp.z <= 0.0001f) continue;

                any = true;
                if (sp.x < minX) minX = sp.x;
                if (sp.x > maxX) maxX = sp.x;
            }
        }

        if (!any) return false;

        minX = Mathf.Clamp(minX, 0f, Screen.width);
        maxX = Mathf.Clamp(maxX, 0f, Screen.width);

        if (maxX < minX)
        {
            float mid = (minX + maxX) * 0.5f;
            minX = mid;
            maxX = mid;
        }

        return true;
    }
}
