using System;

namespace Comet.Core;

public sealed class HandsRecoveryConfig
{
    public bool Enabled { get; set; } = true;
    public double ReloadStaleSeconds { get; set; } = 6;
    public double GrenadeStaleSeconds { get; set; } = 2;
    public double CheckSeconds { get; set; } = .5;
    public double RetrySeconds { get; set; } = 2;
    public int MaxAttempts { get; set; } = 2;
    public void Validate()
    {
        CometConfig.Range(ReloadStaleSeconds,2,20,nameof(ReloadStaleSeconds));
        CometConfig.Range(GrenadeStaleSeconds,1,10,nameof(GrenadeStaleSeconds));
        CometConfig.Range(CheckSeconds,.1,2,nameof(CheckSeconds));
        CometConfig.Range(RetrySeconds,.5,10,nameof(RetrySeconds));
        CometConfig.Range(MaxAttempts,1,3,nameof(MaxAttempts));
    }
}
public sealed class HandsRecoveryFrame
{
    public double Now;
    public string Kind="",Held="",TargetWeapon="",TargetSlot="",Block="",Operation="",Action="",Contact="";
    public int Generation;
    public bool ReloadFlag,NativeReload,GunReady,ReloadIdle,ReloadStranded,SelectorChanging,SwitchCompleted;
    public string NativePhase="";
}
public sealed class HandsRecoveryTrace
{
    public string State="observing",Reason="hands-incoherent",Kind="",Held="",TargetWeapon="",TargetSlot="",Operation="",Action="",Contact="";
    public int Attempts,Limit;
    public double ConfirmedSeconds,RequiredSeconds,RetryRemaining,RequestAge,ValidFor;
    public bool ReloadFlag,NativeReload,Accepted,InHands,Ready,ReloadIdle,ReloadStranded,SelectorChanging,SwitchCompleted;
    public string NativePhase="";
    public string[] Alternatives={"Wait for legitimate reloads, medicine, weapon changes and grenade actions",
        "Request the existing weapon through the native selector; never create items",
        "After 30 seconds with unchanged native phases and an idle animator, reconcile an existing reload only when its actual magazine transaction matches the held weapon",
        "Clear a stale switch only after the held weapon and inventory agree; replay a missing reload idle notification only after the magazine is inserted and animation is idle",
        "Stop after the attempt limit until the actual hands state changes"};
}

// Per-bot scalar watchdog. No controller references, inventory mutation or clock-based success.
public sealed class HandsRecoveryPolicy
{
    public const double RequestTimeout=5;
    public HandsRecoveryTrace? Trace { get; private set; }
    public bool Pending { get; private set; }
    public bool BlocksSwitch=>Pending&&Trace?.Kind=="grenade";
    private double last=-1,since=-1,requestedAt,retryAt;
    private int generation=-1;
    public string Observe(HandsRecoveryFrame f,HandsRecoveryConfig c)
    {
        if(!Finite(f.Now)||f.Now<0||!c.Enabled){End("hands-disabled-or-clock-invalid");return "";}
        bool gap=last>=0&&(f.Now<last||f.Now-last>Math.Max(1,c.CheckSeconds*3));
        last=f.Now;
        var t=Trace;
        if(t!=null&&t.ValidFor>0)
        {
            Update(t,f,c);
            // Acceptance is only an acknowledgement. These observations prove the outcome.
            if((t.Kind=="reload"||t.Kind=="reload-idle")&&!f.ReloadFlag&&!f.NativeReload)
            {Finish("recovered",t.Attempts==0?"hands-native-reload-recovered":t.Kind=="reload-idle"?"hands-reload-idle-completed":"hands-reload-flag-cleared");return "";}
            if(t.Kind=="switch"&&!f.SelectorChanging&&f.GunReady)
            {Finish("recovered","hands-selector-ready-observed");return "";}
            if(t.Kind=="grenade"&&f.GunReady&&f.Held==t.TargetWeapon&&t.TargetWeapon.Length>0)
            {t.InHands=t.Ready=true;Finish("recovered","hands-weapon-ready-observed");return "";}
            if(Pending)
            {
                t.RequestAge=Math.Max(0,f.Now-requestedAt);
                t.InHands=t.Kind=="grenade"&&f.Held==t.TargetWeapon;
                if(gap){Finish("ended","hands-observation-gap");return "";}
                if(t.RequestAge<RequestTimeout)
                {t.State="waiting-native";t.Reason=f.Block.Length>0?f.Block:"hands-waiting-native-result";return "";}
                Pending=false;retryAt=f.Now+c.RetrySeconds;
                t.State="timeout";t.Reason="hands-native-result-unconfirmed";return "";
            }
            if(f.Kind!=t.Kind||f.Held!=t.Held){Finish("ended","hands-state-changed");t=null;}
        }
        if(f.Kind.Length==0||f.Held.Length==0)return "";
        if(t==null||t.ValidFor==0)
        {
            Trace=t=new(){Kind=f.Kind,Held=f.Held,Limit=c.MaxAttempts};since=-1;retryAt=0;generation=f.Generation;
        }
        Update(t,f,c);
        if(gap||generation!=f.Generation){since=-1;generation=f.Generation;}
        if(f.Block.Length>0||f.NativeReload&&!f.ReloadIdle&&!f.ReloadStranded||f.Kind=="switch"&&!f.SwitchCompleted)
        {since=-1;t.ConfirmedSeconds=0;t.State="waiting";t.Reason=f.Block.Length>0?f.Block:"hands-native-reload-active";return "";}
        if(t.Attempts>=c.MaxAttempts)
        {t.State="exhausted";t.Reason="hands-attempt-limit";return "";}
        if(f.Now<retryAt){t.State="waiting";t.Reason="hands-retry-delay";return "";}
        if(since<0)since=f.Now;
        t.ConfirmedSeconds=Math.Max(0,f.Now-since);t.State="observing";t.Reason="hands-confirming-incoherence";
        if(t.ConfirmedSeconds+1e-8<t.RequiredSeconds)return "";
        if(f.Kind=="grenade"&&(f.TargetWeapon.Length==0||f.TargetSlot.Length==0))
        {t.State="waiting";t.Reason="hands-no-existing-weapon";return "";}
        return f.Kind switch {"reload"=>"clear-reload","reload-idle"=>f.ReloadStranded?"reconcile-stranded-reload":"finish-reload-idle","switch"=>"clear-switch",_=>"restore-weapon"};
    }
    private void Update(HandsRecoveryTrace t,HandsRecoveryFrame f,HandsRecoveryConfig c)
    {
        t.Operation=f.Operation;t.Action=f.Action;t.Contact=f.Contact;t.ReloadFlag=f.ReloadFlag;t.NativeReload=f.NativeReload;
        t.NativePhase=f.NativePhase;t.ReloadIdle=f.ReloadIdle;t.ReloadStranded=f.ReloadStranded;t.SelectorChanging=f.SelectorChanging;t.SwitchCompleted=f.SwitchCompleted;
        t.Limit=c.MaxAttempts;t.RequiredSeconds=f.ReloadStranded?30:t.Kind=="reload-idle"?Math.Max(16,c.ReloadStaleSeconds):t.Kind=="grenade"?c.GrenadeStaleSeconds:c.ReloadStaleSeconds;
        t.ValidFor=6;t.RetryRemaining=Math.Max(0,retryAt-f.Now);
        if(!Pending){t.TargetWeapon=f.TargetWeapon;t.TargetSlot=f.TargetSlot;}
    }
    // Reserve before invoking native code, including callbacks that can reenter the selector.
    public void Request(double now)
    {
        if(Trace==null||Pending)return;
        Pending=true;requestedAt=now;Trace.Attempts++;Trace.State="requested";Trace.Reason="hands-native-request";
        Trace.Accepted=Trace.InHands=Trace.Ready=false;Trace.RequestAge=0;
    }
    public void Result(bool accepted,double now,HandsRecoveryConfig c)
    {
        if(Trace==null||!Pending)return;
        Trace.Accepted=accepted;
        if(!accepted){Pending=false;retryAt=now+c.RetrySeconds;Trace.State="refused";Trace.Reason="hands-native-refused";}
    }
    public void End(string reason){if(Trace!=null)Finish("ended",reason);last=since=-1;generation=-1;}
    private void Finish(string state,string reason){Pending=false;Trace!.State=state;Trace.Reason=reason;Trace.ValidFor=0;since=-1;}
    private static bool Finite(double v)=>!double.IsNaN(v)&&!double.IsInfinity(v);
}
