using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace MaoIntegration
{
    public sealed class MaoToolRegistry
    {
        public const int MaxCalls = 6;
        private readonly Dictionary<string, IMaoControlTool> tools = new Dictionary<string, IMaoControlTool>(StringComparer.Ordinal);
        private readonly HashSet<string> seenCalls = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> startedCalls = new HashSet<string>(StringComparer.Ordinal);
        public Func<MaoToolCall, MaoToolRisk, bool> PermissionCheck { get; set; }
        public int CallLimit { get; set; } = MaxCalls;
        public bool TryRisk(string name, out MaoToolRisk risk) { risk = MaoToolRisk.Critical; if (name == null || !tools.TryGetValue(name, out var tool)) return false; risk = tool.Risk; return true; }
        private readonly MaoTaskManager tasks;
        private readonly IMaoControlAudit audit;
        private string owner;
        public int Attempted { get; private set; }
        public int Succeeded { get; private set; }
        public int Failed { get; private set; }
        public bool AuditHealthy { get; private set; } = true;
        public MaoToolRegistry(MaoTaskManager tasks, IMaoControlAudit audit) { this.tasks = tasks; this.audit = audit; }
        public void Register(IMaoControlTool tool)
        {
            if (tool == null || string.IsNullOrWhiteSpace(tool.Name) || tools.ContainsKey(tool.Name)) throw new ArgumentException("Tool names must be unique.");
            tools.Add(tool.Name, tool);
        }
        public void Begin(string taskId)
        {
            if (tasks.ActiveId != taskId || taskId == null) throw new InvalidOperationException("Task does not own execution.");
            if (owner == taskId) throw new InvalidOperationException("Execution lease is already initialized for this task.");
            owner = taskId; seenCalls.Clear(); startedCalls.Clear(); Attempted = Succeeded = Failed = 0; AuditHealthy = true;
        }
        public MaoToolResult Execute(MaoToolCall call)
        {
            if (!TryBeginCall(call, out IMaoControlTool tool, out MaoToolResult denied)) return denied;
            var timer = Stopwatch.StartNew();
            MaoToolResult result;
            try { result = tool.Execute(call) ?? Denied("empty_result"); }
            catch (Exception) { result = Denied("executor_exception"); }
            return CompleteCall(call, result, timer.ElapsedMilliseconds);
        }
        public bool TryBeginCall(MaoToolCall call, out IMaoControlTool tool, out MaoToolResult denied)
        {
            tool = null; denied = BeginCall(call, out tool); return denied == null;
        }
        private MaoToolResult BeginCall(MaoToolCall call, out IMaoControlTool tool)
        {
            tool = null;
            if (call == null || call.TaskId != owner || !tasks.IsRunning(call.TaskId)) return Denied("task_not_running");
            if (!AuditHealthy) return Denied("audit_unavailable");
            if (string.IsNullOrWhiteSpace(call.CallId) || call.CallId.Length > 64) return Reject(call, "invalid_call_id");
            int limit = Math.Max(1, Math.Min(64, CallLimit));
            if (Attempted >= limit && !seenCalls.Contains(call.CallId)) return Reject(call, "call_budget_exceeded");
            if (!seenCalls.Add(call.CallId)) return Reject(call, "duplicate_call");
            if (++Attempted > limit) return Reject(call, "call_budget_exceeded");
            if (string.IsNullOrEmpty(call.Tool) || !tools.TryGetValue(call.Tool, out tool)) return Reject(call, "unknown_tool");
            if (PermissionCheck == null ? tool.Risk > MaoToolRisk.SafeWrite || tool.Risk < MaoToolRisk.ReadOnly : !PermissionCheck(call, tool.Risk)) return Reject(call, "permission_required");
            if (!Write(call, tool.Risk, "started", 0)) { AuditHealthy = false; Failed++; return Denied("audit_unavailable"); }
            // An audit implementation or event observer may cancel synchronously.
            if (!tasks.IsRunning(call.TaskId)) return Denied("task_not_running");
            startedCalls.Add(call.CallId); return null;
        }
        public MaoToolResult CompleteCall(MaoToolCall call, MaoToolResult result, long durationMs)
        {
            if (call == null || call.TaskId != owner || !startedCalls.Remove(call.CallId) || !tasks.IsRunning(call.TaskId)) return Denied("task_not_running");
            result = result ?? Denied("empty_result");
            if (result.Success) Succeeded++; else Failed++;
            if (!Write(call, tools[call.Tool].Risk, result.Code, durationMs))
            { AuditHealthy = false; return new MaoToolResult(false, "audit_unavailable_after_execution", "Bước này đã chạy nhưng Miku chưa ghi được audit; anh kiểm tra kết quả trước khi thử lại nhé."); }
            return result;
        }
        private MaoToolResult Reject(MaoToolCall call, string code)
        {
            Failed++;
            if (!Write(call, MaoToolRisk.ReadOnly, code, 0)) AuditHealthy = false;
            return Denied(code);
        }
        private bool Write(MaoToolCall call, MaoToolRisk risk, string outcome, long duration)
        {
            try { return audit.TryWrite(new MaoControlAuditEntry { taskId = call.TaskId, agent = tasks.Get(call.TaskId)?.Agent,
                tool = call.Tool != null && tools.ContainsKey(call.Tool) ? call.Tool : "unregistered", target = call.Target, risk = (int)risk, outcome = outcome, durationMs = duration }); }
            catch (Exception) { return false; }
        }
        private static MaoToolResult Denied(string code) => new MaoToolResult(false, code, "Miku chưa thực hiện bước này (" + code + ").");
    }
    public enum MaoControlCommand { None, Status, History, Audit, StopAll }
    public static class MaoControlCommandPolicy
    {
        public static MaoControlCommand Parse(string input)
        {
            string text = MaoHarnessPolicy.Normalize(input).Trim(' ', '/', '.', '!', '?');
            text = Regex.Replace(text, @"^miku(?:\s+oi)?\s*[,!:]?\s*", "");
            text = Regex.Replace(text, @"(?:\s+(?:di|nha|nhe|voi|a))+$", "");
            if (text == "task status" || text == "dang lam gi" || text == "dang lam gi vay" || text == "tac vu dang chay khong") return MaoControlCommand.Status;
            if (text == "task history" || text == "xem lich su tac vu") return MaoControlCommand.History;
            if (text == "audit today" || text == "xem audit hom nay" || text == "hom nay may da lam gi tren may") return MaoControlCommand.Audit;
            if (text == "task stopall" || text == "dung moi thu" || text == "dung tat ca tac vu") return MaoControlCommand.StopAll;
            return MaoControlCommand.None;
        }
        public static bool IsReadOnly(string input)
        {
            var command = Parse(input);
            string permission = MaoPermissionPolicy.Control(input);
            if (permission == "status" || permission == "allow" || permission == "deny" || permission?.StartsWith("allow:") == true || permission?.StartsWith("deny:") == true) return true;
            if ((input ?? "").Trim().Equals("/workspace", StringComparison.OrdinalIgnoreCase)) return true;
            return command == MaoControlCommand.Status || command == MaoControlCommand.History || command == MaoControlCommand.Audit || MaoHarnessPolicy.Control(input) == MaoHarnessControl.Status;
        }
    }
}
