using System;
using System.Linq;
using Comet.Core;
using EFT;
using EFT.InventoryLogic;
using HarmonyLib;
using Newtonsoft.Json;
using SAIN.Components;
using SAIN.Components.PlayerComponentSpace;
using SAIN.Layers;
using SAIN.Models.Enums;
using SAIN.Preset.Shared.Enums;
using SAIN.SAINComponent.Classes.Mover;
using UnityEngine;
using UnityEngine.AI;

namespace Comet.Client;

internal sealed class CombatStepRuntime
{
    internal readonly CombatStep Policy = new();
    internal readonly FirePause Pause = new();
    internal Vector3 SpeedPosition;
    internal float SpeedAt,
        MeasuredSpeed = 999,
        PauseLogAt = -1000;
    internal bool PauseOwnsEmpty;
    internal string PauseLogKey = "";
    internal BotComponent? Owner;
    internal IBotPathData? Path;
    internal Vector3 Goal,
        StepOrigin;
    internal float PathStarted,
        LastSafety,
        NextSafety,
        LastCallback,
        LogAt = -1000;
    internal int TargetInstance,
        Candidate,
        Side;
    internal bool Fault;
    internal bool OwnSpeed;
    internal float PreviousSpeed,
        AppliedSpeed;
    internal string LogKey = "";

    internal void Drop()
    {
        Owner = null;
        Path = null;
        Candidate = 0;
        OwnSpeed = false;
        PauseOwnsEmpty = false;
    }
}

public sealed partial class Plugin
{
    private static System.Reflection.FieldInfo combatNativeSpeed = null!;
    private static AccessTools.FieldRef<SAINMoverClass, BotPathDataManual> combatPathOne = null!,
        combatPathTwo = null!;
    private static System.Reflection.MethodInfo combatStopPath = null!;

    private static void PatchCombatStep(Harmony h)
    {
        combatStopPath = AccessTools.Method(
            typeof(BotPathDataManual),
            "Stop",
            new[] { typeof(bool), typeof(string) }
        ) ?? throw new MissingMethodException("M01 native path completion");
        combatPathOne = AccessTools.FieldRefAccess<SAINMoverClass, BotPathDataManual>(
            "_preparedPath1"
        );
        combatPathTwo = AccessTools.FieldRefAccess<SAINMoverClass, BotPathDataManual>(
            "_preparedPath2"
        );
        foreach (var name in new[] { "StandAndShootAction", "DogFightAction" })
        {
            var type = typeof(BotComponent).Assembly.GetType(
                "SAIN.Layers.Combat.Solo." + name,
                true
            );
            h.Patch(
                AccessTools.Method(type, "Update")
                    ?? throw new MissingMethodException("M01 " + name + ".Update"),
                prefix: new HarmonyMethod(typeof(Plugin), nameof(CombatStepAction))
            );
            h.Patch(
                AccessTools.Method(type, "Stop")
                    ?? throw new MissingMethodException("M01 " + name + ".Stop"),
                prefix: new HarmonyMethod(typeof(Plugin), nameof(CombatStepStopped))
            );
        }
        h.Patch(
            AccessTools.Method(typeof(BotPathDataManual), nameof(BotPathDataManual.TickPath)),
            prefix: new HarmonyMethod(typeof(Plugin), nameof(CombatStepPathTick))
        );
        h.Patch(
            AccessTools.Method(typeof(PlayerComponent), nameof(PlayerComponent.OnMakingShot)),
            postfix: new HarmonyMethod(typeof(Plugin), nameof(FirePauseShot))
        );
        var movement = typeof(BotComponent).Assembly.GetType(
            "SAIN.SAINComponent.Classes.PlayerMovementController"
        )
            ?? typeof(BotComponent)
                .Assembly.GetTypes()
                .Single(t => t.Name == "PlayerMovementController");
        combatNativeSpeed = AccessTools.Field(movement, "_targetMoveSpeed")
            ?? throw new MissingFieldException("M01 native target speed");
        if (combatNativeSpeed.FieldType != typeof(float))
            throw new MissingFieldException("M01 native target speed type");
        h.Patch(
            AccessTools.Method(movement, "SetTargetMoveDirection")
                ?? throw new MissingMethodException("M01 native movement input"),
            prefix: new HarmonyMethod(typeof(Plugin), nameof(CombatStepArrival))
        );
    }

    private static bool CombatStepAction(BotAction __instance)
    {
        var p = Instance;
        var b = __instance.Bot;
        if (p?.config == null || !p.states.TryGetValue(b, out var s) || s.CombatStep.Fault)
            return true;
        try
        {
            var r = s.CombatStep;
            r.LastCallback = Time.time;
            if (s.CornerPeek.Policy.Active)
            {
                s.CornerPeek.LastCallback = Time.time;
                p.RefreshCornerPeek(b, s, true);
                return !s.CornerPeek.Policy.Active;
            }
            CombatStepFrame? initialFrame = null;
            if (!r.Policy.Active)
            {
                if (
                    !p.config.Enabled
                    || !p.config.CombatStep.Enabled
                    || Time.time < r.Policy.NextAttempt
                    || !NativeAdapter.IsSupportedAi(b)
                    || !p.Authority
                    || p.Ghost(b.ProfileId)
                )
                    return true;
                var f = initialFrame = p.CombatStepFrame(b, s);
                if (
                    !r.Policy.Begin(
                        f,
                        p.config.CombatStep,
                        p.jumpRandom.NextDouble(),
                        p.jumpRandom.NextDouble(),
                        p.jumpRandom.NextDouble()
                    )
                )
                {
                    p.RecordCombatStep(s);
                    return true;
                }
                r.Owner = b;
                r.TargetInstance = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(
                    b.GoalEnemy
                );
                if (p.TryCornerPeek(b, s, f))
                {
                    p.RefreshCornerPeek(b, s, true);
                    return !s.CornerPeek.Policy.Active;
                }
                r.Candidate = 0;
                r.Side = p.jumpRandom.NextDouble() < .5 ? -1 : 1;
                r.SpeedAt = Time.time;
                r.SpeedPosition = b.Position;
                r.MeasuredSpeed = 999;
                r.Pause.Begin(
                    r.Policy.Trace!.Episode,
                    f.Target,
                    Time.time,
                    p.jumpRandom.NextDouble(),
                    p.jumpRandom.NextDouble(),
                    p.config.FirePause
                );
                p.RecordFirePause(s);
            }
            p.RefreshCombatStep(b, s, true, initialFrame);
            return !r.Policy.Active;
        }
        catch (Exception ex)
        {
            if (s.CornerPeek.Policy.Active)
                p.CornerError(s, ex);
            else
                p.CombatStepError(s, ex);
            return true;
        }
    }

    private static void CombatStepStopped(BotAction __instance)
    {
        var p = Instance;
        if (p != null && p.states.TryGetValue(__instance.Bot, out var s))
        {
            p.EndCombatStep(s, "movement-native-action-ended");
            p.EndCornerPeek(s, "corner-native-action-ended");
            p.EndCombatLean(s, "lean-native-action-ended");
        }
    }

    private static bool CombatStepPathTick(BotPathDataManual __instance)
    {
        var p = Instance;
        if (
            p == null
            || __instance.PathFinder is not SAINMoverClass mover
            || !p.states.TryGetValue(mover.Bot, out var s)
        )
            return true;
        if (s.CoverFeint.Policy.Active && OwnFeintRoute(s.CoverFeint, __instance))
        {
            try
            {
                p.RefreshFeint(mover.Bot, s, false);
            }
            catch (Exception ex)
            {
                p.FeintError(s, ex);
            }
            return ReferenceEquals(mover.ActivePath, __instance);
        }
        if (s.HoldAngle.Policy.Active && ReferenceEquals(s.HoldAngle.Path, __instance))
        {
            try
            {
                p.SampleHoldAngle(mover.Bot, s);
            }
            catch (Exception ex)
            {
                p.HoldAngleError(s, ex);
            }
            return ReferenceEquals(mover.ActivePath, __instance);
        }
        if (s.CornerChase.Policy.Active && ReferenceEquals(s.CornerChase.Path, __instance))
        {
            try
            {
                p.RefreshChase(mover.Bot, s, false);
            }
            catch (Exception ex)
            {
                p.ChaseError(s, ex);
            }
            return ReferenceEquals(mover.ActivePath, __instance);
        }
        if (s.CornerPeek.Policy.Active && ReferenceEquals(s.CornerPeek.Path, __instance))
        {
            try
            {
                p.RefreshCornerPeek(mover.Bot, s, false);
            }
            catch (Exception ex)
            {
                p.CornerError(s, ex);
            }
            return ReferenceEquals(mover.ActivePath, __instance);
        }
        var r = s.CombatStep;
        if (!r.Policy.Active || !ReferenceEquals(r.Path, __instance))
            return true;
        try
        {
            p.RefreshCombatStep(mover.Bot, s, false);
        }
        catch (Exception ex)
        {
            p.CombatStepError(s, ex);
        }
        // Completion clears the old path's corners. Never tick that cleared object after yielding.
        return ReferenceEquals(mover.ActivePath, __instance);
    }

    private static void CombatStepArrival(PlayerComponent playerComp, ref float START_SLOW_DIST)
    {
        var p = Instance;
        var b = playerComp.BotComponent;
        if (
            p != null
            && b != null
            && p.states.TryGetValue(b, out var s)
            && (
                s.CoverFeint.Policy.Active && OwnFeintRoute(s.CoverFeint, b.Mover.ActivePath)
                || s.HoldAngle.Policy.Active && OwnHoldPath(s.HoldAngle)
                || s.CombatStep.Policy.Active && OwnCombatStep(s.CombatStep)
                || s.CornerPeek.Policy.Active && OwnCornerPath(s.CornerPeek)
            )
        )
            START_SLOW_DIST = .15f; // Short native route: brake at the real endpoint, not 0.85 m before a sub-metre step.
    }

    private enum CombatFrameUse { Movement, Lean, AngleScope }

    private CombatStepFrame CombatStepFrame(BotComponent b, BotState s, CombatFrameUse use = CombatFrameUse.Movement)
    {
        var e = b.GoalEnemy;
        var f = new CombatStepFrame
        {
            Enabled = config?.Enabled == true && ContactSafety.Allows(b, e),
            Pmc = NativeAdapter.IsSupportedAi(b),
            Authority = Authority,
            Ghost = Ghost(b.ProfileId),
            Active = b.BotActive && !b.IsDead && !b.GameEnding && b.SAINLayersActive,
        };
        if (use == CombatFrameUse.AngleScope)
            return f;

        var seen = e?.KnownPlaces.LastSeenPlace;
        var position = b.Position;
        var known = seen?.Position ?? position;
        var action = b.CurrentAction?.GetType().Name;
        f.Action =
            action == "StandAndShootAction"
            && b.Decision.CurrentCombatDecision == ECombatDecision.StandAndShoot
                ? "StandAndShoot"
            : action == "DogFightAction"
            && b.Decision.CurrentCombatDecision == ECombatDecision.DogFight
                ? "DogFight"
            : "other";
        f.Now = Time.time;
        f.Target = e?.EnemyProfileId ?? "";
        f.Visible = e?.IsVisible == true && seen != null;
        f.Known = DP(known);
        f.ContactAge = seen?.TimeSincePositionUpdated ?? 0;
        f.Distance = Vector3.Distance(position, known);
        f.Immobile = b.Player.IsInPronePose;
        f.Busy = b.Player.HandsController is IMedsController || b.BotOwner.Medecine?.Using == true;
        if (use == CombatFrameUse.Movement)
        {
            f.Position = DP(position);
            f.Personality = b.Info.Personality.ToString();
            f.Source = seen != null ? "personal-vision" : "";
            f.Grounded = b.Player.MovementContext.IsGrounded;
        }
        return f;
    }

    private void RefreshCombatStep(BotComponent b, BotState s, bool mayPlan, CombatStepFrame? initialFrame = null)
    {
        var r = s.CombatStep;
        if (!r.Policy.Active)
            return;
        if (config == null || b == null || b.IsDead)
        {
            EndCombatStep(s, "movement-bot-ended");
            return;
        }
        var f = initialFrame ?? CombatStepFrame(b, s);
        if (b.GoalEnemy == null || b.GoalEnemy.EnemyProfileId != r.Policy.Trace!.Target)
        {
            EndCombatStep(s, "movement-contact-instance-changed");
            return;
        }
        if (QueuedCombatMovement(b) && r.Path != null)
        {
            EndCombatStep(s, "movement-native-route-queued");
            return;
        }
        if (r.PauseOwnsEmpty && b.Mover.ActivePath != null)
        {
            EndCombatStep(s, "pause-route-replaced");
            return;
        }
        // Check ownership before recording position deltas: another native route's movement is not M01 progress.
        f.NativeRouteValid =
            r.Path == null
            || OwnCombatStep(r)
            || (
                b.Mover.ActivePath == null
                && r.Path.Status == EBotMoveStatus.Complete
                && Vector3.Distance(b.Position, r.Goal) < .2f
            );
        if (!r.Policy.Observe(f, config.CombatStep, r.Pause.Pausing))
        {
            FinishCombatStep(s);
            return;
        }
        if (Time.time > r.SpeedAt)
        {
            r.MeasuredSpeed = (b.Position - r.SpeedPosition).magnitude / (Time.time - r.SpeedAt);
            r.SpeedAt = Time.time;
            r.SpeedPosition = b.Position;
        }
        if (r.Pause.Pausing)
        {
            if (r.Pause.Observe(FirePauseFrame(b, s, f), config.FirePause))
            {
                ApplyFirePauseInput(b, s);
                RecordFirePause(s);
                RecordCombatStep(s);
                return;
            }
            if (r.PauseOwnsEmpty && b.Mover.ActivePath == null && r.MeasuredSpeed > .12f)
                b.BotOwner.AimingManager?.CurrentAiming?.Move(Math.Max(.01f, b.Player.Speed));
            r.PauseOwnsEmpty = false;
            r.Policy.ResumeAfterPause(Time.time);
            RecordFirePause(s, true);
        }
        if (mayPlan && Time.time >= r.Policy.StepDeadline)
        {
            if (!doorBudget.TryBegin(Time.frameCount, b))
                return;
            doorClock.Restart();
            try
            {
                if (!TryFirePause(b, s, f))
                    PlanCombatStep(b, s);
            }
            finally
            {
                doorClock.Stop();
                doorBudget.Charge(doorClock.Elapsed.TotalMilliseconds);
            }
        }
        RecordCombatStep(s);
    }

    private void PlanCombatStep(BotComponent b, BotState s)
    {
        var r = s.CombatStep;
        var t = r.Policy.Trace!;
        var c = config!.CombatStep;
        if (r.Candidate >= 6)
        {
            EndCombatStep(s, "movement-no-safe-step");
            return;
        }
        var anchor = VP(r.Policy.Anchor);
        var forward =
            new Vector3((float)t.KnownX, 0, (float)t.KnownZ)
            - new Vector3(b.Position.x, 0, b.Position.z);
        if (forward.sqrMagnitude < .01f)
        {
            EndCombatStep(s, "movement-no-direction");
            return;
        }
        forward.Normalize();
        var right = Vector3.Cross(Vector3.up, forward);
        int candidate = r.Candidate++;
        Vector3 direction = candidate switch
        {
            0 => right * r.Side,
            1 => -right * r.Side,
            2 => (right * r.Side - forward).normalized,
            3 => (-right * r.Side - forward).normalized,
            4 => (right * r.Side + forward).normalized,
            _ => (-right * r.Side + forward).normalized,
        };
        string label = candidate switch
        {
            0 => r.Side < 0 ? "left" : "right",
            1 => r.Side < 0 ? "right" : "left",
            2 or 3 => "diagonal-back",
            _ => "diagonal-forward",
        };
        if (candidate == 0 && Vector3.Distance(b.Position, anchor) > c.Radius * .65)
        {
            direction = (anchor - b.Position).normalized;
            label = "toward-anchor";
        }
        if (
            OwnThreatens(b, b.Position + direction, out var ownGrenade)
            && Vector3.Dot(direction, ownGrenade - b.Position) > 0
        )
            direction = -direction; // Source DiamondStepper reverses only its next tap.
        var raw = b.Position + direction * (float)Math.Min(.85, c.Radius * .75);
        string why = "movement-no-local-navmesh";
        if (
            NavMesh.SamplePosition(raw, out var sample, .12f, NavMesh.AllAreas)
            && b.Mover.CanGoToPoint(sample.position, out var route, true, .05f)
        )
        {
            why = CombatStep.Route(
                r.Policy.Anchor,
                DP(b.Position),
                DP(sample.position),
                route.corners.Select(DP).ToArray(),
                route.status == NavMeshPathStatus.PathComplete,
                c.Radius
            )
                ? CombatStepSafety(b, s, b.Position, sample.position)
                : "movement-route-outside-local-area";
            if (why.Length == 0)
            {
                // Only replace movement from the currently verified native shooting action.
                if (r.Path != null && !OwnCombatStep(r) && b.Mover.ActivePath != null)
                {
                    EndCombatStep(s, "movement-route-replaced");
                    return;
                }
                // SAIN Cancel(0) still waits at least 0.33 s and queues the replacement. Finish the verified
                // shooting route through SAIN's own completion method so a step starts on the route we observe.
                if (!ReleaseShootingRoute(b, out var released))
                {
                    EndCombatStep(s, "movement-native-route-took-over");
                    return;
                }
                if (released > 0)
                    t.Alternatives = t
                        .Alternatives.Concat(
                            new[]
                            {
                                "native shooting handoff: " + released + " observed legs released",
                            }
                        )
                        .TakeLast(6)
                        .ToArray();
                if (!b.Mover.WalkToPointByWay(route, true, .1f, false))
                {
                    EndCombatStep(s, "movement-native-request-refused");
                    return;
                }
                r.Path = b.Mover.ActivePath;
                r.Goal = sample.position;
                r.StepOrigin = b.Position;
                r.PathStarted = r.Path?.TimeStarted ?? -1;
                if (
                    !OwnCombatStep(r)
                    || !r.Policy.Request(DP(r.Goal), label, Time.time, jumpRandom.NextDouble(), c)
                )
                {
                    EndCombatStep(s, "movement-request-not-observed");
                    return;
                }
                if (!r.OwnSpeed)
                {
                    r.PreviousSpeed = (float)
                        combatNativeSpeed.GetValue(b.PlayerComponent.CharacterController);
                    r.AppliedSpeed = Mathf.Clamp01(b.Info.FileSettings.Move.STRAFE_SPEED);
                    r.OwnSpeed = true;
                    b.Mover.SetTargetMoveSpeed(r.AppliedSpeed);
                }
                r.Path!.RequestEndSprint(ESprintUrgency.None, "COMET combat step");
                r.LastSafety = Time.time;
                r.NextSafety = Time.time + .1f;
                r.Candidate = 0;
                r.Side = -r.Side;
                RecordCombatStep(s, true);
                return;
            }
        }
        t.Alternatives = t.Alternatives.Concat(new[] { label + ": " + why }).TakeLast(6).ToArray();
        RecordCombatStep(s);
    }

    private string CombatStepSafety(BotComponent b, BotState s, Vector3 from, Vector3 to)
    {
        var anchor = s.CombatStep.Policy.Anchor;

        string volume = RunVolume(b, from, to);
        if (volume.Length > 0)
            return "movement-" + volume;
        return "";
    }

    private static int CombatProtection(Vector3 position, Vector3 known)
    {
        int mask = 0;
        for (int i = 0; i < 3; i++)
            if (
                Physics.Linecast(
                    position + Vector3.up * (.6f + i * .5f),
                    known + Vector3.up * 1.4f,
                    LayersMaskController.HighPolyWithTerrainMask,
                    QueryTriggerInteraction.Ignore
                )
            )
                mask |= 1 << i;
        return mask;
    }

    private static bool OwnCombatStep(CombatStepRuntime r) =>
        r.Owner != null
        && r.Path != null
        && ReferenceEquals(r.Owner.Mover.ActivePath, r.Path)
        && r.Path.TimeStarted == r.PathStarted
        && Vector3.Distance(r.Path.Destination, r.Goal) < .02f;

    private static bool QueuedCombatMovement(BotComponent b)
    {
        var active = b.Mover.ActivePath;
        var one = combatPathOne(b.Mover);
        var two = combatPathTwo(b.Mover);
        return !ReferenceEquals(one, active) && one.Status == EBotMoveStatus.ReadyToMove
            || !ReferenceEquals(two, active) && two.Status == EBotMoveStatus.ReadyToMove;
    }

    // Called only from a validated StandAndShoot/DogFight action. SAIN Stop may
    // synchronously promote its second prepared leg; release that exact leg too,
    // never a newly installed/retargeted route from an emergency or another mod.
    private static bool ReleaseShootingRoute(BotComponent b, out int released)
    {
        released = 0;
        if (!ShootingHandoffAllowed(b))
            return false;
        var active = b.Mover.ActivePath;
        var queued = CrossfireQueuedPath(b);
        if (active == null)
            return queued == null;
        if (active is not BotPathDataManual first || queued is not null and not BotPathDataManual)
            return false;
        var destination = queued?.Destination ?? default;
        var observed = Time.time;
        combatStopPath.Invoke(first, new object[] { false, "COMET observed shooting handoff" });
        released++;
        if (
            queued is BotPathDataManual second
            && ReferenceEquals(b.Mover.ActivePath, queued)
            && !QueuedCombatMovement(b)
            && Vector3.Distance(second.Destination, destination) < .02f
            && second.TimeStarted >= observed
            && second.TimeStarted <= Time.time
            && ShootingHandoffAllowed(b)
        )
        {
            combatStopPath.Invoke(second, new object[] { false, "COMET observed shooting queue" });
            released++;
        }
        return b.Mover.ActivePath == null && !QueuedCombatMovement(b) && ShootingHandoffAllowed(b);
    }

    private static bool ShootingHandoffAllowed(BotComponent b)
    {
        return b.Decision.CurrentCombatDecision
                is ECombatDecision.StandAndShoot
                    or ECombatDecision.DogFight
            && b.Decision.CurrentSelfDecision == ESelfActionType.None
            && b.Decision.CurrentSquadDecision == ESquadDecision.None
            && b.Player.MovementContext.IsGrounded
            && b.Grenade.GrenadeDangerPoint == null
            && !b.BotOwner.Medecine.Using
            && !b.BotOwner.WeaponManager.Reload.Reloading
            && !b.BotOwner.WeaponManager.Grenades.ThrowindNow;
    }

    private static IBotPathData? CrossfireQueuedPath(BotComponent b)
    {
        var active = b.Mover.ActivePath;
        var one = combatPathOne(b.Mover);
        var two = combatPathTwo(b.Mover);
        if (!ReferenceEquals(one, active) && one.Status == EBotMoveStatus.ReadyToMove)
            return one;
        return !ReferenceEquals(two, active) && two.Status == EBotMoveStatus.ReadyToMove
            ? two
            : null;
    }

    private void EndCombatStep(BotState s, string why)
    {
        if (!s.CombatStep.Policy.Active)
            return;
        s.CombatStep.Policy.End(why, Time.time);
        FinishCombatStep(s);
    }

    private void FinishCombatStep(BotState s)
    {
        var r = s.CombatStep;
        try
        {
            r.Pause.End(r.Policy.Trace?.Reason ?? "pause-parent-ended");
            RecordFirePause(s, true);
            bool owned = OwnCombatStep(r);
            if (owned && r.Path is BotPathDataManual path)
                combatStopPath.Invoke(path, new object[] { false, "COMET combat movement ended" });
            if (owned && r.Owner != null && !r.Owner.IsDead && r.Owner.Mover.ActivePath == null)
                r.Owner.Player.Move(Vector2.zero);
            if (
                r.OwnSpeed
                && r.Owner != null
                && !r.Owner.IsDead
                && Math.Abs(
                    (float)combatNativeSpeed.GetValue(r.Owner.PlayerComponent.CharacterController)
                        - r.AppliedSpeed
                ) < .001f
            )
                r.Owner.Mover.SetTargetMoveSpeed(r.PreviousSpeed);
        }
        finally
        {
            RecordCombatStep(s, true);
            r.Drop();
        }
    }

    private FirePauseFrame FirePauseFrame(BotComponent b, BotState s, CombatStepFrame f)
    {
        var r = s.CombatStep;
        var weapon = b.Player.HandsController?.Item as Weapon;
        return new()
        {
            Now = Time.time,
            SequenceDeadline = r.Policy.Deadline,
            Target = f.Target,
            Action = f.Action,
            Known = f.Known,
            ContactAge = f.ContactAge,
            Distance = f.Distance,
            SinceHit = b.Medical.TimeSinceShot,
            Speed = r.MeasuredSpeed,
            CompletedSteps = r.Policy.Trace!.CompletedSteps,
            Weapon = weapon?.Id.ToString() ?? "",
            Mode =
                weapon == null ? ""
                : weapon.SelectedFireMode
                    is Weapon.EFireMode.single
                        or Weapon.EFireMode.doubleaction
                    ? "single"
                : "automatic",
            Gate = CombatStep.Gate(f, config!.CombatStep),
            RouteFree = b.Mover.ActivePath == null && !QueuedCombatMovement(b),
            CanFire = b.BotOwner.ShootData?.Shooting == true,
        };
    }

    private bool TryFirePause(BotComponent b, BotState s, CombatStepFrame f)
    {
        var r = s.CombatStep;
        if (!r.Pause.Waiting || !r.Pause.TryStart(FirePauseFrame(b, s, f), config!.FirePause))
        {
            RecordFirePause(s);
            return false;
        }
        RecordFirePause(s, true);
        string why = CombatStepSafety(b, s, b.Position, b.Position);
        if (why.Length > 0)
        {
            r.Pause.End(why);
            RecordFirePause(s, true);
            return false;
        }
        if (QueuedCombatMovement(b) || b.Mover.ActivePath != null && !OwnCombatStep(r))
        {
            EndCombatStep(s, "pause-route-replaced");
            return true;
        }
        if (OwnCombatStep(r) && r.Path is BotPathDataManual path)
            combatStopPath.Invoke(path, new object[] { false, "COMET firing pause" });
        if (b.Mover.ActivePath != null || QueuedCombatMovement(b))
        {
            EndCombatStep(s, "pause-route-replaced");
            return true;
        }
        r.Path = null;
        r.PauseOwnsEmpty = true;
        r.Pause.ConfirmRequest();
        r.LastSafety = Time.time;
        r.NextSafety = Time.time + .1f;
        ApplyFirePauseInput(b, s);
        RecordFirePause(s, true);
        return true;
    }

    private static void ApplyFirePauseInput(BotComponent b, BotState s)
    {
        var r = s.CombatStep;
        if (!r.PauseOwnsEmpty || b.Mover.ActivePath != null || QueuedCombatMovement(b))
            return;
        b.Player.Move(Vector2.zero);
        // SAIN normally feeds this flag from movement updates. Only clear it after actual stationary evidence.
        // During braking retain the native moving value; never change spread, recoil, aim time or fire permission.
        b.BotOwner.AimingManager?.CurrentAiming?.Move(0f);
    }

    private static void FirePauseShot(PlayerComponent __instance, IWeapon __0)
    {
        var p = Instance;
        var b = __instance.BotComponent;
        if (
            p?.config == null
            || b == null
            || !__instance.IsActive
            || __instance.Equipment.CurrentWeaponInfo == null
            || !p.states.TryGetValue(b, out var s)
        )
            return;
        if (s.CornerPrefire.Policy.Active)
        {
            p.PrefireShot(b, s, __0);
            return;
        }
        if (s.CornerPeek.Policy.Active)
        {
            try
            {
                p.RefreshCornerPeek(b, s, false);
                s.CornerPeek.Policy.Shot(
                    Time.time,
                    b.GoalEnemy?.IsVisible == true
                        && b.GoalEnemy.CanShoot
                        && b.FriendlyFire.ClearShot
                );
                p.RecordCornerPeek(s);
            }
            catch (Exception ex)
            {
                p.CornerError(s, ex);
            }
            return;
        }
        if (!s.CombatStep.Pause.Pausing)
            return;
        try
        {
            p.RefreshCombatStep(b, s, false); // Recheck authority, route, contact and interruption before attributing a shot.
            s.CombatStep.Pause.Shot(Time.time);
            p.RecordFirePause(s);
        }
        catch (Exception ex)
        {
            p.CombatStepError(s, ex);
        }
    }

    private void RecordFirePause(BotState s, bool force = false)
    {
        if (!Comet.Core.ReplayCapture.Detailed)
            return;
        var r = s.CombatStep;
        var t = r.Pause.Trace;
        if (t == null)
            return;
        string key = t.Episode + ":" + t.State + ":" + t.Reason;
        if (!force && key == r.PauseLogKey && (!r.Pause.Pausing || Time.time - r.PauseLogAt < .05f))
            return;
        r.PauseLogKey = key;
        r.PauseLogAt = Time.time;
        var json = Diagnostics.Serialize(
            new
            {
                schema = 1,
                session,
                sequence = ++sequence,
                feature = "M02",
                stage = "evaluated",
                decision = "FirePause",
                reason = t.Reason,
                realtime = Time.realtimeSinceStartup,
                utc = DateTime.UtcNow.ToString("O"),
                configRevision = config?.Revision ?? 0,
                profileId = s.ProfileId,
                name = s.Name,
                botInstance = s.Id,
                firePause = t,
                firePauseConfig = config?.FirePause,
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

    private void CombatStepError(BotState s, Exception ex)
    {
        s.CombatStep.Fault = true;
        EndCombatStep(s, "movement-adapter-error");
        Logger.LogWarning("M01 yielded: " + ex.GetBaseException().Message);
    }

    private void RecordCombatStep(BotState s, bool force = false)
    {
        if (!Comet.Core.ReplayCapture.Detailed)
            return;
        var r = s.CombatStep;
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
            + t.Requests
            + ":"
            + t.Alternatives.Length;
        if (!force && key == r.LogKey && (!r.Policy.Active || Time.time - r.LogAt < .25f))
            return;
        r.LogKey = key;
        r.LogAt = Time.time;
        var json = Diagnostics.Serialize(
            new
            {
                schema = 1,
                session,
                sequence = ++sequence,
                feature = "M01",
                stage = "evaluated",
                decision = "CombatStep",
                reason = t.Reason,
                realtime = Time.realtimeSinceStartup,
                utc = DateTime.UtcNow.ToString("O"),
                configRevision = config?.Revision ?? 0,
                profileId = s.ProfileId,
                name = s.Name,
                botInstance = s.Id,
                combatStep = t,
                combatStepConfig = config?.CombatStep,
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
