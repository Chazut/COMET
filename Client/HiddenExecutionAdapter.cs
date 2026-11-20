using System;
using System.Collections.Generic;
using Comet.Core;
using HarmonyLib;
using SAIN.Components;
using SAIN.Preset.Shared.Enums;
using SAIN.Preset.Shared.Models.Enums;
using SAIN.SAINComponent.Classes.Decision;
using SAIN.SAINComponent.Classes.EnemyClasses;
using UnityEngine;
using UnityEngine.AI;

namespace Comet.Client;

internal sealed class HiddenExecutionRuntime
{
    internal readonly HiddenMovementWatch Movement = new();
    internal readonly Dictionary<string, string> Refusals = new();
    internal float HoldOtherBanUntil,
        PickAt = -100,
        LogAt = -100;
    internal Enemy? PickEnemy;
    internal Tactic? Pick;
    internal string LastReason = "";
}

public sealed partial class Plugin
{
    private delegate bool NativeHiddenDecision(
        EnemyDecisionClass decision,
        Enemy enemy,
        out string reason
    );
    private static NativeHiddenDecision nativeShift = null!,
        nativeRush = null!,
        nativeDistantShot = null!;

    private static void PatchHiddenExecution(Harmony h)
    {
        nativeShift = AccessTools.MethodDelegate<NativeHiddenDecision>(
            AccessTools.Method(typeof(EnemyDecisionClass), "shallShiftCover")
        );
        nativeRush = AccessTools.MethodDelegate<NativeHiddenDecision>(
            AccessTools.Method(typeof(EnemyDecisionClass), "shallRushEnemy")
        );
        nativeDistantShot = AccessTools.MethodDelegate<NativeHiddenDecision>(
            AccessTools.Method(typeof(EnemyDecisionClass), "shallShootDistantEnemy")
        );
        h.Patch(AccessTools.Method(typeof(EnemyDecisionClass), "shallRushEnemy"),
            postfix: new HarmonyMethod(typeof(Plugin), nameof(AfterNativeDoorRush)));
        h.Patch(AccessTools.Method(typeof(EnemyDecisionClass), "GetDecision"),
            postfix: new HarmonyMethod(typeof(Plugin), nameof(FinishNativeDoorChoice)));
    }

    // Called only with a blocked shot. SAIN can still be finishing a distant-shot decision.
    private static bool HiddenSightMayTakeOver(BotComponent bot, Enemy enemy, VisibleChoice? visible) =>
        visible?.Decision == null
        && bot.Suppression.CurrentState is not (ESuppressionState.Heavy or ESuppressionState.Extreme)
        && !(bot.Decision.CurrentCombatDecision == ECombatDecision.ShootDistantEnemy
            && nativeDistantShot(bot.Decision.EnemyDecisions, enemy, out _));

    // On native fallback this hook runs inside SAIN's aggressive branch, after its actual rush check.
    private static void AfterNativeDoorRush(EnemyDecisionClass __instance, Enemy enemy, ref bool __result)
    {
        var p = Instance;
        var bot = __instance.Bot;
        if (p == null || !p.states.TryGetValue(bot, out var s) || !s.Door.NativeFallback)
            return;
        s.Door.NativeFallback = false;
        if (__result)
            return;
        try
        {
            if (p.EnsureDoorSession(bot, enemy, s, false))
            {
                s.Door.NativeSelected = true;
                __result = true;
            }
        }
        catch (Exception ex)
        {
            s.Door.Fault = true;
            p.Logger.LogWarning("COMET native door selection yielded to SAIN: " + ex);
        }
    }

    private static void FinishNativeDoorChoice(EnemyDecisionClass __instance, ref ECombatDecision result)
    {
        var p = Instance;
        if (p == null || !p.states.TryGetValue(__instance.Bot, out var s))
            return;
        s.Door.NativeFallback = false;
        if (!s.Door.NativeSelected)
            return;
        s.Door.NativeSelected = false;
        if (result != ECombatDecision.RushEnemy)
            return;
        s.Handoff.Clear();
        result = s.Expected = s.Door.SessionPlan == "trap" ? ECombatDecision.Freeze : ECombatDecision.Search;
        s.Decision = s.Door.SessionPlan == "trap" ? "Hold" : "Push";
        s.RequestedFrame = Time.frameCount;
    }

    private void WatchHiddenMovement(BotComponent b, Enemy e, BotState s)
    {
        var r = s.HiddenExecution;
        string reason = r.Movement.Observe(
            Time.time,
            DP(b.Position),
            b.Decision.CurrentCombatDecision.ToString(),
            r.LastReason == "utilityFallBack",
            b.Cover.CoverInUse != null,
            e.KnownPlaces.BotDistanceFromLastKnown
        );
        if (reason.Length == 0)
            return;
        if (
            reason.StartsWith("zzap-move-stalled-", StringComparison.Ordinal)
            && e.KnownPlaces.LastKnownPlace is { } place
        )
            place.HasArrivedPersonal = true;
        RecordHiddenExecution(b, s, reason);
    }

    private static bool HiddenBeingShot(BotComponent b)
    {
        if (b.BotOwner.Memory.IsUnderFire || b.Medical.TimeSinceShot < 1.5f)
            return true;
        foreach (var e in b.EnemyController.KnownEnemies)
            if (e?.Status != null && (e.Status.ShotAtMeRecently || e.Status.ShotMeRecently))
                return true;
        return false;
    }

    // Called lazily in source preference order. Native decisions can update timers;
    // do not preflight every stance before knowing which one is being considered.
    private string HiddenCanRun(BotComponent b, Enemy e, BotState s, Tactic tactic, bool beingShot)
    {
        var r = s.HiddenExecution;
        string reason = "";
        switch (tactic)
        {
            case Tactic.Push:
                if (
                    Time.time < r.Movement.MoveBanUntil
                    || e.KnownPlaces.BotDistanceFromLastKnown < 2.5f
                        && e.TimeSinceLastKnownUpdated > 3
                )
                    reason = "zzap-push-nowhere";
                else if (!nativeRush(b.Decision.EnemyDecisions, e, out reason))
                {
                    if (EnsureDoorSession(b, e, s, false))
                        return "";
                    reason =
                        b.Info.Personality.ToString() != "Coward"
                        && e.Path.PathToEnemyStatus == NavMeshPathStatus.PathComplete
                        && e.Path.PathLength <= 40
                        && SAIN.SAINComponent.Classes.WeaponFunction.SAINBotSuppressClass.CalcAmmoRatio(
                            b.BotOwner,
                            out _
                        ) >= .35f
                            ? ""
                            : "native-push:" + reason;
                    if (reason.Length == 0 && config!.DoorApproach.Enabled && b.Info.Personality.ToString() != "Wreckless"
                        && e.KnownPlaces.LastKnownPosition is Vector3 known
                        && NearestTacticalDoor(b, e, s, known) != null && ZzapEnemyWeakness.Score(b, e) < .5f)
                        reason = "zzap-push-not-through-door";
                }
                else
                    reason = "";
                break;
            case Tactic.Hold:
                if (beingShot)
                    reason = "zzap-hold-under-fire";
                else if (Time.time < r.HoldOtherBanUntil || SourceOtherEnemyMaySeeMe(b, e))
                {
                    if (Time.time >= r.HoldOtherBanUntil)
                        r.HoldOtherBanUntil = Time.time + 4;
                    reason = "zzap-hold-other-enemy";
                }
                break;
            case Tactic.Flank:
                if (!nativeShift(b.Decision.EnemyDecisions, e, out var shiftReason))
                    reason = "native-shift:" + shiftReason;
                break;
            case Tactic.Search:
                if (Time.time < r.Movement.MoveBanUntil)
                    reason = "zzap-search-stalled";
                else if (!SearchExecutionAdapter.CanStart(b.Search, e, out reason)) { }
                else
                {
                    reason = "";
                    if (b.Decision.CurrentCombatDecision != ECombatDecision.Search)
                        e.Status.NumberOfSearchesStarted++;
                }
                break;
            case Tactic.FallBack:
                if (Time.time < r.Movement.FallBackBanUntil)
                    reason = "zzap-fallback-stalled";
                else if (b.Cover.CoverPoints == null || b.Cover.CoverPoints.Count == 0)
                    reason = "zzap-fallback-no-cover";
                break;
            case Tactic.Grenade:
                if (!NativeAdapter.Throw(b, e, out var grenadeReason))
                    reason = "native-throw-refused:" + grenadeReason;
                break;
        }
        if (reason.Length > 0)
            r.Refusals[tactic.ToString()] = reason;
        return reason;
    }

    private Choice KeepHiddenChoice(
        BotComponent b,
        Enemy e,
        BotState s,
        Choice choice,
        bool beingShot
    )
    {
        var r = s.HiddenExecution;
        if (choice.Decision is Tactic selected)
        {
            r.Pick = selected;
            r.PickEnemy = e;
            r.PickAt = Time.time;
            r.LastReason = "utility" + selected;
            return choice;
        }
        bool sameFight =
            ReferenceEquals(r.PickEnemy, e)
            || (r.PickEnemy != null && !e.IsVisible && e.RealDistance > 15);
        // The pinned source constant is 2 seconds; its old comment still says 1.2.
        if (
            sameFight
            && Time.time - r.PickAt < 2
            && !beingShot
            && r.Pick is Tactic pick
            && pick != Tactic.Grenade
            && b.Decision.CurrentCombatDecision == NativeAdapter.Map(pick)
        )
        {
            r.LastReason = "utilityKeep";
            return s.Engine.Keep(pick, e.EnemyProfileId);
        }
        r.LastReason = "";
        return choice;
    }

    private void RecordHiddenExecution(BotComponent b, BotState s, string reason)
    {
        // Observation only. Bounded even when different refusals alternate each tick.
        var r = s.HiddenExecution;
        if (Time.time - r.LogAt < 3)
            return;
        r.LogAt = Time.time;
        try
        {
            var e = b.GoalEnemy;
            var path = b.Mover.ActivePath;
            var corners = e?.Path.PathCorners;
            int count = e?.Path.PathCornerCount ?? 0;
            var end = corners != null && count > 0 ? corners[count - 1] : (Vector3?)null;
            string json = Diagnostics.Serialize(
                new
                {
                    schema = 1,
                    session,
                    sequence = ++sequence,
                    feature = "A01",
                    stage = "execution-check",
                    decision = s.Decision,
                    reason,
                    realtime = Time.realtimeSinceStartup,
                    profileId = s.ProfileId,
                    name = s.Name,
                    botInstance = s.Id,
                    hiddenExecution = new
                    {
                        target = e?.EnemyProfileId,
                        native = b.Decision.CurrentCombatDecision.ToString(),
                        refusals = r.Refusals,
                        enemyPath = e?.Path.PathToEnemyStatus.ToString(),
                        pathEnd = end.HasValue
                            ? new
                            {
                                x = end.Value.x,
                                y = end.Value.y,
                                z = end.Value.z,
                            }
                            : null,
                        searchState = b.Search.CurrentState.ToString(),
                        searchHasTarget = b.Search.PathFinder.TargetPlace != null,
                        moving = b.Mover.Moving,
                        moveStatus = path?.Status.ToString(),
                        destination = path == null
                            ? null
                            : new
                            {
                                x = path.Destination.x,
                                y = path.Destination.y,
                                z = path.Destination.z,
                            },
                        shiftComplete = b.Decision.EnemyDecisions.ShiftCoverComplete,
                        coverStatus = s.Flank.Point?.StraightDistanceStatus.ToString(),
                        inSelectedCover = s.Flank.Point?.BotInThisCover,
                        moveBanRemaining = Math.Max(0, r.Movement.MoveBanUntil - Time.time),
                        fallBackBanRemaining = Math.Max(0, r.Movement.FallBackBanUntil - Time.time),
                    },
                }
            );
            diagnostics?.Write(json);
            Api.Decisions.Publish(json, _ => { });
        }
        catch
        { /* Diagnostic failure must not change the selected action. */
        }
    }
}
