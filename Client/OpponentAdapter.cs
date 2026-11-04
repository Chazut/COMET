using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Comet.Core;
using EFT;
using EFT.Ballistics;
using EFT.InventoryLogic;
using HarmonyLib;
using SAIN.Components;
using SAIN.Preset.Shared.Enums;
using SAIN.SAINComponent.Classes.EnemyClasses;
using UnityEngine;

namespace Comet.Client;

public sealed partial class Plugin
{
    private int opponentFrame = -1;
    private static void PatchOpponent(Harmony h)
    {
        var heard = AccessTools.Method(typeof(EnemyEvents), nameof(EnemyEvents.EnemyHeard), new[] { typeof(SAINSoundType), typeof(bool), typeof(EnemyPlace) });
        var hit = AccessTools.Method(typeof(EnemyEvents), "enemyHit", new[] { typeof(DamageInfo), typeof(EBodyPart), typeof(float) });
        if (heard == null || hit == null || AccessTools.Field(typeof(EnemyEvents), "Enemy") == null)
            throw new MissingMethodException("A09 perceived opponent events");
        h.Patch(heard, postfix: new HarmonyMethod(typeof(Plugin), nameof(OpponentHeard)));
        h.Patch(hit, postfix: new HarmonyMethod(typeof(Plugin), nameof(OpponentHit)));
    }
    private bool OpponentEligible(BotComponent bot) => config?.Enabled == true && config.Opponent.Enabled && Authority
        && NativeAdapter.IsPmcAi(bot) && bot.BotActive && !Ghost(bot.ProfileId);
    private static void OpponentHeard(Enemy ___Enemy, SAINSoundType __0, EnemyPlace __2)
    {
        var p = Instance;
        if (p == null || (__0 != SAINSoundType.Heal && __0 != SAINSoundType.Surgery)) return;
        try
        {
            var bot = ___Enemy.Bot;
            if (!p.OpponentEligible(bot) || !p.states.TryGetValue(bot, out var state) || __2 == null
                || !ReferenceEquals(__2, ___Enemy.KnownPlaces.LastHeardPlace)) return;
            state.Opponent.InjuryCue(___Enemy.EnemyProfileId, RuntimeHelpers.GetHashCode(___Enemy), Time.time,
                __0 == SAINSoundType.Heal ? "heard-heal" : "heard-surgery", true);
        }
        catch (Exception ex) { p.OpponentError(___Enemy.Bot, ex); }
    }
    private static void OpponentHit(Enemy ___Enemy, DamageInfo __0, EBodyPart __1)
    {
        var p = Instance;
        if (p == null) return;
        try
        {
            var bot = ___Enemy.Bot;
            // The global damage event alone is NOT perception. Require this observer's shot and the hit part in sight.
            // Do not inspect damage amount, health, armor damage or remaining HP.
            if (!p.OpponentEligible(bot) || !p.states.TryGetValue(bot, out var state)
                || __0.Player?.iPlayer?.ProfileId != bot.ProfileId || !___Enemy.IsVisible || bot.Flashed.IsFlashed
                || !OpponentMemory.ValidAge(___Enemy.TimeSinceLastKnownUpdated, .5)
                || OpponentAdapter.PartDuration(___Enemy, __1) < p.config!.Opponent.ObservationSeconds) return;
            state.Opponent.InjuryCue(___Enemy.EnemyProfileId, RuntimeHelpers.GetHashCode(___Enemy), Time.time, "seen-impact", true);
        }
        catch (Exception ex) { p.OpponentError(___Enemy.Bot, ex); }
    }
    private void OpponentError(BotComponent bot, Exception ex)
    {
        if (!states.TryGetValue(bot, out var state)) return;
        state.Opponent.Clear(); state.NextOpponentSample = Time.time + 1;
        if (!state.OpponentWarned) { state.OpponentWarned = true; Logger.LogWarning("COMET A09 observation failed; neutral estimate: " + ex.Message); }
    }
    private OpponentTrace? ReadOpponent(BotComponent bot, Enemy enemy, BotState state, Situation s)
    {
        if (!OpponentEligible(bot) || !s.HasContact) return null;
        var c = config!.Opponent;
        string sample = "memory-only"; double ms = 0;
        try
        {
            // Outside personal sight this path never reads the target's hands or equipment.
            var view = OpponentAdapter.View(enemy, s);
            if (!bot.Flashed.IsFlashed && OpponentMemory.CanObserve(view, c) && Time.time >= state.NextOpponentSample)
            {
                sample = "budget-deferred";
                if (opponentFrame != Time.frameCount)
                {
                    opponentFrame = Time.frameCount; state.NextOpponentSample = Time.time + .5f;
                    var timer = Stopwatch.StartNew();
                    state.Opponent.Observe(s.Target, RuntimeHelpers.GetHashCode(enemy), s.Now, view, c,
                        observation => OpponentAdapter.ObserveVisibleParts(enemy, observation, c));
                    ms = timer.Elapsed.TotalMilliseconds; sample = "visual-sample";
                }
            }
            var result = state.Opponent.Snapshot(s.Target, RuntimeHelpers.GetHashCode(enemy), s.Now, c);
            result.Sample = sample; result.SampleMilliseconds = ms; return result;
        }
        catch (Exception ex)
        {
            OpponentError(bot, ex);
            return new OpponentTrace { Target = s.Target, At = s.Now, Sample = "read-error" };
        }
    }
}

internal static class OpponentAdapter
{
    internal static double PartDuration(Enemy e, EBodyPart part) => e.Vision.EnemyParts.Parts.TryGetValue(part, out var data)
        && data.CanBeSeen && data.LineOfSight && data.TimeSeen > 0 ? Math.Max(0, Time.time - data.TimeSeen) : -1;
    internal static OpponentObservation View(Enemy e, Situation s) => new OpponentObservation {
        Visible = s.Visible, PersonalSight = s.ContactSource == "seen", SightAge = s.ContactAge,
        VisibleFor = e.Vision.VisibleStartTime > 0 ? s.Now - e.Vision.VisibleStartTime : -1, Distance = s.KnownDistance,
        // Both arms must be observed to identify what is held. No new raycasts.
        WeaponVisibleFor = Math.Min(PartDuration(e, EBodyPart.LeftArm), PartDuration(e, EBodyPart.RightArm)),
        BodyVisibleFor = PartDuration(e, EBodyPart.Chest), HeadVisibleFor = PartDuration(e, EBodyPart.Head) };
    internal static void ObserveVisibleParts(Enemy e, OpponentObservation view, OpponentConfig c)
    {
        if (!OpponentMemory.CanObserve(view, c)) return;
        if (view.WeaponVisibleFor >= c.ObservationSeconds)
        {
            // Missing/custom hands are unknown, never "unarmed". Only a coarse external weapon category.
            var weapon = e.EnemyPlayer.HandsController?.Item as Weapon;
            view.Weapon = weapon?.WeapClass ?? "unknown";
        }
        if (view.Distance > c.ProtectionDistance) return;
        if (view.BodyVisibleFor >= c.ObservationSeconds)
        {
            var equipment = e.EnemyPlayer.Inventory?.Equipment;
            if (equipment != null)
            {
                var rig = equipment.GetSlot(EquipmentSlot.TacticalVest)?.ContainedItem;
                // An outer rig can conceal protection. Never inspect plates or child items to resolve uncertainty.
                var outer = rig ?? equipment.GetSlot(EquipmentSlot.ArmorVest)?.ContainedItem;
                view.Body = outer != null ? Protection(outer) : "none-visible";
            }
        }
        if (view.HeadVisibleFor >= c.ObservationSeconds)
        {
            var equipment = e.EnemyPlayer.Inventory?.Equipment;
            if (equipment != null)
            {
                var head = equipment.GetSlot(EquipmentSlot.Headwear)?.ContainedItem;
                view.Head = head == null ? "none-visible" : Protection(head);
            }
        }
    }
    // Root component presence only, not class/durability/contents. Unrecognized shells remain unknown.
    private static string Protection(Item item) => item.GetItemComponent<ArmorComponent>() != null ? "visible-protection" : "unknown";
}
