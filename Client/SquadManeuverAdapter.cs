using System;
using System.Collections.Generic;
using Comet.Core;
using EFT;
using Newtonsoft.Json;
using SAIN.Components;
using SAIN.Models.Enums;
using SAIN.Preset.Shared.Enums;
using UnityEngine;

namespace Comet.Client;

public sealed partial class Plugin
{
    private readonly SquadManeuverBoard maneuverBoard = new();

    private static string ManeuverGroup(BotComponent b) => SquadMemberIdentity.Get(b.Squad.Members);

    private static Vector3 ManeuverRearWatch(BotComponent b, Vector3 known)
    {
        var away = Vector3.ProjectOnPlane(b.Position - known, Vector3.up);
        return b.Position + (away.sqrMagnitude > .01f ? away.normalized : -b.Transform.LookDirection) * 10f;
    }

    private bool TryBeginManeuver(BotComponent b, BotState s)
    {
        var c = config!.SquadManeuver;
        var r = s.Crossfire;
        if (!c.Enabled || Time.time < r.ManeuverNext || r.Policy.Active)
            return false;
        r.ManeuverNext = Time.time + (float)config.Crossfire.Interval;
        var enemy = b.GoalEnemy!;
        var known = enemy.KnownPlaces.LastKnownPosition!.Value;
        double distance = Vector3.Distance(b.Position, known);
        if (distance < c.MinDistance || distance > c.MaxDistance)
            return false;
        string group = ManeuverGroup(b);
        var plan = maneuverBoard.Get(group);
        if (plan?.Active != true)
        {
            if (NativeStormActive(b))
                return DeclineSquad(s, "S03", "maneuver-squad-storm-active");
            var candidates = new List<ManeuverMember>();
            foreach (var mate in b.Squad.Members.Values)
            {
                if (
                    mate == null
                    || !CrossfireScope(mate)
                    || mate.GoalEnemy?.EnemyProfileId != enemy.EnemyProfileId
                )
                    continue;
                if (Vector3.Distance(b.Position, mate.Position) > c.SquadDistance)
                    continue;
                states.TryGetValue(mate, out var ms);
                if (ms != null
                    && (ms.Door.SessionPlan.Length > 0 || DoorPlanActive(ms) || ms.Entry.Policy.Active))
                    continue;
                if (mate.GoalEnemy.IsVisible)
                    return DeclineSquad(s, "S03", SquadManeuverBoard.SomeoneSees);
                if (ms == null)
                {
                    if (states.Count >= 512)
                        continue;
                    ms = new BotState(mate, config, session);
                    states.Add(mate, ms);
                }
                if (
                    ms.Crossfire.Policy.Active
                    || ms.Crossfire.Fault
                    || Time.time < ms.Crossfire.Policy.RetryAt
                    || CrossfireBlock(mate, ms, maneuverCandidate: true).Length > 0
                    || !CrossfireCanAcquire(mate)
                )
                    continue;
                var mk = mate.GoalEnemy.KnownPlaces.LastKnownPosition;
                if (mk == null || mate.GoalEnemy.TimeSinceLastKnownUpdated > c.ContactAge)
                    continue;
                double md = Vector3.Distance(mate.Position, mk.Value);
                if (md < c.MinDistance || md > c.MaxDistance)
                    continue;
                double ammo = TacticalAmmoPercent(mate.BotOwner);
                var health = mate.Memory.Health.HealthStatus;
                candidates.Add(
                    new()
                    {
                        Id = mate.ProfileId,
                        Name = mate.Player.Profile.Nickname,
                        Personality = mate.Info.Personality.ToString(),
                        Ammo = ammo,
                        MovementBlock = health is ETagStatus.BadlyInjured or ETagStatus.Dying
                            ? "flanker-severely-injured"
                            : mate.Decision.CurrentSelfDecision != ESelfActionType.None ? "flanker-self-action" : "",
                        ContactDistanceSquared = Vector3.ProjectOnPlane(mate.Position - known, Vector3.up).sqrMagnitude,
                        Score = SquadManeuverBoard.Score(
                            mate.Info.Personality.ToString(),
                            health == ETagStatus.Healthy,
                            ammo,
                            c
                        ),
                    }
                );
            }
            var previous = maneuverBoard.LastAttempt;
            plan = maneuverBoard.TryStart(
                group,
                enemy.EnemyProfileId,
                Time.time,
                Time.time - enemy.TimeSinceLastKnownUpdated,
                jumpRandom.NextDouble(),
                candidates,
                c
            );
            if (plan == null)
                RecordCrossfireGate(s, "maneuver-not-started: " + maneuverBoard.LastReason);
            if (
                plan == null
                && maneuverBoard.LastAttempt is SquadManeuverPlan declined
                && !ReferenceEquals(previous, declined)
                && declined.Member(b.ProfileId) != null
            )
                RecordManeuverDeclined(b, s, declined);
        }
        if (
            plan == null
            || !plan.Active
            || plan.Enemy != enemy.EnemyProfileId
            || plan.Member(b.ProfileId) is not ManeuverMember role
            || role.Role == "unassigned"
            || role.Status is "ended" or "engaging"
        )
            return false;
        BotComponent? support = null;
        Vector3 center = Vector3.zero;
        int count = 0;
        foreach (var member in plan.Members)
        {
            if (member.Role != "support" || !b.Squad.Members.TryGetValue(member.Id, out var bot)
                || bot == null || bot.IsDead)
                continue;
            support ??= bot;
            center += bot.Position;
            count++;
        }
        r.Maneuver = plan;
        r.ManeuverRole = role;
        r.ManeuverStarted = Time.time;
        var begin = new CrossfireConfig { Participation = 100 };
        r.Policy.Begin(Time.time, begin, 0);
        if (!r.Policy.Active)
        {
            plan.Drop(b.ProfileId, "crossfire-unavailable");
            r.Maneuver = null;
            r.ManeuverRole = null;
            return false;
        }
        r.ContactInstance = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(enemy);
        r.Known = known;
        r.Contact = enemy.EnemyProfileId;
        r.ContactAt = Time.time - enemy.TimeSinceLastKnownUpdated;
        r.Support = support?.ProfileId ?? "";
        r.Reference = count > 0 ? center / count : b.Position;
        r.Origin = b.Position;
        r.Candidate = r.Sample = 0;
        r.Route = null;
        r.Rejects.Clear();
        r.Group = group;
        r.Indoor = b.Player.Environment == EnvironmentType.Indoor;
        RememberCrossfireHandoff(b, r);
        var t = r.Policy.Trace!;
        t.Contact = r.Contact;
        t.Source = NativeAdapter.Capture(b, enemy, true, false).ContactSource;
        t.Support = r.Support;
        t.SupportName = support?.Player.Profile.Nickname ?? "";
        t.X = known.x;
        t.Y = known.y;
        t.Z = known.z;
        t.ContactAge = enemy.TimeSinceLastKnownUpdated;
        t.Roll = plan.Roll * 100;
        t.Chance = c.Chance;
        if (role.Role == "flank")
        {
            role.Status = "planning";
            if (!BeginEntrance(b, s))
                BuildCrossfireCandidates(b, r);
        }
        else
        {
            r.Entrance.Drop();
            r.Goal = b.Position;
            r.Watch = role.Role == "rear" ? ManeuverRearWatch(b, known) : known;
            // Mark the current spot for moving bots. Two stationary roles can already be side by side.
            crossfireClaims.Reserve(s.Id, r.Goal.x, r.Goal.y, r.Goal.z, config.Crossfire.Spacing, Time.time, group);
            r.Policy.Ready(r.Goal.x, r.Goal.y, r.Goal.z, 0, 0);
            role.ReadyAt = Time.time;
        }
        t.Maneuver = plan.Trace(b.ProfileId, Time.time, c);
        RecordCrossfire(s, true);
        return true;
    }

    private void RecordManeuverDeclined(BotComponent b, BotState s, SquadManeuverPlan plan)
    {
        var e = b.GoalEnemy!;
        var known = e.KnownPlaces.LastKnownPosition!.Value;
        var t = new CrossfireTrace
        {
            State = "ended",
            Episode = ++s.Crossfire.ManeuverAttempts,
            Reason = plan.Reason,
            Execution = "kept current native plan",
            Contact = e.EnemyProfileId,
            Source = NativeAdapter.Capture(b, e, true, false).ContactSource,
            ContactAge = e.TimeSinceLastKnownUpdated,
            X = known.x,
            Y = known.y,
            Z = known.z,
            Support = plan.Support,
            SupportName = plan.Member(plan.Support)?.Name ?? "",
            Maneuver = plan.Trace(b.ProfileId, Time.time, config!.SquadManeuver),
            Alternatives = new[] { "Hold the current angle", "Continue native SAIN combat" },
        };
        string json = Diagnostics.Serialize(
            new
            {
                schema = 1,
                session,
                sequence = ++sequence,
                feature = "S03",
                stage = "evaluated",
                decision = "Crossfire",
                reason = t.Reason,
                realtime = Time.realtimeSinceStartup,
                utc = DateTime.UtcNow.ToString("O"),
                configRevision = config.Revision,
                profileId = s.ProfileId,
                name = s.Name,
                botInstance = s.Id,
                crossfire = t,
                squadManeuverConfig = config.SquadManeuver,
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

    private bool ValidateManeuver(BotComponent b, BotState s)
    {
        var r = s.Crossfire;
        var p = r.Maneuver;
        var m = r.ManeuverRole;
        if (p == null || m == null)
            return true;
        var c = config!.SquadManeuver;
        p.Evidence = Math.Max(p.Evidence, r.ContactAt);
        if (!p.Tick(Time.time, c) || m.Status == "ended")
        {
            EndCrossfire(b, s, "maneuver-ended: " + (m.Reason.Length > 0 ? m.Reason : p.Reason));
            return false;
        }
        // Losing an unrelated member must not cancel the remaining support/flank pair.
        // Required support is validated below; departed participating flankers are dropped.
        foreach (var member in p.Members)
            if (
                member.Status != "ended"
                && (
                    !b.Squad.Members.TryGetValue(member.Id, out var bot)
                    || bot == null
                    || bot.IsDead
                    || !bot.BotActive
                )
            )
                p.Drop(member.Id, "maneuver-member-unavailable");
        if (!p.Active || m.Status == "ended")
        {
            EndCrossfire(b, s, "maneuver-required-role-lost");
            return false;
        }
        if (m.Role == "flank" && r.Policy.Trace!.State == "ready" && m.ReadyAt < 0)
        {
            m.ReadyAt = Time.time;
            m.Status = "ready";
        }
        // The live plan owns execution state. Detached member snapshots are built
        // by RecordCrossfire at the diagnostic cadence, not every safety check.
        return true;
    }

    private bool ExecuteManeuverSupport(BotComponent b, BotState s)
    {
        var r = s.Crossfire;
        var m = r.ManeuverRole;
        if (m == null || m.Role == "flank")
            return false;
        // Support may adjust its position or finish an outgoing native route.
        // This never cancels the other members' routes (zzap maneuver semantics).
        if (r.Policy.Trace!.State == "ready")
        {
            r.Policy.Moving(Time.time, 0);
            r.Policy.Progress(Time.time, 0, config!.Crossfire);
        }
        r.Policy.Trace.Reason = m.Confirmed
            ? "maneuver-support-established"
            : "maneuver-support-awaiting-steering";
        r.Policy.Trace.Execution = m.Confirmed
            ? (
                b.GoalEnemy?.IsVisible == true
                    ? "visible anchor; native SAIN aim and fire"
                    : "support angle observed; no flank movement claimed"
            )
            : "awaiting support steering callback";
        // Steering is executed by the native layer callback. Only then is this support confirmed.
        if (r.ManeuverFire && (!r.Maneuver!.MaySuppress || b.GoalEnemy?.IsVisible == true))
        {
            EndDiscipline(s, "suppression-plan-no-longer-needs-fire");
            r.ManeuverFire = false;
        }
        if (
            m.Confirmed
            && m.Role == "support"
            && b.GoalEnemy?.IsVisible != true
            && r.Maneuver!.MaySuppress
            && config!.SuppressionDiscipline.Enabled
            && !s.SuppressionDiscipline.Fault
        )
        {
            var f = DisciplineFrame(b, s, b.GoalEnemy);
            if (
                f.Block.Length == 0
                && !f.Visible
                && f.KnownContact
                && f.ObservedAt >= 0
                && Time.time - f.ObservedAt <= config.SuppressionDiscipline.ContactAge
            )
            {
                r.ManeuverFire = true;
                b.Suppression.TrySuppressAnyEnemy(b.GoalEnemy, b.EnemyController.KnownEnemies);
            }
        }
        return true;
    }

    private void ConfirmManeuver(BotComponent b, BotState s, bool steering = false)
    {
        var r = s.Crossfire;
        var m = r.ManeuverRole;
        var p = r.Maneuver;
        if (m == null || p == null || !p.Active || !r.Layer || !r.Policy.Active)
            return;
        if (
            m.Role == "flank" && r.Policy.Trace!.State is "moving" or "holding"
            || m.Role != "flank"
                && steering
                && Vector3.Angle(
                    b.Player.LookDirection,
                    (m.Role == "rear" ? r.Watch : r.Known) - b.Position
                ) <= 30
        )
        {
            if (!m.Confirmed)
            {
                m.ProgressAt = Time.time;
                m.BestDistance = Vector3.Distance(b.Position, r.Goal);
            }
            m.Confirmed = m.Applied = true;
            m.LastSeen = Time.time;
            m.Status = r.Policy.Trace!.State;
        }
    }

    private void EndManeuver(BotState s, string why)
    {
        var r = s.Crossfire;
        if (r.Maneuver == null || r.ManeuverRole == null)
            return;
        var p = r.Maneuver;
        p.Evidence = Math.Max(p.Evidence, r.ContactAt);
        if (why == "crossfire-angle-engaged")
            p.Engage(s.ProfileId, Time.time);
        else
            p.Drop(s.ProfileId, why);
        if (!p.Active)
            maneuverBoard.Finish(p, Time.time, config?.SquadManeuver ?? new(), p.Reason);
        if (r.Policy.Trace != null)
            r.Policy.Trace.Maneuver = p.Trace(
                s.ProfileId,
                Time.time,
                config?.SquadManeuver ?? new()
            );
        if (r.ManeuverFire)
            EndDiscipline(s, "suppression-support-plan-ended");
        r.ManeuverFire = false;
    }

    private void ManeuverShot(BotState s)
    {
        var r = s.Crossfire;
        var m = r.ManeuverRole;
        if (
            m == null
            || !m.Confirmed
            || m.Role != "support"
            || r.Maneuver?.Active != true
            || !r.Layer
        )
            return;
        m.Shots++;
        m.LastShot = Time.time;
        RecordCrossfire(s, m.Shots == 1);
    }
}
