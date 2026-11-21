using EFT;
using EFT.InventoryLogic;

namespace Comet.Client;

public sealed partial class Plugin
{
    internal static double TacticalAmmoPercent(BotOwner owner)
    {
        var manager = owner?.WeaponManager;
        var reload = manager?.Reload;
        var weapon = manager?.ShootController?.Item;
        if (reload == null || weapon == null)
            return 0;

        // EFT's MaxBulletCount dereferences the magazine, including while a quick reload removes it.
        if (weapon.ReloadMode != Weapon.EReloadMode.OnlyBarrel && weapon.GetCurrentMagazine() == null)
            return 0;

        int capacity = reload.MaxBulletCount;
        return capacity > 0 ? 100d * reload.BulletCount / capacity : 0;
    }
}
