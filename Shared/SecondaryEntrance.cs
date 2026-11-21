using System;
using System.Collections.Generic;

namespace Comet.Core;

public sealed class SecondaryEntranceConfig
{
    public bool Enabled { get; set; } = true;
    public bool AllowEntry { get; set; } = true;
    public bool Siege { get; set; } = true;
    public double WatchMin { get; set; } = 10;
    public double WatchMax { get; set; } = 15;
    public double Extension { get; set; } = 15;
    public double StandOff { get; set; } = 2.5;
    public double MaxPath { get; set; } = 35;
    public double ContactAge { get; set; } = 15;
    public double MinimumAmmo { get; set; } = 50;
    public double GigaChad { get; set; } = 100;
    public double Chad { get; set; } = 100;
    public double Wreckless { get; set; } = 100;
    public double Normal { get; set; } = 60;
    public double Rat { get; set; } = 0;
    public double SnappingTurtle { get; set; } = 0;
    public double Coward { get; set; } = 0;
    public double Timmy { get; set; } = 0;
    public double Retry { get; set; } = 30;

    public void Validate()
    {
        CometConfig.Range(WatchMin, 1, 30, nameof(WatchMin));
        CometConfig.Range(WatchMax, WatchMin, 30, nameof(WatchMax));
        CometConfig.Range(Extension, 0, 30, nameof(Extension));
        CometConfig.Range(StandOff, 1, 4, nameof(StandOff));
        CometConfig.Range(MaxPath, 5, 60, nameof(MaxPath));
        CometConfig.Range(ContactAge, 1, 20, nameof(ContactAge));
        CometConfig.Range(MinimumAmmo, 1, 100, nameof(MinimumAmmo));
        CometConfig.Range(Retry, 5, 120, nameof(Retry));
        foreach (
            var x in new[] { GigaChad, Chad, Wreckless, Normal, Rat, SnappingTurtle, Coward, Timmy }
        )
            CometConfig.Range(x, 0, 100, "Entry chance");
    }

    public double Chance(string personality) =>
        personality switch
        {
            "GigaChad" => GigaChad,
            "Chad" => Chad,
            "Wreckless" => Wreckless,
            "Normal" => Normal,
            "Rat" => Rat,
            "SnappingTurtle" => SnappingTurtle,
            "Coward" => Coward,
            "Timmy" => Timmy,
            _ => 0,
        };
}

public sealed class SecondaryEntranceTrace
{
    public string State { get; set; } = "planning";
    public string Passage { get; set; } = "";
    public string Primary { get; set; } = "";
    public string SupportStatus { get; set; } = "not checked";
    public string EntryReason { get; set; } = "not decided";
    public double WatchDuration { get; set; }
    public double WatchRemaining { get; set; }
    public bool Extended { get; set; }
    public bool Rolled { get; set; }
    public double EntryRoll { get; set; }
    public double EntryChance { get; set; }
    public double Ammo { get; set; }
    public bool Crossed { get; set; }
    public bool OpenRequested { get; set; }
    public bool OpenObserved { get; set; }
    public double PassageX { get; set; }
    public double PassageY { get; set; }
    public double PassageZ { get; set; }
}

// Decision timing only. Native adapters must validate geometry, partner and actual movement.
public sealed class SecondaryEntrance
{
    private sealed class Attempt
    {
        public double At,
            Evidence;
    }

    private readonly Dictionary<string, Attempt> attempts = new();
    public SecondaryEntranceTrace? Trace { get; private set; }
    public double WatchAt { get; private set; }
    private string target = "";
    private double watchUntil,
        evidence,
        last = -1;

    public bool CanTry(string id, double now, double info, SecondaryEntranceConfig c) =>
        c.Enabled
        && MedicalChoice.Finite(now)
        && MedicalChoice.Finite(info)
        && info <= now
        && now >= 0
        && !string.IsNullOrEmpty(id)
        && (!attempts.TryGetValue(id, out var a) || now >= a.At + c.Retry);

    // Older callers supplied a random draw for the initial watch.
    public bool Begin(string id, double now, double info, double watchRoll, SecondaryEntranceConfig c) =>
        Begin(id, now, info, c);

    public bool Begin(string id, double now, double info, SecondaryEntranceConfig c)
    {
        if (!CanTry(id, now, info, c))
            return false;
        target = id;
        evidence = info;
        last = now;
        attempts[id] = new() { At = now, Evidence = info };
        Trace = new() { WatchDuration = c.WatchMax };
        WatchAt = -1;
        return true;
    }

    private static double Clamp(double x) =>
        MedicalChoice.Finite(x) ? Math.Max(0, Math.Min(1, x)) : 1;

    public void Arrived(double now)
    {
        if (Trace == null || WatchAt >= 0)
            return;
        WatchAt = last = now;
        watchUntil = now + Trace.WatchDuration;
        Trace.State = "watching";
        Trace.WatchRemaining = Trace.WatchDuration;
    }

    internal bool WatchElapsed(double now) => WatchAt >= 0 && now > watchUntil;

    // ready / wait / end. One entry roll even if the partner is temporarily occupied.
    public string Decide(
        double now,
        bool fresh,
        string partner,
        double ammo,
        string personality,
        double roll,
        SecondaryEntranceConfig c
    )
    {
        var t = Trace;
        if (t == null || WatchAt < 0)
            return "wait";
        if (!MedicalChoice.Finite(now) || now < last || !c.Enabled)
        {
            t.EntryReason = "clock reset or feature disabled";
            return "end";
        }
        last = now;
        t.SupportStatus = partner;
        t.Ammo = MedicalChoice.Finite(ammo) ? Math.Max(0, Math.Min(100, ammo)) : 0;
        t.WatchRemaining = Math.Max(0, watchUntil - now);
        if (!WatchElapsed(now))
            return "wait";
        if (!fresh)
        {
            t.EntryReason = "enemy no longer believed inside";
            return "end";
        }
        bool firstDecision = !t.Rolled;
        if (firstDecision)
        {
            t.Rolled = true;
            t.EntryRoll = Clamp(roll) * 100;
            t.EntryChance = c.Chance(personality);
        }
        bool selected = c.AllowEntry && t.EntryRoll < t.EntryChance;
        if (selected && partner != "injured" && t.Ammo >= c.MinimumAmmo)
        {
            t.EntryReason = "partner ready and entry selected";
            return "ready";
        }
        t.EntryReason =
            !c.AllowEntry ? "entry disabled"
            : !selected ? "personality keeps watch"
            : partner == "injured" ? "self injured"
            : "insufficient loaded ammunition";
        if (firstDecision && c.Extension > 0)
        {
            t.Extended = partner != "stale";
            watchUntil = (t.Extended ? now : watchUntil) + c.Extension;
            t.WatchRemaining = Math.Max(0, watchUntil - now);
        }
        return now < watchUntil ? "wait" : "end";
    }

    public void End(double now, double? latestEvidence = null)
    {
        if (Trace == null)
            return;
        Trace.State = "ended";
        Trace.WatchRemaining = 0;
        if (attempts.TryGetValue(target, out var a))
        {
            a.At = MedicalChoice.Finite(now) ? now : last;
            a.Evidence =
                latestEvidence.HasValue
                && MedicalChoice.Finite(latestEvidence.Value)
                && latestEvidence.Value <= a.At
                    ? Math.Max(evidence, latestEvidence.Value)
                    : evidence;
        }
    }

    public void ClearCurrent() => Trace = null;
}
