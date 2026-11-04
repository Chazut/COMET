namespace Comet.Core;

// Diagnostic observation only. Never interpreted as a kill/extraction by RR.
public sealed class LifecycleTrace
{
    public string State { get; set; } = "";
    public string Reason { get; set; } = "";
    public string PreviousAction { get; set; } = "";
    public string PreviousInstance { get; set; } = "";
    public int Generation { get; set; }
    public int CleanupSteps { get; set; }
    public int FailedSteps { get; set; }
    public string[] Failures { get; set; } = System.Array.Empty<string>();
    public bool FreshState { get; set; }
    public double ValidFor { get; set; } = 2;
}
