using System;
using System.Linq;
using Comet.Core;
using EFT;
using Newtonsoft.Json;
using SAIN.Components;
using SAIN.Components.PlayerComponentSpace;
using SAIN.Models.Enums;
using SAIN.Preset.Shared.Enums;
using SAIN.SAINComponent.Classes.EnemyClasses;
using SAIN.SAINComponent.Classes.Mover;
using UnityEngine;
using UnityEngine.AI;

namespace Comet.Client;

internal sealed class CornerPeekRuntime
{
    internal readonly CornerPeek Policy = new();
    internal BotComponent? Owner;
    internal IBotPathData? Path;
    internal Vector3 Goal,
        Origin,
        Near,
        Far,
        Direction,
        Previous,
        RouteFrom;
    internal float PathAt,
        LastCallback,
        NextSafety,
        LastSafety,
        LastSpeedAt,
        Speed,
        LogAt = -1000;
    internal int TargetInstance,
        Candidate,
        PlanStage;
    internal bool Fault,
        OwnEmpty,
        OwnSpeed,
        Hidden,
        Open,
        LaneSafe;
    internal float PreviousSpeed,
        AppliedSpeed;
    internal string LogKey = "";

    internal void Drop()
    {
        Owner = null;
        Path = null;
        OwnEmpty = OwnSpeed = false;
    }
}

public sealed partial class Plugin
{
    private bool TryCornerPeek(BotComponent b, BotState s, CombatStepFrame f)
    {
        var r = s.CornerPeek;
        var c = config!.CornerPeek;
        if (r.Fault || !c.Enabled || Time.time < r.Policy.NextAttempt)
            return false;
        var personality = b.Info.Personality.ToString();
        bool bold =
            personality is "Chad" or "GigaChad"
            || personality == "Normal" && CornerBold(b.ProfileId);
        if (
            !r.Policy.Begin(
                f,
                s.CombatStep.Policy.Trace!.Episode,
                c,
                jumpRandom.NextDouble(),
                jumpRandom.NextDouble(),
                jumpRandom.NextDouble(),
                jumpRandom.NextDouble(),
                bold
            )
        )
        {
            RecordCornerPeek(s);
            return false;
        }
        // Take the one already rolled M01 opportunity; M02 is not started for this sequence.
        EndCombatLean(s, "lean-preserve-corner-peek");
        EndCombatStep(s, "movement-delegated-to-corner-peek");
        r.Owner = b;
        r.Origin = r.Previous = b.Position;
        r.Candidate = r.PlanStage = 0;
        r.Speed = 0;
        r.LastSpeedAt = r.LastCallback = Time.time;
        r.TargetInstance = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(b.GoalEnemy);
        r.Path = null;
        r.OwnEmpty = false;
        r.LastSafety = -1000;
        r.NextSafety = 0;
        if (!StopCornerRoute(s, true))
        {
            EndCornerPeek(s, "corner-native-route-taken-over");
            return true;
        }
        RecordCornerPeek(s, true);
        return true;
    }

    private static bool CornerBold(string id)
    {
        unchecked
        {
            int hash = 17;
            foreach (char c in id)
                hash = hash * 31 + c;
            return (hash & 1) == 0;
        }
    }

    private string CornerGate(BotComponent b, BotState s, CombatStepFrame f)
    {
        var r = s.CornerPeek;
        var t = r.Policy.Trace!;
        if (
            config == null
            || !config.CornerPeek.Enabled
            || !config.CombatStep.Enabled
            || !f.Enabled
            || !f.Pmc
            || !f.Authority
            || !f.Active
            || f.Ghost
        )
            return "corner-out-of-scope";
        if (b.GoalEnemy == null || f.Target != t.Target || f.Action != t.Action)
            return "corner-native-action-or-contact-changed";
        if (f.Busy || f.Immobile)
            return "corner-native-priority";
        return "";
    }

    private CombatStepFrame CornerFrame(BotComponent b, BotState s)
    {
        var f = CombatStepFrame(b, s);
        // Never use a hidden contact's live transform or refreshed shared position to aim this maneuver.
        if (!f.Visible)
        {
            f.Known = s.CornerPeek.Policy.Known;
            f.Distance = (f.Position - f.Known).Length;
        }
        return f;
    }

    private bool KeepCornerPeek(
        BotComponent b,
        BotState s,
        Enemy enemy,
        ref ECombatDecision result,
        ref bool succeeded
    )
    {
        var r = s.CornerPeek;
        if (!r.Policy.Active || enemy.IsVisible)
            return false;
        try
        {
            RefreshCornerPeek(b, s, false);
            if (!r.Policy.MayKeep(Time.time, config!.CornerPeek))
                return false;
            // Only the intentional short occlusion retains the existing native shooting action.
            // Self/squad decisions are still selected by SAIN's decision manager, outside this hook.
            result =
                r.Policy.Trace!.Action == "DogFight"
                    ? ECombatDecision.DogFight
                    : ECombatDecision.StandAndShoot;
            succeeded = true;
            b.Steering.LookToPoint(VP(r.Policy.Known) + Vector3.up * 1.3f);
            return true;
        }
        catch (Exception ex)
        {
            CornerError(s, ex);
            return false;
        }
    }

    private void RefreshCornerPeek(BotComponent b, BotState s, bool mayPlan)
    {
        var r = s.CornerPeek;
        if (!r.Policy.Active)
            return;
        if (config == null || b == null || b.IsDead)
        {
            EndCornerPeek(s, "corner-bot-ended");
            return;
        }
        var f = CornerFrame(b, s);
        string why = CornerGate(b, s, f);
        if (why.Length > 0)
        {
            EndCornerPeek(s, why);
            return;
        }
        if (Time.time > r.LastSpeedAt)
        {
            r.Speed = (b.Position - r.Previous).magnitude / (Time.time - r.LastSpeedAt);
            r.Previous = b.Position;
            r.LastSpeedAt = Time.time;
        }
        r.Open = b.GoalEnemy?.IsVisible == true;
        r.Hidden = !r.Open;
        bool arrived = r.Path != null && Vector3.Distance(b.Position, r.Goal) <= .4f;
        bool valid =
            r.Path == null
                ? (!r.OwnEmpty || b.Mover.ActivePath == null)
                : OwnCornerPath(r)
                    || b.Mover.ActivePath == null
                        && r.Path.Status == EBotMoveStatus.Complete
                        && arrived;
        // Ownership and the hard deadline cannot wait for a geometry budget slot.
        if (!valid || Time.time >= r.Policy.Deadline)
        {
            EndCornerPeek(
                s,
                !valid ? "corner-native-route-taken-over" : "corner-sequence-time-limit"
            );
            return;
        }
        var before = r.Policy.Trace.State;
        if (
            !r.Policy.Observe(
                new()
                {
                    Combat = f,
                    RouteValid = valid,
                    AtGoal = arrived,
                    Speed = r.Speed,
                    Grounded = f.Grounded,
                    SinceHit = b.Medical.TimeSinceShot,
                    GeometryFresh = true,
                    LaneSafe = r.LaneSafe,
                    Hidden = r.Hidden,
                    Open = r.Open,
                },
                config.CornerPeek,
                ""
            )
        )
        {
            FinishCornerPeek(s);
            return;
        }
        if (r.Policy.Trace.State == "planning")
        {
            if (mayPlan && doorBudget.TryBegin(Time.frameCount, b))
            {
                doorClock.Restart();
                try
                {
                    PlanCornerPeek(b, s);
                }
                finally
                {
                    doorClock.Stop();
                    doorBudget.Charge(doorClock.Elapsed.TotalMilliseconds);
                }
            }
            else if (mayPlan)
                r.Policy.Preparation.Defer(Time.time, Time.deltaTime);
            RecordCornerPeek(s);
            return;
        }
        if (r.Policy.Holding)
        {
            if (!StopCornerRoute(s))
            {
                EndCornerPeek(s, "corner-route-taken-over");
                return;
            }
            if (r.OwnEmpty)
                b.Player.Move(Vector2.zero);
        }
        if (!f.Visible)
            b.Steering.LookToPoint(VP(r.Policy.Known) + Vector3.up * 1.3f);
        if (mayPlan && r.Policy.WantsRoute)
        {
            if (!doorBudget.TryBegin(Time.frameCount, b))
            {
                RecordCornerPeek(s);
                return;
            }
            doorClock.Restart();
            try
            {
                RequestCornerRoute(b, s);
            }
            finally
            {
                doorClock.Stop();
                doorBudget.Charge(doorClock.Elapsed.TotalMilliseconds);
            }
        }
        // Only a real outward speed can launch the one permitted jump. SAIN retains all firing rules.
        if (
            r.Policy.Trace.State == "peek"
            && r.Policy.Trace.Style == "jump"
            && r.Policy.Trace.Jumps < 3
        )
        {
            var direction = r.Goal - b.Position;
            direction.y = 0;
            var velocity = b.Player.Velocity;
            velocity.y = 0;
            if (
                r.Policy.MayJump(
                    Time.time,
                    Vector3.Dot(velocity, direction.normalized),
                    direction.magnitude,
                    f.Grounded,
                    b.Player.MovementContext.CanJump,
                    b.Player.Physical.Stamina.NormalValue * 100
                )
            )
            {
                if (doorBudget.TryBegin(Time.frameCount, b))
                {
                    doorClock.Restart();
                    try
                    {
                        var lane = CornerJumpSafe(b, direction) ? "" : "corner-jump-lane-blocked";
                        if (lane.Length == 0)
                            r.Policy.JumpAccepted(
                                ControlledJump(
                                    b,
                                    s,
                                    "M03",
                                    "corner-peek",
                                    direction,
                                    r.Goal,
                                    1.1,
                                    0,
                                    true
                                ),
                                jumpRandom.NextDouble()
                            );
                        else
                            r.Policy.Fallback(lane);
                    }
                    finally
                    {
                        doorClock.Stop();
                        doorBudget.Charge(doorClock.Elapsed.TotalMilliseconds);
                    }
                }
            }
        }
        RecordCornerPeek(s, before != r.Policy.Trace.State);
    }

    private void PlanCornerPeek(BotComponent b, BotState s)
    {
        var r = s.CornerPeek;
        var p = r.Policy;
        var t = p.Trace!;
        var c = config!.CornerPeek;
        var velocity = b.Player.Velocity;
        velocity.y = 0;
        // The first callback can precede a displacement sample. Native velocity still carries inertia.
        if (
            !p.SettleOrigin(
                DP(b.Position),
                Math.Max(r.Speed, velocity.magnitude),
                b.Player.MovementContext.IsGrounded,
                Time.time
            )
        )
        {
            if (!p.Active)
                FinishCornerPeek(s);
            return;
        }
        r.Origin = VP(p.Exposed);
        var known = VP(p.Known);
        if (r.PlanStage == 0)
        {
            if (r.Candidate >= 2)
            {
                EndCornerPeek(s, "corner-no-local-cover");
                return;
            }
            var forward = known - r.Origin;
            forward.y = 0;
            if (forward.sqrMagnitude < .01f)
            {
                EndCornerPeek(s, "corner-no-bearing");
                return;
            }
            int i = r.Candidate++;
            float distance = .8f;
            var direction = Vector3.Cross(Vector3.up, forward.normalized) * (i % 2 == 0 ? 1 : -1);
            var raw = r.Origin + direction * distance;
            if (
                !CornerRoute(b, r.Origin, raw, c.MaxStep, out var end, out _, out var why)
                || !Physics.Linecast(
                    known + Vector3.up * 1.5f,
                    end + Vector3.up * 1.2f,
                    LayersMaskController.HighPolyWithTerrainMask
                )
                || (why = "").Length > 0
            )
            {
                p.Alternative("cover " + i + ": " + (why.Length > 0 ? why : "body not protected"));
                RecordCornerPeek(s);
                return;
            }
            r.Near = end;
            r.Direction = direction;
            r.Candidate = 0;
            r.PlanStage = 1;
            return;
        }
        string style = t.Style;
        Vector3 hidden = r.Near;
        if (style == "run-by")
        {
            if (r.Candidate < 3)
            {
                float distance = new[] { 3.5f, 2.5f, 4.5f }[r.Candidate++];
                var raw = r.Origin - r.Direction * distance;
                if (
                    CornerRoute(b, r.Origin, raw, 4.7, out var far, out _, out var why)
                    && Physics.Linecast(
                        known + Vector3.up * 1.5f,
                        far + Vector3.up * 1.2f,
                        LayersMaskController.HighPolyWithTerrainMask
                    )
                    && CornerOpen(b, (r.Origin + far) * .5f, known)
                )
                {
                    hidden = r.Far = far;
                    r.PlanStage = 2;
                }
                else
                {
                    p.Alternative("run-by candidate " + r.Candidate + ": no safe covered crossing");
                    return;
                }
            }
            else
            {
                p.Fallback("no safe far cover or sprint resources");
                style = "step";
            }
        }
        if (style == "jump")
        {
            // Jump only where the upper lane is free and the raised angle actually reduces occlusion.
            if (!CornerJumpSafe(b, r.Origin - r.Near))
            {
                p.Fallback("no useful safe jump corridor or stamina");
                style = "step";
            }
        }
        if (!StopCornerRoute(s))
        {
            EndCornerPeek(s, "corner-native-route-taken-over");
            return;
        }
        if (!p.Plan(DP(hidden), style, Time.time))
        {
            EndCornerPeek(s, "corner-plan-expired");
            return;
        }
        r.LaneSafe = true;
        r.Open = false;
        r.Hidden = false;
        r.LastSafety = Time.time;
        r.NextSafety = Time.time;
        RecordCornerPeek(s, true);
    }

    private bool CornerRoute(
        BotComponent b,
        Vector3 start,
        Vector3 raw,
        double radius,
        out Vector3 end,
        out NavMeshPath path,
        out string why,
        float pose = 1
    )
    {
        end = raw;
        path = new();
        why = "corner-no-local-navmesh";
        if (!NavMesh.SamplePosition(raw, out var sample, .5f, NavMesh.AllAreas))
            return false;
        end = sample.position;
        start = RemixMovementOrigin(b, start, preserveOnMesh: true);
        if (NavMesh.Raycast(start, end, out _, NavMesh.AllAreas))
        {
            why = "corner-navmesh-blocked";
            return false;
        }
        NavMesh.CalculatePath(start, end, NavMesh.AllAreas, path);
        why = "";
        return true;
    }

    private static bool CornerJumpSafe(BotComponent b, Vector3 direction)
    {
        direction.y = 0;
        direction.Normalize();
        var p = b.Position;
        var mask = LayersMaskController.HighPolyWithTerrainMask;
        return !NavMesh.Raycast(p, p + direction * 1.6f, out _, NavMesh.AllAreas)
            && !Physics.Raycast(p + Vector3.up * 1.7f, Vector3.up, .7f, mask)
            && !Physics.Raycast(p + Vector3.up * .5f, direction, 1.8f, mask)
            && !Physics.Raycast(p + Vector3.up * 1.2f, direction, 1.8f, mask);
    }

    private static bool CornerOpen(BotComponent b, Vector3 position, Vector3 known)
    {
        var offset = position - b.Position;
        var aim = known + Vector3.up * 1.3f;
        return ClearAmbushRay(position + Vector3.up * 1.2f, aim);
    }

    private string CornerSafety(BotComponent b, BotState s)
    {
        var r = s.CornerPeek;
        var p = r.Policy;
        var hidden = VP(p.HiddenPoint);
        var exposed = VP(p.Exposed);
        var known = VP(p.Known);
        // Full ground corridor remains checked during flight; actual motion has its separate vertical leash.
        string why = RunVolume(b, exposed, hidden, p.Trace!.Style == "jump");
        if (why.Length > 0)
            return why;
        if (
            !CombatStep.UsefulCover(CombatProtection(hidden, known))
            || !CornerOpen(b, exposed, known)
        )
            return "corner-protection-or-firing-angle-lost";
        r.Hidden = CombatStep.UsefulCover(CombatProtection(b.Position, known));
        r.Open = CornerOpen(b, b.Position, known);
        return "";
    }

    private void RequestCornerRoute(BotComponent b, BotState s)
    {
        var r = s.CornerPeek;
        var p = r.Policy;
        bool outward = p.Trace!.State == "peek-pending";
        bool run = p.Trace.State == "crossing-pending";
        var goal = VP(outward ? p.Exposed : p.HiddenPoint);
        if (
            !CornerRoute(
                b,
                b.Position,
                goal,
                run ? 4.7 : config!.CornerPeek.MaxStep + .25,
                out var endpoint,
                out var route,
                out var why
            )
        )
        {
            EndCornerPeek(s, why.Length > 0 ? why : "corner-route-no-longer-safe");
            return;
        }
        if (!StopCornerRoute(s))
        {
            EndCornerPeek(s, "corner-native-route-taken-over");
            return;
        }
        bool accepted = run
            ? b.Mover.RunToPointByWay(route, true, .1f, ESprintUrgency.High, false)
            : b.Mover.WalkToPointByWay(route, true, .1f, false);
        r.Path = b.Mover.ActivePath;
        r.Goal = endpoint;
        r.PathAt = r.Path?.TimeStarted ?? -1;
        r.RouteFrom = b.Position;
        r.OwnEmpty = false;
        if (!accepted || !OwnCornerPath(r) || !p.Request(Time.time))
        {
            EndCornerPeek(s, "corner-movement-request-unconfirmed");
            return;
        }
        if (!r.OwnSpeed)
        {
            r.PreviousSpeed = (float)
                combatNativeSpeed.GetValue(b.PlayerComponent.CharacterController);
            r.AppliedSpeed = Mathf.Clamp01(b.Info.FileSettings.Move.STRAFE_SPEED);
            r.OwnSpeed = true;
        }
        if (!run)
            b.Mover.SetTargetMoveSpeed(r.AppliedSpeed);
        r.LastSafety = Time.time;
        r.NextSafety = Time.time + .1f;
        RecordCornerPeek(s, true);
    }

    private static bool OwnCornerPath(CornerPeekRuntime r) =>
        r.Owner != null
        && r.Path != null
        && ReferenceEquals(r.Owner.Mover.ActivePath, r.Path)
        && r.Path.TimeStarted == r.PathAt
        && Vector3.Distance(r.Path.Destination, r.Goal) < .02f;

    private bool StopCornerRoute(BotState s, bool initial = false)
    {
        var r = s.CornerPeek;
        var b = r.Owner;
        if (b == null)
            return false;
        if (initial)
        {
            if (!ReleaseShootingRoute(b, out var released))
                return false;
            if (released > 0)
                r.Policy.Trace!.Alternatives = r
                    .Policy.Trace.Alternatives.Concat(
                        new[] { "native shooting handoff: " + released + " observed legs released" }
                    )
                    .TakeLast(6)
                    .ToArray();
            r.Path = null;
            r.OwnEmpty = true;
            return true;
        }
        if (QueuedCombatMovement(b))
            return false;
        var active = b.Mover.ActivePath;
        if (active != null)
        {
            if (!OwnCornerPath(r) && !initial)
                return false;
            if (active is not BotPathDataManual path)
                return false;
            // Initial takeover is only called from the same checked StandAndShoot/DogFight callback.
            path.RequestEndSprint(ESprintUrgency.None, "COMET corner hold");
            combatStopPath.Invoke(path, new object[] { false, "COMET corner hold" });
            if (b.Mover.ActivePath != null)
                return false;
        }
        r.Path = null;
        r.OwnEmpty = true;
        return true;
    }

    private void EndCornerPeek(BotState s, string why)
    {
        if (!s.CornerPeek.Policy.Active)
            return;
        s.CornerPeek.Policy.End(why, Time.time);
        FinishCornerPeek(s);
    }

    private void FinishCornerPeek(BotState s)
    {
        var r = s.CornerPeek;
        try
        {
            bool owned = OwnCornerPath(r);
            var b = r.Owner;
            if (owned && r.Path is BotPathDataManual path)
            {
                path.RequestEndSprint(ESprintUrgency.None, "COMET corner ended");
                combatStopPath.Invoke(path, new object[] { false, "COMET corner ended" });
            }
            if (
                b != null
                && !b.IsDead
                && (owned || r.OwnEmpty)
                && b.Mover.ActivePath == null
                && !QueuedCombatMovement(b)
            )
                b.Player.Move(Vector2.zero);
            if (
                r.OwnSpeed
                && b != null
                && !b.IsDead
                && Math.Abs(
                    (float)combatNativeSpeed.GetValue(b.PlayerComponent.CharacterController)
                        - r.AppliedSpeed
                ) < .001f
            )
                b.Mover.SetTargetMoveSpeed(r.PreviousSpeed);
        }
        finally
        {
            s.CombatStep.Policy.DeferUntil(r.Policy.NextAttempt);
            RecordCornerPeek(s, true);
            r.Drop();
        }
    }

    private void CornerError(BotState s, Exception ex)
    {
        s.CornerPeek.Fault = true;
        EndCornerPeek(s, "corner-adapter-error");
        Logger.LogWarning("M03 yielded: " + ex.GetBaseException().Message);
    }

    private void RecordCornerPeek(BotState s, bool force = false)
    {
        var r = s.CornerPeek;
        var t = r.Policy.Trace;
        if (t == null)
            return;
        string key =
            t.Episode
            + ":"
            + t.State
            + ":"
            + t.Reason
            + ":"
            + t.Jumps
            + ":"
            + t.Alternatives.Length;
        if (!force && key == r.LogKey && (!r.Policy.Active || Time.time - r.LogAt < .1f))
            return;
        r.LogKey = key;
        r.LogAt = Time.time;
        var json = Diagnostics.Serialize(
            new
            {
                schema = 1,
                session,
                sequence = ++sequence,
                feature = "M03",
                stage = "evaluated",
                decision = "CornerPeek",
                reason = t.Reason,
                realtime = Time.realtimeSinceStartup,
                utc = DateTime.UtcNow.ToString("O"),
                configRevision = config?.Revision ?? 0,
                planningWaitSeconds = r.Policy.Preparation.Seconds,
                profileId = s.ProfileId,
                name = s.Name,
                botInstance = s.Id,
                cornerPeek = t,
                cornerPeekConfig = config?.CornerPeek,
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
