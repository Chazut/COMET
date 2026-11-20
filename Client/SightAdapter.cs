using System;
using Comet.Core;
using SAIN.Components;
using SAIN.Preset.Shared.Enums;
using SAIN.SAINComponent.Classes.EnemyClasses;
using UnityEngine;

namespace Comet.Client;

internal static class SightAdapter
{
    internal static SightHold? Capture(BotComponent bot, BotState state, bool authority, bool ghost)
    {
        var e = bot.GoalEnemy;
        var frame = state.SightFrame;
        frame.Now = Time.time;
        frame.Target = e?.EnemyProfileId ?? "";
        frame.Eligible =
            NativeAdapter.IsSupportedAi(bot) && bot.BotActive && authority && !ghost && e != null;
        frame.Visible = e?.IsVisible == true;
        frame.WeaponReady = VisibleAdapter.WeaponReady(bot);
        frame.OwnedPlan =
            (
                state.Feature == "A02" && state.ActiveLabel == "COMET:Shoot"
                || state.Feature == "A03" && state.ActiveLabel == "COMET:KeepAngle"
            )
            && bot.SAINLayersActive
            && bot.Decision.CurrentCombatDecision == state.Expected;
        frame.Weapon = bot.Player.HandsController?.Item?.Id.ToString() ?? "";
        frame.Urgent =
            bot.Decision.CurrentSelfDecision != ESelfActionType.None
            || bot.Decision.CurrentSquadDecision != ESquadDecision.None;
        frame.Action =
            bot.Decision.CurrentCombatDecision == ECombatDecision.StandAndShoot ? "Shoot"
            : bot.Decision.CurrentCombatDecision == ECombatDecision.RushEnemy ? "Push"
            : "";
        frame.HitAt = bot.Medical.TimeLastShot;
        frame.LowAmmo = bot.Decision.SelfActionDecisions.LowOnAmmo(.3f);
        frame.Hurt =
            bot.Memory.Health.HealthStatus == ETagStatus.BadlyInjured
            || bot.Memory.Health.HealthStatus == ETagStatus.Dying;
        frame.Suppressed = bot.Suppression.IsSuppressed;
        frame.Panic = state.Fear.Current.Panic;
        frame.Observed = frame.PushPathValid = false;
        frame.OtherThreat = false;
        if (!frame.Eligible)
        {
            state.SightPath = null;
            return state.Sight.Reset("sight-ineligible");
        }
        frame.Action = bot.Decision.CurrentCombatDecision switch
        {
            ECombatDecision.StandAndShoot => "Shoot",
            ECombatDecision.RushEnemy => "Push",
            ECombatDecision.DogFight => "DogFight",
            _ => "",
        };
        frame.Jiggling = state.CornerPeek.Policy.Active;
        frame.SeenAge = e!.TimeSinceSeen;
        frame.Distance = e.RealDistance;
        frame.Observed = e.Seen;
        foreach (var other in bot.EnemyController.KnownEnemies)
            if (other != null && other != e && (other.IsVisible || other.Status.ShotAtMeRecently))
            {
                frame.OtherThreat = true;
                break;
            }
        if (e.KnownPlaces.LastSeenPlace is { } seen)
        {
            var p = seen.Position;
            frame.X = p.x;
            frame.Y = p.y;
            frame.Z = p.z;
        }
        state.SightAimHeight = 1.2f;
        return state.Sight.Evaluate(frame);
    }
}
