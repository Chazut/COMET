using System;
using System.Collections.Generic;
namespace Comet.Core;

public sealed class CoverMateConfig
{
    public bool Enabled {get;set;}=true;
    public double MateDistance {get;set;}=20;
    public double Near {get;set;}=3;
    public double Far {get;set;}=6;
    public double MaxPath {get;set;}=12;
    public int Protectors {get;set;}=2;
    public double Duration {get;set;}=12;
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
// zzap SquadCombatClass: one coverer per busy teammate, released when the session ends.
public sealed class CoverMateBoard
{
    private readonly Dictionary<string,string> claims=new();
    public bool Available(string owner,string mate,string operation,string sector,double x,double z,double now,CoverMateConfig c)
        =>!claims.TryGetValue(mate,out var claimed)||claimed==owner;
    public bool Reserve(string owner,string mate,string operation,string sector,double x,double z,double now,CoverMateConfig c)
    {if(!Available(owner,mate,operation,sector,x,z,now,c))return false;claims[mate]=owner;return true;}
    public void Release(string owner)
    {var remove=new List<string>();foreach(var pair in claims)if(pair.Value==owner)remove.Add(pair.Key);foreach(var mate in remove)claims.Remove(mate);}
    public void Clear()=>claims.Clear();
}
