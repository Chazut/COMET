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
            if (!ok) throw new Exception("Siege: " + message);
        }

        var complex = SiegeCoverage.Assign(new bool[,] { { true, true }, { true, false } });
        Check(complex != null && complex[0] == 1 && complex[1] == 0, "can reassign an earlier flexible watcher");
        Check(SiegeCoverage.Assign(new bool[,] { { true, false }, { true, false } }) == null, "one watcher cannot cover two exits");
        Check(SiegeCoverage.Assign(new bool[,] { { true, true } }) == null, "single access remains ordinary S02 territory");
        var settings = new SecondaryEntranceConfig();
        Check(settings.Siege && SiegeCoverage.WatchTime(settings, "Rat") > SiegeCoverage.WatchTime(settings, "Chad"),
            "on by default, cautious leader waits longer");

        foreach (string mode in new[] {"full", "duplicate-door", "missing-link", "one-way-link", "unknown-window", "same-view",
            "solo", "heard-only", "shared", "remote", "disabled", "indoor", "ghost", "replica", "moved",
            "occluded", "self-care", "expired", "left-building", "death", "signal-lost", "target-changed",
            "newer-outside", "third-free", "split-targets", "ownership", "contact-visible", "raid-end", "two-rooms", "static-exit"})
        {
            Time.time = 100;
            var p = new Plugin();
            var inside = new SpatialAudioRoom { Colliders = [new BoxCollider { size = new(8, 6, 8) }] };
            var outside = new SpatialAudioRoom { IsOutdoor = true, Colliders = [new BoxCollider { size = new(100,15,100) }] };
            UnityEngine.Object.Rooms = [inside, outside];
            var inner = inside;
            if (mode == "two-rooms")
            {
                inner = new() { Colliders = [new() { size = new(4, 6, 8), transform = new() { position = new(6,0,0) } }] };
                Connect(inside, inner, "hall", 2);
                UnityEngine.Object.Rooms = [inside, inner, outside];
            }
            Connect(inside, outside, "front", -3);
            var second = Connect(inner, outside, mode == "duplicate-door" ? "front" : "back", 3);
            p.world!.Doors["front"] = new Door { Id = "front" };
            p.world.Doors["back"] = new Door { Id = "back" };
            if (mode == "missing-link") second.BackRoom = null!;
            if (mode == "one-way-link") outside.roomConnections.Clear();
            if (mode is "unknown-window" or "static-exit")
            {
                second.portalType = mode == "unknown-window" ? BaseSpatialAudioPortal.PortalType.Window : BaseSpatialAudioPortal.PortalType.Static;
                second.portalCollider.transform.position.z = mode == "unknown-window" ? 15 : 4;
                // Model non-overlapping inside/outside volumes at the static threshold.
                outside.Colliders[0].size = new(100, 15, 10);
                outside.Colliders[0].transform.position.z = 9;
            }
            var a = new BotComponent();
            var b = new BotComponent();
            var c = new BotComponent();
            var bots = mode == "solo" ? new[] { a } : mode is "third-free" or "split-targets" ? new[] { a, b, c } : new[] { a, b };
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
                enemy.EnemyPlayer.IsAI = mode != "remote";
                enemy.KnownPlaces.LastKnownPosition = new(0,0,0);
                enemy.EnemyPosition = new(80,0,30); // Hidden true position must have no effect.
                bot.EnemyController.Enemies[enemy.EnemyProfileId] = enemy;
                bot.EnemyController.KnownEnemies.Add(enemy);
                p.states[bot] = new BotState(bot, p.config!, p.session);
            }
            if (mode == "shared") a.GoalEnemy!.KnownPlaces.LastSquadSeenPlace = new(new(), 99);
            else if (mode != "heard-only") a.GoalEnemy!.KnownPlaces.LastSeenPlace = new(new(), 99);
            if (mode == "newer-outside") b.GoalEnemy!.KnownPlaces.LastSeenPlace = new(new(30,0,0), 100);
            p.config!.SecondaryEntrance.Siege = mode != "disabled";
            if (mode == "indoor") b.Player.Environment = EFT.EnvironmentType.Indoor;
            if (mode == "ghost") p.Ghosts.Add(b.ProfileId);
            if (mode == "replica") p.Authority = false;
            Physics.Cast = (eye, exit) => mode == "same-view" ? exit.x > 0 : Math.Sign(eye.x) != Math.Sign(exit.x);
            if (mode == "split-targets")
            {
                c.GoalEnemy = new Enemy { EnemyProfileId = "other" };
                Check(!p.TestSiege(c), "teammate on another target leaves the other contact eligible this tick");
            }

            bool expected = mode is not ("solo" or "duplicate-door" or "missing-link" or "one-way-link" or "unknown-window"
                or "heard-only" or "same-view" or "disabled" or "indoor" or "ghost" or "replica" or "newer-outside");
            Check(p.TestSiege(a) == expected, mode + " selection");
            if (!expected) { Check(p.states[a].Siege == null, mode + " no partial plan"); continue; }
            Check(p.TestSiege(b) && p.states[a].SiegeExit!.Id != p.states[b].SiegeExit!.Id, mode + " distinct assignments");
            if (mode == "full")
            {
                a.Decision.CurrentCombatDecision = ECombatDecision.Freeze;
                a.CurrentAction = new() { Name = "FreezeAction" };
                Check(!p.TestSiegeGaze(a, out _), "steering waits for native acceptance");
                p.states[a].ActiveLabel = "COMET:Siege - watch exit";
                Check(p.TestSiegeGaze(a, out var gaze) && Vector3.Dot(gaze,
                    (p.states[a].SiegeExit!.Center - a.Transform.EyePosition).normalized) > .99f, "aim at assigned scene marker");
                a.GoalEnemy!.IsVisible = true;
                Check(!p.TestSiegeGaze(a, out _), "native steering immediately owns a visible target");
            }
            if (mode == "third-free")
                Check(!p.TestSiege(c) && p.states[c].Siege == null, "surplus member is free");
            if (mode == "raid-end")
            {
                p.ClearSiegeScene();
                Check(p.states[a].Siege == null && p.states[b].Siege == null, "raid cleanup releases all watchers");
                continue;
            }
            Time.time++;
            switch (mode)
            {
                case "moved": b.Position.x += 2; break;
                case "occluded": Physics.Cast = (_, _) => true; break;
                case "self-care": b.Decision.CurrentSelfDecision = ESelfActionType.Surgery; break;
                case "expired": Time.time = p.states[a].Siege!.Until + .1f; break;
                case "left-building": b.GoalEnemy!.KnownPlaces.LastSeenPlace = new(new(15,0,0), Time.time); break;
                case "death": b.IsDead = true; break;
                case "signal-lost": a.GoalEnemy!.KnownPlaces.LastSeenPlace = null; break;
                case "target-changed": b.GoalEnemy = null; break;
                case "ownership": p.Authority = false; break;
                case "contact-visible": b.GoalEnemy!.IsVisible = true; break;
                default: continue;
            }
            p.TickSiege(a);
            Check(p.states[a].Siege == null && p.states[b].Siege == null, mode + " releases both");
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
        UnityEngine.Object.Rooms = [];
        Console.WriteLine($"S11 siege: {checks} adapter checks passed.");
    }

    private static SpatialAudioPortal Connect(SpatialAudioRoom a, SpatialAudioRoom b, string id, float x)
    {
        var portal = new SpatialAudioPortal { DoorID = id, FrontRoom = a, BackRoom = b };
        portal.portalCollider.transform.position = new(x,1,0);
        a.roomConnections.Add(new() { connectedRoom = b, connectingPortals = [portal] });
        b.roomConnections.Add(new() { connectedRoom = a, connectingPortals = [portal] });
        return portal;
    }
}
