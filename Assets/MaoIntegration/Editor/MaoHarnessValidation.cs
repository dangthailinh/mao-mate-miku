#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEngine;

public static class MaoHarnessValidation
{
    public static void RunCoreTests()
    {
        var assembly = Assembly.Load("MaoIntegration.EditorTests");
        int passed = 0;
        int failed = 0;
        foreach (string name in new[] { "MaoHarnessPolicyTests", "MaoAppearanceLogicTests", "MaoHarnessNotesTests", "MaoHhtechProtocolTests" })
        {
            var type = assembly.GetType("MaoIntegration.Tests." + name, true);
            var instance = Activator.CreateInstance(type);
            foreach (var method in type.GetMethods(BindingFlags.Instance | BindingFlags.Public))
            {
                if (!method.GetCustomAttributes(false).Any(a => a.GetType().FullName == "NUnit.Framework.TestAttribute")) continue;
                try { method.Invoke(instance, null); passed++; }
                catch (TargetInvocationException ex)
                {
                    failed++;
                    Debug.LogError("Core test failed: " + name + "." + method.Name +
                        " error_type=" + (ex.InnerException ?? ex).GetType().Name);
                }
            }
        }
        string directory = Path.Combine(Path.GetDirectoryName(Application.dataPath), "Logs");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "HarnessCoreTests.json"), "{\"passed\":" + passed + ",\"failed\":" + failed + "}");
        Debug.Log("[Mao Harness Tests] passed=" + passed + " failed=" + failed);
        if (failed > 0 || passed < 36) throw new InvalidOperationException("Core checks failed or did not all run.");
    }
}
#endif
