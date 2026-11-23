using System;
using System.Collections.Generic;
using EFT;
using EFT.InventoryLogic;

namespace Comet.Client;

// A worn belt can occupy ArmBand (Pack 'n' Strap). Do not extend the game's
// global slots: human inventories and other bots keep their own access rules.
internal static class ReloadInventory
{
    internal const int Limit = 128;

    internal static bool ReadMagazines(InventoryController controller, IList<Magazine> items)
    {
        controller.GetReachableItemsOfTypeNonAlloc(items);
        return AppendBeltMagazines(controller, items);
    }

    internal static ContainerCollection? WornBelt(InventoryController controller) =>
        controller.Inventory.Equipment.GetSlot(EquipmentSlot.ArmBand)?.ContainedItem as ContainerCollection;

    internal static bool AppendBeltMagazines(InventoryController controller, IList<Magazine> items)
    {
        if (items.Count > Limit)
            return false;
        var belt = WornBelt(controller);
        if (belt == null)
            return true;
        int visited = 0;
        // Direct contents only, just like the native pocket/vest query. No nested
        // container, backpack, secure container or loose weapon is made reachable.
        foreach (var container in belt.Containers)
        foreach (var item in container.Items)
        {
            if (++visited > Limit)
                return false;
            if (item is not Magazine magazine || items.Contains(magazine))
                continue;
            if (items.Count >= Limit)
                return false;
            items.Add(magazine);
        }
        return true;
    }

    internal static string Location(Item item, InventoryEquipment equipment)
    {
        var root = LoadoutAdapter.RootSlot(item, equipment);
        return root?.ToString() ?? "unresolved";
    }
}

// Only the three weapon slots in one synchronous evaluation share these native
// lists. Each gun still checks its own chamber, slot and cartridge compatibility.
internal sealed class ReturnInventoryRead : IDisposable
{
    private readonly List<Magazine> magazines = new();
    private readonly List<Ammo> ammo = new();
    private bool reading, readMagazines, readAmmo;

    internal ReturnInventoryRead Begin()
    {
        // A Harmony hook can re-enter weapon selection while EFT is enumerating.
        var scope = reading ? new ReturnInventoryRead() : this;
        scope.reading = true;
        return scope;
    }

    internal List<Magazine> Magazines(InventoryController inventory)
    {
        if (!readMagazines)
        {
            inventory.GetReachableItemsOfTypeNonAlloc(magazines);
            readMagazines = true;
        }
        return magazines;
    }

    internal List<Ammo> AmmoStacks(InventoryController inventory)
    {
        if (!readAmmo)
        {
            inventory.GetAcceptableItemsNonAlloc(BotReload._availableEquipmentSlots, ammo);
            readAmmo = true;
        }
        return ammo;
    }

    public void Dispose()
    {
        magazines.Clear();
        ammo.Clear();
        reading = readMagazines = readAmmo = false;
    }
}
