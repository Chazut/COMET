using System;
using System.Runtime.CompilerServices;
using Comet.Core;
using EFT;
using EFT.InventoryLogic;
using HarmonyLib;
using Newtonsoft.Json;
using SAIN.Components;
using SAIN.Preset.Shared.Enums;
using SAIN.SAINComponent.Classes.EnemyClasses;
using SAIN.SAINComponent.Classes.WeaponFunction;
using UnityEngine;

namespace Comet.Client;

internal sealed class GrenadeJudgmentRuntime
{
    internal readonly GrenadeJudgmentPolicy Policy = new();
    internal double NextSample,
        LogAt = -1000,
        RoomAt = -1;
    internal int RoomEnemy,
        ArcFrame = -1,
        ArcEnemy;
    internal Vector3 RoomPoint,
        ArcPoint,
        ArcOrigin,
        ArcTarget;
    internal string LogKey = "",
        ArcItem = "";
    internal bool Fault,
        Arc,
        ObservationWarned;
}

public sealed partial class Plugin
{
    private int grenadeProbeFrame = -1;

    private static void PatchGrenadeJudgment(Harmony h)
    {
        h.Patch(
            AccessTools.Method(typeof(BotComponent), nameof(BotComponent.ManualUpdate)),
            postfix: new HarmonyMethod(typeof(Plugin), nameof(TrackGrenadeJudgment))
        );
        h.Patch(
            AccessTools.Method(
                typeof(GrenadeThrowDecider),
                "FindThrowTarget",
                new[] { typeof(Enemy) }
            ) ?? throw new MissingMethodException("G01 FindThrowTarget"),
            prefix: new HarmonyMethod(typeof(Plugin), nameof(GrenadeFindBefore)),
            postfix: new HarmonyMethod(typeof(Plugin), nameof(GrenadeFindAfter))
        );
        h.Patch(
            AccessTools.Method(typeof(GrenadeThrowDecider), "TryThrowGrenade", Type.EmptyTypes)
                ?? throw new MissingMethodException("G01 TryThrowGrenade"),
            prefix: new HarmonyMethod(typeof(Plugin), nameof(GrenadeThrowBefore))
            {
                priority = Priority.Last,
            },
            postfix: new HarmonyMethod(typeof(Plugin), nameof(GrenadeThrowAfter))
        );
    }

    private bool GrenadeJudgmentScope(BotComponent b) =>
        config?.Enabled == true
        && config.GrenadeJudgment.Enabled
        && NativeAdapter.IsSupportedAi(b)
        && b.BotActive
        && !b.GameEnding
        && Authority
        && !Ghost(b.ProfileId);

    private BotState GrenadeState(BotComponent b)
    {
        if (!states.TryGetValue(b, out var s))
            states.Add(b, s = new BotState(b, config!, session));
        return s;
    }

    private static void TrackGrenadeJudgment(BotComponent __instance)
    {
        var p = Instance;
        if (p == null)
            return;
        if (
            p.states.TryGetValue(__instance, out var running)
            && (running.GrenadeTrajectory.Pending || running.GrenadeTrajectory.Selected != null)
        )
        {
            if (!p.GrenadeTrajectoryScope(__instance) || __instance.GoalEnemy == null)
                p.StopGrenadeTrajectory(running, "trajectory-scope-ended");
            else if (running.GrenadeTrajectory.Pending)
                try
                {
                    NativeAdapter.ContinueGrenadeSearch(__instance, running, p.config!);
                }
                catch (Exception ex)
                {
                    running.GrenadeTrajectory.Fault = true;
                    p.FailGrenadeTrajectory(running, "trajectory-adapter-error");
                    p.GrenadeJudgmentError(running, ex);
                }
        }
        if (
            p.states.TryGetValue(__instance, out var choiceState)
            && !p.GrenadeChoiceScope(__instance)
        )
            p.StopGrenadeChoice(choiceState, "choice-scope-ended");
        if (p.states.TryGetValue(__instance, out var fs) && !p.GrenadeFilterScope(__instance))
            p.EndGrenadeFilter(fs, "filter-scope-ended");
        if (!p.GrenadeJudgmentScope(__instance) && !p.GrenadeChoiceScope(__instance))
            return;
        var s = p.GrenadeState(__instance);
        var r = s.GrenadeJudgment;
        if (r.Fault || Time.timeAsDouble < r.NextSample)
            return;
        r.NextSample = Time.timeAsDouble + .25;
        try
        {
            bool pending = r.Policy.Pending;
            try
            {
                if (
                    pending
                    && r.Policy.NativeProgress(
                        __instance.BotOwner.WeaponManager.Grenades.ThrowindNow,
                        __instance.BotOwner.WeaponManager.Grenades.GrenadeActionType.ToString(),
                        __instance.Player.HandsController?.GetType().Name ?? "none",
                        Time.timeAsDouble
                    )
                )
                    p.RecordGrenadeJudgment(s, true);
                r.Policy.Tick(Time.timeAsDouble, p.config!.GrenadeJudgment);
                if (pending)
                    p.RecordGrenadeJudgment(s);
            }
            catch (Exception ex)
            {
                p.GrenadeObservationError(s, ex);
            }
            var e = __instance.GoalEnemy;
            if (e == null)
                return;
            var f = p.GrenadeFrame(__instance, e, s);
            // Observe room classification only while the target is personally visible. Keep a short-lived location fact.
            if (f.Personal && e.IsVisible && e.EnemyPlayer != null)
            {
                r.RoomEnemy = f.Instance;
                r.RoomAt = e.EnemyPlayer.Environment == EnvironmentType.Indoor ? f.Now : -1;
                r.RoomPoint = new Vector3((float)f.X, (float)f.Y, (float)f.Z);
            }
            if (p.GrenadeJudgmentScope(__instance))
                r.Policy.Observe(f);
        }
        catch (Exception ex)
        {
            p.GrenadeJudgmentError(s, ex);
        }
    }

    private GrenadeJudgmentFrame GrenadeFrame(BotComponent b, Enemy e, BotState s)
    {
        var places = e.KnownPlaces;
        var k = places.LastKnownPosition;
        var place = places.LastKnownPlace;
        var r = s.GrenadeJudgment;
        string source =
            place != null && ReferenceEquals(place, places.LastSeenPlace) ? "seen"
            : place != null && ReferenceEquals(place, places.LastHeardPlace) ? "heard"
            : "shared-or-unknown";
        var f = new GrenadeJudgmentFrame
        {
            Now = Time.timeAsDouble,
            Target = e.EnemyProfileId,
            Instance = RuntimeHelpers.GetHashCode(e),
            Personal = k.HasValue && (source == "seen" || source == "heard"),
            Source = source,
            EvidenceAt = places.TimeLastKnownUpdated,
            X = k?.x ?? 0,
            Y = k?.y ?? 0,
            Z = k?.z ?? 0,
            Item = b.BotOwner.WeaponManager.Grenades.grenade?.Id.ToString() ?? "",
        };
        f.Room = e.EnemyPlayer?.Environment == EnvironmentType.Indoor;
        f.Vulnerable = e.Status.VulnerableAction != default;
        var health = b.Memory.Health.HealthStatus;
        if (r.Fault)
            f.Block = "grenade-adapter-error";
        else if (b.BotOwner.Memory.IsUnderFire)
            f.Block = "grenade-immediate-danger";
        else if (e.Path.PathLength < 10)
            f.Block = "grenade-enemy-can-rush";
        else if (health == ETagStatus.BadlyInjured || health == ETagStatus.Dying)
            f.Block = "grenade-treatment-priority";
        else if (s.Fake.Policy.Active || s.Fake.CallbackPending || FakeGuarded(b))
            f.Block = "grenade-feint-owned";
        else
            foreach (var other in b.EnemyController.VisibleEnemies)
                if (!ReferenceEquals(other, e))
                {
                    f.Block = "grenade-other-visible-threat";
                    break;
                }
        return f;
    }

    private static bool GrenadeFindBefore(
        GrenadeThrowDecider __instance,
        Enemy __0,
        ref bool __result,
        out BotState? __state
    )
    {
        __state = null;
        var p = Instance;
        var b = __instance.Bot;
        if (
            p == null
            || (
                !p.GrenadeJudgmentScope(b)
                && !p.GrenadeTrajectoryScope(b)
                && !p.GrenadeChoiceScope(b)
                && !p.GrenadeFilterScope(b)
            )
        )
            return true;
        var s = p.GrenadeState(b);
        var r = s.GrenadeJudgment;
        try
        {
            if (!ContactSafety.Allows(b, __0))
            {
                r.Arc = false;
                p.StopGrenadeTrajectory(s, "trajectory-own-squad-contact");
                p.StopGrenadeChoice(s, "choice-own-squad-contact", "blocked");
                __result = false;
                return false;
            }
            if (p.GrenadeChoiceInProgress(b) || !p.PrepareOffensiveGrenade(b, s))
            {
                __result = false;
                return false;
            }
            if (
                !p.GrenadeJudgmentScope(b)
                && !p.GrenadeTrajectoryScope(b)
                && !p.GrenadeChoiceScope(b)
            )
            {
                __state = s;
                return true;
            }
            var f = p.GrenadeFrame(b, __0, s);
            if (
                p.GrenadeJudgmentScope(b)
                && !r.Policy.Prepare(f, p.config!.GrenadeJudgment, p.config.GrenadeContactLifetime)
            )
            {
                p.RecordGrenadeJudgment(s);
                __result = false;
                return false;
            }
            if (p.GrenadeTrajectoryScope(b))
            {
                var ready = p.PrepareGrenadeTrajectory(b, __0, s, f);
                __result = ready == true;
                if (ready.HasValue)
                    __state = s;
                return false;
            }
            var g = b.BotOwner.WeaponManager.Grenades;
            if (
                r.Arc
                && r.ArcFrame == Time.frameCount
                && r.ArcEnemy == f.Instance
                && r.ArcItem == f.Item
                && (r.ArcPoint - new Vector3((float)f.X, (float)f.Y, (float)f.Z)).sqrMagnitude
                    < .01f
                && (r.ArcOrigin - b.Transform.WeaponData.WeaponRoot).sqrMagnitude < .01f
                && g.AIGreanageThrowData?.IsUpToDate() == true
            )
            {
                __state = s;
                __result = true;
                return false;
            }
            if (p.grenadeProbeFrame == Time.frameCount)
            {
                __result = false;
                return false;
            }
            p.grenadeProbeFrame = Time.frameCount;
            __state = s;
            return true;
        }
        catch (Exception ex)
        {
            p.GrenadeJudgmentError(s, ex);
            __result = false;
            return false;
        }
    }

    private static void GrenadeFindAfter(
        GrenadeThrowDecider __instance,
        Enemy __0,
        ref bool __result,
        BotState? __state
    )
    {
        var p = Instance;
        if (p == null || __state == null)
            return;
        try
        {
            var b = __instance.Bot;
            var r = __state.GrenadeJudgment;
            r.Arc = __result;
            r.ArcFrame = Time.frameCount;
            r.ArcEnemy = RuntimeHelpers.GetHashCode(__0);
            r.ArcItem = b.BotOwner.WeaponManager.Grenades.grenade?.Id.ToString() ?? "";
            r.ArcPoint = __0.KnownPlaces.LastKnownPosition ?? Vector3.zero;
            r.ArcOrigin = b.Transform.WeaponData.WeaponRoot;
            r.ArcTarget = b.BotOwner.WeaponManager.Grenades.AIGreanageThrowData?.Target
                ?? Vector3.zero;
            __result = p.ChooseGrenade(b, __0, __state, __result);
            __result = p.BindOffensiveGrenade(b, __state, __result);
            __result = p.AssessGrenade(b, __0, __state, __result);
        }
        catch (Exception ex)
        {
            p.GrenadeJudgmentError(__state, ex);
            __result = false;
        }
    }

    internal bool AssessGrenade(BotComponent b, Enemy e, BotState s, bool arc)
    {
        if (!GrenadeJudgmentScope(b))
            return arc;
        var f = GrenadeFrame(b, e, s);
        if (arc && f.Block.Length == 0)
            f.Block = GrenadeSafety(b, s, f);
        bool ok = s.GrenadeJudgment.Policy.Evaluate(
            f,
            config!.GrenadeJudgment,
            config.GrenadeContactLifetime,
            arc,
            jumpRandom.NextDouble
        );
        RecordGrenadeJudgment(s);
        return ok;
    }

    private string GrenadeSafety(BotComponent b, BotState s, GrenadeJudgmentFrame f)
    {
        var g = b.BotOwner.WeaponManager.Grenades;
        var d = g.AIGreanageThrowData;
        var item = g.grenade;
        string filtered = FilterReject(b, item);
        if (filtered.Length > 0)
            return filtered;
        if (
            item == null
            || item.IsDummy
            || item.ThrowType.ToString().IndexOf("smoke", StringComparison.OrdinalIgnoreCase) >= 0
        )
            return "grenade-no-suitable-item";
        if (d == null || !d.CanThrow)
            return "grenade-native-arc-unavailable";
        if (!NativeAdapter.GrenadeFriendsClear(b, d.Target))
            return "sain-friendly-distance";
        f.Count = 0;
        foreach (var carried in b.Player.Inventory.GetPlayerItems(EPlayerItems.Equipment))
            if (
                carried is ThrowWeap candidate
                && !candidate.IsDummy
                && FilterReject(b, candidate).Length == 0
            )
                f.Count++;
        return f.Count > 0 ? "" : "grenade-no-usable-inventory";
    }

    private static bool GrenadeThrowBefore(
        GrenadeThrowDecider __instance,
        ref bool __result,
        out BotState? __state
    )
    {
        __state = null;
        var p = Instance;
        var b = __instance.Bot;
        if (
            p == null
            || (
                !p.GrenadeJudgmentScope(b)
                && !p.GrenadeTrajectoryScope(b)
                && !p.GrenadeChoiceScope(b)
                && !p.GrenadeFilterScope(b)
            )
        )
            return true;
        var s = p.GrenadeState(b);
        try
        {
            if (!ContactSafety.AllowsGoal(b))
            {
                s.GrenadeJudgment.Arc = false;
                p.StopGrenadeTrajectory(s, "trajectory-own-squad-contact");
                p.StopGrenadeChoice(s, "choice-own-squad-contact", "blocked");
                __result = false;
                return false;
            }
            if (
                b.GoalEnemy == null
                || !p.ValidateGrenadeTrajectory(b, b.GoalEnemy, s)
                || !p.ValidateGrenadeChoice(b, b.GoalEnemy, s, true)
                || !p.AssessGrenade(b, b.GoalEnemy, s, true)
                || !p.ValidateOffensiveGrenade(b, s)
            )
            {
                __result = false;
                return false;
            }
            if (!p.GrenadeJudgmentScope(b))
                return true;
            p.EnsureClearEvents(); // Observation is optional, never a throw prerequisite.
            var f = p.GrenadeFrame(b, b.GoalEnemy, s);
            s.GrenadeJudgment.Policy.Request(f);
            p.RecordGrenadeJudgment(s, true);
            __state = s;
            return true;
        }
        catch (Exception ex)
        {
            p.GrenadeJudgmentError(s, ex);
            __result = false;
            return false;
        }
    }

    private static void GrenadeThrowAfter(
        GrenadeThrowDecider __instance,
        bool __result,
        BotState? __state
    )
    {
        var p = Instance;
        if (p == null || __state == null)
            return;
        try
        {
            __state.GrenadeJudgment.Policy.ObserveRequest(
                __result,
                __instance.Bot.BotOwner.WeaponManager.Grenades.ThrowindNow,
                Time.timeAsDouble,
                p.config!.GrenadeJudgment
            );
            p.RecordGrenadeJudgment(__state, true);
        }
        catch (Exception ex)
        {
            p.GrenadeObservationError(__state, ex);
        }
    }

    private void JudgmentProjectile(Grenade grenade)
    {
        foreach (var pair in states)
            if (
                pair.Key.ProfileId == grenade.ProfileId
                && pair.Value.GrenadeJudgment.Policy.Released(
                    grenade.WeaponSource?.Id.ToString() ?? "",
                    Time.timeAsDouble
                )
            )
                RecordGrenadeJudgment(pair.Value, true);
    }

    private void EndGrenadeJudgment(BotState s, string reason)
    {
        EndGrenadeFilter(s, reason);
        StopGrenadeChoice(s, reason);
        StopGrenadeTrajectory(s, reason);
        s.GrenadeJudgment.Policy.Clear(reason);
        s.GrenadeJudgment.RoomAt = -1;
        s.GrenadeJudgment.Arc = false;
        RecordGrenadeJudgment(s);
    }

    private void GrenadeJudgmentError(BotState s, Exception ex)
    {
        bool warn = !s.GrenadeJudgment.Fault;
        s.GrenadeJudgment.Fault = true;
        EndGrenadeJudgment(s, "grenade-adapter-error");
        if (warn)
            Logger.LogWarning("G01 blocked grenade requests: " + ex.GetBaseException().Message);
    }

    private void GrenadeObservationError(BotState s, Exception ex)
    {
        if (s.GrenadeJudgment.ObservationWarned)
            return;
        s.GrenadeJudgment.ObservationWarned = true;
        Logger.LogWarning(
            "G01 observation unavailable; native throwing unchanged: "
                + ex.GetBaseException().Message
        );
    }

    private void RecordGrenadeJudgment(BotState s, bool force = false)
    {
        var r = s.GrenadeJudgment;
        var t = r.Policy.Trace;
        if (t == null)
            return;
        double now = Time.timeAsDouble;
        string key = t.Target + "|" + t.State + "|" + t.Reason + "|" + t.Roll;
        if (!force && key == r.LogKey && (t.ValidFor == 0 || now - r.LogAt < 5))
            return;
        r.LogKey = key;
        r.LogAt = now;
        var json = Diagnostics.Serialize(
            new
            {
                schema = 1,
                session,
                sequence = ++sequence,
                feature = "G01",
                stage = "evaluated",
                decision = "GrenadeJudgment",
                reason = t.Reason,
                realtime = Time.realtimeSinceStartup,
                utc = DateTime.UtcNow.ToString("O"),
                configRevision = config?.Revision ?? 0,
                profileId = s.ProfileId,
                name = s.Name,
                botInstance = s.Id,
                grenadeJudgment = t,
                grenadeJudgmentConfig = config?.GrenadeJudgment,
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
