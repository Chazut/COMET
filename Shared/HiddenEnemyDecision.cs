using System;
using System.Collections.Generic;
using System.Linq;

namespace Comet.Core;

// Captured on the Unity thread. Contains no live enemy transform, inventory or health.
public sealed class Situation
{
    public double Now;
    public string Target = "";
    public string Personality = "Normal";
    public bool IsAi, IsSupportedBot, IsAuthority, IsActive, Ghost, Visible, HasContact;
    public double ContactAge, KnownDistance;
    public double Health = 1, Ammo = 1, Weakness, PathLength, Numbers, VisibleMates, FreezeDistance = 45;
    public bool Busy, GrenadeOut, HoldsAngle, Coming, Indoor, CanHeal, OpenGround, Looking, UnderFire, Frozen;
    public double KnownX, KnownY, KnownZ;
    public string ContactSource = "SAIN last-known";
    public bool LowAmmo, Hurt, Suppressed, InCover, BrokeOffToCover;
    public SilentSearchTrace? SilentSearch;
    public OutcomeTrace? Outcome;
    public EncounterTrace? Encounter;
    public FearTrace? Fear;
    public LoadoutTrace? Loadout;
    public OpponentTrace? Opponent;
    public OpportunityTrace? Opportunity;
    public PlanProgress? Progress;
    public Dictionary<Tactic, string> Unavailable = new Dictionary<Tactic, string>();
}

public sealed class Candidate
{
    public double OutcomeDelta {get;set;}
    public double EncounterDelta { get; set; }
    public double SilentSearchDelta { get; set; }
    public double ScoreBeforeProgress { get; set; }
    public double ProgressDelta { get; set; }
    public string ProgressReason { get; set; } = "";
    public string Decision { get; set; } = "";
    public double Score { get; set; }
    public double ScoreBeforeFear { get; set; }
    public double FearDelta { get; set; }
    public double ScoreBeforeLoadout { get; set; }
    public double LoadoutDelta { get; set; }
    public string LoadoutReason { get; set; } = "";
    public double ScoreBeforeOpponent { get; set; }
    public double OpponentDelta { get; set; }
    public string OpponentReason { get; set; } = "";
    public double ScoreBeforeOpportunity { get; set; }
    public double OpportunityDelta { get; set; }
    public string OpportunityReason { get; set; } = "";
    public double ScoreBeforeCoverResponse { get; set; }
    public double CoverResponseDelta { get; set; }
    public string CoverResponseReason { get; set; } = "";
    public bool Eligible { get; set; }
    public string Reason { get; set; } = "";
}

public sealed class Choice
{
    public MixTrace? Mixing { get; set; }
    public Tactic? Decision { get; set; }
    public bool Changed { get; set; }
    public string Reason { get; set; } = "";
    public List<Candidate> Candidates { get; set; } = new List<Candidate>();
}

// HiddenEnemyUtility.Rank, with native eligibility supplied by the adapter.
public sealed class HiddenEnemyDecision
{
    private readonly CometConfig config;
    private readonly DecisionMixer mixer;
    private readonly Random random;
    private Tactic? current;
    private Tactic? beforeSelection;
    private string targetBeforeSelection = "";
    private double holdBeforeSelection;
    private string target = "";
    private double until, holdSince = -1, banKnownAt = -1;
    private List<Candidate> ranked = new();
    private List<Candidate> preference = new();
    public Tactic? Current => current;
    public HiddenEnemyDecision(CometConfig config, string stream = "test", TacticalMistakes? mistakes = null)
    { config.Validate(); this.config = config; random = new Random(StableSeed.Of(stream + "|hidden-time"));
      mixer = new DecisionMixer(config.DecisionVariety, stream, false, mistakes ?? new TacticalMistakes(config.TacticalMistakes, stream)); }
    public bool NeedsEvidence(Situation s) => ranked.Count == 0 || target != s.Target || s.Now >= until || s.UnderFire;
    public static string? Ineligible(Situation s, CometConfig c)
    {
        if (!c.Enabled) return "disabled";
        if (!s.IsAi || !s.IsSupportedBot) return "unsupported-bot";
        if (!s.IsAuthority) return "not-authority";
        if (!s.IsActive) return "inactive";
        if (s.Ghost) return "ghost";
        if (s.Visible) return "enemy-visible";
        if (!s.HasContact || s.Target.Length == 0) return "no-perceived-contact";
        if (!Finite(s.Now) || !Finite(s.ContactAge) || !Finite(s.KnownDistance) || s.KnownDistance < 0 || s.ContactAge < 0) return "invalid-contact";
        if (s.ContactAge > 60) return "contact-expired";
        return null;
    }
    public Choice Evaluate(Situation s, Func<Tactic, string>? canRun = null)
    {
        beforeSelection=current;targetBeforeSelection=target;holdBeforeSelection=holdSince;
        string? rejection = Ineligible(s, config);
        if (rejection != null) return Release(rejection);
        bool same = target == s.Target;
        if (!NeedsEvidence(s)) return SelectAvailable(s, "zzap-ranked-memory", canRun);
        double heldFor = holdSince >= 0 && (same || s.Frozen) ? s.Now - holdSince : 0;
        double knownAt = s.Now - s.ContactAge;
        if (!same || Math.Abs(banKnownAt - knownAt) >= .5) banKnownAt = -1;
        if (banKnownAt < 0 && heldFor >= 40) banKnownAt = knownAt;
        double aggression = config.Aggression.TryGetValue(s.Personality, out var a) ? a : .5;
        var scores = ZzapUtility.Hidden(s, aggression, heldFor, banKnownAt >= 0);
        var candidates = new List<Candidate>();
        foreach (Tactic tactic in Enum.GetValues(typeof(Tactic)))
        {
            // zzap caches preference scores, not temporary execution refusals.
            string reason = config.Weights[tactic.ToString()] == 0 ? "disabled-by-config" : "available";
            candidates.Add(new Candidate { Decision = tactic.ToString(), Eligible = reason == "available", Reason = reason,
                Score = Math.Round(scores[tactic] * config.Weights[tactic.ToString()], 2) });
        }
        var chosen = mixer.Select(candidates, s.Target, s.Personality, same ? "zzap-rescore" : "new-contact", now:s.Now);
        var previous = same ? candidates.Find(c => c.Eligible && c.Decision == preference.FirstOrDefault()?.Decision) : null;
        double best = candidates.Where(c => c.Eligible).Select(c => c.Score).DefaultIfEmpty(-10000).Max();
        if (previous != null && previous.Score >= best - (s.UnderFire ? 15 : 8))
        { chosen = previous; mixer.Retain("zzap-kept-pick", previous.Decision); }
        Tactic? next = chosen == null ? null : (Tactic)Enum.Parse(typeof(Tactic),chosen.Decision);
        ranked = candidates;
        preference = candidates.Where(c => c.Eligible).OrderByDescending(c => c.Score).ToList();
        if (chosen != null) { preference.Remove(chosen); preference.Insert(0, chosen); }
        until = s.Now + (next == Tactic.Hold ? 4 + random.NextDouble()*3 : 2.5 + random.NextDouble()*1.5);
        return SelectAvailable(s, "zzap-ranked-selection", canRun);
    }
    private Choice SelectAvailable(Situation s, string reason, Func<Tactic, string>? canRun = null)
    {
        foreach (var c in ranked)
        {
            var tactic = (Tactic)Enum.Parse(typeof(Tactic), c.Decision);
            c.Reason = config.Weights[c.Decision] == 0 ? "disabled-by-config"
                : s.Unavailable.TryGetValue(tactic, out var why) ? why : "available";
            c.Eligible = c.Reason == "available";
        }
        Candidate? chosen = null;
        bool holdSkipped = false;
        var top = preference.FirstOrDefault();
        foreach (var c in preference)
        {
            // TryUtility uses the preference order (including its deliberate mistakes),
            // then tests execution. Its 0.05/0.12 scores are x100 in the UI contract.
            if (c.Score <= 5) { c.Eligible = false; c.Reason = "zzap-score-too-low"; break; }
            if (!c.Eligible) continue;
            var tactic = (Tactic)Enum.Parse(typeof(Tactic), c.Decision);
            if ((tactic is Tactic.Hold or Tactic.FallBack) && c != top && c.Score < top!.Score - 12
                && !(tactic == Tactic.FallBack && holdSkipped))
            { c.Eligible = false; c.Reason = "zzap-fallback-not-preferred"; continue; }
            string refusal = canRun?.Invoke(tactic) ?? "";
            if (refusal.Length > 0)
            {
                c.Eligible = false; c.Reason = refusal;
                if (tactic == Tactic.Hold && refusal is "zzap-hold-under-fire" or "zzap-hold-other-enemy") holdSkipped = true;
                continue;
            }
            chosen = c;
            break;
        }
        Tactic? next = chosen == null ? null : (Tactic)Enum.Parse(typeof(Tactic), chosen.Decision);
        bool changed = current != next || target != s.Target;
        if (next == Tactic.Hold || s.Frozen)
        { if (holdSince < 0 || target != s.Target) holdSince = s.Now; }
        else holdSince = -1;
        current = next; target = s.Target;
        if (changed && reason == "zzap-ranked-memory") reason = "zzap-capability-refreshed";
        return new Choice { Decision = next, Changed = changed, Candidates = ranked,
            Mixing = mixer.Retain(reason, chosen?.Decision ?? ""), Reason = next == null ? "no-feasible-option" : reason };
    }
    // The adapter marks only this call's failed option unavailable and tries the next rank.
    public Choice Fail(Tactic tactic, double now, string reason) => new Choice { Decision=current, Reason=reason };
    public Choice Keep(Tactic tactic, string contact)
    {
        bool changed = beforeSelection != tactic || targetBeforeSelection != contact;
        if(tactic==Tactic.Hold&&beforeSelection==tactic)holdSince=holdBeforeSelection;
        current = tactic; target = contact;
        return new Choice { Decision=tactic, Changed=changed, Candidates=ranked, Reason="utilityKeep",
            Mixing=mixer.Retain("utilityKeep",tactic.ToString()) };
    }
    public Choice Release(string reason) { bool changed=current.HasValue; current=null; until=0; ranked.Clear(); preference.Clear(); return new Choice {Changed=changed,Reason=reason}; }
    private static bool Finite(double x) => !double.IsNaN(x) && !double.IsInfinity(x);
}

internal static class StableSeed
{
    internal static int Of(string s) { unchecked { uint h=2166136261; foreach(char c in s){h^=c;h*=16777619;} return (int)(h&0x7fffffff); } }
}
