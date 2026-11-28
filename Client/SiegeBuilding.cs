using System;
using System.Collections.Generic;
using Audio.SpatialSystem;
using EFT.Interactive;
using UnityEngine;
using UnityEngine.AI;

namespace Comet.Client;

internal sealed class SiegeExit
{
    internal readonly string Id;
    internal readonly Vector3 Center;
    internal readonly Door? Door;

    internal SiegeExit(string id, Vector3 center, Door? door)
    {
        Id = id;
        Center = center;
        Door = door;
    }

    internal bool IsResolved(LocalDoorLookup lookup, bool complete) =>
        Door is null || (complete && Door != null && lookup.Find(Id) == Door);

    internal bool VisibleFrom(Vector3 eye, double range)
    {
        if ((eye - Center).sqrMagnitude > range * range)
            return false;
        if (
            !Physics.Linecast(
                eye,
                Center,
                out var hit,
                LayersMaskController.HighPolyWithTerrainMask,
                QueryTriggerInteraction.Ignore
            )
        )
            return true;
        return Door != null
            && hit.collider != null
            && hit.collider.GetComponentInParent<Door>() == Door;
    }
}

// Authored audio volumes provide room adjacency, not proof that a bot can walk
// through a portal. Validate the exterior boundary before publishing any exits.
internal sealed class SiegeBuilding
{
    internal readonly HashSet<SpatialAudioRoom> Rooms = new();
    internal readonly List<SiegeExit> Exits = new();

    internal bool ContainsPosition(Vector3 point)
    {
        foreach (var room in Rooms)
            if (room != null && room.IsValid && Contains(room, point))
                return true;
        return false;
    }

    internal static bool Contains(SpatialAudioRoom room, Vector3 point)
    {
        if (!room.Bounds.Contains(point) || room.Colliders == null)
            return false;
        foreach (var box in room.Colliders)
        {
            if (box == null)
                continue;
            var local = box.transform.InverseTransformPoint(point) - box.center;
            var half = box.size * .5f;
            if (
                Math.Abs(local.x) <= half.x
                && Math.Abs(local.y) <= half.y
                && Math.Abs(local.z) <= half.z
            )
                return true;
        }
        return false;
    }

    internal static SiegeBuilding? Read(SpatialAudioRoom start, LocalDoorLookup lookup,
        bool indexComplete, out string reason, out string? unresolvedDoorId)
    {
        reason = "incomplete-room-graph";
        unresolvedDoorId = null;
        var building = new SiegeBuilding();
        var pending = new Queue<SpatialAudioRoom>();
        var portals = new HashSet<BaseSpatialAudioPortal>();
        var doors = new HashSet<Door>();
        pending.Enqueue(start);
        building.Rooms.Add(start);
        while (pending.Count > 0)
        {
            var room = pending.Dequeue();
            if (!room.IsValid || room.roomConnections == null || room.roomConnections.Count == 0)
                return null;
            foreach (var connection in room.roomConnections)
            {
                var next = connection?.connectedRoom;
                if (
                    next == null
                    || !next.IsValid
                    || connection!.connectingPortals == null
                    || connection.connectingPortals.Length == 0
                )
                    return null;
                foreach (var portal in connection.connectingPortals)
                {
                    if (
                        portal == null
                        || !(
                            (portal.FrontRoom == room && portal.BackRoom == next)
                            || (portal.BackRoom == room && portal.FrontRoom == next)
                        )
                        || !LinksBack(next, room, portal)
                    )
                        return null;
                    if (!next.IsOutdoor)
                        continue;
                    if (!portals.Add(portal))
                        continue;
                    var box = portal.portalCollider;
                    if (box == null)
                        return null;
                    var center = box.transform.TransformPoint(box.center);
                    Door? door = null;
                    if (
                        portal.portalType == BaseSpatialAudioPortal.PortalType.Opening
                        && portal is SpatialAudioPortal native
                        && !string.IsNullOrEmpty(native.DoorID)
                    )
                    {
                        door = indexComplete ? lookup.Find(native.DoorID) : null;
                        if (door == null)
                        {
                            reason = indexComplete ? "unresolved-exit-door" : "door-index-pending";
                            unresolvedDoorId = native.DoorID;
                            return null;
                        }
                        if (!doors.Add(door))
                            continue;
                    }
                    else if (!WalkableOpening(box, room, next, center))
                    {
                        reason = "unverified-exterior-portal";
                        return null;
                    }
                    // Multiple unlinked acoustic volumes at one opening cannot
                    // establish that there are two different physical exits.
                    foreach (var prior in building.Exits)
                        if (
                            (prior.Center - center).sqrMagnitude < 1
                            && (door == null || prior.Door == null)
                        )
                        {
                            reason = "ambiguous-exterior-portal";
                            return null;
                        }
                    building.Exits.Add(
                        new SiegeExit(door?.Id ?? "portal-" + portal.ID, center, door)
                    );
                }
                if (!next.IsOutdoor && building.Rooms.Add(next))
                {
                    // Bound work on large, interconnected underground map sections.
                    if (building.Rooms.Count > 64)
                        return null;
                    pending.Enqueue(next);
                }
            }
        }
        reason = building.Exits.Count < 2 ? "fewer-than-two-exits" : "mapped";
        return building.Exits.Count >= 2 ? building : null;
    }

    private static bool LinksBack(
        SpatialAudioRoom source,
        SpatialAudioRoom destination,
        BaseSpatialAudioPortal portal
    )
    {
        if (source.roomConnections == null)
            return false;
        foreach (var link in source.roomConnections)
            if (
                link?.connectedRoom == destination
                && link.connectingPortals != null
                && Array.IndexOf(link.connectingPortals, portal) >= 0
            )
                return true;
        return false;
    }

    private static bool WalkableOpening(
        BoxCollider box,
        SpatialAudioRoom a,
        SpatialAudioRoom b,
        Vector3 center
    )
    {
        var normal = Vector3.ProjectOnPlane(box.transform.forward, Vector3.up).normalized;
        if (normal.sqrMagnitude < .9f)
            return false;
        var bottom = box.transform.TransformPoint(box.center - Vector3.up * box.size.y * .5f);
        float offset = Mathf.Max(
            .75f,
            Mathf.Abs(box.transform.lossyScale.z * box.size.z) * .5f + .25f
        );
        if (
            !NavMesh.SamplePosition(bottom + normal * offset, out var front, .65f, NavMesh.AllAreas)
            || !NavMesh.SamplePosition(
                bottom - normal * offset,
                out var back,
                .65f,
                NavMesh.AllAreas
            )
            || Vector3.Dot(front.position - center, normal) <= 0
            || Vector3.Dot(back.position - center, normal) >= 0
            || NavMesh.Raycast(front.position, back.position, out _, NavMesh.AllAreas)
        )
            return false;
        var f = front.position + Vector3.up;
        var r = back.position + Vector3.up;
        return (Contains(a, f) && Contains(b, r)) || (Contains(a, r) && Contains(b, f));
    }
}
