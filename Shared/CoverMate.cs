using System;
using System.Collections.Generic;
namespace Comet.Core;

public sealed class CoverMateConfig
{
    public bool Enabled {get;set;}=true;
    public double MateDistance {get;set;}=20;
    public double Near {get;set;}=3;
    public double Far {get;set;}=6;
    public double MaxPath {get;set;}=15;
    public int Protectors {get;set;}=2;
    public double Duration {get;set;}=15;
    public double SurgeryDuration {get;set;}=60;
    public double StallTimeout {get;set;}=3;
    public double ReadyDelay {get;set;}=.2;
    public double Retry {get;set;}=4;
    public void Validate()
    {
        CometConfig.Range(MateDistance,5,40,nameof(MateDistance));CometConfig.Range(Near,2,8,nameof(Near));
        CometConfig.Range(Far,Near,12,nameof(Far));CometConfig.Range(MaxPath,3,30,nameof(MaxPath));
        CometConfig.Range(Protectors,1,3,nameof(Protectors));CometConfig.Range(Duration,5,30,nameof(Duration));
        CometConfig.Range(SurgeryDuration,Duration,90,nameof(SurgeryDuration));CometConfig.Range(StallTimeout,1,10,nameof(StallTimeout));
        CometConfig.Range(ReadyDelay,0,1,nameof(ReadyDelay));CometConfig.Range(Retry,1,20,nameof(Retry));
    }
}
public sealed class CoverOperation
{
    public string Id="",Kind="",Item="",Reason="observed operation";
    public double Started,Seen,ReadyAt=-1,Expected,Progress,Changed;
    public bool Busy,Ready,Ended,Stalled;
}
public sealed class CoverMateTrace
{
    public string Mate="",MateName="",Operation="",Treatment="",Sector="",Status="selected",Reason="";
    public bool Busy,WeaponReady,Confirmed,Stalled;
    public double OperationAge,Remaining,Separation;
    public int Protectors;
}
// All records are raid-local detached IDs and scalars. Failed episodes cannot continuously reassign guards.
public sealed class CoverMateBoard
{
    private readonly Dictionary<string,CoverOperation> operations=new();
    private sealed class Claim {public string Owner="",Mate="",Operation="",Sector="";public double X,Z,Until;}
    private readonly Dictionary<string,Claim> claims=new();
    private readonly HashSet<string> attempted=new();
    private int serial;private double clock=-1;
    private bool Clock(double now)
    {if(!MedicalChoice.Finite(now)||now<0)return false;if(now<clock)Clear();clock=now;return true;}
    public CoverOperation? Observe(string mate,double now,string kind,string item,bool busy,bool ready,double expected,double progress,CoverMateConfig c)
    {
        if(!Clock(now)||string.IsNullOrEmpty(mate)||!MedicalChoice.Finite(expected)||!MedicalChoice.Finite(progress))return null;
        operations.TryGetValue(mate,out var o);
        if(o==null||o.Ended&&!o.Stalled&&busy)
        {
            if(!busy||operations.Count>=512&&!operations.ContainsKey(mate))return o;
            o=new(){Id="care-"+(++serial),Kind=kind,Item=item,Started=now,Changed=now,Expected=Math.Max(0,expected),Progress=progress};operations[mate]=o;
        }
        if(o==null)return null;
        o.Seen=now;o.Busy=busy;o.Ready=ready;
        if(progress!=o.Progress){o.Progress=progress;o.Changed=now;}
        // Do not treat a long animation as stalled just because HP does not change until its end.
        if(busy)
        {
            o.ReadyAt=-1;
            if((kind=="Surgery"&&o.Kind!="Surgery")||(item.Length>0&&item!=o.Item))
            {o.Item=item;o.Expected=Math.Max(0,expected);o.Changed=now;}
            if(kind=="Surgery")o.Kind=kind;
            if(!o.Ended)
            {
                double cap=o.Kind=="Surgery"?c.SurgeryDuration:c.Duration;
                double window=o.Expected>0?Math.Min(cap,o.Expected+5):cap;
                if(now-o.Started>=cap||now-o.Changed>=window){o.Stalled=o.Ended=true;o.Reason="operation exceeded observation budget";}
            }
        }
        else if(ready)
        {
            if(o.ReadyAt<0)o.ReadyAt=now;
            if(now-o.ReadyAt>=c.ReadyDelay){o.Ended=true;o.Stalled=false;o.Reason="weapon ready";}
        }
        else o.ReadyAt=-1;
        if(!o.Ended&&!ready&&now-o.Started>=(o.Kind=="Surgery"?c.SurgeryDuration:c.Duration))
        {o.Stalled=o.Ended=true;o.Reason="weapon did not become ready before observation limit";}
        return o;
    }
    public bool Attempt(string owner,string mate,string operation,double now)
    {if(!Clock(now)||attempted.Count>=2048)return false;return attempted.Add(owner+"/"+mate+"/"+operation);}
    public bool Available(string owner,string mate,string operation,string sector,double x,double z,double now,CoverMateConfig c)
    {
        if(!Clock(now)||!MedicalChoice.Finite(x)||!MedicalChoice.Finite(z))return false;
        int count=0;
        foreach(var entry in claims.Values)
        {
            if(entry.Until<=now||entry.Owner==owner||entry.Mate!=mate||entry.Operation!=operation)continue;
            count++;double a=Math.Sqrt(x*x+z*z),b=Math.Sqrt(entry.X*entry.X+entry.Z*entry.Z);
            if(entry.Sector==sector||a<.01||b<.01||(x*entry.X+z*entry.Z)/(a*b)>Math.Cos(35*Math.PI/180))return false;
        }
        return count<c.Protectors;
    }
    public bool Reserve(string owner,string mate,string operation,string sector,double x,double z,double now,CoverMateConfig c)
    {
        if(!Available(owner,mate,operation,sector,x,z,now,c)||claims.Count>=512&&!claims.ContainsKey(owner))return false;
        claims[owner]=new(){Owner=owner,Mate=mate,Operation=operation,Sector=sector,X=x,Z=z,Until=now+3};return true;
    }
    public bool Valid(string owner,double now)=>Clock(now)&&claims.TryGetValue(owner,out var claim)&&claim.Until>now;
    public bool Renew(string owner,double now)
    {if(!Clock(now)||!claims.TryGetValue(owner,out var c)||c.Until<=now)return false;c.Until=now+1;return true;}
    public int Count(string mate,string operation,double now){int n=0;foreach(var c in claims.Values)if(c.Mate==mate&&c.Operation==operation&&c.Until>now)n++;return n;}
    public void Release(string owner)=>claims.Remove(owner);
    public void Clear(){operations.Clear();claims.Clear();attempted.Clear();serial=0;clock=-1;}
}
