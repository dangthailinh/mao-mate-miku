#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using MaoIntegration;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

public static class MaoMateBuild
{
    private const string MainScene = "Assets/MATE ENGINE - Scenes/Mate Engine Main.unity";

    [MenuItem("Mao/Build Windows App")]
    public static void BuildWindowsApp()
    {
        MaoMotionAssetPostprocessor.ConfigureAdvancedLocomotionAndValidate();
        MaoMotionAssetPostprocessor.ConfigureCursorFlightAndValidate();
        string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        string workspaceRoot = Path.GetFullPath(Path.Combine(projectRoot, ".."));
        string outputDirectory = Path.Combine(workspaceRoot, "build", "Mao Mate Engine");
        string trialOutput = Environment.GetCommandLineArgs().FirstOrDefault(a => a.StartsWith("--mao-build-output=", StringComparison.Ordinal));
        if (trialOutput != null) outputDirectory = Path.GetFullPath(trialOutput.Substring("--mao-build-output=".Length));
        bool appearanceTrial = Environment.GetCommandLineArgs().Contains("--mao-build-appearance-trial");
        bool controlTrial = Environment.GetCommandLineArgs().Contains("--mao-build-control-trial");
        if (appearanceTrial && controlTrial) throw new InvalidOperationException("Choose one isolated trial profile.");
        if ((appearanceTrial || controlTrial) && trialOutput == null)
            throw new InvalidOperationException("A trial requires an explicit isolated output directory.");
        if (controlTrial)
        {
            string canonical = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Mao Mate Engine");
            if (outputDirectory.Equals(canonical, StringComparison.OrdinalIgnoreCase) || outputDirectory.StartsWith(canonical + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("A control trial cannot overwrite the installed player.");
        }
        string executable = Path.Combine(outputDirectory, "Mao Mate Engine.exe");

        Directory.CreateDirectory(outputDirectory);
        string previousCompany = PlayerSettings.companyName;
        PlayerSettings.companyName = controlTrial ? "Linh.MikuControlTrial" : appearanceTrial ? "Linh.MikuAppearanceTrial" : "Linh";
        PlayerSettings.productName = "Mao Mate Engine";
        PlayerSettings.bundleVersion = "1.0.0";

        var options = new BuildPlayerOptions
        {
            scenes = new[] { MainScene },
            locationPathName = executable,
            target = BuildTarget.StandaloneWindows64,
            extraScriptingDefines = Environment.GetCommandLineArgs().Contains("--mao-build-defer-hhtech")
                ? new[] { "MAO_DEFER_HHTECH" } : Array.Empty<string>(),
            options = BuildOptions.None
        };

        BuildReport report;
        try { report = BuildPipeline.BuildPlayer(options); }
        finally { if (appearanceTrial || controlTrial) PlayerSettings.companyName = previousCompany; }
        if (report.summary.result != BuildResult.Succeeded)
            throw new InvalidOperationException("Mao Mate Engine build failed: " + report.summary.result);

        DeployMiku(projectRoot, workspaceRoot, outputDirectory);
        DeployHarness(projectRoot, outputDirectory);
        File.WriteAllText(Path.Combine(outputDirectory, "MaoAssets", "release-mode.json"),
            "{\"hhtechDeferred\":" + Environment.GetCommandLineArgs().Contains("--mao-build-defer-hhtech").ToString().ToLowerInvariant() + "}");
        Debug.Log($"[Mao Build] Built {executable} ({report.summary.totalSize} bytes)");
    }

    private static void DeployHarness(string projectRoot, string outputDirectory)
    {
        string source = Path.Combine(projectRoot, "Tools", "MikuHarness");
        string target = Path.Combine(outputDirectory, "MaoAssets", "Harness");
        if (!File.Exists(Path.Combine(source, "bridge.mjs")) || !Directory.Exists(Path.Combine(source, "node_modules")))
            throw new InvalidOperationException("Install the pinned Miku Harness dependencies before building.");
        Directory.CreateDirectory(target);
        foreach (string name in new[] { "bridge.mjs", "policy.mjs", "miku-tools.mjs", "package.json", "package-lock.json", ".npmrc" })
            File.Copy(Path.Combine(source, name), Path.Combine(target, name), true);
        string modules = Path.Combine(source, "node_modules");
        foreach (string file in Directory.EnumerateFiles(modules, "*", SearchOption.AllDirectories))
        {
            string destination = Path.Combine(target, "node_modules", file.Substring(modules.Length + 1));
            Directory.CreateDirectory(Path.GetDirectoryName(destination));
            File.Copy(file, destination, true);
        }
        string node = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs", "node.exe");
        if (!File.Exists(node)) throw new FileNotFoundException("A Node runtime is required to package Miku Harness.", node);
        File.Copy(node, Path.Combine(target, "node.exe"), true);
        string license = Path.Combine(source, "NODE-LICENSE.txt");
        if (!File.Exists(license)) throw new FileNotFoundException("The bundled Node runtime license must be included.", license);
        File.Copy(license, Path.Combine(target, "NODE-LICENSE.txt"), true);
        Debug.Log("[Mao Build] Harness runtime packaged; no credentials or runtime data copied.");
    }

    private static void DeployMiku(string projectRoot, string workspaceRoot, string outputDirectory)
    {
        string source = FindRequiredAsset(projectRoot, workspaceRoot, MaoModelLocator.FileName);

        string assetsDirectory = Path.Combine(outputDirectory, "MaoAssets");
        Directory.CreateDirectory(assetsDirectory);
        File.Copy(source, Path.Combine(assetsDirectory, MaoModelLocator.FileName), true);

        string poseLibrary = FindRequiredAsset(projectRoot, workspaceRoot, "MikuPoses.json");
        File.Copy(poseLibrary, Path.Combine(assetsDirectory, "MikuPoses.json"), true);
    }

    private static string FindRequiredAsset(string projectRoot, string workspaceRoot, string fileName)
    {
        string projectAsset = Path.Combine(projectRoot, "AvatarAssets", fileName);
        if (File.Exists(projectAsset))
            return projectAsset;

        string workspaceAsset = Path.Combine(workspaceRoot, "AvatarAssets", fileName);
        if (File.Exists(workspaceAsset))
            return workspaceAsset;

        throw new FileNotFoundException(
            $"Required Mao asset '{fileName}' was not found in either AvatarAssets directory.",
            projectAsset);
    }
}
#endif
