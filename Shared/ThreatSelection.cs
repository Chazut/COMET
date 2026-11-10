using System;
using System.Collections.Generic;
using System.Linq;

namespace Comet.Core;

// A07 consumes dated perception, never a live hidden transform, enemy health or faction.
public sealed class ThreatContact
{
    public string Id { get; set; } = "";
    public string Source { get; set; } = "";
    public bool Valid { get; set; }
    public bool Visible { get; set; }
    public bool LookingAtMe { get; set; }
    public bool Footsteps { get; set; }
    public bool Gunfire { get; set; }
    public double? ObservedAt { get; set; }
    public double? Distance { get; set; }
    public double? X { get; set; }
    public double? Y { get; set; }
    public double? Z { get; set; }
    public double? HitAt { get; set; }
    public double? ShotAt { get; set; }
}

public sealed class ThreatFrame
{
    public double Now { get; set; }
    public string Current { get; set; } = "";
    public string Trigger { get; set; } = "periodic";
    public bool IsAi { get; set; }
    public bool IsSupportedBot { get; set; }
    public bool IsAuthority { get; set; }
    public bool Active { get; set; }
    public bool Ghost { get; set; }
    public bool NativeUrgency { get; set; }
    public bool Truncated { get; set; }
    public List<ThreatContact> Contacts { get; } = new List<ThreatContact>();
}

public sealed class ThreatScore
{
    public string Target { get; set; } = "";
    public bool Eligible { get; set; }
    public string Reason { get; set; } = "";
    public string Source { get; set; } = "";
    public double? Age { get; set; }
    public double? Distance { get; set; }
    public double? X { get; set; }
    public double? Y { get; set; }
    public double? Z { get; set; }
    public bool Visible { get; set; }
    public bool Immediate { get; set; }
    public double Score { get; set; }
    public double Sight { get; set; }
    public double Hit { get; set; }
    public double Fire { get; set; }
    public double Proximity { get; set; }
    public double Facing { get; set; }
    public double Steps { get; set; }
    public double? HitAt { get; set; }
}

public sealed class ThreatTrace
{
    public double At { get; set; }
    public string Previous { get; set; } = "";
    public string Selected { get; set; } = "";
    public string Reason { get; set; } = "";
    public string Trigger { get; set; } = "";
    public double HoldRemaining { get; set; }
    public double Margin { get; set; }
    public bool Emergency { get; set; }
    public bool Truncated { get; set; }
    public List<ThreatScore> Candidates { get; set; } = new List<ThreatScore>();
}

public sealed class ThreatSelection
{
    public const int CandidateLimit = 32;
    private readonly CometConfig config;
    private string held = "";
    private double holdUntil;
    public ThreatSelection(CometConfig config) { this.config = config; }
    public void Reset() { held = ""; holdUntil = 0; }
    public static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
    private static bool Dated(double? value, double now) => value.HasValue && Finite(value.Value) && value.Value >= 0 && value.Value <= now;
    private static double Fade(double age, double duration) => Math.Max(0, 1 - age / duration);
    public static bool Scope(ThreatFrame f) => f.IsAi && f.IsSupportedBot && f.IsAuthority && f.Active && !f.Ghost && !f.NativeUrgency;

    public ThreatScore Score(ThreatContact e, double now)
    {
        var c = config.ThreatSelection;
        var s = new ThreatScore { Target = e.Id, Source = e.Source, Visible = e.Visible, Reason = "threat-no-perceived-position" };
        if (!e.Valid || string.IsNullOrEmpty(e.Id)) { s.Reason = "threat-invalid-contact"; return s; }
        bool source = e.Source == "seen" || e.Source == "heard" || e.Source == "shared-seen" || e.Source == "shared-heard";
        if (!source || !Dated(e.ObservedAt, now) || !e.Distance.HasValue || !Finite(e.Distance.Value) || e.Distance < 0
            || !e.X.HasValue || !e.Y.HasValue || !e.Z.HasValue || !Finite(e.X.Value) || !Finite(e.Y.Value) || !Finite(e.Z.Value)) return s;
        double age = now - e.ObservedAt!.Value;
        if (age > config.ContactLifetime) { s.Reason = "threat-stale-contact"; return s; }
        s.Eligible = true; s.Age = age; s.Distance = e.Distance; s.X = e.X; s.Y = e.Y; s.Z = e.Z;
        // Only a fresh personal visual observation counts as currently visible.
        s.Visible = e.Visible && e.Source == "seen" && age <= .5;
        double certainty = s.Visible ? 1 : (e.Source == "seen" ? .75 : e.Source == "heard" ? .5 : .35) * Fade(age, config.ContactLifetime);
        s.Sight = s.Visible ? 50 : 10 * certainty;
        s.Proximity = 30 * Math.Max(0, 1 - e.Distance.Value / 60) * certainty;
        if (s.Visible && e.Distance <= c.CloseDistance) s.Proximity += 25 * Math.Max(0, 1 - e.Distance.Value / c.CloseDistance);
        s.Facing = s.Visible && e.LookingAtMe ? 20 : 0;
        // A damage notification identifies an internal attacker, not a location. A visible observation or
        // personally heard gunshot close in time is needed to connect it to a usable threat position.
        bool Correlated(double? at) => Dated(at, now) && (s.Visible || (e.Source == "heard" && e.Gunfire
            && Math.Abs(e.ObservedAt.Value - at!.Value) <= .75));
        s.Hit = Correlated(e.HitAt) ? 100 * Fade(now - e.HitAt!.Value, c.HitMemory) : 0;
        s.Fire = Correlated(e.ShotAt) ? 60 * Fade(now - e.ShotAt!.Value, c.ShotMemory) : 0;
        s.HitAt = s.Hit > 0 ? e.HitAt : null;
        s.Steps = !s.Visible && e.Source == "heard" && e.Footsteps && age <= c.FootstepMemory && e.Distance <= c.FootstepDistance
            ? 25 * Fade(age, c.FootstepMemory) : 0;
        s.Immediate = s.Visible && e.Distance <= c.ImmediateDistance;
        s.Score = s.Sight + s.Proximity + s.Facing + s.Hit + s.Fire + s.Steps;
        s.Reason = s.Hit > 0 ? "threat-recent-attacker" : s.Immediate ? "threat-immediate-visible" : s.Fire > 0
            ? "threat-incoming-fire" : s.Steps > 0 ? "threat-nearby-footsteps" : s.Visible ? "threat-visible-contact" : "threat-known-contact";
        return s;
    }

    public ThreatTrace Evaluate(ThreatFrame f)
    {
        var c = config.ThreatSelection;
        var t = new ThreatTrace { At = f.Now, Previous = f.Current, Trigger = f.Trigger, Margin = c.SwitchMargin, Truncated = f.Truncated };
        if (!Finite(f.Now) || f.Now < 0 || !config.Enabled || !c.Enabled || !Scope(f))
        { Reset(); t.Reason = "threat-yield-to-sain"; return t; }
        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < Math.Min(f.Contacts.Count, CandidateLimit); i++)
            if (ids.Add(f.Contacts[i].Id)) t.Candidates.Add(Score(f.Contacts[i], f.Now));
        t.Truncated |= f.Contacts.Count > CandidateLimit;
        var eligible = t.Candidates.Where(x => x.Eligible).OrderByDescending(x => x.Score).ThenBy(x => x.Target, StringComparer.Ordinal).ToList();
        if (eligible.Count == 0) { Reset(); t.Reason = "threat-no-usable-contact"; return t; }
        var current = eligible.Find(x => x.Target == f.Current);
        var best = eligible[0];
        if (held != f.Current) { held = f.Current; holdUntil = f.Now + c.Commitment; }
        bool stronger(ThreatScore x) => current == null || x.Score > current.Score * (1 + c.SwitchMargin) + 1e-6;
        // Close visible danger may break a hold. A recent hit needs a real score advantage, so two
        // equal shooters trading hits cannot force a switch on every bullet.
        var emergency = eligible.Find(x => x.Target != f.Current && ((x.Immediate && current?.Immediate != true)
            || (x.Hit > 0 && current?.Immediate != true && current?.Hit <= 0 && stronger(x))));
        ThreatScore selected;
        if (current == null) { selected = best; t.Reason = f.Current.Length == 0 ? "threat-first-target" : "threat-current-invalid"; }
        else if (emergency != null) { selected = emergency; t.Emergency = true; t.Reason = emergency.Immediate ? "threat-immediate-visible" : "threat-recent-attacker"; }
        else if (best.Target == current.Target) { selected = current; t.Reason = "threat-kept-best"; }
        else if (f.Now < holdUntil) { selected = current; t.Reason = "threat-kept-commitment"; }
        else if (!stronger(best)) { selected = current; t.Reason = "threat-kept-margin"; }
        else if (t.Truncated) { selected = current; t.Reason = "threat-incomplete-scan"; }
        else { selected = best; t.Reason = "threat-better-target"; }
        if (selected.Target != held) { held = selected.Target; holdUntil = f.Now + c.Commitment; }
        t.Selected = selected.Target; t.HoldRemaining = Math.Max(0, holdUntil - f.Now);
        foreach (var candidate in eligible)
            if (candidate.Target != selected.Target) candidate.Reason = candidate.Target == best.Target && selected.Target == f.Current
                ? t.Reason : "threat-lower-priority";
        return t;
    }
}
