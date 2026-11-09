using System;
using System.Collections;
using System.IO;
using System.Linq;
using UnityEngine;
using Newtonsoft.Json;

// Read-only installation probe. It never grants rights, starts an agent task or opens apps.
public sealed class MaoPermissionStatusProbe : MonoBehaviour
{
    private IEnumerator Start()
    {
        yield return new WaitForSecondsRealtime(12);
        var harness = GetComponent<MaoGeminiChatProvider>().Harness;
        harness.ShowPermissions();
        string[] tools = { "open_target", "save_note", "list_notes", "read_note", "list_directory", "read_file", "write_file", "run_powershell", "open_url" };
        int registered = tools.Count(name => harness.ControlLayer.Tools.TryRisk(name, out _));
        string directory = Path.Combine(Application.persistentDataPath, "PermissionQA"); Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "installed-status.json"), JsonConvert.SerializeObject(new { utc = DateTime.UtcNow.ToString("O"), company = Application.companyName, mode = harness.Permissions.WireMode, registeredTools = registered, busy = harness.IsBusy, harnessInstalled = harness.Installed }));
        yield return new WaitForEndOfFrame(); ScreenCapture.CaptureScreenshot(Path.Combine(directory, "installed-panel.png"));
        Debug.Log("[Mao Permission Probe] mode=" + harness.Permissions.WireMode + " tools=" + registered + " company=" + Application.companyName);
        yield return new WaitForSecondsRealtime(2);
        if (Environment.GetCommandLineArgs().Contains("--mao-smoke-exit")) Application.Quit(registered == 9 ? 0 : 1);
    }
}
