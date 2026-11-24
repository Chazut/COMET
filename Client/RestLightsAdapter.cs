using System;
using System.Collections.Generic;
using System.Reflection;
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

internal sealed class RestDevice
{
    internal LightComponent Device = null!;
    internal string Name = "";
    internal int Mode;
    internal bool LastActive;
}

internal sealed class RestLightsRuntime
{
    internal readonly RestLightsPolicy Policy = new();
    internal readonly List<RestDevice> Devices = new();
    internal Weapon? Weapon;
    internal Player.FirearmController? Hand;
    internal RestLightsTrace? Trace;
    internal string Key = "";
    internal float Next,
        CallbackAt,
        LogAt,
        RequestedAt;
    internal bool Fault;

    internal void Drop()
    {
        Devices.Clear();
        Weapon = null;
        Hand = null;
    }
}

public sealed partial class Plugin
{
    private static Func<string, object>? restOrbitObjective;
    private static FieldInfo? restOrbitStatus,
        restOrbitCategory;

    private static void PatchRestLights(Harmony h)
    {
        try
        {
            var orbit = AccessTools.TypeByName("Orbit.Api.OrbitTelemetry");
            var method = orbit?.GetMethod("GetBotObjective", new[] { typeof(string) });
            if (method != null)
            {
                restOrbitStatus = method.ReturnType.GetField("Status");
                restOrbitCategory = method.ReturnType.GetField("Category");
                if (
                    restOrbitStatus?.FieldType == typeof(string)
                    && restOrbitCategory?.FieldType == typeof(string)
                )
                    restOrbitObjective =
                        (Func<string, object>)
                            Delegate.CreateDelegate(typeof(Func<string, object>), method);
            }
        }
        catch
        {
            restOrbitObjective = null;
        } // Optional ORBIT API mismatch leaves its rest owner unsupported.
        h.Patch(
            AccessTools.Method(typeof(BotLightController), nameof(BotLightController.ManualUpdate)),
            postfix: new HarmonyMethod(typeof(Plugin), nameof(RestLightsTick))
        );
        h.Patch(
            AccessTools.Method(typeof(BotComponent), nameof(BotComponent.Dispose)),
            prefix: new HarmonyMethod(typeof(Plugin), nameof(RestLightsDispose))
        );
    }

    private bool RestScope(BotComponent b) =>
        config?.Enabled == true
        && config.RestLights.Enabled
        && world != null
        && Authority
        && b != null
        && NativeAdapter.IsSupportedAi(b)
        && b.BotActive
        && !b.IsDead
        && !b.GameEnding
        && !Ghost(b.ProfileId);

    private static void RestLightsTick(BotLightController __instance, bool __runOriginal)
    {
        var p = Instance;
        var b = __instance.Bot;
        if (p == null || b == null)
            return;
        try
        {
            p.RefreshRestLights(b, __runOriginal);
        }
        catch (Exception ex)
        {
            if (p.states.TryGetValue(b, out var s))
            {
                if (!s.RestLights.Fault)
                    p.Logger.LogWarning("L03 yielded: " + ex.GetBaseException().Message);
                s.RestLights.Fault = true;
                p.EndRestLights(s, "rest-adapter-error");
            }
        }
    }

    private void RefreshRestLights(BotComponent b, bool ran)
    {
        if (!ran || !RestScope(b))
        {
            if (states.TryGetValue(b, out var old))
                EndRestLights(old, "rest-out-of-scope");
            return;
        }
        // Priority gates are checked before throttling. Native combat and urgent hands always win.
        var hand = b.Player.HandsController as Player.FirearmController;
        string block = RestBlock(b, hand);
        if (block.Length > 0)
        {
            if (states.TryGetValue(b, out var old))
                EndRestLights(old, block);
            return;
        }
        double speed = b.Player.Velocity.magnitude;
        if (double.IsNaN(speed) || double.IsInfinity(speed) || speed > config!.RestLights.MaxSpeed)
        {
            if (states.TryGetValue(b, out var moving))
                EndRestLights(moving, "rest-moving");
            return;
        }
        var layer = b.BotOwner.Brain?.ActiveLayerName() ?? "";
        if (states.TryGetValue(b, out var known) && known.RestLights.Fault)
            return;
        if (
            known != null
            && Time.time < known.RestLights.Next
            && Time.time >= known.RestLights.CallbackAt
            && known.RestLights.Trace?.Layer == layer
            && ReferenceEquals(known.RestLights.Hand, hand)
            && ReferenceEquals(known.RestLights.Weapon, hand!.Item)
        )
            return;
        string behavior = "idleOutOfCombat";
        if (known == null)
        {
            if (states.Count >= 512)
                return;
            known = new BotState(b, config!, session);
            states.Add(b, known);
        }
        var s = known;
        var r = s.RestLights;
        var c = config!.RestLights;
        var weapon = hand!.Item;
        r.CallbackAt = Time.time;
        r.Next = Time.time + .25f;
        string context = "idleOutOfCombat";
        r.Hand = hand;
        r.Weapon = weapon;
        bool ready = r.Policy.Update(Time.time, context, true, c);
        r.Trace ??= new();
        var t = r.Trace;
        t.Layer = layer;
        t.Owner = layer == "OrbitBrainLayer" ? "ORBIT" : "EFT";
        t.Behavior = behavior;
        t.Weapon = weapon.Id.ToString();
        t.Speed = speed;
        t.Episode = r.Policy.Episode;
        t.StillFor = Math.Max(0, Time.time - r.Policy.Since);
        t.WaitRemaining = Math.Max(0, c.HoldDelay - t.StillFor);
        t.CooldownRemaining = Math.Max(0, r.Policy.NextEpisode - Time.time);
        t.ValidFor = .75;
        if (!r.Policy.Consumed)
        {
            t.State = "waiting";
            t.Reason = t.WaitRemaining > 0 ? "rest-stabilizing" : "rest-cooldown";
            if (ready)
                BeginRestExtinction(b, s, hand, weapon);
        }
        else
            ObserveRestExtinction(s, weapon);
        RecordRestLights(s);
    }

    private string RestBlock(BotComponent b, Player.FirearmController? hand)
    {
        if (b.SAINLayersActive || b.GoalEnemy?.IsVisible == true)
            return "rest-combat";
        if (hand?.Item == null)
            return "rest-no-firearm";
        return "";
    }

    private void BeginRestExtinction(
        BotComponent b,
        BotState s,
        Player.FirearmController hand,
        Weapon weapon
    )
    {
        var r = s.RestLights;
        var t = r.Trace!;
        r.Policy.Consume(Time.time, config!.RestLights);
        t.CooldownRemaining = config.RestLights.EpisodeInterval;
        // Reuse L02's bounded cache and localized names, but not its repeated retry loop.
        var cache = DeviceCache(s, weapon);
        if (cache.Unsupported)
        {
            t.State = "yielded";
            t.Reason = "rest-unsupported-accessories";
            return;
        }
        r.Devices.Clear();
        var commands = new List<LightsState>();
        foreach (var e in cache.Entries)
        {
            var d = e.Component;
            r.Devices.Add(
                new()
                {
                    Device = d,
                    Name = e.Name,
                    Mode = d.SelectedMode,
                    LastActive = d.IsActive,
                }
            );
            if (d.IsActive)
                commands.Add(
                    new()
                    {
                        Id = d.Item.Id,
                        IsActive = false,
                        LightMode = d.SelectedMode,
                    }
                );
        }
        t.Requested = commands.Count > 0;
        t.Accepted = false;
        r.RequestedAt = Time.time;
        if (t.Requested)
        {
            // L02's previous confirmed ON is now intentionally obsolete, rather than an external change.
            PauseTacticalDevices(s, "devices-rest-handoff");
            try
            {
                t.Accepted = hand.SetLightsState(commands.ToArray());
                t.Execution = t.Accepted
                    ? "Native OFF accepted; awaiting observation"
                    : "Native OFF refused";
            }
            catch (Exception ex)
            {
                t.Execution = "Native OFF threw: " + ex.GetBaseException().GetType().Name;
            }
        }
        t.State = "pending";
        ObserveRestExtinction(s, weapon);
    }

    private void ObserveRestExtinction(BotState s, Weapon weapon)
    {
        var r = s.RestLights;
        var t = r.Trace!;
        if (t.State == "yielded")
            return;
        int count = 0;
        bool changed = false;
        foreach (var mod in weapon.Mods)
        {
            var d = mod.GetItemComponent<LightComponent>();
            if (d == null)
                continue;
            if (
                count >= r.Devices.Count
                || !ReferenceEquals(d, r.Devices[count].Device)
                || d.SelectedMode != r.Devices[count].Mode
            )
            {
                changed = true;
                break;
            }
            // OFF -> ON after capture means another owner wants this accessory. Never fight it.
            if (!r.Devices[count].LastActive && d.IsActive)
                changed = true;
            r.Devices[count].LastActive = d.IsActive;
            count++;
        }
        changed |= count != r.Devices.Count;
        t.Devices = r.Devices.Count;
        t.ActiveDevices = 0;
        t.Accessories = new RestLightAccessory[t.Devices];
        for (int i = 0; i < t.Devices; i++)
        {
            var e = r.Devices[i];
            if (e.Device.IsActive)
                t.ActiveDevices++;
            t.Accessories[i] = new()
            {
                Id = e.Device.Item.Id.ToString(),
                Name = e.Name,
                Mode = e.Device.SelectedMode,
                Active = e.Device.IsActive,
            };
        }
        t.ObservedOff = t.Devices > 0 && t.ActiveDevices == 0;
        if (changed)
        {
            t.State = "yielded";
            t.Reason = "rest-external-change";
            t.Execution = "Another accessory owner or equipment change; no further OFF";
            r.Devices.Clear();
        }
        else if (t.Devices == 0)
        {
            t.State = "no-device";
            t.Reason = "rest-no-device";
        }
        else if (t.ObservedOff)
        {
            t.State = "dark";
            t.Reason = t.Requested ? "rest-switch-off-confirmed" : "rest-already-dark";
            t.Execution = t.Requested
                ? "OFF observed after native request"
                : "Already OFF; no command";
        }
        else if (Time.time - r.RequestedAt >= config!.TacticalDevices.ConfirmationTimeout)
        {
            t.State = "yielded";
            t.Reason = "rest-off-unconfirmed";
            t.Execution = "OFF not confirmed; no retry during this pause";
            r.Devices.Clear();
        }
        else
        {
            t.State = "pending";
            t.Reason = "rest-awaiting-confirmation";
        }
    }

    private void EndRestLights(BotState s, string reason)
    {
        var r = s.RestLights;
        r.Policy.Release();
        r.Drop();
        r.Next = 0;
        if (r.Trace != null && r.Trace.State != "released")
        {
            r.Trace.State = "released";
            r.Trace.Reason = reason;
            r.Trace.ValidFor = 0;
            RecordRestLights(s);
        }
        r.Trace = null;
    }

    private void SampleRestLights(BotComponent b, BotState s)
    {
        var r = s.RestLights;
        if (r.Trace == null)
            return;
        try
        {
            if (
                !RestScope(b)
                || Time.time - r.CallbackAt > .75f
                || Time.time < r.CallbackAt
                || RestBlock(b, b.Player.HandsController as Player.FirearmController).Length > 0
            )
                EndRestLights(s, "rest-owner-ended");
        }
        catch
        {
            EndRestLights(s, "rest-owner-unavailable");
        }
    }

    private static void RestLightsDispose(BotComponent __instance)
    {
        var p = Instance;
        if (p != null && p.states.TryGetValue(__instance, out var s))
            p.EndRestLights(s, "rest-disposed");
    }

    private void RecordRestLights(BotState s)
    {
        if (!Comet.Core.ReplayCapture.Detailed)
            return;
        var r = s.RestLights;
        var t = r.Trace;
        if (t == null)
            return;
        string key =
            t.State
            + ":"
            + t.Reason
            + ":"
            + t.Behavior
            + ":"
            + t.Weapon
            + ":"
            + t.Episode
            + ":"
            + t.Requested
            + ":"
            + t.Accepted
            + ":"
            + t.ActiveDevices;
        if (key == r.Key && (t.ValidFor == 0 || Time.time < r.LogAt))
            return;
        r.Key = key;
        r.LogAt = Time.time + .5f;
        var json = Diagnostics.Serialize(
            new
            {
                schema = 1,
                session,
                sequence = ++sequence,
                feature = "L03",
                stage = "evaluated",
                decision = "RestLights",
                reason = t.Reason,
                realtime = Time.realtimeSinceStartup,
                utc = DateTime.UtcNow.ToString("O"),
                configRevision = config?.Revision ?? 0,
                profileId = s.ProfileId,
                name = s.Name,
                botInstance = s.Id,
                restLights = t,
                restLightsConfig = config?.RestLights,
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
