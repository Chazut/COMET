using System;
using System.Collections.Generic;
using System.Linq;

namespace Comet.Core;

// A07 ports ThreatPicker from zzap fbb86b83; score units are multiplied by 100 for RR.
public sealed class ThreatContact
{
    public string Id { get; set; } = "";
    public string Source { get; set; } = "";
    public bool Valid { get; set; }
    public bool Visible { get; set; }
    public bool LookingAtMe { get; set; }
    public bool Footsteps { get; set; }
    public bool Gunfire { get; set; }
    public bool IsPmc { get; set; }
    public bool BadlyInjured { get; set; }
    public double HeardAge { get; set; } = double.PositiveInfinity;
    public double HeardDistance { get; set; } = double.PositiveInfinity;
    public double? ObservedAt { get; set; }
    public double? Distance { get; set; }
    public double? X { get; set; }
    public double? Y { get; set; }
    public double? Z { get; set; }
    public double? HitAt { get; set; }
    public double? ShotAt { get; set; }
}

public sealed class ThreatFrame
{
    public double Now { get; set; }
    public string Current { get; set; } = "";
    public string Trigger { get; set; } = "periodic";
    public bool IsAi { get; set; }
    public bool IsSupportedBot { get; set; }
    public bool IsAuthority { get; set; }
    public bool Active { get; set; }
    public bool Ghost { get; set; }
    public bool NativeUrgency { get; set; }
    public bool Truncated { get; set; }
    public List<ThreatContact> Contacts { get; } = new List<ThreatContact>();
}

public sealed class ThreatScore
{
    public string Target { get; set; } = "";
    public bool Eligible { get; set; }
    public string Reason { get; set; } = "";
    public string Source { get; set; } = "";
    public double? Age { get; set; }
    public double? Distance { get; set; }
    public double? X { get; set; }
    public double? Y { get; set; }
    public double? Z { get; set; }
    public bool Visible { get; set; }
    public bool Immediate { get; set; }
    public double Score { get; set; }
    public double Sight { get; set; }
    public double Hit { get; set; }
    public double Fire { get; set; }
    public double Proximity { get; set; }
    public double Facing { get; set; }
    public double Steps { get; set; }
    public double? HitAt { get; set; }
}

public sealed class ThreatTrace
{
    public double At { get; set; }
    public string Previous { get; set; } = "";
    public string Selected { get; set; } = "";
    public string Reason { get; set; } = "";
    public string Trigger { get; set; } = "";
    public double HoldRemaining { get; set; }
    public double Margin { get; set; }
    public bool Emergency { get; set; }
    public bool Truncated { get; set; }
    public List<ThreatScore> Candidates { get; set; } = new List<ThreatScore>();
}

public sealed class ThreatSelection
{
    public const int CandidateLimit = 32;
    private readonly CometConfig config;
    private string held = "";
    private double holdUntil;

    public ThreatSelection(CometConfig config)
    {
        this.config = config;
    }

    public void Reset()
    {
        held = "";
        holdUntil = 0;
    }

    public static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

    private static double Ago(double? time, double now) =>
        time.HasValue && time.Value > 0 ? now - time.Value : 999;

    public static bool Scope(ThreatFrame f) =>
        f.IsAi && f.IsSupportedBot && f.IsAuthority && f.Active && !f.Ghost;

    public ThreatScore Score(ThreatContact e, double now)
    {
        var c = config.ThreatSelection;
        var s = new ThreatScore
        {
            Target = e.Id,
            Source = e.Source,
            Visible = e.Visible,
            Eligible = e.Valid,
            Distance = e.Distance,
            X = e.X,
            Y = e.Y,
            Z = e.Z,
            Age = e.ObservedAt.HasValue ? now - e.ObservedAt.Value : null,
        };
        if (!e.Valid)
        {
            s.Reason = "threat-invalid-contact";
            return s;
        }
        double hit = Ago(e.HitAt, now),
            shot = Ago(e.ShotAt, now);
        s.Sight = e.Visible ? 50 : 0;
        s.Hit = hit < c.HitMemory ? 100 : 0;
        s.Fire = shot < c.ShotMemory ? 60 : 0;
        s.Facing = e.Visible && e.LookingAtMe ? 20 : 0;
        s.Proximity = 60 * Math.Max(0, Math.Min(1, 1 - (e.Distance ?? 60) / 60));
        s.Score =
            s.Sight
            + s.Hit
            + s.Fire
            + s.Facing
            + s.Proximity
            + (hit < 10 || shot < 10 ? 25 : 0)
            + (e.IsPmc ? 30 : 0);
        s.HitAt = s.Hit > 0 ? e.HitAt : null;
        s.Reason =
            s.Hit > 0 ? "threat-recent-attacker"
            : s.Fire > 0 ? "threat-incoming-fire"
            : "threat-known-contact";
        return s;
    }

    private double Flank(ThreatContact e, ThreatContact? goal)
    {
        if (goal == null || goal.Id == e.Id)
            return 0;
        var c = config.ThreatSelection;
        double d = e.Distance ?? 999,
            g = goal.Distance ?? 999;
        if (e.Visible)
            return d <= c.CloseDistance && (d < c.ImmediateDistance || d < g * .6)
                ? 120 + (d < c.ImmediateDistance ? 50 : 0)
                : 0;
        return
            e.Footsteps
            && e.HeardAge <= c.FootstepMemory
            && e.HeardDistance <= c.FootstepDistance
            && e.HeardDistance <= g * .6
            ? 100 + (d < c.ImmediateDistance ? 50 : 0)
            : 0;
    }

    public ThreatTrace Evaluate(ThreatFrame f)
    {
        var c = config.ThreatSelection;
        var t = new ThreatTrace
        {
            At = f.Now,
            Previous = f.Current,
            Trigger = f.Trigger,
            Truncated = f.Truncated,
        };
        if (!config.Enabled || !c.Enabled || !Scope(f) || f.Contacts.Count < 2)
        {
            Reset();
            t.Reason = "threat-yield-to-sain";
            return t;
        }
        var goal = f.Contacts.Find(x => x.Id == f.Current);
        ThreatContact? best = null;
        double bestValue = double.NegativeInfinity;
        foreach (var e in f.Contacts)
        {
            var score = Score(e, f.Now);
            t.Candidates.Add(score);
            if (!score.Eligible || e.Id == f.Current)
                continue;
            double flank = Flank(e, goal),
                hit = Ago(e.HitAt, f.Now),
                shot = Ago(e.ShotAt, f.Now);
            if (hit > c.HitMemory && shot > c.ShotMemory && flank == 0)
            {
                score.Reason = "threat-not-engaging";
                continue;
            }
            bool heardOnly = flank > 0 && !e.Visible;
            if (goal?.Visible == true && !e.Visible && hit > c.HitMemory && !heardOnly)
            {
                score.Reason = "threat-keep-visible-duel";
                continue;
            }
            score.Steps = flank;
            score.Score += flank;
            if (score.Score > bestValue)
            {
                best = e;
                bestValue = score.Score;
            }
        }
        double goalValue = double.NegativeInfinity;
        if (goal?.Valid == true)
        {
            var score = t.Candidates.Find(x => x.Target == goal.Id)!;
            score.Steps = Flank(goal, best);
            score.Score += 35 + (goal.Visible && goal.BadlyInjured ? 30 : 0) + score.Steps;
            goalValue = score.Score;
        }
        if (best != null && bestValue > goalValue)
        {
            held = best.Id;
            holdUntil = f.Now + c.Commitment;
            t.Selected = held;
            t.Reason = "threat-better-target";
        }
        else if (goal?.Valid == true && held == goal.Id)
        {
            if (Ago(goal.HitAt, f.Now) < c.HitMemory || Ago(goal.ShotAt, f.Now) < c.ShotMemory)
                holdUntil = Math.Max(holdUntil, f.Now + 1);
            if (f.Now < holdUntil)
            {
                t.Selected = held;
                t.Reason = "threat-keep-held";
            }
            else
                Reset();
        }
        else
            Reset();
        if (t.Selected.Length == 0)
            t.Reason = "threat-yield-to-sain";
        t.HoldRemaining = Math.Max(0, holdUntil - f.Now);
        return t;
    }
}
