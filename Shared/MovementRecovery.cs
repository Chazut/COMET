using System;
namespace Comet.Core;

public sealed class MovementRecoveryConfig
{
    public bool Enabled {get;set;}=true;
    public bool Teleport {get;set;}=false;
    public double SampleInterval {get;set;}=.5;
    public double ProgressDistance {get;set;}=.6;
    public double EarlyJumpAfter {get;set;}=1;
    public double RepathAfter {get;set;}=3;
    public double JumpAfter {get;set;}=5;
    public double TeleportAfter {get;set;}=8;
    public double StopAfter {get;set;}=14;
    public double TeleportDistance {get;set;}=2;
    public double HumanDistance {get;set;}=50;
    public void Validate()
    {
        CometConfig.Range(SampleInterval,.25,1,nameof(SampleInterval));
        CometConfig.Range(ProgressDistance,.3,1,nameof(ProgressDistance));
        CometConfig.Range(EarlyJumpAfter,1,3,nameof(EarlyJumpAfter));
        CometConfig.Range(RepathAfter,2,6,nameof(RepathAfter));
        CometConfig.Range(JumpAfter,4,10,nameof(JumpAfter));
        CometConfig.Range(TeleportAfter,6,15,nameof(TeleportAfter));
        CometConfig.Range(StopAfter,10,30,nameof(StopAfter));
        CometConfig.Range(TeleportDistance,.5,2,nameof(TeleportDistance));
        CometConfig.Range(HumanDistance,50,150,nameof(HumanDistance));
        if(!(EarlyJumpAfter<RepathAfter&&RepathAfter<JumpAfter&&JumpAfter<TeleportAfter&&TeleportAfter<StopAfter))
            throw new ArgumentException("Recovery stages must be in increasing order.");
    }
}
public sealed class MovementRecoveryFrame
{
    public double Now,X,Y,Z;
    public bool Scoped=true,Intent,MissingCoverRoute;
    public string Pause="",Owner="",Action="",Route="",Contact="";
}
public sealed class MovementRecoveryTrace
{
    public double StationaryPauseSeconds {get;set;}
    public string SelfAction {get;set;}="";
    public bool Healing {get;set;}
    public bool Reloading {get;set;}
    public bool HandsBusy {get;set;}
    public string State {get;set;}="idle";
    public string Reason {get;set;}="recovery-no-movement-intent";
    public string Step {get;set;}="";
    public string Result {get;set;}="";
    public string Owner {get;set;}="";
    public string Action {get;set;}="";
    public string Route {get;set;}="";
    public string Contact {get;set;}="";
    public int Incident {get;set;}
    public double StalledSeconds {get;set;}
    public double Displacement {get;set;}
    public double ValidFor {get;set;}
    public bool TeleportEnabled {get;set;}
    public bool MissingCoverRoute {get;set;}
}

// No Unity references and no path identity based reset. Only physical progress clears an incident.
public sealed class MovementRecoveryPolicy
{
    private double last=double.NaN,x,y,z,stall;
    private bool anchored,eligible,exhausted,teleported;
    private int steps,incident;
    public MovementRecoveryTrace Trace {get;private set;}=new();
    public string RepositionBlock(MovementRecoveryConfig c)=>c.Enabled&&anchored&&(exhausted||stall>=c.RepathAfter)?"movement-awaiting-real-progress":"";
    public bool Handling=>anchored&&!exhausted&&eligible&&stall>=1;
    public string Observe(MovementRecoveryFrame f,MovementRecoveryConfig c)
    {
        if(!c.Enabled||!f.Scoped||!Finite(f.Now)||!Finite(f.X)||!Finite(f.Y)||!Finite(f.Z))
        {End("recovery-out-of-scope");return "";}
        if(Finite(last)&&f.Now<last)End("recovery-clock-reset");
        double dt=Finite(last)&&f.Now-last<=Math.Max(1.5,c.SampleInterval*3)?f.Now-last:0;last=f.Now;
        double moved=anchored?Math.Sqrt((f.X-x)*(f.X-x)+(f.Z-z)*(f.Z-z)):0;
        Trace.Owner=f.Owner;Trace.Action=f.Action;Trace.Route=f.Route;Trace.Contact=f.Contact;
        Trace.TeleportEnabled=c.Teleport;Trace.MissingCoverRoute=f.MissingCoverRoute;Trace.Displacement=moved;Trace.ValidFor=0;
        if(anchored&&moved>=c.ProgressDistance&&!teleported)
        {
            bool recovered=stall>=c.EarlyJumpAfter;
            Anchor(f);Trace.State=recovered?"recovered":"tracking";Trace.Reason=recovered?"recovery-movement-observed":"recovery-moving";
            Trace.StalledSeconds=0;Trace.ValidFor=recovered?0:c.SampleInterval*2;return "";
        }
        if(teleported){x=f.X;y=f.Y;z=f.Z;teleported=false;dt=0;} // A correction is not proof of recovery.
        if(!f.Intent||f.Pause.Length>0)
        {
            eligible=false;Trace.State=exhausted?"exhausted":"paused";
            Trace.Reason=f.Pause.Length>0?f.Pause:"recovery-no-movement-intent";return "";
        }
        if(!anchored)Anchor(f);
        if(eligible)stall+=dt;
        eligible=true;Trace.StalledSeconds=stall;Trace.Incident=incident;
        if(exhausted){Trace.State="exhausted";Trace.Reason="recovery-awaiting-real-progress";return "";}
        Trace.ValidFor=c.SampleInterval*2;
        Trace.State="tracking";Trace.Reason="recovery-observing-movement";
        for(int i=0;i<5;i++)if(stall>=Threshold(c,i)&&(steps&(1<<i))==0)
        {
            steps|=1<<i;Trace.Step=Commands[i];Trace.Result="";
            if(f.MissingCoverRoute && i is 0 or 2 or 3)
            {Trace.State="skipped";Trace.Reason="recovery-cover-needs-route";return "";}
            if(i==3&&!c.Teleport){Trace.State="skipped";Trace.Reason="recovery-teleport-disabled";return "";}
            Trace.State="requested";Trace.Reason="recovery-stall-stage";
            if(i==4){exhausted=true;Trace.ValidFor=0;}
            return Commands[i];
        }
        return "";
    }
    private static readonly string[] Commands={"early-vault","repath","vault-or-jump","teleport","stop"};
    private static double Threshold(MovementRecoveryConfig c,int i)=>i==0?c.EarlyJumpAfter:i==1?c.RepathAfter:i==2?c.JumpAfter:i==3?c.TeleportAfter:c.StopAfter;
    private void Anchor(MovementRecoveryFrame f)
    {x=f.X;y=f.Y;z=f.Z;anchored=true;eligible=false;exhausted=false;teleported=false;stall=0;steps=0;incident++;Trace.Step="";Trace.Result="";}
    public void Result(bool accepted,string reason,bool teleport=false)
    {Trace.State=exhausted?"exhausted":accepted?"attempted":"rejected";Trace.Reason=reason;Trace.Result=accepted?"accepted-awaiting-movement":"refused";teleported=accepted&&teleport;}
    public void End(string reason)
    {last=double.NaN;anchored=eligible=exhausted=teleported=false;stall=0;steps=0;Trace=new(){State="ended",Reason=reason,Incident=incident};}
    private static bool Finite(double n)=>!double.IsNaN(n)&&!double.IsInfinity(n);
}
