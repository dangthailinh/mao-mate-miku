#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEngine;

public static class MaoAppearanceValidation
{
    public static void RunCoreTests()
    {
        var assembly = Assembly.Load("MaoIntegration.EditorTests");
        var type = assembly.GetType("MaoIntegration.Tests.MaoAppearanceLogicTests", true);
        var instance = Activator.CreateInstance(type);
        int passed = 0;
        foreach (var method in type.GetMethods(BindingFlags.Instance | BindingFlags.Public))
        {
            if (!method.GetCustomAttributes(false).Any(a => a.GetType().FullName == "NUnit.Framework.TestAttribute")) continue;
            try { method.Invoke(instance, null); passed++; }
            catch (TargetInvocationException ex) { throw new InvalidOperationException("Appearance test failed: " + method.Name, ex.InnerException); }
        }
        if (passed != 17) throw new InvalidOperationException("Expected seventeen appearance tests, ran " + passed);
        string directory = Path.Combine(Path.GetDirectoryName(Application.dataPath), "Logs");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "AppearanceCoreTests.json"), "{\"passed\":" + passed + ",\"failed\":0}");
        Debug.Log("[Mao Appearance Tests] passed=" + passed + " failed=0");
    }
}
#endif
