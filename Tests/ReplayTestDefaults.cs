using System.Runtime.CompilerServices;
using Comet.Core;

internal static class ReplayTestDefaults
{
    [ModuleInitializer]
    internal static void FullCapture() =>
        ReplayCapture.Configure(true, true, true, true);
}
