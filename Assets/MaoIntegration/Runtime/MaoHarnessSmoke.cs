using System;
using System.Collections;
using System.IO;
using System.Linq;
using UnityEngine;
using MaoIntegration;

public sealed class MaoHarnessSmoke : MonoBehaviour
{
    private IEnumerator Start()
    {
        bool controlSmoke = Environment.GetCommandLineArgs().Any(a => a == "--mao-smoke-control-layer");
        if (controlSmoke && Application.companyName != "Linh.MikuControlTrial")
        { Debug.LogError("[Mao Control Smoke] isolated_trial_required"); Application.Quit(1); yield break; }
        yield return new WaitForSecondsRealtime(5f);
        var chat = GetComponent<MaoGeminiChatProvider>();
        var harness = chat.Harness;
        var permissionMode = harness.Permissions.Mode;
        // Explicit opt-in smoke grants full access only for this bounded test session.
        harness.SetPermissionMode(MaoPermissionMode.FullAccess);
        bool proactive = chat.ProactiveConversationEnabled;
        chat.ProactiveConversationEnabled = false;
        bool requireHhtech = Environment.GetCommandLineArgs().Any(a => a == "--mao-smoke-hhtech");
        int failures = 0;
        string reply = string.Empty;
        bool complete = false;
        chat.SendSilent("bật harness", text => reply = text, () => complete = true);
        if (!complete || !harness.Enabled || !harness.Installed) failures++;
        complete = false;
        float started = Time.realtimeSinceStartup;
        chat.SendSilent("Miku ơi mở Notepad và Calculator giúp anh", text => reply = text, () => complete = true);
        float deadline = Time.realtimeSinceStartup + 110f;
        while (!complete && Time.realtimeSinceStartup < deadline) yield return null;
        bool actions = complete && harness.LastOutcome == "completed" && harness.LastToolCount == 2 &&
            (!requireHhtech || harness.LastProvider == "miku-hhtech");
        string taskProvider = harness.LastProvider;
        if (!actions) failures++;
        bool taskCompleted = !controlSmoke || harness.ControlLayer.Tasks.History().FirstOrDefault()?.Status == MaoTaskStatus.Completed;
        if (!taskCompleted) failures++;
        Debug.Log("[Mao Harness Smoke] real_actions=" + actions + " provider=" + harness.LastProvider + " elapsed=" + (Time.realtimeSinceStartup - started).ToString("0.0"));
        complete = false;
        chat.SendSilent("ghi lại một ghi chú tên Harness QA với nội dung Miku kết nối thành công", text => reply = text, () => complete = true);
        deadline = Time.realtimeSinceStartup + 105f;
        while (!complete && Time.realtimeSinceStartup < deadline) yield return null;
        string notesPath = Path.Combine(Application.persistentDataPath, "mao-harness-notes.json");
        bool saved = complete && harness.LastOutcome == "completed" && File.Exists(notesPath) && File.ReadAllText(notesPath).Contains("Harness QA");
        if (!saved) failures++;
        Debug.Log("[Mao Harness Smoke] note_saved=" + saved);
        complete = false;
        chat.SendSilent("xem danh sách ghi chú của anh", text => reply = text, () => complete = true);
        deadline = Time.realtimeSinceStartup + 105f;
        while (!complete && Time.realtimeSinceStartup < deadline) yield return null;
        bool listed = complete && harness.LastOutcome == "completed" && harness.LastToolCount >= 1;
        if (!listed) failures++;
        Debug.Log("[Mao Harness Smoke] notes_listed=" + listed);
        chat.SendSilent("harness chat Giải thích ngắn ý nghĩa của thời gian", _ => { }, () => { });
        yield return new WaitForSecondsRealtime(0.5f);
        string cancelledId = controlSmoke ? harness.ControlLayer.Tasks.ActiveId : null;
        bool readOnlyStatus = true;
        if (controlSmoke)
        {
            chat.SendSilent("Miku đang làm gì?", text => reply = text, () => { });
            chat.SendSilent("xem lịch sử tác vụ nhé?", text => reply = text, () => { });
            readOnlyStatus = cancelledId != null && harness.ControlLayer.Tasks.IsRunning(cancelledId) && harness.IsBusy;
            if (!readOnlyStatus) failures++;
            Debug.Log("[Mao Control Smoke] status_preserves_running_task=" + readOnlyStatus);
        }
        chat.SendSilent("Miku dừng tác vụ lại đi", text => reply = text, () => { });
        yield return new WaitForSecondsRealtime(3f);
        bool cancelled = !harness.IsBusy && harness.LastOutcome == "cancelled";
        if (controlSmoke) cancelled &= harness.ControlLayer.Tasks.Get(cancelledId)?.Status == MaoTaskStatus.Cancelled;
        if (!cancelled) failures++;
        Debug.Log("[Mao Harness Smoke] cancelled=" + cancelled);
        chat.SendSilent("harness đang chạy không", text => reply = text, () => { });
        bool status = reply.Contains("Harness đang bật");
        if (!status) failures++;
        bool stoppedAll = true, auditRecorded = true;
        if (controlSmoke)
        {
            chat.SendSilent("xem lịch sử tác vụ", text => reply = text, () => { });
            bool history = reply.Contains("Cancelled") && reply.Contains("Completed");
            chat.SendSilent("xem audit hôm nay", text => reply = text, () => { });
            auditRecorded = history && harness.ControlLayer.Audit.ReadToday(100).Any(e => e.taskId == cancelledId && e.outcome == "cancelled");
            if (!auditRecorded) failures++;
            chat.SendSilent("harness chat Nói một câu ngắn về âm nhạc", _ => { }, () => { });
            yield return new WaitForSecondsRealtime(0.5f);
            string stopId = harness.ControlLayer.Tasks.ActiveId;
            chat.SendSilent("Miku dừng mọi thứ", text => reply = text, () => { });
            deadline = Time.realtimeSinceStartup + 12f;
            while (harness.OwnedBridgeAlive && Time.realtimeSinceStartup < deadline) yield return null;
            stoppedAll = stopId != null && harness.ControlLayer.Tasks.Get(stopId)?.Status == MaoTaskStatus.Cancelled &&
                !harness.IsBusy && !harness.OwnedBridgeAlive;
            if (!stoppedAll) failures++;
            Debug.Log("[Mao Control Smoke] history_audit=" + auditRecorded + " stop_all=" + stoppedAll);
        }
        complete = false;
        reply = string.Empty;
        chat.SendSilent("Reply with exactly MAO_CHAT_OK and nothing else.", text => reply = text, () => complete = true);
        deadline = Time.realtimeSinceStartup + 105f;
        while (!complete && Time.realtimeSinceStartup < deadline) yield return null;
        bool casualChat = complete && reply.Trim() == "MAO_CHAT_OK" &&
            (!requireHhtech || chat.LastTextBackendUsed.IndexOf("hhtech", StringComparison.OrdinalIgnoreCase) >= 0);
        if (!casualChat) failures++;
        Debug.Log("[Mao Harness Smoke] casual_chat=" + casualChat + " backend=" + chat.LastTextBackendUsed);
        chat.ProactiveConversationEnabled = proactive;
        harness.SetPermissionMode(permissionMode);
        File.WriteAllText(Path.Combine(Application.persistentDataPath, "MaoHarnessSmoke.json"),
            "{\"failures\":" + failures + ",\"actions\":" + actions.ToString().ToLowerInvariant() +
            ",\"noteSaved\":" + saved.ToString().ToLowerInvariant() + ",\"listed\":" + listed.ToString().ToLowerInvariant() +
            ",\"cancelled\":" + cancelled.ToString().ToLowerInvariant() + ",\"casualChat\":" + casualChat.ToString().ToLowerInvariant() +
            ",\"controlLayer\":" + controlSmoke.ToString().ToLowerInvariant() + ",\"taskCompleted\":" + taskCompleted.ToString().ToLowerInvariant() +
            ",\"readOnlyStatus\":" + readOnlyStatus.ToString().ToLowerInvariant() + ",\"auditRecorded\":" + auditRecorded.ToString().ToLowerInvariant() + ",\"stopAll\":" + stoppedAll.ToString().ToLowerInvariant() +
            ",\"taskProvider\":\"" + taskProvider + "\",\"chatBackend\":\"" + chat.LastTextBackendUsed + "\"}");
        Debug.Log("[Mao Harness Smoke] failures=" + failures);
        harness.Cancel();
        if (Environment.GetCommandLineArgs().Any(a => a == "--mao-smoke-exit")) Application.Quit(failures == 0 ? 0 : 1);
    }
}
