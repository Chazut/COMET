using Comet.Core;
using Comet.Server;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.Json;

internal static class DoorStepTests
{
    public static int Run()
    {
        int n=0;void Check(bool ok,string why){n++;if(!ok)throw new Exception("D04: "+why);}
        void Invalid(Action a){try{a();}catch(ArgumentException){n++;return;}throw new Exception("D04 invalid config accepted");}
        var c=new DoorStepPeekConfig();c.Validate();
        Check(c.Enabled&&c.Chance==50&&c.MaximumPeeks==2&&c.Distance==.8&&c.ObservationMin==.4&&c.ObservationMax==.8&&c.RestMin==.5&&c.RestMax==1&&c.Limit==6&&c.VaryPosture,"approved defaults");
        foreach(double v in new[]{double.NaN,double.PositiveInfinity,-1,999})
        foreach(var change in new Action<DoorStepPeekConfig>[]{x=>x.Chance=v,x=>x.Distance=v,x=>x.ObservationMin=v,x=>x.ObservationMax=v,x=>x.RestMin=v,x=>x.RestMax=v,x=>x.Limit=v})
        {var x=new DoorStepPeekConfig();change(x);Invalid(x.Validate);}
        Invalid(()=>new DoorStepPeekConfig{MaximumPeeks=3}.Validate());Invalid(()=>new DoorStepPeekConfig{MaximumPeeks=0}.Validate());
        Invalid(()=>new DoorStepPeekConfig{ObservationMin=.9,ObservationMax=.8}.Validate());Invalid(()=>new DoorStepPeekConfig{RestMin=1.1,RestMax=1}.Validate());
        Invalid(()=>new CometConfig{DoorStepPeek=null!}.Validate());
        var old=JsonSerializer.Deserialize<CometConfig>("{\"DoorJumpPeek\":{\"Cooldown\":70},\"DoorRunBy\":{\"Enabled\":false}}")!;
        old.Validate();Check(old.DoorStepPeek.Chance==50&&!old.DoorRunBy.Enabled&&old.DoorJumpPeek.Cooldown==70,"old configuration migration preserves choices");
        var dir=Path.Combine(Path.GetTempPath(),"comet-d04-"+Guid.NewGuid().ToString("N"));
        try{
            var store=new ConfigStore(Path.Combine(dir,"config.json"));var cfg=store.Read();cfg.DoorStepPeek.Chance=30;cfg.DoorStepPeek.MaximumPeeks=1;cfg.DoorStepPeek.VaryPosture=false;
            store.Save(cfg,cfg.Revision);var saved=JsonSerializer.Deserialize<CometConfig>(store.ForGame())!;
            Check(saved.DoorStepPeek.Chance==30&&saved.DoorStepPeek.MaximumPeeks==1&&!saved.DoorStepPeek.VaryPosture,"server to game persistence");
            cfg=store.Read();var rev=cfg.Revision;cfg.DoorStepPeek.RestMax=.1;Invalid(()=>store.Save(cfg,rev));Check(store.Read().Revision==rev,"invalid draft cannot replace saved settings");
        }finally{Directory.Delete(dir,true);}
        var stack=new DoorPoint(-1.2,0,-.9);var point=new DoorPoint(-.4,0,-.9);var look=new DoorPoint(.3,1.3,.6);
        DoorJumpTrace Parent()=>new(){State="completed",Door="door",Target="enemy",Source="heard",Personality="Chad",Returned=true,TakeoffObserved=true,LandingObserved=true,
            KnownZ=4,ActualX=stack.X,ActualZ=stack.Z,ContactAge=3};
        foreach(var corrupt in new Action<DoorJumpTrace>[]{x=>x.State="ended",x=>x.Returned=false,x=>x.TakeoffObserved=false,x=>x.LandingObserved=false,x=>x.VisualAcquired=true,x=>x.KnownX=double.NaN,x=>x.ActualY=double.PositiveInfinity,x=>x.ContactAge=-1,x=>x.Target=""})
        {var parent=Parent();corrupt(parent);Check(!new DoorStepPeek().Begin(parent,10,0,c),"only physically completed D02 can continue");}
        foreach(double roll in new[]{double.NaN,double.PositiveInfinity,-.1,1})Check(!new DoorStepPeek().Begin(Parent(),10,roll,c),"invalid roll refused");
        int accepted=0;for(int i=0;i<1000;i++)if(new DoorStepPeek().Begin(Parent(),10,i/1000d,c))accepted++;
        Check(accepted==500,"exact 50 percent boundary");
        c.Chance=0;Check(!new DoorStepPeek().Begin(Parent(),10,0,c),"zero disables probability");c.Chance=100;Check(new DoorStepPeek().Begin(Parent(),10,.999,c),"100 includes every valid roll");c.Chance=50;
        c.Enabled=false;Check(!new DoorStepPeek().Begin(Parent(),10,0,c),"feature toggle");c.Enabled=true;
        DoorStepPeek New(){var p=new DoorStepPeek();Check(p.Begin(Parent(),10,0,c),"continuation selected");return p;}
        var p=New();
        bool Tick(double now,DoorPoint pos,bool at=true,bool facing=true,double pose=1,bool ready=true,bool sprint=false,bool ground=true,bool seen=false)
            =>p.Observe(pos,pose,ground,sprint,at,ready,facing,seen,3+now-10,now,c);
        void Prepare(double time=10,double pose=1){p.Prepared(point,look,pose,.4,.5,"known doorway side",time);Tick(time+.05,stack,pose:pose);}
        p.ReturnStarted(point,10);Check(!p.Trace!.ReturnAccepted,"cannot bypass outward movement and observation");p.Start(stack,10);Check(!p.Trace.MovementAccepted,"cannot bypass plan and actual posture");
        p.NoUsefulNext(10);Check(p.Active,"cannot report success with no completed peek");
        p.Prepared(point,look,.6,.4,.5,"lower angle",10);Tick(10.1,stack,pose:1);Check(p.Trace.State=="preparing","requested posture alone is not actual posture");
        Tick(10.2,stack,pose:.6,ready:false);Check(p.Trace.State=="preparing","weapon readiness required");
        Tick(10.3,stack,pose:.6);Check(p.Trace.State=="outward-pending","observed posture enables movement");
        p.Start(stack,10.3);Tick(10.4,stack,pose:.6);Check(!p.Trace.Arrived&&!p.Trace.MovementObserved,"arrival flag cannot manufacture displacement");
        Tick(10.6,point,pose:.6);Check(p.Trace.State=="observing"&&p.Trace.Arrived&&p.Trace.Observation==0,"timer starts on actual arrival");
        Tick(10.8,point,pose:.6);Tick(10.9,point,facing:false,pose:.6);Check(p.Trace.Observation==0,"lost view resets continuous observation");
        Tick(11.1,point,pose:.6);Tick(11.31,point,pose:.6);Check(p.Trace.State=="return-pending"&&!p.Trace.Returned,"observation completion is not a return");
        p.ReturnStarted(point,11.31);Tick(11.5,point,at:false,pose:.6);Check(!p.Trace.Returned,"return request is not arrival");
        Tick(11.7,stack,pose:.6);Check(p.Trace.Returned&&p.Trace.Completed==1&&p.Trace.State=="resting","actual return starts frame rest");
        Tick(12,stack,pose:.6);Check(p.Trace.State=="resting","minimum rest enforced");Tick(12.21,stack,pose:.6);
        Check(p.Trace.State=="planning"&&p.Trace.Peek==2,"only after rest can another view be considered");
        p.NoUsefulNext(12.22);Check(!p.Active&&p.Trace.State=="completed"&&p.Trace.Completed==1,"one useful peek suffices when no different safe angle");
        p=New();Prepare();p.Start(stack,10.05);Tick(10.2,point);Tick(10.41,point);Tick(10.62,point);p.ReturnStarted(point,10.62);Tick(10.8,stack);Tick(11.31,stack);
        Prepare(11.31,.6);p.Start(stack,11.36);Tick(11.6,point,pose:.6);Tick(11.81,point,pose:.6);Tick(12.02,point,pose:.6);p.ReturnStarted(point,12.02);Tick(12.2,stack,pose:.6);Tick(12.71,stack,pose:.6);
        Check(!p.Active&&p.Trace!.Completed==2&&p.Trace.State=="completed","two complete walking peeks and no third");
        foreach(string fail in new[]{"timeout","planning-timeout","pose-timeout","stalled","return-stalled","visible","airborne","sprint","clock","nan","lost-view","lost-frame","lost-pose"})
        {
            p=New();
            if(fail=="planning-timeout")Tick(11.1,stack);
            else{
                p.Prepared(point,look,1,.4,.5,"angle",10);
                if(fail=="pose-timeout")Tick(11.3,stack,pose:.2);
                else{
                    Tick(10.05,stack);p.Start(stack,10.05);
                    if(fail=="timeout")Tick(16,point);
                    if(fail=="stalled")Tick(11.1,stack,at:false);
                    if(fail=="visible")Tick(10.1,stack,seen:true);
                    if(fail=="airborne")Tick(10.1,stack,ground:false);
                    if(fail=="sprint")Tick(10.1,stack,sprint:true);
                    if(fail=="clock")Tick(9.9,stack);
                    if(fail=="nan")Tick(10.1,new(double.NaN,0,0));
                    if(fail=="lost-view"){Tick(10.2,point);Tick(11.7,point,facing:false);}
                    if(fail=="lost-pose"){Tick(10.2,point);Tick(10.3,point,pose:.1);}
                    if(fail is "return-stalled" or "lost-frame"){
                        Tick(10.2,point);Tick(10.41,point);Tick(10.62,point);p.ReturnStarted(point,10.62);
                        if(fail=="return-stalled")Tick(11.7,point,at:false);
                        else{Tick(10.8,stack);Tick(10.9,point,at:false);}
                    }
                }
            }
            Check(!p.Active&&p.Trace!.State=="ended","interruption cannot become successful completion: "+fail);
        }
        p=New();p.Prepared(point,new(double.NaN,0,0),1,.4,.5,"bad",10);Check(!p.Active,"invalid plan refused");
        p=New();Check(p.SameContact("enemy",new(0,0,4))&&!p.SameContact("other",new(0,0,4))&&!p.SameContact("enemy",new(0,0,7)),"same contact and remembered position");
        Check(!DoorStepGeometry.Different(new(),new(0,0,1),new(),new(0,0,2)),"same viewing angle rejected");
        Check(DoorStepGeometry.Different(new(),new(0,0,1),new(),new(1,0,1)),"different view accepted");
        Check(!DoorStepGeometry.Different(new(),new(),new(),new(1,0,1)),"degenerate view rejected");
        for(int d=0;d<360;d+=10)foreach(int side in new[]{-1,1}){
            double a=d*Math.PI/180;DoorPoint R(DoorPoint v)=>new(10+v.X*Math.Cos(a)-v.Z*Math.Sin(a),3+v.Y,20+v.X*Math.Sin(a)+v.Z*Math.Cos(a));
            var g=new DoorGeometry{Center=R(new()),Axis=new(Math.Cos(a),0,Math.Sin(a)),Normal=new(-Math.Sin(a),0,Math.Cos(a)),HalfWidth=.5};
            var st=R(new(stack.X*side,0,stack.Z));var pt=DoorStepGeometry.Candidate(g,st,.8);
            Check((pt-R(new(point.X*side,0,point.Z))).Length<.001,"left/right rotated short step");
            Check(DoorStepGeometry.Route(g,st,st,pt,new[]{st,pt},true,.8)=="","bounded outward route");
            Check(DoorStepGeometry.Route(g,st,pt,st,new[]{pt,st},true,.8)=="","bounded return route");
            Check(DoorStepGeometry.Route(g,st,st,pt,new[]{st,pt},false,.8)!="","partial path rejected");
            Check(DoorStepGeometry.Route(g,st,st,pt,new[]{st,R(new(0,0,1)),pt},true,.8)!="","shortcut through room rejected");
            Check(!DoorStepGeometry.Contains(g,st,pt,pt+new DoorPoint(0,.5,0)),"wrong floor refused");
            Check(DoorStepGeometry.Contains(g,st,pt,st+(pt-st)*.5),"actual corridor travel");
        }
        using(var pe=new PEReader(File.OpenRead("Client/bin/Release/netstandard2.1/COMET.dll"))){
            var md=pe.GetMetadataReader();List<string> Calls(string method)=>FloorHearingTests.ReadIl(pe.GetMethodBody(md.TypeDefinitions.Select(md.GetTypeDefinition).Single(x=>md.GetString(x.Name)=="Plugin").GetMethods().Select(md.GetMethodDefinition).Single(x=>md.GetString(x.Name)==method).RelativeVirtualAddress).GetILBytes()!)
                .Where(x=>x.op==OpCodes.Call||x.op==OpCodes.Callvirt).Select(x=>FloorHearingTests.Name(md,(int)x.operand!)).ToList();
            foreach(string name in new[]{"IsPmcAi","get_Authority","Ghost","get_IsVisible","get_CurrentSelfDecision","get_CurrentSquadDecision","get_IsUnderFire","SameContact"})Check(Calls("StepGate").Contains(name),"native scope "+name);
            foreach(string name in new[]{"Observe","get_PoseLevel","get_IsGrounded","WalkToPointByWay","Contains","Different","RunVolume","get_EyePosition","get_LookDirection"})Check(Calls("StepDoorPeek").Contains(name),"observed execution contract "+name);
            Check(Calls("FinishDoorJump").Contains("TryStartDoorStep")&&!Calls("FinishDoorRun").Contains("TryStartDoorStep"),"D02 only continuation");
            Check(Calls("Release").Contains("EndDoorStep")&&Calls("Update").Contains("RefreshDoorStep")&&Calls("DoorSearchStep").Contains("StepDoorPeek"),"lifecycle hooked");
            Check(!Calls("TryStartDoorStep").Contains("TryBegin"),"cannot redraw reconnaissance opportunity");
            foreach(string method in new[]{"TryStartDoorStep","StepGate","StepDoorPeek","PrepareDoorStep","FinishDoorStep"})
            foreach(string banned in new[]{"get_EnemyPosition","get_EnemyPlayer","FindObjectsOfType","Teleport","set_DoorState","SetPoseLevel","TryJump","Shoot","SetEnemyVisible"})Check(!Calls(method).Contains(banned),"no hidden perception or forced action "+method+":"+banned);
        }
        Console.WriteLine($"D04: {n} checks passed (continuation, actual posture/motion/view/return, failures, geometry, settings, native contracts). No Unity raid simulated.");return n;
    }
}
