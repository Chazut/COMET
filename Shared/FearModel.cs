using System;
using System.Collections.Generic;

namespace Comet.Core;

public sealed class FearInput
{
    public double Now, Health = 1, PlayerStreak;
    public double? NearbyDeathAt;
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
    public double PlayerStreak { get; set; }
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

// zzap fear is recomputed from the current situation, without smoothing or panic hysteresis.
public sealed class FearModel
{
    private readonly FearConfig config;
    private double last = double.NaN;
    private double deathAt = double.NegativeInfinity;
    private string deathId = "";
    private readonly Queue<string> deaths = new Queue<string>();
    public FearTrace Current { get; private set; } = new FearTrace();
    public FearModel(FearConfig config) { config.Validate(); this.config = config; }
    public bool ObserveDeath(string id, double now, bool witnessed)
    {
        if (!config.Enabled || !witnessed || id.Length == 0 || !Finite(now) || now < deathAt || deaths.Contains(id)) return false;
        deaths.Enqueue(id); if (deaths.Count > 64) deaths.Dequeue();
        deathId = id; deathAt = now; return true;
    }
    // Ghosts and non-authority clients perform no perception work; no catch-up stress on wake.
    public void Pause(double now) { if (Finite(now) && (double.IsNaN(last) || now >= last)) last = now; }
    public FearTrace Update(FearInput s)
    {
        if (!Finite(s.Now) || !Finite(s.Health) || s.Health < 0 || s.Health > 1 || s.KnownEnemies < 0 || s.AvailableAllies < 0
            || (!double.IsNaN(last) && s.Now < last)) return Current;
        last=s.Now;
        double b=config.Base.TryGetValue(s.Personality,out var baseFear)?baseFear:config.Base["Normal"];
        double sensitivity=config.Sensitivity.TryGetValue(s.Personality,out var sen)?sen:config.Sensitivity["Normal"];
        double injury=config.Injury*(1-s.Health);
        double effectiveDeath = s.NearbyDeathAt ?? deathAt;
        double death=s.Now-effectiveDeath<config.DeathMemory?config.AllyDeath:0;
        double outnumbered=s.KnownEnemies>s.AvailableAllies?config.Outnumbered:0;
        double suppression=s.HeavySuppression?config.HeavySuppression:0;
        double streak=Math.Max(0,Math.Min(.25,s.PlayerStreak));
        double target=config.Enabled?Clamp((b+(injury+death+outnumbered+suppression+streak)*sensitivity)*config.Multiplier):0;
        double level=target;
        bool panic=level>config.PanicEnter;
        Current = new FearTrace { At = s.Now, Level = level, Target = target, Base = b, Sensitivity = sensitivity,
            Multiplier = config.Multiplier, Injury = injury, AllyDeath = death, Outnumbered = outnumbered, Suppression = suppression, PlayerStreak=streak,
            KnownEnemies = s.KnownEnemies, AvailableAllies = s.AvailableAllies, CountsTruncated = s.CountsTruncated,
            ScoreCap = config.Enabled ? config.ScoreCap : 0, Panic = panic,
            Transition = panic != Current.Panic ? panic ? "panic-enter" : "panic-exit" : Current.Transition,
            PanicChangedAt = panic != Current.Panic ? s.Now : Current.PanicChangedAt,
            DeathSource = death > 0 ? "nearby-squad-death" : "none", DeathAt = death > 0 ? effectiveDeath : (double?)null };
        return Current;
    }
    private static double Clamp(double x) => Finite(x) ? Math.Max(0, Math.Min(1, x)) : 0;
    private static bool Finite(double x) => !double.IsNaN(x) && !double.IsInfinity(x);
}
