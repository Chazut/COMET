using System;
using System.Collections.Generic;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using SAIN.Components;
using SAIN.Layers;
using SAIN.Preset.Shared.Enums;
using SAIN.SAINComponent.Classes;
using SAIN.SAINComponent.Classes.Decision;
using SAIN.SAINComponent.SubComponents.CoverFinder;
using UnityEngine;

namespace Comet.Client;

public sealed partial class Plugin
{
    // SAIN owns these actions even for ordinary scavs who have no COMET BotState.
    private static Type nativeSeekAction = null!;
    private readonly ConditionalWeakTable<BotComponent, ShiftOrigin> shiftOrigins = new();

    private bool shiftOriginWarning;

    private sealed class ShiftOrigin
    {
        internal BotAction Action = null!;
        internal CoverPoint Point = null!;
        internal object? Enemy;
        internal int Frame;
    }

    private static void PatchShiftCoverOrigin(Harmony h)
    {
        var assembly = typeof(BotComponent).Assembly;
        nativeSeekAction = assembly.GetType(SeekCoverType, true);
        var shift = assembly.GetType("SAIN.Layers.Combat.Solo.Cover.ShiftCoverAction", true);
        h.Patch(AccessTools.Method(shift, "FindPointToGo"),
            transpiler: new HarmonyMethod(typeof(Plugin), nameof(InsertShiftOrigin)));
        h.Patch(AccessTools.Method(nativeSeekAction, "Stop"),
            prefix: new HarmonyMethod(typeof(Plugin), nameof(CaptureShiftOrigin)));
        h.Patch(AccessTools.Method(shift, "Start"),
            prefix: new HarmonyMethod(typeof(Plugin), nameof(BindShiftOrigin)));
        h.Patch(AccessTools.Method(typeof(EnemyDecisionClass), "shallShiftCover"),
            postfix: new HarmonyMethod(typeof(Plugin), nameof(CheckFreshShiftDestination)));
        h.Patch(AccessTools.Method(shift, "Stop"),
            prefix: new HarmonyMethod(typeof(Plugin), nameof(ForgetShiftOrigin)));
    }

    // A fresh shift has only its origin in UsedPoints. If no point can pass
    // SAIN's own first two comparisons, keep seeking until its next native check.
    // Never probe a path here or interfere with continuation of an actual shift.
    private static void CheckFreshShiftDestination(EnemyDecisionClass __instance, ref bool __result, ref string reason)
    {
        if (!__result)
            return;
        var b = __instance.Bot;
        var p = Instance;
        if (p?.config?.Enabled != true || !p.Authority || b == null || b.CurrentAction?.GetType() != nativeSeekAction
            || b.Decision.CurrentCombatDecision == ECombatDecision.ShiftCover || !b.BotActive || b.IsDead
            || b.GameEnding || p.Ghost(b.ProfileId) || b.Cover.CoverInUse is not CoverPoint origin)
            return;
        foreach (var point in b.Cover.CoverPoints)
            if (point != null && point.CoverHeight > origin.CoverHeight
                && (point.Position - origin.Position).sqrMagnitude > 5f)
                return;
        __result = false;
        reason = "no-native-shift-candidate";
        if (!p.config.QuietLogging)
            p.Logger.LogDebug("SAIN kept SeekCover for " + b.ProfileId + ": " + reason);
    }

    private bool IsNativeShift(BotComponent b) =>
        config?.Enabled == true && Authority && b.BotActive && !b.IsDead && !b.GameEnding
        && b.Decision?.CurrentCombatDecision == ECombatDecision.ShiftCover
        && !Ghost(b.ProfileId);

    private static void CaptureShiftOrigin(BotAction __instance)
    {
        var p = Instance;
        var b = __instance.Bot;
        if (p == null || b == null)
            return;
        p.shiftOrigins.Remove(b);
        var point = b.Cover?.CoverInUse;
        if (point == null || !p.IsNativeShift(b))
            return;
        p.shiftOrigins.Add(b, new ShiftOrigin
        {
            Action = __instance, Point = point, Enemy = b.GoalEnemy, Frame = Time.frameCount,
        });
    }

    // Before native Start replaces CurrentAction, verify that this exact Seek
    // action handed off directly. The first movement update can then run later.
    private static void BindShiftOrigin(BotAction __instance)
    {
        var p = Instance;
        var b = __instance.Bot;
        if (p == null || b == null || !p.shiftOrigins.TryGetValue(b, out var origin))
            return;
        if (Time.frameCount - origin.Frame > 1 || !ReferenceEquals(b.CurrentAction, origin.Action)
            || !ReferenceEquals(b.GoalEnemy, origin.Enemy) || !p.IsNativeShift(b))
            p.shiftOrigins.Remove(b);
        else
            origin.Action = __instance;
    }

    private static void ForgetShiftOrigin(BotAction __instance)
    {
        var b = __instance.Bot;
        if (b != null)
            Instance?.shiftOrigins.Remove(b);
    }

    private static CoverPoint? UseShiftOrigin(CoverPoint? native, BotAction action)
    {
        var p = Instance;
        if (p == null)
            return native;
        try
        {
            var b = action.Bot;
            if (b == null || !p.shiftOrigins.TryGetValue(b, out var origin))
                return native;
            p.shiftOrigins.Remove(b);
            if (native != null || !ReferenceEquals(action, origin.Action) || !ReferenceEquals(action, b.CurrentAction)
                || !ReferenceEquals(b.GoalEnemy, origin.Enemy) || !p.IsNativeShift(b))
                return native;
            if (p.config?.QuietLogging == false)
                p.Logger.LogDebug("SAIN ShiftCover using previous SeekCover origin for " + b.ProfileId);
            return origin.Point;
        }
        catch (Exception ex)
        {
            // This helper runs inside stock SAIN IL, outside Harmony's hook boundary.
            if (!p.shiftOriginWarning)
            {
                p.shiftOriginWarning = true;
                p.Logger.LogWarning("SAIN shift origin unavailable: " + ex.GetBaseException().Message);
            }
            return native;
        }
    }

    // Supply only the comparison point. Native cleanup, UsedPoints, height,
    // distance, path acceptance and failure/retry behavior all remain SAIN's.
    private static IEnumerable<CodeInstruction> InsertShiftOrigin(IEnumerable<CodeInstruction> instructions)
    {
        var getter = AccessTools.PropertyGetter(typeof(SAINCoverClass), nameof(SAINCoverClass.CoverInUse));
        var fallback = AccessTools.Method(typeof(Plugin), nameof(UseShiftOrigin));
        int reads = 0;
        foreach (var instruction in instructions)
        {
            yield return instruction;
            if (instruction.Calls(getter))
            {
                reads++;
                yield return new CodeInstruction(OpCodes.Ldarg_0);
                yield return new CodeInstruction(OpCodes.Call, fallback);
            }
        }
        if (reads != 1)
            throw new InvalidOperationException("Unexpected SAIN ShiftCover origin read count: " + reads);
    }
}
