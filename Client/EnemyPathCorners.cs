using System;
using SAIN.SAINComponent.Classes.EnemyClasses;
using UnityEngine;

namespace Comet.Client;

internal static class EnemyPathCorners
{
    internal static double OpportunityLength(SAINEnemyPath? path)
    {
        var corners = Read(path);
        if (corners.Length > 128) return 0;
        double length = 0;
        for (int i = 1; i < corners.Length; i++)
            length += Vector3.Distance(corners[i - 1], corners[i]);
        return length;
    }

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
