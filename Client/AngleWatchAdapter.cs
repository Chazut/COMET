using System;
using System.Collections.Generic;
using Comet.Core;
using EFT;
using HarmonyLib;
using Newtonsoft.Json;
using SAIN.Components;
using SAIN.Models.Enums;
using SAIN.Preset.Shared.Enums;
using SAIN.Preset.Shared.GlobalSettings;
using SAIN.SAINComponent.Classes.Mover;
using UnityEngine;
using UnityEngine.AI;

namespace Comet.Client;

internal sealed class AngleWatchRuntime
{
    internal readonly AngleWatch Policy = new();
    internal object? Enemy;
    internal Vector3 Seen,
        ProbePosition,
        ProbeDirection,
        ProbeKnown;
    internal float SeenAt = -1000,
        GeometryAt = -1000,
        NextGeometry,
        CallbackAt,
        LogAt = -1000,
        SampleAt = -1000;
    internal bool Wall,
        Fault,
        OwnLean;
    internal LeanClass? LeanOwner;
    internal LeanSetting Applied;
    internal AngleCandidate[] Candidates = Array.Empty<AngleCandidate>();
    internal string LogKey = "",
        ProbeSource = "";
    internal float SelectedAt,
        WallSide = 1;
    internal int ClearChecks;
}

public sealed partial class Plugin
{
    private struct AngleRestore
    {
        internal bool Changed;
        internal Vector3 Original;
    }

    private static void PatchAngleWatch(Harmony h)
    {
        var method = AccessTools.Method(typeof(SAINSteeringClass), "TickPlayerSteering")
            ?? throw new MissingMethodException("M07 native steering tick");
        if (
            AccessTools.Field(typeof(SAINSteeringClass), "_targetLookDirection")?.FieldType
            != typeof(Vector3)
        )
            throw new MissingFieldException("M07 native gaze direction");
        h.Patch(
            method,
            prefix: new HarmonyMethod(typeof(Plugin), nameof(AngleSteering)),
            postfix: new HarmonyMethod(typeof(Plugin), nameof(RestoreAngleSteering))
        );
    }

    private void SampleAngle(BotComponent b, BotState s)
    {
        var r = s.AngleWatch;
        if (r.Fault)
            return;
        if (
            config?.Enabled != true
            || !config.AngleWatch.Enabled
            || b == null
            || b.IsDead
            || !b.BotActive
            || !Authority
            || Ghost(s.ProfileId)
            || !NativeAdapter.IsSupportedAi(b)
        )
        {
            r.Enemy = null;
            r.SeenAt = -1000;
            EndAngle(s, "angle-ineligible");
            return;
        }
        var e = b.GoalEnemy;
        if (!SameTacticalContact(r.Enemy, e))
        {
            EndAngle(s, "angle-contact-changed");
            r.Enemy = e;
            r.SeenAt = r.GeometryAt = -1000;
            r.NextGeometry = 0;
            r.Candidates = Array.Empty<AngleCandidate>();
        }
        if (
            e?.IsVisible == true
            && Time.time - r.SampleAt >= .1f
            && e.KnownPlaces.LastSeenPlace is { } seen
        )
        {
            r.Seen = seen.Position;
            r.SeenAt = Time.time;
            r.SampleAt = Time.time;
        }
    }

    private AngleWatchFrame AngleFrame(BotComponent b, BotState s, Vector3 native)
    {
        var r = s.AngleWatch;
        var c = config!.AngleWatch;
        var e = b.GoalEnemy;
        var combat = CombatStepFrame(b, s);
        var f = new AngleWatchFrame
        {
            Now = Time.time,
            Target = e?.EnemyProfileId ?? "",
            Action = b.Decision.CurrentCombatDecision.ToString(),
            Position = DP(b.Position),
            Origin = DP(b.Transform.WeaponRoot),
            NativeDirection = DP(native),
            ObservedDirection = DP(
                b.PlayerComponent.CharacterController.CurrentControlLookDirection
            ),
            Moving = b.Player.Velocity.magnitude >= .5f,
        };
        if (e?.KnownPlaces.LastKnownPosition is Vector3 known)
        {
            f.Source = e.IsVisible ? "personal-vision" : "sain-known-contact";
            f.Known = DP(known);
            f.ObservedAt = Time.time - e.TimeSinceLastKnownUpdated;
        }
        if (!combat.Enabled || !combat.Pmc || !combat.Authority || combat.Ghost || !combat.Active)
            f.Block = "angle-ineligible";
        else if (e == null || e.IsVisible)
            f.Block = "angle-visible-or-no-enemy";
        else if (e.Seen && e.TimeSinceSeen < .5f || s.Sight.Current != null)
            f.Block = "angle-recent-sight";
        else if (s.RetreatWeave.Policy.Active)
            f.Block = "angle-retreat-head-down";
        return f;
    }

    private static void AngleSteering(
        SAINSteeringClass __instance,
        ref Vector3 ____targetLookDirection,
        out AngleRestore __state
    )
    {
        __state = default;
        var p = Instance;
        var b = __instance.Bot;
        if (p?.config == null || !p.states.TryGetValue(b, out var s))
            return;
        if (s.SiegeExit != null && p.SiegeGaze(b, s, out var exitDirection))
        {
            __state = new() { Changed = true, Original = ____targetLookDirection };
            ____targetLookDirection = exitDirection;
            return;
        }
        try
        {
            if (p.WeaveGaze(b, s, out var weaveDirection))
            {
                __state = new() { Changed = true, Original = ____targetLookDirection };
                ____targetLookDirection = weaveDirection;
                return;
            }
        }
        catch (Exception ex)
        {
            p.WeaveError(s, ex);
        }
        try
        {
            if (p.CoverTravelGaze(b, s, out var travelDirection))
            {
                __state = new() { Changed = true, Original = ____targetLookDirection };
                ____targetLookDirection = travelDirection;
                return;
            }
        }
        catch (Exception ex)
        {
            p.CoverTravelError(s, ex);
        }
        try
        {
            if (p.HoldAngleGaze(b, s, out var holdDirection))
            {
                __state = new() { Changed = true, Original = ____targetLookDirection };
                ____targetLookDirection = holdDirection;
                return;
            }
        }
        catch (Exception ex)
        {
            p.HoldAngleError(s, ex);
        }
        if (s.AngleWatch.Fault)
            return;
        var r = s.AngleWatch;
        try
        {
            p.SampleAngle(b, s);
            r.CallbackAt = Time.time;
            var f = p.AngleFrame(b, s, ____targetLookDirection);
            if (f.Block.Length > 0 || f.Source.Length == 0 || !p.config.AngleWatch.Enabled)
            {
                p.EndAngle(s, f.Block.Length > 0 ? f.Block : "angle-no-evidence-or-disabled");
                return;
            }
            p.AngleGeometry(b, s, f);
            var previous = r.Policy.Trace?.Candidate;
            if (!r.Policy.Tick(f, p.config.AngleWatch))
            {
                p.ReleaseAngleLean(s);
                p.RecordAngle(s);
                return;
            }
            if (previous != r.Policy.Trace!.Candidate)
            {
                r.SelectedAt = Time.time;
                r.WallSide = Vector3.SignedAngle(
                    VP(f.NativeDirection),
                    VP(r.Policy.Aim) - b.Transform.WeaponRoot,
                    Vector3.up
                );
            }
            // Only this invocation uses the prepared direction. Do not leave a stale gaze in SAIN's backing field.
            __state = new() { Changed = true, Original = ____targetLookDirection };
            ____targetLookDirection = (VP(r.Policy.Aim) - b.Transform.WeaponRoot).normalized;
            if (r.Policy.Trace!.Kind == "corner")
                b.Mover.ActivePath?.RequestEndSprint(
                    ESprintUrgency.None,
                    "COMET prepare nearby known corner"
                );
            if (r.Policy.Side == 0)
                p.ReleaseAngleLean(s);
            p.RecordAngle(s);
        }
        catch (Exception ex)
        {
            p.AngleError(s, ex);
        }
    }

    private static void RestoreAngleSteering(
        ref Vector3 ____targetLookDirection,
        AngleRestore __state
    )
    {
        if (__state.Changed)
            ____targetLookDirection = __state.Original;
    }

    private void AngleGeometry(BotComponent b, BotState s, AngleWatchFrame f)
    {
        var r = s.AngleWatch;
        var c = config!.AngleWatch;
        var e = b.GoalEnemy;
        if (Time.time - r.GeometryAt < c.Refresh)
        {
            f.Candidates = r.Candidates;
            f.WallBlocked = r.Wall;
            return;
        }
        var options = new List<AngleCandidate>();
        var root = b.Transform.WeaponRoot;
        var eye = b.Transform.EyePosition;
        var native = VP(f.NativeDirection);
        int mask = LayersMaskController.HighPolyWithTerrainMask;
        var corner = e?.VisiblePathPoint;
        bool recent = e?.Seen == true && e.TimeSinceSeen < 1f;
        string action = f.Action;
        if (
            corner is Vector3 point
            && f.Moving
            && !recent
            && e!.TimeSinceLastKnownUpdated <= 30
            && b.BotOwner.Medecine?.Using != true
            && action != "AvoidGrenade"
            && action != "Retreat"
            && action != "RunAway"
            && !s.Entry.Policy.Active
            && !s.RoomClear.Policy.Active
        )
        {
            float dist = Vector3.Distance(b.Position, point);
            if (dist >= .6f && dist <= c.CornerRange)
            {
                var lean = b.Mover.Lean.FindLeanFromBlindCornerAngle(e);
                options.Add(
                    new()
                    {
                        Id = "near-corner",
                        Kind = "corner",
                        Aim = DP(
                            SourcePastCorner(
                                root,
                                point,
                                e.KnownPlaces.LastKnownPosition,
                                b.LookDirection
                            )
                        ),
                        Clear = true,
                        LeanSafe = true,
                        Side =
                            lean == LeanSetting.Left ? -1
                            : lean == LeanSetting.Right ? 1
                            : 0,
                    }
                );
            }
        }
        r.Wall =
            native.sqrMagnitude > .01f
            && Physics.Raycast(
                eye,
                native.normalized,
                (float)(f.Moving ? c.MovingWall : c.StillWall),
                mask
            );
        bool aimingBlocks =
            b.Aim.AimStatus != AimStatus.NoTarget
            && !Physics.Raycast(eye, native.normalized, .8f, mask);
        if (c.WallCorrection && r.Wall && !aimingBlocks)
        {
            if (
                r.Policy.Active
                && r.Policy.Trace!.Kind == "wall"
                && (
                    Time.time - r.SelectedAt < c.HoldMinimum
                    || !Physics.Raycast(eye, (VP(r.Policy.Aim) - root).normalized, 3f, mask)
                )
            )
                options.Add(
                    new()
                    {
                        Id = "kept-wall",
                        Kind = "wall",
                        Aim = DP(root + (VP(r.Policy.Aim) - root).normalized * 3),
                        Clear = true,
                    }
                );
            if (corner is Vector3 p)
            {
                var dir = p + Vector3.up * 1.3f - eye;
                float dist = dir.magnitude;
                if (dist > 1.5f && !Physics.Raycast(eye, dir / dist, Mathf.Min(dist, 4f), mask))
                    options.Add(
                        new()
                        {
                            Id = "wall-corner",
                            Kind = "wall",
                            Aim = DP(SourceClampPitch(eye, eye + dir, native)),
                            Clear = true,
                        }
                    );
            }
            var flat = new Vector3(native.x, 0, native.z).normalized;
            foreach (float step in new[] { 20f, -20f, 40f, -40f, 60f, -60f, 90f, -90f })
            {
                var dir = Quaternion.AngleAxis(r.WallSide < 0 ? -step : step, Vector3.up) * flat;
                if (flat.sqrMagnitude > .01 && !Physics.Raycast(eye, dir, 3f, mask))
                {
                    options.Add(
                        new()
                        {
                            Id = "opening-" + step,
                            Kind = "wall",
                            Aim = DP(root + dir * 3),
                            Clear = true,
                        }
                    );
                    break;
                }
            }
            r.ClearChecks = 0;
        }
        else if (
            !r.Wall
            && r.Policy.Active
            && r.Policy.Trace!.Kind == "wall"
            && ++r.ClearChecks < 2
        )
            options.Add(
                new()
                {
                    Id = "kept-wall",
                    Kind = "wall",
                    Aim = r.Policy.Aim,
                    Clear = true,
                }
            );
        r.Candidates = options.ToArray();
        r.GeometryAt = Time.time;
        f.GeometryAt = r.GeometryAt;
        f.Candidates = r.Candidates;
        f.WallBlocked = r.Wall || r.ClearChecks == 1;
    }

    // R_F / zzap SAINSteeringClass: hold beyond the wall edge, at a level firing angle.
    private static Vector3 SourceClampPitch(Vector3 from, Vector3 target, Vector3 forward)
    {
        var d = target - from;
        float h = new Vector3(d.x, 0, d.z).magnitude;
        if (h < .3f)
        {
            var f = new Vector3(forward.x, 0, forward.z);
            return from + (f.sqrMagnitude > .001f ? f.normalized : Vector3.forward) * 2;
        }
        float rise = Mathf.Max(h, 1.5f) * Mathf.Tan(10f * Mathf.Deg2Rad);
        return new(target.x, from.y + Mathf.Clamp(d.y, -rise, rise), target.z);
    }

    private static Vector3 SourcePastCorner(
        Vector3 root,
        Vector3 corner,
        Vector3? enemy,
        Vector3 forward
    )
    {
        var p = corner;
        if (enemy is Vector3 known)
        {
            var d = known - corner;
            d.y = 0;
            float len = d.magnitude;
            if (len > .5f)
                p = corner + d / len * Mathf.Min(1, len);
            var v = corner - root;
            v.y = 0;
            var e = known - root;
            e.y = 0;
            float dist = v.magnitude;
            if (dist > .5f)
            {
                float cross = v.x * e.z - v.z * e.x;
                if (Mathf.Abs(cross) > .01f)
                    p +=
                        new Vector3(-v.z, 0, v.x)
                        / dist
                        * (cross > 0 ? 1 : -1)
                        * Mathf.Clamp(dist * .3f, .6f, 1.5f);
            }
        }
        return SourceClampPitch(root, p + Vector3.up * 1.3f, forward);
    }

    private static bool AngleLane(Vector3 origin, Vector3 point, float radius)
    {
        var delta = point - origin;
        if (
            !CornerChase.Point(DP(origin))
            || !CornerChase.Point(DP(point))
            || delta.magnitude < .1f
        )
            return false;
        int mask = LayersMaskController.HighPolyWithTerrainMask;
        return !Physics.CheckSphere(origin, radius, mask, QueryTriggerInteraction.Ignore)
            && !Physics.SphereCast(
                origin,
                radius,
                delta.normalized,
                out _,
                delta.magnitude,
                mask,
                QueryTriggerInteraction.Ignore
            );
    }

    private bool WatchLean(LeanClass lean, BotState s)
    {
        var r = s.AngleWatch;
        if (!r.Policy.Active || r.Policy.Side == 0)
            return false;
        var f = AngleFrame(lean.Bot, s, lean.Bot.LookDirection);
        if (
            f.Block.Length > 0
            || !lean.Bot.Info.FileSettings.Move.LEAN_TOGGLE
            || !SainPresetAccess.AllowsLean
        )
        {
            ReleaseAngleLean(s);
            return false;
        }
        r.Applied = r.Policy.Side < 0 ? LeanSetting.Left : LeanSetting.Right;
        r.OwnLean = true;
        r.LeanOwner = lean;
        lean.FastLean(r.Applied);
        r.Policy.Trace!.LeanRequested = true;
        return true;
    }

    private void ReleaseAngleLean(BotState s)
    {
        var r = s.AngleWatch;
        var lean = r.LeanOwner;
        try
        {
            if (
                r.OwnLean
                && lean != null
                && Authority
                && !lean.IsHoldingLean
                && lean.Bot.Cover.CoverInUse == null
                && lean.LeanDirection == r.Applied
            )
                lean.FastLean(LeanSetting.None);
        }
        finally
        {
            r.OwnLean = false;
            r.LeanOwner = null;
        }
    }

    private void EndAngle(BotState s, string reason)
    {
        s.AngleWatch.Policy.End(reason);
        ReleaseAngleLean(s);
        RecordAngle(s);
    }

    private void AngleError(BotState s, Exception ex)
    {
        s.AngleWatch.Fault = true;
        s.AngleWatch.Enemy = null;
        s.AngleWatch.Candidates = Array.Empty<AngleCandidate>();
        try
        {
            EndAngle(s, "angle-adapter-error");
        }
        finally
        {
            Logger.LogWarning("M07 yielded: " + ex.GetBaseException().Message);
        }
    }

    private void RecordAngle(BotState s)
    {
        var r = s.AngleWatch;
        var t = r.Policy.Trace;
        if (t == null)
            return;
        t.LeanRequested = r.OwnLean;
        string key =
            t.Episode + ":" + t.State + ":" + t.Reason + ":" + t.Candidate + ":" + t.Aligned;
        if (key == r.LogKey && (!r.Policy.Active || Time.time - r.LogAt < .1f))
            return;
        r.LogKey = key;
        r.LogAt = Time.time;
        var json = Diagnostics.Serialize(
            new
            {
                schema = 1,
                session,
                sequence = ++sequence,
                feature = "M07",
                stage = "evaluated",
                decision = "AngleWatch",
                reason = t.Reason,
                realtime = Time.realtimeSinceStartup,
                utc = DateTime.UtcNow.ToString("O"),
                configRevision = config?.Revision ?? 0,
                profileId = s.ProfileId,
                name = s.Name,
                botInstance = s.Id,
                angleWatch = t,
                angleWatchConfig = config?.AngleWatch,
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
