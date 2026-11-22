using System;
using SAIN.SAINComponent.Classes.Decision;
using SAIN.SAINComponent.Classes.EnemyClasses;

namespace Comet.Client;

internal sealed class CloseFightRetarget
{
    private float nextCheck;

    internal Enemy? Find(DogFightDecisionClass decision, EnemyList known, Enemy current, float now,
        Func<DogFightDecisionClass, Enemy, bool> eligible)
    {
        if (now < nextCheck)
            return null;
        nextCheck = now + 0.25f;

        float limit = current.Path.PathLength * 0.6f;
        Enemy? best = null;
        for (int i = 0; i < known.Count; i++)
        {
            var candidate = known[i];
            if (candidate == null || ReferenceEquals(candidate, current) || !candidate.IsVisible
                || candidate.Path.PathLength >= limit || !eligible(decision, candidate))
                continue;
            if (best == null || candidate.Path.PathLength < best.Path.PathLength)
                best = candidate;
        }
        return best;
    }
}
