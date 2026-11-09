using MaoIntegration;
using UnityEngine;

[DisallowMultipleComponent]
public sealed class MikuIdleActionController : MonoBehaviour
{
    private const string LayerName = "Mao Idle Actions";
    private static readonly string[] StateNames =
    {
        "Angry", "Jab Cross", "Box Jump", "Jog In Circle", "Flying Kick"
    };

    private Animator animator;
    private int layerIndex = -1;
    private float startedAt;
    private bool playing;
    private MaoIdleActionKind currentAction;
    private Vector3 rootAtStart;
    private bool savedRootMotion;
    private bool hasSavedRootMotion;
    private AvatarAnimatorController avatarAnimatorController;
    private bool savedDancingEnabled;
    private bool savedDraggingOverride;
    private bool performanceOverridesApplied;
    private MaoAnimationCoordinator animationCoordinator;

    public bool IsReady => animator != null && animator.isHuman && layerIndex >= 0;
    public bool IsPlaying => playing;
    public MaoIdleActionKind CurrentAction => currentAction;
    public float RootDrift => animator != null ? Vector3.Distance(rootAtStart, animator.transform.position) : 0f;
    public bool IsAnimationActive
    {
        get
        {
            if (!IsReady || !playing) return false;
            string state = StateName(currentAction);
            AnimatorStateInfo current = animator.GetCurrentAnimatorStateInfo(layerIndex);
            AnimatorStateInfo next = animator.GetNextAnimatorStateInfo(layerIndex);
            return current.IsName(state) || current.IsName(LayerName + "." + state) ||
                   next.IsName(state) || next.IsName(LayerName + "." + state);
        }
    }

    public void Bind(Animator target)
    {
        if (animator == target && IsReady) return;
        animationCoordinator?.SetPersistent(MaoAnimationOwner.Idle, false);
        StopImmediately();
        animator = target;
        animationCoordinator = animator != null
            ? animator.GetComponentInParent<MaoAnimationCoordinator>()
            : null;
        layerIndex = animator != null ? animator.GetLayerIndex(LayerName) : -1;
        avatarAnimatorController = animator != null
            ? animator.GetComponentInParent<AvatarAnimatorController>()
            : null;
        if (animator == null || !animator.isHuman || layerIndex < 0) return;
        savedRootMotion = animator.applyRootMotion;
        hasSavedRootMotion = true;
        animator.applyRootMotion = false;
        animator.SetLayerWeight(layerIndex, 1f);
    }

    public bool Play(MaoIdleActionKind action)
    {
        if (!IsReady) Bind(animator != null ? animator : GetComponent<Animator>());
        if (!IsReady) return false;
        if (animationCoordinator != null && !animationCoordinator.TrySetPersistent(MaoAnimationOwner.Idle, true))
            return false;
        string stateName = StateName(action);
        // Do not call Animator.HasState here. The shipped Unity player runtime
        // does not export that newer API even though the editor reference
        // assembly lets it compile, which turns this path into a
        // MissingMethodException at runtime. CrossFade is the compatible
        // runtime probe; the controller will settle back to Idle if the state
        // is not present.
        currentAction = action;
        playing = true;
        startedAt = Time.unscaledTime;
        rootAtStart = animator.transform.position;
        ApplyPerformanceOverrides();
        if (!hasSavedRootMotion)
        {
            savedRootMotion = animator.applyRootMotion;
            hasSavedRootMotion = true;
        }
        animator.applyRootMotion = false;
        animator.SetLayerWeight(layerIndex, 1f);
        animator.CrossFadeInFixedTime(LayerName + "." + stateName, 0.18f, layerIndex, 0f);
        Debug.Log("[Mao Idle Actions] event=play action=" + stateName);
        return true;
    }

    public void Stop()
    {
        if (!IsReady || !playing) return;
        animator.CrossFadeInFixedTime(LayerName + ".Idle", 0.18f, layerIndex, 0f);
        playing = false;
        animationCoordinator?.SetPersistent(MaoAnimationOwner.Idle, false);
        RestorePerformanceOverrides();
    }

    private void Update()
    {
        if (!playing || !IsReady || Time.unscaledTime - startedAt < 0.25f) return;
        if (RootDrift > 0.20f)
        {
            Debug.LogWarning("[Mao Idle Actions] root_drift_guard=" + RootDrift.ToString("0.000"));
            Stop();
            return;
        }
        if (IsAnimationActive) return;
        AnimatorStateInfo state = animator.GetCurrentAnimatorStateInfo(layerIndex);
        bool settledInIdle = !animator.IsInTransition(layerIndex) &&
                             (state.IsName(LayerName + ".Idle") || state.IsName("Idle"));
        if (settledInIdle || Time.unscaledTime - startedAt > 12f)
        {
            playing = false;
            animationCoordinator?.SetPersistent(MaoAnimationOwner.Idle, false);
            RestorePerformanceOverrides();
        }
    }

    private static string StateName(MaoIdleActionKind action)
    {
        int index = Mathf.Clamp((int)action, 0, StateNames.Length - 1);
        return StateNames[index];
    }

    private void StopImmediately()
    {
        animationCoordinator?.SetPersistent(MaoAnimationOwner.Idle, false);
        playing = false;
        if (animator != null && hasSavedRootMotion) animator.applyRootMotion = savedRootMotion;
        RestorePerformanceOverrides();
        hasSavedRootMotion = false;
    }

    private void ApplyPerformanceOverrides()
    {
        if (performanceOverridesApplied) return;
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

    private void OnDisable() => StopImmediately();
}
