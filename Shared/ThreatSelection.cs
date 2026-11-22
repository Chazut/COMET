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

    internal double HoldRemaining(double now) => Math.Max(0, holdUntil - now);

    public static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

    private static float Ago(double? time, double now) =>
        time.HasValue && time.Value > 0 ? (float)now - (float)time.Value : 999f;

    public static bool Scope(ThreatFrame f) =>
        f.IsAi && f.IsSupportedBot && f.IsAuthority && f.Active && !f.Ghost;

    public ThreatScore Score(ThreatContact e, double now) => Score(e, now, out _);

    private ThreatScore Score(ThreatContact e, double now, out float threat)
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
            threat = 0f;
            s.Reason = "threat-invalid-contact";
            return s;
        }
        float hit = Ago(e.HitAt, now),
            shot = Ago(e.ShotAt, now);
        bool recentHit = hit < c.HitMemory;
        bool recentShot = shot < c.ShotMemory;
        bool facing = e.Visible && e.LookingAtMe;
        float distance = (float)(e.Distance ?? 60);
        float proximity = .6f * Math.Max(0f, Math.Min(1f, 1f - distance / 60f));
        // Keep native units and addition order for the choice. RR displays points on a 100x scale.
        threat = (e.Visible ? .5f : 0f) + (recentHit ? 1f : 0f) + (recentShot ? .6f : 0f)
            + (facing ? .2f : 0f) + proximity + (hit < 10f || shot < 10f ? .25f : 0f)
            + (e.IsPmc ? .3f : 0f);
        s.Sight = e.Visible ? 50 : 0;
        s.Hit = recentHit ? 100 : 0;
        s.Fire = recentShot ? 60 : 0;
        s.Facing = facing ? 20 : 0;
        s.Proximity = proximity * 100d;
        s.Score = threat * 100d;
        s.HitAt = s.Hit > 0 ? e.HitAt : null;
        s.Reason =
            s.Hit > 0 ? "threat-recent-attacker"
            : s.Fire > 0 ? "threat-incoming-fire"
            : "threat-known-contact";
        return s;
    }

    private float Flank(ThreatContact e, ThreatContact? goal)
    {
        if (goal == null || goal.Id == e.Id)
            return 0;
        var c = config.ThreatSelection;
        float d = (float)(e.Distance ?? 999);
        float flankDistance = (float)(goal.Distance ?? 999) * .6f;
        float closeBonus = d < c.ImmediateDistance ? .5f : 0f;
        if (e.Visible)
            return d <= c.CloseDistance && (d < c.ImmediateDistance || d < flankDistance)
                ? 1.2f + closeBonus
                : 0;
        return
            e.Footsteps
            && e.HeardAge <= c.FootstepMemory
            && e.HeardDistance <= c.FootstepDistance
            && (float)e.HeardDistance <= flankDistance
            ? 1f + closeBonus
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
        if (!config.Enabled || !c.Enabled || !Scope(f))
        {
            Reset();
            t.Reason = "threat-yield-to-sain";
            return t;
        }
        if (f.Contacts.Count < 2)
        {
            t.Reason = "threat-yield-to-sain";
            t.HoldRemaining = HoldRemaining(f.Now);
            return t;
        }
        var goal = f.Contacts.Find(x => x.Id == f.Current);
        ThreatContact? best = null;
        ThreatScore? goalScore = null;
        float bestValue = float.NegativeInfinity;
        float goalValue = float.NegativeInfinity;
        foreach (var e in f.Contacts)
        {
            var score = Score(e, f.Now, out float value);
            t.Candidates.Add(score);
            if (ReferenceEquals(e, goal) && score.Eligible)
            {
                goalScore = score;
                goalValue = value;
            }
            if (!score.Eligible || e.Id == f.Current)
                continue;
            float flank = Flank(e, goal),
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
            value += flank;
            score.Steps = flank * 100d;
            score.Score = value * 100d;
            if (value > bestValue)
            {
                best = e;
                bestValue = value;
            }
        }
        if (goalScore != null && goal != null)
        {
            float flank = Flank(goal, best);
            goalValue = goalValue + .35f + (goal.Visible && goal.BadlyInjured ? .3f : 0f);
            goalValue += flank;
            goalScore.Steps = flank * 100d;
            goalScore.Score = goalValue * 100d;
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
        t.HoldRemaining = HoldRemaining(f.Now);
        return t;
    }
}
