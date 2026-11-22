using System;
namespace Comet.Core;

public sealed class DoorMovePhase
{
    private string? phase;
    private float started, nextProgressCheck;
    private DoorPoint previous;

    public string Check(string state, DoorPoint position, bool arrived, double now, out double remaining) =>
        Check(state, position, arrived, phase == state ? started : now, now, out remaining);

    internal string Check(string state, DoorPoint position, bool arrived, double phaseStartedAt, double now, out double remaining)
    {
        float time = (float)now;
        if (state != phase || started != (float)phaseStartedAt)
        {
            phase = state;
            started = (float)phaseStartedAt;
            nextProgressCheck = 0f;
        }
        float elapsed = time - started;
        remaining = Math.Max(0f, 12f - elapsed);
        if (arrived) return "";
        if (elapsed > 12f) return "move-timeout";
        if (elapsed >= .1f && nextProgressCheck > 0f)
        {
            if (time <= nextProgressCheck) return "";
            double dx = position.X - previous.X, dz = position.Z - previous.Z;
            if (dx * dx + dz * dz < .16) return "move-stalled";
        }
        nextProgressCheck = time + 2.5f;
        previous = position;
        return "";
    }

    public void Reset() => phase = null;
}
