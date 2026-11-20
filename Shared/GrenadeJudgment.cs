using System;
using System.Collections.Generic;
using System.Linq;

namespace Comet.Core;

public sealed class GrenadeJudgmentConfig
{
    public bool Enabled { get; set; } = true;
    public double MinRushDistance { get; set; } = 10;
    public double MaxInfoAge { get; set; } = 20;
    public double BaseChance { get; set; } = 50;
    public double CampSeconds { get; set; } = 6;
    public double CampBonus { get; set; } = 30;
    public double RoomBonus { get; set; } = 10;
    public double VulnerableBonus { get; set; } = 15;
    public double LastGrenadePenalty { get; set; } = 20;
    public double FadeStart { get; set; } = 4;
    public double NoArcRetry { get; set; } = 1.5;
    public double RetryMin { get; set; } = 3;
    public double RetryMax { get; set; } = 6;

    public void Validate(double contactLimit)
    {
        CometConfig.Range(MinRushDistance, 0, 40, nameof(MinRushDistance));
        CometConfig.Range(MaxInfoAge, 3, 120, nameof(MaxInfoAge));
        foreach (
            var v in new[] { BaseChance, CampBonus, RoomBonus, VulnerableBonus, LastGrenadePenalty }
        )
            CometConfig.Range(v, 0, 100, "Grenade chance/bonus");
        CometConfig.Range(CampSeconds, 2, 20, nameof(CampSeconds));
        CometConfig.Range(FadeStart, 0, contactLimit - .1, nameof(FadeStart));
        CometConfig.Range(NoArcRetry, .5, 5, nameof(NoArcRetry));
        CometConfig.Range(RetryMin, 1, 15, nameof(RetryMin));
        CometConfig.Range(RetryMax, RetryMin, 30, nameof(RetryMax));
    }
}

public sealed class GrenadeJudgmentFrame
{
    public string Target = "",
        Source = "",
        Block = "",
        Item = "";
    public int Instance,
        Count;
    public double Now,
        EvidenceAt,
        X,
        Y,
        Z;
    public bool Personal,
        Room,
        Vulnerable;
}

public sealed class GrenadeJudgmentTrace
{
    public string Target = "",
        Source = "",
        State = "blocked",
        Reason = "grenade-not-assessed",
        Item = "";
    public long Episode;
    public int Count,
        Samples;
    public double Age,
        X,
        Y,
        Z,
        CampSeconds,
        BaseChance,
        CampBonus,
        RoomBonus,
        VulnerableBonus,
        LastPenalty,
        Fade,
        Chance,
        RetryRemaining,
        ValidFor;
    public double? Roll;
    public bool Requested,
        Accepted,
        Released,
        NativeStarted;
    public string NativePhase = "",
        Hands = "";
    public string[] Alternatives =
    {
        "Keep fighting or use another COMET tactic",
        "Wait for a native trajectory",
        "Continue fighting",
    };
}

// Scalar memory only; changing combat entry points does not create another opportunity.
public sealed class GrenadeJudgmentPolicy
{
    private sealed class Contact
    {
        internal int Instance,
            Samples;
        internal double Stamp = -1,
            Since,
            LastAccess,
            Spent = -1,
            Next,
            RollUntil;
        internal DoorPoint Anchor;
        internal double? Roll;
    }

    private readonly Dictionary<string, Contact> contacts = new();
    private long sequence;
    private double clock = -1,
        pendingAt;
    private string pendingTarget = "",
        pendingItem = "";
    private double pendingStamp;
    private GrenadeJudgmentTrace? pendingTrace;
    public GrenadeJudgmentTrace? Trace { get; private set; }
    public int Count => contacts.Count;
    public bool Pending => pendingItem.Length > 0;

    private Contact Get(GrenadeJudgmentFrame f)
    {
        if (!contacts.TryGetValue(f.Target, out var e) || e.Instance != f.Instance)
        {
            contacts[f.Target] = e = new Contact { Instance = f.Instance };
        }
        e.LastAccess = f.Now;
        return e;
    }

    public void Observe(GrenadeJudgmentFrame f)
    {
        if (!Finite(f.Now) || f.Now < 0)
            return;
        if (f.Now < clock)
        {
            Clear("grenade-clock-reset");
        }
        clock = f.Now;
        if (
            f.Target.Length == 0
            || !Finite(f.EvidenceAt)
            || f.EvidenceAt < 0
            || f.EvidenceAt > f.Now + .01
            || !new DoorPoint(f.X, f.Y, f.Z).Finite
        )
            return;
        var e = Get(f);
        var point = new DoorPoint(f.X, f.Y, f.Z);
        // zzap measures how long the known position has stayed within 1.5 m.
        double d =
            (point.X - e.Anchor.X) * (point.X - e.Anchor.X)
            + (point.Y - e.Anchor.Y) * (point.Y - e.Anchor.Y)
            + (point.Z - e.Anchor.Z) * (point.Z - e.Anchor.Z);
        if (e.Stamp < 0 || d > 2.25)
        {
            e.Since = f.Now;
            e.Samples = 0;
            e.Anchor = point;
        }
        e.Stamp = f.Now;
        e.Samples++;
    }

    public bool Prepare(GrenadeJudgmentFrame f, GrenadeJudgmentConfig c, double limit)
    {
        Observe(f);
        var e = Get(f);
        Tick(f.Now, c); // Diagnostic timeout must not veto the next native throw.
        string why =
            !c.Enabled ? "grenade-judgment-disabled"
            : !Finite(f.Now)
            || f.Now < 0
            || !Finite(f.EvidenceAt)
            || !new DoorPoint(f.X, f.Y, f.Z).Finite
                ? "grenade-invalid-evidence"
            : f.EvidenceAt < 0 || f.Now < f.EvidenceAt || f.Now - f.EvidenceAt > limit
                ? "grenade-contact-expired"
            : f.Block.Length > 0 ? f.Block
            : f.Now < e.Next ? "grenade-retry-wait"
            : "";
        if (why.Length == 0)
            return true;
        // Preserve the pending request trace, including a synchronous projectile callback.
        if (Pending && Trace?.Requested == true)
        {
            Trace.ValidFor = 1;
            return false;
        }
        Set(f, e, "blocked", why);
        return false;
    }

    public bool Evaluate(
        GrenadeJudgmentFrame f,
        GrenadeJudgmentConfig c,
        double limit,
        bool arc,
        Func<double> random
    )
    {
        if (!Prepare(f, c, limit))
            return false;
        var e = Get(f);
        if (!arc)
        {
            e.Next = f.Now + c.NoArcRetry;
            Set(f, e, "blocked", "grenade-no-safe-trajectory");
            return false;
        }
        var t = Set(f, e, "assessed", "grenade-judgment");
        t.BaseChance = c.BaseChance;
        t.CampBonus = f.Now - e.Since > c.CampSeconds ? c.CampBonus : 0;
        t.RoomBonus = f.Room ? c.RoomBonus : 0;
        t.VulnerableBonus = f.Vulnerable ? c.VulnerableBonus : 0;
        t.LastPenalty = t.Age > 10 ? 20 : 0;
        t.Fade = 1;
        t.Chance =
            Math.Max(
                10,
                Math.Min(
                    95,
                    c.BaseChance + t.CampBonus + t.RoomBonus + t.VulnerableBonus - t.LastPenalty
                )
            ) * t.Fade;
        if (e.Roll == null || f.Now >= e.RollUntil)
        {
            double roll = random(),
                retry = random();
            if (!Finite(roll) || roll < 0 || roll >= 1 || !Finite(retry) || retry < 0 || retry >= 1)
            {
                t.State = "blocked";
                t.Reason = "grenade-invalid-roll";
                return false;
            }
            e.Roll = roll * 100;
            e.RollUntil = f.Now + c.RetryMin + (c.RetryMax - c.RetryMin) * retry;
        }
        t.Roll = e.Roll;
        bool allow = t.Roll < t.Chance;
        t.State = allow ? "eligible" : "declined";
        t.Reason = allow ? "grenade-worth-throwing" : "grenade-save-for-later";
        if (!allow)
        {
            e.Next = e.RollUntil;
            t.RetryRemaining = e.Next - f.Now;
        }
        return allow;
    }

    private GrenadeJudgmentTrace Set(GrenadeJudgmentFrame f, Contact e, string state, string why)
    {
        long episode =
            Trace != null && Trace.Target == f.Target && Trace.Roll == e.Roll && e.Roll != null
                ? Trace.Episode
                : ++sequence;
        return Trace = new GrenadeJudgmentTrace
        {
            Episode = episode,
            Target = f.Target,
            Source = f.Source,
            State = state,
            Reason = why,
            Item = f.Item,
            Count = f.Count,
            Age = Math.Max(0, f.Now - f.EvidenceAt),
            X = f.X,
            Y = f.Y,
            Z = f.Z,
            CampSeconds = Math.Max(0, e.Stamp - e.Since),
            Samples = e.Samples,
            RetryRemaining = Math.Max(0, e.Next - f.Now),
            ValidFor = 1,
        };
    }

    public void Request(GrenadeJudgmentFrame f)
    {
        if (Trace?.State != "eligible")
            return;
        pendingTarget = f.Target;
        pendingItem = f.Item;
        pendingStamp = f.EvidenceAt;
        pendingAt = f.Now;
        pendingTrace = Trace;
        Trace.Requested = true;
        Trace.State = "requested";
        Trace.Reason = "grenade-native-request";
    }

    public void Result(bool accepted, double now, GrenadeJudgmentConfig c)
    {
        if (pendingTrace == null)
            return;
        Trace = pendingTrace;
        if (Trace.Released)
            return; // native callback can fire inside DoThrow
        Trace.Accepted = accepted;
        Trace.State = accepted ? "accepted" : "refused";
        Trace.Reason = accepted ? "grenade-native-accepted" : "grenade-native-refused";
        if (!accepted)
        {
            pendingItem = "";
            if (contacts.TryGetValue(pendingTarget, out var e))
                e.Next = now + .5;
        }
    }

    // A true DoThrow return can be a no-op in a non-ready native phase.
    // This observes execution only; it does not introduce another tactical gate or force hands.
    public bool NativeProgress(bool throwing, string phase, string hands, double now)
    {
        if (!Pending || pendingTrace == null)
            return false;
        bool changed =
            pendingTrace.NativePhase != phase
            || pendingTrace.Hands != hands
            || (throwing && !pendingTrace.NativeStarted);
        pendingTrace.NativePhase = phase;
        pendingTrace.Hands = hands;
        if (throwing)
            pendingTrace.NativeStarted = true;
        if (throwing && pendingTrace.State == "unconfirmed")
        {
            pendingTrace.State = "accepted";
            pendingTrace.Reason = "grenade-native-started";
            changed = true;
        }
        if (
            !throwing
            && pendingTrace.NativeStarted
            && now - pendingAt > .5
            && pendingTrace.State != "interrupted"
        )
        {
            pendingTrace.State = "interrupted";
            pendingTrace.Reason = "grenade-native-ended-without-projectile";
            changed = true;
        }
        Trace = pendingTrace;
        return changed;
    }

    public void ObserveRequest(bool returned, bool throwing, double now, GrenadeJudgmentConfig c)
    {
        Result(returned, now, c);
        if (pendingTrace == null || pendingTrace.Released)
            return;
        pendingTrace.NativeStarted |= throwing;
        if (returned && !throwing)
        {
            pendingTrace.State = "unconfirmed";
            pendingTrace.Reason = "grenade-native-returned-without-start";
        }
        // Observation cannot rewrite SAIN's result or impose another retry cooldown.
        // Keep the request available for a delayed projectile callback.
    }

    public bool Tick(double now, GrenadeJudgmentConfig c)
    {
        if (!Pending || now - pendingAt <= 10)
            return false;
        pendingItem = ""; // Missing observer callback must not postpone the next native throw.
        Trace = pendingTrace!;
        Trace.State = "timeout";
        Trace.Reason = "grenade-release-not-observed";
        Trace.ValidFor = 1;
        return false;
    }

    public bool Released(string item, double now)
    {
        if (
            !Pending
            || item != pendingItem
            || !Finite(now)
            || now < pendingAt
            || now - pendingAt > 10
        )
            return false;
        if (contacts.TryGetValue(pendingTarget, out var e))
        {
            e.Spent = pendingStamp;
            e.Roll = null;
        }
        pendingItem = "";
        Trace = pendingTrace!;
        Trace.Released = Trace.Accepted = true;
        Trace.State = "released";
        Trace.Reason = "grenade-projectile-observed";
        Trace.ValidFor = 1;
        return true;
    }

    public void Clear(string reason)
    {
        contacts.Clear();
        pendingItem = "";
        pendingTrace = null;
        clock = -1;
        if (Trace != null)
        {
            Trace.State = "ended";
            Trace.Reason = reason;
            Trace.ValidFor = 0;
        }
    }

    private static bool Finite(double v) => !double.IsNaN(v) && !double.IsInfinity(v);
}
