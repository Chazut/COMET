using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Comet.Core;
using EFT;
using Newtonsoft.Json;
using SAIN.Components;
using SAIN.SAINComponent.Classes.EnemyClasses;
using UnityEngine;

namespace Comet.Client;

internal sealed class EncounterRuntime
{
    internal readonly EncounterMemory Memory = new();
    internal EncounterTrace? Trace;
    internal float SampleAt = -100,
        ProbeAt = -100,
        LogAt = -100;
    internal int ProbeInstance,
        Surface,
        Side;
    internal Vector3 ProbeSeen,
        ProbeEye,
        Edge,
        Normal;
    internal bool Geometry,
        Fault;
    internal string LogKey = "";
}

public sealed partial class Plugin
{
    private void SampleEncounter(BotComponent b, BotState s)
    {
        var r = s.Encounter;
        try
        {
            if (r.Fault)
                return;
            if (
                config?.Enabled != true
                || !config.Encounter.Enabled
                || b == null
                || b.IsDead
                || !b.BotActive
                || b.GameEnding
                || !Authority
                || Ghost(s.ProfileId)
                || !NativeAdapter.IsSupportedAi(b)
            )
            {
                EndEncounter(s, "encounter-out-of-scope");
                return;
            }
            if (Time.time - r.SampleAt < .09f)
                return;
            r.SampleAt = Time.time;
            // Native known-list membership is necessary even when this observer changes target.
            var known = new HashSet<string>();
            int scanned = 0;
            foreach (var contact in b.EnemyController.KnownEnemies)
            {
                if (++scanned > 32)
                {
                    EndEncounter(s, "encounter-contact-budget");
                    return;
                }
                if (
                    contact != null
                    && ThreatUsable(b, contact)
                    && contact.TimeSinceLastKnownUpdated < config.ContactLifetime
                )
                    known.Add(contact.EnemyProfileId);
            }
            r.Memory.Retain(known);
            var e = b.GoalEnemy;
            if (e == null || !known.Contains(e.EnemyProfileId))
            {
                EndEncounter(s, "encounter-native-contact-ended");
                return;
            }
            var seen = e.KnownPlaces.LastSeenPlace;
            var f = new EncounterFrame
            {
                Now = Time.time,
                Target = e.EnemyProfileId,
                Instance = RuntimeHelpers.GetHashCode(e),
                ContactAge = e.TimeSinceLastKnownUpdated,
                ContactLifetime = config.ContactLifetime,
                HasContact = seen != null,
                Visible = e.IsVisible && seen != null,
                Seen = seen == null ? default : DP(seen.Position),
                Observer = DP(b.Position),
                PersonalLatest = ReferenceEquals(e.KnownPlaces.LastKnownPlace, seen),
            };
            if (seen != null)
            {
                var direction = seen.Position + Vector3.up - b.Player.PlayerBones.Head.position;
                f.Watching =
                    Vector3.Angle(
                        b.PlayerComponent.CharacterController.CurrentControlLookDirection,
                        direction
                    ) <= 60;
                if (f.Watching)
                    ProbeEncounter(b, r, f, seen.Position);
            }
            var previous = r.Trace;
            r.Trace = r.Memory.Sample(f, config.Encounter);
            RecordEncounter(s, previous?.Count > 0 && r.Trace.Count == 0);
        }
        catch (Exception ex)
        {
            r.Fault = true;
            EndEncounter(s, "encounter-adapter-error");
            Logger.LogWarning("COMET E03 disabled for this bot: " + ex.Message);
        }
    }

    private void ProbeEncounter(BotComponent b, EncounterRuntime r, EncounterFrame f, Vector3 seen)
    {
        var eye = b.Player.PlayerBones.Head.position;
        bool changed =
            r.ProbeInstance != f.Instance
            || Vector3.Distance(seen, r.ProbeSeen) > .25f
            || Vector3.Distance(eye, r.ProbeEye) > .25f;
        if (changed)
            r.Geometry = false;
        if ((changed || Time.time - r.ProbeAt >= .25f) && doorBudget.TryBegin(Time.frameCount, b))
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                r.ProbeAt = Time.time;
                r.ProbeInstance = f.Instance;
                r.ProbeSeen = seen;
                r.ProbeEye = eye;
                r.Geometry = false;
                var center = seen + Vector3.up;
                var forward = center - eye;
                forward.y = 0;
                if (forward.sqrMagnitude < 1)
                    return;
                var lateral = Vector3.Cross(Vector3.up, forward.normalized);
                int mask = LayersMaskController.HighPolyWithTerrainMask;
                // The point and two flanks are local geometry around remembered sight, not a hidden enemy lookup.
                if (Physics.Linecast(eye, center, mask, QueryTriggerInteraction.Ignore))
                    return;
                for (int side = -1; side <= 1; side += 2)
                {
                    var endpoint = center + lateral * side;
                    if (
                        !Physics.Linecast(
                            eye,
                            endpoint,
                            out var hit,
                            mask,
                            QueryTriggerInteraction.Ignore
                        )
                        || hit.collider == null
                        || Vector3.Distance(hit.point, center) > 2.5f
                        || Math.Abs(hit.normal.y) > .5f
                    )
                        continue;
                    r.Surface = hit.collider.GetInstanceID();
                    r.Side = side;
                    r.Edge = hit.point;
                    r.Normal = hit.normal;
                    r.Geometry = true;
                    break;
                }
            }
            finally
            {
                watch.Stop();
                doorBudget.Charge(watch.Elapsed.TotalMilliseconds);
            }
        }
        f.Geometry = r.Geometry && Time.time - r.ProbeAt <= .3f;
        if (f.Geometry)
        {
            f.Surface = r.Surface;
            f.Side = r.Side;
            f.Edge = DP(r.Edge);
            f.Normal = DP(r.Normal);
        }
    }

    private EncounterTrace? ReadEncounter(BotComponent b, Enemy e, BotState s, Situation situation)
    {
        SampleEncounter(b, s);
        var t = s.Encounter.Trace;
        if (
            t == null
            || t.Target != e.EnemyProfileId
            || t.Instance != RuntimeHelpers.GetHashCode(e)
            || Time.time - s.Encounter.SampleAt > .2f
        )
            return null;
        t = t.Copy();
        if (
            situation.Visible
            || !ReferenceEquals(e.KnownPlaces.LastKnownPlace, e.KnownPlaces.LastSeenPlace)
            || !EncounterMemory.Near(
                new(t.X, t.Y, t.Z),
                new(situation.KnownX, situation.KnownY, situation.KnownZ),
                config!.Encounter.PositionTolerance,
                config.Encounter.HeightTolerance
            )
        )
        {
            t.Eligible = false;
            t.Bonus = 0;
            t.Reason = "encounter-new-or-visible-contact";
        }
        return t;
    }

    private void RecordEncounterChoice(BotState s, Situation situation)
    {
        if (situation.Encounter == null)
            return;
        situation.Encounter.Choice = s.Decision;
        situation.Encounter.Alternatives = s
            .Candidates.OrderByDescending(x => x.Score)
            .Take(6)
            .Select(x =>
                x.Decision
                + ": "
                + (
                    x.Eligible
                        ? "score "
                            + x.Score.ToString(
                                "0.0",
                                System.Globalization.CultureInfo.InvariantCulture
                            )
                        : x.Reason
                )
            )
            .ToArray();
        s.Encounter.Trace = situation.Encounter.Copy();
    }

    private void EndEncounter(BotState s, string reason)
    {
        var r = s.Encounter;
        r.Memory.Clear();
        r.Geometry = false;
        r.ProbeAt = -100;
        if (r.Trace != null && r.Trace.State != "ended")
        {
            r.Trace = r.Trace.Copy();
            r.Trace.State = "ended";
            r.Trace.Reason = reason;
            r.Trace.Eligible = false;
            r.Trace.Bonus = r.Trace.AppliedBonus = r.Trace.ValidFor = 0;
            RecordEncounter(s, true);
        }
        r.Trace = null;
    }

    private void RecordEncounter(BotState s, bool force = false)
    {
        var r = s.Encounter;
        var t = r.Trace;
        if (t == null || (!force && t.Count == 0))
            return;
        string key =
            t.State
            + ":"
            + t.Reason
            + ":"
            + t.Count
            + ":"
            + t.Choice
            + ":"
            + t.AppliedBonus
            + ":"
            + t.Target;
        if (!force && key == r.LogKey && Time.time - r.LogAt < .25f)
            return;
        r.LogKey = key;
        r.LogAt = Time.time;
        if (!Comet.Core.ReplayCapture.Detailed)
            return;
        var json = Diagnostics.Serialize(
            new
            {
                schema = 1,
                session,
                sequence = ++sequence,
                feature = "E03",
                stage = "evaluated",
                decision = "EncounterAdaptation",
                reason = t.Reason,
                realtime = Time.realtimeSinceStartup,
                utc = DateTime.UtcNow.ToString("O"),
                configRevision = config?.Revision ?? 0,
                encounterConfig = config?.Encounter,
                profileId = s.ProfileId,
                name = s.Name,
                botInstance = s.Id,
                encounter = t,
                candidates = t.Choice.Length > 0 ? s.Candidates : null,
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
