using System;
using System.Collections.Generic;

namespace Comet.Core;

public sealed class DoorReservationConfig
{
    public bool Enabled { get; set; } = true;
    public double LeaseSeconds { get; set; } = 150;
    public double RenewInterval { get; set; } = .5;
    public double BusyRetry { get; set; } = 2;

    public void Validate()
    {
        CometConfig.Range(LeaseSeconds, 1, 300, nameof(LeaseSeconds));
        CometConfig.Range(RenewInterval, .1, 1, nameof(RenewInterval));
        CometConfig.Range(BusyRetry, .5, 10, nameof(BusyRetry));
        if (RenewInterval * 2 > LeaseSeconds)
            throw new ArgumentException(
                "Door renewal must leave at least two intervals per lease."
            );
    }
}

public sealed class DoorReservationLease
{
    public long Token { get; internal set; }
    public string Squad { get; internal set; } = "";
    public string Door { get; internal set; } = "";
    public string Owner { get; internal set; } = "";
    public string Instance { get; internal set; } = "";
    public string Phase { get; internal set; } = "";
    public double Deadline { get; internal set; }
    public double Until { get; internal set; }
    public double ValidatedAt { get; internal set; }
}

// Main-thread coordinator, scoped to one raid. Only detached IDs/times, never game objects.
public sealed class DoorReservationBoard
{
    private readonly Dictionary<Tuple<string, string>, DoorReservationLease> claims = new();
    private long serial;
    private double clock = double.NaN;
    public int Count => claims.Count;

    private bool Clock(double now)
    {
        if (!DoorPolicy.Finite(now) || now < 0 || (!double.IsNaN(clock) && now < clock))
        {
            claims.Clear();
            clock = double.NaN;
            return false;
        }
        clock = now;
        return true;
    }

    public void Sweep(double now)
    {
        if (!Clock(now))
            return;
        var dead = new List<Tuple<string, string>>();
        foreach (var x in claims)
            if (now >= x.Value.Until)
                dead.Add(x.Key);
        foreach (var key in dead)
            claims.Remove(key);
    }

    public DoorReservationLease? Find(string squad, string door, double now)
    {
        if (!Clock(now))
            return null;
        var key = Tuple.Create("raid", door);
        if (!claims.TryGetValue(key, out var lease))
            return null;
        if (now >= lease.Until)
        {
            claims.Remove(key);
            return null;
        }
        return lease;
    }

    public bool Acquire(
        string squad,
        string door,
        string owner,
        string instance,
        string phase,
        double now,
        double deadline,
        DoorReservationConfig c,
        out DoorReservationLease? lease,
        out string reason
    )
    {
        lease = null;
        reason = "reservation-invalid-request";
        if (
            !Clock(now)
            || string.IsNullOrEmpty(squad)
            || string.IsNullOrEmpty(door)
            || string.IsNullOrEmpty(owner)
            || string.IsNullOrEmpty(instance)
            || string.IsNullOrEmpty(phase)
            || !DoorPolicy.Finite(deadline)
            || deadline <= now
        )
            return false;
        if (!c.Enabled)
        {
            reason = "reservation-disabled";
            return true;
        }
        var other = Find(squad, door, now);
        // Even the same bot must present its existing token to renew; Acquire never steals/replaces it.
        if (other != null)
        {
            lease = other;
            reason = "reservation-door-busy";
            return false;
        }
        lease = new()
        {
            Token = ++serial,
            Squad = squad,
            Door = door,
            Owner = owner,
            Instance = instance,
            Phase = phase,
            Deadline = deadline,
            Until = Math.Min(deadline, now + c.LeaseSeconds),
            ValidatedAt = now,
        };
        claims.Add(Tuple.Create("raid", door), lease);
        reason = "reservation-acquired";
        return true;
    }

    public bool Owns(DoorReservationLease lease, string squad, string instance, double now) =>
        squad == lease.Squad
        && instance == lease.Instance
        && ReferenceEquals(Find(squad, lease.Door, now), lease);

    public bool Renew(
        DoorReservationLease lease,
        string squad,
        string instance,
        string phase,
        double now,
        bool valid,
        DoorReservationConfig c
    )
    {
        if (
            !valid
            || !c.Enabled
            || !Owns(lease, squad, instance, now)
            || string.IsNullOrEmpty(phase)
        )
        {
            Release(lease);
            return false;
        }
        // The original operation deadline is immutable, including on a phase change or repeated callbacks.
        lease.Phase = phase;
        lease.ValidatedAt = now;
        lease.Until = lease.Deadline;
        return true;
    }

    public bool Release(DoorReservationLease lease)
    {
        var key = Tuple.Create("raid", lease.Door);
        if (
            !claims.TryGetValue(key, out var current)
            || current.Token != lease.Token
            || !ReferenceEquals(current, lease)
        )
            return false;
        return claims.Remove(key);
    }

    public void Clear()
    {
        claims.Clear();
        clock = double.NaN;
    } // Serial intentionally survives Clear.
}

public sealed class DoorReservationTrace
{
    public string State { get; set; } = "";
    public string Reason { get; set; } = "";
    public string Door { get; set; } = "";
    public string Squad { get; set; } = "";
    public string Owner { get; set; } = "";
    public string Phase { get; set; } = "";
    public string Target { get; set; } = "";
    public long Token { get; set; }
    public double Remaining { get; set; }
    public double BudgetRemaining { get; set; }
    public double RetryRemaining { get; set; }
    public double ValidFor { get; set; } = 1.5;
    public bool ActionObserved { get; set; }
    public string[] Alternatives { get; set; } = Array.Empty<string>();
}
