using System;
using System.Linq;
using Comet.Core;
using Newtonsoft.Json;
using SAIN.Components;
using SAIN.Preset.Shared.Enums;
using UnityEngine;
using UnityEngine.AI;

namespace Comet.Client;

internal sealed class DoorInterruptionRuntime
{
    internal readonly DoorInterruption Policy = new();
    internal readonly DoorFloorEvidence Floors = new();

    // Detached ticket. A door and a path are reacquired, never kept alive by an interrupted plan.
    internal DoorGeometry Geometry = new();
    internal DoorPoint Point,
        Known;
    internal double EvidenceAt,
        ContactLimit,
        LastSelection;
    internal int TargetInstance,
        Side,
        Variant;
    internal bool FeintChecked;
    internal float NextScan,
        RequestedAt,
        LogAt = -1000;
    internal string LogKey = "";
}

public sealed partial class Plugin
{
    private void RefreshDoorInterruption(BotComponent b, BotState s, string selected = "")
    {
        if (s.Entry.Policy.Active)
        {
            RefreshDoorEntry(b, s);
            return;
        } // D15 is never suspended/restarted through D12.
        RefreshDoorEmergency(b, s);
        if (s.Emergency.Policy.Active)
            return;
        var r = s.Interruption;
        var p = r.Policy;
        if (b == null || b.IsDead || config?.Enabled != true || !FakeScope(b))
        {
            if (p.Pending || p.Resumed)
                AbandonDoorResume(s, "inactive-ghost-or-non-authority");
            return;
        }
        var work = SupportingDoor(s);
        if (work == null && !p.Pending && !p.Resumed)
            return;
        if (p.Pending || p.Resumed)
        {
            if (!p.Tick(Time.time, config.DoorInterruption))
            {
                AbandonDoorResume(s, p.Trace!.Reason);
                return;
            }
            if (p.Resumed && work == null)
            {
                p.Complete();
                RecordDoorInterruption(s, true);
                return;
            }
        }
        string target = p.Pending ? p.Trace!.Target : DoorOriginalTarget(s);
        string cause = "",
            id = "",
            source = "";
        if (b.BotOwner.Memory.IsUnderFire)
        {
            cause = "incoming-fire";
            source = "unlocated-impact";
        }
        else if (
            selected.Length > 0 && selected != target
            || b.GoalEnemy != null && b.GoalEnemy.EnemyProfileId != target
        )
        {
            cause = "target-selection-changed";
            id = selected.Length > 0 ? selected : b.GoalEnemy.EnemyProfileId;
            source = "normal-target-selection";
        }
        // Source third-party checks on known contacts; incoming fire is checked every callback.
        if (cause.Length == 0 && b.GoalEnemy?.EnemyProfileId == target && b.GoalEnemy.IsVisible)
        {
            // Source enemyVisible ends the current session, without arming a third-party resume.
            if (work != null)
            {
                AbandonDoorResume(s, "enemy-visible");
                Release(s, "door-enemy-visible");
            }
            return;
        }
        if (cause.Length == 0 && Time.time >= r.NextScan)
        {
            r.NextScan = Time.time + .2f;
            int count = 0;
            var g = work?.Geometry ?? r.Geometry;
            foreach (var e in b.EnemyController.KnownEnemies)
            {
                if (e == null || e.EnemyProfileId == target)
                    continue;
                var c = CaptureThreat(b, e);
                if (
                    !c.Valid
                    || !c.X.HasValue
                    || !c.Y.HasValue
                    || !c.Z.HasValue
                    || !c.ObservedAt.HasValue
                    || !c.Distance.HasValue
                )
                    continue;
                var pos = new DoorPoint(c.X.Value, c.Y.Value, c.Z.Value);
                bool floor =
                    floorCache.TryGet(
                        b.ProfileId + ":" + c.Id,
                        Time.time,
                        Point(b.Transform.EyePosition),
                        new(c.X.Value, c.Y.Value, c.Z.Value),
                        out var fg
                    )
                    && fg.Floors > 0;
                floor =
                    c.Source == "heard"
                    && r.Floors.OtherFloor(
                        c.Id,
                        Time.time,
                        c.ObservedAt.Value,
                        DP(b.Transform.EyePosition),
                        pos,
                        floor,
                        config.DoorInterruption.HeardAge
                    );
                bool room =
                    Math.Sign((DP(b.Position) - g.Center).Dot(g.Normal))
                        != (Math.Sign((pos - g.Center).Dot(g.Normal)))
                    && (pos - g.Center).Length <= config.DoorApproach.ContactDistance;
                cause = DoorInterruptionThreat.Reason(
                    new()
                    {
                        Id = c.Id,
                        Source = c.Source,
                        Visible = c.Visible,
                        Gunfire = c.Gunfire,
                        OtherFloor = floor,
                        InTargetRoom = room,
                        Age = Time.time - c.ObservedAt.Value,
                        Distance = c.Distance.Value,
                    },
                    config.DoorInterruption
                );
                if (cause.Length == 0)
                    continue;
                id = c.Id;
                source = c.Source + (floor ? ":other-floor" : "") + (room ? ":target-room" : "");
                break;
            }
        }
        if (cause.Length > 0)
        {
            if (p.Pending)
            {
                p.Threat(Time.time, cause, id, source);
                RecordDoorInterruption(s);
                return;
            }
            if (work != null)
                InterruptDoor(b, s, work, cause, id, source);
        }
        RecordDoorInterruption(s);
    }

    private static string DoorOriginalTarget(BotState s) =>
        s.Ambush.Policy.Active ? s.Ambush.Policy.Trace!.Target
        : s.RoomClear.Policy.Active ? s.RoomClear.Policy.Trace!.Target
        : s.Door.Policy.Trace?.Target ?? s.Situation?.Target ?? "";

    private void InterruptDoor(
        BotComponent b,
        BotState s,
        DoorWork w,
        string cause,
        string threat,
        string evidence
    )
    {
        var r = s.Interruption;
        bool ambush = s.Ambush.Policy.Active,
            clear = s.RoomClear.Policy.Active;
        var a = s.Ambush.Policy.Trace;
        var c = s.RoomClear.Policy.Trace;
        var d = s.Door.Policy.Trace;
        string plan =
            ambush ? "D09"
            : clear ? (c!.Mode == "search" ? "D08" : "D07")
            : s.Fake.Policy.Active ? (s.Fake.Policy.Trace?.Kind == "medical" ? "D06" : "D05")
            : s.StepPeek.Policy.Active ? "D04"
            : s.RunBy.Policy.Active ? "D03"
            : s.Jump.Policy.Active ? "D02"
            : "D01";
        object identity =
            ambush ? (object)a!
            : clear ? c!
            : (
                s.Fake.Policy.Active ? (object)s.Fake.Policy.Trace!
                : s.StepPeek.Policy.Active ? s.StepPeek.Policy.Trace!
                : s.RunBy.Policy.Active ? s.RunBy.Policy.Trace!
                : s.Jump.Policy.Active ? s.Jump.Policy.Trace!
                : d!
            );
        string phase =
            ambush ? a!.State
            : clear ? c!.State
            : s.Fake.Policy.Active ? s.Fake.Policy.Trace!.State
            : s.StepPeek.Policy.Active ? s.StepPeek.Policy.Trace!.State
            : s.RunBy.Policy.Active ? s.RunBy.Policy.Trace!.State
            : s.Jump.Policy.Active ? s.Jump.Policy.Trace!.State
            : d!.State;
        bool safe =
            ambush && a!.Prepared
            || clear && s.RoomClear.Policy.SafeToSuspend
            || plan == "D01" && s.Door.Policy.Active;
        r.Point =
            ambush ? new(a!.X, a.Y, a.Z)
            : clear ? DP(s.RoomClear.Stack)
            : new(d!.X, d.Y, d.Z);
        r.Known =
            ambush ? new(a!.KnownX, a.KnownY, a.KnownZ)
            : clear ? new(c!.KnownX, c.KnownY, c.KnownZ)
            : new(d!.KnownX, d.KnownY, d.KnownZ);
        r.EvidenceAt =
            ambush ? a!.EvidenceAt
            : clear ? c!.EvidenceAt
            : s.Door.EvidenceAt;
        r.ContactLimit = Math.Max(
            config!.DoorApproach.ContactLifetime,
            config.DoorInterruption.ResumeWindow
        );
        if (clear)
            r.ContactLimit = Math.Min(r.ContactLimit, c!.ContactLimit);
        double deadline = Math.Min(
            r.EvidenceAt + r.ContactLimit,
            ambush ? s.Ambush.Policy.Deadline
                : clear ? s.RoomClear.Policy.Deadline
                : s.Door.Policy.Deadline(config.DoorApproach.ApproachLimit)
        );
        r.LastSelection = s.LastSelection;
        r.Geometry = new()
        {
            Id = w.Geometry.Id,
            State = w.Geometry.State,
            Center = w.Geometry.Center,
            Normal = w.Geometry.Normal,
            Axis = w.Geometry.Axis,
            HalfWidth = w.Geometry.HalfWidth,
        };
        r.Side = w.Side;
        r.Variant = w.Variant;
        r.TargetInstance =
            ambush ? s.Ambush.TargetInstance
            : clear ? s.RoomClear.TargetInstance
            : s.Door.TargetInstance;
        r.FeintChecked = ambush ? s.Ambush.FeintChecked : clear && s.RoomClear.FeintChecked;
        bool keep = r.Policy.Interrupt(
            new()
            {
                Attempt = r.Policy.Resumed
                    ? r.Policy.Trace!.Attempt
                    : plan
                        + ":"
                        + System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(identity),
                Plan = plan,
                Phase = phase,
                Door = w.Geometry.Id,
                Target = DoorOriginalTarget(s),
                SafeStage = safe,
                FeintConsumed =
                    (ambush && a!.Feint != "none")
                    || (clear && c!.FeintAttempted)
                    || s.Fake.Policy.Active,
                GrenadeConsumed = clear && c!.ThrowRequested,
            },
            Time.time,
            deadline,
            cause,
            threat,
            evidence,
            config.DoorInterruption
        );
        if (supportOperations.TryGetValue(b, out var op))
        {
            // Support sessions follow source LeaderStillOnDoor (two seconds),
            // rather than being cancelled synchronously with the leader.
            op.Suspended = true;
            op.Door = new() { Geometry = r.Geometry };
        }
        // zzap starts a fresh session at the same door; it never restores a half-finished animation.
        EndDoor(s, "door-third-party");
        EndDoorAmbush(s, "door-third-party");
        EndDoorClear(s, "door-third-party");
        EndDoorFake(s, "door-third-party");
        EndDoorStep(s, "door-third-party");
        EndDoorRun(s, "door-third-party");
        EndDoorJump(s, "door-third-party");
        // Existing native cleanup performs weapon recovery. No item is recreated, no animation replayed.
        Release(s, "door-threat-interruption");
        RecordDoorInterruption(s, true);
    }

    private bool ResumeSafe(BotComponent b, BotState s) =>
        FakeScope(b)
        && !b.IsDead
        && !b.GameEnding
        && b.GoalEnemy?.IsVisible == false
        && !b.BotOwner.Memory.IsUnderFire
        && b.Grenade.GrenadeDangerPoint == null
        && b.Decision.CurrentSelfDecision == ESelfActionType.None;

    private bool OriginalDoorContact(BotComponent b, DoorInterruptionRuntime r)
    {
        var e = b.GoalEnemy;
        return e != null
            && e.EnemyKnown
            && e.EnemyProfileId == r.Policy.Trace?.Target
            && e.KnownPlaces.LastKnownPosition.HasValue
            && e.TimeSinceLastKnownUpdated
                <= Math.Max(
                    config!.DoorApproach.ContactLifetime,
                    config.DoorInterruption.ResumeWindow
                );
    }

    private bool RequestDoorResume(
        BotComponent b,
        BotState s,
        ref ECombatDecision decision,
        ref bool result
    )
    {
        var r = s.Interruption;
        var p = r.Policy;
        // Release cleared A01's old selection. Keep the verified resumed move (and its continuation)
        // instead of rolling A01 again on the next decision tick. Existing phase gates still win.
        if (p.Resumed)
        {
            string? gate =
                s.Door.Policy.Active ? DoorGate(b, b.GoalEnemy, s)
                : s.Jump.Policy.Active ? JumpGate(b, b.GoalEnemy, s)
                : s.RunBy.Policy.Active ? RunGate(b, b.GoalEnemy, s)
                : s.StepPeek.Policy.Active ? StepGate(b, b.GoalEnemy, s)
                : null;
            if (gate == null)
                return false; // D07/D09/feints use their existing continuation handlers.
            if (gate.Length > 0)
            {
                AbandonDoorResume(s, gate);
                return false;
            }
            s.Expected = ECombatDecision.Search;
            s.RequestedFrame = Time.frameCount;
            decision = s.Expected;
            result = true;
            return true;
        }
        if (!p.Pending)
            return false;
        if (p.Trace!.State == "requested")
        {
            if (!ResumeSafe(b, s) || b.GoalEnemy?.EnemyProfileId != p.Trace.Target)
            {
                AbandonDoorResume(s, "resume-priority-changed");
                return false;
            }
            s.Expected = ECombatDecision.Search;
            s.RequestedFrame = Time.frameCount;
            decision = s.Expected;
            result = true;
            return true;
        }
        if (
            !p.Ready(
                Time.time,
                b.GoalEnemy?.EnemyProfileId ?? "",
                ResumeSafe(b, s),
                config!.DoorInterruption
            )
        )
        {
            RecordDoorInterruption(s);
            return false;
        }
        if (!OriginalDoorContact(b, r))
        {
            AbandonDoorResume(s, "original-contact-changed-or-expired");
            return false;
        }

        if (DoorBusy(b, s, p.Trace!.Door))
            return false;
        p.Request();
        r.RequestedAt = Time.time;
        s.Feature = "A01";
        s.Decision = "Search";
        s.Expected = s.Decision == "Hold" ? ECombatDecision.Freeze : ECombatDecision.Search;
        s.Candidates = new()
        {
            new()
            {
                Decision = s.Decision,
                Eligible = true,
                Reason = "door-safe-resume-requested",
            },
        };
        s.Mixing = null;
        s.FearAtSelection = s.Fear.Current;
        s.OpponentAtSelection = null;
        s.OpportunityAtSelection = null;
        s.LoadoutAtSelection = null;
        s.ProgressAtSelection = null;
        Emit(s, "selected", "door-safe-resume-requested");
        s.LastSelection = (float)r.LastSelection;
        s.RequestedFrame = Time.frameCount;
        decision = s.Expected;
        result = true;
        RecordDoorInterruption(s, true);
        return true;
    }

    private void DoorResumeAccepted(BotState s, bool applied)
    {
        if (s.Interruption.Policy.Trace?.State != "requested")
            return;
        s.Interruption.Policy.Accept(applied);
        if (!applied)
            AbandonDoorResume(s, "native-action-refused");
        RecordDoorInterruption(s, true);
    }

    private bool StepDoorResume(BotComponent b, BotState s)
    {
        var r = s.Interruption;
        var p = r.Policy;
        var t = p.Trace!;
        if (t.State != "requested" || !t.NativeAccepted)
            return true;
        string action = "Search";
        if (
            !ResumeSafe(b, s)
            || b.GoalEnemy?.EnemyProfileId != t.Target
            || s.Feature != "A01"
            || s.ActiveLabel != "COMET:" + action
            || b.CurrentAction?.GetType().Name
                != (action == "Hold" ? "FreezeAction" : "SearchAction")
            || !b.SAINLayersActive
        )
        {
            AbandonDoorResume(s, "resume-native-ownership-lost");
            return true;
        }
        if (!OriginalDoorContact(b, r))
        {
            AbandonDoorResume(s, "resume-contact-or-resources-changed");
            return true;
        }
        if (!doorBudget.TryBegin(Time.frameCount, b))
            return true;
        doorClock.Restart();
        try
        {
            DoorWork? w = null;
            int count = 0;
            var voxel = b.BotOwner.VoxelesPersonalData.CurVoxel;
            if (voxel?.DoorLinks != null)
                foreach (var link in voxel.DoorLinks)
                {
                    count++;
                    if (link?.Door == null)
                        continue;
                    var candidate = new DoorWork { Data = new(link) };
                    var data = candidate.Data;
                    if (TryDoorGeometry(data, out var g) && g.Id == t.Door)
                    {
                        w = new()
                        {
                            Data = data,
                            Geometry = g,
                            Side = r.Side,
                            Variant = r.Variant,
                        };
                        break;
                    }
                }
            if (w == null)
            {
                AbandonDoorResume(s, "resume-door-changed-or-unavailable");
                return true;
            }
            var known = b.GoalEnemy!.KnownPlaces.LastKnownPosition!.Value;
            var raw = DoorPolicy.Stack(w.Geometry, DP(b.Position), w.Side, w.Variant);
            string invalid = ValidateDoor(b, w, raw, true, out var path, out var dest, approachStack: true);
            if (invalid.Length > 0)
            {
                AbandonDoorResume(s, invalid);
                return true;
            }
            if (!AcquireDoorReservation(b, s, w, "resume:approach", Time.time + 150))
            {
                AbandonDoorResume(s, "reservation-resume-unavailable");
                return true;
            }
            t.Gates = new[] { "source resume window", "same door", "reachable route" };
            var d = s.Door;
            d.Owner = b;
            d.Chosen = w;
            d.Target = b.GoalEnemy.EnemyProfileId;
            d.TargetInstance = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(
                b.GoalEnemy
            );
            d.Contact = known;
            d.EvidenceAt = Time.time - b.GoalEnemy.TimeSinceLastKnownUpdated;
            d.Started = Time.time;
            d.Policy.Begin(
                d.Target,
                "sain-last-known",
                DP(known),
                b.GoalEnemy.TimeSinceLastKnownUpdated,
                Time.time
            );
            d.Policy.Request(
                new()
                {
                    Door = t.Door,
                    Eligible = true,
                    X = dest.x,
                    Y = dest.y,
                    Z = dest.z,
                    PathLength = path == null ? SupportHorizontal(b.Position, dest) : Length(path.corners),
                },
                DP(b.Position),
                Time.time
            );
            b.Search.Reset();
            bool accepted = RequestDoorApproach(b, d, dest, path);
            d.Policy.Accept(accepted, Time.time);
            d.NextCheck = Time.time + .25f;
            if (!accepted)
            {
                AbandonDoorResume(s, "resume-move-refused");
                return true;
            }
            if (!p.Execute(Time.time, config!.DoorInterruption))
            {
                AbandonDoorResume(s, "resume-budget-expired");
                return true;
            }
            if (supportOperations.TryGetValue(b, out var op) && !op.Ended)
            {
                op.Door = w;
                op.Suspended = false;
            }
            // A resumed session gets the source session duration, not the interrupted phase deadline.
            RecordDoorInterruption(s, true);
            return false;
        }
        finally
        {
            doorClock.Stop();
            doorBudget.Charge(doorClock.Elapsed.TotalMilliseconds);
        }
    }

    private void AbandonDoorResume(BotState s, string why)
    {
        ReleaseDoorReservation(s, why);
        var p = s.Interruption.Policy;
        p.Abandon(why);
        if (s.Door.Policy.Trace?.State == "suspended")
            s.Door.Policy.End(why, Time.time);
        if (s.Ambush.Policy.Trace?.State == "suspended")
            s.Ambush.Policy.AbandonSuspended(why);
        if (s.RoomClear.Policy.Trace?.State == "suspended")
            s.RoomClear.Policy.End(why);
        EndDoor(s, why);
        EndDoorAmbush(s, why);
        EndDoorClear(s, why);
        EndDoorFake(s, why);
        EndDoorStep(s, why);
        EndDoorRun(s, why);
        EndDoorJump(s, why);
        foreach (
            var op in supportOperations
                .Values.Where(o => o.Leader.ProfileId == s.ProfileId)
                .ToArray()
        )
            EndSupportOperation(op, "support-resume-abandoned");
        RecordDoorInterruption(s, true);
    }

    private void ReleaseDoorInterruption(BotState s, string reason)
    {
        var p = s.Interruption.Policy;
        if (!p.Pending && !p.Resumed)
            return;
        if (
            reason.Contains("ghost")
            || reason.Contains("inactive")
            || reason.Contains("authority")
            || reason.Contains("bot-ended")
            || reason.Contains("raid-ended")
            || reason.Contains("error")
            || reason.Contains("out-of-scope")
        )
        {
            s.Interruption.Floors.Clear();
            AbandonDoorResume(s, reason);
        }
    }

    private void RecordDoorInterruption(BotState s, bool force = false)
    {
        var r = s.Interruption;
        var t = r.Policy.Trace;
        if (t == null)
            return;
        string key = t.State + ":" + t.Reason + ":" + t.Cause + ":" + t.NativeAccepted;
        if (
            !force
            && key == r.LogKey
            && (!r.Policy.Pending && !r.Policy.Resumed || Time.time - r.LogAt < .5)
        )
            return;
        r.LogKey = key;
        r.LogAt = Time.time;
        string json = Diagnostics.Serialize(
            new
            {
                schema = 1,
                session,
                sequence = ++sequence,
                feature = "D12",
                stage = "evaluated",
                decision = "DoorInterruption",
                reason = t.Reason,
                realtime = Time.realtimeSinceStartup,
                utc = DateTime.UtcNow.ToString("O"),
                configRevision = config?.Revision ?? 0,
                profileId = s.ProfileId,
                name = s.Name,
                botInstance = s.Id,
                doorInterruption = t,
                doorInterruptionConfig = config?.DoorInterruption,
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
