using System;

namespace Comet.Core;

public sealed class DoorRecoveryConfig
{
    public bool Enabled { get; set; } = true;
    public double SuspicionDelay { get; set; } = 1.2;
    public double BotAnimationGrace { get; set; } = 8;
    public double StableDuration { get; set; } = .5;
    public double SampleInterval { get; set; } = .25;
    public double UncertaintyLimit { get; set; } = 5;

    public void Validate()
    {
        CometConfig.Range(SuspicionDelay, .75, 5, nameof(SuspicionDelay));
        CometConfig.Range(BotAnimationGrace, 4, 15, nameof(BotAnimationGrace));
        CometConfig.Range(StableDuration, .25, 2, nameof(StableDuration));
        CometConfig.Range(SampleInterval, .1, .5, nameof(SampleInterval));
        CometConfig.Range(UncertaintyLimit, 3, 15, nameof(UncertaintyLimit));
        if (StableDuration < SampleInterval || UncertaintyLimit < SuspicionDelay + StableDuration)
            throw new ArgumentException(
                "Door recovery must allow observation before the uncertainty timeout."
            );
    }
}

public sealed class DoorRecoveryTrace
{
    public string State { get; set; } = "ended";
    public string Reason { get; set; } = "recovery-idle";
    public string Door { get; set; } = "";
    public string DoorState { get; set; } = "";
    public string Target { get; set; } = "";
    public string Repair { get; set; } = "";
    public string Network { get; set; } = "solo";
    public double Age { get; set; }
    public double Stable { get; set; }
    public double Angle { get; set; }
    public double TargetAngle { get; set; }
    public double ValidFor { get; set; }
    public bool Requested { get; set; }
    public bool Confirmed { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }
}

// zzap-style local watchdog: a frozen interpolation can be force-finished to its known target.
public sealed class DoorRecovery
{
    private double started,
        last,
        lastAngle,
        stableSince;
    private bool observed;
    public DoorRecoveryTrace Trace { get; private set; } = new();

    public void Begin(string door, string target, DoorPoint position, double now, string network)
    {
        started = last = stableSince = now;
        observed = false;
        Trace = new()
        {
            State = "watching",
            Reason = "recovery-native-request",
            Door = door,
            Target = target,
            Network = network,
            X = position.X,
            Y = position.Y,
            Z = position.Z,
        };
    }

    public void End(string reason)
    {
        Trace.State = "ended";
        Trace.Reason = reason;
        Trace.ValidFor = 0;
    }

    public void Requested(string repair)
    {
        Trace.Requested = true;
        Trace.Repair = repair;
        Trace.State = "verifying";
        Trace.Reason = "recovery-correction-requested";
    }

    public void Confirm(bool success)
    {
        Trace.Confirmed = success;
        Trace.State = success ? "confirmed" : "blocked";
        Trace.Reason = success ? "recovery-observed-consistent" : "recovery-correction-unconfirmed";
        Trace.ValidFor = 0;
    }

    public string Observe(
        double now,
        double angle,
        double targetAngle,
        string doorState,
        bool nativeProgress,
        bool busy,
        bool completed,
        bool coherent,
        bool networkSafe,
        DoorRecoveryConfig c
    )
    {
        var t = Trace;
        if (
            !c.Enabled
            || !DoorPolicy.Finite(now)
            || !DoorPolicy.Finite(angle)
            || !DoorPolicy.Finite(targetAngle)
            || now < last
        )
        {
            End("recovery-invalid-observation");
            return "";
        }
        if (t.State is "ended" or "confirmed" or "blocked")
            return "";
        if (
            !observed
            || now - last > c.SampleInterval * 2 + .05
            || Math.Abs(angle - lastAngle) > .1
        )
            stableSince = now;
        observed = true;
        last = now;
        lastAngle = angle;
        t.Age = now - started;
        t.Stable = now - stableSince;
        t.Angle = angle;
        t.TargetAngle = targetAngle;
        t.DoorState = doorState;
        t.ValidFor = c.SampleInterval * 2 + .1;
        if (coherent)
        {
            if (t.Requested)
                Confirm(true);
            else
                End("recovery-native-or-external-completed");
            return "";
        }
        if (t.Requested)
        {
            Confirm(false);
            return "";
        }
        if (doorState is not ("Open" or "Shut" or "Interacting"))
        {
            End("recovery-protected-door-state");
            return "";
        }
        string repair = doorState == "Interacting" ? "finalize-state" : "align-record";
        bool endpoint = Math.Abs(angle - targetAngle) <= .5;
        if (t.Age >= c.SuspicionDelay && t.Stable >= c.StableDuration && !busy && networkSafe)
        {
            t.State = "ready";
            t.Reason = "recovery-stalled-interaction";
            return endpoint ? repair : "finish-angle";
        }
        t.State = "waiting";
        t.Reason =
            busy ? "recovery-active-user-or-reservation"
            : !networkSafe ? "recovery-network-unverified"
            : !completed ? "recovery-completion-unproven"
            : !endpoint ? "recovery-endpoint-unreached"
            : "recovery-observing-stability";
        if (t.Age >= c.UncertaintyLimit + (busy ? c.BotAnimationGrace : 0))
        {
            t.State = "blocked";
            t.Reason = "recovery-reconsider-uncertain-route";
            t.ValidFor = 0;
        }
        return "";
    }
}
