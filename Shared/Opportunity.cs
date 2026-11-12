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
    public int? ImmediateThreats { get; set; }
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
        if (!c.Enabled || string.IsNullOrEmpty(target) || !Finite(now) || now < 0 || !Finite(x) || !Finite(y) || !Finite(z)) return false;
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
        else if (t.Remaining <= 0) { t.State = "expired"; t.Reason = "opportunity-expired"; }
        else { t.State = "assess"; t.Reason = "opportunity-needs-route"; }
        return t;
    }
    internal static bool Finite(double x) => !double.IsNaN(x) && !double.IsInfinity(x);
}

public sealed class OpportunityTravel
{
    public bool Ready, Complete, WeaponReady, ThreatsTruncated, CanSprint, DoorBusy, Visible, UnderFire;
    public int ThreatCount, ImmediateThreats, Corners;
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
        t.KnownThreats = v.ThreatCount;t.ImmediateThreats=v.ImmediateThreats;
        if (!OpportunityMemory.Finite(v.PathLength) || v.PathLength < 0 || !v.Complete)
        { Reject("opportunity-no-complete-path"); return; }
        t.PathLength=v.PathLength;t.RouteRisk=v.Risk;
        double nerve=s.Personality switch {"Wreckless"=>1.4,"GigaChad"=>1.3,"Chad"=>1.15,"Normal"=>1,_=>.85};
        double left=t.Window*nerve-Math.Max(0,t.Age);
        double eta=v.PathLength/5;
        bool close=v.PathLength<(t.Cue=="surgery"?20:10);
        t.EstimatedSpeed=5;t.TravelMode="zzap-sprint-estimate";t.Eta=eta;
        if(!close&&v.UnderFire){Reject("opportunity-under-fire");return;}
        if(!close&&v.ImmediateThreats>0){Reject("opportunity-other-visible-enemy");return;}
        if(!close&&v.PathLength>c.MaxPath){Reject("opportunity-path-too-long");return;}
        if(!close&&eta>left){Reject("opportunity-too-late");return;}
        t.State = "usable"; t.Reason = "opportunity-arrival-before-estimated-end";
    }
    public static void Adjust(List<Candidate> choices, Situation s, OpportunityConfig c)
    {
        // Source rush policy owns opportunity decisions. No additional score overlay.
        foreach(var a in choices){a.ScoreBeforeOpportunity=a.Score;a.OpportunityDelta=0;}
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
