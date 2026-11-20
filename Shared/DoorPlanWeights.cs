using System.Collections.Generic;

namespace Comet.Core;

// Kept on one bot for the raid. A declined door must not block its neighbours.
public sealed class DoorSessionCooldowns
{
    private readonly Dictionary<string, double> doors = new();
    private double nextBot;
    public bool CanProbe(double now) => now >= nextBot;
    public bool CanUse(string door, double now) => CanProbe(now) && (!doors.TryGetValue(door, out var until) || now >= until);
    public void Finish(string door, double now, bool declined)
    {
        nextBot = now + 8;
        doors[door] = now + (declined ? 25 : 60);
    }
}

public static class DoorPlanWeights
{
    public static string Pick(bool clearEnabled, bool peekEnabled, bool trapEnabled, double quiet,
        double weakness, int nearbyMates, bool hurtOrLowAmmo, double depth, bool heardAtDoor, double roll,
        double roomClearChance = 45, double gigaChadPeekChance = 55)
    {
        double clear = clearEnabled ? roomClearChance / 45 : 0;
        double peek = peekEnabled ? gigaChadPeekChance / 55 : 0;
        double trap = trapEnabled ? (100 - gigaChadPeekChance) / 45 : 0;
        if (clear > 0)
            clear += (quiet >= 10 ? 1.5 : 0) + (weakness >= .5 ? 1 : 0) + .7 * nearbyMates;
        if (hurtOrLowAmmo)
        {
            clear *= .3;
            peek *= .7;
            if (trap > 0) trap += 1.5;
        }
        if (peek > 0 && quiet >= 2 && quiet < 10 && depth < 3) peek++;
        if (trap > 0 && heardAtDoor) trap += 1.5;
        var total = clear + peek + trap;
        var draw = roll * total;
        return total <= 0 ? "" : draw < clear ? "clear" : draw < clear + peek ? "peek" : "trap";
    }
}
