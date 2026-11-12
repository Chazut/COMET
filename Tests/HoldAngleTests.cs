using Comet.Core;
using Comet.Server;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.Json;

internal static class HoldAngleTests
{
    public static int Run()
    {
        int n=0;
        void Check(bool value,string why){if(!value)throw new Exception("M08: "+why);n++;}
        void Invalid(Action a){try{a();}catch(ArgumentException){n++;return;}throw new Exception("M08 accepted invalid config");}
        var c=new HoldAngleConfig();c.Validate();
        HoldAngleFrame F(double now=10)=>new(){Now=now,HoldStarted=9,ObservedAt=9.5,GeometryAt=now,
            Source="personal-vision",Target="enemy-a",Position=new(0,0,0),Known=new(4,0,4),Pose=.3,Speed=0};
        HoldAngleCandidate C()=>new(){Id="left-step",Position=new(.6,0,0),Pose=.65,Safe=true,Cost=11};
        var aim=new DoorPoint(2,1.3,3);
        Check(c.Enabled&&c.AdjustPose&&c.Delay==1&&c.MaxStep==1.5&&c.Spacing==.3,"approved switches and distances");
        Check(c.Limit==4&&c.Refresh==.25&&c.Retry==8&&c.RepeatRetry==15,"approved duration/refresh/retries");
        var moving=new HoldAngle();var settling=F();settling.Speed=1;
        Check(!moving.Begin(settling,c)&&moving.Trace==null,"deceleration does not consume the hold-angle opportunity");
        settling.Now=10.2;settling.Position=new(.2,0,0);settling.Speed=.1;
        Check(moving.Begin(settling,c)&&moving.Origin.X==.2,"hold-angle geometry starts from the actual stop");
        var p=new HoldAngle();var f=F(9.99);Check(!p.Begin(f,c),"original Hold must last one second first");
        Check(p.Begin(F(),c),"begin after original delay");
        Check(p.Trace!.State=="planning"&&!p.Trace.MoveRequested&&!p.Trace.PoseRequested&&!p.Trace.Arrived,"planning claims no native action");
        Check(p.Choose(C(),aim,F(10.1),c),"select bounded safe correction");
        var snapped=new HoldAngle();snapped.Begin(F(),c);snapped.Choose(C(),aim,F(10.1),c);var endpoint=snapped.Goal+new DoorPoint(.1,0,0);
        snapped.AcceptEndpoint(endpoint);Check(snapped.Goal.X==endpoint.X&&snapped.Trace!.X==endpoint.X,"checked mover snap updates policy and telemetry together");
        snapped.AcceptEndpoint(endpoint+new DoorPoint(1,0,0));Check(snapped.Goal.X==endpoint.X,"large unchecked correction cannot move policy endpoint");
        Check(!p.Trace.MoveRequested&&!p.Trace.PoseRequested,"selection is not native acceptance");
        p.Requested(true,true);
        f=F(10.2);f.Position=C().Position;f.EyesClear=f.MuzzleClear=true;
        Check(p.Observe(f,c)&&!p.Trace.Arrived,"arrival without actual pose does not count");
        f=F(10.3);f.Position=C().Position;f.Pose=.65;f.EyesClear=true;
        Check(p.Observe(f,c)&&!p.Trace.Arrived,"clear eyes with blocked muzzle does not count");
        f.MuzzleClear=true;f.Speed=1;Check(p.Observe(f,c)&&!p.Trace.Arrived,"passing endpoint too quickly does not count");
        f.Speed=0;f.GeometryAt=10.1;Check(p.Observe(f,c)&&!p.Trace.Arrived,"cached rays do not prove arrival");
        f.GeometryAt=f.Now;Check(p.Observe(f,c)&&p.Trace.State=="watching"&&p.Trace.Arrived,"native pose plus arrival and actual lanes confirmed");
        Check(Math.Abs(p.Trace.Remaining-9.2)<.0001,"first confirmed sample uses frozen evidence expiry, not the spent correction deadline");
        // A fixed hold keeps its pose without restarting the 4-second correction window or making another step.
        for(int i=1;i<=40;i++){f.Now=10.3+i*.1;f.GeometryAt=f.Now;Check(p.Observe(f,c),"confirmed angle maintained within personal evidence lifetime");}
        f.Now=14.4;f.GeometryAt=f.Now;f.MuzzleClear=false;
        Check(!p.Observe(f,c)&&p.Trace.State=="ended"&&p.Trace.ValidFor==0,"lost muzzle clearance ends confirmed hold");
        f=F(23);f.HoldStarted=22;f.ObservedAt=22.9;
        Check(!p.Begin(f,c),"cooldown alone cannot retry identical failed place");
        f.Known=new(7,0,4);Check(p.Begin(f,c),"meaningful contact change plus elapsed cooldown permits retry");
        p.End("hold-angle-no-safe-correction",f,c,true);Check(p.NextAttempt==38,"repeat failure at same place uses 15 seconds");
        f=F(37.9);f.HoldStarted=36;f.ObservedAt=37.8;f.Position=new(2,0,0);Check(!p.Begin(f,c),"movement alone does not bypass repeat cooldown");
        f.Now=38;f.GeometryAt=38;Check(p.Begin(f,c),"both conditions fulfilled");
        p.End("hold-angle-already-clear",f,c,false);Check(!p.Begin(f,c),"one attempt per Hold even after no-op success");
        foreach(var block in new[]{"hold-angle-ineligible","hold-angle-native-action-priority","hold-angle-preserve-cover-or-tactic",
            "hold-angle-visible-or-no-enemy","hold-angle-urgent-or-busy","hold-angle-native-fire-or-aim"}) {
            p=new();p.Begin(F(),c);f=F(10.1);f.Block=block;Check(!p.Observe(f,c)&&p.Trace!.Reason==block,"priority cancels "+block);
            Check(p.Trace.ValidFor==0,"no stale RR lease after priority");
        }
        foreach(var change in new Action<HoldAngleFrame>[] {f=>f.Source="shared-unknown",f=>f.Target="",f=>f.ObservedAt=11,
            f=>f.HoldStarted=11,f=>f.Now=double.NaN,f=>f.Pose=double.NaN,f=>f.Speed=double.NaN,
            f=>f.Position=new(double.NaN,0,0),f=>f.Known=new(0,double.PositiveInfinity,0)}) {
            p=new();f=F();change(f);Check(!p.Begin(f,c),"invalid input fails closed at start");
            p=new();p.Begin(F(),c);f=F(10.1);change(f);Check(!p.Observe(f,c),"invalid input ends active correction");
            Check(double.IsFinite(p.Trace!.Remaining)&&double.IsFinite(p.Trace.ContactAge)&&double.IsFinite(p.Trace.Distance)&&double.IsFinite(p.NextAttempt),"rejection telemetry remains finite");
        }
        foreach(var change in new Action<HoldAngleCandidate>[] {a=>a.Position=new(1.51,0,0),a=>a.Position=new(0,1.51,0),a=>a.Position=new(double.NaN,0,0),
            a=>a.Safe=false,a=>a.Pose=1.1,a=>a.Cost=double.NaN,a=>a.Id=""}) {
            p=new();p.Begin(F(),c);var candidate=C();change(candidate);Check(!p.Choose(candidate,aim,F(10.1),c),"unsafe/invalid candidate not selected");
        }
        p=new();p.Begin(F(),c);var bounded=C();bounded.Position=new(1.5,0,0);Check(p.Choose(bounded,aim,F(10.1),c),"exact 1.5 m endpoint permitted");
        f=F(10.2);f.Position=new(1.56,0,0);Check(!p.Observe(f,c),"actual overshoot ends movement");
        p=new();p.Begin(F(),c);p.Choose(C(),aim,F(10.1),c);f=F(10.2);f.RouteOwned=false;Check(!p.Observe(f,c),"another native path never counted as correction");
        p=new();p.Begin(F(),c);for(int i=1;i<40;i++)Check(p.Observe(F(10+i*.1),c),"planning consumes correction budget");
        Check(!p.Observe(F(14),c)&&p.Trace!.Reason=="hold-angle-correction-timeout","hard limit includes planning");
        p=new();p.Begin(F(),c);p.Choose(C(),aim,F(10.1),c);f=F(10.3);f.GeometryAt=9.9;Check(!p.Observe(f,c),"deferred safety work cannot keep moving indefinitely");
        p=new();p.Begin(F(),c);f=F(10.6);Check(!p.Observe(f,c),"callback watchdog");
        p=new();p.Begin(F(),c);f=F(10.1);f.HoldStarted=9.1;Check(!p.Observe(f,c),"new parent Hold does not inherit old correction");
        p=new();f=F();f.Source="personal-hearing";f.ObservedAt=5;Check(!p.Begin(f,c),"heard evidence expires after five seconds");
        f.ObservedAt=5.1;Check(p.Begin(f,c),"recent personal hearing allows watch only");
        c.AdjustPose=false;p=new();p.Begin(F(),c);Check(!p.Choose(C(),aim,F(10.1),c),"posture switch enforced in policy");
        bounded=C();bounded.Pose=.3;Check(p.Choose(bounded,aim,F(10.1),c),"movement independent of posture switch");c.AdjustPose=true;
        p=new();p.Begin(F(),c);p.Choose(C(),aim,F(10.1),c);c.Enabled=false;Check(!p.Observe(F(10.2),c),"disable cancels active request");c.Enabled=true;
        foreach(var prop in typeof(HoldAngleConfig).GetProperties().Where(p=>p.PropertyType==typeof(double)))
            foreach(double value in new[]{double.NaN,double.PositiveInfinity,-1,999}) {var bad=new HoldAngleConfig();prop.SetValue(bad,value);Invalid(bad.Validate);}
        Invalid(()=>new HoldAngleConfig{Retry=20,RepeatRetry=15}.Validate());
        Invalid(()=>new HoldAngleConfig{MaxStep=.3,Spacing=.5}.Validate());Invalid(()=>new CometConfig{HoldAngle=null!}.Validate());
        Check(JsonSerializer.Deserialize<CometConfig>("{}")!.HoldAngle.MaxStep==1.5,"old config inherits defaults");
        var dir=Path.Combine(Path.GetTempPath(),"comet-m08-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(dir);var file=Path.Combine(dir,"config.json");
        try {
            var store=new ConfigStore(file);var saved=store.Read();saved.HoldAngle.MaxStep=1.2;saved.HoldAngle.AdjustPose=false;saved=store.Save(saved,saved.Revision);
            var read=new ConfigStore(file).Read();Check(read.HoldAngle.MaxStep==1.2&&!read.HoldAngle.AdjustPose,"server roundtrip");
            string before=File.ReadAllText(file);saved.HoldAngle.Refresh=0;Invalid(()=>store.Save(saved,saved.Revision));Check(File.ReadAllText(file)==before,"invalid config save is atomic");
        }finally {foreach(var path in new[]{file,file+".bak"})if(File.Exists(path))File.Delete(path);Directory.Delete(dir);}
        using(var pe=new PEReader(File.OpenRead("references/sain-4.5.1/SAIN.dll"))) {
            var md=pe.GetMetadataReader();var defs=md.TypeDefinitions.Select(h=>md.GetTypeDefinition(h)).ToArray();
            var freeze=defs.Single(t=>md.GetString(t.Name)=="FreezeAction");
            Check(freeze.GetMethods().Any(h=>md.GetString(md.GetMethodDefinition(h).Name)=="Update"),"official Freeze update hook");
            var pose=defs.Single(t=>md.GetString(t.Name)=="PoseClass");Check(pose.GetMethods().Any(h=>md.GetString(md.GetMethodDefinition(h).Name)=="SetTargetPose"),"native pose request exists");
        }
        // Mix interruption, stale geometry, requested vs observed movement and parent plan changes.
        var random=new Random(808);
        for(int i=0;i<2000;i++) {
            p=new();f=F();Check(p.Begin(f,c),"scenario starts");p.Choose(C(),aim,F(10.05),c);p.Requested(true,true);
            f=F(10.1+random.NextDouble()*.3);f.GeometryAt=f.Now-random.NextDouble()*.6;f.Position=new(random.NextDouble()*2,0,0);
            f.Pose=random.NextDouble();f.EyesClear=random.Next(2)==0;f.MuzzleClear=random.Next(2)==0;f.RouteOwned=i%9!=0;
            if(i%7==0)f.Block="hold-angle-urgent-or-busy";
            bool active=p.Observe(f,c);
            Check(!active||HoldAngle.Gate(f,c)==""&&f.RouteOwned&&(f.Position-p.Origin).Length<=c.MaxStep+.05,"mixed stream respects gates and origin");
            Check(!p.Trace!.Arrived||f.EyesClear&&f.MuzzleClear&&Math.Abs(f.Pose-.65)<=.08&&f.Now-f.GeometryAt<.01,"no fabricated successful correction");
        }
        Console.WriteLine($"M08: {n} checks passed (Hold/priority gates, correction deadlines, actual pose/arrival/rays, retry memory, config, native hooks)");return n;
    }
}
