using System;
using System.Collections.Generic;
using System.Linq;

namespace MaoIntegration
{
    // All operations are owned by Unity's main thread. Node output is queued until Update.
    public sealed class MaoTaskManager
    {
        private sealed class Record
        {
            public string id, request, agent = string.Empty, outcome = "created";
            public MaoTaskStatus status = MaoTaskStatus.Created;
            public DateTime created, updated;
            public MaoTaskSnapshot Snapshot() => new MaoTaskSnapshot(id, request, agent, outcome, status, created, updated);
        }
        private readonly List<Record> records = new List<Record>();
        private readonly IMaoControlAudit audit;
        private readonly int capacity;
        public MaoControlEventBus Events { get; }
        public string ActiveId { get; private set; }
        public MaoTaskManager(IMaoControlAudit audit, MaoControlEventBus events = null, int capacity = 64)
        { this.audit = audit ?? throw new ArgumentNullException(nameof(audit)); Events = events ?? new MaoControlEventBus(); this.capacity = Math.Max(1, Math.Min(100, capacity)); }
        public MaoTaskSnapshot Create(string request)
        {
            if (ActiveId != null) throw new InvalidOperationException("An owned task is still active.");
            var record = new Record { id = Guid.NewGuid().ToString("N"), request = MaoControlRedaction.Clean(request), created = DateTime.UtcNow, updated = DateTime.UtcNow };
            records.Add(record);
            if (records.Count > capacity) records.RemoveAt(0);
            ActiveId = record.id;
            if (!Write(record, "created")) { record.status = MaoTaskStatus.Failed; record.outcome = "audit_unavailable"; ActiveId = null; }
            Events.Publish(record.Snapshot()); return record.Snapshot();
        }
        public MaoTaskSnapshot Get(string id) => records.Find(record => record.id == id)?.Snapshot();
        public IReadOnlyList<MaoTaskSnapshot> History() => records.Select(record => record.Snapshot()).Reverse().ToArray();
        public bool IsRunning(string id) => !string.IsNullOrEmpty(id) && id == ActiveId && Get(id)?.Status == MaoTaskStatus.Running;
        public bool IsActive(string id) => !string.IsNullOrEmpty(id) && id == ActiveId && (Get(id)?.Status == MaoTaskStatus.Running || Get(id)?.Status == MaoTaskStatus.WaitingPermission);
        public bool Assign(string id, string agent)
        {
            Record record = records.Find(r => r.id == id);
            if (record == null || record.status != MaoTaskStatus.Planning || string.IsNullOrEmpty(agent)) return false;
            record.agent = MaoControlRedaction.Label(agent); return true;
        }
        public bool Transition(string id, MaoTaskStatus next, string outcome = null)
        {
            Record record = records.Find(r => r.id == id);
            if (record == null || record.Snapshot().IsTerminal || !Allowed(record.status, next)) return false;
            string code = MaoControlRedaction.Label(outcome ?? next.ToString().ToLowerInvariant());
            // Cancellation remains terminal even when storage fails. Other transitions fail closed.
            bool logged = Write(record, code);
            record.status = !logged && next != MaoTaskStatus.Cancelled ? MaoTaskStatus.Failed : next;
            record.outcome = logged ? code : "audit_unavailable"; record.updated = DateTime.UtcNow;
            if (record.Snapshot().IsTerminal && ActiveId == id) ActiveId = null;
            Events.Publish(record.Snapshot()); return logged || next == MaoTaskStatus.Cancelled;
        }
        public bool Cancel(string id) => Transition(id, MaoTaskStatus.Cancelled, "cancelled");
        private bool Write(Record record, string outcome)
        {
            try { return audit.TryWrite(new MaoControlAuditEntry { taskId = record.id, agent = record.agent, outcome = outcome }); }
            catch (Exception) { return false; }
        }
        private static bool Allowed(MaoTaskStatus current, MaoTaskStatus next)
        {
            if (next == MaoTaskStatus.Cancelled || next == MaoTaskStatus.Failed) return true;
            return current == MaoTaskStatus.Created && next == MaoTaskStatus.Planning ||
                   current == MaoTaskStatus.Planning && next == MaoTaskStatus.Running ||
                   current == MaoTaskStatus.Running && next == MaoTaskStatus.WaitingPermission ||
                   current == MaoTaskStatus.WaitingPermission && next == MaoTaskStatus.Running ||
                   current == MaoTaskStatus.Running && next == MaoTaskStatus.Verifying ||
                   current == MaoTaskStatus.Verifying && next == MaoTaskStatus.Completed;
        }
    }
    public sealed class MaoAgentRouter
    {
        private readonly List<IMaoControlAgent> agents = new List<IMaoControlAgent>();
        public void Register(IMaoControlAgent agent)
        {
            if (agent == null || string.IsNullOrWhiteSpace(agent.Name) || agents.Any(a => a.Name == agent.Name))
                throw new ArgumentException("Agent names must be unique.");
            agents.Add(agent);
        }
        public IMaoControlAgent Route(string request)
        {
            foreach (IMaoControlAgent agent in agents)
                try { if (agent.CanHandle(request)) return agent; } catch (Exception) { }
            return null;
        }
        public IReadOnlyList<string> Names => agents.Select(a => a.Name).ToArray();
    }
}
