namespace Comet.Core;

// A default SeekCover may be a useful stationary wait. Only age out the same
// unreachable contact after a continuous, idle native fallback with no new evidence.
public sealed class StalledCoverContact
{
    public const float WaitSeconds = 45f;
    private object? nativeEnemy;
    private float lastNativeAt, lastObservedAt, since = -1f, contactStamp;
    private DoorPoint anchor;

    public void NativeChoice(object? enemy, float now)
    {
        if (!ReferenceEquals(enemy, nativeEnemy) || now - lastNativeAt > 2f || now < lastNativeAt)
            since = -1f;
        nativeEnemy = enemy;
        lastNativeAt = now;
    }

    public bool IsCurrent(object enemy, float now) =>
        ReferenceEquals(enemy, nativeEnemy) && now >= lastNativeAt && now - lastNativeAt <= 2f;

    public bool ShouldRelease(object enemy, float now, float stamp, DoorPoint position, bool idle)
    {
        if (!IsCurrent(enemy, now)) return false;
        if (now - lastObservedAt > 2f || now < lastObservedAt) since = -1f;
        lastObservedAt = now;
        if (!idle)
        {
            since = -1f;
            return false;
        }
        if (since < 0f || stamp != contactStamp || (position - anchor).Length > .5)
        {
            since = now;
            contactStamp = stamp;
            anchor = position;
        }
        if (now - since < WaitSeconds || now - stamp < WaitSeconds) return false;
        NativeChoice(null, now);
        return true;
    }
}
