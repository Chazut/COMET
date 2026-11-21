using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Comet.Core;
using EFT;
using SAIN.Components;
using SAIN.Preset.Shared.Enums;
using SAIN.SAINComponent.Classes.EnemyClasses;
using UnityEngine;
using UnityEngine.AI;

namespace Comet.Client;

internal sealed partial class SquadStormRuntime
{
    internal SquadStormPlan? Plan;
    internal StormMember? Member;
    internal float Next;
}

public sealed partial class Plugin
{
    private readonly SquadStormBoard stormBoard = new(); // Lifecycle-compatible telemetry store.
    private ConditionalWeakTable<BotsGroup, Dictionary<string, NativeStormPlan>> nativeStormPlans = new();

    private sealed class NativeStormPlan
    {
        internal float Until,
            Score;
        internal string Id = "";
        internal string[] Evidence = Array.Empty<string>();
        internal readonly Dictionary<
            string,
            (double roll, double chance, bool joined, bool escortDecline)
        > Members = new();
    }

    private string StormBlock(BotComponent b, BotState s)
    {
        if (!CrossfireScope(b) || !config!.SquadStorm.Enabled)
            return "storm-out-of-scope";
        var e = b.GoalEnemy;
        var c = config.SquadStorm;
        if (e?.EnemyPlayer == null || !e.Seen)
            return "storm-unseen-enemy";
        // S06 is called directly here. In zzap, shallRushEnemy checks both before it asks the squad.
        if (e.Path.PathToEnemyStatus != NavMeshPathStatus.PathComplete)
            return "storm-rush-path-incomplete";
        if (b.Decision.SelfActionDecisions.LowOnAmmo(.5f))
            return "storm-rush-low-ammo";
        if (e.TimeSinceLastKnownUpdated > c.ContactAge)
            return "storm-stale-contact";
        if (e.Path.PathLength > c.MaxPath)
            return "storm-path-too-long";
        if (b.Memory.Health.HealthStatus is ETagStatus.BadlyInjured or ETagStatus.Dying)
            return "storm-injured";
        if (b.Decision.CurrentSelfDecision != ESelfActionType.None)
            return "storm-self-action";
        if (TacticalAmmoPercent(b.BotOwner) < c.MinimumAmmo)
            return "storm-low-ammo";
        return b.BotOwner.BotsGroup == null ? "storm-no-squad" : "";
    }

    private bool ChooseNativeStorm(
        BotComponent b,
        BotState s,
        ref ECombatDecision result,
        ref bool succeeded
    )
    {
        if (StormBlock(b, s).Length > 0)
            return false;
        var e = b.GoalEnemy!;
        var c = config!.SquadStorm;
        var group = b.BotOwner.BotsGroup!; // StormBlock already checked the group.
        var plans = nativeStormPlans.GetOrCreateValue(group);
        if (!plans.TryGetValue(e.EnemyProfileId, out var plan) || Time.time > plan.Until)
        {
            int near = 1;
            foreach (var m in b.Squad.Members.Values)
                if (
                    m != null
                    && !ReferenceEquals(b, m)
                    && !m.IsDead
                    && Vector3.Distance(m.Position, b.Position) < c.SquadDistance
                )
                    near++;
            if (near < 2)
                return false;
            plan = new NativeStormPlan
            {
                Until = Time.time + (float)c.Retry,
                Id = $"storm-{RuntimeHelpers.GetHashCode(group)}/{e.EnemyProfileId}-{Time.frameCount}",
            };
            plan.Score = ZzapEnemyWeakness.Read(b, e, out plan.Evidence);
            plans[e.EnemyProfileId] = plan;
        }
        if (plan.Score < c.Weakness)
            return DeclineSquad(s, "S06", "storm-weakness-below-threshold");
        if (!plan.Members.TryGetValue(b.ProfileId, out var member))
        {
            double baseChance = c.Chance(b.Info.Personality.ToString());
            double chance = AdjustStormChance(b, e, s, baseChance);
            double roll = jumpRandom.NextDouble();
            member = (
                roll,
                chance,
                roll * 100 < chance,
                roll * 100 >= chance && roll * 100 < baseChance
            );
            plan.Members[b.ProfileId] = member;
        }
        if (!member.joined)
            return DeclineSquad(
                s,
                "S06",
                member.escortDecline ? "storm-expected-support" : "storm-personality-choice"
            );
        // zzap chooses SAIN RushEnemy directly: no rendezvous, separate approach, ETA or reserved destination.
        if (s.Crossfire.Policy.Active)
            EndCrossfire(b, s, "storm-native-rush");
        if (s.Feature != "S06" || s.Decision != "Storm")
            Release(s, "storm-native-rush", false, true);
        s.Feature = "S06";
        s.Decision = "Storm";
        s.Expected = ECombatDecision.RushEnemy;
        s.RequestedFrame = Time.frameCount;
        result = s.Expected;
        succeeded = true;
        if (Time.time >= s.Crossfire.Storm.Next)
        {
            s.Crossfire.Storm.Next = Time.time + 1;
            bool applied = b.Decision.CurrentCombatDecision == ECombatDecision.RushEnemy;
            var trace = new CrossfireTrace
            {
                State = "moving",
                Reason = "storm-native-rush",
                Execution = "SAIN RushEnemy",
                Applied = applied,
                ValidFor = applied ? 1 : 0,
                Contact = e.EnemyProfileId,
                Storm = new SquadStormTrace
                {
                    Plan = plan.Id,
                    State = "active",
                    Status = "moving",
                    Reason = "storm-weak-enemy",
                    Score = plan.Score,
                    Threshold = c.Weakness,
                    Roll = member.roll * 100,
                    Chance = member.chance,
                    Confirmed = applied,
                    Remaining = plan.Until - Time.time,
                    Evidence = plan.Evidence,
                    Members = plan
                        .Members.Select(m => m.Key + ": " + (m.Value.joined ? "rush" : "stay back"))
                        .ToArray(),
                },
            };
            string json = Diagnostics.Serialize(
                new
                {
                    schema = 1,
                    session,
                    sequence = ++sequence,
                    feature = "S06",
                    stage = applied ? "applied" : "selected",
                    decision = "Crossfire",
                    reason = "storm-native-rush",
                    realtime = Time.realtimeSinceStartup,
                    utc = DateTime.UtcNow.ToString("O"),
                    profileId = s.ProfileId,
                    name = s.Name,
                    botInstance = s.Id,
                    configRevision = config.Revision,
                    crossfire = trace,
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
        return true;
    }

    private bool NativeStormActive(BotComponent b) =>
        b.GoalEnemy != null
        && b.BotOwner.BotsGroup is { } group
        && nativeStormPlans.TryGetValue(group, out var plans)
        && plans.TryGetValue(b.GoalEnemy.EnemyProfileId, out var plan)
        && Time.time <= plan.Until
        && plan.Score >= config!.SquadStorm.Weakness;
}
