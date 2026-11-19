using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using Comet.Core;
using EFT;
using EFT.Interactive;
using HarmonyLib;
using Newtonsoft.Json;
using SAIN.Classes;
using SAIN.Components;
using SAIN.Models.Enums;
using SAIN.Preset.Shared.Enums;
using SAIN.SAINComponent.Classes.Mover;
using UnityEngine;

namespace Comet.Client;

internal sealed class DoorTransitRuntime
{
    internal readonly DoorTransit Policy = new();
    internal readonly TransitSpeedLease Speed = new();
    internal readonly List<DoorPoint> Route = new();
    internal BotComponent? Owner;
    internal IBotPathData? Path;
    internal object? Action;
    internal DoorWork? Door;
    internal float Started,
        Checked = -100,
        Next,
        Tick = -100,
        LogAt = -100;
    internal double Side;
    internal bool Writing,
        Fault,
        Validated,
        DirectBlocker,
        SprintOwned,
        DesiredSprint;
    internal (string, string, string, bool) Key;
}

public sealed partial class Plugin
{
    private ConditionalWeakTable<PlayerMovementController, BotState> transitControllers = new();
    private static PropertyInfo transitDoors = null!;
    private static FieldInfo transitDoorIndex = null!;

    private static void PatchDoorTransit(Harmony h)
    {
        transitDoors = AccessTools.Property(typeof(DoorOpener), "_interactionDoors")
            ?? throw new MissingMemberException("C01 native door list");
        transitDoorIndex = AccessTools.Field(typeof(DoorOpener), "_interactionDoorIndex")
            ?? throw new MissingFieldException("C01 native door index");
        if (
            transitDoors.PropertyType != typeof(List<DoorDataStruct>)
            || transitDoorIndex.FieldType != typeof(int)
            || AccessTools.Field(typeof(BotPathDataManual), "Bot")?.FieldType
                != typeof(BotComponent)
        )
            throw new MissingMemberException("C01 native path contract");
        h.Patch(
            AccessTools.Method(typeof(BotPathDataManual), nameof(BotPathDataManual.TickPath)),
            prefix: new HarmonyMethod(typeof(Plugin), nameof(TransitTick)),
            finalizer: new HarmonyMethod(typeof(Plugin), nameof(TransitFinally))
        );
        // Run before D15's native breach choice. Never resurrect a request that D15 has consumed.
        h.Patch(
            AccessTools.Method(typeof(DoorOpener), nameof(DoorOpener.SelectDoor)),
            postfix: new HarmonyMethod(typeof(Plugin), nameof(TransitSelect))
            {
                priority = Priority.First,
            }
        );
        h.Patch(
            AccessTools.Method(
                typeof(PlayerMovementController),
                nameof(PlayerMovementController.SetTargetMoveSpeed)
            ),
            prefix: new HarmonyMethod(typeof(Plugin), nameof(TransitSpeed))
            {
                priority = Priority.Last,
            }
        );
        h.Patch(
            AccessTools.Method(
                typeof(BotPathDataManual),
                nameof(BotPathDataManual.RequestStartSprint)
            ),
            postfix: new HarmonyMethod(typeof(Plugin), nameof(TransitSprintStarted))
        );
        h.Patch(
            AccessTools.Method(
                typeof(BotPathDataManual),
                nameof(BotPathDataManual.RequestEndSprint)
            ),
            postfix: new HarmonyMethod(typeof(Plugin), nameof(TransitSprintEnded))
        );
    }

    private bool TransitScoped(BotComponent b, BotState s, IBotPathData path, bool recovery = false)
    {
        if (
            config?.Enabled != true
            || (!recovery && (!config.DoorTransit.Enabled || s.Transit.Fault))
            || !Authority
            || b == null
            || !NativeAdapter.IsSupportedAi(b)
            || !b.BotActive
            || b.IsDead
            || b.GameEnding
            || Ghost(b.ProfileId)
            || !b.Player.MovementContext.IsGrounded
            || !ReferenceEquals(b.Mover.ActivePath, path)
            || path is not BotPathDataManual manual
            || manual.CancelRequested
            || (
                !recovery
                && (manual.PausedRequested || !path.Moving || path.Status == EBotMoveStatus.Paused)
            )
            || SupportingDoor(s) != null
            || s.Door.Policy.Planning
            || s.Interruption.Policy.Pending
            || s.Support != null
        )
            return false;
        // Dedicated COMET paths remain eligible, including urgent escape. Tactical door plans keep their own movement rules.
        if (
            s.GrenadeEscape.Handled
            && ReferenceEquals(s.GrenadeEscape.Path, path)
            && ReferenceEquals(s.GrenadeEscape.Mesh, path.NavMeshPath)
            && Time.time < s.GrenadeEscape.CommitUntil
        )
            return true;
        if (
            s.Crossfire.Layer
            && s.Crossfire.Policy.Active
            && ReferenceEquals(s.Crossfire.Path, path)
            && s.Crossfire.PathStarted == path.TimeStarted
        )
            return true;
        return b.SAINLayersActive; // zzap: door traversal follows the active PMC path, including ordinary SAIN combat paths.
    }

    private bool TransitCurrent(BotComponent b, BotState s)
    {
        var r = s.Transit;
        return r.Path != null
            && r.Path.TimeStarted == r.Started
            && ReferenceEquals(r.Action, b.CurrentAction)
            && Time.time - r.Checked <= .35f
            && r.Validated
            && TransitScoped(b, s, r.Path);
    }

    private static void TransitTick(BotPathDataManual __instance, BotComponent ___Bot)
    {
        var p = Instance;
        if (p == null || !p.states.TryGetValue(___Bot, out var s))
            return;
        try
        {
            p.PrepareTransit(___Bot, s, __instance);
        }
        catch (Exception ex)
        {
            p.TransitError(s, ex);
        }
    }

    private static Exception? TransitFinally(BotComponent ___Bot, Exception? __exception)
    {
        var p = Instance;
        if (p != null && p.states.TryGetValue(___Bot, out var s))
        {
            if (__exception != null)
                p.TransitError(s, __exception);
            else
                p.WatchDoorTransit(___Bot, s);
        }
        return __exception;
    }

    private void PrepareTransit(BotComponent b, BotState s, BotPathDataManual path)
    {
        var r = s.Transit;
        if (!TransitScoped(b, s, path))
        {
            EndDoorTransit(s, "transit-native-or-tactical-priority");
            return;
        }
        if (
            !ReferenceEquals(r.Path, path)
            || r.Started != path.TimeStarted
            || !ReferenceEquals(r.Action, b.CurrentAction)
        )
        {
            EndDoorTransit(s, "transit-movement-changed");
            r.Owner = b;
            r.Path = path;
            r.Started = path.TimeStarted;
            r.Action = b.CurrentAction;
            r.Next = 0;
            transitControllers.Remove(b.PlayerComponent.CharacterController);
            transitControllers.Add(b.PlayerComponent.CharacterController, s);
        }
        r.Tick = Time.time;
        if (Time.time >= r.Next && doorBudget.TryBegin(Time.frameCount, b))
        {
            var timer = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                ProbeTransit(b, s, path);
                r.Next = Time.time + .1f;
            }
            finally
            {
                timer.Stop();
                doorBudget.Charge(timer.Elapsed.TotalMilliseconds);
            }
        }
        if (!TransitCurrent(b, s))
        {
            RestoreTransitSpeed(r);
            return;
        }
        var t = r.Policy.Trace;
        if (t.State == "blocked")
        {
            s.Progress.Fail(ProgressAdapter.Destination(path.Destination), Time.time, t.Reason);
            RecordDoorTransit(s, true);
            path.Cancel(0);
            EndDoorTransit(s, t.Reason);
            if (s.ActiveLabel.Length > 0)
                Release(s, t.Reason);
            return;
        }
        if (t.StopSprint)
        {
            // Clear intent BEFORE SAIN computes sprint steering, otherwise it also clears aiming and shooting.
            if (!r.SprintOwned)
            {
                r.DesiredSprint = path.WantToSprint;
                r.SprintOwned = true;
            }
            r.Writing = true;
            try
            {
                path.RequestEndSprint(path.SprintUrgency, "COMET C01 blocking door");
            }
            finally
            {
                r.Writing = false;
            }
            b.PlayerComponent.CharacterController.SetWantToSprint(false);
        }
        else
            RestoreTransitSprint(r);
        if (t.Slow)
        {
            if (!r.Speed.Owned)
            {
                float requested = (float)
                    combatNativeSpeed.GetValue(b.PlayerComponent.CharacterController)!;
                WriteTransitSpeed(r, r.Speed.Limit(requested, config!.DoorTransit.SpeedCap));
            }
            t.RequestedSpeed = r.Speed.Requested;
            t.AppliedSpeed = r.Speed.Applied;
        }
        else
            RestoreTransitSpeed(r);
        RecordDoorTransit(s);
    }

    private void ProbeTransit(BotComponent b, BotState s, IBotPathData path)
    {
        var r = s.Transit;
        r.Validated = false;
        if (r.Door != null)
        {
            var g = r.Door.Geometry;
            if (
                r.Side * (DP(b.Position) - g.Center).Dot(g.Normal) < -.25
                && Math.Abs((DP(b.Position) - g.Center).Dot(g.Axis)) < g.HalfWidth + .4
            )
            {
                r.Policy.End("transit-crossing-observed", true);
                RecordDoorTransit(s, true);
                r.Door = null;
            }
        }
        r.Route.Clear();
        r.Route.Add(DP(b.Position));
        if (
            path.PathCorners == null
            || path.PathCorners.Count > 128
            || path.CurrentIndex < 0
            || path.CurrentIndex >= path.PathCorners.Count
        )
            return;
        for (int i = path.CurrentIndex; i < path.PathCorners.Count; i++)
            r.Route.Add(DP(path.PathCorners[i].Position));
        var links = b.BotOwner.VoxelesPersonalData.CurVoxel?.DoorLinks;
        if (links == null || links.Count > 48)
            return;
        DoorWork? best = null;
        double distance = double.PositiveInfinity;
        foreach (var link in links)
        {
            if (link == null || link.Door == null)
                continue;
            var data = new DoorDataStruct(link);
            if (!TryDoorGeometry(data, out var g))
                continue;
            double d = DoorTransit.RouteDistance(g, r.Route, config!.DoorTransit.Anticipation);
            if (d < distance)
            {
                distance = d;
                best = new() { Data = data, Geometry = g };
            }
        }
        r.Checked = Time.time;
        r.Validated = true;
        if (best == null)
        {
            r.Door = null;
            r.Policy.End("transit-no-door-on-route");
            RestoreTransitSpeed(r);
            RecordDoorTransit(s);
            return;
        }
        if (r.Door?.Geometry.Id != best.Geometry.Id)
            r.Side = Math.Sign((DP(b.Position) - best.Geometry.Center).Dot(best.Geometry.Normal));
        r.Door = best;
        var door = best.Data.Door;
        bool interacting = b.DoorOpener.Interacting && b.DoorOpener.GetActiveDoor().Door == door;
        bool busy =
            (
                !interacting
                && (door.DoorState == EDoorState.Interacting || b.BotOwner.DoorOpener.Interacting)
            ) || DoorBusy(b, s, best.Geometry.Id);
        // Also respect a live tactical reservation by another group. Never cancel or steal it.
        foreach (var pair in states)
            if (
                !ReferenceEquals(pair.Value, s)
                && pair.Value.Reservation.Lease is { } lease
                && lease.Door == best.Geometry.Id
                && lease.Until > Time.time
            )
            {
                busy = true;
                break;
            }
        r.DirectBlocker = TransitCollider(b, path, door);
        bool blocked = door.DoorState != EDoorState.Open || r.DirectBlocker;
        if (door.DoorState is not (EDoorState.Open or EDoorState.Shut or EDoorState.Interacting))
            busy = true;
        r.Policy.Observe(
            best.Geometry.Id,
            door.DoorState.ToString(),
            s.Decision,
            DP(b.Position),
            best.Geometry.Center,
            distance,
            Time.time,
            blocked,
            busy,
            interacting,
            config!.DoorTransit
        );
    }

    private static bool TransitCollider(BotComponent b, IBotPathData path, Door door)
    {
        var origin = b.Position + Vector3.up;
        var direction = path.GetCurrentCorner().Position - b.Position;
        direction.y = 0;
        return direction.sqrMagnitude > .001f
            && Physics.SphereCast(
                origin,
                .2f,
                direction.normalized,
                out var hit,
                Math.Min(3, direction.magnitude),
                LayersMaskController.PlayerStaticDoorMask,
                QueryTriggerInteraction.Ignore
            )
            && (
                hit.collider == door.Collider
                || hit.collider != null && hit.collider.GetComponentInParent<Door>() == door
            );
    }

    private static void TransitSelect(
        DoorOpener __instance,
        IBotPathData pathData,
        ref EInteractionType interactionType,
        ref DoorDataStruct currentDoor,
        ref bool __result
    )
    {
        var p = Instance;
        var b = __instance.Bot;
        if (p == null || !p.states.TryGetValue(b, out var s) || __instance.Interacting)
            return;
        try
        {
            var r = s.Transit;
            if (!p.TransitCurrent(b, s) || !ReferenceEquals(pathData, r.Path))
                return;
            var w = r.Door;
            if (
                w == null
                || r.Policy.Trace.State is "waiting" or "blocked"
                || currentDoor.Door != null && currentDoor.Door != w.Data.Door
            )
            {
                __result = false;
            }
            if (w == null || r.Policy.Trace.State is "waiting" or "blocked")
                return;
            if (__result && currentDoor.Door == w.Data.Door)
            {
                if (interactionType == EInteractionType.Close && !r.DirectBlocker)
                    __result = false;
                return;
            }
            // Native cached range and cooldown checks still apply. Do not call TryInteractWithDoor with an arbitrary index.
            if (
                !r.Policy.Trace.Recovery
                || w.Data.Door.DoorState is not (EDoorState.Shut or EDoorState.Open)
            )
                return;
            if (w.Data.Door.DoorState == EDoorState.Open && !r.DirectBlocker)
                return;
            var list = (List<DoorDataStruct>)transitDoors.GetValue(__instance)!;
            for (int i = 0; i < list.Count && i < 48; i++)
            {
                var data = list[i];
                if (data.Door != w.Data.Door)
                    continue;
                data.ManualUpdate(b.Position);
                if (!data.InRangeToInteract(data.Door) || !data.CanInteractByTime(Time.time))
                    return;
                list[i] = data;
                transitDoorIndex.SetValue(__instance, i);
                currentDoor = data;
                interactionType =
                    w.Data.Door.DoorState == EDoorState.Open
                        ? EInteractionType.Close
                        : EInteractionType.Open;
                __result = true;
                r.Policy.Trace.Reason =
                    interactionType == EInteractionType.Close
                        ? "transit-open-leaf-blocks-route"
                        : "transit-shut-door-recovery";
                r.Policy.Trace.Requested = true;
                p.RecordDoorTransit(s, true);
                return;
            }
        }
        catch (Exception ex)
        {
            p.TransitError(s, ex);
        }
    }

    private static void TransitSpeed(PlayerMovementController __instance, ref float __0)
    {
        var p = Instance;
        if (
            p == null
            || !p.transitControllers.TryGetValue(__instance, out var s)
            || s.Transit.Writing
            || s.Transit.Owner is not { } b
        )
            return;
        try
        {
            var r = s.Transit;
            if (p.TransitCurrent(b, s) && r.Policy.Trace.Slow)
                __0 = r.Speed.Limit(__0, p.config!.DoorTransit.SpeedCap);
            else
                r.Speed.Release(float.NaN, out _); // This incoming command owns speed now; never overwrite it.
        }
        catch (Exception ex)
        {
            p.TransitError(s, ex);
        }
    }

    private static void TransitSprintStarted(BotPathDataManual __instance, BotComponent ___Bot) =>
        TransitSprintIntent(__instance, ___Bot, true);

    private static void TransitSprintEnded(BotPathDataManual __instance, BotComponent ___Bot) =>
        TransitSprintIntent(__instance, ___Bot, false);

    private static void TransitSprintIntent(BotPathDataManual path, BotComponent bot, bool start)
    {
        var p = Instance;
        if (p == null || !p.states.TryGetValue(bot, out var s))
            return;
        var r = s.Transit;
        if (
            !r.Writing
            && ReferenceEquals(r.Path, path)
            && r.Started == path.TimeStarted
            && r.SprintOwned
        )
            r.DesiredSprint = start;
    }

    private static void RestoreTransitSprint(DoorTransitRuntime r)
    {
        if (!r.SprintOwned)
            return;
        r.SprintOwned = false;
        if (
            !r.DesiredSprint
            || r.Path is not BotPathDataManual path
            || r.Owner == null
            || r.Owner.IsDead
            || path.CancelRequested
            || !ReferenceEquals(r.Owner.Mover.ActivePath, path)
            || path.TimeStarted != r.Started
            || !ReferenceEquals(r.Owner.CurrentAction, r.Action)
        )
            return;
        r.Writing = true;
        try
        {
            path.RequestStartSprint(path.SprintUrgency, "COMET C01 restriction released");
        }
        finally
        {
            r.Writing = false;
        }
    }

    private static void WriteTransitSpeed(DoorTransitRuntime r, float speed)
    {
        if (r.Owner == null || r.Owner.IsDead)
            return;
        r.Writing = true;
        try
        {
            r.Owner.Mover.SetTargetMoveSpeed(speed);
        }
        finally
        {
            r.Writing = false;
        }
    }

    private static void RestoreTransitSpeed(DoorTransitRuntime r)
    {
        if (!r.Speed.Owned)
            return;
        float current =
            r.Owner == null || r.Owner.IsDead
                ? float.NaN
                : (float)combatNativeSpeed.GetValue(r.Owner.PlayerComponent.CharacterController)!;
        if (r.Speed.Release(current, out var restore))
            WriteTransitSpeed(r, restore);
    }

    private void WatchDoorTransit(BotComponent b, BotState s)
    {
        try
        {
            if (
                s.Transit.Path != null
                && (!TransitCurrent(b, s) || Time.time - s.Transit.Tick > .35f)
            )
                EndDoorTransit(s, "transit-ownership-or-observation-ended");
        }
        catch (Exception ex)
        {
            TransitError(s, ex);
        }
    }

    private void EndDoorTransit(BotState s, string reason)
    {
        var r = s.Transit;
        Exception? failure = null;
        // Teardown can meet disposed native objects. Each restoration and cleanup must still run.
        try
        {
            RestoreTransitSpeed(r);
        }
        catch (Exception ex)
        {
            failure = ex;
            r.Speed.Release(float.NaN, out _);
        }
        try
        {
            RestoreTransitSprint(r);
        }
        catch (Exception ex)
        {
            failure ??= ex;
        }
        try
        {
            bool terminal =
                reason.Contains("ghost")
                || reason.Contains("inactive")
                || reason.Contains("authority")
                || reason.Contains("raid-ended")
                || reason.Contains("bot-ended")
                || reason.Contains("error")
                || reason.Contains("exception")
                || reason.Contains("reconsider");
            bool active = r.Policy.Trace.State != "ended";
            // Scope exit must clear a paused incident too, even if it already emitted its end event.
            if (terminal)
                r.Policy.End(reason);
            else if (active)
                r.Policy.Pause(reason);
            if (active)
                RecordDoorTransit(s, true);
        }
        catch (Exception ex)
        {
            failure ??= ex;
        }
        try
        {
            if (r.Owner != null)
                transitControllers.Remove(r.Owner.PlayerComponent.CharacterController);
        }
        catch (Exception ex)
        {
            failure ??= ex;
        }
        finally
        {
            r.Path = null;
            r.Owner = null;
            r.Action = null;
            r.Door = null;
            r.Route.Clear();
            r.Validated = false;
        }
        if (failure != null && !r.Fault)
        {
            r.Fault = true;
            Logger.LogWarning(
                "COMET C01 cleanup yielded for this bot: " + failure.GetBaseException().Message
            );
        }
    }

    private void TransitError(BotState s, Exception ex)
    {
        if (s.Transit.Fault)
            return;
        s.Transit.Fault = true;
        try
        {
            EndDoorTransit(s, "transit-adapter-error");
        }
        finally
        {
            Logger.LogWarning("COMET C01 yielded for this bot: " + ex.GetBaseException().Message);
        }
    }

    private void RecordDoorTransit(BotState s, bool force = false)
    {
        var r = s.Transit;
        var t = r.Policy.Trace;
        if (t.Door.Length == 0)
            return;
        var key = (t.Door, t.State, t.Reason, t.Requested);
        if (!force && key == r.Key && (t.State == "ended" || Time.time - r.LogAt < .25f))
            return;
        r.Key = key;
        r.LogAt = Time.time;
        var json = Diagnostics.Serialize(
            new
            {
                schema = 1,
                session,
                sequence = ++sequence,
                feature = "C01",
                stage = "evaluated",
                decision = "DoorTransit",
                reason = t.Reason,
                realtime = Time.realtimeSinceStartup,
                utc = DateTime.UtcNow.ToString("O"),
                configRevision = config?.Revision ?? 0,
                doorTransitConfig = config?.DoorTransit,
                profileId = s.ProfileId,
                name = s.Name,
                botInstance = s.Id,
                doorTransit = t,
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
