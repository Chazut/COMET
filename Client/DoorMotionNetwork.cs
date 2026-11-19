using System;
using Comet.Core;
using Comfort.Common;
using EFT;
using EFT.GlobalEvents;
using EFT.Interactive;
using HarmonyLib;

namespace Comet.Api;

// Optional companion transport, shipped with COMET. Solo COMET has no Fika assembly dependency.
public static class DoorMotionNetwork
{
    public static Func<bool>? Ready;
    public static event Action<string, int, float>? Changed;
    public static event Action? RaidEnded;

    public static void Publish(Door door)
    {
        var state = door.DoorState;
        if (state == EDoorState.Interacting)
            state = (
                (WorldInteractiveObject.InteractionState)
                    AccessTools.Field(typeof(WorldInteractiveObject), "_interaction").GetValue(door)
            ).ResultState;
        if (state is EDoorState.Open or EDoorState.Shut)
            Changed?.Invoke(door.Id, (int)state, door.CurrentAngle);
    }

    public static void Clear()
    {
        var handlers = RaidEnded;
        if (handlers == null)
            return;
        System.Collections.Generic.List<Exception>? failures = null;
        foreach (Action callback in handlers.GetInvocationList())
            try
            {
                callback();
            }
            catch (Exception ex)
            {
                (failures ??= new()).Add(ex);
            }
        if (failures != null)
            throw new AggregateException("COMET transport cleanup failed", failures);
    }

    // A late join can receive snapshots before the scene's doors exist. Retry only that case,
    // never a rejected correction (e.g. a newer local human interaction).
    public static bool DoorAvailable(string id) =>
        Singleton<GameWorld>.Instance?.FindDoor(id) != null;

    public static void Completed(Door door) =>
        GlobalEventsController
            .CreateEvent<InteractiveObjectInteractionResultEvent>()
            .Invoke(door, door.DoorState);

    public static bool Apply(string id, int state, float angle)
    {
        if (
            string.IsNullOrEmpty(id)
            || id.Length > 256
            || !DoorPolicy.Finite(angle)
            || Math.Abs(angle) > 720
            || state != (int)EDoorState.Open && state != (int)EDoorState.Shut
        )
            return false;
        var door = Singleton<GameWorld>.Instance?.FindDoor(id) as Door;
        if (door == null || door.GetType() != typeof(Door) || door.DoorState == EDoorState.Locked)
            return false;
        var players = Singleton<GameWorld>.Instance?.AllAlivePlayersList;
        if (players != null)
            foreach (var p in players)
                if (
                    p != null
                    && p.IsYourPlayer
                    && p.MovementContext.InteractionInfo.WorldInteractiveObject == door
                    && p.CurrentStateName
                        is EPlayerState.Approach
                            or EPlayerState.BreachDoor
                            or EPlayerState.DoorInteraction
                )
                    return false;
        bool changed = door.DoorState != (EDoorState)state;
        if (!Align(door, (EDoorState)state, angle))
            return false;
        if (changed)
            Completed(door);
        return true;
    }

    public static bool Align(Door door, EDoorState target, float angle)
    {
        var field = AccessTools.Field(typeof(WorldInteractiveObject), "_interaction");
        var native = (WorldInteractiveObject.InteractionState)field.GetValue(door);
        // Break a stalled interpolation before changing the physical leaf, or it overwrites the correction next frame.
        native.Break = true;
        native.ResultState = target;
        native.ResultAngle = angle;
        door.DoorState = target;
        if (
            !native.Break
            || native.ResultState != target
            || !ReferenceEquals(native, field.GetValue(door))
        )
            return false;
        door.CurrentAngle = angle;
        var previous = (WorldInteractiveObject.InteractionState)
            AccessTools
                .Field(typeof(WorldInteractiveObject), "_previousInteraction")
                .GetValue(door);
        previous.SetEndData(native);
        return true;
    }
}
