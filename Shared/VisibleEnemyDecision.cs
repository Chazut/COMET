using System;
using System.Collections.Generic;
using System.Linq;

namespace Comet.Core;

public enum VisibleTactic
{
    Shoot,
    Cover,
    Push,
}

public sealed class VisibleSituation
{
    public Situation Contact = new Situation();
    internal double EnemyDistance =>
        Contact.RealDistance is double distance && double.IsFinite(distance)
            ? distance
            : Contact.KnownDistance;
    public bool WeaponReady,
        CanShoot,
        CoverReachable,
        PushReachable,
        RecentHit,
        ProbeReady;
    public bool ThreatsTruncated;

    // Adapter evidence for the already executing action, never a substitute probe for new choices.
    public bool ContinuationSafe;
    public string ContinuationReason = "not-evaluated",
        ProbePendingReason = "";
    public string CoverId = "",
        CoverDestination = "",
        ThreatSignature = "",
        CoverReason = "no-reachable-cover",
        PushReason = "no-current-complete-path";
    public int ThreatCount;
    public double RecentHitAt;
    public double CoverDistance = 99;
    public double CurrentExposure,
        CoverExposure,
        CoverRouteRisk,
        PushRouteRisk,
        PushPathLength;
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

// VisibleEnemyUtility.Rank. No additional exposure veto, hysteresis or failure cooldown.
public sealed class VisibleEnemyDecision
{
    private readonly CometConfig config;
    private readonly DecisionMixer mixer;
    private readonly Random random;
    private VisibleTactic? current;
    private string target = "";
    private double until,
        scoredAt;
    private List<Candidate> candidates = new();
    private List<Candidate> preference = new();
    public VisibleTactic? Current => current;
    public EnemyRoleAssessment? RankedEnemy { get; private set; }
    public MixTrace? Mixing => current.HasValue ? mixer.Last : null;

    public VisibleEnemyDecision(
        CometConfig config,
        string stream = "test",
        TacticalMistakes? mistakes = null
    )
    {
        config.Validate();
        this.config = config;
        random = new Random(StableSeed.Of(stream + "|visible-time"));
        mixer = new DecisionMixer(
            config.DecisionVariety,
            stream,
            true,
            mistakes ?? new TacticalMistakes(config.TacticalMistakes, stream)
        );
    }

    public bool NeedsEvidence(Situation s, bool hit) =>
        candidates.Count == 0 || target != s.Target || s.Now >= until || hit;

    public void SuspendForSight() { }

    public void NotifyNativeRefusal()
    {
        until = 0;
    }

    public static string? Ineligible(VisibleSituation v, CometConfig c)
    {
        var s = v.Contact;
        if (!c.Enabled || !c.VisibleCombat.Enabled)
            return "disabled";
        if (!s.IsAi || !s.IsSupportedBot)
            return "unsupported-bot";
        if (!s.IsAuthority)
            return "not-authority";
        if (!s.IsActive || s.Ghost)
            return "inactive-or-ghost";
        if (!s.Visible)
            return "enemy-not-visible";
        if (!s.HasContact || s.Target.Length == 0 || !Finite(s.Now) || !Finite(v.EnemyDistance))
            return "invalid-contact";
        if (!v.WeaponReady)
            return "no-loaded-ammo";
        return null;
    }

    public VisibleChoice Evaluate(VisibleSituation v)
    {
        var s = v.Contact;
        var c = config.VisibleCombat;
        string? rejection = Ineligible(v, config);
        if (rejection != null)
            return Release(rejection);
        bool same = target == s.Target && candidates.Count > 0;
        if (!v.CanShoot)
        {
            if (!v.RecentHit && same && (s.Now < until || s.Now - scoredAt < 1.5))
                return SelectAvailable(v, "zzap-kept-no-shot");
            return Release("no-clear-shot-or-out-of-range");
        }
        if (same && s.Now < until && !v.RecentHit)
            return SelectAvailable(v, "zzap-ranked-memory");
        double aggression = config.Aggression.TryGetValue(s.Personality, out var a) ? a : .5;
        var scores = ZzapUtility.Visible(v, aggression);
        RankedEnemy = s.EnemyRole;
        candidates = new List<Candidate>();
        void Add(VisibleTactic t, double weight, string? reason = null) =>
            candidates.Add(
                new Candidate
                {
                    Decision = t.ToString(),
                    Score = Math.Round(scores[t] * weight, 2),
                    Eligible = weight > 0 && reason == null,
                    Reason = weight == 0 ? "disabled-by-config" : reason ?? "available",
                }
            );
        Add(VisibleTactic.Shoot, c.ShootWeight);
        Add(VisibleTactic.Cover, c.CoverWeight);
        Add(VisibleTactic.Push, c.PushWeight);
        OutcomeScoring.Adjust(candidates, s, config, v.RecentHit);
        var best = mixer.Select(
            candidates,
            s.Target,
            s.Personality,
            "zzap-visible-score",
            now: s.Now
        );
        preference = candidates.Where(x => x.Eligible).OrderByDescending(x => x.Score).ToList();
        if (best != null)
        {
            preference.Remove(best);
            preference.Insert(0, best);
        }
        scoredAt = s.Now;
        until = s.Now + 2 + random.NextDouble();
        return SelectAvailable(v, "zzap-ranked-selection", true);
    }

    private VisibleChoice SelectAvailable(VisibleSituation v, string reason, bool rescored = false)
    {
        foreach (var c in candidates)
        {
            if (c.Reason == "disabled-by-config")
                continue;
            c.Reason =
                c.Decision == "Push"
                    ? (
                        !v.PushReachable ? v.PushReason
                        : v.EnemyDistance >= config.VisibleCombat.MaxPushPath
                            ? "push-too-far"
                        : "available"
                    )
                    : "available";
            c.Eligible = c.Reason == "available";
        }
        var chosen = preference.FirstOrDefault(x => x.Eligible);
        VisibleTactic? next =
            chosen == null
                ? null
                : (VisibleTactic)Enum.Parse(typeof(VisibleTactic), chosen.Decision);
        bool changed = current != next || target != v.Contact.Target;
        current = next;
        target = v.Contact.Target;
        if (changed && !rescored)
            reason = "zzap-capability-refreshed";
        return new VisibleChoice
        {
            Decision = next,
            Changed = changed,
            Reconsidered = rescored,
            Reason = reason,
            Candidates = candidates,
            Mixing = mixer.Retain(reason, chosen?.Decision ?? ""),
        };
    }

    public bool TryContinuePending(VisibleSituation v) => false;

    public void ProbeRefreshed() { }

    public VisibleChoice Release(string reason)
    {
        bool changed = current.HasValue;
        current = null;
        target = "";
        until = 0;
        candidates.Clear();
        preference.Clear();
        RankedEnemy = null;
        return new VisibleChoice { Changed = changed, Reason = reason };
    }

    private static bool Finite(double x) => !double.IsNaN(x) && !double.IsInfinity(x);
}

// A short safety intent survives A02 -> A01, but never survives a raid, ghost transition or target change.
public sealed class CoverIntent
{
    private string target = "";
    private double until;

    public void Accepted(string contact, double now, double seconds)
    {
        target = contact;
        until = now + seconds;
    }

    public bool Allows(string contact, double now) => target == contact && now < until;

    public void Apply(Situation s)
    {
        if (s.Target != target || s.Now >= until || s.Ghost || !s.IsActive)
        {
            Clear();
            return;
        }
        s.BrokeOffToCover = true;
    }

    public void Clear()
    {
        target = "";
        until = 0;
    }
}
