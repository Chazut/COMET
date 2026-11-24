using System;
using System.Collections.Generic;
using Comet.Core;
using EFT;
using EFT.InventoryLogic;
using HarmonyLib;
using Newtonsoft.Json;
using SAIN;
using SAIN.Components;
using SAIN.SAINComponent.Classes.WeaponFunction;
using UnityEngine;

namespace Comet.Client;

internal sealed class TacticalDeviceEntry
{
    internal LightComponent Component = null!;
    internal string Name = "";
    internal readonly TacticalDeviceAttempt Attempt = new();
}

internal sealed class TacticalWeaponDevices
{
    internal Weapon Weapon = null!;
    internal readonly List<TacticalDeviceEntry> Entries = new();
    internal float CheckedAt = -100,
        UsedAt;
    internal bool Dirty = true,
        Unsupported;
}

internal sealed class TacticalDevicesRuntime
{
    internal readonly List<TacticalWeaponDevices> Weapons = new();
    internal TacticalDevicesTrace? Trace;
    internal string Key = "";
    internal float LogAt,
        CallbackAt,
        LastTime,
        Next;
    internal bool Fault;
}

public sealed partial class Plugin
{
    partial void SuspendDevicesForAmbush(BotState s) =>
        PauseTacticalDevices(s, "devices-ambush-owner");

    private static AccessTools.FieldRef<BotLightController, bool> deviceWant = null!;

    private static void PatchTacticalDevices(Harmony h)
    {
        deviceWant = AccessTools.FieldRefAccess<BotLightController, bool>("wantLightOn");
        // Runs after L01 (Priority.Last=0). A concealment veto must win before any normal ON command.
        h.Patch(
            AccessTools.Method(typeof(BotLightController), "updateLightToggle"),
            prefix: new HarmonyMethod(typeof(Plugin), nameof(TacticalDevicesTick))
            {
                priority = -1000,
            }
        );
        h.Patch(
            AccessTools.Method(
                typeof(Player.FirearmController),
                nameof(Player.FirearmController.Drop)
            ),
            prefix: new HarmonyMethod(typeof(Plugin), nameof(TacticalDevicesHolster))
            {
                priority = Priority.Last,
            }
        );
        h.Patch(
            AccessTools.Method(typeof(Player), nameof(Player.OnItemAddedOrRemoved)),
            postfix: new HarmonyMethod(typeof(Plugin), nameof(TacticalDevicesInventoryChanged))
        );
        h.Patch(
            AccessTools.Method(typeof(BotComponent), nameof(BotComponent.Dispose)),
            prefix: new HarmonyMethod(typeof(Plugin), nameof(TacticalDevicesDispose))
        );
    }

    private bool DeviceScope(BotComponent b) =>
        config?.Enabled == true
        && config.TacticalDevices.Enabled
        && world != null
        && Authority
        && b != null
        && NativeAdapter.IsSupportedAi(b)
        && b.BotActive
        && !b.IsDead
        && !b.GameEnding
        && !Ghost(b.ProfileId);

    private BotState DeviceState(BotComponent b)
    {
        if (!states.TryGetValue(b, out var s))
        {
            s = new BotState(b, config!, session);
            states.Add(b, s);
        }
        return s;
    }

    private static bool TacticalDevicesTick(BotLightController __instance, bool __runOriginal)
    {
        var p = Instance;
        var b = __instance.Bot;
        if (p == null || b == null)
            return true;
        try
        {
            if (!p.DeviceScope(b))
            {
                if (p.states.TryGetValue(b, out var old))
                    p.EndTacticalDevices(old, "devices-out-of-scope");
                return true;
            }
            if (!p.states.ContainsKey(b) && p.states.Count >= 512)
                return true;
            var s = p.DeviceState(b);
            var r = s.TacticalDevices;
            r.CallbackAt = Time.time;
            if (r.Fault)
                return true;
            if (!__runOriginal)
            {
                p.PauseTacticalDevices(s, "devices-higher-priority-owner");
                return true;
            }
            if (!b.SAINLayersActive && b.GoalEnemy == null)
            {
                p.PauseTacticalDevices(s, "devices-native-owner-inactive");
                return true;
            }
            // L01 yielded to another mod: normal lighting must not immediately undo that external decision.
            if (s.LightDiscipline.Policy.Trace?.Reason == "light-device-changed-by-other-owner")
            {
                p.PauseTacticalDevices(s, "devices-external-owner");
                return false;
            }
            var hand = b.Player.HandsController as Player.FirearmController;
            if (
                hand == null
                || hand.Item is not Weapon weapon
                || !ReferenceEquals(weapon, b.BotOwner.WeaponManager.CurrentWeapon)
            )
            {
                p.PauseTacticalDevices(s, "devices-no-held-weapon");
                return false;
            }
            bool want = deviceWant(__instance);
            var native = b.BotOwner.BotLight;
            bool dark =
                native._isInDarkPlace
                && SAIN.SAINPlugin
                    .LoadedPreset
                    .GlobalSettings
                    .General
                    .Flashlight
                    .AllowLightOnForDarkBuildings;
            // Preserve SAIN's own role-specific light preference. Player Scavs use
            // assault settings even though they now participate in COMET tactics.
            var flashlight = SAIN.SAINPlugin.LoadedPreset.GlobalSettings.General.Flashlight;
            bool offWithoutEnemy =
                BotEligibility.Kind(b.Player) == "player-scav"
                    ? flashlight.TurnLightOffNoEnemySCAV
                    : flashlight.TurnLightOffNoEnemyPMC;
            if (
                want
                && (
                    !native.CanUseBySettings
                    || !dark && b.BotOwner.Memory.GoalEnemy == null && offWithoutEnemy
                )
            )
            {
                p.PauseTacticalDevices(s, "devices-native-on-not-allowed");
                return false;
            }
            if (!want && dark)
            {
                p.PauseTacticalDevices(s, "devices-native-dark-room");
                return false;
            }
            if (!p.DeviceHandsReady(b, hand))
            {
                p.PauseTacticalDevices(s, "devices-hands-busy");
                return false;
            }
            if (
                r.Trace?.State != "released"
                && r.Trace?.Target == want
                && r.Trace?.Weapon == weapon.Id.ToString()
                && Time.time < r.Next
                && Time.time >= r.LastTime
            )
                return false;
            r.Next = Time.time + .05f;
            p.DriveTacticalDevices(b, s, hand, weapon, want, "SAIN", false);
            return false;
        }
        catch (Exception ex)
        {
            p.TacticalDevicesError(b, ex);
            return true;
        }
    }

    private bool DeviceHandsReady(BotComponent b, Player.FirearmController hand) =>
        WeaponHandsReady(b, hand)
        && !hand.IsInRemoveOperation()
        && !hand.IsInReloadOperation()
        && !hand.IsInSpawnOperation()
        && !hand.IsInventoryOpen()
        && !hand.IsInInteractionStrictCheck()
        && !hand.IsHandsProcessing()
        && !b.BotOwner.Medecine.Using
        && !b.BotOwner.WeaponManager.Reload.Reloading
        && !b.BotOwner.DoorOpener.Interacting
        && !b.DoorOpener.Interacting;

    private TacticalWeaponDevices DeviceCache(BotState s, Weapon weapon)
    {
        var r = s.TacticalDevices;
        if (Time.time < r.LastTime)
            r.Weapons.Clear();
        r.LastTime = Time.time;
        TacticalWeaponDevices? w = null;
        foreach (var entry in r.Weapons)
            if (ReferenceEquals(entry.Weapon, weapon))
            {
                w = entry;
                break;
            }
        if (w == null)
        {
            if (r.Weapons.Count >= 4)
            {
                r.Weapons.Sort((a, b) => a.UsedAt.CompareTo(b.UsedAt));
                r.Weapons.RemoveAt(0);
            }
            w = new() { Weapon = weapon };
            r.Weapons.Add(w);
        }
        w.UsedAt = Time.time;
        if (!w.Dirty && Time.time - w.CheckedAt < 5)
            return w;
        w.CheckedAt = Time.time;
        w.Dirty = false;
        w.Unsupported = false;
        var updated = new List<TacticalDeviceEntry>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var mod in weapon.Mods)
        {
            if (mod.GetItemComponent<LightComponent>() is not { } device)
                continue;
            if (updated.Count >= 16 || !ids.Add(device.Item.Id.ToString()))
            {
                w.Unsupported = true;
                break;
            }
            var prior = w.Entries.Find(x => ReferenceEquals(x.Component, device));
            updated.Add(
                prior ?? new() { Component = device, Name = TacticalDeviceName(device.Item) }
            );
        }
        w.Entries.Clear();
        w.Entries.AddRange(updated);
        return w;
    }

    private static string TacticalDeviceName(Item item)
    {
        // Resolve once per physical accessory, not on each diagnostic heartbeat.
        // Missing/modded localization must never prevent the native light command.
        try
        {
            var key = item.ShortName;
            if (string.IsNullOrWhiteSpace(key))
                return "";
            var name = key.Localized();
            if (string.IsNullOrWhiteSpace(name) || name == key)
                return "";
            name = name.Trim();
            return name.Length <= 160 ? name : name.Substring(0, 160);
        }
        catch
        {
            return "";
        }
    }

    private void DriveTacticalDevices(
        BotComponent b,
        BotState s,
        Player.FirearmController hand,
        Weapon weapon,
        bool target,
        string source,
        bool oneShot
    )
    {
        var r = s.TacticalDevices;
        var c = config!.TacticalDevices;
        var w = DeviceCache(s, weapon);
        var t = new TacticalDevicesTrace
        {
            Action = b.Decision.CurrentCombatDecision.ToString(),
            Weapon = weapon.Id.ToString(),
            Target = target,
            Source = source,
            Devices = w.Entries.Count,
        };
        r.Trace = t;
        if (w.Unsupported)
        {
            t.Devices = 0;
            t.State = "yielded";
            t.Reason = "devices-unsupported-layout";
            RecordTacticalDevices(s);
            return;
        }
        var commands = new List<LightsState>();
        var sending = new List<TacticalDeviceEntry>();
        foreach (var e in w.Entries)
        {
            var a = e.Attempt;
            var d = e.Component;
            a.SetTarget(target, d.SelectedMode);
            if (oneShot)
                a.NextCommand = 0; // A final OFF may precede immediate removal; there is no deferred retry.
            if (a.Observe(Time.time, d.IsActive, d.SelectedMode, c))
            {
                commands.Add(
                    new()
                    {
                        Id = d.Item.Id,
                        IsActive = target,
                        LightMode = d.SelectedMode,
                    }
                );
                sending.Add(e);
            }
        }
        if (commands.Count > 0)
        {
            foreach (var e in sending)
                e.Attempt.Request(Time.time, c);
            t.Requested = true;
            try
            {
                t.Accepted = hand.SetLightsState(commands.ToArray());
            }
            catch (Exception ex)
            {
                t.Execution = "Native command threw: " + ex.GetBaseException().GetType().Name;
            }
            // Whether accepted or refused, record the real per-device result. An accepted call is not proof of success.
            foreach (var e in sending)
                e.Attempt.Observe(Time.time, e.Component.IsActive, e.Component.SelectedMode, c);
        }
        t.Accessories = new TacticalDeviceDetail[w.Entries.Count];
        bool all = true,
            pending = false,
            paused = false,
            yielded = false;
        for (int i = 0; i < w.Entries.Count; i++)
        {
            var e = w.Entries[i];
            var d = e.Component;
            var a = e.Attempt;
            if (d.IsActive)
                t.ActiveDevices++;
            bool matches = d.IsActive == target;
            all &= matches;
            pending |= a.Pending;
            paused |= a.State == "backoff";
            yielded |= a.Yielded;
            t.Accessories[i] = new()
            {
                Id = d.Item.Id.ToString(),
                Name = e.Name,
                Mode = d.SelectedMode,
                Active = d.IsActive,
                Requested = a.Requested,
                Confirmed = a.Confirmed,
                State = a.State,
                Failures = a.Failures,
                Rounds = a.Rounds,
                RetryIn = Math.Max(0, a.RetryAt - Time.time),
            };
        }
        t.Observed = t.Devices > 0 && all;
        t.State =
            t.Devices == 0 ? "no-device"
            : yielded ? "yielded"
            : all ? "confirmed"
            : paused ? "backoff"
            : pending ? "pending"
            : "waiting";
        t.Reason = "devices-" + t.State;
        if (t.Execution == "No command")
            t.Execution =
                t.Requested
                    ? (
                        t.Observed ? "Native request; target state observed"
                        : t.Accepted ? "Native request accepted; awaiting device state"
                        : "Native request refused; awaiting confirmation deadline"
                    )
                : t.Observed ? "Target state already present"
                : "No command; waiting or yielding";
        t.ValidFor = t.State == "no-device" ? 0 : 1;
        RecordTacticalDevices(s);
        if (oneShot)
            foreach (var e in w.Entries)
                e.Attempt.Suspend(); // Never retry a command on a controller after it leaves the hands.
    }

    private static void TacticalDevicesHolster(
        Player.FirearmController __instance,
        bool __runOriginal
    )
    {
        var p = Instance;
        try
        {
            if (
                !__runOriginal
                || p == null
                || __instance.Destroyed
                || __instance._player is not { } player
                || !player.IsAI
                || !SAINEnableClass.GetSAIN(player.ProfileId, out var b)
                || b == null
                || !p.DeviceScope(b)
                || !ReferenceEquals(player.HandsController, __instance)
                || !p.DeviceHandsReady(b, __instance)
            )
                return;
            if (!p.states.ContainsKey(b) && p.states.Count >= 512)
                return;
            var s = p.DeviceState(b);
            if (s.TacticalDevices.Fault)
                return;
            var w = p.DeviceCache(s, __instance.Item);
            w.Dirty = true; // Inspect once more at the transition, while the old weapon's native controller still exists.
            foreach (var e in w.Entries)
                e.Attempt.Suspend();
            p.DriveTacticalDevices(b, s, __instance, __instance.Item, false, "holster", true);
        }
        catch (Exception ex)
        {
            if (p != null)
                p.Logger.LogWarning("L02 holster skipped: " + ex.GetBaseException().Message);
        }
    }

    private static void TacticalDevicesInventoryChanged(Player __instance)
    {
        var p = Instance;
        if (p == null || !__instance.IsAI)
            return;
        try
        {
            if (
                SAINEnableClass.GetSAIN(__instance.ProfileId, out var b)
                && b != null
                && p.states.TryGetValue(b, out var s)
            )
                foreach (var w in s.TacticalDevices.Weapons)
                    w.Dirty = true;
        }
        catch
        { /* The bounded five-second refresh remains available during teardown. */
        }
    }

    private void PauseTacticalDevices(BotState s, string reason)
    {
        var r = s.TacticalDevices;
        foreach (var w in r.Weapons)
        foreach (var e in w.Entries)
            e.Attempt.Suspend();
        if (r.Trace == null)
            return;
        r.Trace.State = "released";
        r.Trace.Reason = reason;
        r.Trace.ValidFor = 0;
        r.Trace.Requested = r.Trace.Accepted = false;
        r.Trace.Execution = "No command; native priority or another owner";
        RecordTacticalDevices(s);
    }

    private void EndTacticalDevices(BotState s, string reason)
    {
        PauseTacticalDevices(s, reason);
        s.TacticalDevices.Weapons.Clear();
    }

    private void SampleTacticalDevices(BotComponent b, BotState s)
    {
        var r = s.TacticalDevices;
        if (r.Weapons.Count == 0)
            return;
        if (!DeviceScope(b) || Time.time - r.CallbackAt > 1 || Time.time < r.CallbackAt)
            EndTacticalDevices(s, "devices-owner-ended");
    }

    private static void TacticalDevicesDispose(BotComponent __instance)
    {
        var p = Instance;
        if (p != null && p.states.TryGetValue(__instance, out var s))
            p.EndTacticalDevices(s, "devices-disposed");
    }

    private void TacticalDevicesError(BotComponent b, Exception ex)
    {
        if (states.TryGetValue(b, out var s))
        {
            if (!s.TacticalDevices.Fault)
                Logger.LogWarning("L02 yielded: " + ex.GetBaseException().Message);
            s.TacticalDevices.Fault = true;
            EndTacticalDevices(s, "devices-adapter-error");
        }
    }

    private void RecordTacticalDevices(BotState s)
    {
        if (!ReplayCapture.Detailed)
            return;
        var r = s.TacticalDevices;
        var t = r.Trace;
        if (t == null)
            return;
        string key =
            t.State
            + ":"
            + t.Reason
            + ":"
            + t.Source
            + ":"
            + t.Weapon
            + ":"
            + t.Target
            + ":"
            + t.Requested
            + ":"
            + t.Accepted
            + ":"
            + t.ActiveDevices;
        foreach (var a in t.Accessories)
            key += ":" + a.Id + ":" + a.Mode + ":" + a.State + ":" + a.Rounds;
        if (key == r.Key && (t.ValidFor == 0 || Time.time < r.LogAt))
            return;
        r.Key = key;
        r.LogAt = Time.time + .5f;
        var stamp = new ReplayStamp(session, ++sequence, Time.realtimeSinceStartup, DateTime.UtcNow.ToString("O"),
            config?.Revision ?? 0, s.ProfileId, s.Name, s.Id);
        Diagnostics.PublishSnapshot(diagnostics, stamp.Capture(t, config?.TacticalDevices), ex =>
        {
            if (!subscriberWarned)
            {
                subscriberWarned = true;
                Logger.LogWarning(ex.Message);
            }
        });
    }
}
