using System;
using Comet.Core;
using EFT;
using EFT.InventoryLogic;
using Newtonsoft.Json;
using SAIN.Components;
using SAIN.Preset.Shared.Enums;
using UnityEngine;

namespace Comet.Client;

internal sealed class PistolSwapRuntime
{
    internal readonly PistolSwapPolicy Policy = new();
    internal float SampleAt,
        LogAt = -1000;
    internal (string, string, string, int, int, bool, bool) LogKey;
    internal bool Fault;
}

public sealed partial class Plugin
{
    private bool PistolScope(BotComponent b) =>
        config?.Enabled == true
        && config.PistolSwap.Enabled
        && NativeAdapter.IsSupportedAi(b)
        && b.BotActive
        && !b.IsDead
        && !b.GameEnding
        && Authority
        && !Ghost(b.ProfileId)
        && b.SAINLayersActive;

    private static Weapon? HolsteredPistol(BotComponent b) =>
        b.Player.Inventory?.Equipment?.GetSlot(EquipmentSlot.Holster)?.ContainedItem as Weapon;

    private static PistolSwapFrame ReadPistol(BotComponent b, Weapon main)
    {
        var eq = b.Player.Inventory?.Equipment;
        var w = HolsteredPistol(b);
        var p = new PistolSwapFrame
        {
            PrimaryInHands =
                ReferenceEquals(eq?.GetSlot(EquipmentSlot.FirstPrimaryWeapon)?.ContainedItem, main)
                || ReferenceEquals(
                    eq?.GetSlot(EquipmentSlot.SecondPrimaryWeapon)?.ContainedItem,
                    main
                ),
        };
        if (w == null)
            return p;
        p.Pistol = w.Id.ToString();
        p.Chamber = w.ChamberAmmoCount;
        p.Magazine = ReloadMagazineRounds(w);
        p.Loaded = p.Chamber + p.Magazine;
        p.Capacity =
            w.ReloadMode == Weapon.EReloadMode.OnlyBarrel
                ? w.Chambers.Length
                : (w.GetCurrentMagazine()?.MaxCount ?? 0) + w.Chambers.Length;
        p.Usable = !ReferenceEquals(w, main);
        if (!p.Usable)
            p.Block = "pistol-malfunction-or-already-held";
        return p;
    }

    // One arbitration point shared with W01. False lets the selected reload proceed in this same call.
    private bool TryPistolSwap(BotComponent b, BotState s, ReloadTimingFrame f, ref bool invoked)
    {
        var r = s.PistolSwap;
        if (r.Fault || !PistolScope(b))
            return false;
        ObserveWeaponReturn(b, s);
        if (s.WeaponReturn.Policy.PistolBlockedFor(Time.time) > 0)
            return false;
        var wm = b.BotOwner.WeaponManager;
        var main = wm.CurrentWeapon;
        if (main == null || main.Id.ToString() != f.Weapon)
            return false;
        var pistol = ReadPistol(b, main);
        bool pick = r.Policy.Evaluate(f, pistol, s.ReloadTiming.Policy.Trace, config!.PistolSwap);
        RecordPistolSwap(b, s);
        if (!pick)
            return false;
        return TrySourcePistol(b, s, f, ref invoked);
    }

    private bool TrySourcePistol(BotComponent b, BotState s, ReloadTimingFrame f, ref bool invoked)
    {
        var r = s.PistolSwap;
        var wm = b.BotOwner.WeaponManager;
        var main = wm.CurrentWeapon;
        var pistol = ReadPistol(b, main);
        // Revalidate actual equipment before the only mutation. A cached BotWeaponInfo is not authoritative.
        var selected = HolsteredPistol(b);
        var selector = wm.Selector;
        if (
            selected == null
            || selected.Id.ToString() != pistol.Pistol
            || ReloadLoaded(selected) != pistol.Loaded
            || !ReferenceEquals(b.Player.HandsController?.Item, main)
            || selector == null
            || !selector.IsWeaponReady
            || wm.Reload.Reloading
        )
        {
            r.Policy.End("pistol-equipment-changed");
            RecordPistolSwap(b, s, true);
            return false;
        }
        r.Policy.Request(Time.time);
        RecordPistolSwap(b, s, true);
        float previousTimer = selector._nextChangeTime;
        float temporary = 0;
        bool accepted = false;
        string previousIntent = s.WeaponReturn.Intent;
        try
        {
            // Bypass only this deliberate attempt's distance-choice throttle, preserving the native timer on success.
            selector._nextChangeTime = temporary;
            invoked = true;
            s.WeaponReturn.Intent = "W02";
            accepted = selector.TryChangeToSlot(EquipmentSlot.Holster, true);
        }
        catch (Exception ex)
        {
            r.Fault = true;
            r.Policy.End("pistol-native-error-after-request");
            Logger.LogWarning("W02 yielded to SAIN: " + ex.GetBaseException().Message);
            RecordPistolSwap(b, s, true);
            return true; // No second native mutation after an uncertain partial call.
        }
        finally
        {
            s.WeaponReturn.Intent = previousIntent;
            if (!accepted && selector._nextChangeTime == temporary)
                selector._nextChangeTime = previousTimer;
        }
        r.Policy.Result(accepted, Time.time, config.PistolSwap);
        RecordPistolSwap(b, s, true);
        if (!accepted)
            return false;
        s.WeaponReturn.ReturnId = f.Weapon;
        s.ReloadTiming.Policy.End("reload-deferred-for-pistol");
        if (config.ReloadTiming.Enabled)
            RecordReloadTiming(b, s, true);
        SamplePistolSwap(b, s);
        return true;
    }

    private void SamplePistolSwap(BotComponent b, BotState s)
    {
        var r = s.PistolSwap;
        if (!r.Policy.Active)
            return;
        if (!PistolScope(b))
        {
            EndPistolSwap(s, "pistol-out-of-scope");
            return;
        }
        if (Time.time < r.SampleAt)
            return;
        r.SampleAt = Time.time + .1f;
        var firearm = b.Player.HandsController as Player.FirearmController;
        string id = firearm?.Item?.Id.ToString() ?? "";
        var wm = b.BotOwner.WeaponManager;
        bool ready =
            firearm != null
            && wm.IsReady
            && wm.Selector.IsWeaponReady
            && !wm.Selector.IsChanging
            && !firearm.IsInInteraction()
            && !firearm.IsInReloadOperation()
            && firearm.CanStartReload();
        r.Policy.Observe(Time.time, id, ready, config!.PistolSwap);
        RecordPistolSwap(b, s);
    }

    private void EndPistolSwap(BotState s, string why)
    {
        s.PistolSwap.Policy.Clear(why);
        RecordPistolSwap(null, s, true);
    }

    private void PistolSwapError(BotState s, Exception ex)
    {
        if (s.PistolSwap.Fault)
            return;
        s.PistolSwap.Fault = true;
        EndPistolSwap(s, "pistol-adapter-error");
        Logger.LogWarning("W02 yielded to SAIN: " + ex.GetBaseException().Message);
    }

    private void RecordPistolSwap(BotComponent? b, BotState s, bool force = false)
    {
        var r = s.PistolSwap;
        var t = r.Policy.Trace;
        if (t == null)
            return;
        var key = (t.State, t.Choice, t.Reason, t.MainLoaded, t.PistolLoaded, t.InHands, t.Ready);
        if (!force && key == r.LogKey && Time.time - r.LogAt < .25f)
            return;
        r.LogKey = key;
        r.LogAt = Time.time;
        if (b != null)
            t.Action =
                b.Decision.CurrentSelfDecision == ESelfActionType.Reload
                    ? "Reload"
                    : b.Decision.CurrentCombatDecision.ToString();
        var json = Diagnostics.Serialize(
            new
            {
                schema = 1,
                session,
                sequence = ++sequence,
                feature = "W02",
                stage = "evaluated",
                decision = "PistolSwap",
                reason = t.Reason,
                realtime = Time.realtimeSinceStartup,
                utc = DateTime.UtcNow.ToString("O"),
                configRevision = config?.Revision ?? 0,
                profileId = s.ProfileId,
                name = s.Name,
                botInstance = s.Id,
                pistolSwap = t,
                pistolSwapConfig = config?.PistolSwap,
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
