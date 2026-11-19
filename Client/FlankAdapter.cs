using System;
using System.Collections.Generic;
using Comet.Core;
using HarmonyLib;
using SAIN.Components;
using SAIN.Models.Enums;
using SAIN.Preset.Shared.Enums;
using SAIN.SAINComponent.SubComponents.CoverFinder;
using UnityEngine;

namespace Comet.Client;

internal sealed class FlankRuntime
{
    internal CoverPoint? Point;
    internal bool MoveAccepted;
    internal string Target = "",
        Reason = "";

    internal void Reset()
    {
        Point = null;
        MoveAccepted = false;
        Target = "";
        Reason = "";
    }
}

// Observe the point and path selected by native ShiftCoverAction.
internal static class FlankAdapter
{
    internal static bool Arrived(BotComponent b, BotState s) =>
        s.Flank.MoveAccepted
        && s.Flank.Point is CoverPoint p
        && (b.Position - p.Position).sqrMagnitude <= 2.25f
        && (
            p.BotInThisCover
            || b.Mover.ActivePath?.Status == EBotMoveStatus.Complete
                && (b.Mover.ActivePath.Destination - p.Position).sqrMagnitude <= 1
        );
}

public sealed partial class Plugin
{
    private static void PatchFlank(Harmony h)
    {
        var type = AccessTools.TypeByName("SAIN.Layers.Combat.Solo.ShiftCoverAction")
            ?? typeof(SAIN.Components.BotComponent).Assembly.GetType(
                "SAIN.Layers.Combat.Solo.ShiftCoverAction"
            );
        // Resolve once at startup against the pinned SAIN binary.
        if (type == null)
            foreach (var candidate in typeof(BotComponent).Assembly.GetTypes())
                if (candidate.Name == "ShiftCoverAction")
                {
                    type = candidate;
                    break;
                }
        if (type == null || AccessTools.Field(type, "NewPoint")?.FieldType != typeof(CoverPoint))
            throw new MissingFieldException("SAIN ShiftCoverAction.NewPoint");
        var method = AccessTools.Method(type, "FindPointToGo");
        if (method == null || method.ReturnType != typeof(bool))
            throw new MissingMethodException("SAIN ShiftCoverAction.FindPointToGo");
        h.Patch(method, postfix: new HarmonyMethod(typeof(Plugin), nameof(ObserveFlankPoint)));
    }

    private static void ObserveFlankPoint(object __instance, CoverPoint ___NewPoint, bool __result)
    {
        var p = Instance;
        var b = FaultOwner(__instance);
        if (
            p?.config?.Enabled != true
            || b == null
            || !p.states.TryGetValue(b, out var s)
            || s.Feature != "A01"
            || s.ActiveLabel != "COMET:Flank"
            || !p.TacticalScope(b, b.GoalEnemy)
        )
            return;
        // Keep native point selection, UsedPoints, completion and movement untouched.
        // These references are diagnostic evidence only.
        s.Flank.Point = ___NewPoint;
        s.Flank.Target = b.GoalEnemy?.EnemyProfileId ?? "";
        s.Flank.MoveAccepted = __result && ___NewPoint != null;
        s.Flank.Reason = __result ? "" : "flank-native-no-point";
        p.RecordHiddenExecution(
            b,
            s,
            __result ? "flank-native-route-accepted" : "flank-native-no-point"
        );
    }
}
