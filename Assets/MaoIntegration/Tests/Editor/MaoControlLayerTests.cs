using MaoIntegration;
using NUnit.Framework;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MaoIntegration.Tests
{
    public sealed class MaoControlLayerTests
    {
        private sealed class Audit : IMaoControlAudit
        {
            public bool available = true;
            public readonly List<MaoControlAuditEntry> entries = new List<MaoControlAuditEntry>();
            public bool TryWrite(MaoControlAuditEntry entry) { entries.Add(entry); return available; }
        }
        private sealed class Agent : IMaoControlAgent
        {
            public string Name => "test";
            public IReadOnlyList<string> Capabilities => new[] { "notes" };
            public bool CanHandle(string request) => request == "notes";
            public IEnumerator Execute(MaoTaskContext context, Action<string> progress, Action<MaoAgentResult> finished) { yield break; }
            public void Cancel(string taskId) { }
            public string GetStatus() => "idle";
        }
        private static string Running(MaoTaskManager manager)
        {
            string id = manager.Create("notes").Id;
            Assert.IsTrue(manager.Transition(id, MaoTaskStatus.Planning));
            Assert.IsTrue(manager.Assign(id, "test"));
            Assert.IsTrue(manager.Transition(id, MaoTaskStatus.Running));
            return id;
        }
        private static MaoToolCall Call(string id, string callId = "call1", string tool = "safe") => new MaoToolCall(id, callId, tool, "local-notes", "notes");
        [Test]
        public void FoundationContractsExistBeforeRuntimeMigration()
        {
            foreach (string type in new[] { "MaoTaskManager", "MaoAgentRouter", "MaoToolRegistry", "MaoControlEventBus", "IMaoControlAgent", "IMaoControlTool", "IMaoControlAudit" })
                Assert.IsNotNull(typeof(MaoHarnessPolicy).Assembly.GetType("MaoIntegration." + type), type);
        }
        [Test] public void TaskCreationHasStableIdentityAndReadOnlyHistory()
        {
            var manager = new MaoTaskManager(new Audit());
            var task = manager.Create("hello");
            Assert.AreEqual(32, task.Id.Length); Assert.AreEqual(MaoTaskStatus.Created, task.Status);
            Assert.AreEqual(task.Id, manager.ActiveId); Assert.AreEqual("hello", manager.History()[0].OriginalRequest);
            Assert.IsTrue(manager.Transition(task.Id, MaoTaskStatus.Planning));
            Assert.AreEqual(MaoTaskStatus.Created, task.Status);
        }
        [Test] public void OnlyOneTaskCanOwnExecution()
        {
            var manager = new MaoTaskManager(new Audit()); manager.Create("one");
            Assert.Throws<InvalidOperationException>(() => manager.Create("two"));
        }
        [Test] public void CompletedTaskRequiresVerificationAndCannotRegress()
        {
            var manager = new MaoTaskManager(new Audit()); string id = Running(manager);
            Assert.IsFalse(manager.Transition(id, MaoTaskStatus.Completed));
            Assert.IsTrue(manager.Transition(id, MaoTaskStatus.Verifying));
            Assert.IsTrue(manager.Transition(id, MaoTaskStatus.Completed));
            Assert.IsFalse(manager.Cancel(id)); Assert.IsFalse(manager.Transition(id, MaoTaskStatus.Failed));
            Assert.IsNull(manager.ActiveId);
        }
        [Test] public void CancellationRejectsLateCompletion()
        {
            var manager = new MaoTaskManager(new Audit()); string id = Running(manager);
            Assert.IsTrue(manager.Cancel(id)); Assert.IsFalse(manager.Transition(id, MaoTaskStatus.Verifying));
            Assert.IsFalse(manager.Transition(id, MaoTaskStatus.Completed)); Assert.AreEqual(MaoTaskStatus.Cancelled, manager.Get(id).Status);
        }
        [Test] public void WrongTaskIdentityCannotCancelOwner()
        {
            var manager = new MaoTaskManager(new Audit()); string id = Running(manager);
            Assert.IsFalse(manager.Cancel("wrong")); Assert.IsTrue(manager.IsRunning(id));
        }
        [Test] public void HistoryIsBoundedAndRequestPreviewRedacted()
        {
            var manager = new MaoTaskManager(new Audit(), capacity: 2);
            for (int i = 0; i < 4; i++) { var task = manager.Create("api_key=private-example-value"); manager.Cancel(task.Id); }
            Assert.AreEqual(2, manager.History().Count);
            Assert.IsFalse(manager.History()[0].OriginalRequest.Contains("private-example-value"));
        }
        [Test] public void ObserverExceptionDoesNotBreakTaskState()
        {
            var bus = new MaoControlEventBus(); int received = 0;
            bus.Published += _ => throw new InvalidOperationException(); bus.Published += _ => received++;
            var manager = new MaoTaskManager(new Audit(), bus); string id = Running(manager);
            Assert.IsTrue(manager.Cancel(id)); Assert.AreEqual(4, received); Assert.AreEqual(4, bus.ObserverFailures);
        }
        [Test] public void AuditUnavailableFailsCreationClosed()
        {
            var manager = new MaoTaskManager(new Audit { available = false });
            var task = manager.Create("notes"); Assert.AreEqual(MaoTaskStatus.Failed, task.Status); Assert.IsNull(manager.ActiveId);
        }
        [Test] public void CancellationRemainsTerminalWhenAuditFails()
        {
            var audit = new Audit(); var manager = new MaoTaskManager(audit); string id = Running(manager);
            audit.available = false; Assert.IsTrue(manager.Cancel(id)); Assert.AreEqual(MaoTaskStatus.Cancelled, manager.Get(id).Status);
        }
        [Test] public void RouterSelectsOnlyAvailableCapableAgent()
        {
            var router = new MaoAgentRouter(); router.Register(new Agent());
            Assert.IsNotNull(router.Route("notes")); Assert.IsNull(router.Route("casual greeting"));
            Assert.Throws<ArgumentException>(() => router.Register(new Agent()));
        }
        [Test] public void ToolExecutesAndAuditsActualResult()
        {
            var audit = new Audit(); var manager = new MaoTaskManager(audit); string id = Running(manager);
            var registry = new MaoToolRegistry(manager, audit); int effects = 0;
            registry.Register(new MaoDelegateTool("safe", MaoToolRisk.SafeWrite, _ => { effects++; return new MaoToolResult(true, "completed", "done"); }));
            registry.Begin(id); Assert.IsTrue(registry.Execute(Call(id)).Success);
            Assert.AreEqual(1, effects); Assert.AreEqual(1, registry.Succeeded);
            Assert.IsTrue(audit.entries.Any(entry => entry.tool == "safe" && entry.outcome == "completed"));
        }
        [Test] public void DuplicateCallCannotRepeatWrite()
        {
            var audit = new Audit(); var manager = new MaoTaskManager(audit); string id = Running(manager);
            var registry = new MaoToolRegistry(manager, audit); int effects = 0;
            registry.Register(new MaoDelegateTool("safe", MaoToolRisk.SafeWrite, _ => { effects++; return new MaoToolResult(true, "ok", ""); })); registry.Begin(id);
            registry.Execute(Call(id)); Assert.AreEqual("duplicate_call", registry.Execute(Call(id)).Code); Assert.AreEqual(1, effects);
        }
        [Test] public void CancelledTaskCannotExecuteNextTool()
        {
            var audit = new Audit(); var manager = new MaoTaskManager(audit); string id = Running(manager);
            var registry = new MaoToolRegistry(manager, audit); registry.Begin(id); manager.Cancel(id);
            Assert.AreEqual("task_not_running", registry.Execute(Call(id)).Code); Assert.AreEqual(0, registry.Attempted);
        }
        [Test] public void WrongOwnerAndUnknownToolFailClosed()
        {
            var audit = new Audit(); var manager = new MaoTaskManager(audit); string id = Running(manager);
            var registry = new MaoToolRegistry(manager, audit); registry.Begin(id);
            Assert.AreEqual("task_not_running", registry.Execute(Call("wrong")).Code);
            Assert.AreEqual("unknown_tool", registry.Execute(Call(id)).Code);
        }
        [Test] public void SensitiveAndCriticalToolsNeedFuturePermissionEngine()
        {
            var audit = new Audit(); var manager = new MaoTaskManager(audit); string id = Running(manager);
            var registry = new MaoToolRegistry(manager, audit); int effects = 0;
            foreach (var risk in new[] { MaoToolRisk.Sensitive, MaoToolRisk.Critical })
                registry.Register(new MaoDelegateTool(risk.ToString(), risk, _ => { effects++; return new MaoToolResult(true, "ok", ""); }));
            registry.Begin(id);
            Assert.AreEqual("permission_required", registry.Execute(Call(id, "a", "Sensitive")).Code);
            Assert.AreEqual("permission_required", registry.Execute(Call(id, "b", "Critical")).Code); Assert.AreEqual(0, effects);
        }
        [Test] public void CallBudgetStopsSeventhExecution()
        {
            var audit = new Audit(); var manager = new MaoTaskManager(audit); string id = Running(manager);
            var registry = new MaoToolRegistry(manager, audit); int effects = 0;
            registry.Register(new MaoDelegateTool("safe", MaoToolRisk.ReadOnly, _ => { effects++; return new MaoToolResult(true, "ok", ""); })); registry.Begin(id);
            for (int i = 0; i < 6; i++) Assert.IsTrue(registry.Execute(Call(id, "call" + i)).Success);
            Assert.AreEqual("call_budget_exceeded", registry.Execute(Call(id, "seventh")).Code); Assert.AreEqual(6, effects);
        }
        [Test] public void UnwritableAuditPreventsSideEffect()
        {
            var audit = new Audit(); var manager = new MaoTaskManager(audit); string id = Running(manager);
            var registry = new MaoToolRegistry(manager, audit); int effects = 0;
            registry.Register(new MaoDelegateTool("safe", MaoToolRisk.SafeWrite, _ => { effects++; return new MaoToolResult(true, "ok", ""); })); registry.Begin(id);
            audit.available = false; Assert.AreEqual("audit_unavailable", registry.Execute(Call(id)).Code); Assert.AreEqual(0, effects);
        }
        [Test] public void AuditFailureAfterEffectDoesNotInviteBlindRetry()
        {
            var audit = new Audit(); var manager = new MaoTaskManager(audit); string id = Running(manager);
            var registry = new MaoToolRegistry(manager, audit);
            registry.Register(new MaoDelegateTool("safe", MaoToolRisk.SafeWrite, _ => { audit.available = false; return new MaoToolResult(true, "ok", ""); })); registry.Begin(id);
            Assert.AreEqual("audit_unavailable_after_execution", registry.Execute(Call(id)).Code); Assert.IsFalse(registry.AuditHealthy);
            Assert.AreEqual(1, registry.Succeeded);
            audit.available = true;
            Assert.AreEqual("audit_unavailable", registry.Execute(Call(id, "next")).Code);
            Assert.AreEqual(1, registry.Succeeded);
        }
        [Test] public void ExecutorExceptionProducesStructuredFailure()
        {
            var audit = new Audit(); var manager = new MaoTaskManager(audit); string id = Running(manager);
            var registry = new MaoToolRegistry(manager, audit);
            registry.Register(new MaoDelegateTool("safe", MaoToolRisk.ReadOnly, _ => throw new InvalidOperationException("private"))); registry.Begin(id);
            Assert.AreEqual("executor_exception", registry.Execute(Call(id)).Code); Assert.AreEqual(1, registry.Failed);
        }
        [Test] public void AuditPersistsMetadataOnlyAndRedactsSecrets()
        {
            string directory = Path.Combine(Path.GetTempPath(), "MaoControlTest-" + Guid.NewGuid().ToString("N"));
            try
            {
                var audit = new MaoControlAudit(directory);
                Assert.IsTrue(audit.TryWrite(new MaoControlAuditEntry { taskId = "test", tool = "safe", target = "Bearer private-example", outcome = "api_key=private-example" }));
                string content = File.ReadAllText(Directory.GetFiles(directory)[0]);
                Assert.IsFalse(content.Contains("private-example")); Assert.IsFalse(content.Contains("UserRequest"));
                Assert.AreEqual(1, audit.ReadToday().Count);
            }
            finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
        }
        [Test] public void StatusAndHistoryControlsAreReadOnly()
        {
            foreach (string input in new[] { "/task status", "Miku đang làm gì", "xem lịch sử tác vụ", "/audit today", "harness đang chạy không" })
                Assert.IsTrue(MaoControlCommandPolicy.IsReadOnly(input), input);
            Assert.IsFalse(MaoControlCommandPolicy.IsReadOnly("Miku dừng mọi thứ"));
            Assert.AreEqual(MaoControlCommand.StopAll, MaoControlCommandPolicy.Parse("Miku dừng mọi thứ"));
        }
        [Test] public void NaturalControlQuestionsAcceptPunctuationAndPoliteSuffixes()
        {
            foreach (string input in new[] { "Miku đang làm gì?", "Miku đang làm gì vậy?", "xem lịch sử tác vụ nhé?", "Miku ơi xem audit hôm nay nha!" })
                Assert.IsTrue(MaoControlCommandPolicy.IsReadOnly(input), input);
        }
        [Test] public void SameTaskCannotResetDuplicateProtectionOrBudget()
        {
            var audit = new Audit(); var manager = new MaoTaskManager(audit); string id = Running(manager);
            var registry = new MaoToolRegistry(manager, audit); int effects = 0;
            registry.Register(new MaoDelegateTool("safe", MaoToolRisk.SafeWrite, _ => { effects++; return new MaoToolResult(true, "ok", ""); }));
            registry.Begin(id); registry.Execute(Call(id));
            Assert.Throws<InvalidOperationException>(() => registry.Begin(id));
            Assert.AreEqual("duplicate_call", registry.Execute(Call(id)).Code); Assert.AreEqual(1, effects);
        }
        [Test] public void RedactionCoversCompleteAuthorizationAndQuotedSecrets()
        {
            foreach (string input in new[] { "Authorization: Basic MAO_FAKE_BASIC_CREDENTIAL", "Authorization=Bearer MAO_FAKE_BEARER_CREDENTIAL",
                "password=\"MAO_FAKE QUOTED PASSWORD\"", "secret='MAO_FAKE QUOTED SECRET'", "token = MAO_FAKE_TOKEN" })
                Assert.IsFalse(MaoControlRedaction.Clean(input).Contains("MAO_FAKE"), input);
        }
    }
}
