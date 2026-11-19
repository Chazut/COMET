using System;
using System.Collections.Generic;

namespace Comet.Core;

public sealed class DoorTransitConfig
{
    public bool Enabled { get; set; } = true;
    public double Anticipation { get; set; } = 3;
    public double SlowDistance { get; set; } = 1.5;
    public double SpeedCap { get; set; } = .25;
    public double BlockedLimit { get; set; } = 4;

    public void Validate()
    {
        CometConfig.Range(Anticipation, 1, 5, nameof(Anticipation));
        CometConfig.Range(SlowDistance, .5, 3, nameof(SlowDistance));
        CometConfig.Range(SpeedCap, .1, .7, nameof(SpeedCap));
        CometConfig.Range(BlockedLimit, 2, 8, nameof(BlockedLimit));
        if (SlowDistance > Anticipation)
            throw new ArgumentException("Door slowing distance must not exceed anticipation.");
    }
}

public sealed class DoorTransitTrace
{
    public string State { get; set; } = "ended";
    public string Reason { get; set; } = "transit-idle";
    public string Door { get; set; } = "";
    public string DoorState { get; set; } = "";
    public string Action { get; set; } = "";
    public double Distance { get; set; }
    public double Blocked { get; set; }
    public double ValidFor { get; set; }
    public bool Slow { get; set; }
    public bool StopSprint { get; set; }
    public bool Recovery { get; set; }
    public bool Requested { get; set; }
    public bool Interacting { get; set; }
    public bool Passed { get; set; }
    public double RequestedSpeed { get; set; }
    public double AppliedSpeed { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }
}

// Detached, raid-local policy. A request or an open door is not evidence of a completed traversal.
public sealed class DoorTransit
{
    private DoorPoint anchor;
    private double last = double.NaN;
    private bool paused;
    public DoorTransitTrace Trace { get; private set; } = new();

    public void End(string reason, bool passed = false)
    {
        Trace.State = "ended";
        Trace.Reason = reason;
        Trace.ValidFor = 0;
        Trace.Slow = false;
        Trace.StopSprint = false;
        Trace.Recovery = false;
        Trace.Passed = passed;
        last = double.NaN;
        paused = false;
    }

    // Release commands immediately, but retain physical evidence across short plan changes.
    public void Pause(string reason)
    {
        var observed = last;
        End(reason);
        last = observed;
        paused = true;
    }

    public DoorTransitTrace Observe(
        string door,
        string doorState,
        string action,
        DoorPoint origin,
        DoorPoint center,
        double distance,
        double now,
        bool blocked,
        bool busy,
        bool interacting,
        DoorTransitConfig c
    )
    {
        if (
            !c.Enabled
            || string.IsNullOrEmpty(door)
            || !origin.Finite
            || !center.Finite
            || !DoorPolicy.Finite(distance)
            || distance < 0
            || distance > c.Anticipation
            || !DoorPolicy.Finite(now)
            || now < 0
            || !double.IsNaN(last) && now < last
        )
        {
            End("transit-invalid-or-out-of-scope");
            return Trace;
        }
        bool resume =
            paused
            && Trace.Door == door
            && !double.IsNaN(last)
            && now - last <= 1
            && (origin - anchor).Length < .2;
        if (Trace.Door != door || Trace.State == "ended" && !resume)
        {
            Trace = new()
            {
                Door = door,
                DoorState = doorState,
                Action = action,
            };
            anchor = origin;
            last = now;
        }
        double dt = double.IsNaN(last) ? 0 : now - last;
        last = now;
        paused = false;
        Trace.DoorState = doorState;
        Trace.Action = action;
        Trace.Distance = distance;
        Trace.X = center.X;
        Trace.Y = center.Y;
        Trace.Z = center.Z;
        Trace.Interacting = interacting;
        Trace.Passed = false;
        Trace.Recovery = false;
        if (
            dt > .5
            || (origin - anchor).Length >= .2
            || !blocked
            || busy
            || interacting
            || distance > c.SlowDistance
        )
        {
            Trace.Blocked = 0;
            anchor = origin;
        }
        else
            Trace.Blocked += Math.Max(0, dt);
        Trace.Slow = blocked && !busy && distance <= c.SlowDistance;
        Trace.StopSprint = blocked && !busy;
        Trace.ValidFor = .35;
        Trace.State =
            interacting ? "interacting"
            : busy ? "waiting"
            : blocked
                ? Trace.Slow ? "slowing"
                    : "approaching"
            : "clear";
        Trace.Reason =
            interacting ? "transit-native-interaction"
            : busy ? "transit-door-busy"
            : blocked ? "transit-door-on-route"
            : "transit-passage-clear";
        Trace.Recovery =
            blocked
            && !busy
            && !interacting
            && (doorState == "Shut" || doorState == "Open")
            && Trace.Blocked >= .75;
        if (Trace.Blocked >= c.BlockedLimit)
        {
            Trace.State = "blocked";
            Trace.Reason = "transit-reconsider-blocked-route";
            Trace.Slow = false;
            Trace.StopSprint = false;
            Trace.Recovery = false;
            Trace.ValidFor = 0;
        }
        return Trace;
    }

    // Distance ALONG the remaining route to the doorway, not to an arbitrary nearby door.
    public static double RouteDistance(DoorGeometry g, IReadOnlyList<DoorPoint> route, double limit)
    {
        if (
            route.Count < 2
            || route.Count > 129
            || !g.Center.Finite
            || !g.Normal.Finite
            || !g.Axis.Finite
            || !DoorPolicy.Finite(g.HalfWidth)
            || g.HalfWidth < .2
            || g.HalfWidth > 2
            || !DoorPolicy.Finite(limit)
            || limit <= 0
        )
            return double.PositiveInfinity;
        double travelled = 0;
        for (int i = 1; i < route.Count; i++)
        {
            var a = route[i - 1];
            var b = route[i];
            if (!a.Finite || !b.Finite)
                return double.PositiveInfinity;
            double da = (a - g.Center).Dot(g.Normal),
                db = (b - g.Center).Dot(g.Normal),
                len = (b - a).Length;
            if (da * db <= 0 && Math.Abs(da - db) > .001)
            {
                double fraction = da / (da - db);
                var p = a + (b - a) * fraction;
                if (
                    Math.Abs(p.Y - g.Center.Y) <= 1.2
                    && Math.Abs((p - g.Center).Dot(g.Axis)) <= g.HalfWidth + .15
                )
                {
                    double d = travelled + len * fraction;
                    return d <= limit ? d : double.PositiveInfinity;
                }
            }
            travelled += len;
            if (travelled > limit)
                break;
        }
        return double.PositiveInfinity;
    }
}

// Remember the latest downstream speed command. Restore only if nobody has overwritten our cap.
public sealed class TransitSpeedLease
{
    public bool Owned { get; private set; }
    public float Requested { get; private set; }
    public float Applied { get; private set; }

    public float Limit(float requested, double cap)
    {
        Requested = requested;
        Applied = Math.Min(requested, (float)cap);
        Owned = true;
        return Applied;
    }

    public bool Release(float current, out float restore)
    {
        restore = Requested;
        bool write = Owned && Math.Abs(current - Applied) < .001f;
        Owned = false;
        return write;
    }
}
