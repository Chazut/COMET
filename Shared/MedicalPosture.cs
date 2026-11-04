using System;
namespace Comet.Core;

public sealed class MedicalPostureConfig
{
    public bool Enabled { get; set; } = true;
    public double Interval { get; set; } = .1;
    public double ResumeDelay { get; set; } = .2;
    public void Validate()
    {
        CometConfig.Range(Interval,.05,.5,nameof(Interval));
        CometConfig.Range(ResumeDelay,0,1,nameof(ResumeDelay));
    }
}
public sealed class MedicalPostureFrame
{
    public double Now, Tilt;
    public bool Using, MedicineHands, WeaponReady, NeutralTarget, Grounded;
    public string Treatment = "", Item = "";
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
    private double stableAt = -1, lastAt = -1;
    public bool Tick(MedicalPostureFrame f,MedicalPostureConfig c)
    {
        bool medicine=f.Using||f.MedicineHands;
        if(!c.Enabled){End("medical-posture-disabled");return false;}
        if(!medicine&&!Blocking)return false;
        if(!Blocking){Trace=new();stableAt=-1;lastAt=-1;}
        var t=Trace!;
        // A missing sampling interval cannot count as continuously observed readiness.
        if(lastAt>=0&&(f.Now<lastAt||f.Now-lastAt>Math.Max(.25,c.Interval*2+.01)))stableAt=-1;
        lastAt=f.Now;
        t.Using=f.Using;t.MedicineHands=f.MedicineHands;t.WeaponReady=f.WeaponReady;
        t.Tilt=MedicalChoice.Finite(f.Tilt)?f.Tilt:0;t.Neutral=MedicalChoice.Finite(f.Tilt)&&Math.Abs(f.Tilt)<=NeutralTolerance&&f.NeutralTarget;
        t.Grounded=f.Grounded;t.ResumeDelay=c.ResumeDelay;t.Applied=true;t.ValidFor=1;
        if(medicine){t.Treatment=f.Treatment;t.Item=f.Item;}
        if(medicine||!f.WeaponReady||!t.Neutral)
        {
            stableAt=-1;t.StableFor=0;
            t.State=medicine?"protecting":!f.WeaponReady?"waiting-weapon":"centering";
            t.Reason=f.Using?"medical-posture-treatment":f.MedicineHands?"medical-posture-medicine-still-in-hands":!f.WeaponReady?"medical-posture-weapon-not-ready":"medical-posture-bust-not-centered";
        }
        else
        {
            if(stableAt<0)stableAt=f.Now;
            t.StableFor=Math.Max(0,f.Now-stableAt);
            bool ready=t.StableFor+1e-6>=c.ResumeDelay;
            t.State=ready?"resumed":"settling";t.Reason=ready?"medical-posture-tactics-resumed":"medical-posture-stability-delay";
            t.Applied=!ready;t.ValidFor=ready?0:1;
        }
        t.Alternatives=new[]{t.Applied?"COMET peeks, lean and new jumps: suspended":"COMET tactics: eligible again, normal checks still apply",
            "Native shooting and emergency travel: not blocked by this guard",
            f.Grounded?"Medicine and crouch/prone animations: native":"Already airborne: native landing preserved"};
        return Blocking;
    }
    public void End(string reason)
    {if(Trace==null)return;Trace.State="ended";Trace.Reason=reason;Trace.Applied=false;Trace.ValidFor=0;stableAt=lastAt=-1;}
}
