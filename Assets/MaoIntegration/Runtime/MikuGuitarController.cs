using System.Collections;
using MaoIntegration;
using UnityEngine;

[DisallowMultipleComponent]
public sealed class MikuGuitarController : MonoBehaviour
{
    private static readonly int GuitarPlayingParameter = Animator.StringToHash("MaoGuitarPlaying");

    [Header("Guitar Asset")]
    [SerializeField] private string guitarResourcePath = "MaoGuitar/Guitar";

    [Header("Placement Fine Tuning")]
    [SerializeField] private Vector3 positionOffset = Vector3.zero;
    [SerializeField] private Vector3 rotationOffset = Vector3.zero;
    [SerializeField, Min(0.01f)] private float scaleMultiplier = 1f;
    [SerializeField, Range(0.2f, 0.8f)] private float handSpanFractionOfGuitar = 0.25f;
    [SerializeField, Range(0f, 45f)] private float neckRaiseDegrees = 20f;
    [SerializeField, Range(-0.2f, 0.2f)] private float stringLineHeightFraction = 0.07f;
    [SerializeField, Range(-0.2f, 0.2f)] private float stringLineDepthFraction = -0.015f;
    [SerializeField] private Vector3 leftHandGripOffset = Vector3.zero;
    [SerializeField] private Vector3 rightHandGripOffset = Vector3.zero;

    [Header("Hand IK")]
    [SerializeField, Range(0f, 1f)] private float maximumIkWeight = 0.86f;
    [SerializeField, Min(0.01f)] private float ikBlendInSeconds = 0.35f;
    [SerializeField, Min(0.01f)] private float ikBlendOutSeconds = 0.25f;

    [Header("Debug")]
    [SerializeField] private bool drawDebugGizmos;

    private Animator animator;
    private AvatarAnimatorController avatarAnimatorController;
    private AvatarLocomotionController locomotion;
    private MaoAnimationCoordinator animationCoordinator;
    private Transform chest;
    private Transform leftHand;
    private Transform rightHand;
    private Transform guitarAnchor;
    private Transform guitarVisualRoot;
    private Transform leftGrip;
    private Transform rightGrip;
    private GameObject guitarInstance;
    private Coroutine visibilityRoutine;
    private int guitarLayerIndex = -1;
    private float currentIkWeight;
    private float targetIkWeight;
    private bool isPlaying;
    private bool savedDancingEnabled;
    private bool savedDraggingOverride;
    private bool performanceOverridesApplied;
    private bool warnedInvalidSetup;
    private float baseGuitarLength = 1f;
    private Vector3 playRootPosition;
    private Vector3 playLeftHandPosition;
    private Vector3 playRightHandPosition;

    public bool IsReady => animator != null && animator.isHuman && guitarLayerIndex >= 0 && guitarInstance != null;
    public bool IsPlayingGuitar => isPlaying;
    public bool IsGuitarVisible => guitarInstance != null && guitarInstance.activeSelf;
    public float CurrentIkWeight => currentIkWeight;
    public float NeckRise => guitarAnchor != null && chest != null
        ? Vector3.Dot(guitarAnchor.right.normalized, chest.up.normalized)
        : 0f;
    public float HandGripError => leftHand != null && rightHand != null && leftGrip != null && rightGrip != null
        ? Vector3.Distance(leftHand.position, leftGrip.position) +
          Vector3.Distance(rightHand.position, rightGrip.position)
        : float.PositiveInfinity;
    public float RootDriftDistance => animator != null
        ? Vector3.Distance(playRootPosition, animator.transform.position)
        : 0f;
    public bool IsAnimationActive
    {
        get
        {
            if (!IsReady) return false;
            AnimatorStateInfo current = animator.GetCurrentAnimatorStateInfo(guitarLayerIndex);
            AnimatorStateInfo next = animator.GetNextAnimatorStateInfo(guitarLayerIndex);
            return current.IsName("Play Guitar") || next.IsName("Play Guitar");
        }
    }

    public string RuntimeDebugSnapshot
    {
        get
        {
            if (!IsReady) return "ready=false";
            AnimatorStateInfo state = animator.GetCurrentAnimatorStateInfo(guitarLayerIndex);
            Bounds propBounds = CalculateWorldBounds(guitarInstance);
            float handDisplacement = leftHand != null && rightHand != null
                ? Vector3.Distance(playLeftHandPosition, leftHand.position) +
                  Vector3.Distance(playRightHandPosition, rightHand.position)
                : -1f;
            return "layerWeight=" + animator.GetLayerWeight(guitarLayerIndex).ToString("0.000") +
                   ";state=" + (state.IsName("Play Guitar") ? "Play Guitar" : state.fullPathHash.ToString()) +
                   ";normalized=" + state.normalizedTime.ToString("0.000") +
                   ";handDisplacement=" + handDisplacement.ToString("0.000") +
                   ";chestScale=" + FormatVector(chest != null ? chest.lossyScale : Vector3.zero) +
                   ";propScale=" + FormatVector(guitarVisualRoot != null ? guitarVisualRoot.localScale : Vector3.zero) +
                   ";propBounds=" + FormatVector(propBounds.size) +
                   ";anchor=" + FormatVector(guitarAnchor != null ? guitarAnchor.position : Vector3.zero) +
                   ";leftHand=" + FormatVector(leftHand != null ? leftHand.position : Vector3.zero) +
                   ";rightHand=" + FormatVector(rightHand != null ? rightHand.position : Vector3.zero) +
                   ";neckRise=" + NeckRise.ToString("0.000") +
                   ";gripError=" + HandGripError.ToString("0.000");
        }
    }

    public void Bind(Animator targetAnimator)
    {
        if (animator == targetAnimator && IsReady) return;
        animationCoordinator?.SetPersistent(MaoAnimationOwner.Guitar, false);
        StopImmediately();
        if (animator != targetAnimator && guitarAnchor != null)
        {
            Destroy(guitarAnchor.gameObject);
            guitarAnchor = guitarVisualRoot = leftGrip = rightGrip = null;
            guitarInstance = null;
        }
        animator = targetAnimator;
        avatarAnimatorController = animator != null
            ? animator.GetComponentInParent<AvatarAnimatorController>()
            : null;
        locomotion = animator != null
            ? animator.GetComponentInParent<AvatarLocomotionController>()
            : null;
        animationCoordinator = animator != null
            ? animator.GetComponentInParent<MaoAnimationCoordinator>()
            : null;
        guitarLayerIndex = -1;
        chest = leftHand = rightHand = null;

        if (animator == null || !animator.isHuman || animator.avatar == null)
        {
            WarnInvalidSetup("a valid Humanoid Animator was not found");
            return;
        }

        animator.applyRootMotion = false;
        guitarLayerIndex = animator.GetLayerIndex("Mao Guitar Layer");
        chest = animator.GetBoneTransform(HumanBodyBones.UpperChest) ??
                animator.GetBoneTransform(HumanBodyBones.Chest);
        leftHand = animator.GetBoneTransform(HumanBodyBones.LeftHand);
        rightHand = animator.GetBoneTransform(HumanBodyBones.RightHand);
        if (guitarLayerIndex < 0 || !HasBoolParameter("MaoGuitarPlaying") ||
            chest == null || leftHand == null || rightHand == null)
        {
            WarnInvalidSetup("the Animator layer, parameter, or required Humanoid bones are missing");
            return;
        }
        animator.SetLayerWeight(guitarLayerIndex, 1f);

        EnsureGuitarInstance();
        if (guitarInstance == null)
        {
            WarnInvalidSetup("Resources/" + guitarResourcePath + " could not be loaded");
            return;
        }
        warnedInvalidSetup = false;
        Debug.Log("[Mao Guitar] controller=ready layer=" + guitarLayerIndex + " prop=" + guitarInstance.name);
    }

    public bool PlayGuitar()
    {
        if (!IsReady)
        {
            Bind(animator != null ? animator : GetComponent<Animator>());
            if (!IsReady) return false;
        }
        if (isPlaying) return true;
        if (animationCoordinator != null && !animationCoordinator.TrySetPersistent(MaoAnimationOwner.Guitar, true))
            return false;

        isPlaying = true;
        playRootPosition = animator.transform.position;
        playLeftHandPosition = leftHand.position;
        playRightHandPosition = rightHand.position;
        targetIkWeight = maximumIkWeight;
        ApplyPerformanceOverrides();
        AlignGuitarToCurrentPose();
        guitarInstance.SetActive(true);
        animator.applyRootMotion = false;
        animator.SetLayerWeight(guitarLayerIndex, 1f);
        animator.SetBool(GuitarPlayingParameter, true);
        if (visibilityRoutine != null) StopCoroutine(visibilityRoutine);
        visibilityRoutine = null;
        Debug.Log("[Mao Guitar] event=play requested=true prop_visible=true");
        return true;
    }

    public bool StopGuitar()
    {
        if (!isPlaying)
        {
            animationCoordinator?.SetPersistent(MaoAnimationOwner.Guitar, false);
            if (animator != null && HasBoolParameter("MaoGuitarPlaying"))
                animator.SetBool(GuitarPlayingParameter, false);
            return true;
        }

        isPlaying = false;
        animationCoordinator?.SetPersistent(MaoAnimationOwner.Guitar, false);
        targetIkWeight = 0f;
        if (animator != null && HasBoolParameter("MaoGuitarPlaying"))
            animator.SetBool(GuitarPlayingParameter, false);
        if (visibilityRoutine != null) StopCoroutine(visibilityRoutine);
        visibilityRoutine = StartCoroutine(HideAfterBlendOut());
        RestorePerformanceOverrides();
        Debug.Log("[Mao Guitar] event=stop requested=true");
        return true;
    }

    private void LateUpdate()
    {
        float duration = targetIkWeight > currentIkWeight ? ikBlendInSeconds : ikBlendOutSeconds;
        currentIkWeight = Mathf.MoveTowards(
            currentIkWeight,
            targetIkWeight,
            Time.unscaledDeltaTime * maximumIkWeight / Mathf.Max(0.01f, duration));
    }

    private void OnAnimatorIK(int layerIndex)
    {
        if (layerIndex != guitarLayerIndex || animator == null || leftGrip == null || rightGrip == null) return;
        if (isPlaying) AlignGuitarToCurrentPose();
        ApplyHandIk(AvatarIKGoal.LeftHand, leftGrip);
        ApplyHandIk(AvatarIKGoal.RightHand, rightGrip);
    }

    private void ApplyHandIk(AvatarIKGoal goal, Transform grip)
    {
        animator.SetIKPositionWeight(goal, currentIkWeight);
        animator.SetIKRotationWeight(goal, currentIkWeight);
        if (currentIkWeight <= 0f) return;
        animator.SetIKPosition(goal, grip.position);
        animator.SetIKRotation(goal, grip.rotation);
    }

    private IEnumerator HideAfterBlendOut()
    {
        yield return new WaitForSecondsRealtime(Mathf.Max(0.28f, ikBlendOutSeconds + 0.05f));
        if (!isPlaying && guitarInstance != null) guitarInstance.SetActive(false);
        visibilityRoutine = null;
        Debug.Log("[Mao Guitar] event=stopped prop_visible=false");
    }

    private void ApplyPerformanceOverrides()
    {
        if (performanceOverridesApplied) return;
        if (locomotion != null) locomotion.SetCursorFollowEnabled(false, false);
        if (avatarAnimatorController != null)
        {
            savedDancingEnabled = avatarAnimatorController.enableDancing;
            savedDraggingOverride = avatarAnimatorController.BlockDraggingOverride;
            avatarAnimatorController.enableDancing = false;
            avatarAnimatorController.BlockDraggingOverride = true;
        }
        performanceOverridesApplied = true;
    }

    private void RestorePerformanceOverrides()
    {
        if (!performanceOverridesApplied) return;
        if (avatarAnimatorController != null)
        {
            avatarAnimatorController.enableDancing = savedDancingEnabled;
            avatarAnimatorController.BlockDraggingOverride = savedDraggingOverride;
        }
        performanceOverridesApplied = false;
    }

    private void EnsureGuitarInstance()
    {
        if (guitarInstance != null) return;
        GameObject prefab = Resources.Load<GameObject>(guitarResourcePath);
        if (prefab == null) return;

        guitarAnchor = new GameObject("Mao Guitar Anchor").transform;
        guitarAnchor.SetParent(chest, false);
        guitarVisualRoot = new GameObject("Mao Guitar Visual Root").transform;
        guitarVisualRoot.SetParent(guitarAnchor, false);
        guitarInstance = Instantiate(prefab, guitarVisualRoot, false);
        guitarInstance.name = "Mao Electric Guitar";
        DisablePropPhysicsAndAnimation(guitarInstance);
        CanonicalizeAndCenterModel(guitarVisualRoot, guitarInstance.transform);
        baseGuitarLength = Mathf.Max(
            0.001f,
            CalculateBoundsInLocalSpace(guitarVisualRoot, guitarInstance).size.x);

        leftGrip = new GameObject("Left Hand Grip").transform;
        leftGrip.SetParent(guitarAnchor, false);
        rightGrip = new GameObject("Right Hand Grip").transform;
        rightGrip.SetParent(guitarAnchor, false);
        guitarInstance.SetActive(false);
    }

    private void AlignGuitarToCurrentPose()
    {
        if (guitarAnchor == null || leftHand == null || rightHand == null) return;
        Vector3 handAxis = leftHand.position - rightHand.position;
        float handDistance = handAxis.magnitude;
        if (handDistance < 0.02f) return;

        Vector3 referenceUp = chest != null ? chest.up : Vector3.up;
        Vector3 fallbackHorizontal = chest != null ? chest.right : Vector3.right;
        Vector3 xAxis = MaoGuitarPoseLogic.BuildRaisedNeckAxis(
            handAxis,
            referenceUp,
            fallbackHorizontal,
            neckRaiseDegrees);
        Vector3 forward = Vector3.Cross(xAxis, referenceUp).normalized;
        if (forward.sqrMagnitude < 0.1f) forward = chest != null ? chest.forward : Vector3.forward;
        Vector3 up = Vector3.Cross(forward, xAxis).normalized;

        guitarAnchor.position = (leftHand.position + rightHand.position) * 0.5f;
        guitarAnchor.rotation = Quaternion.LookRotation(forward, up) * Quaternion.Euler(rotationOffset);

        float localHandDistance = Vector3.Distance(
            guitarAnchor.InverseTransformPoint(leftHand.position),
            guitarAnchor.InverseTransformPoint(rightHand.position));
        float uniformScale = localHandDistance /
                             Mathf.Max(0.05f, handSpanFractionOfGuitar) /
                             baseGuitarLength;
        guitarVisualRoot.localScale = Vector3.one * uniformScale * scaleMultiplier;

        float scaledLength = baseGuitarLength * uniformScale * scaleMultiplier;
        MaoGuitarGripPair grips = MaoGuitarPoseLogic.BuildGripPair(
            scaledLength * handSpanFractionOfGuitar,
            scaledLength * stringLineHeightFraction,
            scaledLength * stringLineDepthFraction);
        leftGrip.localPosition = grips.Left + leftHandGripOffset;
        rightGrip.localPosition = grips.Right + rightHandGripOffset;
        Vector3 gripMidpoint = (leftGrip.localPosition + rightGrip.localPosition) * 0.5f;
        Vector3 handMidpoint = (leftHand.position + rightHand.position) * 0.5f;
        guitarAnchor.position = handMidpoint - guitarAnchor.TransformVector(gripMidpoint) +
                                guitarAnchor.TransformVector(positionOffset);

        leftGrip.rotation = leftHand.rotation;
        rightGrip.rotation = rightHand.rotation;
    }

    private void CanonicalizeAndCenterModel(Transform visualRoot, Transform model)
    {
        Bounds initial = CalculateBoundsInLocalSpace(visualRoot, guitarInstance);
        Vector3 size = initial.size;
        if (size.y > size.x && size.y >= size.z)
            model.localRotation = Quaternion.Euler(0f, 0f, -90f);
        else if (size.z > size.x && size.z > size.y)
            model.localRotation = Quaternion.Euler(0f, 90f, 0f);

        // The supplied prop has its body/neck direction opposite to the Humanoid
        // guitar motion. Rotate the visual prop; never mirror the VRM hierarchy.
        model.localRotation *= Quaternion.Euler(0f, 180f, 0f);

        Bounds canonical = CalculateBoundsInLocalSpace(visualRoot, guitarInstance);
        model.localPosition -= canonical.center;
    }

    private static Bounds CalculateBoundsInLocalSpace(Transform root, GameObject model)
    {
        Renderer[] renderers = model.GetComponentsInChildren<Renderer>(true);
        bool initialized = false;
        Bounds result = new Bounds(Vector3.zero, Vector3.zero);
        for (int index = 0; index < renderers.Length; index++)
        {
            Bounds world = renderers[index].bounds;
            Vector3 min = world.min;
            Vector3 max = world.max;
            for (int corner = 0; corner < 8; corner++)
            {
                Vector3 point = new Vector3(
                    (corner & 1) == 0 ? min.x : max.x,
                    (corner & 2) == 0 ? min.y : max.y,
                    (corner & 4) == 0 ? min.z : max.z);
                point = root.InverseTransformPoint(point);
                if (!initialized)
                {
                    result = new Bounds(point, Vector3.zero);
                    initialized = true;
                }
                else result.Encapsulate(point);
            }
        }
        return result;
    }

    private static Bounds CalculateWorldBounds(GameObject model)
    {
        Renderer[] renderers = model.GetComponentsInChildren<Renderer>(true);
        if (renderers.Length == 0) return new Bounds(Vector3.zero, Vector3.zero);
        Bounds result = renderers[0].bounds;
        for (int index = 1; index < renderers.Length; index++) result.Encapsulate(renderers[index].bounds);
        return result;
    }

    private static string FormatVector(Vector3 value)
    {
        return value.x.ToString("0.000") + "," +
               value.y.ToString("0.000") + "," +
               value.z.ToString("0.000");
    }

    private static void DisablePropPhysicsAndAnimation(GameObject prop)
    {
        Animator[] propAnimators = prop.GetComponentsInChildren<Animator>(true);
        for (int index = 0; index < propAnimators.Length; index++) propAnimators[index].enabled = false;
        Rigidbody[] bodies = prop.GetComponentsInChildren<Rigidbody>(true);
        for (int index = 0; index < bodies.Length; index++)
        {
            bodies[index].isKinematic = true;
            bodies[index].useGravity = false;
        }
        Collider[] colliders = prop.GetComponentsInChildren<Collider>(true);
        for (int index = 0; index < colliders.Length; index++) colliders[index].enabled = false;
    }

    private bool HasBoolParameter(string parameterName)
    {
        if (animator == null) return false;
        AnimatorControllerParameter[] parameters = animator.parameters;
        for (int index = 0; index < parameters.Length; index++)
            if (parameters[index].type == AnimatorControllerParameterType.Bool &&
                parameters[index].name == parameterName)
                return true;
        return false;
    }

    private void WarnInvalidSetup(string reason)
    {
        if (warnedInvalidSetup) return;
        warnedInvalidSetup = true;
        Debug.LogWarning("[Mao Guitar] controller unavailable: " + reason + ". Commands remain safe and can be retried after avatar load.");
    }

    private void StopImmediately()
    {
        animationCoordinator?.SetPersistent(MaoAnimationOwner.Guitar, false);
        if (visibilityRoutine != null) StopCoroutine(visibilityRoutine);
        visibilityRoutine = null;
        isPlaying = false;
        targetIkWeight = currentIkWeight = 0f;
        if (animator != null && animator.isActiveAndEnabled && HasBoolParameter("MaoGuitarPlaying"))
            animator.SetBool(GuitarPlayingParameter, false);
        if (guitarInstance != null) guitarInstance.SetActive(false);
        RestorePerformanceOverrides();
    }

    private void OnDisable()
    {
        animationCoordinator?.SetPersistent(MaoAnimationOwner.Guitar, false);
        StopImmediately();
    }

    private void OnDestroy()
    {
        animationCoordinator?.SetPersistent(MaoAnimationOwner.Guitar, false);
        StopImmediately();
        if (guitarAnchor != null) Destroy(guitarAnchor.gameObject);
    }

    private void OnDrawGizmos()
    {
        if (!drawDebugGizmos || guitarAnchor == null) return;
        Gizmos.color = Color.cyan;
        Gizmos.DrawLine(guitarAnchor.position, guitarAnchor.position + guitarAnchor.right * 0.25f);
        if (leftGrip != null) Gizmos.DrawWireSphere(leftGrip.position, 0.012f);
        if (rightGrip != null) Gizmos.DrawWireSphere(rightGrip.position, 0.012f);
    }
}
