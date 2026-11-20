using System;
namespace Comet.Core;

public sealed class DoorMovePhase
{
    private string phase = "";
    private double started, checkpoint;
    private DoorPoint previous;

    public string Check(string state, DoorPoint position, bool arrived, double now, out double remaining)
    {
        if (state != phase)
        {
            phase = state;
            started = checkpoint = now;
            previous = position;
        }
        remaining = Math.Max(0, 12 - (now - started));
        if (arrived) return "";
        if (now - started > 12) return "move-timeout";
        if (now - checkpoint <= 2.5) return "";
        double dx = position.X - previous.X, dz = position.Z - previous.Z;
        checkpoint = now;
        previous = position;
        return dx * dx + dz * dz < .16 ? "move-stalled" : "";
    }

    public void Reset() => phase = "";
}
