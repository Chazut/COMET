using System;
using EFT;
using HarmonyLib;
using SAIN.Components;
using SAIN.SAINComponent.Classes.Mover;
using UnityEngine;

namespace Comet.Client;

public sealed partial class Plugin
{
    private OpenLeafCarver? openLeaves;
    private bool openLeavesFaulted;

    private void UpdateOpenLeaves(GameWorld current)
    {
        if (openLeavesFaulted)
            return;
        if (config?.Enabled != true || !config.DoorMotion.CarveOpenLeaves || !Authority)
        {
            if (openLeaves != null)
                ClearOpenLeaves();
            return;
        }
        try
        {
            openLeaves ??= new OpenLeafCarver(Time.time, message => Logger.LogInfo(message));
            openLeaves.Tick(Time.time, current);
        }
        catch (Exception error)
        {
            Logger.LogWarning("COMET open door leaves disabled for this raid: " + error);
            ClearOpenLeaves();
            openLeavesFaulted = true;
        }
    }

    private static void PatchOpenLeafPaths(Harmony h)
    {
        h.Patch(AccessTools.Method(typeof(BotPathDataManual), "Initialize"),
            postfix: new HarmonyMethod(typeof(Plugin), nameof(OpenLeafPathInitialized)));
        h.Patch(AccessTools.Method(typeof(BotPathDataManual), nameof(BotPathDataManual.TryUpdatePath)),
            postfix: new HarmonyMethod(typeof(Plugin), nameof(OpenLeafPathReused)));
    }

    private static void OpenLeafPathInitialized(BotPathDataManual __instance)
    {
        Instance?.openLeaves?.Stamp(__instance, Time.time);
    }

    private static void OpenLeafPathReused(BotPathDataManual __instance, BotComponent ___Bot, ref bool __result)
    {
        var p = Instance;
        if (__result && p?.openLeaves is { HasCuts: true } carver && NativeAdapter.IsSupportedAi(___Bot)
            && !p.Ghost(___Bot.ProfileId) && carver.CrossesNewCut(__instance, ___Bot.Position, Time.time))
            __result = false;
    }

    partial void ClearOpenLeaves()
    {
        var owned = openLeaves;
        openLeaves = null;
        openLeavesFaulted = false;
        owned?.Dispose();
    }
}
