using System;

namespace Comet.Core;

public sealed class DoorSpatialConfig
{
    public bool Enabled { get; set; } = true;

    // Distance from the candidate's centre to a door panel, not an extra body radius.
    public double Clearance { get; set; } = .55;

    public void Validate() => CometConfig.Range(Clearance, .25, 1.25, nameof(Clearance));
}

public sealed class DoorSpatialTrace
{
    public double? RawBottom { get; set; }
    public DoorPoint? RawClose1 { get; set; }
    public DoorPoint? RawClose2 { get; set; }
    public DoorPoint? RawOpen1 { get; set; }
    public DoorPoint? RawOpen2 { get; set; }
    public string State { get; set; } = "rejected";
    public string Reason { get; set; } = "geometry-uncertain";
    public string Door { get; set; } = "";
    public string DoorState { get; set; } = "";
    public string Source { get; set; } = "";
    public string Blocker { get; set; } = "";
    public string BlockerState { get; set; } = "";
    public double Margin { get; set; }
    public double ValidFor { get; set; } = .6;
    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }
}

public static class DoorSpatial
{
    // Link points describe two spans. Only the closed span defines the fixed frame.
    // Waypoints custom links reverse these fields; the live leaf is always read from physics.
    public static bool Decode(
        string id,
        string state,
        string linkName,
        DoorPoint close1,
        DoorPoint close2,
        DoorPoint open1,
        DoorPoint open2,
        double bottom,
        out DoorGeometry g
    )
    {
        g = new();
        bool custom =
            linkName != null && linkName.StartsWith("DoorLink_Custom_", StringComparison.Ordinal);
        var a = custom ? open1 : close1;
        var b = custom ? open2 : close2;
        var leafA = custom ? close1 : open1;
        var leafB = custom ? close2 : open2;
        if (
            string.IsNullOrEmpty(id)
            || !a.Finite
            || !b.Finite
            || !leafA.Finite
            || !leafB.Finite
            || !DoorPolicy.Finite(bottom)
            || Math.Abs(a.Y - b.Y) > .35
            || Math.Abs((a.Y + b.Y) * .5 - bottom) > 4
        )
            return false;
        var axis = new DoorPoint(b.X - a.X, 0, b.Z - a.Z);
        double width = axis.Length;
        if (width < .4 || width > 4)
            return false;
        axis = axis * (1 / width);
        g = new()
        {
            Id = id,
            State = state,
            Center = new((a.X + b.X) * .5, bottom, (a.Z + b.Z) * .5),
            Axis = axis,
            Normal = new(axis.Z, 0, -axis.X),
            HalfWidth = width * .5,
            Source = custom ? "waypoints" : "native",
            OpenA = leafA,
            OpenB = leafB,
        };
        return true;
    }

    public static string Candidate(DoorGeometry g, DoorPoint point) =>
        !point.Finite || !g.Center.Finite ? "geometry-invalid-position"
        : Math.Abs(point.Y - g.Center.Y) > .6 ? "geometry-wrong-floor"
        : "";

    public static double SegmentDistance(DoorPoint p, DoorPoint a, DoorPoint b)
    {
        var d = new DoorPoint(b.X - a.X, 0, b.Z - a.Z);
        var offset = new DoorPoint(p.X - a.X, 0, p.Z - a.Z);
        double t = d.Dot(d) > 1e-6 ? Math.Max(0, Math.Min(1, offset.Dot(d) / d.Dot(d))) : 0;
        return (offset - d * t).Length;
    }
}
