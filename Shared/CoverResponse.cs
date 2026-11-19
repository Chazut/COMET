using System;
using System.Collections.Generic;
using System.Linq;

namespace Comet.Core;

// Geometry and own mobility only. No live enemy transform or enemy weapon/health lookup.
public sealed class CoverTravel
{
    public bool Complete,
        DoorBusy,
        CanSprint;
    public double PathLength,
        VerticalTravel,
        StartError,
        WalkSpeed,
        SprintSpeed,
        Stamina;
    public int Corners;

    // Start, 1/3, 2/3, destination. Union of dated known threats; not a hit probability.
    public double[] Exposure = Array.Empty<double>();
}

public sealed class CoverResponseTrace
{
    public string State { get; set; } = "neutral";
    public string Reason { get; set; } = "cover-response-no-route";
    public string Target { get; set; } = "";
    public string CoverId { get; set; } = "";
    public double At { get; set; }
    public double? PathLength { get; set; }
    public double? Eta { get; set; }
    public double? FastEta { get; set; }
    public double? ExposedSeconds { get; set; }
    public double? FastExposedSeconds { get; set; }
    public double? ExposureFraction { get; set; }
    public int KnownThreats { get; set; }
    public string TravelMode { get; set; } = "unknown";
    public double ScoreCap { get; set; }
    public double ShootDelta { get; set; }
    public double CoverDelta { get; set; }
}

public static class CoverResponseModel
{
    public static void Adjust(List<Candidate> choices, VisibleSituation v, CometConfig config)
    {
        // Kept as a passive wire contract. ZzapUtility.Visible contains the source cover/run cost.
        v.CoverResponse = new() { State = "source-utility", Reason = "zzap-cover-run-cost" };
    }
}
