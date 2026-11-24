using System;
using System.Collections.Generic;
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
using SAIN.SAINComponent.Classes.Mover;
using SAIN.SAINComponent.Classes.WeaponFunction;
using SAIN.SAINComponent.SubComponents;
using UnityEngine;
using UnityEngine.AI;

namespace Comet.Client;

internal sealed class GrenadeEscapeRuntime
{
    internal readonly List<OwnGrenadeEvidence> Dangers = new();
    internal readonly HashSet<string> Escaping = new(StringComparer.Ordinal);
    internal readonly HashSet<string> Live = new(StringComparer.Ordinal);
    internal readonly OwnGrenadeSafetyConfig Safety = new();
    internal GrenadeTrackerClass? ReportedDanger;
    internal EGrenadeReaction ReportedReaction;
    internal bool NativeReported;
    internal bool NativeMismatchReported;
    internal readonly List<Vector3> Candidates = new();
    internal readonly List<string> Alternatives = new();
    internal readonly List<GrenadeDispersionLease> DispersionPeers = new();
    internal GrenadeDispersionLease? DispersionLease;
    internal GrenadeDispersionTrace? DispersionTrace;
    internal float DispersionRenewAt,
        DispersionLogAt = -1000;
    internal GrenadeTrackerClass? Selected;
    internal OwnGrenadeEvidence? Hazard;
    internal GrenadeReactionTrace? Trace;
    internal IBotPathData? Path;
    internal NavMeshPath? Mesh;
    internal Vector3 Goal,
        CommittedDanger;
    internal DoorPoint? KnownContact;
    internal float Next,
        CheckAt,
        CommitUntil,
        RetryAt,
        PathCheckAt,
        LogAt = -1000;
    internal int Candidate,
        CoverCandidates;
    internal bool Fault,
        Prone,
        Handled;
    internal string Reaction = "Scatter",
        Key = "",
        Contact = "",
        ContactSource = "";
}

public sealed partial class Plugin
{
    private int escapeProbeFrame = -1,
        escapeProbes;

    private static void PatchGrenadeReaction(Harmony h)
    {
        var action = AccessTools.TypeByName("SAIN.Layers.Combat.Solo.AvoidGrenadeAction")
            ?? throw new MissingMemberException("G05 native grenade action");
        h.Patch(
            AccessTools.Method(action, "Update"),
            prefix: new HarmonyMethod(typeof(Plugin), nameof(EscapeAction))
            {
                priority = Priority.First,
            }
        );
        h.Patch(
            AccessTools.Method(action, "Stop"),
            postfix: new HarmonyMethod(typeof(Plugin), nameof(EscapeActionStopped))
        );
    }

    private bool EscapeScope(BotComponent b) =>
        config?.Enabled == true
        && config.GrenadeReaction.Enabled
        && NativeAdapter.IsSupportedAi(b)
        && !b.IsDead
        && b.BotActive
        && !b.GameEnding
        && b.SAINLayersActive
        && Authority
        && !Ghost(b.ProfileId);

    // Called by the existing G04 postfix: a single writer arbitrates native danger selection.
    private bool SelectGrenadeEscape(GrenadeReactionClass native)
    {
        var b = native.Bot;
        states.TryGetValue(b, out var existing);
        // With no native or own danger and no pending reaction, the original
        // result already matches G05. Keep the normal transition/cleanup path
        // for every bot that has actually handled a grenade.
        if (native.EnemyGrenadesList.Count == 0 && native.DangerGrenade == null &&
            native.Reaction == EGrenadeReaction.None && ownProjectiles.Count == 0 &&
            (existing == null || existing.GrenadeEscape.Next == 0 && !existing.GrenadeEscape.NativeReported))
            return false;
        if (!EscapeScope(b))
        {
            if (existing != null)
                DropGrenadeEscape(b, existing, "grenade-out-of-scope");
            return false;
        }
        var s = existing ?? GrenadeState(b);
        var r = s.GrenadeEscape;
        if (r.Fault)
            return false;
        try
        {
            if (Time.time >= r.Next)
            {
                r.Next = Time.time + (float)config!.GrenadeReaction.Refresh;
                r.Dangers.Clear();
                var live = r.Live;
                live.Clear();
                r.Safety.ExitPadding = config.GrenadeReaction.ExitPadding;
                GrenadeTrackerClass? selected = null;
                OwnGrenadeEvidence? selectedHazard = null;
                double best = 18; // zzap GRENADE_REACT_DISTANCE
                void Add(GrenadeTrackerClass? tracker, OwnGrenadeEvidence e, bool own)
                {
                    // Enemy lifetime belongs to SAIN's live tracker. The eight-second
                    // source retention below applies only to our own thrown grenades.
                    if (
                        e.Exploded
                        || own
                            && (
                                e.Expired(Time.time)
                                || e.Owner == b.ProfileId
                                    && e.Grace(Time.time, config.OwnGrenadeSafety)
                            )
                    )
                        return;
                    r.Dangers.Add(e);
                    string key = e.Owner + ":" + e.Projectile;
                    live.Add(key);
                    bool danger = own
                        ? OwnGrenadeSafetyRules.Threat(
                            e,
                            DP(b.Position),
                            r.Escaping.Contains(key),
                            Time.time,
                            r.Safety,
                            OwnSolidCover,
                            false
                        )
                        : (e.Position - DP(b.Position)).Length <= 7
                            || !OwnSolidCover(
                                DP(b.Position + Vector3.up * 1.2f),
                                e.Position + new DoorPoint(0, .1, 0)
                            );
                    if (!danger)
                    {
                        r.Escaping.Remove(key);
                        return;
                    }
                    r.Escaping.Add(key);
                    double score = (e.Position - DP(b.Position)).Length;
                    if (tracker != null && score < best)
                    {
                        best = score;
                        selected = tracker;
                        selectedHazard = e;
                    }
                }
                foreach (var t in native.EnemyGrenadesList.Values)
                {
                    if (
                        t?.Grenade == null
                        || !t.CanReact
                        || GrenadeKinds.IsSmoke(t.Grenade)
                        || t.Grenade.WeaponSource is not ThrowWeap item
                        || item.IsDummy
                        || item.ThrowType.ToString() is "smoke_grenade" or "gas_grenade"
                    )
                        continue;
                    double radius =
                        ClearDangerRadius(item) - 1 + config.GrenadeReaction.DangerMargin;
                    if (
                        !GrenadeChoiceRules.Finite(radius)
                        || radius <= 0
                        || radius > 150
                        || !DP(t.DangerPoint).Finite
                    )
                        continue;

                    Add(
                        t,
                        new()
                        {
                            Owner = t.Grenade.ProfileId,
                            Item = item.Id.ToString(),
                            Projectile = t.Grenade.Id,
                            Position = DP(t.DangerPoint),
                            Radius = radius,
                        },
                        false
                    );
                }
                if (config.OwnGrenadeSafety.Enabled)
                {
                    SampleOwnProjectiles();
                    foreach (var own in ownProjectiles)
                        if (own.Informed.Contains(b.ProfileId))
                        {
                            var e = own.Evidence;
                            bool duplicate = false;
                            foreach (var known in r.Dangers)
                                if (known.Owner == e.Owner && known.Projectile == e.Projectile)
                                {
                                    duplicate = true;
                                    break;
                                }
                            if (duplicate)
                                continue;
                            Add(
                                ReferenceEquals(s.OwnGrenade.Selected, own)
                                    ? s.OwnGrenade.Tracker
                                    : null,
                                e,
                                true
                            );
                        }
                }
                r.Escaping.RemoveWhere(x => !live.Contains(x));
                if (
                    !ReferenceEquals(r.Selected, selected)
                    || r.Hazard != null
                        && selectedHazard != null
                        && (r.Hazard.Position - selectedHazard.Position).Length > .5
                )
                {
                    r.Candidates.Clear();
                    r.RetryAt = 0;
                    r.CheckAt = 0;
                }
                r.Selected = selected;
                r.Hazard = selectedHazard;
                r.PathCheckAt = 0;
                if (selected == null)
                {
                    StopEscapePath(b, r);
                    r.Candidates.Clear();
                    r.RetryAt = 0;
                    EndGrenadeTrace(s, "grenade-safe-or-native-track-ended");
                }
                else if (r.Trace == null || r.Trace.State == "ended")
                    Release(s, "grenade-escape-priority");
            }
            // Only verified, live CS gas owns its native dodge window. Smoke must
            // not preserve an old Scatter reaction and strand AvoidGrenadeAction.
            if (IsNativeGasDanger(native.DangerGrenade))
            {
                DropGrenadeEscape(b, s, "grenade-special-native-priority");
                return false;
            }
            if (r.Selected?.Grenade == null)
            {
                r.Selected = null;
                r.Hazard = null;
                StopEscapePath(b, r);
                EndGrenadeTrace(s, "grenade-native-track-ended");
            }
            r.Handled = r.Selected != null && r.Hazard != null;
            RecordNativeGrenadeTransition(b, s, native);
            if (!ReferenceEquals(native.DangerGrenade, r.Selected))
                ownDangerSetter.Invoke(native, new object?[] { r.Selected });
            // Tactical movement is supplied in AvoidGrenadeAction. Native safety selection stays non-aggressive.
            var reaction = r.Selected == null ? EGrenadeReaction.None : EGrenadeReaction.Scatter;
            if (native.Reaction != reaction)
                ownReactionSetter.Invoke(native, new object[] { reaction });
            return true;
        }
        catch (Exception ex)
        {
            EscapeError(b, s, ex);
            return false;
        }
    }

    private static bool EscapeAction(BotAction __instance)
    {
        var p = Instance;
        var b = __instance.Bot;
        if (p?.TryGasAction(b) == true)
            return false;
        if (
            p == null
            || !p.EscapeScope(b)
            || !p.states.TryGetValue(b, out var s)
            || s.GrenadeEscape.Fault
            || !s.GrenadeEscape.Handled
        )
            return true;
        if (s.GrenadeEscape.Hazard == null || s.GrenadeEscape.Selected?.Grenade == null)
        {
            p.DropGrenadeEscape(b, s, "grenade-action-track-ended");
            return true;
        }
        try
        {
            p.UpdateGrenadeEscape(b, s);
            return false;
        }
        catch (Exception ex)
        {
            p.EscapeError(b, s, ex);
            return true;
        }
    }

    private void UpdateGrenadeEscape(BotComponent b, BotState s)
    {
        var r = s.GrenadeEscape;
        var c = config!.GrenadeReaction;
        if (r.Hazard == null || r.Selected?.Grenade == null)
            return;
        var danger = VP(r.Hazard.Position);
        if (
            r.Path != null
            && (danger - r.CommittedDanger).sqrMagnitude <= 16
            && (b.Position - r.Goal).sqrMagnitude >= 4
            && Time.time < r.CommitUntil
        )
            return;
        r.CommittedDanger = danger;
        r.CommitUntil = Time.time + (float)c.Commitment;
        var enemy = b.GoalEnemy;
        float distance = Vector3.Distance(b.Position, danger);
        r.Reaction =
            distance < 6
                ? "Scatter"
                : GrenadeReactionRules.Preference(b.Info.Personality.ToString(), enemy != null);
        r.Contact = enemy?.EnemyProfileId ?? "";
        r.ContactSource = enemy == null ? "" : RelocationContactSource(enemy);
        var away = b.Position - danger;
        away.y = 0;
        if (away.sqrMagnitude < .01f)
        {
            away = -b.LookDirection;
            away.y = 0;
        }
        away.Normalize();
        if (r.Reaction == "Push" && distance > 6 && enemy != null)
        {
            var to = enemy.EnemyPosition - b.Position;
            to.y = 0;
            if (to.sqrMagnitude > .01)
            {
                to.Normalize();
                if (Vector3.Dot(away, to) > 0)
                    away = (away + to).normalized;
            }
        }
        int index = 0;
        if (config.GrenadeDispersion.Enabled && b.Squad.BotInGroup)
        {
            foreach (var id in b.Squad.Members.Keys)
            {
                if (id == b.ProfileId)
                    break;
                index++;
            }
        }
        if (index > 0)
            away =
                Quaternion.Euler(0, 35 * ((index + 1) / 2) * (index % 2 == 0 ? 1 : -1), 0) * away;
        r.Alternatives.Clear();
        float metres = 8 - distance;
        if (metres > 0 && b.Grenade.GrenadeReactionClass.EstimatedTimeRemaining * 5 < metres)
        {
            b.Mover.Prone.SetProne(true);
            r.Prone = true;
            r.Path = null;
            RecordGrenadeEscape(b, s, "prone", "grenade-source-blast-imminent");
            return;
        }
        bool sprint = distance < 8 || r.Reaction is "Retreat" or "Scatter";
        bool Start(Vector3 goal, bool move)
        {
            if (!move)
                return false;
            r.Goal = goal;
            r.Path = b.Mover.ActivePath;
            r.Mesh = r.Path?.NavMeshPath;
            r.Prone = false;
            b.Mover.Prone.SetProne(false);
            if (index > 0)
                ReserveDispersion(b, s, new GrenadeDispersionAssessment(), false);
            RecordGrenadeEscape(b, s, "moving", "grenade-source-escape-selected", true);
            return true;
        }
        if (distance >= 8)
        {
            var cp = b.Cover.FindPointInDirection(-away, .25f, r.Reaction == "Relocate" ? 4 : 8);
            if (
                cp != null
                && Start(
                    cp.Position,
                    b.Mover.GoToCoverPoint(
                        cp,
                        sprint,
                        sprint ? ESprintUrgency.High : ESprintUrgency.Low
                    )
                )
            )
                return;
        }
        float range = (float)(r.Reaction == "Retreat" ? c.RetreatDistance : c.ScatterDistance);
        for (int step = 0; step < 3; step++)
            foreach (float angle in new[] { 0f, 40f, -40f, 80f, -80f, 130f, -130f })
            {
                var target =
                    b.Position + Quaternion.Euler(0, angle, 0) * away * range * (1 - step * .33f);
                if (!NavMesh.SamplePosition(target, out var nav, 3, NavMesh.AllAreas))
                    continue;
                bool moving = sprint
                    ? b.Mover.RunToPoint(nav.position, false, -1, ESprintUrgency.High)
                    : b.Mover.WalkToPoint(nav.position, false);
                if (Start(nav.position, moving))
                    return;
            }
        r.Path = null;
        RecordGrenadeEscape(b, s, "planning", "grenade-native-movement-refused");
    }

    private static void EscapeActionStopped(BotAction __instance)
    {
        if (Instance is { } p && p.states.TryGetValue(__instance.Bot, out var s))
        {
            p.StopEscapePath(__instance.Bot, s.GrenadeEscape);
            s.GrenadeEscape.Candidates.Clear();
            p.EndGrenadeTrace(s, "grenade-native-action-ended");
        }
    }

    private void StopEscapePath(BotComponent? b, GrenadeEscapeRuntime r)
    {
        ReleaseDispersion(b, r, "dispersion-route-released");
        if (
            b != null
            && r.Path != null
            && ReferenceEquals(b.Mover.ActivePath, r.Path)
            && ReferenceEquals(r.Path.NavMeshPath, r.Mesh)
        )
            combatStopPath.Invoke(r.Path, new object[] { false, "COMET G05 escape released" });
        if (b != null && r.Prone)
            b.Mover.Prone.SetProne(false);
        r.Prone = false;
        r.Path = null;
        r.Mesh = null;
    }

    private void DropGrenadeEscape(BotComponent? b, BotState s, string reason)
    {
        var r = s.GrenadeEscape;
        StopEscapePath(b, r);
        if (
            b != null
            && r.Selected != null
            && ReferenceEquals(b.Grenade.GrenadeReactionClass.DangerGrenade, r.Selected)
        )
        {
            ownDangerSetter.Invoke(b.Grenade.GrenadeReactionClass, new object?[] { null });
            ownReactionSetter.Invoke(
                b.Grenade.GrenadeReactionClass,
                new object[] { EGrenadeReaction.None }
            );
        }
        r.Selected = null;
        r.Hazard = null;
        r.Dangers.Clear();
        r.Escaping.Clear();
        r.Live.Clear();
        r.Candidates.Clear();
        r.Handled = false;
        r.Next = 0;
        r.RetryAt = 0;
        EndGrenadeTrace(s, reason);
        r.ReportedDanger = null;
        r.NativeReported = false;
        r.NativeMismatchReported = false;
    }

    private void EscapeError(BotComponent b, BotState s, Exception ex)
    {
        if (!s.GrenadeEscape.Fault)
            Logger.LogWarning("G05 yielded to SAIN: " + ex.GetBaseException().Message);
        s.GrenadeEscape.Fault = true;
        DropGrenadeEscape(b, s, "grenade-adapter-error");
    }

    private void EndGrenadeTrace(BotState s, string reason)
    {
        if (s.GrenadeEscape.Trace is not { } t || t.State == "ended")
            return;
        t.State = "ended";
        t.Reason = reason;
        t.ValidFor = 0;
        WriteGrenadeTrace(s, true);
    }

    private void RecordNativeGrenadeTransition(
        BotComponent b,
        BotState s,
        GrenadeReactionClass native
    )
    {
        var r = s.GrenadeEscape;
        var reaction = r.Handled ? EGrenadeReaction.Scatter : EGrenadeReaction.None;
        bool mismatch =
            !ReferenceEquals(native.DangerGrenade, r.Selected) || native.Reaction != reaction;
        bool alreadyReported =
            r.NativeReported
            && ReferenceEquals(r.ReportedDanger, r.Selected)
            && r.ReportedReaction == reaction
            && (!mismatch || r.NativeMismatchReported);
        r.NativeMismatchReported = mismatch;
        if (alreadyReported)
            return;
        bool relevant =
            r.NativeReported
            || native.DangerGrenade != null
            || native.Reaction != EGrenadeReaction.None
            || r.Handled;
        r.NativeReported = true;
        r.ReportedDanger = r.Selected;
        r.ReportedReaction = reaction;
        if (!relevant)
            return;
        var json = Diagnostics.Serialize(
            new
            {
                schema = 1,
                session,
                sequence = ++sequence,
                feature = "G05",
                stage = "native-reaction",
                reason = r.Handled
                    ? "grenade-native-danger-selected"
                    : "grenade-native-danger-cleared",
                realtime = Time.realtimeSinceStartup,
                utc = DateTime.UtcNow.ToString("O"),
                profileId = s.ProfileId,
                name = s.Name,
                botInstance = s.Id,
                nativeBefore = native.Reaction.ToString(),
                reaction = reaction.ToString(),
                previousProjectile = native.DangerGrenade?.Grenade?.Id,
                projectile = r.Selected?.Grenade?.Id,
                tracked = native.EnemyGrenadesList.Count,
                handled = r.Handled,
                distance = r.Hazard == null
                    ? (double?)null
                    : (DP(b.Position) - r.Hazard.Position).Length,
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

    private void RecordGrenadeEscape(
        BotComponent b,
        BotState s,
        string state,
        string reason,
        bool force = false
    )
    {
        var r = s.GrenadeEscape;
        var e = r.Hazard;
        if (e == null)
            return;
        double length = 0;
        var corners = r.Mesh?.corners;
        if (corners != null)
            for (int i = 1; i < corners.Length; i++)
                length += Vector3.Distance(corners[i - 1], corners[i]);
        r.Trace = new()
        {
            State = state,
            Reason = reason,
            Reaction = r.Reaction,
            Personality = b.Info.Personality.ToString(),
            Owner = e.Owner,
            Item = e.Item,
            Projectile = e.Projectile,
            Source =
                e.Owner == b.ProfileId ? "own-throw"
                : r.Selected == s.OwnGrenade.Tracker ? "observed-squad-throw"
                : "sain-detected",
            Active = r.Dangers.Count,
            X = e.Position.X,
            Y = e.Position.Y,
            Z = e.Position.Z,
            Radius = e.Radius,
            Distance = (DP(b.Position) - e.Position).Length,
            HasGoal = r.Path != null,
            GoalX = r.Goal.x,
            GoalY = r.Goal.y,
            GoalZ = r.Goal.z,
            PathLength = length,
            Contact = r.Contact,
            ContactSource = r.ContactSource,
            Alternatives = r.Alternatives.ToArray(),
        };
        WriteGrenadeTrace(s, force);
    }

    private void WriteGrenadeTrace(BotState s, bool force = false)
    {
        var r = s.GrenadeEscape;
        var t = r.Trace;
        if (t == null)
            return;
        string key = t.State + "|" + t.Reason + "|" + t.Owner + "|" + t.Projectile;
        if (!force && key == r.Key && Time.time - r.LogAt < .4f)
            return;
        r.Key = key;
        r.LogAt = Time.time;
        var json = Diagnostics.Serialize(
            new
            {
                schema = 1,
                session,
                sequence = ++sequence,
                feature = "G05",
                stage = "evaluated",
                decision = "GrenadeReaction",
                reason = t.Reason,
                realtime = Time.realtimeSinceStartup,
                utc = DateTime.UtcNow.ToString("O"),
                configRevision = config?.Revision ?? 0,
                profileId = s.ProfileId,
                name = s.Name,
                botInstance = s.Id,
                grenadeReaction = t,
                grenadeReactionConfig = config?.GrenadeReaction,
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
