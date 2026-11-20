using System;
using System.Collections.Generic;

namespace Comet.Core;

public sealed class GrenadeTrajectoryConfig
{
    public const double DefaultDistance = 45;
    public bool Enabled { get; set; } = true;
    public bool Corners { get; set; } = true;
    public bool Doors { get; set; } = true;
    public double Distance { get; set; } = DefaultDistance;
    public double CornerDistance { get; set; } = 5;
    public double DoorDepthFirst { get; set; } = 1.8;
    public double DoorDepthSecond { get; set; } = 1.2;
    public double DoorDepthThird { get; set; } = 2.6;
    public double PreparedLifetime { get; set; } = .5;

    public void Validate()
    {
        CometConfig.Range(Distance, 10, 75, nameof(Distance));
        CometConfig.Range(CornerDistance, 1, 8, nameof(CornerDistance));
        CometConfig.Range(DoorDepthFirst, .8, 4, nameof(DoorDepthFirst));
        CometConfig.Range(DoorDepthSecond, .8, 4, nameof(DoorDepthSecond));
        CometConfig.Range(DoorDepthThird, .8, 4, nameof(DoorDepthThird));
        CometConfig.Range(PreparedLifetime, .1, .5, nameof(PreparedLifetime));
    }
}

public sealed class GrenadeTrajectoryAlternative
{
    public string Kind { get; set; } = "last-known";
    public string Reason { get; set; } = "";
    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }
    public int Angle { get; set; }
    public bool Accepted { get; set; }
    public double Clearance { get; set; }
}

public sealed class GrenadeTrajectoryTrace
{
    public string State { get; set; } = "searching";
    public string Reason { get; set; } = "trajectory-searching";
    public string Target { get; set; } = "";
    public string Source { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Door { get; set; } = "";
    public double KnownX { get; set; }
    public double KnownY { get; set; }
    public double KnownZ { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }
    public int Angle { get; set; }
    public int Attempts { get; set; }
    public double Clearance { get; set; }
    public double ValidFor { get; set; } = .5;
    public List<GrenadeTrajectoryAlternative> Alternatives { get; set; } = new();
}

// Shared by every bot and by execution revalidation. A slow native call cannot be preempted.
public sealed class GrenadeArcBudget
{
    private int frame = -1,
        count;
    private double elapsed;

    public bool TryBegin(int current)
    {
        if (frame != current)
        {
            frame = current;
            count = 0;
            elapsed = 0;
        }
        if (count >= 2 || elapsed >= .5)
            return false;
        count++;
        return true;
    }

    public void Charge(double ms) =>
        elapsed += double.IsNaN(ms) || ms < 0 ? double.PositiveInfinity : ms;

    public void Clear()
    {
        frame = -1;
        count = 0;
        elapsed = 0;
    }
}

public static class GrenadeTrajectoryRules
{
    public const int AttemptLimit = 12;
    public const double SearchLifetime = 2;

    public static int[] Angles(bool doorway, bool indoors, float distance = 6) =>
        doorway || indoors && distance < 6 ? new[] { 15, 25 } : new[] { 15, 25, 35, 45 };

    public static double Dispersion(double distance) =>
        .5 + 4.5 * Math.Max(0, Math.Min(1, (distance - 10) / 40));

    public static bool Fresh(double now, double prepared, double lifetime) =>
        ImpactReaction.Finite(now) && now >= prepared && now - prepared < lifetime;
}
