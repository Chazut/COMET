using Comet.Core;
using Comet.Server;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.Json;

internal static class DoorRunTests
{
    public static int Run()
    {
        int n=0;void Check(bool ok,string why){n++;if(!ok)throw new Exception("D03: "+why);}
        void Invalid(Action a){try{a();}catch(ArgumentException){n++;return;}throw new Exception("D03 invalid config accepted");}
        var c=new DoorRunByConfig();var j=new DoorJumpPeekConfig();c.Validate();
        Check(c.Enabled&&c.Fallback&&c.Preference==40&&c.MinimumStamina==35&&c.TurnDelay==.35&&c.Limit==6&&c.Watch==2,"approved defaults");
        foreach(var value in new[]{double.NaN,double.PositiveInfinity,-1,999})
        foreach(var mutate in new Action<DoorRunByConfig>[]{x=>x.Preference=value,x=>x.MinimumStamina=value,x=>x.TurnDelay=value,x=>x.Limit=value,x=>x.Watch=value})
        {var x=new DoorRunByConfig();mutate(x);Invalid(x.Validate);}
        Invalid(()=>new CometConfig{DoorRunBy=null!}.Validate());
        var old=JsonSerializer.Deserialize<CometConfig>("{\"Enabled\":false,\"DoorJumpPeek\":{\"Enabled\":false,\"Cooldown\":70,\"Chances\":{\"GigaChad\":10,\"Chad\":0,\"Wreckless\":15,\"Normal\":10,\"Rat\":1,\"SnappingTurtle\":0,\"Coward\":0,\"Timmy\":0}}}")!;
        old.Validate();Check(!old.Enabled&&!old.DoorJumpPeek.Enabled&&old.DoorRunBy.Enabled&&old.DoorJumpPeek.Chances["Rat"]==1&&old.DoorJumpPeek.Cooldown==70,"old values retained as shared opportunities; master disable preserved");
        string dir=Path.Combine(Path.GetTempPath(),"comet-d03-"+Guid.NewGuid().ToString("N"));
        try{
            var store=new ConfigStore(Path.Combine(dir,"config.json"));var cfg=store.Read();cfg.DoorRunBy.Preference=65;cfg.DoorRunBy.Watch=1;cfg.DoorRunBy.Fallback=false;cfg.DoorJumpPeek.Chances["Chad"]=28;
            store.Save(cfg,cfg.Revision);var saved=JsonSerializer.Deserialize<CometConfig>(store.ForGame())!;
            Check(saved.DoorRunBy.Preference==65&&!saved.DoorRunBy.Fallback&&saved.DoorRunBy.Watch==1&&saved.DoorJumpPeek.Chances["Chad"]==28,"server to client config persistence");
            cfg=store.Read();long revision=cfg.Revision;cfg.DoorRunBy.TurnDelay=.2;Invalid(()=>store.Save(cfg,revision));Check(store.Read().Revision==revision,"invalid edit cannot overwrite config");
        }finally{Directory.Delete(dir,true);}
        var contact=new DoorPoint(0,0,4);var stack=new DoorPoint(-1.2,0,-1.25);var far=new DoorPoint(1.9,0,-1.25);var angle=new DoorPoint(-1.3,0,-2.4);
        var opportunity=new DoorReconOpportunity();
        DoorRunTrace? Begin(double time=10,double age=2,double roll=0,double style=.2,string person="Chad",string target="target")=>opportunity.TryBegin("door",target,"heard",person,contact,age,time,roll,style,j,c);
        foreach(var pair in j.Chances)
        {
            opportunity.Clear();var t=Begin(roll:Math.Max(0,(pair.Value-.01)/100),person:pair.Key);Check((t?.State=="validating")== (pair.Value>0),"personality boundary below "+pair.Key);
            opportunity.Clear();t=Begin(roll:pair.Value/100,person:pair.Key);Check(t?.State=="ended","chance boundary exclusive "+pair.Key);
        }
        opportunity.Clear();Check(Begin(roll:.9)?.Reason=="recon-roll-declined","decline recorded");
        Check(Begin(11,0)==null&&Begin(56,48)==null,"decline consumes shared cooldown and same evidence");
        Check(Begin(56,1)!=null,"new evidence after cooldown accepted");Check(Begin(57,0,target:"other")==null,"cooldown across contacts and styles");
        opportunity.Clear();Check(Begin()!=null,"raid reset clears history");
        foreach(var invalid in new[]{double.NaN,double.PositiveInfinity,-.1,1.0}){opportunity.Clear();Check(Begin(roll:invalid)==null&&Begin(style:invalid)==null,"invalid random never selected");}
        opportunity.Clear();
        for(int x=0;x<32;x++)Check(Begin(10+x*46,0,target:"target"+x)!=null,"bounded evidence entry "+x);
        Check(Begin(2000,0,target:"overflow")==null,"history overflow conservative");
        Check(DoorReconOpportunity.Choose(true,true,.39999,j,c)=="run-by"&&DoorReconOpportunity.Choose(true,true,.4,j,c)=="jump","40 percent conditional preference");
        Check(DoorReconOpportunity.Choose(false,true,.8,j,c)=="run-by"&&DoorReconOpportunity.Choose(true,false,.1,j,c)=="jump","single feasible style");
        c.Fallback=false;Check(DoorReconOpportunity.Choose(false,true,.2,j,c)=="none","disabled fallback");
        j.Enabled=false;Check(DoorReconOpportunity.Choose(true,true,.8,j,c)=="run-by","run-by only ignores jump preference and fallback");
        c.Enabled=false;Check(DoorReconOpportunity.Choose(true,true,0,j,c)=="none","both disabled");opportunity.Clear();Check(Begin()==null,"both disabled do not consume rolls");
        j.Enabled=c.Enabled=c.Fallback=true;
        // Deterministic grid demonstrates total opportunity probability, not additive per-style rolls.
        int runCount=0,jumpCount=0,declined=0;
        for(int roll=0;roll<100;roll++)for(int style=0;style<100;style++)
        {
            opportunity.Clear();var t=Begin(roll:roll/100d,style:style/100d)!;
            if(t.State=="ended")declined++;else if(DoorReconOpportunity.Choose(true,true,style/100d,j,c)=="run-by")runCount++;else jumpCount++;
        }
        Check(runCount==1000&&jumpCount==1500&&declined==7500,"Chad 10% run, 15% jump, 75% normal");
        DoorRunBy Selected(){opportunity.Clear();var p=new DoorRunBy();p.Begin(Begin()!,10);return p;}
        var p=Selected();p.Start(stack,10);
        bool Tick(DoorPoint pos,double time,bool at=false,bool sprint=false,double speed=1.5,bool ready=true,bool ground=true,bool seen=false)
            =>p.Observe(pos,speed,50,ground,sprint,at,ready,seen,2+time-10,time,c);
        Tick(stack,10.1,sprint:true);Check(!p.Trace!.SprintObserved,"sprint flag alone is not movement");
        Tick(stack+new DoorPoint(.3,0,0),10.2,sprint:true,speed:-2);Check(!p.Trace.SprintObserved,"opposite motion is not outward sprint");
        Tick(stack+new DoorPoint(.5,0,0),10.3,sprint:true);Check(p.Trace.SprintObserved&&!p.Trace.FarArrived,"sprint observed before arrival");
        Tick(far,10.9,at:true,sprint:true);Check(p.Trace.State=="turning"&&p.Trace.FarArrived,"actual far arrival");
        Tick(far,11.3,at:true,sprint:true);Check(p.Trace.State=="turning"&&!p.Trace.SprintExitObserved,"elapsed pause cannot substitute actual sprint exit");
        Tick(far,11.4,at:true,ready:false);Check(p.Trace.State=="turning","wait for weapon ready");
        Tick(far,11.5,at:true);Check(p.Trace.State=="return-pending"&&p.Trace.SprintExitObserved&&!p.Trace.ReturnAccepted,"ready to revalidate return; not yet returning");
        p.ReturnStarted(far,11.5);Tick(new(.5,0,-1.8),12);Tick(angle,12.8,at:true);Check(p.Trace.State=="watching"&&p.Trace.Returned,"observed walking return");
        Tick(angle,14.79,at:true);Check(p.Active,"final angle watch separate from movement ceiling");Tick(angle,14.81,at:true);Check(!p.Active&&p.Trace.State=="completed","bounded useful watch");
        foreach(var failure in new[]{"stationary-sprint","walking-only","no-sprint-arrival","timeout","visible","airborne","nan","clock","return-sprint","stuck-return","watch-lost"})
        {
            p=Selected();p.Start(stack,10);
            if(failure=="stationary-sprint")Tick(stack,11.1,sprint:true);
            if(failure=="walking-only")Tick(new(0,0,-1.25),11.1,sprint:false);
            if(failure=="no-sprint-arrival")Tick(far,10.2,at:true);
            if(failure=="timeout")Tick(stack,16);
            if(failure=="visible")Tick(stack,10.1,seen:true);
            if(failure=="airborne")Tick(stack,10.1,ground:false);
            if(failure=="nan")Tick(new(double.NaN,0,0),10.1);
            if(failure=="clock")Tick(stack,9.9);
            if(failure is "return-sprint" or "stuck-return" or "watch-lost")
            {
                Tick(new(0,0,-1.25),10.3,sprint:true);Tick(far,10.9,at:true);Tick(far,11.4,at:true);p.ReturnStarted(far,11.4);
                if(failure=="return-sprint")Tick(far,11.5,sprint:true);
                if(failure=="stuck-return")Tick(far,12.5);
                if(failure=="watch-lost"){Tick(angle,12,at:true);Tick(angle,12.1,at:false);}
            }
            Check(!p.Active&&p.Trace!.State=="ended","failure releases ownership: "+failure);
        }
        p=Selected();p.Opening(10);Tick(stack,14.4);Check(p.Active&&!p.Trace!.DoorOpenObserved,"opening requested not observed");Tick(stack,14.6);Check(!p.Active,"open watchdog");
        p=Selected();p.Opening(10);p.Opened(12);p.Start(stack,12.2);Tick(stack,12.3);Check(Math.Abs(p.Trace!.Elapsed-.1)<.001,"opening excluded from six seconds");
        p=Selected();p.Delegate(10.2);Check(!p.Active&&p.Trace!.State=="delegated"&&!p.Trace.MovementAccepted,"D02 handoff cannot run D03 too");
        var jump=new DoorJumpPeek();jump.BeginSelected(p.Trace!,10.2);Check(jump.Active&&jump.Trace!.Roll==p.Trace.Roll&&jump.Trace.StyleRoll==p.Trace.StyleRoll,"jump adopts selection without second roll");
        Check(p.SameContact("target",contact)&&!p.SameContact("other",contact)&&!p.SameContact("target",contact+new DoorPoint(3,0,0)),"contact scope");
        for(int degrees=0;degrees<360;degrees+=5)foreach(int side in new[]{-1,1})
        {
            double a=degrees*Math.PI/180;DoorPoint R(DoorPoint v)=>new(15+v.X*Math.Cos(a)-v.Z*Math.Sin(a),2+v.Y,20+v.X*Math.Sin(a)+v.Z*Math.Cos(a));
            var g=new DoorGeometry{Center=R(new()),Axis=new(Math.Cos(a),0,Math.Sin(a)),Normal=new(-Math.Sin(a),0,Math.Cos(a)),HalfWidth=.5};
            DoorPoint Flip(DoorPoint v)=>new(v.X*side,v.Y,v.Z);
            var st=R(Flip(stack));var f=DoorRunGeometry.Far(g,st);var an=DoorRunGeometry.Angle(g,st);
            Check((f-R(Flip(far))).Length<.001&&(an-R(Flip(angle))).Length<.001,"rotated left/right destinations "+degrees);
            Check(DoorRunGeometry.ProtectedFar(g,st,f)&&!DoorRunGeometry.ProtectedFar(g,st,R(new(.2,0,-1.25))),"far side beyond frame");
            Check(DoorRunGeometry.Route(g,st,st,f,f,new[]{st,f},true)=="","outward corridor path");
            Check(DoorRunGeometry.Route(g,st,f,an,an,new[]{f,an},true)=="","return corridor path");
            Check(DoorRunGeometry.Route(g,st,st,f,f,new[]{st,f},false)!="","partial route refused");
            Check(DoorRunGeometry.Route(g,st,st,f,f+new DoorPoint(0,.3,0),new[]{st,f},true)!="","wrong floor refused");
            Check(DoorRunGeometry.Route(g,st,st,f,f,new[]{st,R(new(0,0,1)),f},true)!="","room shortcut refused");
            Check(DoorRunGeometry.Contains(g,st,st,f,st+(f-st)*.5),"actual motion inside lane");
            Check(!DoorRunGeometry.Contains(g,st,st,f,R(new(0,0,.5)))&&!DoorRunGeometry.Contains(g,st,st,f,st+new DoorPoint(0,.5,0)),"room drift and airborne motion refused");
        }
        using(var pe=new PEReader(File.OpenRead("Client/bin/Release/netstandard2.1/COMET.dll")))
        {
            var md=pe.GetMetadataReader();List<string> Calls(string method)=>FloorHearingTests.ReadIl(pe.GetMethodBody(md.TypeDefinitions.Select(md.GetTypeDefinition).Single(x=>md.GetString(x.Name)=="Plugin").GetMethods().Select(md.GetMethodDefinition).Single(x=>md.GetString(x.Name)==method).RelativeVirtualAddress).GetILBytes()!)
                .Where(x=>x.op==OpCodes.Call||x.op==OpCodes.Callvirt).Select(x=>FloorHearingTests.Name(md,(int)x.operand!)).ToList();
            foreach(var name in new[]{"IsPmcAi","get_Authority","Ghost","get_IsVisible","get_CurrentSelfDecision","get_CurrentSquadDecision","get_IsUnderFire","SameContact"})Check(Calls("RunGate").Contains(name),"scope "+name);
            foreach(var name in new[]{"CapsuleCastNonAlloc","OverlapCapsuleNonAlloc","Raycast","Clear"})Check(Calls("RunVolume").Contains(name),"swept volume "+name);
            foreach(var name in new[]{"Observe","get_Velocity","get_IsSprintEnabled","get_IsGrounded","WalkToPointByWay","Contains","RunVolume"})Check(Calls("StepDoorRun").Contains(name),"actual execution "+name);
            Check(Calls("PrepareDoorRecon").Contains("RunToPointByWay")&&Calls("PrepareDoorRecon").Contains("BeginSelected"),"native sprint and single-roll handoff");
            Check(Calls("Release").Contains("EndDoorRun")&&Calls("Update").Contains("RefreshDoorRun")&&Calls("TryStartDoorJump").Contains("TryStartDoorRecon"),"lifecycle and shared handoff");
            foreach(var method in new[]{"RunGate","StepDoorRun","PrepareDoorRecon","PrepareRunRoutes","RunVolume","RunAngleSight","FinishDoorRun"})
            foreach(var banned in new[]{"get_EnemyPosition","get_EnemyPlayer","FindObjectsOfType","Teleport","set_DoorState","set_IsGrounded","TryJump","SetEnemyVisible"})Check(!Calls(method).Contains(banned),"no cheating/game state forcing "+method+":"+banned);
        }
        Console.WriteLine($"D03: {n} checks passed (shared opportunity, route/return geometry, actual sprint, interruptions, persistence, native contracts). No Unity raid simulated.");return n;
    }
}
