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
    public double KnownX, KnownY, KnownZ;
    public string ContactSource = "SAIN last-known";
    public bool LowAmmo, Hurt, Suppressed, InCover;
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

public sealed class HiddenEnemyDecision
{
    private readonly CometConfig config;
    private readonly Dictionary<Tactic, double> blockedUntil = new Dictionary<Tactic, double>();
    private Tactic? current;
    private string target = "";
    private double since;
    private readonly DecisionMixer mixer;
    private readonly SilentSearchReviews silentReviews = new();
    private string danger = "";
    private double selectionGap;
    private bool nativeRetry;
    private readonly OpportunityRecheck opportunityRecheck = new OpportunityRecheck();
    public Tactic? Current => current;
    public HiddenEnemyDecision(CometConfig config, string stream = "test", TacticalMistakes? mistakes = null)
    { config.Validate(); this.config = config; mixer = new DecisionMixer(config.DecisionVariety, stream, false, mistakes ?? new TacticalMistakes(config.TacticalMistakes, stream)); }

    public static string? Ineligible(Situation s, CometConfig c)
    {
        if (!c.Enabled) return "disabled";
        if (!s.IsAi || !s.IsSupportedBot) return "unsupported-bot";
        if (!s.IsAuthority) return "not-authority";
        if (!s.IsActive) return "inactive";
        if (s.Ghost) return "ghost";
        if (s.Visible) return "enemy-visible";
        if (!s.HasContact || s.Target.Length == 0) return "no-perceived-contact";
        if (!Finite(s.Now) || !Finite(s.ContactAge) || !Finite(s.KnownDistance) || s.KnownDistance < 0 || s.ContactAge < 0)
            return "invalid-contact";
        if (s.ContactAge > c.ContactLifetime) return "contact-expired";
        return null;
    }

    public Choice Evaluate(Situation s)
    {
        var rejected = Ineligible(s, config);
        if (rejected != null) return Release(rejected);
        bool targetChanged = target != s.Target;
        if (targetChanged) target = s.Target;
        // A12 observes execution across features; legacy timers remain a fallback when observation is unavailable/disabled.
        bool expired = s.Progress?.Enabled != true && current.HasValue && s.Now - since >= (current == Tactic.Hold ? config.HoldLimit : config.PlanLimit);
        if (expired) blockedUntil[current!.Value] = s.Now + config.RetryDelay;
        double aggression = config.Aggression.TryGetValue(s.Personality, out var a) ? a : .5;
        double freshness = Math.Max(0, 1 - s.ContactAge / config.ContactLifetime);
        var scores = new Dictionary<Tactic, double>
        {
            [Tactic.Push] = 15 + 60 * aggression + 25 * freshness - (s.Hurt ? 45 : 0) - (s.Suppressed ? 25 : 0),
            [Tactic.Grenade] = 55 + 20 * freshness + (s.InCover ? 10 : 0),
            [Tactic.Hold] = 20 + 40 * (1 - aggression) + (s.InCover ? 20 : -10) + (s.Suppressed ? 10 : 0),
            [Tactic.Flank] = 40 + 20 * aggression + (s.Suppressed ? 20 : 0),
            [Tactic.Search] = 25 + 40 * (1 - freshness) + 15 * aggression,
            [Tactic.FallBack] = 10 + 30 * (1 - aggression) + (s.Hurt ? 55 : 0) + (s.LowAmmo ? 60 : 0) + (s.Suppressed ? 20 : 0)
        };
        var candidates = new List<Candidate>();
        foreach (Tactic tactic in Enum.GetValues(typeof(Tactic)))
        {
            string reason = "available";
            if (config.Weights[tactic.ToString()] == 0) reason = "disabled-by-config";
            else if (blockedUntil.TryGetValue(tactic, out var until) && until > s.Now) reason = "retry-cooldown";
            else if (s.Unavailable.TryGetValue(tactic, out var why)) reason = why;
            else if ((tactic == Tactic.Push || tactic == Tactic.Flank || tactic == Tactic.Search)
                && (targetChanged || current != tactic)
                && config.ContactLifetime - s.ContactAge < Math.Min(1, config.Commitment)) reason = "contact-window-too-short-to-start";
            else if (tactic == Tactic.Push && s.KnownDistance > config.PushMaxDistance) reason = "push-too-far";
            else if ((tactic == Tactic.Push || tactic == Tactic.Flank || tactic == Tactic.Search) && s.LowAmmo) reason = "low-ammo";
            else if (tactic == Tactic.Push && (s.Hurt || s.Suppressed)) reason = "unsafe-push";
            else if (tactic == Tactic.Grenade && s.ContactAge > config.GrenadeContactLifetime) reason = "grenade-contact-stale";
            candidates.Add(new Candidate { Decision = tactic.ToString(), Eligible = reason == "available", Reason = reason,
                Score = Math.Round(Math.Max(0, scores[tactic]) * config.Weights[tactic.ToString()], 2) });
        }
        var search = candidates.Find(c => c.Decision == "Search");
        if (config.SilentSearch.Enabled && s.SilentSearch?.Eligible == true && search?.Eligible == true)
        { search.SilentSearchDelta = s.SilentSearch.Bonus * config.Weights["Search"]; search.Score += search.SilentSearchDelta; }
        EncounterScoring.Adjust(candidates,s,config);
        OutcomeScoring.Adjust(candidates,s,config);
        LoadoutModel.Adjust(candidates, s, config.Loadout);
        OpponentAssessment.Adjust(candidates, s, config.Opponent);
        OpportunityModel.Adjust(candidates, s, config.Opportunity);
        string opportunityTrigger = opportunityRecheck.Update(s.Opportunity);
        FearModel.Adjust(candidates, s, config.Fear);
        s.Progress?.Adjust(candidates, s.Now);
        bool panic = config.Fear.Enabled && s.Fear?.Panic == true;
        var best = candidates.Where(c => c.Eligible).OrderByDescending(c => c.Score).FirstOrDefault();
        var previous = candidates.FirstOrDefault(c => c.Eligible && c.Decision == current?.ToString());
        string nextDanger = s.Hurt + "/" + s.LowAmmo + "/" + s.Suppressed + "/" + panic + "/" + (config.Loadout.Enabled ? s.Loadout?.PlanKey : "");
        bool dangerChanged = current.HasValue && nextDanger != danger;
        string selectionReason = expired ? "plan-limit" : targetChanged ? "new-contact" : current.HasValue ? "score-improved" : "new-plan";
        bool silentReview = config.SilentSearch.Enabled && s.SilentSearch != null && search?.Eligible == true && !panic && !s.Hurt && !s.Suppressed && !s.LowAmmo
            && (!current.HasValue || s.Now-since >= config.Commitment) && silentReviews.TryReview(s.SilentSearch);
        if(s.SilentSearch != null) s.SilentSearch.Reconsider = silentReview;
        bool kept = false;
        if (previous != null && best != null && !expired && !targetChanged && !dangerChanged && opportunityTrigger.Length == 0 && !silentReview && !nativeRetry && (!panic || previous == best))
        {
            if (s.Now - since < config.Commitment) { kept = true; selectionReason = "commitment"; }
            else if (best == previous || best.Score - previous.Score <= selectionGap + config.SwitchMargin)
            { kept = true; selectionReason = "hysteresis"; }
        }
        if (kept) { best = previous; mixer.Retain("kept-" + selectionReason); }
        else
        {
            selectionReason = dangerChanged ? "danger-changed" : previous == null && current.HasValue ? "previous-unavailable" : selectionReason;
            if (silentReview) selectionReason = "silent-search-delay-elapsed";
            if (!dangerChanged && opportunityTrigger.Length > 0) selectionReason = opportunityTrigger;
            double topScore = best?.Score ?? 0;
            if (panic) selectionReason = "panic-best-feasible";
            best = mixer.Select(candidates, s.Target, s.Personality, selectionReason, panic || s.Hurt || s.LowAmmo || s.Suppressed, nativeRetry, s.Now);
            selectionGap = Math.Max(0, topScore - (best?.Score ?? 0));
            nativeRetry = false;
        }
        var next = best == null ? (Tactic?)null : (Tactic)Enum.Parse(typeof(Tactic), best.Decision);
        bool changed = current != next || targetChanged || (!kept && next.HasValue);
        // Switching between hidden enemies must not reset an uninterrupted hold/run forever.
        if (current != next) since = s.Now;
        current = next; danger = nextDanger;
        return new Choice { Decision = next, Changed = changed, Candidates = candidates, Mixing = mixer.Last,
            Reason = next == null ? "no-feasible-option" : selectionReason };
    }

    public Choice Fail(Tactic tactic, double now, string reason)
    {
        blockedUntil[tactic] = now + config.RetryDelay;
        nativeRetry = true;
        return Release(reason);
    }
    public Choice Release(string reason)
    {
        bool changed = current.HasValue;
        current = null;
        return new Choice { Changed = changed, Reason = reason };
    }
    private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
}
