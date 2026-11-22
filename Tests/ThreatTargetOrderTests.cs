using Comet.Client;
using Comet.Core;
using SAIN.SAINComponent.Classes.Decision;
using SAIN.SAINComponent.Classes.EnemyClasses;

// Exercise the shipped dispatcher and threat scorer with a controlled native decision.
internal static class ThreatTargetOrderTests
{
    public static int Run()
    {
        var close = new Enemy("close", 3);
        var attacker = new Enemy("attacker", 30);
        attacker.Contact.HitAt = 10;
        var known = new EnemyList { attacker, close };
        var controller = new SAINEnemyController(known);
        var decision = new DogFightDecisionClass(known);

        var first = Plugin.SelectForTest(decision, controller);
        if (first != attacker || controller.Evaluations != 1 || decision.Calls != 1)
            throw new Exception("A07 must select the attacker after native close combat declines.");

        decision.Accepted = close;
        var second = Plugin.SelectForTest(decision, controller);
        if (second != close || controller.Evaluations != 1 || controller.NativeWins != 1 || decision.Calls != 2)
            throw new Exception("Native close combat must take the other enemy during A07's active hold.");

        decision.Accepted = attacker;
        if (Plugin.SelectForTest(decision, controller) != attacker || controller.Evaluations != 1
            || controller.NativeWins != 2 || decision.Calls != 3)
            throw new Exception("A native choice matching the previous A07 choice must still be attributed to SAIN.");

        decision.Accepted = null;
        if (Plugin.SelectForTest(decision, controller) != attacker || controller.Evaluations != 2 || decision.Calls != 4)
            throw new Exception("A07 must be consulted again as soon as native close combat declines.");

        known.Remove(attacker);
        if (Plugin.SelectForTest(decision, controller) != close || controller.Fallbacks != 1
            || controller.Evaluations != 3 || decision.Calls != 5)
            throw new Exception("A lone contact after native refusal must reach ordinary SAIN selection.");
        return 5;
    }
}

namespace SAIN.SAINComponent.Classes.EnemyClasses
{
    internal sealed class Enemy(string id, float distance)
    {
        public ThreatContact Contact { get; } = new() { Id = id, Distance = distance, Visible = true, Valid = true };
    }

    internal sealed class EnemyList : List<Enemy> { }

    internal sealed class SAINEnemyController(EnemyList known)
    {
        public EnemyList KnownEnemies => known;
        public ThreatSelection Bot { get; } = new(new CometConfig());
        public int Evaluations, NativeWins, Fallbacks;
    }
}

namespace SAIN.SAINComponent.Classes.Decision
{
    internal sealed class DogFightDecisionClass(EnemyList expected)
    {
        public int Calls;
        public Enemy? Accepted;
        public bool CheckShallDogFight(EnemyList known, out Enemy target)
        {
            if (!ReferenceEquals(known, expected))
                throw new Exception("SAIN must receive its original known-enemy list.");
            Calls++;
            target = Accepted!;
            return Accepted != null;
        }
    }
}

namespace Comet.Client
{
    public sealed partial class Plugin
    {
        internal static Enemy SelectForTest(DogFightDecisionClass decision, SAINEnemyController controller)
        {
            if (ChooseDogFightOrThreat(decision, controller.KnownEnemies, out var target, controller))
                return target;
            controller.Fallbacks++;
            return controller.KnownEnemies.Single();
        }

        private static void NativeDogFightSelected(SAINEnemyController controller) => controller.NativeWins++;

        private static bool TrySelectThreat(ThreatSelection selector, SAINEnemyController controller, ref Enemy target)
        {
            controller.Evaluations++;
            var frame = new ThreatFrame { Now = 10, Active = true, IsAuthority = true, IsAi = true, IsSupportedBot = true };
            frame.Contacts.AddRange(controller.KnownEnemies.Select(e => e.Contact));
            var selected = selector.Evaluate(frame).Selected;
            target = controller.KnownEnemies.FirstOrDefault(e => e.Contact.Id == selected)!;
            return target != null;
        }
    }
}
