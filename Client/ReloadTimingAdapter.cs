using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using Comet.Core;
using EFT;
using EFT.InventoryLogic;
using HarmonyLib;
using Newtonsoft.Json;
using SAIN.Components;
using SAIN.Preset.Shared.Enums;
using SAIN.SAINComponent.Classes.Decision;
using SAIN.SAINComponent.Classes.EnemyClasses;
using UnityEngine;

namespace Comet.Client;

internal sealed class ReloadTimingRuntime
{
    internal readonly ReloadTimingPolicy Policy = new();
    internal readonly List<Magazine> Magazines = new();
    internal readonly List<Ammo> Ammo = new();
    internal readonly List<Magazine> BeltMagazines = new();
    internal float BeltRefillAt;
    internal bool InventoryFault;
    internal TacticalMistakes? Mistakes;
    internal ReloadTimingFrame? LastFrame;
    internal Player.FirearmController? Firearm;
    internal float Next,
        SampleAt,
        LogAt = -1000;
    internal string Key = "",
        LogKey = "";
    internal bool Fault;

    internal void Drop()
    {
        Firearm = null;
        Magazines.Clear();
        Ammo.Clear();
        BeltMagazines.Clear();
        LastFrame = null;
    }
}

public sealed partial class Plugin
{
    private static System.Reflection.FieldInfo reloadLastTime = null!;
    private static Weapon? barrelReloadRequest;

    private static void PatchReloadTiming(Harmony h)
    {
        reloadLastTime = AccessTools.Field(typeof(SelfActionDecisionClass), "_lastReloadTime")
            ?? throw new MissingFieldException("W01 SAIN reload clock");
        h.Patch(
            AccessTools.Method(
                typeof(SelfActionDecisionClass),
                "CheckDoReload",
                new[] { typeof(Enemy), typeof(BotComponent) }
            ),
            prefix: new HarmonyMethod(typeof(Plugin), nameof(ChooseReloadTiming))
        );
        h.Patch(
            AccessTools.Method(typeof(BotReloadOnlyBarrel), nameof(BotReloadOnlyBarrel.ReloadAmmo)),
            transpiler: new HarmonyMethod(typeof(Plugin), nameof(PartialBarrelReload))
        );
    }

    private static IEnumerable<CodeInstruction> PartialBarrelReload(
        IEnumerable<CodeInstruction> instructions
    )
    {
        var code = instructions.ToList();
        var getter = AccessTools.PropertyGetter(typeof(Weapon), nameof(Weapon.ChamberAmmoCount));
        var hits = code.Select((x, i) => (x, i)).Where(x => x.x.Calls(getter)).ToArray();
        // Harmony can widen short branches before invoking the transpiler. Both sizes
        // express the same predicate; keep checking the condition and constant values.
        if (
            hits.Length != 1
            || hits[0].i + 4 >= code.Count
            || (
                code[hits[0].i + 1].opcode != OpCodes.Brfalse_S
                && code[hits[0].i + 1].opcode != OpCodes.Brfalse
            )
            || code[hits[0].i + 2].opcode != OpCodes.Ldc_I4_0
            || (
                code[hits[0].i + 3].opcode != OpCodes.Br_S
                && code[hits[0].i + 3].opcode != OpCodes.Br
            )
            || code[hits[0].i + 4].opcode != OpCodes.Ldc_I4_1
        )
            throw new InvalidOperationException(
                "W01 partial barrel reload contract changed: "
                    + string.Join("; ", hits.SelectMany(hit => code.Skip(hit.i).Take(5)))
            );
        // Only the empty-chamber predicate changes during our request. Native ammo
        // selection, animation, inventory transactions and completion callback stay intact.
        code[hits[0].i].opcode = OpCodes.Call;
        code[hits[0].i].operand = AccessTools.Method(
            typeof(Plugin),
            nameof(BarrelReloadChamberPredicate)
        );
        return code;
    }

    private static int BarrelReloadChamberPredicate(Weapon weapon) =>
        ReferenceEquals(weapon, barrelReloadRequest)
        && weapon.ReloadMode == Weapon.EReloadMode.OnlyBarrel
        && weapon.ChamberAmmoCount < weapon.Chambers.Length
            ? 0
            : weapon.ChamberAmmoCount;

    private bool ReloadScope(BotComponent b) =>
        config?.Enabled == true
        && (config.ReloadTiming.Enabled || config.PistolSwap.Enabled)
        && NativeAdapter.IsSupportedAi(b)
        && b.BotActive
        && !b.IsDead
        && !b.GameEnding
        && Authority
        && !Ghost(b.ProfileId)
        && b.SAINLayersActive;

    // A STANAG magazine can fit a 5.56 weapon while containing .300 ammunition.
    // Use EFT's next-cartridge check; mixed magazines are re-evaluated as they feed.
    private static int ReloadMagazineRounds(Weapon w) =>
        w.ReloadMode != Weapon.EReloadMode.OnlyBarrel
        && w.GetCurrentMagazine() is Magazine mag
        && mag.IsAmmoCompatible(w.Chambers)
            ? mag.Count
            : 0;

    private static int ReloadLoaded(Weapon w) => w.ChamberAmmoCount + ReloadMagazineRounds(w);

    private static bool ChooseReloadTiming(
        SelfActionDecisionClass __instance,
        Enemy enemy,
        BotComponent bot,
        ref bool __result
    )
    {
        var p = Instance;
        if (p == null || bot == null || !p.ReloadScope(bot) || enemy == null)
            return true;
        BotState? s = null;
        bool invoked = false;
        try
        {
            if (!p.states.TryGetValue(bot, out s))
            {
                s = new BotState(bot, p.config!, p.session);
                p.states.Add(bot, s);
            }
            var r = s.ReloadTiming;
            var wm = bot.BotOwner.WeaponManager;
            if (r.Fault || wm == null || wm.IsMelee)
                return true;
            var reload = wm.Reload;
            var firearm = bot.Player.HandsController as Player.FirearmController;
            if (reload == null || firearm == null)
                return true;
            __result = false;
            // Native continuation (including its own cancellation rules) runs before this hook.
            if (
                reload.Reloading
                || firearm.IsInReloadOperation()
                || firearm.IsInInteraction()
                || bot.BotOwner.Medecine?.Using == true
                || !wm.IsReady
                || wm.ShootController?.CanStartReload() != true
                || Time.time - (float)reloadLastTime.GetValue(__instance) < 1
            )
                return false;
            if (
                wm.Malfunctions.HaveMalfunction()
                && wm.Malfunctions.MalfunctionType() != Weapon.EMalfunctionState.Misfire
            )
                return true;
            if (enemy.Events.OnSearch.Value)
                return true;
            var weapon = firearm.Item;
            var id = weapon.Id.ToString();
            int loaded = ReloadLoaded(weapon);
            var f = p.ReloadFrame(bot, enemy, s, weapon);
            r.LastFrame = f;
            string choice = r.Policy.Evaluate(f, p.config!.ReloadTiming);
            if (choice == "Native")
                return true;
            p.RecordReloadTiming(bot, s);
            // The source ranks Reload / Hold / Pistol together, then tries each in that order.
            p.ObserveWeaponReturn(bot, s);
            var pistol = ReadPistol(bot, weapon);
            bool pistolReady =
                p.config.PistolSwap.Enabled
                && pistol.PrimaryInHands
                && pistol.Loaded > 0
                && !s.PistolSwap.Policy.RetryBlocked(Time.time)
                && s.WeaponReturn.Policy.PistolBlockedFor(Time.time) == 0;
            double ratio = r.Policy.Trace!.Ratio;
            double pistolScore =
                pistolReady && f.Visible && f.Distance < p.config.PistolSwap.MaxDistance
                    ? .2
                        + .6 * (1 - ratio)
                        + (f.Distance < 15 ? .2 : 0)
                        + (f.Looking ? .15 : 0)
                        - (ratio > .1 ? .6 : 0)
                    : 0;
            var ranked = new List<Candidate>
            {
                new()
                {
                    Decision = "Reload",
                    Score = r.Policy.Trace.ReloadScore * 100,
                    Eligible = true,
                },
                new()
                {
                    Decision = "Hold",
                    Score = r.Policy.Trace.HoldScore * 100,
                    Eligible = true,
                },
                new()
                {
                    Decision = "Pistol",
                    Score = pistolScore * 100,
                    Eligible = true,
                },
            };
            ranked = ranked.OrderByDescending(x => x.Score).ToList();
            r.Mistakes ??= new TacticalMistakes(p.config.TacticalMistakes, s.Id + "|reload");
            r.Mistakes.ApplyToRanking(
                ranked,
                ranked[0],
                new MixTrace(),
                bot.Info.Personality.ToString(),
                false,
                Time.time
            );
            foreach (var option in ranked)
            {
                if (option.Decision == "Hold")
                    break;
                if (option.Decision == "Pistol" && pistolScore > 0 && pistolReady)
                {
                    var noCompetition = new ReloadTimingTrace
                    {
                        ReloadScore = -100,
                        HoldScore = -100,
                    };
                    if (
                        s.PistolSwap.Policy.Evaluate(f, pistol, noCompetition, p.config.PistolSwap)
                        && p.TrySourcePistol(bot, s, f, ref invoked)
                    )
                        return false;
                }
                if (
                    option.Decision == "Reload"
                    && !r.Policy.RetryBlocked(f)
                    && p.SourceReloadNow(__instance, bot, s, firearm, weapon, f)
                )
                {
                    __result = true;
                    return false;
                }
            }
            if (loaded <= 0)
            {
                if (p.TryPistolSwap(bot, s, f, ref invoked))
                    return false;
                // The native empty-weapon fallback retains magazine filling and ammo-aware weapon selection.
                return true;
            }
            return false;
        }
        catch (Exception ex)
        {
            if (s != null)
                p.ReloadTimingError(s, ex);
            return !invoked;
        }
    }

    private bool SourceReloadNow(
        SelfActionDecisionClass decision,
        BotComponent bot,
        BotState s,
        Player.FirearmController firearm,
        Weapon weapon,
        ReloadTimingFrame f
    )
    {
        var r = s.ReloadTiming;
        var reload = bot.BotOwner.WeaponManager.Reload;
        if (r.Policy.Trace != null)
            r.Policy.Trace.Choice = "Reload";
        r.Policy.Request(f);
        r.Firearm = firearm;
        bool available = reload.CanReload(true, out var mag, out var ammo);
        if (!available)
        {
            r.Policy.Result(false, f, config!.ReloadTiming);
            RecordReloadTiming(bot, s, true);
            return false;
        }
        bot.BotOwner.ShootData.EndShoot();
        reload.Reloading = true;
        reloadLastTime.SetValue(decision, Time.time);
        if (mag != null)
            reload.ReloadMagazine(mag);
        else if (ammo != null && ammo.Count > 0)
        {
            var previous = barrelReloadRequest;
            barrelReloadRequest = weapon;
            try
            {
                reload.ReloadAmmo(ammo);
            }
            finally
            {
                barrelReloadRequest = previous;
            }
        }
        r.Policy.Result(true, f, config!.ReloadTiming);
        RecordReloadTiming(bot, s, true);
        return true;
    }

    private ReloadTimingFrame ReloadFrame(BotComponent b, Enemy e, BotState s, Weapon w)
    {
        var mag = w.GetCurrentMagazine();
        var f = new ReloadTimingFrame
        {
            Now = Time.time,
            Weapon = w.Id.ToString(),
            WeaponClass = w.WeapClass,
            Target = e.EnemyProfileId,
            Distance = e.RealDistance,
            ContactAge = e.TimeSinceLastKnownUpdated,
            SightAge = e.Seen ? e.TimeSinceSeen : 999,
            Visible = e.IsVisible,
            Looking = e.IsVisible && e.EnemyLookingAtMe,
            Incoming = b.Medical.TimeSinceShot < 1.5,
            Protected = b.Cover.CoverInUse?.BotInThisCover == true,
            BusyObserved = e.Status.VulnerableAction != SAIN.Models.Enums.EEnemyAction.None,
            Chamber = w.ChamberAmmoCount,
            Magazine = ReloadMagazineRounds(w),
            Capacity =
                w.ReloadMode == Weapon.EReloadMode.OnlyBarrel
                    ? w.Chambers.Length
                    : (mag?.MaxCount ?? 0) + w.Chambers.Length,
        };
        f.Loaded = f.Chamber + f.Magazine;
        return f;
    }

    private ReloadTimingFrame ReloadResources(
        BotComponent b,
        BotState s,
        Weapon w,
        ReloadTimingFrame f,
        bool force = false
    )
    {
        var r = s.ReloadTiming;
        var mag = w.GetCurrentMagazine();
        if (
            f.Capacity <= 0
            || !force && f.Loaded >= f.Capacity * config!.ReloadTiming.ConsiderBelow / 100
        )
        {
            f.Resource = "not-needed";
            return f;
        }
        // Read only reachable compatible inventory. These lists are cleared in the caller's finally block.
        Magazine? best = null;
        var slot = w.GetMagazineSlot();
        if (
            w.ReloadMode
            is Weapon.EReloadMode.ExternalMagazine
                or Weapon.EReloadMode.ExternalMagazineWithInternalReloadSupport
        )
        {
            if (!ReloadInventory.ReadMagazines(b.Player.InventoryController, r.Magazines))
            {
                f.Block = "reload-inventory-limit";
                return f;
            }
            f.ResourcesChecked = true;
            foreach (var m in r.Magazines)
            {
                if (ReferenceEquals(m, mag))
                    continue;
                f.MagazineCandidates++;
                if (ReloadInventory.Location(m, b.Player.Inventory.Equipment) == "ArmBand")
                    f.BeltMagazines++;
                if (m.Count <= f.Magazine)
                {
                    f.NonImprovingMagazines++;
                    continue;
                }
                if (!m.IsAmmoCompatible(w.Chambers))
                {
                    f.RejectedMagazines++;
                    f.ResourceRejection = "magazine-ammo-incompatible";
                    continue;
                }
                if (slot == null)
                {
                    f.ResourceRejection = "no-magazine-slot";
                    continue;
                }
                var check = ItemManipulator.CheckMoveIgnoringTargetItem(
                    m,
                    slot,
                    b.Player.InventoryController
                );
                if (!check.Succeeded)
                {
                    f.RejectedMagazines++;
                    if (f.ResourceRejection.Length == 0)
                    {
                        var why = check.Error?.ToString() ?? "inventory-move-refused";
                        f.ResourceRejection = why.Substring(0, Math.Min(160, why.Length));
                    }
                    continue;
                }
                if (best == null || m.Count > best.Count)
                    best = m;
            }
        }
        if (best != null)
        {
            f.Resource = "magazine";
            f.Reserve = best.Count;
            f.ResourceKey = best.Id + ":" + best.Count;
            f.ResourceLocation = ReloadInventory.Location(best, b.Player.Inventory.Equipment);
            f.ResourceItem = best.ShortName.Localized();
        }
        else if (w is Revolver && mag != null)
        {
            b.Player.InventoryController.GetReachableItemsOfTypeNonAlloc(r.Ammo, (Ammo a) =>
                a.StackObjectsCount > 0
                && b.Player.InventoryController.Examined(a)
                && a.CheckAction(null).Succeeded
                && mag.CheckCompatibility(a)
            );
            if (r.Ammo.Count > 128)
            {
                f.Block = "reload-inventory-limit";
                return f;
            }
            foreach (var a in r.Ammo)
                f.Reserve += a.StackObjectsCount;
            if (f.Reserve > 0)
            {
                f.Resource = "loose-ammo";
                f.ResourceKey = f.Resource + ":" + f.Reserve;
            }
        }
        else if (w.ReloadMode != Weapon.EReloadMode.ExternalMagazine && w.Chambers.Length > 0)
        {
            b.Player.InventoryController.GetAcceptableItemsNonAlloc(
                BotReload._availableEquipmentSlots,
                r.Ammo,
                (Ammo a) =>
                    a.StackObjectsCount > 0
                    && w.Chambers[0].CanAccept(a)
                    && a.CheckAction(null).Succeeded
            );
            if (r.Ammo.Count > 128)
            {
                f.Block = "reload-inventory-limit";
                return f;
            }
            foreach (var a in r.Ammo)
                f.Reserve += a.StackObjectsCount;
            if (f.Reserve > 0)
            {
                f.Resource = "loose-ammo";
                f.ResourceKey = f.Resource + ":" + f.Reserve;
            }
        }
        return f;
    }

    private void SampleReloadTiming(BotComponent b, BotState s)
    {
        var r = s.ReloadTiming;
        if (!r.Policy.Active)
            return;
        if (!ReloadScope(b))
        {
            EndReloadTiming(s, "reload-out-of-scope");
            return;
        }
        if (Time.time < r.SampleAt)
            return;
        r.SampleAt = Time.time + .1f;
        var f = r.Firearm;
        bool same = f != null && ReferenceEquals(b.Player.HandsController, f);
        if (r.LastFrame != null && r.Policy.Trace != null)
            r.Policy.Trace.ContactAge =
                r.LastFrame.ContactAge + Math.Max(0, Time.time - r.LastFrame.Now);
        r.Policy.Observe(
            Time.time,
            same && f!.IsInReloadOperation(),
            b.BotOwner.WeaponManager?.Reload?.Reloading == true,
            same ? ReloadLoaded(f!.Item) : 0,
            same
        );
        if (!r.Policy.Active)
        {
            if (r.LastFrame != null)
            {
                r.LastFrame.Now = Time.time;
                r.Policy.CompleteObservation(r.LastFrame, config!.ReloadTiming);
            }
            r.Drop();
        }
        RecordReloadTiming(b, s);
    }

    private void EndReloadTiming(BotState s, string why)
    {
        var r = s.ReloadTiming;
        r.Policy.Clear(why);
        r.Drop();
        RecordReloadTiming(null, s, true);
    }

    private void ReloadTimingError(BotState s, Exception ex)
    {
        if (s.ReloadTiming.Fault)
            return;
        s.ReloadTiming.Fault = true;
        EndReloadTiming(s, "reload-adapter-error");
        Logger.LogWarning("W01 yielded to SAIN: " + ex.GetBaseException().Message);
    }

    private void RecordReloadTiming(BotComponent? b, BotState s, bool force = false)
    {
        var r = s.ReloadTiming;
        var t = r.Policy.Trace;
        if (t == null)
            return;
        if (b != null)
            t.Action =
                b.Decision.CurrentSelfDecision == ESelfActionType.Reload
                    ? "Reload"
                    : b.Decision.CurrentCombatDecision.ToString();
        string key =
            t.State
            + ":"
            + t.Choice
            + ":"
            + t.Reason
            + ":"
            + t.Loaded
            + ":"
            + t.Reserve
            + ":"
            + t.Started;
        if (!force && key == r.LogKey && Time.time - r.LogAt < .25f)
            return;
        r.LogKey = key;
        r.LogAt = Time.time;
        var json = Diagnostics.Serialize(
            new
            {
                schema = 1,
                session,
                sequence = ++sequence,
                feature = "W01",
                stage = "evaluated",
                decision = "ReloadTiming",
                reason = t.Reason,
                realtime = Time.realtimeSinceStartup,
                utc = DateTime.UtcNow.ToString("O"),
                configRevision = config?.Revision ?? 0,
                profileId = s.ProfileId,
                name = s.Name,
                botInstance = s.Id,
                reloadTiming = t,
                reloadTimingConfig = config?.ReloadTiming,
            }
        );
        diagnostics?.Write(json);
        Api.Decisions.Publish(json, ex =>
        {
            if (!subscriberWarned)
            {
                subscriberWarned = true;
                Logger.LogWarning(ex.Message);
            }
        });
    }
}
