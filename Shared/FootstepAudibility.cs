using System;

namespace Comet.Core;

public static class FootstepAudibility
{
    // Only called for AI footfalls that actually played. EFT mixes sprint and walk
    // samples differently, so the playback gain is a poor measure of AI hearing range.
    public static string? Apply(MovementHearingConfig settings, bool sprint, bool crouched,
        bool covert, float speed, float maxSpeed, float walkBaseRange, ref float range, ref float volume)
    {
        if (!settings.GaitBasedRange || !(volume > 0) || float.IsInfinity(volume)
            || !(range > 0) || float.IsInfinity(range) || !(walkBaseRange > 0) || float.IsInfinity(walkBaseRange))
            return null;

        float walkRange = walkBaseRange * (float)settings.WalkRangeMultiplier;
        volume = 1f; // Weather and SAIN's global footstep volume are applied after this.
        if (sprint)
        {
            range = Math.Max(walkBaseRange, range) * (float)(settings.WalkRangeMultiplier * settings.SprintRelativeToWalk);
            return "sprint";
        }

        // A zero speed can be reported during an animation transition. Only a positive
        // slow pace or an explicit quiet pose marks an audible step as sneaking.
        if (crouched || covert || (speed > 0 && speed <= maxSpeed * .25f))
        {
            range = walkRange * (float)settings.QuietFractionOfWalk;
            return "quiet";
        }

        range = walkRange;
        return "walk";
    }
}
