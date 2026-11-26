global using BotOwner = EFT.Owner;
using EFT;
using Comet.Client;
using EFT.InventoryLogic;

internal static class ReloadGainCases
{
    internal static void Run(Action<bool, string> check)
    {
        var inventory = new InventoryController();
        var gun = new Weapon { Magazine = new Magazine { Count = 22, MaxCount = 30 } };
        var spare = new Magazine { Count = 10, MaxCount = 30 };
        List<Ammo> rounds = null!;
        var spareBuffer = new List<Magazine>();
        var ammoBuffer = new List<Ammo>();
        Magazine candidate = null!;
        bool Hold()
        {
            candidate = spare;
            rounds = null!;
            return ReloadMagazineGain.KeepCurrent(gun, inventory, ref candidate, ref rounds, spareBuffer, ammoBuffer, out _, out _);
        }
        check(Hold() && candidate == null, "Nezys keeps the 22-round mag instead of dropping it for 10");
        gun.Magazine.Count = 13;
        check(Hold(), "ESP-Detection keeps 13 instead of swapping for 10");
        gun.Magazine.Count = 10;
        for (int i = 0; i < 10; i++) check(Hold(), "Repeated equal proposals never begin an animation");
        gun.Magazine.Count = 9;
        check(!Hold() && candidate == spare, "Even one round gained after firing permits an immediate reload");
        gun.Magazine.Count = 10;
        spare.Count = 30;
        check(!Hold(), "A newly looted or refilled spare works without firing");
        spare.Count = 1;
        gun.Magazine.Count = 0;
        check(!Hold(), "One-round emergency reload keeps working");
        gun.Magazine.Count = 22;
        gun.Magazine.AmmoCompatible = false;
        check(!Hold(), "One compatible round replaces a nonfeeding 22-round magazine");
        gun.Magazine.AmmoCompatible = true;
        gun.Chambers[0].RequiredCaliber = "Caliber556x45NATO";
        gun.Magazine.Cartridges._items.Add(new Ammo { Caliber = "wrong", StackObjectsCount = 21 });
        gun.Magazine.Cartridges._items.Add(new Ammo { StackObjectsCount = 1 });
        spare.Count = 2;
        check(!Hold(), "Two compatible rounds beat one feedable round atop 21 incompatible rounds");
        gun.Magazine.Cartridges._items.Clear();
        spare.Count = 30;
        spare.Cartridges._items.Add(new Ammo { Caliber = "wrong", StackObjectsCount = 29 });
        spare.Cartridges._items.Add(new Ammo { StackObjectsCount = 1 });
        var alternative = new Magazine { Count = 24 };
        inventory.Reachable.Add(alternative);
        check(!Hold() && candidate == alternative && spareBuffer.Count == 0, "A bad 30-round spare cannot hide a working 24-round one");
        inventory.Reachable.Clear();
        spare.Cartridges._items.Clear();
        spare.Count = 2;
        gun.MalfState.State = Weapon.EMalfunctionState.Misfire;
        check(!Hold(), "Misfire retains native recovery");
        gun.MalfState.State = Weapon.EMalfunctionState.None;
        gun.ReloadMode = Weapon.EReloadMode.ExternalMagazineWithInternalReloadSupport;
        inventory.Reachable.Add(new Ammo { Caliber = "wrong" });
        check(Hold(), "Hybrid with no loadable loose ammo holds the current mag");
        var stack = new Ammo();
        inventory.Reachable.Add(stack);
        check(!Hold() && candidate == null && rounds.SequenceEqual(new[] { stack }) && ammoBuffer.Count == 0,
            "Hybrid keeps its mag and hands EFT an independent loose-round list");
        gun.ReloadMode = Weapon.EReloadMode.InternalMagazine;
        check(!Hold() && candidate == spare, "Internal loading passes through");
        gun.ReloadMode = Weapon.EReloadMode.OnlyBarrel;
        check(!Hold(), "Single-shell loading passes through");
        gun.ReloadMode = Weapon.EReloadMode.ExternalMagazine;
        gun.Magazine = null!;
        check(!Hold(), "A missing mag can be replaced immediately");
    }
}

namespace Comet.Client
{
    internal sealed class SelfActionDecisionClass { }
    internal sealed class BotReloadMagazine : BotReload { }

    public sealed partial class Plugin
    {
        private bool reloadGainFaultLogged;
        [ThreadStatic] private static BotReload? sainReloadProbe;
        private static Func<SelfActionDecisionClass, BotOwner, BotReload, bool> sainTryReload = null!;

        internal static void TestMagazineDecision(Action<bool, string> check)
        {
            var plugin = Instance = new Plugin();
            var bot = new SAIN.Components.BotComponent();
            bot.Player.ProfileId = "reload-loop";
            SAIN.SAINEnableClass.Bots[bot.ProfileId] = bot;
            var gun = new Weapon { Magazine = new Magazine { Count = 22, MaxCount = 30 } };
            var operation = new BotReloadMagazine { _player = bot.Player, ShootController = new() { Item = gun } };
            var spare = new Magazine { Count = 10 };
            bool nativeReady = true;
            int swaps = 0;
            sainTryReload = (_, _, _) =>
            {
                var candidate = spare;
                List<Ammo> ammo = null!;
                bool result = nativeReady;
                CheckMagazineGain(operation, ref candidate, ref ammo, ref result);
                if (result) swaps++;
                return result;
            };
            int Attempt() => ProbeSainReload(new(), bot.BotOwner, operation);
            check(Attempt() == -1 && Attempt() == -1 && swaps == 0, "SAIN holds without starting a reload or requesting its weapon fallback");
            nativeReady = false;
            check(Attempt() == 0, "Genuine SAIN refusal immediately after a hold retains its fallback");
            nativeReady = true;
            spare.Count = 23;
            check(Attempt() == 1 && swaps == 1, "Fuller magazine accepted at the same time and without a shot");
            spare.Count = 10;
            plugin.Authority = false;
            check(Attempt() == 1, "Remote Fika replica keeps its native result");
            plugin.Authority = true;
            plugin.config!.Enabled = false;
            check(Attempt() == 1, "Disabled COMET keeps its native result");
            plugin.config.Enabled = true;
            bot.Pmc = false;
            check(Attempt() == 1, "Ordinary AI retains its native result");
            sainTryReload = (_, _, _) => throw new OperationCanceledException();
            try { Attempt(); throw new Exception("Native exception was swallowed"); }
            catch (OperationCanceledException) { }
            check(sainReloadProbe == null, "No retained bot when the native call throws");
        }
    }
}
