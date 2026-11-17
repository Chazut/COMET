using System;
using EFT.InventoryLogic;
using SAIN.Components.PlayerComponentSpace;
using SAIN.Models.Enums;
using SAIN.SAINComponent.Classes.EnemyClasses;
using UnityEngine;

namespace Comet.Client;

// Read-only observations of real shots, not successful requests to start firing.
// Native visibility is cached and is not proof of what a player can see in a clip.
internal sealed class ShotJournalRuntime
{
    internal long Total, Hidden, Manual, Unknown, Detailed, Reported, Suppression, Blindfire, DirectHidden;
    internal float DetailAt = -1000, SummaryAt;
    internal bool Fault;
}

public sealed partial class Plugin
{
    private static void JournalShotObserved(PlayerComponent __instance, IWeapon __0)
    {
        var p = Instance;
        if (p?.config?.CombatJournal.Enabled != true) return;
        try
        {
            var b = __instance.BotComponent;
            if (b == null || !p.JournalScope(b)) return;
            var s = p.JournalState(b.Player);
            if (s == null || s.Journal.Shots.Fault || s.Journal.Fault) return;
            var r = s.Journal.Shots;
            try
            {
                // Manual suppression/prefire may target a different contact from GoalEnemy.
                // LastShotEnemy is SAIN's last firing target, not a fresh aim selection.
                bool manual = b.SAINLayersActive && b.ManualShoot.Reason != EShootReason.None;
                Enemy? enemy = !b.SAINLayersActive ? null : manual
                    ? prefireEnemy.GetValue(b.ManualShoot) as Enemy : b.Shoot.LastShotEnemy;
                string intent = !b.SAINLayersActive ? "unknown-other-layer" : b.ManualShoot.Reason switch
                {
                    EShootReason.Suppress or EShootReason.SquadSuppressing or EShootReason.WalkToCoverSuppress => "suppression",
                    EShootReason.Blindfire => "blindfire",
                    EShootReason.None => "direct",
                    _ => "other-manual"
                };
                r.Total++;
                if (manual) r.Manual++;
                if (intent == "suppression") r.Suppression++;
                if (intent == "blindfire") r.Blindfire++;
                if (intent == "direct" && enemy?.IsVisible == false) r.DirectHidden++;
                if (enemy == null) r.Unknown++;
                else if (!enemy.IsVisible) r.Hidden++;
                if (Time.time < r.DetailAt) return;
                r.DetailAt = Time.time + 1;
                var parts = enemy?.Vision.EnemyParts.PartsArray;
                int seenParts = 0, shootParts = 0, bothParts = 0;
                // At most seven native body parts; no rays, aim selection or inventory scan.
                if (parts != null)
                    for (int i = 0; i < Math.Min(7, parts.Length); i++)
                    {
                        var part = parts[i];
                        if (part.CanBeSeen) seenParts++;
                        if (part.CanShoot) shootParts++;
                        if (part.CanBeSeen && part.CanShoot) bothParts++;
                    }
                var light = enemy?.EnemyPlayerComponent?.Flashlight;
                r.Detailed++;
                p.JournalWrite(s, "T01", "shot", "native-shot-observed", new
                {
                    kind = "shot", totalShots = r.Total,
                    intent, visibilitySource = "cached-native-flags",
                    targetSource = !b.SAINLayersActive ? "unknown-other-layer" : manual ? "manual-shot" : "last-sain-shot",
                    target = enemy?.EnemyProfileId ?? "", goalTarget = b.GoalEnemy?.EnemyProfileId ?? "",
                    manualReason = manual ? b.ManualShoot.Reason.ToString() : "",
                    visible = (bool?)enemy?.IsVisible, lineOfSight = (bool?)enemy?.InLineOfSight,
                    canShoot = (bool?)enemy?.CanShoot, canBeSeen = (bool?)enemy?.Vision.EnemyParts.CanBeSeen,
                    secondsSinceSeen = enemy?.Seen == true ? (double?)enemy.TimeSinceSeen : null,
                    contactAge = (double?)enemy?.TimeSinceLastKnownUpdated,
                    chosenPart = enemy?.AimTarget.ChosenPart?.ToString(),
                    partsAvailable = parts != null, seenParts, shootParts, bothParts,
                    targetWhiteLight = (bool?)light?.WhiteLight,
                    targetLaser = (bool?)light?.Laser,
                    context = p.JournalContext(s)
                });
            }
            catch (Exception ex)
            {
                // A failed shot observer must not disable the rest of the journal or combat.
                r.Fault = true;
                p.JournalReadError("native-shot", ex);
            }
        }
        catch (Exception ex) { p.JournalReadError("native-shot-scope", ex); }
    }

    private void FlushShotJournal(BotState s, bool force = false)
    {
        var r = s.Journal.Shots;
        if (r.Total == r.Reported || (!force && Time.time < r.SummaryAt)) return;
        r.SummaryAt = Time.time + 5;
        JournalWrite(s, "T01", "shot-summary", "native-shot-totals", new
        {
            kind = "shot-summary", totalShots = r.Total, hiddenTargetShots = r.Hidden,
            manualShots = r.Manual, unknownTargetShots = r.Unknown, detailedShots = r.Detailed,
            suppressionShots = r.Suppression, blindfireShots = r.Blindfire, directHiddenTargetShots = r.DirectHidden,
            sampledOutShots = r.Total - r.Detailed
        });
        r.Reported = r.Total;
    }
}
