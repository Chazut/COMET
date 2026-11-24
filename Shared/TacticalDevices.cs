using System;

namespace Comet.Core;

public sealed class TacticalDevicesConfig
{
    internal TacticalDevicesConfig CopyForReplay() => (TacticalDevicesConfig)MemberwiseClone();

    public bool Enabled { get; set; } = true;
    public double CommandInterval { get; set; } = .25;
    public double ConfirmationTimeout { get; set; } = .8;
    public int AttemptsBeforePause { get; set; } = 2;
    public double InitialPause { get; set; } = 60;
    public double MaximumPause { get; set; } = 600;

    public void Validate()
    {
        CometConfig.Range(CommandInterval, .1, 2, nameof(CommandInterval));
        CometConfig.Range(ConfirmationTimeout, .1, 3, nameof(ConfirmationTimeout));
        CometConfig.Range(AttemptsBeforePause, 1, 5, nameof(AttemptsBeforePause));
        CometConfig.Range(InitialPause, 1, 60, nameof(InitialPause));
        CometConfig.Range(MaximumPause, 1, 600, nameof(MaximumPause));
        if (MaximumPause < InitialPause)
            throw new ArgumentException("MaximumPause must be at least InitialPause.");
    }
}

// A separate clock for each physical accessory. No Unity references or implicit light/IR assumptions.
public sealed class TacticalDeviceAttempt
{
    public bool Target,
        Pending,
        Confirmed,
        Yielded,
        Requested;
    public int Failures,
        Rounds,
        Mode;
    public double RequestedAt,
        RetryAt,
        NextCommand;
    private bool initialized;
    public string State = "idle";

    public void SetTarget(bool target, int mode)
    {
        if (initialized && Target == target)
            return;
        initialized = true;
        Target = target;
        Mode = mode;
        Pending = Confirmed = Yielded = Requested = false;
        Failures = Rounds = 0;
        RetryAt = 0; // NextCommand still prevents rapid contradictory commands.
        State = "waiting";
    }

    public void Suspend()
    {
        Pending = Confirmed = false;
        State = "waiting";
    }

    public void Yield()
    {
        Pending = Confirmed = false;
        Yielded = true;
        State = "yielded";
    }

    public bool Observe(double now, bool active, int mode, TacticalDevicesConfig c)
    {
        Mode = mode;
        Yielded = false;
        if (active == Target)
        {
            Pending = false;
            Confirmed = true;
            Failures = Rounds = 0;
            RetryAt = 0;
            State = "confirmed";
            return false;
        }
        Confirmed = false;
        if (Pending)
        {
            if (now - RequestedAt < c.ConfirmationTimeout)
            {
                State = "pending";
                return false;
            }
            Pending = false;
            Failures++;
            if (Failures >= c.AttemptsBeforePause)
            {
                RetryAt =
                    now
                    + Math.Min(
                        c.MaximumPause,
                        c.InitialPause * Math.Pow(2, Math.Min(Rounds++, 10))
                    );
                Failures = 0;
            }
        }
        State = now < RetryAt ? "backoff" : "waiting";
        return now >= RetryAt && now >= NextCommand;
    }

    public void Request(double now, TacticalDevicesConfig c)
    {
        Requested = true;
        Pending = true;
        Confirmed = false;
        RequestedAt = now;
        NextCommand = now + c.CommandInterval;
        State = "pending";
    }
}

public sealed class TacticalDeviceDetail
{
    internal TacticalDeviceDetail CopyForReplay() => (TacticalDeviceDetail)MemberwiseClone();

    public string Id = "",
        Name = "",
        State = "";
    public bool Active,
        Requested,
        Confirmed;
    public int Mode,
        Failures,
        Rounds;
    public double RetryIn;
}

public sealed class TacticalDevicesTrace
{
    internal TacticalDevicesTrace CopyForReplay()
    {
        var copy = (TacticalDevicesTrace)MemberwiseClone();
        if (Alternatives.Length != 0)
            copy.Alternatives = (string[])Alternatives.Clone();
        if (Accessories.Length != 0)
            copy.Accessories = Array.ConvertAll(Accessories, static entry => entry.CopyForReplay());
        return copy;
    }

    public string State = "waiting",
        Reason = "",
        Action = "",
        Weapon = "",
        Source = "SAIN",
        Execution = "No command";
    public bool Target,
        Requested,
        Accepted,
        Observed;
    public int Devices,
        ActiveDevices;
    public double ValidFor;
    public TacticalDeviceDetail[] Accessories = Array.Empty<TacticalDeviceDetail>();
    public string[] Alternatives =
    {
        "Keep already correct accessories unchanged",
        "Wait for ready hands",
        "Retry only failed accessories after a bounded pause",
        "Yield to ambush concealment or external changes",
    };
}
