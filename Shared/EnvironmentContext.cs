using System;

namespace Comet.Core;

public sealed class EnvironmentContextConfig
{
    public bool Enabled { get; set; } = true;
    public double Refresh { get; set; } = .5;
    public double Movement { get; set; } = 1;
    public double CeilingDistance { get; set; } = 20;

    public void Validate()
    {
        CometConfig.Range(Refresh, .1, 2, nameof(Refresh));
        CometConfig.Range(Movement, .1, 3, nameof(Movement));
        CometConfig.Range(CeilingDistance, 2, 30, nameof(CeilingDistance));
    }
}

public sealed class EnvironmentTrace
{
    public string Reported { get; set; } = "unknown";
    public bool? AiIndoorZone { get; set; }
    public bool? EftIndoor { get; set; }
    public string Source { get; set; } = "unavailable";
    public bool Conflict { get; set; }
    public double Age { get; set; }
    public string Ceiling { get; set; } = "not-checked";
    public double? CeilingHeight { get; set; }
    public double? CeilingAge { get; set; }
    public double CheckedDistance { get; set; }
    public string ProbeReason { get; set; } = "not-requested";
    public string Purpose { get; set; } = "context";
    public string Influence { get; set; } = "observation-only";
    public double ValidFor { get; set; }
}

public readonly struct CeilingResult
{
    public readonly string State,
        Reason;
    public readonly double? Height;

    public CeilingResult(string state, string reason, double? height = null)
    {
        State = state;
        Reason = reason;
        Height = height;
    }
}

// Own-position observations only. No world objects, contacts or inferred cover are retained.
public sealed class EnvironmentContext
{
    private double at = double.NegativeInfinity,
        x,
        y,
        z,
        roofAt = double.NegativeInfinity,
        attemptAt = double.NegativeInfinity,
        rx,
        ry,
        rz,
        range;
    private bool? ai,
        eft;
    private CeilingResult roof = new("not-checked", "not-requested");

    public void Clear()
    {
        at = roofAt = attemptAt = double.NegativeInfinity;
        ai = eft = null;
        roof = new("not-checked", "not-requested");
        range = 0;
    }

    private static bool Valid(
        double now,
        double a,
        double px,
        double py,
        double pz,
        double qx,
        double qy,
        double qz,
        EnvironmentContextConfig c
    ) =>
        ImpactReaction.Finite(now)
        && ImpactReaction.Finite(px)
        && ImpactReaction.Finite(py)
        && ImpactReaction.Finite(pz)
        && now >= a
        && now - a < c.Refresh
        && (px - qx) * (px - qx) + (py - qy) * (py - qy) + (pz - qz) * (pz - qz)
            < c.Movement * c.Movement;

    public bool NeedsSample(
        double now,
        double px,
        double py,
        double pz,
        EnvironmentContextConfig c
    ) => !Valid(now, at, px, py, pz, x, y, z, c);

    public bool NeedsCeiling(
        double now,
        double px,
        double py,
        double pz,
        EnvironmentContextConfig c
    ) => range != c.CeilingDistance || !Valid(now, roofAt, px, py, pz, rx, ry, rz, c);

    public void Observe(double now, double px, double py, double pz, bool? aiZone, bool? eftIndoor)
    {
        at = now;
        x = px;
        y = py;
        z = pz;
        ai = aiZone;
        eft = eftIndoor;
    }

    public void ObserveCeiling(
        double now,
        double px,
        double py,
        double pz,
        EnvironmentContextConfig c,
        CeilingResult result
    )
    {
        // Deferred work does not consume the cache lifetime; a later caller may retry.
        attemptAt = now;
        roofAt = result.State == "deferred" ? double.NegativeInfinity : now;
        rx = px;
        ry = py;
        rz = pz;
        range = c.CeilingDistance;
        roof = result;
    }

    public EnvironmentTrace Read(
        double now,
        double px,
        double py,
        double pz,
        EnvironmentContextConfig c,
        string purpose = "context"
    )
    {
        var t = new EnvironmentTrace { Purpose = purpose };
        if (!c.Enabled || NeedsSample(now, px, py, pz, c))
            return t;
        t.AiIndoorZone = ai;
        t.EftIndoor = eft;
        t.Age = now - at;
        t.ValidFor = Math.Max(0, c.Refresh - t.Age);
        t.Source = ai.HasValue
            ? (eft.HasValue ? "ai-zone+eft-environment" : "ai-zone")
            : (eft.HasValue ? "eft-environment" : "unavailable");
        // No AI zone is not positive outdoor evidence (notably on Factory).
        t.Conflict = ai == true && eft == false;
        t.Reported =
            t.Conflict ? "conflicting"
            : ai == true || eft == true ? "indoor"
            : eft == false ? "outdoor"
            : "unknown";
        if (!NeedsCeiling(now, px, py, pz, c))
        {
            t.Ceiling = roof.State;
            t.CeilingHeight = roof.Height;
            t.CeilingAge = now - roofAt;
            t.CheckedDistance = range;
            t.ProbeReason = roof.Reason;
            t.ValidFor = Math.Min(t.ValidFor, Math.Max(0, c.Refresh - (now - roofAt)));
        }
        else if (roof.State == "deferred" && Valid(now, attemptAt, px, py, pz, rx, ry, rz, c))
        {
            t.Ceiling = "deferred";
            t.ProbeReason = roof.Reason;
            t.ValidFor = Math.Min(t.ValidFor, c.Refresh - (now - attemptAt));
        }
        else if (range > 0)
            t.ProbeReason = "stale-or-moved";
        return t;
    }
}

// One shared budget for all P10 consumers/bots, reset per rendered frame.
// Time is a soft cutoff between queries, not a promise about the cost of a Unity call.
public sealed class EnvironmentProbeBudget
{
    private int frame = -1,
        count;
    private double elapsed;

    public bool TryBegin(int currentFrame)
    {
        if (currentFrame != frame)
        {
            frame = currentFrame;
            count = 0;
            elapsed = 0;
        }
        if (count >= 4 || elapsed >= .25)
            return false;
        count++;
        return true;
    }

    public void Charge(double ms)
    {
        elapsed += double.IsNaN(ms) || ms < 0 ? double.PositiveInfinity : ms;
    }

    public void Clear()
    {
        frame = -1;
        count = 0;
        elapsed = 0;
    }
}

public static class CeilingClassifier
{
    // Inspect the nearest upward blocker. A wall side, vegetation or a dynamic prop is uncertain.
    // Never skip an uncertain first blocker and call a roof further above confirmed.
    public static CeilingResult Classify(
        bool saturated,
        bool hit,
        bool structural,
        double distance,
        double normalY,
        double range
    )
    {
        if (saturated)
            return new("uncertain", "probe-saturated");
        if (!hit)
            return new("clear-to-limit", "no-hit-within-limit");
        if (
            !structural
            || !ImpactReaction.Finite(distance)
            || distance <= 0
            || distance > range
            || !ImpactReaction.Finite(normalY)
            || normalY > -.5
        )
            return new("uncertain", "unconfirmed-overhead-obstacle");
        return new("detected", "overhead-surface", distance);
    }
}
