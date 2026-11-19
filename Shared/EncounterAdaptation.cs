using System;
using System.Collections.Generic;
using System.Linq;

namespace Comet.Core;

public sealed class EncounterConfig
{
    public bool Enabled { get; set; } = true;
    public int Appearances { get; set; } = 3;
    public double Window { get; set; } = 20;
    public double PositionTolerance { get; set; } = 2;
    public double HeightTolerance { get; set; } = 1;
    public double SightConfirmation { get; set; } = .2;
    public double SightLoss { get; set; } = .5;
    public double ScoreCap { get; set; } = 15;
    public double Retention { get; set; } = 30;

    public void Validate()
    {
        CometConfig.Range(Appearances, 2, 10, nameof(Appearances));
        CometConfig.Range(Window, 3, 60, nameof(Window));
        CometConfig.Range(PositionTolerance, .5, 3, nameof(PositionTolerance));
        CometConfig.Range(HeightTolerance, .25, 1.5, nameof(HeightTolerance));
        CometConfig.Range(SightConfirmation, .1, 1, nameof(SightConfirmation));
        CometConfig.Range(SightLoss, .2, 3, nameof(SightLoss));
        CometConfig.Range(ScoreCap, 0, 25, nameof(ScoreCap));
        CometConfig.Range(Retention, 3, 60, nameof(Retention));
    }
}

public sealed class EncounterFrame
{
    public double Now,
        ContactAge,
        ContactLifetime = 25;
    public string Target = "";
    public int Instance,
        Surface,
        Side;
    public bool Scoped = true,
        HasContact = true,
        Visible,
        PersonalLatest = true,
        Geometry,
        Watching = true;

    // Only populated from personal visible evidence and local geometry. Never a hidden transform.
    public DoorPoint Seen,
        Observer,
        Edge,
        Normal;
}

public sealed class EncounterTrace
{
    public string State { get; set; } = "inactive";
    public string Reason { get; set; } = "encounter-no-observations";
    public string Target { get; set; } = "";
    public int Instance { get; set; }
    public int Count { get; set; }
    public int Required { get; set; }
    public double[] Observations { get; set; } = Array.Empty<double>();
    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }
    public double Age { get; set; }
    public double Remaining { get; set; }
    public double ValidFor { get; set; }
    public double Bonus { get; set; }
    public double AppliedBonus { get; set; }
    public bool Eligible { get; set; }
    public string Choice { get; set; } = "";
    public string HoldRefusal { get; set; } = "";
    public string[] Alternatives { get; set; } = Array.Empty<string>();

    public EncounterTrace Copy()
    {
        var t = (EncounterTrace)MemberwiseClone();
        t.Observations = (double[])Observations.Clone();
        t.Alternatives = (string[])Alternatives.Clone();
        return t;
    }
}

// Per observer, value-only, at most eight contacts. No persistence or squad propagation.
public sealed class EncounterMemory
{
    private sealed class Track
    {
        internal int Instance,
            Surface,
            Side;
        internal DoorPoint Anchor,
            Observer,
            Edge,
            Normal;
        internal readonly List<double> Episodes = new();
        internal double Last = -1,
            LastSeen = -1,
            VisibleSince = -1,
            HiddenSince = -1;
        internal bool WasVisible,
            Counted,
            Armed = true;
    }

    private readonly Dictionary<string, Track> tracks = new();
    public int Count => tracks.Count;

    public void Clear() => tracks.Clear();

    public void Retain(ISet<string> known)
    {
        foreach (var key in tracks.Keys.ToArray())
            if (!known.Contains(key))
                tracks.Remove(key);
    }

    public void Forget(string target) => tracks.Remove(target);

    public static bool Near(DoorPoint a, DoorPoint b, double horizontal, double vertical)
    {
        var d = a - b;
        return a.Finite
            && b.Finite
            && d.X * d.X + d.Z * d.Z <= horizontal * horizontal
            && Math.Abs(d.Y) <= vertical;
    }

    public EncounterTrace Sample(EncounterFrame f, EncounterConfig c)
    {
        var t = new EncounterTrace
        {
            Target = f.Target,
            Instance = f.Instance,
            Required = c.Appearances,
        };
        if (!c.Enabled || !f.Scoped)
        {
            Clear();
            t.Reason = "encounter-out-of-scope";
            return t;
        }
        if (f.Target.Length == 0)
            return t;
        if (
            !DoorPolicy.Finite(f.Now)
            || !DoorPolicy.Finite(f.ContactAge)
            || f.ContactAge < 0
            || f.ContactAge >= f.ContactLifetime
            || !DoorPolicy.Finite(f.ContactLifetime)
            || f.ContactLifetime <= 0
            || !f.Observer.Finite
            || !f.HasContact
        )
        {
            Forget(f.Target);
            t.State = "ended";
            t.Reason = "encounter-native-contact-ended";
            return t;
        }
        foreach (var key in tracks.Keys.ToArray())
            if (f.Now - tracks[key].LastSeen >= c.Retention)
                tracks.Remove(key);
        if (tracks.TryGetValue(f.Target, out var r) && r.Instance != f.Instance)
        {
            Forget(f.Target);
            r = null;
        }
        if (r == null)
        {
            if (!f.Visible || tracks.Count >= 8)
            {
                t.Reason = "encounter-no-personal-sight-or-capacity";
                return t;
            }
            r = new Track { Instance = f.Instance, Observer = f.Observer };
            tracks[f.Target] = r;
        }
        bool vantage = !Near(r.Observer, f.Observer, 2, .75);
        bool contradicted =
            f.Visible
            && r.Episodes.Count > 0
            && (!f.Seen.Finite || !Near(r.Anchor, f.Seen, c.PositionTolerance, c.HeightTolerance));
        if (vantage || contradicted || f.Now < r.Last)
        {
            r = new Track { Instance = f.Instance, Observer = f.Observer };
            tracks[f.Target] = r;
        }
        // A missed sample/target switch is not evidence that the enemy hid. Require a real new loss cycle.
        if (r.Last >= 0 && f.Now - r.Last > .35)
        {
            r.VisibleSince = r.HiddenSince = -1;
            r.Armed = false;
            r.Counted = true;
        }
        r.Last = f.Now;
        r.Episodes.RemoveAll(x => f.Now - x > c.Window);
        if (!f.Watching)
        {
            r.VisibleSince = r.HiddenSince = -1;
            r.Armed = false;
            r.Counted = true;
            r.WasVisible = false;
            t.Reason = "encounter-observer-looking-away";
            return t;
        }
        if (f.Visible)
        {
            if (!f.Seen.Finite)
            {
                Forget(f.Target);
                t.State = "ended";
                t.Reason = "encounter-invalid-sight";
                return t;
            }
            r.LastSeen = f.Now;
            if (!r.WasVisible)
            {
                if (r.HiddenSince >= 0 && f.Now - r.HiddenSince >= c.SightLoss)
                {
                    r.Armed = true;
                    r.Counted = false;
                }
                r.VisibleSince = f.Now;
            }
            if (r.VisibleSince < 0)
                r.VisibleSince = f.Now;
            r.HiddenSince = -1;
            if (
                f.Geometry
                && f.Surface != 0
                && f.Edge.Finite
                && f.Normal.Finite
                && f.Now - r.VisibleSince >= c.SightConfirmation
            )
            {
                bool same =
                    r.Episodes.Count == 0
                    || (
                        r.Surface == f.Surface
                        && r.Side == f.Side
                        && Near(r.Edge, f.Edge, c.PositionTolerance, c.HeightTolerance)
                        && r.Normal.Dot(f.Normal) > .85
                    );
                if (!same)
                {
                    r.Episodes.Clear();
                    r.Armed = false;
                    r.Counted = true;
                }
                if (r.Armed && !r.Counted)
                {
                    if (r.Episodes.Count == 0)
                    {
                        r.Anchor = f.Seen;
                        r.Observer = f.Observer;
                        r.Edge = f.Edge;
                        r.Normal = f.Normal;
                        r.Surface = f.Surface;
                        r.Side = f.Side;
                    }
                    if (r.Episodes.Count == 10)
                        r.Episodes.RemoveAt(0);
                    r.Episodes.Add(f.Now);
                    r.Counted = true;
                    r.Armed = false;
                }
            }
            r.WasVisible = true;
        }
        else
        {
            if (r.WasVisible || r.HiddenSince < 0)
                r.HiddenSince = f.Now;
            r.VisibleSince = -1;
            r.WasVisible = false;
        }
        t.Count = r.Episodes.Count;
        t.Observations = r.Episodes.ToArray();
        t.X = r.Anchor.X;
        t.Y = r.Anchor.Y;
        t.Z = r.Anchor.Z;
        t.Age = Math.Max(0, f.Now - r.LastSeen);
        t.Remaining = Math.Max(0, Math.Min(c.Retention - t.Age, f.ContactLifetime - f.ContactAge));
        if (r.Episodes.Count > 0)
            t.Remaining = Math.Min(t.Remaining, Math.Max(0, c.Window - (f.Now - r.Episodes[0])));
        t.ValidFor = Math.Min(.35, t.Remaining);
        bool latest =
            f.PersonalLatest && Near(r.Anchor, f.Seen, c.PositionTolerance, c.HeightTolerance);
        bool sameOpening =
            f.Geometry
            && f.Surface == r.Surface
            && f.Side == r.Side
            && Near(r.Edge, f.Edge, c.PositionTolerance, c.HeightTolerance)
            && r.Normal.Dot(f.Normal) > .85;
        t.Eligible =
            !f.Visible && latest && sameOpening && t.Count >= c.Appearances && t.Remaining > 0;
        t.Bonus = t.Eligible ? c.ScoreCap : 0;
        t.State =
            t.Eligible ? "ready"
            : t.Count > 0 ? "observing"
            : "inactive";
        t.Reason =
            t.Eligible ? "encounter-repeated-opening"
            : f.Visible ? "encounter-visible-native-response"
            : !latest ? "encounter-latest-contact-differs"
            : !sameOpening ? "encounter-opening-unverified"
            : "encounter-insufficient-returns";
        return t;
    }
}
