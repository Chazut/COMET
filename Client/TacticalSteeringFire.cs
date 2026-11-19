using SAIN.Components;

namespace Comet.Client;

internal enum TacticalSteeringKind
{
    SquadAngle,
    CornerChase,
    CoverFeint,
    PostCombat,
}

// Preserve the firing part of zzap's steering callbacks before applying our look target.
// Do not gate this on GoalEnemy.IsVisible: SAIN also considers other visible enemies.
internal static class TacticalSteeringFire
{
    internal static bool TryShoot(BotComponent bot, TacticalSteeringKind kind)
    {
        var enemy = bot.GoalEnemy;
        bool fired = bot.Shoot.ShootAnyVisibleEnemies(enemy);
        bool suppressed = false;
        if (
            !fired
            && (kind == TacticalSteeringKind.SquadAngle || kind == TacticalSteeringKind.CoverFeint)
        )
            suppressed = bot.Suppression.TrySuppressAnyEnemy(
                enemy,
                bot.EnemyController.KnownEnemies
            );

        if (fired || suppressed)
        {
            if (kind != TacticalSteeringKind.PostCombat)
            {
                bool steered = bot.Steering.SteerByPriority(enemy, false);
                if (!steered && kind == TacticalSteeringKind.CornerChase)
                    bot.Steering.LookToLastKnownEnemyPosition(enemy);
            }
        }
        Plugin.ObserveSteeringFire(bot, kind, fired, suppressed);
        return fired || suppressed;
    }
}
