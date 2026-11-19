using System;
using System.Collections.Generic;

namespace Comet.Core;

public sealed class ShotGeometryConfig
{
    public bool Enabled { get; set; } = true;
    public double CacheSeconds { get; set; } = .3;
    public double MuzzleMovement { get; set; } = .25;
    public double DirectionDegrees { get; set; } = 5;
    public bool AllowPenetration { get; set; } = true;

    public void Validate()
    {
        CometConfig.Range(CacheSeconds, 0, .3, nameof(CacheSeconds));
        CometConfig.Range(MuzzleMovement, .02, .5, nameof(MuzzleMovement));
        CometConfig.Range(DirectionDegrees, .5, 10, nameof(DirectionDegrees));
    }
}

// Detached native surface/ammunition observations. No random draw, Shot or Unity reference.
public sealed class ShotSurface
{
    public string Material = "unknown",
        Lane = "aim",
        Kind = "solid";
    public double Distance,
        Level,
        Chance,
        Ricochet,
        Incidence;
    public bool Known,
        Forward = true;
}

public sealed class ShotGeometryFrame
{
    public bool Complete,
        OriginClear,
        AllySafe,
        AmmoKnown,
        Visible,
        Blindfire;
    public double TargetDistance,
        FloorDifference;
    public double Power,
        Deviation,
        AmmoChance,
        AmmoRicochet,
        Degradation;
    public ShotSurface[] Surfaces = Array.Empty<ShotSurface>();
}

public sealed class ShotGeometryTrace
{
    public string State = "deferred",
        Reason = "geometry-budget",
        Consumer = "",
        Target = "",
        Action = "",
        Weapon = "",
        Ammo = "";
    public bool Allowed,
        Fresh,
        AllySafe,
        Cached,
        PenetrationEstimated;
    public int PassThroughSurfaces;
    public double Age,
        ValidFor = .2,
        EstimatedChance,
        ConservativePower,
        MuzzleX,
        MuzzleY,
        MuzzleZ,
        AimX,
        AimY,
        AimZ;
    public ShotSurface[] Surfaces = Array.Empty<ShotSurface>();
    public string[] Alternatives =
    {
        "Wait for a clear muzzle or opening",
        "Keep the native movement or cover action",
        "Normal SAIN fire on a visible target",
    };
}

public static class ShotGeometryPolicy
{
    public static ShotGeometryTrace Evaluate(ShotGeometryFrame f, ShotGeometryConfig c)
    {
        var t = new ShotGeometryTrace { Fresh = f.Complete, AllySafe = f.AllySafe };
        if (!f.Complete)
            return t;
        t.State = "clear";
        t.Allowed = true;
        t.Reason = "geometry-clear";
        if (f.Visible)
        {
            t.Reason = "geometry-visible-target";
            return t;
        }
        t.Surfaces = Snapshot(f.Surfaces ?? Array.Empty<ShotSurface>());
        foreach (var surface in t.Surfaces)
        {
            // Keep the explicit door/glass permission. Bullet penetration is the game's job.
            if (surface.Kind is "door" or "glass")
            {
                t.PassThroughSurfaces++;
                continue;
            }
            if (f.TargetDistance - surface.Distance < .75)
                continue;
            bool otherFloor = Math.Abs(f.FloorDifference) >= 2.3;
            bool wallInFace = !f.Blindfire && surface.Distance < 2.5;
            if (otherFloor || wallInFace)
            {
                t.State = "blocked";
                t.Allowed = false;
                t.Reason = otherFloor ? "geometry-other-floor" : "geometry-wall-in-face";
                break;
            }
        }
        return t;
    }

    private static ShotSurface[] Snapshot(ShotSurface[] surfaces)
    {
        var result = new ShotSurface[surfaces.Length];
        for (int i = 0; i < result.Length; i++)
        {
            var s = surfaces[i] ?? new();
            result[i] = new()
            {
                Kind = s.Kind ?? "solid",
                Material = s.Material ?? "unknown",
                Lane = s.Lane ?? "aim",
                Known = s.Known,
                Forward = s.Forward,
                Distance = Finite(s.Distance) ? Math.Max(0, s.Distance) : 0,
                Level = Finite(s.Level) ? Math.Max(0, s.Level) : 0,
                Chance = Unit(s.Chance) ? s.Chance : 0,
                Ricochet = Unit(s.Ricochet) ? s.Ricochet : 0,
                Incidence = Finite(s.Incidence) ? Math.Max(0, Math.Min(180, s.Incidence)) : 0,
            };
        }
        return result;
    }

    private static bool Finite(double x) => DoorPolicy.Finite(x);

    private static bool Unit(double x) => Finite(x) && x >= 0 && x <= 1;

    public static bool CanReuse(
        double now,
        double at,
        int frame,
        int checkedFrame,
        ShotGeometryTrace? trace,
        DoorPoint muzzle,
        DoorPoint oldMuzzle,
        DoorPoint aim,
        DoorPoint oldAim,
        DoorPoint direction,
        DoorPoint oldDirection,
        string key,
        string oldKey,
        bool stable,
        ShotGeometryConfig c
    )
    {
        return trace?.Fresh == true
            && trace.State != "released"
            && Finite(now)
            && now >= at
            && now - at < c.CacheSeconds
            && key == oldKey
            && aim.Finite
            && oldAim.Finite
            && (aim - oldAim).Length < 1;
    }
}
