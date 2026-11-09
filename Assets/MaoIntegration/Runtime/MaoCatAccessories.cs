using System;
using System.Collections.Generic;
using UnityEngine;

[DisallowMultipleComponent]
public sealed class MaoCatAccessories : MonoBehaviour
{
    public int OwnedResources => owned.Count;
    public bool EarsVisible => ears != null && ears.activeSelf;
    public bool TailVisible => tail != null && tail.activeSelf;
    public bool EarsAvailable => head != null && sourceMaterial != null && headFrameValid;
    public bool TailAvailable => hips != null && sourceMaterial != null && hipsFrameValid;

    private readonly List<UnityEngine.Object> owned = new List<UnityEngine.Object>(4);
    private SkinnedMeshRenderer body;
    private Animator animator;
    private Transform head, hips;
    private Matrix4x4 headFrame, hipsFrame;
    private bool headFrameValid, hipsFrameValid;
    private Material sourceMaterial, outerMaterial, innerMaterial;
    private GameObject ears, tail;
    private Mesh earsMesh, tailMesh;
    private Vector3 tailYawAxis, tailPitchAxis;
    private float tailPhase;
    private int appliedHairColor = -1;
    private bool requestedEars, requestedTail;
    private int requestedHairColor;

    public bool Bind(SkinnedMeshRenderer targetBody, Animator targetAnimator)
    {
        if (body == targetBody && animator == targetAnimator && sourceMaterial != null &&
            (EarsAvailable || TailAvailable))
            return EarsAvailable || TailAvailable;
        Clear();
        body = targetBody;
        animator = targetAnimator;
        head = hips = null;
        sourceMaterial = null;
        headFrameValid = hipsFrameValid = false;
        if (body == null || body.sharedMesh == null || !body.sharedMesh.isReadable ||
            animator == null || animator.avatar == null || !animator.avatar.isValid || !animator.isHuman) return false;

        foreach (Material material in body.sharedMaterials)
            if (material != null && material.name.StartsWith("Hair", StringComparison.Ordinal))
            { sourceMaterial = material; break; }
        if (sourceMaterial == null) return false;
        head = animator.GetBoneTransform(HumanBodyBones.Head);
        hips = animator.GetBoneTransform(HumanBodyBones.Hips);
        Transform[] bones = body.bones;
        Matrix4x4[] bindposes = body.sharedMesh.bindposes;
        headFrameValid = TryFrame(head, bones, bindposes, out headFrame);
        hipsFrameValid = TryFrame(hips, bones, bindposes, out hipsFrame);
        tailYawAxis = hipsFrame.MultiplyVector(Vector3.up).normalized;
        tailPitchAxis = hipsFrame.MultiplyVector(Vector3.right).normalized;
        return EarsAvailable || TailAvailable;
    }

    public bool Apply(bool earsEnabled, bool tailEnabled, int hairColor = 0)
    {
        // Reject before mutation: an unavailable second accessory cannot partly enable the first.
        if ((earsEnabled && !EarsAvailable) || (tailEnabled && !TailAvailable)) return false;
        int color = hairColor >= 0 && hairColor <= 2 ? hairColor : 0;
        if (!isActiveAndEnabled)
        {
            requestedEars = earsEnabled; requestedTail = tailEnabled; requestedHairColor = color;
            return true;
        }
        if (EarsVisible == earsEnabled && TailVisible == tailEnabled && appliedHairColor == color)
            return true;

        GameObject newEars = null, newTail = null;
        Mesh newEarsMesh = null, newTailMesh = null;
        Material newOuter = null, newInner = null;
        try
        {
            Material outer = outerMaterial;
            if ((earsEnabled || tailEnabled) && outer == null)
                outer = newOuter = CreateMaterial("Miku cat turquoise", Color.white);
            Material inner = innerMaterial;
            if (earsEnabled && ears == null)
            {
                if (inner == null) inner = newInner = CreateMaterial("Miku cat pink ear interiors", new Color(1f, 0.57f, 0.68f));
                newEarsMesh = MaoCatAccessoryMeshes.BuildEars(headFrame);
                newEars = CreateObject("Miku cat ears", head, newEarsMesh, new[] { outer, inner });
            }
            if (tailEnabled && tail == null)
            {
                newTailMesh = MaoCatAccessoryMeshes.BuildTail(hipsFrame);
                newTail = CreateObject("Miku cat tail", hips, newTailMesh, new[] { outer });
                newTail.transform.localPosition = hipsFrame.MultiplyVector(new Vector3(0f, 0.06f, -0.12f));
            }
        }
        catch (Exception ex)
        {
            DisposeObject(newEars); DisposeObject(newTail);
            DisposeResource(newEarsMesh); DisposeResource(newTailMesh);
            DisposeResource(newOuter); DisposeResource(newInner);
            Debug.LogWarning("[Mao Cat Accessories] apply rejected: " + ex.GetType().Name);
            return false;
        }

        if (newOuter != null) { outerMaterial = newOuter; owned.Add(newOuter); }
        if (newInner != null) { innerMaterial = newInner; owned.Add(newInner); }
        if (newEars != null) { ears = newEars; earsMesh = newEarsMesh; owned.Add(earsMesh); ears.SetActive(true); }
        if (newTail != null) { tail = newTail; tailMesh = newTailMesh; owned.Add(tailMesh); tail.SetActive(true); }
        if (!earsEnabled)
        {
            DisposeObject(ears); ears = null;
            Release(earsMesh); earsMesh = null;
            Release(innerMaterial); innerMaterial = null;
        }
        if (!tailEnabled)
        {
            DisposeObject(tail); tail = null;
            Release(tailMesh); tailMesh = null;
            tailPhase = 0f;
        }
        if (!earsEnabled && !tailEnabled)
        { Release(outerMaterial); outerMaterial = null; }
        if (outerMaterial != null && (newOuter != null || appliedHairColor != color))
            Tint(outerMaterial, color == 1 ? new Color(0.96f, 0.48f, 0.70f) :
                color == 2 ? new Color(0.30f, 0.52f, 0.95f) : new Color(0.16f, 0.83f, 0.79f));
        appliedHairColor = color;
        requestedEars = earsEnabled; requestedTail = tailEnabled; requestedHairColor = color;
        return true;
    }

    public void Clear()
    {
        requestedEars = requestedTail = false;
        requestedHairColor = 0;
        ReleaseVisuals();
    }

    private void ReleaseVisuals()
    {
        DisposeObject(ears); DisposeObject(tail);
        ears = tail = null;
        earsMesh = tailMesh = null;
        outerMaterial = innerMaterial = null;
        foreach (UnityEngine.Object resource in owned) DisposeResource(resource);
        owned.Clear();
        appliedHairColor = -1;
        tailPhase = 0f;
    }

    private static bool TryFrame(Transform bone, Transform[] bones, Matrix4x4[] binds, out Matrix4x4 frame)
    {
        frame = Matrix4x4.identity;
        if (bone == null) return false;
        int index = Array.IndexOf(bones, bone);
        if (index < 0 || index >= binds.Length) return false;
        frame = binds[index];
        for (int i = 0; i < 16; i++)
            if (float.IsNaN(frame[i]) || float.IsInfinity(frame[i]) || Mathf.Abs(frame[i]) > 10000f) return false;
        float determinant = frame.determinant;
        return !float.IsInfinity(determinant) && determinant > 0.000001f;
    }

    private GameObject CreateObject(string name, Transform bone, Mesh mesh, Material[] materials)
    {
        var accessory = new GameObject(name);
        try
        {
            accessory.SetActive(false);
            accessory.layer = body.gameObject.layer;
            accessory.transform.SetParent(bone, false);
            var filter = accessory.AddComponent<MeshFilter>(); filter.sharedMesh = mesh;
            var renderer = accessory.AddComponent<MeshRenderer>(); renderer.sharedMaterials = materials;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = body.receiveShadows;
            return accessory;
        }
        catch { DisposeObject(accessory); throw; }
    }

    private Material CreateMaterial(string name, Color color)
    {
        var material = new Material(sourceMaterial) { name = name };
        try
        {
            material.mainTexture = Texture2D.whiteTexture;
            if (material.HasProperty("_ShadeTexture")) material.SetTexture("_ShadeTexture", Texture2D.whiteTexture);
            if (material.HasProperty("_BumpMap")) material.SetTexture("_BumpMap", null);
            if (material.HasProperty("_EmissionMap")) material.SetTexture("_EmissionMap", null);
            if (material.HasProperty("_EmissionColor")) material.SetColor("_EmissionColor", Color.black);
            Tint(material, color);
            return material;
        }
        catch { DisposeResource(material); throw; }
    }

    private static void Tint(Material material, Color color)
    {
        if (material.HasProperty("_Color")) material.SetColor("_Color", color);
        if (material.HasProperty("_ShadeColor")) material.SetColor("_ShadeColor", color * new Color(0.63f, 0.68f, 0.72f, 1f));
        if (material.HasProperty("_OutlineColor")) material.SetColor("_OutlineColor", color * new Color(0.38f, 0.38f, 0.38f, 1f));
    }

    private void Release(UnityEngine.Object resource)
    {
        if (resource == null) return;
        owned.Remove(resource); DisposeResource(resource);
    }

    private static void DisposeObject(GameObject accessory)
    {
        if (accessory == null) return;
        // Destruction is deferred in a player; hide immediately to prevent a one-frame duplicate.
        accessory.SetActive(false); DisposeResource(accessory);
    }

    private static void DisposeResource(UnityEngine.Object resource)
    {
        if (resource == null) return;
        if (Application.isPlaying) UnityEngine.Object.Destroy(resource);
        else UnityEngine.Object.DestroyImmediate(resource);
    }

    private void LateUpdate()
    {
        if (tail == null || !tail.activeSelf) return;
        tailPhase = Mathf.Repeat(tailPhase + Time.unscaledDeltaTime * 1.9f, Mathf.PI * 2f);
        tail.transform.localRotation = Quaternion.AngleAxis(Mathf.Sin(tailPhase) * 8f, tailYawAxis) *
            Quaternion.AngleAxis(Mathf.Sin(tailPhase * 2f + 0.8f) * 2f, tailPitchAxis);
    }

    private void OnEnable() { Apply(requestedEars, requestedTail, requestedHairColor); }
    private void OnDisable() { ReleaseVisuals(); }
    private void OnDestroy() { Clear(); }
}
