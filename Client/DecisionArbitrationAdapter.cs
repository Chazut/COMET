using System;
using Comet.Core;
using EFT;
using SAIN.Components;
using SAIN.Preset.Shared.Enums;
using SAIN.SAINComponent.Classes.Decision;
using SAIN.SAINComponent.Classes.EnemyClasses;
using UnityEngine;

namespace Comet.Client;

public sealed partial class Plugin
{
    private bool TacticalScope(BotComponent b, Enemy? enemy) => config?.Enabled == true && world != null
        && Authority && NativeAdapter.IsPmcAi(b) && b.BotActive && !b.GameEnding && !Ghost(b.ProfileId)
        && enemy != null && ContactSafety.Allows(b,enemy) && !enemy.IsZombie && enemy.KnownPlaces.LastKnownPosition.HasValue
        && enemy.TimeSinceLastKnownUpdated >= 0 && enemy.TimeSinceLastKnownUpdated <= config.ContactLifetime;

    private static bool TacticalHandsBusy(BotComponent b)
    {
        var w = b.BotOwner.WeaponManager;
        return w == null || b.Player.HandsController == null || b.BotOwner.Medecine?.Using == true
            || b.Player.HandsController is IMedsController || w.Reload.Reloading || w.Selector.IsChanging
            || w.Grenades.ThrowindNow || b.Player.HandsController.IsInInteractionStrictCheck();
    }

    // Reaching EnemyDecisions means SAIN's CURRENT safety/self/squad selection declined.
    // Current* still contains the PREVIOUS commit here; it cannot veto the next choice.
    private static bool NativeChoiceHasPriority(BotComponent b, BotState s) => TacticalHandsBusy(b)
        || b.BotOwner.DoorOpener.Interacting || b.Grenade.GrenadeDangerPoint != null
        || b.Decision.DogFightDecision.DogFightActive || b.BotOwner.WeaponManager.IsMelee;

    // Called only after SAIN has made its emergency/self-care/close-combat decision.
    // Override ordinary squad tactics at the commit point, without mutating the previous
    // SAIN decision or running its decision manager recursively.
    private static void ArbitrateSquad(BotDecisionManager __instance, ref ECombatDecision solo,
        ref ESquadDecision squad, ESelfActionType self, Enemy enemy)
    {
        var p = Instance; var b = __instance.Bot;
        if (p == null || solo != ECombatDecision.None || self != ESelfActionType.None
            || !TacticalPriority.OrdinarySquad(squad.ToString()) || !p.TacticalScope(b, enemy)
            || TacticalHandsBusy(b) || b.BotOwner.DoorOpener.Interacting
            || b.Grenade.GrenadeDangerPoint != null || b.Decision.DogFightDecision.DogFightActive
            || b.BotOwner.WeaponManager.IsMelee) return;
        if (p.states.TryGetValue(b, out var old) && old.ArbitratingSquad) return;
        // Choose creates the state if necessary. Its ordinary-priority check must use this
        // new commit (self=None), not the previous tick's CurrentSquadDecision.
        if (old == null) { old = new BotState(b, p.config!, p.session); p.states.Add(b, old); }
        if (old.Execution.CoolingDown(Time.time)) return;
        var proposed = ECombatDecision.None; bool chosen = false;
        old.ArbitratingSquad = true;
        try
        {
            bool runNative = Choose(b.Decision.EnemyDecisions, ref proposed, enemy, b.EnemyController.KnownEnemies, ref chosen);
            if (!runNative && chosen && old.RequestedFrame == Time.frameCount)
            {
                p.ArbitrationEvent(old, "tactical-priority-over-" + squad, solo + "/" + self + "/" + squad);
                solo = proposed; squad = ESquadDecision.None;
            }
        }
        finally { old.ArbitratingSquad = false; }
    }

    private void ArbitrationEvent(BotState s, string reason, string native)
    {
        // Only transitions, and at most once per reason per second; no per-frame spam.
        string key = reason + ":" + s.Decision + ":" + native;
        if (s.ArbitrationKey == key && Time.time - s.ArbitrationAt < 1) return;
        s.ArbitrationKey = key; s.ArbitrationAt = Time.time; s.ArbitrationReason = reason; s.ArbitrationNative = native;
        Emit(s, "evaluated", reason, "A13");
    }

    private static string ExecutionAction(BotState s) => s.Feature == "A03"
        ? s.Decision == "ContinuePush" ? "Push" : s.Decision == "KeepAngle" ? "Shoot" : "" : s.Decision;

    private void SampleDecisionExecution(BotComponent b, BotState s)
    {
        if ((!s.Execution.Active && !s.PlanService.Active) || Time.time < s.ExecutionSampleAt) return;
        s.ExecutionSampleAt = Time.time + .1f;
        if(s.PlanService.Active) { ObservePlanService(b,s,true); return; }
        if (s.Retired || !TacticalScope(b, b.GoalEnemy)) { s.Execution.Clear(); return; }
        var d = b.Decision;
        bool paused = TacticalHandsBusy(b) || b.BotOwner.DoorOpener.Interacting
            || d.CurrentSelfDecision != ESelfActionType.None || d.CurrentSquadDecision != ESquadDecision.None
            || d.CurrentCombatDecision == ECombatDecision.AvoidGrenade;
        bool executed = s.ActiveLabel.Length > 0 && d.CurrentCombatDecision == s.Expected
            && b.SAINLayersActive && OutcomeAction(b, ExecutionAction(s));
        string reason = s.Execution.Sample(Time.time, executed, paused);
        if (reason.Length == 0) return;
        ArbitrationEvent(s, reason, d.CurrentCombatDecision + "/" + (b.CurrentAction?.GetType().Name ?? "unobserved"));
        if (s.Execution.Phase != "timed-out") return;
        // Preserve a retry delay even through Release. No forced Stop/Reset/teleport of native work.
        if (s.Feature == "A01" && Enum.TryParse<Tactic>(s.Decision, out var tactic)) s.Engine.Fail(tactic, Time.time, reason);
        if (s.Feature == "A02") s.VisibleEngine.NotifyNativeRefusal();
        YieldToSain(s, reason);
    }
}
