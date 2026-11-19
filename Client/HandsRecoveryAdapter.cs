using System;
using Comet.Core;
using EFT;
using EFT.InventoryLogic;
using HarmonyLib;
using Newtonsoft.Json;
using SAIN;
using SAIN.Components;
using UnityEngine;

namespace Comet.Client;

internal sealed class HandsRecoveryRuntime
{
    internal readonly HandsRecoveryPolicy Policy = new();
    internal object? Hands,
        Operation,
        Reload,
        Transaction,
        Magazine;
    internal double Next,
        LogAt = -1000;
    internal int Generation,
        Loaded = -1;
    internal bool Issuing,
        Fault;
    internal string LogKey = "";
    internal string NativePhase = "";

    internal void Drop()
    {
        Hands = Operation = Reload = Transaction = Magazine = null;
        Loaded = -1;
        Issuing = false;
    }
}

public sealed partial class Plugin
{
    private static void PatchHandsRecovery(Harmony h)
    {
        h.Patch(
            AccessTools.Method(typeof(BotComponent), nameof(BotComponent.ManualUpdate)),
            postfix: new HarmonyMethod(typeof(Plugin), nameof(TrackHandsRecovery))
        );
    }

    private bool HandsRecoveryScope(BotComponent b) =>
        config?.Enabled == true
        && config.HandsRecovery.Enabled
        && NativeAdapter.IsSupportedAi(b)
        && b.BotActive
        && !b.IsDead
        && !b.GameEnding
        && Authority
        && !Ghost(b.ProfileId);

    private static void TrackHandsRecovery(BotComponent __instance)
    {
        var p = Instance;
        if (p == null)
            return;
        try
        {
            if (p.HandsRecoveryScope(__instance) && !p.states.ContainsKey(__instance))
                p.states.Add(__instance, new BotState(__instance, p.config!, p.session));
        }
        catch (Exception ex)
        {
            if (!p.handsEnrollmentWarned)
            {
                p.handsEnrollmentWarned = true;
                p.Logger.LogWarning("W10 enrollment yielded: " + ex.Message);
            }
        }
    }

    private bool handsEnrollmentWarned;

    private HandsRecoveryFrame ReadHandsRecovery(BotComponent b, BotState s)
    {
        var gun = b.Player.HandsController as Player.FirearmController;
        return new HandsRecoveryFrame
        {
            Now = Time.timeAsDouble,
            Kind = "reload",
            Held = gun?.Item?.Id.ToString() ?? "",
            ReloadFlag = b.BotOwner.WeaponManager?.Reload?.Reloading == true,
            NativeReload = gun?.IsInReloadOperation() == true,
            Operation = gun?.CurrentOperation?.GetType().Name ?? "no-firearm",
        };
    }

    private void SampleHandsRecovery(BotComponent b, BotState s)
    {
        var r = s.HandsRecovery;
        if (r.Fault || !HandsRecoveryScope(b))
        {
            EndHandsRecovery(s, "hands-out-of-scope");
            return;
        }
        double now = Time.timeAsDouble;
        if (now < r.Next && now >= r.Next - config!.HandsRecovery.CheckSeconds)
            return;
        r.Next = now + config!.HandsRecovery.CheckSeconds;
        var f = ReadHandsRecovery(b, s);
        string command = r.Policy.Observe(f, config.HandsRecovery);
        if (command.Length == 0)
        {
            RecordHandsRecovery(s);
            return;
        }
        var fresh = ReadHandsRecovery(b, s);
        if (!HandsRecoveryScope(b) || !fresh.ReloadFlag || fresh.NativeReload)
        {
            r.Policy.Observe(fresh, config.HandsRecovery);
            RecordHandsRecovery(s, true);
            return;
        }
        r.Policy.Request(now);
        b.BotOwner.WeaponManager.Reload.Reloading = false;
        bool accepted = !b.BotOwner.WeaponManager.Reload.Reloading;

        r.Policy.Result(accepted, now, config.HandsRecovery);
        RecordHandsRecovery(s, true);
    }

    private void EndHandsRecovery(BotState s, string reason)
    {
        var r = s.HandsRecovery;
        bool active = r.Policy.Trace?.ValidFor > 0;
        r.Policy.End(reason);
        r.Drop();
        if (active)
            RecordHandsRecovery(s, true);
    }

    private void HandsRecoveryError(BotState s, Exception ex)
    {
        var r = s.HandsRecovery;
        bool warn = !r.Fault;
        r.Fault = true;
        try
        {
            EndHandsRecovery(s, "hands-adapter-error");
        }
        catch
        {
            r.Drop();
            r.Policy.End("hands-adapter-error");
        }
        if (warn)
            Logger.LogWarning("W10 yielded to native hands: " + ex.GetBaseException().Message);
    }

    private void RecordHandsRecovery(BotState s, bool force = false)
    {
        var r = s.HandsRecovery;
        var t = r.Policy.Trace;
        if (t == null)
            return;
        double now = Time.timeAsDouble;
        string key =
            t.State
            + "|"
            + t.Reason
            + "|"
            + t.Attempts
            + "|"
            + t.NativePhase
            + "|"
            + t.ReloadIdle
            + "|"
            + t.SwitchCompleted;
        if (
            !force
            && key == r.LogKey
            && (t.ValidFor == 0 || now - r.LogAt < (r.Policy.Pending ? 1 : 5))
        )
            return;
        r.LogKey = key;
        r.LogAt = now;
        var json = Diagnostics.Serialize(
            new
            {
                schema = 1,
                session,
                sequence = ++sequence,
                feature = "W10",
                stage = "evaluated",
                decision = "HandsRecovery",
                reason = t.Reason,
                realtime = Time.realtimeSinceStartup,
                utc = DateTime.UtcNow.ToString("O"),
                configRevision = config?.Revision ?? 0,
                profileId = s.ProfileId,
                name = s.Name,
                botInstance = s.Id,
                handsRecovery = t,
                handsRecoveryConfig = config?.HandsRecovery,
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
