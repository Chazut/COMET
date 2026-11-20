using System.Collections.Generic;

namespace Comet.Core;

public enum DoorSessionEnd
{
    Ordinary,
    GoalEnemyChanged,
    EnemySpotted,
    RushInstead,
    Interrupted,
    ResumableGoalChange,
    MoveTimeout,
    Stuck,
    NoPath,
    DoorDidNotOpen,
    OpenFailed,
}

// Kept on one bot for the raid. A declined door must not block its neighbours.
public sealed class DoorSessionCooldowns
{
    private readonly Dictionary<string, double> doors = new();
    private double nextBot;
    private string? declinedDoor;
    public bool CanProbe(double now) => now >= nextBot;
    private bool DoorReady(string door, double now) => !doors.TryGetValue(door, out var until) || now >= until;
    public bool CanUse(string door, double now) => CanProbe(now) && DoorReady(door, now);
    public bool CanProbeHold(double now) => declinedDoor != null || CanProbe(now);
    public bool CanUseHold(string door, double now) => CanProbeHold(now) && (door == declinedDoor || DoorReady(door, now));
    public void Finish(string door, double now, bool declined)
    {
        Set(door, now, declined ? 25 : 60, 8);
        if (declined) declinedDoor = door;
    }

    public (double SameDoor, double OtherDoor) FinishSession(string door, double started, double now, DoorSessionEnd reason)
    {
        if (reason is DoorSessionEnd.MoveTimeout or DoorSessionEnd.Stuck or DoorSessionEnd.NoPath
            or DoorSessionEnd.DoorDidNotOpen or DoorSessionEnd.OpenFailed)
            return FinishFailure(door, now);
        if (reason == DoorSessionEnd.ResumableGoalChange)
            return Set(door, now, 5, 3);
        if (now - started < 1.5 && reason is DoorSessionEnd.GoalEnemyChanged
            or DoorSessionEnd.EnemySpotted or DoorSessionEnd.RushInstead or DoorSessionEnd.Interrupted)
            return Set(door, now, 8, 3);
        return Set(door, now, 60, 8);
    }

    public (double SameDoor, double OtherDoor) FinishFailure(string door, double now) => Set(door, now, 60, 30);

    // Only terminal outcomes of an issued movement or opening attempt qualify.
    public static DoorSessionEnd FailureReason(string reason, bool doorOpen) => reason switch
    {
        "move-timeout" or "peek-move-timeout" or "run-by-move-timeout" or "step-move-timeout"
            or "door-approach-timeout" or "ambush-approach-timeout" or "support-move-timeout"
            or "entry-approach-timeout" or "entry-crossing-timeout" => DoorSessionEnd.MoveTimeout,
        "move-stalled" or "peek-move-stalled" or "run-by-move-stalled" or "step-move-stalled"
            or "support-move-stuck" => DoorSessionEnd.Stuck,
        "door-mover-refused" or "ambush-mover-refused" or "ambush-movement-refused"
            or "peek-native-movement-refused" or "peek-return-movement-refused" or "peek-return-no-path"
            or "recon-native-movement-refused" or "run-by-native-movement-refused"
            or "run-by-return-movement-refused" or "run-by-return-no-path"
            or "step-movement-refused" or "step-route-invalidated"
            or "clear-native-move-refused" or "clear-entry-recheck:clear-no-path"
            or "support-move-refused" or "resume-move-refused"
            or "entry-approach-refused" or "entry-crossing-refused" => DoorSessionEnd.NoPath,
        "peek-door-did-not-open" or "recon-door-did-not-open" or "clear-open-timeout"
            when !doorOpen => DoorSessionEnd.DoorDidNotOpen,
        "peek-native-open-refused" or "recon-native-open-refused" or "clear-native-open-refused"
            => DoorSessionEnd.OpenFailed,
        _ => DoorSessionEnd.Ordinary,
    };

    private (double SameDoor, double OtherDoor) Set(string door, double now, double sameDoor, double otherDoor)
    {
        nextBot = now + otherDoor;
        declinedDoor = null;
        doors[door] = now + sameDoor;
        return (sameDoor, otherDoor);
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
