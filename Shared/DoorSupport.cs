using System;
using System.Collections.Generic;
using System.Linq;

namespace Comet.Core;

public sealed class DoorSupportConfig
{
    public bool Enabled { get; set; } = true;
    public double MaxDistance { get; set; } = 20;
    public int MaxSupports { get; set; } = 2;
    public double OfferLifetime { get; set; } = 3;
    public double Duration { get; set; } = 150;
    public double Spacing { get; set; } = 1.5;

    public void Validate()
    {
        CometConfig.Range(MaxDistance, 3, 25, nameof(MaxDistance));
        CometConfig.Range(MaxSupports, 1, 2, nameof(MaxSupports));
        CometConfig.Range(OfferLifetime, .5, 5, nameof(OfferLifetime));
        CometConfig.Range(Duration, 3, 300, nameof(Duration));
        CometConfig.Range(Spacing, 1, 3, nameof(Spacing));
    }
}

public sealed class DoorSupportTrace
{
    public string Operation { get; set; } = "";
    public string Leader { get; set; } = "";
    public string LeaderName { get; set; } = "";
    public string SupportName { get; set; } = "";
    public string Support { get; set; } = "";
    public string Door { get; set; } = "";
    public string Target { get; set; } = "";
    public string Role { get; set; } = "";
    public string State { get; set; } = "proposed";
    public string Reason { get; set; } = "support-offered";
    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }
    public double LookX { get; set; }
    public double LookY { get; set; }
    public double LookZ { get; set; }
    public double Remaining { get; set; }
    public double OfferRemaining { get; set; }
    public double PathLength { get; set; }
    public double Protection { get; set; }
    public bool Accepted { get; set; }
    public bool MovementRequested { get; set; }
    public bool MovementAccepted { get; set; }
    public bool Arrived { get; set; }
    public bool Retained { get; set; }
    public bool EyesClear { get; set; }
    public bool MuzzleClear { get; set; }
    public bool AngleVerified { get; set; }
    public double ValidFor { get; set; } = 1.5;
    public List<string> Alternatives { get; set; } = new();
}

// Main-thread coordinator. One reservation per recipient and per role. No Unity objects here.
// The operation has a fixed deadline: neither reassignment nor refreshed telemetry renews it.
public sealed class DoorSupportBoard
{
    private sealed class Operation
    {
        public string Leader = "",
            Door = "",
            Target = "";
        public double Until;
        public int Max;
        public readonly Dictionary<string, int> Attempts = new();
        public readonly HashSet<string> Tried = new();
    }

    private sealed class Lease
    {
        public DoorSupportTrace Trace = new();
        public double OfferUntil,
            Until,
            Spacing;
    }

    private readonly Dictionary<string, Operation> operations = new();
    private readonly Dictionary<string, Lease> leases = new();
    public int Reservations => leases.Count;

    public bool Begin(
        string id,
        string leader,
        string door,
        string target,
        double now,
        double available,
        DoorSupportConfig c
    )
    {
        if (
            !c.Enabled
            || !Finite(now)
            || !Finite(available)
            || available <= 0
            || string.IsNullOrEmpty(id)
            || string.IsNullOrEmpty(leader)
            || string.IsNullOrEmpty(door)
            || string.IsNullOrEmpty(target)
            || operations.ContainsKey(id)
        )
            return false;
        operations.Add(
            id,
            new()
            {
                Leader = leader,
                Door = door,
                Target = target,
                Until = now + Math.Min(c.Duration, available),
                Max = c.MaxSupports,
            }
        );
        return true;
    }

    public bool CanOffer(string id, string role, string recipient, double now)
    {
        if (
            !operations.TryGetValue(id, out var o)
            || !Finite(now)
            || now >= o.Until
            || recipient == o.Leader
            || string.IsNullOrEmpty(recipient)
            || role != "overwatch" && role != "rear-guard"
            || o.Max == 1 && role == "rear-guard"
            || leases.ContainsKey(recipient)
        )
            return false;
        return !leases.Values.Any(l => l.Trace.Operation == id && l.Trace.Role == role);
    }

    public bool Available(DoorPoint point, double spacing, string except = "") =>
        point.Finite
        && Finite(spacing)
        && spacing > 0
        && !leases.Any(x =>
            x.Key != except
            && (point - new DoorPoint(x.Value.Trace.X, x.Value.Trace.Y, x.Value.Trace.Z)).Length
                < Math.Max(spacing, x.Value.Spacing)
        );

    public DoorSupportTrace? Offer(
        string id,
        string recipient,
        string role,
        DoorPoint point,
        DoorPoint look,
        double length,
        double protection,
        double now,
        DoorSupportConfig c
    )
    {
        if (
            !CanOffer(id, role, recipient, now)
            || !point.Finite
            || !look.Finite
            || !Finite(length)
            || length < 0
        )
            return null;
        var o = operations[id];
        o.Attempts.TryGetValue(role, out int attempts);
        o.Attempts[role] = attempts + 1;
        o.Tried.Add(role + ":" + recipient);
        var t = new DoorSupportTrace
        {
            Operation = id,
            Leader = o.Leader,
            Support = recipient,
            Door = o.Door,
            Target = o.Target,
            Role = role,
            X = point.X,
            Y = point.Y,
            Z = point.Z,
            LookX = look.X,
            LookY = look.Y,
            LookZ = look.Z,
            PathLength = length,
            Protection = protection,
            Remaining = o.Until - now,
            OfferRemaining = Math.Min(c.OfferLifetime, o.Until - now),
        };
        leases.Add(
            recipient,
            new()
            {
                Trace = t,
                Until = o.Until,
                OfferUntil = now + t.OfferRemaining,
                Spacing = c.Spacing,
            }
        );
        return t;
    }

    public bool Observe(string recipient, double now, double remaining)
    {
        if (!leases.TryGetValue(recipient, out var l))
            return false;
        if (!Finite(now) || !Finite(remaining) || remaining <= 0)
        {
            End(recipient, "support-budget-ended");
            return false;
        }
        l.Until = Math.Min(l.Until, now + remaining);
        var t = l.Trace;
        t.Remaining = Math.Max(0, l.Until - now);
        t.OfferRemaining = Math.Max(0, l.OfferUntil - now);
        if (now >= l.Until || !t.Accepted && now >= l.OfferUntil)
        {
            End(recipient, t.Accepted ? "support-budget-ended" : "support-offer-expired");
            return false;
        }
        return true;
    }

    public bool Accept(string recipient, double now)
    {
        if (!leases.TryGetValue(recipient, out var l) || !Observe(recipient, now, l.Until - now))
            return false;
        l.Trace.Accepted = true;
        l.Trace.State = "accepted";
        l.Trace.Reason = "support-native-accepted";
        return true;
    }

    public void RequestMove(string recipient, bool accepted)
    {
        if (!leases.TryGetValue(recipient, out var l) || !l.Trace.Accepted)
            return;
        var t = l.Trace;
        t.MovementRequested = true;
        t.MovementAccepted = accepted;
        t.State = accepted ? "moving" : "released";
        t.Reason = accepted ? "support-moving" : "support-move-refused";
        if (!accepted)
            leases.Remove(recipient);
    }

    public bool Arrive(
        string recipient,
        DoorPoint actual,
        bool nativeComplete,
        bool eyes,
        bool muzzle
    )
    {
        if (!leases.TryGetValue(recipient, out var l) || !l.Trace.Accepted)
            return false;
        var t = l.Trace;
        if (!actual.Finite || Horizontal(actual - new DoorPoint(t.X, t.Y, t.Z)) >= .6)
            return false;
        t.Arrived = t.MovementAccepted;
        t.Retained = !t.MovementRequested;
        t.EyesClear = eyes;
        t.MuzzleClear = muzzle;
        t.AngleVerified = eyes && muzzle;
        // Geometry observations describe the view; they do not veto the source Hold step.
        t.State = "holding";
        t.Reason = "support-at-post";
        return true;
    }

    public void End(string recipient, string reason)
    {
        if (!leases.TryGetValue(recipient, out var l))
            return;
        l.Trace.State = "released";
        l.Trace.Reason = reason;
        l.Trace.AngleVerified = false;
        l.Trace.Remaining = l.Trace.OfferRemaining = 0;
        leases.Remove(recipient);
    }

    public void EndOperation(string id, string reason)
    {
        foreach (
            var recipient in leases
                .Where(x => x.Value.Trace.Operation == id)
                .Select(x => x.Key)
                .ToArray()
        )
            End(recipient, reason);
        operations.Remove(id);
    }

    public void Clear()
    {
        foreach (var id in operations.Keys.ToArray())
            EndOperation(id, "support-raid-ended");
    }

    private static bool Finite(double n) => DoorPolicy.Finite(n);

    private static double Horizontal(DoorPoint p) => Math.Sqrt(p.X * p.X + p.Z * p.Z);
}

// DoorTacticClass.LeaderStillOnDoor: death is immediate, leaving the door gets two seconds.
public sealed class SourceDoorLeaderLifetime
{
    private double goneAt = -1;

    public bool Continue(double now, bool alive, bool onDoor)
    {
        if (!alive)
            return false;
        if (onDoor)
        {
            goneAt = -1;
            return true;
        }
        if (goneAt < 0)
            goneAt = now;
        return now - goneAt <= 2;
    }
}
