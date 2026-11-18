using Comet.Client;
using Comet.Core;
using SAIN.Components;
using SAIN.Preset.Shared.Enums;
using SAIN.SAINComponent.Classes.EnemyClasses;
using UnityEngine;

internal static class SquadGrenadeHarness
{
    internal static void Run()
    {
        int checks = 0;
        void Check(bool ok, string scenario)
        {
            checks++;
            if (!ok) throw new Exception("Squad grenade: " + scenario);
        }
        foreach (var scenario in new[] { "normal", "not-attacked", "hit", "ai-enemy", "visible", "line-of-sight",
            "solo", "different-player", "host-disabled", "ghost", "too-far", "grenade-refused", "disabled",
            "team-disabled", "partner-reloading", "retreat", "third-member-attacked", "partner-death", "regrouped",
            "target-changed", "other-visible", "concurrent", "remote-player", "meds", "ownership-lost" })
        {
            var p = new Plugin();
            var thrower = new BotComponent();
            var holder = new BotComponent();
            var third = new BotComponent { Position = new(8, 0, 0) };
            var members = new Dictionary<string, BotComponent>();
            foreach (var bot in new[] { thrower, holder, third })
            {
                members.Add(bot.ProfileId, bot);
                bot.Squad.Members = members;
                bot.GoalEnemy!.EnemyPlayer.IsAI = false;
                bot.GoalEnemy.EnemyProfileId = scenario == "remote-player" ? "fika-peer" : "player";
                bot.EnemyController.KnownEnemies.Add(bot.GoalEnemy);
                bot.EnemyController.Enemies.Add(bot.GoalEnemy.EnemyProfileId, bot.GoalEnemy);
            }
            if (scenario is not ("concurrent" or "third-member-attacked")) members.Remove(third.ProfileId);
            var enemy = thrower.GoalEnemy!;
            holder.GoalEnemy!.Status.ShotAtMeRecently = true;
            p.states[thrower] = new BotState(thrower, p.config!, p.session);
            NativeAdapter.ThrowCalls = 0;
            NativeAdapter.AllowThrow = scenario != "grenade-refused";
            switch (scenario)
            {
                case "not-attacked": holder.GoalEnemy.Status.ShotAtMeRecently = false; break;
                case "hit": holder.GoalEnemy.Status.ShotAtMeRecently = false; holder.GoalEnemy.Status.ShotMeRecently = true; break;
                case "ai-enemy": enemy.EnemyPlayer.IsAI = true; break;
                case "visible": enemy.IsVisible = true; break;
                case "line-of-sight": enemy.InLineOfSight = true; break;
                case "solo": members.Remove(holder.ProfileId); break;
                case "different-player": holder.GoalEnemy.EnemyProfileId = "another player"; break;
                case "host-disabled": p.Authority = false; break;
                case "ghost": p.Ghosts.Add(holder.ProfileId); break;
                case "too-far": holder.Position = new(100, 0, 0); break;
                case "disabled": p.config!.SquadManeuver.GrenadeSuppression = false; break;
                case "team-disabled": p.config!.SquadManeuver.Enabled = false; break;
                case "partner-reloading": holder.BotOwner.WeaponManager.Reload.Reloading = true; break;
                case "retreat": holder.Decision.CurrentSquadDecision = ESquadDecision.Retreat; break;
                case "third-member-attacked":
                    holder.GoalEnemy.Status.ShotAtMeRecently = false;
                    third.GoalEnemy!.Status.ShotMeRecently = true;
                    third.GoalEnemy = new Enemy { EnemyProfileId = "different-goal" };
                    break;
            }
            bool paired = scenario is "normal" or "hit" or "third-member-attacked" or "partner-death" or "regrouped"
                or "target-changed" or "other-visible" or "concurrent" or "remote-player" or "meds" or "ownership-lost";
            var situation = new Situation();
            Check(p.TestStart(thrower, situation) == (paired ? ECombatDecision.ThrowGrenade : ECombatDecision.None), scenario);
            Check(NativeAdapter.ThrowCalls == (paired || scenario == "grenade-refused" ? 1 : 0), scenario + " native calls");
            if (!paired)
            {
                Check(p.states[thrower].SquadGrenade == null, scenario + " no reservation");
                if (scenario == "grenade-refused") Check(situation.Unavailable.ContainsKey(Tactic.Grenade), "no duplicate native attempt");
                continue;
            }
            if (scenario == "concurrent")
            {
                p.states.TryAdd(third, new BotState(third, p.config!, p.session));
                Check(p.TestStart(third, new()) == ECombatDecision.None && NativeAdapter.ThrowCalls == 1, "one concurrent pair");
            }
            Check(p.TestHold(thrower) == ECombatDecision.None, "thrower cannot also hold");
            Check(p.TestHold(holder) == ECombatDecision.Freeze, "assigned holder uses the native angle");
            if (scenario == "normal")
            {
                Time.frameCount++;
                Check(p.TestHold(holder) == ECombatDecision.Freeze, "hold continues across decision frames during the throw");
                p.Observe(holder);
                Check(!p.states[holder].Crossfire.Policy.Active, "crossfire cannot replace the angle during the throw");
            }
            holder.GoalEnemy.IsVisible = true;
            Check(p.TestHold(holder) == ECombatDecision.StandAndShoot, "holder can shoot a visible contact");
            Time.frameCount++;
            switch (scenario)
            {
                case "partner-death": thrower.IsDead = true; break;
                case "regrouped": members.Remove(thrower.ProfileId); break;
                case "target-changed": thrower.GoalEnemy = null; break;
                case "meds": holder.Decision.CurrentSelfDecision = ESelfActionType.Surgery; break;
                case "ownership-lost": p.Authority = false; break;
                case "other-visible":
                    holder.GoalEnemy.IsVisible = false;
                    holder.EnemyController.KnownEnemies.Add(new Enemy { IsVisible = true });
                    break;
                default: thrower.BotOwner.WeaponManager.Grenades.ThrowindNow = false; break;
            }
            Check(p.TestHold(holder) == ECombatDecision.None, scenario + " yields");
        }
        Console.WriteLine($"S10 grenade suppression: {checks} adapter checks passed.");
    }
}

namespace SAIN.SAINComponent.Classes.EnemyClasses
{
    public partial class Enemy
    {
        public bool IsAI => EnemyPlayer.IsAI;
        public bool InLineOfSight;
        public readonly ShotStatus Status = new();
    }
    public class ShotStatus { public bool ShotAtMeRecently, ShotMeRecently; }
}
namespace SAIN.Components
{
    public partial class EnemyController
    {
        public Dictionary<string, Enemy> Enemies = new();
    }
}
namespace Comet.Core
{
    internal enum Tactic { Grenade }
    internal class Candidate { public string Decision = "", Reason = ""; public bool Eligible; }
}
namespace Comet.Client
{
    internal partial class Situation { public Dictionary<Tactic, string> Unavailable = new(); }
    internal partial class BotState
    {
        internal SquadGrenadePlan? SquadGrenade;
        internal bool Retired;
        internal List<Candidate> Candidates = new();
        internal object? Mixing;
    }
    internal static partial class NativeAdapter
    {
        internal static bool AllowThrow;
        internal static int ThrowCalls;
        internal static bool Throw(BotComponent b, Enemy e, out string reason)
        {
            ThrowCalls++;
            reason = AllowThrow ? "startThrow" : "refused";
            return b.BotOwner.WeaponManager.Grenades.ThrowindNow = AllowThrow;
        }
    }
    internal static class VisibleAdapter
    {
        internal static bool WeaponReady(BotComponent b) => b.BotOwner.WeaponManager.HaveBullets;
    }
    public sealed partial class Plugin
    {
        internal ECombatDecision TestStart(BotComponent b, Situation s)
        {
            var action = ECombatDecision.None;
            bool accepted = false;
            return StartSquadGrenade(b, states[b], b.GoalEnemy!, s, ref action, ref accepted) && accepted ? action : ECombatDecision.None;
        }
        internal ECombatDecision TestHold(BotComponent b)
        {
            var action = ECombatDecision.None;
            bool accepted = false;
            return HoldForSquadGrenade(b, states[b], b.GoalEnemy!, ref action, ref accepted) && accepted ? action : ECombatDecision.None;
        }
        private static bool ReadDowned(EFT.Player player) => player is EFT.DownedPlayer { Downed: true };
        private bool TacticalScope(BotComponent b, Enemy e) => world != null && config!.Enabled && Authority && b.Pmc
            && !b.IsDead && b.BotActive && !Ghost(b.ProfileId) && e.EnemyKnown;
        private static bool NativeChoiceHasPriority(BotComponent b, BotState s) => b.BotOwner.WeaponManager.Reload.Reloading;
        private void Emit(BotState s, string stage, string reason) => diagnostics?.Write(s.Decision);
    }
}
