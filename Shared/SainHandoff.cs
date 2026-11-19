using System;

namespace Comet.Core;

public sealed class HandoffCue
{
    public string Target = "";
    public bool Visible;
    public double At = -1,
        X,
        Y,
        Z;
}

public sealed class HandoffTrace
{
    public string State { get; set; } = "idle";
    public string Reason { get; set; } = "";
    public string Kind { get; set; } = "";
    public string FromFeature { get; set; } = "";
    public string FromAction { get; set; } = "";
    public string NativeCombat { get; set; } = "";
    public string NativeSelf { get; set; } = "";
    public string NativeSquad { get; set; } = "";
    public double At { get; set; }
    public double RetryRemaining { get; set; }
    public double GraceRemaining { get; set; }
    public bool NativeObserved { get; set; }
    public string PathCheck { get; set; } = "";
}

// Per-bot lifecycle only: never calls native movement, hands or animation resets.
public sealed class SainHandoff
{
    private readonly HandoffConfig config;
    private double until,
        graceStart = double.NaN,
        last = double.NaN;
    private HandoffCue cue = new HandoffCue();
    private string feature = "",
        action = "",
        reason = "",
        kind = "",
        combat = "",
        self = "",
        squad = "";
    private bool observed,
        graceUsed,
        retryAdmitted;
    private string pathCheck = "";
    private double pathCheckLoggedAt = double.NegativeInfinity;
    public bool Yielded { get; private set; }
    public HandoffTrace Current { get; private set; } = new HandoffTrace();
    public string LastAction => action;
    public string LastFeature => feature;

    public SainHandoff(HandoffConfig config)
    {
        config.Validate();
        this.config = config;
    }

    private static bool Finite(double n) => !double.IsNaN(n) && !double.IsInfinity(n);

    private bool Clock(double now)
    {
        bool valid = Finite(now) && now >= 0 && (!Finite(last) || now >= last);
        if (!valid)
        {
            Clear();
            return false;
        }
        last = now;
        return true;
    }

    private static HandoffCue Copy(HandoffCue c) =>
        new HandoffCue
        {
            Target = c.Target,
            Visible = c.Visible,
            At = c.At,
            X = c.X,
            Y = c.Y,
            Z = c.Z,
        };

    private static bool Valid(HandoffCue c) =>
        c.Target.Length > 0
        && Finite(c.At)
        && c.At >= 0
        && Finite(c.X)
        && Finite(c.Y)
        && Finite(c.Z);

    private HandoffTrace Trace(double now, string state) =>
        new HandoffTrace
        {
            State = state,
            Reason = reason,
            Kind = kind,
            At = now,
            FromFeature = feature,
            FromAction = action,
            NativeCombat = combat,
            NativeSelf = self,
            NativeSquad = squad,
            NativeObserved = observed,
            RetryRemaining = Math.Max(0, until - now),
            GraceRemaining = Finite(graceStart)
                ? Math.Max(0, config.Grace - (now - graceStart))
                : 0,
            PathCheck = pathCheck,
        };

    public void Clear()
    {
        Yielded = observed = graceUsed = retryAdmitted = false;
        until = 0;
        graceStart = last = double.NaN;
        cue = new HandoffCue();
        feature = action = reason = kind = combat = self = squad = "";
        Current = new HandoffTrace();
        pathCheck = "";
        pathCheckLoggedAt = double.NegativeInfinity;
    }

    public void Accepted(double now, string fromFeature, string fromAction, HandoffCue contact)
    {
        if (!Clock(now))
            return;
        if (feature != fromFeature || action != fromAction || cue.Target != contact.Target)
        {
            graceStart = double.NaN;
            graceUsed = false;
        }
        Yielded = false;
        observed = retryAdmitted = false;
        until = 0;
        feature = fromFeature;
        action = fromAction;
        if (!Finite(graceStart))
            cue = Copy(contact); // A grace window keeps its original contact anchor.
        reason = kind = combat = self = squad = "";
        // Repeated native acceptance during a grace window must not restart that window.
        Current = Trace(now, Finite(graceStart) ? "grace" : "comet");
    }

    public void Healthy(double now)
    {
        if (!Clock(now))
            return;
        graceStart = double.NaN;
        graceUsed = false;
        pathCheck = "";
        Current.PathCheck = "";
    }

    public bool ReportPathCheck(double now)
    {
        if (pathCheck.Length == 0 || !Finite(now) || now - pathCheckLoggedAt < 1)
            return false;
        // One aggregate reason per second at most; no per-frame refusal spam.
        pathCheckLoggedAt = now;
        return true;
    }

    public bool Begin(double now, string why, string category, HandoffCue contact)
    {
        if (!Clock(now))
            return false;
        bool fault = category == "error";
        if (Yielded && !retryAdmitted && (!fault || kind == "error"))
            return false; // Repeated callbacks never extend a cooldown.
        if (Yielded && retryAdmitted && reason == why && kind == category)
        {
            // A real retry failed again: bound future work, without logging the same handoff every second.
            retryAdmitted = false;
            until = fault ? now + config.ErrorRetry : now;
            cue = Copy(contact);
            Current = Trace(now, observed ? "native-observed" : "native-pending");
            return false;
        }
        retryAdmitted = false;
        Yielded = true;
        observed = false;
        reason = why;
        kind = category;
        cue = Copy(contact);
        until = fault ? now + config.ErrorRetry : now;
        graceStart = double.NaN;
        combat = self = squad = "";
        Current = Trace(now, "native-pending");
        return true;
    }

    public bool Blocked(double now, HandoffCue contact)
    {
        if (!Clock(now))
            return true;
        if (!Yielded)
            return false;
        bool blocked = kind == "error" && now < until;
        if (!blocked)
            retryAdmitted = true;
        Current = Trace(now, observed ? "native-observed" : "native-pending");
        return blocked;
    }

    public bool ObserveNative(
        double now,
        string nativeCombat,
        string nativeSelf,
        string nativeSquad
    )
    {
        if (!Clock(now) || !Yielded)
            return false;
        if (observed)
            return false; // One actual next decision, not a log of every native decision tick.
        combat = nativeCombat;
        self = nativeSelf;
        squad = nativeSquad;
        observed = true;
        Current = Trace(now, "native-observed");
        return true;
    }
}
