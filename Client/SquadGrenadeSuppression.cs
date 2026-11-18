using Comet.Core;
using SAIN.Components;
using SAIN.Preset.Shared.Enums;
using SAIN.SAINComponent.Classes.EnemyClasses;
using UnityEngine;

namespace Comet.Client;

// Shared only by the two participating bot states. No actor references survive a raid.
internal sealed class SquadGrenadePlan
{
    internal readonly string Thrower, Holder, Contact;
    internal readonly int StartFrame;
    internal bool Ended;

    internal SquadGrenadePlan(string thrower, string holder, string contact)
    {
        Thrower = thrower;
        Holder = holder;
        Contact = contact;
        StartFrame = Time.frameCount;
    }
}

public sealed partial class Plugin
{
    private bool SquadGrenadesEnabled => config?.Enabled == true
        && config.SquadManeuver.Enabled && config.SquadManeuver.GrenadeSuppression;

    private bool ActiveSquadGrenade(BotComponent bot, BotState state)
    {
        var plan = state.SquadGrenade;
        if (plan == null) return false;
        var members = bot.Squad?.Members;
        if (!plan.Ended && SquadGrenadesEnabled && members != null
            && members.TryGetValue(plan.Thrower, out var thrower) && members.TryGetValue(plan.Holder, out var holder)
            && SameGrenadeContact(thrower, plan.Contact) && SameGrenadeContact(holder, plan.Contact)
            && states.TryGetValue(thrower, out var ts) && ReferenceEquals(ts.SquadGrenade, plan)
            && states.TryGetValue(holder, out var hs) && ReferenceEquals(hs.SquadGrenade, plan)
            && HolderAvailable(holder, hs)
            && (plan.StartFrame == Time.frameCount || thrower.BotOwner.WeaponManager.Grenades.ThrowindNow))
            return true;
        plan.Ended = true;
        state.SquadGrenade = null;
        return false;
    }

    private bool SameGrenadeContact(BotComponent bot, string contact) =>
        bot != null && bot.GoalEnemy is {} enemy && !enemy.IsAI && enemy.EnemyProfileId == contact
        && TacticalScope(bot, enemy) && !ReadDowned(bot.Player);

    private static bool HolderAvailable(BotComponent bot, BotState state) =>
        !state.Retired && !state.Crossfire.Policy.Active && !DoorPlanActive(state)
        && !state.CoverFeint.Policy.Active && !state.Relocation.Policy.Active
        && bot.Decision.CurrentSelfDecision == ESelfActionType.None
        && (bot.Decision.CurrentSquadDecision == ESquadDecision.None
            || TacticalPriority.OrdinarySquad(bot.Decision.CurrentSquadDecision.ToString()))
        && !NativeChoiceHasPriority(bot, state) && VisibleAdapter.WeaponReady(bot);

    private bool StartSquadGrenade(BotComponent bot, BotState state, Enemy enemy, Situation situation,
        ref ECombatDecision decision, ref bool accepted)
    {
        if (!SquadGrenadesEnabled || enemy.IsAI || enemy.IsVisible || enemy.InLineOfSight
            || !SameGrenadeContact(bot, enemy.EnemyProfileId)
            || bot.BotOwner.WeaponManager.Grenades.ThrowindNow || bot.Squad?.Members is not {} members
            || members.Count < 2) return false;

        BotState? partner = null;
        float distanceLimit = (float)config!.SquadManeuver.SquadDistance;
        float bestDistance = distanceLimit * distanceLimit;
        bool attacked = false;
        foreach (var member in members.Values)
        {
            if (member == null || member.IsDead || !member.BotActive) continue;
            if (!attacked && member.EnemyController.Enemies.TryGetValue(enemy.EnemyProfileId, out var contact))
                attacked = contact?.Status is {} status && (status.ShotAtMeRecently || status.ShotMeRecently);

            states.TryGetValue(member, out var memberState);
            if (memberState != null && ActiveSquadGrenade(member, memberState)) return false;
            if (ReferenceEquals(member, bot) || !SameGrenadeContact(member, enemy.EnemyProfileId)) continue;
            float distance = (bot.Position - member.Position).sqrMagnitude;
            if (distance > bestDistance) continue;
            if (memberState == null)
            {
                memberState = new BotState(member, config, session);
                states.Add(member, memberState);
            }
            if (memberState.Siege != null || !HolderAvailable(member, memberState)) continue;
            bestDistance = distance;
            partner = memberState;
        }
        if (!attacked || partner == null) return false;

        // GetDecision starts the native throw. A refusal falls back to the normal
        // ranking without calling the same side-effecting native method twice.
        if (!NativeAdapter.Throw(bot, enemy, out string reason))
        {
            situation.Unavailable[Tactic.Grenade] = "native-throw-refused:" + reason;
            return false;
        }
        var plan = new SquadGrenadePlan(state.ProfileId, partner.ProfileId, enemy.EnemyProfileId);
        state.SquadGrenade = partner.SquadGrenade = plan;
        SetSquadGrenadeRole(state, "Grenade suppression - throw", ECombatDecision.ThrowGrenade, ref decision, ref accepted);
        return true;
    }

    private bool HoldForSquadGrenade(BotComponent bot, BotState state, Enemy enemy,
        ref ECombatDecision decision, ref bool accepted)
    {
        var plan = state.SquadGrenade;
        if (plan == null || plan.Holder != bot.ProfileId || plan.Contact != enemy.EnemyProfileId
            || !ActiveSquadGrenade(bot, state)) return false;
        if (!enemy.IsVisible)
            foreach (var other in bot.EnemyController.KnownEnemies)
                if (other != null && other.IsVisible) return false;
        var action = enemy.IsVisible ? ECombatDecision.StandAndShoot : ECombatDecision.Freeze;
        SetSquadGrenadeRole(state, "Grenade suppression - hold angle", action, ref decision, ref accepted);
        return true;
    }

    private void SetSquadGrenadeRole(BotState state, string role, ECombatDecision action,
        ref ECombatDecision decision, ref bool accepted)
    {
        if (state.Feature != "S10" || state.Decision != role)
        {
            Release(state, "squad-grenade-role", false, true);
            state.Feature = "S10";
            state.Decision = role;
            state.Candidates = new() { new Candidate { Decision = role, Eligible = true, Reason = "player-engaged-squad" } };
            state.Mixing = null;
            Emit(state, "selected", "squad-grenade-role");
        }
        state.Expected = decision = action;
        state.RequestedFrame = Time.frameCount;
        accepted = true;
    }
}
