using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Comet.Core;
using Comfort.Common;
using EFT;
using EFT.Interactive;
using HarmonyLib;
using Newtonsoft.Json;
using SAIN.Components;
using SAIN.SAINComponent.Classes.Mover;
using UnityEngine;

namespace Comet.Client;

internal sealed class DoorRecoveryEpoch
{
    internal long Generation,
        Consumed = -1,
        Sent = -1;
    internal object? Sender;
}

internal sealed class DoorRecoveryRuntime
{
    internal readonly DoorRecovery Policy = new();
    internal Door? Door;
    internal DoorRecoveryEpoch? Epoch;
    internal WorldInteractiveObject.InteractionState? Interaction;
    internal BotPathDataManual? Path;
    internal object? Action;
    internal long Generation;
    internal float PathStarted,
        TargetAngle,
        Next,
        LogAt = -100;
    internal EDoorState Target;
    internal bool Fault,
        Proven;
    internal string Key = "";
}

internal sealed class DoorRecoveryCapture
{
    internal BotState State = null!;
    internal Door Door = null!;
    internal DoorRecoveryEpoch Epoch = null!;
    internal DoorGeometry Geometry = null!;
    internal BotPathDataManual Path = null!;
    internal EDoorState Target;
    internal object? Action;
}

public sealed partial class Plugin
{
    // Weak keys: no scene subscription, retained door list or GameWorld reference across raids.
    private ConditionalWeakTable<WorldInteractiveObject, DoorRecoveryEpoch> recoveryEpochs = new();
    private readonly ScratchList<DoorPoint> recoveryRoute = new();
    private static FieldInfo recoveryInteraction = null!,
        recoveryPrevious = null!,
        recoveryDeadline = null!;
    private static MethodInfo recoveryClear = null!;
    private static bool recoveryFikaContract;

    private static void CaptureDoorLayer(SAIN.SAINComponent.Classes.SAINActivationClass __instance, out bool __state) =>
        __state = __instance.SAINLayersActive;

    private static void RecoveryLayerDeactivated(SAIN.SAINComponent.Classes.SAINActivationClass __instance, bool __state)
    {
        if (__state && !__instance.SAINLayersActive)
            ClearDeactivatedDoor(__instance.Bot);
    }

    private static void RecoveryBotDeactivated(SAIN.SAINComponent.Classes.SAINActivationClass __instance, bool __0)
    {
        if (!__0)
            ClearDeactivatedDoor(__instance.Bot);
    }

    private static void ClearDeactivatedDoor(BotComponent bot)
    {
        var p = Instance;
        if (bot.DoorOpener?.Interacting != true || p?.config?.Enabled != true
            || !p.config.DoorRecovery.Enabled || !p.Authority || !NativeAdapter.IsSupportedAi(bot))
            return;
        recoveryClear.Invoke(bot.DoorOpener, null);
        p.Logger.LogDebug($"C02 restored door collision on SAIN deactivation for {bot.ProfileId}");
    }

    private static void PatchDoorRecovery(Harmony h)
    {
        recoveryInteraction = AccessTools.Field(typeof(WorldInteractiveObject), "_interaction");
        recoveryPrevious = AccessTools.Field(
            typeof(WorldInteractiveObject),
            "_previousInteraction"
        );
        recoveryDeadline = AccessTools.Field(typeof(DoorOpener), "_doorInteractionEndTime");
        recoveryClear = AccessTools.Method(typeof(DoorOpener), "Clear");
        if (
            recoveryInteraction?.FieldType != typeof(WorldInteractiveObject.InteractionState)
            || recoveryPrevious?.FieldType != typeof(WorldInteractiveObject.InteractionState)
            || recoveryDeadline?.FieldType != typeof(float)
            || recoveryClear == null
        )
            throw new MissingMemberException("C02 native door recovery contract");
        var activation = typeof(SAIN.SAINComponent.Classes.SAINActivationClass);
        h.Patch(AccessTools.Method(activation, "SetActive"),
            postfix: new HarmonyMethod(typeof(Plugin), nameof(RecoveryBotDeactivated)));
        h.Patch(AccessTools.Method(activation, "ManualUpdate"),
            prefix: new HarmonyMethod(typeof(Plugin), nameof(CaptureDoorLayer)),
            postfix: new HarmonyMethod(typeof(Plugin), nameof(RecoveryLayerDeactivated)));
        h.Patch(
            AccessTools.Method(typeof(DoorOpener), nameof(DoorOpener.TryInteractWithDoor)),
            prefix: new HarmonyMethod(typeof(Plugin), nameof(RecoveryBefore)),
            postfix: new HarmonyMethod(typeof(Plugin), nameof(RecoveryAfter))
        );
        h.Patch(
            AccessTools.Method(typeof(BotPathDataManual), nameof(BotPathDataManual.TickPath)),
            prefix: new HarmonyMethod(typeof(Plugin), nameof(RecoveryOnPath))
        );
        foreach (string method in new[] { "LockForInteraction", "InitializeSmoothOpenInteraction" })
            h.Patch(
                AccessTools.Method(typeof(WorldInteractiveObject), method),
                prefix: new HarmonyMethod(typeof(Plugin), nameof(RecoveryGeneration))
            );
        // Fika 2.4.5: ExecuteInteraction sends this exact intent via ReliableOrdered after native execution.
        // Only finalize that already-sent intent. Unknown implementations remain diagnostic/replan only.
        recoveryFikaContract = false;
        var fika = AccessTools.TypeByName("Fika.Core.Main.Players.FikaPlayer");
        var execute = fika == null ? null : AccessTools.Method(fika, "ExecuteInteraction");
        if (
            execute?.DeclaringType == fika
            && execute?.GetMethodBody()?.GetILAsByteArray() is { } il
        )
        {
            using var sha = SHA256.Create();
            recoveryFikaContract =
                BitConverter.ToString(sha.ComputeHash(il)).Replace("-", "")
                == "4A113304AF7AEFC536B8CA345036FFAE4EA348450F926C57E60DC45411A828C6";
            if (recoveryFikaContract)
                h.Patch(
                    execute,
                    postfix: new HarmonyMethod(typeof(Plugin), nameof(RecoveryFikaSent))
                    {
                        priority = Priority.Last,
                    }
                );
        }
    }

    private static void RecoveryGeneration(WorldInteractiveObject __instance)
    {
        if (Instance?.recoveryEpochs.TryGetValue(__instance, out var epoch) == true)
        {
            epoch.Generation++;
            epoch.Sent = -1;
            epoch.Sender = null;
        }
    }

    private static void RecoveryFikaSent(object __instance, WorldInteractiveObject __0)
    {
        var p = Instance;
        if (
            p == null
            || !recoveryFikaContract
            || !p.Authority
            || __0.ForceLocalInteraction
            || !p.recoveryEpochs.TryGetValue(__0, out var epoch)
        )
            return;
        epoch.Sent = epoch.Generation;
        epoch.Sender = __instance;
    }

    private static void RecoveryBefore(
        DoorOpener __instance,
        EInteractionType __0,
        DoorDataStruct __2,
        out DoorRecoveryCapture? __state
    )
    {
        __state = null;
        var p = Instance;
        var b = __instance.Bot;
        if (p == null || !p.states.TryGetValue(b, out var s))
            return;
        try
        {
            if (
                s.Recovery.Fault
                || p.config?.DoorRecovery.Enabled != true
                || b.Mover.ActivePath is not BotPathDataManual path
                || !p.TransitScoped(b, s, path, true)
            )
                return;
            var door = __2.Door;
            if (
                door == null
                || door.GetType() != typeof(Door)
                || !door.Operatable
                || !door.enabled
                || !door.gameObject.activeInHierarchy
                || door.NoInteractionsAllowed
                || (__0 != EInteractionType.Open && __0 != EInteractionType.Close)
                || door.DoorState is not (EDoorState.Open or EDoorState.Shut)
                || !TryDoorGeometry(__2, out var g)
            )
                return;
            var corners = path.PathCorners;
            if (
                corners == null
                || corners.Count > 128
                || path.CurrentIndex < 0
                || path.CurrentIndex >= corners.Count
            )
                return;
            var route = new List<DoorPoint> { DP(b.Position) };
            for (int i = path.CurrentIndex; i < corners.Count; i++)
                route.Add(DP(corners[i].Position));
            if (double.IsPositiveInfinity(DoorTransit.RouteDistance(g, route, 3)))
                return;
            __state = new()
            {
                State = s,
                Door = door,
                Epoch = p.recoveryEpochs.GetValue(door, _ => new()),
                Geometry = g,
                Path = path,
                Action = b.CurrentAction,
                Target = __0 == EInteractionType.Open ? EDoorState.Open : EDoorState.Shut,
            };
        }
        catch (Exception ex)
        {
            p.DoorRecoveryError(s, ex);
        }
    }

    private static void RecoveryAfter(
        DoorOpener __instance,
        bool __result,
        DoorRecoveryCapture? __state
    )
    {
        var p = Instance;
        if (p == null || !__result || __state == null)
            return;
        var c = __state;
        var s = c.State;
        try
        {
            if (c.Epoch.Consumed == c.Epoch.Generation)
                return;
            var native = (WorldInteractiveObject.InteractionState)
                recoveryInteraction.GetValue(c.Door)!;
            if (
                native == null
                || native.ResultState != c.Target
                || !DoorPolicy.Finite(native.ResultAngle)
            )
                return;
            p.EndDoorRecovery(s, "recovery-replaced-interaction");
            var r = s.Recovery;
            r.Door = c.Door;
            r.Epoch = c.Epoch;
            r.Generation = c.Epoch.Generation;
            r.Interaction = native;
            r.Path = c.Path;
            r.PathStarted = c.Path.TimeStarted;
            r.Action = c.Action;
            r.Target = c.Target;
            r.TargetAngle = native.ResultAngle;
            r.Next = Time.time;
            r.Proven = true;
            r.Policy.Begin(
                c.Door.Id,
                c.Target.ToString(),
                c.Geometry.Center,
                Time.time,
                !p.fikaPresent ? "solo"
                    : c.Epoch.Sent == r.Generation
                    && ReferenceEquals(c.Epoch.Sender, __instance.Bot.Player)
                        ? "fika-original-reliable-intent"
                    : "fika-unverified"
            );
            p.RecordDoorRecovery(s, true);
        }
        catch (Exception ex)
        {
            p.DoorRecoveryError(s, ex);
        }
    }

    private static void RecoveryOnPath(BotPathDataManual __instance, BotComponent ___Bot)
    {
        var p = Instance;
        if (p == null || !p.states.TryGetValue(___Bot, out var s))
            return;
        var r = s.Recovery;
        try
        {
            if (
                r.Door != null
                || r.Fault
                || p.config?.DoorRecovery.Enabled != true
                || Time.time < r.Next
                || !p.TransitScoped(___Bot, s, __instance, true)
            )
                return;
            if (!p.doorBudget.TryBegin(Time.frameCount, ___Bot))
                return;
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            var route = default(ScratchList<DoorPoint>.Lease);
            try
            {
                r.Next = Time.time + (float)p.config.DoorRecovery.SampleInterval;
                var metrics = RaidSamplingMetrics.Current;
                if (metrics != null)
                    metrics.DoorScans++;
                var links = ___Bot.BotOwner.VoxelesPersonalData.CurVoxel?.DoorLinks;
                var corners = __instance.PathCorners;
                if (
                    links == null
                    || links.Count > 48
                    || corners == null
                    || corners.Count > 128
                    || __instance.CurrentIndex < 0
                    || __instance.CurrentIndex >= corners.Count
                )
                    return;
                DoorWork? best = null;
                double distance = double.PositiveInfinity;
                foreach (var link in links)
                {
                    var door = link?.Door;
                    if (
                        door == null
                        || door.GetType() != typeof(Door)
                        || door.DoorState
                            is not (EDoorState.Interacting or EDoorState.Open or EDoorState.Shut)
                        || door.FallbackState == EDoorState.Locked
                    )
                        continue;
                    var record = (WorldInteractiveObject.InteractionState)
                        recoveryInteraction.GetValue(door)!;
                    if (
                        door.DoorState != EDoorState.Interacting
                        && (
                            record == null
                            || record.IsInProgress
                            || record.ResultState == door.DoorState
                            || record.Break
                        )
                    )
                        continue;
                    var data = new DoorDataStruct(link!);
                    if (!TryDoorGeometry(data, out var g))
                        continue;
                    if (route.Items == null)
                    {
                        route = p.recoveryRoute.Rent();
                        route.Items.Add(DP(___Bot.Position));
                        for (int i = __instance.CurrentIndex; i < corners.Count; i++)
                            route.Items.Add(DP(corners[i].Position));
                        if (metrics != null)
                        {
                            metrics.DoorRoutesCopied++;
                            metrics.DoorPathCornersCopied += route.Items.Count - 1;
                        }
                    }
                    double d = DoorTransit.RouteDistance(g, route.Items, 3);
                    if (d < distance)
                    {
                        distance = d;
                        best = new() { Data = data, Geometry = g };
                    }
                }
                if (best == null)
                    return;
                var native = (WorldInteractiveObject.InteractionState)
                    recoveryInteraction.GetValue(best.Data.Door)!;
                if (
                    native == null
                    || native.ResultState is not (EDoorState.Open or EDoorState.Shut)
                )
                    return;
                r.Door = best.Data.Door;
                r.Epoch = p.recoveryEpochs.GetValue(r.Door, _ => new());
                r.Generation = r.Epoch.Generation;
                r.Interaction = native;
                r.Path = __instance;
                r.PathStarted = __instance.TimeStarted;
                r.Action = ___Bot.CurrentAction;
                r.Target =
                    r.Door.DoorState == EDoorState.Interacting
                        ? native.ResultState
                        : r.Door.DoorState;
                r.TargetAngle =
                    r.Door.DoorState == EDoorState.Interacting
                        ? native.ResultAngle
                        : r.Door.CurrentAngle;
                r.Proven = false;
                // Local stuck doors also recover: use their actual open/close target, never invent an unlock.
                r.Policy.Begin(
                    r.Door.Id,
                    r.Target.ToString(),
                    best.Geometry.Center,
                    Time.time,
                    "unowned-local-interaction"
                );
                p.RecordDoorRecovery(s, true);
            }
            finally
            {
                route.Dispose();
                long elapsed = System.Diagnostics.Stopwatch.GetTimestamp() - started;
                p.doorBudget.Charge(elapsed * 1000d / System.Diagnostics.Stopwatch.Frequency);
            }
        }
        catch (Exception ex)
        {
            p.DoorRecoveryError(s, ex);
        }
    }

    private bool RecoveryBusy(
        BotComponent b,
        BotState s,
        Door door,
        bool ignoreOwnAnimation = false
    )
    {
        if (DoorBusy(b, s, door.Id))
            return true;
        foreach (var pair in states)
        {
            if (
                !ReferenceEquals(pair.Value, s)
                && pair.Value.Reservation.Lease is { } lease
                && lease.Door == door.Id
                && lease.Until > Time.time
            )
                return true;
            if (
                !ReferenceEquals(pair.Key, b)
                && pair.Key.DoorOpener.Interacting
                && pair.Key.DoorOpener.GetActiveDoor().Door == door
            )
                return true;
        }
        // Existing player registry only, bounded and only while a nearby interaction is watched.
        var players = Singleton<GameWorld>.Instance?.AllAlivePlayersList;
        if (players == null || players.Count > 256)
            return true;
        foreach (var player in players)
            if (
                player != null
                && (!ignoreOwnAnimation || player != b.Player)
                && player.MovementContext?.CurrentState != null
                && player.MovementContext.InteractionInfo.WorldInteractiveObject == door
                && player.CurrentStateName
                    is EPlayerState.Approach
                        or EPlayerState.BreachDoor
                        or EPlayerState.DoorInteraction
                        or EPlayerState.DoorInteractionZombieState
            )
                return true;
        return false;
    }

    private void WatchDoorRecovery(BotComponent b, BotState s)
    {
        var r = s.Recovery;
        if (r.Door == null)
            return;
        try
        {
            if (
                r.Fault
                || config?.DoorRecovery.Enabled != true
                || r.Path == null
                || !TransitScoped(b, s, r.Path, true)
                || r.Path.TimeStarted != r.PathStarted
                || !ReferenceEquals(r.Action, b.CurrentAction)
                || !r.Door.enabled
                || !r.Door.gameObject.activeInHierarchy
                || !r.Door.Operatable
                || r.Door.NoInteractionsAllowed
            )
            {
                EndDoorRecovery(s, "recovery-ownership-ended");
                return;
            }
            if (
                r.Epoch!.Generation != r.Generation
                || !ReferenceEquals(r.Interaction, recoveryInteraction.GetValue(r.Door))
            )
            {
                EndDoorRecovery(s, "recovery-newer-interaction");
                return;
            }
            if (Time.time < r.Next || !doorBudget.TryBegin(Time.frameCount, b))
                return;
            var timer = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                SampleDoorRecovery(b, s);
                r.Next = Time.time + (float)config.DoorRecovery.SampleInterval;
            }
            finally
            {
                timer.Stop();
                doorBudget.Charge(timer.Elapsed.TotalMilliseconds);
            }
        }
        catch (Exception ex)
        {
            DoorRecoveryError(s, ex);
        }
    }

    private void SampleDoorRecovery(BotComponent b, BotState s)
    {
        var r = s.Recovery;
        var door = r.Door!;
        var native = r.Interaction!;
        bool busy = RecoveryBusy(
            b,
            s,
            door,
            r.Policy.Trace.Age >= config!.DoorRecovery.BotAnimationGrace
        );
        if (!busy && Math.Abs(door.CurrentAngle - r.TargetAngle) > .5f)
            busy = !SwingClear(door, door.CurrentAngle, r.TargetAngle, b, true);
        bool network = DoorSyncReady && !door.ForceLocalInteraction;
        if (!fikaPresent)
            network = true;
        r.Policy.Trace.Network = fikaPresent
            ? (network ? "comet-door-sync" : "comet-door-sync-unavailable")
            : "solo";
        bool coherent =
            door.DoorState == r.Target
            && native.ResultState == r.Target
            && !native.IsInProgress
            && Math.Abs(door.CurrentAngle - r.TargetAngle) <= .5f;
        string repair = r.Policy.Observe(
            Time.time,
            door.CurrentAngle,
            r.TargetAngle,
            door.DoorState.ToString(),
            native.IsInProgress,
            busy,
            native.TimeIsOver,
            coherent,
            network,
            config!.DoorRecovery
        );
        // Clear only this expired SAIN latch, never an active player animation or a newer request.
        if (
            coherent
            && !busy
            && b.DoorOpener.Interacting
            && b.DoorOpener.GetActiveDoor().Door == door
            && Time.time > (float)recoveryDeadline.GetValue(b.DoorOpener)!
        )
            recoveryClear.Invoke(b.DoorOpener, null);
        if (repair.Length > 0)
        {
            r.Epoch!.Consumed = r.Generation;
            r.Policy.Requested(repair);
            RecordDoorRecovery(s, true);
            // Stop the broken interpolation, then finalize its target and its physical angle.
            native.Break = true;
            door.DoorState = r.Target;
            if (
                r.Epoch.Generation != r.Generation
                || !ReferenceEquals(r.Interaction, recoveryInteraction.GetValue(door))
            )
            {
                EndDoorRecovery(s, "recovery-newer-interaction-during-correction");
                return;
            }
            door.CurrentAngle = r.TargetAngle;
            native.ResultAngle = r.TargetAngle;
            native.ResultState = r.Target;
            native.Break = true;
            ((WorldInteractiveObject.InteractionState)recoveryPrevious.GetValue(door)!).SetEndData(
                native
            );
            Api.DoorMotionNetwork.Publish(door);
            Api.DoorMotionNetwork.Completed(door);
        }
        RecordDoorRecovery(s);
        if (r.Policy.Trace.State == "blocked")
        {
            s.Progress.Fail(
                ProgressAdapter.Destination(r.Path!.Destination),
                Time.time,
                r.Policy.Trace.Reason
            );
            r.Path.Cancel(0);
            string reason = r.Policy.Trace.Reason;
            EndDoorRecovery(s, reason);
            if (s.ActiveLabel.Length > 0)
                Release(s, reason);
        }
        else if (r.Policy.Trace.State is "ended" or "confirmed")
            EndDoorRecovery(s, r.Policy.Trace.Reason);
    }

    private void EndDoorRecovery(BotState s, string reason)
    {
        var r = s.Recovery;
        if (r.Door == null)
            return;
        try
        {
            if (r.Policy.Trace.State is not ("confirmed" or "blocked" or "ended"))
                r.Policy.End(reason);
            RecordDoorRecovery(s, true);
        }
        finally
        {
            if (r.Epoch != null && r.Epoch.Generation == r.Generation)
            {
                r.Epoch.Sender = null;
                r.Epoch.Sent = -1;
            }
            r.Door = null;
            r.Epoch = null;
            r.Interaction = null;
            r.Path = null;
            r.Action = null;
        }
    }

    private void DoorRecoveryError(BotState s, Exception ex)
    {
        if (s.Recovery.Fault)
            return;
        s.Recovery.Fault = true;
        try
        {
            EndDoorRecovery(s, "recovery-adapter-error");
        }
        finally
        {
            Logger.LogWarning("COMET C02 yielded for this bot: " + ex.GetBaseException().Message);
        }
    }

    private void RecordDoorRecovery(BotState s, bool force = false)
    {
        var r = s.Recovery;
        var t = r.Policy.Trace;
        if (t.Door.Length == 0)
            return;
        string key = t.Door + ":" + t.State + ":" + t.Reason;
        // Keep RR observations fresh even when the server selects a faster sample interval.
        if (
            !force
            && r.Key == key
            && Time.time - r.LogAt < (float)(config?.DoorRecovery.SampleInterval ?? .25) - .001f
        )
            return;
        r.Key = key;
        r.LogAt = Time.time;
        var json = Diagnostics.Serialize(
            new
            {
                schema = 1,
                session,
                sequence = ++sequence,
                feature = "C02",
                stage = "evaluated",
                decision = "DoorRecovery",
                reason = t.Reason,
                realtime = Time.realtimeSinceStartup,
                utc = DateTime.UtcNow.ToString("O"),
                configRevision = config?.Revision ?? 0,
                doorRecoveryConfig = config?.DoorRecovery,
                profileId = s.ProfileId,
                name = s.Name,
                botInstance = s.Id,
                doorRecovery = t,
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
