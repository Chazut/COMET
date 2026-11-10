using System;
using System.Collections.Generic;
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

    internal static bool AppendBeltMagazines(InventoryController controller, IList<Magazine> items)
    {
        if (items.Count > Limit) return false;
        var belt = controller.Inventory.Equipment.GetSlot(EquipmentSlot.ArmBand)?.ContainedItem as ContainerCollection;
        if (belt == null) return true;
        int visited = 0;
        // Direct contents only, just like the native pocket/vest query. No nested
        // container, backpack, secure container or loose weapon is made reachable.
        foreach (var container in belt.Containers)
            foreach (var item in container.Items)
            {
                if (++visited > Limit) return false;
                if (item is not Magazine magazine || items.Contains(magazine)) continue;
                if (items.Count >= Limit) return false;
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
