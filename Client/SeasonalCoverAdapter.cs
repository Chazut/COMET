using System;
using System.Runtime.CompilerServices;
using HarmonyLib;
using SAIN.Components;
using SAIN.SAINComponent.Classes;
using SAIN.SAINComponent.Classes.Mover;
using SAIN.SAINComponent.SubComponents.CoverFinder;
using SAIN.Models.Enums;
using UnityEngine;
using UnityEngine.AI;

namespace Comet.Client;

internal sealed class SeasonalCoverRuntime
{
    internal float LogAt = -100;
    internal int Rejected;
}

public sealed partial class Plugin
{
    // Foliage volumes do not follow autumn/winter material changes. Do not change
    // their layers or the vision masks: evergreens and other observers need them.
    private static readonly Collider[] seasonalCoverOverlaps = new Collider[8];
    private sealed class SeasonalPositionCheck
    {
        internal ESeason Season;
        internal Vector3 Position;
        internal bool Checked, Inside;
        internal int ProtectionFrame = -1;
        internal Vector3 Threat;
        internal bool Protected;
    }
    private static readonly ConditionalWeakTable<CoverPoint, SeasonalPositionCheck> seasonalCoverPositions = new();

    private static void PatchSeasonalCover(Harmony h)
    {
        h.Patch(AccessTools.Method(typeof(CoverAnalyzer), "CheckColliderForCover"),
            prefix: new HarmonyMethod(typeof(Plugin), nameof(SeasonalCoverCandidate)),
            postfix: new HarmonyMethod(typeof(Plugin), nameof(SeasonalCoverPosition)));
        h.Patch(AccessTools.Method(typeof(SAINCoverClass), "FindCoverPoint"),
            prefix: new HarmonyMethod(typeof(Plugin), nameof(SeasonalCoverSelection)));
        h.Patch(AccessTools.Method(typeof(SAINCoverClass), "FindPointInDirection"),
            prefix: new HarmonyMethod(typeof(Plugin), nameof(SeasonalCoverSelection)));
        h.Patch(AccessTools.Method(typeof(SAINCoverClass), "ManualUpdate"),
            prefix: new HarmonyMethod(typeof(Plugin), nameof(SeasonalCoverRetained)));
        h.Patch(AccessTools.Method(typeof(SAINMoverClass), "GoToCoverPoint",
            new[] { typeof(CoverPoint), typeof(bool), typeof(ESprintUrgency) }),
            prefix: new HarmonyMethod(typeof(Plugin), nameof(SeasonalCoverMove)));
    }

    private static bool SeasonalCoverScope(BotComponent b)
    {
        var p = Instance;
        var season = SeasonsController.Controller?.Season;
        return (season == ESeason.Autumn || season == ESeason.AutumnLate || season == ESeason.Winter)
            && p?.config?.Enabled == true && p.Authority && b != null
            && b.BotActive && !b.IsDead && !b.GameEnding && b.SAINLayersActive
            && b.GoalEnemy != null && NativeAdapter.IsSupportedAi(b) && !p.Ghost(b.ProfileId);
    }

    private static bool FoliageCoverCollider(Collider? collider)
        => collider != null && ((1 << collider.gameObject.layer) & (int)LayersMaskController.Foliage) != 0;

    // Static foliage occupancy can be cached independently of the threat.
    private static bool CoverPositionInFoliage(Vector3 position)
    {
        try
        {
            return Physics.OverlapSphereNonAlloc(position + Vector3.up, .05f, seasonalCoverOverlaps,
                LayersMaskController.Foliage, QueryTriggerInteraction.Collide) > 0;
        }
        finally { Array.Clear(seasonalCoverOverlaps, 0, seasonalCoverOverlaps.Length); }
    }

    private static bool SeasonalCoverCandidate(CoverAnalyzer __instance, Collider collider,
        ref string reason, ref NavMeshHit navMeshHit, ref bool __result)
    {
        if (!SeasonalCoverScope(__instance.Bot) || !FoliageCoverCollider(collider)) return true;
        reason = "seasonal-foliage-is-not-cover";
        navMeshHit = default;
        __result = false;
        RecordSeasonalCover(__instance.Bot, collider, reason);
        return false;
    }

    // SAIN tests nearby cover within three metres. Cast from the bot's body
    // towards the known threat so a trunk behind the bot or beyond the enemy
    // cannot qualify. Only solid world geometry can return a hit.
    private static bool SolidCoverBetween(Vector3 position, Vector3 threat)
    {
        var towardThreat = threat - position;
        float distance = towardThreat.magnitude;
        return distance > 0f && Physics.Raycast(position + Vector3.up, towardThreat / distance,
            Mathf.Min(distance, 3f), LayersMaskController.HighPolyWithTerrainMask, QueryTriggerInteraction.Ignore);
    }

    private static bool BareFoliageAt(Vector3 position, Vector3 threat)
        => CoverPositionInFoliage(position) && !SolidCoverBetween(position, threat);

    private static void SeasonalCoverPosition(CoverAnalyzer __instance, Collider collider, Vector3 targetPosition,
        NavMeshHit navMeshHit, ref string reason, ref bool __result)
    {
        if (!__result || !SeasonalCoverScope(__instance.Bot) || !BareFoliageAt(navMeshHit.position, targetPosition)) return;
        __result = false;
        reason = "seasonal-cover-position-inside-foliage";
        RecordSeasonalCover(__instance.Bot, collider, reason);
    }

    internal static bool SeasonalCoverUsable(BotComponent b, CoverPoint? point)
    {
        if (point == null || !SeasonalCoverScope(b)) return true;
        if (FoliageCoverCollider(point.Collider)) return false;
        // Retained/custom points may predate the seasonal change or bypass the
        // native analyzer. Cache static occupancy by point/position/season.
        var check = seasonalCoverPositions.GetOrCreateValue(point);
        var season = SeasonsController.Controller!.Season;
        if (!check.Checked || check.Season != season || check.Position != point.Position)
        {
            check.Inside = CoverPositionInFoliage(point.Position);
            check.Season = season;
            check.Position = point.Position;
            check.Checked = true;
            check.ProtectionFrame = -1;
        }
        if (!check.Inside) return true;
        if (b.GoalEnemy?.LastKnownPosition is not Vector3 threat) return false;
        if (check.ProtectionFrame != Time.frameCount || check.Threat != threat)
        {
            check.Protected = SolidCoverBetween(point.Position, threat);
            check.Threat = threat;
            check.ProtectionFrame = Time.frameCount;
        }
        return check.Protected;
    }

    private static void SeasonalCoverSelection(SAINCoverClass __instance)
    {
        var b = __instance.Bot;
        if (!SeasonalCoverScope(b)) return;
        var points = __instance.CoverPoints;
        for (int i = points.Count - 1; i >= 0; i--)
            if (points[i] != null && !SeasonalCoverUsable(b, points[i]))
            {
                var collider = points[i].Collider;
                points.RemoveAt(i);
                RecordSeasonalCover(b, collider, "seasonal-cached-foliage-cover-removed");
            }
    }

    private static void SeasonalCoverRetained(SAINCoverClass __instance)
    {
        var b = __instance.Bot;
        if (!SeasonalCoverScope(b)) return;
        var held = __instance.CoverInUse;
        var moving = __instance.CoverPoint_MovingTo;
        if (held != null && !SeasonalCoverUsable(b, held))
        {
            SourceCoverAccess.Forget(__instance, held);
            RecordSeasonalCover(b, held.Collider, "seasonal-held-foliage-cover-released");
        }
        if (moving != null && !SeasonalCoverUsable(b, moving))
        {
            SourceCoverAccess.Forget(__instance, moving);
            RecordSeasonalCover(b, moving.Collider, "seasonal-moving-foliage-cover-released");
        }
    }

    private static bool SeasonalCoverMove(SAINMoverClass __instance, CoverPoint point, ref bool __result)
    {
        if (SeasonalCoverUsable(__instance.Bot, point)) return true;
        __result = false;
        SourceCoverAccess.Forget(__instance.Bot.Cover, point);
        RecordSeasonalCover(__instance.Bot, point.Collider, "seasonal-foliage-cover-move-refused");
        return false;
    }

    private static void RecordSeasonalCover(BotComponent b, Collider? collider, string reason)
    {
        var p = Instance;
        if (p == null || !p.states.TryGetValue(b, out var s)) return;
        try
        {
            var r = s.SeasonalCover;
            r.Rejected++;
            if (Time.time - r.LogAt < 5) return;
            r.LogAt = Time.time;
            var json = Diagnostics.Serialize(new {
                schema = 1, session = p.session, sequence = ++p.sequence,
                feature = "C07", stage = "evaluated", decision = "SeasonalCover", reason,
                realtime = Time.realtimeSinceStartup, utc = DateTime.UtcNow.ToString("O"),
                configRevision = p.config?.Revision ?? 0, profileId = s.ProfileId, name = s.Name, botInstance = s.Id,
                seasonalCover = new { season = SeasonsController.Controller?.Season.ToString(),
                    collider = collider != null ? collider.name : "", rejected = r.Rejected }
            });
            p.diagnostics?.Write(json);
            Api.Decisions.Publish(json, _ => { });
        }
        catch { /* Observer failure must not alter cover selection. */ }
    }
}
