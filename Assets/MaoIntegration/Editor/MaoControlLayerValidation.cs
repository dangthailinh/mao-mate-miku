#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using MaoIntegration;

public static class MaoControlLayerValidation
{
    public static void ValidateAndBuild() { RunCoreTests(); MaoMateBuild.BuildWindowsApp(); }
    public static void RunCoreTests()
    {
        Assembly assembly = Assembly.Load("MaoIntegration.EditorTests");
        int passed = 0, failed = 0;
        foreach (string name in new[] { "MaoPermissionTests", "MaoControlLayerTests", "MaoHarnessPolicyTests", "MaoHarnessNotesTests", "MaoAppearanceLogicTests", "MaoHhtechProtocolTests" })
        {
            Type type = assembly.GetType("MaoIntegration.Tests." + name, true);
            object instance = Activator.CreateInstance(type);
            foreach (MethodInfo method in type.GetMethods(BindingFlags.Instance | BindingFlags.Public))
            {
                if (!method.GetCustomAttributes(false).Any(a => a.GetType().FullName == "NUnit.Framework.TestAttribute")) continue;
                try { method.Invoke(instance, null); passed++; }
                catch (TargetInvocationException exception)
                {
                    failed++;
                    Debug.LogError("[Mao Control Tests] failed=" + name + "." + method.Name + " error_type=" + (exception.InnerException ?? exception).GetType().Name);
                }
            }
        }
        foreach (int mode in new[] { 0, 1, 2 })
        {
            bool cancel = mode == 1;
            GameObject service = new GameObject("Control Validation");
            string validationAudit = Path.Combine(Path.GetTempPath(), "MaoAdapterValidation-" + Guid.NewGuid().ToString("N"));
            try
            {
                var harness = service.AddComponent<MaoHarnessRuntime>();
                var layer = service.AddComponent<MaoControlLayerRuntime>();
                layer.InitializeForValidation(harness, validationAudit);
                layer.Router.Register(new ValidationAgent(mode));
                bool finished = false;
                string reply = null;
                IEnumerator routine = layer.RunRoutine("control-validation", string.Empty, _ => { }, text => { finished = true; reply = text; });
                if (!routine.MoveNext()) throw new InvalidOperationException("Test agent did not start.");
                string id = layer.Tasks.ActiveId;
                if (cancel) layer.CancelActive();
                int steps = 0;
                while (routine.MoveNext() && ++steps < 100) { }
                MaoTaskSnapshot task = layer.Tasks.Get(id);
                if (steps >= 100 || task == null || task.Status != (cancel ? MaoTaskStatus.Cancelled : MaoTaskStatus.Failed) ||
                    finished == cancel || layer.Tasks.ActiveId != null || mode == 2 && (reply == null || reply.Contains("FAKE_SUCCESS")))
                    throw new InvalidOperationException("Adapter termination contract failed.");
                passed++;
                Debug.Log("[Mao Control Tests] adapter=" + (mode == 2 ? "mismatch_reply_does_not_claim_success" : cancel ? "cancel_blocks_late_result" : "nested_exception_fails_task") + " passed=true");
            }
            catch (Exception exception) { failed++; Debug.LogError("[Mao Control Tests] adapter_failed error_type=" + exception.GetType().Name); }
            finally
            {
                UnityEngine.Object.DestroyImmediate(service);
                if (Directory.Exists(validationAudit)) Directory.Delete(validationAudit, true);
            }
        }
        string directory = Path.Combine(Path.GetDirectoryName(Application.dataPath), "Logs");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "ControlLayerCoreTests.json"), "{\"passed\":" + passed + ",\"failed\":" + failed + "}");
        Debug.Log("[Mao Control Tests] passed=" + passed + " failed=" + failed);
        if (failed > 0 || passed < 46) throw new InvalidOperationException("Control foundation checks failed or did not all run.");
    }

    private sealed class ValidationAgent : IMaoControlAgent
    {
        private readonly int mode;
        public ValidationAgent(int mode) { this.mode = mode; }
        public string Name => "validation";
        public IReadOnlyList<string> Capabilities => new[] { "test-only" };
        public bool CanHandle(string request) => request == "control-validation";
        public IEnumerator Execute(MaoTaskContext context, Action<string> progress, Action<MaoAgentResult> finished)
        { yield return Nested(finished); }
        private IEnumerator Nested(Action<MaoAgentResult> finished)
        {
            yield return null;
            if (mode == 0) throw new InvalidOperationException("Synthetic validation failure.");
            finished(new MaoAgentResult(mode != 2, mode == 2 ? "tool_count_mismatch" : "completed", mode == 2 ? "FAKE_SUCCESS" : "test"));
        }
        public void Cancel(string id) { }
        public string GetStatus() => "test-only";
    }
}
#endif
