using System;
using System.Runtime.CompilerServices;
using Comet.Core;
using HarmonyLib;
using SAIN.Components;
using SAIN.Preset.Shared.Enums;
using SAIN.SAINComponent.Classes.EnemyClasses;
using UnityEngine;

namespace Comet.Client;

public sealed partial class Plugin
{
    internal OpportunityConfig RaidOpportunityConfig => config!.Opportunity;

    private static void PatchOpportunity(Harmony h)
    {
        var heard = AccessTools.Method(
            typeof(EnemyEvents),
            nameof(EnemyEvents.EnemyHeard),
            new[] { typeof(SAINSoundType), typeof(bool), typeof(EnemyPlace) }
        );
        if (heard == null || AccessTools.Field(typeof(EnemyEvents), "Enemy") == null)
            throw new MissingMethodException("A10 personal sound event");
        h.Patch(heard, postfix: new HarmonyMethod(typeof(Plugin), nameof(OpportunityHeard)));
    }

    private static void OpportunityHeard(Enemy ___Enemy, SAINSoundType __0, EnemyPlace __2)
    {
        var p = Instance;
        if (p?.config?.Enabled != true || !p.config.Opportunity.Enabled)
            return;
        string cue = __0 switch
        {
            SAINSoundType.Reload => "reload",
            SAINSoundType.Heal => "heal",
            SAINSoundType.Surgery => "surgery",
            SAINSoundType.Looting => "looting",
            SAINSoundType.DryFire => "dry-fire",
            SAINSoundType.Shot => "shot",
            SAINSoundType.SuppressedShot => "shot",
            SAINSoundType.Sprint => "sprint",
            SAINSoundType.GrenadeDraw => "grenade",
            SAINSoundType.GrenadePin => "grenade",
            _ => "",
        };
        if (cue.Length == 0)
            return;
        var bot = ___Enemy.Bot;
        try
        {
            if (
                !p.Authority
                || !NativeAdapter.IsSupportedAi(bot)
                || !bot.BotActive
                || p.Ghost(bot.ProfileId)
                || __2 == null
                || !ReferenceEquals(__2, ___Enemy.KnownPlaces.LastHeardPlace)
            )
                return;
            // A sound can arrive before the first combat decision. Capture it then, without any global enemy cache.
            if (!p.states.TryGetValue(bot, out var state))
            {
                state = new BotState(bot, p.config, p.session);
                p.states.Add(bot, state);
            }
            var pos = __2.Position;
            state.Opportunity.Hear(
                ___Enemy.EnemyProfileId,
                RuntimeHelpers.GetHashCode(___Enemy),
                Time.time,
                cue,
                true,
                pos.x,
                pos.y,
                pos.z,
                p.config.Opportunity
            );
        }
        catch (Exception ex)
        {
            p.OpportunityError(bot, ex);
        }
    }

    private OpportunityTrace? ReadOpportunity(
        BotComponent bot,
        Enemy enemy,
        BotState state,
        Situation s,
        VisibleSituation? visible = null
    )
    {
        if (!config!.Opportunity.Enabled)
            return null;
        try
        {
            var t = state.Opportunity.Snapshot(
                enemy.EnemyProfileId,
                RuntimeHelpers.GetHashCode(enemy),
                s.Now,
                config.Opportunity
            );
            if (t.State != "assess")
                return t.Episode == 0 ? null : t;
            // Reuse the A02 bounded, shared-budget probe. No extra path query, scene scan or hidden transform access.
            var v = visible ?? VisibleAdapter.Capture(bot, enemy, s, state, config);
            var travel = new OpportunityTravel
            {
                Ready = v.ProbeReady,
                Complete = v.PushReachable,
                ThreatCount = v.ThreatCount,
                ImmediateThreats = OpportunityImmediateThreats(bot, enemy),
                Risk = v.PushRouteRisk,
                UnderFire = bot.BotOwner.Memory.IsUnderFire,
            };
            // Assess rejects incomplete routes before using distance. In that case
            // SAIN can still have corners from the previous route.
            if (travel.Ready && travel.Complete)
                travel.PathLength = EnemyPathCorners.OpportunityLength(enemy.Path);
            OpportunityModel.Assess(t, s, travel, config.Opportunity);
            return t;
        }
        catch (Exception ex)
        {
            OpportunityError(bot, ex);
            return null;
        }
    }

    private static int OpportunityImmediateThreats(BotComponent bot, Enemy target)
    {
        int scanned = 0;
        foreach (var other in bot.EnemyController.KnownEnemies)
        {
            if (
                other != null
                && other.EnemyProfileId != target.EnemyProfileId
                && other.EnemyKnown
                && other.IsVisible
            )
                return 1;
        }
        return 0;
    }

    private void OpportunityError(BotComponent bot, Exception ex)
    {
        if (!states.TryGetValue(bot, out var s))
            return;
        s.Opportunity.Clear();
        s.OpportunityAtSelection = null;
        if (!s.OpportunityWarned)
        {
            s.OpportunityWarned = true;
            Logger.LogWarning(
                "COMET A10 sound/route estimate failed; no opportunity bonus: " + ex.Message
            );
        }
    }

    private void RecordOpportunity(BotState state, Situation s)
    {
        state.OpportunityAtSelection = s.Opportunity?.Copy();
        var t = s.Opportunity;
        if (t == null)
        {
            state.OpportunityLogKey = "";
            return;
        }
        string key = t.Target + ":" + t.Episode + ":" + t.State + ":" + t.Reason;
        // Reasons/episode transitions only, never every decision tick.
        if (key != state.OpportunityLogKey)
        {
            state.OpportunityLogKey = key;
            Emit(state, "evaluated", t.Reason);
        }
    }
}
