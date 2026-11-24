using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Comfort.Common;
using Comet.Core;
using EFT;
using EFT.Interactive;
using SAIN.SAINComponent.Classes.Mover;
using UnityEngine;
using UnityEngine.AI;

namespace Comet.Client;

// OpenLeafCarver and BotPathData.TryUpdatePath, zzap remix 7.6.
// One instance belongs to one authoritative raid. No bot is needed to discover scene-open doors.
internal sealed class OpenLeafCarver
{
    private const float CheckInterval = 1f, LateDiscovery = 60f, RetryInterval = 5f, CutLifetime = 60f;
    private const float ClearanceSquared = 2f * 2f, PathRangeSquared = 2.5f * 2.5f;
    private const int CutsPerCheck = 16;
    private readonly Dictionary<Door, List<NavMeshDoorLink>> linksByDoor = new();
    private readonly HashSet<Door> enabledAtStart = new();
    private readonly List<NavMeshDoorLink> waiting = new(), gone = new();
    private readonly Dictionary<NavMeshDoorLink, (NavMeshObstacle open, NavMeshObstacle breached)> carved = new();
    private readonly List<(float time, Vector3 middle)> cuts = new();
    private readonly Dictionary<NavMeshObstacle, (bool enabled, bool carving, int links)> owned = new();
    private sealed class PathStamp { public float Time; }
    private readonly ConditionalWeakTable<BotPathDataManual, PathStamp> pathTimes = new();
    private readonly Action<string> log;
    private readonly float started;
    private float nextCheck;
    private BotDoorsController? controller;
    private List<NavMeshDoorLink>? source;
    private readonly HashSet<NavMeshDoorLink> registered = new();
    private readonly List<int> unresolved = new();
    private int sourceCount;
    private float nextDiscovery;
    private bool waitingReported;
    private int activated, closed, repaths, deferredNearby, deferredReversed;
    internal bool HasCuts => cuts.Count != 0;

    internal OpenLeafCarver(float now, Action<string> log)
    {
        started = nextCheck = now;
        this.log = log;
    }

    internal void Tick(float now, GameWorld world)
    {
        if (now < nextCheck)
            return;
        nextCheck = now + CheckInterval;
        bool firstDiscovery = registered.Count == 0;
        Discover(now);
        if (firstDiscovery)
            return;

        foreach (var link in carved.Keys)
            if (link == null || link.Door == null || !Live(link.Door))
                gone.Add(link!); // Destroyed Unity objects still have a dictionary key.
        foreach (var link in gone)
        {
            Close(link);
            if (link != null && link.Door != null && link.Door.DoorState == EDoorState.Open)
                waiting.Add(link);
        }
        gone.Clear();
        // Cuts are chronological. Unity applies a new cut on the following frame.
        int expired = 0;
        while (expired < cuts.Count && now - cuts[expired].time > CutLifetime)
            expired++;
        if (expired != 0)
            cuts.RemoveRange(0, expired);

        int applied = 0;
        for (int i = waiting.Count - 1; i >= 0 && applied < CutsPerCheck; i--)
        {
            var link = waiting[i];
            if (link == null || link.Door == null || link.Door.DoorState != EDoorState.Open)
            {
                waiting.RemoveAt(i);
                continue;
            }
            var door = link.Door;
            if (!Live(door))
                continue;
            if (door is not SlidingDoor && DoorMotion.Reversed(
                door.GetAngle(EDoorState.Shut), door.GetAngle(EDoorState.Open), door.CurrentAngle))
            {
                deferredReversed++;
                continue;
            }
            if (SomeoneNear(link.MidOpen, world))
            {
                deferredNearby++;
                continue;
            }
            waiting.RemoveAt(i);
            carved.Add(link, (link.Carver_Opened, link.Carver_Breached));
            Acquire(link.Carver_Opened);
            Acquire(link.Carver_Breached);
            cuts.Add((now, link.MidOpen));
            activated++;
            applied++;
        }
    }

    private void Discover(float now)
    {
        if (now < nextDiscovery)
            return;
        nextDiscovery = now + (now - started >= LateDiscovery || registered.Count != 0 ? RetryInterval : CheckInterval);
        if (Singleton<IBotGame>.Instance != null)
        {
            if (controller == null)
                controller = UnityEngine.Object.FindObjectOfType<BotDoorsController>();
            if (controller != null && controller._navMeshDoorLinks is { } links)
            {
                if (!ReferenceEquals(source, links) || links.Count < sourceCount)
                {
                    source = links;
                    sourceCount = 0;
                    unresolved.Clear();
                }
                int before = registered.Count;
                for (int i = unresolved.Count - 1; i >= 0; i--)
                    if (Register(links[unresolved[i]]))
                        unresolved.RemoveAt(i);
                for (; sourceCount < links.Count; sourceCount++)
                    if (!Register(links[sourceCount]))
                        unresolved.Add(sourceCount);
                if (registered.Count != before)
                    log($"COMET open door leaves: {registered.Count} links indexed, {linksByDoor.Count} doors, {waiting.Count} open links queued ({now - started:F0}s).");
            }
        }
        if (registered.Count == 0 && !waitingReported && now - started >= LateDiscovery)
        {
            waitingReported = true;
            log("COMET open door leaves: still waiting for initialized door links; retrying.");
        }
    }

    private bool Register(NavMeshDoorLink link)
    {
        if (link == null || link.Door == null || link.Carver_Opened == null)
            return false;
        if (registered.Add(link))
        {
            var door = link.Door;
            if (!linksByDoor.TryGetValue(door, out var group))
            {
                linksByDoor.Add(door, group = new());
                door.OnDoorStateChanged += DoorChanged;
                if (door.enabled)
                    enabledAtStart.Add(door);
            }
            group.Add(link);
            if (door.DoorState == EDoorState.Open)
                waiting.Add(link);
        }
        return true;
    }

    private bool Live(Door door) => door.gameObject.activeInHierarchy && (door.enabled || !enabledAtStart.Contains(door));

    private static bool SomeoneNear(Vector3 point, GameWorld world)
    {
        var players = world.AllAlivePlayersList;
        if (players != null)
            for (int i = 0; i < players.Count; i++)
            {
                var player = players[i];
                if (player != null && (player.Position - point).sqrMagnitude < ClearanceSquared)
                    return true;
            }
        return false;
    }

    private void DoorChanged(WorldInteractiveObject obj, EDoorState previous, EDoorState next)
    {
        if (obj is not Door door || !linksByDoor.TryGetValue(door, out var links))
            return;
        foreach (var link in links)
            if (next == EDoorState.Open)
            {
                if (link != null && !carved.ContainsKey(link) && !waiting.Contains(link))
                    waiting.Add(link);
            }
            else
            {
                waiting.Remove(link);
                Close(link);
            }
    }

    private void Close(NavMeshDoorLink link)
    {
        if (!carved.Remove(link, out var obstacles))
            return;
        Release(obstacles.open);
        Release(obstacles.breached);
        closed++;
    }

    private void Acquire(NavMeshObstacle obstacle)
    {
        if (obstacle == null)
            return;
        if (owned.TryGetValue(obstacle, out var state))
        {
            owned[obstacle] = (state.enabled, state.carving, state.links + 1);
            return;
        }
        owned.Add(obstacle, (obstacle.enabled, obstacle.carving, 1));
        obstacle.carving = true;
        obstacle.enabled = true;
    }

    private void Release(NavMeshObstacle obstacle)
    {
        if (ReferenceEquals(obstacle, null) || !owned.TryGetValue(obstacle, out var state))
            return;
        if (state.links > 1)
        {
            owned[obstacle] = (state.enabled, state.carving, state.links - 1);
            return;
        }
        owned.Remove(obstacle);
        if (obstacle != null)
        {
            obstacle.carving = false;
            obstacle.enabled = false;
        }
    }

    internal void Stamp(BotPathDataManual path, float now) => pathTimes.GetOrCreateValue(path).Time = now;

    internal bool CrossesNewCut(BotPathDataManual path, Vector3 position, float now)
    {
        if (cuts.Count == 0)
            return false;
        // A path created while configuration was loading predates the first cut.
        float madeAt = pathTimes.TryGetValue(path, out var stamp) ? stamp.Time : float.NegativeInfinity;
        for (int c = cuts.Count - 1; c >= 0; c--)
        {
            var cut = cuts[c];
            if (cut.time < madeAt)
                break;
            if (cut.time >= now)
                continue;
            var from = position;
            for (int i = Math.Max(0, path.CurrentIndex); i < path.PathCorners.Count; i++)
            {
                var to = path.PathCorners[i].Position;
                var segment = to - from;
                float lengthSquared = segment.sqrMagnitude;
                float t = lengthSquared < .000001f ? 0 : Mathf.Clamp01(Vector3.Dot(cut.middle - from, segment) / lengthSquared);
                if ((from + segment * t - cut.middle).sqrMagnitude < PathRangeSquared)
                {
                    repaths++;
                    return true;
                }
                from = to;
            }
        }
        return false;
    }

    internal void Dispose()
    {
        foreach (var door in linksByDoor.Keys)
            if (door != null)
                door.OnDoorStateChanged -= DoorChanged;
        foreach (var pair in owned)
            if (pair.Key != null)
            {
                pair.Key.carving = pair.Value.carving;
                pair.Key.enabled = pair.Value.enabled;
            }
        owned.Clear();
        linksByDoor.Clear();
        waiting.Clear();
        enabledAtStart.Clear();
        gone.Clear();
        carved.Clear();
        cuts.Clear();
        log($"COMET open door leaves: {activated} cuts, {closed} removed, {repaths} stale paths, {deferredNearby} actor deferrals, {deferredReversed} reversed-leaf deferrals.");
    }
}
