namespace Comet.Core;

// One instance per observer/contact. The native aim and friendly-fire checks still own the trigger.
internal sealed class OpeningShot
{
    internal const float FreshSeconds = 1.5f;
    internal float SettleSeconds { get; }
    private readonly float deadline;
    private readonly float sightStart;
    private float readySince = -1f;
    private float authorizedAt = -1f;
    internal bool Finished { get; private set; }
    internal bool Head { get; }

    internal OpeningShot(float now, float sightStart, bool head, float distance)
    {
        // Pick once at acquisition: one second up close, two at 100 m and beyond.
        SettleSeconds = 1f + System.Math.Clamp((distance - 10f) / 90f, 0f, 1f);
        deadline = now + FreshSeconds + SettleSeconds;
        this.sightStart = sightStart;
        Head = head;
    }

    internal static bool LowThreat(float facingObserver, bool underPressure) =>
        !underPressure && facingObserver < .75f;

    internal bool Continue(float now, float visibleSince, bool eligible)
    {
        if (!eligible || now > deadline || visibleSince != sightStart)
            Finish();
        return !Finished;
    }

    internal bool AllowTrigger(float now, bool nativeReady, bool aligned)
    {
        if (Finished)
            return nativeReady;
        if (!nativeReady)
        {
            readySince = -1f;
            return false;
        }
        if (readySince < 0f)
            readySince = now;
        if (!aligned || now - readySince < SettleSeconds)
            return false;
        authorizedAt = now;
        return true;
    }

    // SAIN stamps LastTriggerPressd when it actually presses the trigger. A refused request,
    // a later native burst or another weapon must never be cut short.
    internal bool OwnsTrigger(float triggerAt) =>
        !Finished && authorizedAt >= 0f && triggerAt == authorizedAt;

    internal bool ObserveShot(float triggerAt)
    {
        if (!OwnsTrigger(triggerAt))
            return false;
        Finish();
        return true;
    }

    internal void Finish() => Finished = true;
}
