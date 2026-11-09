using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MaoIntegration;
using UnityEngine;

[DisallowMultipleComponent]
public sealed class MaoAppearanceController : MonoBehaviour
{
    public static MaoAppearanceController Active { get; private set; }
    public MaoAppearanceProfile Profile { get; private set; } = new MaoAppearanceProfile();
    public bool Supported { get; private set; }
    public int OwnedResources => owned.Count + (accessories != null ? accessories.OwnedResources : 0);
    private SkinnedMeshRenderer body;
    private Mesh originalMesh;
    private Material[] originalMaterials;
    private Animator animator;
    private Transform head;
    private readonly List<UnityEngine.Object> owned = new List<UnityEngine.Object>();
    private GameObject jacket;
    private MaoCatAccessories accessories;
    private float nextAccessoryBindAt;
    private static bool AccessorySmoke => MaoAppearanceVerification.Active;
    // Visual verification has its own profile and cannot overwrite the user's appearance choices.
    private string SettingsPath => AccessorySmoke
        ? Path.Combine(Application.temporaryCachePath, "MaoCatAccessoriesSmoke", "mao-appearance.json")
        : Path.Combine(Application.persistentDataPath, "mao-appearance.json");

    public static void Ensure(GameObject model)
    {
        if (model == null) return;
        var existing = model.GetComponent<MaoAppearanceController>();
        if (existing != null) return;
        var controller = model.AddComponent<MaoAppearanceController>();
        controller.Bind();
    }

    private void Bind()
    {
        animator = GetComponentInChildren<Animator>(true);
        body = GetComponentsInChildren<SkinnedMeshRenderer>(true).FirstOrDefault(r =>
            r.sharedMesh != null && r.sharedMaterials.Any(m => m != null && m.name.StartsWith("Costume", StringComparison.Ordinal)) &&
            r.sharedMaterials.Any(m => m != null && m.name.StartsWith("Hair", StringComparison.Ordinal)));
        Supported = body != null && animator != null && animator.isHuman && body.sharedMesh.isReadable;
        if (!Supported) return;
        head = animator.GetBoneTransform(HumanBodyBones.Head);
        if (head == null) { Supported = false; return; }
        originalMesh = body.sharedMesh;
        originalMaterials = body.sharedMaterials;
        accessories = GetComponent<MaoCatAccessories>() ?? gameObject.AddComponent<MaoCatAccessories>();
        accessories.Bind(body, animator);
        Debug.Log("[Mao Cat Bind] runtime ears=" + accessories.EarsAvailable + " tail=" + accessories.TailAvailable +
            " component_active=" + accessories.isActiveAndEnabled + " controller_active=" + isActiveAndEnabled);
        Active = this;
        try
        {
            if (File.Exists(SettingsPath)) Profile = JsonUtility.FromJson<MaoAppearanceProfile>(File.ReadAllText(SettingsPath)) ?? new MaoAppearanceProfile();
            Profile.Normalize();
            if (!accessories.EarsAvailable) Profile.catEars = 0;
            if (!accessories.TailAvailable) Profile.catTail = 0;
            Apply();
        }
        catch (Exception ex)
        {
            Profile = new MaoAppearanceProfile(); RestoreResources();
            Debug.LogWarning("[Mao Appearance] saved profile could not be applied: " + ex.GetType().Name);
        }
        Debug.Log("[Mao Appearance] bound supported=" + Supported + " shapes=" + originalMesh.blendShapeCount);
        if (Array.IndexOf(Environment.GetCommandLineArgs(), "--mao-smoke-appearance") >= 0)
            gameObject.AddComponent<MaoAppearanceSmoke>();
        if (AccessorySmoke) MaoCatAccessoriesSmoke.Ensure();
    }

    public bool CanSelect(MaoAppearancePart part)
    {
        if (!Supported || !isActiveAndEnabled || MaoAppearanceProfile.Count(part) == 0) return false;
        if (accessories != null && accessories.isActiveAndEnabled && !accessories.EarsAvailable &&
            !accessories.TailAvailable && Time.unscaledTime >= nextAccessoryBindAt)
        {
            nextAccessoryBindAt = Time.unscaledTime + 1f;
            accessories.Bind(body, animator);
        }
        if (part == MaoAppearancePart.CatEars) return accessories != null && accessories.isActiveAndEnabled && accessories.EarsAvailable;
        if (part == MaoAppearancePart.CatTail) return accessories != null && accessories.isActiveAndEnabled && accessories.TailAvailable;
        return true;
    }

    public bool Select(MaoAppearancePart part, int id, bool persist = true)
    {
        if (!CanSelect(part)) return false;
        var previous = Profile.Copy();
        if (!Profile.TrySet(part, id)) return false;
        bool accessoryOnly = part == MaoAppearancePart.CatEars || part == MaoAppearancePart.CatTail;
        try { if (accessoryOnly) ApplyAccessories(); else Apply(); if (persist) Save(); return true; }
        catch (Exception ex)
        {
            Profile = previous;
            try { if (accessoryOnly) ApplyAccessories(); else Apply(); } catch { RestoreResources(); }
            Debug.LogError("[Mao Appearance] selection rolled back: " + ex.GetType().Name);
            return false;
        }
    }

    public void ResetAppearance(bool persist = true)
    {
        Profile.Reset(); RestoreResources();
        if (persist) Save();
    }

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath));
        string temporary = SettingsPath + ".tmp";
        File.WriteAllText(temporary, JsonUtility.ToJson(Profile, true));
        if (File.Exists(SettingsPath)) File.Replace(temporary, SettingsPath, SettingsPath + ".bak");
        else File.Move(temporary, SettingsPath);
    }

    private void Apply()
    {
        RestoreResources(false);
        if (Profile.hair != 0 || Profile.face != 0)
        {
            Mesh mesh = MaoAppearanceMeshBuilder.Build(body, head, Profile.hair, Profile.face);
            owned.Add(mesh); body.sharedMesh = mesh;
        }
        var materials = (Material[])originalMaterials.Clone();
        for (int i = 0; i < materials.Length; i++)
        {
            var source = materials[i];
            if (source == null) continue;
            int kind = -1, id = 0;
            if (source.name.StartsWith("Hair", StringComparison.Ordinal)) { kind = 0; id = Profile.hairColor; }
            if (source.name.StartsWith("Costume", StringComparison.Ordinal)) { kind = 1; id = Profile.outfit == 3 ? 1 : Profile.outfit; }
            if (source.name.StartsWith("Eye", StringComparison.Ordinal)) { kind = 2; id = Profile.eyeColor; }
            if (kind < 0 || id == 0 || source.mainTexture == null) continue;
            var material = new Material(source); owned.Add(material);
            var texture = MaoAppearancePalette.Recolor(source.mainTexture, kind, id); owned.Add(texture);
            material.mainTexture = texture;
            if (material.HasProperty("_ShadeTexture")) material.SetTexture("_ShadeTexture", texture);
            materials[i] = material;
        }
        body.sharedMaterials = materials;
        if (Profile.outfit == 3) CreateJacket();
        ApplyAccessories();
    }

    private void ApplyAccessories()
    {
        if (accessories != null && !accessories.Apply(Profile.catEars == 1, Profile.catTail == 1, Profile.hairColor))
            throw new InvalidOperationException("The selected accessory is unavailable for this avatar.");
    }

    private void RestoreResources(bool includeAccessories = true)
    {
        if (includeAccessories && accessories != null) accessories.Clear();
        if (body != null && originalMesh != null) { body.sharedMesh = originalMesh; body.sharedMaterials = originalMaterials; }
        if (jacket != null) Destroy(jacket);
        jacket = null;
        foreach (var resource in owned) if (resource != null) Destroy(resource);
        owned.Clear();
    }

    private void CreateJacket()
    {
        Mesh mesh = MaoAppearanceGarment.Build(body, animator);
        if (mesh == null) return;
        owned.Add(mesh);
        Material source = originalMaterials.First(m => m != null && m.name.StartsWith("Costume", StringComparison.Ordinal));
        var material = new Material(source); owned.Add(material);
        material.mainTexture = Texture2D.whiteTexture;
        if (material.HasProperty("_ShadeTexture")) material.SetTexture("_ShadeTexture", Texture2D.whiteTexture);
        if (material.HasProperty("_Color")) material.SetColor("_Color", new Color(0.19f, 0.22f, 0.28f));
        if (material.HasProperty("_ShadeColor")) material.SetColor("_ShadeColor", new Color(0.10f, 0.13f, 0.18f));
        if (material.HasProperty("_EmissionColor")) material.SetColor("_EmissionColor", Color.black);
        jacket = new GameObject("Miku appearance jacket");
        jacket.layer = body.gameObject.layer;
        jacket.transform.SetParent(body.transform, false);
        var renderer = jacket.AddComponent<SkinnedMeshRenderer>();
        renderer.sharedMesh = mesh; renderer.bones = body.bones; renderer.rootBone = body.rootBone;
        renderer.sharedMaterial = material; renderer.localBounds = body.localBounds;
    }
    private void OnEnable() { if (Supported) { Active = this; ApplyAccessories(); } }
    private void OnDisable() { if (Active == this) Active = null; if (accessories != null) accessories.Clear(); }
    private void OnDestroy() { if (Active == this) Active = null; RestoreResources(); }
}
