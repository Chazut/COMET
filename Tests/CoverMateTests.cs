using System;
using Comet.Core;

internal static class CoverMateTests
{
    internal static int Run()
    {
        int n = 0;
        void C(bool value, string why)
        {
            n++;
            if (!value)
                throw new Exception(why);
        }
        var c = new CoverMateConfig();
        var board = new CoverMateBoard();
        C(c.Duration == 12 && c.MateDistance == 20 && c.Retry == 4, "source teammate cover timing");
        C(board.Reserve("a", "healer", "operation", "sector", 0, 1, 1, c), "first guard starts");
        C(
            !board.Available("b", "healer", "new-operation", "different-sector", 1, 0, 500, c),
            "one guard per mate even across operation and sector changes"
        );
        C(
            board.Available("a", "healer", "changed", "changed", 1, 0, 500, c),
            "existing owner retains claim without lease renewal"
        );
        C(
            board.Reserve("b", "reloader", "operation", "sector", 0, 1, 500, c),
            "another teammate can have their own guard"
        );
        board.Release("a");
        C(
            board.Reserve("c", "healer", "operation", "sector", 0, 1, 501, c),
            "session release allows another guard"
        );
        board.Clear();
        C(board.Available("a", "healer", "", "", 0, 0, 0, c), "raid reset clears claims");
        return n;
    }
}
