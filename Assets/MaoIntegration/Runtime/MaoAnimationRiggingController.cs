using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

/// <summary>
/// Optional bridge to Unity Animation Rigging. The bridge deliberately uses
/// reflection so a player built without the package (or with a stripped
/// AnimationModule) keeps the existing Animator path instead of failing type
/// loading at startup. A full player build that contains the package resolves
/// and builds the same head-look rig without changing this assembly boundary.
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(Animator))]
public sealed class MaoAnimationRiggingController : MonoBehaviour
{
    [Header("Head Look")]
    [SerializeField] private bool enableHeadLookRig = true;
    [SerializeField, Range(0f, 1f)] private float maxWeight = 0.35f;
    [SerializeField, Range(0f, 60f)] private float yawLimit = 24f;
    [SerializeField, Range(0f, 45f)] private float pitchLimit = 15f;
    [SerializeField] private string headAimAxis = "Z";

    private Animator animator;
    private Component rigBuilder;
    private Component headRig;
    private Transform lookTarget;
    private Transform head;
    private Vector2 targetScreen;
    private float requestedWeight;
    private bool targetValid;
    private bool suppress;
    private bool ready;
    private bool loggedFailure;
    private bool initializationStarted;
    private Type rigBuilderType;
    private Type rigType;
    private Type multiAimType;
    private Type rigLayerType;

    public bool IsReady => ready && rigBuilder != null && IsGraphValid();
    public bool IsEnabled => enableHeadLookRig;
    public float CurrentWeight => headRig != null ? ReadFloat(headRig, "weight") : 0f;

    public static MaoAnimationRiggingController Ensure(GameObject model, Animator value)
    {
        if (model == null || value == null || !value.isHuman) return null;
        MaoAnimationRiggingController controller = value.GetComponent<MaoAnimationRiggingController>();
        if (controller == null) controller = value.gameObject.AddComponent<MaoAnimationRiggingController>();
        controller.Bind(value);
        return controller;
    }

    private void Awake() { animator = GetComponent<Animator>(); }
    private void Start() { BeginInitialization(); }

    public void Bind(Animator value)
    {
        if (value == null || !value.isHuman) return;
        animator = value;
        if (isActiveAndEnabled) BeginInitialization();
    }

    public void SetLookTarget(Vector2 screenPosition, float interest, bool isSuppressed)
    {
        targetScreen = screenPosition;
        MaoRiggingLookPose pose = MaoRiggingLookLogic.Calculate(
            screenPosition, Screen.width, Screen.height, interest, yawLimit, pitchLimit);
        targetValid = pose.Valid;
        requestedWeight = pose.Valid && IsFinite(maxWeight) ? pose.Weight * Mathf.Clamp01(maxWeight) : 0f;
        suppress = isSuppressed;
    }

    public void SetHeadLookEnabled(bool value)
    {
        enableHeadLookRig = value;
        if (!value) requestedWeight = 0f;
        else if (!ready)
        {
            initializationStarted = false;
            BeginInitialization();
        }
    }

    private void BeginInitialization()
    {
        if (initializationStarted) return;
        initializationStarted = true;
        StartCoroutine(InitializeAfterAnimatorRebind());
    }

    private IEnumerator InitializeAfterAnimatorRebind()
    {
        yield return null;
        yield return null;
        for (int attempt = 0; attempt < 4; attempt++)
        {
            yield return null;
            if (BuildHeadLookRig()) yield break;
        }
        initializationStarted = false;
    }

    private bool BuildHeadLookRig()
    {
        ready = false;
        if (!enableHeadLookRig || animator == null || !animator.isHuman) return false;
        head = animator.GetBoneTransform(HumanBodyBones.Head);
        if (head == null)
        {
            LogBuildFailure("humanoid-head-missing");
            return false;
        }

        if (!ResolvePackageTypes())
        {
            LogBuildFailure("runtime-package-missing");
            return false;
        }

        rigBuilder = animator.GetComponent(rigBuilderType) ?? animator.gameObject.AddComponent(rigBuilderType);
        SetMember(rigBuilder, "enabled", false);
        Transform rigRoot = FindOrCreateChild(animator.transform, "Mao Animation Rigging");
        lookTarget = FindOrCreateChild(animator.transform, "Mao Look Target");
        headRig = rigRoot.GetComponent(rigType) ?? rigRoot.gameObject.AddComponent(rigType);
        Component constraint = rigRoot.GetComponent(multiAimType) ?? rigRoot.gameObject.AddComponent(multiAimType);
        lookTarget.position = head.position + head.rotation * AimVector(headAimAxis) * 0.25f;

        if (!ConfigureMultiAim(constraint))
        {
            LogBuildFailure("multi-aim-api-unavailable");
            return false;
        }

        SetMember(headRig, "weight", 0f);
        object layersObject = GetMember(rigBuilder, "layers");
        if (!(layersObject is IList layers))
        {
            LogBuildFailure("rig-layer-list-unavailable");
            return false;
        }

        InvokeIfPresent(rigBuilder, "Clear");
        bool ownsLayer = false;
        foreach (object layer in layers)
        {
            if (layer != null && ReferenceEquals(GetMember(layer, "rig"), headRig)) ownsLayer = true;
        }
        if (!ownsLayer)
            layers.Add(Activator.CreateInstance(rigLayerType, new object[] { headRig, true }));

        object buildResult = Invoke(rigBuilder, "Build");
        bool built = buildResult is bool && (bool)buildResult;
        SetMember(rigBuilder, "enabled", built);
        ready = built && IsGraphValid();
        if (!ready)
        {
            LogBuildFailure("rig-builder-failed");
            return false;
        }

        Debug.Log("[Mao Rigging] event=ready channel=head-look package=1.4.1 optional_reflection=true");
        return true;
    }

    private bool ConfigureMultiAim(Component constraint)
    {
        object data = GetMember(constraint, "data");
        if (data == null) return false;
        if (!SetMember(data, "constrainedObject", head) || !SetMember(data, "maintainOffset", true)) return false;
        SetEnumMember(data, "aimAxis", headAimAxis);
        SetEnumMember(data, "upAxis", "Y");
        SetEnumMember(data, "worldUpType", "SceneUp");
        SetMember(data, "constrainedXAxis", true);
        SetMember(data, "constrainedYAxis", true);
        SetMember(data, "constrainedZAxis", true);
        SetMember(data, "limits", new Vector2(-Mathf.Abs(yawLimit), Mathf.Abs(yawLimit)));

        Type weightedArrayType = FindType("UnityEngine.Animations.Rigging.WeightedTransformArray");
        Type weightedTransformType = FindType("UnityEngine.Animations.Rigging.WeightedTransform");
        if (weightedArrayType == null || weightedTransformType == null) return false;
        // Start empty and add exactly one source.  The constructor argument is
        // the current length (not capacity), so passing 1 and then calling Add
        // would leave a null slot at index 0 and put the target at index 1.
        object sources = Activator.CreateInstance(weightedArrayType, new object[] { 0 });
        object source = Activator.CreateInstance(weightedTransformType, new object[] { lookTarget, 1f });
        MethodInfo add = weightedArrayType.GetMethod("Add", BindingFlags.Instance | BindingFlags.Public);
        if (add == null) return false;
        add.Invoke(sources, new[] { source });
        if (!SetMember(data, "sourceObjects", sources)) return false;
        return SetMember(constraint, "data", data) && SetMember(constraint, "weight", 1f);
    }

    private void LateUpdate()
    {
        if (!IsReady || lookTarget == null || head == null) return;
        Camera camera = Camera.main;
        bool canAim = false;
        if (camera != null && targetValid)
        {
            float rawDepth = camera.WorldToScreenPoint(head.position).z;
            if (IsFinite(rawDepth) && rawDepth > camera.nearClipPlane)
            {
                Vector3 world = camera.ScreenToWorldPoint(new Vector3(targetScreen.x, targetScreen.y, rawDepth));
                if (IsFinite(world.x) && IsFinite(world.y) && IsFinite(world.z))
                {
                    Vector3 relative = world - head.position;
                    Vector3 cameraUp = camera.transform.up.normalized;
                    float vertical = Vector3.Dot(relative, cameraUp);
                    Vector3 horizontal = relative - cameraUp * vertical;
                    float maxVertical = Mathf.Tan(Mathf.Clamp(pitchLimit, 0f, 45f) * Mathf.Deg2Rad)
                        * Mathf.Max(horizontal.magnitude, 0.001f);
                    lookTarget.position = head.position + horizontal + cameraUp * Mathf.Clamp(vertical, -maxVertical, maxVertical);
                    canAim = true;
                }
            }
        }
        float desired = enableHeadLookRig && canAim && !suppress ? requestedWeight : 0f;
        float current = ReadFloat(headRig, "weight");
        float smoothing = 1f - Mathf.Exp(-10f * Mathf.Max(0.0001f, Time.unscaledDeltaTime));
        SetMember(headRig, "weight", Mathf.Lerp(current, desired, smoothing));
    }

    private bool ResolvePackageTypes()
    {
        rigBuilderType = FindType("UnityEngine.Animations.Rigging.RigBuilder");
        rigType = FindType("UnityEngine.Animations.Rigging.Rig");
        multiAimType = FindType("UnityEngine.Animations.Rigging.MultiAimConstraint");
        rigLayerType = FindType("UnityEngine.Animations.Rigging.RigLayer");
        return rigBuilderType != null && rigType != null && multiAimType != null && rigLayerType != null;
    }

    private bool IsGraphValid()
    {
        object graph = GetMember(rigBuilder, "graph");
        object result = graph != null ? Invoke(graph, "IsValid") : null;
        return result is bool && (bool)result;
    }

    private static Type FindType(string fullName)
    {
        return Type.GetType(fullName + ", Unity.Animation.Rigging", false) ?? Type.GetType(fullName, false);
    }

    private static Transform FindOrCreateChild(Transform parent, string name)
    {
        Transform child = parent.Find(name);
        if (child != null) return child;
        GameObject created = new GameObject(name);
        created.transform.SetParent(parent, false);
        return created.transform;
    }

    private void LogBuildFailure(string reason)
    {
        if (loggedFailure) return;
        loggedFailure = true;
        Debug.LogWarning("[Mao Rigging] event=skipped reason=" + reason + " animator=" + (animator != null ? animator.name : "none"));
    }

    private static object GetMember(object target, string name)
    {
        if (target == null) return null;
        Type type = target.GetType();
        PropertyInfo property = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (property != null && property.CanRead) return property.GetValue(target, null);
        FieldInfo field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        return field != null ? field.GetValue(target) : null;
    }

    private static bool SetMember(object target, string name, object value)
    {
        if (target == null) return false;
        Type type = target.GetType();
        PropertyInfo property = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (property != null && property.CanWrite && (value == null || property.PropertyType.IsInstanceOfType(value)))
        {
            property.SetValue(target, value, null);
            return true;
        }
        FieldInfo field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (field != null && (value == null || field.FieldType.IsInstanceOfType(value)))
        {
            field.SetValue(target, value);
            return true;
        }
        return false;
    }

    private static bool SetEnumMember(object target, string name, string value)
    {
        object current = GetMember(target, name);
        if (current == null) return false;
        Type type = current.GetType();
        if (!type.IsEnum) return false;
        return SetMember(target, name, Enum.Parse(type, value, true));
    }

    private static object Invoke(object target, string methodName)
    {
        if (target == null) return null;
        MethodInfo method = target.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
        return method != null ? method.Invoke(target, null) : null;
    }

    private static void InvokeIfPresent(object target, string methodName) { try { Invoke(target, methodName); } catch { } }

    private static float ReadFloat(object target, string member)
    {
        object value = GetMember(target, member);
        return value is float ? (float)value : 0f;
    }

    private static bool IsFinite(float value) { return !float.IsNaN(value) && !float.IsInfinity(value); }

    private static Vector3 AimVector(string axis)
    {
        switch ((axis ?? string.Empty).ToUpperInvariant())
        {
            case "X": return Vector3.right;
            case "X_NEG": return Vector3.left;
            case "Y": return Vector3.up;
            case "Y_NEG": return Vector3.down;
            case "Z_NEG": return Vector3.back;
            default: return Vector3.forward;
        }
    }

    private void OnDestroy()
    {
        if (rigBuilder != null) InvokeIfPresent(rigBuilder, "Clear");
    }
}
