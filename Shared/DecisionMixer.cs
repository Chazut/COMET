using System;
using System.Collections.Generic;
using System.Linq;

namespace Comet.Core;

public sealed class MixOption
{
    public string Decision { get; set; } = "";
    public double Score { get; set; }
    public double Probability { get; set; }
    public string Reason { get; set; } = "";
}

// The distribution is the one used at selection, never a prediction based on later scores.
public sealed class MixTrace
{
    public MistakeTrace? Mistake { get; set; }
    public string Seed { get; set; } = "";
    public string State { get; set; } = "";
    public string Trigger { get; set; } = "";
    public string Selected { get; set; } = "";
    public int Encounter { get; set; }
    public int Opportunity { get; set; }
    public int DrawIndex { get; set; }
    public double? Roll { get; set; }
    public double Temperature { get; set; }
    public double ScoreGap { get; set; }
    public List<MixOption> Options { get; set; } = new List<MixOption>();
}

// One instance per bot AND policy. No Unity/global RNG, wall-clock seed or shared counters.
public sealed class DecisionMixer
{
    private readonly DecisionVarietyConfig config;
    private readonly string stream;
    private readonly bool visible;
    private readonly TacticalMistakes? mistakes;
    private string target = "";
    private uint state;
    private int encounter, opportunity, draw;
    public MixTrace? Last { get; private set; }
    public DecisionMixer(DecisionVarietyConfig config, string stream, bool visible, TacticalMistakes? mistakes = null)
    { this.config = config; this.stream = stream; this.visible = visible; this.mistakes = mistakes; }

    public Candidate? Select(List<Candidate> candidates, string contact, string personality, string trigger, bool urgent = false, bool retry = false, double now = 0)
    {
        if (target != contact || encounter == 0)
        {
            target = contact; encounter++; draw = 0;
            state = Hash(stream + "|" + (visible ? "visible" : "hidden") + "|" + contact + "|" + encounter);
        }
        var ranked = candidates.Where(x => x.Eligible && Finite(x.Score) )
            .OrderByDescending(x => x.Score).ToList();
        var best = ranked.FirstOrDefault();
        double gap = 0; // zzap has no relative score-band veto.
        double intensity = visible ? config.VisibleIntensity : config.HiddenIntensity;
        double temperature = 6 * intensity * PersonalityScale(personality);
        var pool = ranked.Take(3).Where((x,i) => i == 0 || x.Score > 5).ToList();
        bool deterministic = visible || !config.Enabled || intensity == 0 || temperature <= 0 || pool.Count <= 1;
        string mode = visible ? "zzap-visible-best" : !config.Enabled || intensity == 0 ? "disabled-best"
            : pool.Count <= 1 || temperature <= 0 ? "single-option" : "weighted-roll";
        var trace = new MixTrace { Seed = state.ToString("x8"), Encounter = encounter, Opportunity = ++opportunity,
            DrawIndex = draw, State = mode, Trigger = trigger, Temperature = temperature, ScoreGap = gap };
        double total = deterministic ? 1 : pool.Sum(x => Math.Exp((x.Score - best!.Score) / temperature));
        foreach (var c in candidates)
        {
            bool valid = ranked.Contains(c), included = pool.Contains(c);
            double probability = deterministic ? (ReferenceEquals(c, best) ? 1 : 0)
                : included ? Math.Exp((c.Score - best!.Score) / temperature) / total : 0;
            trace.Options.Add(new MixOption { Decision = c.Decision, Score = Finite(c.Score) ? c.Score : 0, Probability = probability,
                Reason = !valid ? c.Eligible ? "invalid-score" : c.Reason : !included ? "outside-positive-top-three"
                : deterministic && !ReferenceEquals(c, best) ? mode : "in-pool" });
        }
        var chosen = best;
        if (!deterministic)
        {
            double roll = Next(); trace.Roll = roll; trace.DrawIndex = ++draw;
            double cumulative = 0; chosen = pool[pool.Count - 1];
            foreach (var c in pool)
            {
                cumulative += trace.Options.First(x => x.Decision == c.Decision).Probability;
                if (roll < cumulative) { chosen = c; break; }
            }
        }
        trace.Selected = chosen?.Decision ?? ""; Last = trace;
        return mistakes == null ? chosen : mistakes.Select(candidates, chosen, trace, personality, visible, now, urgent, retry);
    }

    public MixTrace? Retain(string reason, string? selected = null)
    {
        var t = Last;
        if (t == null || t.State == reason) return t;
        return Last = new MixTrace { Seed = t.Seed, State = reason, Trigger = t.Trigger, Selected = selected ?? t.Selected,
            Encounter = t.Encounter, Opportunity = t.Opportunity, DrawIndex = t.DrawIndex, Roll = t.Roll,
            Temperature = t.Temperature, ScoreGap = t.ScoreGap, Options = t.Options, Mistake = t.Mistake };
    }
    private double Next()
    { state ^= state << 13; state ^= state >> 17; state ^= state << 5; return state / 4294967296.0; }
    private static uint Hash(string value)
    {
        uint h = 2166136261;
        unchecked { foreach (char c in value) { h ^= c; h *= 16777619; } }
        return h == 0 ? 0x9e3779b9 : h;
    }
    private static double PersonalityScale(string p) => p == "Wreckless" ? 2 : p == "GigaChad" ? 1.5 : p == "Chad" ? 1.2
        : p == "SnappingTurtle" ? .6 : p == "Rat" || p == "Coward" ? .5 : 1;
    private static bool Finite(double x) => !double.IsNaN(x) && !double.IsInfinity(x);
}
