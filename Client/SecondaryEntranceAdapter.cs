using System;
using System.Collections.Generic;
using System.Linq;
using Comet.Core;
using EFT;
using EFT.Interactive;
using SAIN.Components;
using SAIN.Preset.Shared.Enums;
using SAIN.SAINComponent.Classes.Mover;
using UnityEngine;
using UnityEngine.AI;

namespace Comet.Client;

internal sealed class EntranceRuntime
{
    internal int Calculations;
    internal readonly SecondaryEntrance Policy = new();
    internal readonly List<DoorWork> Doors = new();
    internal readonly List<Vector3> Radial = new();
    internal int Index,
        Stage;
    internal DoorWork? Chosen;
    internal bool Virtual,
        Entering,
        Preparing,
        Opening;
    internal Vector3 Primary,
        Center,
        Normal,
        Stack;
    internal string PrimaryId = "";
    internal DoorGeometry? PrimaryGeometry;
    internal float OpenAt,
        Started;
    internal NavMeshPath? PrimaryPath;
    internal DoorReservationLease? Lease;
    internal double NextRenew;

    internal void Drop()
    {
        Doors.Clear();
        Radial.Clear();
        Chosen = null;
        PrimaryGeometry = null;
        PrimaryPath = null;
        Lease = null;
        Entering = Preparing = Opening = Virtual = false;
        Stage = Index = 0;
    }
}

public sealed partial class Plugin
{
    private static bool EntranceOwnOpening(BotComponent b, BotState s) =>
        s.Crossfire.Policy.Active
        && s.Crossfire.Entrance.Opening
        && s.Crossfire.Entrance.Chosen?.Data.Door != null
        && Time.time - s.Crossfire.Entrance.OpenAt < 8;

    private bool BeginEntrance(BotComponent b, BotState s)
    {
        var r = s.Crossfire;
        var a = r.Entrance;
        var c = config!.SecondaryEntrance;
        a.Drop();
        a.Policy.ClearCurrent();
        r.Policy.Trace!.Entrance = null;
        if (!r.Indoor)
            return DeclineSquad(s, "S02", "entrance-outdoors-use-crossfire");
        if (!a.Policy.Begin(r.Contact, Time.time, r.ContactAt, c))
            return DeclineSquad(s, "S02", "entrance-disabled-stale-or-retry");
        r.Policy.Trace.Entrance = a.Policy.Trace;
        a.Started = Time.time;
        a.Calculations = 0;
        // Local door links only. Ordinary open passages are also checked from bounded NavMesh paths below.

        for (int ring = 0; ring < 3; ring++)
        for (int i = 0; i < 12; i++)
        {
            float angle = i * Mathf.PI / 6;
            float radius = 5 + ring * 3;
            a.Radial.Add(r.Known + new Vector3(Mathf.Cos(angle), 0, Mathf.Sin(angle)) * radius);
        }
        return true;
    }

    private static void CollectEntranceDoors(BotComponent b, EntranceRuntime a, Vector3 known)
    {
        var links = b.BotOwner.VoxelesPersonalData.CurVoxel?.DoorLinks;
        if (links == null)
            return;
        int count = 0;
        foreach (var link in links)
        {
            if (++count > 48 || a.Doors.Count >= 24)
                break;
            if (link?.Door == null)
                continue;
            var data = new DoorDataStruct(link);
            if (
                !TryDoorGeometry(data, out var g)
                || Vector3.Distance(VP(g.Center), known) > 12
                || a.Doors.Any(x => x.Geometry.Id == g.Id)
            )
                continue;
            a.Doors.Add(new() { Data = data, Geometry = g });
        }
    }

    private static bool EntrancePath(NavMeshPath p, Vector3 from, Vector3 to, float limit)
    {
        var cs = p.corners;
        return p.status == NavMeshPathStatus.PathComplete
            && cs.Length >= 2
            && cs.Length <= 24
            && Vector3.Distance(cs[0], from) < 1
            && Vector3.Distance(cs[cs.Length - 1], to) < 1
            && Length(cs) <= limit;
    }

    private static bool CrossesEntrance(Vector3[] path, DoorGeometry g)
    {
        var center = VP(g.Center);
        var normal = VP(g.Normal);
        var axis = VP(g.Axis);
        for (int i = 1; i < path.Length; i++)
        {
            float x = Vector3.Dot(path[i - 1] - center, normal),
                y = Vector3.Dot(path[i] - center, normal);
            if (x * y >= 0 || Math.Abs(x - y) < .01)
                continue;
            var hit = path[i - 1] + (path[i] - path[i - 1]) * (x / (x - y));
            if (
                Math.Abs(hit.y - center.y) < .8
                && Math.Abs(Vector3.Dot(hit - center, axis)) < g.HalfWidth + .2
            )
                return true;
        }
        return false;
    }

    // A bend alone is not an entrance: require a narrow physical throat with two static sides,
    // a traversable standing lane and the same floor. Unknown/ambiguous geometry falls back to S01.
    private bool EntranceThroat(BotComponent b, Vector3 from, Vector3 toward, out DoorGeometry g)
    {
        g = new();
        var dir = toward - from;
        dir.y = 0;
        if (dir.magnitude < 1)
            return false;
        dir = dir.normalized;
        var side = new Vector3(-dir.z, 0, dir.x);
        var eye = from + Vector3.up * 1.2f;
        if (
            !Physics.Raycast(
                eye,
                side,
                out var left,
                1.8f,
                LayersMaskController.HighPolyWithTerrainMask,
                QueryTriggerInteraction.Ignore
            )
            || !Physics.Raycast(
                eye,
                side * -1,
                out var right,
                1.8f,
                LayersMaskController.HighPolyWithTerrainMask,
                QueryTriggerInteraction.Ignore
            )
        )
            return false;
        if (
            left.collider == null
            || right.collider == null
            || left.collider.attachedRigidbody != null
            || right.collider.attachedRigidbody != null
            || left.collider.GetComponentInParent<Door>() != null
            || right.collider.GetComponentInParent<Door>() != null
        )
            return false;
        float width = left.distance + right.distance;
        if (width < .8 || width > 3.4)
            return false;
        var center = from + side * ((left.distance - right.distance) * .5f);
        if (RunVolume(b, center - dir * .8f, center + dir * .8f).Length > 0)
            return false;
        var path = new NavMeshPath();
        if (
            !NavMesh.CalculatePath(center - dir * .8f, center + dir * .8f, NavMesh.AllAreas, path)
            || !EntrancePath(path, center - dir * .8f, center + dir * .8f, 2.2f)
        )
            return false;
        g = new()
        {
            Id = $"passage:{Math.Round(center.x)}:{Math.Round(center.y)}:{Math.Round(center.z)}",
            State = "Open",
            Center = DP(center),
            Axis = DP(side),
            Normal = DP(dir),
            HalfWidth = width * .5,
        };
        return true;
    }

    private bool PathThroat(BotComponent b, NavMeshPath path, out DoorGeometry g)
    {
        var cs = path.corners;
        g = new();
        if (cs.Length < 3)
            return false;
        // A bounded probe just before the last bend. It must pass real geometry checks.
        var corner = cs[cs.Length - 2];
        var previous = cs[cs.Length - 3];
        var dir = (corner - previous).normalized;
        return EntranceThroat(b, corner - dir * .5f, cs[cs.Length - 1], out g);
    }

    private void FallbackEntrance(BotComponent b, BotState s, string why)
    {
        var r = s.Crossfire;
        var a = r.Entrance;
        a.Policy.Trace!.EntryReason = why;
        a.Policy.End(Time.time, r.ContactAt);
        RecordCrossfire(s, true);
        r.Policy.Trace!.EntranceFallback = why;
        RecordCrossfireGate(s, "secondary-entrance-fallback-to-S01: " + why, true, "S02");
        a.Drop();
        r.Policy.Trace.Entrance = null;
        r.Policy.Trace.Candidates = 0;
        r.Route = null;
        r.Candidate = r.Sample = 0;
        r.Samples.Clear();
        if (!r.Rejects.Contains(why) && r.Rejects.Count < 8)
            r.Rejects.Add(why);
        r.Policy.Trace.Alternatives = r.Rejects.ToArray();
        BuildCrossfireCandidates(b, r);
    }

    // zzap FindOtherEntrance: infer access from path corners; no physical-door proof.
    private void ProbeEntrance(BotComponent b, BotState s)
    {
        var r = s.Crossfire;
        var a = r.Entrance;
        var c = config!.SecondaryEntrance;
        Vector3 Flat(Vector3 p)
        {
            p.y = 0;
            return p;
        }
        if (a.Stage == 0)
        {
            if (
                !NavMesh.SamplePosition(r.Known, out var en, 1, NavMesh.AllAreas)
                || !NavMesh.SamplePosition(r.Reference, out var mn, 1, NavMesh.AllAreas)
            )
            {
                FallbackEntrance(b, s, "no navmesh");
                return;
            }
            var p = new NavMeshPath();
            if (
                !NavMesh.CalculatePath(mn.position, en.position, NavMesh.AllAreas, p)
                || p.corners.Length < 3
            )
            {
                FallbackEntrance(b, s, "partner direct route");
                return;
            }
            a.Primary = p.corners[p.corners.Length - 2];
            a.PrimaryPath = p;
            a.PrimaryId = "path-corner";
            a.Stage = 1;
            return;
        }
        if (a.Index >= a.Radial.Count || a.Calculations >= 24)
        {
            if (a.Chosen == null)
            {
                FallbackEntrance(b, s, "no alternative entrance path");
                return;
            }
            if (
                !crossfireClaims.Reserve(
                    s.Id,
                    r.Goal.x,
                    r.Goal.y,
                    r.Goal.z,
                    config.Crossfire.Spacing,
                    Time.time,
                    r.Group
                )
            )
            {
                FallbackEntrance(b, s, "watch point claimed");
                return;
            }
            r.Policy.Ready(r.Goal.x, r.Goal.y, r.Goal.z, r.Angle, r.Length);
            r.Policy.Trace!.Reason = "secondary-entrance-path-found";
            a.Policy.Trace!.State = "moving";
            return;
        }
        var raw = a.Radial[a.Index++];
        if (
            !NavMesh.SamplePosition(raw, out var nav, 1, NavMesh.AllAreas)
            || Flat(nav.position - a.Primary).magnitude < 4
            || Flat(nav.position - r.Reference).magnitude < 4
        )
            return;
        if (!NavMesh.SamplePosition(r.Known, out var enemy, 1, NavMesh.AllAreas))
            return;
        a.Calculations++;
        var routeIn = new NavMeshPath();
        if (
            !NavMesh.CalculatePath(nav.position, enemy.position, NavMesh.AllAreas, routeIn)
            || routeIn.status != NavMeshPathStatus.PathComplete
            || routeIn.corners.Length < 3
            || Length(routeIn.corners) > 15
        )
            return;
        var cs = routeIn.corners;
        var door = cs[cs.Length - 2];
        if (Flat(door - a.Primary).magnitude < 3)
            return;
        var back = Flat(cs[cs.Length - 3] - door);
        float n = Math.Min(2.5f, back.magnitude);
        if (
            n < .8
            || !NavMesh.SamplePosition(
                door + back.normalized * n,
                out var hold,
                1,
                NavMesh.AllAreas
            )
        )
            return;
        if (
            Physics.Linecast(
                hold.position + Vector3.up * 1.5f,
                door + Vector3.up * 1.2f,
                LayersMaskController.HighPolyWithTerrainMask
            )
            || CrossfireSpace(b, s, hold.position, true).Length > 0
            || !b.Mover.CanGoToPoint(hold.position, out var path, true)
        )
            return;
        float len = (float)Length(path.corners);
        if (len > c.MaxPath || a.Chosen != null && len >= r.Length)
            return;
        var normal = Flat(door - hold.position).normalized;
        var g = new DoorGeometry
        {
            Id = "path-entrance",
            Center = DP(door),
            Normal = DP(normal),
            Axis = DP(Vector3.Cross(Vector3.up, normal)),
            State = "Open",
            HalfWidth = 1,
        };
        a.Chosen = new() { Geometry = g };
        a.Virtual = true;
        a.Center = door;
        a.Normal = normal;
        a.Stack = hold.position;
        r.Goal = hold.position;
        r.Route = path;
        r.Length = len;
        r.Angle = CrossfireAngle(a.Primary, door, r.Known);
        r.Samples.Clear();
        r.Sample = 0;
        var t = a.Policy.Trace!;
        t.Passage = g.Id;
        t.PassageX = door.x;
        t.PassageY = door.y;
        t.PassageZ = door.z;
    }

    private string EntranceApproachSafety(BotComponent b, BotState s, Vector3 point)
    {
        var r = s.Crossfire;
        string why = CrossfireSpace(b, s, point, false);
        if (why.Length == 0 && CrossfireSight(point, r.Known))
            why = "approach exposes the remembered enemy position";
        if (why.Length == 0)
            why = RunVolume(b, point, point + Vector3.up * .01f);
        return why;
    }

    private bool ReserveEntrance(BotComponent b, BotState s)
    {
        var a = s.Crossfire.Entrance;
        var c = config!;
        if (!c.DoorReservation.Enabled)
            return true;
        return doorReservations.Acquire(
            ReservationSquad(b, s),
            a.Chosen!.Geometry.Id,
            s.ProfileId,
            s.Id,
            "secondary-entrance",
            Time.time,
            Time.time
                + c.Crossfire.MoveTimeout * 2
                + c.SecondaryEntrance.WatchMax
                + c.SecondaryEntrance.Extension
                + 8,
            c.DoorReservation,
            out a.Lease,
            out _
        );
    }

    private void ReleaseEntrance(BotState s)
    {
        var a = s.Crossfire.Entrance;
        if (a.Lease != null)
            doorReservations.Release(a.Lease);
        a.Lease = null;
    }

    private string EntranceSupportStatus(BotComponent b, BotState s)
    {
        if (b.Memory.Health.HealthStatus is ETagStatus.BadlyInjured or ETagStatus.Dying)
            return "injured";
        return b.GoalEnemy.TimeSinceLastKnownUpdated < SecondaryEntrance.RecentContactSeconds ? "ready" : "stale";
    }

    private bool ValidateEntranceActive(BotComponent b, BotState s, bool callback)
    {
        var a = s.Crossfire.Entrance;
        if (!config!.SecondaryEntrance.Enabled)
        {
            EndCrossfire(b, s, "secondary-entrance-disabled");
            return false;
        }
        if (s.Crossfire.Policy.Trace!.State != "planning" && a.Chosen == null)
        {
            EndCrossfire(b, s, "secondary-entrance-missing-target");
            return false;
        }
        a.Policy.Trace!.SupportStatus = EntranceSupportStatus(b, s);
        a.Policy.Trace.Ammo = TacticalAmmoPercent(b.BotOwner);
        return true;
    }

    private void ExecuteEntrance(BotComponent b, BotState s)
    {
        var r = s.Crossfire;
        var a = r.Entrance;
        var t = a.Policy.Trace!;
        var c = config!.SecondaryEntrance;
        if (!ValidateEntranceActive(b, s, true))
            return;
        if (a.Opening)
        {
            if (Time.time - a.OpenAt >= 8)
            {
                EndCrossfire(b, s, "secondary-entrance-opening-timeout");
                return;
            }
            if (
                a.Chosen!.Data.Door.DoorState != EDoorState.Open
                || JumpInteractionState(b)
                || b.BotOwner.DoorOpener.Interacting
            )
                return;
            a.Opening = false;
            t.OpenObserved = true;
            a.Preparing = true;
        }
        if (a.Entering)
        {
            if (Vector3.Dot(b.Position - a.Center, a.Normal) > .25)
                t.Crossed = true;
            if (Vector3.Distance(b.Position, r.Goal) <= .8)
            {
                EndCrossfire(
                    b,
                    s,
                    t.Crossed
                        ? "secondary-entrance-crossed-and-reached"
                        : "secondary-entrance-arrival-without-crossing"
                );
                return;
            }
        }
        if (r.Policy.Trace!.State == "holding" && !a.Entering && !a.Preparing)
        {
            a.Policy.Arrived(Time.time);
            t.State = "watching";
            var enemy = b.GoalEnemy;
            float contactAge = enemy.TimeSinceLastKnownUpdated;
            bool stillInside = a.Policy.WatchElapsed(Time.time)
                && contactAge < 15f
                && enemy.KnownPlaces.LastKnownPosition is Vector3 known
                && Vector3.ProjectOnPlane(known - a.Center, Vector3.up).sqrMagnitude < 12f * 12f;
            string choice = a.Policy.Decide(
                Time.time,
                stillInside,
                contactAge < SecondaryEntrance.RecentContactSeconds,
                t.SupportStatus,
                t.Ammo,
                b.Info.Personality.ToString(),
                stillInside && c.AllowEntry ? jumpRandom.NextDouble() : 0,
                c
            );
            if (choice == "end")
            {
                EndCrossfire(b, s, "secondary-entrance-" + t.EntryReason.Replace(' ', '-'));
                return;
            }
            if (choice != "ready")
                return;
            a.Preparing = true;
            t.State = "preparing-entry";
        }
        if (!a.Preparing || !doorBudget.TryBegin(Time.frameCount, b))
            return;
        doorClock.Restart();
        try
        {
            if (!a.Virtual && a.Chosen!.Data.Door.DoorState == EDoorState.Shut)
            {
                if (!RequestJumpDoorOpen(b, new DoorJumpRuntime { Door = a.Chosen }))
                {
                    EndCrossfire(b, s, "secondary-entrance-native-open-refused");
                    return;
                }
                a.Opening = true;
                a.Preparing = false;
                a.OpenAt = Time.time;
                t.OpenRequested = true;
                t.State = "opening";
                return;
            }
            t.OpenObserved = true;
            var raw = a.Center + a.Normal * 2.5f;
            if (
                !NavMesh.SamplePosition(raw, out var nav, 1, NavMesh.AllAreas)
                || !b.Mover.CanGoToPoint(nav.position, out var route, true)
            )
            {
                EndCrossfire(b, s, "secondary-entrance-no-path");
                return;
            }
            var cs = route.corners;
            r.Goal = nav.position;
            r.Route = route;
            r.Length = (float)Length(cs);
            r.Samples.Clear();
            r.Samples.AddRange(cs);
            if (!IssueCrossfireMove(b, r, sprint: true))
            {
                EndCrossfire(b, s, "secondary-entrance-native-entry-refused");
                return;
            }
            if (!OwnCrossfirePath(b, r))
            {
                r.Path = null;
                EndCrossfire(b, s, "secondary-entrance-native-entry-unobserved");
                return;
            }
            r.Policy.Ready(r.Goal.x, r.Goal.y, r.Goal.z, r.Angle, r.Length);
            r.Policy.Moving(Time.time, Vector3.Distance(b.Position, r.Goal));
            a.Preparing = false;
            a.Entering = true;
            t.State = "entering";
            r.LastSafety = Time.time;
            r.Policy.Trace!.Reason = "secondary-entrance-entry-approved";
            r.Policy.Trace.Execution = "native secondary entry route observed";
        }
        finally
        {
            doorClock.Stop();
            doorBudget.Charge(doorClock.Elapsed.TotalMilliseconds);
        }
    }
}
