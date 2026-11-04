namespace Comet.Core;

// An intercepted invalid call, not a death, hit or successful combat action.
public sealed class ValidityTrace
{
    public string Operation { get; set; } = "";
    public string Reason { get; set; } = "";
    public string Source { get; set; } = "";
    public string SourceInstance { get; set; } = "";
    public int Blocked { get; set; }
    public bool Summary { get; set; }
    public double ValidFor { get; set; } = 2;
}
