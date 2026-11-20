using System;
using System.Collections.Generic;
using System.Linq;

namespace Comet.Core;

public sealed class MistakeTrace
{
    public string State { get; set; } = "";
    public string Seed { get; set; } = "";
    public string Best { get; set; } = "";
    public string Before { get; set; } = "";
    public string Selected { get; set; } = "";
    public string Personality { get; set; } = "";
    public int Opportunity { get; set; }
    public int DrawIndex { get; set; }
    public int? SwappedFromRank { get; set; }
    public double At { get; set; }
    public double? NextAllowedAt { get; set; }
    public double? Roll { get; set; }
    public double Chance { get; set; }
    public double BestScore { get; set; }
    public double SelectedScore { get; set; }
    public double ScoreLoss { get; set; }
    public double MaxLoss { get; set; }
    public List<MixOption> Options { get; set; } = new List<MixOption>();
}

// R_F's UtilityMistake: a five-second verdict per bot and decision family.
public sealed class TacticalMistakes
{
    private readonly TacticalMistakesConfig config;
    private readonly Func<double> nextRoll;
    private readonly UtilityMistakeVerdict hiddenVerdict = new(), visibleVerdict = new();
    private int draws,
        opportunities;

    public TacticalMistakes(TacticalMistakesConfig config, string stream)
    {
        this.config = config;
        nextRoll = new Random(StableSeed.Of(stream + "|mistake")).NextDouble;
    }

    public Candidate? Select(
        List<Candidate> candidates,
        Candidate? before,
        MixTrace mixing,
        string personality,
        bool visible,
        double now,
        bool urgent,
        bool retry
    )
    {
        var ranked = candidates
            .Where(c => c.Eligible && !double.IsNaN(c.Score) && !double.IsInfinity(c.Score))
            .OrderByDescending(c => c.Score)
            .ToList();
        return ApplyToRanking(ranked, before, mixing, personality, visible, now, candidates);
    }

    internal Candidate? ApplyToRanking(
        List<Candidate> ranked,
        Candidate? before,
        MixTrace mixing,
        string personality,
        bool visible,
        double now,
        List<Candidate>? allCandidates = null
    )
    {
        var best = ranked.FirstOrDefault();
        double chance = config.ChanceFor(personality);
        var trace = new MistakeTrace
        {
            At = now,
            Opportunity = ++opportunities,
            DrawIndex = draws,
            Best = best?.Decision ?? "",
            Before = before?.Decision ?? "",
            Selected = before?.Decision ?? "",
            BestScore = best?.Score ?? 0,
            SelectedScore = before?.Score ?? 0,
            Chance = chance,
            MaxLoss = .35,
            Personality = personality,
        };
        Candidate? chosen = before;
        if (!config.Enabled || chance == 0)
            trace.State = "disabled";
        else if (best == null || before == null || ranked.Count < 2 || ranked[1].Score <= 0)
            trace.State = "no-positive-alternative";
        else if (before.Decision != best.Decision)
            trace.State = "a04-already-varied";
        else
        {
            var verdict = (visible ? visibleVerdict : hiddenVerdict).Choose(ranked,
                c => c.Decision, c => c.Score, now, chance, nextRoll);
            draws += verdict.Draws;
            trace.State = verdict.State;
            trace.Roll = verdict.Roll;
            trace.NextAllowedAt = verdict.Until;
            chosen = ranked[verdict.Rank];
        }
        trace.Selected = chosen?.Decision ?? "";
        trace.SelectedScore = chosen?.Score ?? 0;
        trace.DrawIndex = draws;
        trace.ScoreLoss = best == null ? 0 : Math.Max(0, best.Score - trace.SelectedScore) / 100;
        foreach (var c in allCandidates ?? ranked)
            trace.Options.Add(
                new MixOption
                {
                    Decision = c.Decision,
                    Score = c.Score,
                    Reason =
                        !c.Eligible ? c.Reason
                        : c.Score <= 0 ? "nonpositive"
                        : c == best ? "best"
                        : "ranked-alternative",
                }
            );
        if (chosen != null && chosen != before)
        {
            int index = ranked.IndexOf(chosen);
            trace.SwappedFromRank = index + 1;
            (ranked[0], ranked[index]) = (ranked[index], ranked[0]);
        }
        mixing.Mistake = trace;
        mixing.Selected = trace.Selected;
        return chosen;
    }
}
