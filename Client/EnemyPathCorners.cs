using System;
using SAIN.SAINComponent.Classes.EnemyClasses;
using UnityEngine;

namespace Comet.Client;

internal static class EnemyPathCorners
{
    // SAIN reuses an oversized array. Its tail can contain zeroes or an older
    // route, and Clear() resets the count without clearing that array.
    // Consume this view synchronously; never retain it across a path update.
    internal static ReadOnlySpan<Vector3> Read(SAINEnemyPath? path)
    {
        var buffer = path?.PathCorners;
        int count = path?.PathCornerCount ?? 0;
        if (buffer == null || count <= 0 || count > buffer.Length)
            return ReadOnlySpan<Vector3>.Empty;
        return new ReadOnlySpan<Vector3>(buffer, 0, count);
    }
}
