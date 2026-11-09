using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MaoIntegration;
using UnityEngine;
using Debug = UnityEngine.Debug;

[Serializable]
public sealed class MaoHarnessPacket
{
    public string type, id, text, context, message, code, callId, kind, target, title, content, noteId, provider, executionSummary, path, command, cwd, url, permissionMode;
    public bool success, overwrite, requiresTools;
    public int toolCount, failedToolCount, protocol, timeoutSeconds;
    public long expiresAtUnixMs;
}

public sealed class MaoHarnessRuntime : MonoBehaviour
{
    private const string EnabledPreference = "MaoHarness.Enabled";
    private readonly ConcurrentQueue<string> incoming = new ConcurrentQueue<string>();
    private readonly HashSet<string> completedCalls = new HashSet<string>();
    private Process bridge;
    private StreamWriter writer;
    private string activeId, currentUserText;
    private bool bridgeReady, stopping;
    private MaoHarnessPacket completion;
    private int queuedLines;
    private int closingBridges;
    private DateTime nextOpenUtc = DateTime.MinValue;
    private MaoControlLayerRuntime controlLayer;
    private MaoPermissionPolicy permissions;
    private CancellationTokenSource toolCancellation;
    private bool toolBusy;
    public MaoPermissionPolicy Permissions => permissions ?? (permissions = new MaoPermissionPolicy((MaoPermissionMode)PlayerPrefs.GetInt("MaoPermissions.Mode", (int)MaoPermissionMode.Ask)));
    public string Workspace => PlayerPrefs.GetString("MaoPermissions.Workspace", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
    public void ShowPermissions() { (GetComponent<MaoPermissionPanel>() ?? gameObject.AddComponent<MaoPermissionPanel>()).Show(this); }
    public void SetPermissionMode(MaoPermissionMode mode)
    {
        ShutdownOwnedBridge(); Permissions.SetMode(mode);
        PlayerPrefs.SetInt("MaoPermissions.Mode", (int)Permissions.Mode); PlayerPrefs.Save();
        Debug.Log("[Mao Permissions] mode=" + Permissions.WireMode);
    }
    public string PermissionStatus => "Quyền: " + Permissions.WireMode + ". Workspace: " + Workspace + ". Full access dùng tài khoản Windows hiện tại, không tự nâng Administrator.";
    public bool DecidePermission(bool allow, string callId = null) => Permissions.Decide(callId ?? Permissions.Pending?.CallId, allow);
    public MaoControlLayerRuntime ControlLayer
    {
        get
        {
            if (controlLayer == null) { controlLayer = GetComponent<MaoControlLayerRuntime>() ?? gameObject.AddComponent<MaoControlLayerRuntime>(); controlLayer.Initialize(this); }
            return controlLayer;
        }
    }

    public bool Enabled => PlayerPrefs.GetInt(EnabledPreference, 1) == 1;
    public bool IsBusy => !string.IsNullOrEmpty(activeId);
    public string LastProvider { get; private set; } = string.Empty;
    public int LastToolCount { get; private set; }
    public string LastOutcome { get; private set; } = "idle";
    public bool Installed => ResolveBridge() != null && ResolveNode(ResolveBridge()) != null;
    public bool OwnedBridgeAlive => BridgeAlive() || System.Threading.Volatile.Read(ref closingBridges) > 0;

    public bool TryHandleControl(string input, out string reply)
    {
        string permission = MaoPermissionPolicy.Control(input);
        if (permission != null)
        {
            if (permission == "full" || permission == "ask" || permission == "read-only") SetPermissionMode(permission == "full" ? MaoPermissionMode.FullAccess : permission == "ask" ? MaoPermissionMode.Ask : MaoPermissionMode.ReadOnly);
            if (permission.StartsWith("allow") || permission.StartsWith("deny"))
            {
                bool allowed = permission.StartsWith("allow"); int separator = permission.IndexOf(':');
                reply = DecidePermission(allowed, separator < 0 ? null : permission.Substring(separator + 1)) ? allowed ? "Đã cho phép đúng bước đang chờ, một lần." : "Đã từ chối bước đang chờ." : "Không có bước phù hợp đang chờ duyệt.";
            }
            else { reply = PermissionStatus; if (permission == "status") ShowPermissions(); }
            return true;
        }
        if ((input ?? "").Trim().Equals("/workspace", StringComparison.OrdinalIgnoreCase)) { reply = PermissionStatus; return true; }
        if ((input ?? "").Trim().StartsWith("/workspace ", StringComparison.OrdinalIgnoreCase))
        {
            string candidate = input.Trim().Substring(11).Trim().Trim('"');
            try { if (!Path.IsPathRooted(candidate) || !Directory.Exists(candidate)) throw new ArgumentException(); candidate = Path.GetFullPath(candidate); }
            catch { reply = "Workspace phải là thư mục tuyệt đối đang tồn tại."; return true; }
            ShutdownOwnedBridge(); PlayerPrefs.SetString("MaoPermissions.Workspace", candidate); PlayerPrefs.Save(); reply = PermissionStatus; return true;
        }
        if (ControlLayer.TryHandleControl(input, out reply)) return true;
        reply = null;
        switch (MaoHarnessPolicy.Control(input))
        {
            case MaoHarnessControl.Enable:
                PlayerPrefs.SetInt(EnabledPreference, 1); PlayerPrefs.Save();
                GetComponent<MaoComputerAgentRuntime>()?.TryHandleUserCommand("/computer on", out _);
                reply = Installed ? "Đã bật Harness. Anh cứ nhắn mở app, web hoặc lưu ghi chú cho Miku nha." : "Đã bật Harness, nhưng bộ chạy chưa được cài đủ.";
                return true;
            case MaoHarnessControl.Disable:
                Cancel(); PlayerPrefs.SetInt(EnabledPreference, 0); PlayerPrefs.Save(); StopBridge();
                reply = "Đã tắt Harness rồi nha."; return true;
            case MaoHarnessControl.Cancel:
                bool wasBusy = IsBusy; Cancel();
                reply = wasBusy ? "Đã dừng tác vụ rồi nha. Các ứng dụng đã mở vẫn giữ nguyên." : "Miku không có tác vụ nào đang chạy.";
                return true;
            case MaoHarnessControl.Status:
                reply = !Installed ? "Harness chưa được cài đủ." : !Enabled ? "Harness đang tắt." :
                    "Harness đang bật" + (IsBusy ? ", đang xử lý tác vụ." : ", sẵn sàng nhận tác vụ.") +
                    (LastProvider.Length == 0 ? string.Empty : " AI gần nhất: " + LastProvider + ".");
                return true;
            default: return false;
        }
    }

    public IEnumerator RunRoutine(string userText, string context, Action<string> onProgress, Action<string> onFinished)
    {
        return ControlLayer.RunRoutine(userText, context, onProgress, onFinished);
    }

    internal IEnumerator RunManagedRoutine(string userText, string context, string jobId, Action<string> onProgress, Action<string> onFinished)
    {
        if (!Enabled) { LastOutcome = "disabled"; onFinished("Harness đang tắt. Anh nhắn “bật harness” để bật nhé."); yield break; }
        if (!Installed) { LastOutcome = "not_installed"; onFinished("Miku chưa tìm thấy bộ chạy Harness. Cần cài bản Miku có Harness."); yield break; }
        if (!ControlLayer.Tasks.IsRunning(jobId)) yield break;
        activeId = jobId; currentUserText = userText; completion = null; completedCalls.Clear(); LastToolCount = 0; LastOutcome = "running";
        onProgress?.Invoke("Miku đang xử lý tác vụ…");
        if (!StartBridge())
        {
            activeId = null; LastOutcome = "launch_failed";
            onFinished("Miku chưa khởi động được bộ chạy tác vụ."); yield break;
        }
        float deadline = Time.realtimeSinceStartup + (Permissions.Mode == MaoPermissionMode.ReadOnly ? 100f : 610f);
        while (!bridgeReady && activeId == jobId && Time.realtimeSinceStartup < deadline && BridgeAlive()) yield return null;
        if (activeId != jobId) yield break;
        string hostContext = "Workspace: " + Workspace + "\nQuyền do người dùng chọn: " + Permissions.WireMode + "\n" + context;
        if (hostContext.Length > 6000) hostContext = hostContext.Substring(0, 6000);
        if (!bridgeReady || !SendPacket(new MaoHarnessPacket { type = "run", id = jobId, text = userText, context = hostContext, permissionMode = Permissions.WireMode, requiresTools = MaoHarnessPolicy.RequiresToolExecution(userText) }))
        {
            ClearFailedJob(jobId); LastOutcome = "launch_failed";
            onFinished("Miku chưa kết nối được bộ chạy tác vụ."); yield break;
        }
        Debug.Log("[Mao Harness] event=task_started id=" + jobId);
        while (activeId == jobId && completion == null && Time.realtimeSinceStartup < deadline && BridgeAlive()) yield return null;
        if (activeId != jobId) yield break;
        var result = completion;
        if (result == null)
        {
            ClearFailedJob(jobId); LastOutcome = "timeout_or_exit";
            onFinished("Tác vụ chờ hơi lâu hoặc bộ chạy đã dừng. Anh thử lại nhé."); yield break;
        }
        activeId = null; currentUserText = null; completion = null;
        LastProvider = result.provider ?? LastProvider;
        LastToolCount = result.toolCount;
        LastOutcome = result.type == "result" ? "completed" : result.code ?? "failed";
        string answer = result.type == "result" ? result.text : result.message;
        if (result.type == "result" && result.failedToolCount > 0)
        {
            LastOutcome = "partial_failure";
            answer = "Miku chưa hoàn tất mọi bước. Đây là kết quả thực tế:";
        }
        if (result.type == "result" && result.toolCount == 0 && MaoHarnessPolicy.RequiresToolExecution(userText))
        {
            LastOutcome = "no_tool_execution";
            answer = "Miku chưa thực hiện được tác vụ này bằng công cụ.";
        }
        if (result.type == "result" && !string.IsNullOrWhiteSpace(result.executionSummary)) answer += result.executionSummary;
        Debug.Log("[Mao Harness] event=task_finished id=" + jobId + " outcome=" + LastOutcome + " provider=" + LastProvider + " tools=" + LastToolCount);
        onFinished(string.IsNullOrWhiteSpace(answer) ? "Miku chưa nhận được câu trả lời từ bộ chạy tác vụ." : answer);
    }

    public void Cancel()
    {
        toolCancellation?.Cancel(); Permissions.Clear();
        string cancelled = activeId;
        activeId = null; currentUserText = null; completion = null;
        controlLayer?.NotifyCancelled(cancelled);
        if (string.IsNullOrEmpty(cancelled)) return;
        SendPacket(new MaoHarnessPacket { type = "cancel", id = cancelled });
        LastOutcome = "cancelled";
        Debug.Log("[Mao Harness] event=task_cancelled id=" + cancelled);
    }

    internal void CancelOwned(string id) { if (activeId == id) Cancel(); }
    internal void ShutdownOwnedBridge() { Cancel(); StopBridge(); }
    private void ClearFailedJob(string id)
    {
        if (activeId != id) return;
        toolCancellation?.Cancel(); Permissions.Clear();
        activeId = null; currentUserText = null; completion = null;
        SendPacket(new MaoHarnessPacket { type = "cancel", id = id });
    }

    private void Update()
    {
        for (int count = 0; count < 24 && incoming.TryDequeue(out string line); count++)
        {
            System.Threading.Interlocked.Decrement(ref queuedLines);
            MaoHarnessPacket packet;
            try { packet = JsonUtility.FromJson<MaoHarnessPacket>(line); } catch { continue; }
            if (packet == null) continue;
            if (packet.type == "ready") { bridgeReady = packet.protocol == 1; continue; }
            if (string.IsNullOrEmpty(activeId) || packet.id != activeId) continue;
            if (packet.type == "result" || packet.type == "error") completion = packet;
            else if (packet.type == "tool") StartCoroutine(ExecuteTool(packet, currentUserText));
        }
    }

    private IEnumerator ExecuteTool(MaoHarnessPacket packet, string userText)
    {
        if (string.IsNullOrWhiteSpace(packet.callId) || packet.callId.Length > 64 || !completedCalls.Add(packet.callId)) yield break;
        // The executor starts its cooldown after Process.Start returns, not when
        // this coroutine begins. Base spacing on completed launches as well.
        while (packet.kind == "open_target" && DateTime.UtcNow < nextOpenUtc && packet.id == activeId && !ToolExpired(packet)) yield return null;
        if (packet.id != activeId || !Enabled) yield break;
        // Serialize tools, including while a human decision is pending.
        while (toolBusy && packet.id == activeId && !ToolExpired(packet)) yield return null;
        if (packet.id != activeId) yield break;
        if (ToolExpired(packet))
        {
            SendPacket(new MaoHarnessPacket { type = "tool_result", id = packet.id, callId = packet.callId, success = false, message = "Bước này đã hết thời gian chờ; Miku chưa thực hiện." });
            yield break;
        }
        toolBusy = true;
        try
        {
            string auditTarget = packet.kind == "open_target" ? (MaoHarnessPolicy.IsKnownTarget(packet.target) ? packet.target : "unknown-target") : packet.kind == "run_powershell" ? "powershell" : packet.kind == "open_url" ? "web" : packet.kind?.Contains("file") == true || packet.kind == "list_directory" ? "filesystem" : "local-notes";
            var call = new MaoToolCall(packet.id, packet.callId, packet.kind, auditTarget, userText,
                new Dictionary<string, string> { ["target"] = packet.target, ["title"] = packet.title, ["content"] = packet.content, ["noteId"] = packet.noteId, ["path"] = packet.path, ["command"] = packet.command, ["cwd"] = packet.cwd, ["url"] = packet.url, ["overwrite"] = packet.overwrite.ToString(), ["timeoutSeconds"] = (packet.timeoutSeconds == 0 ? 60 : packet.timeoutSeconds).ToString() });
            MaoToolResult result = null;
            if (ControlLayer.Tools.TryRisk(packet.kind, out MaoToolRisk risk) && risk != MaoToolRisk.ReadOnly && Permissions.Mode == MaoPermissionMode.Ask)
            {
                if (!Permissions.Request(call)) result = new MaoToolResult(false, "approval_busy", "Đã có bước khác đang chờ duyệt.");
                else
                {
                    ControlLayer.Tasks.Transition(packet.id, MaoTaskStatus.WaitingPermission); ShowPermissions();
                    while (packet.id == activeId && !ToolExpired(packet) && !Permissions.Rejected && !Permissions.IsApproved(call)) yield return null;
                    if (packet.id != activeId) yield break;
                    if (!Permissions.IsApproved(call)) { result = new MaoToolResult(false, "permission_denied", "Anh chưa cho phép bước này hoặc đã từ chối; chưa thực hiện."); Permissions.Clear(); }
                    ControlLayer.Tasks.Transition(packet.id, MaoTaskStatus.Running);
                }
            }
            // A human approval does not extend the tool lease. Recheck after waiting,
            // immediately before either synchronous or asynchronous execution.
            if (result == null && ToolExpired(packet))
            {
                Permissions.Clear();
                result = new MaoToolResult(false, "approval_expired", "Bước này đã hết thời gian duyệt; Miku chưa thực hiện.");
            }
            if (result == null)
            {
                bool native = packet.kind == "list_directory" || packet.kind == "read_file" || packet.kind == "write_file" || packet.kind == "run_powershell" || packet.kind == "open_url";
                if (!native) result = ControlLayer.Tools.Execute(call);
                else if (ControlLayer.Tools.TryBeginCall(call, out _, out result))
                {
                    var executor = new MaoComputerToolExecutor(Workspace, Path.Combine(Application.persistentDataPath, "MikuFileBackups"));
                    var cancellation = new CancellationTokenSource(); toolCancellation = cancellation;
                    var timer = Stopwatch.StartNew();
                    Task<MaoToolResult> work = Task.Run(() => { try { return executor.Execute(call, cancellation.Token); } catch (OperationCanceledException) { return new MaoToolResult(false, "cancelled", "Đã hủy bước này. Các thay đổi đã chạy trước đó không tự hoàn tác."); } catch (Exception exception) { return new MaoToolResult(false, "executor_failed", "Bước này lỗi: " + exception.GetType().Name); } });
                    while (!work.IsCompleted && packet.id == activeId && !ToolExpired(packet)) yield return null;
                    if (!work.IsCompleted) cancellation.Cancel();
                    while (!work.IsCompleted) yield return null;
                    if (ReferenceEquals(toolCancellation, cancellation)) toolCancellation = null;
                    cancellation.Dispose();
                    if (packet.id != activeId) yield break;
                    result = ControlLayer.Tools.CompleteCall(call, work.Result, timer.ElapsedMilliseconds);
                }
            }
            if (packet.id == activeId && result != null) SendPacket(new MaoHarnessPacket { type = "tool_result", id = packet.id, callId = packet.callId, success = result.Success, message = MaoControlRedaction.CleanContent(result.Reply, 11500) });
        }
        finally { toolBusy = false; }
    }

    internal MaoToolResult ExecuteValidatedTool(MaoToolCall call)
    {
        bool success = false;
        string reply = "Miku không thực hiện được công cụ này.";
        try
        {
            if (call.Tool == "open_target")
            {
                MaoComputerAction action;
                bool found = MaoComputerActionCatalog.TryResolveApplication(call.Argument("target"), out action) ||
                    MaoComputerActionCatalog.TryResolveWebsite(call.Argument("target"), out action) || MaoComputerActionCatalog.TryResolveSettings(call.Argument("target"), out action);
                if (!found) reply = "Mục này không nằm trong danh sách ứng dụng.";
                else
                {
                    var executor = GetComponent<MaoComputerAgentRuntime>();
                    if (!executor.Enabled) executor.TryHandleUserCommand("/computer on", out _);
                    reply = executor.ExecuteAuthorizedAction(action); success = executor.LastExecutionSucceeded;
                    nextOpenUtc = DateTime.UtcNow.AddSeconds(2.1);
                }
            }
            else if (call.Tool == "save_note" || call.Tool == "list_notes" || call.Tool == "read_note")
            {
                var notes = new MaoHarnessNotes(Path.Combine(Application.persistentDataPath, "mao-harness-notes.json"));
                success = notes.Execute(call.Tool, call.Argument("title"), call.Argument("content"), call.Argument("noteId"), out reply);
            }
            else reply = "Miku chưa được yêu cầu thao tác với ghi chú.";
        }
        catch (Exception exception)
        {
            Debug.LogWarning("[Mao Harness] event=tool_failed error_type=" + exception.GetType().Name);
            reply = "Miku gặp lỗi khi thực hiện bước này.";
        }
        return new MaoToolResult(success, success ? "completed" : "executor_denied_or_failed", reply);
    }

    private bool StartBridge()
    {
        if (BridgeAlive()) return true;
        StopBridge(); stopping = false; bridgeReady = false;
        while (incoming.TryDequeue(out _)) { }
        queuedLines = 0;
        try
        {
            string script = ResolveBridge();
            var start = new ProcessStartInfo { FileName = ResolveNode(script),
                Arguments = Quote(script) + " " + Quote("--data=" + Path.Combine(Application.persistentDataPath, "HarnessRuntime")),
                WorkingDirectory = Path.GetDirectoryName(script), UseShellExecute = false, CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
#if MAO_DEFER_HHTECH
            start.EnvironmentVariables["MIKU_HARNESS_DEFER_HHTECH"] = "1";
#endif
            var launched = new Process { StartInfo = start };
            bridge = launched;
            launched.OutputDataReceived += (_, args) =>
            {
                if (stopping || !ReferenceEquals(bridge, launched) || string.IsNullOrEmpty(args.Data) || args.Data.Length > 80000) return;
                if (System.Threading.Interlocked.Increment(ref queuedLines) > 128) { System.Threading.Interlocked.Decrement(ref queuedLines); return; }
                incoming.Enqueue(args.Data);
            };
            bridge.ErrorDataReceived += (_, __) => { };
            bridge.Start();
            writer = new StreamWriter(bridge.StandardInput.BaseStream, new UTF8Encoding(false)) { AutoFlush = true };
            bridge.BeginOutputReadLine(); bridge.BeginErrorReadLine();
            return true;
        }
        catch (Exception exception)
        {
            Debug.LogWarning("[Mao Harness] event=launch_failed error_type=" + exception.GetType().Name);
            StopBridge(); return false;
        }
    }

    private static bool ToolExpired(MaoHarnessPacket packet) =>
        !MaoPermissionPolicy.DeadlineOpen(packet.expiresAtUnixMs, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

    private bool SendPacket(MaoHarnessPacket packet)
    {
        try { if (!BridgeAlive() || writer == null) return false; writer.WriteLine(JsonUtility.ToJson(packet)); return true; }
        catch (IOException) { return false; }
        catch (ObjectDisposedException) { return false; }
    }
    private bool BridgeAlive() { try { return bridge != null && !bridge.HasExited; } catch (InvalidOperationException) { return false; } }

    private static string ResolveBridge()
    {
        string app = Path.GetDirectoryName(Application.dataPath);
        string[] candidates = { Path.Combine(app, "MaoAssets", "Harness", "bridge.mjs"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MaoMateEngine", "Harness", "bridge.mjs")
#if UNITY_EDITOR
            , Path.Combine(app, "Tools", "MikuHarness", "bridge.mjs")
#endif
        };
        foreach (string candidate in candidates) if (File.Exists(candidate)) return candidate;
        return null;
    }
    private static string ResolveNode(string script)
    {
        if (script == null) return null;
        string bundled = Path.Combine(Path.GetDirectoryName(script), "node.exe");
        if (File.Exists(bundled)) return bundled;
        string system = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs", "node.exe");
        return File.Exists(system) ? system : null;
    }
    private static string Quote(string text) => "\"" + text.Replace("\"", string.Empty) + "\"";

    private void StopBridge()
    {
        if (bridge == null) return;
        SendPacket(new MaoHarnessPacket { type = "shutdown" });
        stopping = true;
        try { writer?.Dispose(); } catch (IOException) { }
        writer = null;
        Process owned = bridge; bridge = null; bridgeReady = false;
        System.Threading.Interlocked.Increment(ref closingBridges);
        System.Threading.Tasks.Task.Run(() => { try { if (!owned.WaitForExit(10000)) owned.Kill(); owned.WaitForExit(2000); } catch (Exception) { }
            finally { owned.Dispose(); System.Threading.Interlocked.Decrement(ref closingBridges); } });
    }
    private void OnApplicationQuit() { Cancel(); StopBridge(); }
    private void OnDestroy() { Cancel(); StopBridge(); }
}
