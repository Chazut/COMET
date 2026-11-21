using System;
using System.Collections.Generic;
using Comet.Core;
using DrakiaXYZ.BigBrain.Brains;
using EFT;
using HarmonyLib;
using Newtonsoft.Json;
using SAIN;
using SAIN.Components;
using SAIN.Preset.Shared.Enums;
using SAIN.SAINComponent.Classes.Decision;
using SAIN.SAINComponent.Classes.EnemyClasses;
using SAIN.SAINComponent.Classes.Mover;
using UnityEngine;
using UnityEngine.AI;

namespace Comet.Client;

internal sealed class CrossfireRuntime
{
    internal readonly Crossfire Policy = new();
    internal readonly CoverMateRuntime Care = new();
    internal readonly SquadTradeRuntime Trade = new();
    internal readonly SquadStormRuntime Storm = new();
    internal int ManeuverAttempts;
    internal bool ManeuverFire;
    internal SquadManeuverPlan? Maneuver;
    internal ManeuverMember? ManeuverRole;
    internal float ManeuverStarted,
        ManeuverNext;
    internal Vector3 Watch;
    internal readonly EntranceRuntime Entrance = new();
    internal float Next,
        LogAt,
        ContactAt,
        LastSafety,
        StartWait,
        NextMoveOrder;
    internal SourcePeekSpot? Peek;
    internal int ProbeFrame = -1,
        Candidate,
        Sample,
        FinalizedEpisode,
        ContactInstance;
    internal bool Layer,
        Fault,
        Indoor,
        TraceApplied,
        FallbackPass;
    internal float OldPose,
        AppliedPose;
    internal bool OwnPose;
    internal string Key = "",
        Support = "",
        Contact = "",
        Group = "";
    internal Vector3 Known,
        Origin,
        Reference,
        Goal;
    internal readonly List<Vector3> Candidates = new(),
        Samples = new();
    internal readonly List<string> Rejects = new();
    internal NavMeshPath? Route;
    internal IBotPathData? Path;
    internal float PathStarted,
        Angle,
        Length;
    internal IBotPathData? HandoffPath;
    internal IBotPathData? HandoffQueued;
    internal Vector3 HandoffDestination,
        HandoffQueuedDestination;
    internal float HandoffQueuedStarted,
        HandoffObservedAt,
        HandoffWait = -1;
    internal float HandoffStarted,
        NextEligibility,
        GateLogAt,
        AirborneSince = -1,
        NextTraceAt;
    internal readonly Dictionary<string, float> EligibilityLogAt = new();
    internal readonly Dictionary<string, int> EligibilitySkipped = new();
    internal string GateKey = "",
        TraceState = "";
}

public sealed partial class Plugin
{
    private readonly CrossfireClaims crossfireClaims = new();

    private static string SquadRosterIdentity(BotComponent b) => SquadMemberIdentity.Get(b.Squad.Members);

    private static void PatchCrossfire(Harmony h)
    {
        h.Patch(
            AccessTools.Method(typeof(BotDecisionManager), nameof(BotDecisionManager.ManualUpdate)),
            postfix: new HarmonyMethod(typeof(Plugin), nameof(ObserveCrossfire))
        );
        BrainManager.AddCustomLayer(
            typeof(CrossfireLayer),
            new()
            {
                "PMC",
                "PmcUsec",
                "PmcBear",
                "Assault",
                "ExUsec",
                "Knight",
                "BigPipe",
                "BirdEye",
            },
            21
        );
    }

    private bool CrossfireScope(BotComponent b) =>
        config?.Enabled == true
        && (
            config.Crossfire.Enabled
            || config.SquadManeuver.Enabled
            || config.CoverMate.Enabled
            || config.SquadTrade.Enabled
            || config.SquadStorm.Enabled
        )
        && world != null
        && Authority
        && b != null
        && NativeAdapter.IsSupportedAi(b)
        && b.BotActive
        && !b.IsDead
        && !b.GameEnding
        && !Ghost(b.ProfileId)
        && ContactSafety.AllowsGoal(b);

    private string CrossfireBlock(
        BotComponent b,
        BotState s,
        bool maneuverCandidate = false
    )
    {
        bool maneuver = maneuverCandidate || s.Crossfire.Maneuver != null;
        if (
            !CrossfireScope(b)
            || !(maneuver ? config!.SquadManeuver.Enabled : config!.Crossfire.Enabled)
        )
            return "crossfire-out-of-scope";
        var e = b.GoalEnemy;
        if (
            e == null
            || !e.EnemyKnown
            || e.KnownPlaces.LastKnownPosition is not Vector3 known
            || !ValidPoint(known)
        )
            return "crossfire-no-known-contact";
        bool supporting =
            s.Crossfire.ManeuverRole?.Role == "support"
            || e.IsVisible && maneuverCandidate
                && b.Squad.SquadInfo?.Id is string squadId
                && maneuverBoard.Get(squadId) is { Active: true } plan
                && plan.Member(b.ProfileId)?.Role == "support";

        if (e.IsVisible && !supporting && !s.Crossfire.Policy.Active)
            return "crossfire-enemy-visible";
        double contactAge = s.Crossfire.Policy.Active ? 25 : config!.Crossfire.ContactAge;
        bool continuing =
            s.Crossfire.Policy.CanContinueRememberedRoute(Time.time, config.Crossfire)
            || supporting
                && s.Crossfire.Maneuver?.HasProgressingFlanker(Time.time, config.SquadManeuver)
                    == true;
        if (s.Crossfire.Policy.Trace != null)
            s.Crossfire.Policy.Trace.ContinuingRememberedRoute =
                continuing && e.TimeSinceLastKnownUpdated > contactAge;
        if (
            !float.IsFinite(e.TimeSinceLastKnownUpdated)
            || e.TimeSinceLastKnownUpdated < 0
            || !maneuver && e.TimeSinceLastKnownUpdated > contactAge && !continuing
        )
            return "crossfire-contact-stale";
        if (
            !maneuver && !s.Crossfire.Policy.Active
            && Vector3.ProjectOnPlane(known - b.Position, Vector3.up).magnitude > config.Crossfire.EnemyDistance
        )
            return "crossfire-enemy-too-far";
        if (s.Crossfire.Policy.Active && e.EnemyProfileId != s.Crossfire.Contact)
            return "crossfire-contact-changed";
        return CrossfireCommonBlock(
            b,
            s,
            allowGoalVisible: supporting || s.Crossfire.Policy.Active
        );
    }

    private static bool DoorPlanActive(BotState s) =>
        s.Door.Policy.Active
        || s.Jump.Policy.Active
        || s.RunBy.Policy.Active
        || s.StepPeek.Policy.Active
        || s.Fake.Policy.Active
        || s.RoomClear.Policy.Active
        || s.Ambush.Policy.Active;

    private string CrossfireCommonBlock(
        BotComponent b,
        BotState s,
        bool trade = false,
        bool allowGoalVisible = false
    )
    {
        // SquadCombatClass: the moving angle yields to incoming fire; fire/rear/trade roles do not.
        bool holdsUnderFire = s.Crossfire.ManeuverRole?.Role is "support" or "rear";
        if (b.BotOwner.Memory.IsUnderFire && !holdsUnderFire && !trade)
            return "crossfire-under-fire";
        // Active plans keep their identity while native hands/physics finish their work.
        if (!s.Crossfire.Policy.Active && DoorPlanActive(s))
            return "crossfire-door-tactic";
        if (
            !s.Crossfire.Policy.Active
            && (
                (!trade && !b.Squad.BotInGroup)
                || b.Squad.Members == null
                || b.Squad.Members.Count < (trade ? 1 : 2)
            )
        )
            return "crossfire-no-squad";
        return "";
    }

    private BotComponent? CrossfireSupport(BotComponent b, string id = "")
    {
        if (b.Squad.Members == null)
            return null;
        foreach (var mate in b.Squad.Members.Values)
        {
            if (
                mate == null
                || ReferenceEquals(b, mate)
                || id.Length > 0 && mate.ProfileId != id
                || mate.IsDead
                || mate.GoalEnemy?.EnemyProfileId != b.GoalEnemy?.EnemyProfileId
                || mate.GoalEnemy?.IsVisible != true
            )
                continue;
            return mate;
        }
        return null;
    }

    private static void ObserveCrossfire(BotDecisionManager __instance)
    {
        var p = Instance;
        var b = __instance.Bot;
        if (p == null || !p.CrossfireScope(b))
            return;
        try
        {
            if (!p.states.TryGetValue(b, out var s))
            {
                if (p.states.Count >= 512)
                    return;
                s = new BotState(b, p.config!, p.session);
                p.states.Add(b, s);
            }
            if (!s.Crossfire.Policy.Active)
            {
                if (Time.time < s.Crossfire.NextEligibility)
                    return;
                s.Crossfire.NextEligibility = Time.time + (float)p.config!.Crossfire.Interval;
                if (s.Siege != null || p.ActiveSquadGrenade(b, s))
                    return;
            }
            p.RefreshCrossfire(b, s, true);
        }
        catch (Exception ex)
        {
            if (p.states.TryGetValue(b, out var s))
                p.CrossfireError(b, s, ex);
        }
    }

    private void SampleCrossfire(BotComponent b, BotState s)
    {
        if (!s.Crossfire.Policy.Active)
            return;
        try
        {
            RefreshCrossfire(b, s, false);
        }
        catch (Exception ex)
        {
            CrossfireError(b, s, ex);
        }
    }

    private bool RefreshCrossfire(BotComponent b, BotState s, bool mayStart)
    {
        var r = s.Crossfire;
        if (r.Fault)
            return false;
        if (!r.Policy.Active && mayStart)
        {
            // All variants share these safety gates. Do not rebuild several squad
            // candidates on every tick when a native emergency already excludes them.
            string common = CrossfireCommonBlock(b, s, true, allowGoalVisible: true);
            if (common.Length > 0)
            {
                RecordCrossfireGate(s, common);
                return false;
            }
        }
        if (r.Policy.Active && r.Policy.Trace?.Trade != null)
            return RefreshSquadTrade(b, s);
        if (!r.Policy.Active && mayStart && TryBeginSquadTrade(b, s))
            return RefreshSquadTrade(b, s);
        if (r.Policy.Active && r.Policy.Trace?.CoverMate != null)
            return RefreshCoverMate(b, s);
        var observed = b.GoalEnemy;
        if (
            r.Policy.Active
            && observed?.EnemyKnown == true
            && observed.EnemyProfileId == r.Contact
            && observed.KnownPlaces.LastKnownPosition is Vector3 remembered
            && float.IsFinite(observed.TimeSinceLastKnownUpdated)
            && observed.TimeSinceLastKnownUpdated >= 0
        )
            r.ContactAt = Math.Max(r.ContactAt, Time.time - observed.TimeSinceLastKnownUpdated);
        // The freshness and range of the proposer matter only when forming a new S03.
        bool mayStartManeuver = !r.Policy.Active && mayStart && config!.SquadManeuver.Enabled;
        string block = CrossfireBlock(b, s, maneuverCandidate: mayStartManeuver);
        if (r.Policy.Active && block.Length > 0)
        {
            EndCrossfire(b, s, block);
            return false;
        }
        if (
            r.Policy.Active
            && r.Policy.Trace!.Entrance != null
            && observed!.IsVisible
            && observed.CanShoot
        )
        {
            r.Policy.Trace.Outcome = "contact-engaged";
            EndCrossfire(b, s, "secondary-entrance-contact-engaged");
            return false;
        }
        if (r.Maneuver != null && r.Policy.Active && !ValidateManeuver(b, s))
            return false;
        if (r.Policy.Active && !RevalidateCrossfireContext(b, s))
        {
            if (r.Policy.Active && !r.Policy.CheckMotion(Time.time, config!.Crossfire))
                EndCrossfire(b, s, r.Policy.Trace!.Reason);
            return r.Policy.Active && r.Policy.Trace!.State != "planning";
        }
        var c = config!.Crossfire;
        if (mayStartManeuver && block.Length == 0)
            TryBeginManeuver(b, s);
        if (!r.Policy.Active)
        {
            if (!mayStart)
                return false;
            // A declined or ineligible maneuver leaves teammate cover ahead of plain crossfire.
            if (TryBeginCoverMate(b, s))
                return RefreshCoverMate(b, s);
            if (mayStartManeuver)
                block = CrossfireBlock(b, s);
            if (block.Length > 0)
            {
                RecordCrossfireGate(s, block, feature: "S01");
                return false;
            }
        }
        var support =
            r.Maneuver != null
            && r.Policy.Active
            && b.Squad.Members!.TryGetValue(r.Support, out var plannedSupport)
                ? plannedSupport
                : CrossfireSupport(b, r.Policy.Active ? r.Support : "");
        if (r.Policy.Active)
        {
            if (r.Policy.Trace!.Entrance != null && !ValidateEntranceActive(b, s, false))
                return false;
            if (
                !r.Policy.CheckMotion(
                    Time.time,
                    c,
                    holdLimit: r.Policy.Trace.Entrance != null
                        ? config.SecondaryEntrance.WatchMax + config.SecondaryEntrance.Extension + 8
                        : null
                )
            )
            {
                EndCrossfire(b, s, r.Policy.Trace!.Reason);
                return false;
            }
            // zzap claims influence selection; expiry never cancels an existing session.
            r.Policy.Trace.ContactAge = Math.Max(0, Time.time - r.ContactAt);
        }
        else
        {
            if (Time.time < r.Next || Time.time < r.Policy.RetryAt)
                return false;
            if (support == null)
            {
                RecordCrossfireGate(s, "crossfire-no-visible-support", feature: "S01");
                return false;
            }
            r.Next = Time.time + (float)c.Interval;
            r.Maneuver = null;
            r.ManeuverRole = null;
            bool selected = r.Policy.Begin(Time.time, c);
            var e = b.GoalEnemy!;
            var situation = NativeAdapter.Capture(b, e, true, false);
            r.ContactInstance = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(e);
            r.Known = support.GoalEnemy!.EnemyPosition;
            r.Contact = e.EnemyProfileId;
            r.ContactAt = Time.time - e.TimeSinceLastKnownUpdated;
            r.Support = support.ProfileId;
            r.Reference = support.Position;
            r.Origin = b.Position;
            r.Candidate = r.Sample = 0;
            r.Route = null;
            r.Rejects.Clear();
            RememberCrossfireHandoff(b, r);
            // Detached squad identity: reservations must not expose other groups' hidden positions.
            r.Group = SquadRosterIdentity(b);
            r.Indoor =
                b.Player.Environment == EnvironmentType.Indoor
                || e.EnemyPlayer?.Environment == EnvironmentType.Indoor;
            var t = r.Policy.Trace!;
            t.Contact = r.Contact;
            t.Source = "visible-squadmate-enemy-position";
            t.Support = r.Support;
            t.SupportName = support.Player.Profile.Nickname;
            t.X = r.Known.x;
            t.Y = r.Known.y;
            t.Z = r.Known.z;
            t.ContactAge = e.TimeSinceLastKnownUpdated;
            if (!selected)
            {
                RecordCrossfire(s, true);
                return false;
            }
            if (!BeginEntrance(b, s))
                BuildCrossfireCandidates(b, r);
            RecordCrossfire(s, true);
        }
        if (r.Policy.Trace!.State == "planning" && b.Player.MovementContext.IsGrounded)
        {
            if (r.ProbeFrame != Time.frameCount && doorBudget.TryBegin(Time.frameCount, b))
            {
                r.ProbeFrame = Time.frameCount;
                doorClock.Restart();
                try
                {
                    if (r.Policy.Trace.Entrance != null)
                        ProbeEntrance(b, s);
                    else
                        ProbeCrossfire(b, s);
                }
                finally
                {
                    doorClock.Stop();
                    doorBudget.Charge(doorClock.Elapsed.TotalMilliseconds);
                }
            }
        }
        r.Policy.Applied(r.Layer && (r.ManeuverRole == null || r.ManeuverRole.Confirmed));
        RecordCrossfire(s, false);
        return r.Policy.Active && r.Policy.Trace.State != "planning";
    }

    private bool ContinueCrossfireDecision(
        BotComponent b,
        BotState s,
        ref ECombatDecision result,
        ref bool succeeded
    )
    {
        if (!s.Crossfire.Policy.Active)
            return false;
        if (!RefreshCrossfire(b, s, false) && !CrossfirePreparing(s))
            return false;
        Release(s, "crossfire-layer-request", false, true);
        result = ECombatDecision.Freeze;
        succeeded = true;
        return true;
    }

    private static bool CrossfirePreparing(BotState s) =>
        s.Crossfire.Policy.Active && s.Crossfire.Policy.Trace?.State == "planning";

    internal static bool CrossfireActive(BotOwner owner)
    {
        var p = Instance;
        if (
            p == null
            || !SAINEnableClass.GetSAIN(owner.ProfileId, out var b)
            || b == null
            || !p.states.TryGetValue(b, out var s)
        )
            return false;
        try
        {
            return p.RefreshCrossfire(b, s, false) || CrossfirePreparing(s);
        }
        catch (Exception ex)
        {
            p.CrossfireError(b, s, ex);
            return false;
        }
    }

    internal static void RunCrossfire(BotOwner owner, bool active)
    {
        var p = Instance;
        if (
            p == null
            || !SAINEnableClass.GetSAIN(owner.ProfileId, out var b)
            || b == null
            || !p.states.TryGetValue(b, out var s)
        )
            return;
        try
        {
            var r = s.Crossfire;
            if (!active)
            {
                if (r.Layer)
                    p.EndCrossfire(b, s, "crossfire-layer-released");
                return;
            }
            if (!p.RefreshCrossfire(b, s, false) && !CrossfirePreparing(s))
                return;
            if (!b.Player.MovementContext.IsGrounded)
                return;
            if (!r.Layer)
            {
                if (!p.TakeCrossfireHandoff(b, s))
                {
                    if (r.Policy.Trace?.Handoff?.Reason == "waiting-native-queue")
                        return;
                    p.EndCrossfire(
                        b,
                        s,
                        "crossfire-handoff-" + (r.Policy.Trace?.Handoff?.Reason ?? "unavailable")
                    );
                    return;
                }
                r.Layer = true;
                r.StartWait = Time.time;
                owner.PatrollingData.Pause();
                p.Release(s, "crossfire-layer-active", false, true);
            }
            // Reserve the tactical handoff during preparation, before the outgoing
            // SAIN path moves the origin out of the search. This is not yet movement.
            if (CrossfirePreparing(s))
                return;
            if (r.Policy.Trace!.Trade != null)
            {
                p.ExecuteSquadTrade(b, s);
                if (r.Policy.Active)
                    p.RefreshSquadTrade(b, s);
                return;
            }
            if (r.Policy.Trace!.CoverMate != null)
            {
                p.ExecuteCoverMate(b, s);
                if (r.Policy.Active)
                    p.RefreshCoverMate(b, s);
                return;
            }
            if (p.ExecuteManeuverSupport(b, s))
            {
                r.Policy.Applied(r.Layer && (r.ManeuverRole == null || r.ManeuverRole.Confirmed));
                p.RecordCrossfire(s, false);
                return;
            }
            if (r.Policy.Trace!.Entrance != null)
            {
                p.ExecuteEntrance(b, s);
                if (!r.Policy.Active)
                    return;
            }
            if (!r.Entrance.Opening)
                p.ExecuteCrossfire(b, s);
            p.ConfirmManeuver(b, s);
            r.Policy.Applied(r.Layer && (r.ManeuverRole == null || r.ManeuverRole.Confirmed));
            p.RecordCrossfire(s, false);
        }
        catch (Exception ex)
        {
            p.CrossfireError(b, s, ex);
        }
    }

    internal static bool SteerCrossfire(BotOwner owner)
    {
        var p = Instance;
        if (
            p == null
            || !SAINEnableClass.GetSAIN(owner.ProfileId, out var b)
            || b == null
            || !p.states.TryGetValue(b, out var s)
            || !s.Crossfire.Layer
        )
            return false;
        try
        {
            if (
                (
                    s.Crossfire.Policy.Trace?.Storm != null ? p.StormBlock(b, s)
                    : s.Crossfire.Policy.Trace?.Trade != null ? p.SquadTradeBlock(b, s)
                    : s.Crossfire.Policy.Trace?.CoverMate != null ? p.CoverMateBlock(b, s)
                    : p.CrossfireBlock(b, s)
                ).Length > 0
            )
                return false;
            if (s.Crossfire.ManeuverRole?.Role == "support" && b.GoalEnemy?.IsVisible == true)
            {
                // BotAction's ordinary steering callback performs SAIN's visible
                // aim/fire checks. Do not substitute blind suppression for a visible target.
                p.ConfirmManeuver(b, s, true);
                return false;
            }
            if (s.Crossfire.Policy.Trace?.State == "moving"
                && s.Crossfire.ManeuverRole?.Role is not ("rear" or "support"))
                b.Steering.LookToMovingDirection();
            else
                b.Steering.LookToPoint(
                    (
                        s.Crossfire.Policy.Trace?.Entrance != null ? s.Crossfire.Entrance.Center
                        : s.Crossfire.ManeuverRole?.Role == "rear" ? s.Crossfire.Watch
                        : s.Crossfire.Known
                    )
                        + Vector3.up * 1.3f
                );
            p.ConfirmManeuver(b, s, true);
            p.ConfirmCoverMate(b, s);
            p.ConfirmSquadTrade(b, s);
            return true;
        }
        catch (Exception ex)
        {
            p.CrossfireError(b, s, ex);
            return false;
        }
    }

    private void EndCrossfire(BotComponent b, BotState s, string reason)
    {
        var r = s.Crossfire;
        if (
            r.Policy.Trace == null
            || r.Policy.Trace.Episode == r.FinalizedEpisode && !r.Layer && r.Path == null
        )
            return;
        r.Peek?.Reset();
        EndManeuver(s, reason);
        EndCoverMate(s, reason);
        EndSquadTrade(s, reason);
        if (r.Policy.Trace.Entrance != null)
        {
            r.Entrance.Policy.End(Time.time, r.ContactAt);
            ReleaseEntrance(s);
        }
        r.Policy.End(Time.time, config?.Crossfire ?? new(), reason);
        r.Layer = false;
        crossfireClaims.Release(s.Id);
        if (r.OwnPose && Math.Abs(b.Mover.Pose.PoseValue.TargetValue - r.AppliedPose) < .01)
            b.Mover.SetTargetPose(r.OldPose);
        r.OwnPose = false;
        try
        {
            StopCrossfireRoute(b, r);
        }
        catch (Exception ex)
        {
            Logger.LogWarning("S01 route release: " + ex.GetBaseException().Message);
        }
        finally
        {
            r.Route = null;
            r.HandoffPath = null;
            r.HandoffQueued = null;
            r.HandoffWait = -1;
            r.AirborneSince = -1;
            r.Samples.Clear();
            r.Candidates.Clear();
            RecordCrossfire(s, true);
            r.Entrance.Drop();
            r.Maneuver = null;
            r.ManeuverRole = null;
            r.Storm.Plan = null;
            r.Storm.Member = null;
        }
    }

    private void CrossfireError(BotComponent b, BotState s, Exception ex)
    {
        if (s.Crossfire.Fault)
            return;
        s.Crossfire.Fault = true;
        EndCrossfire(b, s, "crossfire-adapter-error");
        Logger.LogWarning("S01 yielded: " + ex.GetBaseException().Message);
    }

    private void RecordCrossfire(BotState s, bool force)
    {
        var r = s.Crossfire;
        var t = r.Policy.Trace;
        if (t == null)
            return;
        if (t.State == "ended")
            r.FinalizedEpisode = t.Episode;
        // Detailed member snapshots and labels are not needed on every steering
        // callback. State transitions still publish immediately.
        if (
            !force
            && t.State == r.TraceState
            && t.Applied == r.TraceApplied
            && Time.time < r.NextTraceAt
        )
            return;
        r.TraceState = t.State;
        r.TraceApplied = t.Applied;
        r.NextTraceAt = Time.time + .1f;
        if (r.Maneuver != null && r.ManeuverRole != null)
            t.Maneuver = r.Maneuver.Trace(s.ProfileId, Time.time, config!.SquadManeuver);
        if (r.Storm.Plan != null && r.Storm.Member != null)
            t.Storm = r.Storm.Plan.Trace(s.ProfileId, Time.time, config!.SquadStorm);
        string label =
            t.Storm != null ? "StormMoving"
            : t.Trade != null
                ? "Trade"
                    + (
                        t.State == "moving"
                            ? (t.Trade.Choice == "push" ? "Pushing" : "Moving")
                            : "Watching"
                    )
            : t.CoverMate != null ? "CoverMate" + (t.State == "moving" ? "Moving" : "Holding")
            : t.Maneuver != null
                ? "Maneuver"
                    + (
                        r.ManeuverRole?.Role == "rear" ? "Rear"
                        : r.ManeuverRole?.Role == "support"
                            ? (
                                Time.time - r.ManeuverRole.LastShot < .3
                                    ? "Suppressing"
                                    : "Supporting"
                            )
                        : t.State == "moving" ? "Moving"
                        : "Holding"
                    )
            : t.Entrance == null ? char.ToUpperInvariant(t.State[0]) + t.State.Substring(1)
            : r.Entrance.Entering ? "EntranceEntering"
            : t.State == "moving" ? "EntranceMoving"
            : "EntranceWatching";
        Api.Decisions.SetCrossfire(s.ProfileId, t.Applied ? "COMET:Crossfire" + label : "");
        string key =
            t.Episode
            + ":"
            + t.State
            + ":"
            + t.Reason
            + ":"
            + t.Execution
            + ":"
            + t.Applied
            + ":"
            + t.Entrance?.State
            + ":"
            + t.Entrance?.SupportStatus
            + ":"
            + t.Entrance?.EntryReason
            + ":"
            + t.Maneuver?.State
            + ":"
            + t.Maneuver?.SupportReady
            + ":"
            + t.Maneuver?.FireState
            + ":"
            + t.CoverMate?.Status
            + ":"
            + t.CoverMate?.Busy
            + ":"
            + t.CoverMate?.WeaponReady
            + ":"
            + t.CoverMate?.Confirmed
            + ":"
            + t.Trade?.Choice
            + ":"
            + t.Trade?.Status
            + ":"
            + t.Trade?.Confirmed
            + ":"
            + t.Storm?.State
            + ":"
            + t.Storm?.Status
            + ":"
            + t.Storm?.Ready
            + ":"
            + t.Storm?.Reason;
        if (!force && key == r.Key && (!t.Applied || Time.time < r.LogAt))
            return;
        r.Key = key;
        r.LogAt = Time.time + .5f;
        var json = Diagnostics.Serialize(
            new
            {
                schema = 1,
                session,
                sequence = ++sequence,
                feature = t.Storm != null ? "S06"
                : t.Trade != null ? "S05"
                : t.CoverMate != null ? "S04"
                : t.Maneuver != null ? "S03"
                : t.Entrance == null ? "S01"
                : "S02",
                stage = "evaluated",
                decision = "Crossfire",
                reason = t.Reason,
                realtime = Time.realtimeSinceStartup,
                utc = DateTime.UtcNow.ToString("O"),
                configRevision = config?.Revision ?? 0,
                profileId = s.ProfileId,
                name = s.Name,
                botInstance = s.Id,
                crossfire = t,
                squadStormConfig = t.Storm != null ? config?.SquadStorm : null,
                squadTradeConfig = t.Trade != null ? config?.SquadTrade : null,
                coverMateConfig = t.CoverMate != null ? config?.CoverMate : null,
                squadManeuverConfig = t.Maneuver != null ? config?.SquadManeuver : null,
                crossfireConfig = config?.Crossfire,
                secondaryEntranceConfig = t.Entrance != null ? config?.SecondaryEntrance : null,
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
