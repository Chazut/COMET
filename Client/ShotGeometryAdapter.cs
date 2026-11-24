using System;
using System.Collections.Generic;
using Comet.Core;
using Comfort.Common;
using EFT;
using EFT.Ballistics;
using EFT.Interactive;
using EFT.InventoryLogic;
using HarmonyLib;
using Newtonsoft.Json;
using SAIN.Components;
using SAIN.Models.Enums;
using SAIN.SAINComponent.Classes.EnemyClasses;
using SAIN.SAINComponent.Classes.WeaponFunction;
using UnityEngine;

namespace Comet.Client;

internal sealed class ShotGeometryRuntime
{
    internal ShotGeometryTrace? Trace;
    internal Vector3 Muzzle,
        Aim,
        Direction;
    internal float At = -1000,
        LogAt = -1000,
        NativeStarted;
    internal int Frame = -1;
    internal bool StaticBlocked,
        Fault,
        Tracked;
    internal string Key = "",
        LogKey = "";
    internal BotComponent? Owner;
    internal Enemy? Enemy;
    internal Vector3 NativeAim;
    internal EShootReason NativeReason;
    internal readonly RaycastHit[] Forward = new RaycastHit[32],
        Backward = new RaycastHit[32],
        Allies = new RaycastHit[32];
    internal readonly Collider[] Overlaps = new Collider[32];
}

public sealed partial class Plugin
{
    // Harmony stores __state per declaring type: keep this module independent of other hooks.
    private static class I01GeometryHooks
    {
        internal static bool GeometryRequest(
            ManualShootClass __instance,
            Enemy Enemy,
            Vector3 targetPos,
            EShootReason reason,
            ref bool __result,
            ref BotState? __state
        ) =>
            Plugin.GeometryRequest(__instance, Enemy, targetPos, reason, ref __result, ref __state);

        internal static void GeometryResult(
            ManualShootClass __instance,
            Enemy Enemy,
            bool __result,
            BotState? __state
        ) => Plugin.GeometryResult(__instance, Enemy, __result, __state);
    }

    private static void PatchShotGeometry(Harmony h)
    {
        h.Patch(
            AccessTools.Method(typeof(ManualShootClass), nameof(ManualShootClass.TryShoot)),
            prefix: new HarmonyMethod(
                typeof(I01GeometryHooks),
                nameof(I01GeometryHooks.GeometryRequest)
            ),
            postfix: new HarmonyMethod(
                typeof(I01GeometryHooks),
                nameof(I01GeometryHooks.GeometryResult)
            )
        );
        h.Patch(
            AccessTools.Method(typeof(ManualShootClass), nameof(ManualShootClass.ManualUpdate)),
            prefix: new HarmonyMethod(typeof(Plugin), nameof(GeometryUpdate))
            {
                priority = Priority.First,
            }
        );
        h.Patch(
            AccessTools.Method(typeof(ManualShootClass), nameof(ManualShootClass.Reset)),
            postfix: new HarmonyMethod(typeof(Plugin), nameof(GeometryReset))
        );
    }

    private bool GeometryScope(BotComponent b) =>
        ContactSafety.AllowsGoal(b)
        && config?.Enabled == true
        && config.ShotGeometry.Enabled
        && NativeAdapter.IsSupportedAi(b)
        && Authority
        && b.BotActive
        && !b.IsDead
        && !b.GameEnding
        && !Ghost(b.ProfileId)
        && b.SAINLayersActive;

    // Used by M06, W07 and ordinary native manual fire. Null preserves their pre-W08 checks when disabled/faulted.
    private ShotGeometryTrace? CheckShotGeometry(
        BotComponent b,
        BotState s,
        Vector3 aim,
        string consumer,
        string target
    )
    {
        var r = s.ShotGeometry;
        if (r.Fault)
            return null;
        try
        {
            if (!GeometryScope(b))
                return null;
            var muzzle = b.Transform.WeaponData.FirePort;
            var direction = b.Transform.WeaponData.PointDirection;
            var weapon = (b.Player.HandsController as Player.FirearmController)?.Item;
            string key = consumer + ":" + target;
            bool visible = b.GoalEnemy?.EnemyProfileId == target && b.GoalEnemy.IsVisible;
            if (
                !visible
                && ShotGeometryPolicy.CanReuse(
                    Time.time,
                    r.At,
                    Time.frameCount,
                    r.Frame,
                    r.Trace,
                    DP(muzzle),
                    DP(r.Muzzle),
                    DP(aim),
                    DP(r.Aim),
                    DP(direction),
                    DP(r.Direction),
                    key,
                    r.Key,
                    true,
                    config!.ShotGeometry
                )
            )
            {
                r.Trace!.Cached = true;
                r.Trace.Age = Time.time - r.At;
                RecordGeometry(s);
                return r.Trace;
            }
            var line = aim - muzzle;
            float distance = line.magnitude;
            var f = new ShotGeometryFrame
            {
                Complete = true,
                OriginClear = true,
                AllySafe = true,
                Visible = visible,
                Blindfire = consumer.IndexOf("blind", StringComparison.OrdinalIgnoreCase) >= 0,
                TargetDistance = distance,
                FloorDifference =
                    (b.GoalEnemy?.KnownPlaces.LastKnownPosition ?? aim).y - b.Position.y,
            };
            if (
                !visible
                && distance >= .5f
                && Physics.Raycast(
                    muzzle,
                    line / distance,
                    out var hit,
                    distance,
                    LayersMaskController.HighPolyWithTerrainMask
                )
            )
            {
                string kind = "solid";
                var collider = hit.collider;
                if (collider?.GetComponentInParent<Door>() != null)
                    kind = "door";
                else if (
                    collider?.GetComponentInParent<BallisticCollider>() is { } ballistic
                    && ballistic.TypeOfMaterial is MaterialType.Glass or MaterialType.GlassShattered
                )
                    kind = "glass";
                f.Surfaces = new[]
                {
                    new ShotSurface { Distance = hit.distance, Kind = kind },
                };
            }
            r.Trace = ShotGeometryPolicy.Evaluate(f, config!.ShotGeometry);
            CompleteGeometry(
                b,
                s,
                muzzle,
                aim,
                direction,
                key,
                consumer,
                target,
                weapon,
                null,
                true
            );
            return r.Trace;
        }
        catch (Exception ex)
        {
            GeometryError(s, ex);
            return null;
        }
    }

    private void CompleteGeometry(
        BotComponent b,
        BotState s,
        Vector3 muzzle,
        Vector3 aim,
        Vector3 direction,
        string key,
        string consumer,
        string target,
        Weapon? weapon,
        Ammo? ammo,
        bool stable
    )
    {
        var r = s.ShotGeometry;
        var t = r.Trace!;
        r.At = Time.time;
        r.Frame = Time.frameCount;
        r.Key = key;
        r.Muzzle = muzzle;
        r.Aim = aim;
        r.Direction = direction;
        r.StaticBlocked = stable;
        t.Consumer = consumer;
        t.Target = target;
        t.Action = b.Decision.CurrentCombatDecision.ToString();
        t.Weapon = weapon?.Id.ToString() ?? "";
        t.Ammo = ammo?.TemplateId.ToString() ?? "";
        if (CornerChase.Point(DP(muzzle)))
        {
            t.MuzzleX = muzzle.x;
            t.MuzzleY = muzzle.y;
            t.MuzzleZ = muzzle.z;
        }
        if (CornerChase.Point(DP(aim)))
        {
            t.AimX = aim.x;
            t.AimY = aim.y;
            t.AimZ = aim.z;
        }
        RecordGeometry(s);
    }

    private static bool GeometryRequest(
        ManualShootClass __instance,
        Enemy Enemy,
        Vector3 targetPos,
        EShootReason reason,
        ref bool __result,
        ref BotState? __state
    )
    {
        var p = Instance;
        var b = __instance.Bot;
        BotState? state = null;
        if (p == null)
            return true;
        try
        {
            if (!p.GeometryScope(b) || Enemy?.IsVisible != false)
                return true;
            if (!p.states.TryGetValue(b, out var s))
            {
                s = new BotState(b, p.config!, p.session);
                p.states.Add(b, s);
            }
            state = s;
            // Their own request already goes through the shared checker, with stricter contact and ammo policy.
            if (
                s.CornerPrefire.InRequest
                || reason == EShootReason.Suppress
                    && p.DisciplineScope(b)
                    && !s.SuppressionDiscipline.Fault
            )
                return true;
            var t = p.CheckShotGeometry(b, s, targetPos, reason.ToString(), Enemy.EnemyProfileId);
            if (t == null)
                return true;
            if (!t.Allowed)
            {
                p.StopGeometryOwned(s);
                __result = false;
                return false;
            }
            __state = s;
            return true;
        }
        catch (Exception ex)
        {
            if (state != null)
                p.GeometryError(state, ex);
            return true;
        }
    }

    private static void GeometryResult(
        ManualShootClass __instance,
        Enemy Enemy,
        bool __result,
        BotState? __state
    )
    {
        if (__state == null)
            return;
        var r = __state.ShotGeometry;
        try
        {
            if (!__result)
            {
                Instance?.StopGeometryOwned(__state);
                return;
            }
            r.Owner = __instance.Bot;
            r.Enemy = Enemy;
            r.NativeAim = __instance.ShootPosition;
            r.NativeReason = __instance.Reason;
            r.NativeStarted = (float)prefireStarted.GetValue(__instance);
            r.Tracked = true;
        }
        catch (Exception ex)
        {
            Instance?.GeometryError(__state, ex);
        }
    }

    private static bool GeometryOwns(ShotGeometryRuntime r) =>
        r.Tracked
        && r.Owner != null
        && r.Owner.ManualShoot.Reason == r.NativeReason
        && ReferenceEquals(prefireEnemy.GetValue(r.Owner.ManualShoot), r.Enemy)
        && (float)prefireStarted.GetValue(r.Owner.ManualShoot) == r.NativeStarted;

    private static void GeometryUpdate(ManualShootClass __instance)
    {
        var p = Instance;
        if (p == null || !p.states.TryGetValue(__instance.Bot, out var s))
            return;
        var r = s.ShotGeometry;
        if (!r.Tracked)
            return;
        try
        {
            if (!p.GeometryScope(__instance.Bot) || r.Enemy?.IsVisible != false || !GeometryOwns(r))
            {
                p.ReleaseGeometry(s, "geometry-native-handoff");
                return;
            }
            // The same native request may move its remembered aim. Revalidate it, never silently drop the guard.
            r.NativeAim = __instance.ShootPosition;
            var t = p.CheckShotGeometry(
                __instance.Bot,
                s,
                __instance.ShootPosition,
                r.NativeReason.ToString(),
                r.Enemy.EnemyProfileId
            );
            if (t?.Allowed == false)
                p.StopGeometryOwned(s);
        }
        catch (Exception ex)
        {
            p.GeometryError(s, ex);
        }
    }

    private static void GeometryReset(ManualShootClass __instance)
    {
        var p = Instance;
        if (p != null && p.states.TryGetValue(__instance.Bot, out var s) && s.ShotGeometry.Tracked)
        {
            try
            {
                p.ReleaseGeometry(s, "geometry-native-reset");
            }
            catch (Exception ex)
            {
                p.GeometryError(s, ex);
            }
        }
    }

    private void StopGeometryOwned(BotState s)
    {
        var r = s.ShotGeometry;
        var owner = r.Owner;
        bool own = false;
        try
        {
            own = GeometryOwns(r);
        }
        finally
        {
            r.Tracked = false;
            r.Owner = null;
            r.Enemy = null;
        }
        if (own)
            owner!.ManualShoot.Reset();
    }

    private void ReleaseGeometry(BotState s, string reason)
    {
        var r = s.ShotGeometry;
        r.Tracked = false;
        r.Owner = null;
        r.Enemy = null;
        r.Frame = -1;
        r.Key = "";
        GeometryClearBuffers(r);
        if (r.Trace != null && r.Trace.State != "released")
        {
            r.Trace.State = "released";
            r.Trace.Allowed = false;
            r.Trace.ValidFor = 0;
            r.Trace.Reason = reason;
            RecordGeometry(s);
        }
    }

    private static void GeometryClearBuffers(ShotGeometryRuntime r)
    {
        Array.Clear(r.Forward, 0, r.Forward.Length);
        Array.Clear(r.Backward, 0, r.Backward.Length);
        Array.Clear(r.Allies, 0, r.Allies.Length);
        Array.Clear(r.Overlaps, 0, r.Overlaps.Length);
    }

    private void GeometryError(BotState s, Exception ex)
    {
        var r = s.ShotGeometry;
        bool warn = !r.Fault;
        r.Fault = true;
        try
        {
            StopGeometryOwned(s);
        }
        catch
        { /* Foreign/native teardown must not break the game loop. */
        }
        try
        {
            ReleaseGeometry(s, "geometry-adapter-error");
        }
        catch
        {
            r.Tracked = false;
            r.Owner = null;
            r.Enemy = null;
            GeometryClearBuffers(r);
        }
        if (warn)
            Logger.LogWarning("W08 yielded: " + ex.GetBaseException().Message);
    }

    private void RecordGeometry(BotState s)
    {
        if (!Comet.Core.ReplayCapture.Detailed)
            return;
        var r = s.ShotGeometry;
        var t = r.Trace;
        if (t == null)
            return;
        string key = t.State + ":" + t.Reason + ":" + t.Consumer + ":" + t.Target + ":" + t.Action;
        if (key == r.LogKey && (t.State == "released" || Time.time - r.LogAt < .1f))
            return;
        r.LogKey = key;
        r.LogAt = Time.time;
        var json = Diagnostics.Serialize(
            new
            {
                schema = 1,
                session,
                sequence = ++sequence,
                feature = "W08",
                stage = "evaluated",
                decision = "ShotGeometry",
                reason = t.Reason,
                realtime = Time.realtimeSinceStartup,
                utc = DateTime.UtcNow.ToString("O"),
                configRevision = config?.Revision ?? 0,
                profileId = s.ProfileId,
                name = s.Name,
                botInstance = s.Id,
                shotGeometry = t,
                shotGeometryConfig = config?.ShotGeometry,
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
