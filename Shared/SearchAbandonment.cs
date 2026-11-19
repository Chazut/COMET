using System;
using System.Collections.Generic;

namespace Comet.Core;

public sealed class SearchAbandonmentConfig
{
    public bool Enabled { get; set; } = true;
    public double Silence { get; set; } = 20;
    public double Inspection { get; set; } = 3;
    public double Radius { get; set; } = 3;
    public double FailedMemory { get; set; } = 30;

    public void Validate()
    {
        CometConfig.Range(Silence, 5, 120, nameof(Silence));
        CometConfig.Range(Inspection, 1, 15, nameof(Inspection));
        CometConfig.Range(Radius, 1, 5, nameof(Radius));
        CometConfig.Range(FailedMemory, 0, 120, nameof(FailedMemory));
    }
}

public sealed class SearchAbandonmentFrame
{
    public double Now,
        Stamp,
        X,
        Y,
        Z,
        Distance;
    public string Target = "";
    public int Instance;
    public bool Scoped = true,
        NativeKnown = true,
        Active = true,
        PersonalSearch = true,
        Visible,
        Unsafe,
        SameFloor = true,
        ClearView = true,
        Sampled = true,
        Facing = true;
}

public sealed class SearchAbandonmentTrace
{
    public string State { get; set; } = "inactive";
    public string Reason { get; set; } = "search-check-unavailable";
    public string Target { get; set; } = "";
    public double Silence { get; set; }
    public double RequiredSilence { get; set; }
    public double Inspected { get; set; }
    public double RequiredInspection { get; set; }
    public double Distance { get; set; }
    public double MemoryRemaining { get; set; }
    public double ValidFor { get; set; } = 1.5;
    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }
    public bool ForgetRequested { get; set; }
    public bool ForgetObserved { get; set; }
    public int Interruptions { get; set; }
    public bool PlaceVisited { get; set; }
    public string NativeSearchState { get; set; } = "";
    public string NativeSearchTarget { get; set; } = "";
    public bool NativeSearchActive { get; set; }
    public bool NativeSearchHasTarget { get; set; }
    public string EnemyPathStatus { get; set; } = "";
}

// Source EnemyKnownChecker: forget a visited place after 20 seconds without news.
public sealed class SearchAbandonment
{
    public SearchAbandonmentTrace? Current { get; private set; }
    public int RememberedCount => 0;

    public bool Remembers(string target) => false;

    public bool NeedsSafetyCheck(string target) => false;

    public void ForgetFailure(string target) { }

    public void Clear() => Current = null;

    public void Interrupt(string reason)
    {
        if (Current != null)
        {
            Current.State = "ended";
            Current.Reason = reason;
            Current.ValidFor = 0;
        }
    }

    public SearchAbandonmentTrace Evaluate(SearchAbandonmentFrame f, SearchAbandonmentConfig c)
    {
        var t = Current = new SearchAbandonmentTrace
        {
            Target = f.Target,
            RequiredSilence = c.Silence,
            Silence = f.Now - f.Stamp,
            X = f.X,
            Y = f.Y,
            Z = f.Z,
            Distance = f.Distance,
            PlaceVisited = f.PersonalSearch,
        };
        if (!c.Enabled || !f.Scoped || !f.NativeKnown)
        {
            t.State = "ended";
            t.Reason = "search-check-out-of-scope";
            return t;
        }
        if (!f.Visible && f.PersonalSearch && t.Silence >= c.Silence)
        {
            t.State = "requested";
            t.Reason = "search-check-position-empty";
            t.ForgetRequested = true;
        }
        else
        {
            t.State = "watching";
            t.Reason =
                f.Visible ? "search-check-visible"
                : !f.PersonalSearch ? "search-place-not-visited"
                : "search-check-waiting-for-silence";
        }
        return t;
    }

    public bool ObserveForgotten(
        SearchAbandonmentFrame f,
        SearchAbandonmentConfig c,
        bool nativeUnknown
    )
    {
        if (!nativeUnknown || Current?.ForgetRequested != true || Current.Target != f.Target)
            return false;
        Current.State = "abandoned";
        Current.ForgetObserved = true;
        Current.ValidFor = 3;
        return true;
    }
}
