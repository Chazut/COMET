using System;
using System.Collections.Generic;

namespace Comet.Core;

public sealed class CoverFeintConfig
{
    public bool Enabled { get; set; } = true;
    public bool BaitPeek { get; set; } = true;
    public bool MagazineCheck { get; set; } = true;
    public bool Jump { get; set; } = true;
    public double PeekChance { get; set; } = 35;
    public double MagazineChance { get; set; } = 30;
    public double MinDistance { get; set; } = 5;
    public double MaxDistance { get; set; } = 40;
    public double MagazineDistance { get; set; } = 20;
    public double SightLoss { get; set; } = 2;
    public double Memory { get; set; } = 30;
    public double MinStep { get; set; } = .9;
    public double MaxStep { get; set; } = 2;
    public double ExposureMin { get; set; } = .2;
    public double ExposureMax { get; set; } = .35;
    public int Peeks { get; set; } = 2;
    public double HoldMin { get; set; } = 4;
    public double HoldMax { get; set; } = 7;
    public double RetryDelay { get; set; } = 20;

    public void Validate()
    {
        CometConfig.Range(PeekChance, 0, 100, nameof(PeekChance));
        CometConfig.Range(MagazineChance, 0, 100, nameof(MagazineChance));
        CometConfig.Range(MinDistance, 3, 15, nameof(MinDistance));
        CometConfig.Range(MaxDistance, 10, 60, nameof(MaxDistance));
        CometConfig.Range(MagazineDistance, 5, 30, nameof(MagazineDistance));
        CometConfig.Range(SightLoss, 1, 5, nameof(SightLoss));
        CometConfig.Range(Memory, 3, 60, nameof(Memory));
        CometConfig.Range(MinStep, .5, 2, nameof(MinStep));
        CometConfig.Range(MaxStep, 1, 3, nameof(MaxStep));
        CometConfig.Range(ExposureMin, .1, .5, nameof(ExposureMin));
        CometConfig.Range(ExposureMax, .15, .8, nameof(ExposureMax));
        CometConfig.Range(Peeks, 1, 2, nameof(Peeks));
        CometConfig.Range(HoldMin, 1, 8, nameof(HoldMin));
        CometConfig.Range(HoldMax, 2, 10, nameof(HoldMax));
        CometConfig.Range(RetryDelay, 10, 90, nameof(RetryDelay));
        if (
            MaxDistance < MinDistance
            || MagazineDistance < MinDistance
            || MagazineDistance > MaxDistance
            || Memory < SightLoss
            || MaxStep < MinStep
            || ExposureMax < ExposureMin
            || HoldMax < HoldMin
        )
            throw new ArgumentException("Cover feint limits must be ordered.");
    }
}

public sealed class CoverFeintFrame
{
    public double Now,
        Age,
        SightAge,
        Distance,
        Fire = -1,
        Heard = -1,
        Speed,
        AmmoRatio;
    public string Target = "",
        Key = "",
        Block = "";
    public DoorPoint Position,
        Known;
    public bool MagazineAudible,
        Outdoor,
        Cover,
        Ready,
        Healthy,
        Bold,
        Visible,
        Coming,
        Mates,
        Grounded = true,
        RouteValid = true,
        AtGoal,
        Protected,
        Exposed;
}

public sealed class CoverFeintTrace
{
    public string State = "planning",
        Kind = "",
        FallbackReason = "",
        Reason = "",
        Target = "",
        Source = "personal-vision",
        Action = "Freeze",
        SoundEvent = "";
    public int Episode,
        CompletedPeeks,
        MoveRequests,
        JumpsAccepted,
        SoundEvents,
        PlaybackSubmissions,
        SafetyDeferrals;
    public double ValidFor,
        Elapsed,
        ContactAge,
        PeekChance,
        MagazineChance,
        PeekRoll,
        MagazineRoll,
        OrderRoll,
        PeekWeight,
        MagazineWeight,
        Exposure,
        Hold,
        SharedCooldownUntil,
        Pause,
        Speed,
        DistanceMoved;
    public double X,
        Y,
        Z,
        KnownX,
        KnownY,
        KnownZ,
        GoalX,
        GoalY,
        GoalZ;
    public bool Requested,
        Accepted,
        MovementObserved,
        ExposedObserved,
        ReturnedObserved,
        MagazineRequested,
        PeekPointSelected,
        RecentShotAtMe,
        SquadmateEngaging,
        HeardApproach,
        MagazineAccepted,
        AnimationObserved,
        WeaponReady,
        DrewFire,
        ApproachObserved;
    public DoorPoint? FirstPeekPoint, MagazineVisiblePathPoint;
    public bool? FirstPeekCrouched, UsedOppositePoint;
    public float? AppliedPeekPose;
    public float PullBackFrom, PullBackUntil;
    public bool PullBackRequested;
    public string[] Alternatives = Array.Empty<string>();
}

public sealed class CoverFeintPolicy
{
    private readonly RepositionCooldown cooldown;

    public CoverFeintPolicy() : this(new RepositionCooldown()) { }
    public CoverFeintPolicy(RepositionCooldown cooldown) => this.cooldown = cooldown;

    public readonly PreparationWait Preparation = new();

    private sealed class History
    {
        public double At,
            Fire,
            Heard;
        public DoorPoint Known;
    }

    private double nextRoll;
    private bool alternatePending;
    private Func<double> rollPeekPose = null!;
    private int episode;
    private double started,
        phaseAt,
        lastTime,
        progressAt,
        best,
        fireAt,
        heardAt;
    public CoverFeintTrace? Trace { get; private set; }
    public DoorPoint Home,
        Known,
        Goal;
    public DoorPoint LookPoint => Trace is { Kind: "magazine", MagazineVisiblePathPoint: { } point }
        ? point : Known;
    public bool Active => Trace != null && Trace.State != "ended";
    public bool Moving => Trace?.State is "out" or "back";
    public bool WantsRoute => Trace?.State is "out-pending" or "back-pending";
    public DoorPoint? OppositePeekProbe =>
        Trace is { State: "pause", UsedOppositePoint: null, FirstPeekPoint: { } first }
            ? Home + (Home - first) : null;
    public float? TargetPeekPose => Trace switch
    {
        { State: "out" or "out-pending", FirstPeekCrouched: bool low, CompletedPeeks: var count } =>
            (count == 0 ? low : !low) ? .55f : 1f,
        { State: "final-hold" } => 1f,
        _ => null,
    };
    public int HistoryCount => 0;

    public static string Gate(CoverFeintFrame f, CoverFeintConfig c)
    {
        if (!c.Enabled)
            return "feint-disabled";
        if (f.Block.Length > 0)
            return f.Block;
        if (
            !OutdoorRelocation.Valid(f.Now)
            || !OutdoorRelocation.Valid(f.Age)
            || !OutdoorRelocation.Valid(f.SightAge)
            || !OutdoorRelocation.Valid(f.Speed)
            || !OutdoorRelocation.Valid(f.Distance)
            || !DoorPolicy.Finite(f.Fire)
            || !DoorPolicy.Finite(f.Heard)
            || !DoorPolicy.Finite(f.AmmoRatio)
            || f.AmmoRatio < 0
            || f.AmmoRatio > 1
            || !f.Position.Finite
            || !f.Known.Finite
            || f.Target.Length == 0
            || f.Key.Length == 0
        )
            return "feint-invalid-context";
        if (
            !f.Cover
            || f.Visible
            || f.SightAge < c.SightLoss
            || f.SightAge > c.Memory
            || f.Distance < c.MinDistance
            || f.Distance > c.MaxDistance
        )
            return "feint-wait-for-covered-known-contact";
        return "";
    }

    public static bool MateEngaging(string target, string mateTarget, bool visible, double sightAge) =>
        mateTarget == target && (visible || sightAge < 5);

    private static bool CanPeek(CoverFeintFrame f, CoverFeintConfig c) => c.BaitPeek && f.Bold;

    private static bool CanCheckMagazine(CoverFeintFrame f, CoverFeintConfig c) =>
        c.MagazineCheck && f.AmmoRatio >= .5;

    public bool Begin(CoverFeintFrame f, CoverFeintConfig c, Func<double> random)
    {
        if (Active || f.Now < cooldown.Until || Gate(f, c).Length > 0)
            return false;
        if (f.Now < nextRoll)
            return false;
        nextRoll = f.Now + c.RetryDelay;
        bool peek = CanPeek(f, c), mag = CanCheckMagazine(f, c);
        if (!peek && !mag)
            return false;
        double order = random(),
            pr = random(),
            mr = random(),
            ex = random(),
            hold = random(),
            pause = random();
        foreach (double v in new[] { order, pr, mr, ex, hold, pause })
            if (!DoorPolicy.Finite(v) || v < 0 || v >= 1)
                return false;
        bool recentShotAtMe = f.Fire > 0 && f.Now - f.Fire < 10;
        double pw =
                (1 + (recentShotAtMe ? 1 : 0)) * (f.Healthy ? 1 : .4)
                + (f.Mates ? .7 : 0),
            mw =
                (1 + (f.Coming ? 1.5 : 0) + (f.Distance < 20 ? .5 : 0))
                * (f.Distance > 30 ? .4 : 1);
        bool peekFirst = order * (pw + mw) < pw;
        bool p = peek && pr * 100 < c.PeekChance,
            m = mag && mr * 100 < c.MagazineChance;
        string kind = peekFirst
            ? (
                p ? "peek"
                : m ? "magazine"
                : ""
            )
            : (
                m ? "magazine"
                : p ? "peek"
                : ""
            );
        alternatePending = kind == (peekFirst ? "peek" : "magazine");
        rollPeekPose = random;
        Preparation.Clear();
        Home = Goal = f.Position;
        Known = f.Known;
        started = phaseAt = lastTime = f.Now;
        fireAt = f.Fire;
        heardAt = f.Heard;
        Trace = new()
        {
            Episode = ++episode,
            Kind = kind,
            Target = f.Target,
            Requested = kind.Length > 0,
            PeekChance = c.PeekChance,
            MagazineChance = c.MagazineChance,
            PeekRoll = pr * 100,
            MagazineRoll = mr * 100,
            OrderRoll = order * 100,
            PeekWeight = pw,
            MagazineWeight = mw,
            RecentShotAtMe = recentShotAtMe,
            SquadmateEngaging = f.Mates,
            HeardApproach = f.Coming,
            Exposure = c.ExposureMin + ex * (c.ExposureMax - c.ExposureMin),
            Hold = c.HoldMin + hold * (c.HoldMax - c.HoldMin),
            Pause = .8 + pause * 1.4,
            ContactAge = f.Age,
            WeaponReady = f.Ready,
            KnownX = Known.X,
            KnownY = Known.Y,
            KnownZ = Known.Z,
            ValidFor = .3,
            Alternatives = new[]
            {
                peek
                    ? "Bait peek: eligible"
                    : "Bait peek: disabled or personality/health unsuitable",
                mag ? "Magazine check: eligible" : "Magazine check: unavailable or too far",
                "Stay in cover; no forced opponent reaction",
            },
        };
        if (kind.Length == 0)
        {
            End("feint-chose-to-stay");
            return false;
        }
        Set("planning", "feint-selected", f.Now);
        return true;
    }

    public void Accepted(bool accepted) => Accepted(accepted, lastTime);

    public void Accepted(bool accepted, double now) => Accepted(accepted, now, false);

    public void Accepted(bool accepted, double now, bool standAndShoot)
    {
        if (!Active)
            return;
        if (!accepted && standAndShoot && Trace!.PeekPointSelected)
        {
            EndOnSight("feint-stand-and-shoot", now);
            return;
        }
        Trace!.Accepted = accepted;
        if (!accepted)
            End("feint-native-priority", now);
    }

    public void DeferSafety(CoverFeintFrame f, CoverFeintConfig c, double lastSafety)
    {
        if (!Active)
            return;
        Trace!.SafetyDeferrals++;
        if (!DoorPolicy.Finite(f.Now) || f.Now < started || !DoorPolicy.Finite(lastSafety))
        {
            End("feint-invalid-safety-clock");
            return;
        }
        Trace.Reason = "feint-waiting-for-safety-budget";
    }

    public void Alternative(string why)
    {
        if (Trace == null)
            return;
        var a = new List<string>(Trace.Alternatives);
        if (a.Count == 8)
            a.RemoveAt(0);
        a.Add(why);
        Trace.Alternatives = a.ToArray();
    }

    public bool Plan(DoorPoint goal, double now)
    {
        if (!Active || Trace!.State != "planning" || !goal.Finite)
            return false;
        SetGoal(goal);
        Trace.PeekPointSelected = Trace.Kind == "peek";
        if (Trace.PeekPointSelected)
        {
            Trace.FirstPeekPoint = goal;
            Trace.FirstPeekCrouched = rollPeekPose() < .4;
            alternatePending = false;
        }
        Set(Trace.Kind == "peek" ? "out-pending" : "mag-pending", "feint-plan-ready", now);
        return true;
    }

    public void ResolveOppositePeek(DoorPoint? sampled, double now)
    {
        if (Trace is not { State: "pause", UsedOppositePoint: null, FirstPeekPoint: { } first } t)
            return;
        t.UsedOppositePoint = sampled.HasValue;
        SetGoal(sampled ?? first);
        AdvancePeekPause(now);
    }

    private void AdvancePeekPause(double now)
    {
        if (Trace is { State: "pause", UsedOppositePoint: bool opposite } t && now - phaseAt >= t.Pause)
            Set("out-pending", opposite ? "feint-opposite-side" : "feint-reuse-opening", now);
    }

    private void SetGoal(DoorPoint goal)
    {
        Goal = goal;
        Trace!.GoalX = goal.X;
        Trace.GoalY = goal.Y;
        Trace.GoalZ = goal.Z;
    }

    public void RequestMove(double now)
    {
        if (!WantsRoute)
            return;
        Trace!.MoveRequests++;
        progressAt = now;
        best = double.MaxValue;
        Set(Trace.State == "out-pending" ? "out" : "back", "feint-native-move-requested", now);
    }

    public void NoPeekPoint(CoverFeintFrame f, CoverFeintConfig c)
    {
        if (Trace is { Kind: "peek", State: "planning" })
            PreparationFailed("feint-no-peek-point", f, c);
    }

    public void NoFirearm(CoverFeintFrame f, CoverFeintConfig c)
    {
        if (Trace?.State == "mag-pending")
            PreparationFailed("feint-no-firearm", f, c);
    }

    private void PreparationFailed(string why, CoverFeintFrame f, CoverFeintConfig c)
    {
        var t = Trace!;
        End(why, f.Now);
        if (!alternatePending)
            return;
        alternatePending = false;
        bool tryMagazine = t.Kind == "peek";
        if (tryMagazine
            ? !CanCheckMagazine(f, c) || t.MagazineRoll >= c.MagazineChance
            : !CanPeek(f, c) || t.PeekRoll >= c.PeekChance)
            return;

        // Continue the original ordered choice. A refused native check has already started its cooldown.
        t.Kind = tryMagazine ? "magazine" : "peek";
        t.FallbackReason = why;
        Alternative(why + " -> " + t.Kind);
        Home = Goal = f.Position;
        Set("planning", "feint-trying-alternate", f.Now);
    }

    public void RequestMagazineCheck(DoorPoint? visiblePathPoint)
    {
        if (Trace is { State: "mag-pending", MagazineRequested: false } t)
        {
            t.MagazineVisiblePathPoint = visiblePathPoint;
            t.MagazineRequested = true;
        }
    }

    public void MagazineResult(bool accepted, bool operation, CoverFeintFrame f, CoverFeintConfig c)
    {
        if (Trace?.State != "mag-pending")
            return;
        Trace.MagazineRequested = true;
        Trace.MagazineAccepted = accepted;
        Trace.AnimationObserved = accepted && operation;
        if (!accepted)
        {
            PreparationFailed("feint-magazine-refused", f, c);
            return;
        }
        alternatePending = false;
        Set("mag-check", "feint-waiting-for-actual-animation", f.Now);
    }

    public void MagazineObserved(bool operation, bool ready, double now)
    {
        if (Trace?.State != "mag-check")
            return;
        Trace.AnimationObserved |= operation;
        Trace.WeaponReady = ready;
        if (now - phaseAt >= 1.8)
            Set("hold", "feint-hold-approach", now);
    }

    public void Sound(string name, bool playback)
    {
        if (!Active || Trace!.Kind != "magazine" || Trace.State != "mag-check")
            return;
        if (playback)
            Trace.PlaybackSubmissions++;
        else
        {
            Trace.SoundEvents++;
            Trace.SoundEvent = name;
        }
    }

    public void Jump(bool accepted)
    {
        if (Active && accepted)
            Trace!.JumpsAccepted++;
    }

    public bool Observe(CoverFeintFrame f, CoverFeintConfig c)
    {
        if (!Active)
            return false;
        var t = Trace!;
        lastTime = f.Now;
        t.Elapsed = f.Now - started;
        t.ContactAge = f.Age;
        t.X = f.Position.X;
        t.Y = f.Position.Y;
        t.Z = f.Position.Z;
        t.Speed = f.Speed;
        t.DistanceMoved = (f.Position - Home).Length;
        t.WeaponReady = f.Ready;
        if (
            f.Block.Length > 0
            || !c.Enabled
            || f.Target != t.Target
            || t.Elapsed >= 30
        )
        {
            End(f.Block.Length > 0 ? f.Block : "feint-session-ended");
            return false;
        }
        if (f.Visible)
        {
            EndOnSight("feint-enemy-spotted", f.Now);
            return false;
        }
        t.DrewFire |= f.Fire > fireAt + .01;
        t.ApproachObserved |= f.Coming && f.Heard > heardAt + .01;
        if (t.Kind == "magazine")
        {
            if (t.State == "hold" && f.Now - phaseAt >= t.Hold)
                End("feint-hold-finished");
            return Active;
        }
        if (Moving)
        {
            t.MovementObserved |= t.DistanceMoved > .15;
            if (t.State == "out" && (f.AtGoal || f.Now - phaseAt > .9))
            {
                t.ExposedObserved |= f.Exposed;
                Set("show", "feint-show-shoulder", f.Now);
            }
            else if (t.State == "back" && (f.AtGoal || f.Now - phaseAt > 1.2))
            {
                t.ReturnedObserved |= f.AtGoal;
                t.CompletedPeeks++;
                Set(
                    t.CompletedPeeks >= c.Peeks ? "final-hold" : "pause",
                    "feint-peek-complete",
                    f.Now
                );
            }
        }
        if (t.State == "show" && f.Now - phaseAt >= t.Exposure)
            Return(f.Now, "feint-short-exposure-complete");
        if (t.State == "pause")
            AdvancePeekPause(f.Now);
        if (t.State == "final-hold" && f.Now - phaseAt >= 2.5)
            End("feint-hold-finished");
        return Active;
    }

    private void Return(double now, string why)
    {
        SetGoal(Home);
        Set("back-pending", why, now);
    }

    private void Set(string state, string reason, double now)
    {
        Trace!.State = state;
        Trace.Reason = reason;
        phaseAt = now;
        Trace.ValidFor = .3;
    }

    public void End(string why) => End(why, lastTime);

    private void EndOnSight(string why, double now)
    {
        if (Trace is { Kind: "peek", PeekPointSelected: true } t)
        {
            t.PullBackFrom = (float)now + .9f;
            t.PullBackUntil = t.PullBackFrom + 1.6f;
        }
        End(why, now);
    }

    public bool ShouldPullBack(double now) =>
        Trace is { PullBackUntil: > 0 } t && now >= t.PullBackFrom && now < t.PullBackUntil;

    public void End(string why, double now)
    {
        if (Trace == null)
            return;
        if (Active && (Trace.PeekPointSelected || (Trace.Kind == "magazine" && Trace.MagazineRequested)))
            Trace.SharedCooldownUntil = cooldown.AfterSession(now);
        Trace.State = "ended";
        Trace.Reason = why;
        Trace.ValidFor = 0;
    }

    public void Clear(string why) => Clear(why, lastTime);

    public void Clear(string why, double now) => Clear(why, now, false);

    public void Clear(string why, double now, bool preservePullBack)
    {
        if (Active)
            End(why, now);
        if (!preservePullBack && Trace != null)
            Trace.PullBackUntil = Trace.PullBackFrom = 0;
        nextRoll = 0;
    }
}
