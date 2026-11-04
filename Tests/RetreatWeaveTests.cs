using Comet.Core;
using Comet.Server;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.Json;

internal static class RetreatWeaveTests
{
    public static int Run()
    {
        int n=0;void Check(bool v,string why){if(!v)throw new Exception("M11: "+why);n++;}
        void Invalid(Action a){try{a();}catch(ArgumentException){n++;return;}throw new Exception("M11 accepted invalid config");}
        var c=new RetreatWeaveConfig();c.Validate();
        RetreatWeaveFrame F(double now=10,double goal=20)=>new(){Now=now,VisionAge=.1,FireAge=1000,Distance=30,RearAngle=150,Speed=5,ForwardDot=1,
            GoalDistance=goal,GeometryAge=0,Action="Retreat",Target="seen-enemy",Context="cover-A",Source="personal-vision",Position=new(now,0,0),Known=new(-20,0,0),Goal=new(40,0,0),
            Sprinting=true,Grounded=true,CanJump=true,Left=true,Right=true,Straight=true,JumpSafe=true,LandingSafe=true};
        double Zero()=>0;double High()=>.999;
        Check(c.Enabled&&c.Weave&&c.HeadDown&&c.Jumps&&c.Angle==30&&c.Pitch==55&&c.SwitchMin==.45&&c.SwitchMax==.8,"zzap movement defaults");
        Check(c.SwitchJumpChance==20&&c.LandingJumpChance==60&&c.JumpMinSpeed==3.8&&c.Distance==80,"zzap jumping defaults");
        var p=new RetreatWeavePolicy();Check(p.Tick(F(),c,Zero),"start running away");
        Check(p.Trace!.Pitch==55&&Math.Abs(p.Trace.Yaw)==30&&p.Trace.Jump==""&&p.Trace.Switches==0,"first switch delayed, independent head and weave");
        p.Tick(F(10.44,18),c,Zero);Check(p.Trace!.JumpRequests==0,"no immediate startup jump");
        p.Tick(F(10.46,17),c,Zero);Check(p.Trace!.Jump=="switch"&&p.Trace.Yaw==0&&p.Trace.JumpRequests==1,"20 percent switch opportunity");
        p.JumpResult(true,10.46);Check(p.Trace.AcceptedJumps==1&&p.Trace.Takeoffs==0,"native acceptance is not takeoff");
        var air=F(10.55,16);air.Grounded=false;p.Tick(air,c,Zero);Check(p.Trace!.Yaw==0&&p.Trace.Takeoffs==1,"straight flight and observed takeoff");
        Check(p.ObserveFlight(true,11.1)&&p.Trace.Landings==1,"landing observed outside movement callback");
        p.Tick(F(11.4,12),c,Zero);Check(p.Trace!.Jump=="switch"&&p.Trace.JumpRequests==2,"multiple switch jumps retained");
        p=new();p.Tick(F(10,3.4),c,Zero);Check(p.Trace!.Jump=="landing"&&p.Trace.LandingRollSpent,"arrival jump uses one roll at 3-5 m");
        p.JumpResult(false,10);p.End("interrupted");p.Tick(F(11,3.2),c,Zero);Check(p.Trace!.JumpRequests==1&&p.Trace.Jump=="","same cover does not reroll after refusal or interruption");
        var other=F(12,3.2);other.Context="cover-B";p.Tick(other,c,Zero);Check(p.Trace!.Jump=="landing","new destination permits a new landing roll");
        p=new();p.Tick(F(10,3.4),c,High);Check(p.Trace!.LandingRollSpent&&p.Trace.JumpRequests==0,"failed 60 percent roll consumed");
        p.Tick(F(11,3.2),c,Zero);Check(p.Trace!.JumpRequests==0,"no second arrival roll");
        p=new();p.Tick(F(),c,Zero);p.Tick(F(10.5,5.5),c,Zero);Check(p.Trace!.Jump=="","no intermediate hop within 6m");
        p=new();p.Tick(F(),c,High);p.Tick(F(10.81,18),c,High);Check(p.Trace!.Switches==1&&p.Trace.JumpRequests==0,"switch random rejection");
        p=new();var slow=F();slow.Speed=3;p.Tick(slow,c,Zero);slow.Now=10.46;slow.GoalDistance=19;p.Tick(slow,c,Zero);Check(p.Trace!.Jump=="","hop waits for real speed");
        slow.Now=10.6;slow.Speed=4;p.Tick(slow,c,Zero);Check(p.Trace!.Jump=="switch","pending hop uses 0.5s speed window");
        foreach(var mutate in new Action<RetreatWeaveFrame>[] {f=>f.Block="ghost",f=>f.Block="non-pmc",f=>f.Block="remote",f=>f.Block="firing",f=>f.Block="medical",f=>f.Block="door",
            f=>f.AtCover=true,f=>f.GoalDistance=.9,f=>f.Sprinting=false,f=>f.RearAngle=60,f=>f.Distance=81,f=>f.Action="RushEnemy",f=>f.Source="shared",f=>f.Target="",
            f=>f.Now=double.NaN,f=>f.Speed=double.PositiveInfinity,f=>f.GeometryAge=-1,f=>f.ForwardDot=2,f=>f.Known=new(double.NaN,0,0)}) {
            p=new();p.Tick(F(),c,Zero);var f=F(10.5);mutate(f);Check(!p.Tick(f,c,Zero),"safety release overrides head minimum");
            Check(p.Trace!.Yaw==0&&p.Trace.Pitch==0&&p.Trace.ValidFor==0,"release has no lingering request");
        }
        p=new();p.Tick(F(),c,Zero);var gap=F(10.1,19);gap.Sprinting=false;Check(p.Tick(gap,c,Zero),"brief sprint gap retained");
        gap.Now=10.31;Check(!p.Tick(gap,c,Zero),"gap expires at 0.3s");
        p=new();var low=F();low.VisionAge=2.1;Check(!p.Tick(low,c,Zero),"old sight does not start retreat");
        low.Source="personal-fire";low.FireAge=2.9;Check(p.Tick(low,c,Zero),"personally located incoming fire starts it");
        low.Now=11;low.GoalDistance=17;low.VisionAge=8;low.FireAge=8;Check(p.Tick(low,c,Zero),"sustain to cover using ten-second memory");
        low.FireAge=low.VisionAge=10;Check(!p.Tick(low,c,Zero),"stale personal evidence ends it");
        p=new();for(int i=0;i<10;i++)Check(p.Tick(F(10+i*.5,30-i),c,High),"no artificial three second cap");
        p=new();p.Tick(F(),c,High);Check(!p.Tick(F(11.3),c,High)&&p.Trace!.Reason=="weave-no-cover-progress","physical progress watchdog");
        Check(!p.Tick(F(12,18),c,High),"same destination cannot loop stuck weaving");
        p=new();var left=F();left.Left=false;p.Tick(left,c,Zero);Check(p.Trace!.Yaw==30,"flip to clear side");
        left.Now=10.1;left.Right=false;p.Tick(left,c,Zero);Check(p.Trace!.Yaw==0,"both sides blocked use native route");
        left.GeometryAge=1;left.Now=10.2;p.Tick(left,c,Zero);Check(p.Trace!.Yaw==0&&p.Trace.Pitch==0,"expired geometry restores gaze and route");
        p=new();var noJump=F(10,3.4);noJump.JumpSafe=noJump.LandingSafe=false;p.Tick(noJump,c,Zero);Check(p.Trace!.Jump==""&&!p.Trace.LandingRollSpent,"unsafe landing never consumes a safe opportunity");
        noJump.LandingSafe=true;p.Tick(noJump,c,Zero);Check(p.Trace!.Jump=="","clear destination with blocked predicted flight cannot authorize a landing jump");
        noJump.JumpSafe=true;p.Tick(noJump,c,Zero);p.JumpResult(true,noJump.Now);p.Requested(false,false,c);
        var json=Newtonsoft.Json.JsonConvert.SerializeObject(p.Trace,Comet.Client.Diagnostics.Json);
        using(var wire=JsonDocument.Parse(json)) {
            Check(wire.RootElement.GetProperty("jumpRequests").GetInt32()==1&&wire.RootElement.GetProperty("acceptedJumps").GetInt32()==1,"wire counters");
            Check(wire.RootElement.GetProperty("takeoffs").GetInt32()==0&&wire.RootElement.GetProperty("jumpOutcome").GetString()=="accepted-awaiting-takeoff","wire distinguishes native acceptance from observation");
            Check(wire.RootElement.GetProperty("validFor").GetDouble()<=.25&&wire.RootElement.GetProperty("source").GetString()=="personal-vision","wire lease and evidence");
        }
        var cfg=new RetreatWeaveConfig{HeadDown=false,Jumps=false};p=new();p.Tick(F(),cfg,Zero);Check(p.Trace!.Pitch==0&&p.Trace.Yaw!=0,"independent head toggle");
        cfg=new(){Weave=false,Jumps=false};p=new();p.Tick(F(),cfg,Zero);Check(p.Trace!.Pitch==55&&p.Trace.Yaw==0,"head without zigzag");
        p.Requested(false,true,c);Check(p.Trace.ValidFor>0&&p.Trace.GazeRequested&&!p.Trace.MoveRequested,"gaze request distinct");
        p.End("raid-ended");Check(p.Trace.State=="ended"&&p.Trace.ValidFor==0&&!p.Trace.GazeRequested,"cleanup expires overlay");
        p=new();p.Tick(F(10,3.4),c,Zero);p.JumpResult(true,10);p.ObserveFlight(true,10.51);Check(p.Trace!.Takeoffs==0&&p.Trace.JumpOutcome=="takeoff-not-observed","failed actual takeoff stays distinct");
        foreach(var prop in typeof(RetreatWeaveConfig).GetProperties().Where(x=>x.PropertyType==typeof(double)))
            foreach(double v in new[]{double.NaN,double.PositiveInfinity,-1,999}){cfg=new();prop.SetValue(cfg,v);Invalid(cfg.Validate);}
        Invalid(()=>new RetreatWeaveConfig{SwitchMin=1,SwitchMax=.5}.Validate());Invalid(()=>new CometConfig{RetreatWeave=null!}.Validate());
        Check(JsonSerializer.Deserialize<CometConfig>("{}")!.RetreatWeave.Pitch==55,"old config migration");
        var dir=Path.Combine(Path.GetTempPath(),"comet-m11-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(dir);var file=Path.Combine(dir,"config.json");
        try {var store=new ConfigStore(file);var save=store.Read();save.RetreatWeave.Angle=25;store.Save(save,save.Revision);
            var read=new ConfigStore(file).Read();Check(read.RetreatWeave.Angle==25,"persistence");var before=File.ReadAllText(file);read.RetreatWeave.Pitch=90;
            Invalid(()=>store.Save(read,read.Revision));Check(File.ReadAllText(file)==before,"invalid save atomic");
        }finally{foreach(var path in new[]{file,file+".bak"})if(File.Exists(path))File.Delete(path);Directory.Delete(dir);}
        using(var pe=new PEReader(File.OpenRead("references/sain-4.5.1/SAIN.dll"))) {
            var md=pe.GetMetadataReader();var types=md.TypeDefinitions.Select(h=>md.GetTypeDefinition(h)).ToArray();
            foreach(var pair in new[]{("PlayerMovementController","SetTargetMoveDirection"),("SAINSteeringClass","TickPlayerSteering"),("SAINMoverClass","TryJump")})
                Check(types.Single(t=>md.GetString(t.Name)==pair.Item1).GetMethods().Any(h=>md.GetString(md.GetMethodDefinition(h).Name)==pair.Item2),"official native hook "+pair);
        }
        var random=new Random(1111);p=new();
        for(int i=0;i<2000;i++) {
            var f=F(20+i*.05,40-i%300*.1);f.Context="cover-"+(i/300);f.Sprinting=i%17!=0;f.Left=i%5!=0;f.Right=i%7!=0;f.JumpSafe=i%11!=0;f.LandingSafe=i%13!=0;
            if(i%29==0)f.Block="firing";p.Tick(f,c,random.NextDouble);var t=p.Trace!;
            Check(!p.Active||RetreatWeavePolicy.Gate(f,c)=="","mixed input safety");
            Check(t.Yaw>=0||f.Left,"never weave into blocked left");Check(t.Yaw<=0||f.Right,"never weave into blocked right");
            Check(t.Jump==""||f.Grounded&&f.CanJump&&f.Speed>=c.JumpMinSpeed,"jump permissions");
            if(t.Jump!="")p.JumpResult(false,f.Now);
            Check(t.Takeoffs<=t.AcceptedJumps&&t.Landings<=t.Takeoffs,"observed flight counters ordered");
        }
        Console.WriteLine($"M11: {n} assertions passed.");return n;
    }
}
