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
    private readonly Random random;

    private sealed class Verdict
    {
        public string Best = "",
            Picked = "";
        public double Until;
    }

    private readonly Dictionary<bool, Verdict> verdicts = new();
    private int draws,
        opportunities;

    public TacticalMistakes(TacticalMistakesConfig config, string stream)
    {
        this.config = config;
        random = new Random(StableSeed.Of(stream + "|mistake"));
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
        var best = ranked.FirstOrDefault();
        double chance = Math.Min(
            1,
            config.Multiplier * (config.Chances.TryGetValue(personality, out var p) ? p : .05)
        );
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
            if (
                !verdicts.TryGetValue(visible, out var v)
                || now >= v.Until
                || v.Best != best.Decision
            )
            {
                double roll = random.NextDouble();
                draws++;
                trace.Roll = roll;
                string pick = "";
                if (roll < chance)
                {
                    int i = 1;
                    if (ranked.Count > 2 && ranked[2].Score > 0)
                    {
                        draws++;
                        if (random.NextDouble() < .3)
                            i = 2;
                    }
                    pick = ranked[i].Decision;
                }
                v = new Verdict
                {
                    Best = best.Decision,
                    Picked = pick,
                    Until = now + 5,
                };
                verdicts[visible] = v;
            }
            trace.NextAllowedAt = v.Until;
            var alternative = ranked.Find(c => c.Decision == v.Picked && c.Score > 0);
            if (alternative == null)
                trace.State = "kept-best";
            else if (best.Score - alternative.Score > 35)
                trace.State = "score-loss-too-large";
            else
            {
                chosen = alternative;
                trace.State = "mistake-selected";
            }
        }
        trace.Selected = chosen?.Decision ?? "";
        trace.SelectedScore = chosen?.Score ?? 0;
        trace.DrawIndex = draws;
        trace.ScoreLoss = best == null ? 0 : Math.Max(0, best.Score - trace.SelectedScore) / 100;
        foreach (var c in candidates)
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
        mixing.Mistake = trace;
        mixing.Selected = trace.Selected;
        return chosen;
    }
}
