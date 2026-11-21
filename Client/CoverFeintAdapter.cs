using System;
using System.Collections.Generic;
using System.Linq;
using Comet.Core;
using EFT;
using HarmonyLib;
using Newtonsoft.Json;
using SAIN.Components;
using SAIN.Models.Enums;
using SAIN.Preset.Shared.Enums;
using SAIN.SAINComponent.Classes.Decision;
using SAIN.SAINComponent.Classes.EnemyClasses;
using SAIN.SAINComponent.Classes.Mover;
using UnityEngine;
using UnityEngine.AI;

namespace Comet.Client;

internal sealed class CoverFeintRuntime(RepositionCooldown cooldown)
{
    internal readonly CoverFeintPolicy Policy = new(cooldown, UnityEngine.Random.Range);
    internal readonly Dictionary<IBotPathData, NavMeshPath> Routes = new();
    internal BotComponent? Owner;
    internal Enemy? Enemy;
    internal Player.FirearmController? Firearm;
    internal bool IssuingMove;
    internal float Next,
        LastSafety = -1000,
        NextSafety,
        SeenAt,
        LogAt = -1000;
    internal int Candidate,
        RequestFrame = -1,
        ProbeFrame = -1,
        FirstSide;
    internal bool Starting,
        Fault,
        Protected,
        Exposed,
        OwnSpeed;
    internal float PreviousSpeed,
        AppliedSpeed;
    internal string LogKey = "";

    internal void Drop()
    {
        Owner = null;
        Enemy = null;
        Firearm = null;
        IssuingMove = false;
        Routes.Clear();
        RequestFrame = -1;
        OwnSpeed = false;
    }
}

public sealed partial class Plugin
{
    private static System.Reflection.FieldInfo feintBridge = null!,
        feintClipRelease = null!;

    private static void PatchCoverFeint(Harmony h)
    {
        var freeze = typeof(BotComponent).Assembly.GetType(
            "SAIN.Layers.Combat.Solo.FreezeAction",
            true
        )!;
        h.Patch(
            AccessTools.Method(typeof(SAIN.Layers.BotAction), "OnSteeringTicked"),
            prefix: new HarmonyMethod(typeof(Plugin), nameof(FeintSteering))
        );
        h.Patch(
            AccessTools.Method(freeze, "Update"),
            prefix: new HarmonyMethod(typeof(Plugin), nameof(FeintAction))
        );
        h.Patch(
            AccessTools.Method(typeof(BotPathDataManual), "Initialize"),
            postfix: new HarmonyMethod(typeof(Plugin), nameof(FeintPathInitialized))
        );
        feintBridge = AccessTools.Field(typeof(BaseSoundPlayer), "playersBridge")
            ?? throw new MissingFieldException("M14 sound bridge");
        feintClipRelease = AccessTools.Field(typeof(BaseSoundPlayer), "_clipSourceReleaseTime")
            ?? throw new MissingFieldException("M14 native playback evidence");
        foreach (string name in new[] { "SoundAtPointEventHandler", "EventReceiver" })
            h.Patch(
                AccessTools.Method(typeof(BaseSoundPlayer), name),
                prefix: new HarmonyMethod(typeof(Plugin), nameof(FeintSoundEvent))
            );
        h.Patch(
            AccessTools.Method(typeof(BaseSoundPlayer), "PlayClipInternal"),
            prefix: new HarmonyMethod(typeof(Plugin), nameof(FeintSoundBefore)),
            postfix: new HarmonyMethod(typeof(Plugin), nameof(FeintSoundAfter))
        );
    }

    private bool FeintScope(BotComponent b, BotState s) =>
        ContactSafety.AllowsGoal(b) && FeintBotScope(b, s);

    private bool FeintBotScope(BotComponent b, BotState s) =>
        config?.Enabled == true
        && config.CoverFeint.Enabled
        && NativeAdapter.IsSupportedAi(b)
        && b.BotActive
        && !b.IsDead
        && !b.GameEnding
        && Authority
        && !Ghost(s.ProfileId)
        && b.SAINLayersActive;

    private static bool MagazineOperation(Player.FirearmController? f) =>
        f?.CurrentHandsOperation is Player.FirearmController.UtilityOperation op
        && op._utilityOperationType
            == Player.FirearmController.UtilityOperation.EUtilityType.CheckMagazine;

    private CoverFeintFrame FeintFrame(BotComponent b, BotState s)
    {
        var r = s.CoverFeint;
        var e = b.GoalEnemy;
        var seen = e?.KnownPlaces.LastSeenPlace;
        var known = e?.KnownPlaces.LastKnownPosition ?? b.Position;
        var f = new CoverFeintFrame
        {
            Now = Time.time,
            Position = DP(b.Position),
            Known = DP(known),
            Target = e?.EnemyProfileId ?? "",
            Key = e == null ? "" : RelocationKey(e),
            Age = r.Policy.Active ? Time.time - r.SeenAt : seen?.TimeSincePositionUpdated ?? 999,
            SightAge = e?.Seen == true ? e.TimeSinceSeen : 999,
            Visible = e?.IsVisible == true,
            Distance = Vector3.Distance(b.Position, known),
            Cover = b.Cover.CoverInUse != null,
            Ready = VisibleAdapter.WeaponReady(b),
            Healthy = b.Memory.Health.HealthStatus == ETagStatus.Healthy,
            Bold = b.Info.PersonalitySettings.Rush.CanRushEnemyReloadHeal,
            Speed = b.Player.Velocity.magnitude,
            Grounded = b.Player.MovementContext.IsGrounded,
            AmmoRatio = TacticalAmmoPercent(b.BotOwner) / 100,
            Protected = r.Protected,
            Exposed = r.Exposed,
            AtGoal = AtFeintGoal(b, r.Policy.Goal),
            Fire = e?.Status.TimeLastShotAtMe ?? -1,
        };
        f.MagazineAudible = true;
        f.Outdoor =
            b.Player.Environment == EnvironmentType.Outdoor && b.Player.AIData?.EnvironmentId == 0;
        if (e?.Hearing != null && contactSounds.TryGetValue(e.Hearing, out var sound))
        {
            f.Heard = sound.At;
            f.Coming = sound.IsCloserStep((float)f.Now, b.Position, known);
        }
        if (b.Squad?.Members != null)
            foreach (var mate in b.Squad.Members.Values)
            {
                if (
                    mate != null
                    && !ReferenceEquals(mate, b)
                    && !mate.IsDead
                    && mate.GoalEnemy is { } goal
                    && CoverFeintPolicy.MateEngaging(f.Target, goal.EnemyProfileId, goal.IsVisible, goal.TimeSinceSeen)
                )
                {
                    f.Mates = true;
                    break;
                }
            }
        if (!FeintScope(b, s))
            f.Block = "feint-out-of-scope";
        else if (e == null || !e.Seen)
            f.Block = "feint-unseen-contact";
        else if (r.Policy.Active && !SameTacticalContact(e, r.Enemy))
            f.Block = "feint-contact-changed";
        else if (b.BotOwner.Memory.IsUnderFire)
            f.Block = "feint-under-fire";
        else if (
            !r.Policy.Active
            && (
                b.Decision.CurrentSelfDecision != ESelfActionType.None
                || b.Decision.CurrentSquadDecision != ESquadDecision.None
                || s.Door.Policy.Active
                || s.Entry.Policy.Active
                || s.Relocation.Policy.Active
            )
        )
            f.Block = "feint-other-tactic";
        if (b.BotOwner.WeaponManager.Grenades.ThrowindNow)
            f.Block = "feint-throwing";
        foreach (var other in b.EnemyController.KnownEnemies)
            if (other != null && !SameTacticalContact(other, e) && other.IsVisible)
            {
                f.Block = "feint-other-visible-enemy";
                break;
            }
        return f;
    }

    private bool ChooseFeint(
        BotComponent b,
        Enemy e,
        BotState s,
        ref ECombatDecision result,
        ref bool succeeded
    )
    {
        var r = s.CoverFeint;
        if (config == null || r.Fault)
            return false;
        try
        {
            if (!r.Policy.Active)
            {
                if (Time.time < r.Next)
                    return false;
                r.Next = Time.time + .4f;
                if (Time.time < s.RepositionCooldown.Until)
                    return false;
                var f = FeintFrame(b, s);
                var old = r.Policy.Trace;
                if (!r.Policy.Begin(f, config.CoverFeint, jumpRandom.NextDouble))
                {
                    if (!ReferenceEquals(old, r.Policy.Trace))
                        RecordFeint(s, true);
                    return false;
                }
                r.Starting = true;
                try
                {
                    Release(s, "feint-start");
                }
                finally
                {
                    r.Starting = false;
                }
                r.Owner = b;
                r.Enemy = e;
                r.SeenAt = Time.time - (float)f.Age;
                r.Candidate = 0;
                r.FirstSide = 0;
                r.LastSafety = -1000;
                r.NextSafety = 0;
            }
            RefreshFeint(b, s, false);
            if (!r.Policy.Active)
                return false;
            r.RequestFrame = Time.frameCount;
            result = ECombatDecision.Freeze;
            succeeded = true;
            return true;
        }
        catch (Exception ex)
        {
            FeintError(s, ex);
            return false;
        }
    }

    private bool FeintAccepted(BotDecisionManager manager, BotState s)
    {
        var r = s.CoverFeint;
        if (!r.Policy.Active)
            return false;
        bool ours =
            r.RequestFrame == Time.frameCount
            && manager.CurrentCombatDecision == ECombatDecision.Freeze
            && manager.CurrentSelfDecision == ESelfActionType.None
            && manager.CurrentSquadDecision == ESquadDecision.None;
        r.RequestFrame = -1;
        r.Policy.Accepted(ours, Time.time, manager.CurrentCombatDecision == ECombatDecision.StandAndShoot);
        if (!ours)
        {
            FinishFeint(s);
            return false;
        }
        RecordFeint(s);
        return true;
    }

    private void RequestFeintPullBack(BotState s, ref ECombatDecision decision, ref bool succeeded)
    {
        var t = s.CoverFeint.Policy.Trace!;
        if (s.Feature != "M14" || s.Decision != "FallBack")
        {
            Release(s, "feint-pullback", preserveVisiblePlan: true, preserveHiddenPreference: true);
            s.Feature = "M14";
            s.Decision = "FallBack";
            s.Mixing = null;
            s.Candidates = new() { new() { Decision = "FallBack", Eligible = true, Reason = "feint-pullback" } };
            Emit(s, "selected", "feint-pullback");
        }
        s.Expected = decision = ECombatDecision.Retreat;
        s.RequestedFrame = Time.frameCount;
        succeeded = true;
        if (!t.PullBackRequested)
        {
            t.PullBackRequested = true;
            RecordFeint(s, true);
        }
    }

    private static bool FeintAction(SAIN.Layers.BotAction __instance)
    {
        var b = __instance.Bot;
        var p = Instance;
        if (
            p == null
            || b == null
            || !p.states.TryGetValue(b, out var s)
            || !s.CoverFeint.Policy.Active
        )
            return true;
        try
        {
            p.RefreshFeint(b, s, true);
            return !s.CoverFeint.Policy.Active;
        }
        catch (Exception ex)
        {
            p.FeintError(s, ex);
            return true;
        }
    }

    private static bool FeintSteering(SAIN.Layers.BotAction __instance)
    {
        var p = Instance;
        var b = __instance.Bot;
        if (
            __instance.Name != "FreezeAction"
            || p == null
            || b == null
            || !ReferenceEquals(b.CurrentAction, __instance)
            || !p.states.TryGetValue(b, out var s)
            || !s.CoverFeint.Policy.Active
            || !p.FeintScope(b, s)
            || b.GoalEnemy?.IsVisible == true
        )
            return true;
        if (TacticalSteeringFire.TryShoot(b, TacticalSteeringKind.CoverFeint))
            return false;
        b.Steering.LookToPoint(VP(s.CoverFeint.Policy.LookPoint) + Vector3.up * 1.3f);
        return false;
    }

    private void SampleFeint(BotComponent b, BotState s)
    {
        if (s.CoverFeint.Fault)
            return;
        if (!FeintScope(b, s))
        {
            // An absent goal skips enemy decisions. The short retreat window belongs to this bot.
            bool keepWindow = b.GoalEnemy == null && s.CoverFeint.Policy.Trace is { } t
                && Time.time < t.PullBackUntil && FeintBotScope(b, s);
            EndFeint(s, "feint-out-of-scope", true, preservePullBack: keepWindow);
            return;
        }
        if (s.CoverFeint.Policy.Active)
            RefreshFeint(b, s, false);
    }

    private void RefreshFeint(BotComponent b, BotState s, bool execute)
    {
        var r = s.CoverFeint;
        var p = r.Policy;
        if (!p.Active || config == null)
            return;
        var f = FeintFrame(b, s);
        if (f.Block.Length > 0 || !f.RouteValid)
        {
            EndFeint(s, f.Block.Length > 0 ? f.Block : "feint-native-route-taken-over");
            return;
        }
        f.Protected = r.Protected;
        f.Exposed = r.Exposed;
        bool actionTick = execute && p.Trace!.Accepted && b.Decision.CurrentCombatDecision == ECombatDecision.Freeze;
        // On a movement tick the native action attempts the jump before testing arrival or timeout.
        if (!p.Observe(f, config.CoverFeint, actionTick && !p.Moving && !p.WantsRoute))
        {
            FinishFeint(s);
            return;
        }
        if (p.Trace!.State == "mag-check")
            p.MagazineObserved(MagazineOperation(r.Firearm), f.Ready, Time.time);
        if (!p.Active)
        {
            FinishFeint(s);
            return;
        }
        // Native Freeze owns steering and shooting. Only its accepted action callback executes the tactic.
        if (actionTick)
        {
            var opposite = p.OppositePeekProbe;
            bool needsPoint = p.Trace.State == "planning" || opposite.HasValue;
            if (
                needsPoint
                && r.ProbeFrame != Time.frameCount
                && doorBudget.TryBegin(Time.frameCount, b)
            )
            {
                r.ProbeFrame = Time.frameCount;
                doorClock.Restart();
                try
                {
                    if (opposite is DoorPoint mirror)
                        p.ResolveOppositePeek(SampleOppositePeek(mirror, p.Known), f.Now);
                    else
                        PlanFeint(b, s, f, config.CoverFeint);
                }
                finally
                {
                    doorClock.Stop();
                    doorBudget.Charge(doorClock.Elapsed.TotalMilliseconds);
                }
            }
            else if (needsPoint)
                p.Preparation.Defer(Time.time, Time.deltaTime);
            if (!p.Active)
            {
                FinishFeint(s);
                return;
            }
            if (p.TargetPeekPose is float pose)
            {
                b.Mover.SetTargetPose(pose);
                p.Trace.AppliedPeekPose = pose;
            }
            if (p.Moving || p.WantsRoute)
            {
                if (p.PeekJumpKind(f.Now, config.CoverFeint) is string kind)
                    TryFeintJump(b, s, kind);
                // Planning or finishing the exposure can change the destination during this callback.
                f.AtGoal = AtFeintGoal(b, p.Goal);
                if (!f.AtGoal && p.MoveOrderDue(f.Now))
                    p.MoveResult(f.Now, MoveFeint(b, r));
                if (!p.Active)
                {
                    FinishFeint(s);
                    return;
                }
                p.AdvanceMove(f, config.CoverFeint);
            }
            if (p.Trace.State == "mag-pending")
            {
                r.Firearm = b.Player.HandsController as Player.FirearmController;
                if (r.Firearm == null)
                    p.NoFirearm(f, config.CoverFeint);
                else
                {
                    // The source session starts immediately before the firearm request, even if it refuses.
                    p.RequestMagazineCheck(b.GoalEnemy?.VisiblePathPoint is Vector3 point ? DP(point) : null);
                    bool accepted = r.Firearm.CheckAmmo();
                    p.MagazineResult(accepted, MagazineOperation(r.Firearm), f, config.CoverFeint);
                }
                if (!p.Active)
                {
                    FinishFeint(s);
                    return;
                }
            }
            if (!p.Moving && !p.WantsRoute)
            {
                StopFeintRoutes(r);
                if (b.Mover.ActivePath == null && !QueuedCombatMovement(b))
                    b.Player.Move(Vector2.zero);
            }
        }
        RecordFeint(s);
    }

    private void PlanFeint(BotComponent b, BotState s, CoverFeintFrame f, CoverFeintConfig settings)
    {
        var r = s.CoverFeint;
        var p = r.Policy;
        if (p.Trace!.Kind == "peek")
        {
            if (TryPlanFeintPoint(b, r) || r.Candidate < 16)
                return;
            p.NoPeekPoint(f, settings);
            if (!p.Active)
                return;
        }
        p.Plan(p.Home, f.Now);
    }

    private static DoorPoint? SampleOppositePeek(DoorPoint mirror, DoorPoint known)
    {
        if (!NavMesh.SamplePosition(VP(mirror), out var hit, .6f, NavMesh.AllAreas)
            || Physics.Linecast(hit.position + Vector3.up * 1.4f, VP(known) + Vector3.up * 1.3f,
                LayersMaskController.HighPolyWithTerrainMask))
            return null;
        return DP(hit.position);
    }

    private bool TryPlanFeintPoint(BotComponent b, CoverFeintRuntime r)
    {
        var p = r.Policy;
        int i = r.Candidate++;
        var forward = VP(p.Known) - VP(p.Home);
        forward.y = 0;
        forward.Normalize();
        if (i == 0)
            r.FirstSide = jumpRandom.NextDouble() < .5 ? 1 : -1;
        int side = (i < 8 ? 1 : -1) * r.FirstSide;
        var lateral = Vector3.Cross(Vector3.up, forward);
        float[] distances = { 1.1f, 1.5f, .9f, 2f };
        float d = distances[i % 4];
        var raw = VP(p.Home) + lateral * side * d + forward * (i % 8 < 4 ? 0 : .6f) * d;
        if (
            !NavMesh.SamplePosition(raw, out var nav, .6f, NavMesh.AllAreas)
            || NavMesh.Raycast(
                RemixMovementOrigin(b, b.Position, preserveOnMesh: true),
                nav.position,
                out _,
                NavMesh.AllAreas
            )
        )
            return false;
        for (int t = 0; t < 3; t++)
        {
            var target =
                VP(p.Known)
                + Vector3.up * 1.3f
                + lateral
                    * (
                        t == 0 ? 0
                        : t == 1 ? .8f
                        : -.8f
                    );
            if (
                Physics.Linecast(
                    nav.position + Vector3.up * 1.4f,
                    target,
                    LayersMaskController.HighPolyWithTerrainMask
                )
            )
                continue;
            r.Candidate = 0;
            return p.Plan(DP(nav.position), Time.time);
        }
        return false;
    }

    private static bool AtFeintGoal(BotComponent b, DoorPoint goal) =>
        Vector2.Distance(new(b.Position.x, b.Position.z), new((float)goal.X, (float)goal.Z)) < .7f;

    private void TryFeintJump(BotComponent b, BotState s, string kind)
    {
        var policy = s.CoverFeint.Policy;
        var origin = b.Position;
        var landing = VP(policy.Goal);
        if (!b.Player.MovementContext.IsGrounded || !b.Player.MovementContext.CanJump
            || !CoverFeintJump.HasClearRunUp(origin, landing, b.Player.Velocity))
            return;

        policy.JumpResult(ControlledJump(b, s, "M14", kind, landing - origin, landing,
            CoverFeintJump.MinimumForwardSpeed, 0, true));
    }

    private static bool MoveFeint(BotComponent b, CoverFeintRuntime r)
    {
        var goal = VP(r.Policy.Goal);
        r.IssuingMove = true;
        try
        {
            if (r.Policy.SprintPeekLeg)
                return b.Mover.RunToPoint(goal, true, .49f, ESprintUrgency.High);
            if (!b.Mover.WalkToPoint(goal, true, .49f))
                return false;
        }
        finally
        {
            r.IssuingMove = false;
        }
        if (!r.OwnSpeed)
        {
            r.PreviousSpeed = (float)
                combatNativeSpeed.GetValue(b.PlayerComponent.CharacterController);
            r.AppliedSpeed = 1;
            r.OwnSpeed = true;
        }
        b.Mover.SetTargetMoveSpeed(1);
        return true;
    }

    private static void FeintPathInitialized(BotPathDataManual __instance, NavMeshPath path)
    {
        if (
            __instance.PathFinder is SAINMoverClass m
            && Instance?.states.TryGetValue(m.Bot, out var s) == true
        )
        {
            var r = s.CoverFeint;
            r.Routes.Remove(__instance);
            if (r.IssuingMove && r.Policy.Active)
                r.Routes[__instance] = path;
        }
    }

    private static bool OwnFeintRoute(CoverFeintRuntime r, IBotPathData? path) =>
        path != null
        && r.Routes.TryGetValue(path, out var mesh)
        && ReferenceEquals(mesh, path.NavMeshPath);

    private static void StopFeintRoutes(CoverFeintRuntime r)
    {
        if (r.Owner == null)
            return;
        foreach (var path in r.Routes.Keys.ToArray())
            if (OwnFeintRoute(r, path))
            {
                if (
                    ReferenceEquals(r.Owner.Mover.ActivePath, path)
                    && path is BotPathDataManual manual
                )
                    combatStopPath.Invoke(manual, new object[] { false, "COMET cover feint" });
                else if (path.Status == EBotMoveStatus.ReadyToMove)
                {
                    relocationPathStatus.Invoke(path, new object[] { EBotMoveStatus.Complete });
                    relocationClearPath.Invoke(path, null);
                }
            }
        r.Routes.Clear();
    }

    private static BotState? FeintSoundOwner(BaseSoundPlayer sound)
    {
        if (
            (feintBridge.GetValue(sound) as BaseSoundPlayer.IObserverToPlayerBridge)?.iPlayer
            is not Player player
        )
            return null;
        var b = player.AIData?.BotOwner?.GetComponent<BotComponent>();
        var p = Instance;
        return
            b != null
            && p?.states.TryGetValue(b, out var s) == true
            && p.FeintScope(b, s)
            && s.CoverFeint.Policy.Active
            && s.CoverFeint.Policy.Trace?.State == "mag-check"
            && ReferenceEquals(player.HandsController, s.CoverFeint.Firearm)
            && MagazineOperation(s.CoverFeint.Firearm)
            ? s
            : null;
    }

    private static void FeintSoundEvent(BaseSoundPlayer __instance, string __0)
    {
        try
        {
            var s = FeintSoundOwner(__instance);
            s?.CoverFeint.Policy.Sound(__0, false);
        }
        catch
        { /* Observation must never suppress native audio. */
        }
    }

    private static void FeintSoundBefore(BaseSoundPlayer __instance, out double __state)
    {
        __state = -1;
        try
        {
            if (FeintSoundOwner(__instance) != null)
                __state = (double)feintClipRelease.GetValue(__instance);
        }
        catch { }
    }

    private static void FeintSoundAfter(
        BaseSoundPlayer __instance,
        double __state,
        bool __runOriginal
    )
    {
        try
        {
            if (
                __runOriginal
                && __state >= 0
                && (double)feintClipRelease.GetValue(__instance) > __state
            )
                FeintSoundOwner(__instance)?.CoverFeint.Policy.Sound("", true);
        }
        catch { }
    }

    private void EndFeint(BotState s, string why, bool clear = false, bool preservePullBack = false)
    {
        var r = s.CoverFeint;
        if (r.Starting)
            return;
        bool active = r.Policy.Active;
        if (clear)
            r.Policy.Clear(why, Time.time, preservePullBack);
        else if (active)
            r.Policy.End(why, Time.time);
        if (active)
            FinishFeint(s);
        else if (clear)
            r.Drop();
    }

    private void FinishFeint(BotState s)
    {
        var r = s.CoverFeint;
        try
        {
            if (
                !(
                    s.MedicalPosture.Policy.Blocking
                    && r.Owner != null
                    && !r.Owner.Player.MovementContext.IsGrounded
                )
            )
                StopFeintRoutes(r);
            if (r.Owner != null && !r.Owner.IsDead && r.Policy.Trace?.AppliedPeekPose is float applied
                && CoverFeintPolicy.StillOwnsLoweredPose(applied, r.Owner.Mover.Pose.PoseValue.TargetValue))
                r.Owner.Mover.SetTargetPose(1f);
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
            RecordFeint(s, true);
        }
        finally
        {
            r.Drop();
        }
    }

    private void FeintError(BotState s, Exception ex)
    {
        if (s.CoverFeint.Fault)
            return;
        s.CoverFeint.Fault = true;
        EndFeint(s, "feint-adapter-error", true);
        Logger.LogWarning("M14 yielded: " + ex.GetBaseException().Message);
    }

    private void RecordFeint(BotState s, bool force = false)
    {
        var r = s.CoverFeint;
        var t = r.Policy.Trace;
        if (t == null)
            return;
        string key =
            t.Episode + ":" + t.State + ":" + t.Reason + ":" + t.Accepted + ":" + t.SoundEvents;
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
                feature = "M14",
                stage = "evaluated",
                decision = "CoverFeint",
                reason = t.Reason,
                realtime = Time.realtimeSinceStartup,
                utc = DateTime.UtcNow.ToString("O"),
                configRevision = config?.Revision ?? 0,
                planningWaitSeconds = r.Policy.Preparation.Seconds,
                profileId = s.ProfileId,
                name = s.Name,
                botInstance = s.Id,
                coverFeint = t,
                coverFeintConfig = config?.CoverFeint,
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
