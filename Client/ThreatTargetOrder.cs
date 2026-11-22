using SAIN.SAINComponent.Classes.Decision;
using SAIN.SAINComponent.Classes.EnemyClasses;

namespace Comet.Client;

public sealed partial class Plugin
{
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
