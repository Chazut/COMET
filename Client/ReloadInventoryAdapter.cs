using System;
using EFT;
using EFT.InventoryLogic;
using HarmonyLib;
using UnityEngine;

namespace Comet.Client;

public sealed partial class Plugin
{
    private static void PatchReloadInventory(Harmony h)
    {
        h.Patch(AccessTools.Method(typeof(BotReload), nameof(BotReload.GetMagazineForReload)),
            postfix: new HarmonyMethod(typeof(Plugin), nameof(ChooseBeltMagazine)));
        h.Patch(AccessTools.Method(typeof(BotReload), nameof(BotReload.AddAmmoToMagazines)),
            postfix: new HarmonyMethod(typeof(Plugin), nameof(RefillBeltMagazines)));
        h.Patch(AccessTools.PropertyGetter(typeof(BotWeaponManager), nameof(BotWeaponManager.HaveBullets)),
            postfix: new HarmonyMethod(typeof(Plugin), nameof(CompatibleBullets)));
    }

    private static void CompatibleBullets(BotWeaponManager __instance, ref bool __result)
    {
        try
        {
            var p = Instance;
            if (!__result || p?.config?.Enabled != true || !p.Authority) return;
            var weapon = __instance.CurrentWeapon;
            if (weapon == null || weapon.GetCurrentMagazine() is not Magazine mag
                || mag.IsAmmoCompatible(weapon.Chambers)) return;
            if (!p.WeaponReturnState(__instance.Selector, out _, out _)) return;
            // Preserve the native threshold, chambered shot and no-ammo fallback.
            __result = ReloadLoaded(weapon) > __instance.CriticalBulletCount;
        }
        catch { /* Native result remains authoritative during teardown. */ }
    }

    private static void ChooseBeltMagazine(BotReload __instance, Weapon weapon, ref Magazine __result)
    {
        var p = Instance; var s = p?.JournalState(__instance._player);
        if (s == null || s.ReloadTiming.InventoryFault) return;
        var r = s.ReloadTiming;
        try
        {
            bool wrongAmmo = __result != null && !__result.IsAmmoCompatible(weapon.Chambers);
            if (wrongAmmo)
            {
                var rejected = __result;
                __result = null!;
                try { p!.JournalWrite(s, "W01", "rejected", "magazine-ammo-incompatible", weapon: new {
                    weapon = weapon.Id.ToString(), weaponCaliber = weapon.AmmoCaliber,
                    magazine = rejected.Id.ToString(), rounds = rejected.Count,
                    nextAmmo = rejected.Cartridges.Last?.TemplateId.ToString(),
                    nextCaliber = (rejected.Cartridges.Last as Ammo)?.Caliber
                }, limited: true); } catch (Exception ex) { p!.JournalError(s, ex); }
                if (!ReloadInventory.ReadMagazines(__instance._player.InventoryController, r.BeltMagazines)) return;
            }
            else if (!ReloadInventory.AppendBeltMagazines(__instance._player.InventoryController, r.BeltMagazines)) return;
            var slot = weapon.GetMagazineSlot(); if (slot == null) return;
            var best = __result;
            foreach (var m in r.BeltMagazines)
                if (!ReferenceEquals(m, weapon.GetCurrentMagazine()) && m.Count > 0 && m.IsAmmoCompatible(weapon.Chambers) && (best == null || m.Count > best.Count)
                    && ItemManipulator.CheckMoveIgnoringTargetItem(m, slot, __instance._player.InventoryController).Succeeded) best = m;
            // Keep the native reload transaction, animation, restrictions and callbacks.
            if (best != null) __result = best;
        }
        catch (Exception ex) { p!.ReloadInventoryError(s, ex); }
        finally { r.BeltMagazines.Clear(); }
    }

    private static void RefillBeltMagazines(BotReload __instance)
    {
        var p = Instance; var s = p?.JournalState(__instance._player);
        if (s == null || s.ReloadTiming.InventoryFault) return;
        var r = s.ReloadTiming;
        if (Time.time < r.BeltRefillAt) return;
        r.BeltRefillAt = Time.time + 2f;
        try
        {
            var weapon = __instance.ShootController?.Item;
            if (weapon?.ReloadMode != Weapon.EReloadMode.ExternalMagazine) return;
            var slot = weapon.GetMagazineSlot(); if (slot == null) return;
            // Exclude items already handled by the native call (including other
            // mods' expanded access lists), so no refill transaction is duplicated.
            __instance._player.InventoryController.GetReachableItemsOfTypeNonAlloc(r.BeltMagazines);
            int native = r.BeltMagazines.Count;
            if (!ReloadInventory.AppendBeltMagazines(__instance._player.InventoryController, r.BeltMagazines)) return;
            for (int i = native; i < r.BeltMagazines.Count; i++)
            {
                var mag = r.BeltMagazines[i];
                if (!ReferenceEquals(mag, weapon.GetCurrentMagazine()) && mag.Count < mag.MaxCount && slot.CanAccept(mag) && mag.IsAmmoCompatible(weapon.Chambers))
                    __instance.AddAmmoToMag(weapon, mag);
            }
        }
        catch (Exception ex) { p!.ReloadInventoryError(s, ex); }
        finally { r.BeltMagazines.Clear(); }
    }

    private void ReloadInventoryError(BotState s, Exception ex)
    {
        if (s.ReloadTiming.InventoryFault) return;
        s.ReloadTiming.InventoryFault = true;
        Logger.LogWarning("COMET belt reload compatibility stopped for " + s.Name + ": " + ex.GetBaseException().Message);
    }
}
