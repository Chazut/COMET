using SAIN.Components;

namespace Comet.Client;

public sealed partial class Plugin
{
    internal int Actions,
        OldCallbacks;

    internal void Act(BotComponent b)
    {
        Actions++;
        if (states.TryGetValue(b, out var s))
            s.OwnCommand = true;
    }

    internal void Callback(BotState s)
    {
        OldCallbacks++;
        s.OwnCommand = true;
    }

    // prefix: new HarmonyMethod(typeof(Plugin), nameof(Prefix))
    internal bool Prefix(BotComponent b, ref int input, out int local)
    {
        input = 99;
        local = 10;
        Act(b);
        return false;
    }

    internal void Dependency(BotComponent b)
    {
        Act(b);
        Actions += 10;
    }

    internal void NestedDeath(BotState s)
    {
        s.Owner.IsDead = true;
        Callback(s);
    }

    internal void OtherBot(BotState s, BotComponent other)
    {
        Act(other);
    }
}
