using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using EFT;
using UnityEngine;

namespace Comet.Client;

// Samples the raid scene on the AI authority. No screen, local camera or network packets are needed.
internal sealed class SceneExposure
{
    private const float CacheSeconds = 0.35f;
    private const float MoveThresholdSquared = 1.5f * 1.5f;

    private sealed class Reading
    {
        internal float Next, Level = -1f;
        internal Vector3 Position;
        internal bool Queued;
        internal float RequestedAt;
        internal Vector3 RequestedEye;
    }

    private readonly struct Fixture
    {
        internal readonly Light Light;
        internal readonly CullingLightObject? Culling;
        internal readonly Vector3 Position;
        internal readonly float Radius;

        internal Fixture(Light light)
        {
            Light = light;
            Culling = light.GetComponent<CullingLightObject>();
            Position = light.transform.position;
            Radius = light.range;
        }
    }

    private static readonly Vector3[] probeDirections = { Vector3.up, Vector3.down, Vector3.forward,
        Vector3.back, Vector3.left, Vector3.right };
    private readonly Color[] probeColors = new Color[probeDirections.Length];
    private ConditionalWeakTable<Player, Reading> targets = new();
    private readonly Queue<Reading> pendingAi = new();
    private int lastAiFrame = -1;
    private List<Fixture> fixtures = new(), building = new();
    private Light[]? discovered;
    private float nextScan;
    private float lastRequest;
    private int cursor;
    private bool ready;
    internal int Samples;
    internal int AiSamples, AiCacheHits, AiDeferred;
    internal long AiSampleTicks, MaxAiSampleTicks;
    internal int PendingAi => pendingAi.Count;
    internal int FixtureCount => fixtures.Count;

    internal void RefreshFixtures(float now)
    {
        if (discovered == null)
        {
            if (now < nextScan || (ready && now - lastRequest > 10f)) return;
            discovered = UnityEngine.Object.FindObjectsByType<Light>(FindObjectsSortMode.None);
            cursor = 0;
            building.Clear();
        }
        // Keep the completed list usable while indexing newly loaded or switched fixtures.
        int end = Math.Min(discovered.Length, cursor + 48);
        while (cursor < end)
        {
            var light = discovered[cursor++];
            if (light == null || (light.type != LightType.Point && light.type != LightType.Spot) ||
                light.GetComponentInParent<Player>() != null || light.GetComponentInParent<Camera>() != null) continue;
            building.Add(new Fixture(light));
        }
        if (cursor < discovered.Length) return;
        (fixtures, building) = (building, fixtures);
        building.Clear();
        discovered = null;
        nextScan = now + 15f;
        ready = true;
    }

    internal float Read(Player target, Vector3 eye, float now, bool ai)
    {
        lastRequest = now;
        if (!ready || !(eye.sqrMagnitude > 0f) || float.IsInfinity(eye.sqrMagnitude)) return -1f;
        var reading = targets.GetOrCreateValue(target);
        bool near = (eye - reading.Position).sqrMagnitude < MoveThresholdSquared;
        bool fresh = now < reading.Next && near;
        if (ai && (!fresh || reading.Queued))
        {
            reading.RequestedAt = now;
            reading.RequestedEye = eye;
        }
        if (fresh)
        {
            if (ai) AiCacheHits++;
            return reading.Level;
        }
        if (!ai) return Sample(reading, eye, now);

        // One queue entry per target, even if several observers ask in the same frame.
        if (!reading.Queued)
        {
            reading.Queued = true;
            pendingAi.Enqueue(reading);
        }
        MeasurePendingAi(now);
        if (!reading.Queued) return reading.Level;
        AiDeferred++;
        return near && now < reading.Next + CacheSeconds ? reading.Level : -1f;
    }

    internal void MeasurePendingAi(float now)
    {
        if (pendingAi.Count == 0 || lastAiFrame == Time.frameCount) return;
        while (pendingAi.Count > 0)
        {
            var reading = pendingAi.Dequeue();
            reading.Queued = false;
            if (now - reading.RequestedAt > CacheSeconds) continue;
            lastAiFrame = Time.frameCount;
            AiSamples++;
            long start = Stopwatch.GetTimestamp();
            try { Sample(reading, reading.RequestedEye, now); }
            finally
            {
                long ticks = Stopwatch.GetTimestamp() - start;
                AiSampleTicks += ticks;
                MaxAiSampleTicks = Math.Max(MaxAiSampleTicks, ticks);
            }
            return;
        }
    }

    private float Sample(Reading reading, Vector3 eye, float now)
    {
        Samples++;
        reading.Level = Measure(eye);
        reading.Next = now + CacheSeconds;
        reading.Position = eye;
        return reading.Level;
    }

    private float Measure(Vector3 eye)
    {
        float level = -1f;
        var probes = LightmapSettings.lightProbes;
        if (probes != null && probes.count > 0)
        {
            LightProbes.GetInterpolatedProbe(eye, null, out var probe);
            probe.Evaluate(probeDirections, probeColors);
            float sum = 0f;
            foreach (var color in probeColors) sum += Mathf.Max(0f, color.grayscale);
            level = Mathf.Clamp01(sum / probeColors.Length);
        }
        var sky = TOD_Sky.Instance;
        if (sky != null && sky.Initialized)
        {
            var sun = sky.SunDirection;
            if (sun.sqrMagnitude > 0.5f)
            {
                float day = Mathf.Clamp01(sun.y * 4f);
                bool inSun = day > 0f && !Physics.Raycast(eye, sun, 1500f,
                    LayersMaskController.HighPolyWithTerrainMask, QueryTriggerInteraction.Ignore);
                level = Mathf.Max(level, inSun ? day : 0.12f * day);
            }
        }
        if (level >= 0.6f) return level;

        int rays = 0;
        foreach (var entry in fixtures)
        {
            var delta = eye - entry.Position;
            float squared = delta.sqrMagnitude;
            if (squared >= entry.Radius * entry.Radius) continue;
            var light = entry.Light;
            if (light == null || !light.gameObject.activeInHierarchy) continue;
            level = Mathf.Max(0f, level);
            // EFT fades some lights relative to the rendering camera. Read their logical switch
            // and uncropped intensity so a distant guest sees the same lighting as the host.
            var culling = entry.Culling;
            float power = culling != null
                ? (culling.IsLightEnabled ? culling.CurrentLightIntensity : 0f)
                : (light.enabled ? light.intensity : 0f);
            if (!(power > 0f)) continue;
            float distance = Mathf.Sqrt(squared);
            if (light.type == LightType.Spot && distance > 0.01f &&
                Vector3.Dot(delta / distance, light.transform.forward) < Mathf.Cos(light.spotAngle * Mathf.Deg2Rad * 0.5f)) continue;
            float falloff = 1f - distance / entry.Radius;
            float contribution = power * light.color.grayscale * falloff * falloff;
            if (!(contribution > 0.01f)) continue;
            // A crowded cluster must not generate unbounded physics work or give free concealment.
            if (++rays > 8) return -1f;
            if (Physics.Linecast(eye, entry.Position, LayersMaskController.HighPolyWithTerrainMask,
                    QueryTriggerInteraction.Ignore)) continue;
            level = Mathf.Clamp01(level + contribution);
            if (level >= 0.6f) break;
        }
        return float.IsNaN(level) || float.IsInfinity(level) ? -1f : level;
    }

    internal void ResetCounters()
    {
        Samples = AiSamples = AiCacheHits = AiDeferred = 0;
        AiSampleTicks = MaxAiSampleTicks = 0;
    }

    internal void Reset()
    {
        targets = new();
        pendingAi.Clear();
        lastAiFrame = -1;
        fixtures.Clear();
        building.Clear();
        discovered = null;
        ready = false;
        lastRequest = nextScan = 0;
        ResetCounters();
    }
}
