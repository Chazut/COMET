namespace Comet.Core;

public sealed class TacticCountersConfig
{
    public bool Enabled { get; set; }
    public int MaxOpportunities { get; set; } = 16384;

    public void Validate() =>
        CometConfig.Range(MaxOpportunities, 1024, 65536, nameof(MaxOpportunities));
}
