using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MaoIntegration;
using UnityEngine;

// A thin Unity adapter around the pure foundation; no new shell, model or OS executor.
public sealed class MaoControlLayerRuntime : MonoBehaviour
{
    private MaoHarnessRuntime harness;
    private IMaoControlAgent activeAgent;
    private string ownedId;
    public MaoTaskManager Tasks { get; private set; }
    public MaoToolRegistry Tools { get; private set; }
    public MaoAgentRouter Router { get; private set; }
    public MaoControlAudit Audit { get; private set; }

    public void Initialize(MaoHarnessRuntime runtime)
    {
        InitializeCore(runtime, Path.Combine(Application.persistentDataPath, "ControlLayerAudit"));
    }
#if UNITY_EDITOR
    public void InitializeForValidation(MaoHarnessRuntime runtime, string auditDirectory)
    {
        InitializeCore(runtime, auditDirectory);
    }
#endif
    private void InitializeCore(MaoHarnessRuntime runtime, string auditDirectory)
    {
        if (Tasks != null) return;
        harness = runtime;
        Audit = new MaoControlAudit(auditDirectory);
        Tasks = new MaoTaskManager(Audit);
        Router = new MaoAgentRouter(); Router.Register(new HarnessAgent(runtime));
        Tools = new MaoToolRegistry(Tasks, Audit);
        Tools.PermissionCheck = runtime.Permissions.Authorize;
        foreach (string name in new[] { "open_target", "save_note", "list_notes", "read_note", "list_directory", "read_file", "write_file", "run_powershell", "open_url" })
            Tools.Register(new MaoDelegateTool(name, name == "run_powershell" ? MaoToolRisk.Critical : name == "list_notes" || name == "read_note" || name == "list_directory" || name == "read_file" ? MaoToolRisk.ReadOnly : MaoToolRisk.SafeWrite, runtime.ExecuteValidatedTool));
        Tasks.Events.Published += e => Debug.Log("[Mao Control] event=task_state id=" + e.Task.Id + " status=" + e.Task.Status + " outcome=" + e.Task.Outcome);
    }

    public bool TryHandleControl(string input, out string reply)
    {
        reply = null;
        switch (MaoControlCommandPolicy.Parse(input))
        {
            case MaoControlCommand.Status:
                var task = Tasks.Get(Tasks.ActiveId);
                reply = task == null ? "Miku không có tác vụ Control Layer đang chạy." : "Miku đang xử lý tác vụ " + Short(task.Id) + " qua " + task.Agent + " (" + task.Status + ").";
                return true;
            case MaoControlCommand.History:
                var history = Tasks.History().Take(5).ToArray();
                reply = history.Length == 0 ? "Chưa có tác vụ trong phiên này nha." : "Tác vụ gần đây trong phiên:\n" + string.Join("\n", history.Select(t => Short(t.Id) + " · " + t.Status + " · " + t.Outcome));
                return true;
            case MaoControlCommand.Audit:
                var entries = Audit.ReadToday(8);
                reply = entries.Count == 0 ? "Chưa có audit hôm nay, hoặc file chưa đọc được." : "Audit UTC hôm nay:\n" + string.Join("\n", entries.Select(e => Short(e.taskId) + " · " + (string.IsNullOrEmpty(e.tool) ? "task" : e.tool) + " · " + e.outcome));
                return true;
            case MaoControlCommand.StopAll:
                CancelActive(); harness.ShutdownOwnedBridge();
                reply = "Đã dừng tác vụ Control Layer và bộ chạy Harness. App đã mở vẫn giữ nguyên nha.";
                return true;
            default: return false;
        }
    }

    public IEnumerator RunRoutine(string request, string context, Action<string> progress, Action<string> finished)
    {
        CancelActive(); harness.Cancel();
        MaoTaskSnapshot task = Tasks.Create(request);
        if (task.IsTerminal) { finished?.Invoke("Miku chưa ghi được audit nên chưa chạy tác vụ."); yield break; }
        string id = task.Id;
        if (!Tasks.Transition(id, MaoTaskStatus.Planning)) { finished?.Invoke("Miku chưa ghi được audit nên chưa chạy tác vụ."); yield break; }
        IMaoControlAgent agent = Router.Route(request);
        if (agent == null) { Tasks.Transition(id, MaoTaskStatus.Failed, "no_capable_agent"); finished?.Invoke("Miku chưa có agent phù hợp với tác vụ này."); yield break; }
        Tasks.Assign(id, agent.Name);
        if (!Tasks.Transition(id, MaoTaskStatus.Running)) { finished?.Invoke("Miku chưa ghi được audit nên chưa chạy tác vụ."); yield break; }
        Tools.CallLimit = harness.Permissions.Budget;
        Tools.Begin(id); activeAgent = agent; ownedId = id;
        MaoAgentResult result = null;
        var stack = new Stack<IEnumerator>();
        string failure = null;
        try { stack.Push(agent.Execute(new MaoTaskContext(id, request, context), text => { if (Tasks.IsRunning(id)) progress?.Invoke(text); }, value => result = value)); }
        catch (Exception) { failure = "agent_launch_exception"; }
        float deadline = Time.realtimeSinceStartup + 620f;
        // Unity executes nested IEnumerator values itself, outside an outer catch. Step nested
        // routines explicitly to make exceptions observable. YieldInstruction values still go to Unity.
        // https://docs.unity3d.com/6000.2/Documentation/ScriptReference/MonoBehaviour.StartCoroutine.html
        while (failure == null && stack.Count > 0 && Tasks.IsActive(id))
        {
            if (Time.realtimeSinceStartup >= deadline) { failure = "deadline_exceeded"; break; }
            object yielded = null; bool moved = false;
            try { moved = stack.Peek().MoveNext(); if (moved) yielded = stack.Peek().Current; }
            catch (Exception) { failure = "agent_exception"; }
            if (failure != null) break;
            if (!moved) { DisposeRoutine(stack.Pop()); continue; }
            if (yielded is IEnumerator nested) stack.Push(nested);
            else yield return yielded;
        }
        while (stack.Count > 0) DisposeRoutine(stack.Pop());
        if (!Tasks.IsActive(id)) { Release(id); yield break; }
        if (failure != null)
        {
            Tasks.Transition(id, MaoTaskStatus.Failed, failure); agent.Cancel(id); Release(id);
            finished?.Invoke("Bộ chạy tác vụ gặp lỗi hoặc quá thời gian. Miku đã dừng, không tự chạy lại thao tác."); yield break;
        }
        bool success = result != null && result.Success && Tools.AuditHealthy && Tools.Failed == 0 &&
            (!MaoHarnessPolicy.RequiresToolExecution(request) || Tools.Succeeded > 0);
        string outcome = result?.Outcome ?? "missing_agent_result";
        if (!Tools.AuditHealthy) outcome = "audit_unavailable_after_execution";
        else if (Tools.Failed > 0) outcome = "tool_failure";
        else if (MaoHarnessPolicy.RequiresToolExecution(request) && Tools.Succeeded == 0) outcome = "no_verified_tool_execution";
        bool verifying = Tasks.Transition(id, MaoTaskStatus.Verifying);
        if (verifying) Tasks.Transition(id, success ? MaoTaskStatus.Completed : MaoTaskStatus.Failed, outcome);
        Release(id);
        string reply = result?.Reply ?? "Miku chưa nhận được kết quả tác vụ.";
        if (Tasks.Get(id).Status != MaoTaskStatus.Completed && success)
            reply = "Tác vụ đã trả kết quả nhưng Miku chưa ghi đủ audit; anh kiểm tra trước khi thử lại nhé.";
        else if (!success && outcome == "tool_count_mismatch")
            reply = "Miku chưa xác minh hoàn tất: số bước bridge báo không khớp với công cụ thực sự đã chạy. Anh kiểm tra trước khi thử lại nhé.";
        else if (!success && result != null && result.Success)
            reply = "Miku chưa xác minh hoàn tất tác vụ (" + outcome + "). Anh kiểm tra trước khi chạy lại nhé.";
        finished?.Invoke(reply);
    }

    public void CancelActive()
    {
        string id = Tasks?.ActiveId;
        if (id == null) return;
        // Terminal first, so an agent cannot synchronously execute another tool while cancelling.
        Tasks.Cancel(id);
        IMaoControlAgent agent = activeAgent;
        Release(id); agent?.Cancel(id);
    }
    internal void NotifyCancelled(string id)
    {
        if (id != null && Tasks?.ActiveId == id) Tasks.Cancel(id);
        Release(id);
    }
    private void Release(string id) { if (ownedId == id) { activeAgent = null; ownedId = null; } }
    private static void DisposeRoutine(IEnumerator routine) { try { (routine as IDisposable)?.Dispose(); } catch (Exception) { } }
    private static string Short(string id) => string.IsNullOrEmpty(id) ? "-" : id.Substring(0, Math.Min(8, id.Length));
    private void OnDestroy() { CancelActive(); }

    private sealed class HarnessAgent : IMaoControlAgent
    {
        private readonly MaoHarnessRuntime runtime;
        public HarnessAgent(MaoHarnessRuntime runtime) { this.runtime = runtime; }
        public string Name => "harness";
        public IReadOnlyList<string> Capabilities => new[] { "catalog-open", "local-notes", "files", "powershell", "web-open", "human-permissions" };
        public bool CanHandle(string request) => MaoHarnessPolicy.ShouldRoute(request) && MaoHarnessPolicy.Control(request) == MaoHarnessControl.None;
        public IEnumerator Execute(MaoTaskContext context, Action<string> progress, Action<MaoAgentResult> finished)
        {
            string reply = null;
            yield return runtime.RunManagedRoutine(context.UserRequest, context.AgentContext, context.TaskId, progress, text => reply = text);
            bool verified = runtime.LastOutcome == "completed" && runtime.LastToolCount == runtime.ControlLayer.Tools.Succeeded;
            finished(new MaoAgentResult(verified, verified ? "completed" : runtime.LastOutcome == "completed" ? "tool_count_mismatch" : runtime.LastOutcome, reply));
        }
        public void Cancel(string taskId) { runtime.CancelOwned(taskId); }
        public string GetStatus() => runtime.IsBusy ? "running" : runtime.LastOutcome;
    }
}
