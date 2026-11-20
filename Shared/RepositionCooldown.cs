namespace Comet.Core;

// One clock per bot, shared by outdoor relocation and both cover feints.
public sealed class RepositionCooldown
{
    public double Until { get; private set; }

    internal double AfterSession(double now) => Until = now + 5;
}
