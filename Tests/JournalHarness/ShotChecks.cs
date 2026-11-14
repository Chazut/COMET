using Comet.Client;
using EFT.InventoryLogic;
using SAIN.Components;
using SAIN.Models.Enums;
using SAIN.SAINComponent.Classes.EnemyClasses;
using Newtonsoft.Json.Linq;
using UnityEngine;

internal static class ShotChecks
{
    internal static void Run()
    {
        int checks = 0;
        void Check(bool ok, string message) { checks++; if (!ok) throw new Exception(message); }
        var p = new Plugin(); Plugin.Instance = p;
        var b = new BotComponent(); b.Player.ProfileId = "shot-observer";
        b.BotOwner.GetPlayer = b.Player; b.BotOwner.WeaponManager.Selector = new() { _owner = b.BotOwner };
        b.Player.HandsController = new EFT.Player.FirearmController();
        SAIN.SAINEnableClass.Bots[b.ProfileId] = b;
        Comet.Api.Decisions.Events.Clear();
        JObject[] Events() => Comet.Api.Decisions.Events.Select(JObject.Parse).ToArray();
        JObject LastShot() => (JObject)Events().Last(x => (string?)x["eventKind"] == "shot")["combatJournal"]!;
        void At(float t) { Time.time = Time.realtimeSinceStartup = t; }

        var target = new SAIN.Components.Enemy { EnemyProfileId = "secondary", IsVisible = true, CanShoot = true, Seen = true };
        target.Vision.EnemyParts.CanBeSeen = true;
        target.Vision.EnemyParts.PartsArray = [new Part { CanBeSeen = true }, new Part { CanShoot = true }];
        target.EnemyPlayerComponent.Flashlight.WhiteLight = true;
        b.Shoot.LastShotEnemy = target;
        p.config!.CombatJournal.Enabled = false; p.Shot(b);
        Check(Events().Length == 0, "disabled combat journal has no shot observations");
        p.config.CombatJournal.Enabled = true;
        foreach (var scope in new[] { "replica", "ghost", "non-pmc", "human", "inactive", "ending", "downed" })
        {
            p.Authority = scope != "replica"; p.Sleep = scope == "ghost"; b.Pmc = scope != "non-pmc";
            b.Player.IsAI = scope != "human"; b.BotActive = scope != "inactive"; b.GameEnding = scope == "ending"; b.Player.Downed = scope == "downed";
            p.Shot(b); Check(Events().Length == 0, scope + " does not report shots");
        }
        b.Player.Downed = false;
        At(100); p.Shot(b);
        var shot = LastShot();
        Check((string?)shot["target"] == "secondary" && (string?)shot["goalTarget"] == "enemy", "actual native target is not silently replaced with GoalEnemy");
        Check((int?)shot["seenParts"] == 1 && (int?)shot["shootParts"] == 1 && (int?)shot["bothParts"] == 0, "visibility and muzzle checks may concern different body parts");
        Check((bool?)shot["targetWhiteLight"] == true && (bool?)shot["visible"] == true, "target light and native visibility recorded separately");
        b.ManualShoot.Reason = EShootReason.Suppress;
        b.ManualShoot.ManualShootEnemy = new SAIN.Components.Enemy { EnemyProfileId = "prefire", IsVisible = false, Seen = true, TimeSinceSeen = 4 };
        At(101); p.Shot(b); shot = LastShot();
        Check((string?)shot["target"] == "prefire" && (string?)shot["targetSource"] == "manual-shot" && (bool?)shot["visible"] == false, "suppression at a hidden known contact is observable, not vetoed");
        Check((string?)shot["manualReason"] == "Suppress" && (double?)shot["secondsSinceSeen"] == 4, "manual purpose and contact age survive output");
        for (int i = 0; i < 99; i++) p.Shot(b);
        Check(Events().Count(x => (string?)x["eventKind"] == "shot") == 2, "actual shot storm produces at most one detail per second");
        p.Stop(b);
        var total = Events().Last(x => (string?)x["eventKind"] == "shot-summary")["combatJournal"]!;
        Check((int?)total["totalShots"] == 101 && (int?)total["hiddenTargetShots"] == 100 && (int?)total["manualShots"] == 100 && (int?)total["sampledOutShots"] == 99, "all shots counted even when details sampled, lifecycle flush retains final counts");
        Check(b.Player.InventoryController.Scans == 0 && b.ManualShoot.Reason == EShootReason.Suppress && target.IsVisible, "observation never scans inventory or alters combat state");
        int before = Events().Length; p.Stop(b);
        Check(Events().Skip(before).All(x => (string?)x["eventKind"] != "shot-summary"), "summary not duplicated when lifecycle ends twice");
        b.SAINLayersActive = false; At(102); p.Shot(b); shot = LastShot();
        Check((string?)shot["target"] == "" && (string?)shot["targetSource"] == "unknown-other-layer", "other-layer fire cannot inherit a stale SAIN target");
        b.SAINLayersActive = true; b.ManualShoot.ManualShootEnemy = null; At(103); p.Shot(b);
        Check((string?)LastShot()["target"] == "" && LastShot()["visible"]!.Type == JTokenType.Null, "unknown manual target does not fall back to old normal target or invent visibility");
        b.ManualShoot.Reason = EShootReason.None; target.Vision = null!; At(104); p.Shot(b);
        Check(p.states[b].Journal.Shots.Fault && !p.states[b].Journal.Fault && p.config.Enabled, "failed native shot read disables only shot observer");
        p.world = null; before = Events().Length; p.Shot(b);
        Check(Events().Length == before, "closed raid ignores late shots");
        Console.WriteLine($"T01 actual-shot production observer: {checks} checks passed.");
    }
}
