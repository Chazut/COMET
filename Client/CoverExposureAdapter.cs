using System;
using HarmonyLib;
using SAIN.Components;
using SAIN.Models.Enums;
using SAIN.SAINComponent.Classes;
using SAIN.SAINComponent.Classes.Mover;
using SAIN.SAINComponent.SubComponents.CoverFinder;
using UnityEngine;

namespace Comet.Client;

internal sealed class CoverExposureRuntime
{
    internal float LogAt = -100;
    internal int Hits,
        Released,
        Refused;
}

public sealed partial class Plugin
{
    private static void PatchCoverExposure(Harmony h)
    {
        h.Patch(
            AccessTools.Method(typeof(SAINCoverClass), "GetHit"),
            postfix: new HarmonyMethod(typeof(Plugin), nameof(CoverExposureHit))
        );
        foreach (var name in new[] { "ManualUpdate", "UpdateCover" })
            h.Patch(
                AccessTools.Method(typeof(SAINCoverClass), name),
                prefix: new HarmonyMethod(typeof(Plugin), nameof(CoverExposureRetained))
            );
        h.Patch(
            AccessTools.Method(
                typeof(SAINMoverClass),
                "GoToCoverPoint",
                new[] { typeof(CoverPoint), typeof(bool), typeof(ESprintUrgency) }
            ),
            prefix: new HarmonyMethod(typeof(Plugin), nameof(CoverExposureMove))
        );
    }

    private static bool CoverExposureScope(BotComponent b)
    {
        var p = Instance;
        return p?.config?.Enabled == true
            && p.Authority
            && b != null
            && b.BotActive
            && !b.IsDead
            && !b.GameEnding
            && b.SAINLayersActive
            && b.GoalEnemy != null
            && NativeAdapter.IsSupportedAi(b)
            && !p.Ghost(b.ProfileId);
    }

    // Arrival is useful for locomotion, but foliage is concealment, not a
    // protective medical bonus. No raycast, treatment veto or cancellation.
    private static bool NativeCoverProtects(BotComponent b)
    {
        var point = b.Cover.CoverInUse;
        return point?.BotInThisCover == true
            && !point.CoverData.IsBad
            && !point.Spotted
            && !FoliageCoverCollider(point.Collider);
    }

    internal static bool CoverExposed(BotComponent b, CoverPoint? point) =>
        point != null && CoverExposureScope(b) && point.Spotted;

    private static void CoverExposureHit(SAINCoverClass __instance)
    {
        var b = __instance.Bot;
        var point = __instance.CoverInUse;
        if (point == null || !CoverExposureScope(b))
            return;
        // Read AFTER SAIN has counted damage and raised OnSpottedInCover.
        // Its thresholds and two-second expiry remain the sole authority.
        bool exposed = point.Spotted;
        if (exposed)
            SourceCoverAccess.Forget(__instance, point);
        RecordCoverExposure(
            b,
            point,
            exposed ? "hit-cover-released" : "hit-below-native-spotted-threshold",
            true,
            exposed,
            false
        );
    }

    private static void CoverExposureRetained(SAINCoverClass __instance)
    {
        var b = __instance.Bot;
        if (!CoverExposureScope(b))
            return;
        var held = __instance.CoverInUse;
        var moving = __instance.CoverPoint_MovingTo;
        if (held != null && held.Spotted)
        {
            SourceCoverAccess.Forget(__instance, held);
            RecordCoverExposure(b, held, "spotted-held-cover-released", false, true, false);
        }
        if (moving != null && moving.Spotted && !ReferenceEquals(moving, held))
        {
            SourceCoverAccess.Forget(__instance, moving);
            RecordCoverExposure(b, moving, "spotted-moving-cover-released", false, true, false);
        }
    }

    private static bool CoverExposureMove(
        SAINMoverClass __instance,
        CoverPoint point,
        ref bool __result
    )
    {
        if (!CoverExposed(__instance.Bot, point))
            return true;
        // FindCoverPoint already tries the next candidate when GoToCoverPoint
        // fails. Keep its ordering and cached points, including native expiry.
        __result = false;
        SourceCoverAccess.Forget(__instance.Bot.Cover, point);
        RecordCoverExposure(
            __instance.Bot,
            point,
            "spotted-cover-move-refused",
            false,
            false,
            true
        );
        return false;
    }

    private static void RecordCoverExposure(
        BotComponent b,
        CoverPoint point,
        string reason,
        bool hit,
        bool released,
        bool refused
    )
    {
        var p = Instance;
        if (p == null || !p.states.TryGetValue(b, out var s))
            return;
        try
        {
            var r = s.CoverExposure;
            if (hit)
                r.Hits++;
            if (released)
                r.Released++;
            if (refused)
                r.Refused++;
            if (Time.time - r.LogAt < 5 && !(released && r.Released == 1))
                return;
            r.LogAt = Time.time;
            var json = Diagnostics.Serialize(
                new
                {
                    schema = 1,
                    session = p.session,
                    sequence = ++p.sequence,
                    feature = "C07",
                    stage = "evaluated",
                    decision = "CoverExposure",
                    reason,
                    realtime = Time.realtimeSinceStartup,
                    utc = DateTime.UtcNow.ToString("O"),
                    configRevision = p.config?.Revision ?? 0,
                    profileId = s.ProfileId,
                    name = s.Name,
                    botInstance = s.Id,
                    coverExposure = new
                    {
                        foliage = FoliageCoverCollider(point.Collider),
                        collider = point.Collider != null ? point.Collider.name : "",
                        position = new
                        {
                            x = point.Position.x,
                            y = point.Position.y,
                            z = point.Position.z,
                        },
                        nativeSpotted = point.Spotted,
                        nativeBad = point.CoverData.IsBad,
                        hits = r.Hits,
                        released = r.Released,
                        refused = r.Refused,
                    },
                }
            );
            p.diagnostics?.Write(json);
            Api.Decisions.Publish(json, _ => { });
        }
        catch
        { /* An observer cannot block movement or alter a damage reaction. */
        }
    }
}
