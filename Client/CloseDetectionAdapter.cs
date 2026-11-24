using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using Comet.Core;
using HarmonyLib;
using Newtonsoft.Json;
using SAIN.Components;
using SAIN.SAINComponent.Classes.EnemyClasses;
using UnityEngine;

namespace Comet.Client;

public sealed partial class Plugin
{
    private static Func<Enemy, float> closeThird = null!,
        closeUnknown = null!;
    private static Func<EnemyVisionClass, Enemy> closeVisionEnemy = null!;
    private readonly Dictionary<string, int> closeCounters = new();
    private float nextCloseSummary,
        closeFaultUntil;
    private bool closeWarned;

    private static void PatchCloseDetection(Harmony h)
    {
        closeThird =
            (Func<Enemy, float>)
                AccessTools
                    .Method(typeof(EnemyGainSightClass), "CalcThirdPartyMod")
                    .CreateDelegate(typeof(Func<Enemy, float>));
        closeUnknown =
            (Func<Enemy, float>)
                AccessTools
                    .Method(typeof(EnemyGainSightClass), "CalcUnknownMod")
                    .CreateDelegate(typeof(Func<Enemy, float>));
        closeVisionEnemy = AccessTools.MethodDelegate<Func<EnemyVisionClass, Enemy>>(
            AccessTools.PropertyGetter(typeof(EnemyVisionClass), "Enemy")
        );
        h.Patch(
            AccessTools.Method(typeof(EnemyGainSightClass), "CalcModifier"),
            postfix: new HarmonyMethod(typeof(Plugin), nameof(CloseModifier))
        );
        h.Patch(
            AccessTools.Method(typeof(EnemyVisionClass), "UpdateVisibleState"),
            prefix: new HarmonyMethod(typeof(Plugin), nameof(CloseVisibleBefore)),
            postfix: new HarmonyMethod(typeof(Plugin), nameof(CloseVisibleAfter))
        );
    }

    private bool CloseScoped(BotComponent b) =>
        world != null
        && config?.Enabled == true
        && config.CloseDetection.Enabled
        && config.CloseDetection.ReductionPercent > 0
        && NativeAdapter.IsSupportedAi(b)
        && b.BotActive
        && !b.GameEnding
        && Authority
        && !Ghost(b.ProfileId);

    private static void CloseModifier(Enemy enemy, ref float __result)
    {
        var p = Instance;
        if (p == null)
            return;
        try
        {
            __result = p.FilterCloseSight(enemy, __result);
        }
        catch (Exception ex)
        {
            p.CloseError(ex);
        }
    }

    private float FilterCloseSight(Enemy enemy, float native)
    {
        var b = enemy.Bot;
        if (!CloseScoped(b) || Time.time < closeFaultUntil)
        {
            if (states.TryGetValue(b, out var inactive))
                inactive.CloseEvidence.Clear();
            return native;
        }
        var c = config!.CloseDetection;
        double distance = enemy.RealDistance;
        // No extra per-part work, allocation or log traffic for distant targets.
        if (
            !CloseDetection.Finite(distance)
            || distance < 0
            || distance >= c.StartDistance
            || !CloseDetection.Finite(native)
            || native <= 0
        )
            return native;
        var f = new CloseDetectionFrame
        {
            MasterEnabled = true,
            SupportedAi = true,
            Active = true,
            Authority = true,
            ValidTarget =
                enemy.EnemyPlayer != null
                && enemy.EnemyPlayer.HealthController.IsAlive
                && !Ghost(enemy.EnemyProfileId),
            Distance = distance,
            NativeModifier = native,
            Blind = b.Flashed.IsFlashed,
        };
        if (CloseDetection.Eligibility(c, f).Length == 0)
        {
            // These two native helpers only read current settings/contact state. Re-read them to
            // isolate their penalty from the native result; no rolls, rays or contact writes.
            f.ThirdParty = closeThird(enemy);
            f.Unknown = closeUnknown(enemy);
        }
        var t = CloseDetection.Evaluate(c, f);
        t.Target = enemy.EnemyProfileId;
        CountClose(t.Reason);
        if (!states.TryGetValue(b, out var s))
        {
            if (states.Count >= 512)
                return (float)t.FinalModifier;
            s = new BotState(b, config!, session);
            states.Add(b, s);
        }
        s.CloseEvidence.Observe(t.Target, Time.time, t);
        RecordClose(s, t, false);
        return (float)t.FinalModifier;
    }

    private static void CloseVisibleBefore(EnemyVisionClass __instance, out bool __state) =>
        __state = __instance.IsVisible;

    private static void CloseVisibleAfter(EnemyVisionClass __instance, bool __state)
    {
        var p = Instance;
        if (p == null || __state || !__instance.IsVisible)
            return;
        try
        {
            var enemy = closeVisionEnemy(__instance);
            if (
                !p.CloseScoped(enemy.Bot)
                || Time.time < p.closeFaultUntil
                || p.Ghost(enemy.EnemyProfileId)
            )
                return;
            if (!p.states.TryGetValue(enemy.Bot, out var s))
                return;
            var t = s.CloseEvidence.Acquired(enemy.EnemyProfileId, Time.time);
            if (t == null)
                return;
            p.CountClose(t.Reason);
            p.RecordClose(s, t, true);
        }
        catch (Exception ex)
        {
            p.CloseError(ex);
        }
    }

    private void CloseError(Exception ex)
    {
        CountClose("adapter-error");
        closeFaultUntil = Time.time + 10;
        foreach (var s in states.Values)
            s.CloseEvidence.Clear();
        if (!closeWarned)
        {
            closeWarned = true;
            Logger.LogWarning("COMET P05 keeps native vision after an error: " + ex.Message);
        }
    }

    private void CountClose(string reason)
    {
        closeCounters.TryGetValue(reason, out int n);
        closeCounters[reason] = n + 1;
    }

    private void RecordClose(BotState s, CloseDetectionTrace t, bool acquired)
    {
        // Separate lanes keep an acquisition observable even just after a calculation record.
        if (Time.time < (acquired ? s.CloseNextAcquired : s.CloseNextLog))
        {
            s.CloseCoalesced++;
            return;
        }
        if (acquired)
            s.CloseNextAcquired = Time.time + .5f;
        else
            s.CloseNextLog = Time.time + .5f;
        if (!Comet.Core.ReplayCapture.Detailed)
            return;
        var json = Diagnostics.Serialize(
            new
            {
                schema = 1,
                session,
                sequence = ++sequence,
                feature = "P05",
                stage = "evaluated",
                decision = "CloseDetection",
                reason = t.Reason,
                realtime = Time.realtimeSinceStartup,
                utc = DateTime.UtcNow.ToString("O"),
                cometVersion = CometVersion.Release,
                configRevision = config?.Revision ?? 0,
                closeConfig = config?.CloseDetection,
                profileId = s.ProfileId,
                name = s.Name,
                botInstance = s.Id,
                closeDetection = t,
                coalesced = s.CloseCoalesced,
            }
        );
        s.CloseCoalesced = 0;
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

    private void FlushCloseCounters(bool force = false)
    {
        if (!force && Time.time < nextCloseSummary)
            return;
        nextCloseSummary = Time.time + 10;
        if (closeCounters.Count == 0)
            return;
        diagnostics?.WriteEssential(
            Diagnostics.Serialize(
                new
                {
                    schema = 1,
                    session,
                    feature = "P05",
                    stage = "summary",
                    reason = "close-counters",
                    realtime = Time.realtimeSinceStartup,
                    counters = closeCounters,
                }
            )
        );
        closeCounters.Clear();
    }

    private void ClearCloseDetection()
    {
        FlushCloseCounters(true);
        foreach (var s in states.Values)
            s.CloseEvidence.Clear();
        nextCloseSummary = closeFaultUntil = 0;
        closeWarned = false;
    }
}
