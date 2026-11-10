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
        var heard = AccessTools.Method(typeof(EnemyEvents), nameof(EnemyEvents.EnemyHeard), new[] { typeof(SAINSoundType), typeof(bool), typeof(EnemyPlace) });
        if (heard == null || AccessTools.Field(typeof(EnemyEvents), "Enemy") == null) throw new MissingMethodException("A10 personal sound event");
        h.Patch(heard, postfix: new HarmonyMethod(typeof(Plugin), nameof(OpportunityHeard)));
    }
    private static void OpportunityHeard(Enemy ___Enemy, SAINSoundType __0, EnemyPlace __2)
    {
        var p = Instance;
        if (p?.config?.Enabled != true || !p.config.Opportunity.Enabled) return;
        string cue = __0 switch {
            SAINSoundType.Reload => "reload", SAINSoundType.Heal => "heal", SAINSoundType.Surgery => "surgery",
            SAINSoundType.Looting => "looting", SAINSoundType.DryFire => "dry-fire",
            SAINSoundType.Shot => "shot", SAINSoundType.SuppressedShot => "shot", SAINSoundType.Sprint => "sprint",
            SAINSoundType.GrenadeDraw => "grenade", SAINSoundType.GrenadePin => "grenade", _ => "" };
        if (cue.Length == 0) return;
        var bot = ___Enemy.Bot;
        try
        {
            if (!p.Authority || !NativeAdapter.IsSupportedAi(bot) || !bot.BotActive || p.Ghost(bot.ProfileId) || __2 == null
                || !ReferenceEquals(__2, ___Enemy.KnownPlaces.LastHeardPlace)) return;
            // A sound can arrive before the first combat decision. Capture it then, without any global enemy cache.
            if (!p.states.TryGetValue(bot, out var state)) { state = new BotState(bot, p.config, p.session); p.states.Add(bot, state); }
            var pos = __2.Position;
            state.Opportunity.Hear(___Enemy.EnemyProfileId, RuntimeHelpers.GetHashCode(___Enemy), Time.time, cue, true, pos.x, pos.y, pos.z, p.config.Opportunity);
        }
        catch (Exception ex) { p.OpportunityError(bot, ex); }
    }
    private OpportunityTrace? ReadOpportunity(BotComponent bot, Enemy enemy, BotState state, Situation s, VisibleSituation? visible = null)
    {
        if (!config!.Opportunity.Enabled) return null;
        try
        {
            var t = state.Opportunity.Snapshot(enemy.EnemyProfileId, RuntimeHelpers.GetHashCode(enemy), s.Now, config.Opportunity);
            if (t.State != "assess") return t.Episode == 0 ? null : t;
            // Reuse the A02 bounded, shared-budget probe. No extra path query, scene scan or hidden transform access.
            var v = visible ?? VisibleAdapter.Capture(bot, enemy, s, state, config);
            var corners = EnemyPathCorners.Read(enemy.Path);
            double length = 0, vertical = 0;
            int count = corners.Length;
            if (count > 128) { count = 0; }
            else for (int i = 1; i < count; i++) { length += Vector3.Distance(corners[i - 1], corners[i]); vertical += Math.Abs(corners[i].y - corners[i - 1].y); }
            var own = bot.Player.MovementContext;
            OpportunityModel.Assess(t, s, new OpportunityTravel {
                Ready = v.ProbeReady, Complete = v.PushReachable, WeaponReady = v.WeaponReady,
                ThreatCount = v.ThreatCount, ThreatsTruncated = v.ThreatsTruncated, Risk = v.PushRouteRisk, RiskLimit = config.VisibleCombat.RiskTolerance,
                PathLength = length, Corners = count, VerticalTravel = vertical,
                PositionError = Vector3.Distance(new Vector3((float)s.KnownX, (float)s.KnownY, (float)s.KnownZ), new Vector3((float)t.X, (float)t.Y, (float)t.Z)),
                WalkSpeed = own.MaxSpeed, SprintSpeed = own.SprintSpeed, Stamina = bot.Player.Physical.Stamina.NormalValue,
                CanSprint = own.CanSprint && bot.BotOwner.CanSprintPlayer && length > bot.BotOwner.Settings.FileSettings.Move.RUN_TO_COVER_MIN,
                DoorBusy = bot.BotOwner.DoorOpener.Interacting, Visible = enemy.InLineOfSight, UnderFire = bot.BotOwner.Memory.IsUnderFire
            }, config.Opportunity);
            return t;
        }
        catch (Exception ex) { OpportunityError(bot, ex); return null; }
    }
    private void OpportunityError(BotComponent bot, Exception ex)
    {
        if (!states.TryGetValue(bot, out var s)) return;
        s.Opportunity.Clear(); s.OpportunityAtSelection = null;
        if (!s.OpportunityWarned) { s.OpportunityWarned = true; Logger.LogWarning("COMET A10 sound/route estimate failed; no opportunity bonus: " + ex.Message); }
    }
    private void RecordOpportunity(BotState state, Situation s)
    {
        state.OpportunityAtSelection = s.Opportunity?.Copy();
        var t = s.Opportunity;
        if (t == null) { state.OpportunityLogKey = ""; return; }
        string key = t.Target + ":" + t.Episode + ":" + t.State + ":" + t.Reason;
        // Reasons/episode transitions only, never every decision tick.
        if (key != state.OpportunityLogKey)
        { state.OpportunityLogKey = key; Emit(state, "evaluated", t.Reason); }
    }
}
