namespace Comet.Core;

public sealed class PerformanceConfig
{
    public const double FrameBudgetMs = 16.67;

    public bool Enabled { get; set; }
    public bool MeasureAllocations { get; set; } = true;
    public double SlowFrameMs { get; set; } = FrameBudgetMs;
    public double ReportIntervalSeconds { get; set; } = 10;

    public void Validate()
    {
        CometConfig.Range(SlowFrameMs, 16, 1000, nameof(SlowFrameMs));
        CometConfig.Range(ReportIntervalSeconds, 5, 60, nameof(ReportIntervalSeconds));
    }
}
