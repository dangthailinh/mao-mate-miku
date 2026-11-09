using System.Collections.Generic;

namespace MaoIntegration
{
    public enum MaoTextProviderBackend
    {
        None,
        Hhtech,
        Qwen,
        Gemini
    }

    public static class MaoTextProviderPolicy
    {
        public static IReadOnlyList<MaoTextProviderBackend> GetAttemptOrder(
            bool hhtechConfigured, bool qwenConfigured, bool geminiConfigured, bool hasImage)
        {
            var order = new List<MaoTextProviderBackend>(3);
            if (hasImage && geminiConfigured) order.Add(MaoTextProviderBackend.Gemini);
            if (hhtechConfigured) order.Add(MaoTextProviderBackend.Hhtech);
            foreach (MaoTextBackend fallback in MaoTextBackendPolicy.GetAttemptOrder(qwenConfigured, geminiConfigured, hasImage))
            {
                MaoTextProviderBackend backend = fallback == MaoTextBackend.Qwen
                    ? MaoTextProviderBackend.Qwen : MaoTextProviderBackend.Gemini;
                if (!order.Contains(backend)) order.Add(backend);
            }
            return order;
        }
    }
}
