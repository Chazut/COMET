using System;
using System.Collections.Generic;

namespace Comet.Core;

public sealed class SquadVoiceConfig
{
    public string Mode { get; set; } = "Disabled";
    public double Chance { get; set; } = 50;
    public double BotDelay { get; set; } = 5;
    public double SquadDelay { get; set; } = 2;

    public void Validate()
    {
        if (Mode != "Normal" && Mode != "Reduced" && Mode != "Disabled")
            throw new ArgumentException("Unknown squad voice mode.");
        CometConfig.Range(Chance, 0, 100, nameof(Chance));
        CometConfig.Range(BotDelay, 0, 30, nameof(BotDelay));
        CometConfig.Range(SquadDelay, 0, 15, nameof(SquadDelay));
    }
}

public sealed class SquadVoiceTrace
{
    public string Request = "",
        State = "requested",
        Reason = "squad-voice-requested",
        Phrase = "",
        Source = "",
        Mode = "Normal",
        Action = "",
        Execution = "native request observed";
    public bool Urgent,
        Filtered,
        Played;
    public double Chance,
        Roll = -1,
        BotWait,
        SquadWait,
        ValidFor;
    public string[] Alternatives = Array.Empty<string>();
}

// Only detached identifiers and times live here; nothing retains a BotComponent or GameWorld.
public sealed class SquadVoicePolicy
{
    private readonly Dictionary<string, double> bots = new(),
        squads = new();
    private double last = -1;

    private void Clock(double now)
    {
        if (now < last)
            Clear();
        last = now;
        // Prune only after enough time: largest configurable delay is 30 seconds.
        if (bots.Count + squads.Count > 256)
        {
            Prune(bots, now);
            Prune(squads, now);
        }
    }

    private static void Prune(Dictionary<string, double> values, double now)
    {
        foreach (var key in new List<string>(values.Keys))
            if (now - values[key] > 30)
                values.Remove(key);
    }

    public bool Evaluate(
        SquadVoiceTrace t,
        SquadVoiceConfig c,
        string bot,
        string squad,
        double now,
        Func<double> roll
    )
    {
        t.Mode = c.Mode;
        t.Chance = c.Chance;
        t.Roll = -1;
        t.BotWait = t.SquadWait = t.ValidFor = 0;
        t.Filtered = t.Played = false;
        if (!DoorPolicy.Finite(now) || now < 0)
        {
            t.Reason = "squad-voice-clock-unavailable";
            return true;
        }
        Clock(now);
        if (c.Mode == "Disabled")
            return Reject(t, "squad-voice-disabled");
        // zzap has a callouts on/off switch, no second chance roll or per-squad debounce.
        t.Reason = "squad-voice-native-normal";
        return true;
    }

    private static bool Reject(SquadVoiceTrace t, string reason)
    {
        t.State = "filtered";
        t.Reason = reason;
        t.Filtered = true;
        t.ValidFor = 2;
        t.Execution = "COMET prevented this native squad voice attempt";
        return false;
    }

    public void Played(SquadVoiceTrace t, string bot, string squad, double now)
    {
        t.State = "native-played";
        t.Played = true;
        t.Filtered = false;
        t.ValidFor = 2;
        t.Execution = "SAIN accepted a voice clip; remote audibility is not measured";
        if (!DoorPolicy.Finite(now) || now < 0)
            return;
        Clock(now);
        bots[bot] = now;
        squads[squad] = now;
    }

    public void Clear()
    {
        bots.Clear();
        squads.Clear();
        last = -1;
    }
}
