using System;
using System.Collections.Generic;
using System.Linq;

namespace Comet.Core;

public enum VisibleTactic { Shoot, Cover, Push }

public sealed class VisibleSituation
{
    public Situation Contact = new Situation();
    public bool WeaponReady, CanShoot, CoverReachable, PushReachable, RecentHit, ProbeReady;
    public bool ThreatsTruncated;
    // Adapter evidence for the already executing action, never a substitute probe for new choices.
    public bool ContinuationSafe;
    public string ContinuationReason = "not-evaluated", ProbePendingReason = "";
    public string CoverId = "", CoverDestination = "", ThreatSignature = "", CoverReason = "no-reachable-cover", PushReason = "no-current-complete-path";
    public int ThreatCount;
    public double RecentHitAt;
    public double CurrentExposure, CoverExposure, CoverRouteRisk, PushRouteRisk, PushPathLength;
    public CoverTravel? CoverTravel;
    public CoverResponseTrace? CoverResponse;
}

public sealed class VisibleChoice
{
    public MixTrace? Mixing;
    public VisibleTactic? Decision;
    public bool Changed;
    public bool Reconsidered;
    public string Reason = "";
    public List<Candidate> Candidates = new List<Candidate>();
}

// Pure policy: all perception and physics arrive as a bounded, dated snapshot.
public sealed class VisibleEnemyDecision
{
    private readonly CometConfig config;
    private VisibleTactic? current;
    private string target = "", threats = "", cover = "", coverDestination = "";
    private double since;
    private bool hit;
    private double lastHit;
    private string danger = "";
    private readonly DecisionMixer mixer;
    private double selectionGap;
    private bool resuming;
    private bool nativeRetry;
    private string responsePlan = "";
    private readonly PlanContinuity pendingProbe = new();
    private List<Candidate> lastCandidates = new();
    private double responseChangedAt = double.NegativeInfinity;
    private readonly OpportunityRecheck opportunityRecheck = new OpportunityRecheck();
    public VisibleTactic? Current => current;
    public VisibleEnemyDecision(CometConfig config, string stream = "test", TacticalMistakes? mistakes = null)
    { config.Validate(); this.config = config; mixer = new DecisionMixer(config.DecisionVariety, stream, true, mistakes ?? new TacticalMistakes(config.TacticalMistakes, stream)); }
    // Native ownership is released by the adapter; only the checked plan survives A03.
    public void SuspendForSight() { resuming = current.HasValue; if (resuming) mixer.Retain("kept-sight-continuity"); }
    public MixTrace? Mixing => current.HasValue ? mixer.Last : null;
    public void NotifyNativeRefusal() { nativeRetry = true; }
    public static string? Ineligible(VisibleSituation v, CometConfig c)
    {
        var s = v.Contact;
        if (!c.Enabled || !c.VisibleCombat.Enabled) return "disabled";
        if (!s.IsAi || !s.IsSupportedBot) return "unsupported-bot";
        if (!s.IsAuthority) return "not-authority";
        if (!s.IsActive || s.Ghost) return "inactive-or-ghost";
        if (!s.Visible) return "enemy-not-visible";
        if (!s.HasContact || s.Target.Length == 0 || !Finite(s.Now) || !Finite(s.ContactAge) || s.ContactAge < 0
            || s.ContactAge > c.ContactLifetime || !Finite(s.KnownDistance) || s.KnownDistance < 0
            || !Finite(s.KnownX) || !Finite(s.KnownY) || !Finite(s.KnownZ)) return "invalid-contact";
        if (!v.WeaponReady) return "weapon-or-hands-busy-yield-to-sain";
        if (!v.ProbeReady) return "exposure-probe-pending";
        if (new[] { v.CurrentExposure, v.CoverExposure, v.CoverRouteRisk, v.PushRouteRisk }.Any(x => !Finite(x) || x < 0 || x > 1)
            || !Finite(v.PushPathLength) || v.PushPathLength < 0 || v.ThreatCount < 1) return "invalid-exposure-snapshot";
        return null;
    }
    public VisibleChoice Evaluate(VisibleSituation v)
    {
        var rejection = Ineligible(v, config);
        if (rejection == "exposure-probe-pending" && TryContinuePending(v))
        {
            mixer.Retain("kept-pending-exposure");
            return new VisibleChoice { Decision = current, Reason = "plan-kept-pending-exposure",
                Candidates = lastCandidates, Mixing = mixer.Last };
        }
        if (rejection != null) return Release(rejection);
        pendingProbe.Clear();
        var s = v.Contact; var c = config.VisibleCombat;
        double a = config.Aggression.TryGetValue(s.Personality, out var aggression) ? aggression : .5;
        var candidates = new List<Candidate>();
        void Add(VisibleTactic tactic, double score, double weight, string? refusal, string reason)
        {
            if (weight == 0) refusal = "disabled-by-config";
            candidates.Add(new Candidate { Decision = tactic.ToString(), Score = Math.Round(Math.Max(0, score) * weight, 2),
                Eligible = refusal == null, Reason = refusal ?? reason });
        }
        Add(VisibleTactic.Shoot, 65 + (s.InCover ? 20 : 0) - 45 * v.CurrentExposure - (s.Hurt ? 25 : 0) - (s.Suppressed ? 20 : 0),
            c.ShootWeight, !v.CanShoot ? "no-clear-shot-or-out-of-range" : null, s.InCover ? "shoot-from-cover" : "clear-shot");
        string? coverRefusal = !v.CoverReachable ? v.CoverReason : v.CoverExposure > .5 ? "cover-destination-exposed"
            : v.CoverRouteRisk > c.RiskTolerance ? "cover-route-exposed" : null;
        Add(VisibleTactic.Cover, 35 + 35 * (1 - a) + 45 * v.CurrentExposure + (s.Hurt ? 30 : 0)
            + (s.LowAmmo ? 15 : 0) + (s.Suppressed || v.RecentHit ? 25 : 0) - 40 * v.CoverRouteRisk - 30 * v.CoverExposure,
            c.CoverWeight, coverRefusal, s.Hurt ? "hurt-seek-cover" : s.LowAmmo ? "low-ammo-seek-cover" : v.RecentHit ? "hit-seek-cover" : "reachable-protected-cover");
        string? pushRefusal = !v.PushReachable ? v.PushReason : v.PushPathLength > c.MaxPushPath ? "push-path-too-long"
            : s.LowAmmo ? "low-ammo" : s.Hurt || s.Suppressed ? "unsafe-push"
            : v.ThreatsTruncated || v.ThreatCount > 1 ? "additional-known-threat" : v.PushRouteRisk > c.RiskTolerance ? "push-route-exposed" : null;
        Add(VisibleTactic.Push, 25 + 65 * a - 50 * v.PushRouteRisk - Math.Min(20, v.PushPathLength * .5), c.PushWeight, pushRefusal, "reachable-close-contact");
        OutcomeScoring.Adjust(candidates,s,config,v.RecentHit||v.ThreatCount>1||v.ThreatsTruncated);
        LoadoutModel.Adjust(candidates, s, config.Loadout);
        OpponentAssessment.Adjust(candidates, s, config.Opponent);
        OpportunityModel.Adjust(candidates, s, config.Opportunity);
        CoverResponseModel.Adjust(candidates, v, config);
        string opportunityTrigger = opportunityRecheck.Update(s.Opportunity);
        FearModel.Adjust(candidates, s, config.Fear);
        s.Progress?.Adjust(candidates, s.Now);
        string nextResponsePlan = v.CoverResponse?.State is "quick-cover" or "return-fire"
            ? v.CoverId + ":" + v.CoverResponse.State : "";
        // Safety/feasibility bypass remains immediate. Preference changes are coalesced to avoid a noisy probe rerolling every tick.
        bool responseChanged = nextResponsePlan != responsePlan && s.Now - responseChangedAt >= .25;
        if (responseChanged) { responsePlan = nextResponsePlan; responseChangedAt = s.Now; }
        bool panic = config.Fear.Enabled && s.Fear?.Panic == true;
        var best = candidates.Where(x => x.Eligible).OrderByDescending(x => x.Score).FirstOrDefault();
        var previous = candidates.FirstOrDefault(x => x.Eligible && x.Decision == current?.ToString());
        string nextDanger = Danger(s, config);
        bool retryingNative = nativeRetry;
        bool invalidate = s.Target != target || v.ThreatSignature != threats
            || (current == VisibleTactic.Cover && (cover != v.CoverId || coverDestination != v.CoverDestination))
            || (v.RecentHit && (!hit || v.RecentHitAt > lastHit + .01)) || nextDanger != danger || responseChanged;
        string reason = best?.Reason ?? "no-feasible-option";
        bool kept = false;
        if (!invalidate && opportunityTrigger.Length == 0 && !nativeRetry && previous != null && best != null && (!panic || previous == best))
        {
            if (s.Now - since < c.Commitment) { kept = true; reason = "commitment"; }
            else if (best == previous || best.Score - previous.Score <= selectionGap + c.SwitchMargin)
            { kept = true; reason = "hysteresis"; }
        }
        if (kept) { best = previous; mixer.Retain(resuming ? "kept-sight-continuity" : "kept-" + reason); }
        else
        {
            string trigger = s.Target != target ? "new-contact" : invalidate ? "situation-changed" : previous == null && current.HasValue ? "previous-unavailable" : "score-improved";
            if (!invalidate && opportunityTrigger.Length > 0) trigger = opportunityTrigger;
            if (panic) trigger = "panic-best-feasible";
            double topScore = best?.Score ?? 0;
            best = mixer.Select(candidates, s.Target, s.Personality, trigger,
                panic || s.Hurt || s.LowAmmo || s.Suppressed || v.RecentHit || v.ThreatCount > 1 || v.ThreatsTruncated, nativeRetry, s.Now);
            nativeRetry = false;
            selectionGap = Math.Max(0, topScore - (best?.Score ?? 0));
            reason = best?.Reason ?? "no-feasible-option";
        }
        var next = best == null ? (VisibleTactic?)null : (VisibleTactic)Enum.Parse(typeof(VisibleTactic), best.Decision);
        // A hit or a changed threat rescoring the SAME cover is not a new movement
        // command. Keep its execution/progress clock while still reconsidering safety.
        bool sameCover = current == VisibleTactic.Cover && next == current && target == s.Target
            && cover.Length > 0 && cover == v.CoverId && coverDestination == v.CoverDestination && !resuming && !retryingNative;
        bool reconsidered = !kept && next.HasValue;
        bool changed = !sameCover && (current != next || invalidate || resuming || reconsidered);
        if (changed && !kept) since = s.Now;
        current = next; target = s.Target; threats = v.ThreatSignature; cover = v.CoverId; coverDestination = v.CoverDestination;
        hit = v.RecentHit; lastHit = v.RecentHitAt; danger = nextDanger;
        resuming = false;
        lastCandidates = candidates;
        return new VisibleChoice { Decision = next, Changed = changed, Reconsidered = reconsidered,
            Reason = reason, Candidates = candidates, Mixing = mixer.Last };
    }
    // Shared by the chooser and SAIN's early ContinueMoveToCover branch. One deadline
    // survives both call paths; repeated acceptance cannot extend an unmeasured plan.
    public bool TryContinuePending(VisibleSituation v)
    {
        string reason = Ineligible(v, config) != "exposure-probe-pending" ? "not-pending"
            : !current.HasValue ? "no-accepted-plan" : nativeRetry ? "native-refused"
            : !v.ContinuationSafe ? (v.ContinuationReason.Length > 0 ? v.ContinuationReason : "unverified-plan")
            : v.Contact.Target != target ? "contact-changed" : v.ThreatSignature != threats ? "threats-changed"
            : config.Fear.Enabled && v.Contact.Fear?.Panic == true ? "panic"
            : v.RecentHit && (!hit || v.RecentHitAt > lastHit + .01) ? "new-hit"
            : Danger(v.Contact,config) != danger ? "danger-changed" : "";
        if(reason.Length==0&&!pendingProbe.Keep(v.Contact.Now,config.Handoff.Grace,target+":"+current,true))reason="grace-expired";
        if(reason.Length>0){v.ContinuationReason=reason;return false;}
        v.ContinuationReason="verified-plan-retained";
        return true;
    }
    public void ProbeRefreshed() => pendingProbe.Clear();
    private static string Danger(Situation s, CometConfig c) => s.Hurt + "/" + s.LowAmmo + "/" + s.Suppressed
        + "/" + (c.Fear.Enabled && s.Fear?.Panic == true) + "/" + (c.Loadout.Enabled ? s.Loadout?.PlanKey : "");
    public VisibleChoice Release(string reason)
    { pendingProbe.Clear(); lastCandidates = new(); bool changed = current.HasValue; current = null; target = threats = cover = coverDestination = responsePlan = ""; responseChangedAt = double.NegativeInfinity;
        hit = resuming = false; return new VisibleChoice { Changed = changed, Reason = reason }; }
    private static bool Finite(double x) => !double.IsNaN(x) && !double.IsInfinity(x);
}

// A short safety intent survives A02 -> A01, but never survives a raid, ghost transition or target change.
public sealed class CoverIntent
{
    private string target = "";
    private double until;
    public void Accepted(string contact, double now, double seconds) { target = contact; until = now + seconds; }
    public bool Allows(string contact, double now) => target == contact && now < until;
    public void Apply(Situation s)
    {
        if (s.Target != target || s.Now >= until || s.Ghost || !s.IsActive) { Clear(); return; }
        foreach (var tactic in new[] { Tactic.Push, Tactic.Flank, Tactic.Search, Tactic.Grenade }) s.Unavailable[tactic] = "recent-cover-intent";
    }
    public void Clear() { target = ""; until = 0; }
}
