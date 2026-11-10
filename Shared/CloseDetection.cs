using System;
using System.Collections.Generic;

namespace Comet.Core;

public sealed class CloseDetectionConfig
{
    public bool Enabled { get; set; } = true;
    public double StartDistance { get; set; } = 6;
    public double FullDistance { get; set; } = 3;
    public double ReductionPercent { get; set; } = 100;
    public void Validate()
    {
        CometConfig.Range(StartDistance, 1, 12, nameof(StartDistance));
        CometConfig.Range(FullDistance, .5, StartDistance - .1, nameof(FullDistance));
        CometConfig.Range(ReductionPercent, 0, 100, nameof(ReductionPercent));
    }
}

public sealed class CloseDetectionFrame
{
    public bool MasterEnabled, SupportedAi, Active, Authority, Ghost, ValidTarget, Blind, InFov, ClearRecentRay;
    public double Distance, ThirdParty = 1, Unknown = 1, NativeModifier = 1;
}
public sealed class CloseDetectionTrace
{
    public string State = "native", Reason = "close-native", Target = "";
    public double Distance, ThirdParty, Unknown, AttentionBefore = 1, AttentionAfter = 1, Reduction, NativeModifier, FinalModifier;
    public double? CorrectionAge;
    public bool VisualAcquired, AttentionEvaluated;
}

// Adjusts only the positive part of the two attention penalties, never the rest of SAIN's
// gain-sight calculation. No visibility/contact mutation and no world access belong here.
public static class CloseDetection
{
    public static bool Finite(double x) => !double.IsNaN(x) && !double.IsInfinity(x);
    public static string Eligibility(CloseDetectionConfig c, CloseDetectionFrame f)
    {
        if (!f.MasterEnabled || !c.Enabled || c.ReductionPercent == 0) return "close-disabled";
        if (!f.SupportedAi || !f.Active || !f.Authority || f.Ghost) return "close-out-of-scope";
        if (!f.ValidTarget) return "close-invalid-target";
        if (!Finite(f.Distance) || f.Distance < 0 || !Finite(f.NativeModifier) || f.NativeModifier <= 0) return "close-invalid-input";
        if (f.Distance >= c.StartDistance) return "close-out-of-range";
        if (f.Blind) return "close-blinded";
        if (!f.InFov) return "close-outside-fov";
        if (!f.ClearRecentRay) return "close-no-recent-clear-ray";
        return "";
    }
    public static CloseDetectionTrace Evaluate(CloseDetectionConfig c, CloseDetectionFrame f)
    {
        var t = new CloseDetectionTrace { Distance = f.Distance, ThirdParty = f.ThirdParty, Unknown = f.Unknown,
            NativeModifier = f.NativeModifier, FinalModifier = f.NativeModifier };
        t.Reason = Eligibility(c, f);
        if (t.Reason.Length != 0) return t;
        if (!Finite(f.ThirdParty) || !Finite(f.Unknown) || f.ThirdParty <= 0 || f.Unknown <= 0)
        { t.Reason = "close-invalid-attention"; return t; }
        t.AttentionBefore = Math.Max(1, f.ThirdParty) * Math.Max(1, f.Unknown);
        if (!Finite(t.AttentionBefore)) { t.AttentionBefore = 1; t.Reason = "close-invalid-attention"; return t; }
        t.AttentionEvaluated = true;
        t.Reduction = Math.Min(1, Math.Max(0, (c.StartDistance - f.Distance) / (c.StartDistance - c.FullDistance))) * c.ReductionPercent / 100;
        t.AttentionAfter = 1 + (t.AttentionBefore - 1) * (1 - t.Reduction);
        t.FinalModifier = f.NativeModifier * (t.AttentionAfter / t.AttentionBefore);
        if (t.FinalModifier < f.NativeModifier)
        { t.State = "adjusted"; t.Reason = "close-attention-reduced"; }
        else t.Reason = "close-no-extra-attention-penalty";
        return t;
    }
    // Ray timestamps begin at zero in SAIN. A real successful body-part check is required.
    // A subsequent failed check invalidates it immediately, even within the native .25 s cache.
    public static bool FreshRay(double now, double success, double check, bool hitObstacle, bool hasBodyPart)
        => hasBodyPart && !hitObstacle && Finite(now) && Finite(success) && Finite(check)
            && success > 0 && check == success && now >= check && now - check <= .25;
}

// Bounded diagnostic correlation only. It cannot turn a contact visible or select a target.
public sealed class CloseDetectionEvidence
{
    private readonly Dictionary<string, (double time, CloseDetectionTrace trace)> entries = new();
    public int Count => entries.Count;
    public void Observe(string target, double now, CloseDetectionTrace trace)
    {
        if (trace.State != "adjusted") { entries.Remove(target); return; }
        if (!entries.ContainsKey(target) && entries.Count >= 8) entries.Clear();
        entries[target] = (now, trace);
    }
    public CloseDetectionTrace? Acquired(string target, double now)
    {
        if (!entries.TryGetValue(target, out var e)) return null;
        entries.Remove(target);
        if (now < e.time || now - e.time > 1) return null;
        var t = e.trace; t.State = "acquired"; t.Reason = "close-native-visual-acquired";
        t.VisualAcquired = true; t.CorrectionAge = now - e.time;
        return t;
    }
    public void Clear() => entries.Clear();
}
