using System;

namespace Comet.Core;

public static class FootstepAudibility
{
    public static (string Gait, float Range) GetRange(MovementHearingConfig settings, bool sprint,
        bool crouched, bool covert, float speed, float maxSpeed)
    {
        if (sprint)
            return ("sprint", (float)settings.SprintRangeMeters);
        // A zero speed can be reported during a normal walking animation transition.
        if (crouched || covert || (speed > 0 && speed <= maxSpeed * .25f))
            return ("quiet", (float)settings.QuietRangeMeters);
        return ("walk", (float)settings.WalkRangeMeters);
    }

    public static float ListenerFactor(bool hasHeadset, bool heavyHelmet, bool dyingWithoutPainkillers,
        bool sprinting, bool heavyBreath) =>
        (hasHeadset ? 1f : .6f) * (heavyHelmet ? .8f : 1f) * (dyingWithoutPainkillers ? .8f : 1f)
        * (sprinting ? .85f : 1f) * (heavyBreath ? .65f : 1f);

    public static float EnvironmentFactor(bool differentEnvironment, bool differentBunker, bool differentDepth) =>
        (differentEnvironment ? .7f : 1f) * (differentBunker ? .2f : differentDepth ? .66f : 1f);

    // One roll per eligible footfall. Match the familiar distance falloff without reading
    // the preset's hard cap, which could otherwise reject the sound before its range check.
    public static float HearingChance(float distance, float effectiveRange, bool hasHeadset)
    {
        if (!(distance >= 0 && distance <= effectiveRange && effectiveRange > 0))
            return 0;
        float near = hasHeadset ? 1f : .25f;
        if (distance <= near)
            return 1;
        float far = Math.Max(effectiveRange, hasHeadset ? 70f : 40f);
        return Math.Max(.01f, 1f - (distance - near) / (far - near));
    }
}
