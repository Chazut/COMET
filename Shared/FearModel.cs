using System;
using System.Collections.Generic;

namespace Comet.Core;

public sealed class FearInput
{
    public double Now, Health = 1;
    public string Personality = "Normal";
    public bool HeavySuppression, CountsTruncated;
    public int KnownEnemies, AvailableAllies;
}

public static class FearEvidence
{
    public static bool KnownThreat(double age, double distanceSquared, double lifetime) =>
        !double.IsNaN(age) && age >= 0 && age <= Math.Min(20, lifetime)
        && distanceSquared >= 0 && distanceSquared <= 3600;
    public static bool InView(double distance, double angle, double visionDistance, double halfAngle, double range, bool flashed) =>
        !flashed && distance > .01 && distance <= Math.Min(range, visionDistance)
        && angle >= 0 && angle <= Math.Min(60, halfAngle);
}

public sealed class FearTrace
{
    public double At { get; set; }
    public double Level { get; set; }
    public double Target { get; set; }
    public double Base { get; set; }
    public double Sensitivity { get; set; }
    public double Multiplier { get; set; }
    public double Injury { get; set; }
    public double AllyDeath { get; set; }
    public double Outnumbered { get; set; }
    public double Suppression { get; set; }
    public double ScoreCap { get; set; }
    public int KnownEnemies { get; set; }
    public int AvailableAllies { get; set; }
    public bool CountsTruncated { get; set; }
    public bool Panic { get; set; }
    public string Transition { get; set; } = "steady";
    public double? PanicChangedAt { get; set; }
    public string DeathSource { get; set; } = "none";
    public double? DeathAt { get; set; }
}

// Per observer, never per target. No global player kill counter or live enemy health.
public sealed class FearModel
{
    private readonly FearConfig config;
    private double last = double.NaN, lastStress = double.NegativeInfinity, health = 1;
    private double deathAt = double.NegativeInfinity;
    private string deathId = "";
    private readonly Queue<string> deaths = new Queue<string>();
    public FearTrace Current { get; private set; } = new FearTrace();
    public FearModel(FearConfig config) { config.Validate(); this.config = config; }
    public bool ObserveDeath(string id, double now, bool witnessed)
    {
        if (!config.Enabled || !witnessed || id.Length == 0 || !Finite(now) || now < deathAt || deaths.Contains(id)) return false;
        deaths.Enqueue(id); if (deaths.Count > 64) deaths.Dequeue();
        deathId = id; deathAt = now; lastStress = Math.Max(lastStress, now); return true;
    }
    // Ghosts and non-authority clients perform no perception work; no catch-up stress on wake.
    public void Pause(double now) { if (Finite(now) && (double.IsNaN(last) || now >= last)) last = now; }
    public FearTrace Update(FearInput s)
    {
        if (!Finite(s.Now) || !Finite(s.Health) || s.Health < 0 || s.Health > 1 || s.KnownEnemies < 0 || s.AvailableAllies < 0
            || (!double.IsNaN(last) && s.Now < last)) return Current;
        bool first = double.IsNaN(last);
        // Never apply a newly sampled stressor retroactively across a long frame stall.
        double dt = first ? 0 : Math.Min(.5, s.Now - last);
        double start = s.Now - dt;
        last = s.Now;
        double b = config.Base.TryGetValue(s.Personality, out var baseFear) ? baseFear : config.Base["Normal"];
        double sensitivity = config.Sensitivity.TryGetValue(s.Personality, out var sen) ? sen : config.Sensitivity["Normal"];
        bool numbers = s.KnownEnemies > 1 + s.AvailableAllies && !s.CountsTruncated;
        if (s.Health < health - .0001 || s.HeavySuppression || numbers) lastStress = s.Now;
        health = s.Health;
        double injury = config.Injury * (1 - s.Health);
        double death = config.AllyDeath * Math.Max(0, 1 - (s.Now - deathAt) / config.DeathMemory);
        double outnumbered = numbers ? config.Outnumbered : 0;
        double suppression = s.HeavySuppression ? config.HeavySuppression : 0;
        double target = Clamp((b + (injury + death + outnumbered + suppression) * sensitivity) * config.Multiplier);
        double level = first ? Clamp(b * config.Multiplier) : Current.Level;
        double recoveryTime = Math.Max(0, s.Now - Math.Max(start, lastStress + config.RecoveryDelay));
        if (target > level) level = Math.Min(target, level + dt * config.RisePerSecond);
        else level = Math.Max(target, level - recoveryTime * config.RecoveryPerSecond);
        if (!config.Enabled) level = target = 0;
        bool panic = config.Enabled && (Current.Panic ? level > config.PanicExit : level >= config.PanicEnter);
        Current = new FearTrace { At = s.Now, Level = level, Target = target, Base = b, Sensitivity = sensitivity,
            Multiplier = config.Multiplier, Injury = injury, AllyDeath = death, Outnumbered = outnumbered, Suppression = suppression,
            KnownEnemies = s.KnownEnemies, AvailableAllies = s.AvailableAllies, CountsTruncated = s.CountsTruncated,
            ScoreCap = config.Enabled ? config.ScoreCap : 0, Panic = panic,
            Transition = panic != Current.Panic ? panic ? "panic-enter" : "panic-exit" : Current.Transition,
            PanicChangedAt = panic != Current.Panic ? s.Now : Current.PanicChangedAt,
            DeathSource = death > 0 ? "witnessed-squad-death" : "none", DeathAt = death > 0 ? deathAt : (double?)null };
        return Current;
    }
    public static void Adjust(List<Candidate> candidates, Situation s, FearConfig config)
    {
        double amount = config.Enabled && s.Fear != null ? config.ScoreCap * Clamp(s.Fear.Level) : 0;
        foreach (var c in candidates)
        {
            c.ScoreBeforeFear = c.Score;
            // Never make an ineligible action usable. Shooting stays available at its original score.
            double factor = !c.Eligible ? 0 : c.Decision == "Push" ? -.8 : c.Decision == "Flank" ? -.2
                : c.Decision == "Cover" || c.Decision == "FallBack" ? 1 : c.Decision == "Hold" && s.InCover ? .5 : 0;
            c.Score = Math.Round(Math.Max(0, c.Score + amount * factor), 3);
            c.FearDelta = c.Score - c.ScoreBeforeFear;
        }
    }
    private static double Clamp(double x) => Finite(x) ? Math.Max(0, Math.Min(1, x)) : 0;
    private static bool Finite(double x) => !double.IsNaN(x) && !double.IsInfinity(x);
}
