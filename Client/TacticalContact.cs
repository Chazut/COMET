using SAIN.SAINComponent.Classes.EnemyClasses;

namespace Comet.Client;

public sealed partial class Plugin
{
    private static bool SameTacticalContact(object? left,object? right)
        =>ReferenceEquals(left,right)||(left is Enemy a&&right is Enemy b
            &&!string.IsNullOrEmpty(a.EnemyProfileId)&&a.EnemyProfileId==b.EnemyProfileId);
}
