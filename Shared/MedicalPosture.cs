using System;

namespace Comet.Core;

public sealed class MedicalPostureConfig
{
    public bool Enabled { get; set; } = true;
    public double Interval { get; set; } = .1;
    public double ResumeDelay { get; set; } = 0;

    public void Validate()
    {
        CometConfig.Range(Interval, .05, .5, nameof(Interval));
        CometConfig.Range(ResumeDelay, 0, 1, nameof(ResumeDelay));
    }
}

public sealed class MedicalPostureFrame
{
    public double Now,
        Tilt;
    public bool Using,
        MedicineHands,
        WeaponReady,
        NeutralTarget,
        Grounded;
    public string Treatment = "",
        Item = "";
}

public sealed class MedicalPostureTrace
{
    public string State { get; set; } = "protecting";
    public string Reason { get; set; } = "medical-posture-treatment";
    public string Treatment { get; set; } = "";
    public string Item { get; set; } = "";
    public bool Using { get; set; }
    public bool MedicineHands { get; set; }
    public bool WeaponReady { get; set; }
    public bool Neutral { get; set; }
    public bool Grounded { get; set; }
    public double Tilt { get; set; }
    public double StableFor { get; set; }
    public double ResumeDelay { get; set; }
    public bool Applied { get; set; }
    public double ValidFor { get; set; }
    public string[] Alternatives { get; set; } = Array.Empty<string>();
}

// A guard, never an owner of medicine, locomotion, shooting or the physical pose.
public sealed class MedicalPosture
{
    public const double NeutralTolerance = .1; // SAIN tilt units; full lean is +/-5.
    public MedicalPostureTrace? Trace { get; private set; }
    public bool Blocking => Trace?.Applied == true;
    private double stableAt = -1,
        lastAt = -1;

    public bool Tick(MedicalPostureFrame f, MedicalPostureConfig c)
    {
        bool medicine = f.Using || f.MedicineHands;
        if (!c.Enabled)
        {
            End("medical-posture-disabled");
            return false;
        }
        if (!medicine && !Blocking)
            return false;
        if (!Blocking)
        {
            Trace = new();
            stableAt = -1;
            lastAt = -1;
        }
        var t = Trace!;
        t.Using = f.Using;
        t.MedicineHands = f.MedicineHands;
        t.WeaponReady = f.WeaponReady;
        t.Tilt = f.Tilt;
        t.Neutral = f.NeutralTarget;
        t.Grounded = f.Grounded;
        t.Treatment = f.Treatment;
        t.Item = f.Item;
        t.Applied = medicine;
        t.ValidFor = medicine ? 1 : 0;
        t.State = medicine ? "protecting" : "resumed";
        t.Reason = medicine ? "medical-posture-treatment" : "medical-posture-tactics-resumed";
        t.Alternatives = new[]
        {
            t.Applied
                ? "COMET peeks, lean and new jumps: suspended"
                : "COMET tactics: eligible again, normal checks still apply",
            "Native shooting and emergency travel: not blocked by this guard",
            f.Grounded
                ? "Medicine and crouch/prone animations: native"
                : "Already airborne: native landing preserved",
        };
        return Blocking;
    }

    public void End(string reason)
    {
        if (Trace == null)
            return;
        Trace.State = "ended";
        Trace.Reason = reason;
        Trace.Applied = false;
        Trace.ValidFor = 0;
        stableAt = lastAt = -1;
    }
}
