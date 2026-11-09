using System;
using System.Collections;
using System.Collections.Generic;
using MaoIntegration;
using UnityEngine;
using Debug = UnityEngine.Debug;

[DisallowMultipleComponent]
public sealed class MaoReactionDirector : MonoBehaviour
{
    [Header("Autonomy Configuration")]
    public MaoAutonomyRuntimeSettings settings = new MaoAutonomyRuntimeSettings();

    [Header("Live Status (Read Only)")]
    [SerializeField] private MaoDesktopActivity currentActivity;
    [SerializeField] private string currentDecision = "starting";
    [SerializeField, Range(0f, 1f)] private float curiosityScore;
    [SerializeField, Range(0f, 1f)] private float noveltyScore;
    [SerializeField, Range(0f, 1f)] private float physicalScore;
    [SerializeField, Range(0f, 1f)] private float speechScore;
    [SerializeField, Range(0f, 1f)] private float visionScore;
    [SerializeField] private MaoConversationPhysicalState conversationState = MaoConversationPhysicalState.Waiting;

    private MaoGeminiChatProvider provider;
    private MaoInitiativeSystem initiative;
    private MaoCuriositySystem curiosity;
    private MaoContextNoveltyTracker novelty;
    private MaoReactionHistory history;
    private MaoSpeechBudget speechBudget;
    private readonly MaoDesktopContextObserver contextObserver = new MaoDesktopContextObserver();
    private MaoDesktopContextSnapshot context = new MaoDesktopContextSnapshot();
    private Animator animator;
    private AvatarInteractionHandler interactions;
    private AvatarAnimatorController animatorController;
    private AvatarPoseController poseController;
    private UniversalBlendshapes expressions;
    private MaoAttentionSystem attention;
    private AvatarLocomotionController locomotion;
    private MikuGuitarController guitarController;
    private MikuIdleActionController idleActionController;
    private MikuDesktopGravityController gravityController;
    private MaoAnimationCoordinator animationCoordinator;
    private bool? requestedCursorFollowState;
    private bool requestedRunState;
    private bool requestedGravityState;
    private float nextEvaluationAt;
    private float nextIdleActionAt;
    private float nextAvatarSearchAt;
    private float lastReactionAt;
    private float lastSpeechAt;
    private float lastVisionAt;
    private float expressionStopAt;
    private float pendingInitiativeExpiresAt;
    private float lastUserInteractionAt;
    private int ignoredInitiatives;
    private int lastPoseIndex = -1;
    private int lastIdleActionIndex = -1;
    private string lastPhysicalIntent = string.Empty;
    private bool debugPanelVisible;
    private Vector2 previousCursor;
    private string lastDebugEvent = "boot";
    private float lastPatAt;
    private int patDepth;
    private bool ownsExpression;
    private string lastManualExpression = "neutral";
    private float savedJoy;
    private float savedFun;
    private float savedSorrow;
    private float savedAngry;
    private float savedNeutral;
    private float savedI;
    private float savedBlink;
    private float savedBlinkLeft;
    private float savedBlinkRight;
    private float savedLookDown;
    private float savedLookLeft;

    public bool CursorFollowEnabled => requestedCursorFollowState == true &&
                                       locomotion != null && locomotion.FollowCursorEnabled;
    public MaoLocomotionGait CurrentCursorGait => CursorFollowEnabled
        ? locomotion.CurrentCursorGait
        : MaoLocomotionGait.Idle;
    public bool CursorRunEnabled => CurrentCursorGait == MaoLocomotionGait.Run;
    public bool AdaptiveMotionReady => locomotion != null &&
                                       (locomotion.AdvancedGroundMotionReady || locomotion.AdaptiveMotionReady);
    public bool AdvancedGroundMotionReady => locomotion != null && locomotion.AdvancedGroundMotionReady;
    public MaoGroundLocomotionState CurrentGroundState => locomotion != null
        ? locomotion.CurrentGroundState
        : MaoGroundLocomotionState.Idle;
    public bool AdvancedGroundAnimationActive => locomotion != null && locomotion.AdvancedGroundAnimationActive;
    public bool CursorFlightReady => locomotion != null && locomotion.CursorFlightReady;
    public MaoCursorFlightPhase CursorFlightPhase => locomotion != null
        ? locomotion.CurrentCursorFlightPhase
        : MaoCursorFlightPhase.Grounded;
    public bool CursorFlightAnimationActive => locomotion != null && locomotion.CursorFlightAnimationActive;
    public bool MacaronFlightVisualRequested => locomotion != null && locomotion.MacaronFlightVisualRequested;
    public float CursorFacingYawDegrees => locomotion != null ? locomotion.CurrentFacingYawDegrees : 0f;
    public bool GuitarReady => guitarController != null && guitarController.IsReady;
    public bool GuitarPlaying => guitarController != null && guitarController.IsPlayingGuitar;
    public bool GuitarVisible => guitarController != null && guitarController.IsGuitarVisible;
    public bool GuitarAnimationActive => guitarController != null && guitarController.IsAnimationActive;
    public float GuitarRootDriftDistance => guitarController != null ? guitarController.RootDriftDistance : 0f;
    public float GuitarNeckRise => guitarController != null ? guitarController.NeckRise : 0f;
    public float GuitarHandGripError => guitarController != null ? guitarController.HandGripError : float.PositiveInfinity;
    public bool IdleActionsReady => idleActionController != null && idleActionController.IsReady;
    public bool IdleActionPlaying => idleActionController != null && idleActionController.IsPlaying;
    public bool IdleActionAnimationActive => idleActionController != null && idleActionController.IsAnimationActive;
    public float IdleActionRootDrift => idleActionController != null ? idleActionController.RootDrift : 0f;
    public MaoIdleActionKind CurrentIdleAction => idleActionController != null
        ? idleActionController.CurrentAction
        : MaoIdleActionKind.Angry;
    public bool GravityReady => gravityController != null && gravityController.IsReady;
    public bool GravityEnabled => requestedGravityState && gravityController != null && gravityController.GravityEnabled;
    public bool GravityBusy => gravityController != null && gravityController.IsBusy;
    public bool GravityAnimationActive => gravityController != null && gravityController.IsAnimationActive;
    public MikuGravityPhase GravityPhase => gravityController != null ? gravityController.Phase : MikuGravityPhase.Disabled;
    public bool GravityTaskbarSitting => gravityController != null && gravityController.TaskbarSitting;
    public int GravityTaskbarSitPoseIndex => gravityController != null ? gravityController.TaskbarSitPoseIndex : -1;
    public bool GravityTaskbarSitAnimationActive => gravityController != null && gravityController.TaskbarSitAnimationActive;
    public bool GravityLandingFootIkApplied => gravityController != null && gravityController.LandingFootIkApplied;
    public float GravityTaskbarSnapErrorPixels => gravityController != null
        ? gravityController.TaskbarSnapErrorPixels
        : float.PositiveInfinity;
    public MaoGravityLandingMode GravityLandingMode => gravityController != null
        ? gravityController.LandingMode
        : MaoGravityLandingMode.HardLanding;
    public float GravitySurfaceErrorPixels => gravityController != null ? gravityController.SurfaceErrorPixels : float.PositiveInfinity;
    public bool StandingForAutonomousIdleAction => IsStandingForAutonomousIdleAction();
    public int BoundAvatarInstanceId => animator != null ? animator.GetInstanceID() : 0;
    public string GuitarRuntimeDebugSnapshot => guitarController != null
        ? guitarController.RuntimeDebugSnapshot
        : "controller=null";
    public MaoAnimationOwner CurrentAnimationOwner => animationCoordinator != null
        ? animationCoordinator.CurrentOwner
        : MaoAnimationOwner.None;
    public string CurrentExpression => ownsExpression ? lastManualExpression : "neutral";
    public bool ExpressionActive => ownsExpression && Time.unscaledTime < expressionStopAt;

    public bool PlayGuitar()
    {
        BindAvatarIfNeeded();
        if (animationCoordinator != null && !animationCoordinator.CanAcquire(MaoAnimationOwner.Guitar))
        {
            lastDebugEvent = "guitar:blocked-by-animation";
            return false;
        }
        requestedCursorFollowState = false;
        requestedRunState = false;
        if (TryBindLocomotion()) locomotion.SetCursorFollowEnabled(false, false);
        if (poseController != null && poseController.IsPoseActive) poseController.StopPose();
        idleActionController?.Stop();
        if (!TryBindGuitar())
        {
            lastDebugEvent = "guitar:setup-unavailable";
            return false;
        }
        bool started = guitarController.PlayGuitar();
        if (started) animationCoordinator?.SetPersistent(MaoAnimationOwner.Guitar, true);
        lastDebugEvent = started ? "guitar:playing" : "guitar:play-failed";
        return started;
    }

    /// <summary>
    /// Applies a user-selected expression for a short, blended interval.  The
    /// same allowlisted profiles used by chat reactions are reused so the UI
    /// cannot inject animator parameters or leave a stale face behind.
    /// </summary>
    public bool SetManualExpression(string emotion)
    {
        BindAvatarIfNeeded();
        if (expressions == null) return false;
        string normalized = (emotion ?? string.Empty).Trim().ToLowerInvariant();
        switch (normalized)
        {
            case "neutral": case "happy": case "curious": case "shy": case "surprised":
            case "concerned": case "playful": case "angry": case "pout": case "sad":
            case "sleepy": case "embarrassed": break;
            default: return false;
        }
        SetExpression(normalized, 4f);
        if (normalized == "pout" || normalized == "angry") TryTriggerGesture("Pout", "Cross Arms", "Contextual Idle");
        else if (normalized == "shy" || normalized == "embarrassed") TryTriggerGesture("Shy", "Look Away", "Contextual Idle");
        else if (normalized == "sleepy") TryTriggerGesture("Sleepy", "Yawn", "Contextual Idle");
        lastDebugEvent = "expression:manual-" + normalized;
        return true;
    }

    public bool StopGuitar()
    {
        if (!TryBindGuitar())
        {
            lastDebugEvent = "guitar:stop-no-controller";
            return true;
        }
        bool stopped = guitarController.StopGuitar();
        animationCoordinator?.SetPersistent(MaoAnimationOwner.Guitar, false);
        lastDebugEvent = stopped ? "guitar:stopping" : "guitar:stop-failed";
        return stopped;
    }

    public bool SetCursorFollowEnabled(bool enabled)
    {
        return SetCursorFollowEnabled(enabled, false);
    }

    public bool SetCursorFollowEnabled(bool enabled, bool preferRunning)
    {
        if (enabled && GuitarPlaying)
        {
            lastDebugEvent = "movement:blocked-by-guitar";
            return false;
        }
        requestedCursorFollowState = enabled;
        if (enabled) idleActionController?.Stop();
        requestedRunState = enabled && preferRunning;
        if (!TryBindLocomotion())
        {
            lastDebugEvent = enabled
                ? (requestedRunState ? "movement:cursor-run-queued" : "movement:cursor-follow-queued")
                : "movement:stop-queued";
            return true;
        }
        locomotion.SetCursorFollowEnabled(enabled, requestedRunState);
        animationCoordinator?.SetPersistent(MaoAnimationOwner.Locomotion, enabled);
        lastDebugEvent = enabled ? "movement:cursor-adaptive" : "movement:stopped";
        return true;
    }

    internal bool BeginAdaptiveMotionSmokeProbe(int direction = 1)
    {
        if (!TryBindLocomotion()) return false;
        if (locomotion.AdvancedGroundMotionReady)
            return locomotion.BeginAdvancedGroundMotionSmokeProbe(direction, true);
        return locomotion.BeginAdaptiveMotionSmokeProbe(direction, locomotion.CursorRunDistancePixels + 120f);
    }

    internal bool BeginAdaptiveWalkSmokeProbe(int direction)
    {
        if (!TryBindLocomotion()) return false;
        if (locomotion.AdvancedGroundMotionReady)
            return locomotion.BeginAdvancedGroundMotionSmokeProbe(direction, false);
        float walkDistance = Mathf.Max(1f, locomotion.CursorRunDistancePixels - 60f);
        return locomotion.BeginAdaptiveMotionSmokeProbe(direction, walkDistance);
    }

    internal bool BeginCursorFlightSmokeProbe(MaoCursorFlightPhase phase)
    {
        return TryBindLocomotion() && locomotion.BeginCursorFlightSmokeProbe(phase);
    }

    internal void EndCursorFlightSmokeProbe()
    {
        if (TryBindLocomotion()) locomotion.EndCursorFlightSmokeProbe();
    }

    internal bool CursorFacingAgreesWithDirection(int direction)
    {
        return TryBindLocomotion() && locomotion.FacingAgreesWithDirection(direction);
    }

    private bool TryBindLocomotion()
    {
        if (animator != null && animator.gameObject.activeInHierarchy)
        {
            AvatarLocomotionController avatarLocomotion =
                animator.GetComponentInParent<AvatarLocomotionController>();
            if (avatarLocomotion == null)
            {
                avatarLocomotion = animator.gameObject.AddComponent<AvatarLocomotionController>();
                avatarLocomotion.EnableLocomotion = false;
                avatarLocomotion.AvatarBoundsRoot = animator.transform;
                Debug.Log("[Mao Movement] event=locomotion_attached_runtime");
            }
            locomotion = avatarLocomotion;
        }
        else if (locomotion == null || !locomotion.gameObject.activeInHierarchy)
        {
            locomotion = null;
            AvatarLocomotionController[] controllers = Resources.FindObjectsOfTypeAll<AvatarLocomotionController>();
            for (int index = 0; index < controllers.Length; index++)
            {
                AvatarLocomotionController candidate = controllers[index];
                if (candidate != null && candidate.gameObject.activeInHierarchy)
                {
                    locomotion = candidate;
                    break;
                }
            }
        }
        if (locomotion == null) return false;
        if (animator != null) locomotion.SetAnimator(animator);
        return true;
    }

    private bool TryBindGuitar()
    {
        if (animator == null || !animator.gameObject.activeInHierarchy) return false;
        if (guitarController == null || guitarController.gameObject != animator.gameObject)
        {
            guitarController = animator.GetComponent<MikuGuitarController>();
            if (guitarController == null) guitarController = animator.gameObject.AddComponent<MikuGuitarController>();
        }
        guitarController.Bind(animator);
        return guitarController.IsReady;
    }

    private bool TryBindIdleActions()
    {
        if (animator == null || !animator.gameObject.activeInHierarchy) return false;
        if (idleActionController == null || idleActionController.gameObject != animator.gameObject)
        {
            idleActionController = animator.GetComponent<MikuIdleActionController>();
            if (idleActionController == null)
                idleActionController = animator.gameObject.AddComponent<MikuIdleActionController>();
        }
        idleActionController.Bind(animator);
        return idleActionController.IsReady;
    }

    private bool TryBindGravity()
    {
        if (animator == null || !animator.gameObject.activeInHierarchy) return false;
        if (gravityController == null || gravityController.gameObject != animator.gameObject)
        {
            gravityController = animator.GetComponent<MikuDesktopGravityController>();
            if (gravityController == null)
                gravityController = animator.gameObject.AddComponent<MikuDesktopGravityController>();
        }
        gravityController.Bind(animator);
        if (gravityController.IsReady && gravityController.GravityEnabled != requestedGravityState)
            gravityController.SetEnabled(requestedGravityState);
        return gravityController.IsReady;
    }

    public bool SetGravityEnabled(bool enabled)
    {
        BindAvatarIfNeeded();
        requestedGravityState = enabled;
        if (!TryBindGravity())
        {
            lastDebugEvent = enabled ? "gravity:queued" : "gravity:disable-queued";
            return !enabled;
        }
        if (enabled)
        {
            requestedCursorFollowState = false;
            requestedRunState = false;
            if (TryBindLocomotion()) locomotion.SetCursorFollowEnabled(false, false);
            if (GuitarPlaying) StopGuitar();
            idleActionController?.Stop();
            if (poseController != null && poseController.IsPoseActive) poseController.StopPose();
        }
        bool changed = gravityController.SetEnabled(enabled);
        animationCoordinator?.SetPersistent(MaoAnimationOwner.Gravity, enabled && (GravityBusy || GravityAnimationActive));
        lastDebugEvent = "gravity:" + (enabled ? "enabled" : "disabled");
        return changed;
    }

    public bool ToggleGravity()
    {
        return SetGravityEnabled(!GravityEnabled);
    }

    internal bool DebugLiftAndDropGravity(int liftPixels)
    {
        return TryBindGravity() && gravityController.DebugLiftAndDrop(liftPixels);
    }

    internal bool DebugInterruptGravityRecoveryForDrag()
    {
        return TryBindGravity() && gravityController.DebugInterruptRecoveryForDrag();
    }

    internal bool DebugReleaseGravityNearTaskbar(int offsetPixels)
    {
        return TryBindGravity() && gravityController.DebugReleaseNearTaskbar(offsetPixels);
    }

    internal void DebugSetGravityPhysicalDragSuppressed(bool suppressed)
    {
        if (TryBindGravity()) gravityController.DebugSetPhysicalDragSuppressed(suppressed);
    }

    public bool PlayIdleAction(MaoIdleActionKind action)
    {
        BindAvatarIfNeeded();
        if (!TryBindIdleActions()) return false;
        if (GuitarPlaying) StopGuitar();
        requestedCursorFollowState = false;
        requestedRunState = false;
        if (TryBindLocomotion()) locomotion.SetCursorFollowEnabled(false, false);
        if (poseController != null && poseController.IsPoseActive) poseController.StopPose();
        bool played = idleActionController.Play(action);
        if (played)
        {
            animationCoordinator?.SetPersistent(MaoAnimationOwner.Idle, true);
            lastIdleActionIndex = (int)action;
            lastReactionAt = Time.unscaledTime;
            ScheduleIdleAction(Time.unscaledTime);
            lastDebugEvent = "idle-action:" + action;
        }
        return played;
    }

    private void Awake()
    {
        if (MaoAppearanceVerification.Active) settings.enabled = false;
        provider = GetComponent<MaoGeminiChatProvider>();
        initiative = new MaoInitiativeSystem(settings);
        curiosity = new MaoCuriositySystem(0.25f + settings.personality.Curiosity * 0.15f);
        novelty = new MaoContextNoveltyTracker(settings.noveltyCapacity);
        history = new MaoReactionHistory(settings.reactionHistoryCapacity);
        speechBudget = new MaoSpeechBudget(settings.speechBudgetCapacity, settings.speechTokenRegenerationSeconds, Time.unscaledTime);
        float now = Time.unscaledTime;
        lastReactionAt = lastSpeechAt = lastVisionAt = now;
        ScheduleIdleAction(now);
        previousCursor = Input.mousePosition;
        if (provider != null)
        {
            provider.ConversationStateChanged += OnConversationStateChanged;
            provider.SemanticReactionRequested += OnSemanticReaction;
        }
        Debug.Log("[Mao Autonomy] event=started scheduler=low-frequency computer_agent=allowlisted_immediate_execution");
    }

    private void Update()
    {
        if (Input.GetKeyDown(settings.debugPanelKey)) debugPanelVisible = !debugPanelVisible;
        float now = Time.unscaledTime;
        if (now >= nextAvatarSearchAt)
        {
            nextAvatarSearchAt = now + 2f;
            BindAvatarIfNeeded();
        }
        SyncAnimationOwnership();
        if (!MaoAppearanceVerification.Active && now >= nextIdleActionAt) TryPlayAutonomousIdleAction(now);
        if (expressions != null && expressionStopAt > 0f && now >= expressionStopAt) ClearExpression();
        if (pendingInitiativeExpiresAt > 0f && now >= pendingInitiativeExpiresAt)
        {
            ignoredInitiatives = Mathf.Min(6, ignoredInitiatives + 1);
            pendingInitiativeExpiresAt = 0f;
        }
        if (!settings.enabled || now < nextEvaluationAt) return;
        nextEvaluationAt = now + settings.evaluationInterval;
        Evaluate(now);
    }

    private void BindAvatarIfNeeded()
    {
        if (animator != null && animator.gameObject.activeInHierarchy)
        {
            TryBindAnimationCoordinator();
            TryBindGuitar();
            TryBindIdleActions();
            TryBindGravity();
            return;
        }
        Animator[] candidates = UnityEngine.Object.FindObjectsByType<Animator>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
        Animator selected = null;
        for (int index = 0; index < candidates.Length; index++)
            if (candidates[index] != null && candidates[index].isHuman) { selected = candidates[index]; break; }
        if (selected == null) return;

        if (interactions != null) interactions.InteractionTriggered -= OnUserInteraction;
        animator = selected;
        interactions = animator.GetComponentInParent<AvatarInteractionHandler>();
        animatorController = animator.GetComponentInParent<AvatarAnimatorController>();
        poseController = animator.GetComponentInParent<AvatarPoseController>();
        expressions = animator.GetComponentInParent<UniversalBlendshapes>();
        if (expressions == null) expressions = animator.GetComponentInChildren<UniversalBlendshapes>(true);
        if (interactions != null) interactions.InteractionTriggered += OnUserInteraction;
        GameObject owner = interactions != null ? interactions.gameObject : animator.gameObject;
        TryBindAnimationCoordinator(owner);
        attention = owner.GetComponent<MaoAttentionSystem>();
        if (attention == null) attention = owner.AddComponent<MaoAttentionSystem>();
        attention.Bind(animator, expressions, settings);
        if (TryBindLocomotion() && requestedCursorFollowState.HasValue)
            locomotion.SetCursorFollowEnabled(requestedCursorFollowState.Value, requestedRunState);
        TryBindGuitar();
        TryBindIdleActions();
        TryBindGravity();
        SyncAnimationOwnership();
        lastDebugEvent = "avatar-bound";
        Debug.Log("[Mao Autonomy] event=avatar_bound attention=true guitar=" + GuitarReady);
    }

    private void Evaluate(float now)
    {
        if (GuitarPlaying)
        {
            currentDecision = "Performance: guitar";
            return;
        }
        if (IdleActionPlaying)
        {
            currentDecision = "Physical: idle action " + CurrentIdleAction;
            return;
        }
        if (GravityBusy)
        {
            currentDecision = "Physical: gravity " + GravityPhase;
            return;
        }
        MaoDesktopContextSnapshot latest = contextObserver.Poll();
        bool contextChanged = latest.ContextChanged;
        context = latest;
        currentActivity = context.Activity;
        noveltyScore = novelty.Observe(context.Activity + "|" + context.ContextHash, now);
        curiosity.Advance(
            settings.evaluationInterval,
            noveltyScore,
            contextChanged,
            context.ScreenChanged,
            settings.curiosityDecayPerSecond);
        curiosityScore = curiosity.Value;
        speechBudget.Update(now);

        if (context.UserReturned)
        {
            ignoredInitiatives = 0;
            pendingInitiativeExpiresAt = 0f;
            if (poseController != null && poseController.IsPoseActive) poseController.StopPose();
        }

        float recentInteractionBias = now - lastUserInteractionAt <= 60f ? 0.15f : 0f;
        float salience = context.UserReturned ? 1f : contextChanged ? 0.72f : 0.18f;
        salience = Mathf.Clamp01(salience + recentInteractionBias);
        float stimulationNeed = Mathf.Clamp01((now - lastReactionAt) / 600f + context.IdleSeconds / 1200f);
        var input = new MaoInitiativeInput
        {
            IdleSeconds = context.IdleSeconds,
            UserIsActive = context.UserIsActive,
            ScreenChanged = provider != null && provider.ScreenVisionObservationReady,
            Novelty = noveltyScore,
            Curiosity = curiosityScore,
            FocusProtection = context.FocusProtection,
            SecondsSinceReaction = now - lastReactionAt,
            SecondsSinceSpeech = now - lastSpeechAt,
            SecondsSinceVision = now - lastVisionAt,
            ConversationBusy = provider != null && provider.IsBusy,
            IgnoredInitiatives = ignoredInitiatives,
            StimulationNeed = stimulationNeed,
            Salience = salience,
            Personality = settings.personality
        };
        MaoInitiativeDecision decision = initiative.Evaluate(input);
        physicalScore = decision.PhysicalScore;
        speechScore = decision.SpeechScore;
        visionScore = decision.VisionScore;
        currentDecision = decision.Channel + ": " + decision.Reason;

        if (context.UserReturned && now - lastReactionAt >= Mathf.Min(12f, settings.physicalCooldown))
        {
            PerformPhysical(now, true);
            return;
        }

        switch (decision.Channel)
        {
            case MaoInitiativeChannel.Physical:
                PerformPhysical(now, false);
                break;
            case MaoInitiativeChannel.Vision:
                BeginVision(now);
                break;
            case MaoInitiativeChannel.Speech:
                float dynamicProbability = Mathf.Clamp(
                    settings.personality.Talkativeness * (1f - context.FocusProtection) * 0.65f + salience * 0.20f,
                    0.03f,
                    0.42f);
                // Recent reciprocal chat makes a small amount of initiative feel earned.  Keep
                // the adjustment bounded and retain the existing speech cooldown/focus guard so
                // this never turns into a constant stream of interruptions.
                if (provider != null)
                {
                    float engagementBias = Mathf.Lerp(-0.04f, 0.10f,
                        Mathf.Clamp01(provider.ConversationEngagementScore));
                    dynamicProbability = Mathf.Clamp(dynamicProbability + engagementBias, 0.03f, 0.42f);
                }
                if (UnityEngine.Random.value <= dynamicProbability) PerformSpeech(now);
                else currentDecision = "None: speech-probability-skip";
                break;
            default:
                RequestIdleGaze(now);
                break;
        }
    }

    private void PerformPhysical(float now, bool userReturned)
    {
        if (animator == null) return;
        string intent = SelectPhysicalIntent(userReturned);
        if (!history.CanUse(MaoInitiativeChannel.Physical, intent, currentActivity.ToString(), context.ProcessName, context.ContextHash, now, settings.physicalCooldown))
        {
            currentDecision = "None: repeated-physical-context";
            return;
        }

        bool performed = false;
        if (intent == "return-wave")
        {
            performed = TriggerCoordinatedInteraction("Right Hand Wave");
            attention?.RequestTarget(MaoAttentionTargetType.User, ScreenCenter(0.58f), 0.95f, 0.95f, 3.5f);
            SetExpression("happy", 2.2f);
        }
        else if (intent == "long-idle-pose")
        {
            performed = PlayNonRepeatingPose(now);
        }
        else if (intent == "cursor-curiosity")
        {
            performed = attention != null && attention.RequestTarget(
                MaoAttentionTargetType.Cursor, Input.mousePosition, 0.72f, 0.82f, 4f);
            SetExpression("curious", 1.8f);
        }
        else if (intent == "contextual-idle")
        {
            if (animationCoordinator == null || animationCoordinator.CanAcquire(MaoAnimationOwner.Idle))
            {
                if (animatorController != null)
                    performed = animatorController.RequestContextualIdle(ContextualIdleIndex(currentActivity), settings.minimumContextualIdleDuration);
                if (interactions != null) performed |= interactions.TriggerInteraction("Contextual Idle");
                if (performed)
                    animationCoordinator?.TryAcquire(MaoAnimationOwner.Idle, settings.minimumContextualIdleDuration);
            }
            attention?.RequestTarget(MaoAttentionTargetType.Screen, ScreenCenter(0.55f), 0.50f, 0.55f, 3f);
        }
        else
        {
            performed = attention != null && attention.RequestTarget(
                MaoAttentionTargetType.Away,
                new Vector2(Screen.width * UnityEngine.Random.Range(0.28f, 0.72f), Screen.height * UnityEngine.Random.Range(0.38f, 0.76f)),
                0.35f,
                0.42f,
                UnityEngine.Random.Range(2.5f, 5f));
            if (UnityEngine.Random.value < 0.45f) SetExpression("playful", 1.4f);
        }

        if (!performed) return;
        history.Record(MaoInitiativeChannel.Physical, intent, currentActivity.ToString(), context.ProcessName, context.ContextHash, now);
        lastPhysicalIntent = intent;
        lastReactionAt = now;
        curiosity.Satisfy(intent == "long-idle-pose" ? 0.32f : 0.20f);
        currentDecision = "Physical: " + intent;
        lastDebugEvent = "physical:" + intent;
    }

    private string SelectPhysicalIntent(bool userReturned)
    {
        if (userReturned) return "return-wave";
        var candidates = new List<WeightedIntent>(5);
        float cursorDistance = CursorDistanceToHead();
        if (cursorDistance >= 0f && cursorDistance < 190f) candidates.Add(new WeightedIntent("cursor-curiosity", 3.2f));
        if (context.IdleSeconds >= settings.longIdlePoseSeconds && poseController != null && poseController.PoseCount > 0)
            candidates.Add(new WeightedIntent("long-idle-pose", 0.75f));
        candidates.Add(new WeightedIntent("contextual-idle", context.ContextChanged ? 2.8f : 1.4f));
        candidates.Add(new WeightedIntent("micro-gaze", 2.4f));
        for (int index = 0; index < candidates.Count; index++)
            if (candidates[index].Intent == lastPhysicalIntent) candidates[index].Weight *= 0.08f;
        float total = 0f;
        for (int index = 0; index < candidates.Count; index++) total += candidates[index].Weight;
        float pick = UnityEngine.Random.value * total;
        for (int index = 0; index < candidates.Count; index++)
        {
            pick -= candidates[index].Weight;
            if (pick <= 0f) return candidates[index].Intent;
        }
        return "micro-gaze";
    }

    private void BeginVision(float now)
    {
        if (provider == null || !provider.IsConfigured || !provider.ScreenVisionEnabled ||
            !provider.ScreenVisionObservationReady || provider.IsBusy)
        {
            currentDecision = "None: vision-unavailable";
            return;
        }
        string visionContextKey = context.ContextHash + "|" + provider.ScreenVisionRequestCount;
        if (!history.CanUse(MaoInitiativeChannel.Vision, "observe", currentActivity.ToString(),
                context.ProcessName, visionContextKey, now, settings.visionCooldown))
        {
            currentDecision = "None: repeated-vision-context";
            return;
        }
        attention?.RequestTarget(MaoAttentionTargetType.Screen, ScreenCenter(0.56f), 0.82f, 0.90f, 4f);
        SetExpression("curious", 1.8f);
        lastReactionAt = now;
        currentDecision = "Vision: physical-observe-first";
        StartCoroutine(DelayedVisionObservation(
            visionContextKey, context.ProcessName, currentActivity.ToString()));
    }

    private IEnumerator DelayedVisionObservation(string visionContextKey, string activeApp, string activity)
    {
        yield return new WaitForSecondsRealtime(1.0f);
        if (provider == null || provider.IsBusy) yield break;
        if (!provider.ObserveScreenIfChanged(OnVisionComplete, false, activeApp, activity))
        {
            currentDecision = "None: local-screen-gate-closed";
            lastDebugEvent = "vision:local-gate-closed";
            yield break;
        }
        float now = Time.unscaledTime;
        history.Record(MaoInitiativeChannel.Vision, "observe", activity, activeApp, visionContextKey, now);
        lastVisionAt = now;
        curiosity.Satisfy(0.28f);
        lastDebugEvent = "vision:request-started";
    }

    private void OnVisionComplete(string comment)
    {
        float now = Time.unscaledTime;
        if (string.IsNullOrWhiteSpace(comment))
        {
            lastDebugEvent = "vision:quiet";
            return;
        }
        if (!history.CanUse(MaoInitiativeChannel.Speech, "vision-comment", currentActivity.ToString(), context.ProcessName, context.ContextHash, now, settings.speechCooldown))
        {
            lastDebugEvent = "vision:duplicate-comment-suppressed";
            return;
        }
        if (!speechBudget.TrySpend(now))
        {
            lastDebugEvent = "vision:speech-budget-empty";
            return;
        }
        if (!provider.TryPublishScreenVisionMessage(
                comment,
                provider.LastScreenVisionHadMeaningfulChange || context.ContextChanged))
        {
            lastDebugEvent = "vision:suppressed-" + provider.LastProactiveSuppressionReason;
            return;
        }
        history.Record(MaoInitiativeChannel.Speech, "vision-comment", currentActivity.ToString(), context.ProcessName, context.ContextHash, now);
        lastSpeechAt = now;
        pendingInitiativeExpiresAt = now + 35f;
        lastDebugEvent = "vision:comment-published";
    }

    private void PerformSpeech(float now)
    {
        if (provider != null && provider.SecondsSinceProactiveMessage < 30d)
        {
            currentDecision = "None: recent-proactive-message";
            return;
        }
        string intent = "context-question";
        if (!history.CanUse(MaoInitiativeChannel.Speech, intent, currentActivity.ToString(), context.ProcessName, context.ContextHash, now, settings.speechCooldown))
        {
            currentDecision = "None: repeated-speech-context";
            return;
        }
        if (!speechBudget.TrySpend(now))
        {
            currentDecision = "None: speech-budget-empty";
            return;
        }

        string line = ContextualLine(currentActivity, DateTime.Now.Hour);
        if (provider == null || !provider.TryPublishContextualProactiveMessage(line, context.ContextChanged))
        {
            currentDecision = "None: proactive-" + (provider != null ? provider.LastProactiveSuppressionReason : "unavailable");
            return;
        }
        TriggerCoordinatedVoiceReaction();
        attention?.RequestTarget(MaoAttentionTargetType.User, ScreenCenter(0.58f), 0.78f, 0.78f, 4f);
        history.Record(MaoInitiativeChannel.Speech, intent, currentActivity.ToString(), context.ProcessName, context.ContextHash, now);
        lastSpeechAt = lastReactionAt = now;
        pendingInitiativeExpiresAt = now + 35f;
        currentDecision = "Speech: rare-context-question";
        lastDebugEvent = "speech:published";
    }

    private void RequestIdleGaze(float now)
    {
        if (attention == null || attention.CurrentInterest > 0.12f) return;
        if (UnityEngine.Random.value > 0.12f) return;
        attention.RequestTarget(
            MaoAttentionTargetType.Away,
            new Vector2(Screen.width * UnityEngine.Random.Range(0.30f, 0.70f), Screen.height * UnityEngine.Random.Range(0.38f, 0.72f)),
            0.12f,
            0.20f,
            UnityEngine.Random.Range(2f, 5f));
    }

    private void OnConversationStateChanged(MaoConversationPhysicalState state)
    {
        conversationState = state;
        if (state != MaoConversationPhysicalState.Waiting) ScheduleIdleAction(Time.unscaledTime);
        switch (state)
        {
            case MaoConversationPhysicalState.Thinking:
                attention?.RequestTarget(MaoAttentionTargetType.Screen, ScreenCenter(0.54f), 0.90f, 0.86f, 8f);
                SetExpression("curious", 2.5f);
                break;
            case MaoConversationPhysicalState.Speaking:
                attention?.RequestTarget(MaoAttentionTargetType.Conversation, ScreenCenter(0.60f), 0.96f, 0.92f, 10f);
                if (!GuitarPlaying && !IdleActionPlaying && !GravityBusy) TriggerCoordinatedVoiceReaction();
                break;
            case MaoConversationPhysicalState.Listening:
                attention?.RequestTarget(MaoAttentionTargetType.User, ScreenCenter(0.60f), 1f, 1f, 10f);
                break;
            default:
                attention?.RequestTarget(MaoAttentionTargetType.User, ScreenCenter(0.56f), 0.42f, 0.35f, 2f);
                break;
        }
    }

    private void OnSemanticReaction(MaoSemanticReaction reaction)
    {
        if (reaction == null) return;
        float expressionDuration = ExpressionDuration(reaction.Emotion);
        SetExpression(reaction.Emotion, expressionDuration);
        switch (reaction.Attention)
        {
            case "screen": attention?.RequestTarget(MaoAttentionTargetType.Screen, ScreenCenter(0.55f), 0.80f, 0.82f, 4f); break;
            case "cursor": attention?.RequestTarget(MaoAttentionTargetType.Cursor, Input.mousePosition, 0.80f, 0.82f, 4f); break;
            case "away": attention?.RequestTarget(MaoAttentionTargetType.Away, new Vector2(Screen.width * 0.7f, Screen.height * 0.65f), 0.55f, 0.5f, 3f); break;
            default: attention?.RequestTarget(MaoAttentionTargetType.User, ScreenCenter(0.58f), 0.75f, 0.75f, 4f); break;
        }
        if (GuitarPlaying || IdleActionPlaying || GravityBusy) return;
        switch (reaction.Gesture)
        {
            case "wave": TryTriggerGesture("Right Hand Wave", "Wave"); break;
            case "celebrate": TryTriggerGesture("Contextual Idle", "Celebrate"); break;
            case "ponder":
            case "tilt": attention?.RequestTarget(MaoAttentionTargetType.Away, new Vector2(Screen.width * 0.62f, Screen.height * 0.68f), 0.70f, 0.72f, 3f); break;
            case "nod": TriggerCoordinatedVoiceReaction(); break;
            case "look_away":
                attention?.RequestTarget(MaoAttentionTargetType.Away,
                    new Vector2(Screen.width * 0.72f, Screen.height * 0.64f), 0.86f, 0.82f, 3.5f);
                TryTriggerGesture("Look Away", "LookAway", "Shy");
                break;
            case "thinking":
                attention?.RequestTarget(MaoAttentionTargetType.Screen,
                    ScreenCenter(0.54f), 0.84f, 0.82f, 4f);
                TryTriggerGesture("Thinking", "Ponder", "Contextual Idle");
                break;
            case "pout": TryTriggerGesture("Pout", "Shy", "Contextual Idle"); break;
            case "cross_arms": TryTriggerGesture("Cross Arms", "CrossArms", "Contextual Idle"); break;
            case "laugh": TryTriggerGesture("Laugh", "Happy", "Contextual Idle"); break;
            case "clap": TryTriggerGesture("Clap", "Celebrate", "Contextual Idle"); break;
            case "confused": TryTriggerGesture("Confused", "Thinking", "Contextual Idle"); break;
            case "surprised": TryTriggerGesture("Surprised", "Contextual Idle"); break;
            case "sleepy":
            case "yawn": TryTriggerGesture("Yawn", "Sleepy", "Contextual Idle"); break;
            case "stretch": TryTriggerGesture("Stretch", "Contextual Idle"); break;
            case "shy": TryTriggerGesture("Shy", "Look Away", "Contextual Idle"); break;
        }
    }

    private void TryTriggerGesture(params string[] names)
    {
        TriggerCoordinatedInteraction(names);
    }

    private bool TriggerCoordinatedInteraction(params string[] names)
    {
        if (interactions == null || names == null) return false;
        bool lease = animationCoordinator == null || animationCoordinator.TryAcquire(MaoAnimationOwner.Interaction, 1.35f);
        if (!lease)
        {
            lastDebugEvent = "gesture:blocked-by-" + animationCoordinator.CurrentOwner;
            return false;
        }
        for (int index = 0; index < names.Length; index++)
        {
            string name = names[index];
            if (!string.IsNullOrWhiteSpace(name) && interactions.TriggerInteraction(name))
            {
                return true;
            }
        }
        if (animationCoordinator != null) animationCoordinator.Release(MaoAnimationOwner.Interaction);
        return false;
    }

    private bool TriggerCoordinatedVoiceReaction()
    {
        if (interactions == null) return false;
        bool lease = animationCoordinator == null || animationCoordinator.TryAcquire(MaoAnimationOwner.Interaction, 1.35f);
        if (!lease)
        {
            lastDebugEvent = "voice-gesture:blocked-by-" + animationCoordinator.CurrentOwner;
            return false;
        }
        bool triggered = interactions.TriggerVoiceReaction();
        if (!triggered && animationCoordinator != null) animationCoordinator.Release(MaoAnimationOwner.Interaction);
        return triggered;
    }

    private void TryBindAnimationCoordinator()
    {
        GameObject owner = interactions != null ? interactions.gameObject : animator != null ? animator.gameObject : null;
        TryBindAnimationCoordinator(owner);
    }

    private void TryBindAnimationCoordinator(GameObject owner)
    {
        if (animator == null) return;
        MaoAnimationCoordinator hierarchyCoordinator = animator.GetComponentInParent<MaoAnimationCoordinator>();
        if (hierarchyCoordinator != null)
            animationCoordinator = hierarchyCoordinator;
        else if (animationCoordinator == null || !IsInAvatarHierarchy(animationCoordinator.transform, animator.transform))
            animationCoordinator = (owner != null ? owner : animator.gameObject).AddComponent<MaoAnimationCoordinator>();
        animationCoordinator.Bind(animator);
    }

    private static bool IsInAvatarHierarchy(Transform candidate, Transform avatarTransform)
    {
        if (candidate == null || avatarTransform == null) return false;
        for (Transform current = avatarTransform; current != null; current = current.parent)
            if (current == candidate.transform) return true;
        return false;
    }

    private void SyncAnimationOwnership()
    {
        if (animationCoordinator == null) return;
        bool flight = CursorFlightAnimationActive;
        bool locomotionActive = CursorFollowEnabled && !flight;
        bool poseActive = poseController != null && poseController.IsPoseActive;

        // Set low-priority owners first. Disabling a high-priority owner then
        // lets the coordinator restore the next valid owner automatically.
        animationCoordinator.SetPersistent(MaoAnimationOwner.Idle, IdleActionPlaying);
        animationCoordinator.SetPersistent(MaoAnimationOwner.Locomotion, locomotionActive);
        animationCoordinator.SetPersistent(MaoAnimationOwner.CursorFlight, flight);
        animationCoordinator.SetPersistent(MaoAnimationOwner.Pose, poseActive);
        animationCoordinator.SetPersistent(MaoAnimationOwner.Guitar, GuitarPlaying);
        // Gravity owns its lifecycle from MikuDesktopGravityController.  Do
        // not mirror a second derived flag here; doing so can release/reacquire
        // the same owner every frame while the fall phase is transitioning.
    }

    private void OnUserInteraction(AvatarInteractionType type, string region)
    {
        float now = Time.unscaledTime;
        lastUserInteractionAt = now;
        idleActionController?.Stop();
        ScheduleIdleAction(now);
        ignoredInitiatives = 0;
        pendingInitiativeExpiresAt = 0f;
        curiosity.Satisfy(0.12f);
        if (poseController != null && poseController.IsPoseActive) poseController.StopPose();
        attention?.RequestTarget(MaoAttentionTargetType.Interaction, Input.mousePosition, 1f, 1f, 4f);
        if (type == AvatarInteractionType.HeadPat)
        {
            provider?.RecordPositiveInteraction();
            patDepth = now - lastPatAt <= 20f ? Mathf.Min(3, patDepth + 1) : 1;
            lastPatAt = now;
            SetExpression(patDepth >= 2 ? "playful" : "happy", 1.8f + patDepth * 0.35f);
        }
        else if (type == AvatarInteractionType.Hover)
        {
            SetExpression("happy", 1.4f);
        }
        lastDebugEvent = "user-interaction:" + type;
    }

    private bool PlayNonRepeatingPose(float now)
    {
        if (poseController == null || poseController.PoseCount <= 0 || poseController.IsPoseActive) return false;
        if (animationCoordinator != null && !animationCoordinator.CanAcquire(MaoAnimationOwner.Pose)) return false;
        int next = UnityEngine.Random.Range(0, poseController.PoseCount);
        if (poseController.PoseCount > 1 && next == lastPoseIndex) next = (next + 1) % poseController.PoseCount;
        float duration = UnityEngine.Random.Range(
            settings.poseMinimumDuration,
            Mathf.Max(settings.poseMinimumDuration, settings.poseMaximumDuration));
        if (!poseController.PlayPoseForDuration(next, duration)) return false;
        animationCoordinator?.TryAcquire(MaoAnimationOwner.Pose, duration);
        lastPoseIndex = next;
        return true;
    }

    private void TryPlayAutonomousIdleAction(float now)
    {
        if (!IdleActionsReady || IdleActionPlaying || GuitarPlaying || CursorFollowEnabled || GravityEnabled ||
            (animationCoordinator != null && !animationCoordinator.CanAcquire(MaoAnimationOwner.Idle)) ||
            (animatorController != null && animatorController.isDancing) ||
            (poseController != null && poseController.IsPoseActive) ||
            !IsStandingForAutonomousIdleAction() ||
            conversationState != MaoConversationPhysicalState.Waiting ||
            (provider != null && provider.IsBusy))
        {
            nextIdleActionAt = now + 5f;
            return;
        }

        // The imported combat clips (Angry/Jab Cross/Box Jump/Flying Kick) are
        // explicit commands, not unattended idle behavior.  Autonomous idle
        // reactions stay low-impact and cute so they cannot kick the user or
        // unexpectedly take over locomotion.
        bool performed = false;
        if (animatorController != null)
            performed = animatorController.RequestContextualIdle(
                ContextualIdleIndex(currentActivity), settings.minimumContextualIdleDuration);
        if (!performed && interactions != null)
            performed = TriggerCoordinatedInteraction("Contextual Idle");
        if (performed)
        {
            SetExpression(UnityEngine.Random.value < 0.5f ? "playful" : "curious", 2.2f);
            lastReactionAt = now;
            lastDebugEvent = "idle-action:contextual-cute";
        }
        ScheduleIdleAction(now);
    }

    private bool IsStandingForAutonomousIdleAction()
    {
        if (animator == null || !animator.isActiveAndEnabled) return false;

        bool isSitting = GetAnimatorBool("isSitting");
        bool isSleeping = GetAnimatorBool("IsSleeping");
        bool isWindowSitting = GetAnimatorBool("isWindowSit");
        bool isTaskbarSitting = GetAnimatorBool("isTaskbarSit") || GravityTaskbarSitting;
        bool isLying = false;
        bool isMounted = false;
        bool isTransitioning = false;

        for (int layer = 0; layer < animator.layerCount; layer++)
        {
            isTransitioning |= animator.IsInTransition(layer);
            AnimatorStateInfo current = animator.GetCurrentAnimatorStateInfo(layer);
            AnimatorStateInfo next = animator.GetNextAnimatorStateInfo(layer);
            isLying |= IsRestrictedPhysicalState(current) || IsRestrictedPhysicalState(next);
        }

        AnimatorControllerParameter[] parameters = animator.parameters;
        for (int index = 0; index < parameters.Length; index++)
        {
            AnimatorControllerParameter parameter = parameters[index];
            if (parameter.type != AnimatorControllerParameterType.Bool || !animator.GetBool(parameter.nameHash)) continue;
            string name = parameter.name.ToLowerInvariant();
            if (name.Contains("sit")) isSitting = true;
            if (name.Contains("lie") || name.Contains("sleep")) isLying = true;
            if (name.Contains("mount") || name.Contains("ride") || name.Contains("horse") || name.Contains("saddle"))
                isMounted = true;
        }

        for (Transform parent = animator.transform.parent; parent != null; parent = parent.parent)
        {
            string name = parent.name.ToLowerInvariant();
            if (name.Contains("mount") || name.Contains("mounted") || name.Contains("ride") ||
                name.Contains("riding") || name.Contains("horse") || name.Contains("saddle"))
            {
                isMounted = true;
                break;
            }
        }

        return MaoIdleActionPolicy.CanPlayAutonomous(
            isSitting,
            isSleeping,
            isWindowSitting,
            isTaskbarSitting,
            isLying,
            isMounted,
            isTransitioning);
    }

    private bool GetAnimatorBool(string parameterName)
    {
        int hash = Animator.StringToHash(parameterName);
        AnimatorControllerParameter[] parameters = animator.parameters;
        for (int index = 0; index < parameters.Length; index++)
            if (parameters[index].type == AnimatorControllerParameterType.Bool && parameters[index].nameHash == hash)
                return animator.GetBool(hash);
        return false;
    }

    private static bool IsRestrictedPhysicalState(AnimatorStateInfo state)
    {
        return state.IsName("Sitting") || state.IsName("Base Layer.Sitting") ||
               state.IsName("WindowSit") || state.IsName("Base Layer.WindowSit") ||
               state.IsName("Sleeping") || state.IsName("Base Layer.Sleeping") ||
               state.IsName("Lying") || state.IsName("Base Layer.Lying") ||
               state.IsName("Lie") || state.IsName("Base Layer.Lie") ||
               state.IsName("Mounted") || state.IsName("Base Layer.Mounted") ||
               state.IsName("Riding") || state.IsName("Base Layer.Riding") ||
               state.IsName("Ride") || state.IsName("Base Layer.Ride");
    }

    private void ScheduleIdleAction(float now)
    {
        nextIdleActionAt = now + UnityEngine.Random.Range(60f, 120f);
    }

    private float CursorDistanceToHead()
    {
        if (animator == null || Camera.main == null || !animator.isHuman) return -1f;
        Transform head = animator.GetBoneTransform(HumanBodyBones.Head);
        if (head == null) return -1f;
        Vector3 screen = Camera.main.WorldToScreenPoint(head.position);
        if (screen.z <= 0f) return -1f;
        Vector2 cursor = Input.mousePosition;
        float movement = (cursor - previousCursor).magnitude;
        previousCursor = cursor;
        return Vector2.Distance(cursor, screen) - Mathf.Min(60f, movement * 0.25f);
    }

    private void SetExpression(string emotion, float duration)
    {
        if (expressions == null) return;
        if (ownsExpression) ClearExpression();
        savedJoy = expressions.Joy;
        savedFun = expressions.Fun;
        savedSorrow = expressions.Sorrow;
        savedAngry = expressions.Angry;
        savedNeutral = expressions.Neutral;
        savedI = expressions.I;
        savedBlink = expressions.Blink;
        savedBlinkLeft = expressions.Blink_L;
        savedBlinkRight = expressions.Blink_R;
        savedLookDown = expressions.LookDown;
        savedLookLeft = expressions.LookLeft;
        ownsExpression = true;
        lastManualExpression = (emotion ?? "neutral").Trim().ToLowerInvariant();
        switch ((emotion ?? string.Empty).ToLowerInvariant())
        {
            case "happy": expressions.Joy = 0.42f; break;
            case "playful": expressions.Fun = 0.38f; break;
            case "shy": expressions.Fun = 0.22f; expressions.Blink_L = 0.18f; break;
            case "surprised": expressions.I = 0.18f; break;
            case "concerned": expressions.Sorrow = 0.28f; break;
            case "curious": expressions.Fun = 0.16f; break;
            case "angry": expressions.Angry = 0.52f; expressions.LookDown = 0.08f; break;
            // VRM does not guarantee a dedicated pout/embarrassed key.  A restrained
            // angry+sorrow blend reads as a pout without forcing an unsupported shape.
            case "pout": expressions.Angry = 0.24f; expressions.Sorrow = 0.16f; expressions.LookDown = 0.12f; break;
            case "sad": expressions.Sorrow = 0.42f; expressions.LookDown = 0.16f; break;
            case "sleepy": expressions.Sorrow = 0.12f; expressions.Blink = 0.38f; expressions.Blink_L = 0.26f; expressions.Blink_R = 0.26f; break;
            case "embarrassed": expressions.Fun = 0.18f; expressions.Sorrow = 0.10f; expressions.Blink_L = 0.22f; expressions.Blink_R = 0.12f; expressions.LookLeft = 0.10f; break;
            default: expressions.Neutral = 0.12f; break;
        }
        expressionStopAt = Time.unscaledTime + Mathf.Max(0.3f, duration);
    }

    private void ClearExpression()
    {
        if (expressions != null && ownsExpression)
        {
            expressions.Joy = savedJoy;
            expressions.Fun = savedFun;
            expressions.Sorrow = savedSorrow;
            expressions.Angry = savedAngry;
            expressions.Neutral = savedNeutral;
            expressions.I = savedI;
            expressions.Blink = savedBlink;
            expressions.Blink_L = savedBlinkLeft;
            expressions.Blink_R = savedBlinkRight;
            expressions.LookDown = savedLookDown;
            expressions.LookLeft = savedLookLeft;
        }
        ownsExpression = false;
        lastManualExpression = "neutral";
        expressionStopAt = 0f;
    }

    private static float ExpressionDuration(string emotion)
    {
        switch ((emotion ?? string.Empty).ToLowerInvariant())
        {
            case "angry":
            case "pout": return 3.6f;
            case "sad": return 3.2f;
            case "sleepy": return 4.5f;
            case "embarrassed":
            case "shy": return 2.8f;
            case "surprised": return 1.8f;
            default: return 2.2f;
        }
    }

    private static int ContextualIdleIndex(MaoDesktopActivity activity)
    {
        switch (activity)
        {
            case MaoDesktopActivity.Coding: return 2;
            case MaoDesktopActivity.Video: return 5;
            case MaoDesktopActivity.Gaming: return 4;
            case MaoDesktopActivity.Browsing: return 3;
            case MaoDesktopActivity.Away: return 6;
            default: return 1;
        }
    }

    private static string ContextualLine(MaoDesktopActivity activity, int hour)
    {
        // Use the shared bounded fallback table so autonomous speech and model replies keep the
        // same concise Vietnamese voice even when no provider request is made.
        int variant = Math.Abs(hour) % 2;
        switch (activity)
        {
            case MaoDesktopActivity.Coding: return MaoVietnameseFallback.GetLine(MaoContextualActivity.Coding, variant);
            case MaoDesktopActivity.Video: return MaoVietnameseFallback.GetLine(MaoContextualActivity.Video, variant);
            case MaoDesktopActivity.Gaming: return MaoVietnameseFallback.GetLine(MaoContextualActivity.Gaming, variant);
            case MaoDesktopActivity.Browsing: return MaoVietnameseFallback.GetLine(MaoContextualActivity.Browsing, variant);
            case MaoDesktopActivity.Away: return MaoVietnameseFallback.GetLine(MaoContextualActivity.Away, variant);
            default: return MaoVietnameseFallback.GetLine(MaoContextualActivity.General, variant);
        }
    }

    private static Vector2 ScreenCenter(float vertical) => new Vector2(Screen.width * 0.5f, Screen.height * vertical);

    private void OnGUI()
    {
        if (!debugPanelVisible) return;
        var panel = new Rect(16f, 16f, 500f, 342f);
        GUI.Box(panel, "Miku Autonomy - F10");
        float x = panel.x + 12f;
        float y = panel.y + 28f;
        const float width = 476f;
        const float line = 25f;
        GUI.Label(new Rect(x, y, width, line), "State: " + currentDecision); y += line;
        GUI.Label(new Rect(x, y, width, line), string.Format(
            "Initiative  physical {0:0.00}   speech {1:0.00}   vision {2:0.00}",
            physicalScore, speechScore, visionScore)); y += line;
        GUI.Label(new Rect(x, y, width, line), string.Format(
            "Mind  curiosity {0:0.00}   novelty {1:0.00}   speech budget {2:0.00}/{3}",
            curiosityScore, noveltyScore, speechBudget != null ? speechBudget.Available : 0f, settings.speechBudgetCapacity)); y += line;
        GUI.Label(new Rect(x, y, width, line), "Context: " + currentActivity + "  app=" +
            (string.IsNullOrEmpty(context.ProcessName) ? "unknown" : context.ProcessName)); y += line;
        GUI.Label(new Rect(x, y, width, line), string.Format(
            "User idle {0:0}s   focus protection {1:0.00}   ignored initiatives {2}",
            context.IdleSeconds, context.FocusProtection, ignoredInitiatives)); y += line;
        GUI.Label(new Rect(x, y, width, line), "Conversation: " + conversationState + "   attention: " +
            (attention != null ? attention.CurrentTargetType.ToString() : "unbound")); y += line;
        GUI.Label(new Rect(x, y, width, line), "Movement: " +
            (CursorRunEnabled ? "running sideways after cursor" :
             CurrentCursorGait == MaoLocomotionGait.Walk ? "walking sideways after cursor" :
             CursorFollowEnabled ? "adaptive follow (waiting/near cursor)" : "stopped/normal")); y += line;
        GUI.Label(new Rect(x, y, width, line), "Guitar: " +
            (GuitarPlaying ? "playing" : GuitarReady ? "ready" : "not ready")); y += line;
        GUI.Label(new Rect(x, y, width, line), "Animation owner: " +
            CurrentAnimationOwner + (animationCoordinator != null
                ? string.Format(" ({0:0.0}s)", animationCoordinator.CurrentOwnerRemaining)
                : " (unbound)")); y += line;
        GUI.Label(new Rect(x, y, width, line), string.Format(
            "Last physical {0:0}s   speech {1:0}s   vision {2:0}s ago",
            Time.unscaledTime - lastReactionAt,
            Time.unscaledTime - lastSpeechAt,
            Time.unscaledTime - lastVisionAt)); y += line;
        GUI.Label(new Rect(x, y, width, line), "Last event: " + lastDebugEvent); y += line;
        GUI.Label(new Rect(x, y, width, line), "Privacy: screenshots in RAM only - model tags allowlisted - no desktop control");
    }

    private void OnDestroy()
    {
        if (provider != null)
        {
            provider.ConversationStateChanged -= OnConversationStateChanged;
            provider.SemanticReactionRequested -= OnSemanticReaction;
        }
        if (interactions != null) interactions.InteractionTriggered -= OnUserInteraction;
        if (guitarController != null) guitarController.StopGuitar();
        if (idleActionController != null) idleActionController.Stop();
        if (gravityController != null) gravityController.SetEnabled(false);
        if (locomotion != null) locomotion.Stop();
        if (poseController != null) poseController.StopPose();
        if (animationCoordinator != null) animationCoordinator.ForceRelease("reaction_director_destroyed");
        ClearExpression();
    }

    private sealed class WeightedIntent
    {
        public WeightedIntent(string intent, float weight)
        {
            Intent = intent;
            Weight = weight;
        }

        public string Intent;
        public float Weight;
    }
}
