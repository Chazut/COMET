using System;
using System.Collections.Generic;

namespace Comet.Core;

// A default SeekCover may be a useful stationary wait. Only age out the same
// hidden contact after a continuous, idle native fallback with no new evidence.
public sealed class StalledCoverContact
{
    public const float WaitSeconds = 45f;
    public const float RefusedRouteWaitSeconds = 90f;
    private object? nativeEnemy, refusedEnemy;
    private float refusedStamp;

    private float lastNativeAt, lastObservedAt, since = -1f, contactStamp;
    private DoorPoint anchor;

    public void ObserveChoice(object enemy, float stamp, bool actionChosen, IReadOnlyDictionary<string, string> refusals)
    {
        if (actionChosen)
        {
            refusedEnemy = null;
            return;
        }
        // SAIN is queried lazily. An unevaluated option supplies no new evidence.
        if (!refusals.TryGetValue(nameof(Tactic.Search), out var search) ||
            !refusals.TryGetValue(nameof(Tactic.Flank), out var shift)) return;
        refusedEnemy = search.StartsWith("search-native-refused:", StringComparison.Ordinal) &&
            shift.StartsWith("native-shift:", StringComparison.Ordinal) ? enemy : null;
        refusedStamp = stamp;
    }

    public void NativeChoice(object? enemy, float now)
    {
        if (!ReferenceEquals(enemy, nativeEnemy) || now - lastNativeAt > 2f || now < lastNativeAt)
            since = -1f;
        if (!ReferenceEquals(enemy, refusedEnemy)) refusedEnemy = null;
        nativeEnemy = enemy;
        lastNativeAt = now;
    }

    public bool IsCurrent(object enemy, float now) =>
        ReferenceEquals(enemy, nativeEnemy) && now >= lastNativeAt && now - lastNativeAt <= 2f;

    public bool ShouldRelease(object enemy, float now, float stamp, DoorPoint position, bool idle, bool completePath = false)
    {
        if (!IsCurrent(enemy, now)) return false;
        if (now - lastObservedAt > 2f || now < lastObservedAt) since = -1f;
        lastObservedAt = now;
        if (!idle || completePath && (!ReferenceEquals(enemy, refusedEnemy) || stamp != refusedStamp))
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
        var wait = completePath ? RefusedRouteWaitSeconds : WaitSeconds;
        if (now - since < wait || now - stamp < wait) return false;
        NativeChoice(null, now);
        return true;
    }
}
