using System;
using System.Runtime.CompilerServices;
using Comet.Core;
using HarmonyLib;
using EFT;
using Newtonsoft.Json;
using SAIN.Components.BotComponentSpace.Classes.EnemyClasses;
using SAIN.Models.Enums;
using UnityEngine.AI;
using SAIN.Preset.Shared.Enums;
using SAIN.SAINComponent.Classes.EnemyClasses;
using UnityEngine;

namespace Comet.Client;

public sealed partial class Plugin
{
    private static readonly AccessTools.FieldRef<EnemyKnownChecker, Enemy> abandonmentEnemy =
        AccessTools.FieldRefAccess<EnemyKnownChecker, Enemy>("Enemy");

    private static void PatchSearchAbandonment(Harmony h)
    {
        h.Patch(
            AccessTools.Method(typeof(EnemyKnownChecker), "ShallKnowEnemy"),
            postfix: new HarmonyMethod(typeof(Plugin), nameof(CheckSearchedPlace))
        );
        h.Patch(
            AccessTools.Method(typeof(EnemyKnownChecker), nameof(EnemyKnownChecker.TickEnemy)),
            postfix: new HarmonyMethod(typeof(Plugin), nameof(SearchForgetObserved))
        );
    }

    private static bool StalledOnUnreachableContact(BotState s, Enemy e, float now, double ambushRadius)
    {
        var watch = s.StalledCover;
        var b = e.Bot;
        var place = e.KnownPlaces;
        bool idle = ReferenceEquals(b.GoalEnemy, e) && b.SAINLayersActive
            && BotEligibility.Kind(b.Player) == "pmc" && !e.IsVisible
            && b.Decision.CurrentCombatDecision == ECombatDecision.SeekCover
            && b.Decision.CurrentSelfDecision == ESelfActionType.None
            && b.Decision.CurrentSquadDecision == ESquadDecision.None
            && !b.Mover.Moving && b.Mover.ActivePath == null
            && e.Path.PathToEnemyStatus != NavMeshPathStatus.PathComplete
            && b.Search.PathFinder.TargetPlace == null
            && !(b.Cover.CoverInUse?.BotInThisCover == true && place.BotDistanceFromLastKnown <= ambushRadius)
            && !s.HoldAngle.Policy.Active
            && !(s.AngleWatch.Policy.Active && s.AngleWatch.Policy.Trace?.Kind == "corner")
            && b.Aim.AimStatus == AimStatus.NoTarget
            && !b.Suppression.IsSuppressed && b.ManualShoot.Reason == EShootReason.None
            && !b.BotOwner.ShootData.Shooting && !HiddenBeingShot(b, 5f);
        return watch.ShouldRelease(e, now, place.TimeLastKnownUpdated, DP(b.Position), idle);
    }

    private static bool NeedsAbandonment(EnemyKnownChecker checker, bool pending)
    {
        var p = Instance;
        if (p?.config == null)
            return false;
        var e = abandonmentEnemy(checker);
        return p.states.TryGetValue(e.Bot, out var s)
            && (!pending || s.AbandonPending?.Target == e.EnemyProfileId);
    }

    private static void CheckSearchedPlace(
        EnemyKnownChecker __instance,
        float currentTime,
        ref bool __result
    )
    {
        var p = Instance;
        if (p?.config == null)
            return;
        var e = abandonmentEnemy(__instance);
        var b = e.Bot;
        if (!p.states.TryGetValue(b, out var s))
            return;
        try
        {
            if (
                s.AbandonFault
                || !p.config.Enabled
                || !p.config.SearchAbandonment.Enabled
                || !NativeAdapter.IsSupportedAi(b)
                || !b.BotActive
                || b.IsDead
                || b.GameEnding
                || !p.Authority
                || p.Ghost(b.ProfileId)
            )
                return;
            var place = e.KnownPlaces.LastKnownPlace;
            if (!__result || place == null)
                return;
            if (s.StalledCover.IsCurrent(e, currentTime)
                && StalledOnUnreachableContact(s, e, currentTime, p.config.AmbushRadius))
            {
                __result = false; // SAIN clears this bot's local knowledge and handles future reacquisition.
                p.ArbitrationEvent(s, "unreachable-stale-seek-cover", "ForgetContact");
                return;
            }
            var point = place.Position;
            var f = new SearchAbandonmentFrame
            {
                Now = currentTime,
                Stamp = e.KnownPlaces.TimeLastKnownUpdated,
                Target = e.EnemyProfileId,
                Instance = RuntimeHelpers.GetHashCode(e),
                X = point.x,
                Y = point.y,
                Z = point.z,
                Distance = Vector3.Distance(b.Position, point),
                NativeKnown = __result,
                Visible = e.IsVisible,
                PersonalSearch = place.HasArrivedPersonal || place.HasArrivedSquad,
            };
            var t = s.Abandonment.Evaluate(f, p.config.SearchAbandonment);
            p.RecordAbandonment(s);
            if (t.ForgetRequested)
            {
                s.AbandonPending = f;
                __result = false;
            }
        }
        catch (Exception ex)
        {
            s.AbandonFault = true;
            p.EndAbandonment(s, "search-check-adapter-error", true);
            p.Logger.LogWarning("COMET P09 yielded: " + ex.Message);
        }
    }

    private static void SearchForgetObserved(EnemyKnownChecker __instance)
    {
        var p = Instance;
        if (p?.config == null)
            return;
        var e = abandonmentEnemy(__instance);
        if (
            !p.states.TryGetValue(e.Bot, out var s)
            || s.AbandonPending is not SearchAbandonmentFrame f
            || f.Target != e.EnemyProfileId
        )
            return;
        s.AbandonPending = null;
        if (
            s.Abandonment.ObserveForgotten(
                f,
                p.config.SearchAbandonment,
                !e.EnemyKnown && e.KnownPlaces.LastKnownPlace == null
            )
        )
            p.RecordAbandonment(s, true);
    }

    private void EndAbandonment(BotState s, string reason, bool clear = false)
    {
        if (
            s.Abandonment.Current is SearchAbandonmentTrace t
            && (t.State == "reached" || t.State == "checking" || t.State == "requested")
        )
        {
            s.Abandonment.Interrupt(reason);
            RecordAbandonment(s, true);
        }
        s.AbandonPending = null;
        if (clear)
        {
            s.Abandonment.Clear();
            s.AbandonLogs.Clear();
        }
    }

    private void RecordAbandonment(BotState s, bool force = false)
    {
        var t = s.Abandonment.Current;
        if (t == null)
            return;
        if (!s.AbandonLogs.Allow(t.Target, t.State, t.Reason, Time.time, force))
            return;
        // Read cached native state only when a throttled diagnostic is emitted.
        // These fields do not participate in the source abandonment decision.
        try
        {
            var search = s.Owner.Search;
            t.NativeSearchState = search?.CurrentState.ToString() ?? "unavailable";
            t.NativeSearchTarget = search?.SearchTarget?.EnemyProfileId ?? "";
            t.NativeSearchActive = search?.SearchActive == true;
            t.NativeSearchHasTarget = search?.PathFinder?.TargetPlace != null;
            var goal = s.Owner.GoalEnemy;
            t.EnemyPathStatus =
                goal?.EnemyProfileId == t.Target
                    ? goal.Path?.PathToEnemy?.status.ToString() ?? "none"
                    : "not-current-target";
        }
        catch
        {
            t.NativeSearchState = "unavailable";
        }
        var json = Diagnostics.Serialize(
            new
            {
                schema = 1,
                session,
                sequence = ++sequence,
                feature = "P09",
                stage = "evaluated",
                decision = "SearchAbandonment",
                reason = t.Reason,
                realtime = Time.realtimeSinceStartup,
                utc = DateTime.UtcNow.ToString("O"),
                configRevision = config?.Revision ?? 0,
                searchAbandonmentConfig = config?.SearchAbandonment,
                profileId = s.ProfileId,
                name = s.Name,
                botInstance = s.Id,
                searchAbandonment = t,
            }
        );
        diagnostics?.Write(json);
        Api.Decisions.Publish(json, ex =>
        {
            if (!subscriberWarned)
            {
                subscriberWarned = true;
                Logger.LogWarning(ex.Message);
            }
        });
    }
}
