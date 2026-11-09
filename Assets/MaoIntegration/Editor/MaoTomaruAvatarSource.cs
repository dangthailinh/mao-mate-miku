#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

/// <summary>Prepares the owner's local Tomaru source FBX for avatar editing.</summary>
public static class MaoTomaruAvatarSource
{
    private const string SourceRoot = "Assets/MaoIntegration/LocalAvatarSources/TomaruMikuNT";
    private const string FbxPath = SourceRoot + "/HatsuneMikuNT.fbx";

    [Serializable]
    private sealed class ImportReport
    {
        public string source;
        public bool humanoidValid;
        public int humanoidBoneCount;
        public int rendererCount;
        public int vertexCount;
        public int triangleCount;
        public int blendShapeCount;
        public int textureCount;
        public string[] materialNames;
        public bool clothingHasDedicatedMaterial;
    }

    [MenuItem("Mao/Avatar Sources/Prepare Tomaru Miku NT")]
    public static void PrepareAndValidate()
    {
        if (!File.Exists(FbxPath))
            throw new FileNotFoundException("Stage the owner's Tomaru FBX before importing it.", FbxPath);

        AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
        var importer = AssetImporter.GetAtPath(FbxPath) as ModelImporter;
        if (importer == null)
            throw new InvalidOperationException("Unity did not create a ModelImporter for the Tomaru FBX.");

        // Unity creates the Avatar from this source skeleton, retaining morph targets.
        importer.animationType = ModelImporterAnimationType.Human;
        importer.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
        importer.importAnimation = false;
        importer.importBlendShapes = true;
        importer.importCameras = false;
        importer.importLights = false;
        importer.isReadable = true;
        importer.SaveAndReimport();

        var model = AssetDatabase.LoadAssetAtPath<GameObject>(FbxPath);
        if (model == null)
            throw new InvalidOperationException("Tomaru source FBX did not import as a GameObject.");
        var animator = model.GetComponentInChildren<Animator>(true);
        bool valid = animator != null && animator.avatar != null &&
                     animator.avatar.isValid && animator.avatar.isHuman;
        if (!valid)
            throw new InvalidOperationException("Tomaru source Avatar is not a valid Humanoid; runtime model was not changed.");

        var renderers = model.GetComponentsInChildren<SkinnedMeshRenderer>(true);
        var meshes = renderers.Select(r => r.sharedMesh).Where(m => m != null).Distinct().ToArray();
        var materials = renderers.SelectMany(r => r.sharedMaterials)
            .Where(m => m != null).Select(m => m.name).Distinct().OrderBy(n => n).ToArray();
        int boneCount = 0;
        foreach (HumanBodyBones bone in Enum.GetValues(typeof(HumanBodyBones)))
            if (bone != HumanBodyBones.LastBone && animator.GetBoneTransform(bone) != null)
                boneCount++;

        var report = new ImportReport
        {
            source = FbxPath,
            humanoidValid = valid,
            humanoidBoneCount = boneCount,
            rendererCount = renderers.Length,
            vertexCount = meshes.Sum(m => m.vertexCount),
            triangleCount = meshes.Sum(m => m.triangles.Length / 3),
            blendShapeCount = meshes.Sum(m => m.blendShapeCount),
            textureCount = Directory.GetFiles(SourceRoot + "/Textures", "*.png").Length,
            materialNames = materials,
            clothingHasDedicatedMaterial = materials.Any(n => n.IndexOf("Costume", StringComparison.OrdinalIgnoreCase) >= 0)
        };
        if (report.rendererCount == 0 || report.vertexCount == 0 || report.blendShapeCount == 0)
            throw new InvalidOperationException("Tomaru source is missing its skinned mesh or facial morph targets.");

        string reportDirectory = Path.Combine(Path.GetDirectoryName(Application.dataPath), "Logs");
        Directory.CreateDirectory(reportDirectory);
        File.WriteAllText(Path.Combine(reportDirectory, "TomaruAvatarSourceTrial.json"), JsonUtility.ToJson(report, true));
        Debug.Log("[Mao Tomaru Source] " + JsonUtility.ToJson(report));
    }
}
#endif
