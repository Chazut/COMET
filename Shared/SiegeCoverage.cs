using System;

namespace Comet.Core;

internal static class SiegeCoverage
{
    // Rows are distinct map exits. Columns are available SAIN squad members.
    // Reassign an earlier match when its member is the only option for a later exit.
    internal static int[]? Assign(bool[,] visible)
    {
        int exits = visible.GetLength(0),
            members = visible.GetLength(1);
        if (exits < 2 || exits > members)
            return null;
        var memberExit = new int[members];
        Array.Fill(memberExit, -1);
        bool Match(int exit, bool[] visited)
        {
            for (int member = 0; member < members; member++)
            {
                if (visited[member] || !visible[exit, member])
                    continue;
                visited[member] = true;
                if (memberExit[member] >= 0 && !Match(memberExit[member], visited))
                    continue;
                memberExit[member] = exit;
                return true;
            }
            return false;
        }
        for (int exit = 0; exit < exits; exit++)
            if (!Match(exit, new bool[members]))
                return null;
        var watchers = new int[exits];
        for (int member = 0; member < members; member++)
            if (memberExit[member] >= 0)
                watchers[memberExit[member]] = member;
        return watchers;
    }

    internal static double WatchTime(SecondaryEntranceConfig settings, string leaderPersonality)
    {
        double patience = 1 - settings.Chance(leaderPersonality) / 100;
        return settings.WatchMin
            + patience * (settings.WatchMax + settings.Extension - settings.WatchMin);
    }
}
