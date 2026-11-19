using System;
using Comet.Core;
using HarmonyLib;
using SAIN.Classes.Bot.Search;
using SAIN.Preset.Shared.Enums;
using SAIN.SAINComponent.Classes.EnemyClasses;
using UnityEngine;

namespace Comet.Client;

public sealed partial class Plugin
{
    private static void PatchSearchExecution(Harmony harmony) =>
        harmony.Patch(
            AccessTools.Method(typeof(SearchClass), nameof(SearchClass.Search)),
            prefix: new HarmonyMethod(typeof(Plugin), nameof(PrepareCometSearch))
        );

    private static bool PrepareCometSearch(SearchClass __instance, Enemy enemy)
    {
        var p = Instance;
        var bot = __instance.Bot;
        if (
            p?.config?.Enabled != true
            || !p.Authority
            || !NativeAdapter.IsSupportedAi(bot)
            || !bot.BotActive
            || bot.IsDead
            || bot.GameEnding
            || p.Ghost(bot.ProfileId)
            || !p.states.TryGetValue(bot, out var state)
            || state.Decision != "Search"
            || state.Expected != ECombatDecision.Search
            || state.ActiveLabel.Length == 0
            || bot.Decision.CurrentSelfDecision != ESelfActionType.None
            || bot.Decision.CurrentSquadDecision != ESquadDecision.None
        )
            return true;
        try
        {
            if (!ContactSafety.Allows(bot, enemy))
                return true;
            var ready = SearchExecutionAdapter.Prepare(__instance, enemy, out var reason);
            if (reason != "search-target-retained")
                p.ArbitrationEvent(state, reason, "Search");
            if (ready)
                return true;
            state.Engine.Fail(Tactic.Search, Time.time, reason);
            p.YieldToSain(state, reason);
            return false;
        }
        catch (Exception ex)
        {
            p.YieldToSain(state, "search-execution-error");
            p.Logger.LogWarning("COMET search preparation failed: " + ex.Message);
            return true;
        }
    }
}
