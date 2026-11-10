using System;
using Comet.Core;
using EFT;
using EFT.Ballistics;
using HarmonyLib;
using SAIN.BotController.Classes;
using SAIN.Components;
using UnityEngine;

namespace Comet.Client;

public sealed partial class Plugin
{
    private int fearFrame = -1, fearSamples;
    private static void PatchFear(Harmony h)
    {
        var method = AccessTools.Method(typeof(Squad), "memberWasKilled", new[] { typeof(Player), typeof(IPlayer), typeof(DamageInfo), typeof(EBodyPart) });
        if (method == null) throw new MissingMethodException("SAIN squad death observation");
        h.Patch(method, prefix: new HarmonyMethod(typeof(Plugin), nameof(ObserveSquadDeath)));
    }
    private static bool Witnesses(BotComponent bot, Vector3 position, float range)
    {
        if (bot.Flashed.IsFlashed) return false;
        var offset = position - bot.Transform.EyePosition;
        float distance = offset.magnitude;
        float angle = Math.Min(60, bot.Info.FileSettings.Core.VisibleAngle / 2);
        return FearEvidence.InView(distance, Vector3.Angle(bot.Transform.LookDirection, offset), bot.BotOwner.LookSensor.VisibleDist, angle, range, false)
            && !Physics.Raycast(bot.Transform.EyePosition, offset / distance, distance, LayersMaskController.HighPolyWithTerrainMask);
    }
    private static void ObserveSquadDeath(Squad __instance, Player player)
    {
        var p = Instance;
        if (p?.config == null || !p.config.Enabled || !p.config.Fear.Enabled || !p.Authority || player == null || p.Ghost(player.ProfileId)) return;
        // The event is a notification, NOT evidence of knowledge. Check each observer at the moment of death.
        int checkedMembers = 0;
        foreach (var bot in __instance.Members.Values)
        {
            if (++checkedMembers > 64) break;
            try
            {
                if (!NativeAdapter.IsSupportedAi(bot) || bot.ProfileId == player.ProfileId || !bot.BotActive || p.Ghost(bot.ProfileId)) continue;
                if (!Witnesses(bot, player.Position + Vector3.up, 40)) continue;
                if (!p.states.TryGetValue(bot, out var state))
                { state = new BotState(bot, p.config, p.session); p.states.Add(bot, state); }
                state.Fear.ObserveDeath(player.ProfileId, Time.time, true);
                state.NextFearSample = 0;
            }
            catch (Exception ex) { p.Logger.LogWarning("COMET death perception yielded: " + ex.Message); }
        }
    }
    private void RefreshFear(BotComponent bot, BotState state)
    {
        if (config == null || Time.time < state.NextFearSample) return;
        if (!config.Enabled || !config.Fear.Enabled || !NativeAdapter.IsSupportedAi(bot) || !bot.BotActive || !Authority || Ghost(state.ProfileId))
        { state.NextFearSample = Time.time + .25f; state.Fear.Pause(Time.time); return; }
        if (fearFrame != Time.frameCount) { fearFrame = Time.frameCount; fearSamples = 0; }
        if (fearSamples >= 2) return; // At most 16 supporting-ally LOS rays per frame across all PMCs.
        fearSamples++;
        state.NextFearSample = Time.time + .25f;
        try
        {
            var input = new FearInput { Now = Time.time, Personality = bot.Info.Personality.ToString(),
                Health = bot.Player.HealthController.GetBodyPartHealth(EBodyPart.Common, false).Normalized,
                HeavySuppression = bot.Suppression.IsHeavySuppressed };
            int scanned = 0;
            foreach (var e in bot.EnemyController.KnownEnemies)
            {
                if (++scanned > 32) { input.CountsTruncated = true; break; }
                if (e == null) continue;
                var pos = e.KnownPlaces.LastKnownPosition;
                float age = e.TimeSinceLastKnownUpdated;
                // Dated perceived positions only, within the local encounter. No enemy transform/health.
                if (pos.HasValue && FearEvidence.KnownThreat(age, (pos.Value - bot.Position).sqrMagnitude, config.ContactLifetime)) input.KnownEnemies++;
            }
            // Reuse SAIN's local LOS cache, then recheck view and supporting capability.
            // Bound costly rays; if truncated we decline to infer numerical disadvantage.
            scanned = 0;
            foreach (var mate in bot.Squad.VisibleMembers)
            {
                if (++scanned > 8) { input.CountsTruncated = true; break; }
                if (mate == null || mate == bot || mate.IsDead || !mate.BotActive || Ghost(mate.ProfileId)
                    || mate.Medical.TimeSinceShot < 1.5f || mate.Decision.CurrentSelfDecision != SAIN.Preset.Shared.Enums.ESelfActionType.None) continue;
                if (Witnesses(bot, mate.Transform.BodyPosition, 30) && VisibleAdapter.WeaponReady(mate)) input.AvailableAllies++;
            }
            state.Fear.Update(input);
        }
        catch (Exception ex)
        {
            state.Fear.Pause(Time.time); state.NextFearSample = Time.time + 5;
            Logger.LogWarning("COMET fear observation yielded: " + ex.Message);
        }
    }
}
