using System;

namespace Comet.Core;

public sealed class PistolSwapConfig
{
    public bool Enabled { get; set; } = true;
    public double MaxDistance { get; set; } = 30;
    public double LowAmmoPercent { get; set; } = 10;
    public int MaxMainRounds { get; set; } = 3;
    public double ContactMemory { get; set; } = 1.5;
    public double RetryDelay { get; set; } = 3;

    public void Validate()
    {
        CometConfig.Range(MaxDistance, 5, 60, nameof(MaxDistance));
        CometConfig.Range(LowAmmoPercent, 0, 30, nameof(LowAmmoPercent));
        CometConfig.Range(MaxMainRounds, 0, 10, nameof(MaxMainRounds));
        CometConfig.Range(ContactMemory, 0, 3, nameof(ContactMemory));
        CometConfig.Range(RetryDelay, .5, 10, nameof(RetryDelay));
    }
}

public sealed class PistolSwapFrame
{
    public string Pistol = "",
        Block = "";
    public int Loaded,
        Chamber,
        Magazine,
        Capacity;
    public bool Usable,
        PrimaryInHands;
}

public sealed class PistolSwapTrace
{
    public string State = "choice",
        Choice = "Native",
        Reason = "",
        Weapon = "",
        Pistol = "",
        Target = "",
        Source = "",
        Action = "";
    public int Episode,
        MainLoaded,
        MainCapacity,
        PistolLoaded,
        PistolChamber,
        PistolMagazine,
        PistolCapacity;
    public double Distance,
        ContactAge,
        ReloadScore,
        HoldScore,
        PistolScore,
        Elapsed,
        ValidFor = .5;
    public bool Visible,
        Protected,
        Incoming,
        Requested,
        Accepted,
        InHands,
        Ready;
    public string[] Alternatives = Array.Empty<string>();
}

public sealed class PistolSwapPolicy
{
    public PistolSwapTrace? Trace { get; private set; }
    public bool Active => Trace?.State is "requested" or "drawing";
    private double retryAt,
        requestAt,
        lastNow;
    private int episode;

    public bool RetryBlocked(double now) => now < retryAt;

    public bool Evaluate(
        ReloadTimingFrame f,
        PistolSwapFrame p,
        ReloadTimingTrace? reload,
        PistolSwapConfig c
    )
    {
        var t = new PistolSwapTrace
        {
            Episode = ++episode,
            Weapon = f.Weapon,
            Pistol = p.Pistol,
            Target = f.Target,
            Source = f.Source,
            MainLoaded = f.Loaded,
            MainCapacity = f.Capacity,
            PistolLoaded = p.Loaded,
            PistolChamber = p.Chamber,
            PistolMagazine = p.Magazine,
            PistolCapacity = p.Capacity,
            Distance = f.Distance,
            ContactAge = f.ContactAge,
            Visible = f.Visible,
            Protected = f.Protected,
            Incoming = f.Incoming,
            ReloadScore = reload?.ReloadScore ?? 0,
            HoldScore = reload?.HoldScore ?? 0,
            Choice = reload?.Choice ?? "Native",
        };
        Trace = t;
        bool No(string why)
        {
            t.Reason = why;
            return false;
        }
        if (
            !c.Enabled
            || RetryBlocked(f.Now)
            || p.Pistol.Length == 0
            || !p.PrimaryInHands
            || p.Loaded <= 0
        )
            return No("pistol-not-ready");
        if (f.Target.Length == 0)
            return No("pistol-no-contact");
        double ratio = f.Capacity > 0 ? (double)f.Loaded / f.Capacity : 1;
        t.PistolScore =
            f.Visible && f.Distance < c.MaxDistance
                ? .2
                    + .6 * (1 - ratio)
                    + (f.Distance < 15 ? .2 : 0)
                    + (f.Looking ? .15 : 0)
                    - (ratio > c.LowAmmoPercent / 100 ? .6 : 0)
                : 0;
        bool emptyFallback =
            f.Loaded == 0
            && (f.Visible || f.ContactAge < c.ContactMemory)
            && f.Distance <= c.MaxDistance;
        if (
            !emptyFallback
            && (
                t.PistolScore <= 0 || t.PistolScore <= t.ReloadScore || t.PistolScore <= t.HoldScore
            )
        )
            return No("pistol-source-ranking-other-choice");
        t.Choice = "Pistol";
        t.Reason = "pistol-close-threat-low-ammo";
        return true;
    }

    public void Request(double now)
    {
        if (Trace?.Choice != "Pistol" || Active)
            return;
        Trace.Requested = true;
        Trace.State = "requested";
        Trace.Reason = "pistol-native-request";
        requestAt = now;
    }

    public void Result(bool accepted, double now, PistolSwapConfig c)
    {
        if (Trace?.Requested != true)
            return;
        Trace.Accepted = accepted;
        if (!accepted)
        {
            End("pistol-native-refused");
            retryAt = now + c.RetryDelay;
        }
    }

    public void Observe(double now, string inHands, bool ready, PistolSwapConfig c)
    {
        if (!Active)
            return;
        var t = Trace!;
        if (!DoorPolicy.Finite(now) || now < requestAt)
        {
            End("pistol-clock-changed");
            retryAt = 0;
            return;
        }
        t.ContactAge += Math.Max(0, now - requestAt - t.Elapsed);
        t.Elapsed = now - requestAt;
        if (inHands == t.Pistol)
        {
            t.InHands = true;
            t.State = "drawing";
            t.Reason = "pistol-in-hands";
            if (ready)
            {
                t.Ready = true;
                t.State = "ready";
                t.Reason = "pistol-ready-observed";
                return;
            }
        }
        else if (inHands.Length > 0 && (inHands != t.Weapon || t.InHands))
        {
            End("pistol-other-weapon-taken");
            return;
        }
        if (t.Elapsed >= 5)
        {
            End("pistol-switch-unconfirmed");
        } // Observation timeout, never cancel animation.
    }

    public void End(string why)
    {
        if (Trace == null)
            return;
        Trace.State = "ended";
        Trace.Reason = why;
        Trace.ValidFor = 0;
    }

    public void Clear(string why)
    {
        End(why);
        retryAt = 0;
    }
}
