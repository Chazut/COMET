global using SAIN.Helpers;
using Comet.Core;
using EFT;
using EFT.InventoryLogic;
using SAIN.Components;
using UnityEngine;

namespace Comet.Client;

public sealed partial class Plugin
{
    internal static int TestBeltReload()
    {
        int n = 0;
        void Check(bool ok, string why)
        {
            n++;
            if (!ok)
                throw new Exception("Belt reload: " + why);
        }
        foreach (double reloadWeight in new[] { 0d, 1d, 2d })
        foreach (double hold in new[] { 0d, 1d, 2d })
        foreach (double pistol in new[] { 0d, 1d, 2d })
        {
            var expected = new[] { ("Reload", reloadWeight), ("Hold", hold), ("Pistol", pistol) }
                .OrderByDescending(x => x.Item2).Select(x => x.Item1);
            Check(RankReloadOptions(reloadWeight, hold, pistol).Select(c => c.Decision)
                .SequenceEqual(expected), "stable source order including ties");
        }
        var p = Instance = new Plugin();
        var b = new BotComponent();
        b.Player.ProfileId = "belt-bot";
        b.BotOwner.GetPlayer = b.Player;
        SAIN.SAINEnableClass.Bots[b.ProfileId] = b;
        var w = new Weapon
        {
            Magazine = new Magazine { Count = 0 },
            ChamberAmmoCount = 0,
        };
        var reload = new BotReload
        {
            _player = b.Player,
            ShootController = new() { Item = w },
        };
        var belt = new ContainerCollection { Containers = new() { new Container() } };
        b.Player.Inventory.Equipment.Belt.Item = belt;
        var mags = Enumerable
            .Range(0, 3)
            .Select(i => new Magazine
            {
                Id = "gen1-" + i,
                Root = EquipmentSlot.ArmBand,
                Count = 20,
            })
            .ToArray();
        belt.Containers[0].Items.AddRange(mags);
        var list = new List<Magazine>();
        b.Player.InventoryController.GetReachableItemsOfTypeNonAlloc(list);
        Check(list.Count == 0, "reproduce XHB5: base slots cannot see any of three belt magazines");
        Check(
            ReloadInventory.ReadMagazines(b.Player.InventoryController, list) && list.Count == 3,
            "production reader discovers direct belt contents"
        );
        var s = p.JournalState(b.Player)!;
        var f = p.ReloadResources(
            b,
            s,
            w,
            new()
            {
                Now = 1,
                Weapon = "m4",
                Capacity = 21,
            },
            true
        );
        Check(
            f.Reserve == 20
                && f.Resource == "magazine"
                && f.ResourceLocation == "ArmBand"
                && f.BeltMagazines == 3,
            "W01 finds a loaded magazine, not just spare loose ammo"
        );
        Check(
            f.ResourcesChecked && f.MagazineCandidates == 3 && f.RejectedMagazines == 0,
            "diagnostic exposes candidate origin and count"
        );
        var policy = new ReloadTimingPolicy();
        policy.Evaluate(f, new());
        Check(
            policy.Trace!.ResourceLocation == "ArmBand" && policy.Trace.BeltMagazines == 3,
            "diagnostic survives serialization model"
        );
        var trace = Newtonsoft.Json.Linq.JObject.Parse(Diagnostics.Serialize(policy.Trace));
        Check(
            (string?)trace["resourceLocation"] == "ArmBand"
                && (int?)trace["magazineCandidates"] == 3,
            "new evidence is present in serialized logs"
        );
        s.ReloadTiming.Magazines.Clear();
        s.ReloadTiming.Ammo.Clear();
        Magazine chosen = null!;
        ChooseBeltMagazine(reload, w, ref chosen);
        Check(chosen == mags[0], "native reload receives the real loaded belt magazine");
        Check(s.ReloadTiming.BeltMagazines.Count == 0, "native scratch inventory cleared");
        var better = new Magazine { Count = 30 };
        chosen = better;
        ChooseBeltMagazine(reload, w, ref chosen);
        Check(chosen == better, "keep better native candidate");
        mags[0].Movable = false;
        mags[1].Count = 0;
        chosen = null!;
        ChooseBeltMagazine(reload, w, ref chosen);
        Check(chosen == mags[2], "locked and empty magazines never selected");
        w.MagSlot.Accept = false;
        chosen = null!;
        ChooseBeltMagazine(reload, w, ref chosen);
        Check(chosen == null, "incompatible magazines remain refused");
        f = p.ReloadResources(b, s, w, new() { Capacity = 21 }, true);
        Check(
            f.RejectedMagazines == 2
                && f.NonImprovingMagazines == 1
                && f.ResourceRejection.Length > 0,
            "exact refusal class logged"
        );
        s.ReloadTiming.Magazines.Clear();
        w.MagSlot.Accept = true;
        mags[0].Movable = true;
        foreach (var m in mags)
            m.Count = 0;
        Time.time = 10;
        RefillBeltMagazines(reload);
        Check(
            reload.Refilled == 3 && mags.All(m => m.Count == 20),
            "empty belt magazines use existing native refill API"
        );
        RefillBeltMagazines(reload);
        Check(reload.Refilled == 3, "no repeated refill in same interval");
        foreach (var m in mags)
            m.Count = 0;
        b.Player.InventoryController.Reachable.AddRange(mags);
        Time.time = 13;
        RefillBeltMagazines(reload);
        Check(
            reload.Refilled == 3,
            "no duplicate refill when another mod already makes belt reachable"
        );
        list.Clear();
        Check(
            ReloadInventory.ReadMagazines(b.Player.InventoryController, list) && list.Count == 3,
            "no duplicate candidates with expanded native slots"
        );
        b.Player.InventoryController.Reachable.Clear();
        var nested = new ContainerCollection
        {
            Containers = new() { new Container { Items = new() { new Magazine { Count = 100 } } } },
        };
        belt.Containers[0].Items.Add(nested);
        list.Clear();
        ReloadInventory.ReadMagazines(b.Player.InventoryController, list);
        Check(list.Count == 3, "no traversal of nested bags or cases");
        foreach (
            var mode in new[]
            {
                "human",
                "non-pmc",
                "replica",
                "ghost",
                "downed",
                "disabled",
                "dead",
            }
        )
        {
            b.Player.IsAI = mode != "human";
            b.Pmc = mode != "non-pmc";
            p.Authority = mode != "replica";
            p.Sleep = mode == "ghost";
            b.Player.Downed = mode == "downed";
            p.config!.Enabled = mode != "disabled";
            b.IsDead = mode == "dead";
            mags[0].Count = 20;
            chosen = null!;
            ChooseBeltMagazine(reload, w, ref chosen);
            Check(chosen == null, "native result unchanged outside scope: " + mode);
            Time.time += 3;
            int old = reload.Refilled;
            RefillBeltMagazines(reload);
            Check(old == reload.Refilled, "no inventory writes outside scope: " + mode);
        }
        b.Player.IsAI = b.Pmc = p.Authority = p.config!.Enabled = true;
        p.Sleep = b.Player.Downed = b.IsDead = false;
        for (int i = 0; i < 129; i++)
            belt.Containers[0].Items.Add(new Magazine());
        list.Clear();
        Check(
            !ReloadInventory.ReadMagazines(b.Player.InventoryController, list) && list.Count <= 128,
            "bounded modded inventory scan reports truncation"
        );
        chosen = null!;
        ChooseBeltMagazine(reload, w, ref chosen);
        Check(
            chosen == null && s.ReloadTiming.BeltMagazines.Count == 0,
            "partial scan cannot authorize native reload"
        );
        belt.Containers[0].Items.RemoveRange(3, belt.Containers[0].Items.Count - 3);
        // Raid457: a 10-round .300 PMAG physically fits Woods' M4, but cannot feed it.
        var wrong = new Magazine
        {
            Count = 10,
            AmmoCompatible = false,
            Cartridges = new() { Last = new Ammo { Caliber = "Caliber762x35" } },
        };
        var correct = new Magazine { Count = 8 };
        b.Player.InventoryController.Reachable.AddRange(new[] { wrong, correct });
        foreach (var m in mags)
            m.Count = 0;
        chosen = wrong;
        ChooseBeltMagazine(reload, w, ref chosen);
        Check(
            chosen == correct,
            "wrong native PMAG replaced with a reachable, chamber-compatible magazine"
        );
        Check(
            Comet.Api.Decisions.Events.Any(x =>
                x.Contains("magazine-ammo-incompatible") && x.Contains("Caliber762x35")
            ),
            "caliber mismatch reaches the diagnostic journal"
        );
        Diagnostics.Fail = true;
        chosen = wrong;
        ChooseBeltMagazine(reload, w, ref chosen);
        Diagnostics.Fail = false;
        Check(
            chosen == correct && s.Journal.Fault && !s.ReloadTiming.InventoryFault,
            "observer failure cannot restore bad ammo or disable reload selection"
        );
        b.Player.InventoryController.Reachable.Remove(correct);
        chosen = wrong;
        ChooseBeltMagazine(reload, w, ref chosen);
        Check(chosen == null, "no compatible spare does not return the wrong native magazine");
        w.Magazine = wrong;
        w.ChamberAmmoCount = 1;
        Check(
            ReloadLoaded(w) == 1,
            "a valid chambered round remains usable with a wrong-caliber magazine"
        );
        w.ChamberAmmoCount = 0;
        Check(ReloadLoaded(w) == 0, "ten wrong rounds cannot keep an empty weapon usable");
        wrong.AmmoCompatible = true;
        Check(ReloadLoaded(w) == 10, "mixed magazine with compatible next round remains usable");
        wrong.AmmoCompatible = false;
        var wm = new BotWeaponManager
        {
            CurrentWeapon = w,
            Selector = new() { _owner = b.BotOwner },
        };
        bool bullets = true;
        CompatibleBullets(wm, ref bullets);
        Check(!bullets, "native no-ammo path sees incompatible magazine as empty");
        w.ChamberAmmoCount = 1;
        bullets = true;
        CompatibleBullets(wm, ref bullets);
        Check(bullets, "native threshold preserves last chambered shot");
        wm.CriticalBulletCount = 1;
        bullets = true;
        CompatibleBullets(wm, ref bullets);
        Check(!bullets, "native critical bullet threshold is preserved");
        wm.CriticalBulletCount = 0;
        w.ChamberAmmoCount = 0;
        p.Authority = false;
        bullets = true;
        CompatibleBullets(wm, ref bullets);
        Check(bullets, "Fika replica never changes native ammo result");
        p.Authority = true;
        p.Guns[(b, EquipmentSlot.FirstPrimaryWeapon)] = w;
        var backup = new Weapon
        {
            Magazine = new Magazine { Count = 50 },
            ChamberAmmoCount = 1,
        };
        p.Guns[(b, EquipmentSlot.SecondPrimaryWeapon)] = backup;
        var scratch = new ReturnInventoryRead();
        var inventory = scratch.Begin();
        var main = p.ReadReturnGun(b, inventory, EquipmentSlot.FirstPrimaryWeapon);
        var other = p.ReadReturnGun(b, inventory, EquipmentSlot.SecondPrimaryWeapon);
        Check(
            main.Loaded == 0 && main.Reserve == 0 && other.Loaded == 51,
            "Woods fallback frame sees unusable M4 and loaded ISO"
        );
        int scans = b.Player.InventoryController.Scans;
        backup.ChamberAmmoCount = 0;
        backup.Magazine.Count = 0;
        p.ReadReturnGun(b, inventory, EquipmentSlot.SecondPrimaryWeapon);
        Check(b.Player.InventoryController.Scans == scans, "two empty guns share the reachable slot scan");
        using (var reentrant = scratch.Begin())
        {
            Check(!ReferenceEquals(inventory, reentrant), "reentrant selection has isolated storage");
            p.ReadReturnGun(b, reentrant, EquipmentSlot.FirstPrimaryWeapon);
        }
        inventory.Dispose();
        correct.Count = 19;
        b.Player.InventoryController.Reachable.Add(correct);
        using (var next = scratch.Begin())
        {
            var updated = p.ReadReturnGun(b, next, EquipmentSlot.FirstPrimaryWeapon);
            Check(updated.Reserve == 19, "next evaluation immediately sees changed inventory");
        }
        var returnPolicy = new WeaponReturnPolicy();
        Check(
            returnPolicy.Fallback(
                new()
                {
                    Current = main,
                    Main = main,
                    Other = other,
                }
            ) == other.Id,
            "existing fallback selects loaded secondary without changing ranking"
        );
        w.Magazine = new Magazine { Count = 0 };
        b.Player.InventoryController.Reachable.Clear();
        reload.FailFill = true;
        Time.time += 3;
        mags[0].Count = 0;
        RefillBeltMagazines(reload);
        Check(
            s.ReloadTiming.InventoryFault && s.ReloadTiming.BeltMagazines.Count == 0,
            "transaction failure safely disables compatibility for this bot"
        );
        s.ReloadTiming.InventoryFault = false;
        b.Player.Inventory.Equipment.Belt.Item = null;
        Time.time += 3;
        scans = b.Player.InventoryController.Scans;
        RefillBeltMagazines(reload);
        Check(b.Player.InventoryController.Scans == scans, "beltless refill avoids native inventory scan");
        Console.WriteLine(
            $"W01 belt resource production adapters: {n} checks passed (platform doubles)."
        );
        return n;
    }
}
