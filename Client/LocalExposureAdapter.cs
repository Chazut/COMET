using System;
using HarmonyLib;
using SAIN.SAINComponent.Classes.EnemyClasses;
using UnityEngine;

namespace Comet.Client;

public sealed partial class Plugin
{
    private readonly SceneExposure exposure = new();
    private readonly int[] exposureOutcomes = new int[(int)ExposureReason.Count];
    private int exposureRemoteChecks,
        exposureAiChecks,
        exposureAiShadow;
    private float nextExposureSummary,
        nextExposureDetail,
        retryExposureAt;

    private static void PatchLocalExposure(Harmony h)
    {
        h.Patch(
            AccessTools.Method(typeof(EnemyGainSightClass), "CalcModifier"),
            postfix: new HarmonyMethod(typeof(Plugin), nameof(ApplyLocalExposure))
            {
                priority = Priority.Last,
            }
        );
    }

    private static void ApplyLocalExposure(Enemy enemy, ref float __result)
    {
        var p = Instance;
        if (
            p?.config?.LightBasedDetection != true
            || !p.NativeFixScope(enemy.Bot)
            || enemy.EnemyPlayer == null
            || !(__result > 0f)
            || float.IsInfinity(__result)
        )
            return;

        var player = enemy.EnemyPlayer;
        bool ai = enemy.IsAI;
        var now = Time.time;
        // SAIN keeps these timestamps on the observer's own Enemy, even for a remote human.
        var info = enemy.EnemyInfo;
        var reason = ExposureResponse.Check(
            now,
            enemy.RealDistance,
            enemy.IsVisible,
            enemy.Seen,
            enemy.Vision.TimeLastSeen,
            info.LastGetHitTime,
            info.LastDoHitTime,
            enemy.Bot.BotOwner.NightVision?.UsingNow == true,
            enemy.EnemyPlayerComponent?.Flashlight?.WhiteLight == true
        );
        float level = -1f,
            factor = 1f;
        if (reason == ExposureReason.Sample)
        {
            level = p.exposure.Read(player, enemy.EnemyTransform.EyePosition, now, ai);
            factor = ExposureResponse.Multiplier(level, enemy.RealDistance);
            reason =
                level < 0f ? ExposureReason.Unknown
                : factor > 1f ? ExposureReason.Shadow
                : ExposureReason.Bright;
        }
        float original = __result;
        // SAIN divides native acquisition speed by this value. Keep its own preset and repeat-contact terms.
        float adjusted = original * factor;
        if (!float.IsInfinity(adjusted))
            __result = adjusted;
        p.exposureOutcomes[(int)reason]++;
        if (ai)
        {
            p.exposureAiChecks++;
            if (reason == ExposureReason.Shadow)
                p.exposureAiShadow++;
        }
        bool remote = !ai && p.fikaPresent && !player.IsYourPlayer;
        if (remote)
            p.exposureRemoteChecks++;
        if (now >= p.nextExposureDetail && p.config?.QuietLogging == false)
        {
            p.nextExposureDetail = now + 5f;
            p.RecordExposureDetail(
                enemy.Bot.ProfileId,
                enemy.EnemyProfileId,
                ai,
                remote,
                reason,
                level,
                factor,
                original,
                __result
            );
        }
    }

    private void RecordExposureDetail(
        string observer,
        string target,
        bool ai,
        bool remote,
        ExposureReason reason,
        float level,
        float factor,
        float nativeModifier,
        float finalModifier
    )
    {
        try
        {
            diagnostics?.Write(
                Diagnostics.Serialize(
                    new
                    {
                        schema = 1,
                        session,
                        feature = "local-exposure",
                        stage = "evaluated",
                        realtime = Time.realtimeSinceStartup,
                        observer,
                        target,
                        ai,
                        remote,
                        reason = reason.ToString(),
                        level,
                        factor,
                        nativeModifier,
                        finalModifier,
                    }
                )
            );
        }
        catch (Exception ex)
        {
            WarnFaultSink(ex);
        }
    }

    private void UpdateExposureDiagnostics()
    {
        if (config?.Enabled != true || !config.LightBasedDetection
            || (!config.EnablePmcs && !config.EnableAiPlayerScavs) || !Authority)
            return;
        float now = Time.time;
        if (now >= retryExposureAt)
        {
            try
            {
                exposure.RefreshFixtures(now);
                exposure.MeasurePendingAi(now);
            }
            catch (Exception e)
            {
                exposure.Reset();
                retryExposureAt = now + 30f;
                Logger.LogWarning("Local lighting unavailable: " + e.Message);
            }
        }
        if (now < nextExposureSummary)
            return;
        nextExposureSummary = now + 10f;
        RecordExposureSummary();
    }

    private void RecordExposureSummary()
    {
        int total = 0;
        foreach (int count in exposureOutcomes)
            total += count;
        if (total == 0 && exposure.Samples == 0)
            return;
        double tickMs = 1000d / System.Diagnostics.Stopwatch.Frequency;
        try
        {
            diagnostics?.WriteEssential(
                Diagnostics.Serialize(
                    new
                    {
                        schema = 1,
                        session,
                        feature = "local-exposure",
                        stage = "summary",
                        realtime = Time.realtimeSinceStartup,
                        checks = total,
                        remoteChecks = exposureRemoteChecks,
                        humanChecks = total - exposureAiChecks,
                        aiChecks = exposureAiChecks,
                        aiShadow = exposureAiShadow,
                        aiSamples = exposure.AiSamples,
                        aiCacheHits = exposure.AiCacheHits,
                        aiDeferred = exposure.AiDeferred,
                        aiPending = exposure.PendingAi,
                        aiSampleTotalMs = exposure.AiSampleTicks * tickMs,
                        aiSampleMaxMs = exposure.MaxAiSampleTicks * tickMs,
                        aiLightingMaxSamplesPerFrame = exposure.AiLightingFrames.MaxSamplesPerFrame,
                        aiLightingFramesOverBudget = exposure.AiLightingFrames.FramesOverBudget,
                        shadow = exposureOutcomes[(int)ExposureReason.Shadow],
                        bright = exposureOutcomes[(int)ExposureReason.Bright],
                        recentSight = exposureOutcomes[(int)ExposureReason.Contact],
                        recentHit = exposureOutcomes[(int)ExposureReason.Hit],
                        close = exposureOutcomes[(int)ExposureReason.Close],
                        nvg = exposureOutcomes[(int)ExposureReason.NightVision],
                        flashlight = exposureOutcomes[(int)ExposureReason.Light],
                        unknown = exposureOutcomes[(int)ExposureReason.Unknown],
                        samples = exposure.Samples,
                        fixtures = exposure.FixtureCount,
                    }
                )
            );
        }
        catch (Exception ex)
        {
            WarnFaultSink(ex);
        }
        Array.Clear(exposureOutcomes, 0, exposureOutcomes.Length);
        exposureRemoteChecks = exposureAiChecks = exposureAiShadow = 0;
        exposure.ResetCounters();
    }

    partial void FinishLocalExposure()
    {
        try
        {
            RecordExposureSummary();
        }
        finally
        {
            exposure.EndRaid();
            Array.Clear(exposureOutcomes, 0, exposureOutcomes.Length);
            exposureRemoteChecks = exposureAiChecks = exposureAiShadow = 0;
            nextExposureDetail = nextExposureSummary = retryExposureAt = 0;
        }
    }
}
