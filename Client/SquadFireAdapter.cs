using System;
using Comet.Core;
using EFT;
using HarmonyLib;
using SAIN.Components;
using SAIN.Preset.Shared.Enums;
using SAIN.SAINComponent.Classes;
using UnityEngine;

namespace Comet.Client;

internal sealed class SquadFireRuntime
{
    internal readonly SquadFirePolicy Policy = new();
    internal readonly ShotGeometryRuntime Surfaces = new();
    internal float Callback = -100,
        LogAt = -100;
    internal string LogKey = "";
    internal bool Fault;
}

public sealed partial class Plugin
{
    private static void PatchSquadFire(Harmony h)
    {
        h.Patch(
            AccessTools.Method(typeof(SAINFriendlyFireClass), "CheckFriendlyFire"),
            prefix: new HarmonyMethod(typeof(Plugin), nameof(SquadFireCheck))
        );
        h.Patch(
            AccessTools.Method(
                typeof(SAINFriendlyFireClass),
                nameof(SAINFriendlyFireClass.ManualUpdate)
            ),
            prefix: new HarmonyMethod(typeof(Plugin), nameof(SquadFireUpdate))
        );
    }

    private bool SquadFireScope(BotComponent b) =>
        config?.Enabled == true
        && config.SquadFire.Enabled
        && Authority
        && world != null
        && b != null
        && NativeAdapter.IsSupportedAi(b)
        && b.BotActive
        && !b.IsDead
        && !b.GameEnding
        && !Ghost(b.ProfileId)
        && b.Squad?.Members?.Count > 1;

    // Source TeammateCrossing: predicted endpoint, fixed body heights, no sweep, solid test, budget veto or clear delay.
    private static bool SquadFireCheck(
        Vector3 weaponFirePort,
        float distance,
        Vector3 weaponPointDirection,
        BotComponent bot,
        ref FriendlyFireStatus __result
    )
    {
        var p = Instance;
        if (p == null || !p.SquadFireScope(bot))
            return true;
        BotState? s = null;
        try
        {
            if (!p.states.TryGetValue(bot, out s))
            {
                s = new BotState(bot, p.config!, p.session);
                p.states.Add(bot, s);
            }
            var r = s.SquadFire;
            if (r.Fault)
                return true;
            r.Callback = Time.time;
            var f = new SquadFireFrame
            {
                Now = Time.time,
                Fresh = true,
                Muzzle = DP(weaponFirePort),
                End = DP(weaponFirePort + weaponPointDirection.normalized * distance),
            };
            if (weaponPointDirection.sqrMagnitude >= .0001f)
                foreach (var mate in bot.Squad.Members.Values)
                {
                    if (
                        mate == null
                        || ReferenceEquals(mate, bot)
                        || mate.IsDead
                        || mate.Player == null
                    )
                        continue;
                    var predicted =
                        mate.Position
                        + mate.Player.Velocity * (float)p.config!.SquadFire.Prediction;
                    foreach (float height in new[] { .5f, 1f, 1.5f })
                    {
                        var point = predicted + Vector3.up * height;
                        if (
                            !SquadFireGeometry.Crosses(
                                f.Muzzle,
                                f.End,
                                DP(mate.Position + Vector3.up * height),
                                DP(point),
                                p.config.SquadFire.PredictionRadius
                            )
                        )
                            continue;
                        f.Risk = true;
                        f.Ally = mate.ProfileId;
                        f.AllyName = mate.Player.Profile.Nickname;
                        f.Kind = "prediction";
                        f.Position = DP(mate.Position);
                        f.Predicted = DP(predicted);
                        f.Distance = Vector3.Dot(
                            point - weaponFirePort,
                            weaponPointDirection.normalized
                        );
                        f.Speed = mate.Player.Velocity.magnitude;
                        break;
                    }
                    if (f.Risk)
                        break;
                }
            bool block = r.Policy.Tick(f, p.config!.SquadFire);
            if (block)
            {
                __result = FriendlyFireStatus.FriendlyBlock;
                r.Policy.Applied(false, false, true, p.config.SquadFire);
            }
            p.RecordSquadFire(s);
            return !block;
        }
        catch (Exception ex)
        {
            if (s != null)
                p.SquadFireError(s, ex);
            return true;
        }
    }

    private static void SquadFireUpdate(SAINFriendlyFireClass __instance)
    {
        var p = Instance;
        var b = __instance.Bot;
        if (p == null || !p.SquadFireScope(b) || b.BotOwner.ShootData?.Shooting != true)
            return;
        try
        {
            float d = b.BotOwner.AimingManager?.CurrentAiming?.LastDist2Target ?? 50;
            __instance.UpdateFriendlyFireStatus(
                Mathf.Max(d, 5),
                b.Transform.WeaponData.FirePort,
                b.Transform.WeaponData.PointDirection,
                b
            );
        }
        catch (Exception ex)
        {
            if (p.states.TryGetValue(b, out var s))
                p.SquadFireError(s, ex);
        }
    }

    private void SampleSquadFire(BotComponent b, BotState s)
    {
        if (
            s.SquadFire.Policy.Blocking
            && (!SquadFireScope(b) || Time.time - s.SquadFire.Callback > .25f)
        )
            EndSquadFire(s, "squad-fire-scope-or-callback-ended");
    }

    private void EndSquadFire(BotState s, string reason)
    {
        if (!s.SquadFire.Policy.Blocking)
            return;
        s.SquadFire.Policy.End(reason);
        GeometryClearBuffers(s.SquadFire.Surfaces);
        RecordSquadFire(s, true);
    }

    private void SquadFireError(BotState s, Exception ex)
    {
        var r = s.SquadFire;
        if (!r.Fault)
            Logger.LogWarning("S08 yielded: " + ex.GetBaseException().Message);
        r.Fault = true;
        EndSquadFire(s, "squad-fire-adapter-error");
    }

    private void RecordSquadFire(BotState s, bool force = false)
    {
        var r = s.SquadFire;
        var t = r.Policy.Trace;
        if (t == null)
            return;
        string key = t.State + ":" + t.Reason + ":" + t.Ally + ":" + t.Execution;
        if (!force && key == r.LogKey && (!r.Policy.Blocking || Time.time - r.LogAt < .1f))
            return;
        r.LogKey = key;
        r.LogAt = Time.time;
        var json = Diagnostics.Serialize(
            new
            {
                schema = 1,
                session,
                sequence = ++sequence,
                feature = "S08",
                stage = "evaluated",
                decision = "SquadFire",
                reason = t.Reason,
                realtime = Time.realtimeSinceStartup,
                utc = DateTime.UtcNow.ToString("O"),
                configRevision = config?.Revision ?? 0,
                profileId = s.ProfileId,
                name = s.Name,
                botInstance = s.Id,
                squadFire = t,
                squadFireConfig = config?.SquadFire,
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
