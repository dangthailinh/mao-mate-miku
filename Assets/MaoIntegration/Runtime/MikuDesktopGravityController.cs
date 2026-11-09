using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using MaoIntegration;
using UnityEngine;

public enum MikuGravityPhase
{
    Disabled,
    Supported,
    Dragging,
    Falling,
    Impact,
    StandingUp,
    HardLanding
}

[DisallowMultipleComponent]
public sealed class MikuDesktopGravityController : MonoBehaviour
{
    private const string LayerName = "Mao Gravity";
    private const string FallingState = "Falling";
    private const string ImpactState = "Falling Flat Impact";
    private const string StandingState = "Standing Up";
    private const string HardLandingState = "Hard Landing";
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint MonitorDefaultToNearest = 2;
    private const int DwmwaCloaked = 14;
    private const int GwlExStyle = -20;
    private const long WsExTransparent = 0x00000020L;

    [SerializeField] private float accelerationPixelsPerSecondSquared = 1800f;
    [SerializeField] private float maximumFallSpeedPixelsPerSecond = 2200f;
    [SerializeField] private float minimumDragPixels = 4f;
    [SerializeField] private float humanoidSurfacePaddingPixels = 8f;
    [SerializeField] private float fullFallMinimumDistancePixels = 320f;
    [SerializeField] private float taskbarSitSnapDistancePixels = 48f;
    [SerializeField] private int taskbarSitPoseCount = 4;
    [SerializeField] private float taskbarSeatSettleSeconds = 0.65f;

    private readonly List<MaoGravitySurface> surfaces = new List<MaoGravitySurface>(64);
    private readonly List<float> humanoidContactYs = new List<float>(12);
    private readonly StringBuilder textBuffer = new StringBuilder(256);
    private Animator animator;
    private AvatarAnimatorController avatarController;
    private AvatarWindowHandler windowHandler;
    private AvatarTaskbarController taskbarController;
    private MaoAnimationCoordinator animationCoordinator;
    private Renderer[] renderers;
    private Transform[] humanoidContactBones;
    private Transform[] taskbarContactBones;
    private Transform leftFootBone;
    private Transform rightFootBone;
    private Camera targetCamera;
    private IntPtr unityWindow;
    private IntPtr supportWindow;
    private int layerIndex = -1;
    private bool gravityEnabled;
    private bool wasDragging;
    private bool dragMoved;
    private bool hasSupport;
    private bool overridesApplied;
    private bool savedDancingEnabled;
    private bool savedDraggingOverride;
    private Vector2 dragStartCursor;
    private float contactOffsetX;
    private float contactOffsetY;
    private float supportTop;
    private float fallVelocity;
    private float phaseStartedAt;
    private MikuGravityPhase phase = MikuGravityPhase.Disabled;
    private bool usingHumanoidContact;
    private bool taskbarSitting;
    private bool landingFootIkApplied;
    private int taskbarSitPoseIndex = -1;
    private float taskbarSeatSettleUntil;
    private float taskbarSeatErrorPixels = float.PositiveInfinity;
    private bool taskbarSeatAlignmentLogged;
    private bool debugPhysicalDragSuppressed;
    private Quaternion landingLeftFootRotation;
    private Quaternion landingRightFootRotation;
    private MaoGravityLandingMode landingMode = MaoGravityLandingMode.HardLanding;

    public bool IsReady => animator != null && animator.isHuman && layerIndex >= 0 && unityWindow != IntPtr.Zero;
    public bool GravityEnabled => gravityEnabled;
    public bool IsBusy => phase == MikuGravityPhase.Falling || phase == MikuGravityPhase.Impact ||
                          phase == MikuGravityPhase.StandingUp || phase == MikuGravityPhase.HardLanding;
    public MikuGravityPhase Phase => phase;
    public MaoGravityLandingMode LandingMode => landingMode;
    public bool TaskbarSitting => taskbarSitting && animator != null &&
                                  animator.GetBool("isWindowSit") && animator.GetBool("isTaskbarSit");
    public int TaskbarSitPoseIndex => taskbarSitting ? taskbarSitPoseIndex : -1;
    public bool TaskbarSitAnimationActive => taskbarSitting && IsBaseStateActive("WindowSit");
    public bool LandingFootIkApplied => landingFootIkApplied;
    public float TaskbarSnapErrorPixels
    {
        get
        {
            return taskbarSitting ? taskbarSeatErrorPixels : float.PositiveInfinity;
        }
    }
    public float SurfaceErrorPixels
    {
        get
        {
            if (!hasSupport || !TryGetWindowRect(unityWindow, out RectI own)) return float.PositiveInfinity;
            return Mathf.Abs(own.Top + contactOffsetY - ResolveSupportTop());
        }
    }
    public bool IsAnimationActive => IsStateActive(StateForPhase(phase));

    public void Bind(Animator target)
    {
        if (animator == target && IsReady) return;
        bool reenable = gravityEnabled;
        DisableRuntime();
        animator = target;
        avatarController = animator != null ? animator.GetComponentInParent<AvatarAnimatorController>() : null;
        animationCoordinator = animator != null ? animator.GetComponentInParent<MaoAnimationCoordinator>() : null;
        windowHandler = animator != null ? animator.GetComponentInParent<AvatarWindowHandler>() : null;
        taskbarController = animator != null ? animator.GetComponentInParent<AvatarTaskbarController>() : null;
        renderers = animator != null ? animator.GetComponentsInChildren<Renderer>(true) : null;
        CacheHumanoidContactBones();
        targetCamera = Camera.main;
        layerIndex = animator != null ? animator.GetLayerIndex(LayerName) : -1;
        unityWindow = ResolveUnityWindow();
        if (animator != null)
        {
            animator.applyRootMotion = false;
            if (layerIndex >= 0) animator.SetLayerWeight(layerIndex, 1f);
        }
        if (reenable) SetEnabled(true);
    }

    public bool SetEnabled(bool enabled)
    {
        if (enabled && !IsReady) return false;
        gravityEnabled = enabled;
        if (windowHandler != null) windowHandler.ExternalSitBlocked = enabled;
        if (!enabled)
        {
            DisableRuntime();
            phase = MikuGravityPhase.Disabled;
        }
        else if (phase == MikuGravityPhase.Disabled)
        {
            phase = MikuGravityPhase.Supported;
            hasSupport = false;
            CaptureContactOffsets();
        }
        SyncAnimationOwnership();
        UnityEngine.Debug.Log("[Mao Gravity] event=" + (enabled ? "enabled" : "disabled"));
        return true;
    }

    public bool Toggle()
    {
        bool target = !gravityEnabled;
        return SetEnabled(target) && gravityEnabled == target;
    }

    public bool DebugLiftAndDrop(int liftPixels)
    {
        if (!gravityEnabled || !IsReady || !TryGetWindowRect(unityWindow, out RectI own)) return false;
        SetUnityWindowPosition(own.Left, own.Top - Mathf.Max(80, liftPixels));
        supportWindow = IntPtr.Zero;
        BeginFall(true);
        return phase == MikuGravityPhase.Falling || phase == MikuGravityPhase.Impact;
    }

    internal bool DebugInterruptRecoveryForDrag()
    {
        if (!gravityEnabled || (phase != MikuGravityPhase.Impact && phase != MikuGravityPhase.StandingUp &&
                                phase != MikuGravityPhase.HardLanding))
            return false;
        BeginDragging();
        return phase == MikuGravityPhase.Dragging;
    }

    internal bool DebugReleaseNearTaskbar(int offsetPixels)
    {
        if (!gravityEnabled || !IsReady || !TryGetWindowRect(unityWindow, out RectI own) ||
            !TryGetTaskbarContactDesktop(out Vector2 taskbarContact)) return false;
        float taskbarTop = GetWorkAreaBottomAt(taskbarContact.x, taskbarContact.y);
        int nextTop = own.Top + Mathf.RoundToInt(taskbarTop + offsetPixels - taskbarContact.y);
        SetUnityWindowPosition(own.Left, nextTop);
        phase = MikuGravityPhase.Dragging;
        dragMoved = true;
        CompleteDragRelease();
        return taskbarSitting;
    }

    internal void DebugSetPhysicalDragSuppressed(bool suppressed)
    {
        debugPhysicalDragSuppressed = suppressed;
        wasDragging = false;
    }

    private void Update()
    {
#if !(UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN)
        return;
#else
        if (!gravityEnabled || !IsReady) return;
        bool dragging = !debugPhysicalDragSuppressed && avatarController != null && avatarController.isDragging;
        if (dragging)
        {
            if (!wasDragging) BeginDragging();
            TrackDragDistance();
        }
        else if (wasDragging)
        {
            CompleteDragRelease();
        }
        wasDragging = dragging;

        switch (phase)
        {
            case MikuGravityPhase.Supported: FollowSupport(); break;
            case MikuGravityPhase.Falling: StepFall(); break;
            case MikuGravityPhase.Impact: StepImpact(); break;
            case MikuGravityPhase.StandingUp: StepStandingUp(); break;
            case MikuGravityPhase.HardLanding: StepHardLanding(); break;
        }
        SyncAnimationOwnership();
#endif
    }

    private void LateUpdate()
    {
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
        if (gravityEnabled && taskbarSitting)
        {
            if (Time.unscaledTime <= taskbarSeatSettleUntil || !taskbarSeatAlignmentLogged)
                AlignTaskbarSeat();
            return;
        }
        if (!gravityEnabled || !hasSupport) return;
        if (phase != MikuGravityPhase.Impact && phase != MikuGravityPhase.StandingUp &&
            phase != MikuGravityPhase.HardLanding) return;
        if (CaptureContactOffsets()) AlignToSupport();
#endif
    }

    private void OnAnimatorIK(int animatorLayerIndex)
    {
        if (animator == null || animatorLayerIndex != layerIndex || phase != MikuGravityPhase.HardLanding ||
            leftFootBone == null || rightFootBone == null) return;
        animator.SetIKRotationWeight(AvatarIKGoal.LeftFoot, 1f);
        animator.SetIKRotationWeight(AvatarIKGoal.RightFoot, 1f);
        animator.SetIKRotation(AvatarIKGoal.LeftFoot, landingLeftFootRotation);
        animator.SetIKRotation(AvatarIKGoal.RightFoot, landingRightFootRotation);
        landingFootIkApplied = true;
    }

    private void BeginDragging()
    {
        if (animationCoordinator != null && !animationCoordinator.TrySetPersistent(MaoAnimationOwner.Gravity, true))
            return;
        bool interruptedRecovery = phase == MikuGravityPhase.Impact || phase == MikuGravityPhase.StandingUp ||
                                   phase == MikuGravityPhase.HardLanding;
        StopAnimationLayer();
        RestoreOverrides();
        supportWindow = IntPtr.Zero;
        hasSupport = false;
        ClearTaskbarSitting();
        phase = MikuGravityPhase.Dragging;
        dragMoved = false;
        dragStartCursor = Input.mousePosition;
        if (interruptedRecovery)
            UnityEngine.Debug.Log("[Mao Gravity] event=recovery_cancelled_by_drag");
    }

    private void TrackDragDistance()
    {
        if (!dragMoved && Vector2.Distance(dragStartCursor, Input.mousePosition) >= minimumDragPixels)
            dragMoved = true;
    }

    private void CompleteDragRelease()
    {
        if (!dragMoved)
        {
            phase = MikuGravityPhase.Supported;
            return;
        }
        if (!TryPreferTaskbarSitting()) BeginFall();
    }

    private bool TryPreferTaskbarSitting()
    {
        if (!TryGetWindowRect(unityWindow, out RectI own) ||
            !TryGetTaskbarContactDesktop(out Vector2 taskbarContact)) return false;
        float taskbarTop = GetWorkAreaBottomAt(taskbarContact.x, taskbarContact.y);
        bool preferSitting = MaoGravityInteractionLogic.ShouldPreferTaskbarSitting(
                taskbarContact.y,
                taskbarTop,
                taskbarSitSnapDistancePixels);
        UnityEngine.Debug.Log("[Mao Gravity] event=taskbar_probe window_bottom=" + own.Bottom +
                              " contact_y=" + taskbarContact.y.ToString("0.0") +
                              " taskbar_top=" + taskbarTop.ToString("0.0") +
                              " delta=" + Mathf.Abs(taskbarContact.y - taskbarTop).ToString("0.0") +
                              " preferred=" + preferSitting);
        if (!preferSitting) return false;
        if (animationCoordinator != null && !animationCoordinator.TrySetPersistent(MaoAnimationOwner.Gravity, true))
            return false;

        StopCompetingActions();
        StopAnimationLayer();
        RestoreOverrides();
        supportWindow = IntPtr.Zero;
        hasSupport = false;
        fallVelocity = 0f;
        taskbarSitting = true;
        supportTop = taskbarTop;
        phase = MikuGravityPhase.Supported;
        SetUnityWindowPosition(own.Left, Mathf.RoundToInt(taskbarTop - own.Height));
        int sitPoseCount = Mathf.Max(1, taskbarSitPoseCount);
        taskbarSitPoseIndex = MaoGravityInteractionLogic.SelectTaskbarSitPose(
            taskbarSitPoseIndex,
            sitPoseCount,
            UnityEngine.Random.Range(0, sitPoseCount));
        if (taskbarController != null)
        {
            taskbarController.ExternalSitRequested = false;
            taskbarController.ExternalWindowSitRequested = true;
        }
        animator.SetBool("isSitting", false);
        animator.SetFloat("WindowSitIndex", taskbarSitPoseIndex);
        animator.SetBool("isTaskbarSit", true);
        animator.SetBool("isWindowSit", true);
        animator.CrossFadeInFixedTime("Base Layer.WindowSit", 0.16f, 0, 0f);
        taskbarSeatSettleUntil = Time.unscaledTime + Mathf.Max(0.2f, taskbarSeatSettleSeconds);
        taskbarSeatErrorPixels = float.PositiveInfinity;
        taskbarSeatAlignmentLogged = false;
        UnityEngine.Debug.Log("[Mao Gravity] event=taskbar_sit gravity=true pose=" + taskbarSitPoseIndex);
        return true;
    }

    private void BeginFall(bool desktopOnly = false)
    {
        if (animationCoordinator != null && !animationCoordinator.TrySetPersistent(MaoAnimationOwner.Gravity, true))
            return;
        if (!CaptureContactOffsets() || !TryGetWindowRect(unityWindow, out RectI own)) return;
        StopCompetingActions();
        float contactX = own.Left + contactOffsetX;
        float contactY = own.Top + contactOffsetY;
        float floorTop = GetWorkAreaBottomAt(contactX, contactY);
        if (desktopOnly) surfaces.Clear();
        else CollectWindowSurfaces();
        MaoGravitySurface selected = MaoGravityLandingLogic.SelectSurface(contactX, contactY, floorTop, surfaces);
        float dropDistance = Mathf.Max(0f, selected.Top - contactY);
        landingMode = MaoGravityInteractionLogic.SelectLandingMode(
            dropDistance,
            fullFallMinimumDistancePixels);
        supportWindow = selected.IsDesktopFloor ? IntPtr.Zero : new IntPtr(selected.Id);
        hasSupport = false;
        ClearTaskbarSitting();
        supportTop = selected.Top;
        fallVelocity = 0f;
        landingFootIkApplied = false;
        phase = MikuGravityPhase.Falling;
        phaseStartedAt = Time.unscaledTime;
        ApplyOverrides();
        if (landingMode == MaoGravityLandingMode.FullFallRecovery) PlayState(FallingState, 0.08f);
        else StopAnimationLayer();
        UnityEngine.Debug.Log("[Mao Gravity] event=fall target=" +
                              (selected.IsDesktopFloor ? "desktop" : "window") +
                              " distance=" + dropDistance.ToString("0.0") +
                              " mode=" + landingMode);
        if (selected.Top - contactY <= 2f) Land();
    }

    private void StopCompetingActions()
    {
        GetComponent<MikuGuitarController>()?.StopGuitar();
        GetComponent<MikuIdleActionController>()?.Stop();
        GetComponentInParent<AvatarPoseController>()?.StopPose();
        GetComponentInParent<AvatarLocomotionController>()?.SetCursorFollowEnabled(false, false);
    }

    private void ClearTaskbarSitting()
    {
        if (taskbarController != null)
        {
            taskbarController.ExternalSitRequested = false;
            taskbarController.ExternalWindowSitRequested = false;
        }
        if (taskbarSitting && animator != null)
        {
            animator.SetBool("isWindowSit", false);
            animator.SetBool("isTaskbarSit", false);
            animator.SetBool("isSitting", false);
        }
        taskbarSitting = false;
        taskbarSeatSettleUntil = 0f;
        taskbarSeatErrorPixels = float.PositiveInfinity;
        taskbarSeatAlignmentLogged = false;
    }

    private void AlignTaskbarSeat()
    {
        if (windowHandler == null || !windowHandler.TryGetCurrentPoseSeatDesktop(out Vector2 seat) ||
            !TryGetWindowRect(unityWindow, out RectI own)) return;
        float targetSeatY = supportTop + windowHandler.seatOffsetPx;
        float nextTop = MaoGravityInteractionLogic.AlignWindowTopToSeat(own.Top, seat.y, targetSeatY);
        taskbarSeatErrorPixels = Mathf.Abs(seat.y - targetSeatY);
        if (taskbarSeatErrorPixels > 0.5f)
            SetUnityWindowPosition(own.Left, Mathf.RoundToInt(nextTop));
        if (!taskbarSeatAlignmentLogged && Time.unscaledTime >= taskbarSeatSettleUntil)
        {
            taskbarSeatAlignmentLogged = true;
            UnityEngine.Debug.Log("[Mao Gravity] event=taskbar_seat_aligned pose=" + taskbarSitPoseIndex +
                                  " error=" + taskbarSeatErrorPixels.ToString("0.0"));
        }
    }

    private void StepFall()
    {
        if (!TryGetWindowRect(unityWindow, out RectI own)) return;
        if (supportWindow != IntPtr.Zero)
        {
            if (!TryGetWindowRect(supportWindow, out RectI target))
            {
                BeginFall();
                return;
            }
            supportTop = target.Top;
        }

        float contactY = own.Top + contactOffsetY;
        MaoGravityMotionStep step = MaoGravityLandingLogic.Advance(
            contactY,
            supportTop,
            fallVelocity,
            accelerationPixelsPerSecondSquared,
            maximumFallSpeedPixelsPerSecond,
            Time.unscaledDeltaTime);
        fallVelocity = step.Velocity;
        int nextTop = own.Top + Mathf.RoundToInt(step.DeltaY);
        if (step.Landed) nextTop = own.Top + Mathf.RoundToInt(supportTop - contactY);
        if (nextTop != own.Top) SetUnityWindowPosition(own.Left, nextTop);
        if (step.Landed) Land();
    }

    private void Land()
    {
        fallVelocity = 0f;
        hasSupport = true;
        if (landingMode == MaoGravityLandingMode.HardLanding)
        {
            CaptureLandingFootRotations();
            phase = MikuGravityPhase.HardLanding;
            RefreshDragBlock();
            phaseStartedAt = Time.unscaledTime;
            PlayState(HardLandingState, 0.04f);
            UnityEngine.Debug.Log("[Mao Gravity] event=hard_landing surface_error=" +
                                  SurfaceErrorPixels.ToString("0.0") +
                                  " contact=" + (usingHumanoidContact ? "humanoid" : "renderer"));
            return;
        }
        phase = MikuGravityPhase.Impact;
        RefreshDragBlock();
        phaseStartedAt = Time.unscaledTime;
        PlayState(ImpactState, 0.04f);
        UnityEngine.Debug.Log("[Mao Gravity] event=impact surface_error=" + SurfaceErrorPixels.ToString("0.0") +
                              " contact=" + (usingHumanoidContact ? "humanoid" : "renderer"));
    }

    private void StepImpact()
    {
        if (StateFinished(ImpactState, 0.92f) || Time.unscaledTime - phaseStartedAt > 8f)
        {
            phase = MikuGravityPhase.StandingUp;
            phaseStartedAt = Time.unscaledTime;
            PlayState(StandingState, 0.10f);
            UnityEngine.Debug.Log("[Mao Gravity] event=standing_up");
        }
    }

    private void StepStandingUp()
    {
        if (!StateFinished(StandingState, 0.94f) && Time.unscaledTime - phaseStartedAt <= 14f) return;
        StopAnimationLayer();
        phase = MikuGravityPhase.Supported;
        RestoreOverrides();
        CaptureContactOffsets();
        AlignToSupport();
        UnityEngine.Debug.Log("[Mao Gravity] event=recovered surface_error=" + SurfaceErrorPixels.ToString("0.0"));
    }

    private void StepHardLanding()
    {
        if (!StateFinished(HardLandingState, 0.94f) && Time.unscaledTime - phaseStartedAt <= 8f) return;
        StopAnimationLayer();
        phase = MikuGravityPhase.Supported;
        RestoreOverrides();
        CaptureContactOffsets();
        AlignToSupport();
        UnityEngine.Debug.Log("[Mao Gravity] event=hard_landing_recovered surface_error=" +
                              SurfaceErrorPixels.ToString("0.0"));
    }

    private void FollowSupport()
    {
        if (!hasSupport || wasDragging || phase != MikuGravityPhase.Supported) return;
        if (supportWindow == IntPtr.Zero)
        {
            if (TryGetWindowRect(unityWindow, out RectI desktopOwn))
                supportTop = GetWorkAreaBottomAt(
                    desktopOwn.Left + contactOffsetX,
                    desktopOwn.Top + contactOffsetY);
            AlignToSupport();
            return;
        }
        if (!TryGetWindowRect(supportWindow, out RectI target) || !TryGetWindowRect(unityWindow, out RectI own))
        {
            BeginFall();
            return;
        }
        float contactX = own.Left + contactOffsetX;
        if (contactX < target.Left || contactX > target.Right)
        {
            BeginFall();
            return;
        }
        supportTop = target.Top;
        AlignToSupport();
    }

    private void AlignToSupport()
    {
        if (!TryGetWindowRect(unityWindow, out RectI own)) return;
        float desiredTop = ResolveSupportTop() - contactOffsetY;
        int nextTop = Mathf.RoundToInt(desiredTop);
        if (nextTop != own.Top) SetUnityWindowPosition(own.Left, nextTop);
    }

    private float ResolveSupportTop()
    {
        if (supportWindow != IntPtr.Zero && TryGetWindowRect(supportWindow, out RectI target))
            return target.Top;
        return supportTop;
    }

    private bool CaptureContactOffsets()
    {
        if (!TryGetAvatarContactDesktop(out Vector2 contact) || !TryGetWindowRect(unityWindow, out RectI own))
            return false;
        contactOffsetX = contact.x - own.Left;
        contactOffsetY = contact.y - own.Top;
        return true;
    }

    private bool TryGetAvatarContactDesktop(out Vector2 contact)
    {
        contact = default;
        if (animator == null) return false;
        if (targetCamera == null) targetCamera = Camera.main;
        if (targetCamera == null || !TryGetClientDesktopRect(unityWindow, out RectI client)) return false;

        Bounds bounds = default;
        bool hasBounds = false;
        if (renderers == null || renderers.Length == 0)
            renderers = animator.GetComponentsInChildren<Renderer>(true);
        for (int index = 0; index < renderers.Length; index++)
        {
            Renderer item = renderers[index];
            if (item == null || !item.enabled || !item.gameObject.activeInHierarchy) continue;
            if (!hasBounds) { bounds = item.bounds; hasBounds = true; }
            else bounds.Encapsulate(item.bounds);
        }
        Vector3 world = hasBounds
            ? new Vector3(bounds.center.x, bounds.min.y, bounds.center.z)
            : animator.transform.position;
        if (!TryWorldToDesktop(world, client, out contact)) return false;

        humanoidContactYs.Clear();
        if (humanoidContactBones != null)
        {
            for (int index = 0; index < humanoidContactBones.Length; index++)
            {
                Transform bone = humanoidContactBones[index];
                if (bone != null && TryWorldToDesktop(bone.position, client, out Vector2 boneDesktop))
                    humanoidContactYs.Add(boneDesktop.y);
            }
        }
        usingHumanoidContact = humanoidContactYs.Count > 0;
        contact.y = MaoGravityInteractionLogic.SelectBodyContactY(
            contact.y,
            humanoidContactYs,
            humanoidSurfacePaddingPixels);
        return true;
    }

    private bool TryWorldToDesktop(Vector3 world, RectI client, out Vector2 desktop)
    {
        desktop = default;
        Vector3 screen = targetCamera.WorldToScreenPoint(world);
        if (screen.z <= 0f) return false;
        float scaleX = client.Width / Mathf.Max(1f, targetCamera.pixelWidth);
        float scaleY = client.Height / Mathf.Max(1f, targetCamera.pixelHeight);
        desktop = new Vector2(
            client.Left + screen.x * scaleX,
            client.Top + (targetCamera.pixelHeight - screen.y) * scaleY);
        return true;
    }

    private void CacheHumanoidContactBones()
    {
        if (animator == null || !animator.isHuman)
        {
            humanoidContactBones = null;
            taskbarContactBones = null;
            leftFootBone = null;
            rightFootBone = null;
            return;
        }

        HumanBodyBones[] boneIds =
        {
            HumanBodyBones.Hips,
            HumanBodyBones.Head,
            HumanBodyBones.LeftHand,
            HumanBodyBones.RightHand,
            HumanBodyBones.LeftLowerArm,
            HumanBodyBones.RightLowerArm,
            HumanBodyBones.LeftFoot,
            HumanBodyBones.RightFoot,
            HumanBodyBones.LeftToes,
            HumanBodyBones.RightToes,
            HumanBodyBones.LeftLowerLeg,
            HumanBodyBones.RightLowerLeg
        };
        humanoidContactBones = new Transform[boneIds.Length];
        for (int index = 0; index < boneIds.Length; index++)
            humanoidContactBones[index] = animator.GetBoneTransform(boneIds[index]);

        HumanBodyBones[] taskbarBoneIds =
        {
            HumanBodyBones.LeftFoot,
            HumanBodyBones.RightFoot,
            HumanBodyBones.LeftToes,
            HumanBodyBones.RightToes
        };
        taskbarContactBones = new Transform[taskbarBoneIds.Length];
        for (int index = 0; index < taskbarBoneIds.Length; index++)
            taskbarContactBones[index] = animator.GetBoneTransform(taskbarBoneIds[index]);
        leftFootBone = animator.GetBoneTransform(HumanBodyBones.LeftFoot);
        rightFootBone = animator.GetBoneTransform(HumanBodyBones.RightFoot);
    }

    private void CaptureLandingFootRotations()
    {
        if (leftFootBone != null) landingLeftFootRotation = leftFootBone.rotation;
        if (rightFootBone != null) landingRightFootRotation = rightFootBone.rotation;
        landingFootIkApplied = false;
    }

    private bool TryGetTaskbarContactDesktop(out Vector2 contact)
    {
        contact = default;
        if (targetCamera == null) targetCamera = Camera.main;
        if (targetCamera == null || taskbarContactBones == null ||
            !TryGetClientDesktopRect(unityWindow, out RectI client)) return false;

        float sumX = 0f;
        float lowestY = float.NegativeInfinity;
        int count = 0;
        for (int index = 0; index < taskbarContactBones.Length; index++)
        {
            Transform bone = taskbarContactBones[index];
            if (bone == null || !TryWorldToDesktop(bone.position, client, out Vector2 desktop)) continue;
            sumX += desktop.x;
            lowestY = Mathf.Max(lowestY, desktop.y);
            count++;
        }
        if (count == 0) return false;
        contact = new Vector2(sumX / count, lowestY + Mathf.Max(0f, humanoidSurfacePaddingPixels));
        return true;
    }

    private void CollectWindowSurfaces()
    {
        surfaces.Clear();
        uint ownProcess = (uint)Process.GetCurrentProcess().Id;
        Kirurobo.WinApi.EnumWindows((handle, _) =>
        {
            if (handle == unityWindow || !Kirurobo.WinApi.IsWindowVisible(handle) || Kirurobo.WinApi.IsIconic(handle)) return true;
            Kirurobo.WinApi.GetWindowThreadProcessId(handle, out ulong processId);
            if (processId == ownProcess || Kirurobo.WinApi.GetParent(handle) != IntPtr.Zero) return true;
            if (!TryGetWindowRect(handle, out RectI rect) || rect.Width < 160 || rect.Height < 60) return true;
            if (IsCloaked(handle) || IsTransparent(handle)) return true;
            textBuffer.Clear();
            Kirurobo.WinApi.GetWindowText(handle, textBuffer, textBuffer.Capacity);
            if (textBuffer.Length == 0) return true;
            textBuffer.Clear();
            Kirurobo.WinApi.GetClassName(handle, textBuffer, textBuffer.Capacity);
            string className = textBuffer.ToString();
            if (className == "Progman" || className == "WorkerW" || className == "Shell_TrayWnd" ||
                className == "Shell_SecondaryTrayWnd") return true;
            surfaces.Add(new MaoGravitySurface(rect.Left, rect.Right, rect.Top, handle.ToInt64()));
            return true;
        }, IntPtr.Zero);
    }

    private void PlayState(string state, float transitionSeconds)
    {
        if (!IsReady) return;
        animator.applyRootMotion = false;
        animator.SetLayerWeight(layerIndex, 1f);
        animator.CrossFadeInFixedTime(LayerName + "." + state, transitionSeconds, layerIndex, 0f);
    }

    private void StopAnimationLayer()
    {
        if (animator != null && layerIndex >= 0)
            animator.CrossFadeInFixedTime(LayerName + ".Idle", 0.10f, layerIndex, 0f);
    }

    private bool StateFinished(string stateName, float normalizedTime)
    {
        if (!IsReady || animator.IsInTransition(layerIndex)) return false;
        AnimatorStateInfo state = animator.GetCurrentAnimatorStateInfo(layerIndex);
        return (state.IsName(stateName) || state.IsName(LayerName + "." + stateName)) &&
               state.normalizedTime >= normalizedTime;
    }

    private bool IsStateActive(string stateName)
    {
        if (!IsReady || string.IsNullOrEmpty(stateName)) return false;
        AnimatorStateInfo current = animator.GetCurrentAnimatorStateInfo(layerIndex);
        AnimatorStateInfo next = animator.GetNextAnimatorStateInfo(layerIndex);
        return current.IsName(stateName) || current.IsName(LayerName + "." + stateName) ||
               next.IsName(stateName) || next.IsName(LayerName + "." + stateName);
    }

    private static string StateForPhase(MikuGravityPhase value)
    {
        switch (value)
        {
            case MikuGravityPhase.Falling: return FallingState;
            case MikuGravityPhase.Impact: return ImpactState;
            case MikuGravityPhase.StandingUp: return StandingState;
            case MikuGravityPhase.HardLanding: return HardLandingState;
            default: return string.Empty;
        }
    }

    private void ApplyOverrides()
    {
        if (overridesApplied) return;
        if (avatarController != null)
        {
            savedDancingEnabled = avatarController.enableDancing;
            savedDraggingOverride = avatarController.BlockDraggingOverride;
            avatarController.enableDancing = false;
            avatarController.BlockDraggingOverride = savedDraggingOverride ||
                MaoGravityInteractionLogic.ShouldBlockUserDrag(gravityEnabled, phase == MikuGravityPhase.Falling);
        }
        overridesApplied = true;
    }

    private void RefreshDragBlock()
    {
        if (!overridesApplied || avatarController == null) return;
        avatarController.BlockDraggingOverride = savedDraggingOverride ||
            MaoGravityInteractionLogic.ShouldBlockUserDrag(gravityEnabled, phase == MikuGravityPhase.Falling);
    }

    private void RestoreOverrides()
    {
        if (!overridesApplied) return;
        if (avatarController != null)
        {
            avatarController.enableDancing = savedDancingEnabled;
            avatarController.BlockDraggingOverride = savedDraggingOverride;
        }
        overridesApplied = false;
    }

    private void DisableRuntime()
    {
        animationCoordinator?.SetPersistent(MaoAnimationOwner.Gravity, false);
        StopAnimationLayer();
        RestoreOverrides();
        if (windowHandler != null) windowHandler.ExternalSitBlocked = false;
        supportWindow = IntPtr.Zero;
        hasSupport = false;
        ClearTaskbarSitting();
        wasDragging = false;
        dragMoved = false;
        fallVelocity = 0f;
        landingFootIkApplied = false;
    }

    private void SyncAnimationOwnership()
    {
        bool exclusive = gravityEnabled &&
            (phase == MikuGravityPhase.Dragging || phase == MikuGravityPhase.Falling ||
             phase == MikuGravityPhase.Impact || phase == MikuGravityPhase.StandingUp ||
             phase == MikuGravityPhase.HardLanding || taskbarSitting);
        animationCoordinator?.SetPersistent(MaoAnimationOwner.Gravity, exclusive);
    }

    private bool IsBaseStateActive(string stateName)
    {
        if (animator == null || animator.layerCount == 0) return false;
        AnimatorStateInfo current = animator.GetCurrentAnimatorStateInfo(0);
        AnimatorStateInfo next = animator.GetNextAnimatorStateInfo(0);
        return current.IsName(stateName) || current.IsName("Base Layer." + stateName) ||
               next.IsName(stateName) || next.IsName("Base Layer." + stateName);
    }

    private IntPtr ResolveUnityWindow()
    {
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
        return Process.GetCurrentProcess().MainWindowHandle;
#else
        return IntPtr.Zero;
#endif
    }

    private void SetUnityWindowPosition(int left, int top)
    {
        Kirurobo.WinApi.SetWindowPos(
            unityWindow,
            IntPtr.Zero,
            left,
            top,
            0,
            0,
            SwpNoSize | SwpNoZOrder | SwpNoActivate);
    }

    private static bool TryGetWindowRect(IntPtr handle, out RectI rect)
    {
        rect = default;
        if (handle == IntPtr.Zero || !Kirurobo.WinApi.GetWindowRect(handle, out Kirurobo.WinApi.RECT raw)) return false;
        rect = new RectI(raw.left, raw.top, raw.right, raw.bottom);
        return rect.Width > 0 && rect.Height > 0;
    }

    private static bool TryGetClientDesktopRect(IntPtr handle, out RectI rect)
    {
        rect = default;
        if (handle == IntPtr.Zero || !Kirurobo.WinApi.GetClientRect(handle, out Kirurobo.WinApi.RECT client)) return false;
        Point origin = new Point { X = 0, Y = 0 };
        if (!ClientToScreen(handle, ref origin)) return false;
        rect = new RectI(origin.X, origin.Y, origin.X + client.right, origin.Y + client.bottom);
        return rect.Width > 0 && rect.Height > 0;
    }

    private static float GetWorkAreaBottom(IntPtr handle)
    {
        IntPtr monitor = MonitorFromWindow(handle, MonitorDefaultToNearest);
        MonitorInfo info = new MonitorInfo { Size = Marshal.SizeOf(typeof(MonitorInfo)) };
        return monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref info)
            ? info.Work.Bottom
            : Display.main.systemHeight;
    }

    private static float GetWorkAreaBottomAt(float desktopX, float desktopY)
    {
        Point point = new Point { X = Mathf.RoundToInt(desktopX), Y = Mathf.RoundToInt(desktopY) };
        IntPtr monitor = MonitorFromPoint(point, MonitorDefaultToNearest);
        MonitorInfo info = new MonitorInfo { Size = Marshal.SizeOf(typeof(MonitorInfo)) };
        return monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref info)
            ? info.Work.Bottom
            : GetWorkAreaBottom(IntPtr.Zero);
    }

    private static bool IsCloaked(IntPtr handle)
    {
        return DwmGetWindowAttribute(handle, DwmwaCloaked, out int cloaked, sizeof(int)) == 0 && cloaked != 0;
    }

    private static bool IsTransparent(IntPtr handle)
    {
        long style = unchecked((long)Kirurobo.WinApi.GetWindowLong(handle, GwlExStyle));
        return (style & WsExTransparent) != 0;
    }

    private void OnDisable()
    {
        animationCoordinator?.SetPersistent(MaoAnimationOwner.Gravity, false);
        gravityEnabled = false;
        DisableRuntime();
        phase = MikuGravityPhase.Disabled;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct RectI
    {
        public RectI(int left, int top, int right, int bottom)
        {
            Left = left; Top = top; Right = right; Bottom = bottom;
        }
        public readonly int Left;
        public readonly int Top;
        public readonly int Right;
        public readonly int Bottom;
        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }

    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr handle, ref Point point);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr handle, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(Point point, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Auto)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr handle, int attribute, out int value, int size);
}
