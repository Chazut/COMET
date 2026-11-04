using System;

namespace Comet.Core;

public sealed class RecoilRecoveryConfig
{
    public bool Enabled { get; set; } = false;
    public double DmrMultiplier { get; set; } = 3;
    public double SniperMultiplier { get; set; } = 3;
    public void Validate()
    {
        CometConfig.Range(DmrMultiplier,1,3,nameof(DmrMultiplier));
        CometConfig.Range(SniperMultiplier,1,3,nameof(SniperMultiplier));
    }
}
public sealed class RecoilRecoveryTrace
{
    public string State="started",Reason="recoil-marksman",Weapon="",WeaponClass="marksmanRifle",Action="",Target="";
    public bool Applied;
    public double Multiplier=3,BeforeHorizontal,BeforeVertical,AfterHorizontal,AfterVertical,Coefficient,Step,Elapsed,ValidFor=.75;
    public int Shots,Steps;
    public string[] Alternatives={"Keep native SAIN recovery (module off or multiplier x1)","Initial recoil, fire rate and hit outcome stay native"};
}
public static class RecoilRecoveryPolicy
{
    public static bool Finite(double v)=>!double.IsNaN(v)&&!double.IsInfinity(v);
    public static double Multiplier(RecoilRecoveryConfig c,string weaponClass)=>!c.Enabled?1:
        weaponClass=="marksmanRifle"?c.DmrMultiplier:weaponClass=="sniperRifle"?c.SniperMultiplier:1;

    // At one native fixed step this is zzap's original (1 - step * coefficient * multiplier).
    // Fractional powers preserve that decay over real elapsed time, independent of callback frequency.
    // x1 is deliberately never processed here: the adapter executes the original SAIN method unchanged.
    public static bool TryStep(double horizontal,double vertical,double elapsed,double fixedStep,double coefficient,double multiplier,
        out float nextHorizontal,out float nextVertical,out bool finished)
    {
        nextHorizontal=(float)horizontal;nextVertical=(float)vertical;finished=false;
        if(!Finite(horizontal)||!Finite(vertical)||Math.Abs(horizontal)>180||Math.Abs(vertical)>180
            ||!Finite(elapsed)||elapsed<0||elapsed>.25||!Finite(fixedStep)||fixedStep<=0||fixedStep>.1
            ||!Finite(coefficient)||coefficient<0||!Finite(multiplier)||multiplier<=1||multiplier>3)return false;
        if(elapsed==0)return true;
        double retention=Math.Max(0,1-fixedStep*coefficient*multiplier);
        double factor=Math.Pow(retention,elapsed/fixedStep);
        nextHorizontal=(float)(horizontal*factor);nextVertical=(float)(vertical*factor);
        // Signed offsets need absolute magnitudes. This never changes native x1/OFF termination.
        finished=Math.Abs(nextHorizontal)<=.001&&Math.Abs(nextVertical)<=.001;
        if(finished){nextHorizontal=0;nextVertical=0;}
        return true;
    }
}
