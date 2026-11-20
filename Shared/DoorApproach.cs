using System;
using System.Collections.Generic;
using System.Linq;

namespace Comet.Core;

public sealed class DoorApproachConfig
{
    public bool Enabled { get; set; } = true;
    public double DoorDistance { get; set; } = 8;
    public double ContactDistance { get; set; } = 12;
    public double UnseenDelay { get; set; } = 1.5;
    public double ContactLifetime { get; set; } = 60;
    public double ApproachLimit { get; set; } = 12;

    public void Validate()
    {
        CometConfig.Range(DoorDistance, 2, 15, nameof(DoorDistance));
        CometConfig.Range(ContactDistance, 2, 25, nameof(ContactDistance));
        CometConfig.Range(UnseenDelay, .5, 5, nameof(UnseenDelay));
        CometConfig.Range(ContactLifetime, 3, 120, nameof(ContactLifetime));
        CometConfig.Range(ApproachLimit, 3, 20, nameof(ApproachLimit));
    }
}

// Detached geometry and observations: no Unity/game references survive in the policy.
public readonly struct DoorPoint
{
    public readonly double X,
        Y,
        Z;

    public DoorPoint(double x, double y, double z)
    {
        X = x;
        Y = y;
        Z = z;
    }

    public bool Finite => DoorPolicy.Finite(X) && DoorPolicy.Finite(Y) && DoorPolicy.Finite(Z);

    public static DoorPoint operator +(DoorPoint a, DoorPoint b) =>
        new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);

    public static DoorPoint operator -(DoorPoint a, DoorPoint b) =>
        new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);

    public static DoorPoint operator *(DoorPoint a, double b) => new(a.X * b, a.Y * b, a.Z * b);

    public double Length => Math.Sqrt(X * X + Y * Y + Z * Z);

    public double Dot(DoorPoint b) => X * b.X + Y * b.Y + Z * b.Z;
}

public sealed class DoorGeometry
{
    public string Id = "",
        State = "";
    public string Source = "native";
    public DoorPoint OpenA,
        OpenB;
    public DoorPoint Center,
        Axis,
        Normal;
    public double HalfWidth;
}

public sealed class DoorCandidate
{
    public string Door { get; set; } = "";
    public string Side { get; set; } = "";
    public string Reason { get; set; } = "";
    public bool Eligible { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }
    public double PathLength { get; set; }
    public double Score { get; set; }
    public DoorPoint Point => new(X, Y, Z);
}

public sealed class DoorApproachTrace
{
    public long Episode { get; set; }
    public string NativeRoute { get; set; } = "";
    public string State { get; set; } = "planning";
    public string Reason { get; set; } = "door-evaluating";
    public string Target { get; set; } = "";
    public string Source { get; set; } = "";
    public string Door { get; set; } = "";
    public string Side { get; set; } = "";
    public double KnownX { get; set; }
    public double KnownY { get; set; }
    public double KnownZ { get; set; }
    public double ContactAge { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }
    public double PathLength { get; set; }
    public double Elapsed { get; set; }
    public double Remaining { get; set; }
    public bool Requested { get; set; }
    public bool MovementAccepted { get; set; }
    public bool Moved { get; set; }
    public bool Arrived { get; set; }
    public int DoorsScanned { get; set; }
    public int DoorsEligible { get; set; }
    public bool ScanLimitReached { get; set; }
    public Dictionary<string, int> GeometryRefusals { get; set; } = new();
    public double ValidFor { get; set; } = 1.5;
    public List<DoorCandidate> Candidates { get; set; } = new();
}

public static class DoorPolicy
{
    public static bool Finite(double n) => !double.IsNaN(n) && !double.IsInfinity(n);

    public static string Gate(
        bool scoped,
        bool search,
        bool unsafeNow,
        bool known,
        bool visible,
        double age,
        double unseen,
        DoorApproachConfig c,
        double? searchLifetime = null
    )
    {
        if (!c.Enabled || !scoped)
            return "door-out-of-scope";
        if (!search)
            return "door-search-not-owned";
        if (unsafeNow || visible)
            return "door-native-priority";
        if (!known || !Finite(age) || age < 0 || age > (searchLifetime ?? c.ContactLifetime))
            return "door-contact-expired";
        if (!Finite(unseen) || unseen < c.UnseenDelay)
            return "door-contact-just-lost";
        return "";
    }

    public static string Geometry(
        DoorGeometry g,
        DoorPoint bot,
        DoorPoint known,
        IReadOnlyList<DoorPoint> route,
        DoorApproachConfig c
    )
    {
        if (
            !bot.Finite
            || !known.Finite
            || !g.Center.Finite
            || !g.Axis.Finite
            || !g.Normal.Finite
            || !Finite(g.HalfWidth)
            || g.Id.Length == 0
            || g.HalfWidth < .2
            || Math.Abs(g.Axis.Length - 1) > .01
            || Math.Abs(g.Normal.Length - 1) > .01
            || Math.Abs(g.Axis.Dot(g.Normal)) > .01
            || Math.Abs(g.Axis.Y) > .01
            || Math.Abs(g.Normal.Y) > .01
        )
            return "door-invalid-geometry";
        if (g.State != "Open" && g.State != "Shut")
            return "door-not-openable";
        if (new DoorPoint(bot.X - g.Center.X, 0, bot.Z - g.Center.Z).Length > c.DoorDistance)
            return "door-too-far";
        if (Math.Abs(bot.Y - g.Center.Y) > 1.8)
            return "door-wrong-floor";
        if (new DoorPoint(known.X - g.Center.X, 0, known.Z - g.Center.Z).Length > c.ContactDistance)
            return "door-contact-too-far";
        double side = (bot - g.Center).Dot(g.Normal),
            opposite = (known - g.Center).Dot(g.Normal);
        if (
            Math.Abs(side) < .3
            || Math.Abs(opposite) < .6
            || Math.Sign(side) == Math.Sign(opposite)
        )
            return "door-not-between-contact";
        return "";
    }

    public static DoorPoint Stack(DoorGeometry g, DoorPoint bot, int side, int variant = 0) =>
        g.Center
        + g.Normal
            * (
                Math.Sign((bot - g.Center).Dot(g.Normal))
                * (
                    variant
                    / 4 switch
                    {
                        0 => .9,
                        1 => 1.3,
                        2 => .6,
                        _ => 1.8,
                    }
                )
            )
        + g.Axis
            * (
                side
                * (
                    g.HalfWidth
                    + (
                        variant
                        % 4 switch
                        {
                            0 => .7,
                            1 => .4,
                            2 => 1.1,
                            _ => .15,
                        }
                    )
                )
            );

    public static string Placement(
        DoorGeometry g,
        DoorPoint bot,
        DoorPoint raw,
        DoorPoint sampled,
        IReadOnlyList<DoorPoint> path,
        bool complete,
        bool approachStack = false
    )
    {
        if (!sampled.Finite)
            return "door-bad-navmesh-sample";
        double sign = Math.Sign((bot - g.Center).Dot(g.Normal));
        if (
            (sampled - g.Center).Dot(g.Normal) * sign <= .3
            || Math.Abs((sampled - g.Center).Dot(g.Axis)) < g.HalfWidth * .9
        )
            return "door-point-in-opening";
        if (approachStack && DoorApproach.NearStack(bot, sampled))
            return "";
        if (!complete || path.Count < 2)
            return "door-incomplete-stack-route";
        double length = 0;
        for (int i = 0; i < path.Count; i++)
        {
            if (!path[i].Finite)
                return "door-invalid-route";
            if (i > 0)
                length += (path[i] - path[i - 1]).Length;
        }
        if (length > Math.Max(14, (sampled - bot).Length * 3))
            return "door-route-detour";
        return "";
    }

    public static DoorCandidate? Best(IEnumerable<DoorCandidate> candidates) =>
        candidates
            .Where(x =>
                x.Eligible
                && Finite(x.Score)
                && x.Point.Finite
                && Finite(x.PathLength)
                && x.PathLength >= 0
            )
            .OrderBy(x => x.Score)
            .ThenBy(x => x.Door, StringComparer.Ordinal)
            .ThenBy(x => x.Side, StringComparer.Ordinal)
            .FirstOrDefault();
}

public sealed class DoorApproach
{
    public readonly PreparationWait Preparation = new();

    public DoorApproachTrace? Trace { get; private set; }
    public bool Active => Trace?.State == "approaching";
    public bool Planning => Trace?.State == "planning";
    private double started,
        last = double.NaN,
        blockedUntil;
    private DoorPoint origin,
        known;
    private string consumedTarget = "";
    private DoorPoint consumedPoint;
    private double consumedAt;
    private long episode;

    public bool PlanningExpired(double now) =>
        Planning && (!DoorPolicy.Finite(now) || now < started || now - started > 150);

    public bool CanPlan(string target, DoorPoint point, double now) =>
        DoorPolicy.Finite(now)
        && point.Finite
        && now >= blockedUntil
        && !(
            target == consumedTarget && now - consumedAt < 60 && (point - consumedPoint).Length < 2
        );

    public void Begin(string target, string source, DoorPoint point, double age, double now)
    {
        Preparation.Clear();
        known = point;
        started = last = now;
        Trace = new()
        {
            Target = target,
            Source = source,
            KnownX = point.X,
            KnownY = point.Y,
            KnownZ = point.Z,
            ContactAge = age,
        };
    }

    public void Request(DoorCandidate c, DoorPoint bot, double now)
    {
        if (!Planning || !c.Eligible)
            throw new InvalidOperationException("No verified door preparation");
        var t = Trace!;
        t.Episode = ++episode;
        t.Door = c.Door;
        t.Side = c.Side;
        t.X = c.X;
        t.Y = c.Y;
        t.Z = c.Z;
        t.PathLength = c.PathLength;
        t.Requested = true;
        t.Reason = "door-move-requested";
        origin = bot;
        started = last = now;
    }

    public void Accept(bool accepted, double now)
    {
        var t = Trace!;
        t.MovementAccepted = accepted;
        if (!accepted)
        {
            End("door-mover-refused", now);
            return;
        }
        t.State = "approaching";
        t.Reason = "door-move-accepted";
        consumedTarget = t.Target;
        consumedPoint = known;
        consumedAt = now;
    }

    public bool Observe(
        string target,
        DoorPoint contact,
        DoorPoint bot,
        double age,
        double now,
        double limit
    )
    {
        if (!Active)
            return false;
        var t = Trace!;
        if (!DoorPolicy.Finite(now) || now < last || !bot.Finite || !contact.Finite)
        {
            End("door-invalid-clock-or-position", now);
            return false;
        }
        last = now;
        t.ContactAge = age;
        t.Elapsed = Math.Max(0, now - started);
        t.Remaining = Math.Max(0, limit - t.Elapsed);
        if (target != t.Target)
        {
            End("door-contact-changed", now);
            return false;
        }
        t.Moved |= (bot - origin).Length >= .35;
        if (AtStack(bot, new DoorPoint(t.X, t.Y, t.Z)))
        {
            t.Arrived = true;
            t.State = "arrived";
            t.Reason = "door-arrived-resume-search";
            t.ValidFor = 1.5;
            blockedUntil = now + 8;
            return false;
        }
        if (t.Elapsed >= limit)
        {
            End("door-approach-timeout", now);
            return false;
        }
        t.Reason = "door-approaching";
        return true;
    }

    public void End(string reason, double now)
    {
        if (Trace != null)
        {
            Trace.State = "ended";
            Trace.Reason = reason;
            Trace.ValidFor = 1.5;
            Trace.Remaining = 0;
        }
        blockedUntil = DoorPolicy.Finite(now) ? now + 8 : 0;
    }

    public static bool NearStack(DoorPoint bot, DoorPoint goal) => HorizontalSquared(bot, goal) < .9 * .9;

    public static bool AtStack(DoorPoint bot, DoorPoint goal) => HorizontalSquared(bot, goal) < .6 * .6;

    private static double HorizontalSquared(DoorPoint bot, DoorPoint goal)
    {
        double dx = bot.X - goal.X, dz = bot.Z - goal.Z;
        return dx * dx + dz * dz;
    }

    public double Deadline(double limit) => started + limit;

    public void Suspend()
    {
        if (Active)
        {
            Trace!.State = "suspended";
            Trace.Reason = "door-threat-interruption";
            Trace.ValidFor = 0;
        }
    }

    public bool Resume(DoorPoint from, double now, double limit)
    {
        if (Trace?.State != "suspended" || !from.Finite || !DoorPolicy.Finite(now) || now < last)
            return false;
        origin = from;
        started = last = now;
        Trace.State = "approaching";
        Trace.Reason = "door-safe-approach-resumed";
        Trace.ValidFor = 1.5;
        return true;
    }

    public void Clear()
    {
        Trace = null;
        consumedTarget = "";
        blockedUntil = consumedAt = started = 0;
        last = double.NaN;
    }
}

public sealed class DoorWorkBudget
{
    private int frame = -1,
        count;
    private double elapsed;
    private readonly System.Collections.Generic.List<Pending> pending = new();

    private sealed class Pending
    {
        public object? Owner;
        public string Site = "";
        public int Line,
            Seen;
    }

    public long Deferred { get; private set; }
    public int PendingCount => pending.Count;

    // FIFO per actor and call site. A busy early callback cannot consume every frame.
    // Expire abandoned requests; never hold a destroyed raid actor indefinitely.
    public bool TryBegin(
        int current,
        object? owner = null,
        [System.Runtime.CompilerServices.CallerFilePath] string site = "",
        [System.Runtime.CompilerServices.CallerLineNumber] int line = 0
    )
    {
        if (frame != current)
        {
            frame = current;
            count = 0;
            elapsed = 0;
            for (int i = pending.Count - 1; i >= 0; i--)
                if (current < pending[i].Seen || current - pending[i].Seen > 2)
                    pending.RemoveAt(i);
        }
        // No request object or closure on the uncontended path.
        if (pending.Count == 0 && count < 2 && elapsed < .5)
        {
            count++;
            return true;
        }
        int index = -1;
        for (int i = 0; i < pending.Count; i++)
            if (
                ReferenceEquals(pending[i].Owner, owner)
                && pending[i].Site == site
                && pending[i].Line == line
            )
            {
                index = i;
                break;
            }
        if (index < 0)
        {
            if (pending.Count >= 256)
            {
                Deferred++;
                return false;
            }
            pending.Add(
                new()
                {
                    Owner = owner,
                    Site = site,
                    Line = line,
                    Seen = current,
                }
            );
            index = pending.Count - 1;
        }
        else
            pending[index].Seen = current;
        if (count >= 2 || elapsed >= .5 || index != 0)
        {
            Deferred++;
            return false;
        }
        pending.RemoveAt(0);
        count++;
        return true;
    }

    public void Charge(double ms)
    {
        if (!DoorPolicy.Finite(ms) || ms < 0)
            elapsed = double.PositiveInfinity;
        else
            elapsed += ms;
    }

    public void Clear()
    {
        frame = -1;
        count = 0;
        elapsed = 0;
        pending.Clear();
        Deferred = 0;
    }
}
