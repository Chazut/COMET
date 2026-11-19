using System;
using SAIN.Components;

namespace Comet.Client;

// postfix: new HarmonyMethod(typeof(Plugin), nameof(CheckSearchedPlace))
// postfix: new HarmonyMethod(typeof(Plugin), nameof(SearchForgetObserved))
internal class SearchProbe
{
    internal BotComponent Bot = new();
    internal bool Relevant,
        Pending;
}

public sealed partial class Plugin
{
    internal int SearchChecks,
        PendingChecks;
    internal bool ThrowPreflight;

    private static bool NeedsAbandonment(SearchProbe checker, bool pending)
    {
        if (Instance!.ThrowPreflight)
            throw new Exception("injected preflight");
        return pending ? checker.Pending : checker.Relevant;
    }

    internal void CheckSearchedPlace(SearchProbe __instance, ref bool __result)
    {
        SearchChecks++;
        __result = false;
    }

    internal void SearchForgetObserved(SearchProbe __instance)
    {
        PendingChecks++;
    }
}
