using Comet.Core;
using System.Text.Json;

static class HandsRecoveryTests
{
    public static int Run()
    {
        int n=0;void Check(bool ok,string why){n++;if(!ok)throw new Exception("W10: "+why);}
        void Invalid(Action a){try{a();}catch(ArgumentException){n++;return;}throw new Exception("W10 accepted invalid settings");}
        var c=new HandsRecoveryConfig();c.Validate();Check(c.Enabled&&c.ReloadStaleSeconds==6&&c.GrenadeStaleSeconds==2&&c.CheckSeconds==.5&&c.RetrySeconds==2&&c.MaxAttempts==2,"approved defaults");
        var old=JsonSerializer.Deserialize<CometConfig>("{}")!;old.Validate();Check(old.HandsRecovery.Enabled,"old config loads defaults");
        Invalid(()=>new CometConfig{HandsRecovery=null!}.Validate());
        foreach(double bad in new[]{double.NaN,double.PositiveInfinity,double.NegativeInfinity,-1,100})
        {
            Invalid(()=>new HandsRecoveryConfig{ReloadStaleSeconds=bad}.Validate());Invalid(()=>new HandsRecoveryConfig{GrenadeStaleSeconds=bad}.Validate());
            Invalid(()=>new HandsRecoveryConfig{CheckSeconds=bad}.Validate());Invalid(()=>new HandsRecoveryConfig{RetrySeconds=bad}.Validate());
        }
        foreach(int bad in new[]{0,4})Invalid(()=>new HandsRecoveryConfig{MaxAttempts=bad}.Validate());
        var saved=JsonSerializer.Deserialize<HandsRecoveryConfig>(JsonSerializer.Serialize(new HandsRecoveryConfig{Enabled=false,ReloadStaleSeconds=9,GrenadeStaleSeconds=3,CheckSeconds=.2,RetrySeconds=4,MaxAttempts=1}))!;
        saved.Validate();Check(!saved.Enabled&&saved.ReloadStaleSeconds==9&&saved.GrenadeStaleSeconds==3&&saved.CheckSeconds==.2&&saved.RetrySeconds==4&&saved.MaxAttempts==1,"settings roundtrip");
        HandsRecoveryFrame Frame(string kind="reload")=>new(){Kind=kind,Held=kind=="reload"?"rifle":"grenade",TargetWeapon="rifle",TargetSlot="FirstPrimaryWeapon",ReloadFlag=kind=="reload",Operation="Idling",Action="StandAndShoot",Contact="known"};
        string Tick(HandsRecoveryPolicy p,HandsRecoveryFrame f,double now){f.Now=now;return p.Observe(f,c);}
        var traces=new List<HandsRecoveryTrace>();
        void Snapshot(HandsRecoveryPolicy p){traces.Add(JsonSerializer.Deserialize<HandsRecoveryTrace>(JsonSerializer.Serialize(p.Trace,new JsonSerializerOptions{IncludeFields=true}),new JsonSerializerOptions{IncludeFields=true})!);}
        {
            var p=new HandsRecoveryPolicy();var f=Frame();
            for(double now=0;now<6;now+=.5)Check(Tick(p,f,now)=="","no early clear");
            Check(Tick(p,f,6)=="clear-reload","continuous six seconds required");p.Request(6);p.Result(true,6,c);
            Check(p.Pending&&p.Trace!.State=="requested","accepted clear still awaits observation");
            f.ReloadFlag=false;f.Kind="";Tick(p,f,6.5);Check(p.Trace!.State=="recovered"&&!p.Pending&&p.Trace.ValidFor==0,"observed flag clear");
        }
        foreach(string block in new[]{"hands-native-reload-active","hands-medical-active","hands-weapon-changing","hands-feint-owned","hands-grenade-action-active","hands-unknown-or-committed-operation"})
        {
            var p=new HandsRecoveryPolicy();var f=Frame();
            for(double now=0;now<5;now+=.5)Tick(p,f,now);
            f.Block=block;for(double now=5;now<=65;now+=.5)Check(Tick(p,f,now)=="","arbitrarily long legitimate operation untouched");
            f.Block="";for(double now=65.5;now<71.5;now+=.5)Check(Tick(p,f,now)=="","confirmation restarts after legitimate operation");
            Check(Tick(p,f,71.5)=="clear-reload","six stale seconds after long operation");
        }
        {
            var p=new HandsRecoveryPolicy();var f=Frame();
            for(double now=0;now<6;now+=.5){f.Generation++;Check(Tick(p,f,now)=="","ammo/operation progress resets stale timer");}
            f.Now=100;Check(p.Observe(f,c)==""&&p.Trace!.ConfirmedSeconds==0,"observation gap not inactivity");
            Check(Tick(p,f,1)==""&&p.Trace!.ConfirmedSeconds==0,"clock reversal cannot shortcut");
            f.NativeReload=true;Check(Tick(p,f,1.5)=="","native reload flag independently protects");
        }
        {
            var p=new HandsRecoveryPolicy();var f=Frame("grenade");
            for(double now=0;now<2;now+=.5)Check(Tick(p,f,now)=="","grenade confirmation");
            Check(Tick(p,f,2)=="restore-weapon","abandoned idle grenade");Snapshot(p);
            p.Request(2);p.Result(true,2,c);Snapshot(p);Check(p.BlocksSwitch&&!p.Trace!.Ready,"accepted restoration not ready");
            f.Held="rifle";f.Kind="";f.Block="hands-weapon-changing";Tick(p,f,2.5);Snapshot(p);
            Check(p.Pending&&p.Trace!.InHands&&!p.Trace.Ready,"actual gun drawing is not ready");
            f.Block="";f.GunReady=true;Tick(p,f,3);Snapshot(p);
            Check(!p.Pending&&p.Trace!.Ready&&p.Trace.State=="recovered","exact existing gun ready completes");
        }
        foreach(bool accept in new[]{false,true})
        {
            var p=new HandsRecoveryPolicy();var f=Frame("grenade");int calls=0;
            for(double now=0;now<120;now+=.5)
            {
                if(Tick(p,f,now)!=""){p.Request(now);p.Result(accept,now,c);calls++;}
            }
            Check(calls==2&&p.Trace!.State=="exhausted"&&!p.Pending,"refused/unconfirmed native calls bounded to two");
            f.Generation++;Tick(p,f,120);Check(p.Trace!.State=="exhausted","request-generated operation changes cannot renew attempts");
            f.Held="another-grenade";Tick(p,f,120.5);Check(p.Trace!.Attempts==0,"different actual item starts new incident");
        }
        {
            var p=new HandsRecoveryPolicy();var f=Frame("grenade");for(double now=0;now<=2;now+=.5)Tick(p,f,now);
            p.Request(2);p.Result(false,2,c);
            for(double now=2.5;now<4;now+=.5)Check(Tick(p,f,now)=="","two-second retry delay");
            Check(Tick(p,f,4)=="restore-weapon","unchanged confirmed incident retries after exactly two seconds");
            p.Request(4);p.Result(true,4,c);f.Held="different-gun";f.GunReady=true;f.Kind="";
            for(double now=4.5;now<=10;now+=.5)Tick(p,f,now);
            Check(p.Trace!.State!="recovered"&&!p.Trace.Ready,"different gun cannot confirm our restoration");
        }
        {
            var p=new HandsRecoveryPolicy();var f=Frame("grenade");f.TargetWeapon="";f.TargetSlot="";
            for(double now=0;now<20;now+=.5)Check(Tick(p,f,now)=="","never invent a missing weapon");
            Check(p.Trace!.Reason=="hands-no-existing-weapon"&&p.Trace.Attempts==0,"no target diagnostic");
            p.End("raid-ended");Check(!p.Pending&&p.Trace.ValidFor==0,"release lease");
            f.Now=double.NaN;Check(p.Observe(f,c)=="","bad clock safe");
        }
        var options=new JsonSerializerOptions{IncludeFields=true,PropertyNamingPolicy=JsonNamingPolicy.CamelCase,WriteIndented=true};
        var dir=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../artifacts"));Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir,"w10-wire-traces.json"),JsonSerializer.Serialize(traces,options));
        File.WriteAllText(Path.Combine(dir,"w10-hands-recovery.json"),JsonSerializer.Serialize(new{handsRecovery=traces[1],handsRecoveryConfig=c},options));
        Console.WriteLine($"W10: {n} checks passed");return n;
    }
}
