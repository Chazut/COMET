using System;

namespace Comet.Core;

public sealed class ExplosionAwarenessConfig
{
    public bool Enabled { get; set; } = true;
    public double NearDistance { get; set; } = 25;
    public double LookDuration { get; set; } = 3;
    public double LookInterval { get; set; } = 8;

    public void Validate()
    {
        CometConfig.Range(NearDistance, 5, 100, nameof(NearDistance));
        CometConfig.Range(LookDuration, .5, 8, nameof(LookDuration));
        CometConfig.Range(LookInterval, Math.Max(3, LookDuration), 60, nameof(LookInterval));
    }
}

public sealed class ExplosionTrace
{
    public string State { get; set; } = "heard";
    public string Reason { get; set; } = "explosion-heard";
    public string Signal { get; set; } = "";
    public string Kind { get; set; } = "blast";
    public string Evidence { get; set; } = "unknown";
    public string Target { get; set; } = "";
    public double EvidenceAge { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }
    public double? ContactX { get; set; }
    public double? ContactY { get; set; }
    public double? ContactZ { get; set; }
    public double Distance { get; set; }
    public bool Audible { get; set; }
    public bool Applied { get; set; }
    public double Remaining { get; set; }
    public double ValidFor { get; set; }
    public string[] Alternatives { get; set; } = Array.Empty<string>();

    public ExplosionTrace Copy() => (ExplosionTrace)MemberwiseClone();
}

// An explosion is not a fresh sighting of its owner. This policy cannot create enemy contacts.
public sealed class ExplosionAwareness
{
    private double until,
        nextLook,
        last = -1;
    public ExplosionTrace? Look { get; private set; }

    public ExplosionTrace Receive(
        ExplosionTrace t,
        double now,
        string priority,
        ExplosionAwarenessConfig c
    )
    {
        if (!Finite(now) || now < last)
        {
            Look = null;
            t.State = "ignored";
            t.Reason = "explosion-clock-reset";
            return t;
        }
        last = now;
        if (!t.Audible)
        {
            t.State = "ignored";
            t.Reason = "explosion-not-heard";
            return t;
        }
        if (t.Kind != "blast")
        {
            t.Reason = "explosion-special-effect-kept-separate";
            return t;
        }
        if (t.Evidence == "own" || t.Evidence == "known-ally")
        {
            t.Reason = "explosion-known-friendly";
            return t;
        }
        if (t.Distance > c.NearDistance)
        {
            t.Reason = "explosion-outside-attention-radius";
            return t;
        }
        if (priority.Length != 0)
        {
            t.Reason = priority;
            return t;
        }
        if (Look != null || now < nextLook)
        {
            t.Reason = "explosion-attention-cooldown";
            return t;
        }
        until = now + c.LookDuration;
        nextLook = now + c.LookInterval;
        t.State = "look";
        t.Reason = "explosion-nearby-attention";
        t.Remaining = c.LookDuration;
        Look = t.Copy();
        return t;
    }

    public ExplosionTrace? Tick(double now, string priority, bool applied)
    {
        if (Look == null)
            return null;
        if (!Finite(now) || now < last)
            return Stop("explosion-clock-reset");
        last = now;
        if (priority.Length != 0)
            return Stop(priority);
        if (now >= until)
            return Stop("explosion-attention-complete");
        Look.Remaining = until - now;
        if (applied)
            Look.Applied = true;
        Look.ValidFor = Look.Applied ? Math.Min(1, Look.Remaining) : 0;
        return Look.Copy();
    }

    public ExplosionTrace? Stop(string reason)
    {
        if (Look == null)
            return null;
        var t = Look.Copy();
        Look = null;
        t.State = "ended";
        t.Reason = reason;
        t.Applied = false;
        t.Remaining = t.ValidFor = 0;
        return t;
    }

    public static bool Finite(double x) => !double.IsNaN(x) && !double.IsInfinity(x);
}
