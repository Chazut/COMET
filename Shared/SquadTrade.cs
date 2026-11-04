using System;
using System.Collections.Generic;
namespace Comet.Core;

public sealed class SquadTradeConfig
{
    public bool Enabled {get;set;}=true;
    public double Window {get;set;}=10;
    public double DelayMin {get;set;}=.3;
    public double DelayMax {get;set;}=.8;
    public double ContactAge {get;set;}=5;
    public double MaxDistance {get;set;}=40;
    public double MaxPath {get;set;}=25;
    public double Duration {get;set;}=15;
    public double StallTimeout {get;set;}=3;
    public double MinimumAmmo {get;set;}=50;
    public double AggressiveChance {get;set;}=60;
    public double OtherChance {get;set;}=20;
    public void Validate()
    {
        CometConfig.Range(Window,1,20,nameof(Window));CometConfig.Range(DelayMin,0,3,nameof(DelayMin));
        CometConfig.Range(DelayMax,DelayMin,Math.Min(5,Window),nameof(DelayMax));CometConfig.Range(ContactAge,1,10,nameof(ContactAge));
        CometConfig.Range(MaxDistance,5,80,nameof(MaxDistance));CometConfig.Range(MaxPath,3,40,nameof(MaxPath));
        CometConfig.Range(Duration,3,30,nameof(Duration));CometConfig.Range(StallTimeout,1,Math.Min(10,Duration),nameof(StallTimeout));
        CometConfig.Range(MinimumAmmo,1,100,nameof(MinimumAmmo));CometConfig.Range(AggressiveChance,0,100,nameof(AggressiveChance));CometConfig.Range(OtherChance,0,100,nameof(OtherChance));
    }
}
public sealed class SquadTradeTrace
{
    public string Death="",Mate="",MateName="",Evidence="witnessed death",Confidence="sector only",Choice="watch",Status="selected",Reason="";
    public bool Confirmed;
    public double DeathAge,Delay,Ammo,Remaining,Roll,Chance;
}
public sealed class TradeLoss
{
    public string Id="",Mate="",Name="",AssociatedContact="";
    public double At,ReadyAt;
    public bool Attempted;
    public int Group;
}
// Per observer, bounded, detached records. Disappearance is deliberately not an input.
public sealed class SquadTradeMemory
{
    private readonly Dictionary<string,TradeLoss> losses=new();
    private double clock=-1;private int serial;
    public int Count=>losses.Count;
    private bool Clock(double now)
    {if(!MedicalChoice.Finite(now)||now<0)return false;if(now<clock)Clear();clock=now;return true;}
    public TradeLoss? Observe(string mate,string name,double now,bool dead,bool downed,bool witnessed,string associated,double delayRoll,SquadTradeConfig c,int group=0)
    {
        if(!Clock(now)||!c.Enabled||string.IsNullOrEmpty(mate)||!dead||downed||!witnessed)return null;
        if(losses.TryGetValue(mate,out var old))return old;
        if(losses.Count>=128)return null; // Fail closed; never evict and re-roll a death in this raid.
        double roll=MedicalChoice.Finite(delayRoll)?Math.Max(0,Math.Min(1,delayRoll)):1;
        var loss=new TradeLoss{Id="loss-"+(++serial),Mate=mate,Name=name,At=now,ReadyAt=now+c.DelayMin+roll*(c.DelayMax-c.DelayMin),AssociatedContact=associated,Group=group};
        losses.Add(mate,loss);return loss;
    }
    public TradeLoss? Take(double now,SquadTradeConfig c)
    {
        if(!Clock(now)||!c.Enabled)return null;
        foreach(var loss in losses.Values)
        {
            if(loss.Attempted||now<loss.ReadyAt)continue;
            loss.Attempted=true;
            if(now-loss.At<=c.Window)return loss;
        }
        return null;
    }
    public static string Choose(bool associated,bool aggressive,double ammo,bool panic,double roll,SquadTradeConfig c,out string why)
    {
        why=!associated?"killer not identified by this observer":panic?"fear veto":!MedicalChoice.Finite(ammo)||ammo<c.MinimumAmmo?"insufficient loaded magazine":"personality roll";
        return associated&&!panic&&MedicalChoice.Finite(ammo)&&ammo>=c.MinimumAmmo&&MedicalChoice.Finite(roll)&&roll>=0&&roll<1
            &&roll*100<(aggressive?c.AggressiveChance:c.OtherChance)?"push":"watch";
    }
    public void Clear(){losses.Clear();serial=0;clock=-1;}
}
