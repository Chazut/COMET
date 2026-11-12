using System;
using System.Reflection;
using Comet.Core;
using HarmonyLib;
using SAIN.Components;
using SAIN.SAINComponent.Classes.Mover;
using SAIN.SAINComponent.Classes.WeaponFunction;
using SAIN.SAINComponent.Classes.EnemyClasses;
using UnityEngine;

namespace Comet.Client;

public sealed partial class Plugin
{
    private static PropertyInfo actionBot = null!;
    private static void PatchSightContinuity(Harmony harmony)
    {
        harmony.Patch(AccessTools.Method(typeof(SAINSteeringClass), "SteerByPriority"), prefix: new HarmonyMethod(typeof(Plugin), nameof(SteerFrozenSight)));
        harmony.Patch(AccessTools.Method(typeof(SAINSteeringClass), "LookToEnemy"), prefix: new HarmonyMethod(typeof(Plugin), nameof(LookFrozenSight)));
    }
    private void RefreshSight(BotComponent bot, BotState state)
    {
        try
        {
            if (config == null || !config.Enabled || !config.SightContinuity.Enabled)
                state.Sight.Reset("sight-continuity-disabled");
            else if (Time.time < state.FaultUntil) state.Sight.Reset("sight-adapter-cooldown");
            else if (bot == null || bot.IsDead || !Authority || Ghost(state.ProfileId))
            { state.Sight.Reset("sight-ineligible"); state.SightPath = null; }
            else
            {
                RefreshFear(bot, state);
                SightAdapter.Capture(bot, state, Authority, false);
            }
            if (state.Feature == "A03" && state.Sight.Current == null && state.Decision.Length > 0)
            {
                string reason = state.Sight.Reason == "sight-armed" || state.Sight.Reason == "sight-waiting-stable-view"
                    ? "sight-reacquired" : state.Sight.Reason;
                // Reacquisition is a COMET-to-COMET transition at the next decision commit.
                // Keep the current native action until then. Invalid/expired memory still releases immediately.
                if(reason != "sight-reacquired")Release(state, reason, false);
            }
        }
        catch (Exception ex)
        {
            state.Sight.Reset("sight-adapter-error"); state.SightPath = null;
            bool warn=Time.time>=state.FaultUntil;
            YieldToSain(state, "sight-adapter-error", "error");
            if (warn) Logger.LogWarning("COMET sight continuity yielded: " + ex.Message);
        }
    }
    private static Situation SightSituation(Situation situation, SightHold sight)
    {
        situation.KnownX = sight.X; situation.KnownY = sight.Y; situation.KnownZ = sight.Z;
        situation.KnownDistance = sight.Distance; situation.ContactSource = "seen";
        situation.ContactAge = Math.Max(0, situation.Now - sight.SeenAt);
        return situation;
    }
    private static bool TrySight(BotComponent bot, out BotState state, out SightHold sight)
    {
        state = null!; sight = null!;
        var p = Instance;
        if (p?.config == null || !p.config.Enabled || !p.config.SightContinuity.Enabled || bot == null
            || !p.states.TryGetValue(bot, out state) || state.Feature != "A03" || state.ActiveLabel.Length == 0) return false;
        p.RefreshSight(bot, state);
        sight = state.Sight.Current!;
        return sight != null && state.ActiveLabel.Length > 0;
    }
    private static bool SteerFrozenSight(SAINSteeringClass __instance, Enemy enemy, ref bool __result)
    {
        if (!TrySight(__instance.Bot, out var state, out var sight)) return true;
        if (enemy != null && enemy.EnemyProfileId != sight.Target) return true;
        __instance.LookToPoint(new Vector3((float)sight.X, (float)sight.Y + state.SightAimHeight, (float)sight.Z));
        __result = true; return false;
    }
    private static bool LookFrozenSight(SAINSteeringClass __instance, Enemy enemy)
    {
        if (enemy == null || !TrySight(__instance.Bot, out var state, out var sight) || enemy.EnemyProfileId != sight.Target) return true;
        __instance.LookToPoint(new Vector3((float)sight.X, (float)sight.Y + state.SightAimHeight, (float)sight.Z));
        return false;
    }
    private static bool NoBlindContinuityFire(ManualShootClass __instance, Enemy Enemy, ref bool __result)
    {
        // Only M06's current guarded call may prefire. A03 still forbids every other blind request.
        if (Instance?.PermitPrefireRequest(__instance, Enemy) == true) return true;
        if (!TrySight(__instance.Bot, out _, out _) || Enemy?.IsVisible == true) return true;
        __instance.Reset(); __result = false; return false;
    }
    private static bool ContinueFrozenRush(object __instance)
    {
        try
        {
            var bot = actionBot.GetValue(__instance) as BotComponent;
            return bot == null || !TrySight(bot, out _, out var sight) || sight.Action != "Push";
        }
        catch (Exception ex) { Instance?.Logger.LogWarning("COMET rush continuity yielded: " + ex.Message); return true; }
    }
}
