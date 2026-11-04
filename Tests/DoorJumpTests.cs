using Comet.Core;
using Comet.Server;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.Json;

internal static class DoorJumpTests
{
    public static int Run()
    {
        int n=0;void Check(bool yes,string why){n++;if(!yes)throw new Exception("D02: "+why);}
        void Invalid(Action a){try{a();}catch(ArgumentException){n++;return;}throw new Exception("D02 invalid config accepted");}
        var c=new DoorJumpPeekConfig();c.Validate();
        Check(c.Enabled&&!c.ReturnJump&&c.MinimumStamina==35&&c.MinimumSpeed==1.2&&c.Observation==.4&&c.Limit==4&&c.Cooldown==45,"approved limits");
        Check(c.Chances.Values.SequenceEqual(new double[]{35,25,15,10,0,0,0,0}),"approved personality chances");
        foreach(var value in new[]{double.NaN,double.PositiveInfinity,-1,999})
        foreach(var mutation in new Action<DoorJumpPeekConfig>[]{q=>q.MinimumStamina=value,q=>q.MinimumSpeed=value,q=>q.Observation=value,q=>q.Limit=value,q=>q.Cooldown=value,q=>q.Chances["Normal"]=value})
        {var q=new DoorJumpPeekConfig();mutation(q);Invalid(q.Validate);}
        Invalid(()=>new DoorJumpPeekConfig{Chances=null!}.Validate());Invalid(()=>new CometConfig{DoorJumpPeek=null!}.Validate());
        var missing=new DoorJumpPeekConfig();missing.Chances.Remove("Rat");Invalid(missing.Validate);
        var old=JsonSerializer.Deserialize<CometConfig>("{\"Enabled\":false}")!;old.Validate();Check(!old.Enabled&&old.DoorJumpPeek.Enabled,"old config keeps master OFF and adds defaults");
        string dir=Path.Combine(Path.GetTempPath(),"comet-d02-"+Guid.NewGuid().ToString("N"));
        try{
            var store=new ConfigStore(Path.Combine(dir,"config.json"));var q=store.Read();q.DoorJumpPeek.ReturnJump=true;q.DoorJumpPeek.Chances["Rat"]=11;q.DoorJumpPeek.MinimumSpeed=1.5;store.Save(q,q.Revision);
            var saved=JsonSerializer.Deserialize<CometConfig>(store.ForGame())!;
            Check(saved.DoorJumpPeek.ReturnJump&&saved.DoorJumpPeek.Chances["Rat"]==11&&saved.DoorJumpPeek.MinimumSpeed==1.5,"server persistence and game delivery");
            long rev=store.Read().Revision;q=store.Read();q.DoorJumpPeek.Observation=2;Invalid(()=>store.Save(q,q.Revision));Check(store.Read().Revision==rev,"invalid edit cannot change stored revision");
        }finally{Directory.Delete(dir,true);}
        var contact=new DoorPoint(0,0,3);var stack=new DoorPoint(-1.2,0,-1.25);var landing=new DoorPoint(.25,0,-1.25);
        DoorJumpPeek Selected(double roll=0,string personality="Normal") {var p=new DoorJumpPeek();p.TrySelect("door","enemy","heard",personality,contact,2,10,roll,c);return p;}
        foreach(var pair in c.Chances)
        {
            var candidate=Selected(Math.Max(0,(pair.Value-.001)/100),pair.Key);Check(candidate.Active==(pair.Value>0),"personality below boundary "+pair.Key);
            candidate=Selected(pair.Value/100,pair.Key);Check(!candidate.Active,"chance boundary exclusive "+pair.Key);
        }
        var denied=Selected(.8);Check(denied.Trace!.Reason=="peek-roll-declined","decline recorded");
        Check(!denied.TrySelect("door","enemy","heard","Normal",contact,48,56,0,c),"same evidence cannot reroll after cooldown");
        Check(!denied.TrySelect("door","enemy","heard","Normal",contact,1,12,0,c),"fresh evidence still respects cooldown");
        Check(denied.TrySelect("door","enemy","heard","Normal",contact,1,56,0,c),"fresh evidence and elapsed cooldown allow retry");
        denied.End("cancel",56);denied.Clear();Check(denied.Trace==null&&denied.TrySelect("door","enemy","heard","Normal",contact,1,2,0,c),"raid clear releases history");
        foreach(var random in new[]{double.NaN,double.PositiveInfinity,-.1,1.0})Check(!Selected(random).Active,"invalid RNG refused");
        var p=Selected();p.Start(stack,10);
        bool Tick(DoorPoint pos,double time,bool ground=true,bool at=false,double speed=1.3,double stamina=50,double vy=0,bool seen=false)
            =>p.Observe(pos,speed,stamina,ground,at,seen,2+time-10,time,c,vy);
        Tick(stack,10.1);Check(!p.MayJump(true,true,1.4,10.1,c),"velocity alone without actual displacement is insufficient");
        var running=stack+new DoorPoint(.25,0,0);Tick(running,10.2);
        Check(p.MayJump(true,true,1.2,10.2,c),"native eligibility + observed directional motion");
        Check(!p.MayJump(false,true,1.2,10.2,c)&&!p.MayJump(true,false,1.2,10.2,c)&&!p.MayJump(true,true,.5,10.2,c),"native refusal, airborne and no run-up space");
        Tick(running,10.21,speed:-2);Check(!p.MayJump(true,true,1.2,10.21,c),"moving in opposite direction is not run-up");
        Tick(running,10.22,stamina:34.9);Check(!p.MayJump(true,true,1.2,10.22,c),"stamina threshold enforced");
        Tick(running,10.23);p.JumpAccepted(true,10.23);Check(!p.Trace!.TakeoffObserved,"native request is not takeoff");
        Tick(running,10.24,ground:false,vy:-1);Check(!p.Trace.TakeoffObserved,"falling is not jump takeoff");
        Tick(running+new DoorPoint(.1,.2,0),10.3,ground:false,vy:2);Check(p.Trace.TakeoffObserved&&!p.Trace.LandingObserved,"upward native motion confirms takeoff only");
        Tick(landing+new DoorPoint(0,.4,0),10.7,ground:false,at:true,vy:-2);Check(p.Trace.State=="airborne"&&p.Trace.ObservationElapsed==0,"arrival in air cannot start observation");
        Tick(landing,10.9,at:true);Check(p.Trace.State=="landing"&&p.Trace.LandingObserved,"landing separately observed");
        Tick(landing,10.91,at:true);Check(p.Trace.State=="observing","observation begins after grounded arrival");
        Tick(landing,11.2,at:true);Check(p.Trace.State=="observing","full observation window from arrival");
        Tick(landing,11.32,at:true);Check(p.Trace.State=="return-pending","return after maximum observation");
        p.ReturnStarted(11.32);Tick(landing,11.4);Check(!p.MayJump(true,true,1.4,11.4,c),"return jump default OFF");
        Tick(stack,11.9,at:true);Check(p.Trace.Returned&&p.Trace.State=="completed","return confirmed only at observed destination");
        Check(!Tick(stack,12,at:true),"completed plan cannot resume");
        foreach(var test in new[]{"no-takeoff","no-runup","timeout","visible","rewind","nan","landing-missed"})
        {
            p=Selected();p.Start(stack,10);
            if(test=="no-takeoff"){Tick(running,10.2);p.JumpAccepted(true,10.2);Tick(running,10.7);}
            if(test=="no-runup")Tick(stack,11.01,speed:0);
            if(test=="timeout")Tick(running,14);
            if(test=="visible")Tick(running,10.1,seen:true);
            if(test=="rewind")Tick(running,9.9);
            if(test=="nan")Tick(new(double.NaN,0,0),10.1);
            if(test=="landing-missed"){Tick(running,10.2);p.JumpAccepted(true,10.2);Tick(running,10.3,ground:false,vy:2);Tick(stack,10.7);Tick(stack,11.4);}
            Check(!p.Active&&!p.Trace!.Returned,"unsafe/failed attempt ends: "+test);
            if(test=="visible")Check(p.Trace!.VisualAcquired,"real visual acquisition recorded on interruption");
        }
        p=Selected();p.Opening(10);Tick(stack,14.4);Check(p.Active&&!p.Trace!.DoorOpenObserved,"opening request not completion");Tick(stack,14.6);Check(!p.Active,"opening watchdog");
        p=Selected();p.Opening(10);p.Opened(11);p.Start(stack,11.2);Tick(running,11.3);Check(Math.Abs(p.Trace!.Elapsed-.1)<.001,"opening time excluded from movement ceiling");
        Check(p.SameContact("enemy",contact)&&!p.SameContact("other",contact)&&!p.SameContact("enemy",contact+new DoorPoint(3,0,0)),"no new/live target substitution");
        // Return jump still requires its own native takeoff and landing, and cannot loop.
        c.ReturnJump=true;p=Selected();p.Start(stack,10);Tick(running,10.2);p.JumpAccepted(true,10.2);Tick(running,10.3,ground:false,vy:2);Tick(landing,10.7,at:true);Tick(landing,10.71,at:true);Tick(landing,11.12,at:true);
        p.ReturnStarted(11.12);Tick(running,11.2);Check(p.MayJump(true,true,1,11.2,c),"opt-in return jump eligible");p.JumpAccepted(true,11.2);
        Tick(running,11.3,ground:false,vy:2);Tick(stack,11.7,at:true);Tick(stack,11.71,at:true);Check(p.Trace!.Returned&&p.Trace.ReturnLandingObserved,"return jump completion needs landing");
        c.ReturnJump=false;
        for(int degrees=0;degrees<360;degrees+=5)
        {
            double a=degrees*Math.PI/180;DoorPoint R(DoorPoint v)=>new(15+v.X*Math.Cos(a)-v.Z*Math.Sin(a),2+v.Y,20+v.X*Math.Sin(a)+v.Z*Math.Cos(a));
            var g=new DoorGeometry{Id="test",State="Open",Center=R(new()),Axis=new(Math.Cos(a),0,Math.Sin(a)),Normal=new(-Math.Sin(a),0,Math.Cos(a)),HalfWidth=.5};
            var raw=DoorJumpGeometry.Landing(g,R(stack));
            Check((raw-R(landing)).Length<.001,"rotation-invariant corridor landing "+degrees);
            Check(DoorJumpGeometry.Route(g,R(stack),raw,raw,new[]{R(stack),raw},true)=="","direct corridor path "+degrees);
            Check(DoorJumpGeometry.Route(g,R(stack),raw,raw,new[]{R(stack),R(new(0,0,1)),raw},true)!="","route cannot enter room "+degrees);
            Check(DoorJumpGeometry.ContainsMotion(g,R(stack),raw,R(new(0,.7,-1.25))),"observed jump remains on verified lane "+degrees);
            Check(!DoorJumpGeometry.ContainsMotion(g,R(stack),raw,R(new(0,.2,0))),"actual room entry cannot continue "+degrees);
            Check(!DoorJumpGeometry.ContainsMotion(g,R(stack),raw,R(new(0,1.2,-1.25))),"actual motion exceeds checked height "+degrees);
            Check(!DoorJumpGeometry.ContainsMotion(g,R(stack),raw,R(new(0,0,-2))),"actual sideways drift refused "+degrees);
        }
        var plain=new DoorGeometry{Id="door",State="Open",Center=new(),Axis=new(1,0,0),Normal=new(0,0,1),HalfWidth=.5};
        Check(DoorJumpGeometry.Route(plain,stack,landing,landing,new[]{stack,landing},false)!="","partial route refused");
        Check(DoorJumpGeometry.Route(plain,stack,landing,landing+new DoorPoint(0,.3,0),new[]{stack,landing},true)!="","wrong-floor sample refused");
        Check(DoorJumpGeometry.Route(plain,stack,landing,landing,new[]{stack,new DoorPoint(-.8,0,-2),landing},true)!="","corner detour is not a jumping lane");
        using(var pe=new PEReader(File.OpenRead("Client/bin/Release/netstandard2.1/COMET.dll")))
        {
            var md=pe.GetMetadataReader();
            List<string> Calls(string method)=>FloorHearingTests.ReadIl(pe.GetMethodBody(md.TypeDefinitions.Select(md.GetTypeDefinition).Single(x=>md.GetString(x.Name)=="Plugin").GetMethods().Select(md.GetMethodDefinition).Single(x=>md.GetString(x.Name)==method).RelativeVirtualAddress).GetILBytes()!)
                .Where(x=>x.op==OpCodes.Call||x.op==OpCodes.Callvirt).Select(x=>FloorHearingTests.Name(md,(int)x.operand!)).ToList();
            foreach(var required in new[]{"IsPmcAi","get_Authority","Ghost","get_IsVisible","get_CurrentSelfDecision","get_CurrentSquadDecision","get_IsUnderFire","get_EnemyKnown","SameContact"})Check(Calls("JumpGate").Contains(required),"compiled scope contract "+required);
            foreach(var required in new[]{"CapsuleCastNonAlloc","OverlapCapsuleNonAlloc","Raycast","Clear"})Check(Calls("JumpLane").Contains(required),"compiled swept clearance "+required);
            foreach(var required in new[]{"get_Velocity","get_IsGrounded","ControlledJump","Observe","WalkToPointByWay","LookToPoint"})Check(Calls("StepDoorJump").Contains(required),"compiled native observation "+required);
            foreach(var required in new[]{"Interact","ExecuteInteraction","InRangeToInteract","ShallInteract"})Check(Calls("RequestJumpDoorOpen").Contains(required),"native opening "+required);
            Check(Calls("StepDoor").Contains("TryStartDoorJump")&&Calls("Release").Contains("EndDoorJump")&&Calls("Update").Contains("RefreshDoorJump"),"D01 handoff and lifecycle integration");
            Check(Calls("StepDoorJump").Contains("OnJumpLane")&&Calls("OnJumpLane").Contains("ContainsMotion"),"actual runtime motion checked by tested geometry");
            Check(Calls("EndDoorJump").Contains("get_EnemyProfileId")&&Calls("EndDoorJump").Contains("GetHashCode"),"acquisition on release restricted to the original contact instance");
            foreach(var method in new[]{"JumpGate","StepDoorJump","PrepareJumpRoutes","JumpLane","RequestJumpDoorOpen","FinishDoorJump"})foreach(var banned in new[]{"get_EnemyPosition","get_EnemyPlayer","FindObjectsOfType","Teleport","set_DoorState","set_IsGrounded","SetEnemyVisible"})Check(!Calls(method).Contains(banned),"no hidden position or forced game state "+method+":"+banned);
        }
        Console.WriteLine($"D02: {n} checks passed (chance, route geometry, native scope, opening, actual jump/landing, interruptions, cooldown, persistence). No Unity raid simulated.");return n;
    }
}
