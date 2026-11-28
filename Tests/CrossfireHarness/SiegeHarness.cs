using Audio.SpatialSystem;
using Comet.Client;
using Comet.Core;
using EFT.Interactive;
using SAIN.Components;
using SAIN.Preset.Shared.Enums;
using SAIN.SAINComponent.Classes.EnemyClasses;
using UnityEngine;

internal static class SiegeHarness
{
    internal static void Run()
    {
        int checks = 0;
        void Check(bool ok, string message)
        {
            checks++;
            if (!ok)
                throw new Exception("Siege: " + message);
        }

        var complex = SiegeCoverage.Assign(
            new bool[,]
            {
                { true, true },
                { true, false },
            }
        );
        Check(
            complex != null && complex[0] == 1 && complex[1] == 0,
            "can reassign an earlier flexible watcher"
        );
        Check(
            SiegeCoverage.Assign(
                new bool[,]
                {
                    { true, false },
                    { true, false },
                }
            ) == null,
            "one watcher cannot cover two exits"
        );
        Check(
            SiegeCoverage.Assign(
                new bool[,]
                {
                    { true, true },
                }
            ) == null,
            "single access remains ordinary S02 territory"
        );
        var settings = new SecondaryEntranceConfig();
        Check(
            settings.Siege
                && SiegeCoverage.WatchTime(settings, "Rat")
                    > SiegeCoverage.WatchTime(settings, "Chad"),
            "on by default, cautious leader waits longer"
        );

        foreach (
            string mode in new[]
            {
                "full",
                "duplicate-door",
                "missing-link",
                "one-way-link",
                "unknown-window",
                "same-view",
                "solo",
                "heard-only",
                "shared",
                "remote",
                "disabled",
                "indoor",
                "ghost",
                "replica",
                "moved",
                "occluded",
                "self-care",
                "expired",
                "left-building",
                "death",
                "signal-lost",
                "target-changed",
                "newer-outside",
                "third-free",
                "split-targets",
                "ownership",
                "contact-visible",
                "raid-end",
                "two-rooms",
                "late-registry",
                "replaced-building",
                "static-exit",
                "reserve-four-exits",
                "reserve-mixed",
                "reserve-missing-watcher",
                "reserve-blocked-passage",
                "reserve-late-door",
                "reserve-lost-door",
                "doorless-building",
            }
        )
        {
            bool reserve = mode.StartsWith("reserve-", StringComparison.Ordinal);
            Time.time = reserve ? 271.3f : 100;
            var p = new Plugin();
            var inside = new SpatialAudioRoom
            {
                Colliders = [new BoxCollider { size = new(8, 6, 8) }],
            };
            var outside = new SpatialAudioRoom
            {
                IsOutdoor = true,
                Colliders = [new BoxCollider { size = new(100, 15, 100) }],
            };
            var group = new SpatialAudioCrossSceneGroup { AudioRooms = [inside, outside] };
            SpatialAudioCrossSceneGroup.AllCrossGroups.Clear();
            SpatialAudioCrossSceneGroup.AllCrossGroups.Add(group);
            var storage = new AudioRoomStorage { _validSpatialAudioRooms = [inside, outside] };
            SpatialAudioSystem.Instance = new();
            SpatialAudioSystem.Instance.Publish(storage);
            var inner = inside;
            if (mode == "two-rooms")
            {
                inner = new()
                {
                    Colliders =
                    [
                        new()
                        {
                            size = new(4, 6, 8),
                            transform = new() { position = new(6, 0, 0) },
                        },
                    ],
                };
                Connect(inside, inner, "hall", 2);
                group.AudioRooms.Add(inner);
                storage._validSpatialAudioRooms.Add(inner);
            }
            var first = Connect(inside, outside, "front", -3);
            var second = Connect(inner, outside, mode == "duplicate-door" ? "front" : "back", 3);
            p.localDoorIds.Add(new Door { Id = "front" });
            p.localDoorIds.Add(new Door { Id = "back" });
            if (mode == "missing-link")
                second.BackRoom = null!;
            if (mode == "one-way-link")
                outside.roomConnections.Clear();
            if (mode is "unknown-window" or "static-exit")
            {
                second.portalType =
                    mode == "unknown-window"
                        ? BaseSpatialAudioPortal.PortalType.Window
                        : BaseSpatialAudioPortal.PortalType.Static;
                second.portalCollider.transform.position.z = mode == "unknown-window" ? 15 : 4;
                // Model non-overlapping inside/outside volumes at the static threshold.
                outside.Colliders[0].size = new(100, 15, 10);
                outside.Colliders[0].transform.position.z = 9;
            }
            if (reserve || mode == "doorless-building")
            {
                outside.Colliders[0].size = new(100, 15, 10);
                outside.Colliders[0].transform.position.z = 9;
                if (mode == "doorless-building")
                {
                    first.DoorID = second.DoorID = "";
                    first.ID = 1;
                    second.ID = 2;
                    first.portalCollider.transform.position.z = second.portalCollider.transform.position.z = 4;
                    p.LocalDoorsComplete = false;
                }
                else
                {
                    // Representative geometry: the Reserve recording contains no exit inventory.
                    var passage = Connect(inside, outside, "", 1.5f);
                    passage.ID = 77;
                    passage.portalCollider.transform.position.z = mode == "reserve-blocked-passage" ? 15 : 4;
                }
                if (mode == "reserve-four-exits")
                {
                    Connect(inside, outside, "service", -1.5f);
                    p.localDoorIds.Add(new Door { Id = "service" });
                }
            }
            var a = new BotComponent();
            var b = new BotComponent();
            var c = new BotComponent();
            var bots =
                mode == "solo" ? new[] { a }
                : reserve || mode is "third-free" or "split-targets" ? new[] { a, b, c }
                : new[] { a, b };
            if (reserve)
            {
                a.ProfileId = a.Player.ProfileId = a.BotOwner.ProfileId = "6b0a98f7bd99ac3e54d3583b";
                b.ProfileId = b.Player.ProfileId = b.BotOwner.ProfileId = "6b0a98f7bd99ac3e54d35a35";
            }
            var members = bots.ToDictionary(bot => bot.ProfileId);
            for (int i = 0; i < bots.Length; i++)
            {
                var bot = bots[i];
                bot.SAINLayersActive = true;
                bot.Squad.Members = members;
                bot.Squad.LeaderComponent = a;
                bot.Position = new(i == 0 ? -3 : 3, 0, 8);
                bot.Transform.EyePosition = bot.Position + Vector3.up;
                var enemy = bot.GoalEnemy!;
                if (reserve)
                    enemy.EnemyProfileId = "6b0a98f7bd99ac3e54d35473"; // DY-77-- on Reserve
                enemy.EnemyPlayer.IsAI = mode != "remote";
                enemy.KnownPlaces.LastKnownPosition = new(0, 0, 0);
                enemy.EnemyPosition = new(80, 0, 30); // Hidden true position must have no effect.
                bot.EnemyController.Enemies[enemy.EnemyProfileId] = enemy;
                bot.EnemyController.KnownEnemies.Add(enemy);
                p.states[bot] = new BotState(bot, p.config!, p.session);
            }
            if (mode == "shared")
                a.GoalEnemy!.KnownPlaces.LastSquadSeenPlace = new(new(), 99);
            else if (mode != "heard-only")
                a.GoalEnemy!.KnownPlaces.LastSeenPlace = new(new(), Time.time - 1);
            if (mode == "newer-outside")
                b.GoalEnemy!.KnownPlaces.LastSeenPlace = new(new(30, 0, 0), 100);
            p.config!.SecondaryEntrance.Siege = mode != "disabled";
            if (mode == "indoor")
                b.Player.Environment = EFT.EnvironmentType.Indoor;
            if (mode == "reserve-missing-watcher")
                c.Player.Environment = EFT.EnvironmentType.Indoor;
            if (mode == "ghost")
                p.Ghosts.Add(b.ProfileId);
            if (mode == "replica")
                p.Authority = false;
            Physics.Cast = (eye, exit) =>
                mode == "same-view" ? exit.x > 0 : Math.Sign(eye.x) != Math.Sign(exit.x);
            if (mode == "split-targets")
            {
                c.GoalEnemy = new Enemy { EnemyProfileId = "other" };
                Check(
                    !p.TestSiege(c),
                    "teammate on another target leaves the other contact eligible this tick"
                );
            }

            if (mode == "late-registry")
            {
                SpatialAudioSystem.Instance.Publish(null);
                SpatialAudioCrossSceneGroup.AllCrossGroups.Clear();
                Check(!p.TestSiege(a), "early lookup with no rooms can retry the same contact");
                SpatialAudioSystem.Instance.Publish(storage);
                Time.time += (float)p.config!.Crossfire.Interval + .01f;
            }
            if (mode == "replaced-building")
            {
                second.DoorID = "front";
                Check(!p.TestSiege(a), "single exit is cached for the original layout");
                second.DoorID = "back";
                group.AudioRooms = [.. group.AudioRooms];
                Time.time += (float)p.config!.Crossfire.Interval + .01f;
            }
            if (mode == "reserve-late-door")
            {
                // Use the three observed probe times. Each new sighting permits another evaluation.
                second.DoorID = "unmapped-fixture";
                p.LocalDoorsComplete = false;
                Check(!p.TestSiege(a) && p.states[a].Crossfire.GateKey == "siege-door-index-pending",
                    "04:31.3 / 91-XM_: pending local index leaves everyone free");
                Time.time = 280.3f;
                a.GoalEnemy!.KnownPlaces.LastSeenPlace = new(new(), Time.time);
                p.LocalDoorsComplete = true;
                Check(!p.TestSiege(b) && p.states[b].Crossfire.GateKey == "siege-unresolved-exit-door",
                    "04:40.3 / China-LEIBABA: missing named exit prevents partial siege");
                Check(p.diagnostics!.Lines.Any(line => line.Contains("unmapped-fixture")
                    && line.Contains(a.GoalEnemy.EnemyProfileId)), "refusal identifies target and unknown door");
                Time.time = 286.6f;
                a.GoalEnemy.KnownPlaces.LastSeenPlace = new(new(), Time.time);
                p.localDoorIds.Add(new Door { Id = second.DoorID });
                Check(p.TestSiege(b), "04:46.6: resolved index invalidates the cached refusal");
            }
            bool expected =
                mode
                is not (
                    "solo"
                    or "duplicate-door"
                    or "missing-link"
                    or "one-way-link"
                    or "unknown-window"
                    or "heard-only"
                    or "same-view"
                    or "disabled"
                    or "indoor"
                    or "ghost"
                    or "replica"
                    or "newer-outside"
                    or "reserve-missing-watcher"
                    or "reserve-four-exits"
                    or "reserve-blocked-passage"
                );
            Check(p.TestSiege(a) == expected, mode + " selection");
            if (!expected)
            {
                Check(p.states.Values.All(state => state.Siege == null), mode + " no partial plan");
                if (reserve)
                {
                    string reason = mode switch
                    {
                        "reserve-four-exits" => "siege-too-few-members",
                        "reserve-blocked-passage" => "siege-unverified-exterior-portal",
                        _ => "siege-too-few-available-watchers",
                    };
                    Check(p.states[a].Crossfire.GateKey == reason, mode + " refusal reason");
                }
                continue;
            }
            Check(
                p.TestSiege(b) && p.states[a].SiegeExit!.Id != p.states[b].SiegeExit!.Id,
                mode + " distinct assignments"
            );
            if (reserve)
            {
                Check(p.TestSiege(c) && p.states.Values.Count(s => s.SiegeExit?.Id == "portal-77") == 1
                    && p.states.Values.Select(s => s.SiegeExit?.Id).Distinct().Count() == 3,
                    "two doors and an open passage each require their own watcher");
            }
            if (mode == "full")
            {
                a.Decision.CurrentCombatDecision = ECombatDecision.Freeze;
                a.CurrentAction = new() { Name = "FreezeAction" };
                Check(!p.TestSiegeGaze(a, out _), "steering waits for native acceptance");
                p.states[a].ActiveLabel = "COMET:Siege - watch exit";
                Check(
                    p.TestSiegeGaze(a, out var gaze)
                        && Vector3.Dot(
                            gaze,
                            (p.states[a].SiegeExit!.Center - a.Transform.EyePosition).normalized
                        ) > .99f,
                    "aim at assigned scene marker"
                );
                a.GoalEnemy!.IsVisible = true;
                Check(
                    !p.TestSiegeGaze(a, out _),
                    "native steering immediately owns a visible target"
                );
            }
            if (mode == "third-free")
                Check(!p.TestSiege(c) && p.states[c].Siege == null, "surplus member is free");
            if (mode == "raid-end")
            {
                p.ClearSiegeScene();
                Check(
                    p.states[a].Siege == null && p.states[b].Siege == null,
                    "raid cleanup releases all watchers"
                );
                continue;
            }
            Time.time++;
            switch (mode)
            {
                case "reserve-lost-door":
                    p.localDoorIds.Add(new Door { Id = "front" });
                    break;
                case "moved":
                    b.Position.x += 2;
                    break;
                case "occluded":
                    Physics.Cast = (_, _) => true;
                    break;
                case "self-care":
                    b.Decision.CurrentSelfDecision = ESelfActionType.Surgery;
                    break;
                case "expired":
                    Time.time = p.states[a].Siege!.Until + .1f;
                    break;
                case "left-building":
                    b.GoalEnemy!.KnownPlaces.LastSeenPlace = new(new(15, 0, 0), Time.time);
                    break;
                case "death":
                    b.IsDead = true;
                    break;
                case "signal-lost":
                    a.GoalEnemy!.KnownPlaces.LastSeenPlace = null;
                    break;
                case "target-changed":
                    b.GoalEnemy = null;
                    break;
                case "ownership":
                    p.Authority = false;
                    break;
                case "contact-visible":
                    b.GoalEnemy!.IsVisible = true;
                    break;
                default:
                    continue;
            }
            p.TickSiege(a);
            Check(p.states.Values.All(state => state.Siege == null), mode + " releases every watcher");
            if (mode == "expired")
            {
                Time.time++;
                Check(!p.TestSiege(a), "same visual memory cannot restart expired watch");
                a.GoalEnemy!.KnownPlaces.LastSeenPlace = new(new(), Time.time);
                Time.time++;
                Check(p.TestSiege(a), "fresh sighting can start a new watch");
            }
        }
        Physics.Cast = null;
        TestRoomSources(Check);
        SpatialAudioCrossSceneGroup.AllCrossGroups.Clear();
        SpatialAudioSystem.Instance = null;
        Console.WriteLine($"S11 siege: {checks} adapter checks passed.");
    }

    private static void TestRoomSources(Action<bool, string> check)
    {
        SpatialAudioSystem.Instance = null;
        var groups = SpatialAudioCrossSceneGroup.AllCrossGroups;
        groups.Clear();
        var catalog = new SiegeRoomCatalog();
        var point = Vector3.up;
        check(catalog.Refresh() && catalog.RoomAt(point) == null, "no registry yet");
        check(!catalog.Refresh() && !catalog.HasValidatedRooms, "early miss can be retried");

        SpatialAudioRoom Room(float size) => new()
        {
            RoomSize = size,
            Colliders = [new BoxCollider { size = new(8, 6, 8) }],
        };
        var large = Room(15);
        var first = Room(2);
        var second = Room(2);
        var inactive = Room(1);
        inactive.gameObject.activeInHierarchy = false;
        var outdoor = Room(1);
        outdoor.IsOutdoor = true;
        var invalid = Room(1);
        invalid.IsValid = false;
        var group = new SpatialAudioCrossSceneGroup { AudioRooms = [large, inactive, outdoor, invalid] };
        groups.Add(group);
        check(catalog.Refresh() && catalog.RoomAt(point) == large, "headless group lists, active indoor volumes only");
        group.AudioRooms.Add(second);
        check(catalog.Refresh() && catalog.RoomAt(point) == second, "streamed room changes the selection");
        var storage = new AudioRoomStorage { _validSpatialAudioRooms = [second, large, first] };
        SpatialAudioSystem.Instance = new();
        SpatialAudioSystem.Instance.Publish(storage);
        check(catalog.Refresh() && catalog.HasValidatedRooms, "published native storage invalidates old misses");
        int checksBefore = second.Bounds.Checks;
        check(catalog.RoomAt(point) == first, "smallest overlapping volume, instance ID breaks equal sizes");
        check(second.Bounds.Checks == checksBefore + 1, "overlapping registries check each room only once");
        check(!catalog.Refresh() && catalog.RoomAt(new(100, 0, 0)) == null, "stable registry, outdoor contact");

        groups.Clear(); // EFT clears the shared list when even one additive group is destroyed.
        check(catalog.Refresh() && catalog.RoomAt(point) == first, "remaining native rooms survive group unload");
        var extra = Room(1);
        groups.Add(new() { AudioRooms = [extra] });
        check(catalog.Refresh() && catalog.RoomAt(point) == extra, "new additive group augments native storage");
        groups[0] = new() { AudioRooms = [large] };
        check(catalog.Refresh() && catalog.RoomAt(point) == first, "same-count group replacement drops old volume");
        SpatialAudioSystem.Instance.Publish(new() { _validSpatialAudioRooms = [large, second, inactive] });
        check(catalog.Refresh() && catalog.RoomAt(point) == second, "same-count native list replacement");
        UnityEngine.SceneManagement.SceneManager.sceneCount++;
        check(catalog.Refresh(), "scene change invalidates building maps even with the same lists");
        catalog.Clear();
        groups.Clear();
        SpatialAudioSystem.Instance = null;
        check(catalog.Refresh() && catalog.RoomAt(point) == null, "raid cleanup drops every room reference");
    }

    private static SpatialAudioPortal Connect(
        SpatialAudioRoom a,
        SpatialAudioRoom b,
        string id,
        float x
    )
    {
        var portal = new SpatialAudioPortal
        {
            DoorID = id,
            FrontRoom = a,
            BackRoom = b,
        };
        portal.portalCollider.transform.position = new(x, 1, 0);
        a.roomConnections.Add(new() { connectedRoom = b, connectingPortals = [portal] });
        b.roomConnections.Add(new() { connectedRoom = a, connectingPortals = [portal] });
        return portal;
    }
}
