using System;
using System.Collections.Generic;

namespace Comet.Core;

public sealed class TacticalMistakesConfig
{
    public bool Enabled { get; set; } = true;
    public double Multiplier { get; set; } = 1;
    public double HiddenMaxLoss { get; set; } = .25;
    public double VisibleMaxLoss { get; set; } = .20;
    public double MinimumInterval { get; set; } = 10;
    public Dictionary<string, double> Chances { get; set; } =
        new Dictionary<string, double>
        {
            ["Timmy"] = .12,
            ["Wreckless"] = .08,
            ["Coward"] = .06,
            ["Normal"] = .05,
            ["Chad"] = .04,
            ["Rat"] = .03,
            ["SnappingTurtle"] = .03,
            ["GigaChad"] = .02,
        };

    public double ChanceFor(string personality) => Math.Min(1,
        Multiplier * (Chances.TryGetValue(personality, out var chance) ? chance : .05));

    public void Validate()
    {
        CometConfig.Range(Multiplier, 0, 3, nameof(Multiplier));
        CometConfig.Range(HiddenMaxLoss, 0, .5, nameof(HiddenMaxLoss));
        CometConfig.Range(VisibleMaxLoss, 0, .5, nameof(VisibleMaxLoss));
        CometConfig.Range(MinimumInterval, 1, 120, nameof(MinimumInterval));
        if (Chances == null || Chances.Count != 8)
            throw new ArgumentException("Expected eight mistake chances.");
        foreach (
            var name in new[]
            {
                "Normal",
                "Chad",
                "GigaChad",
                "Wreckless",
                "Rat",
                "SnappingTurtle",
                "Coward",
                "Timmy",
            }
        )
            if (!Chances.TryGetValue(name, out var chance))
                throw new ArgumentException("Missing mistake chance: " + name);
            else
                CometConfig.Range(chance, 0, 1, name + " mistake chance");
    }
}
