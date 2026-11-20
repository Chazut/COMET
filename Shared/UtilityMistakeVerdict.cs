using System;
using System.Collections.Generic;

namespace Comet.Core;

// One instance per bot and utility family. Only the identity of the pick is cached,
// so fresh scores and availability still decide whether the verdict can be used.
internal sealed class UtilityMistakeVerdict
{
    private string best = "", picked = "";
    private double until;

    internal (int Rank, string State, double? Until, double? Roll, int Draws) Choose<T>(
        IReadOnlyList<T> ranked, Func<T, string> name, Func<T, double> score,
        double now, double chance, Func<double> next)
    {
        if (chance <= 0) return (0, "disabled", null, null, 0);
        if (ranked.Count < 2 || score(ranked[1]) <= 0)
            return (0, "no-positive-alternative", null, null, 0);
        double? roll = null;
        int draws = 0;
        if (now >= until || best != name(ranked[0]))
        {
            best = name(ranked[0]);
            picked = "";
            until = now + 5;
            roll = next();
            draws++;
            if (roll < chance)
            {
                int pick = 1;
                if (ranked.Count > 2 && score(ranked[2]) > 0)
                {
                    draws++;
                    if (next() < .3) pick = 2;
                }
                picked = name(ranked[pick]);
            }
        }
        for (int i = 1; i < ranked.Count; i++)
        {
            if (name(ranked[i]) != picked || score(ranked[i]) <= 0) continue;
            if (score(ranked[0]) - score(ranked[i]) > 35)
                return (0, "score-loss-too-large", until, roll, draws);
            return (i, "mistake-selected", until, roll, draws);
        }
        return (0, "kept-best", until, roll, draws);
    }
}
