using System;
using System.Reflection;
using Comet.Core;
using EFT;
using HarmonyLib;
using SAIN.Components;
using SAIN.Preset.Shared.GlobalSettings;
using SAIN.Preset.Shared.Enums;
using SAIN.SAINComponent.Classes.Decision;
using SAIN.SAINComponent.Classes.EnemyClasses;
using SAIN.SAINComponent.Classes.WeaponFunction;
using UnityEngine;
using UnityEngine.AI;

namespace Comet.Client;

internal static class NativeAdapter
{
    private static MethodInfo checkThrow = null!, checkTarget = null!, prepareThrow = null!, checkGrenadeFriends=null!;
    private static int probeFrame = -1;
    internal static void ValidateContracts()
    {
        var type = typeof(GrenadeThrowDecider);
        checkThrow = AccessTools.Method(type, "CheckCanThrow", new[] { typeof(string).MakeByRefType() }) ?? throw new MissingMethodException("SAIN CheckCanThrow");
        checkTarget = AccessTools.Method(type, "CanThrowAtEnemy", new[] { typeof(Enemy), typeof(string).MakeByRefType() }) ?? throw new MissingMethodException("SAIN CanThrowAtEnemy");
        prepareThrow = AccessTools.Method(type, "FindThrowTarget", new[] { typeof(Enemy) }) ?? throw new MissingMethodException("SAIN FindThrowTarget");
        checkGrenadeFriends=AccessTools.Method(type,"CheckFriendlyDistances",new[]{typeof(Vector3)})??throw new MissingMethodException("SAIN CheckFriendlyDistances");
        if (AccessTools.Method(typeof(BotDecisionManager), "SetDecisions") == null) throw new MissingMethodException("SAIN SetDecisions");
        if (AccessTools.Method(typeof(BotDecisionManager), "ContinueMoveToCover") == null) throw new MissingMethodException("SAIN ContinueMoveToCover");
        if (AccessTools.Method(typeof(SAIN.SAINComponent.Classes.SAINCoverClass), "FindCoverPoint") == null) throw new MissingMethodException("SAIN FindCoverPoint");
        if (AccessTools.Method(typeof(SAIN.SAINComponent.Classes.Mover.SAINMoverClass), "GoToCoverPoint",
            new[] {typeof(SAIN.SAINComponent.SubComponents.CoverFinder.CoverPoint),typeof(bool),typeof(SAIN.Models.Enums.ESprintUrgency)})?.ReturnType != typeof(bool))
            throw new MissingMethodException("SAIN GoToCoverPoint");
    }
    internal static bool IsPmcAi(BotComponent bot)
    {
        if (bot == null || bot.Player == null || !bot.Player.IsAI || bot.IsDead) return false;
        var role = bot.Player.Profile.Info.Settings.Role;
        return role == WildSpawnType.pmcUSEC || role == WildSpawnType.pmcBEAR;
    }
    internal static Situation Capture(BotComponent bot, Enemy enemy, bool authority, bool ghost)
    {
        Vector3? known = enemy.KnownPlaces.LastKnownPosition;
        var places = enemy.KnownPlaces;
        var source = ReferenceEquals(places.LastKnownPlace, places.LastSeenPlace) ? "seen"
            : ReferenceEquals(places.LastKnownPlace, places.LastHeardPlace) ? "heard"
            : ReferenceEquals(places.LastKnownPlace, places.LastSquadSeenPlace) ? "shared-seen"
            : ReferenceEquals(places.LastKnownPlace, places.LastSquadHeardPlace) ? "shared-heard" : "sain-last-known";
        var health = bot.Memory.Health.HealthStatus;
        return new Situation {
            Now = Time.time, Target = enemy.EnemyProfileId, Personality = bot.Info.Personality.ToString(),
            IsAi = bot.Player.IsAI, IsPmc = IsPmcAi(bot), IsAuthority = authority, IsActive = bot.BotActive && !bot.IsDead,
            Ghost = ghost, Visible = enemy.IsVisible, HasContact = known.HasValue && ContactSafety.Allows(bot,enemy),
            ContactAge = enemy.TimeSinceLastKnownUpdated, KnownDistance = known.HasValue ? Vector3.Distance(bot.Position, known.Value) : 0,
            KnownX = known?.x ?? 0, KnownY = known?.y ?? 0, KnownZ = known?.z ?? 0, ContactSource = source,
            LowAmmo = bot.Decision.SelfActionDecisions.LowOnAmmo(.3f), Hurt = health == ETagStatus.BadlyInjured || health == ETagStatus.Dying,
            Suppressed = bot.Suppression.IsSuppressed, InCover = bot.Cover.CoverInUse != null
        };
    }
    internal static void Capabilities(BotComponent bot, Enemy enemy, Situation s, BotState state, CometConfig config)
    {
        var path = enemy.Path.PathToEnemy;
        var corners = EnemyPathCorners.Read(enemy.Path);
        var known = enemy.KnownPlaces.LastKnownPosition!.Value;
        bool complete = path != null && path.status == NavMeshPathStatus.PathComplete && corners.Length > 1
            && (corners[corners.Length - 1] - known).sqrMagnitude <= 4 && (corners[0] - bot.Position).sqrMagnitude <= 16;
        if (!complete) { s.Unavailable[Tactic.Push] = "no-current-complete-path"; s.Unavailable[Tactic.Search] = "no-current-complete-path"; }
        else if (enemy.Path.PathLength > config.PushMaxDistance) s.Unavailable[Tactic.Push] = "push-path-too-long";
        if (!bot.Info.PersonalitySettings.Search.WillSearchForEnemy) s.Unavailable[Tactic.Search] = "sain-personality-search-disabled";
        if (config.SilentSearch.Enabled && !enemy.Seen && !bot.Info.PersonalitySettings.Search.WillSearchFromAudio)
            s.Unavailable[Tactic.Search] = "sain-personality-audio-search-disabled";
        if (config.SilentSearch.Enabled && (enemy.KnownPlaces.LastKnownPlace?.HasArrivedPersonal == true || enemy.KnownPlaces.LastKnownPlace?.HasArrivedSquad == true))
            s.Unavailable[Tactic.Search] = "silent-search-already-searched";
        bool cover = false;
        bool guidedCover=config.Progress.Enabled && !state.ProgressUnavailable;
        if(guidedCover) state.SelectedCover=VisibleAdapter.SelectCover(bot,state.Progress);
        // Reuse SAIN's cover cache. No global scene searches or new NavMesh scans.
        int scannedCover=0;
        if(!guidedCover) foreach (var point in bot.Cover.CoverPoints)
        {
            if(++scannedCover>24)break;
            if (point == null || point.CoverData.IsBad || point.PathToPoint == null || point.PathToPoint.status != NavMeshPathStatus.PathComplete || (point.PathData.TimeSinceLastUpdated > 3 && !Plugin.RetainedCover(bot, point))) continue;
            if(!state.ProgressUnavailable && state.Progress.Blocked(ProgressAdapter.CoverKey(point),s.Now))continue;
            cover = true;
            break;
        }
        // The legacy existence scan is less strict than SelectCover (spotted points,
        // origin/destination validation). Never advertise a guided retreat without
        // the actual point we will request, or fall back to a stale native cover.
        if(guidedCover)cover=state.SelectedCover!=null;
        if (!cover) s.Unavailable[Tactic.FallBack] = "no-reachable-cover";
        string flankReason=FlankAdapter.Capability(bot,state);
        if(flankReason.Length>0)s.Unavailable[Tactic.Flank]=flankReason;
        var weapon = bot.BotOwner.WeaponManager;
        if (weapon == null || !weapon.HaveBullets || weapon.Reload.Reloading || weapon.Selector.IsChanging || bot.Player.HandsController.IsInInteractionStrictCheck())
        {
            foreach (Tactic tactic in Enum.GetValues(typeof(Tactic))) s.Unavailable[tactic] = "weapon-or-hands-busy-yield-to-sain";
            return;
        }
        s.Unavailable[Tactic.Grenade] = GrenadeCapability(bot, enemy, known, state, config);
        if (s.Unavailable[Tactic.Grenade] == "ready") s.Unavailable.Remove(Tactic.Grenade);
    }
    private static string GrenadeCapability(BotComponent bot, Enemy enemy, Vector3 known, BotState state, CometConfig config)
    {
        var grenades = bot.BotOwner.WeaponManager.Grenades;
        if (grenades.ThrowindNow) return "ready";
        if (config.Weights["Grenade"] == 0 || enemy.TimeSinceLastKnownUpdated > config.GrenadeContactLifetime) return "disabled-or-stale-contact";
        if (!SainPresetAccess.AllowsGrenades(enemy.IsAI) || !bot.Info.FileSettings.Core.CanGrenade) return "sain-grenades-disabled";
        if (!grenades.HaveGrenade) return "no-grenade";
        var decider = bot.Grenade.GrenadeThrowDecider;
        object[] reason = { "" };
        if (!(bool)checkThrow.Invoke(decider, reason)) return "sain:" + reason[0];
        object[] target = { enemy, "" };
        if (!(bool)checkTarget.Invoke(decider, target)) return "sain:" + target[1];
        // Preparation computes a trajectory but NEVER throws. Actual DoThrow occurs only after selection.
        if (config.GrenadeTrajectory.Enabled || Time.time >= state.NextGrenadeProbe || (known - state.GrenadeTarget).sqrMagnitude > 1)
        {
            // Capability, room-clearing and search continuation can all query in
            // one frame. Advance the same incremental trajectory at most once.
            bool same=state.GrenadeProbeGate.Same(Time.frameCount,enemy.EnemyProfileId,known.x,known.y,known.z);
            if(state.GrenadeProbeGate.TryEnter(Time.frameCount,enemy.EnemyProfileId,known.x,known.y,known.z))
            {
                  state.GrenadeReady = false;
                  if (!config.GrenadeTrajectory.Enabled && probeFrame == Time.frameCount) return "probe-budget-deferred";
                  probeFrame = Time.frameCount;
                  state.NextGrenadeProbe = Time.time + .75f; state.GrenadeTarget = known;
                  Plugin.Instance?.RefreshEnvironment(bot,state,true);
                  state.GrenadeReady = (bool)prepareThrow.Invoke(decider, new object[] { enemy });
            }
            else if(!same)return "probe-budget-deferred";
        }
        bool ready=state.GrenadeReady && grenades.ReadyToThrow;
        if(ready && config.GrenadeJudgment.Enabled && Plugin.Instance!=null)ready=Plugin.Instance.AssessGrenade(bot,enemy,state,true);
        return ready ? "ready" : "no-ready-throw-trajectory";
    }
    internal static void ContinueGrenadeSearch(BotComponent b,BotState s,CometConfig c)
    {
        var e=b.GoalEnemy;var known=e?.KnownPlaces.LastKnownPosition;
        if(e!=null&&known.HasValue&&ContactSafety.Allows(b,e))GrenadeCapability(b,e,known.Value,s,c);
    }
    internal static bool GrenadeFriendsClear(BotComponent b,Vector3 target)
        =>(bool)checkGrenadeFriends.Invoke(b.Grenade.GrenadeThrowDecider,new object[]{target});
    internal static string PrepareRoomGrenade(BotComponent b,Enemy e,BotState s,CometConfig c)
    {
        if(b.BotOwner.WeaponManager.Grenades.ThrowindNow)return "clear-foreign-throw-in-progress";
        string reason=GrenadeCapability(b,e,e.KnownPlaces.LastKnownPosition!.Value,s,c);
        return reason=="ready"?"":reason;
    }
    internal static bool Throw(BotComponent bot, Enemy enemy, out string reason)
    {
        if(!ContactSafety.Allows(bot,enemy)){reason="grenade-own-squad-contact";return false;}
        return bot.Grenade.GrenadeThrowDecider.GetDecision(enemy, out reason);
    }
    internal static ECombatDecision Map(Tactic tactic) => tactic switch {
        Tactic.Push => ECombatDecision.RushEnemy, Tactic.Grenade => ECombatDecision.ThrowGrenade,
        Tactic.Hold => ECombatDecision.Freeze, Tactic.Flank => ECombatDecision.ShiftCover,
        Tactic.Search => ECombatDecision.Search, Tactic.FallBack => ECombatDecision.Retreat,
        _ => throw new ArgumentOutOfRangeException(nameof(tactic))
    };
}
