using System;

namespace MaoIntegration
{
    // Fixed opt-in flags shared by the renderer tests and AI services. Ordinary launches
    // never change routing or persistence behavior through this verification guard.
    public static class MaoAppearanceVerification
    {
        public static bool Active { get; } = IsRequested(Environment.GetCommandLineArgs());

        public static bool IsRequested(string[] arguments) => arguments != null &&
            (Array.IndexOf(arguments, "--mao-smoke-cat-accessories") >= 0 ||
             Array.IndexOf(arguments, "--mao-smoke-cat-accessories-reload") >= 0);
    }
}
