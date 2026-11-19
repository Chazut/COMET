using System;

namespace Comet.Core;

public sealed class DoorMotionConfig
{
    public bool Enabled { get; set; } = true;
    public bool PushAway { get; set; } = true;
    public double StuckDelay { get; set; } = 1.5;
    public double LeafDistance { get; set; } = 1.2;
    public double SwingDuration { get; set; } = .5;
    public double HumanClearance { get; set; } = 2.5;

    public void Validate()
    {
        CometConfig.Range(StuckDelay, .75, 5, nameof(StuckDelay));
        CometConfig.Range(LeafDistance, .5, 2, nameof(LeafDistance));
        CometConfig.Range(SwingDuration, .25, 2, nameof(SwingDuration));
        CometConfig.Range(HumanClearance, 1.5, 4, nameof(HumanClearance));
    }
}

public static class DoorMotion
{
    public static double Delta(double from, double to) => ((to - from) % 360 + 540) % 360 - 180;

    public static bool Reversed(double shut, double open, double current) =>
        DoorPolicy.Finite(shut)
        && DoorPolicy.Finite(open)
        && DoorPolicy.Finite(current)
        && Math.Abs(Delta(shut, open)) > 20
        && Math.Abs(Delta(shut, current)) > 20
        && Math.Sign(Delta(shut, open)) != Math.Sign(Delta(shut, current));

    public static double Away(double shut, double open) => shut - Delta(shut, open);

    public static double At(double from, double to, double elapsed, double duration) =>
        from + (to - from) * Math.Max(0, Math.Min(1, elapsed / duration));
}

public sealed class DoorMotionTrace
{
    public string State { get; set; } = "ended";
    public string Reason { get; set; } = "motion-idle";
    public string Door { get; set; } = "";
    public string DoorState { get; set; } = "";
    public string Action { get; set; } = "";
    public double Angle { get; set; }
    public double TargetAngle { get; set; }
    public double ValidFor { get; set; } = .35;
    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }
}
