using System;
using System.Collections;
using System.IO;
using System.Linq;
using MaoIntegration;
using UnityEngine;

public sealed class MaoPermissionSmoke : MonoBehaviour
{
    private int failures;
    private void Check(string name, bool result) { if (!result) failures++; Debug.Log("[Mao Permission Smoke] " + name + "=" + result); }
    private IEnumerator Start()
    {
        if (Application.companyName != "Linh.MikuControlTrial") { Debug.LogError("[Mao Permission Smoke] isolated_trial_required"); Application.Quit(1); yield break; }
        yield return new WaitForSecondsRealtime(5);
        var chat = GetComponent<MaoGeminiChatProvider>(); var h = chat.Harness;
        bool proactive = chat.ProactiveConversationEnabled; chat.ProactiveConversationEnabled = false;
        var previousMode = h.Permissions.Mode; string previousWorkspace = h.Workspace;
        string fixture = Path.Combine(Application.persistentDataPath, "PermissionFixture-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(fixture);
        h.TryHandleControl("/workspace " + fixture, out _); h.TryHandleControl("bật harness", out _);
        string reply = ""; bool done = false;
        h.SetPermissionMode(MaoPermissionMode.Ask);
        chat.SendSilent("Tạo file ask.txt trong workspace với nội dung MIKU_ASK_OK bằng write_file, không dùng PowerShell.", t => reply = t, () => done = true);
        float deadline = Time.realtimeSinceStartup + 100; bool approvalSeen = false, preserved = false;
        while (!done && Time.realtimeSinceStartup < deadline)
        {
            if (!approvalSeen && h.Permissions.Pending != null)
            {
                approvalSeen = true; Check("ask_no_write_before_approval", !File.Exists(Path.Combine(fixture, "ask.txt")));
                string id = h.ControlLayer.Tasks.ActiveId;
                chat.SendSilent("/permissions status", _ => { }, () => { });
                preserved = h.ControlLayer.Tasks.IsActive(id);
                yield return new WaitForEndOfFrame(); ScreenCapture.CaptureScreenshot(Path.Combine(Application.persistentDataPath, "Permissions-Ask.png"));
                chat.SendSilent("cho phép lần này", _ => { }, () => { });
            }
            yield return null;
        }
        Check("approval_preserves_task", approvalSeen && preserved);
        Check("ask_real_file", done && File.Exists(Path.Combine(fixture, "ask.txt")) && File.ReadAllText(Path.Combine(fixture, "ask.txt")) == "MIKU_ASK_OK");
        h.Cancel(); h.SetPermissionMode(MaoPermissionMode.Ask); done = false;
        chat.SendSilent("Tạo file denied.txt trong workspace với nội dung DENIED bằng write_file, không dùng PowerShell.", t => reply = t, () => done = true);
        deadline = Time.realtimeSinceStartup + 100; bool denied = false;
        while (!done && Time.realtimeSinceStartup < deadline) { if (h.Permissions.Pending != null) { denied = true; chat.SendSilent("từ chối", _ => { }, () => { }); } yield return null; }
        Check("denied_no_side_effect", denied && !File.Exists(Path.Combine(fixture, "denied.txt")));
        h.Cancel(); h.SetPermissionMode(MaoPermissionMode.FullAccess); h.ShowPermissions();
        yield return new WaitForEndOfFrame(); ScreenCapture.CaptureScreenshot(Path.Combine(Application.persistentDataPath, "Permissions-Full.png"));
        done = false;
        chat.SendSilent("Thực hiện bằng công cụ: tạo file full.txt trong workspace nội dung MIKU_FULL_OK bằng write_file, đọc lại bằng read_file rồi chạy PowerShell Write-Output 'MIKU_SHELL_OK'. Không chỉ giải thích.", t => reply = t, () => done = true);
        deadline = Time.realtimeSinceStartup + 180;
        while (!done && Time.realtimeSinceStartup < deadline) yield return null;
        var audit = h.ControlLayer.Audit.ReadToday(100);
        string fullTask = h.ControlLayer.Tasks.History().FirstOrDefault()?.Id;
        Check("full_real_file", done && File.Exists(Path.Combine(fixture, "full.txt")) && File.ReadAllText(Path.Combine(fixture, "full.txt")) == "MIKU_FULL_OK");
        Check("full_real_read_and_powershell", done && audit.Any(a => a.taskId == fullTask && a.tool == "read_file" && a.outcome == "completed") && audit.Any(a => a.taskId == fullTask && a.tool == "run_powershell" && a.outcome == "completed"));
        Check("full_no_confirmation", h.Permissions.Pending == null);
        string provider = h.LastProvider;
        h.Cancel(); h.SetPermissionMode(MaoPermissionMode.ReadOnly); done = false;
        chat.SendSilent("Tạo file blocked.txt trong workspace bằng write_file với nội dung BLOCKED.", t => reply = t, () => done = true);
        deadline = Time.realtimeSinceStartup + 100; while (!done && Time.realtimeSinceStartup < deadline) yield return null;
        Check("read_only_no_write", done && !File.Exists(Path.Combine(fixture, "blocked.txt")));
        h.Cancel(); h.SetPermissionMode(MaoPermissionMode.FullAccess); done = false;
        chat.SendSilent("Chạy PowerShell trong workspace: Start-Sleep -Seconds 30; Set-Content -LiteralPath cancelled.txt -Value SHOULD_NOT_EXIST", t => reply = t, () => done = true);
        deadline = Time.realtimeSinceStartup + 100; bool shellStarted = false;
        while (!done && Time.realtimeSinceStartup < deadline)
        {
            string task = h.ControlLayer.Tasks.ActiveId;
            if (task != null && h.ControlLayer.Audit.ReadToday(10).Any(a => a.taskId == task && a.tool == "run_powershell" && a.outcome == "started")) { shellStarted = true; yield return new WaitForSecondsRealtime(1); break; }
            yield return new WaitForSecondsRealtime(0.1f);
        }
        string cancelledTask = h.ControlLayer.Tasks.ActiveId;
        chat.SendSilent("thu hồi full quyền", _ => { }, () => { });
        yield return new WaitForSecondsRealtime(3);
        Check("downgrade_cancels_task", shellStarted && cancelledTask != null && h.ControlLayer.Tasks.Get(cancelledTask)?.Status == MaoTaskStatus.Cancelled && !h.IsBusy && !File.Exists(Path.Combine(fixture, "cancelled.txt")));
        h.TryHandleControl("/workspace " + previousWorkspace, out _); h.SetPermissionMode(previousMode); chat.ProactiveConversationEnabled = proactive;
        File.WriteAllText(Path.Combine(Application.persistentDataPath, "MaoPermissionSmoke.json"), "{\"failures\":" + failures + ",\"provider\":\"" + provider + "\",\"utc\":\"" + DateTime.UtcNow.ToString("O") + "\"}");
        Debug.Log("[Mao Permission Smoke] failures=" + failures);
        if (Environment.GetCommandLineArgs().Contains("--mao-smoke-exit")) Application.Quit(failures == 0 ? 0 : 1);
    }
}
