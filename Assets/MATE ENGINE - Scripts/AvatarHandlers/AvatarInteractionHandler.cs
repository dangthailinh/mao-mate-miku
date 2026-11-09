using System;
using System.Collections.Generic;
using MaoIntegration;
using UnityEngine;

public enum AvatarInteractionType
{
    Hover,
    Click,
    DoubleClick,
    HeadPat,
    Drag,
    Poke,
    Hand,
    Wave,
    Face,
    Random,
    Voice
}

public enum AvatarInteractionParameterMode
{
    Auto,
    Trigger,
    Bool
}

[Serializable]
public sealed class InteractionRegion
{
    public string name = "Interaction";
    public HumanBodyBones targetBone = HumanBodyBones.Head;
    public Vector3 localOffset;
    public Vector3 worldOffset;
    [Min(0.001f)] public float interactionRadius = 0.08f;
    public AvatarInteractionType interactionType = AvatarInteractionType.Click;

    [Header("Body Animation")]
    public string animationParameter;
    public AvatarInteractionParameterMode animationParameterMode = AvatarInteractionParameterMode.Auto;
    public string animationState;
    public string animationLayer = "Interaction Layer";

    [Header("Face Animation")]
    public string faceAnimationParameter;
    public AvatarInteractionParameterMode faceParameterMode = AvatarInteractionParameterMode.Auto;
    public string faceAnimationState;
    public string faceAnimationLayer = "Face Layer";

    [Header("Timing and State")]
    [Min(0f)] public float cooldown = 1f;
    [Min(0.05f)] public float reactionDuration = 0.8f;
    [Range(0.01f, 0.5f)] public float transitionDuration = 0.12f;
    public List<string> allowedStates = new List<string>();

    [Header("Audio")]
    public List<AudioClip> voiceClips = new List<AudioClip>();

    [Header("Head Pat Gesture")]
    [Range(90f, 1080f)] public float patCircleDegrees = 540f;
    [Range(0f, 50f)] public float patMinimumRadius = 12f;
    [Range(0.1f, 2f)] public float patResetTime = 0.6f;
    [Range(20f, 600f)] public float patWiggleDistance = 180f;
    [Range(1, 10)] public int patDirectionChanges = 3;
    [Range(1f, 20f)] public float patMinimumSampleDistance = 4f;

    [NonSerialized] public Transform bone;
    [NonSerialized] public int animationHash;
    [NonSerialized] public int faceHash;
    [NonSerialized] public int animationLayerIndex = -1;
    [NonSerialized] public int faceLayerIndex = -1;
    [NonSerialized] public AnimatorControllerParameterType animationParameterType;
    [NonSerialized] public AnimatorControllerParameterType faceParameterType;
    [NonSerialized] public bool hasAnimationParameter;
    [NonSerialized] public bool hasFaceParameter;
    [NonSerialized] public bool wasHovering;
    [NonSerialized] public bool bodyBoolActive;
    [NonSerialized] public bool faceBoolActive;
    [NonSerialized] public float resetAt;
    [NonSerialized] public float cooldownUntil;
    [NonSerialized] public HashSet<int> allowedStateHashes;
    [NonSerialized] public AvatarPatGestureDetector patDetector;
}

[DisallowMultipleComponent]
public sealed class AvatarInteractionHandler : MonoBehaviour
{
    [Header("References")]
    public Animator avatarAnimator;
    public Camera interactionCamera;
    public AudioSource voiceAudioSource;

    [Header("Interaction Regions")]
    public List<InteractionRegion> regions = new List<InteractionRegion>();
    public bool createRecommendedRegionsWhenEmpty = true;

    [Header("Input")]
    [Range(0.1f, 0.6f)] public float doubleClickInterval = 0.3f;
    [Range(1f, 100f)] public float dragThreshold = 12f;

    [Header("Global State Whitelist")]
    public List<string> allowedStates = new List<string>();

    [Header("Random Reactions")]
    public bool enableRandomReactions;
    [Min(1f)] public float minRandomReactionDelay = 20f;
    [Min(1f)] public float maxRandomReactionDelay = 60f;

    [Header("Debug")]
    public bool showDebugInteractionRegions;
    public Color debugRegionColor = new Color(0.2f, 0.8f, 1f, 0.8f);

    public event Action<AvatarInteractionType, string> InteractionTriggered;

    readonly Dictionary<int, AnimatorControllerParameterType> parameterTypes =
        new Dictionary<int, AnimatorControllerParameterType>();
    readonly HashSet<int> globalAllowedStateHashes = new HashSet<int>();

    static readonly int IsCustomDancingHash = Animator.StringToHash("isCustomDancing");
    static readonly int IsDancingHash = Animator.StringToHash("isDancing");
    static readonly int IsBigScreenHash = Animator.StringToHash("isBigScreen");
    static readonly int IsBigScreenSaverHash = Animator.StringToHash("isBigScreenSaver");
    static readonly int IsBigScreenAlarmHash = Animator.StringToHash("isBigScreenAlarm");
    static readonly int IsWaitingForDancingHash = Animator.StringToHash("isWaitingForDancing");

    bool isSetup;
    bool pointerDown;
    bool dragTriggered;
    Vector2 pointerDownPosition;
    int dragRegionIndex = -1;
    int pendingSingleRegionIndex = -1;
    float pendingSingleReleasedAt = -1f;
    HumanBodyBones lastReleasedBone = HumanBodyBones.LastBone;
    float lastReleasedAt = -1f;
    int activePriority;
    float activeUntil;
    float nextRecoveryAt;
    float nextRandomReactionAt;
    MaoAnimationCoordinator animationCoordinator;

    void Awake()
    {
        EnsureRecommendedRegions();
        if (avatarAnimator == null) avatarAnimator = GetComponentInChildren<Animator>(true);
        if (interactionCamera == null) interactionCamera = Camera.main;
        if (voiceAudioSource == null) voiceAudioSource = GetComponent<AudioSource>();
        if (voiceAudioSource == null) voiceAudioSource = gameObject.AddComponent<AudioSource>();
        SetAnimator(avatarAnimator);
        ScheduleRandomReaction();
    }

    void Reset()
    {
        EnsureRecommendedRegions();
    }

    void EnsureRecommendedRegions()
    {
        if (!createRecommendedRegionsWhenEmpty) return;
        if (regions == null) regions = new List<InteractionRegion>();
        bool hasRecommendedSet = regions.Count == 0;
        for (int index = 0; index < regions.Count && !hasRecommendedSet; index++)
            hasRecommendedSet = regions[index] != null &&
                (string.Equals(regions[index].name, "Head Hover", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(regions[index].name, "Contextual Idle", StringComparison.OrdinalIgnoreCase));
        if (!hasRecommendedSet) return;
        AddRecommendedRegion("Head Hover", HumanBodyBones.Head, AvatarInteractionType.Hover, "InteractionHoverHead", 0.09f);
        AddRecommendedRegion("Head Pat", HumanBodyBones.Head, AvatarInteractionType.HeadPat, "InteractionHeadPat", 0.1f);
        AddRecommendedRegion("Right Hand Wave", HumanBodyBones.RightHand, AvatarInteractionType.Wave, "InteractionWave", 0.1f);
        AddRecommendedRegion("Face Double Click", HumanBodyBones.Head, AvatarInteractionType.DoubleClick, "InteractionSurprised", 0.09f);
        AddRecommendedRegion("Chest Poke", HumanBodyBones.Chest, AvatarInteractionType.Poke, "InteractionPoke", 0.11f);
        AddRecommendedRegion("Left Hand Drag", HumanBodyBones.LeftHand, AvatarInteractionType.Drag, "InteractionDrag", 0.1f);
        AddRecommendedRegion("Contextual Idle", HumanBodyBones.UpperChest, AvatarInteractionType.Random, "InteractionRandom", 0.1f);
        AddRecommendedRegion("Voice Reaction", HumanBodyBones.UpperChest, AvatarInteractionType.Voice, "InteractionVoice", 0.1f);
    }

    void AddRecommendedRegion(
        string regionName,
        HumanBodyBones bone,
        AvatarInteractionType type,
        string parameter,
        float radius)
    {
        for (int index = 0; index < regions.Count; index++)
            if (regions[index] != null && string.Equals(regions[index].name, regionName, StringComparison.OrdinalIgnoreCase)) return;
        regions.Add(CreateRegion(regionName, bone, type, parameter, radius));
    }

    static InteractionRegion CreateRegion(
        string regionName,
        HumanBodyBones bone,
        AvatarInteractionType type,
        string parameter,
        float radius)
    {
        return new InteractionRegion
        {
            name = regionName,
            targetBone = bone,
            interactionType = type,
            animationParameter = parameter,
            interactionRadius = radius,
            allowedStates = new List<string> { "Idle", "Walk Left", "Walk Right", "Sitting" }
        };
    }

    void OnEnable()
    {
        if (!isSetup && avatarAnimator != null) SetupAnimator();
    }

    void OnDisable()
    {
        ResetInteractionState(true);
    }

    public void SetAnimator(Animator animator)
    {
        animationCoordinator?.SetPersistent(MaoAnimationOwner.Interaction, false);
        ResetAnimatorBools();
        avatarAnimator = animator;
        animationCoordinator = avatarAnimator != null
            ? avatarAnimator.GetComponentInParent<MaoAnimationCoordinator>()
            : null;
        isSetup = false;
        ResetInteractionState(false);
        if (avatarAnimator != null) SetupAnimator();
    }

    void SetupAnimator()
    {
        if (avatarAnimator == null) return;

        parameterTypes.Clear();
        AnimatorControllerParameter[] parameters = avatarAnimator.parameters;
        for (int i = 0; i < parameters.Length; i++)
            parameterTypes[parameters[i].nameHash] = parameters[i].type;

        globalAllowedStateHashes.Clear();
        AddStateHashes(allowedStates, globalAllowedStateHashes);

        for (int i = 0; i < regions.Count; i++)
        {
            InteractionRegion region = regions[i];
            if (region == null) continue;

            region.bone = avatarAnimator.isHuman ? avatarAnimator.GetBoneTransform(region.targetBone) : null;
            region.animationHash = Animator.StringToHash(region.animationParameter ?? string.Empty);
            region.faceHash = Animator.StringToHash(region.faceAnimationParameter ?? string.Empty);
            region.animationLayerIndex = ResolveLayer(region.animationLayer);
            region.faceLayerIndex = ResolveLayer(region.faceAnimationLayer);
            region.hasAnimationParameter = TryResolveParameter(
                region.animationHash, region.animationParameterMode, out region.animationParameterType);
            region.hasFaceParameter = TryResolveParameter(
                region.faceHash, region.faceParameterMode, out region.faceParameterType);
            region.allowedStateHashes = region.allowedStateHashes ?? new HashSet<int>();
            region.allowedStateHashes.Clear();
            AddStateHashes(region.allowedStates, region.allowedStateHashes);
            region.patDetector = region.patDetector ?? new AvatarPatGestureDetector();
            ResetRegionRuntime(region, false);
        }

        isSetup = true;
    }

    void Update()
    {
        RecoverReferencesIfNeeded();
        if (!isSetup || avatarAnimator == null || interactionCamera == null) return;

        float now = Time.unscaledTime;
        UpdateTimedResets(now);
        ResolvePendingSingleClick(now);

        bool blocked = IsGloballyBlocked();
        Vector2 pointer = Input.mousePosition;
        UpdateHoverAndPat(pointer, now, blocked);

        if (blocked)
        {
            CancelPointerGesture();
            return;
        }

        HandlePointer(pointer, now);
        UpdateRandomReaction(now);
    }

    void RecoverReferencesIfNeeded()
    {
        float now = Time.unscaledTime;
        if (avatarAnimator != null && avatarAnimator.isActiveAndEnabled)
        {
            if (interactionCamera == null) interactionCamera = Camera.main;
            return;
        }
        if (now < nextRecoveryAt) return;
        nextRecoveryAt = now + 1f;

        Animator found = GetComponentInChildren<Animator>(true);
        if (found == null) found = GetComponentInParent<Animator>();
        if (found != avatarAnimator) SetAnimator(found);
        if (interactionCamera == null) interactionCamera = Camera.main;
    }

    void HandlePointer(Vector2 pointer, float now)
    {
        if (Input.GetMouseButtonDown(0))
        {
            pointerDown = true;
            dragTriggered = false;
            pointerDownPosition = pointer;
            dragRegionIndex = FindBestRegion(pointer, AvatarInteractionType.Drag);
        }

        if (pointerDown && !dragTriggered && dragRegionIndex >= 0
            && AvatarInteractionGestureLogic.HasReachedDragThreshold(pointerDownPosition, pointer, dragThreshold))
        {
            dragTriggered = TriggerRegion(dragRegionIndex, now);
        }

        if (!Input.GetMouseButtonUp(0)) return;

        if (!dragTriggered) HandleClickRelease(pointer, now);
        CancelPointerGesture();
    }

    void HandleClickRelease(Vector2 pointer, float now)
    {
        int doubleRegion = FindBestRegion(pointer, AvatarInteractionType.DoubleClick);
        int singleRegion = FindBestSingleClickRegion(pointer);
        int hitRegion = doubleRegion >= 0 ? doubleRegion : singleRegion;
        if (hitRegion < 0) return;

        HumanBodyBones bone = regions[hitRegion].targetBone;
        bool isDoubleClick = bone == lastReleasedBone
                             && AvatarInteractionGestureLogic.IsDoubleClick(lastReleasedAt, now, doubleClickInterval);

        if (isDoubleClick && doubleRegion >= 0)
        {
            pendingSingleRegionIndex = -1;
            pendingSingleReleasedAt = -1f;
            lastReleasedAt = -1f;
            lastReleasedBone = HumanBodyBones.LastBone;
            TriggerRegion(doubleRegion, now);
            return;
        }

        lastReleasedAt = now;
        lastReleasedBone = bone;
        pendingSingleRegionIndex = singleRegion;
        pendingSingleReleasedAt = now;
    }

    void ResolvePendingSingleClick(float now)
    {
        if (pendingSingleRegionIndex < 0) return;
        if (!AvatarInteractionGestureLogic.ShouldResolveSingleClick(
                pendingSingleReleasedAt, now, doubleClickInterval)) return;

        int regionIndex = pendingSingleRegionIndex;
        pendingSingleRegionIndex = -1;
        pendingSingleReleasedAt = -1f;
        TriggerRegion(regionIndex, now);
    }

    void UpdateHoverAndPat(Vector2 pointer, float now, bool blocked)
    {
        for (int i = 0; i < regions.Count; i++)
        {
            InteractionRegion region = regions[i];
            if (region == null || region.bone == null) continue;
            bool inside = IsPointerInside(region, pointer);

            if (region.interactionType == AvatarInteractionType.Hover)
            {
                bool cooldownReady = region.wasHovering || now >= region.cooldownUntil;
                bool shouldHover = inside
                                   && !blocked
                                   && cooldownReady
                                   && CanTriggerRegion(region, now, false)
                                   && AvatarInteractionGestureLogic.CanPreempt(
                                       activePriority, PriorityFor(AvatarInteractionType.Hover));
                if (shouldHover != region.wasHovering)
                {
                    region.wasHovering = shouldHover;
                    SetHoverState(region, shouldHover, now);
                }
            }
            else if (region.interactionType == AvatarInteractionType.HeadPat)
            {
                if (inside && !blocked && CanTriggerRegion(region, now, false))
                {
                    bool completed = region.patDetector.AddSample(
                        GetRegionScreenCenter(region), pointer, now,
                        region.patCircleDegrees, region.patMinimumRadius, region.patResetTime,
                        region.patWiggleDistance, region.patDirectionChanges, region.patMinimumSampleDistance);
                    if (completed) TriggerRegion(i, now);
                }
                else
                {
                    region.patDetector.Reset();
                }
            }
        }
    }

    void SetHoverState(InteractionRegion region, bool state, float now)
    {
        if (state)
        {
            if (!AvatarInteractionGestureLogic.CanPreempt(activePriority, PriorityFor(AvatarInteractionType.Hover)))
                return;
            ApplyRegionAnimation(region, true, now);
            PlayVoice(region);
            activePriority = PriorityFor(AvatarInteractionType.Hover);
            InteractionTriggered?.Invoke(region.interactionType, region.name);
        }
        else
        {
            StopRegionBools(region);
            region.cooldownUntil = now + Mathf.Max(0f, region.cooldown);
            RefreshActivePriority(now);
        }
    }

    bool TriggerRegion(int regionIndex, float now)
    {
        if (regionIndex < 0 || regionIndex >= regions.Count) return false;
        InteractionRegion region = regions[regionIndex];
        if (!CanTriggerRegion(region, now, true)) return false;

        bool bodyAnimation = region.hasAnimationParameter || !string.IsNullOrEmpty(region.animationState);
        if (bodyAnimation && animationCoordinator != null &&
            !animationCoordinator.TryAcquire(MaoAnimationOwner.Interaction, Mathf.Max(0.05f, region.reactionDuration)))
        {
            Debug.Log("[Mao Interaction] event=blocked_by_owner region=" + region.name +
                      " owner=" + animationCoordinator.CurrentOwner);
            return false;
        }

        int priority = PriorityFor(region.interactionType);
        if (!AvatarInteractionGestureLogic.CanPreempt(activePriority, priority)) return false;

        StopLowerPriorityHover(priority);
        ApplyRegionAnimation(region, false, now);
        PlayVoice(region);
        region.cooldownUntil = now + Mathf.Max(0f, region.cooldown);
        activePriority = priority;
        activeUntil = Mathf.Max(activeUntil, now + Mathf.Max(0.05f, region.reactionDuration));
        InteractionTriggered?.Invoke(region.interactionType, region.name);
        return true;
    }

    void ApplyRegionAnimation(InteractionRegion region, bool persistent, float now)
    {
        ApplyAnimation(
            region.hasAnimationParameter, region.animationParameterType, region.animationHash,
            region.animationState, region.animationLayerIndex, region.transitionDuration,
            persistent, region, false);
        ApplyAnimation(
            region.hasFaceParameter, region.faceParameterType, region.faceHash,
            region.faceAnimationState, region.faceLayerIndex, region.transitionDuration,
            persistent, region, true);

        if (!persistent) region.resetAt = now + Mathf.Max(0.05f, region.reactionDuration);
    }

    void ApplyAnimation(
        bool hasParameter,
        AnimatorControllerParameterType parameterType,
        int parameterHash,
        string stateName,
        int layerIndex,
        float transitionDuration,
        bool persistent,
        InteractionRegion region,
        bool face)
    {
        if (hasParameter && parameterType == AnimatorControllerParameterType.Trigger)
        {
            avatarAnimator.SetTrigger(parameterHash);
            return;
        }
        if (hasParameter && parameterType == AnimatorControllerParameterType.Bool)
        {
            avatarAnimator.SetBool(parameterHash, true);
            if (face) region.faceBoolActive = true;
            else region.bodyBoolActive = true;
            return;
        }
        if (!string.IsNullOrEmpty(stateName) && layerIndex >= 0)
            avatarAnimator.CrossFadeInFixedTime(stateName, Mathf.Clamp(transitionDuration, 0.01f, 0.5f), layerIndex);
    }

    void UpdateTimedResets(float now)
    {
        for (int i = 0; i < regions.Count; i++)
        {
            InteractionRegion region = regions[i];
            if (region == null || region.wasHovering || region.resetAt <= 0f || now < region.resetAt) continue;
            StopRegionBools(region);
            region.resetAt = 0f;
        }
        RefreshActivePriority(now);
    }

    void StopLowerPriorityHover(int incomingPriority)
    {
        if (incomingPriority <= PriorityFor(AvatarInteractionType.Hover)) return;
        for (int i = 0; i < regions.Count; i++)
        {
            InteractionRegion region = regions[i];
            if (region == null || region.interactionType != AvatarInteractionType.Hover || !region.wasHovering) continue;
            region.wasHovering = false;
            StopRegionBools(region);
        }
    }

    void StopRegionBools(InteractionRegion region)
    {
        if (avatarAnimator != null && region.bodyBoolActive && region.hasAnimationParameter)
            avatarAnimator.SetBool(region.animationHash, false);
        if (avatarAnimator != null && region.faceBoolActive && region.hasFaceParameter)
            avatarAnimator.SetBool(region.faceHash, false);
        region.bodyBoolActive = false;
        region.faceBoolActive = false;
    }

    bool CanTriggerRegion(InteractionRegion region, float now, bool checkCooldown)
    {
        if (region == null || avatarAnimator == null) return false;
        if (RequiresPointerBone(region.interactionType) && region.bone == null) return false;
        if (checkCooldown && now < region.cooldownUntil) return false;
        if (IsGloballyBlocked()) return false;

        AnimatorStateInfo state = avatarAnimator.GetCurrentAnimatorStateInfo(0);
        HashSet<int> whitelist = region.allowedStateHashes != null && region.allowedStateHashes.Count > 0
            ? region.allowedStateHashes
            : globalAllowedStateHashes;
        return whitelist.Count == 0 || whitelist.Contains(state.shortNameHash);
    }

    bool IsGloballyBlocked()
    {
        if (MenuActions.IsReactionBlocked()) return true;
        AvatarPoseController poseController = GetComponent<AvatarPoseController>();
        if (poseController != null && poseController.IsPoseActive) return true;
        return GetAnimatorBool(IsCustomDancingHash)
               || GetAnimatorBool(IsDancingHash)
               || GetAnimatorBool(IsBigScreenHash)
               || GetAnimatorBool(IsBigScreenSaverHash)
               || GetAnimatorBool(IsBigScreenAlarmHash)
               || GetAnimatorBool(IsWaitingForDancingHash);
    }

    bool GetAnimatorBool(int hash)
    {
        AnimatorControllerParameterType type;
        return parameterTypes.TryGetValue(hash, out type)
               && type == AnimatorControllerParameterType.Bool
               && avatarAnimator.GetBool(hash);
    }

    int FindBestSingleClickRegion(Vector2 pointer)
    {
        int best = -1;
        float bestDistance = float.MaxValue;
        for (int i = 0; i < regions.Count; i++)
        {
            InteractionRegion region = regions[i];
            if (region == null || !IsSingleClickType(region.interactionType) || !IsPointerInside(region, pointer)) continue;
            float distance = (pointer - GetRegionScreenCenter(region)).sqrMagnitude;
            if (distance >= bestDistance) continue;
            best = i;
            bestDistance = distance;
        }
        return best;
    }

    int FindBestRegion(Vector2 pointer, AvatarInteractionType type)
    {
        int best = -1;
        float bestDistance = float.MaxValue;
        for (int i = 0; i < regions.Count; i++)
        {
            InteractionRegion region = regions[i];
            if (region == null || region.interactionType != type || !IsPointerInside(region, pointer)) continue;
            float distance = (pointer - GetRegionScreenCenter(region)).sqrMagnitude;
            if (distance >= bestDistance) continue;
            best = i;
            bestDistance = distance;
        }
        return best;
    }

    bool IsPointerInside(InteractionRegion region, Vector2 pointer)
    {
        if (region.bone == null || interactionCamera == null) return false;
        Vector3 world = GetRegionWorldCenter(region);
        Vector3 screen = interactionCamera.WorldToScreenPoint(world);
        if (screen.z <= 0f) return false;
        Vector3 edge = interactionCamera.WorldToScreenPoint(
            world + interactionCamera.transform.right * region.interactionRadius * region.bone.lossyScale.magnitude);
        float radiusSquared = ((Vector2)screen - (Vector2)edge).sqrMagnitude;
        return (pointer - (Vector2)screen).sqrMagnitude <= radiusSquared;
    }

    Vector2 GetRegionScreenCenter(InteractionRegion region)
    {
        return interactionCamera.WorldToScreenPoint(GetRegionWorldCenter(region));
    }

    Vector3 GetRegionWorldCenter(InteractionRegion region)
    {
        return region.bone.position + region.bone.TransformVector(region.localOffset) + region.worldOffset;
    }

    void UpdateRandomReaction(float now)
    {
        if (!enableRandomReactions || now < nextRandomReactionAt) return;
        ScheduleRandomReaction();
        if (pointerDown || pendingSingleRegionIndex >= 0 || activePriority > 0 || !IsBaseIdle()) return;

        int count = 0;
        for (int i = 0; i < regions.Count; i++)
            if (regions[i] != null && regions[i].interactionType == AvatarInteractionType.Random) count++;
        if (count == 0) return;

        int selected = UnityEngine.Random.Range(0, count);
        for (int i = 0; i < regions.Count; i++)
        {
            if (regions[i] == null || regions[i].interactionType != AvatarInteractionType.Random) continue;
            if (selected-- == 0) { TriggerRegion(i, now); return; }
        }
    }

    public bool TriggerVoiceReaction()
    {
        float now = Time.unscaledTime;
        for (int i = 0; i < regions.Count; i++)
            if (regions[i] != null && regions[i].interactionType == AvatarInteractionType.Voice)
                return TriggerRegion(i, now);
        return false;
    }

    public bool TriggerInteraction(string regionName)
    {
        if (string.IsNullOrEmpty(regionName)) return false;
        float now = Time.unscaledTime;
        for (int i = 0; i < regions.Count; i++)
            if (regions[i] != null && string.Equals(regions[i].name, regionName, StringComparison.OrdinalIgnoreCase))
                return TriggerRegion(i, now);
        return false;
    }

    void PlayVoice(InteractionRegion region)
    {
        if (voiceAudioSource == null || voiceAudioSource.isPlaying || region.voiceClips == null || region.voiceClips.Count == 0)
            return;
        AudioClip clip = region.voiceClips[UnityEngine.Random.Range(0, region.voiceClips.Count)];
        if (clip == null) return;
        voiceAudioSource.clip = clip;
        voiceAudioSource.Play();
    }

    bool IsBaseIdle()
    {
        AnimatorStateInfo state = avatarAnimator.GetCurrentAnimatorStateInfo(0);
        return state.IsName("Idle") || state.shortNameHash == Animator.StringToHash("Idle");
    }

    void ScheduleRandomReaction()
    {
        float min = Mathf.Max(1f, minRandomReactionDelay);
        float max = Mathf.Max(min, maxRandomReactionDelay);
        nextRandomReactionAt = Time.unscaledTime + UnityEngine.Random.Range(min, max);
    }

    void RefreshActivePriority(float now)
    {
        if (now < activeUntil) return;
        activePriority = 0;
        for (int i = 0; i < regions.Count; i++)
            if (regions[i] != null && regions[i].wasHovering)
                activePriority = Mathf.Max(activePriority, PriorityFor(AvatarInteractionType.Hover));
    }

    void CancelPointerGesture()
    {
        pointerDown = false;
        dragTriggered = false;
        dragRegionIndex = -1;
    }

    void ResetInteractionState(bool resetAnimator)
    {
        animationCoordinator?.SetPersistent(MaoAnimationOwner.Interaction, false);
        if (resetAnimator) ResetAnimatorBools();
        CancelPointerGesture();
        pendingSingleRegionIndex = -1;
        pendingSingleReleasedAt = -1f;
        lastReleasedAt = -1f;
        lastReleasedBone = HumanBodyBones.LastBone;
        activePriority = 0;
        activeUntil = 0f;
        for (int i = 0; i < regions.Count; i++)
            if (regions[i] != null) ResetRegionRuntime(regions[i], false);
    }

    void ResetAnimatorBools()
    {
        if (avatarAnimator == null) return;
        for (int i = 0; i < regions.Count; i++)
            if (regions[i] != null) StopRegionBools(regions[i]);
    }

    static void ResetRegionRuntime(InteractionRegion region, bool clearBone)
    {
        if (clearBone) region.bone = null;
        region.wasHovering = false;
        region.bodyBoolActive = false;
        region.faceBoolActive = false;
        region.resetAt = 0f;
        region.cooldownUntil = 0f;
        if (region.patDetector != null) region.patDetector.Reset();
    }

    bool TryResolveParameter(
        int hash,
        AvatarInteractionParameterMode requestedMode,
        out AnimatorControllerParameterType resolvedType)
    {
        resolvedType = AnimatorControllerParameterType.Bool;
        AnimatorControllerParameterType actual;
        if (hash == 0 || !parameterTypes.TryGetValue(hash, out actual)) return false;
        if (actual != AnimatorControllerParameterType.Bool && actual != AnimatorControllerParameterType.Trigger) return false;
        if (requestedMode == AvatarInteractionParameterMode.Bool && actual != AnimatorControllerParameterType.Bool) return false;
        if (requestedMode == AvatarInteractionParameterMode.Trigger && actual != AnimatorControllerParameterType.Trigger) return false;
        resolvedType = actual;
        return true;
    }

    int ResolveLayer(string layerName)
    {
        if (string.IsNullOrEmpty(layerName)) return 0;
        return avatarAnimator.GetLayerIndex(layerName);
    }

    static void AddStateHashes(List<string> names, HashSet<int> destination)
    {
        if (names == null) return;
        for (int i = 0; i < names.Count; i++)
            if (!string.IsNullOrEmpty(names[i])) destination.Add(Animator.StringToHash(names[i]));
    }

    static bool IsSingleClickType(AvatarInteractionType type)
    {
        return type == AvatarInteractionType.Click
               || type == AvatarInteractionType.Poke
               || type == AvatarInteractionType.Hand
               || type == AvatarInteractionType.Wave
               || type == AvatarInteractionType.Face;
    }

    static bool RequiresPointerBone(AvatarInteractionType type)
    {
        return type != AvatarInteractionType.Random && type != AvatarInteractionType.Voice;
    }

    static int PriorityFor(AvatarInteractionType type)
    {
        switch (type)
        {
            case AvatarInteractionType.Drag: return 60;
            case AvatarInteractionType.HeadPat: return 50;
            case AvatarInteractionType.DoubleClick: return 40;
            case AvatarInteractionType.Click:
            case AvatarInteractionType.Poke:
            case AvatarInteractionType.Hand:
            case AvatarInteractionType.Wave:
            case AvatarInteractionType.Face:
            case AvatarInteractionType.Voice: return 30;
            case AvatarInteractionType.Hover: return 20;
            case AvatarInteractionType.Random: return 10;
            default: return 0;
        }
    }

#if UNITY_EDITOR
    void OnDrawGizmosSelected()
    {
        if (!showDebugInteractionRegions || regions == null) return;
        Animator animator = avatarAnimator != null ? avatarAnimator : GetComponentInChildren<Animator>(true);
        if (animator == null || !animator.isHuman) return;

        for (int i = 0; i < regions.Count; i++)
        {
            InteractionRegion region = regions[i];
            if (region == null) continue;
            Transform bone = animator.GetBoneTransform(region.targetBone);
            if (bone == null) continue;
            Vector3 center = bone.position + bone.TransformVector(region.localOffset) + region.worldOffset;
            Gizmos.color = debugRegionColor;
            Gizmos.DrawWireSphere(center, region.interactionRadius * bone.lossyScale.magnitude);
        }
    }
#endif
}
