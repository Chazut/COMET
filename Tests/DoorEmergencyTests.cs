using Comet.Core;
using Comet.Server;
using System.Text.Json;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

internal static class DoorEmergencyTests
{
    internal static int Run()
    {
        int n=0;void Check(bool ok,string why){if(!ok)throw new Exception("D13: "+why);n++;}
        void Invalid(Action a){try{a();}catch(ArgumentException){n++;return;}throw new Exception("D13 invalid config accepted");}
        var c=new DoorEmergencyConfig();c.Validate();
        Check(c.Enabled&&c.VisibleDistance==10&&c.SoundAge==1.2&&c.SoundDoorDistance==7&&c.MaxPath==5&&c.Duration==3&&c.Stabilization==.5,"approved defaults");
        Check(JsonSerializer.Deserialize<CometConfig>("{}")!.DoorEmergency.Duration==3,"old config migration");
        foreach(var prop in typeof(DoorEmergencyConfig).GetProperties().Where(x=>x.PropertyType==typeof(double)))
        foreach(var bad in new[]{-1d,double.NaN,double.PositiveInfinity,double.NegativeInfinity,1000})
        {var invalid=new DoorEmergencyConfig();prop.SetValue(invalid,bad);Invalid(invalid.Validate);}
        Invalid(()=>new DoorEmergencyConfig{Duration=.5,Stabilization=1}.Validate());
        string dir=Path.Combine(Path.GetTempPath(),"comet-d13-"+Guid.NewGuid().ToString("N"));
        try
        {
            var store=new ConfigStore(Path.Combine(dir,"config.json"));var cfg=store.Read();cfg.DoorEmergency.Enabled=false;cfg.DoorEmergency.MaxPath=4;
            var saved=store.Save(cfg,cfg.Revision);Check(!store.Read().DoorEmergency.Enabled&&store.Read().DoorEmergency.MaxPath==4,"authoritative persistence");
            saved.DoorEmergency.Duration=9;Invalid(()=>store.Save(saved,saved.Revision));Check(store.Read().DoorEmergency.Duration==3,"invalid save atomic");
        }finally{Directory.Delete(dir,true);}
        Check(DoorEmergencyRules.Visible(true,.5,10,c),"vision exact limits");
        foreach(var v in new[]{(false,0d,1d),(true,.51,1d),(true,0d,10.01),(true,-1d,1d),(true,double.NaN,1d),(true,0d,double.PositiveInfinity)})
            Check(!DoorEmergencyRules.Visible(v.Item1,v.Item2,v.Item3,c),"nonpersonal or stale vision rejected");
        foreach(string type in new[]{"Sprint","Jump","Land","Door","DoorBreach"})Check(DoorEmergencyRules.Heard(type,true,false,1.2,7,c),"approach sound "+type);
        foreach(string type in new[]{"FootStep","GunShot","shared-heard","", "JumpLandDoorBreach"})Check(!DoorEmergencyRules.Heard(type,true,false,0,1,c),"unrelated cue "+type);
        Check(!DoorEmergencyRules.Heard("Sprint",false,false,0,1,c),"shared cue refused");Check(!DoorEmergencyRules.Heard("Sprint",true,true,0,1,c),"other floor refused");
        Check(!DoorEmergencyRules.Heard("Sprint",true,false,1.21,1,c)&&!DoorEmergencyRules.Heard("Sprint",true,false,0,7.01,c),"hearing limits");
        Check(!DoorEmergencyRules.Heard("Sprint",true,false,double.NaN,1,c)&&!DoorEmergencyRules.Heard("Sprint",true,false,0,double.NaN,c),"bad hearing numbers");
        Check(DoorEmergencyRules.CanStart(true,true,false,true,false,false),"feint eligible");
        Check(DoorEmergencyRules.CanStart(true,true,false,false,true,false),"preparation eligible");
        Check(!DoorEmergencyRules.CanStart(true,true,false,false,false,false),"no ordinary combat takeover");
        Check(!DoorEmergencyRules.CanStart(false,true,false,true,true,false),"disabled");Check(!DoorEmergencyRules.CanStart(true,false,false,true,true,false),"nonPMC/ghost/nonauthority");
        Check(!DoorEmergencyRules.CanStart(true,true,true,true,true,false),"urgent medical/grenade/squad wins");
        Check(!DoorEmergencyRules.CanStart(true,true,false,true,true,true),"committed throw not cancelled");
        Check(DoorEmergencyRules.Route(true,true,true,1,2,5,.75,.25,false,c)=="","safe route exact limits");
        Check(DoorEmergencyRules.Route(false,true,true,0,3,2,0,0,false,c)=="incomplete-route","partial route");
        Check(DoorEmergencyRules.Route(true,false,true,0,3,2,0,0,false,c)=="obstacle-or-clearance","obstacle");
        Check(DoorEmergencyRules.Route(true,true,false,0,3,2,0,0,false,c)=="ally-or-reservation","ally");
        Check(DoorEmergencyRules.Route(true,true,true,0,3,5.01,0,0,false,c)=="path-too-long","long route");
        Check(DoorEmergencyRules.Route(true,true,true,0,3,2,.76,0,false,c)=="unsafe-height-or-advance","floor change");
        Check(DoorEmergencyRules.Route(true,true,true,0,3,2,0,.26,false,c)=="unsafe-height-or-advance","advance toward danger");
        Check(DoorEmergencyRules.Route(true,true,true,2,2,2,0,0,false,c)=="no-better-protection","keep good cover");
        Check(DoorEmergencyRules.Route(true,true,true,0,1,2,0,0,false,c)=="no-better-protection","poor cover");
        Check(DoorEmergencyRules.Route(true,true,true,0,3,2,0,0,true,c)=="other-known-threat-exposure","other known threat");
        foreach(var bad in new[]{double.NaN,double.PositiveInfinity,-1d,0d})Check(DoorEmergencyRules.Route(true,true,true,0,3,bad,0,0,false,c)!="","invalid length");
        DoorEmergencyTrace Ticket(bool visual=true,string id="one")=>new(){Attempt=id,Door="door",Plan="D05",Phase="drawing",Target="known",Evidence=visual?"personal-vision":"personal-sound:Sprint",Confirmed=visual,Hands="GrenadeHandsController",Distance=6,EvidenceAge=.1};
        DoorEmergency Start(bool visual=true){var p=new DoorEmergency();Check(p.Begin(Ticket(visual),100,c),"episode start");return p;}
        var p=Start();Check(p.Tick(100.49,true,false,c),"short stabilization");Check(!p.Tick(100.5,true,false,c)&&p.Trace!.Reason=="weapon-ready-native-combat","ready ends exactly at .5, not 1.5");
        Check(!p.Begin(Ticket(),101,c),"same attempt consumed");Check(p.Begin(Ticket(id:"two"),101,c),"new attempt independent");
        p=Start();Check(!p.Tick(100.1,false,true,c),"urgent action interrupts stabilization");
        p=Start();Check(p.Tick(102.99,false,false,c)&&!p.Tick(103,false,false,c),"fixed 3-second cap without weapon");
        p=Start(false);Check(!p.Trace!.Confirmed&&p.Trace.Choice=="recover-only"&&!p.Trace.MoveAccepted,"heard warning never implies retreat");
        Check(!p.Tick(100.01,true,false,c),"heard warning needs no artificial hold");
        p=Start(false);p.Confirm("seen",.1,8);Check(p.Deadline==103&&p.Trace!.Confirmed,"new vision upgrades evidence, never refunds time");
        Check(!p.Tick(103,false,false,c),"upgraded evidence original deadline");
        p=Start();p.Observe(2,2,true,true);Check(!p.Trace!.ActionObserved&&!p.Trace.Arrived,"request is not acceptance");
        p.Accepted(true);p.Observe(0,0,false,false);Check(p.Trace!.ActionObserved&&!p.Trace.MovementObserved,"native callback is not movement");
        p.Trace.MoveAccepted=true;p.Trace.Choice="retreat";p.Observe(.2,0,true,true);Check(!p.Trace.Arrived,"no progress cannot arrive");
        p.Observe(.2,.2,false,true);Check(p.Trace.MovementObserved&&!p.Trace.Arrived,"movement alone is not arrival");
        p.Observe(.3,.3,true,true);Check(p.Trace.Arrived,"complete native path and close destination confirm arrival");
        p.Accepted(false);Check(!p.Active&&p.Trace.NativeAccepted&&p.Trace.ActionObserved,"later refusal retains historical proof");
        p=Start();p.Accepted(false);Check(!p.Active&&!p.Trace!.NativeAccepted,"initial refusal stays unaccepted");
        p=Start();Check(!p.Tick(99,false,false,c),"backwards clock ends");p=Start();Check(!p.Tick(double.NaN,false,false,c),"nonfinite clock ends");
        p=Start();Check(!p.Tick(101,false,false,new(){Enabled=false}),"disable ends active episode");p.Clear();Check(p.Trace==null&&!p.Active,"raid cleanup");
        // Exercise real feint and D12 policies together: no instant hands, item refund, or safe resume.
        var f=new DoorFakeGrenade();var fc=new DoorFakeGrenadeConfig();fc.Chances["Chad"]=100;
        Check(f.Begin("door","known","heard","Chad",new(0,0,5),1,100,0,fc),"real fake policy begins");f.Draw("grenade","rifle",100);f.Abort("door-emergency",100.1);
        Check(f.Trace!.Interrupted.Length>0&&!f.Trace.WeaponReady,"abort still waits for real weapon");
        Check(!f.Begin("door","known","heard","Chad",new(0,0,5),1,100.2,0,fc),"aborted feint cannot reroll");
        var resume=new DoorInterruption();resume.Interrupt(new(){Attempt="old",Plan="D09",Phase="holding",Door="door",Target="known",SafeStage=true,FeintConsumed=true},100,120,"incoming-fire","","heard",new());
        resume.Abandon("door-emergency-cancel-plan");Check(!resume.Ready(104,"known",true,new())&&!resume.Pending&&!resume.Resumed,"D12 cannot resume emergency-abandoned plan");
        // Native contract checks against the exact unmodified SAIN reference used by the build.
        using(var pe=new PEReader(File.OpenRead("references/sain-4.5.1/SAIN.dll")))
        {
            var md=pe.GetMetadataReader();foreach(var name in new[]{"SeekCoverAction","FreezeAction"})
            {var type=md.TypeDefinitions.Select(h=>md.GetTypeDefinition(h)).Single(t=>md.GetString(t.Name)==name);Check(type.GetMethods().Select(h=>md.GetMethodDefinition(h)).Any(m=>md.GetString(m.Name)=="Update"),"official native action "+name);}
        }
        p=Start();p.Trace!.Choice="retreat";p.Trace.PathLength=3;p.Trace.X=1;p.Trace.Z=-3;p.Trace.SafePosition=true;p.Trace.Alternatives=new[]{"Stay: exposed","Local cover 1: route-obstacle","Local cover 2: verified"};p.Accepted(true);p.Trace.MoveAccepted=true;p.Observe(.5,.5,false,false);p.Tick(101,false,false,c);
        var json=JsonSerializer.Serialize(new{schema=1,botInstance="instance",doorEmergency=p.Trace,doorEmergencyConfig=c},new JsonSerializerOptions{PropertyNamingPolicy=JsonNamingPolicy.CamelCase,WriteIndented=true});
        Directory.CreateDirectory("artifacts");File.WriteAllText("artifacts/d13-door-emergency.json",json);
        Console.WriteLine($"D13: {n} checks passed (evidence, geometry gates, recovery, original attempt, authoritative config, native contracts)");return n;
    }
}
