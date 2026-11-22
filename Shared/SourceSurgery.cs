using System;

namespace Comet.Core;

// BotSurgery.CheckEnemies + CheckRelaxed from fbb86b83. No COMET cancellation radius.
public sealed class SourceSurgery
{
    private bool relaxed,
        rollHit;
    private double rollUntil;
    public string LastCheck { get; private set; } = "";

    private bool Verdict(string reason, bool allowed = false)
    {
        LastCheck = reason;
        return allowed;
    }

    public static (double chance, double quiet, double distance) Limits(string p) =>
        p switch
        {
            "Rat" => (80, 20, 30),
            "Coward" => (80, 20, 35),
            "SnappingTurtle" => (70, 20, 30),
            "Timmy" => (60, 25, 40),
            "Chad" => (35, 30, 45),
            "GigaChad" => (25, 30, 50),
            "Wreckless" => (15, 35, 50),
            _ => (50, 25, 40),
        };

    public bool Allows(
        double now,
        string personality,
        bool usingKit,
        bool usable,
        bool strictClear,
        bool inCover,
        bool visible,
        double nearest,
        double seenAge,
        double knownAge,
        double sinceHit,
        Func<double> roll,
        string unavailableReason = "native-kit-not-ready"
    )
    {
        if (!usable)
            return Verdict(unavailableReason);
        if (strictClear)
            return Verdict("strict-area-clear", true);
        var c = Limits(personality);
        if (usingKit && relaxed)
        {
            bool keep = sinceHit >= 1 && !visible && nearest >= c.distance * .5;
            if (!keep)
                relaxed = false;
            return keep ? Verdict("relaxed-in-progress", true)
                : Verdict(sinceHit < 1 ? "recent-hit" : visible ? "enemy-visible" : "enemy-too-close");
        }
        relaxed = false;
        if (visible)
            return Verdict("enemy-visible");
        if (!inCover)
            return Verdict("cover-not-reached");
        if (nearest < c.distance)
            return Verdict("enemy-too-close");
        if (seenAge < c.quiet)
            return Verdict("recent-sighting");
        if (knownAge < c.quiet)
            return Verdict("recent-contact");
        if (now >= rollUntil)
        {
            rollUntil = now + 20;
            rollHit = roll() < c.chance;
        }
        if (!rollHit)
            return Verdict("personality-roll");
        relaxed = true;
        return Verdict("relaxed-cover", true);
    }
}
