using System;
using System.Text;
using MaoIntegration;
using UnityEngine;

internal sealed class MaoVisualFacingRuntime
{
    private const string VisualRootName = "MaoLocomotionVisualRoot";

    private Animator animator;
    private Transform visualRoot;
    private Quaternion baseLocalRotation = Quaternion.identity;
    private Vector3 neutralForwardLocal = Vector3.forward;
    private float observedForwardSign = 1f;
    private bool observedForwardSignReady;
    private float currentYaw;
    private float desiredYaw;
    private float rightYaw = 90f;
    private float leftYaw = -90f;
    private bool calibratedFromCamera;
    private bool diagnosticsLogged;

    public float CurrentYaw => currentYaw;
    public float DesiredYaw => desiredYaw;
    public float RightYaw => rightYaw;
    public float LeftYaw => leftYaw;
    public Transform VisualRoot => visualRoot;

    public void Bind(Animator targetAnimator, Camera camera, float fallbackSideAngle, bool enableDebug)
    {
        if (animator == targetAnimator && visualRoot != null)
        {
            if (!calibratedFromCamera && camera != null) Calibrate(camera, fallbackSideAngle);
            if (enableDebug && !diagnosticsLogged) LogDiagnostics();
            return;
        }
        ResetRotation();
        animator = targetAnimator;
        visualRoot = null;
        observedForwardSignReady = false;
        calibratedFromCamera = false;
        diagnosticsLogged = false;
        currentYaw = 0f;
        desiredYaw = 0f;
        if (animator == null) return;

        animator.applyRootMotion = false;
        visualRoot = EnsureSeparateVisualRoot(animator.transform);
        baseLocalRotation = visualRoot.localRotation;
        Calibrate(camera, fallbackSideAngle);

        if (enableDebug) LogDiagnostics();
    }

    public void Tick(float deltaTime, float degreesPerSecond)
    {
        if (visualRoot == null) return;
        currentYaw = Mathf.MoveTowardsAngle(
            currentYaw,
            desiredYaw,
            Mathf.Max(90f, degreesPerSecond) * Mathf.Max(0f, deltaTime));
        visualRoot.localRotation = baseLocalRotation * Quaternion.Euler(0f, currentYaw, 0f);
    }

    public void FaceDirection(int direction)
    {
        desiredYaw = direction < 0 ? leftYaw : direction > 0 ? rightYaw : 0f;
    }

    public bool IsAligned(float toleranceDegrees)
    {
        return MaoFacingLogic.IsAligned(currentYaw, desiredYaw, toleranceDegrees);
    }

    public Vector3 PredictedVisualForwardWorld()
    {
        if (visualRoot == null) return Vector3.forward;
        Vector3 directionInParent =
            (baseLocalRotation * Quaternion.Euler(0f, currentYaw, 0f)) * neutralForwardLocal;
        return visualRoot.parent != null
            ? visualRoot.parent.TransformDirection(directionInParent).normalized
            : directionInParent.normalized;
    }

    public bool TryGetObservedVisualForwardWorld(out Vector3 visualForward)
    {
        visualForward = PredictedVisualForwardWorld();
        if (animator == null || animator.avatar == null || !animator.avatar.isHuman) return false;

        try
        {
            Transform hips = animator.GetBoneTransform(HumanBodyBones.Hips);
            Transform head = animator.GetBoneTransform(HumanBodyBones.Head);
            Transform leftLeg = animator.GetBoneTransform(HumanBodyBones.LeftUpperLeg);
            Transform rightLeg = animator.GetBoneTransform(HumanBodyBones.RightUpperLeg);
            if (hips == null || head == null || leftLeg == null || rightLeg == null) return false;

            Vector3 up = (head.position - hips.position).normalized;
            Vector3 right = (rightLeg.position - leftLeg.position).normalized;
            Vector3 candidate = Vector3.Cross(right, up).normalized;
            if (candidate.sqrMagnitude < 0.25f) return false;

            if (!observedForwardSignReady)
            {
                observedForwardSign = Vector3.Dot(candidate, PredictedVisualForwardWorld()) >= 0f ? 1f : -1f;
                observedForwardSignReady = true;
            }

            visualForward = candidate * observedForwardSign;
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    public void ResetRotation()
    {
        if (visualRoot != null) visualRoot.localRotation = baseLocalRotation;
        currentYaw = 0f;
        desiredYaw = 0f;
    }

    private void Calibrate(Camera camera, float fallbackSideAngle)
    {
        Camera resolvedCamera = camera != null ? camera : Camera.main;
        if (resolvedCamera == null)
        {
            rightYaw = Mathf.Abs(fallbackSideAngle);
            leftYaw = -rightYaw;
            return;
        }

        Vector3 worldUp = visualRoot.up;
        Vector3 neutralWorld = Vector3.ProjectOnPlane(-resolvedCamera.transform.forward, worldUp).normalized;
        Vector3 rightWorld = Vector3.ProjectOnPlane(resolvedCamera.transform.right, worldUp).normalized;
        if (neutralWorld.sqrMagnitude < 0.25f || rightWorld.sqrMagnitude < 0.25f) return;

        Transform parent = visualRoot.parent;
        Vector3 neutralParent = parent != null ? parent.InverseTransformDirection(neutralWorld) : neutralWorld;
        Vector3 rightParent = parent != null ? parent.InverseTransformDirection(rightWorld) : rightWorld;
        neutralForwardLocal = Quaternion.Inverse(baseLocalRotation) * neutralParent.normalized;
        Vector3 rightLocal = Quaternion.Inverse(baseLocalRotation) * rightParent.normalized;

        rightYaw = MaoFacingLogic.CalculateYawDegrees(
            neutralForwardLocal.x,
            neutralForwardLocal.z,
            rightLocal.x,
            rightLocal.z);
        leftYaw = MaoFacingLogic.CalculateYawDegrees(
            neutralForwardLocal.x,
            neutralForwardLocal.z,
            -rightLocal.x,
            -rightLocal.z);
        calibratedFromCamera = true;
    }

    private static Transform EnsureSeparateVisualRoot(Transform modelRoot)
    {
        if (modelRoot.parent != null && modelRoot.parent.name == VisualRootName)
            return modelRoot.parent;

        Transform previousParent = modelRoot.parent;
        var wrapper = new GameObject(VisualRootName);
        wrapper.layer = modelRoot.gameObject.layer;
        Transform root = wrapper.transform;
        root.SetParent(previousParent, false);
        root.localPosition = modelRoot.localPosition;
        root.localRotation = modelRoot.localRotation;
        root.localScale = modelRoot.localScale;

        modelRoot.SetParent(root, false);
        modelRoot.localPosition = Vector3.zero;
        modelRoot.localRotation = Quaternion.identity;
        modelRoot.localScale = Vector3.one;
        return root;
    }

    private void LogDiagnostics()
    {
        diagnosticsLogged = true;
        Debug.Log("[Mao Locomotion Axis] animator=" + HierarchyPath(animator.transform) +
                  " visual_root=" + HierarchyPath(visualRoot) +
                  " parent_rotation=" +
                  (visualRoot.parent != null ? visualRoot.parent.eulerAngles.ToString("F1") : "none") +
                  " right_yaw=" + rightYaw.ToString("0.0") +
                  " left_yaw=" + leftYaw.ToString("0.0") +
                  " root_motion=" + animator.applyRootMotion);
    }

    private static string HierarchyPath(Transform value)
    {
        if (value == null) return "null";
        var path = new StringBuilder(value.name);
        Transform parent = value.parent;
        while (parent != null)
        {
            path.Insert(0, parent.name + "/");
            parent = parent.parent;
        }
        return path.ToString();
    }
}
