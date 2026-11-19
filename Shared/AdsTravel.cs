using System;

namespace Comet.Core;

public sealed class AdsTravelConfig
{
    public bool Enabled { get; set; } = true;
    public bool Cover { get; set; } = true;
    public bool Retreat { get; set; } = true;
    public bool Flee { get; set; } = true;
    public bool Push { get; set; } = true;
    public double SinceShot { get; set; } = .3;
    public double Stability { get; set; } = .4;
    public double Refresh { get; set; } = .1;

    public void Validate()
    {
        CometConfig.Range(SinceShot, .1, 1, nameof(SinceShot));
        CometConfig.Range(Stability, .2, 1, nameof(Stability));
        CometConfig.Range(Refresh, .05, .2, nameof(Refresh));
    }
}

public sealed class AdsTravelFrame
{
    public double Now,
        SinceShot,
        Speed;
    public string Action = "",
        Target = "",
        Block = "";
    public bool NativeADS,
        ObservedADS,
        Moving,
        InCover,
        Ready,
        Firing,
        Preparing,
        Sprinting;
}

public sealed class AdsTravelTrace
{
    public string State = "native",
        Reason = "",
        Action = "",
        Target = "";
    public double ValidFor,
        SinceShot,
        Speed,
        RetryIn;
    public bool NativeADS,
        RequestedOff,
        ObservedADS,
        Moving,
        Firing,
        Preparing,
        Sprinting;
    public string[] Alternatives = Array.Empty<string>();
}

public sealed class AdsTravelPolicy
{
    public AdsTravelTrace? Trace { get; private set; }
    public bool Active { get; private set; }
    private double releasedAt = -1000,
        last = -1;
    private string context = "";
    private static readonly string[] alternatives =
    {
        "Keep ADS: shooting, preparing a shot, stationary or another action",
        "Release ADS: native cover travel, retreat, escape or non-firing push",
        "Return to SAIN: recompute its current ADS wish",
    };

    public static string Gate(AdsTravelFrame f, AdsTravelConfig c)
    {
        if (!c.Enabled)
            return "ads-disabled";
        if (f.Block.Length > 0)
            return f.Block;
        if (
            !DoorPolicy.Finite(f.Now)
            || f.Now < 0
            || !DoorPolicy.Finite(f.SinceShot)
            || f.SinceShot < 0
            || !DoorPolicy.Finite(f.Speed)
            || f.Speed < 0
        )
            return "ads-invalid-input";
        if (f.Firing || f.Preparing)
            return "ads-fire-priority";
        if (f.SinceShot < c.SinceShot)
            return "ads-recent-shot";
        if (!f.Ready)
            return "ads-weapon-busy";
        if (!f.Moving || f.Speed < .15)
            return "ads-stopped";
        if (f.InCover)
            return "ads-arrived-in-cover";
        if (f.Sprinting)
            return "ads-native-sprint";
        bool enabled = f.Action switch
        {
            "SeekCover" => c.Cover,
            "Retreat" => c.Retreat,
            "RunAway" => c.Flee,
            "RushEnemy" => c.Push,
            _ => false,
        };
        if (!enabled)
            return "ads-action-not-enabled";
        return "";
    }

    public bool Tick(AdsTravelFrame f, AdsTravelConfig c)
    {
        string reason = Gate(f, c),
            key = f.Action + ":" + f.Target;
        if (reason.Length == 0 && f.Now < last)
            reason = "ads-clock-reset";
        if (reason.Length == 0 && Active && key != context)
            reason = "ads-context-changed";
        if (reason.Length == 0 && !f.NativeADS)
            reason = "ads-native-already-off";
        bool allow = reason.Length == 0;
        if (!allow && Active)
        {
            Active = false;
            releasedAt = f.Now;
        }
        if (allow && !Active && f.Now - releasedAt < c.Stability)
        {
            allow = false;
            reason = "ads-stability-wait";
        }
        Active = allow;
        context = key;
        last = f.Now;
        if (allow)
            reason = "ads-release-for-" + f.Action;
        Trace = new()
        {
            State =
                allow ? "suppressing"
                : reason == "ads-stability-wait" ? "waiting"
                : "native",
            Reason = reason,
            Action = f.Action,
            Target = f.Target,
            NativeADS = f.NativeADS,
            ObservedADS = f.ObservedADS,
            Moving = f.Moving,
            Firing = f.Firing,
            Preparing = f.Preparing,
            Sprinting = f.Sprinting,
            SinceShot = Safe(f.SinceShot),
            Speed = Safe(f.Speed),
            RetryIn = Safe(c.Stability - (f.Now - releasedAt)),
            Alternatives = alternatives,
        };
        return allow;
    }

    public void Observe(bool observed, bool requested, double speed, AdsTravelConfig c)
    {
        if (Trace == null)
            return;
        Trace.ObservedADS = observed;
        Trace.Speed = Safe(speed);
        Trace.RequestedOff = Active && requested;
        Trace.ValidFor = Trace.RequestedOff ? Math.Min(.25, c.Refresh + .1) : 0;
    }

    public void End(string reason, double now, bool observed, bool native)
    {
        if (Active)
            releasedAt = now;
        Active = false;
        if (Trace == null)
            return;
        Trace.State = "released";
        Trace.Reason = reason;
        Trace.ValidFor = 0;
        Trace.RequestedOff = false;
        Trace.ObservedADS = observed;
        Trace.NativeADS = native;
        Trace.RetryIn = 0;
    }

    private static double Safe(double v) => DoorPolicy.Finite(v) ? Math.Max(0, v) : 0;
}
