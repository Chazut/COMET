using SAIN.SAINComponent.Classes.Decision;
using SAIN.SAINComponent.Classes.EnemyClasses;

namespace Comet.Client;

public sealed partial class Plugin
{
    private static float CloseFightScanInterval(float nativeInterval, DogFightDecisionClass decision)
    {
        var bot = decision.Bot;
        return bot.GoalEnemy?.IsVisible == true && Instance?.NativeFixScope(bot) == true
            ? 0.1f
            : nativeInterval;
    }

    private static bool ChooseDogFightOrThreat(
        DogFightDecisionClass decision, EnemyList known, out Enemy target, SAINEnemyController controller)
    {
        if (decision.CheckShallDogFight(known, out target))
        {
            NativeDogFightSelected(controller);
            return true;
        }
        return TrySelectThreat(controller.Bot, controller, ref target);
    }
}
