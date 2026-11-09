#if UNITY_EDITOR
using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using UnityEditor;
using UnityEngine;

public static class MaoDancePackBuilder
{
    private const string TempRoot = "Assets/__MaoDancePackTemp__";
    private const string KawakiAudio = "Assets/MaoIntegration/DancePacks/Audio/kawakiwoameku.mp3";
    private const string KawakiMotion = "Assets/MATE ENGINE - Animations/PET_DANCING/PET_DANCING_10.anim";
    private const string UnravelAudio = "Assets/MaoIntegration/DancePacks/Audio/ado-unravel.mp3";
    private const string UnravelMotion = "Assets/MATE ENGINE - Animations/PET_DANCING/PET_DANCING_11.anim";
    private const string HanabiAudio = "Assets/MaoIntegration/DancePacks/Audio/uchiage-hanabi.mp3";
    private const string HanabiMotion = "Assets/MATE ENGINE - Animations/PET_DANCING/PET_DANCING_13.anim";

    [Serializable]
    private sealed class DanceMeta
    {
        public string songName;
        public string songAuthor;
        public string mmdAuthor;
        public float songLength;
        public string placeholderClipName;
    }

    private sealed class PackSpec
    {
        public string FileName;
        public string SongName;
        public string SongAuthor;
        public string MotionAuthor;
        public string AudioAsset;
        public string MotionAsset;
    }

    [MenuItem("Mao/Dance Packs/Build Kawaki wo Ameku")]
    public static void BuildKawaki()
    {
        Build(new PackSpec
        {
            FileName = "01 - Kawaki wo Ameku (Minami)",
            SongName = "Kawaki wo Ameku",
            SongAuthor = "Minami",
            MotionAuthor = "Mate Engine PET_DANCING_10",
            AudioAsset = KawakiAudio,
            MotionAsset = KawakiMotion
        });
    }

    [MenuItem("Mao/Dance Packs/Build Unravel")]
    public static void BuildUnravel()
    {
        Build(new PackSpec
        {
            FileName = "02 - Unravel (Ado Cover)",
            SongName = "Unravel",
            SongAuthor = "Ado cover (original by TK)",
            MotionAuthor = "Mate Engine PET_DANCING_11",
            AudioAsset = UnravelAudio,
            MotionAsset = UnravelMotion
        });
    }

    [MenuItem("Mao/Dance Packs/Build Uchiage Hanabi")]
    public static void BuildHanabi()
    {
        Build(new PackSpec
        {
            FileName = "03 - Uchiage Hanabi (DAOKO x Kenshi Yonezu)",
            SongName = "Uchiage Hanabi",
            SongAuthor = "DAOKO x Kenshi Yonezu",
            MotionAuthor = "Mate Engine PET_DANCING_13",
            AudioAsset = HanabiAudio,
            MotionAsset = HanabiMotion
        });
    }

    [MenuItem("Mao/Dance Packs/Build All Three")]
    public static void BuildAll()
    {
        BuildKawaki();
        BuildUnravel();
        BuildHanabi();
        Debug.Log("[Mao Dance Pack] ALL_THREE_OK");
    }

    private static void Build(PackSpec spec)
    {
        AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
        ValidateSource(spec);
        ConfigureAudio(spec.AudioAsset);

        string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        string outputRoot = Path.Combine(projectRoot, "ExportedDancePacks");
        string buildRoot = Path.Combine(projectRoot, "TempDancePacks", Sanitize(spec.FileName));
        Directory.CreateDirectory(outputRoot);
        ResetDirectory(buildRoot);
        ResetAssetDirectory(TempRoot);

        string tempClipPath = TempRoot + "/dance.anim";
        AnimationClip sourceClip = AssetDatabase.LoadAssetAtPath<AnimationClip>(spec.MotionAsset);
        var loopClip = new AnimationClip();
        EditorUtility.CopySerialized(sourceClip, loopClip);
        loopClip.name = spec.SongName + " Dance";
        AnimationClipSettings settings = AnimationUtility.GetAnimationClipSettings(loopClip);
        settings.loopTime = true;
        settings.loopBlend = true;
        settings.keepOriginalOrientation = true;
        settings.keepOriginalPositionY = true;
        settings.keepOriginalPositionXZ = true;
        AnimationUtility.SetAnimationClipSettings(loopClip, settings);
        AssetDatabase.CreateAsset(loopClip, tempClipPath);

        AssetImporter clipImporter = AssetImporter.GetAtPath(tempClipPath);
        AssetImporter audioImporter = AssetImporter.GetAtPath(spec.AudioAsset);
        clipImporter.assetBundleName = "dance.bundle";
        audioImporter.assetBundleName = "dance.bundle";
        AssetDatabase.SaveAssets();

        try
        {
            AssetBundleManifest manifest = BuildPipeline.BuildAssetBundles(
                buildRoot,
                BuildAssetBundleOptions.ChunkBasedCompression | BuildAssetBundleOptions.ForceRebuildAssetBundle,
                BuildTarget.StandaloneWindows64);
            if (manifest == null) throw new InvalidOperationException("Unity did not produce an AssetBundle manifest.");

            string bundlePath = Path.Combine(buildRoot, "dance.bundle");
            ValidateBundle(bundlePath, spec);

            AudioClip audio = AssetDatabase.LoadAssetAtPath<AudioClip>(spec.AudioAsset);
            var meta = new DanceMeta
            {
                songName = spec.SongName,
                songAuthor = spec.SongAuthor,
                mmdAuthor = spec.MotionAuthor,
                songLength = audio.length,
                placeholderClipName = "CUSTOM_DANCE"
            };
            File.WriteAllText(Path.Combine(buildRoot, "dance_meta.json"), JsonUtility.ToJson(meta, true));

            string packagePath = Path.Combine(outputRoot, spec.FileName + ".me");
            if (File.Exists(packagePath)) File.Delete(packagePath);
            ZipFile.CreateFromDirectory(buildRoot, packagePath, System.IO.Compression.CompressionLevel.Optimal, false);
            ValidatePackage(packagePath, spec);
            Debug.Log($"[Mao Dance Pack] BUILT_OK | {spec.SongName} | audio={audio.length:F2}s | motion={sourceClip.length:F2}s | {packagePath}");
        }
        finally
        {
            clipImporter.assetBundleName = string.Empty;
            audioImporter.assetBundleName = string.Empty;
            AssetDatabase.RemoveAssetBundleName("dance.bundle", true);
            AssetDatabase.DeleteAsset(TempRoot);
            AssetDatabase.SaveAssets();
        }
    }

    private static void ValidateSource(PackSpec spec)
    {
        if (spec == null) throw new ArgumentNullException(nameof(spec));
        if (string.IsNullOrWhiteSpace(spec.FileName)) throw new InvalidOperationException("Dance pack filename is required.");
        if (AssetDatabase.LoadAssetAtPath<AnimationClip>(spec.MotionAsset) == null)
            throw new FileNotFoundException("Dance motion was not found.", spec.MotionAsset);
        if (AssetDatabase.LoadAssetAtPath<AudioClip>(spec.AudioAsset) == null)
            throw new FileNotFoundException("Dance audio was not imported.", spec.AudioAsset);
    }

    private static void ConfigureAudio(string assetPath)
    {
        var importer = AssetImporter.GetAtPath(assetPath) as AudioImporter;
        if (importer == null) throw new InvalidOperationException("Audio importer is unavailable for " + assetPath);
        AudioImporterSampleSettings settings = importer.defaultSampleSettings;
        settings.loadType = AudioClipLoadType.Streaming;
        settings.compressionFormat = AudioCompressionFormat.Vorbis;
        settings.quality = 0.72f;
        settings.preloadAudioData = false;
        importer.defaultSampleSettings = settings;
        importer.loadInBackground = true;
        importer.SaveAndReimport();
    }

    private static void ValidateBundle(string bundlePath, PackSpec spec)
    {
        if (!File.Exists(bundlePath)) throw new FileNotFoundException("Dance bundle was not created.", bundlePath);
        AssetBundle bundle = AssetBundle.LoadFromFile(bundlePath);
        if (bundle == null) throw new InvalidDataException("Dance bundle cannot be opened: " + bundlePath);
        try
        {
            AnimationClip motion = bundle.LoadAllAssets<AnimationClip>().FirstOrDefault();
            AudioClip audio = bundle.LoadAllAssets<AudioClip>().FirstOrDefault();
            if (motion == null || motion.length <= 0f) throw new InvalidDataException(spec.SongName + " has no valid AnimationClip.");
            if (!motion.humanMotion) throw new InvalidDataException(spec.SongName + " motion is not Humanoid-retargetable.");
            if (!motion.isLooping) throw new InvalidDataException(spec.SongName + " motion is not configured to loop.");
            if (audio == null || audio.length <= 0f) throw new InvalidDataException(spec.SongName + " has no valid AudioClip.");
        }
        finally
        {
            bundle.Unload(true);
        }
    }

    private static void ValidatePackage(string packagePath, PackSpec spec)
    {
        var info = new FileInfo(packagePath);
        if (!info.Exists || info.Length < 100_000) throw new InvalidDataException("Dance package is empty or incomplete: " + packagePath);
        using (ZipArchive archive = ZipFile.OpenRead(packagePath))
        {
            if (!archive.Entries.Any(entry => entry.FullName.EndsWith("dance.bundle", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException(spec.SongName + " package is missing dance.bundle.");
            if (!archive.Entries.Any(entry => entry.FullName.EndsWith("dance_meta.json", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException(spec.SongName + " package is missing dance_meta.json.");
        }
    }

    private static void ResetDirectory(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, true);
        Directory.CreateDirectory(path);
    }

    private static void ResetAssetDirectory(string assetPath)
    {
        if (AssetDatabase.IsValidFolder(assetPath)) AssetDatabase.DeleteAsset(assetPath);
        string parent = Path.GetDirectoryName(assetPath)?.Replace('\\', '/');
        string name = Path.GetFileName(assetPath);
        AssetDatabase.CreateFolder(parent, name);
    }

    private static string Sanitize(string value)
    {
        foreach (char invalid in Path.GetInvalidFileNameChars()) value = value.Replace(invalid, '_');
        return value;
    }
}
#endif
