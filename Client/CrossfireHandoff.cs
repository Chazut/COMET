using System;
using Comet.Core;
using SAIN.Components;
using SAIN.Preset.Shared.Enums;
using SAIN.SAINComponent.Classes.Mover;
using UnityEngine;

namespace Comet.Client;

public sealed partial class Plugin
{
    // A hidden-contact tactical choice can replace ordinary search/cover work, not
    // grenade avoidance, melee, self-care or an unknown native combat decision.
    private static bool CrossfireTacticalDecision(BotComponent b) =>
        b.Decision.CurrentCombatDecision
            is ECombatDecision.None
                or ECombatDecision.Search
                or ECombatDecision.Freeze
                or ECombatDecision.StandAndShoot
                or ECombatDecision.SeekCover
                or ECombatDecision.ShiftCover
                or ECombatDecision.MoveToEngage
                or ECombatDecision.ShootDistantEnemy
                or ECombatDecision.CreepOnEnemy;

    // Freeze/standing decisions can be committed before SAIN releases the outgoing
    // manual path. Observe that path too; acquisition still verifies its identity,
    // start time, ground state and current priority before cancelling anything.
    private static bool CrossfireCanHandoff(BotComponent b) =>
        CrossfireTacticalDecision(b) && b.Mover.ActivePath is BotPathDataManual;

    private static bool CrossfireCanAcquire(BotComponent b)
    {
        if (
            !CrossfireTacticalDecision(b)
            || b.Mover.ActivePath is not null and not BotPathDataManual
        )
            return false;
        var queued = CrossfireQueuedPath(b);
        return queued is null or BotPathDataManual;
    }

    private static void RememberCrossfireHandoff(BotComponent b, CrossfireRuntime r)
    {
        r.HandoffPath = CrossfireCanHandoff(b) ? b.Mover.ActivePath : null;
        r.HandoffStarted = r.HandoffPath?.TimeStarted ?? 0;
        r.HandoffDestination = r.HandoffPath?.Destination ?? default;
        r.HandoffQueued = CrossfireTacticalDecision(b) ? CrossfireQueuedPath(b) : null;
        r.HandoffQueuedStarted = r.HandoffQueued?.TimeStarted ?? 0;
        r.HandoffQueuedDestination = r.HandoffQueued?.Destination ?? default;
        r.HandoffWait = -1;
        r.HandoffObservedAt = Time.time;
        r.Policy.Trace!.Handoff = new()
        {
            PlannedDecision = b.Decision.CurrentCombatDecision.ToString(),
            ExpectedPath =
                r.HandoffPath == null
                    ? 0
                    : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(r.HandoffPath),
            ExpectedStarted = r.HandoffStarted,
            ExpectedQueuedPath =
                r.HandoffQueued == null
                    ? 0
                    : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(r.HandoffQueued),
        };
    }

    private bool PendingCrossfireHandoff(BotComponent b, CrossfireRuntime r)
    {
        // The decision manager commits Freeze before BigBrain starts our action.
        // Sprint can still reflect the outgoing path during this handoff.
        var path = b.Mover.ActivePath;
        return r.Policy.Active
            && r.HandoffPath != null
            && CrossfireTacticalDecision(b)
            && (
                path == null
                || path is BotPathDataManual
                    && ReferenceEquals(path, r.HandoffPath)
                    && path.TimeStarted == r.HandoffStarted
            );
    }

    private bool TakeCrossfireHandoff(BotComponent b, BotState s)
    {
        var r = s.Crossfire;
        var path = b.Mover.ActivePath;
        var evidence = r.Policy.Trace!.Handoff ??= new();
        evidence.Decision = b.Decision.CurrentCombatDecision.ToString();
        evidence.CurrentPath =
            path == null ? 0 : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(path);
        evidence.CurrentStarted = path?.TimeStarted ?? 0;
        evidence.PathType = path?.GetType().Name ?? "none";
        var queued = CrossfireQueuedPath(b);
        // SAIN may prepare the next ordinary leg between planning and our layer's
        // first callback. Acquire it only while the original active path is still
        // unchanged and all current emergency/ownership guards have passed.
        if (
            queued is BotPathDataManual
            && path != null
            && ReferenceEquals(path, r.HandoffPath)
            && path.TimeStarted == r.HandoffStarted
            && (path.Destination - r.HandoffDestination).sqrMagnitude < .0004f
            && CrossfireTacticalDecision(b)
            && b.Player.MovementContext.IsGrounded
        )
        {
            evidence.QueueCapturedAtHandoff =
                !ReferenceEquals(queued, r.HandoffQueued)
                || queued.TimeStarted != r.HandoffQueuedStarted
                || (queued.Destination - r.HandoffQueuedDestination).sqrMagnitude >= .0004f;
            r.HandoffQueued = queued;
            r.HandoffQueuedStarted = queued.TimeStarted;
            r.HandoffQueuedDestination = queued.Destination;
            evidence.ExpectedQueuedPath =
                System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(queued);
        }
        evidence.QueuedPath =
            queued == null ? 0 : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(queued);
        bool observedQueue =
            queued == null
            || ReferenceEquals(queued, r.HandoffQueued)
                && queued.TimeStarted == r.HandoffQueuedStarted
                && (queued.Destination - r.HandoffQueuedDestination).sqrMagnitude < .0004f;
        bool promoted =
            path != null
            && ReferenceEquals(path, r.HandoffQueued)
            && (path.Destination - r.HandoffQueuedDestination).sqrMagnitude < .0004f
            && path.TimeStarted >= r.HandoffObservedAt
            && path.TimeStarted <= Time.time;
        evidence.Reason =
            !CrossfireTacticalDecision(b) ? "protected-decision"
            : !b.Player.MovementContext.IsGrounded ? "airborne"
            : path == null ? "already-released"
            : path is not BotPathDataManual ? "non-manual-path"
            : promoted ? "observed-tactical-path"
            : r.HandoffPath == null ? "unobserved-path"
            : !ReferenceEquals(path, r.HandoffPath) ? "path-replaced"
            : path.TimeStarted != r.HandoffStarted ? "path-restarted"
            : (path.Destination - r.HandoffDestination).sqrMagnitude >= .0004f ? "path-retargeted"
            : "observed-tactical-path";
        if (evidence.Reason != "observed-tactical-path" && evidence.Reason != "already-released")
            return false;
        if (
            !observedQueue
            || queued is not null and not BotPathDataManual
            || path == null && queued != null
        )
        {
            // A new queued path may finish its ordinary native transition. Never
            // cancel an unobserved/reused route or keep a squad waiting indefinitely.
            if (r.HandoffWait < 0)
                r.HandoffWait = Time.time;
            evidence.WaitSeconds = Time.time - r.HandoffWait;
            evidence.Reason =
                evidence.WaitSeconds < .75 ? "waiting-native-queue" : "unobserved-queue-timeout";
            RecordCrossfire(s, evidence.WaitSeconds == 0);
            return false;
        }
        if (evidence.Reason == "already-released" && queued == null)
        {
            r.HandoffPath = null;
            r.HandoffQueued = null;
            return true;
        }
        if (evidence.Reason != "observed-tactical-path")
            return false;
        var manual = (BotPathDataManual)path!;
        // Only after BigBrain selected our action, and only the path observed when
        // planning. Never cancel a new route installed by an emergency or another mod.
        combatStopPath.Invoke(manual, new object[] { false, "COMET squad tactical handoff" });
        evidence.ReleasedPaths++;
        // SAIN PathComplete synchronously starts its prepared alternate route.
        // Release it only if it is exactly the queue observed for this plan.
        var next = b.Mover.ActivePath;
        if (
            queued is BotPathDataManual prepared
            && ReferenceEquals(next, queued)
            && CrossfireTacticalDecision(b)
            && !QueuedCombatMovement(b)
            && (prepared.Destination - r.HandoffQueuedDestination).sqrMagnitude < .0004f
            && prepared.TimeStarted >= r.HandoffObservedAt
            && prepared.TimeStarted <= Time.time
        )
        {
            combatStopPath.Invoke(
                prepared,
                new object[] { false, "COMET observed squad queue handoff" }
            );
            evidence.ReleasedPaths++;
        }
        r.HandoffPath = null;
        bool released = b.Mover.ActivePath == null && !QueuedCombatMovement(b);
        evidence.Reason = released
            ? "released-observed-tactical-path"
            : "native-release-unconfirmed";
        if (released)
            r.HandoffQueued = null;
        RecordCrossfireGate(s, "squad-tactical-route-handoff", true);
        RecordCrossfire(s, true);
        return released;
    }

    private void RecordCrossfireGate(
        BotState s,
        string reason,
        bool force = false,
        string feature = "S03"
    )
    {
        if (!ShouldLogCrossfireGate(s, feature, reason, force, out var skipped))
            return;
        // Eligibility is diagnostic evidence, not a claimed action on the RR map.
        diagnostics?.Write(
            Diagnostics.Serialize(
                new
                {
                    schema = 1,
                    session,
                    sequence = ++sequence,
                    feature,
                    stage = "eligibility",
                    reason,
                    skipped,
                    realtime = Time.realtimeSinceStartup,
                    profileId = s.ProfileId,
                    name = s.Name,
                    botInstance = s.Id,
                }
            )
        );
    }

    private bool ShouldLogCrossfireGate(BotState s, string feature, string reason, bool force, out int skipped)
    {
        skipped = 0;
        if (!Comet.Core.ReplayCapture.Detailed)
            return false;
        var r = s.Crossfire;
        if (!force && r.EligibilityLogAt.TryGetValue(feature, out var next) && Time.time < next)
        {
            r.EligibilitySkipped.TryGetValue(feature, out int count);
            r.EligibilitySkipped[feature] = count + 1;
            return false;
        }
        r.EligibilitySkipped.TryGetValue(feature, out skipped);
        r.EligibilitySkipped[feature] = 0;
        r.EligibilityLogAt[feature] = Time.time + 5;
        r.GateKey = reason;
        r.GateLogAt = Time.time + 5;
        return true;
    }

    private bool DeclineSquad(BotState s, string feature, string reason)
    {
        RecordCrossfireGate(s, reason, feature: feature);
        return false;
    }
}
