namespace Comet.Core;

// R_F EnemyDecisionClass.WatchFallBackStall, fbb86b83. One watch spans
// Search/Push/FallBack changes; intentional Hold/Flank never consume it.
public sealed class HiddenMovementWatch
{
    private double since = -1;
    private DoorPoint position;
    public double MoveBanUntil { get; private set; }
    public double FallBackBanUntil { get; private set; }

    public void Pause() => since = -1;

    public string Observe(
        double now,
        DoorPoint pos,
        string nativeAction,
        bool utilityFallBack,
        bool inCover,
        double knownDistance
    )
    {
        bool fallingBack = nativeAction == "SeekCover" && utilityFallBack && !inCover;
        bool moving = fallingBack || nativeAction is "Search" or "RushEnemy";
        if (!moving)
        {
            since = -1;
            return "";
        }
        if (since < 0 || (pos - position).Length > .5)
        {
            since = now;
            position = pos;
            return "";
        }
        bool atKnown = nativeAction == "Search" && knownDistance < 2;
        double limit =
            fallingBack ? 5
            : atKnown ? 2
            : 6;
        if (now - since < limit)
            return "";
        since = -1;
        FallBackBanUntil = now + 15;
        if (fallingBack)
            return "zzap-fallback-stalled";
        MoveBanUntil = now + 12;
        return "zzap-move-stalled-" + nativeAction;
    }
}
