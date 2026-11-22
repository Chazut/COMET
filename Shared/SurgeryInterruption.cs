using System;
using System.Collections.Generic;

namespace Comet.Core;

public sealed class SurgeryInterruptionConfig
{
    public bool Enabled { get; set; } = true;
    public double Interval { get; set; } = .1;
    public double VisibleDistance { get; set; } = 40;
    public double KnownDistance { get; set; } = 20;
    public double KnownAge { get; set; } = 3;
    public double FootstepDistance { get; set; } = 15;
    public double FootstepAge { get; set; } = 2;
    public double RetryDelay { get; set; } = 8;

    public void Validate()
    {
        CometConfig.Range(Interval, .05, .5, nameof(Interval));
        CometConfig.Range(VisibleDistance, 5, 100, nameof(VisibleDistance));
        CometConfig.Range(KnownDistance, 5, 60, nameof(KnownDistance));
        CometConfig.Range(KnownAge, .5, 10, nameof(KnownAge));
        CometConfig.Range(FootstepDistance, 5, 40, nameof(FootstepDistance));
        CometConfig.Range(FootstepAge, .5, 10, nameof(FootstepAge));
        CometConfig.Range(RetryDelay, 3, 60, nameof(RetryDelay));
    }
}

public sealed class SurgeryEvidence
{
    public string Kind { get; set; } = "known";
    public string Contact { get; set; } = "";
    public double Age { get; set; }
    public double Distance { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }
}

public sealed class SurgeryFrame
{
    public double Now;
    public bool SourceAllowed = true;
    public string? SourceCheck;
    public bool Hit,
        Grenade,
        NativeCancelled,
        Hands,
        OperationPending,
        Completed,
        WeaponReady;
    public readonly List<SurgeryEvidence> Evidence = new();
}

public sealed class SurgeryInterruptionTrace
{
    public string Origin { get; set; } = "comet";
    public string State { get; set; } = "monitoring";
    public string Reason { get; set; } = "surgery-continue-safe";
    public string? SourceRefusal { get; set; }
    public string Item { get; set; } = "";
    public string Part { get; set; } = "";
    public bool Requested { get; set; }
    public bool NativeCancelled { get; set; }
    public bool HandsReleased { get; set; }
    public bool WeaponReady { get; set; }
    public bool Completed { get; set; }
    public bool Applied { get; set; }
    public double ValidFor { get; set; }
    public double RetryRemaining { get; set; }
    public SurgeryEvidence? Evidence { get; set; }
    public string[] Alternatives { get; set; } = Array.Empty<string>();
}

// Watches one real H01 operation. No healing, animation or resource mutation here.
public sealed class SurgeryInterruption
{
    public SurgeryInterruptionTrace? Trace { get; private set; }
    public double BeganAt { get; private set; }
    public double RetryAt { get; private set; }
    private double cancelAt;
    public bool Active => Trace != null && Trace.State != "ended" && Trace.State != "weapon-ready";
    public bool Cancelling => Active && Trace!.Requested;

    public void Begin(double now, string item, string part)
    {
        BeganAt = now;
        Trace = new() { Item = item, Part = part };
    }

    public bool Tick(SurgeryFrame f, SurgeryInterruptionConfig c)
    {
        if (!Active)
            return false;
        var t = Trace!;
        t.Completed |= f.Completed;
        t.NativeCancelled |= f.NativeCancelled;
        t.RetryRemaining = Math.Max(0, RetryAt - f.Now);
        if (t.Requested)
        {
            t.HandsReleased = !f.Hands && !f.OperationPending;
            t.WeaponReady = t.HandsReleased && f.WeaponReady;
            t.State =
                t.WeaponReady ? "weapon-ready"
                : t.HandsReleased ? "hands-released"
                : "cancel-requested";
            t.Applied = !t.WeaponReady;
            t.ValidFor = t.Applied ? 1 : 0;
            if (f.Now - cancelAt > 30 && !t.WeaponReady)
                End("surgery-native-return-timeout");
            return false;
        }
        // A limb repaired this frame wins over danger arriving in the same frame.
        if (f.Completed || !f.Hands && !f.OperationPending)
        {
            End(f.Completed ? "surgery-completed" : "surgery-operation-ended");
            return false;
        }
        t.Reason = "surgery-continue-safe";
        t.Evidence = null;
        t.Alternatives = new[]
        {
            "continue: no qualifying personal danger",
            "interrupt: thresholds not reached",
        };
        if (f.NativeCancelled)
            t.Reason = "surgery-native-cancel-observed";
        else if (!f.SourceAllowed)
        {
            t.Reason = "surgery-source-area-not-clear";
            t.SourceRefusal = f.SourceCheck;
        }
        t.Applied = f.Hands;
        t.ValidFor = t.Applied ? 1 : 0;
        if (t.Reason.StartsWith("surgery-continue", StringComparison.Ordinal))
            return false;
        t.Requested = true;
        t.State = "cancel-requested";
        t.Applied = true;
        t.ValidFor = 1;
        cancelAt = f.Now;
        RetryAt = 0;
        t.RetryRemaining = 0;
        t.Alternatives = new[]
        {
            "interrupt: personal danger or native cancellation",
            "continue: unsafe",
            "restart surgery: wait, then recheck H01 safety",
        };
        return !f.NativeCancelled;
    }

    public void End(string reason)
    {
        if (Trace == null)
            return;
        Trace.State = "ended";
        Trace.Reason = reason;
        Trace.Applied = false;
        Trace.ValidFor = 0;
    }
}
