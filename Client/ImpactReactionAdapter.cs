using System;
using System.Collections.Generic;
using Comet.Core;
using EFT;
using EFT.Ballistics;
using HarmonyLib;
using SAIN.Components;
using SAIN.Models.Enums;
using SAIN.Models.Structs;
using SAIN.Preset;
using SAIN.Preset.Shared.Enums;
using SAIN.SAINComponent.Classes;
using SAIN.SAINComponent.Classes.Decision;
using SAIN.SAINComponent.Classes.EnemyClasses;
using UnityEngine;

namespace Comet.Client;

internal sealed class ImpactRuntime
{
    internal readonly ImpactReaction Memory;
    internal bool Owned;
    internal float NextLog,
        LastHeartbeat;
    internal int Coalesced;
    internal string LogKey = "";

    internal ImpactRuntime(ImpactReactionConfig config)
    {
        Memory = new ImpactReaction(config);
    }
}

public sealed partial class Plugin
{
    private static int impactProbeFrame = -1;
    private readonly Dictionary<string, int> impactCounters = new();
    private float nextImpactSummary;

    private static void PatchImpactReaction(Harmony h)
    {
        // Before GetEnemy: the source first registers a previously unknown shooter, then updates hit status.
        h.Patch(
            AccessTools.Method(
                typeof(BotHitByEnemyClass),
                nameof(BotHitByEnemyClass.GetHit),
                new[] { typeof(DamageInfo), typeof(EBodyPart), typeof(float) }
            ),
            prefix: new HarmonyMethod(typeof(Plugin), nameof(ImpactHit))
        );
    }

    private static bool ModdedImpactType(BotComponent b) =>
        BotTypeDefinitions.BotTypes.TryGetValue(b.Info.Profile.WildSpawnType, out var type)
        && ModdedImpactRules.Registered(type.BrainsToApply?.Count ?? 0, type.BaseBrain);

    private static void ImpactHit(BotHitByEnemyClass __instance, DamageInfo __0)
    {
        var p = Instance;
        var b = __instance.Bot;
        if (
            p == null
            || p.world == null
            || p.config?.Enabled != true
            || !p.config.ImpactReaction.Enabled
            || !p.Authority
            || !b.BotActive
            || b.IsDead
            || b.GameEnding
            || p.Ghost(b.ProfileId)
        )
            return;
        // Narrow source exception only. This does not admit modded roles into any other COMET tactic.
        if (!ModdedImpactType(b))
        {
            p.CountImpact("impact-standard-role-native");
            return;
        }
        var shooter = __0.Player?.iPlayer;
        if (shooter?.HealthController?.IsAlive != true)
            return;
        try
        {
            var enemy = b.EnemyController.CheckAddEnemy(shooter);
            if (enemy == null)
            {
                var group = b.BotOwner.BotsGroup;
                if (
                    group == null
                    || group.Enemies.ContainsKey(shooter)
                    || group.IsAlly(shooter)
                    || !group.IsPlayerEnemy(shooter)
                )
                {
                    p.CountImpact("impact-modded-not-hostile");
                    return;
                }
                group.AddEnemy(shooter, EBotEnemyCause.checkAddTODO);
                enemy = b.EnemyController.CheckAddEnemy(shooter);
                if (enemy == null)
                {
                    p.CountImpact("impact-modded-contact-unavailable");
                    return;
                }
            }
            bool wasKnown = enemy.EnemyKnown;
            var report = new SAINHearingReport
            {
                position = shooter.Position,
                soundType = SAINSoundType.Shot,
                placeType = EEnemyPlaceType.Injury,
                isDanger = true,
                shallReportToSquad = false,
            };
            enemy.KnownPlaces.UpdatePersonalHeardPosition(report, Time.time);
            b.Memory.SetUnderFire(enemy, shooter.Position);
            string reason = wasKnown
                ? "impact-modded-injury-refreshed"
                : "impact-modded-shooter-revealed";
            p.CountImpact(reason);
            // Telemetry is emitted after the source action and must not be an eligibility gate.
            var s = p.GrenadeState(b);
            p.RecordImpact(
                s,
                new ImpactTrace
                {
                    State = "observed",
                    Reason = reason,
                    Contact = shooter.ProfileId,
                    Evidence = "zzap-modded-injury-position",
                    At = Time.time,
                    NativeAccepted = true,
                    Hits = 1,
                }
            );
        }
        catch (Exception ex)
        {
            p.CountImpact("impact-adapter-error");
            p.Logger.LogWarning("COMET P07: " + ex.Message);
        }
    }

    private bool ImpactDecisionObserved(BotDecisionManager d, BotState s) => false;

    private void RefreshImpact(BotComponent b, BotState s) { }

    private void EndImpact(BotComponent b, BotState s, string reason)
    {
        s.Impact.Owned = false;
        s.Impact.Memory.End(Time.time, reason);
    }

    private void CountImpact(string reason)
    {
        impactCounters.TryGetValue(reason, out int n);
        impactCounters[reason] = n + 1;
    }

    private void RecordImpact(BotState s, ImpactTrace t, bool force = false)
    {
        var r = s.Impact;
        string key = t.State + ":" + t.Reason + ":" + t.Observed;
        if (!force && Time.time < r.NextLog)
        {
            r.Coalesced++;
            return;
        }
        if (
            !force
            && key == r.LogKey
            && Time.time - r.LastHeartbeat < .5f
            && t.State == "requested"
        )
            return;
        r.NextLog = Time.time + .5f;
        r.LogKey = key;
        var json = Diagnostics.Serialize(
            new
            {
                schema = 1,
                session,
                sequence = ++sequence,
                feature = "P07",
                stage = "evaluated",
                decision = "ImpactReaction",
                reason = t.Reason,
                realtime = Time.realtimeSinceStartup,
                utc = DateTime.UtcNow.ToString("O"),
                cometVersion = CometVersion.Release,
                configRevision = config?.Revision ?? 0,
                impactConfig = config?.ImpactReaction,
                profileId = s.ProfileId,
                name = s.Name,
                botInstance = s.Id,
                impact = t,
                coalesced = r.Coalesced,
            }
        );
        r.Coalesced = 0;
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

    private void FlushImpactCounters(bool force = false)
    {
        if (!force && Time.time < nextImpactSummary)
            return;
        nextImpactSummary = Time.time + 10;
        if (impactCounters.Count == 0)
            return;
        diagnostics?.WriteEssential(
            Diagnostics.Serialize(
                new
                {
                    schema = 1,
                    session,
                    feature = "P07",
                    stage = "summary",
                    realtime = Time.realtimeSinceStartup,
                    counters = impactCounters,
                }
            )
        );
        impactCounters.Clear();
    }
}
