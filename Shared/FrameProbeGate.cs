namespace Comet.Core;

// A volatile capability must still be checked by its caller. This gate only
// deduplicates expensive geometry work, never authorizes an action.
public sealed class FrameProbeGate
{
    private int frame = -1;
    private string contact = "";
    private float x, y, z;
    public bool Same(int current, string target, float px, float py, float pz)
        => frame == current && contact == target && x == px && y == py && z == pz;
    public bool TryEnter(int current, string target, float px, float py, float pz)
    {
        if (frame == current) return false;
        frame = current; contact = target; x = px; y = py; z = pz;
        return true;
    }
}
