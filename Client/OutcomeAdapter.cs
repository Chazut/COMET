using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Comet.Core;
using EFT;
using EFT.Ballistics;
using HarmonyLib;
using Newtonsoft.Json;
using SAIN.Components;
using SAIN.Helpers;
using SAIN.Preset.Shared.Enums;
using SAIN.SAINComponent.Classes;
using SAIN.SAINComponent.Classes.EnemyClasses;
using UnityEngine;

namespace Comet.Client;

internal sealed class OutcomeRuntime
{
    internal readonly TacticalOutcomes Memory = new();
    internal readonly HashSet<string> Known = new();
    internal readonly (string Action, DoorPoint? Destination)[] Options = new (string, DoorPoint?)[
        7
    ];
    internal float At = -100,
        LogAt = -100;
    internal int LogEpisode = -1;
    internal string LogState = "",
        LogReason = "",
        LogResult = "";
    internal bool Fault;
}

public sealed partial class Plugin
{
    private static void PatchOutcomes(Harmony h)
    {
        var hit = AccessTools.Method(
            typeof(SAINBotMedicalClass),
            nameof(SAINBotMedicalClass.GetHit),
            new[] { typeof(DamageInfo), typeof(EBodyPart), typeof(float) }
        );
        if (hit == null)
            throw new MissingMethodException("E04 native own-hit contract");
        h.Patch(hit, postfix: new HarmonyMethod(typeof(Plugin), nameof(OutcomeHit)));
    }

    private bool OutcomeScoped(BotComponent b) =>
        config?.Enabled == true
        && config.Outcomes.Enabled
        && Authority
        && b != null
        && NativeAdapter.IsSupportedAi(b)
        && b.BotActive
        && !b.IsDead
        && !b.GameEnding
        && !Ghost(b.ProfileId);

    private static bool OutcomeAction(BotComponent b, string action)
    {
        var type = b.CurrentAction?.GetType();
        if (type == null || type.Assembly != typeof(BotComponent).Assembly)
            return false;
        if (action == "Grenade suppression - hold angle")
            action =
                b.Decision.CurrentCombatDecision == ECombatDecision.StandAndShoot
                    ? "Shoot"
                    : "Hold";
        string name = action switch
        {
            "Hold" => "FreezeAction",
            "Shoot" => "StandAndShootAction",
            "Push" => "RushEnemyAction",
            "DogFight" => "DogFightAction",
            "Search" => "SearchAction",
            "Flank" => "ShiftCoverAction",
            "Cover" or "FallBack" => "SeekCoverAction",
            "Grenade" or "Grenade suppression - throw" => "ThrowGrenadeAction",
            _ => "",
        };
        return name.Length > 0 && type.Name == name;
    }

    private OutcomeFrame CaptureOutcome(BotComponent b, BotState s)
    {
        var e = b.GoalEnemy;
        var known = e?.KnownPlaces.LastKnownPosition;
        string action = ExecutionAction(s);
        var d = b.Decision;
        var f = new OutcomeFrame
        {
            Now = Time.time,
            Scoped = OutcomeScoped(b),
            Target = e?.EnemyProfileId ?? "",
            Instance = e == null ? 0 : RuntimeHelpers.GetHashCode(e),
            HasContact = e != null && known.HasValue && ThreatUsable(b, e),
            ContactAge = e?.TimeSinceLastKnownUpdated ?? 0,
            ContactLifetime = config!.ContactLifetime,
            Known = known.HasValue ? DP(known.Value) : default,
            Origin = DP(b.Position),
            Visible = e?.IsVisible == true,
            Health = b
                .Player.HealthController.GetBodyPartHealth(EBodyPart.Common, false)
                .Normalized,
            Hurt = b.Memory.Health.HealthStatus != ETagStatus.Healthy,
            Action = action,
            Owned =
                (s.Feature is "A01" or "A02" or "A03")
                && s.ActiveLabel.Length > 0
                && d.CurrentCombatDecision == s.Expected
                && b.SAINLayersActive
                && OutcomeAction(b, action),
            ForcedRetreat =
                d.CurrentCombatDecision is ECombatDecision.Retreat or ECombatDecision.SeekCover
                || d.CurrentSelfDecision != ESelfActionType.None,
            Paused =
                d.CurrentSelfDecision != ESelfActionType.None
                || d.CurrentSquadDecision != ESquadDecision.None
                || b.BotOwner.DoorOpener.Interacting
                || !VisibleAdapter.WeaponReady(b),
        };
        var destination = OutcomeDestination(b, s, action);
        f.HasDestination = destination.HasValue;
        f.Destination = destination ?? default;
        var p = s.Probe;
        if (
            p != null
            && Time.time - p.At >= 0
            && Time.time - p.At <= .75
            && Vector3.Distance(p.Origin, b.Position) <= .5f
            && p.Threats.Any(x =>
                x.Id == f.Target && known.HasValue && Vector3.Distance(x.Position, known.Value) <= 2
            )
        )
        {
            f.ExposureKnown = true;
            f.Exposure = p.Current;
        }
        var cover = b.Cover.CoverInUse;
        f.Protected =
            cover != null
            && VisibleAdapter.ValidCover(b, cover)
            && Vector3.Distance(b.Position, cover.Position) <= 1.5f;
        return f;
    }

    private static DoorPoint? OutcomeDestination(BotComponent b, BotState s, string action)
    {
        if (action is "Push" or "Search")
        {
            var p = b.GoalEnemy?.KnownPlaces.LastKnownPosition;
            return p.HasValue ? DP(p.Value) : null;
        }
        if (action is "Cover" or "FallBack" or "Flank")
        {
            var p =
                action == "Flank"
                    ? s.Flank.Point
                    : s.SelectedCover ?? b.Cover.CoverPoint_MovingTo ?? b.Cover.CoverInUse;
            return p == null ? null : DP(p.Position);
        }
        return null;
    }

    private void SampleOutcome(BotComponent b, BotState s)
    {
        var r = s.Outcomes;
        try
        {
            if (r.Fault)
                return;
            if (!OutcomeScoped(b))
            {
                EndOutcome(s, "outcome-out-of-scope");
                return;
            }
            if (Time.time - r.At < .1f)
                return;
            r.At = Time.time;
            var known = r.Known;
            known.Clear();
            int count = 0;
            foreach (var e in b.EnemyController.KnownEnemies)
            {
                if (++count > 32)
                {
                    EndOutcome(s, "outcome-contact-budget");
                    return;
                }
                if (
                    e != null
                    && ThreatUsable(b, e)
                    && e.TimeSinceLastKnownUpdated < config!.ContactLifetime
                )
                    known.Add(e.EnemyProfileId);
            }
            r.Memory.Retain(known);
            var f = CaptureOutcome(b, s);
            f.HasContact &= known.Contains(f.Target);
            var t = r.Memory.Observe(f, config!.Outcomes);
            RecordOutcome(s, t);
        }
        catch (Exception ex)
        {
            OutcomeError(s, ex);
        }
    }

    private static void OutcomeHit(SAINBotMedicalClass __instance, DamageInfo __0, float __2)
    {
        var p = Instance;
        var b = __instance.Bot;
        if (p == null || !p.states.TryGetValue(b, out var s) || s.Outcomes.Fault)
            return;
        try
        {
            if (!p.OutcomeScoped(b) || !s.Outcomes.Memory.HasAttempt)
                return;
            string recognized = "";
            int instance = 0;
            var shooter = __0.Player?.iPlayer;
            if (
                __0.DamageType == EDamageType.Bullet
                && !__0.DelayedDamage
                && shooter != null
                && shooter.ProfileId != b.ProfileId
            )
            {
                var e = b.EnemyController.GetEnemy(shooter.ProfileId, true);
                if (
                    e != null
                    && p.ThreatUsable(b, e)
                    && e.TimeSinceLastKnownUpdated < p.config!.ContactLifetime
                )
                {
                    var seen = e.KnownPlaces.LastSeenPlace;
                    var heard = e.KnownPlaces.LastHeardPlace;
                    if (
                        e.IsVisible
                            && seen != null
                            && seen.TimeSincePositionUpdated >= 0
                            && seen.TimeSincePositionUpdated <= .5
                        || heard != null
                            && heard.SoundType.HasValue
                            && heard.SoundType.Value.IsGunShot()
                            && heard.TimeSincePositionUpdated >= 0
                            && heard.TimeSincePositionUpdated <= .75
                    )
                    {
                        recognized = e.EnemyProfileId;
                        instance = RuntimeHelpers.GetHashCode(e);
                    }
                }
            }
            // The engine damage owner only qualifies a personally perceived attacker; no position or health is read.
            s.Outcomes.Memory.Hit(Time.time, recognized, instance, __2);
        }
        catch (Exception ex)
        {
            p.OutcomeError(s, ex);
        }
    }

    private OutcomeTrace? ReviewOutcome(BotComponent b, BotState s)
    {
        if (s.Outcomes.Fault || !OutcomeScoped(b))
            return null;
        try
        {
            var f = CaptureOutcome(b, s);
            var options = s.Outcomes.Options;
            for (int i = 0; i < OutcomeActions.Length; i++)
                options[i] = (OutcomeActions[i], OutcomeDestination(b, s, OutcomeActions[i]));
            return s.Outcomes.Memory.Review(f, options, config!.Outcomes);
        }
        catch (Exception ex)
        {
            OutcomeError(s, ex);
            return null;
        }
    }

    private static readonly string[] OutcomeActions =
    {
        "Hold",
        "Shoot",
        "Push",
        "Search",
        "Flank",
        "Cover",
        "FallBack",
    };

    private static void RecordOutcomeChoice(BotState s, Situation situation)
    {
        var t = situation.Outcome;
        if (t == null)
            return;
        t.Choice = s.Decision;
        t.Alternatives = s
            .Candidates.OrderByDescending(x => x.Score)
            .Take(6)
            .Select(x =>
                x.Decision
                + ": "
                + (
                    x.Eligible
                        ? x.Score.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)
                        : x.Reason
                )
            )
            .ToArray();
    }

    private void EndOutcome(BotState s, string reason)
    {
        var r = s.Outcomes;
        bool had = r.Memory.HasAttempt || r.Memory.HistoryCount > 0;
        r.Memory.Clear(reason);
        r.Known.Clear();
        if (had)
            RecordOutcome(s, r.Memory.Trace, true);
        if (s.Situation != null)
            s.Situation.Outcome = null;
    }

    private void OutcomeError(BotState s, Exception ex)
    {
        s.Outcomes.Fault = true;
        EndOutcome(s, "outcome-adapter-error");
        Logger.LogWarning("COMET E04 disabled for this bot: " + ex.Message);
    }

    private void RecordOutcome(BotState s, OutcomeTrace t, bool force = false)
    {
        var r = s.Outcomes;
        if (!force && (t.Episode == 0 || t.State == "ended"))
            return;
        if (
            !force
            && t.Episode == r.LogEpisode
            && t.State == r.LogState
            && t.Reason == r.LogReason
            && t.Result == r.LogResult
            && Time.time - r.LogAt < .25f
        )
            return;
        r.LogEpisode = t.Episode;
        r.LogState = t.State;
        r.LogReason = t.Reason;
        r.LogResult = t.Result;
        r.LogAt = Time.time;
        if ((t.Remaining <= 0 || t.ValidFor <= 0) && !force)
            return;
        if (!Comet.Core.ReplayCapture.Detailed)
            return;
        var json = Diagnostics.Serialize(
            new
            {
                schema = 1,
                session,
                sequence = ++sequence,
                feature = "E04",
                stage = "evaluated",
                decision = "TacticalOutcome",
                reason = t.Reason,
                realtime = Time.realtimeSinceStartup,
                utc = DateTime.UtcNow.ToString("O"),
                configRevision = config?.Revision ?? 0,
                outcomeConfig = config?.Outcomes,
                profileId = s.ProfileId,
                name = s.Name,
                botInstance = s.Id,
                outcome = t,
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
