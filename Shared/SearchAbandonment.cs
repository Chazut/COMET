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

// Value snapshot for the native hook. Callers of the public frame API keep its reference semantics.
internal struct SearchContactSample
{
    internal string Target;
    internal double Silence, X, Y, Z, Distance;
    internal bool Visible, Visited;
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
    private SearchContactSample sample;
    private double requiredSilence;
    private string? state;
    private string reason = "";
    private bool requested;
    private SearchAbandonmentTrace? trace;

    internal string? CurrentState => trace?.State ?? state;

    public SearchAbandonmentTrace? Current => state == null ? null : trace ??= new SearchAbandonmentTrace
    {
        Target = sample.Target, Silence = sample.Silence, RequiredSilence = requiredSilence,
        X = sample.X, Y = sample.Y, Z = sample.Z, Distance = sample.Distance,
        PlaceVisited = sample.Visited, State = state, Reason = reason, ForgetRequested = requested,
    };

    internal string CurrentTarget => trace?.Target ?? sample.Target;
    internal string CurrentReason => trace?.Reason ?? reason;
    public int RememberedCount => 0;

    public bool Remembers(string target) => false;

    public bool NeedsSafetyCheck(string target) => false;

    public void ForgetFailure(string target) { }

    public void Clear()
    {
        sample = default;
        state = null;
        trace = null;
    }

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
        EvaluateContact(new SearchContactSample
        {
            Target = f.Target, Silence = f.Now - f.Stamp, X = f.X, Y = f.Y, Z = f.Z,
            Visible = f.Visible, Visited = f.PersonalSearch, Distance = f.Distance,
        }, c, f.Scoped && f.NativeKnown);
        return Current!;
    }

    internal bool EvaluateContact(in SearchContactSample contact, SearchAbandonmentConfig c, bool inScope)
    {
        sample = contact;
        requiredSilence = c.Silence;
        trace = null; // Previously published traces remain independent snapshots.
        requested = false;
        if (!c.Enabled || !inScope)
        {
            state = "ended";
            reason = "search-check-out-of-scope";
        }
        else if (!contact.Visible && contact.Visited && contact.Silence >= c.Silence)
        {
            state = "requested";
            reason = "search-check-position-empty";
            requested = true;
        }
        else
        {
            state = "watching";
            reason = contact.Visible ? "search-check-visible"
                : !contact.Visited ? "search-place-not-visited" : "search-check-waiting-for-silence";
        }
        return requested;
    }

    public bool ObserveForgotten(
        SearchAbandonmentFrame f,
        SearchAbandonmentConfig c,
        bool nativeUnknown
    ) => ObserveForgotten(f.Target, nativeUnknown);

    internal bool ObserveForgotten(string target, bool nativeUnknown)
    {
        if (!nativeUnknown || !(trace?.ForgetRequested ?? requested) || state == null
            || (trace?.Target ?? sample.Target) != target)
            return false;
        Current!.State = "abandoned";
        Current.ForgetObserved = true;
        Current.ValidFor = 3;
        return true;
    }
}
