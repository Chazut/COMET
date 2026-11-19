using System;
using System.Collections.Generic;
using Comet.Core;
using EFT;
using EFT.InventoryLogic;
using HarmonyLib;
using Newtonsoft.Json;
using SAIN.Components;
using SAIN.Preset.Shared.Enums;
using SAIN.SAINComponent.Classes.WeaponFunction;
using UnityEngine;

namespace Comet.Client;

internal sealed class LightDeviceSnapshot
{
    internal LightComponent Component = null!;
    internal int Mode;
}

internal sealed class LightDisciplineRuntime
{
    internal readonly LightDisciplinePolicy Policy = new();
    internal readonly LightDisciplineFrame Frame = new();
    internal readonly List<LightDeviceSnapshot> Devices = new();
    internal Weapon? Weapon;
    internal object? Enemy;
    internal bool Captured,
        Fault,
        ModesDirty;
    internal string[] Modes = Array.Empty<string>();
    internal float CallbackAt,
        Next,
        LogAt,
        ForceAt;
    internal double LastActuatorMilliseconds;
    internal (
        string State,
        string Reason,
        string Contact,
        string Action,
        string Tactic,
        bool Requested,
        bool ObservedOff,
        int ActiveDevices,
        bool NightVision
    ) Key;

    internal void Drop()
    {
        Devices.Clear();
        Modes = Array.Empty<string>();
        Weapon = null;
        Enemy = null;
        Captured = false;
        Next = 0;
    }
}

public sealed partial class Plugin
{
    partial void SuspendDevicesForAmbush(BotState s);

    private static void PatchLightDiscipline(Harmony h)
    {
        // Preserve SAIN's desired light state and clocks: only veto its actuator while concealment owns it.
        h.Patch(
            AccessTools.Method(typeof(BotLightController), "updateLightToggle")
                ?? throw new MissingMethodException("L01 SAIN light actuator"),
            prefix: new HarmonyMethod(typeof(Plugin), nameof(LightDisciplineTick))
            {
                priority = Priority.Last,
            }
        );
        h.Patch(
            AccessTools.Method(typeof(BotComponent), nameof(BotComponent.Dispose)),
            prefix: new HarmonyMethod(typeof(Plugin), nameof(LightDisciplineDispose))
        );
    }

    private bool LightScope(BotComponent b) =>
        config?.Enabled == true
        && config.LightDiscipline.Enabled
        && world != null
        && Authority
        && b != null
        && NativeAdapter.IsSupportedAi(b)
        && b.BotActive
        && !b.IsDead
        && !b.GameEnding
        && !Ghost(b.ProfileId)
        && b.SAINLayersActive;

    private static bool LightDisciplineTick(BotLightController __instance, bool __runOriginal)
    {
        var p = Instance;
        var b = __instance.Bot;
        if (p == null || b == null)
            return true;
        try
        {
            if (!__runOriginal || !p.LightScope(b))
            {
                if (p.states.TryGetValue(b, out var old))
                    p.EndLightDiscipline(old, "light-control-released");
                return true;
            }
            if (!p.states.TryGetValue(b, out var s))
            {
                if (b.GoalEnemy == null || p.states.Count >= 512)
                    return true;
                s = new BotState(b, p.config!, p.session);
                p.states.Add(b, s);
            }
            var r = s.LightDiscipline;
            if (r.Fault)
                return true;
            r.CallbackAt = Time.time;
            var hand = b.Player.HandsController as Player.FirearmController;
            var weapon = hand?.Item as Weapon;
            if (!ReferenceEquals(r.Enemy, b.GoalEnemy) || !ReferenceEquals(r.Weapon, weapon))
            {
                p.EndLightDiscipline(s, "light-contact-or-weapon-changed");
                r.Weapon = weapon;
                r.Enemy = b.GoalEnemy;
            }
            // Priority/ownership gates remain immediate, even inside the short evaluation interval.
            string block = p.LightBlock(b, hand);
            if (block.Length > 0)
            {
                p.EndLightDiscipline(s, block);
                return true;
            }
            if (Time.time >= r.Next || Time.time < r.Next - .05f)
            {
                r.Next = Time.time + .05f;
                var f = p.LightFrame(b, s);
                bool dark = r.Policy.Evaluate(f, p.config!.LightDiscipline);
                if (!dark)
                {
                    r.Captured = false;
                    r.Devices.Clear();
                    r.Modes = Array.Empty<string>();
                    p.RecordLightDiscipline(s);
                    return true;
                }
                p.SuspendDevicesForAmbush(s);
                r.Captured = true;
                int deviceIndex = 0;
                foreach (var mod in weapon!.Mods)
                    if (mod.GetItemComponent<LightComponent>() is { } device)
                    {
                        if (deviceIndex == r.Devices.Count)
                        {
                            r.Devices.Add(new());
                            r.ModesDirty = true;
                        }
                        var snapshot = r.Devices[deviceIndex++];
                        if (
                            !ReferenceEquals(snapshot.Component, device)
                            || snapshot.Mode != device.SelectedMode
                        )
                            r.ModesDirty = true;
                        snapshot.Component = device;
                        snapshot.Mode = device.SelectedMode;
                    }
                if (deviceIndex < r.Devices.Count)
                {
                    r.Devices.RemoveRange(deviceIndex, r.Devices.Count - deviceIndex);
                    r.ModesDirty = true;
                }
                if (Time.time >= r.ForceAt)
                {
                    r.ForceAt = Time.time + .5f;
                    List<LightsState>? commands = null;
                    foreach (var d in r.Devices)
                        if (d.Component.IsActive)
                            (commands ??= new List<LightsState>()).Add(
                                new LightsState
                                {
                                    Id = d.Component.Item.Id,
                                    IsActive = false,
                                    LightMode = d.Mode,
                                }
                            );
                    if (commands != null)
                    {
                        long started = System.Diagnostics.Stopwatch.GetTimestamp();
                        hand!.SetLightsState(commands.ToArray());
                        r.LastActuatorMilliseconds =
                            (System.Diagnostics.Stopwatch.GetTimestamp() - started)
                            * 1000d
                            / System.Diagnostics.Stopwatch.Frequency;
                        p.ObserveLightDevices(s, true);
                    }
                }
                p.ObserveLightDevices(s, false);
                p.RecordLightDiscipline(s);
            }
            return !r.Policy.Dark;
        }
        catch (Exception ex)
        {
            if (p.states.TryGetValue(b, out var s))
            {
                if (!s.LightDiscipline.Fault)
                    p.Logger.LogWarning("L01 yielded: " + ex.GetBaseException().Message);
                s.LightDiscipline.Fault = true;
                try
                {
                    if (s.LightDiscipline.Policy.Trace != null)
                        p.ObserveLightDevices(s, false);
                }
                catch { }
                p.EndLightDiscipline(s, "light-adapter-error");
            }
            return true;
        }
    }

    private string LightBlock(BotComponent b, Player.FirearmController? hand)
    {
        if (
            hand == null
            || hand.Item is not Weapon
            || !ReferenceEquals(hand.Item, b.BotOwner.WeaponManager.CurrentWeapon)
        )
            return "light-weapon-not-in-hands";
        return "";
    }

    private LightDisciplineFrame LightFrame(BotComponent b, BotState s)
    {
        var e = b.GoalEnemy;
        var place = e?.KnownPlaces.LastKnownPlace;
        bool known = e != null && (e.Seen || e.Heard);
        string source = e?.Seen == true ? "seen" : "heard";
        string action = b.Decision.CurrentCombatDecision.ToString();
        string tactic = action != "Search" ? "holding-angle" : "";
        // Reuse the input, not the trace: Evaluate copies values synchronously.
        var f = s.LightDiscipline.Frame;
        f.Now = Time.time;
        f.Contact = e?.EnemyProfileId ?? "";
        f.Source = known ? source : "";
        f.Action = action;
        f.Tactic = tactic;
        f.Known = place != null ? DP(place.Position) : null;
        f.ContactAge = e?.TimeSinceLastKnownUpdated ?? double.NaN;
        f.Distance = e?.RealDistance ?? double.NaN;
        f.Speed = b.Player.Velocity.magnitude;
        f.Visible = e?.IsVisible == true;
        f.NightVision = b.BotOwner.NightVision.UsingNow;
        return f;
    }

    private static bool LightDevicesUnchanged(LightDisciplineRuntime r)
    {
        int count = 0;
        foreach (var mod in r.Weapon!.Mods)
        {
            if (mod.GetItemComponent<LightComponent>() is not { } device)
                continue;
            if (count >= r.Devices.Count)
                return false;
            var d = r.Devices[count++];
            if (
                !ReferenceEquals(d.Component, device)
                || device.IsActive
                || device.SelectedMode != d.Mode
            )
                return false;
        }
        return count == r.Devices.Count;
    }

    private void ObserveLightDevices(BotState s, bool requested)
    {
        var r = s.LightDiscipline;
        int active = 0;
        bool changed = r.ModesDirty || r.Modes.Length != r.Devices.Count;
        foreach (var d in r.Devices)
        {
            if (d.Component.IsActive)
                active++;
            if (d.Component.SelectedMode != d.Mode)
                changed = true;
        }
        if (changed)
        {
            r.Modes = new string[r.Devices.Count];
            for (int i = 0; i < r.Devices.Count; i++)
                r.Modes[i] =
                    r.Devices[i].Component.Item.Id + ":" + r.Devices[i].Component.SelectedMode;
            r.ModesDirty = false;
        }
        r.Policy.Observe(r.Devices.Count, active, r.Modes, requested);
        r.Policy.Trace!.Weapon = r.Weapon?.Id.ToString() ?? "";
    }

    private void SampleLightDiscipline(BotComponent b, BotState s)
    {
        var r = s.LightDiscipline;
        if (r.Policy.Trace == null || r.Policy.Trace.State == "released")
            return;
        if (!LightScope(b) || Time.time - r.CallbackAt > 1 || Time.time < r.CallbackAt)
            EndLightDiscipline(s, "light-owner-or-callback-ended");
    }

    private void EndLightDiscipline(BotState s, string reason)
    {
        var r = s.LightDiscipline;
        if (r.Policy.Trace != null && r.Policy.Trace.State != "released")
        {
            r.Policy.Clear(reason);
            RecordLightDiscipline(s, true);
        }
        else
            r.Policy.Clear(reason);
        r.Drop(); // No saved ON state is written back to another owner's weapon.
    }

    private static void LightDisciplineDispose(BotComponent __instance)
    {
        var p = Instance;
        if (p != null && p.states.TryGetValue(__instance, out var s))
            p.EndLightDiscipline(s, "light-disposed");
    }

    private void RecordLightDiscipline(BotState s, bool force = false)
    {
        var r = s.LightDiscipline;
        var t = r.Policy.Trace;
        if (t == null)
            return;
        var key = (
            t.State,
            t.Reason,
            t.Contact,
            t.Action,
            t.Tactic,
            t.Requested,
            t.ObservedOff,
            t.ActiveDevices,
            t.NightVision
        );
        if (!force && key == r.Key && (!r.Policy.Dark || Time.time < r.LogAt))
            return;
        r.Key = key;
        r.LogAt = Time.time + .5f;
        var json = Diagnostics.Serialize(
            new
            {
                schema = 1,
                session,
                sequence = ++sequence,
                feature = "L01",
                stage = "evaluated",
                decision = "LightDiscipline",
                reason = t.Reason,
                realtime = Time.realtimeSinceStartup,
                utc = DateTime.UtcNow.ToString("O"),
                configRevision = config?.Revision ?? 0,
                profileId = s.ProfileId,
                name = s.Name,
                botInstance = s.Id,
                lightDiscipline = t,
                lightActuatorMs = r.LastActuatorMilliseconds,
                lightDisciplineConfig = config?.LightDiscipline,
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
