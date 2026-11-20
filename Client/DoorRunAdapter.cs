using System;
using System.Linq;
using Comet.Core;
using EFT.Interactive;
using Newtonsoft.Json;
using SAIN.Components;
using SAIN.Models.Enums;
using SAIN.Preset.Shared.Enums;
using SAIN.SAINComponent.Classes.EnemyClasses;
using SAIN.SAINComponent.Classes.Mover;
using UnityEngine;
using UnityEngine.AI;

namespace Comet.Client;

internal sealed class DoorRunRuntime
{
    internal readonly DoorReconOpportunity Opportunity = new();
    internal readonly DoorRunBy Policy = new();
    internal BotComponent? Owner;
    internal DoorWork? Door;
    internal DoorJumpRuntime? JumpCandidate;
    internal IBotPathData? OwnedPath;
    internal NavMeshPath? Outward,
        Return;
    internal Vector3 Stack,
        Far,
        Angle,
        ReturnFrom,
        Look;
    internal readonly DoorMovePhase SpecialMove = new();
    internal float SpecialAt, NextOrder;
    internal int TargetInstance,
        ValidationStep;
    internal float LastCallback,
        NextCheck,
        LastCheck,
        OpenedAt,
        LogAt = -1000;
    internal bool Fault;
    internal string LogKey = "";

    internal void DropReferences()
    {
        Owner = null;
        Door = null;
        JumpCandidate?.DropReferences();
        JumpCandidate = null;
        OwnedPath = null;
        Outward = null;
        Return = null;
    }
}

public sealed partial class Plugin
{
    private void TryStartDoorRecon(BotComponent b, Enemy e, BotState s, DoorWork w)
    {
        var r = s.RunBy;
        if (
            s.Fake.Policy.Active
            || r.Fault
            || s.Jump.Fault
            || s.StepPeek.Policy.Active
            || r.Policy.Active
            || s.Jump.Policy.Active
            || DoorGate(b, e, s).Length > 0
            || e.KnownPlaces.LastKnownPosition is not Vector3 known
        )
            return;
        var t = r.Opportunity.TryBegin(
            w.Geometry.Id,
            e.EnemyProfileId,
            s.Situation?.ContactSource ?? "sain-last-known",
            b.Info.Personality.ToString(),
            DP(known),
            e.TimeSinceLastKnownUpdated,
            Time.time,
            s.Door.SelectionRoll / 100,
            jumpRandom.NextDouble(),
            config!.DoorJumpPeek,
            config.DoorRunBy,
            s.Door.SelectionChance
        );
        if (t == null)
            return;
        r.Policy.Begin(t, Time.time);
        if (r.Policy.Active)
        {
            r.Owner = b;
            r.Door = w;
            r.Stack = b.Position;
            r.Look = VP(w.Geometry.Center) + Vector3.up * 1.3f;
            r.TargetInstance = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(e);
            r.SpecialMove.Reset();
            r.ValidationStep = 0;
            r.LastCallback = Time.time;
            r.JumpCandidate = new()
            {
                Owner = b,
                Door = w,
                Stack = b.Position,
            };
            StopQuiet(b, s, "quiet-door-recon-plan");
            EndAbandonment(s, "search-check-door-recon");
            EndSilentSearch(s, "silent-search-door-recon");
        }
        RecordDoorRun(s, true);
    }

    private string RunGate(BotComponent b, Enemy? e, BotState s)
    {
        var r = s.RunBy;
        var t = r.Policy.Trace!;
        if (
            config?.Enabled != true
            || !config.DoorApproach.Enabled
            || (!config.DoorJumpPeek.Enabled && !config.DoorRunBy.Enabled)
            || t.Style == "run-by" && !config.DoorRunBy.Enabled
            || r.Fault
            || !NativeAdapter.IsSupportedAi(b)
            || !b.BotActive
            || b.IsDead
            || b.GameEnding
            || !Authority
            || Ghost(b.ProfileId)
        )
            return "run-by-out-of-scope";
        if (MedicalPostureGuard(b))
            return "medical-posture-protected";
        if (
            e == null
            || !ReferenceEquals(e, b.GoalEnemy)
            || !e.EnemyKnown
            || e.KnownPlaces.LastKnownPosition is not Vector3 known
            || !r.Policy.SameContact(e.EnemyProfileId, DP(known))
        )
            return "run-by-contact-changed-or-expired";
        if (e.IsVisible)
            t.VisualAcquired = true;
        if (!TacticalDoorSearch(b, s))
            return "run-by-search-not-owned";
        if (
            b.BotOwner.Memory.IsUnderFire
            || b.Grenade.GrenadeDangerPoint != null
            || b.Decision.CurrentSelfDecision != ESelfActionType.None
        )
            return "run-by-native-priority";
        if ((b.DoorOpener.Interacting || b.BotOwner.DoorOpener.Interacting) && t.State != "opening")
            return "run-by-other-door-interaction";
        return "";
    }

    private static bool HearRoomEnemyAtDoor(Enemy e, DoorRunRuntime r)
    {
        var heard = e.KnownPlaces.LastHeardPlace;
        return heard != null && heard.SoundType == SAINSoundType.FootStep
            && Time.time - silentHeardAt(heard) <= 1f && SupportHorizontal(heard.Position, VP(r.Door!.Geometry.Center)) <= 4;
    }

    private bool StepGroundOrSettle(BotComponent b, BotState s)
    {
        var r = s.RunBy;
        var t = r.Policy.Trace!;
        bool settle = t.State == "heard-settle", outward = t.State == "ground-out";
        var target = settle ? r.Angle : outward ? r.Far : r.Stack;
        bool arrived = SupportHorizontal(b.Position, target) < .6f || settle && Time.time - r.SpecialAt > 1.5f;
        b.Steering.LookToPoint(r.Look);
        string why = r.SpecialMove.Check(t.State, DP(b.Position), arrived, Time.time, out double remaining);
        t.Remaining = remaining;
        t.ActualX = b.Position.x; t.ActualY = b.Position.y; t.ActualZ = b.Position.z;
        if (why.Length > 0) { EndDoorRun(s, why); return true; }
        if (!outward && !settle && Time.time - r.SpecialAt < .15f) return false;
        if (arrived)
        {
            if (outward)
            {
                t.FarArrived = true;
                r.NextOrder = 0;
                r.SpecialAt = Time.time;
                r.Policy.Special("ground-back", "ground-peek-return", Time.time);
            }
            else if (settle)
            {
                t.Returned = true;
                r.SpecialAt = Time.time;
                s.Door.Feint = "";
                r.Policy.Special("heard-watch", "run-by-heard-angle-reached", Time.time);
            }
            else
            {
                t.Returned = true;
                r.Policy.Special("completed", "ground-peek-returned", Time.time);
                FinishDoorRun(s);
            }
            return false;
        }
        if (Time.time >= r.NextOrder)
        {
            r.NextOrder = Time.time + .75f;
            if (!OrderDoorPoint(b, target, .42f, !settle, ref r.OwnedPath))
            {
                EndDoorRun(s, "recon-native-movement-refused");
                return true;
            }
            t.MovementAccepted = true;
        }
        return false;
    }

    private static bool RunOwnOpening(BotComponent b, BotState s) =>
        s.RunBy.Policy.Active
        && s.RunBy.Policy.Trace?.State == "opening"
        && ReferenceEquals(s.RunBy.Owner, b)
        && s.RunBy.Door?.Data.Door != null
        && Time.time - s.RunBy.OpenedAt <= 4.5f;

    private bool StepDoorRun(BotComponent b, Enemy e, BotState s)
    {
        var r = s.RunBy;
        r.LastCallback = Time.time;
        string gate = RunGate(b, e, s);
        if (gate.Length > 0)
        {
            EndDoorRun(s, gate);
            return true;
        }
        var t = r.Policy.Trace!;
        var c = config!.DoorRunBy;
        if (t.State is "ground-out" or "ground-back" or "heard-settle")
            return StepGroundOrSettle(b, s);
        if (t.State == "heard-watch")
        {
            b.Steering.LookToPoint(r.Look);
            t.WatchElapsed = Time.time - r.SpecialAt;
            if (t.WatchElapsed >= c.Watch)
            {
                r.Policy.Special("completed", "run-by-held-exit", Time.time);
                FinishDoorRun(s);
            }
            return false;
        }
        if (t.State is "across" or "turning" or "returning" && HearRoomEnemyAtDoor(e, r))
        {
            var g = r.Door!.Geometry;
            bool inOpening = Math.Abs(Vector3.Dot(b.Position - VP(g.Center), VP(g.Axis))) < g.HalfWidth + .35;
            r.Angle = inOpening ? SupportHorizontal(b.Position, r.Stack) <= SupportHorizontal(b.Position, r.Far)
                ? r.Stack : r.Far : b.Position;
            r.Look = VP(g.Center) + Vector3.up * 1.3f;
            b.Player.EnableSprint(false);
            b.Mover.Stop();
            r.NextOrder = 0;
            r.SpecialAt = Time.time;
            r.Policy.Special("heard-settle", "run-by-heard-room-enemy", Time.time);
            return StepGroundOrSettle(b, s);
        }
        bool back = t.State is "returning" or "watching";
        Vector3 destination = back ? r.Angle : r.Far;
        var direction = destination - b.Position;
        direction.y = 0;
        var velocity = b.Player.Velocity;
        velocity.y = 0;
        bool arrived = Vector3.Distance(b.Position, destination) <= .35f;
        if (t.MovementAccepted)
        {
            if (
                !OwnsRunPath(r) && !arrived
            )
            {
                EndDoorRun(s, "run-by-path-replaced");
                return true;
            }
            if (
                !DoorRunGeometry.Contains(
                    r.Door!.Geometry,
                    DP(r.Stack),
                    DP(back ? r.ReturnFrom : r.Stack),
                    DP(destination),
                    DP(b.Position)
                )
            )
            {
                EndDoorRun(s, "run-by-left-verified-lane");
                return true;
            }
        }
        if (
            !r.Policy.Observe(
                DP(b.Position),
                Vector3.Dot(velocity, direction.normalized),
                b.Player.Physical.Stamina.NormalValue * 100,
                b.Player.MovementContext.IsGrounded,
                b.Player.IsSprintEnabled || b.Mover.Running,
                arrived,
                VisibleAdapter.WeaponReady(b),
                e.IsVisible,
                e.TimeSinceLastKnownUpdated,
                Time.time,
                c
            )
        )
        {
            FinishDoorRun(s);
            return true;
        }
        RecordDoorRun(s);
        if (t.State == "opening")
        {
            if (
                r.Door!.Data.Door.DoorState == EDoorState.Open
                && Time.time - r.OpenedAt >= 1.25f
                && !JumpInteractionState(b)
                && !b.DoorOpener.Interacting
                && !b.BotOwner.DoorOpener.Interacting
            )
            {
                r.Policy.Opened(Time.time);
                r.ValidationStep = 0;
                RecordDoorRun(s, true);
            }
            return false;
        }
        if (t.State == "validating")
        {
            if (!doorBudget.TryBegin(Time.frameCount, b))
                return false;
            doorClock.Restart();
            try
            {
                return PrepareDoorRecon(b, e, s);
            }
            finally
            {
                doorClock.Stop();
                doorBudget.Charge(doorClock.Elapsed.TotalMilliseconds);
            }
        }
        if (r.Door!.Data.Door.DoorState != EDoorState.Open)
        {
            EndDoorRun(s, "run-by-door-state-changed");
            return true;
        }
        if (t.State == "turning")
            r.OwnedPath?.RequestEndSprint(ESprintUrgency.None, "COMET run-by walking return");
        if (Time.time >= r.NextCheck || t.State == "return-pending")
        {
            if (!doorBudget.TryBegin(Time.frameCount, b))
            {
                return false;
            }
            doorClock.Restart();
            try
            {
                string why = RunLane(b, r, back);
                if (why.Length > 0)
                {
                    EndDoorRun(s, why);
                    return true;
                }
                if (t.State == "return-pending")
                {
                    // Use the observed far arrival as the start, with both route and full body volume rechecked.
                    if (!b.Mover.CanGoToPoint(r.Angle, out var path, true, .1f))
                    {
                        EndDoorRun(s, "run-by-return-no-path");
                        return true;
                    }
                    if (
                        DoorRunGeometry
                            .Route(
                                r.Door.Geometry,
                                DP(r.Stack),
                                DP(b.Position),
                                DP(r.Angle),
                                DP(r.Angle),
                                path.corners.Select(DP).ToArray(),
                                path.status == NavMeshPathStatus.PathComplete
                            )
                            .Length > 0
                    )
                    {
                        EndDoorRun(s, "run-by-return-route-invalidated");
                        return true;
                    }
                    why = RunVolume(b, b.Position, r.Angle);
                    if (why.Length > 0 || !RunAngleSight(b, r))
                    {
                        EndDoorRun(s, why.Length > 0 ? why : "run-by-angle-no-door-sightline");
                        return true;
                    }
                    if (!b.Mover.WalkToPointByWay(path, true, .2f, false))
                    {
                        EndDoorRun(s, "run-by-return-movement-refused");
                        return true;
                    }
                    r.ReturnFrom = b.Position;
                    r.Return = path;
                    r.OwnedPath = IssuedDoorPath(b, path);
                    r.Policy.ReturnStarted(DP(b.Position), Time.time);
                    if (!OwnsRunPath(r) && Vector3.Distance(b.Position, r.Angle) > .35f)
                    {
                        EndDoorRun(s, "run-by-return-path-not-observed");
                        return true;
                    }
                    RecordDoorRun(s, true);
                }
                r.LastCheck = Time.time;
                r.NextCheck = Time.time + .2f;
            }
            finally
            {
                doorClock.Stop();
                doorBudget.Charge(doorClock.Elapsed.TotalMilliseconds);
            }
        }
        b.Steering.LookToPoint(r.Look);
        return false;
    }

    // One bounded planning unit per Search call, all sharing D01/D02's frame budget.
    private bool PrepareDoorRecon(BotComponent b, Enemy e, BotState s)
    {
        var r = s.RunBy;
        var t = r.Policy.Trace!;
        var j = config!.DoorJumpPeek;
        var c = config.DoorRunBy;
        if (
            r.Door == null
            || !TryDoorGeometry(r.Door.Data, out var g)
            || g.State != "Open" && g.State != "Shut"
            || (g.Center - r.Door.Geometry.Center).Length > .1
            || Math.Abs(g.Axis.Dot(r.Door.Geometry.Axis)) < .99
            || Vector3.Distance(b.Position, r.Stack) > .35f
        )
        {
            EndDoorRun(s, "recon-door-or-stack-changed");
            return true;
        }
        if (g.State == "Shut")
        {
            if (!RequestJumpDoorOpen(b, r.JumpCandidate!))
            {
                EndDoorRun(s, "recon-native-open-refused");
                return true;
            }
            r.OpenedAt = Time.time;
            r.Policy.Opening(Time.time);
            RecordDoorRun(s, true);
            return false;
        }
        t.DoorOpenObserved = true;
        switch (r.ValidationStep++)
        {
            case 0:
                t.JumpReason = !j.Enabled || !b.Info.Profile.IsPMC
                    ? "jump-disabled"
                    : PrepareJumpRoutes(b, r.JumpCandidate!);
                return false;
            case 1:
                if (t.JumpReason.Length == 0)
                    t.JumpReason = JumpLane(b, r.JumpCandidate!, true);
                t.JumpEligible = t.JumpReason.Length == 0;
                return false;
            case 2:
                t.RunReason = !c.Enabled ? "run-by-disabled" : PrepareRunRoutes(b, r);
                return false;
            case 3:
                if (t.RunReason.Length == 0)
                {
                    t.RunReason = RunLane(b, r, false);
                    if (t.RunReason.Length == 0)
                        t.RunReason = RunVolume(b, r.Far, r.Angle);
                }
                t.RunEligible = t.RunReason.Length == 0;
                t.Style = DoorReconOpportunity.Choose(
                    t.JumpEligible,
                    t.RunEligible,
                    t.StyleRoll / 100,
                    j,
                    c
                );
                t.Alternatives = new[]
                {
                    "Jump: " + (t.JumpEligible ? "eligible" : t.JumpReason),
                    "Run-by: " + (t.RunEligible ? "eligible" : t.RunReason),
                    "Ground peek when neither special movement is available.",
                };
                if (t.Style == "none")
                {
                    EndDoorRun(s, "recon-no-eligible-style");
                    return true;
                }
                if (t.Style == "ground")
                {
                    var center = VP(g.Center);
                    var normal = VP(g.Normal);
                    var point = center + normal * Mathf.Sign(Vector3.Dot(r.Stack - center, normal)) * 1.25f;
                    if (!NavMesh.SamplePosition(point, out var sample, .7f, NavMesh.AllAreas)
                        || Vector3.Dot(sample.position - center, normal) * Mathf.Sign(Vector3.Dot(r.Stack - center, normal)) <= .3f)
                    {
                        EndDoorRun(s, "ground-peek-no-point");
                        return true;
                    }
                    r.Far = sample.position;
                    r.Look = VP(new(t.KnownX, t.KnownY, t.KnownZ)) + Vector3.up * 1.2f;
                    r.NextOrder = 0;
                    r.SpecialAt = Time.time;
                    r.Policy.Special("ground-out", "ground-peek-selected", Time.time);
                    RecordDoorRun(s, true);
                    return StepGroundOrSettle(b, s);
                }
                if (t.Style == "jump")
                {
                    var p = s.Jump;
                    var candidate = r.JumpCandidate!;
                    p.Owner = b;
                    p.Door = r.Door;
                    p.Stack = r.Stack;
                    p.Look = VP(new(t.KnownX, t.KnownY, t.KnownZ)) + Vector3.up * 1.2f;
                    p.Landing = candidate.Landing;
                    p.Outward = candidate.Outward;
                    p.Return = candidate.Return;
                    p.TargetInstance = r.TargetInstance;
                    p.ValidationStep = 0;
                    p.LastCallback = Time.time;
                    p.Policy.BeginSelected(t, Time.time);
                    r.Policy.Delegate(Time.time);
                    RecordDoorRun(s, true);
                    r.DropReferences();
                    RecordDoorJump(s, true);
                    return false;
                }
                // Recheck the chosen outward volume immediately before requesting sprint.
                string why = RunLane(b, r, false);
                if (why.Length > 0)
                {
                    EndDoorRun(s, why);
                    return true;
                }
                if (!b.Mover.RunToPointByWay(r.Outward!, true, .2f, ESprintUrgency.High, false))
                {
                    EndDoorRun(s, "run-by-native-movement-refused");
                    return true;
                }
                r.OwnedPath = IssuedDoorPath(b, r.Outward!);
                r.Policy.Start(DP(b.Position), Time.time);
                if (!OwnsRunPath(r) && Vector3.Distance(b.Position, r.Far) > .35f)
                {
                    EndDoorRun(s, "run-by-path-not-observed");
                    return true;
                }
                r.LastCheck = Time.time;
                r.NextCheck = Time.time + .2f;
                RecordDoorRun(s, true);
                return false;
        }
        EndDoorRun(s, "recon-invalid-planning-stage");
        return true;
    }

    private string PrepareRunRoutes(BotComponent b, DoorRunRuntime r)
    {
        var g = r.Door!.Geometry;
        var center = VP(g.Center);
        var axis = VP(g.Axis);
        var side = VP(g.Normal) * Math.Sign((DP(r.Stack) - g.Center).Dot(g.Normal));
        float edge = Mathf.Sign(Vector3.Dot(r.Stack - center, axis));
        if (edge == 0)
            edge = 1;
        float half = Mathf.Max(.4f, Mathf.Abs(Vector3.Dot(r.Stack - center, axis)) - .4f);
        foreach (float depth in new[] { 1.1f, .8f, 1.5f })
        foreach (float extra in new[] { 1.4f, 1f, 1.8f })
        {
            var raw = center + side * depth - axis * edge * (half + extra);
            if (
                !NavMesh.SamplePosition(raw, out var hit, .5f, NavMesh.AllAreas)
                || Vector3.Dot(hit.position - center, side) < .3f
            )
                continue;
            if (SourceRunLane(g, r.Stack, hit.position).Length > 0)
                continue;
            if (
                !b.Mover.CanGoToPoint(hit.position, out var path, true)
                || Length(path.corners) >= 9
            )
                continue;
            r.Far = hit.position;
            r.ReturnFrom = r.Far;
            r.Outward = path;
            // Source falls back to the stack when no angle is available.
            r.Angle = r.Stack;
            var mouth = center - side * .4f + Vector3.up * 1.3f;
            bool found = false;
            foreach (float d in new[] { 2.4f, 2f, 2.9f })
            {
                foreach (float wide in new[] { .8f, .4f, 1.3f })
                {
                    var angle = center + side * d + axis * edge * ((float)g.HalfWidth + wide);
                    if (
                        !NavMesh.SamplePosition(angle, out var h, .7f, NavMesh.AllAreas)
                        || Vector3.Dot(h.position - center, side) < 1.4f
                    )
                        continue;
                    if (
                        Physics.Linecast(
                            h.position + Vector3.up * 1.4f,
                            mouth,
                            LayersMaskController.HighPolyWithTerrainMask
                        ) || NavMesh.Raycast(r.Far, h.position, out _, NavMesh.AllAreas)
                    )
                        continue;
                    r.Angle = h.position;
                    found = true;
                    break;
                }
                if (found)
                    break;
            }
            var back = new NavMeshPath();
            NavMesh.CalculatePath(r.Far, r.Angle, NavMesh.AllAreas, back);
            r.Return = back;
            var t = r.Policy.Trace!;
            t.StackX = r.Stack.x;
            t.StackY = r.Stack.y;
            t.StackZ = r.Stack.z;
            t.FarX = r.Far.x;
            t.FarY = r.Far.y;
            t.FarZ = r.Far.z;
            t.AngleX = r.Angle.x;
            t.AngleY = r.Angle.y;
            t.AngleZ = r.Angle.z;
            return "";
        }
        return "run-by-no-source-lane";
    }

    private static string SourceRunLane(DoorGeometry g, Vector3 from, Vector3 to)
    {
        if (NavMesh.Raycast(from, to, out _, NavMesh.AllAreas))
            return "run-by-lane-navmesh-edge";
        int steps = Math.Max(
            2,
            (int)Math.Ceiling(Vector2.Distance(new(from.x, from.z), new(to.x, to.z)) / .25f)
        );
        for (int i = 0; i <= steps; i++)
            if (
                DoorSpatial.SegmentDistance(
                    DP(Vector3.Lerp(from, to, (float)i / steps)),
                    g.OpenA,
                    g.OpenB
                ) < .55
            )
                return "run-by-open-leaf-in-lane";
        return "";
    }

    private bool RunAngleSight(BotComponent b, DoorRunRuntime r)
    {
        var g = r.Door!.Geometry;
        var mouth =
            VP(g.Center + g.Normal * (-Math.Sign((DP(r.Stack) - g.Center).Dot(g.Normal)) * .4))
            + Vector3.up * 1.3f;
        return TacticalSightline(b, r.Angle + Vector3.up * 1.4f, mouth);
    }

    private string RunLane(BotComponent b, DoorRunRuntime r, bool back)
    {
        if (r.Door?.Data.Door == null || r.Door.Data.Door.DoorState != EDoorState.Open)
            return "run-by-door-not-open";

        return SourceRunLane(
            r.Door.Geometry,
            back ? r.ReturnFrom : r.Stack,
            back ? r.Angle : r.Far
        );
    }

    private string RunVolume(
        BotComponent b,
        Vector3 start,
        Vector3 end,
        bool overhead = false,
        float pose = 1
    ) => TacticalVolume(b, start, end, overhead, pose);

    private static bool OwnsRunPath(DoorRunRuntime r) =>
        r.Owner != null
        && r.OwnedPath != null
        && CurrentDoorPath(r.Owner, r.OwnedPath)
        && Vector3.Distance(
            r.OwnedPath.Destination,
            r.Policy.Trace?.ReturnAccepted == true ? r.Angle : r.Far
        ) < .3f;

    private void EndDoorRun(BotState s, string reason)
    {
        var r = s.RunBy;
        if (!r.Policy.Active)
            return;
        var enemy = r.Owner?.GoalEnemy;
        if (enemy?.IsVisible == true && enemy.EnemyProfileId == r.Policy.Trace!.Target)
            r.Policy.Trace.VisualAcquired = true;
        r.Policy.End(reason, Time.time);
        FinishDoorRun(s);
    }

    private void FinishDoorRun(BotState s)
    {
        var r = s.RunBy;
        if (r.Policy.Trace is { State: "ended" } ended)
            CloseFailedDoorSession(s, ended.Door, ended.Reason, r.Door?.Data.Door?.DoorState == EDoorState.Open);
        if (
            r.Owner != null
            && r.OwnedPath != null
            && CurrentDoorPath(r.Owner, r.OwnedPath)
        )
        {
            r.OwnedPath.RequestEndSprint(ESprintUrgency.None, "COMET run-by released");
            r.OwnedPath.Cancel();
            if (r.Owner.CurrentAction?.GetType().Name == "SearchAction")
                r.Owner.Search.Reset();
        }
        RecordDoorRun(s, true);
        if (r.Policy.Trace is { State: "completed" } t)
        {
            if (t.Returned && t.Reason != "run-by-held-exit" && r.Owner != null
                && SupportHorizontal(r.Owner.Position, r.Stack) <= .9f)
            {
                // The continuation consumes only the observation from the preceding peek.
                var peek = new DoorJumpTrace
                {
                    State = t.State, Returned = true, VisualAcquired = t.VisualAcquired,
                    Door = t.Door, Target = t.Target, Source = t.Source, Personality = t.Personality,
                    KnownX = t.KnownX, KnownY = t.KnownY, KnownZ = t.KnownZ, ContactAge = t.ContactAge,
                    ActualX = r.Owner.Position.x, ActualY = r.Owner.Position.y, ActualZ = r.Owner.Position.z,
                };
                TryStartDoorStep(s, r.Owner, r.Door, r.TargetInstance, peek);
            }
            if (!s.StepPeek.Policy.Active)
                FinishPeekFeint(s, r.Owner, r.Door);
        }
        r.DropReferences();
    }

    private void RefreshDoorRun(BotComponent b, BotState s)
    {
        if (!s.RunBy.Policy.Active)
            return;
        string gate = RunGate(b, b.GoalEnemy, s);
        if (gate.Length > 0)
            EndDoorRun(s, gate);
    }

    private void RecordDoorRun(BotState s, bool force = false)
    {
        var r = s.RunBy;
        var t = r.Policy.Trace;
        if (t == null)
            return;
        string key = t.State + ":" + t.Reason;
        if (!force && key == r.LogKey && Time.time - r.LogAt < .5)
            return;
        r.LogAt = Time.time;
        r.LogKey = key;
        var json = Diagnostics.Serialize(
            new
            {
                schema = 1,
                session,
                sequence = ++sequence,
                feature = "D03",
                stage = "evaluated",
                decision = "DoorRunBy",
                reason = t.Reason,
                realtime = Time.realtimeSinceStartup,
                utc = DateTime.UtcNow.ToString("O"),
                configRevision = config?.Revision ?? 0,
                doorReconConfig = new
                {
                    chances = config?.DoorJumpPeek.Chances,
                    cooldown = config?.DoorJumpPeek.Cooldown,
                },
                doorRunByConfig = config?.DoorRunBy,
                profileId = s.ProfileId,
                name = s.Name,
                botInstance = s.Id,
                doorRunBy = t,
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
