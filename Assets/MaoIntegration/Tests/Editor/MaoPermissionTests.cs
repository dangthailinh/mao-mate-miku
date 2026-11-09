using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using MaoIntegration;
using NUnit.Framework;

namespace MaoIntegration.Tests
{
    public sealed class MaoPermissionTests
    {
        private sealed class Audit : IMaoControlAudit { public bool TryWrite(MaoControlAuditEntry entry) => true; }
        private static MaoToolCall Call(string id = "one", string text = "first") => new MaoToolCall("task", id, "write_file", "filesystem", "write", new Dictionary<string, string> { ["content"] = text, ["path"] = "example.txt", ["overwrite"] = "false" });
        [Test] public void DefaultIsAskAndCannotWriteWithoutDecision() { var p = new MaoPermissionPolicy(); Assert.AreEqual(MaoPermissionMode.Ask, p.Mode); Assert.IsFalse(p.Authorize(Call(), MaoToolRisk.SafeWrite)); }
        [Test] public void ReadOnlyNeverRunsWritesOrShell() { var p = new MaoPermissionPolicy(MaoPermissionMode.ReadOnly); Assert.IsTrue(p.Authorize(Call(), MaoToolRisk.ReadOnly)); Assert.IsFalse(p.Authorize(Call(), MaoToolRisk.SafeWrite)); Assert.IsFalse(p.Authorize(Call(), MaoToolRisk.Critical)); Assert.IsFalse(p.Request(Call())); }
        [Test] public void AskGrantMatchesExactArgumentsAndIsSingleUse() { var p = new MaoPermissionPolicy(); Assert.IsTrue(p.Request(Call())); Assert.IsTrue(p.Decide("one", true)); Assert.IsFalse(p.Authorize(Call(text: "changed"), MaoToolRisk.SafeWrite)); Assert.IsFalse(p.Authorize(Call("other"), MaoToolRisk.SafeWrite)); Assert.IsTrue(p.Authorize(Call(), MaoToolRisk.SafeWrite)); Assert.IsFalse(p.Authorize(Call(), MaoToolRisk.SafeWrite)); }
        [Test] public void RejectionAndDowngradeRemoveGrants() { var p = new MaoPermissionPolicy(); p.Request(Call()); p.Decide("one", false); Assert.IsTrue(p.Rejected); Assert.IsFalse(p.Authorize(Call(), MaoToolRisk.SafeWrite)); p.SetMode(MaoPermissionMode.FullAccess); Assert.IsTrue(p.Authorize(Call(), MaoToolRisk.Critical)); p.SetMode(MaoPermissionMode.ReadOnly); Assert.IsFalse(p.Authorize(Call(), MaoToolRisk.Critical)); }
        [Test] public void InvalidModesAndRisksFailClosed() { var p = new MaoPermissionPolicy((MaoPermissionMode)88); Assert.AreEqual(MaoPermissionMode.ReadOnly, p.Mode); p.SetMode(MaoPermissionMode.FullAccess); Assert.IsFalse(p.Authorize(Call(), (MaoToolRisk)88)); }
        [Test] public void ApprovalDeadlineIsClosedAtAndAfterExpiry()
        {
            Assert.IsTrue(MaoPermissionPolicy.DeadlineOpen(101, 100));
            Assert.IsFalse(MaoPermissionPolicy.DeadlineOpen(100, 100));
            Assert.IsFalse(MaoPermissionPolicy.DeadlineOpen(99, 100));
            Assert.IsFalse(MaoPermissionPolicy.DeadlineOpen(0, 100));
        }
        [Test] public void ControlsAreTrustedExplicitPhrasesOnly() { Assert.AreEqual("full", MaoPermissionPolicy.Control("cấp full quyền cho miku")); Assert.IsNull(MaoPermissionPolicy.Control("giải thích full access là gì")); Assert.IsTrue(MaoControlCommandPolicy.IsReadOnly("cho phép lần này")); }
        [Test] public void ApprovalPreviewPreservesLinesRedactsAndMarksLongInput()
        {
            Assert.AreEqual("first\nsecond", MaoControlRedaction.ApprovalPreview("first\nsecond"));
            Assert.IsFalse(MaoControlRedaction.ApprovalPreview("password=hidden-value").Contains("hidden-value"));
            Assert.IsTrue(MaoControlRedaction.ApprovalPreview(new string('a', 32000)).Contains("Đã rút gọn"));
            Assert.Less(MaoControlRedaction.ApprovalPreview(new string('a', 32000)).Length, 12400);
        }
        [Test] public void FileExecutorCreatesReadsAndBacksUpBeforeOverwrite()
        {
            string root = Path.Combine(Path.GetTempPath(), "MaoPermissionTest-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
            try {
                var e = new MaoComputerToolExecutor(root, Path.Combine(root, "backups"));
                MaoToolCall write(string content, bool overwrite) => new MaoToolCall("task", "call", "write_file", "filesystem", "write", new Dictionary<string, string> { ["path"] = "sample.txt", ["content"] = content, ["overwrite"] = overwrite.ToString() });
                Assert.IsTrue(e.Execute(write("first", false), CancellationToken.None).Success);
                Assert.IsFalse(e.Execute(write("bad", false), CancellationToken.None).Success);
                Assert.IsTrue(e.Execute(write("second", true), CancellationToken.None).Success);
                Assert.AreEqual("second", File.ReadAllText(Path.Combine(root, "sample.txt")));
                Assert.AreEqual("first", File.ReadAllText(Directory.GetFiles(Path.Combine(root, "backups"), "*.bak")[0]));
                Assert.IsTrue(e.Execute(new MaoToolCall("task", "read", "read_file", "filesystem", "read", new Dictionary<string, string> { ["path"] = "sample.txt" }), CancellationToken.None).Reply.Contains("second"));
            } finally { Directory.Delete(root, true); }
        }
        [Test] public void CancelledExecutorDoesNotCreateFile()
        {
            string root = Path.Combine(Path.GetTempPath(), "MaoCancelledTest-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
            try { var token = new CancellationToken(true); Assert.Throws<OperationCanceledException>(() => new MaoComputerToolExecutor(root, Path.Combine(root, "backups")).Execute(Call(), token)); Assert.IsFalse(File.Exists(Path.Combine(root, "example.txt"))); }
            finally { Directory.Delete(root, true); }
        }
        [Test] public void NewTasksRouteButHowToStaysChat() { Assert.IsTrue(MaoHarnessPolicy.ShouldRoute("tạo file demo.txt và chạy powershell kiểm tra")); Assert.IsFalse(MaoHarnessPolicy.ShouldRoute("hướng dẫn chạy powershell")); }
        [Test] public void PowerShellUsesActualExitCodeAndUtf8Output()
        {
            string root = Path.GetTempPath(); var e = new MaoComputerToolExecutor(root, Path.Combine(root, "unused-backups"));
            MaoToolCall shell(string command) => new MaoToolCall("task", "shell", "run_powershell", "powershell", "test", new Dictionary<string, string> { ["command"] = command, ["timeoutSeconds"] = "5" });
            var ok = e.Execute(shell("Write-Output 'MIKU_PS_OK'"), CancellationToken.None); Assert.IsTrue(ok.Success); Assert.IsTrue(ok.Reply.Contains("MIKU_PS_OK"));
            Assert.IsFalse(e.Execute(shell("exit 7"), CancellationToken.None).Success);
        }
        [Test] public void PowerShellTimeoutKillsOwnedWorker()
        {
            var e = new MaoComputerToolExecutor(Path.GetTempPath(), Path.Combine(Path.GetTempPath(), "unused-backups"));
            var call = new MaoToolCall("task", "shell", "run_powershell", "powershell", "test", new Dictionary<string, string> { ["command"] = "Start-Sleep -Seconds 20", ["timeoutSeconds"] = "1" });
            Assert.AreEqual("timeout", e.Execute(call, CancellationToken.None).Code);
        }
        [Test] public void ApprovalWaitingCanResumeAndCancellationRemainsTerminal()
        {
            var tasks = new MaoTaskManager(new Audit()); string id = tasks.Create("write").Id;
            tasks.Transition(id, MaoTaskStatus.Planning); tasks.Transition(id, MaoTaskStatus.Running);
            Assert.IsTrue(tasks.Transition(id, MaoTaskStatus.WaitingPermission)); Assert.IsTrue(tasks.IsActive(id)); Assert.IsFalse(tasks.IsRunning(id));
            Assert.IsTrue(tasks.Transition(id, MaoTaskStatus.Running)); Assert.IsTrue(tasks.Cancel(id)); Assert.IsFalse(tasks.Transition(id, MaoTaskStatus.Running));
        }
        [Test] public void AsyncRegistryCannotBypassPermissionOrPublishAfterCancel()
        {
            var tasks = new MaoTaskManager(new Audit()); string id = tasks.Create("write").Id; tasks.Transition(id, MaoTaskStatus.Planning); tasks.Transition(id, MaoTaskStatus.Running);
            var permissions = new MaoPermissionPolicy(); var tools = new MaoToolRegistry(tasks, new Audit()) { PermissionCheck = permissions.Authorize };
            int executed = 0; tools.Register(new MaoDelegateTool("shell", MaoToolRisk.Critical, _ => { executed++; return new MaoToolResult(true, "completed", "done"); })); tools.Begin(id);
            var first = new MaoToolCall(id, "one", "shell", "powershell", "test"); Assert.IsFalse(tools.TryBeginCall(first, out _, out _)); Assert.AreEqual(0, executed);
            permissions.SetMode(MaoPermissionMode.FullAccess); var second = new MaoToolCall(id, "two", "shell", "powershell", "test"); Assert.IsTrue(tools.TryBeginCall(second, out _, out _));
            tasks.Cancel(id); Assert.IsFalse(tools.CompleteCall(second, new MaoToolResult(true, "completed", "done"), 1).Success); Assert.AreEqual(0, tools.Succeeded);
        }
        [Test] public void CancellationDoesNotBlockCallerAndPreventsDelayedFileWrite()
        {
            string root = Path.Combine(Path.GetTempPath(), "MaoCancelWorker-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
            try {
                var e = new MaoComputerToolExecutor(root, Path.Combine(root, "backup")); using (var cancellation = new CancellationTokenSource()) {
                    var call = new MaoToolCall("task", "shell", "run_powershell", "powershell", "test", new Dictionary<string, string> { ["command"] = "Start-Sleep -Seconds 15; Set-Content -LiteralPath marker.txt -Value UNEXPECTED", ["timeoutSeconds"] = "20" });
                    var worker = System.Threading.Tasks.Task.Run(() => e.Execute(call, cancellation.Token)); Thread.Sleep(1000);
                    var timer = System.Diagnostics.Stopwatch.StartNew(); cancellation.Cancel(); timer.Stop(); Assert.Less(timer.ElapsedMilliseconds, 500);
                    try { worker.Wait(8000); } catch (AggregateException) { }
                    Assert.IsTrue(worker.IsCompleted); Assert.IsFalse(File.Exists(Path.Combine(root, "marker.txt")));
                }
            } finally { Directory.Delete(root, true); }
        }
    }
}
