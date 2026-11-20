using System;
using Comet.Core;
using EFT;
using SAIN.Components;
using SAIN.Models.Enums;
using SAIN.Preset.Shared.Enums;
using SAIN.SAINComponent.Classes.EnemyClasses;
using SAIN.SAINComponent.Classes.WeaponFunction;
using UnityEngine;
using UnityEngine.AI;

namespace Comet.Client;

public sealed partial class Plugin
{
    private bool HasDoorAction(BotState s) =>
        s.Door.Policy.Active || s.Door.Policy.Planning || s.Jump.Policy.Active
        || s.RunBy.Policy.Active || s.StepPeek.Policy.Active || s.RoomClear.Policy.Active
        || s.Entry.Policy.Active || s.Fake.Policy.Active || s.Interruption.Policy.Pending;

    private static DoorSessionEnd DoorSessionReleaseReason(BotState s, string reason)
    {
        if (reason == "door-enemy-visible")
            return DoorSessionEnd.EnemySpotted;
        if (reason == "storm-native-rush")
            return DoorSessionEnd.RushInstead;
        // These paths run after SAIN committed its decision. A pre-decision priority check
        // (including the door opener's own interaction) does not establish an action change.
        if (reason != "sain-request-not-applied" && reason != "native-decision-cleared"
            && !reason.StartsWith("sain-priority:", StringComparison.Ordinal))
            return DoorSessionEnd.Ordinary;
        var next = s.Owner.Decision.CurrentCombatDecision;
        if (next == (s.Door.SessionPlan == "trap" ? ECombatDecision.Freeze : ECombatDecision.Search))
            return DoorSessionEnd.Ordinary;
        if (next == ECombatDecision.StandAndShoot)
            return DoorSessionEnd.EnemySpotted;
        return s.Owner.BotOwner.Memory.IsUnderFire ? DoorSessionEnd.Ordinary : DoorSessionEnd.Interrupted;
    }

    private void CloseFailedDoorSession(BotState s, string door, string outcome, bool doorOpen = false,
        bool independent = false)
    {
        var reason = DoorSessionCooldowns.FailureReason(outcome, doorOpen);
        if (reason == DoorSessionEnd.Ordinary || door.Length == 0)
            return;
        var r = s.Door;
        if (r.SessionDoor == door)
            CloseDoorSession(s, reason);
        else if (independent && r.SessionDoor.Length == 0)
        {
            // Resumed approaches, utility holds and support assignments can own a move independently.
            var cooldown = r.Cooldowns.FinishFailure(door, Time.time);
            Logger.LogDebug($"D01 {s.ProfileId} ended {outcome} at {door} reason={reason} "
                + $"retrySame={cooldown.SameDoor}s retryOther={cooldown.OtherDoor}s");
        }
    }

    private void CloseDoorSession(BotState s, DoorSessionEnd reason)
    {
        var r = s.Door;
        if (r.SessionDoor.Length == 0)
            return;
        float now = Time.time;
        string door = r.SessionDoor, plan = r.SessionPlan, target = r.SessionEnemy;
        var cooldown = r.Cooldowns.FinishSession(door, r.SessionAt, now, reason);
        r.SessionDoor = r.SessionPlan = r.SessionEnemy = r.Feint = "";
        r.FromSearch = r.Entered = false;
        Logger.LogDebug($"D01 {s.ProfileId} ended {plan} at {door} target={target} reason={reason} "
            + $"age={now - r.SessionAt:F3}s retrySame={cooldown.SameDoor}s retryOther={cooldown.OtherDoor}s");
    }

    private bool KeepCombatDoor(BotComponent b, Enemy enemy, BotState s, ref ECombatDecision decision, ref bool result)
    {
        var r = s.Door;
        if (r.SessionPlan.Length == 0)
            return false;
        bool trap = r.SessionPlan == "trap";
        if (enemy.EnemyProfileId != r.SessionEnemy)
        {
            CloseDoorSession(s, DoorSessionEnd.GoalEnemyChanged);
            return false;
        }
        if (Time.time - r.SessionAt > 150 || b.BotOwner.Memory.IsUnderFire
            || (r.Entered && !HasDoorAction(s) && !s.Ambush.Policy.Active))
        {
            CloseDoorSession(s, DoorSessionEnd.Ordinary);
            return false;
        }
        decision = s.Expected = trap ? ECombatDecision.Freeze : ECombatDecision.Search;
        s.RequestedFrame = Time.frameCount;
        result = true;
        return true;
    }

    private bool EnsureDoorSession(BotComponent b, Enemy e, BotState s, bool search)
    {
        var r = s.Door;
        if (r.SessionPlan.Length != 0)
        {
            if (r.SessionEnemy == e.EnemyProfileId)
                return true;
            if (b.GoalEnemy?.EnemyProfileId == e.EnemyProfileId)
                CloseDoorSession(s, DoorSessionEnd.GoalEnemyChanged);
            return false;
        }
        var c = config!;
        if (!c.Enabled || !Authority || !b.BotActive || b.GameEnding || Ghost(b.ProfileId) || !NativeAdapter.IsSupportedAi(b)
            || !c.DoorApproach.Enabled || r.Fault || e.IsVisible || !e.EnemyKnown
            || e.KnownPlaces.LastKnownPosition is not Vector3 known
            || e.Seen && e.TimeSinceSeen < c.DoorApproach.UnseenDelay || !r.Cooldowns.CanProbe(Time.time)
            || search && (!c.DoorRoomClear.Enabled || !c.DoorSearchClear.Enabled))
            return false;
        double age = e.TimeSinceLastKnownUpdated;
        if (age > (search ? c.DoorSearchClear.ContactLifetime : c.DoorApproach.ContactLifetime))
            return false;
        string personality = b.Info.Personality.ToString();
        double chance = search ? c.DoorSearchClear.Chance
            : c.DoorJumpPeek.Chances.TryGetValue(personality, out var value) ? value : 0;
        if (!search && age >= 10 && age <= 60) chance *= 1.8;
        if (chance <= 0) return false;

        if (NearestTacticalDoor(b, e, s, known) is not { } door || DoorBusy(b, s, door.Geometry.Id)
            || !r.Cooldowns.CanUse(door.Geometry.Id, Time.time))
            return false;
        double roll = jumpRandom.NextDouble() * 100;
        string plan = roll <= chance ? search ? "clear" : PickDoorPlan(b, e, door, personality) : "";
        if (plan.Length == 0)
        {
            Logger.LogDebug($"D01 {b.ProfileId} declined door {door.Geometry.Id} ({(search ? "search" : "combat")}, {roll:F1}/{chance:F1})");
            r.Cooldowns.Finish(door.Geometry.Id, Time.time, declined: true);
            return false;
        }
        Logger.LogDebug($"D01 {b.ProfileId} selected {plan} at {door.Geometry.Id} ({(search ? "search" : "combat")}, {roll:F1}/{chance:F1})");
        r.NextPlan = 0;
        r.Entered = false;
        r.SessionAt = Time.time;
        r.SessionEnemy = e.EnemyProfileId;
        r.SessionDoor = door.Geometry.Id;
        r.FromSearch = search;
        r.SessionPlan = plan;
        r.SelectionChance = Math.Min(100, chance);
        r.SelectionRoll = roll;
        r.Feint = plan == "peek" ? PlanPeekFeint(b, personality) : "";
        return true;
    }

    private DoorWork? NearestTacticalDoor(BotComponent b, Enemy e, BotState s, Vector3 known)
    {
        var r = s.Door;
        var c = config!;
        if (r.ProbeTarget != e.EnemyProfileId || Time.time >= r.ProbeUntil)
        {
            r.ProbeTarget = e.EnemyProfileId;
            r.ProbeUntil = Time.time + .25f;
            r.Nearby = null;
            double nearest = double.MaxValue;
            foreach (var link in NearbyDoors(b))
            {
                if (link?.Door == null) continue;
                var work = new DoorWork { Data = new(link) };
                if (!TryDoorGeometry(work.Data, out var g) ||
                    DoorPolicy.Geometry(g, DP(b.Position), DP(known), Array.Empty<DoorPoint>(), c.DoorApproach).Length != 0)
                    continue;
                double distance = SupportHorizontal(b.Position, VP(g.Center));
                if (distance >= nearest) continue;
                work.Geometry = g;
                r.Nearby = work;
                nearest = distance;
            }
        }
        return r.Nearby;
    }

    private string PickDoorPlan(BotComponent b, Enemy e, DoorWork door, string personality)
    {
        var c = config!;
        if (personality is "Rat" or "SnappingTurtle") return c.DoorAmbush.Enabled ? "trap" : "";
        if (personality == "Wreckless" || personality == "Normal" && !b.Info.Profile.IsPMC)
            return c.DoorRoomClear.Enabled ? "clear" : "";
        if (personality is not ("GigaChad" or "Chad" or "Normal")) return "";
        var center = VP(door.Geometry.Center);
        var normal = VP(door.Geometry.Normal);
        float side = Mathf.Sign(Vector3.Dot(b.Position - center, normal));
        bool peekPoint = NavMesh.SamplePosition(center + normal * side * 1.25f, out var hit, .7f, NavMesh.AllAreas)
            && Vector3.Dot(hit.position - center, normal) * side > .3f;
        int mates = 0;
        if (b.Squad?.Members != null)
            foreach (var mate in b.Squad.Members.Values)
                if (mate != null && !ReferenceEquals(mate, b) && !mate.IsDead && (mate.Position - center).magnitude < 15) mates++;
        var heard = e.KnownPlaces.LastHeardPlace;
        bool moving = heard != null && Time.time - silentHeardAt(heard) < 3
            && heard.SoundType is SAINSoundType.FootStep or SAINSoundType.Sprint
            && (heard.Position - center).magnitude < 5;
        float depth = Mathf.Abs(Vector3.Dot(e.KnownPlaces.LastKnownPosition!.Value - center, normal));
        return DoorPlanWeights.Pick(c.DoorRoomClear.Enabled, c.DoorJumpPeek.Enabled && peekPoint,
            personality == "GigaChad" && c.DoorAmbush.Enabled, e.TimeSinceLastKnownUpdated,
            ZzapEnemyWeakness.Score(b, e), mates, b.Memory.Health.HealthStatus != ETagStatus.Healthy
                || SAINBotSuppressClass.CalcAmmoRatio(b.BotOwner, out _) < .5f, depth, moving, jumpRandom.NextDouble(),
            c.DoorRoomClear.RoomClearChance, c.DoorJumpPeek.GigaChadPeekChance);
    }

    private string PlanPeekFeint(BotComponent b, string personality)
    {
        var c = config!;
        if (!c.DoorFakeGrenade.Chances.TryGetValue(personality, out double chance) || jumpRandom.NextDouble() * 100 >= chance)
            return "";
        bool grenade = c.DoorFakeGrenade.Enabled && SainPresetAccess.AllowsGrenades() && b.Info.FileSettings.Core.CanGrenade
            && b.BotOwner.WeaponManager?.Grenades.HaveGrenade == true;
        return DoorFeintChoice.Pick(grenade, FindFakeMedical(b, out _, out _), jumpRandom.NextDouble(), c.DoorFakeMedical.Preference);
    }

    private void FinishPeekFeint(BotState s, BotComponent? b, DoorWork? door)
    {
        if (b?.GoalEnemy is not { } e || door == null || s.Door.Feint.Length == 0) return;
        TryStartDoorFake(b, e, s, door, planned: s.Door.Feint);
        s.Door.Feint = "";
    }
}
