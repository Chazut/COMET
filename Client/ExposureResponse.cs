using System;

namespace Comet.Client;

internal enum ExposureReason { Sample, Contact, Hit, Close, NightVision, Light, Unknown, Bright, Shadow, Count }

internal static class ExposureResponse
{
    private static bool Within(float now, float timestamp, float seconds) =>
        timestamp >= 0f && now >= timestamp && now - timestamp <= seconds;

    internal static ExposureReason Check(float now, float distance, bool visible, bool seen, float lastSeen,
        float lastReceivedHit, float lastDealtHit, bool nightVision, bool whiteLight)
    {
        if (!(distance > 6f) || float.IsInfinity(distance)) return ExposureReason.Close;
        if (visible || (seen && Within(now, lastSeen, 10f))) return ExposureReason.Contact;
        if (Within(now, lastReceivedHit, 8f) || Within(now, lastDealtHit, 8f)) return ExposureReason.Hit;
        if (nightVision) return ExposureReason.NightVision;
        if (whiteLight) return ExposureReason.Light;
        return ExposureReason.Sample;
    }

    internal static float Multiplier(float light, float distance)
    {
        if (!(light >= 0f && light < 0.6f) || !(distance > 6f)) return 1f;
        float shadow = 1f - light / 0.6f;
        return 1f + 0.35f * shadow * Math.Min(1f, (distance - 6f) / 24f);
    }
}
