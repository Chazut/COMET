namespace Comet.Core;

public sealed class ControlJournalConfig
{
    public bool Enabled { get; set; }
    public double LongWait { get; set; } = 30;
    public double RapidReturn { get; set; } = 5;
    public int CycleThreshold { get; set; } = 3;
    public double Window { get; set; } = 20;

    public void Validate()
    {
        CometConfig.Range(LongWait, 5, 120, nameof(LongWait));
        CometConfig.Range(RapidReturn, 1, 15, nameof(RapidReturn));
        CometConfig.Range(CycleThreshold, 2, 12, nameof(CycleThreshold));
        CometConfig.Range(Window, 5, 60, nameof(Window));
    }
}

public sealed class CombatJournalConfig
{
    public bool Enabled { get; set; } = true;
    public double SampleInterval { get; set; } = .25;

    public void Validate() => CometConfig.Range(SampleInterval, .1, 2, nameof(SampleInterval));
}

public sealed class WeaponJournalConfig
{
    public bool Enabled { get; set; }
    public double Window { get; set; } = 20;
    public int ChangeThreshold { get; set; } = 3;
    public double ConfirmationTimeout { get; set; } = 5;

    public void Validate()
    {
        CometConfig.Range(Window, 5, 60, nameof(Window));
        CometConfig.Range(ChangeThreshold, 3, 12, nameof(ChangeThreshold));
        CometConfig.Range(ConfirmationTimeout, 1, 30, nameof(ConfirmationTimeout));
    }
}
