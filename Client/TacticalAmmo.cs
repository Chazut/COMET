using EFT;
using EFT.InventoryLogic;

namespace Comet.Client;

public sealed partial class Plugin
{
    internal static double TacticalAmmoPercent(BotOwner owner)
    {
        var ammo = TacticalAmmoCounts(owner);
        return ammo.Capacity > 0 ? 100d * ammo.Rounds / ammo.Capacity : 0;
    }

    private static (int Rounds, int Capacity) TacticalAmmoCounts(BotOwner owner)
    {
        var manager = owner?.WeaponManager;
        var reload = manager?.Reload;
        var weapon = manager?.ShootController?.Item;
        if (reload == null || weapon == null)
            return default;

        // EFT's MaxBulletCount dereferences the magazine, including while a quick reload removes it.
        int rounds = reload.BulletCount;
        if (weapon.ReloadMode != Weapon.EReloadMode.OnlyBarrel && weapon.GetCurrentMagazine() == null)
            return (rounds, 0);

        return (rounds, reload.MaxBulletCount);
    }
}
