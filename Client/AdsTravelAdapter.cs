using System;
using Comet.Core;
using EFT;
using HarmonyLib;
using Newtonsoft.Json;
using SAIN.Components;
using SAIN.Components.PlayerComponentSpace;
using SAIN.Models.Enums;
using SAIN.Preset.Shared.Enums;
using SAIN.SAINComponent.Classes;
using SAIN.SAINComponent.Classes.EnemyClasses;
using SAIN.SAINComponent.Classes.WeaponFunction;
using UnityEngine;

namespace Comet.Client;

internal sealed class AdsTravelRuntime
{
    internal readonly AdsTravelPolicy Policy = new();
    internal BotComponent? Owner;
    internal bool NativeWanted,
        NativeKnown,
        Owned,
        Fault;
    internal float LastShot = -1000,
        LastFirePreparation = -1000,
        NextEval,
        CallbackAt,
        LogAt = -1000;
    internal string LogKey = "";
}

public sealed partial class Plugin
{
    private static void PatchAdsTravel(Harmony h)
    {
        h.Patch(
            AccessTools.Method(
                typeof(AimDownSightsController),
                nameof(AimDownSightsController.ShallAimDownSights)
            ),
            postfix: new HarmonyMethod(typeof(Plugin), nameof(AdsNativeWish))
        );
        h.Patch(
            AccessTools.Method(
                typeof(AimDownSightsController),
                nameof(AimDownSightsController.ManualUpdate)
            ),
            prefix: new HarmonyMethod(typeof(Plugin), nameof(AdsBeforeUpdate)),
            postfix: new HarmonyMethod(typeof(Plugin), nameof(AdsAfterUpdate))
        );
        h.Patch(
            AccessTools.Method(typeof(SAINShootData), "AimAndShootAtEnemy")
                ?? throw new MissingMethodException("M10 native firing preparation"),
            prefix: new HarmonyMethod(typeof(Plugin), nameof(AdsPreparingShot))
        );
        h.Patch(
            AccessTools.Method(typeof(ManualShootClass), nameof(ManualShootClass.TryShoot)),
            prefix: new HarmonyMethod(typeof(Plugin), nameof(AdsManualFire))
            {
                priority = Priority.First,
            }
        );
        h.Patch(
            AccessTools.Method(typeof(PlayerComponent), nameof(PlayerComponent.OnMakingShot)),
            postfix: new HarmonyMethod(typeof(Plugin), nameof(AdsObservedShot))
        );
    }

    private static void AdsNativeWish(AimDownSightsController __instance, bool __result)
    {
        var p = Instance;
        if (p != null && p.states.TryGetValue(__instance.Bot, out var s))
        {
            s.AdsTravel.NativeWanted = __result;
            s.AdsTravel.NativeKnown = true;
        }
    }

    private bool AdsEligible(BotComponent b, BotState s) =>
        config?.Enabled == true
        && config.AdsTravel.Enabled
        && Authority
        && !Ghost(s.ProfileId)
        && NativeAdapter.IsSupportedAi(b)
        && !b.IsDead
        && b.BotActive
        && !b.GameEnding
        && b.SAINLayersActive;

    private static bool ObservedAds(BotComponent b) =>
        b.BotOwner.WeaponManager?.ShootController?.IsAiming == true;

    private AdsTravelFrame AdsFrame(BotComponent b, BotState s)
    {
        var r = s.AdsTravel;
        var path = b.Mover.ActivePath;
        string action = b.Decision.CurrentCombatDecision.ToString();
        string type = b.CurrentAction?.GetType().FullName ?? "";
        bool owner = b.CurrentAction?.GetType().Assembly == typeof(BotComponent).Assembly;
        if (type == "SAIN.Layers.Combat.Solo.AvoidGrenadeAction")
            action = "RunAway";
        bool routeOwner =
            owner
            && (
                type == SeekCoverType && (action == "SeekCover" || action == "Retreat")
                || type == "SAIN.Layers.Combat.Solo.RushEnemyAction" && action == "RushEnemy"
                || type == "SAIN.Layers.Combat.Solo.AvoidGrenadeAction"
            );
        string block =
            !AdsEligible(b, s) ? "ads-ineligible"
            : !routeOwner ? "ads-native-action-priority"
            : "";
        if (
            block.Length == 0
            && (
                path == null
                || path.Crawling
                || path.Status == EBotMoveStatus.DoorInteraction
                || b.Player.IsInPronePose
                || s.CornerPrefire.Policy.Active
                || s.CornerChase.Policy.Active
                || s.CornerPeek.Policy.Active
                || s.CombatStep.Policy.Active
                || s.HoldAngle.Policy.Active
                || s.Entry.Policy.Active
                || s.RoomClear.Policy.Active
                || s.Ambush.Policy.Active
            )
        )
            block = "ads-preserve-other-tactic";
        var velocity = b.Player.Velocity;
        velocity.y = 0;
        bool preparing =
            b.Aim.AimStatus != AimStatus.NoTarget
            || b.ManualShoot.Reason != EShootReason.None
            || Time.time - r.LastFirePreparation < config!.AdsTravel.SinceShot;
        return new()
        {
            Now = Time.time,
            SinceShot = Time.time - r.LastShot,
            Speed = velocity.magnitude,
            Action = action,
            Target = b.GoalEnemy?.EnemyProfileId ?? "",
            Block = block,
            NativeADS = r.NativeWanted,
            ObservedADS = ObservedAds(b),
            Moving = path?.Moving == true,
            InCover = b.Cover.CoverInUse != null,
            Ready =
                b.Player.HandsController is Player.FirearmController
                && VisibleAdapter.WeaponReady(b),
            Firing = b.BotOwner.ShootData.Shooting || b.ManualShoot.Shooting,
            Preparing = preparing,
            Sprinting = b.Player.IsSprintEnabled || b.Mover.Running,
        };
    }

    private static void AdsBeforeUpdate(AimDownSightsController __instance)
    {
        var p = Instance;
        var b = __instance.Bot;
        if (p?.config == null || !p.states.TryGetValue(b, out var s) || s.AdsTravel.Fault)
            return;
        try
        {
            var r = s.AdsTravel;
            r.CallbackAt = Time.time;
            r.Owner = b;
            if (!p.AdsEligible(b, s))
            {
                p.EndAdsTravel(s, "ads-ineligible");
                return;
            }
            // Keep native intent separate: ManualUpdate's cached false may be our own previous command.
            if (!r.NativeKnown)
            {
                r.NativeWanted = __instance.AimingDownSights;
                r.NativeKnown = true;
            }
            var f = p.AdsFrame(b, s);
            string gate = AdsTravelPolicy.Gate(f, p.config.AdsTravel);
            if (r.Owned && gate.Length > 0)
            {
                p.EndAdsTravel(s, gate);
                return;
            }
            if (Time.time >= r.NextEval)
            {
                r.NextEval = Time.time + (float)p.config.AdsTravel.Refresh;
                if (!r.Policy.Tick(f, p.config.AdsTravel))
                {
                    if (r.Owned)
                        p.EndAdsTravel(s, r.Policy.Trace!.Reason);
                    else
                        p.RecordAdsTravel(s);
                    return;
                }
            }
            if (!r.Policy.Active)
                return;
            __instance.SetADS(false, true);
            r.Owned = true;
            r.Policy.Observe(ObservedAds(b), true, f.Speed, p.config.AdsTravel);
        }
        catch (Exception ex)
        {
            p.AdsTravelError(s, ex);
        }
    }

    private static void AdsAfterUpdate(AimDownSightsController __instance)
    {
        var p = Instance;
        if (
            p?.config == null
            || !p.states.TryGetValue(__instance.Bot, out var s)
            || !s.AdsTravel.Owned
        )
            return;
        try
        {
            s.AdsTravel.Policy.Observe(
                ObservedAds(__instance.Bot),
                true,
                __instance.Bot.Player.Velocity.magnitude,
                p.config.AdsTravel
            );
            p.RecordAdsTravel(s);
        }
        catch (Exception ex)
        {
            p.AdsTravelError(s, ex);
        }
    }

    private static void AdsPreparingShot(Enemy Enemy, BotComponent bot)
    {
        if (Enemy?.IsVisible == true && Enemy.CanShoot)
            AdsFirePriority(bot, "ads-fire-preparation");
    }

    private static void AdsManualFire(ManualShootClass __instance) =>
        AdsFirePriority(__instance.Bot, "ads-manual-fire-priority");

    private static void AdsFirePriority(BotComponent b, string reason)
    {
        var p = Instance;
        if (p == null || !p.states.TryGetValue(b, out var s))
            return;
        s.AdsTravel.LastFirePreparation = Time.time;
        if (s.AdsTravel.Owned)
            p.EndAdsTravel(s, reason);
    }

    private static void AdsObservedShot(PlayerComponent __instance)
    {
        var p = Instance;
        var b = __instance.BotComponent;
        if (p != null && b != null && p.states.TryGetValue(b, out var s))
            s.AdsTravel.LastShot = Time.time;
    }

    private void WatchAdsTravel(BotComponent b, BotState s)
    {
        var r = s.AdsTravel;
        if (!r.Owned)
            return;
        try
        {
            string reason = AdsTravelPolicy.Gate(AdsFrame(b, s), config!.AdsTravel);
            if (reason.Length > 0)
                EndAdsTravel(s, reason);
        }
        catch (Exception ex)
        {
            AdsTravelError(s, ex);
        }
    }

    private void EndAdsTravel(BotState s, string reason)
    {
        var r = s.AdsTravel;
        var b = r.Owner;
        bool had = r.Owned;
        r.Owned = false;
        try
        {
            // Never make a new aim command on a remote, sleeping or destroyed actor.
            // Do not overwrite another system which already restored ADS.
            if (
                had
                && b != null
                && Authority
                && !Ghost(s.ProfileId)
                && !b.IsDead
                && b.BotActive
                && !b.GameEnding
                && b.SAINLayersActive
                && !ObservedAds(b)
            )
            {
                var controller = b.AimDownSightsController;
                var enemy = b.GoalEnemy;
                bool wanted = controller.ShallAimDownSights(
                    enemy?.KnownPlaces.LastKnownPosition,
                    enemy
                );
                r.NativeWanted = wanted;
                r.NativeKnown = true;
                controller.SetADS(wanted, true);
            }
            if (had || r.Policy.Active)
            {
                r.Policy.End(
                    reason,
                    Time.time,
                    b != null && !b.IsDead && ObservedAds(b),
                    r.NativeWanted
                );
                RecordAdsTravel(s, true);
            }
        }
        catch (Exception ex)
        {
            r.Fault = true;
            r.Policy.End("ads-release-error", Time.time, false, r.NativeWanted);
            Logger.LogWarning("M10 release yielded: " + ex.GetBaseException().Message);
        }
        finally
        {
            r.Owner = null;
        }
    }

    private void AdsTravelError(BotState s, Exception ex)
    {
        s.AdsTravel.Fault = true;
        EndAdsTravel(s, "ads-adapter-error");
        Logger.LogWarning("M10 yielded: " + ex.GetBaseException().Message);
    }

    private void RecordAdsTravel(BotState s, bool force = false)
    {
        if (!Comet.Core.ReplayCapture.Detailed)
            return;
        var r = s.AdsTravel;
        var t = r.Policy.Trace;
        if (t == null || t.Action.Length == 0)
            return;
        string key =
            t.State
            + ":"
            + t.Reason
            + ":"
            + t.Action
            + ":"
            + t.Target
            + ":"
            + t.NativeADS
            + ":"
            + t.ObservedADS
            + ":"
            + t.RequestedOff;
        if (!force && key == r.LogKey && (t.State != "suppressing" || Time.time - r.LogAt < .1f))
            return;
        r.LogKey = key;
        r.LogAt = Time.time;
        var json = Diagnostics.Serialize(
            new
            {
                schema = 1,
                session,
                sequence = ++sequence,
                feature = "M10",
                stage = "evaluated",
                decision = "AdsTravel",
                reason = t.Reason,
                realtime = Time.realtimeSinceStartup,
                utc = DateTime.UtcNow.ToString("O"),
                configRevision = config?.Revision ?? 0,
                profileId = s.ProfileId,
                name = s.Name,
                botInstance = s.Id,
                adsTravel = t,
                adsTravelConfig = config?.AdsTravel,
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
