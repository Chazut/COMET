using System;
using System.Collections.Generic;
using Comet.Core;
using EFT;
using HarmonyLib;
using Newtonsoft.Json;
using SAIN.Components;
using SAIN.Layers;
using SAIN.Models.Enums;
using SAIN.Preset.Shared.Enums;
using SAIN.SAINComponent.Classes.Mover;
using UnityEngine;
using UnityEngine.AI;

namespace Comet.Client;

internal sealed class HoldAngleRuntime
{
    internal readonly HoldAngle Policy = new();
    internal SourcePeekSpot? Peek;
    internal float Started = -1,
        ExposureAt;
    internal bool Exposed;
    internal BotComponent? Owner;
    internal object? Enemy;
    internal IBotPathData? Path;
    internal Vector3 Seen,
        Known,
        Aim,
        Goal;
    internal float SeenAt = -1000,
        SampleAt = -1000,
        ObservedAt,
        PathAt,
        CallbackAt,
        GeometryAt = -1000,
        NextGeometry,
        LogAt = -1000,
        BasePose,
        OldPose,
        AppliedPose;
    internal bool Fault,
        OwnPose,
        Baseline,
        ClearEyes,
        ClearMuzzle;
    internal int Candidate,
        PlanFrame = -1;
    internal string Source = "",
        LogKey = "";
    internal HoldAngleCandidate? Best;
    internal HoldAngleFrame? LastFrame;
    internal readonly List<string> Alternatives = new();

    internal void Drop()
    {
        Owner = null;
        Path = null;
        OwnPose = false;
        Best = null;
        LastFrame = null;
        Alternatives.Clear();
    }
}

public sealed partial class Plugin
{
    private static void PatchHoldAngle(Harmony h)
    {
        var type = typeof(BotComponent).Assembly.GetType(
            "SAIN.Layers.Combat.Solo.FreezeAction",
            true
        );
        h.Patch(
            AccessTools.Method(type, "Update")
                ?? throw new MissingMethodException("M08 Freeze.Update"),
            postfix: new HarmonyMethod(typeof(Plugin), nameof(HoldAngleStep))
        );
        // Freeze inherits Stop; filter in the callback so other native actions remain untouched.
        h.Patch(
            AccessTools.Method(type, "Stop") ?? throw new MissingMethodException("M08 Freeze.Stop"),
            prefix: new HarmonyMethod(typeof(Plugin), nameof(HoldAngleStop))
        );
    }

    private void SampleHoldAngle(BotComponent b, BotState s)
    {
        var r = s.HoldAngle;
        if (r.Fault)
            return;
        if (
            config?.Enabled != true
            || !config.HoldAngle.Enabled
            || b.IsDead
            || !b.BotActive
            || !Authority
            || Ghost(s.ProfileId)
            || !NativeAdapter.IsSupportedAi(b)
            || b.Decision.CurrentCombatDecision != ECombatDecision.Freeze
        )
            EndHoldAngle(s, "hold-angle-ineligible");
    }

    private HoldAngleFrame HoldFrame(BotComponent b, BotState s)
    {
        var r = s.HoldAngle;
        var e = b.GoalEnemy;
        return new()
        {
            Now = Time.time,
            HoldStarted = r.Started,
            Target = e?.EnemyProfileId ?? "",
            Position = DP(b.Position),
            Pose = b.Player.PoseLevel,
            Speed = b.Player.Velocity.magnitude,
            Known = DP(e?.KnownPlaces.LastKnownPosition ?? b.Position),
            Source = "sain-known-contact",
            ObservedAt = e == null ? Time.time : Time.time - e.TimeSinceLastKnownUpdated,
            EyesClear = r.ClearEyes,
            MuzzleClear = r.ClearMuzzle,
            GeometryAt = Time.time,
        };
    }

    private static void HoldAngleStep(BotAction __instance)
    {
        var p = Instance;
        var b = __instance.Bot;
        if (p?.config == null || !p.states.TryGetValue(b, out var s) || s.HoldAngle.Fault)
            return;
        if (s.SiegeExit != null && s.Feature == "S11")
            return;
        try
        {
            p.SampleHoldAngle(b, s);
            if (
                !p.config.Enabled
                || !p.config.HoldAngle.Enabled
                || !p.Authority
                || p.Ghost(s.ProfileId)
                || !NativeAdapter.IsSupportedAi(b)
            )
                return;
            var r = s.HoldAngle;
            var e = b.GoalEnemy;
            if (r.Started < 0)
            {
                r.Started = Time.time;
                r.Owner = b;
                r.Peek = new(b, "freeze");
                r.Peek.Reset();
            }
            if (e?.VisiblePathPoint is not Vector3 corner)
                return;
            r.Aim = SourcePastCorner(
                b.Transform.WeaponRoot,
                corner,
                e.KnownPlaces.LastKnownPosition,
                b.LookDirection
            );
            r.Known = e.KnownPlaces.LastKnownPosition ?? corner;
            if (!r.Policy.Active)
                r.Policy.Begin(p.HoldFrame(b, s), p.config.HoldAngle);
            if (Time.time - r.Started < p.config.HoldAngle.Delay)
                return;
            if (Time.time >= r.ExposureAt)
            {
                r.ExposureAt = Time.time + .5f;
                r.Exposed = SourceOtherEnemyMaySeeMe(b, e);
            }
            if (!r.Exposed || r.Peek!.Moving)
                r.Peek!.Tick(r.Aim, corner, (float)p.config.HoldAngle.MaxStep);
            if (r.Peek!.StandToSee && p.config.HoldAngle.AdjustPose)
                b.Mover.SetTargetPose(1f);
            r.Path = r.Peek.Moving ? b.Mover.ActivePath : null;
            r.PathAt = r.Path?.TimeStarted ?? -1;
            r.Goal = r.Peek.Destination ?? b.Position;
            r.Policy.SourceObservation(
                r.Peek.Moving,
                r.Peek.StandToSee,
                DP(r.Goal),
                r.Peek.Attempts
            );
            p.RecordHoldAngle(s);
        }
        catch (Exception ex)
        {
            p.HoldAngleError(s, ex);
        }
    }

    // zzap EnemyDecisionClass.OtherEnemyMaySeeMe, pinned fbb86b83.
    private static bool SourceOtherEnemyMaySeeMe(
        BotComponent b,
        SAIN.SAINComponent.Classes.EnemyClasses.Enemy goal
    )
    {
        foreach (var e in b.EnemyController.KnownEnemies)
        {
            if (e == null || ReferenceEquals(e, goal))
                continue;
            if (e.IsVisible || e.InLineOfSight)
                return true;
            var last = e.KnownPlaces.LastKnownPosition;
            bool seen = e.Seen && e.TimeSinceSeen < 15 && e.RealDistance < 80;
            bool heard = e.Heard && e.TimeSinceHeard < 6 && e.RealDistance < 25;
            if (
                (seen || heard)
                && last.HasValue
                && !Physics.Linecast(
                    last.Value + Vector3.up * 1.5f,
                    b.Transform.EyePosition,
                    LayersMaskController.HighPolyWithTerrainMask
                )
            )
                return true;
        }
        return false;
    }

    private static void HoldAngleStop(BotAction __instance)
    {
        var p = Instance;
        if (
            ((object)__instance).GetType().FullName == "SAIN.Layers.Combat.Solo.FreezeAction"
            && p != null
            && p.states.TryGetValue(__instance.Bot, out var s)
        )
            p.EndHoldAngle(s, "hold-angle-native-action-ended");
    }

    private static bool OwnHoldPath(HoldAngleRuntime r) =>
        r.Owner != null
        && r.Path != null
        && ReferenceEquals(r.Owner.Mover.ActivePath, r.Path)
        && r.Path.TimeStarted == r.PathAt
        && Vector3.Distance(r.Path.Destination, r.Goal) < .02f;

    private void StopHoldPath(BotState s)
    {
        var r = s.HoldAngle;
        if (OwnHoldPath(r) && r.Path is BotPathDataManual path)
        {
            path.RequestEndSprint(ESprintUrgency.None, "COMET hold angle ended");
            combatStopPath.Invoke(path, new object[] { false, "COMET hold angle ended" });
        }
        r.Path = null;
    }

    private bool HoldAngleGaze(BotComponent b, BotState s, out Vector3 direction)
    {
        direction = default;
        var r = s.HoldAngle;
        if (!r.Policy.Active || r.Policy.Trace!.State == "planning")
            return false;

        direction = (r.Aim - b.Transform.WeaponRoot).normalized;
        return true;
    }

    private void EndHoldAngle(BotState s, string reason, bool reconsider = false)
    {
        var r = s.HoldAngle;
        r.Peek?.Reset();
        r.Peek = null;
        r.Started = -1;
        if (!r.Policy.Active || config == null)
            return;
        // Cleanup must not query a destroyed Unity actor or a replacement enemy.
        var f = r.LastFrame
            ?? new HoldAngleFrame
            {
                Source = r.Source,
                Known = DP(r.Known),
                ObservedAt = r.ObservedAt,
            };
        f.Now = Time.time;
        r.Policy.End(reason, f, config.HoldAngle, reconsider);
        FinishHoldAngle(s, reconsider);
    }

    private void FinishHoldAngle(BotState s, bool reconsider)
    {
        var r = s.HoldAngle;
        try
        {
            StopHoldPath(s);
            if (
                r.Owner != null
                && !r.Owner.IsDead
                && r.OwnPose
                && Math.Abs(r.Owner.Mover.Pose.PoseValue.TargetValue - r.AppliedPose) < .01
            )
                r.Owner.Mover.SetTargetPose(r.OldPose);

            RecordHoldAngle(s, true);
        }
        finally
        {
            r.Drop();
        }
    }

    private void HoldAngleError(BotState s, Exception ex)
    {
        s.HoldAngle.Fault = true;
        EndHoldAngle(s, "hold-angle-adapter-error");
        Logger.LogWarning("M08 yielded: " + ex.GetBaseException().Message);
    }

    private void RecordHoldAngle(BotState s, bool force = false)
    {
        var r = s.HoldAngle;
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
            + t.PoseRequested
            + ":"
            + t.MoveRequested;
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
                feature = "M08",
                stage = "evaluated",
                decision = "HoldAngle",
                reason = t.Reason,
                realtime = Time.realtimeSinceStartup,
                utc = DateTime.UtcNow.ToString("O"),
                configRevision = config?.Revision ?? 0,
                planningWaitSeconds = r.Policy.Preparation.Seconds,
                profileId = s.ProfileId,
                name = s.Name,
                botInstance = s.Id,
                holdAngle = t,
                holdAngleConfig = config?.HoldAngle,
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
