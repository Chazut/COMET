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
        frame.Now = Time.time; frame.Target = e?.EnemyProfileId ?? "";
        frame.Eligible = NativeAdapter.IsSupportedAi(bot) && bot.BotActive && authority && !ghost && e != null;
        frame.Visible = e?.IsVisible == true; frame.WeaponReady = VisibleAdapter.WeaponReady(bot);
        frame.OwnedPlan = (state.Feature == "A02" && state.ActiveLabel == "COMET:Shoot"
            || state.Feature == "A03" && state.ActiveLabel == "COMET:KeepAngle")
            && bot.SAINLayersActive && bot.Decision.CurrentCombatDecision == state.Expected;
        frame.Weapon = bot.Player.HandsController?.Item?.Id.ToString() ?? "";
        frame.Urgent = bot.Decision.CurrentSelfDecision != ESelfActionType.None || bot.Decision.CurrentSquadDecision != ESquadDecision.None;
        frame.Action = bot.Decision.CurrentCombatDecision == ECombatDecision.StandAndShoot ? "Shoot"
            : bot.Decision.CurrentCombatDecision == ECombatDecision.RushEnemy ? "Push" : "";
        frame.HitAt = bot.Medical.TimeLastShot; frame.LowAmmo = bot.Decision.SelfActionDecisions.LowOnAmmo(.3f);
        frame.Hurt = bot.Memory.Health.HealthStatus == ETagStatus.BadlyInjured || bot.Memory.Health.HealthStatus == ETagStatus.Dying;
        frame.Suppressed = bot.Suppression.IsSuppressed;
        frame.Panic = state.Fear.Current.Panic;
        frame.Observed = frame.PushPathValid = false;
        frame.OtherThreat = bot.BotOwner.Memory.IsUnderFire && bot.Memory.LastUnderFireEnemy != e;
        if (!frame.Eligible) { state.SightPath = null; return state.Sight.Reset("sight-ineligible"); }
        // A03 must not preserve a rush beyond the personal cue that justified it.
        if (frame.Action == "Push" && (state.Decision == "Push" || state.Decision == "ContinuePush") && state.OpportunityAtSelection?.Bonus > 0)
        {
            var cue = state.Opportunity.Snapshot(e!.EnemyProfileId, System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(e), frame.Now, Plugin.Instance!.RaidOpportunityConfig);
            if (!OpportunityModel.CanContinueSight(state.OpportunityAtSelection, cue))
            { state.SightPath = null; return state.Sight.Reset("opportunity-closed"); }
        }
        if (frame.Panic) { state.SightPath = null; return state.Sight.Evaluate(frame); }
        if ((frame.Action == "Shoot" && bot.CurrentAction?.Name != "StandAndShootAction")
            || (frame.Action == "Push" && bot.CurrentAction?.Name != "RushEnemyAction")) frame.Action = "";
        int checkedEnemies = 0;
        foreach (var other in bot.EnemyController.KnownEnemies)
        {
            if (++checkedEnemies > 32) { frame.OtherThreat = true; break; }
            if (other != null && other != e && (other.IsVisible || other.Status.ShotAtMeRecently)) { frame.OtherThreat = true; break; }
        }
        if (frame.Visible)
        {
            // Read personal visual knowledge only. Heard/shared positions cannot refresh a visual snapshot.
            var seen = e!.KnownPlaces.LastSeenPlace;
            frame.Observed = seen != null; frame.SeenAge = e.TimeSinceSeen;
            if (seen != null)
            {
                var p = seen.Position;
                frame.X = p.x; frame.Y = p.y; frame.Z = p.z; frame.Distance = Vector3.Distance(bot.Position, p);
                state.SightAimHeight = Mathf.Clamp(bot.Transform.WeaponRoot.y - bot.Position.y, .5f, 1.6f);
                var path = bot.Mover.ActivePath;
                state.SightPath = frame.Action == "Push" && path != null && path.Moving && (path.Destination - p).sqrMagnitude <= 4
                    && VisibleAdapter.ValidPath(path.NavMeshPath, bot.Position, path.Destination) ? path : null;
                state.SightDestination = path?.Destination ?? Vector3.zero;
            }
        }
        else
        {
            var path = bot.Mover.ActivePath;
            frame.PushPathValid = state.SightPath != null && ReferenceEquals(path, state.SightPath) && path.Moving
                && (path.Destination - state.SightDestination).sqrMagnitude < .01f
                && VisibleAdapter.ValidPath(path.NavMeshPath, bot.Position, state.SightDestination);
        }
        return state.Sight.Evaluate(frame);
    }
}
