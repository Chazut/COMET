using System;
using EFT;
using HarmonyLib;
using SAIN;
using SAIN.Components;

namespace Comet.Client;

public sealed partial class Plugin
{
    private static void PatchGrenadeFilter(Harmony h)
    {
        h.Patch(
            AccessTools.Method(typeof(BotGrenadeController), nameof(BotGrenadeController.DoThrow)),
            prefix: new HarmonyMethod(typeof(Plugin), nameof(OffensiveThrowGuard))
            {
                priority = Priority.Last,
            }
        );
    }

    private static bool OffensiveThrowGuard(BotGrenadeController __instance, ref bool __result)
    {
        var p = Instance;
        if (
            p == null
            || __instance?._owner == null
            || !SAINEnableClass.GetSAIN(__instance._owner.ProfileId, out var b)
            || b == null
            || !p.GrenadeFilterScope(b)
        )
            return true;
        var s = p.GrenadeState(b);
        try
        {
            if (p.ValidateOffensiveGrenade(b, s))
                return true;
        }
        catch (Exception ex)
        {
            p.FilterError(s, ex);
        }
        __result = false;
        return false;
    }
}
