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

// One instance for both tactical policies of one bot. Neither contact changes nor
// ownership releases reset its clock. A05 never consumes A04 or Unity random state.
public sealed class TacticalMistakes
{
    private readonly TacticalMistakesConfig config;
    private uint state;
    private int draws, opportunities;
    private double? lastMistakeAt;
    public TacticalMistakes(TacticalMistakesConfig config, string stream)
    {
        this.config = config;
        uint h = 2166136261;
        unchecked { foreach (char c in stream + "|A05") { h ^= c; h *= 16777619; } }
        state = h == 0 ? 0x9e3779b9 : h;
    }

    public Candidate? Select(List<Candidate> candidates, Candidate? before, MixTrace mixing,
        string personality, bool visible, double now, bool urgent, bool retry)
    {
        var ranked = candidates.Where(c => c.Eligible && Finite(c.Score) && c.Score > 0)
            .OrderByDescending(c => c.Score).ThenBy(c => c.Decision, StringComparer.Ordinal).ToList();
        var best = ranked.FirstOrDefault();
        double chance = Math.Min(1, config.Multiplier * (config.Chances.TryGetValue(personality, out var p) ? p : config.Chances["Normal"]));
        double lossLimit = visible ? config.VisibleMaxLoss : config.HiddenMaxLoss;
        // Even when A04 is disabled, its configured score band separates ordinary
        // variety from a mistake. Equal-score choices are never mistakes.
        var pool = ranked.Where(c => best != null && c.Score < best.Score * (1 - mixing.ScoreGap)
            && c.Score >= best.Score * (1 - lossLimit)).ToList();
        var trace = new MistakeTrace { Seed = state.ToString("x8"), Opportunity = ++opportunities, DrawIndex = draws,
            At = Finite(now) ? now : 0, NextAllowedAt = lastMistakeAt + config.MinimumInterval,
            Best = best?.Decision ?? "", Before = before?.Decision ?? "", Selected = before?.Decision ?? "",
            BestScore = best?.Score ?? 0, SelectedScore = before?.Score ?? 0, Chance = chance, MaxLoss = lossLimit,
            Personality = personality };
        string blocked = !config.Enabled || chance == 0 ? "disabled" : !Finite(now) ? "invalid-time"
            : urgent ? "urgent" : retry ? "native-retry" : best == null || before == null ? "no-feasible-option"
            : !ReferenceEquals(before, best) ? "a04-already-varied"
            : lastMistakeAt.HasValue && now < lastMistakeAt.Value + config.MinimumInterval ? "cooldown"
            : pool.Count == 0 ? "no-plausible-alternative" : "";
        var chosen = before;
        trace.State = blocked;
        if (blocked.Length == 0)
        {
            state ^= state << 13; state ^= state >> 17; state ^= state << 5;
            double roll = state / 4294967296.0;
            trace.Roll = roll; trace.DrawIndex = ++draws;
            trace.State = "kept-best";
            if (roll < chance)
            {
                // One draw, split into equal subintervals for eligible alternatives.
                chosen = pool[Math.Min(pool.Count - 1, (int)(roll / chance * pool.Count))];
                lastMistakeAt = now; trace.NextAllowedAt = now + config.MinimumInterval;
                trace.State = "mistake-selected";
            }
        }
        foreach (var c in candidates)
        {
            bool valid = ranked.Contains(c), alternate = pool.Contains(c);
            string reason = !c.Eligible ? c.Reason : !valid ? "nonpositive-or-invalid-score" : ReferenceEquals(c, best) ? "best"
                : best != null && c.Score >= best.Score * (1 - mixing.ScoreGap) ? "inside-a04-band"
                : !alternate ? "score-loss-too-large" : "plausible-alternative";
            double probability = blocked.Length > 0 ? ReferenceEquals(c, before) ? 1 : 0
                : ReferenceEquals(c, best) ? 1 - chance : alternate ? chance / pool.Count : 0;
            trace.Options.Add(new MixOption { Decision = c.Decision, Score = Finite(c.Score) ? c.Score : 0,
                Probability = probability, Reason = reason });
        }
        trace.Selected = chosen?.Decision ?? ""; trace.SelectedScore = chosen?.Score ?? 0;
        trace.ScoreLoss = best == null ? 0 : Math.Max(0, (best.Score - trace.SelectedScore) / best.Score);
        mixing.Mistake = trace;
        return chosen;
    }
    private static bool Finite(double x) => !double.IsNaN(x) && !double.IsInfinity(x);
}
