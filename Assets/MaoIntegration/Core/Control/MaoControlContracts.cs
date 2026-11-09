using System;
using System.Collections;
using System.Collections.Generic;

namespace MaoIntegration
{
    public enum MaoTaskStatus { Created, Planning, WaitingPermission, Running, WaitingAgent, Verifying, Completed, Failed, Cancelled }
    public enum MaoToolRisk { ReadOnly = 0, SafeWrite = 1, Sensitive = 2, Critical = 3 }

    public sealed class MaoTaskSnapshot
    {
        public string Id { get; }
        public string OriginalRequest { get; }
        public string Agent { get; }
        public string Outcome { get; }
        public MaoTaskStatus Status { get; }
        public DateTime CreatedUtc { get; }
        public DateTime UpdatedUtc { get; }
        public bool IsTerminal => Status == MaoTaskStatus.Completed || Status == MaoTaskStatus.Failed || Status == MaoTaskStatus.Cancelled;
        internal MaoTaskSnapshot(string id, string request, string agent, string outcome, MaoTaskStatus status, DateTime created, DateTime updated)
        { Id = id; OriginalRequest = request; Agent = agent; Outcome = outcome; Status = status; CreatedUtc = created; UpdatedUtc = updated; }
    }

    // Raw context exists only while executing; it is not part of the persistent audit schema.
    public sealed class MaoTaskContext
    {
        public string TaskId { get; }
        public string UserRequest { get; }
        public string AgentContext { get; }
        public MaoTaskContext(string id, string request, string context)
        { TaskId = id; UserRequest = request ?? string.Empty; AgentContext = context ?? string.Empty; }
    }
    public sealed class MaoAgentResult
    {
        public bool Success { get; }
        public string Outcome { get; }
        public string Reply { get; }
        public MaoAgentResult(bool success, string outcome, string reply)
        { Success = success; Outcome = outcome ?? "failed"; Reply = reply ?? string.Empty; }
    }
    public interface IMaoControlAgent
    {
        string Name { get; }
        IReadOnlyList<string> Capabilities { get; }
        bool CanHandle(string request);
        IEnumerator Execute(MaoTaskContext context, Action<string> progress, Action<MaoAgentResult> finished);
        void Cancel(string taskId);
        string GetStatus();
    }
    public sealed class MaoToolCall
    {
        public string TaskId { get; }
        public string CallId { get; }
        public string Tool { get; }
        public string Target { get; }
        public string UserRequest { get; }
        public IReadOnlyDictionary<string, string> Arguments { get; }
        public MaoToolCall(string taskId, string callId, string tool, string target, string request, IDictionary<string, string> arguments = null)
        {
            TaskId = taskId; CallId = callId; Tool = tool; Target = target; UserRequest = request ?? string.Empty;
            Arguments = new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(
                arguments == null ? new Dictionary<string, string>() : new Dictionary<string, string>(arguments));
        }
        public string Argument(string key) => Arguments.TryGetValue(key, out string value) ? value : null;
    }
    public sealed class MaoToolResult
    {
        public bool Success { get; }
        public string Code { get; }
        public string Reply { get; }
        public MaoToolResult(bool success, string code, string reply)
        { Success = success; Code = code; Reply = reply ?? string.Empty; }
    }
    public interface IMaoControlTool
    {
        string Name { get; }
        MaoToolRisk Risk { get; }
        MaoToolResult Execute(MaoToolCall call);
    }
    public sealed class MaoDelegateTool : IMaoControlTool
    {
        private readonly Func<MaoToolCall, MaoToolResult> execute;
        public string Name { get; }
        public MaoToolRisk Risk { get; }
        public MaoDelegateTool(string name, MaoToolRisk risk, Func<MaoToolCall, MaoToolResult> execute)
        { Name = name; Risk = risk; this.execute = execute ?? throw new ArgumentNullException(nameof(execute)); }
        public MaoToolResult Execute(MaoToolCall call) => execute(call);
    }
    public sealed class MaoControlAuditEntry
    {
        public string utc, taskId, agent, tool, target, outcome;
        public int risk;
        public long durationMs;
    }
    public interface IMaoControlAudit { bool TryWrite(MaoControlAuditEntry entry); }
    public sealed class MaoControlEvent
    {
        public MaoTaskSnapshot Task { get; }
        public MaoControlEvent(MaoTaskSnapshot task) { Task = task; }
    }
    public sealed class MaoControlEventBus
    {
        public event Action<MaoControlEvent> Published;
        public int ObserverFailures { get; private set; }
        public void Publish(MaoTaskSnapshot task)
        {
            var handlers = Published;
            if (handlers == null) return;
            foreach (Action<MaoControlEvent> handler in handlers.GetInvocationList())
                try { handler(new MaoControlEvent(task)); } catch (Exception) { ObserverFailures++; }
        }
    }
}
