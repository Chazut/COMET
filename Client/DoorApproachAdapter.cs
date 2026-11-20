using System;
using System.Collections.Generic;
using System.Linq;
using Comet.Core;
using EFT;
using EFT.Interactive;
using HarmonyLib;
using Newtonsoft.Json;
using SAIN.Classes.Bot.Search;
using SAIN.Components;
using SAIN.Preset.Shared.Enums;
using SAIN.Models.Enums;
using SAIN.SAINComponent.Classes.EnemyClasses;
using SAIN.SAINComponent.Classes.Mover;
using UnityEngine;
using UnityEngine.AI;

namespace Comet.Client;

internal sealed class DoorWork
{
    internal DoorDataStruct Data;
    internal DoorGeometry Geometry = new();
    internal int Side,
        Variant;
}

internal sealed class DoorRuntime
{
    internal readonly DoorApproach Policy = new();
    internal readonly List<DoorWork> Work = new();
    internal DoorWork? Chosen;
    internal string SessionPlan = "", SessionDoor = "", SessionEnemy = "", Feint = "";
    internal bool FromSearch, Entered, NativeFallback, NativeSelected;
    internal DoorWork? Nearby;
    internal float ProbeUntil, SessionAt;
    internal double SelectionChance, SelectionRoll;
    internal string ProbeTarget = "";
    internal readonly DoorSessionCooldowns Cooldowns = new();
    internal IBotPathData? OwnedPath;
    internal NavMeshPath? IssuedRoute;
    internal float NextMoveOrder;
    internal BotComponent? Owner;
    internal double EvidenceAt;
    internal float NextCheck,
        Started,
        LogAt = -1000,
        NextPlan;
    internal bool Fault;
    internal string LogKey = "",
        Target = "";
    internal Vector3 Contact;
    internal int TargetInstance;

    internal void DropReferences()
    {
        Work.Clear();
        Chosen = null;
        OwnedPath = null;
        IssuedRoute = null;
        Owner = null;
    }
}

public sealed partial class Plugin
{
    private readonly DoorWorkBudget doorBudget = new();
    private readonly System.Diagnostics.Stopwatch doorClock = new();
    private readonly Collider[] doorOccupants = new Collider[24];

    private static DoorPoint DP(Vector3 p) => new(p.x, p.y, p.z);

    private static Vector3 VP(DoorPoint p) => new((float)p.X, (float)p.Y, (float)p.Z);

    private static void PatchDoorApproach(Harmony h)
    {
        var m = AccessTools.Method(
            typeof(SearchClass),
            nameof(SearchClass.Search),
            new[] { typeof(bool), typeof(Enemy) }
        ) ?? throw new MissingMethodException("D01 Search(bool, Enemy)");
        h.Patch(
            m,
            prefix: new HarmonyMethod(typeof(Plugin), nameof(DoorSearchStep))
            {
                priority = Priority.First,
            }
        );
    }

    private static bool TacticalDoorSearch(BotComponent b, BotState s) =>
        b.SAINLayersActive
        && b.Decision.CurrentCombatDecision == ECombatDecision.Search
        && b.CurrentAction?.GetType().Name == "SearchAction"
        && (s.ActiveLabel.Length == 0 || s.ActiveLabel == "COMET:Search"
            || s.Door.SessionPlan.Length > 0 && s.ActiveLabel == "COMET:Push")
        && !s.Handoff.Blocked(Time.time, HandoffContact(s.Situation));

    private string DoorGate(BotComponent b, Enemy? e, BotState s)
    {
        if (!ContactSafety.Allows(b, e))
            return "door-no-hostile-contact";
        if (MedicalPostureGuard(b))
            return "medical-posture-protected";
        var c = config!;
        bool scope =
            c.Enabled
            && !s.Door.Fault
            && NativeAdapter.IsSupportedAi(b)
            && b.BotActive
            && !b.GameEnding
            && Authority
            && !Ghost(b.ProfileId);
        bool search = TacticalDoorSearch(b, s) && SameTacticalContact(e, b.GoalEnemy);
        bool unsafeNow =
            b.BotOwner.Memory.IsUnderFire
            || b.Grenade.GrenadeDangerPoint != null
            || b.Decision.CurrentSelfDecision != ESelfActionType.None;
        if (s.Door.SessionPlan.Length > 0 && e != null && e.EnemyProfileId == s.Door.SessionEnemy)
        {
            if (!scope || !c.DoorApproach.Enabled) return "door-out-of-scope";
            if (!search) return "door-search-not-owned";
            if (unsafeNow) return "door-native-priority";
            return e.EnemyKnown && e.KnownPlaces.LastKnownPosition.HasValue ? "" : "door-contact-expired";
        }
        double unseen =
            e == null ? 0
            : e.Seen ? e.TimeSinceSeen
            : Time.time;
        return DoorPolicy.Gate(
            scope,
            search,
            unsafeNow,
            e?.EnemyKnown == true && e.KnownPlaces.LastKnownPosition.HasValue,
            e?.IsVisible == true,
            e?.TimeSinceLastKnownUpdated ?? -1,
            unseen,
            c.DoorApproach,
            c.DoorSearchClear.Enabled
                ? Math.Max(c.DoorApproach.ContactLifetime, c.DoorSearchClear.ContactLifetime)
                : null
        );
    }

    private static bool DoorSearchStep(SearchClass __instance, Enemy enemy)
    {
        var p = Instance;
        var b = __instance.Bot;
        if (p?.config == null || !p.states.TryGetValue(b, out var s))
            return true;
        if (!ContactSafety.Allows(b, enemy))
        {
            p.Release(s, "contact-ended-own-squad");
            return true;
        }
        if (s.Entry.Policy.Active)
        {
            p.RefreshDoorEntry(b, s);
            return !s.Entry.Policy.Active;
        }
        try
        {
            if (!p.GuardDoorReservation(b, s))
                return true;
            p.RefreshDoorInterruption(b, s);
            if (!p.GuardDoorReservation(b, s))
                return true;
            if (!p.HasDoorAction(s) && !p.EnsureDoorSession(b, enemy, s, true))
                return true;
            s.Door.Entered = true;
            bool fallback =
                s.Interruption.Policy.Pending ? p.StepDoorResume(b, s)
                : s.RoomClear.Policy.Active ? p.StepDoorClear(b, s)
                : s.Fake.Policy.Active ? p.StepDoorFake(b, s)
                : s.StepPeek.Policy.Active ? p.StepDoorPeek(b, enemy, s)
                : s.RunBy.Policy.Active ? p.StepDoorRun(b, enemy, s)
                : s.Jump.Policy.Active ? p.StepDoorJump(b, enemy, s)
                : p.StepDoor(b, enemy, s);
            if (s.Door.Entered && !p.HasDoorAction(s))
                p.CloseDoorSession(s);
            return p.CompleteDoorReservationStep(b, s, fallback);
        }
        catch (Exception ex)
        {
            if (s.Interruption.Policy.Pending || s.Interruption.Policy.Resumed)
                p.AbandonDoorResume(s, "door-resume-adapter-error");
            if (s.RoomClear.Policy.Active)
            {
                s.RoomClear.Fault = true;
                p.EndDoorClear(s, "clear-adapter-error");
            }
            if (s.Fake.Policy.Active)
                p.EndDoorFake(s, "fake-adapter-error");
            if (s.StepPeek.Policy.Active)
            {
                s.StepPeek.Fault = true;
                p.EndDoorStep(s, "step-adapter-error");
            }
            if (s.RunBy.Policy.Active)
            {
                s.RunBy.Fault = true;
                p.EndDoorRun(s, "run-by-adapter-error");
            }
            if (s.Jump.Policy.Active)
            {
                s.Jump.Fault = true;
                p.EndDoorJump(s, "peek-adapter-error");
            }
            s.Door.Fault = true;
            p.EndDoor(s, "door-adapter-error");
            p.ReleaseDoorReservation(s, "reservation-adapter-error");
            p.Logger.LogWarning("COMET doorway adapter yielded: " + ex.Message);
            return true;
        }
    }

    // false means this one Search invocation was replaced by an already accepted, bounded native move.
    private bool StepDoor(BotComponent b, Enemy e, BotState s)
    {
        var r = s.Door;
        if (s.Interruption.Policy.Pending)
            return true;
        string gate = ContactSafety.Allows(b, e) ? DoorGate(b, e, s) : "door-own-squad-contact";
        if (gate.Length > 0)
        {
            if (r.Policy.Active || r.Policy.Planning)
                EndDoor(s, gate);
            return true;
        }
        var known = e.KnownPlaces.LastKnownPosition!.Value;
        var c = config!.DoorApproach;
        if (r.Policy.Active)
        {
            if (e.EnemyProfileId != r.Target)
            {
                EndDoor(s, "door-contact-instance-changed");
                return true;
            }
            var t = r.Policy.Trace!;
            var stack = new Vector3((float)t.X, (float)t.Y, (float)t.Z);
            t.NativeRoute = DoorApproach.AtStack(DP(b.Position), DP(stack)) ? "at-stack" : DoorRouteState(b, r.OwnedPath, r.IssuedRoute);
            if (
                !r.Policy.Observe(
                    e.EnemyProfileId,
                    DP(known),
                    DP(b.Position),
                    e.TimeSinceLastKnownUpdated,
                    Time.time,
                    c.ApproachLimit
                )
            )
            {
                var work = r.Chosen;
                bool reached = t.Arrived;
                FinishDoor(s);
                if (reached && work != null)
                {
                    if (r.SessionPlan == "clear")
                        TryStartDoorClear(b, e, s, work);
                    else if (r.SessionPlan == "peek")
                        TryStartDoorJump(b, e, s, work);
                }
                return !s.RoomClear.Policy.Active
                    && !s.Fake.Policy.Active
                    && !s.Jump.Policy.Active
                    && !s.RunBy.Policy.Active && !s.StepPeek.Policy.Active;
            }
            // The source refreshes the move order every 0.75 s while approaching.
            if (Time.time >= r.NextMoveOrder)
            {
                r.NextMoveOrder = Time.time + .75f;
                if (!ReferenceEquals(r.OwnedPath?.NavMeshPath, r.IssuedRoute))
                    r.OwnedPath = null;
                if (!OrderDoorPoint(b, stack, .42f, false, ref r.OwnedPath))
                {
                    EndDoor(s, "door-mover-refused");
                    return true;
                }
                r.IssuedRoute = r.OwnedPath?.NavMeshPath;
            }
            b.Mover.SetTargetMoveSpeed(.75f);
            if (Time.time >= r.NextCheck)
            {
                if (!doorBudget.TryBegin(Time.frameCount, b))
                {
                    // Pending work cannot invalidate the route already accepted by SAIN.
                }
                else
                {
                    doorClock.Restart();
                    string invalid;
                    try
                    {
                        invalid = ValidateDoor(
                            b,
                            r.Chosen!,
                            new DoorPoint(t.X, t.Y, t.Z),
                            false,
                            out _,
                            out _,
                            stationary: true
                        );
                    }
                    finally
                    {
                        doorClock.Stop();
                        doorBudget.Charge(doorClock.Elapsed.TotalMilliseconds);
                    }
                    if (invalid.Length > 0)
                    {
                        EndDoor(s, invalid);
                        return true;
                    }
                    r.NextCheck = Time.time + .25f;
                }
            }
            RecordDoor(s);
            return false;
        }
        if (!r.Policy.Planning)
        {
            if (Time.time < r.NextPlan || r.SessionPlan.Length == 0 && !r.Policy.CanPlan(e.EnemyProfileId, DP(known), Time.time))
                return true;
            r.NextPlan = Time.time + 2;
            StartDoorPlan(b, e, s, known);
            if (!r.Policy.Planning)
                return true;
        }
        if (r.Target != e.EnemyProfileId || r.Policy.PlanningExpired(Time.time))
        {
            EndDoor(s, "door-plan-became-stale");
            return true;
        }
        if (!doorBudget.TryBegin(Time.frameCount, b))
        {
            r.Policy.Preparation.Defer(Time.time, Time.deltaTime);
            return true;
        }
        doorClock.Restart();
        try
        {
            if (r.Work.Count > 0 && r.Chosen == null)
            {
                var work = r.Work[0];
                r.Work.RemoveAt(0);
                if (DoorBusy(b, s, work.Geometry.Id))
                {
                    r.Policy.Trace!.Candidates.Add(
                        new() { Door = work.Geometry.Id, Reason = "reservation-door-busy" }
                    );
                    return true;
                }
                var raw = DoorPolicy.Stack(work.Geometry, DP(b.Position), work.Side, work.Variant);
                string why = ValidateDoor(b, work, raw, true, out var path, out var point, approachStack: true);
                var candidate = new DoorCandidate
                {
                    Door = work.Geometry.Id,
                    Side = work.Side < 0 ? "left" : "right",
                    X = point.x,
                    Y = point.y,
                    Z = point.z,
                    Reason = why,
                    Eligible = why.Length == 0,
                };
                if (candidate.Eligible)
                {
                    candidate.PathLength = path == null ? SupportHorizontal(b.Position, point) : Length(path.corners);
                    candidate.Score = Math.Round(
                        candidate.PathLength + Math.Abs(point.y - b.Position.y) * 2,
                        3
                    );
                    candidate.Reason = "door-reachable-protected-frame";
                }
                r.Policy.Trace!.Candidates.Add(candidate);
                var best = DoorPolicy.Best(r.Policy.Trace.Candidates);
                if (ReferenceEquals(best, candidate))
                {
                    r.Chosen = work;
                    r.Work.Clear();
                }
                // One candidate work unit. Other bots/frames share the same bounded allowance.
                return true;
            }
            var selected = DoorPolicy.Best(r.Policy.Trace!.Candidates);
            if (selected == null || r.Chosen == null)
            {
                EndDoor(s, "door-no-safe-frame-point");
                return true;
            }
            string refusal = ValidateDoor(
                b,
                r.Chosen,
                selected.Point,
                true,
                out var finalPath,
                out var finalPoint,
                approachStack: true
            );
            if (refusal.Length > 0)
            {
                EndDoor(s, refusal);
                return true;
            }
            selected.X = finalPoint.x;
            selected.Y = finalPoint.y;
            selected.Z = finalPoint.z;
            selected.PathLength = finalPath == null ? SupportHorizontal(b.Position, finalPoint) : Length(finalPath.corners);
            if (!AcquireDoorReservation(b, s, r.Chosen, "approach", ReservationDeadline(b, s)))
            {
                EndDoor(s, "reservation-unavailable");
                return true;
            }
            r.Policy.Request(selected, DP(b.Position), Time.time);
            RecordDoor(s, true);
            // Reset search's corner/wait bookkeeping before temporarily supplying our verified route.
            b.Search.Reset();
            bool accepted = RequestDoorApproach(b, r, finalPoint, finalPath);
            r.Policy.Accept(accepted, Time.time);
            if (!accepted)
            {
                FinishDoor(s);
                return true;
            }
            r.Owner = b;
            r.NextCheck = Time.time + .25f;
            r.Started = Time.time;
            RecordDoor(s, true);
            return false;
        }
        finally
        {
            doorClock.Stop();
            doorBudget.Charge(doorClock.Elapsed.TotalMilliseconds);
        }
    }

    private void StartDoorPlan(BotComponent b, Enemy e, BotState s, Vector3 known)
    {
        var r = s.Door;
        var route = RememberedDoorRoute(b, known);
        r.DropReferences();
        r.Policy.Begin(
            e.EnemyProfileId,
            s.Situation?.ContactSource ?? "sain-last-known",
            DP(known),
            e.TimeSinceLastKnownUpdated,
            Time.time
        );
        r.Target = e.EnemyProfileId;
        r.TargetInstance = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(e);
        r.Contact = known;
        r.Started = Time.time;
        r.EvidenceAt = Time.time - e.TimeSinceLastKnownUpdated;
        var candidates = new List<DoorWork>();
        int visited = 0;
        foreach (var link in NearbyDoors(b))
        {
            visited++;
            r.Policy.Trace!.DoorsScanned++;
            if (link == null || link.Door == null)
                continue;
            var work = new DoorWork { Data = new DoorDataStruct(link) };
            if (!TryDoorGeometry(work.Data, out var g))
            {
                CountDoorGeometryRefusal(r.Policy.Trace, "geometry-invalid-frame");
                InvalidDoorGeometry(b, work.Data, DP(b.Position));
                continue;
            }
            work.Geometry = g;
            if (r.SessionDoor.Length > 0 && r.SessionDoor != g.Id)
                continue;
            string why = DoorPolicy.Geometry(
                g,
                DP(b.Position),
                DP(known),
                route,
                config!.DoorApproach
            );
            if (why.Length == 0)
            {
                candidates.Add(work);
                r.Policy.Trace.DoorsEligible++;
            }
            else
            {
                CountDoorGeometryRefusal(r.Policy.Trace, why);
                if (
                    (g.Center - DP(b.Position)).Length <= config!.DoorApproach.DoorDistance
                    && r.Policy.Trace.Candidates.Count < 12
                )
                    r.Policy.Trace.Candidates.Add(new() { Door = g.Id, Reason = why });
            }
        }
        r.Policy.Trace!.ScanLimitReached = visited >= 48;
        foreach (
            var d in candidates
                .OrderBy(x => (x.Geometry.Center - DP(b.Position)).Length)
                .ThenBy(x => x.Geometry.Id, StringComparer.Ordinal)
                .Take(3)
        )
        foreach (int variant in Enumerable.Range(0, 16))
        foreach (int side in new[] { -1, 1 })
            r.Work.Add(
                new()
                {
                    Data = d.Data,
                    Geometry = d.Geometry,
                    Side = side,
                    Variant = variant,
                }
            );
        if (r.Work.Count == 0)
        {
            EndDoor(s, "door-no-relevant-local-door");
            return;
        }
        RecordDoor(s, true);
    }

    private static void CountDoorGeometryRefusal(DoorApproachTrace trace, string reason)
    {
        trace.GeometryRefusals.TryGetValue(reason, out int count);
        trace.GeometryRefusals[reason] = count + 1;
    }

    private string ValidateDoor(
        BotComponent b,
        DoorWork w,
        DoorPoint raw,
        bool calculate,
        out NavMeshPath? path,
        out Vector3 point,
        DoorApproachConfig? approach = null,
        bool stationary = false,
        bool approachStack = false
    )
    {
        path = null;
        point = VP(raw);
        if (!TryDoorGeometry(w.Data, out var g))
        {
            InvalidDoorGeometry(b, w.Data, raw);
            return "geometry-invalid-frame";
        }
        if (g.State != "Open" && g.State != "Shut")
            return "door-state-changed";
        if (
            (g.Center - w.Geometry.Center).Length > .1
            || Math.Abs(g.Axis.Dot(w.Geometry.Axis)) < .99
        )
            return "door-frame-changed";
        // Revalidate route relevance at selection/move request, using only the remembered native path.
        var e = b.GoalEnemy;
        var known = e?.KnownPlaces.LastKnownPosition;
        if (e == null || known == null)
            return "door-contact-ended";
        if (calculate)
        {
            string relevant = DoorPolicy.Geometry(
                g,
                DP(b.Position),
                DP(known.Value),
                RememberedDoorRoute(b, known.Value),
                approach ?? config!.DoorApproach
            );
            if (relevant.Length > 0)
                return relevant;
            if (!NavMesh.SamplePosition(point, out var sample, .7f, NavMesh.AllAreas))
                return "door-no-local-navmesh";
            point = sample.position;
            bool nearStack = approachStack && DoorApproach.NearStack(DP(b.Position), DP(point));
            if (!nearStack && !b.Mover.CanGoToPoint(point, out path, true, .1f))
                return "door-incomplete-stack-route";
            string placement = DoorPolicy.Placement(
                g,
                DP(b.Position),
                raw,
                DP(point),
                path?.corners.Select(DP).ToArray() ?? Array.Empty<DoorPoint>(),
                path?.status == NavMeshPathStatus.PathComplete,
                approachStack
            );
            if (placement.Length > 0)
                return placement;
        }
        else if (!stationary && b.Mover.ActivePath?.PathStatus != NavMeshPathStatus.PathComplete)
            return "door-route-invalidated";
        // zzap FindStackPoint: only the open leaf's flat segment clearance, not
        // an additional capsule/protective-wall test.
        if (DoorSpatial.SegmentDistance(DP(point), g.OpenA, g.OpenB) < .8)
            return "door-open-leaf-clearance";
        return "";
    }

    private static double Length(Vector3[] corners)
    {
        double d = 0;
        for (int i = 1; i < corners.Length; i++)
            d += Vector3.Distance(corners[i - 1], corners[i]);
        return d;
    }

    private static IBotPathData? QueuedDoorPath(BotComponent bot) =>
        combatPathOne != null && combatPathTwo != null ? CrossfireQueuedPath(bot) : null;

    // Point-based SAIN calls may reuse an order or create one in either buffer.
    // Retain only our previous route or a new native NavMeshPath created by this call.
    private static bool OrderDoorPoint(BotComponent bot, Vector3 point, float reach, bool sprint, ref IBotPathData? owned)
    {
        var previous = owned?.NavMeshPath;
        var activeBefore = bot.Mover.ActivePath?.NavMeshPath;
        var queuedBefore = QueuedDoorPath(bot)?.NavMeshPath;
        bool accepted = sprint ? bot.Mover.RunToPoint(point, true, reach, ESprintUrgency.High)
            : bot.Mover.WalkToPoint(point, true, reach);
        bool Matches(IBotPathData? leg) => leg?.NavMeshPath != null
            && (leg.Destination - point).sqrMagnitude < .09f
            && (ReferenceEquals(leg.NavMeshPath, previous)
                || !ReferenceEquals(leg.NavMeshPath, activeBefore) && !ReferenceEquals(leg.NavMeshPath, queuedBefore));
        var queued = QueuedDoorPath(bot);
        var active = bot.Mover.ActivePath;
        owned = !accepted ? null : Matches(queued) ? queued : Matches(active) ? active : null;
        return accepted;
    }

    private static IBotPathData? IssuedDoorPath(BotComponent bot, NavMeshPath route)
    {
        return SelectDoorLeg(QueuedDoorPath(bot), bot.Mover.ActivePath, route);
    }

    private static IBotPathData? SelectDoorLeg(IBotPathData? queued, IBotPathData? active, NavMeshPath route)
    {
        if (queued != null && ReferenceEquals(queued.NavMeshPath, route))
            return queued;
        return active != null && ReferenceEquals(active.NavMeshPath, route) ? active : null;
    }

    private static bool CurrentDoorPath(BotComponent bot, IBotPathData? path) =>
        path != null && (ReferenceEquals(bot.Mover.ActivePath, path)
            || ReferenceEquals(QueuedDoorPath(bot), path));

    private static string DoorRouteState(BotComponent bot, IBotPathData? path, NavMeshPath? route)
    {
        if (path == null || !ReferenceEquals(path.NavMeshPath, route))
            return "unobserved";
        if (ReferenceEquals(bot.Mover.ActivePath, path))
            return "active";
        return CurrentDoorPath(bot, path) ? "queued" : "released";
    }

    private static bool RequestDoorApproach(BotComponent bot, DoorRuntime r, Vector3 stack, NavMeshPath? route)
    {
        bool atStack = DoorApproach.AtStack(DP(bot.Position), DP(stack));
        r.OwnedPath = null;
        bool accepted = atStack || (route == null
            ? OrderDoorPoint(bot, stack, .42f, false, ref r.OwnedPath)
            : bot.Mover.WalkToPointByWay(route, true, .42f, false));
        if (accepted && !atStack && route != null)
            r.OwnedPath = IssuedDoorPath(bot, route);
        r.IssuedRoute = r.OwnedPath?.NavMeshPath;
        r.NextMoveOrder = Time.time + .75f;
        r.Policy.Trace!.NativeRoute = atStack ? "at-stack" : DoorRouteState(bot, r.OwnedPath, r.IssuedRoute);
        return accepted;
    }

    private static bool OwnsDoorPath(DoorRuntime r) =>
        r.Owner != null && r.OwnedPath != null && r.IssuedRoute != null
        && ReferenceEquals(r.OwnedPath.NavMeshPath, r.IssuedRoute)
        && CurrentDoorPath(r.Owner, r.OwnedPath);

    private void FinishDoor(BotState s)
    {
        var r = s.Door;
        if (OwnsDoorPath(r))
        {
            // Never stop a replacement action's path. A native Search will rebuild from its real position.
            r.OwnedPath!.Cancel();
            if (
                r.Owner!.Decision.CurrentCombatDecision == ECombatDecision.Search
                && r.Owner.CurrentAction?.GetType().Name == "SearchAction"
            )
                r.Owner.Search.Reset();
        }
        RecordDoor(s, true);
        r.DropReferences();
    }

    private void EndDoor(BotState s, string reason)
    {
        if (!s.Door.Policy.Active && !s.Door.Policy.Planning)
            return;
        s.Door.Policy.End(reason, Time.time);
        FinishDoor(s);
    }

    private void RefreshDoor(BotComponent b, BotState s)
    {
        var r = s.Door;
        if (!r.Policy.Active && !r.Policy.Planning)
            return;
        string gate = DoorGate(b, b.GoalEnemy, s);
        if (gate.Length > 0)
        {
            EndDoor(s, gate);
            return;
        }
        if (r.Policy.PlanningExpired(Time.time))
        {
            EndDoor(s, "door-plan-became-stale");
            return;
        }
        if (r.Policy.Active && Time.time - r.Started > config!.DoorApproach.ApproachLimit + 1)
            EndDoor(s, "door-action-callback-stopped");
    }

    private void RecordDoor(BotState s, bool force = false)
    {
        var r = s.Door;
        var t = r.Policy.Trace;
        if (t == null)
            return;
        string key = t.State + ":" + t.Reason + ":" + t.NativeRoute;
        if (!force && key == r.LogKey && Time.time - r.LogAt < 1)
            return;
        r.LogAt = Time.time;
        r.LogKey = key;
        var json = Diagnostics.Serialize(
            new
            {
                schema = 1,
                session,
                sequence = ++sequence,
                feature = "D01",
                stage = "evaluated",
                decision = "DoorApproach",
                reason = t.Reason,
                realtime = Time.realtimeSinceStartup,
                utc = DateTime.UtcNow.ToString("O"),
                configRevision = config?.Revision ?? 0,
                planningWaitSeconds = r.Policy.Preparation.Seconds,
                doorApproachConfig = config?.DoorApproach,
                profileId = s.ProfileId,
                name = s.Name,
                botInstance = s.Id,
                doorPlan = r.SessionPlan,
                entry = r.FromSearch ? "search" : "combat",
                selectionChance = r.SelectionChance,
                selectionRoll = r.SelectionRoll,
                doorApproach = t,
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
