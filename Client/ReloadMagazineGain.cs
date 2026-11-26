using System.Collections.Generic;
using EFT;
using EFT.InventoryLogic;

namespace Comet.Client;

internal static class ReloadMagazineGain
{
    // Called only on an accepted native proposal, after any native spare-magazine refill.
    internal static bool KeepCurrent(Weapon weapon, InventoryController inventory, ref Magazine candidate,
        ref List<Ammo> loose, List<Magazine> spareBuffer, List<Ammo> ammoBuffer, out int loaded, out int replacement)
    {
        loaded = replacement = 0;
        if (candidate == null || loose?.Count > 0 || weapon.MalfState.State != Weapon.EMalfunctionState.None ||
            weapon.ReloadMode is not (Weapon.EReloadMode.ExternalMagazine or Weapon.EReloadMode.ExternalMagazineWithInternalReloadSupport) ||
            weapon.GetCurrentMagazine() is not { } current)
            return false;
        loaded = Feedable(weapon, current);
        if (loaded == 0) return false;
        replacement = Feedable(weapon, candidate);
        if (replacement > loaded) return false;
        // EFT ranks spares by total rounds. A mixed mag can hide a shorter spare that actually feeds longer.
        if (replacement < candidate.Count &&
            BetterMixedAlternative(weapon, inventory, current, loaded, spareBuffer) is { } better)
        {
            candidate = better;
            return false;
        }

        // EFT prefers a detachable mag on a hybrid. Keeping it still permits the
        // same loose-round path EFT uses when no spare mag was found.
        if (weapon.ReloadMode == Weapon.EReloadMode.ExternalMagazineWithInternalReloadSupport &&
            current.Count < current.MaxCount && weapon.Chambers.Length > 0)
        {
            try
            {
                inventory.GetAcceptableItemsNonAlloc(BotReload._availableEquipmentSlots, ammoBuffer,
                    a => a.StackObjectsCount > 0 && weapon.Chambers[0].CanAccept(a) && a.CheckAction(null).Succeeded);
                if (ammoBuffer.Count > 0)
                {
                    loose = new List<Ammo>(ammoBuffer); // EFT owns the list through animation completion.
                    candidate = null!;
                    return false;
                }
            }
            finally { ammoBuffer.Clear(); }
        }
        candidate = null!;
        return true;
    }

    private static Magazine? BetterMixedAlternative(Weapon weapon, InventoryController inventory, Magazine current,
        int loaded, List<Magazine> buffer)
    {
        try
        {
            if (weapon.GetMagazineSlot() is not { } slot || !ReloadInventory.ReadMagazines(inventory, buffer)) return null;
            Magazine? best = null;
            foreach (var mag in buffer)
            {
                if (ReferenceEquals(mag, current) || mag.Count <= loaded) continue;
                int feed = Feedable(weapon, mag);
                if (feed <= loaded || !ItemManipulator.CheckMoveIgnoringTargetItem(mag, slot, inventory).Succeeded) continue;
                best = mag;
                loaded = feed;
            }
            return best;
        }
        finally { buffer.Clear(); }
    }

    private static int Feedable(Weapon weapon, Magazine magazine)
    {
        if (magazine.Count == 0 || !magazine.IsAmmoCompatible(weapon.Chambers)) return 0;
        var stacks = magazine.Cartridges._items;
        if (stacks.Count <= 1 || weapon.Chambers.Length == 0) return magazine.Count;
        // EFT feeds from Last. Count only the consecutive compatible stacks before a bad cartridge.
        int count = stacks[stacks.Count - 1].StackObjectsCount;
        for (int i = stacks.Count - 2; i >= 0; i--)
        {
            bool accepted = false;
            foreach (var chamber in weapon.Chambers)
                if (chamber.CanAccept(stacks[i])) { accepted = true; break; }
            if (!accepted) break;
            count += stacks[i].StackObjectsCount;
        }
        return count;
    }
}
