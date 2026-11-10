using Comet.Core;
using Comet.Server;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.Json;

internal static class DoorApproachTests
{
    public static int Run()
    {
        int n=0;void Check(bool ok,string why){n++;if(!ok)throw new Exception("D01: "+why);}
        void Invalid(Action a){try{a();}catch(ArgumentException){n++;return;}throw new Exception("D01 accepted invalid config");}
        var c=new DoorApproachConfig();c.Validate();
        Check(c.Enabled&&c.DoorDistance==8&&c.ContactDistance==12&&c.UnseenDelay==1.5&&c.ContactLifetime==60&&c.ApproachLimit==12,"approved defaults");
        foreach(var v in new[]{double.NaN,double.PositiveInfinity,-1,999})
            foreach(var set in new Action<DoorApproachConfig>[]{x=>x.DoorDistance=v,x=>x.ContactDistance=v,x=>x.UnseenDelay=v,x=>x.ContactLifetime=v,x=>x.ApproachLimit=v})
            {var q=new DoorApproachConfig();set(q);Invalid(q.Validate);}
        Invalid(()=>new CometConfig{DoorApproach=null!}.Validate());
        var old=JsonSerializer.Deserialize<CometConfig>("{\"Enabled\":false}")!;old.Validate();Check(old.DoorApproach.Enabled&&!old.Enabled,"migration preserves master OFF");
        var dir=Path.Combine(Path.GetTempPath(),"comet-d01-"+Guid.NewGuid().ToString("N"));
        try{
            var store=new ConfigStore(Path.Combine(dir,"config.json"));var q=store.Read();q.DoorApproach=new(){Enabled=false,DoorDistance=10,ContactDistance=14,UnseenDelay=2,ContactLifetime=40,ApproachLimit=15};store.Save(q,q.Revision);
            var saved=JsonSerializer.Deserialize<CometConfig>(store.ForGame())!.DoorApproach;
            Check(!saved.Enabled&&saved.DoorDistance==10&&saved.ContactDistance==14&&saved.UnseenDelay==2&&saved.ContactLifetime==40&&saved.ApproachLimit==15,"server save/game response roundtrip");
            var revision=store.Read().Revision;q=store.Read();q.DoorApproach.ContactLifetime=0;Invalid(()=>store.Save(q,q.Revision));Check(store.Read().Revision==revision,"invalid edit atomic");
        }finally{Directory.Delete(dir,true);}
        Check(DoorPolicy.Gate(true,true,false,true,false,60,1.5,c)=="","eligible boundary");
        foreach(bool scope in new[]{true,false})foreach(bool search in new[]{true,false})foreach(bool urgent in new[]{true,false})
        foreach(bool known in new[]{true,false})foreach(bool visible in new[]{true,false})
            Check((DoorPolicy.Gate(scope,search,urgent,known,visible,2,2,c)=="")==(scope&&search&&!urgent&&known&&!visible),"scope/ownership/priority truth table");
        foreach(var age in new[]{-1,double.NaN,double.PositiveInfinity,60.01})Check(DoorPolicy.Gate(true,true,false,true,false,age,2,c)!="","stale/invalid contact");
        Check(DoorPolicy.Gate(true,true,false,true,false,2,1.49,c)!="","do not interrupt momentary occlusion");
        Check(DoorPolicy.Gate(true,true,false,true,false,2,2,new(){Enabled=false})!="","disabled gate");
        var g=new DoorGeometry{Id="test",State="Shut",Center=new(0,0,0),Axis=new(1,0,0),Normal=new(0,0,1),HalfWidth=.5};
        var bot=new DoorPoint(0,0,-3);var contact=new DoorPoint(0,0,3);var route=new[]{bot,contact};
        Check(DoorPolicy.Geometry(g,bot,contact,route,c)=="","closed door on route");g.State="Open";Check(DoorPolicy.Geometry(g,bot,contact,route,c)=="","open door eligible");
        foreach(var state in new[]{"Locked","Interacting","Broken","Unknown"}){g.State=state;Check(DoorPolicy.Geometry(g,bot,contact,route,c)!="","unsupported/transition state");}g.State="Shut";
        Check(DoorPolicy.Geometry(g,bot,new(0,0,-1),route,c)!="","same side rejected");
        Check(DoorPolicy.Geometry(g,new(0,3,-3),contact,route,c)=="door-wrong-floor","door on different floor");
        Check(DoorPolicy.Geometry(g,new(0,0,-9),contact,route,c)=="door-too-far","maximum door range");
        Check(DoorPolicy.Geometry(g,bot,new(0,0,13),route,c)=="door-contact-too-far","contact distance limit");
        Check(DoorPolicy.Geometry(g,bot,contact,new[]{bot,new DoorPoint(4,0,-2),new(4,0,2),contact},c)=="door-not-on-known-route","infinite plane crossing not enough");
        Check(DoorPolicy.Geometry(g,bot,contact,new[]{bot,new DoorPoint(0,3,-1),new(0,3,1),contact},c)=="door-not-on-known-route","route crosses another floor");
        Check(DoorPolicy.Geometry(g,bot,contact,new[]{bot,new DoorPoint(0,0,1),new(double.NaN,0,2),contact},c)=="door-invalid-route","later invalid corner cannot be hidden by earlier crossing");
        foreach(int side in new[]{-1,1})foreach(int variant in new[]{0,1})
        {
            var point=DoorPolicy.Stack(g,bot,side,variant);var path=new[]{bot,point};
            Check(DoorPolicy.Placement(g,bot,point,point,path,true)=="","both frame sides and narrow-corridor fallback");
            Check(DoorPolicy.Placement(g,bot,point,point,path,false)!="","partial path refused");
            Check(DoorPolicy.Placement(g,bot,point,point+new DoorPoint(.4,0,0),path,true)=="door-bad-navmesh-sample","sample cannot jump wall gap");
            Check(DoorPolicy.Placement(g,bot,point,point+new DoorPoint(0,3,0),path,true)!="","sample cannot jump floor");
            Check(DoorPolicy.Placement(g,bot,point,point,new[]{bot,new DoorPoint(0,0,1),point},true)=="door-route-crosses-room-or-floor","stack route cannot enter room first");
            Check(DoorPolicy.Placement(g,bot,point,point,new[]{bot,new DoorPoint(20,0,-3),point},true)=="door-route-detour","long detour refused");
            Check(DoorPolicy.Placement(g,bot,point,point,new[]{bot,new DoorPoint(0,3,-3),point},true)=="door-route-crosses-room-or-floor","stairs to another floor refused");
        }
        var opening=new DoorPoint(0,0,-.9);Check(DoorPolicy.Placement(g,bot,opening,opening,new[]{bot,opening},true)=="door-point-in-opening","never stand in the opening");
        // Rotation/translation invariance prevents axis-aligned tests from hiding a geometric bug.
        for(int degrees=0;degrees<360;degrees+=5)
        {
            double a=degrees*Math.PI/180;DoorPoint R(DoorPoint p)=>new(150+p.X*Math.Cos(a)-p.Z*Math.Sin(a),8+p.Y,-220+p.X*Math.Sin(a)+p.Z*Math.Cos(a));
            var rg=new DoorGeometry{Id="rotated",State="Open",Center=R(g.Center),Axis=new(Math.Cos(a),0,Math.Sin(a)),Normal=new(-Math.Sin(a),0,Math.Cos(a)),HalfWidth=.5};
            Check(DoorPolicy.Geometry(rg,R(bot),R(contact),route.Select(R).ToArray(),c)=="","rotated route "+degrees);
            foreach(int side in new[]{-1,1}){var pt=DoorPolicy.Stack(rg,R(bot),side);Check(DoorPolicy.Placement(rg,R(bot),pt,pt,new[]{R(bot),pt},true)=="","rotated side "+degrees);}
        }
        var bad=new DoorCandidate{Door="nearest",Eligible=false,Score=0,Reason="door-point-occupied"};
        var good=new DoorCandidate{Door="farther",Side="right",Eligible=true,Score=4,PathLength=4,X=1.2,Y=0,Z=-.9};
        Check(ReferenceEquals(DoorPolicy.Best(new[]{bad,good}),good),"bad nearest door cannot hide usable alternative");
        var other=new DoorCandidate{Door="best",Side="left",Eligible=true,Score=3,PathLength=3};
        Check(ReferenceEquals(DoorPolicy.Best(new[]{good,other}),other),"compare all verified sides");
        Check(DoorPolicy.Best(new[]{new DoorCandidate{Eligible=true,Score=double.NaN},bad})==null,"invalid score not eligible");
        DoorApproach Active(){var r=new DoorApproach();r.Begin("enemy","heard",contact,2,10);r.Trace!.Candidates.Add(good);r.Request(good,bot,10);r.Accept(true,10);return r;}
        var runtime=new DoorApproach();runtime.Begin("enemy","heard",contact,2,10);runtime.Request(good,bot,10);
        Check(!runtime.PlanningExpired(11)&&runtime.PlanningExpired(11.01),"pending work expires even if Search stops invoking its callback");
        Check(runtime.PlanningExpired(9)&&runtime.PlanningExpired(double.NaN),"pending work rejects invalid clocks");
        Check(runtime.Trace!.Requested&&!runtime.Trace.MovementAccepted&&!runtime.Active,"request is not movement");runtime.Accept(false,10);Check(!runtime.Active&&!runtime.Trace.Arrived,"mover refusal cannot fabricate arrival");
        runtime=Active();Check(runtime.Observe("enemy",contact,bot,2,10.5,12,true,false)&&!runtime.Trace!.Moved,"accepted stationary path not reported as movement");
        Check(runtime.Observe("enemy",contact,bot+new DoorPoint(.4,0,0),3,11,12,true,false)&&runtime.Trace!.Moved,"movement observed after displacement");
        Check(runtime.Observe("enemy",contact,good.Point,3,11.5,12,true,false)&&!runtime.Trace!.Arrived,"nearby point without native completion not arrival");
        Check(!runtime.Observe("enemy",contact,good.Point,4,12,12,true,true)&&runtime.Trace!.Arrived,"native completion and proximity confirm arrival");
        Check(!runtime.CanPlan("enemy",contact,21)&&runtime.CanPlan("enemy",contact+new DoorPoint(3,0,0),21),"no repeated frame visit for same remembered contact; changed position can plan");
        Check(runtime.CanPlan("enemy",contact,70),"bounded consumed-contact memory");
        foreach(var reason in new[]{"door-native-priority","inactive-ghost-or-non-authority","raid-ended","progress-stalled","door-point-occupied"})
        {runtime=Active();runtime.End(reason,11);Check(!runtime.Active&&runtime.Trace!.Reason==reason&&!runtime.Trace.Arrived,"interruption preserves truth: "+reason);}
        runtime=Active();Check(!runtime.Observe("different",contact,bot,2,11,12,true,false)&&runtime.Trace!.Reason=="door-contact-changed","target switch ends approach");
        runtime=Active();Check(!runtime.Observe("enemy",contact+new DoorPoint(3,0,0),bot,2,11,12,true,false),"new known place invalidates preparation");
        runtime=Active();Check(!runtime.Observe("enemy",contact,bot,2,11,12,false,false)&&runtime.Trace!.Reason=="door-path-replaced","replacement path yields");
        runtime=Active();Check(!runtime.Observe("enemy",contact,bot,2,22,12,true,false)&&runtime.Trace!.Reason=="door-approach-timeout","hard approach cap");
        runtime=Active();Check(!runtime.Observe("enemy",contact,bot,2,9,12,true,false),"clock rewind yields");runtime.Clear();Check(runtime.Trace==null&&runtime.CanPlan("enemy",contact,1),"lifecycle clears consumed contacts");
        var progress=new PlanProgress(new());
        bool failed=false;for(int i=0;i<28;i++)failed|=progress.Observe(new(){Now=10+i*.25,Owned=true,HasThreat=true,Action="Search",Attempt="frame",Intent="known",Route="verified",Remaining=4,Target="enemy"});
        Check(failed&&progress.Blocked("frame",17)&&progress.Blocked("known",17),"A12 interrupts stuck Search and blocks frame/intent");
        var budget=new DoorWorkBudget();Check(budget.TryBegin(1)&&budget.TryBegin(1)&&!budget.TryBegin(1),"shared two-unit cap");
        Check(budget.TryBegin(2),"next frame renews budget");budget.Charge(.5);Check(!budget.TryBegin(2),"soft time cap");budget.Clear();Check(budget.TryBegin(2),"raid resets budget");budget.Charge(double.NaN);Check(!budget.TryBegin(2),"unknown timing fails closed");
        runtime=Active();using(var doc=JsonDocument.Parse(Newtonsoft.Json.JsonConvert.SerializeObject(new{doorApproach=runtime.Trace},Comet.Client.Diagnostics.Json)))
            Check(doc.RootElement.GetProperty("doorApproach").GetProperty("movementAccepted").GetBoolean(),"camel case telemetry contract");
        using(var pe=new PEReader(File.OpenRead("Client/bin/Release/netstandard2.1/COMET.dll")))
        {
            var md=pe.GetMetadataReader();
            List<string> Calls(string method)=>FloorHearingTests.ReadIl(pe.GetMethodBody(md.TypeDefinitions.Select(md.GetTypeDefinition).Single(x=>md.GetString(x.Name)=="Plugin").GetMethods().Select(md.GetMethodDefinition).Single(x=>md.GetString(x.Name)==method).RelativeVirtualAddress).GetILBytes()!)
                .Where(x=>x.op==OpCodes.Call||x.op==OpCodes.Callvirt).Select(x=>FloorHearingTests.Name(md,(int)x.operand!)).ToList();
            foreach(var required in new[]{"IsSupportedAi","get_Authority","Ghost","get_BotActive","get_CurrentSelfDecision","get_CurrentSquadDecision","get_EnemyKnown","Gate"})Check(Calls("DoorGate").Contains(required),"actual scope contract: "+required);
            foreach(var required in new[]{"CanGoToPoint","Placement","OverlapCapsuleNonAlloc","Raycast","Clear"})Check(Calls("ValidateDoor").Contains(required),"native validation contract: "+required);
            Check(Calls("StepDoor").Contains("WalkToPointByWay")&&Calls("FinishDoor").Contains("OwnsDoorPath")&&Calls("FinishDoor").Contains("Cancel"),"native movement and scoped cancellation wired");
            Check(Calls("Release").Contains("EndDoor")&&Calls("Update").Contains("RefreshDoor"),"release and sweep lifecycle wired");
            foreach(var method in new[]{"DoorGate","StartDoorPlan","ValidateDoor","StepDoor"})foreach(var banned in new[]{"get_EnemyPosition","get_EnemyPlayer","FindObjectsOfType","Teleport","DoThrow","set_DoorState"})Check(!Calls(method).Contains(banned),"no live position / global scene / forced door: "+method+":"+banned);
        }
        Console.WriteLine($"D01: {n} checks passed (geometry, routes, lifecycle, A12, scope, server persistence and native contracts). No Unity raid simulated.");return n;
    }
}
