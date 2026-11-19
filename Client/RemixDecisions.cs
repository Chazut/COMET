using System;
using System.Runtime.CompilerServices;
using EFT;
using HarmonyLib;
using SAIN;
using SAIN.Components;
using SAIN.Preset;
using SAIN.Preset.Shared.Enums;
using SAIN.SAINComponent.Classes.Decision;
using SAIN.SAINComponent.Classes.Talk;
using SAIN.SAINComponent.Classes.WeaponFunction;
using UnityEngine;

namespace Comet.Client;

public sealed partial class Plugin
{
    private ConditionalWeakTable<BotComponent, RemixBot> remixBots = new();

    private sealed class RemixBot
    {
        internal bool RolledThisWound;
        internal bool PresetApplied;
    }

    private static class RemixSettings
    {
        internal static readonly Func<SquadDecisionClass, BotComponent, bool> SharesEnemy =
            (Func<SquadDecisionClass, BotComponent, bool>)
                AccessTools
                    .Method(typeof(SquadDecisionClass), "doesMemberShareEnemy")
                    .CreateDelegate(typeof(Func<SquadDecisionClass, BotComponent, bool>));
        internal static readonly System.Reflection.MethodInfo Talk = AccessTools.DeclaredMethod(
            typeof(EnemyTalk),
            "UpdatePresetSettings"
        );
        internal static readonly System.Reflection.MethodInfo Grenades = AccessTools.DeclaredMethod(
            typeof(GrenadeThrowDecider),
            "UpdatePresetSettings"
        );
    }

    private static void PatchRemixDecisions(Harmony h)
    {
        h.Patch(
            AccessTools.Method(typeof(SquadDecisionClass), "shallGroupSearch", Type.EmptyTypes),
            prefix: new HarmonyMethod(typeof(Plugin), nameof(RemixGroupSearch))
        );
        h.Patch(
            AccessTools.Method(typeof(BotComponent), "InitializeBot"),
            postfix: new HarmonyMethod(typeof(Plugin), nameof(RemixBotSpawned))
        );
        h.Patch(
            AccessTools.Method(typeof(EnemyTalk), "UpdatePresetSettings"),
            postfix: new HarmonyMethod(typeof(Plugin), nameof(RemixTalkUpdated))
        );
        h.Patch(
            AccessTools.Method(typeof(GrenadeThrowDecider), "UpdatePresetSettings"),
            postfix: new HarmonyMethod(typeof(Plugin), nameof(RemixGrenadeUpdated))
        );
        h.Patch(
            AccessTools.Method(typeof(EnemyTalk), "ShallFakeDeath"),
            prefix: new HarmonyMethod(typeof(Plugin), nameof(RemixWoundRoll))
        );
        h.Patch(
            AccessTools.Method(typeof(EnemyTalk), "tryFakeDeathGrenade"),
            prefix: new HarmonyMethod(typeof(Plugin), nameof(RemixGrenadeScream))
        );
    }

    private static bool RemixGroupSearch(SquadDecisionClass __instance, ref bool __result)
    {
        var bot = __instance.Bot;
        var p = Instance;
        if (p == null || !p.NativeFixScope(bot))
            return true;
        var leader = bot.Squad.SquadInfo?.LeaderComponent;
        __result =
            leader != null
            && leader != bot
            && bot.GoalEnemy != null
            && leader.Decision.CurrentCombatDecision == ECombatDecision.Search
            && RemixSettings.SharesEnemy(__instance, leader);
        if (__result)
            p.CountNativeFix(NativeFix.FollowSearchingLeader, bot);
        return false;
    }

    private static void RemixBotSpawned(BotComponent __instance, bool __result)
    {
        if (__result)
            Instance?.ConfigureRemixBot(__instance);
    }

    // Initial bots can finish SAIN initialization before the async raid config arrives.
    private void ConfigureExistingRemixBots()
    {
        var manager = BotManagerComponent.Instance;
        if (manager == null || !ReferenceEquals(manager.GameWorld, world))
            return;
        RememberRemixManager(manager);
        if (manager.Bots == null)
            return;
        foreach (var bot in manager.Bots.Values)
            ConfigureRemixBot(bot);
    }

    private void ConfigureRemixBot(BotComponent bot)
    {
        if (
            config?.Enabled != true
            || world == null
            || !Authority
            || !NativeAdapter.IsSupportedAi(bot)
        )
            return;
        if (!remixBots.TryGetValue(bot, out var state))
        {
            state = new RemixBot();
            remixBots.Add(bot, state);
            RegisterPathOwner(bot);
            RemoveCachedDoorCover(bot);
        }
        var preset = SAINPlugin.LoadedPreset;
        if (state.PresetApplied || bot.Info?.FileSettings == null || preset?.GlobalSettings == null)
            return;
        try
        {
            var args = new object[] { preset };
            RemixSettings.Talk.Invoke(bot.Talk.EnemyTalk, args);
            RemixSettings.Grenades.Invoke(bot.Grenade.GrenadeThrowDecider, args);
            state.PresetApplied = true;
            CountNativeFix(NativeFix.SpawnPreset, bot);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(
                $"SAIN spawn preset for {bot.ProfileId}: {ex.GetBaseException().Message}"
            );
        }
    }

    private static void RemixTalkUpdated(
        EnemyTalk __instance,
        SAINPresetClass preset,
        ref bool ____canRespondToEnemy
    )
    {
        var bot = __instance.Bot;
        if (Instance?.remixBots.TryGetValue(bot, out _) != true)
            return;
        if (bot.Info?.FileSettings != null)
            ____canRespondToEnemy &= bot.Info.FileSettings.Mind.BotTaunts;
        bot.Talk.GroupTalk.updateConfigSettings(preset);
    }

    private static void RemixGrenadeUpdated(
        GrenadeThrowDecider __instance,
        ref float ____minThrowDistPercent
    )
    {
        // Native UpdatePresetSettings also changes the trajectory cutoff to .66.
        // Remix7.6 explicitly preserves the pre-update cutoff, including on F6 reloads.
        if (Instance?.remixBots.TryGetValue(__instance.Bot, out _) == true)
            ____minThrowDistPercent = 0f;
    }

    private static bool RemixWoundRoll(
        EnemyTalk __instance,
        ref bool __result,
        bool ___CanFakeDeath,
        float ___FakeDeathChance,
        ref float ____fakeDeathTimer
    )
    {
        var p = Instance;
        var bot = __instance.Bot;
        if (p == null || !p.NativeFixScope(bot) || !p.remixBots.TryGetValue(bot, out var state))
            return true;
        __result = false;
        var health = bot.Memory.Health.HealthStatus;
        if (health != ETagStatus.BadlyInjured && health != ETagStatus.Dying)
        {
            state.RolledThisWound = false;
            return false;
        }
        if (
            state.RolledThisWound
            || !___CanFakeDeath
            || !bot.Talk.CanTalk
            || bot.GoalEnemy == null
            || bot.Squad.BotInGroup
            || ____fakeDeathTimer >= Time.time
            || (bot.GoalEnemy.EnemyPosition - bot.BotOwner.Position).sqrMagnitude >= 70f * 70f
        )
            return false;
        state.RolledThisWound = true;
        p.CountNativeFix(NativeFix.WoundVoiceRoll, bot);
        if (!MyExtensions.RandomBool(___FakeDeathChance))
            return false;
        ____fakeDeathTimer = Time.time + 30f;
        bot.Talk.Say(EPhraseTrigger.OnDeath);
        __result = true;
        return false;
    }

    private static bool RemixGrenadeScream(EnemyTalk __instance) =>
        Instance?.NativeFixScope(__instance.Bot) != true || __instance.Bot.Talk.CanTalk;
}
