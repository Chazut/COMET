using System;
using System.Collections.Generic;
using Comet.Core;
using EFT;
using HarmonyLib;
using SAIN;
using SAIN.Components;
using SAIN.Components.PlayerComponentSpace;
using SAIN.SAINComponent.Classes.EnemyClasses;
using SAIN.SAINComponent.Classes.WeaponFunction;
using SAIN.Types.Jobs;
using UnityEngine;
using SainVector = SAIN.Helpers.Vector;

namespace Comet.Client;

// Adapted from liquidwarp/SPT-SAIN. See THIRD_PARTY_NOTICES.md for exact commits.
// These patches correct native calculations, not tactical eligibility.
public sealed partial class Plugin
{
    private enum NativeFix { PathRays, DeferredPathCache, ClearedPathCache, GrenadePrediction,
        TargetActivity, SelfTarget, RecoilStep, SignedRecoil, PoseStep, LeanStep, Count }
    private readonly long[] nativeFixCounts = new long[(int)NativeFix.Count];
    private static readonly string[] nativeFixLabels = { "PathRays", "DeferredPathCache", "ClearedPathCache",
        "GrenadePrediction", "TargetActivity", "SelfTarget", "RecoilStep", "SignedRecoil", "PoseStep", "LeanStep" };
    private readonly string[] nativeFixBots = new string[(int)NativeFix.Count];
    private bool nativeFixDirty;
    private float nativeFixNext;

    private bool NativeFixScope(BotComponent? bot) => world != null && config?.Enabled == true && Authority
        && bot != null && NativeAdapter.IsSupportedAi(bot) && bot.BotActive && !bot.IsDead
        && !bot.GameEnding && !Ghost(bot.ProfileId);

    private static void PatchLiquidwarpFixes(Harmony h)
    {
        foreach (var ctor in typeof(PathVisionJob).GetConstructors())
            h.Patch(ctor, postfix: new HarmonyMethod(typeof(Plugin), nameof(FixPathVision)));
        h.Patch(AccessTools.Method(typeof(SAINEnemyPath), nameof(SAINEnemyPath.ShallCalcNewPath)),
            prefix: new HarmonyMethod(typeof(Plugin), nameof(SavePathCache)),
            postfix: new HarmonyMethod(typeof(Plugin), nameof(RestoreDeferredPathCache)));
        h.Patch(AccessTools.Method(typeof(SAINEnemyPath), nameof(SAINEnemyPath.Clear)),
            postfix: new HarmonyMethod(typeof(Plugin), nameof(ClearPathCache)));
        h.Patch(AccessTools.Method(typeof(Enemy), nameof(Enemy.IsEnemyActive)),
            prefix: new HarmonyMethod(typeof(Plugin), nameof(FixEnemyActivity)));
        h.Patch(AccessTools.Method(typeof(Enemy), nameof(Enemy.IsEnemyValid)),
            postfix: new HarmonyMethod(typeof(Plugin), nameof(FixEnemyIdentity)));
        h.Patch(AccessTools.Method(typeof(GrenadeController), "GrenadeThrown"),
            prefix: new HarmonyMethod(typeof(Plugin), nameof(BeginGrenadePrediction)),
            finalizer: new HarmonyMethod(typeof(Plugin), nameof(EndGrenadePrediction)));
        h.Patch(AccessTools.Method(typeof(GrenadeReactionClass), nameof(GrenadeReactionClass.EnemyGrenadeThrown)),
            prefix: new HarmonyMethod(typeof(Plugin), nameof(FixGrenadePrediction)));
        h.Patch(AccessTools.Method(typeof(SainVector), "CheckThreePoints"),
            prefix: new HarmonyMethod(typeof(Plugin), nameof(FixGrenadeSegment)));
        PatchNativeTiming(h);
    }

    private static void FixPathVision(ref PathVisionJob __instance)
    {
        var p = Instance;
        if (p == null || !p.NativeFixScope(__instance.Enemy?.Bot)) return;
        var commands = __instance.Commands;
        for (int i = 0; i < commands.Length; i++)
        {
            // Both native constructors store point-origin as the direction. Keep
            // all other command fields, including the caller's collision query.
            var command = commands[i];
            var delta = command.direction;
            command.direction = delta.normalized;
            command.distance = delta.magnitude;
            commands[i] = command;
        }
        p.CountNativeFix(NativeFix.PathRays, __instance.Enemy?.Bot, commands.Length);
    }

    private struct PathCacheSnapshot
    {
        internal bool Scoped;
        internal Vector3? Enemy;
        internal Vector3 Bot;
    }
    private static void SavePathCache(SAINEnemyPath __instance, Vector3? ____enemyLastPosChecked,
        Vector3 ____botLastPosChecked, out PathCacheSnapshot __state)
    {
        __state = new() { Scoped = Instance?.NativeFixScope(__instance.Bot) == true,
            Enemy = ____enemyLastPosChecked, Bot = ____botLastPosChecked };
    }
    private static void RestoreDeferredPathCache(SAINEnemyPath __instance, bool __result,
        ref Vector3? ____enemyLastPosChecked, ref Vector3 ____botLastPosChecked, PathCacheSnapshot __state)
    {
        if (!__state.Scoped || __result) return;
        // Same result as moving the existing time gate before checkPositionsChanged:
        // a refused calculation must not consume a change of position.
        bool changed = ____enemyLastPosChecked != __state.Enemy || ____botLastPosChecked != __state.Bot;
        ____enemyLastPosChecked = __state.Enemy;
        ____botLastPosChecked = __state.Bot;
        if (changed) Instance?.CountNativeFix(NativeFix.DeferredPathCache, __instance.Bot);
    }
    private static void ClearPathCache(SAINEnemyPath __instance, ref Vector3? ____enemyLastPosChecked)
    {
        var p = Instance;
        if (p == null || !p.NativeFixScope(__instance.Bot)) return;
        ____enemyLastPosChecked = null;
        p.CountNativeFix(NativeFix.ClearedPathCache, __instance.Bot);
    }

    private static bool FixEnemyActivity(Enemy enemy, ref bool __result)
    {
        var p = Instance;
        if (enemy == null || p == null || !p.NativeFixScope(enemy.Bot)) return true;
        // Original SAIN checks retained, using the target instead of the observer.
        bool active = enemy.EnemyPlayerComponent?.IsActive == true;
        if (active != enemy.PlayerComponent.IsActive) p.CountNativeFix(NativeFix.TargetActivity, enemy.Bot);
        if (active && enemy.IsAI)
        {
            var owner = enemy.EnemyPlayerComponent?.BotOwner;
            active = owner != null && owner.BotState == EBotState.Active
                && owner.StandBy.StandByType == BotStandByType.active;
        }
        __result = active;
        return false;
    }
    private static void FixEnemyIdentity(string botProfileId, PlayerComponent enemyPlayerComp, ref bool __result)
    {
        var p = Instance;
        if (!__result || p == null || !SAINEnableClass.GetSAIN(botProfileId, out var observer)
            || !p.NativeFixScope(observer)) return;
        // Native null/health checks have already run. Its AI/self check was
        // unreachable because the enclosing condition required BotOwner == null.
        if (enemyPlayerComp.IsAI && enemyPlayerComp.BotOwner?.ProfileId == botProfileId)
        {
            __result = false;
            p.CountNativeFix(NativeFix.SelfTarget, observer);
        }
    }

    private sealed class GrenadePrediction
    {
        internal Grenade Grenade = null!;
        internal Vector3 Position, Force, Point;
        internal float Mass;
        internal bool Calculated;
    }
    [ThreadStatic] private static GrenadePrediction? grenadePrediction;
    [ThreadStatic] private static bool correctingGrenadeSegment;
    private static void BeginGrenadePrediction(Grenade grenade, Vector3 position, Vector3 force, float mass,
        out GrenadePrediction? __state)
    {
        __state = grenadePrediction;
        var p = Instance;
        grenadePrediction = p?.world != null && p.config?.Enabled == true && p.Authority
            ? new() { Grenade = grenade, Position = position, Force = force, Mass = mass } : null;
    }
    private static Exception? EndGrenadePrediction(Exception? __exception, GrenadePrediction? __state)
    {
        grenadePrediction = __state;
        return __exception;
    }
    private static void FixGrenadePrediction(GrenadeReactionClass __instance, Grenade grenade, ref Vector3 dangerPoint)
    {
        var p = Instance;
        var prediction = grenadePrediction;
        if (p == null || !p.NativeFixScope(__instance.Bot) || prediction == null
            || !ReferenceEquals(prediction.Grenade, grenade)) return;
        if (!prediction.Calculated)
        {
            bool previous = correctingGrenadeSegment;
            correctingGrenadeSegment = true;
            try { prediction.Point = SainVector.DangerPoint(prediction.Position, prediction.Force, prediction.Mass); }
            finally { correctingGrenadeSegment = previous; }
            prediction.Calculated = true;
        }
        dangerPoint = prediction.Point;
        p.CountNativeFix(NativeFix.GrenadePrediction, __instance.Bot);
    }
    private static bool FixGrenadeSegment(Vector3 from, Vector3 midPoint, Vector3 target,
        ref Vector3 hitPos, ref bool __result)
    {
        if (!correctingGrenadeSegment) return true;
        var first = midPoint - from;
        var second = target - midPoint;
        if (Physics.Raycast(new Ray(from, first), out var hit, first.magnitude, LayersMaskController.HighPolyWithTerrainMask)
            || Physics.Raycast(new Ray(midPoint, second), out hit, second.magnitude, LayersMaskController.HighPolyWithTerrainMask))
        {
            hitPos = hit.point;
            __result = false;
        }
        else { hitPos = target; __result = true; }
        return false;
    }

    private void CountNativeFix(NativeFix kind, BotComponent? bot, int count = 1)
    {
        nativeFixCounts[(int)kind] += count;
        nativeFixBots[(int)kind] = bot?.ProfileId ?? "";
        nativeFixDirty = true;
    }
    private void FlushNativeFixes(bool final = false)
    {
        if (!nativeFixDirty || !final && Time.realtimeSinceStartup < nativeFixNext) return;
        nativeFixDirty = false;
        nativeFixNext = Time.realtimeSinceStartup + 5;
        var json = Diagnostics.Serialize(new { schema = 1, session, sequence = ++sequence, feature = "C11",
            stage = "native-fix-summary", reason = "liquidwarp-native-corrections", final,
            realtime = Time.realtimeSinceStartup, counts = nativeFixCounts, lastBot = nativeFixBots,
            labels = nativeFixLabels, configRevision = config?.Revision ?? 0 });
        if (final) diagnostics?.WriteEssential(json); else diagnostics?.Write(json);
        Api.Decisions.Publish(json, WarnFaultSink);
    }
    private void ClearNativeFixes()
    {
        FlushNativeFixes(true);
        Array.Clear(nativeFixCounts, 0, nativeFixCounts.Length);
        Array.Clear(nativeFixBots, 0, nativeFixBots.Length);
        nativeFixNext = 0;
        recoilClocks = new();
        smoothingClocks = new();
        grenadePrediction = null;
        correctingGrenadeSegment = false;
    }
}
