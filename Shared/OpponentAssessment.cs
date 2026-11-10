using System;
using System.Collections.Generic;
using System.Linq;

namespace Comet.Core;

// Detached evidence, never an enemy object, live transform, health value or inventory tree.
public sealed class OpponentEvidence
{
    public string Kind = "", Value = "", Source = "";
    public double ObservedAt, Age, Confidence;
    public bool Active;
    public OpponentEvidence Copy() => (OpponentEvidence)MemberwiseClone();
}
public sealed class OpponentTrace
{
    public string Target = "", State = "unknown", Sample = "memory-only";
    public double At, ScoreCap, Advantage, SampleMilliseconds;
    public List<OpponentEvidence> Evidence = new List<OpponentEvidence>();
    public OpponentTrace Copy() => new OpponentTrace { Target = Target, State = State, Sample = Sample, At = At,
        ScoreCap = ScoreCap, Advantage = Advantage, SampleMilliseconds = SampleMilliseconds, Evidence = Evidence.Select(x => x.Copy()).ToList() };
}
public sealed class OpponentObservation
{
    public bool Visible, PersonalSight;
    public double SightAge, VisibleFor, Distance;
    public double WeaponVisibleFor = -1, BodyVisibleFor = -1, HeadVisibleFor = -1;
    public string Weapon = "unknown", Body = "unknown", Head = "unknown";
}

public sealed class OpponentMemory
{
    private sealed class Entry
    {
        public int Instance;
        public double Used;
        public readonly Dictionary<string, OpponentEvidence> Evidence = new Dictionary<string, OpponentEvidence>();
    }
    private readonly Dictionary<string, Entry> contacts = new Dictionary<string, Entry>();
    public int Count => contacts.Count;
    public void Clear() => contacts.Clear();
    private Entry Get(string target, int instance, double now)
    {
        if (contacts.TryGetValue(target, out var e) && e.Instance == instance) { e.Used = now; return e; }
        if (contacts.Count >= 32 && !contacts.ContainsKey(target)) contacts.Remove(contacts.OrderBy(x => x.Value.Used).First().Key);
        return contacts[target] = new Entry { Instance = instance, Used = now };
    }
    public static bool CanObserve(OpponentObservation o, OpponentConfig c) => o.Visible && o.PersonalSight
        && ValidAge(o.SightAge, .5) && Finite(o.VisibleFor) && o.VisibleFor >= c.ObservationSeconds
        && Finite(o.Distance) && o.Distance >= 0 && o.Distance <= c.WeaponDistance;
    public void Observe(string target, int instance, double now, OpponentObservation o, OpponentConfig c, Action<OpponentObservation>? identify = null)
    {
        if (!c.Enabled || !Finite(now) || target.Length == 0 || !CanObserve(o, c)) return;
        identify?.Invoke(o); // Sensitive getters run only after the perception gate, including in the live adapter.
        var e = Get(target, instance, now);
        if (Duration(o.WeaponVisibleFor, c)) Set(e, "weapon", KnownWeapon(o.Weapon) ? o.Weapon : "unknown", "seen-hands", now, .8);
        if (o.Distance <= c.ProtectionDistance)
        {
            if (Duration(o.BodyVisibleFor, c)) Set(e, "body", Protection(o.Body), "seen-torso", now, .6);
            if (Duration(o.HeadVisibleFor, c)) Set(e, "head", Protection(o.Head), "seen-head", now, .6);
        }
    }
    // Callers must supply a perception-qualified event, not global damage/health notifications.
    public void InjuryCue(string target, int instance, double now, string cue, bool perceived)
    {
        if (!perceived || !Finite(now) || target.Length == 0 || (cue != "seen-impact" && cue != "heard-heal" && cue != "heard-surgery")) return;
        Set(Get(target, instance, now), "injury", "possibly-injured", cue, now, cue == "seen-impact" ? .45 : .35);
    }
    public OpponentTrace Snapshot(string target, int instance, double now, OpponentConfig c)
    {
        var t = new OpponentTrace { Target = target, At = now, ScoreCap = c.ScoreCap };
        if (!c.Enabled || !Finite(now) || target.Length == 0) return t;
        // A new instance of the same profile cannot inherit old observations.
        var e = Get(target, instance, now);
        foreach (var pair in e.Evidence)
        {
            var v = pair.Value.Copy(); v.Age = now - v.ObservedAt;
            double life = v.Kind == "weapon" ? c.WeaponMemory : v.Kind == "injury" ? c.InjuryMemory : c.ProtectionMemory;
            v.Confidence *= ValidAge(v.Age, life) ? Math.Max(0, 1 - v.Age / life) : 0;
            v.Active = v.Value != "unknown" && v.Confidence > 0;
            t.Evidence.Add(v);
        }
        t.State = t.Evidence.Any(v => v.Active) ? "estimated" : "unknown";
        return t;
    }
    private static void Set(Entry e, string kind, string value, string source, double at, double confidence)
    {
        if (e.Evidence.TryGetValue(kind, out var old) && old.ObservedAt > at) return;
        e.Evidence[kind] = new OpponentEvidence { Kind = kind, Value = value, Source = source, ObservedAt = at, Confidence = confidence };
    }
    public static bool KnownWeapon(string s) => s == "pistol" || s == "shotgun" || s == "smg" || s == "assaultRifle"
        || s == "assaultCarbine" || s == "machinegun" || s == "marksmanRifle" || s == "sniperRifle";
    private static string Protection(string s) => s == "visible-protection" || s == "none-visible" ? s : "unknown";
    private static bool Duration(double v, OpponentConfig c) => Finite(v) && v >= c.ObservationSeconds;
    internal static bool Finite(double v) => !double.IsNaN(v) && !double.IsInfinity(v);
    internal static bool ValidAge(double v, double max) => Finite(v) && v >= 0 && v <= max;
}

public static class OpponentAssessment
{
    public static void Adjust(List<Candidate> choices, Situation s, OpponentConfig c)
    {
        foreach (var a in choices) { a.ScoreBeforeOpponent = a.Score; a.OpponentDelta = 0; a.OpponentReason = "opponent-neutral"; }
        var t = s.Opponent;
        if (t != null) t.Advantage = 0;
        if (!c.Enabled || !s.IsAi || !s.IsSupportedBot || !s.IsAuthority || !s.IsActive || s.Ghost || !s.HasContact
            || t == null || t.Target != s.Target || t.State != "estimated" || !OpponentMemory.ValidAge(s.Now - t.At, .5)
            || !OpponentMemory.Finite(s.KnownDistance) || s.KnownDistance < 0) return;
        double advantage = 0;
        var reasons = new List<string>();
        bool ownKnown = s.Loadout?.State == "ready" && s.Loadout.Ready && OpponentMemory.ValidAge(s.Now - s.Loadout.At, .5);
        bool ownLonger = ownKnown && (s.Loadout!.WeaponClass == "assaultRifle" || s.Loadout.WeaponClass == "assaultCarbine"
            || s.Loadout.WeaponClass == "marksmanRifle" || s.Loadout.WeaponClass == "sniperRifle" || s.Loadout.WeaponClass == "machinegun");
        foreach (var e in t.Evidence)
        {
            double life = e.Kind == "weapon" ? c.WeaponMemory : e.Kind == "injury" ? c.InjuryMemory : c.ProtectionMemory;
            if (!e.Active || !OpponentMemory.Finite(e.Confidence) || e.Confidence <= 0 || e.Confidence > 1
                || !OpponentMemory.ValidAge(s.Now - e.ObservedAt, life)) continue;
            double v = 0;
            if (e.Kind == "injury" && e.Value == "possibly-injured") v = .25;
            else if (e.Kind == "body") v = e.Value == "none-visible" ? .15 : e.Value == "visible-protection" ? -.08 : 0;
            else if (e.Kind == "head") v = e.Value == "none-visible" ? .10 : e.Value == "visible-protection" ? -.04 : 0;
            else if (e.Kind == "weapon")
            {
                double near = Math.Max(0, 1 - s.KnownDistance / 20);
                if (e.Value == "shotgun") v = -.65 * near; // Ammunition is unknown: no assumed safe distance.
                else if (e.Value == "smg") v = -.25 * near;
                else if (e.Value == "pistol" && ownLonger) v = .25 * Math.Min(1, Math.Max(0, (s.KnownDistance - 10) / 20));
                else if (e.Value == "sniperRifle" && ownKnown && s.Loadout!.Cqb > .5) v = .4 * near;
            }
            if (v != 0) { advantage += v * e.Confidence; reasons.Add(e.Kind + ":" + e.Value); }
        }
        t.Advantage = Math.Max(-1, Math.Min(1, advantage));
        foreach (var a in choices)
        {
            if (!a.Eligible) { a.OpponentReason = "opponent-native-refusal-kept"; continue; }
            double factor = a.Decision == "Push" ? 1 : a.Decision == "Shoot" ? .35 : a.Decision == "Cover" || a.Decision == "FallBack" ? -.5 : a.Decision == "Hold" ? -.25 : 0;
            double delta = Math.Max(-c.ScoreCap, Math.Min(c.ScoreCap, t.Advantage * factor * c.ScoreCap));
            double rounded = Math.Round(Math.Max(0, a.Score + delta), 2);
            a.Score = a.ScoreBeforeOpponent + Math.Max(-c.ScoreCap, Math.Min(c.ScoreCap, rounded - a.ScoreBeforeOpponent));
            a.OpponentDelta = a.Score - a.ScoreBeforeOpponent;
            if (a.OpponentDelta != 0) a.OpponentReason = "perceived-advantage:" + string.Join(",", reasons);
        }
    }
}
