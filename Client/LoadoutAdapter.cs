using System;
using System.Diagnostics;
using System.Linq;
using Comet.Core;
using EFT;
using EFT.InventoryLogic;
using HarmonyLib;
using SAIN.Components;
using SAIN.SAINComponent.Classes.Info;
using UnityEngine;

namespace Comet.Client;

internal sealed class LoadoutRuntime
{
    internal readonly LoadoutRefresh Refresh = new LoadoutRefresh();
    internal LoadoutTrace? Cached;
}

public sealed partial class Plugin
{
    private int loadoutFrame = -1;
    private static void PatchLoadout(Harmony h)
    {
        var inventory = AccessTools.Method(typeof(Player), nameof(Player.OnItemAddedOrRemoved));
        var modified = AccessTools.Method(typeof(WeaponInfo), nameof(WeaponInfo.WeaponModified), new[] { typeof(Player) });
        if (inventory == null || modified == null) throw new MissingMethodException("A08 equipment notification API");
        h.Patch(inventory, postfix: new HarmonyMethod(typeof(Plugin), nameof(LoadoutInventoryChanged)));
        h.Patch(modified, postfix: new HarmonyMethod(typeof(Plugin), nameof(LoadoutWeaponModified)));
    }
    private static void LoadoutInventoryChanged(Player __instance) => EquipmentNotification(__instance, "inventory-changed");
    private static void LoadoutWeaponModified(Player __0) => EquipmentNotification(__0, "attachments-changed");
    private static void EquipmentNotification(Player player,string reason)
    { Instance?.InvalidateLoadout(player,reason); Instance?.MarkWeaponSync(player,reason); }
    private void InvalidateLoadout(Player player, string reason)
    {
        if (config?.Enabled != true || !config.Loadout.Enabled || !Authority || player == null || !player.IsAI) return;
        foreach (var pair in states)
            if (ReferenceEquals(pair.Key.Player, player)) { pair.Value.Loadout.Refresh.Invalidate(reason); break; }
    }
    private LoadoutTrace? ReadLoadout(BotComponent bot, BotState state)
    {
        if (config?.Enabled != true || !config.Loadout.Enabled || !Authority || !NativeAdapter.IsSupportedAi(bot) || !bot.BotActive || Ghost(bot.ProfileId)) return null;
        var runtime = state.Loadout;
        var weapon = bot.Player.HandsController?.Item as Weapon;
        if (weapon == null) { runtime.Refresh.Invalidate("no-weapon-in-hands"); return new LoadoutTrace { State = "no-weapon", At = Time.time }; }
        string id = weapon.Id.ToString(), ammo = weapon.CurrentAmmoTemplate?._id.ToString() ?? "";
        try
        {
            if (runtime.Refresh.NeedsRefresh(Time.time, id, ammo) && loadoutFrame != Time.frameCount)
            {
                loadoutFrame = Time.frameCount; // One inventory inspection globally per frame, only at decision time.
                var timer = Stopwatch.StartNew();
                runtime.Cached = LoadoutAdapter.Read(bot, weapon, config.Loadout, runtime.Refresh.Reason);
                runtime.Cached.SampleMilliseconds = timer.Elapsed.TotalMilliseconds;
                runtime.Refresh.Complete(Time.time, id, ammo, config.Loadout.RefreshSeconds);
            }
            if (runtime.Cached == null || !runtime.Refresh.IsFresh(Time.time, id, ammo))
                return LoadoutTrace.Pending(runtime.Cached, id, Time.time, runtime.Refresh.Reason);
            var trace = runtime.Cached.Copy();
            LoadoutAdapter.Current(bot, weapon, trace);
            return trace;
        }
        catch (Exception ex)
        {
            runtime.Refresh.Failed(Time.time); runtime.Cached = null;
            if (!state.LoadoutWarned) { state.LoadoutWarned = true; Logger.LogWarning("COMET A08 equipment read failed; neutral influence: " + ex.Message); }
            return new LoadoutTrace { State = "read-error", WeaponId = id, At = Time.time };
        }
    }
}

internal static class LoadoutAdapter
{
    internal static readonly EquipmentSlot[] Carried = { EquipmentSlot.Pockets, EquipmentSlot.TacticalVest, EquipmentSlot.Backpack, EquipmentSlot.SecuredContainer, EquipmentSlot.ArmBand };
    internal static LoadoutTrace Read(BotComponent bot, Weapon weapon, LoadoutConfig c, string reason)
    {
        var info = bot.PlayerComponent?.Equipment?.CurrentWeaponInfo;
        // SAIN can still be processing an equipment event. Never use optics from the previous gun.
        bool infoMatches = info != null && ReferenceEquals(info.Weapon, weapon);
        string ammoKind = weapon.CurrentAmmoTemplate?.ammoType ?? "";
        bool slug = weapon.WeapClass == "shotgun" && ammoKind == "bullet" && weapon.CurrentAmmoTemplate?.buckshotBullets <= 1;
        var p = infoMatches ? LoadoutModel.Classify(weapon.WeapClass, weapon.BoltAction,
            weapon.WeapFireType?.Contains(Weapon.EFireMode.fullauto) == true, weapon.FireRate, info!.HasOptic, info.HasRedDot, slug ? "slug" : "", c)
            : new LoadoutTrace { State = "weapon-info-pending", WeaponClass = weapon.WeapClass };
        p.WeaponId = weapon.Id.ToString(); p.Weapon = weapon.ShortName.Localized(); p.Ammo = slug ? "slug" : ammoKind;
        p.At = p.ResourceAt = Time.time; p.ScoreCap = c.ScoreCap; p.RefreshReason = reason;
        var equipment = bot.Player.Inventory.Equipment;
        var magSlot = weapon.GetMagazineSlot();
        var chamber = weapon.Chambers != null && weapon.Chambers.Length > 0 ? weapon.Chambers[0] : null;
        foreach (var item in bot.Player.Inventory.GetPlayerItems(EPlayerItems.Equipment))
        {
            if (++p.ItemsScanned > 256) { p.ResourceReason = "inventory-scan-limit"; break; }
            var slot = RootSlot(item, equipment);
            if (slot == null) continue;
            bool fast = (slot == EquipmentSlot.TacticalVest || slot == EquipmentSlot.Pockets || slot == EquipmentSlot.ArmBand)
                && ReferenceEquals(item.CurrentAddress?.Container?.ParentItem, equipment.GetSlot(slot.Value)?.ContainedItem);
            if (item is Magazine mag && !ReferenceEquals(mag, weapon.GetCurrentMagazine()) && magSlot != null
                && LoadoutModel.UsableSpareMagazine(fast, magSlot.CanAccept(mag), mag.Count, mag.IsAmmoCompatible(weapon.Chambers))) p.CompatibleMagazines++;
            else if (item is Ammo round && !(item.CurrentAddress?.Container?.ParentItem is Magazine) && !(item.CurrentAddress?.Container?.ParentItem is Weapon)
                && chamber != null && round.StackObjectsCount > 0 && chamber.CanAccept(round)) p.LooseRounds += round.StackObjectsCount;
            else if (item is ThrowWeap) p.Grenades++;
            var med = item.GetItemComponent<MedKitComponent>();
            if (med != null && med.HpResource > 0) p.MedicalItems++;
        }
        p.ResourcesKnown = p.ResourceReason.Length == 0;
        if (!p.ResourcesKnown) { p.CompatibleMagazines = p.LooseRounds = p.Grenades = p.MedicalItems = 0; }
        Current(bot, weapon, p);
        return p;
    }
    internal static void Current(BotComponent bot, Weapon weapon, LoadoutTrace p)
    {
        p.At = Time.time;
        p.Ready = VisibleAdapter.WeaponReady(bot);
        p.LoadedRounds = weapon.ChamberAmmoCount + (weapon.ReloadMode == Weapon.EReloadMode.OnlyBarrel ? 0 : weapon.GetCurrentMagazine()?.Count ?? 0);
        if (bot.BotOwner.WeaponManager.Grenades?.HaveGrenade != true) p.Grenades = 0;
        var med = bot.BotOwner.Medecine;
        var aid = med?.FirstAid;
        var surgery = med?.SurgicalKit;
        // Current native treatment target plus a nonempty, carried item. No Refresh/CanReload/Apply calls here.
        bool enabled = bot.BotOwner.Settings.FileSettings.Mind.CAN_USE_MEDS;
        p.CanFirstAid = enabled && p.ResourcesKnown && aid?.Damaged == true && UsableMed(aid, bot.Player);
        p.CanSurgery = enabled && p.ResourcesKnown && surgery?.HaveWork == true && UsableMed(surgery, bot.Player);
    }
    private static bool UsableMed(BotAbstractMedsToPart treatment, Player player)
    {
        var item = treatment.CurUsingMeds;
        if (item == null || !treatment._bodyPartToHeal.HasValue) return false;
        var resource = item.GetItemComponent<MedKitComponent>()?.HpResource ?? 0;
        if (resource <= 0 || RootSlot(item, player.Inventory.Equipment) == null) return false;
        return LoadoutModel.UsableMedicalSupply(resource, true, true,
            player.HealthController.CanApplyItem(item, treatment._bodyPartToHeal.Value));
    }
    internal static EquipmentSlot? RootSlot(Item item, InventoryEquipment equipment)
    {
        Item current = item;
        for (int depth = 0; depth < 16; depth++)
        {
            foreach (var slot in Carried) if (ReferenceEquals(current, equipment.GetSlot(slot)?.ContainedItem)) return slot;
            // EFT's Parent getter throws for a detached/depleted treatment item.
            // CurrentAddress is the nullable ownership evidence during inventory transitions.
            var parent = current.CurrentAddress?.Container?.ParentItem;
            if (parent == null || ReferenceEquals(parent, current) || ReferenceEquals(parent, equipment)) return null;
            current = parent;
        }
        return null;
    }
}
