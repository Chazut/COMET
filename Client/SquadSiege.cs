using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Comet.Core;
using EFT;
using SAIN.Components;
using SAIN.Preset.Shared.Enums;
using UnityEngine;

namespace Comet.Client;

internal sealed class SiegeWatch
{
    internal readonly BotState State;
    internal readonly SiegeExit Exit;
    internal readonly Vector3 Position;

    internal SiegeWatch(BotState state, SiegeExit exit)
    {
        State = state;
        Exit = exit;
        Position = state.Owner.Position;
    }
}

internal sealed class SiegeSquad
{
    internal sealed class Observation
    {
        internal float UsedAt = float.NegativeInfinity,
            NextProbe;
        internal Vector3 Point;
        internal int Scenes = -1;
        internal SpatialAudioRoom? Room;
    }

    internal readonly Dictionary<string, Observation> Contacts = new();
    internal readonly List<SiegeWatch> Watches = new();
    internal SiegeBuilding? Building;
    internal string Contact = "";
    internal float Until,
        NextCheck;
}

public sealed partial class Plugin
{
    private ConditionalWeakTable<object, SiegeSquad> siegeSquads = new();
    private SpatialAudioRoom[]? siegeRooms;
    private int siegeScenes = -1;
    private readonly Dictionary<SpatialAudioRoom, SiegeBuilding?> siegeBuildings = new();

    partial void ClearSieges()
    {
        foreach (var state in states.Values)
            if (state.Siege != null)
                EndSiege(state.Siege, "siege-raid-ended");
        siegeRooms = null;
        siegeScenes = -1;
        siegeBuildings.Clear();
        siegeSquads = new();
    }

    private SpatialAudioRoom? SiegeRoomAt(Vector3 feet)
    {
        int scenes = UnityEngine.SceneManagement.SceneManager.sceneCount;
        if (siegeRooms == null || scenes != siegeScenes)
        {
            siegeRooms = UnityEngine.Object.FindObjectsOfType<SpatialAudioRoom>();
            siegeBuildings.Clear();
            siegeScenes = scenes;
        }
        var point = feet + Vector3.up;
        SpatialAudioRoom? best = null;
        foreach (var room in siegeRooms)
            if (
                room != null
                && room.IsValid
                && !room.IsOutdoor
                && SiegeBuilding.Contains(room, point)
                && (best == null || room.RoomSize < best.RoomSize)
            )
                best = room;
        return best;
    }

    private SiegeBuilding? SiegeMap(SpatialAudioRoom room, out string reason)
    {
        if (siegeBuildings.TryGetValue(room, out var cached))
        {
            reason = cached == null ? "unverified-exits" : "mapped";
            return cached;
        }
        var building = SiegeBuilding.Read(room, world!, out reason);
        if (building != null)
            foreach (var part in building.Rooms)
                siegeBuildings[part] = building;
        else if (reason != "incomplete-room-graph")
            siegeBuildings[room] = null;
        return building;
    }

    // Use current position only on genuine SAIN visibility. Hidden enemies
    // contribute a timestamped visual memory from the same squad.
    private bool SiegeSighting(
        Dictionary<string, BotComponent> members,
        string contact,
        out Vector3 point,
        out float at
    )
    {
        point = default;
        at = float.NegativeInfinity;
        foreach (var member in members.Values)
        {
            if (
                member == null
                || member.IsDead
                || !member.BotActive
                || Ghost(member.ProfileId)
                || !member.EnemyController.Enemies.TryGetValue(contact, out var enemy)
                || enemy == null
                || !enemy.EnemyKnown
                || !ContactSafety.Allows(member, enemy)
            )
                continue;
            if (enemy.IsVisible)
            {
                point = enemy.EnemyPosition;
                at = Time.time;
                return ValidPoint(point);
            }
            var seen = enemy.KnownPlaces.LastSeenPlace;
            var shared = enemy.KnownPlaces.LastSquadSeenPlace;
            if (
                shared != null
                && (seen == null || shared.TimeSincePositionUpdated < seen.TimeSincePositionUpdated)
            )
                seen = shared;
            if (seen == null || !ValidPoint(seen.Position))
                continue;
            float stamp = Time.time - seen.TimeSincePositionUpdated;
            if (stamp <= at || !float.IsFinite(stamp))
                continue;
            point = seen.Position;
            at = stamp;
        }
        return Time.time - at <= config!.SecondaryEntrance.ContactAge;
    }

    private bool SiegeAvailable(BotComponent b, BotState state, string contact)
    {
        if (
            b.IsDead
            || !b.SAINLayersActive
            || b.GoalEnemy is not { EnemyKnown: true } enemy
            || enemy.EnemyProfileId != contact
            || !TacticalScope(b, enemy)
            || ReadDowned(b.Player)
            || b.Memory.Location.IsIndoors
            || b.Player.Environment == EnvironmentType.Indoor
            || !HolderAvailable(b, state)
            || state.PlanService.Active
            || NativeStormActive(b)
            || ActiveSquadGrenade(b, state)
            || Time.time < state.FaultUntil
        )
            return false;
        foreach (var other in b.EnemyController.KnownEnemies)
            if (other != null && other.IsVisible)
                return false;
        return true;
    }

    private bool HoldForSiege(
        BotComponent bot,
        BotState state,
        ref ECombatDecision decision,
        ref bool accepted
    )
    {
        var settings = config!.SecondaryEntrance;
        var members = bot.Squad?.Members;
        if (!settings.Enabled || !settings.Siege || members == null || members.Count < 2)
        {
            if (state.Siege != null)
                EndSiege(state.Siege, "siege-disabled-or-group-ended");
            return false;
        }
        var group = siegeSquads.GetValue(members, _ => new SiegeSquad());
        if (state.Siege != null && state.Siege != group)
            EndSiege(state.Siege, "siege-group-changed");
        if (group.Building != null)
        {
            if (!ValidateSiege(group, members))
                return false;
        }
        else
        {
            var target = bot.GoalEnemy;
            if (target == null || target.IsVisible)
                return false;
            if (!group.Contacts.TryGetValue(target.EnemyProfileId, out var observation))
                group.Contacts.Add(
                    target.EnemyProfileId,
                    observation = new SiegeSquad.Observation()
                );
            if (
                Time.time < observation.NextProbe
                || !SiegeAvailable(bot, state, target.EnemyProfileId)
            )
                return false;
            observation.NextProbe = Time.time + (float)config.Crossfire.Interval;
            if (
                !SiegeSighting(members, target.EnemyProfileId, out var position, out float at)
                || at <= observation.UsedAt + .001f
            )
                return false;
            int scenes = UnityEngine.SceneManagement.SceneManager.sceneCount;
            if (observation.Scenes != scenes || observation.Point != position)
            {
                observation.Room = SiegeRoomAt(position);
                observation.Scenes = scenes;
                observation.Point = position;
            }
            var room = observation.Room;
            if (room == null)
                return false;
            var building = SiegeMap(room, out string reason);
            if (building == null || building.Exits.Count > members.Count)
            {
                RecordCrossfireGate(
                    state,
                    building == null ? "siege-" + reason : "siege-too-few-members",
                    feature: "S11"
                );
                return false;
            }
            var candidates = new List<BotState>();
            foreach (var member in members.Values)
            {
                if (
                    member == null
                    || member.IsDead
                    || member.GoalEnemy?.EnemyProfileId != target.EnemyProfileId
                )
                    continue;
                if (!states.TryGetValue(member, out var actor))
                {
                    if (states.Count >= 512 || !TacticalScope(member, member.GoalEnemy))
                        continue;
                    states.Add(member, actor = new BotState(member, config, session));
                }
                if (actor.Siege != null || !SiegeAvailable(member, actor, target.EnemyProfileId))
                    continue;
                candidates.Add(actor);
            }
            if (candidates.Count < building.Exits.Count)
                return false;
            var coverage = new bool[building.Exits.Count, candidates.Count];
            for (int e = 0; e < building.Exits.Count; e++)
            for (int m = 0; m < candidates.Count; m++)
                coverage[e, m] = building
                    .Exits[e]
                    .VisibleFrom(candidates[m].Owner.Transform.EyePosition, settings.MaxPath);
            var watchers = SiegeCoverage.Assign(coverage);
            if (watchers == null)
            {
                RecordCrossfireGate(state, "siege-incomplete-coverage", feature: "S11");
                return false;
            }
            var leader = bot.Squad!.LeaderComponent;
            group.Until =
                Time.time
                + (float)
                    SiegeCoverage.WatchTime(
                        settings,
                        leader?.Info.Personality.ToString() ?? "Normal"
                    );
            group.Contact = target.EnemyProfileId;
            group.Building = building;
            group.NextCheck = Time.time + (float)config.Crossfire.Interval;
            observation.UsedAt = at;
            for (int e = 0; e < watchers.Length; e++)
            {
                var watcher = candidates[watchers[e]];
                watcher.Siege = group;
                watcher.SiegeExit = building.Exits[e];
                group.Watches.Add(new SiegeWatch(watcher, building.Exits[e]));
            }
        }
        if (state.Siege != group)
            return false;
        if (!SiegeAvailable(bot, state, group.Contact))
        {
            EndSiege(group, "siege-member-unavailable");
            return false;
        }
        if (state.Decision != "Siege - watch exit" || state.Feature != "S11")
        {
            Release(state, "siege-taking-position", false, true);
            state.Feature = "S11";
            state.Decision = "Siege - watch exit";
            state.Candidates = new()
            {
                new Candidate
                {
                    Decision = state.Decision,
                    Eligible = true,
                    Reason = state.SiegeExit!.Id,
                },
            };
            state.Mixing = null;
            Emit(state, "selected", "siege-exits-covered");
        }
        state.Expected = decision = ECombatDecision.Freeze;
        state.RequestedFrame = Time.frameCount;
        accepted = true;
        return true;
    }

    private bool ValidateSiege(SiegeSquad group, Dictionary<string, BotComponent> members)
    {
        if (Time.time >= group.Until)
        {
            EndSiege(group, "siege-watch-expired");
            return false;
        }
        if (Time.time < group.NextCheck)
            return true;
        group.NextCheck = Time.time + (float)config!.Crossfire.Interval;
        if (!SiegeSighting(members, group.Contact, out var position, out _))
        {
            EndSiege(group, "siege-contact-lost");
            return false;
        }
        if (!group.Building!.ContainsPosition(position + Vector3.up))
        {
            EndSiege(group, "siege-contact-left-building");
            return false;
        }
        foreach (var watch in group.Watches)
        {
            var s = watch.State;
            if (
                !members.TryGetValue(s.ProfileId, out var b)
                || b == null
                || b != s.Owner
                || s.Siege != group
                || !SiegeAvailable(b, s, group.Contact)
            )
            {
                EndSiege(group, "siege-member-unavailable");
                return false;
            }
            if (
                (b.Position - watch.Position).sqrMagnitude > 1
                || !watch.Exit.VisibleFrom(
                    b.Transform.EyePosition,
                    config.SecondaryEntrance.MaxPath
                )
            )
            {
                EndSiege(group, "siege-coverage-lost");
                return false;
            }
        }
        return true;
    }

    private void SampleSiege(BotComponent b, BotState s)
    {
        if (s.Siege is not { } group)
            return;
        if (
            !Authority
            || config?.Enabled != true
            || !config.SecondaryEntrance.Enabled
            || !config.SecondaryEntrance.Siege
            || b == null
            || b.Squad?.Members is not { } members
            || Ghost(s.ProfileId)
        )
            EndSiege(group, "siege-scope-ended");
        else
            ValidateSiege(group, members);
    }

    private void EndSiege(SiegeSquad group, string reason)
    {
        if (group.Building == null)
            return;
        group.Building = null;
        var observation = group.Contacts[group.Contact];
        observation.UsedAt = Time.time;
        observation.NextProbe = Time.time + (float)(config?.Crossfire.Interval ?? 1);
        foreach (var watch in group.Watches)
        {
            var s = watch.State;
            if (s.Siege != group)
                continue;
            if (s.Feature == "S11" && s.Decision.Length > 0)
            {
                Emit(s, "released", reason);
                s.RequestedFrame = -1;
                s.Decision = s.ActiveLabel = "";
                Api.Decisions.Set(s.ProfileId, "");
            }
            s.Siege = null;
            s.SiegeExit = null;
        }
        group.Watches.Clear();
    }

    private bool SiegeGaze(BotComponent b, BotState s, out Vector3 direction)
    {
        direction = default;
        if (
            s.SiegeExit == null
            || s.ActiveLabel != "COMET:Siege - watch exit"
            || b.GoalEnemy?.IsVisible != false
            || config?.Enabled != true
            || !config.SecondaryEntrance.Siege
            || !config.SecondaryEntrance.Enabled
            || !Authority
            || !b.BotActive
            || b.IsDead
            || Ghost(s.ProfileId)
            || b.Decision.CurrentSelfDecision != ESelfActionType.None
            || b.Decision.CurrentCombatDecision != ECombatDecision.Freeze
            || b.CurrentAction?.Name != "FreezeAction"
        )
            return false;
        foreach (var contact in b.EnemyController.KnownEnemies)
            if (contact != null && contact.IsVisible)
                return false;
        direction = (s.SiegeExit.Center - b.Transform.EyePosition).normalized;
        return true;
    }
}
