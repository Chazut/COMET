using System;
using SAIN.Components;
using SAIN.SAINComponent.Classes.EnemyClasses;

namespace Comet.Client;

// SAIN can briefly retain a squad member as GoalEnemy during spawn/group setup.
// Never infer friendship from USEC/BEAR: other squads remain valid opponents.
internal static class ContactSafety
{
    internal static bool Allows(BotComponent b, Enemy? enemy)
    {
        if (b == null || enemy == null || string.IsNullOrEmpty(enemy.EnemyProfileId))
            return false;
        if (enemy.EnemyProfileId == b.ProfileId)
            return false;
        if (b.Squad?.Members?.ContainsKey(enemy.EnemyProfileId) == true)
            return false;
        var group = b.BotOwner?.BotsGroup;
        var other = enemy.EnemyPlayer?.AIData?.BotOwner;
        return group == null
            || other == null
            || !ReferenceEquals(group, other.BotsGroup) && !group.Contains(other);
    }

    internal static bool AllowsGoal(BotComponent b) => b != null && Allows(b, b.GoalEnemy);
}
