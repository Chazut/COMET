using System;
using System.Collections.Generic;

namespace Comet.Core;

public sealed class GrenadeDispersionConfig
{
    public bool Enabled { get; set; } = true;
    public double Spacing { get; set; } = 3;
    public double MemberRange { get; set; } = 25;

    public void Validate()
    {
        CometConfig.Range(Spacing, 1, 8, nameof(Spacing));
        CometConfig.Range(MemberRange, 5, 60, nameof(MemberRange));
    }
}

// Value-only reservations: no retained bots, game worlds or native paths.
public sealed class GrenadeDispersionLease
{
    public string Profile = "",
        Instance = "",
        Squad = "";
    public long Token;
    public DoorPoint Goal;
    public double Expires;
}

public sealed class GrenadeDispersionAssessment
{
    public bool Conflict;
    public string Peer = "";
    public double Nearest = -1;
    public int Peers;
}

public sealed class GrenadeDispersionTrace
{
    public string State = "reserved",
        Reason = "dispersion-separated-destination",
        Squad = "",
        Peer = "";
    public long Token;
    public int Peers;
    public double X,
        Y,
        Z,
        Spacing,
        MemberRange,
        Nearest = -1,
        LeaseRemaining,
        ValidFor = .5;
    public string[] Alternatives = Array.Empty<string>();
}

public static class GrenadeDispersionRules
{
    public const double LeaseSeconds = 1,
        RenewalSeconds = .2,
        FloorTolerance = 1.5;

    public static bool Nearby(DoorPoint a, DoorPoint b, double range) =>
        a.Finite && b.Finite && (a - b).Length <= range;

    public static GrenadeDispersionAssessment Assess(
        DoorPoint goal,
        IReadOnlyList<GrenadeDispersionLease> peers,
        double spacing,
        double now
    )
    {
        var result = new GrenadeDispersionAssessment();
        foreach (var lease in peers)
        {
            if (lease.Expires <= now || !lease.Goal.Finite)
                continue;
            result.Peers++;
            // Overlapping map coordinates on separate floors must not reserve each other's exits.
            if (Math.Abs(goal.Y - lease.Goal.Y) > FloorTolerance)
                continue;
            double distance = (goal - lease.Goal).Length;
            if (result.Nearest < 0 || distance < result.Nearest)
            {
                result.Nearest = distance;
                result.Peer = lease.Profile;
            }
        }
        result.Conflict = result.Nearest >= 0 && result.Nearest < spacing;
        return result;
    }
}
