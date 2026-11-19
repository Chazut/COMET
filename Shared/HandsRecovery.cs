using System;

namespace Comet.Core;

public sealed class HandsRecoveryConfig
{
    public bool Enabled { get; set; } = true;
    public double ReloadStaleSeconds { get; set; } = 6;
    public double GrenadeStaleSeconds { get; set; } = 2;
    public double CheckSeconds { get; set; } = .5;
    public double RetrySeconds { get; set; } = 2;
    public int MaxAttempts { get; set; } = 2;

    public void Validate()
    {
        CometConfig.Range(ReloadStaleSeconds, 2, 20, nameof(ReloadStaleSeconds));
        CometConfig.Range(GrenadeStaleSeconds, 1, 10, nameof(GrenadeStaleSeconds));
        CometConfig.Range(CheckSeconds, .1, 2, nameof(CheckSeconds));
        CometConfig.Range(RetrySeconds, .5, 10, nameof(RetrySeconds));
        CometConfig.Range(MaxAttempts, 1, 3, nameof(MaxAttempts));
    }
}

public sealed class HandsRecoveryFrame
{
    public double Now;
    public string Kind = "",
        Held = "",
        TargetWeapon = "",
        TargetSlot = "",
        Block = "",
        Operation = "",
        Action = "",
        Contact = "";
    public int Generation;
    public bool ReloadFlag,
        NativeReload,
        GunReady,
        ReloadIdle,
        ReloadStranded,
        SelectorChanging,
        SwitchCompleted;
    public string NativePhase = "";
}

public sealed class HandsRecoveryTrace
{
    public string State = "observing",
        Reason = "hands-incoherent",
        Kind = "",
        Held = "",
        TargetWeapon = "",
        TargetSlot = "",
        Operation = "",
        Action = "",
        Contact = "";
    public int Attempts,
        Limit;
    public double ConfirmedSeconds,
        RequiredSeconds,
        RetryRemaining,
        RequestAge,
        ValidFor;
    public bool ReloadFlag,
        NativeReload,
        Accepted,
        InHands,
        Ready,
        ReloadIdle,
        ReloadStranded,
        SelectorChanging,
        SwitchCompleted;
    public string NativePhase = "";
    public string[] Alternatives =
    {
        "Wait while the actual native reload is running",
        "Clear only the stale Reloading flag after six seconds, as in zzap",
    };
}

// zzap EnemyDecisionClass.WatchStuckReload. Diagnostics do not block native
// weapon changes or attempt additional inventory/animation repairs.
public sealed class HandsRecoveryPolicy
{
    public const double RequestTimeout = 5; // legacy wire value, no behavioural timeout
    public HandsRecoveryTrace? Trace { get; private set; }
    public bool Pending => false;
    public bool BlocksSwitch => false;
    private double since = -1;

    public string Observe(HandsRecoveryFrame f, HandsRecoveryConfig c)
    {
        if (!c.Enabled || double.IsNaN(f.Now) || double.IsInfinity(f.Now))
        {
            End("hands-disabled-or-clock-invalid");
            return "";
        }
        if (!f.ReloadFlag)
        {
            if (Trace != null)
            {
                Trace.ReloadFlag = false;
                Trace.NativeReload = f.NativeReload;
                Trace.State = "recovered";
                Trace.Reason = "hands-native-reload-recovered";
                Trace.ValidFor = 0;
            }
            since = -1;
            return "";
        }
        if (since < 0 || f.Now < since)
        {
            since = f.Now;
            Trace = new HandsRecoveryTrace
            {
                Kind = "reload",
                Reason = "hands-source-reload-watch",
                Held = f.Held,
            };
        }
        var t = Trace!;
        t.RequiredSeconds = c.ReloadStaleSeconds;
        t.ConfirmedSeconds = f.Now - since;
        t.ReloadFlag = f.ReloadFlag;
        t.NativeReload = f.NativeReload;
        t.Operation = f.Operation;
        t.NativePhase = f.NativePhase;
        t.State = "observing";
        t.ValidFor = 1.5;
        if (t.ConfirmedSeconds < c.ReloadStaleSeconds || f.NativeReload)
            return "";
        return "clear-reload";
    }

    public void Request(double now)
    {
        if (Trace != null)
        {
            Trace.Attempts++;
            Trace.State = "requested";
        }
    }

    public void Result(bool accepted, double now, HandsRecoveryConfig c)
    {
        if (Trace != null)
        {
            Trace.Accepted = accepted;
            if (accepted)
                Trace.ReloadFlag = false;
            Trace.State = accepted ? "recovered" : "observing";
            Trace.Reason = accepted ? "hands-reload-flag-cleared" : "hands-native-refused";
            Trace.ValidFor = accepted ? 0 : 1.5;
        }
        if (accepted)
            since = -1;
    }

    public void End(string reason)
    {
        since = -1;
        if (Trace != null)
        {
            Trace.State = "ended";
            Trace.Reason = reason;
            Trace.ValidFor = 0;
        }
    }
}
