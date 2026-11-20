using System;
using System.Reflection;
using Comet.Core;
using EFT;
using HarmonyLib;
using SAIN.Components;
using SAIN.Preset.Shared.Enums;
using SAIN.Preset.Shared.GlobalSettings;
using SAIN.SAINComponent.Classes.Decision;
using SAIN.SAINComponent.Classes.EnemyClasses;
using SAIN.SAINComponent.Classes.WeaponFunction;
using UnityEngine;
using UnityEngine.AI;

namespace Comet.Client;

internal static class NativeAdapter
{
    private static MethodInfo checkThrow = null!,
        checkTarget = null!,
        prepareThrow = null!,
        checkGrenadeFriends = null!;
    private static int probeFrame = -1;

    internal static void ValidateContracts()
    {
        var type = typeof(GrenadeThrowDecider);
        checkThrow = AccessTools.Method(
            type,
            "CheckCanThrow",
            new[] { typeof(string).MakeByRefType() }
        ) ?? throw new MissingMethodException("SAIN CheckCanThrow");
        checkTarget = AccessTools.Method(
            type,
            "CanThrowAtEnemy",
            new[] { typeof(Enemy), typeof(string).MakeByRefType() }
        ) ?? throw new MissingMethodException("SAIN CanThrowAtEnemy");
        prepareThrow = AccessTools.Method(type, "FindThrowTarget", new[] { typeof(Enemy) })
            ?? throw new MissingMethodException("SAIN FindThrowTarget");
        checkGrenadeFriends = AccessTools.Method(
            type,
            "CheckFriendlyDistances",
            new[] { typeof(Vector3) }
        ) ?? throw new MissingMethodException("SAIN CheckFriendlyDistances");
        if (AccessTools.Method(typeof(BotDecisionManager), "SetDecisions") == null)
            throw new MissingMethodException("SAIN SetDecisions");
        if (AccessTools.Method(typeof(BotDecisionManager), "ContinueMoveToCover") == null)
            throw new MissingMethodException("SAIN ContinueMoveToCover");
        if (
            AccessTools.Method(typeof(SAIN.SAINComponent.Classes.SAINCoverClass), "FindCoverPoint")
            == null
        )
            throw new MissingMethodException("SAIN FindCoverPoint");
        if (
            AccessTools
                .Method(
                    typeof(SAIN.SAINComponent.Classes.Mover.SAINMoverClass),
                    "GoToCoverPoint",
                    new[]
                    {
                        typeof(SAIN.SAINComponent.SubComponents.CoverFinder.CoverPoint),
                        typeof(bool),
                        typeof(SAIN.Models.Enums.ESprintUrgency),
                    }
                )
                ?.ReturnType != typeof(bool)
        )
            throw new MissingMethodException("SAIN GoToCoverPoint");
    }

    internal static bool IsSupportedAi(BotComponent bot)
    {
        return bot != null && !bot.IsDead && BotEligibility.Supports(bot.Player);
    }

    internal static Situation Capture(BotComponent bot, Enemy enemy, bool authority, bool ghost)
    {
        Vector3? known = enemy.KnownPlaces.LastKnownPosition;
        var places = enemy.KnownPlaces;
        var source =
            ReferenceEquals(places.LastKnownPlace, places.LastSeenPlace) ? "seen"
            : ReferenceEquals(places.LastKnownPlace, places.LastHeardPlace) ? "heard"
            : ReferenceEquals(places.LastKnownPlace, places.LastSquadSeenPlace) ? "shared-seen"
            : ReferenceEquals(places.LastKnownPlace, places.LastSquadHeardPlace) ? "shared-heard"
            : "sain-last-known";
        var health = bot.Memory.Health.HealthStatus;
        return new Situation
        {
            Now = Time.time,
            Target = enemy.EnemyProfileId,
            Personality = bot.Info.Personality.ToString(),
            IsAi = bot.Player.IsAI,
            IsSupportedBot = IsSupportedAi(bot),
            IsAuthority = authority,
            IsActive = bot.BotActive && !bot.IsDead,
            Ghost = ghost,
            Visible = enemy.IsVisible,
            SightWithoutShot = enemy.IsVisible && !enemy.CanShoot && (enemy.Seen || enemy.Heard),
            HasContact = known.HasValue && ContactSafety.Allows(bot, enemy),
            ContactAge = enemy.TimeSinceLastKnownUpdated,
            RealDistance = enemy.RealDistance,
            KnownDistance = known.HasValue ? Vector3.Distance(bot.Position, known.Value) : 0,
            KnownX = known?.x ?? 0,
            KnownY = known?.y ?? 0,
            KnownZ = known?.z ?? 0,
            ContactSource = source,
            LowAmmo = bot.Decision.SelfActionDecisions.LowOnAmmo(.3f),
            Hurt = health == ETagStatus.BadlyInjured || health == ETagStatus.Dying,
            UnderFire = bot.BotOwner.Memory.IsUnderFire,
            Frozen = bot.Decision.CurrentCombatDecision == ECombatDecision.Freeze,
            Suppressed = bot.Suppression.IsSuppressed,
            InCover = bot.Cover.CoverInUse != null,
        };
    }

    internal static void Capabilities(
        BotComponent bot,
        Enemy enemy,
        Situation s,
        BotState state,
        CometConfig config
    )
    {
        var known = enemy.KnownPlaces.LastKnownPosition!.Value;
        state.SelectedCover = VisibleAdapter.SelectCover(bot);
        // Push, Search and ShiftCover execute their native deciders lazily in rank order.
        s.Unavailable[Tactic.Grenade] = GrenadeCapability(bot, enemy, known, state, config);
        if (s.Unavailable[Tactic.Grenade] == "ready")
            s.Unavailable.Remove(Tactic.Grenade);
    }

    private static string GrenadeCapability(
        BotComponent bot,
        Enemy enemy,
        Vector3 known,
        BotState state,
        CometConfig config
    )
    {
        var grenades = bot.BotOwner.WeaponManager.Grenades;
        if (grenades.ThrowindNow)
            return "ready";
        if (
            config.Weights["Grenade"] == 0
            || enemy.TimeSinceLastKnownUpdated > config.GrenadeContactLifetime
        )
            return "disabled-or-stale-contact";
        if (!SainPresetAccess.AllowsGrenades(enemy.IsAI) || !bot.Info.FileSettings.Core.CanGrenade)
            return "sain-grenades-disabled";
        if (!grenades.HaveGrenade)
            return "no-grenade";
        var decider = bot.Grenade.GrenadeThrowDecider;
        object[] reason = { "" };
        if (!(bool)checkThrow.Invoke(decider, reason))
            return "sain:" + reason[0];
        object[] target = { enemy, "" };
        if (!(bool)checkTarget.Invoke(decider, target))
            return "sain:" + target[1];
        // Preparation computes a trajectory but NEVER throws. Actual DoThrow occurs only after selection.
        if (
            config.GrenadeTrajectory.Enabled
            || Time.time >= state.NextGrenadeProbe
            || (known - state.GrenadeTarget).sqrMagnitude > 1
        )
        {
            // Capability, room-clearing and search continuation can all query in
            // one frame. Advance the same incremental trajectory at most once.
            bool same = state.GrenadeProbeGate.Same(
                Time.frameCount,
                enemy.EnemyProfileId,
                known.x,
                known.y,
                known.z
            );
            if (
                state.GrenadeProbeGate.TryEnter(
                    Time.frameCount,
                    enemy.EnemyProfileId,
                    known.x,
                    known.y,
                    known.z
                )
            )
            {
                state.GrenadeReady = false;
                if (!config.GrenadeTrajectory.Enabled && probeFrame == Time.frameCount)
                    return "probe-budget-deferred";
                probeFrame = Time.frameCount;
                state.NextGrenadeProbe = Time.time + .75f;
                state.GrenadeTarget = known;
                Plugin.Instance?.RefreshEnvironment(bot, state, true);
                state.GrenadeReady = (bool)prepareThrow.Invoke(decider, new object[] { enemy });
            }
            else if (!same)
                return "probe-budget-deferred";
        }
        bool ready = state.GrenadeReady && grenades.ReadyToThrow;
        if (ready && config.GrenadeJudgment.Enabled && Plugin.Instance != null)
            ready = Plugin.Instance.AssessGrenade(bot, enemy, state, true);
        return ready ? "ready" : "no-ready-throw-trajectory";
    }

    internal static void ContinueGrenadeSearch(BotComponent b, BotState s, CometConfig c)
    {
        var e = b.GoalEnemy;
        var known = e?.KnownPlaces.LastKnownPosition;
        if (e != null && known.HasValue && ContactSafety.Allows(b, e))
            GrenadeCapability(b, e, known.Value, s, c);
    }

    internal static bool GrenadeFriendsClear(BotComponent b, Vector3 target) =>
        (bool)checkGrenadeFriends.Invoke(b.Grenade.GrenadeThrowDecider, new object[] { target });

    internal static string PrepareRoomGrenade(BotComponent b, Enemy e, BotState s, CometConfig c)
    {
        if (b.BotOwner.WeaponManager.Grenades.ThrowindNow)
            return "clear-foreign-throw-in-progress";
        string reason = GrenadeCapability(b, e, e.KnownPlaces.LastKnownPosition!.Value, s, c);
        return reason == "ready" ? "" : reason;
    }

    internal static bool Throw(BotComponent bot, Enemy enemy, out string reason)
    {
        if (!ContactSafety.Allows(bot, enemy))
        {
            reason = "grenade-own-squad-contact";
            return false;
        }
        return bot.Grenade.GrenadeThrowDecider.GetDecision(enemy, out reason);
    }

    internal static ECombatDecision Map(Tactic tactic) =>
        tactic switch
        {
            Tactic.Push => ECombatDecision.RushEnemy,
            Tactic.Grenade => ECombatDecision.ThrowGrenade,
            Tactic.Hold => ECombatDecision.Freeze,
            Tactic.Flank => ECombatDecision.ShiftCover,
            Tactic.Search => ECombatDecision.Search,
            Tactic.FallBack => ECombatDecision.SeekCover,
            _ => throw new ArgumentOutOfRangeException(nameof(tactic)),
        };
}
