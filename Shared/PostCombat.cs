using System;
using System.Collections.Generic;

namespace Comet.Core;

public sealed class PostCombatConfig
{
    public bool Enabled { get; set; } = true;
    public double Interval { get; set; } = .25;
    public double ReloadBelow { get; set; } = 70;
    public double WatchMin { get; set; } = 3;
    public double WatchMax { get; set; } = 6;
    public double CoverRadius { get; set; } = 25;
    public double RegroupDistance { get; set; } = 25;
    public double AdvanceMin { get; set; } = 4;
    public double AdvanceMax { get; set; } = 40;
    public double Duration { get; set; } = 14;
    public double MedicalDuration { get; set; } = 60;
    public Dictionary<string, double> AdvanceChances { get; set; } =
        new()
        {
            ["Wreckless"] = 85,
            ["GigaChad"] = 80,
            ["Chad"] = 65,
            ["Normal"] = 40,
            ["Timmy"] = 30,
            ["Rat"] = 10,
            ["SnappingTurtle"] = 10,
            ["Coward"] = 0,
        };

    public void Validate()
    {
        CometConfig.Range(Interval, .1, 1, nameof(Interval));
        CometConfig.Range(ReloadBelow, 0, 100, nameof(ReloadBelow));
        CometConfig.Range(WatchMin, 0, 15, nameof(WatchMin));
        CometConfig.Range(WatchMax, WatchMin, 20, nameof(WatchMax));
        CometConfig.Range(CoverRadius, 5, 40, nameof(CoverRadius));
        CometConfig.Range(RegroupDistance, 5, 60, nameof(RegroupDistance));
        CometConfig.Range(AdvanceMin, 0, 20, nameof(AdvanceMin));
        CometConfig.Range(AdvanceMax, AdvanceMin, 60, nameof(AdvanceMax));
        CometConfig.Range(Duration, WatchMax, 60, nameof(Duration));
        CometConfig.Range(MedicalDuration, Duration, 120, nameof(MedicalDuration));
        if (AdvanceChances == null || AdvanceChances.Count != 8)
            throw new ArgumentException("Eight post-combat personality chances required");
        foreach (
            var key in new[]
            {
                "Wreckless",
                "GigaChad",
                "Chad",
                "Normal",
                "Timmy",
                "Rat",
                "SnappingTurtle",
                "Coward",
            }
        )
        {
            if (!AdvanceChances.TryGetValue(key, out var v))
                throw new ArgumentException("Missing post-combat chance: " + key);
            CometConfig.Range(v, 0, 100, key);
        }
    }
}

public sealed class PostCombatFrame
{
    public double Now, AmmoPercent = 100;
    public bool Scope = true,
        Threat,
        NativePriority,
        MedicalBusy,
        NativeMedicalWork,
        MedicalUseful,
        MedicalUrgent,
        Reloading,
        HandsReady = true;
    public bool NeedsCover,
        Regroup,
        CanAdvance,
        BadlyInjuredOrDying,
        Heard,
        Coming;
    public string Personality = "Normal";
    public bool MedicalWorkLeft => MedicalBusy || MedicalUseful || NativeMedicalWork;
}

public sealed class PostCombatTrace
{
    public string State { get; set; } = "watch";
    public string Reason { get; set; } = "post-combat-start";
    public int Episode { get; set; }
    public bool Applied { get; set; }
    public double ValidFor { get; set; }
    public double Elapsed { get; set; }
    public double CombatLayerSeconds { get; set; }
    public double SecondsSinceCombat { get; set; }
    public double Remaining { get; set; }
    public double WatchRemaining { get; set; }
    public double? LastCoverWatchDuration { get; set; }
    public double AmmoPercent { get; set; }
    public bool Reloading { get; set; }
    public bool MedicalUseful { get; set; }
    public bool MedicalBusy { get; set; }
    public bool MedicalWorkLeft { get; set; }
    public double AdvanceChance { get; set; }
    public string AdvanceHealthStatus { get; set; } = "";
    public double AdvanceRoll { get; set; }
    public bool AdvanceSelected { get; set; }
    public double? InspectionDistance { get; set; }
    public float? LeaderDistance { get; set; }
    public string Contact { get; set; } = "";
    public double ContactAge { get; set; }
    public double? X { get; set; }
    public double? Y { get; set; }
    public double? Z { get; set; }
    public string Execution { get; set; } = "pending layer";
    public string LastLook { get; set; } = "";
    public string[] Alternatives { get; set; } = Array.Empty<string>();
}

// A single bounded episode. Failed optional work is not retried every update.
public sealed class PostCombat
{
    public const string WindowExpired = "post-combat-window-expired";
    public const string OrdinaryWindowExpired = "post-combat-time-budget-ended";
    private const double MaxSecondsSinceCombat = 60;
    public PostCombatTrace? Trace { get; private set; }
    public bool Active { get; private set; }
    public bool WillAdvance => Active && !advanceDone && Trace?.AdvanceSelected == true;
    public bool WantsReload => Active && !reloadDone && Trace is { Reloading: false, MedicalBusy: false };
    public double LastFight { get; private set; } = -1000;
    private bool armed,
        coverDone,
        reloadDone,
        advanceDone;
    private double start,
        watchUntil,
        combatLayerSince = -1,
        lastNow = -1;
    private int episode;

    public bool WantsRecovery(double now) =>
        (Active || armed && now >= LastFight) && now < LastFight + MaxSecondsSinceCombat;

    internal double OrdinaryDeadline(PostCombatConfig c) => LastFight + c.Duration;

    internal bool QuietWindowExpired(PostCombatFrame f, PostCombatConfig c) =>
        f.Now >= OrdinaryDeadline(c) && !f.Heard && !f.MedicalWorkLeft;

    public void ObserveFight(double now)
    {
        if (!MedicalChoice.Finite(now))
            return;
        if (combatLayerSince < 0 || now < LastFight)
            combatLayerSince = now;
        LastFight = now;
        armed = now - combatLayerSince >= 1.5;
    }

    public void ResetCombatWindow()
    {
        combatLayerSince = -1;
        armed = false;
    }

    public bool Tick(PostCombatFrame f, PostCombatConfig c, double watchRoll, double advanceRoll)
    {
        if (
            !c.Enabled
            || !f.Scope
            || !MedicalChoice.Finite(f.Now)
            || !MedicalChoice.Finite(f.AmmoPercent)
            || lastNow >= 0 && f.Now < lastNow
        )
        {
            Clear("post-combat-out-of-scope");
            return false;
        }
        lastNow = f.Now;
        if (!WantsRecovery(f.Now))
            return Active ? End(WindowExpired) : false;
        if (f.Threat || f.NativePriority)
        {
            if (Active)
            {
                End(f.Threat ? "post-combat-threat-returned" : "post-combat-native-priority");
                armed = false;
            }
            return false;
        }
        bool quietWindowExpired = QuietWindowExpired(f, c);
        if (!Active)
        {
            if (quietWindowExpired)
                return false;
            armed = false;
            Active = true;
            start = f.Now;
            double watchDuration = c.WatchMin + Clamp(watchRoll) * (c.WatchMax - c.WatchMin);
            watchUntil = f.Now + watchDuration;
            coverDone = reloadDone = advanceDone = false;
            double chance = c.AdvanceChances.TryGetValue(f.Personality, out var v) ? v : 40;
            if (f.BadlyInjuredOrDying)
                chance *= .2;
            Trace = new()
            {
                Episode = ++episode,
                CombatLayerSeconds = LastFight - combatLayerSince,
                AdvanceChance = chance,
                AdvanceRoll = Clamp(advanceRoll) * 100,
                AdvanceSelected = f.CanAdvance && Clamp(advanceRoll) * 100 < chance,
            };
        }
        var t = Trace!;
        t.Elapsed = Math.Max(0, f.Now - start);
        t.SecondsSinceCombat = f.Now - LastFight;
        t.MedicalBusy = f.MedicalBusy;
        t.MedicalWorkLeft = f.MedicalWorkLeft;
        t.MedicalUseful = f.MedicalUseful;
        t.AmmoPercent = f.AmmoPercent;
        t.Reloading = f.Reloading;
        if (quietWindowExpired)
            return End(OrdinaryWindowExpired);
        if (f.Heard)
        {
            t.AdvanceSelected = false;
            watchUntil = Math.Max(watchUntil, f.Now + 3);
        }
        double windowLeft = LastFight + MaxSecondsSinceCombat - f.Now;
        t.WatchRemaining = Math.Min(windowLeft, Math.Max(0, watchUntil - f.Now));
        double ordinaryLeft = OrdinaryDeadline(c) - f.Now;
        double budget = f.MedicalWorkLeft ? c.MedicalDuration - t.Elapsed : ordinaryLeft;
        if (f.Heard)
            budget = Math.Max(budget, t.WatchRemaining);
        t.Remaining = Math.Min(windowLeft, Math.Max(0, budget));
        if (f.MedicalBusy)
            return Stage("medical", "post-combat-native-treatment-active");
        if (!f.Reloading && f.AmmoPercent >= c.ReloadBelow)
            reloadDone = true;
        if (!coverDone && f.NeedsCover && t.Elapsed < c.Duration)
            return Stage("cover", "post-combat-seek-protection");
        coverDone = true;
        if (f.Reloading)
            return TryRegroup(f.Heard, f.Regroup) || Stage("reload", "post-combat-native-reload-active");
        if (WantsReload)
            return Stage("reload", "post-combat-top-up-magazine");
        if (f.MedicalUseful && !f.Coming)
            return Stage("medical", "post-combat-useful-treatment");
        if (TryRegroup(f.Heard, f.Regroup))
            return true;
        if (f.Now <= watchUntil)
            return Stage("watch", "post-combat-watch-last-threat");
        if (WillAdvance)
            return Stage("check", "post-combat-check-known-position");
        if (f.MedicalWorkLeft)
            return Stage("watch", "post-combat-waiting-for-treatment");
        return End("post-combat-work-complete");
    }

    internal static bool CanRegroup(bool heard, bool leaderOutOfRange) => !heard && leaderOutOfRange;

    internal bool TryRegroup(bool heard, bool leaderOutOfRange) =>
        CanRegroup(heard, leaderOutOfRange) && Stage("regroup", "post-combat-rejoin-leader");

    public void RenewCoverWatch(double now, double duration)
    {
        watchUntil = now + duration;
        Trace!.LastCoverWatchDuration = duration;
        Trace.WatchRemaining = Math.Min(duration, LastFight + MaxSecondsSinceCombat - now);
    }

    public void Complete(string stage, string outcome)
    {
        if (stage == "cover")
            coverDone = true;
        if (stage == "reload")
            reloadDone = true;
        if (stage == "check")
            advanceDone = true;
        if (Trace != null)
            Trace.Execution = outcome;
    }

    public void Applied(bool value)
    {
        if (Trace != null)
        {
            Trace.Applied = value && Active;
            Trace.ValidFor = Trace.Applied ? 1 : 0;
        }
    }

    private bool Stage(string state, string why)
    {
        var t = Trace!;
        if (t.State != state)
            t.Execution = "awaiting native step";
        t.State = state;
        t.Reason = why;
        t.Alternatives = new[]
        {
            "New threat: native combat immediately",
            "No loot or extraction objective",
            "Finish early when useful work is done",
        };
        return true;
    }

    public bool End(string why) => End(why, lastNow);

    public bool End(string why, double now)
    {
        if (Trace != null)
        {
            if (Active)
            {
                Trace.Elapsed = Math.Max(0, now - start);
                Trace.SecondsSinceCombat = Math.Max(0, now - LastFight);
            }
            Trace.State = "ended";
            Trace.Reason = why;
            Trace.Applied = false;
            Trace.ValidFor = 0;
            Trace.Remaining = Trace.WatchRemaining = 0;
        }
        Active = false;
        return false;
    }

    public void Clear(string why)
    {
        End(why);
        ResetCombatWindow();
        LastFight = -1000;
        lastNow = -1;
    }

    private static double Clamp(double v) =>
        MedicalChoice.Finite(v) ? Math.Max(0, Math.Min(1, v)) : 1;
}
