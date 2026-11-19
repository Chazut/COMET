using System;
using System.Collections.Generic;

namespace Comet.Core;

public static class MovementHearingScope
{
    public static bool Source(bool ai, bool active, bool ghost) => ai && active && !ghost;

    public static bool Listener(bool authority, bool pmcAi, bool active, bool ending, bool ghost) =>
        authority && pmcAi && active && !ending && !ghost;
}

// Bounded ledgers: no Unity or hidden enemy state in the repeat policy.
public sealed class MovementHearingGate
{
    public const double MaxAge = 1;
    public const int Capacity = 4096;
    private readonly Dictionary<string, Entry> entries = new Dictionary<string, Entry>();
    private readonly List<string> expired = new List<string>();

    private readonly struct Entry
    {
        public readonly double At,
            Observed;
        public readonly bool Added;

        public Entry(double at, double observed, bool added)
        {
            At = at;
            Observed = observed;
            Added = added;
        }
    }

    public int Count => entries.Count;

    public static bool Fresh(double now, double emitted) =>
        Finite(now) && Finite(emitted) && emitted <= now && now - emitted <= MaxAge;

    private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

    public bool Allows(
        string key,
        double now,
        double emitted,
        double interval,
        bool added,
        out string reason
    )
    {
        reason = "movement-queued";
        if (!Fresh(now, emitted))
        {
            reason = "movement-expired";
            return false;
        }
        if (!Finite(interval) || interval < .05 || interval > 1)
        {
            reason = "movement-invalid";
            return false;
        }
        if (
            entries.TryGetValue(key, out var e)
            && (added || e.Added)
            && Math.Abs(emitted - e.At) < interval - .00001
        )
        {
            reason = "movement-duplicate";
            return false;
        }
        if (!entries.ContainsKey(key) && entries.Count >= Capacity)
        {
            Prune(now);
            if (entries.Count >= Capacity)
            {
                reason = "movement-capacity";
                return false;
            }
        }
        return true;
    }

    public void Record(string key, double now, double emitted, bool added)
    {
        if (entries.Count < Capacity || entries.ContainsKey(key))
            entries[key] = new Entry(emitted, now, added);
    }

    public void Prune(double now)
    {
        expired.Clear();
        foreach (var pair in entries)
            if (now - pair.Value.Observed > 2 || now < pair.Value.Observed)
                expired.Add(pair.Key);
        foreach (var key in expired)
            entries.Remove(key);
    }

    public void Clear()
    {
        entries.Clear();
        expired.Clear();
    }
}
