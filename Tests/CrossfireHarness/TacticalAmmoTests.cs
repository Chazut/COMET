using Comet.Client;
using EFT;
using EFT.InventoryLogic;

internal static class TacticalAmmoTests
{
    public static void Run()
    {
        var owner = new BotOwner();
        var manager = owner.WeaponManager;
        var gun = manager.CurrentWeapon;
        void Expect(double expected, string scenario)
        {
            double actual = Plugin.TacticalAmmoPercent(owner);
            if (actual != expected)
                throw new Exception($"Tactical ammo ({scenario}): expected {expected}, got {actual}");
        }

        gun.Mag.Count = 14;
        Expect(1400d / 30, "empty chamber");
        gun.ChamberAmmoCount = 1;
        Expect(50, "half full including the chamber");
        gun.Mag.Count = 30;
        Expect(3100d / 30, "full magazine plus chamber");
        gun.Mag.Count = 0;
        Expect(100d / 30, "last round is in the chamber");
        gun.Mag.MaxCount = 100;
        gun.Mag.Count = 52;
        Expect(53, "exact configurable percentage including the chamber");
        gun.Mag.MaxCount = 0;
        Expect(0, "zero magazine capacity");
        gun.Mag = null!;
        Expect(0, "magazine removed during quick reload");
        gun.ReloadMode = Weapon.EReloadMode.OnlyBarrel;
        gun.ChamberCapacity = 2;
        Expect(50, "one of two chambers loaded, no magazine");
        gun.ChamberCapacity = 0;
        Expect(0, "zero chamber capacity");
        manager.ShootController!.Item = null;
        Expect(0, "no weapon in hands");
        manager.ShootController = null;
        Expect(0, "no firearm controller");
        Console.WriteLine("Tactical ammo: 11 checks passed.");
    }
}
