using System;
using System.Reflection;
using Comet.Core;
using HarmonyLib;
using SAIN.Classes.Bot.Search;
using SAIN.Components;
using SAIN.SAINComponent.Classes.EnemyClasses;
using UnityEngine;
using UnityEngine.AI;
using static SAIN.SAINComponent.Classes.Search.SearchReasonsStruct;

namespace Comet.Client;

public sealed partial class Plugin
{
    private delegate bool CanSearch(
        SearchDecider decider,
        Enemy enemy,
        out ECantStartReason reason
    );
    private delegate bool WantSearch(
        SearchDecider decider,
        Enemy enemy,
        out EWantToSearchReason reason
    );
    private static CanSearch canStartSearch = null!;
    private static WantSearch shallSearch = null!;

    private static void PatchSourceSearch(Harmony h)
    {
        canStartSearch = AccessTools.MethodDelegate<CanSearch>(
            AccessTools.Method(typeof(SearchDecider), "CanStartSearch")
        );
        shallSearch = AccessTools.MethodDelegate<WantSearch>(
            AccessTools.Method(typeof(SearchDecider), "ShallSearch")
        );
        h.Patch(
            AccessTools.Method(typeof(SearchDecider), "WantToSearch"),
            postfix: new HarmonyMethod(typeof(Plugin), nameof(QuietAudioSearch))
        );
        h.Patch(
            AccessTools.Method(typeof(SearchDecider), "shallBeginSearch"),
            postfix: new HarmonyMethod(typeof(Plugin), nameof(QuietSearchAllowed))
        );
        h.Patch(
            AccessTools.Method(typeof(SearchDecider), "ShallContinueSearch"),
            postfix: new HarmonyMethod(typeof(Plugin), nameof(QuietSearchAllowed))
        );
        h.Patch(
            AccessTools.Method(typeof(SearchClass), nameof(SearchClass.Search)),
            prefix: new HarmonyMethod(typeof(Plugin), nameof(WalkQuietSearch))
        );
        h.Patch(
            AccessTools.Method(typeof(SearchPathFinder), "checkFinishedSearch"),
            postfix: new HarmonyMethod(typeof(Plugin), nameof(FinishPartialSearch))
        );
    }

    private bool SourceSearchScope(BotComponent b, Enemy? e) =>
        config?.Enabled == true
        && world != null
        && Authority
        && NativeAdapter.IsSupportedAi(b)
        && b.BotActive
        && !b.GameEnding
        && !Ghost(b.ProfileId)
        && e != null
        && ContactSafety.Allows(b, e);

    private static bool QuietHeardSearch(BotComponent b, Enemy e)
    {
        var p = Instance;
        if (p?.config?.SilentSearch.Enabled != true || !p.SourceSearchScope(b, e))
            return false;
        var settings = b.Info.PersonalitySettings.Search;
        return SourceSearchRules.QuietHeard(
            e.Seen,
            e.Heard,
            e.TimeSinceLastKnownUpdated,
            settings.WillSearchForEnemy,
            settings.WillSearchFromAudio,
            b.Info.TimeBeforeSearch
        );
    }

    private static void QuietAudioSearch(
        SearchDecider __instance,
        Enemy enemy,
        ref WantSearchReasonsStruct reasons,
        ref bool __result
    )
    {
        if (
            __result
            || reasons.NotWantToSearchReason != ENotWantToSearchReason.WontSearchFromAudio
            || !QuietHeardSearch(__instance.Bot, enemy)
        )
            return;
        // Resume at exactly the next native check; no override of suppression or visited places.
        if (!canStartSearch(__instance, enemy, out reasons.CantStartReason))
            reasons.NotWantToSearchReason = ENotWantToSearchReason.CantStart;
        else if (!shallSearch(__instance, enemy, out reasons.WantToSearchReason))
            reasons.NotWantToSearchReason = ENotWantToSearchReason.ShallNotSearch;
        else
        {
            reasons.NotWantToSearchReason = ENotWantToSearchReason.None;
            __result = true;
        }
    }

    private static void QuietSearchAllowed(
        SearchDecider __instance,
        Enemy enemy,
        MethodBase __originalMethod,
        ref EWantToSearchReason reason,
        ref bool __result
    )
    {
        if (__result || !QuietHeardSearch(__instance.Bot, enemy))
            return;
        reason =
            __originalMethod.Name == "ShallContinueSearch"
                ? EWantToSearchReason.ContinueSearch_EnemyNotHeard
                : EWantToSearchReason.NewSearch_EnemyNotHeard;
        __result = true;
    }

    private static void WalkQuietSearch(SearchClass __instance, Enemy enemy, ref bool shallSprint)
    {
        if (QuietHeardSearch(__instance.Bot, enemy))
            shallSprint = false;
    }

    private static void FinishPartialSearch(SearchPathFinder __instance, Enemy enemy)
    {
        var p = Instance;
        var b = __instance.Bot;
        if (p == null || !p.SourceSearchScope(b, enemy))
            return;
        var place = __instance.TargetPlace;
        if (
            place == null
            || !ReferenceEquals(place, enemy.KnownPlaces.LastKnownPlace)
            || place.HasArrivedPersonal
            || place.HasArrivedSquad
        )
            return;
        int count = enemy.Path.PathCornerCount;
        if (enemy.Path.PathToEnemyStatus != NavMeshPathStatus.PathPartial || count < 1)
            return;
        if (
            !SourceSearchRules.PartialEnd(
                true,
                count,
                (enemy.Path.PathCorners[count - 1] - b.Position).sqrMagnitude
            )
        )
            return;
        enemy.KnownPlaces.SetPlaceAsSearched(place);
        __instance.Reset();
        if (p.states.TryGetValue(b, out var s))
            p.RecordHiddenExecution(b, s, "zzap-search-partial-end-reached");
    }
}
