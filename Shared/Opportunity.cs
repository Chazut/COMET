using System;
using System.Collections.Generic;
using System.Linq;

namespace Comet.Core;

public sealed class OpportunityTrace
{
    public string Target { get; set; } = "";
    public long Episode { get; set; }
    public string Cue { get; set; } = "";
    public string State { get; set; } = "unknown";
    public string Reason { get; set; } = "no-personal-opportunity";
    public double At { get; set; }
    public double HeardAt { get; set; }
    public double Age { get; set; }
    public double Remaining { get; set; }
    public double Window { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }
    public double? PathLength { get; set; }
    public double? Eta { get; set; }
    public double? EstimatedSpeed { get; set; }
    public double? RouteRisk { get; set; }
    public int? KnownThreats { get; set; }
    public string TravelMode { get; set; } = "not-assessed";
    public double Bonus { get; set; }
    public double ScoreCap { get; set; }
    public double SafetyMargin { get; set; }
    public OpportunityTrace Copy() => (OpportunityTrace)MemberwiseClone();
}

// Scalar, per-observer knowledge only. Repeated sounds retain the FIRST timestamp and position.
public sealed class OpportunityMemory
{
    private sealed class Entry
    {
        public int Instance;
        public double LastSound;
        public bool Interrupted;
        public OpportunityTrace Trace = new OpportunityTrace();
    }
    private readonly Dictionary<string, Entry> entries = new Dictionary<string, Entry>();
    private long sequence;
    public int Count => entries.Count;
    public void Clear() => entries.Clear();
    public bool Hear(string target, int instance, double now, string cue, bool personal, double x, double y, double z, OpportunityConfig c)
    {
        if (!c.Enabled || !personal || string.IsNullOrEmpty(target) || !Finite(now) || now < 0 || !Finite(x) || !Finite(y) || !Finite(z)) return false;
        bool cancel = cue == "shot" || cue == "sprint" || cue == "grenade";
        double window = c.Window(cue);
        if (!cancel && window <= 0) return false; // Steps never reset the clock.
        if (entries.TryGetValue(target, out var old) && old.Instance != instance) { entries.Remove(target); old = null; }
        if (old != null && now < old.LastSound) return false;
        if (cancel)
        {
            if (old == null) return false;
            old.LastSound = now;
            if (old.Interrupted) return false;
            old.Interrupted = true; old.Trace.State = "interrupted"; old.Trace.Reason = "heard-" + cue;
            return true;
        }
        if (old != null && !old.Interrupted && now - old.LastSound < c.RearmSilence)
        {
            old.LastSound = now;
            // A distinct perceived action is a new episode; repeats of the same action never extend it.
            if (old.Trace.Cue == cue) return false;
        }
        if (entries.Count >= 32 && !entries.ContainsKey(target)) entries.Remove(entries.OrderBy(p => p.Value.LastSound).First().Key);
        entries[target] = new Entry { Instance = instance, LastSound = now, Trace = new OpportunityTrace {
            Target = target, Episode = ++sequence, Cue = cue, HeardAt = now, Window = window, X = x, Y = y, Z = z } };
        return true;
    }
    public OpportunityTrace Snapshot(string target, int instance, double now, OpportunityConfig c)
    {
        var none = new OpportunityTrace { Target = target, At = now, ScoreCap = c.ScoreCap, SafetyMargin = c.SafetyMargin };
        if (!entries.TryGetValue(target, out var e)) return none;
        if (e.Instance != instance) { entries.Remove(target); return none; }
        var t = e.Trace.Copy(); t.At = now; t.Age = now - t.HeardAt; t.ScoreCap = c.ScoreCap; t.SafetyMargin = c.SafetyMargin;
        t.Remaining = Math.Max(0, t.Window - t.Age);
        if (!c.Enabled) { t.State = "disabled"; t.Reason = "opportunity-disabled"; }
        else if (!Finite(now) || !Finite(t.Age) || t.Age < 0) { t.State = "rejected"; t.Reason = "invalid-opportunity-time"; }
        else if (e.Interrupted) { /* Keep the perceived interruption. */ }
        else if (t.Remaining <= c.SafetyMargin) { t.State = "expired"; t.Reason = "opportunity-expired"; }
        else if (t.Age < c.ReactionDelay) { t.State = "waiting"; t.Reason = "opportunity-reaction-delay"; }
        else { t.State = "assess"; t.Reason = "opportunity-needs-route"; }
        return t;
    }
    internal static bool Finite(double x) => !double.IsNaN(x) && !double.IsInfinity(x);
}

public sealed class OpportunityTravel
{
    public bool Ready, Complete, WeaponReady, ThreatsTruncated, CanSprint, DoorBusy, Visible, UnderFire;
    public int ThreatCount, Corners;
    public double PathLength, Risk, RiskLimit, WalkSpeed, SprintSpeed, Stamina, PositionError, VerticalTravel;
}

public static class OpportunityModel
{
    public static bool CanContinueSight(OpportunityTrace previous, OpportunityTrace current) => current.State == "assess"
        && previous.Target == current.Target && previous.Episode == current.Episode && previous.Bonus > 0;
    public static void Assess(OpportunityTrace t, Situation s, OpportunityTravel v, OpportunityConfig c)
    {
        if (t.State != "assess") return;
        void Reject(string why) { t.State = "rejected"; t.Reason = why; }
        if (!c.Enabled || !s.IsAi || !s.IsSupportedBot || !s.IsAuthority || !s.IsActive || s.Ghost || !s.HasContact || t.Target != s.Target)
        { Reject("opportunity-ineligible-observer"); return; }
        if (!v.Ready) { Reject("opportunity-probe-pending"); return; }
        t.KnownThreats = v.ThreatCount;
        if (!OpportunityMemory.Finite(v.PathLength) || v.PathLength < 0 || !OpportunityMemory.Finite(v.Risk) || v.Risk < 0 || v.Risk > 1
            || !OpportunityMemory.Finite(v.RiskLimit) || v.RiskLimit < 0 || v.RiskLimit > 1 || !OpportunityMemory.Finite(v.PositionError)
            || v.PositionError < 0 || !OpportunityMemory.Finite(v.VerticalTravel) || v.VerticalTravel < 0 || v.Corners < 2)
        { Reject("opportunity-invalid-route"); return; }
        t.PathLength = v.PathLength; t.RouteRisk = v.Risk;
        if (!v.Complete) { Reject("opportunity-no-complete-path"); return; }
        if (v.PositionError > 3) { Reject("opportunity-contact-moved"); return; }
        if (v.PathLength > c.MaxPath) { Reject("opportunity-path-too-long"); return; }
        if (!v.WeaponReady || s.LowAmmo || s.Hurt || s.Suppressed || v.UnderFire || s.Fear?.Panic == true) { Reject("opportunity-own-safety"); return; }
        if (v.ThreatsTruncated || v.ThreatCount != 1) { Reject("opportunity-other-known-threats"); return; }
        if (v.Risk > v.RiskLimit) { Reject("opportunity-route-exposed"); return; }
        if (v.DoorBusy) { Reject("opportunity-door-busy"); return; }
        if (!OpportunityMemory.Finite(v.WalkSpeed) || v.WalkSpeed <= 0 || !OpportunityMemory.Finite(v.SprintSpeed) || v.SprintSpeed < 0
            || !OpportunityMemory.Finite(v.Stamina) || v.Stamina < 0 || v.Stamina > 1)
        { Reject("opportunity-mobility-unknown"); return; }
        // Conservative upper bounds, NOT a replacement movement controller. Visible rushes use SAIN's dogfight walk.
        double walk = Math.Min(3, v.WalkSpeed) * .8;
        bool sprint = v.CanSprint && !v.Visible && v.Stamina >= .4 && v.PathLength > 3 && v.SprintSpeed > walk;
        double speed = sprint ? Math.Min(6, v.SprintSpeed) * .75 : walk;
        // Reserve stamina; never assume an entire route can be sprinted. Extra corners/stairs cost time.
        double sprintMetres = sprint ? Math.Min(v.PathLength, (v.Stamina - .3) * 20) : 0;
        double eta = sprintMetres / speed + (v.PathLength - sprintMetres) / walk + Math.Max(0, v.Corners - 2) * .3 + v.VerticalTravel * .25 + .25;
        t.EstimatedSpeed = speed; t.TravelMode = sprint ? "limited-sprint-and-walk" : "walk"; t.Eta = eta;
        if (eta + c.SafetyMargin >= t.Remaining) { Reject("opportunity-too-late"); return; }
        t.State = "usable"; t.Reason = "opportunity-arrival-before-estimated-end";
    }
    public static void Adjust(List<Candidate> choices, Situation s, OpportunityConfig c)
    {
        foreach (var a in choices) { a.ScoreBeforeOpportunity = a.Score; a.OpportunityDelta = 0; a.OpportunityReason = "opportunity-neutral"; }
        var t = s.Opportunity;
        if (t == null) return;
        t.Bonus = 0;
        if (!c.Enabled || t.State != "usable" || t.Target != s.Target || !s.IsAi || !s.IsSupportedBot || !s.IsActive || !s.IsAuthority || s.Ghost
            || !s.HasContact || s.LowAmmo || s.Hurt || s.Suppressed || s.Fear?.Panic == true
            || !OpponentMemory.ValidAge(s.Now - t.At, .5) || t.Eta == null || !OpportunityMemory.Finite(t.Eta.Value)
            || t.Eta < 0 || t.Window - (s.Now - t.HeardAt) <= t.Eta + c.SafetyMargin) return;
        var push = choices.FirstOrDefault(a => a.Decision == "Push");
        if (push == null || !push.Eligible) { t.State = "rejected"; t.Reason = "opportunity-push-unavailable"; return; }
        // Same treatment may also feed A09: bound their total positive Push influence.
        double bonus = Math.Max(0, Math.Min(c.ScoreCap, c.CombinedScoreCap - Math.Max(0, push.OpponentDelta)));
        push.OpportunityDelta = bonus; push.Score += bonus; t.Bonus = bonus;
        push.OpportunityReason = t.Reason;
    }
}

// Once per episode, not once per decision tick or each temporary path-budget refusal.
public sealed class OpportunityRecheck
{
    private readonly HashSet<string> used = new HashSet<string>();
    private readonly Queue<string> order = new Queue<string>();
    private readonly HashSet<string> closed = new HashSet<string>();
    private string active = "";
    public string Update(OpportunityTrace? t)
    {
        bool usable = t?.Bonus > 0;
        bool terminal = t == null || t.State == "expired" || t.State == "interrupted" || t.State == "disabled";
        string closeKey = active + (terminal ? ":terminal" : ":unavailable");
        string result = active.Length > 0 && !usable && closed.Add(closeKey) ? "opportunity-closed" : "";
        if (usable)
        {
            string key = t!.Target + ":" + t.Episode;
            if (used.Add(key)) {
                order.Enqueue(key);
                if (order.Count > 32) { var old = order.Dequeue(); used.Remove(old); closed.Remove(old + ":terminal"); closed.Remove(old + ":unavailable"); }
                result = "new-heard-opportunity";
            }
            active = key;
        }
        return result;
    }
}
