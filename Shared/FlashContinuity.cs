using System;
namespace Comet.Core;
public sealed class FlashContinuityConfig { public bool Enabled { get; set; } = true; }
public static class FlashContinuityRules
{
    public static bool Finite(double x) => !double.IsNaN(x) && !double.IsInfinity(x);
    public static double End(double now, double duration, double previous)
        => Math.Max(now + Math.Max(0, duration), previous);
    public static double StrongEnd(double now, double duration, double fraction, double previous)
        => Math.Max(now + Math.Max(0, duration) * Math.Max(0, Math.Min(1, fraction)), previous);
}
public sealed class FlashContinuityTrace
{
    public string State = "active", Reason = "flash-active", Source = "native-flash", Target = "";
    public double RequestedDuration, Remaining, StrongRemaining, BlindFireRemaining, ValidFor = 1;
    public bool HasRemembered, GasActive;
    public double X, Y, Z;
    public string[] Alternatives = Array.Empty<string>();
}
