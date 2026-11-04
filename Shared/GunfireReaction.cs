using System;

namespace Comet.Core;

public sealed class GunfireConfig
{
    public bool Enabled { get; set; } = true;
    public double NearDistance { get; set; } = 100;
    public double InterestDistance { get; set; } = 250;
    public double LookDuration { get; set; } = 3;
    public double LookInterval { get; set; } = 8;
    public double SearchInterval { get; set; } = 60;
    public double IncomingMemory { get; set; } = 5;
    public bool AllowDistantSearch { get; set; } = true;
    public void Validate()
    {
        CometConfig.Range(NearDistance, 10, 300, nameof(NearDistance));
        CometConfig.Range(InterestDistance, NearDistance, 500, nameof(InterestDistance));
        CometConfig.Range(LookDuration, .5, 8, nameof(LookDuration));
        CometConfig.Range(LookInterval, Math.Max(3, LookDuration), 60, nameof(LookInterval));
        CometConfig.Range(SearchInterval, 15, 300, nameof(SearchInterval));
        CometConfig.Range(IncomingMemory, 1, 15, nameof(IncomingMemory));
    }
}

// Only supplied AFTER SAIN's native audibility check. Nothing in this policy creates hearing.
public sealed class GunfireFrame
{
    public double Now, EmittedAt, Distance;
    public string Source = "", Personality = "", Owner = "", Unsafe = "";
    public bool Scoped = true, Audible = true, Incoming, Engaged, Busy, CanLook = true, CanSearch = true, PersonalityChases;
}

public sealed class GunfireTrace
{
    public string State { get; set; } = "ignored";
    public string Reason { get; set; } = "gunfire-no-signal";
    public string Source { get; set; } = "";
    public string Personality { get; set; } = "";
    public string Owner { get; set; } = "";
    public double At { get; set; }
    public double EmittedAt { get; set; }
    public double Distance { get; set; }
    public bool Audible { get; set; }
    public bool Incoming { get; set; }
    public bool Engaged { get; set; }
    public bool PassToSain { get; set; }
    public bool SearchObserved { get; set; }
    public bool Applied { get; set; }
    public double Remaining { get; set; }
    public double ValidFor { get; set; }
    public double SearchCooldown { get; set; }
    public double LookCooldown { get; set; }
    public double? X { get; set; }
    public double? Y { get; set; }
    public double? Z { get; set; }
    public GunfireTrace Copy() => (GunfireTrace)MemberwiseClone();
}

public sealed class GunfireReaction
{
    private readonly GunfireConfig config;
    private double last = -1, nextLook, nextSearch, end;
    public GunfireTrace? Look { get; private set; }
    public GunfireReaction(GunfireConfig config) { this.config = config; }
    private static bool Finite(double x) => !double.IsNaN(x) && !double.IsInfinity(x);
    public static bool Recent(double now, double at, double window) => Finite(now) && Finite(at) && at > 0 && at <= now && now - at <= window;

    public GunfireTrace Evaluate(GunfireFrame f)
    {
        var t = new GunfireTrace { At = f.Now, EmittedAt = f.EmittedAt, Distance = f.Distance, Source = f.Source,
            Personality = f.Personality, Owner = f.Owner, Audible = f.Audible, Incoming = f.Incoming, Engaged = f.Engaged,
            LookCooldown = Math.Max(0, nextLook - f.Now), SearchCooldown = Math.Max(0, nextSearch - f.Now) };
        GunfireTrace Result(string state, string reason, bool pass = false) { t.State = state; t.Reason = reason; t.PassToSain = pass; return t; }
        if (!config.Enabled || !f.Scoped) return Result("native", "gunfire-out-of-scope", true);
        if (!Finite(f.Now) || !Finite(f.Distance) || f.Distance < 0 || !Finite(f.EmittedAt) || f.EmittedAt > f.Now)
            return Result("native", "gunfire-invalid-signal", true);
        if (last > f.Now) { Look = null; return Result("native", "gunfire-clock-reset", true); }
        last = f.Now;
        if (!f.Audible) return Result("ignored", "gunfire-inaudible");
        // Danger and a real ongoing engagement never lose native updates to distance/cooldown filters.
        if (f.Incoming || f.Engaged) return Result("native", f.Incoming ? "gunfire-directed-at-me" : "gunfire-engaged-contact", true);
        if (f.Now - f.EmittedAt > 2) return Result("ignored", "gunfire-stale-signal");
        if (f.Distance <= config.NearDistance) return Result("native", "gunfire-near-contact", true);
        if (f.Distance > config.InterestDistance) return Result("ignored", "gunfire-too-distant");
        if (f.Busy) return Result("ignored", f.Unsafe.Length > 0 ? f.Unsafe : "gunfire-priority");
        if (Look != null && f.Now < end) return Result("ignored", "gunfire-look-already-active");
        if (config.AllowDistantSearch && f.PersonalityChases && f.CanSearch && f.Now >= nextSearch)
        {
            nextSearch = f.Now + config.SearchInterval; t.SearchCooldown = config.SearchInterval;
            return Result("search", "gunfire-search-allowed", true);
        }
        if (!f.CanLook) return Result("ignored", "gunfire-steering-unavailable");
        if (f.Now < nextLook) return Result("ignored", "gunfire-look-cooldown");
        nextLook = f.Now + config.LookInterval; end = f.Now + config.LookDuration;
        t.LookCooldown = config.LookInterval; t.Remaining = config.LookDuration;
        t.Reason = !f.CanSearch && f.Owner == "ORBIT" ? "gunfire-orbit-keeps-objective" : "gunfire-look-at-sound";
        t.State = "look"; Look = t.Copy(); return t;
    }

    public void Locate(double x, double y, double z)
    {
        if (Look == null || !Finite(x) || !Finite(y) || !Finite(z)) throw new ArgumentException("No valid perceived gunfire position.");
        Look.X = x; Look.Y = y; Look.Z = z;
    }
    // The lease is absolute: more bullets and repeated updates cannot prolong it.
    public GunfireTrace? Tick(double now, string unsafeReason = "")
    {
        if (Look == null) return null;
        if (!Finite(now) || now < last) return Stop(now, "gunfire-clock-reset");
        last = now;
        if (unsafeReason.Length > 0) return Stop(now, unsafeReason);
        if (now >= end) return Stop(now, "gunfire-look-complete");
        Look.Remaining = end - now; Look.At = now;
        Look.ValidFor = Look.Applied ? Math.Min(1.5, Look.Remaining) : 0;
        return Look.Copy();
    }
    public GunfireTrace? Applied(double now)
    {
        if (Look == null || !Look.X.HasValue || !Look.Y.HasValue || !Look.Z.HasValue) return null;
        var t = Tick(now); if (Look == null) return t;
        Look.Applied = true; Look.ValidFor = Math.Min(1.5, Look.Remaining); return Look.Copy();
    }
    public GunfireTrace? Stop(double now, string reason)
    {
        if (Look == null) return null;
        var t = Look.Copy(); t.State = "ended"; t.Reason = reason; t.At = Finite(now) ? now : last;
        t.Remaining = t.ValidFor = 0; t.Applied = false; Look = null; return t;
    }
}

// Keep applied-look heartbeats independent of refused bullets: sustained fire must not
// consume every log slot and let RR's 1.5-second display lease expire during a real glance.
public sealed class GunfireLogThrottle
{
    private double ordinaryAt = double.NegativeInfinity, appliedAt = double.NegativeInfinity;
    private bool wasApplied;
    private int coalesced;
    public bool ShouldWrite(double now, GunfireTrace trace, bool force = false)
    {
        bool applied = trace.State == "look" && trace.Applied;
        double previous = applied ? appliedAt : ordinaryAt;
        if (!force && !(applied && !wasApplied) && now >= previous && now - previous < 1)
        { if (coalesced < int.MaxValue) coalesced++; return false; }
        if (applied) appliedAt = now; else ordinaryAt = now;
        if (trace.State == "look" || trace.State == "ended") wasApplied = applied;
        return true;
    }
    public int TakeCoalesced() { int count = coalesced; coalesced = 0; return count; }
}
