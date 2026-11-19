using System.Text.Json;
using Comet.Client;
using SAIN.SAINComponent.Classes.EnemyClasses;
using UnityEngine;

internal static class AiExposureTests
{
    internal static void Run(bool fika, Action<bool, string> check)
    {
        Time.time = Time.realtimeSinceStartup = 50f;
        Time.frameCount = 50;
        TOD_Sky.Instance = new() { SunDirection = Vector3.up };
        Physics.Roof = true;
        UnityEngine.Object.Scene = [];
        var plugin = Plugin.Instance = new Plugin { fikaPresent = fika };
        plugin.Update();
        var a = new Enemy { IsAI = true };
        var b = new Enemy { IsAI = true };
        var c = new Enemy { IsAI = true };
        var secondObserver = new Enemy { IsAI = true, EnemyPlayer = a.EnemyPlayer };
        var human = new Enemy();
        const float preset = 4f;
        void Sight(Enemy enemy, float factor, string label) =>
            check(Math.Abs(Plugin.Sight(enemy, preset) - preset * factor) < 0.0001f, label);
        void NextFrame(float seconds)
        {
            Time.frameCount++;
            Time.realtimeSinceStartup = Time.time += seconds;
        }

        Sight(a, 1.28f, "first AI gets this frame's measurement");
        Sight(b, 1f, "new AI waits at native gain");
        Sight(c, 1f, "a third target cannot exceed the frame budget");
        Sight(b, 1f, "repeated request does not requeue ahead of another target");
        Sight(secondObserver, 1.28f, "different observer shares the same AI's light");
        secondObserver.Seen = true;
        secondObserver.Vision.TimeLastSeen = Time.time;
        Sight(secondObserver, 1f, "contact memory is private to the observer");
        Sight(a, 1.28f, "other observer retains the shadow effect");
        Sight(human, 1.28f, "human or Fika guest samples immediately despite the AI budget");
        plugin.Update();
        check(plugin.Samples == 2, "same-frame update spends no second AI sample");

        NextFrame(.02f);
        Sight(c, 1f, "oldest pending target goes first even if another asks first");
        Sight(b, 1.28f, "queued sample can be consumed by any observer");
        plugin.Update();
        check(plugin.Samples == 3, "one AI sample across callback and update");
        NextFrame(.02f);
        plugin.Update();
        Sight(c, 1.28f, "a queued target progresses without another sight callback");
        check(plugin.Samples == 4, "all targets share one sample each");

        NextFrame(.36f);
        Physics.Roof = false;
        Sight(b, 1f, "expired target refreshes in sunlight");
        Sight(a, 1.28f, "slightly expired stationary target can use its recent reading");
        a.EnemyTransform.EyePosition = new Vector3(20, 1, 0);
        Sight(a, 1f, "moving into a new area discards stale concealment");
        NextFrame(.02f);
        Physics.Roof = true;
        plugin.Update();
        Sight(a, 1.28f, "deferred sample uses the latest requested eye position");
        check(plugin.Samples == 6, "updated position was measured only once");
        a.Bot.BotOwner.NightVision.UsingNow = true;
        a.EnemyTransform.EyePosition = new Vector3(1, 2, 0);
        Sight(a, 1f, "NVGs preserve native detection for bot targets");
        a.Bot.BotOwner.NightVision.UsingNow = false;
        a.EnemyInfo.LastDoHitTime = Time.time;
        Sight(a, 1f, "hit memory bypasses the light sample for AI targets");
        a.EnemyInfo.LastDoHitTime = -10;

        Sight(c, 1.28f, "demand for the next frame is queued");
        NextFrame(1f);
        plugin.Update();
        check(plugin.Samples == 6, "abandoned old requests generate no scene work");
        Sight(a, 1.28f, "fresh demand can use this frame after an abandoned request");
        Sight(c, 1f, "old cached shade expires even for a stationary target");
        plugin.End();
        var summary = plugin
            .diagnostics.Lines.Select(line => JsonDocument.Parse(line).RootElement)
            .Last(line => line.GetProperty("stage").GetString() == "summary");
        int Count(string key) => summary.GetProperty(key).GetInt32();
        check(
            Count("aiSamples") == 6 && Count("samples") == 7 && Count("aiPending") == 1,
            "summary separates human and AI samples and pending work"
        );
        check(
            Count("aiLightingMaxSamplesPerFrame") == 1 && Count("aiLightingFramesOverBudget") == 0,
            "only actual AI lighting measurements count towards the frame budget in solo and on the Fika host"
        );
        check(
            Count("aiDeferred") > 0
                && Count("aiCacheHits") > 0
                && Count("aiShadow") > 0
                && Count("aiChecks") + Count("humanChecks") == Count("checks")
                && Count("humanChecks") == 1
                && Count("remoteChecks") == (fika ? 1 : 0),
            "remote human and AI counters stay distinct"
        );
        check(
            summary.GetProperty("aiSampleMaxMs").GetDouble() >= 0
                && summary.GetProperty("aiSampleTotalMs").GetDouble()
                    >= summary.GetProperty("aiSampleMaxMs").GetDouble(),
            "performance summary includes bounded per-measurement time"
        );
        plugin.Update();
        check(plugin.Samples == 0, "raid cleanup drops pending AI targets");
        if (fika)
        {
            plugin.Authority = false;
            Sight(a, 1f, "Fika replica leaves the original SAIN modifier");
            plugin.Update();
            check(plugin.Samples == 0, "Fika replica never samples or queues AI");
        }
    }

    internal static void RunCrowdedScene(Action<bool, string> check)
    {
        var sampler = new SceneExposure();
        sampler.RefreshFixtures(Time.time);
        var targets = Enumerable.Range(0, 32).Select(_ => new EFT.Player()).ToArray();
        var seenInShade = new bool[targets.Length];
        for (int frame = 0; frame < 100; frame++)
        {
            Time.frameCount++;
            Time.realtimeSinceStartup = Time.time += 1f / 60f;
            int before = sampler.AiSamples;
            for (int observer = 0; observer < 4; observer++)
            for (int i = 0; i < targets.Length; i++)
            {
                float level = sampler.Read(targets[i], Vector3.up, Time.time, true);
                seenInShade[i] |= level >= 0f && level < 0.6f;
            }
            sampler.MeasurePendingAi(Time.time);
            check(
                sampler.AiSamples - before <= 1,
                "32 targets, four observers: one AI sample per frame"
            );
            check(sampler.PendingAi < targets.Length, "waiting targets are deduplicated");
        }
        check(seenInShade.All(v => v), "late targets are served despite sustained demand");
        int pending = sampler.PendingAi;
        sampler.ResetCounters();
        sampler.MeasurePendingAi(Time.time);
        check(
            sampler.AiSamples == 0 && sampler.PendingAi == pending,
            "summary rollover preserves the frame budget and queue"
        );
        var raid = sampler.AiLightingFrames;
        check(
            raid.MaxSamplesPerFrame == 1 && raid.FramesOverBudget == 0,
            "raid totals survive diagnostic rollover under sustained demand"
        );
        // Exercise the actual sampling path if a sampler recovery allows another read in the same frame.
        for (int repeat = 0; repeat < 2; repeat++)
        {
            sampler.Reset();
            sampler.RefreshFixtures(Time.time);
            sampler.Read(targets[0], Vector3.up, Time.time, true);
        }
        check(
            raid.MaxSamplesPerFrame == 3 && raid.FramesOverBudget == 1,
            "three real measurements count one violating frame even across sampler resets"
        );
        sampler.EndRaid();
        check(
            raid.MaxSamplesPerFrame == 3
                && sampler.AiLightingFrames.MaxSamplesPerFrame == 0
                && sampler.AiLightingFrames.FramesOverBudget == 0,
            "ending the raid retains final totals for the writer and starts fresh for the next raid"
        );
    }
}
