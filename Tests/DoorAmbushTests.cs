using Comet.Core;
using Comet.Server;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text.Json;

internal static class DoorAmbushTests
{
    internal static int Run()
    {
        int n=0;void Check(bool ok,string why){if(!ok)throw new Exception("D09: "+why);n++;}
        void Invalid(Action a){try{a();}catch(ArgumentException){n++;return;}throw new Exception("D09 invalid config accepted");}
        var c=new DoorAmbushConfig();c.Validate();
        Check(c.Enabled&&c.AllowFeint&&c.DoorDistance==8&&c.ContactLifetime==10&&c.TargetCooldown==60,"approved defaults");
        foreach(var (name,min,max) in new[]{("Normal",6,10),("Chad",6,10),("GigaChad",6,10),("Rat",12,20),("SnappingTurtle",10,15),("Coward",5,8),("Timmy",5,8),("Wreckless",5,8)})
            Check(c.Waits[name].Min==min&&c.Waits[name].Max==max,"wait range "+name);
        foreach(var bad in new[]{double.NaN,double.PositiveInfinity,-1d})
        {Invalid(()=>new DoorAmbushConfig{DoorDistance=bad}.Validate());Invalid(()=>new DoorAmbushConfig{ContactLifetime=bad}.Validate());Invalid(()=>new DoorAmbushConfig{TargetCooldown=bad}.Validate());}
        Invalid(()=>new DoorAmbushConfig{Waits=null!}.Validate());Invalid(()=>new DoorAmbushConfig{Waits=new()}.Validate());
        var range=new DoorAmbushConfig();range.Waits["Rat"].Min=30;Invalid(range.Validate);
        range=new();range.Waits["Rat"].Max=double.NaN;Invalid(range.Validate);
        Check(JsonSerializer.Deserialize<CometConfig>("{}")!.DoorAmbush.Waits["Rat"].Max==20,"older configs gain D09 defaults");
        Invalid(()=>new CometConfig{DoorAmbush=null!}.Validate());
        var dir=Path.Combine(Path.GetTempPath(),"comet-d09-"+Guid.NewGuid().ToString("N"));
        try{
            var store=new ConfigStore(Path.Combine(dir,"config.json"));var draft=store.Read();draft.DoorAmbush.Waits["Rat"].Min=11;draft.DoorAmbush.AllowFeint=false;
            var saved=store.Save(draft,draft.Revision);var loaded=JsonSerializer.Deserialize<CometConfig>(store.ForGame())!;
            Check(loaded.DoorAmbush.Waits["Rat"].Min==11&&!loaded.DoorAmbush.AllowFeint&&loaded.DoorSearchClear.Chance==70,"server atomic roundtrip preserves other features");
            saved.DoorAmbush.ContactLifetime=26;Invalid(()=>store.Save(saved,saved.Revision));Check(store.Read().DoorAmbush.ContactLifetime==10,"invalid edit cannot replace saved settings");
        }finally{Directory.Delete(dir,true);}
        bool Begin(DoorAmbush p,string personality="Rat",double age=2,double now=100,double held=0,double limit=20,double memory=25,double roll=.5,string target="enemy")
            =>p.Begin(target,"heard",personality,new(0,0,5),age,now,roll,memory,held,limit,c);
        DoorAmbush Start(){var p=new DoorAmbush();Check(Begin(p),"eligible Hold starts once");return p;}
        void Prepare(DoorAmbush p)=>p.Prepare(new(){Door="door",Side="left",Eligible=true,X=-1,Y=0,Z=-1},new(0,1.2,-.65),.2,"Shut");
        DoorAmbush Arrive(){var p=Start();Prepare(p);p.Request(new(-2,0,-2),true);p.Movement(new(-1,0,-1),true,true,101);Check(p.Angle(true,true,101.1),"observed clear angle");return p;}
        foreach(string name in c.Waits.Keys)
        {
            var p=new DoorAmbush();Check(Begin(p,name,roll:0),"minimum roll "+name);Check(p.Trace!.WaitDuration==c.Waits[name].Min,"exact minimum "+name);
            p=new();Check(Begin(p,name,roll:.99999),"upper roll "+name);Check(p.Trace!.WaitDuration<c.Waits[name].Max&&p.Trace.WaitDuration>=c.Waits[name].Min,"bounded upper "+name);
        }
        foreach(var age in new[]{-1d,10.1,25,double.NaN,double.PositiveInfinity})Check(!Begin(new(),age:age),"invalid/stale contact");
        foreach(var roll in new[]{-1d,1,double.NaN,double.PositiveInfinity})Check(!Begin(new(),roll:roll),"invalid roll");
        Check(!Begin(new(),personality:"Unknown"),"unrecognized personality does not invent a range");
        Check(!Begin(new(),held:20),"exhausted shared wait cannot restart");Check(!Begin(new(),memory:1),"native/global memory not extended");
        Check(!new DoorAmbush().Begin("enemy","heard","Rat",new(0,0,5),2,100,0,25,0,20,new(){Enabled=false}),"toggle disables start");
        var history=Start();history.End("done");
        Check(!Begin(history,age:1,now:101),"fresh cue alone cannot bypass cooldown");
        Check(!Begin(history,age:62,now:160),"same old evidence cannot retry");
        Check(Begin(history,age:1,now:161),"cooldown AND fresh cue permits retry");history.End("done");
        var bounded=new DoorAmbush();for(int i=0;i<32;i++){Check(Begin(bounded,target:"enemy-"+i),"bounded attempt");bounded.End("done");}
        Check(!Begin(bounded,target:"overflow"),"bounded history refuses new key instead of evicting cooldown");bounded.Clear();Check(Begin(bounded,target:"overflow"),"disposal clears history");
        var planning=Start();Check(!planning.Observe(101.1,3.1,true,new(0,0,5),0,20,12)&&planning.Trace!.Reason=="ambush-planning-expired","planning timeout");
        var request=Start();Prepare(request);request.Request(new(-2,0,-2),true);Check(request.Trace!.MovementAccepted&&!request.Trace.Arrived&&!request.Trace.AngleVerified,"request is not observed arrival");
        request.Movement(new(-1,0,-1),false,true,100.5);Check(!request.Trace.Arrived&&request.Trace.MovementObserved,"proximity needs native completion");
        request.Movement(new(-3,0,-3),true,true,100.6);Check(!request.Trace.Arrived,"native completion needs proximity");
        request.Movement(new(-1,0,-1),true,true,101);Check(request.Trace.Arrived&&!request.Trace.AngleVerified,"arrival distinct from angle");
        Check(!request.Angle(true,false,101.1)&&request.Active,"settling allows brief pose transition");
        Check(!request.Angle(true,false,102.1)&&!request.Active,"no endless muzzle into wall");
        var refused=Start();Prepare(refused);refused.Request(new(-1,0,-1),false);Check(!refused.Active&&!refused.Trace!.Arrived,"native move refusal");
        var replaced=Start();Prepare(replaced);replaced.Request(new(-2,0,-2),true);replaced.Movement(new(-1,0,-1),true,false,101);Check(!replaced.Active&&!replaced.Trace!.Arrived,"replacement path not claimed");
        var arrived=Arrive();Check(arrived.Observe(102,4,true,new(0,0,5),2,20,12),"hold continues without fresh information");
        Check(arrived.Trace!.HoldElapsed==1&&arrived.Trace.Remaining==15,"wait starts on arrival, shared cap remains");
        Check(!arrived.Observe(117,19,true,new(0,0,5),17,20,12)&&arrived.Trace.Reason=="ambush-wait-complete-reconsider","selected duration ends with reevaluation");
        var limited=Arrive();Check(!limited.Observe(105,7,true,new(0,0,5),20,20,12)&&limited.Trace!.Reason=="ambush-shared-hold-limit","A12 budget wins before personality timer");
        var displaced=Arrive();Check(displaced.MaintainPosition(new(-.8,0,-1)),"small motion preserves verified hold");
        Check(!displaced.MaintainPosition(new(-1,0,0))&&!displaced.Active&&displaced.Trace!.Reason=="ambush-position-lost","external displacement ends hold instead of watching from wrong point");
        var cumulative=new DoorAmbush();Check(Begin(cumulative,held:18),"only remaining shared time available");Prepare(cumulative);cumulative.Request(new(-1,0,-1),true);cumulative.Movement(new(-1,0,-1),true,true,100.1);
        Check(!cumulative.Observe(102,4,true,new(0,0,5),0,20,12),"external budget reset cannot renew captured deadline");
        var fallback=new DoorAmbush();Check(Begin(fallback,limit:5),"legacy timer allowed");Prepare(fallback);fallback.Request(new(-1,0,-1),true);fallback.Movement(new(-1,0,-1),true,true,101);
        Check(!fallback.Observe(105,7,true,new(0,0,5),0,5,12),"A12 unavailable still has hard deadline");
        var stale=new DoorAmbush();Check(Begin(stale,age:10,memory:12),"near memory expiry begins");Prepare(stale);stale.Request(new(-1,0,-1),true);stale.Movement(new(-1,0,-1),true,true,100.1);
        Check(!stale.Observe(102,12,true,new(0,0,5),2,20,12)&&stale.Trace!.Reason=="ambush-contact-expired","memory expiry wins");
        foreach(var mutation in new[]{"forgotten","new-position","fresh-same-position","rewind","budget","angle"})
        {
            var p=Arrive();bool active=mutation switch{
                "forgotten"=>p.Observe(102,4,false,new(0,0,5),2,20,12),
                "new-position"=>p.Observe(102,4,true,new(0,0,9),2,20,12),
                "fresh-same-position"=>p.Observe(102,1,true,new(0,0,5),2,20,12),
                "rewind"=>p.Observe(99,1,true,new(0,0,5),2,20,12),
                "budget"=>p.Observe(102,4,true,new(0,0,5),20,20,12),
                _=>p.Angle(false,true,102)};
            Check(!active&&!p.Active,"interruption "+mutation);
            p.Movement(new(-1,0,-1),true,true,103);Check(!p.Angle(true,true,103),"late callbacks cannot restart "+mutation);
        }
        var progress=new PlanProgress(new());
        for(int i=0;i<=80;i++)progress.Observe(new(){Now=i*.25,Owned=true,HasThreat=true,Action="Hold",Target="enemy",X=i<20?i*.05:1,Scoped=true});
        Check(progress.Current.HoldSeconds==20&&progress.Current.Reconsider,"D09 remains Hold: walking/posture changes do not reset A12 wait");
        // Inspect shipped IL, not only policy tests: native hooks, guards and cleanup must actually be wired.
        using(var pe=new PEReader(File.OpenRead("Client/bin/Release/netstandard2.1/COMET.dll")))
        {
            var md=pe.GetMetadataReader();var plugin=md.TypeDefinitions.Select(md.GetTypeDefinition).Single(x=>md.GetString(x.Name)=="Plugin");
            List<string> Calls(string method)
            {
                // Compiler-generated delegate bodies may live in a nested display class.
                var definitions=method.StartsWith("<",StringComparison.Ordinal)?md.MethodDefinitions.Select(md.GetMethodDefinition):plugin.GetMethods().Select(md.GetMethodDefinition);
                var definition=definitions.Single(x=>md.GetString(x.Name)==method);
                var calls=new List<string>();
                foreach(var instruction in FloorHearingTests.ReadIl(pe.GetMethodBody(definition.RelativeVirtualAddress).GetILBytes()!))
                {
                    if(instruction.op!=OpCodes.Call&&instruction.op!=OpCodes.Callvirt&&instruction.op!=OpCodes.Ldftn)continue;
                    int token=(int)instruction.operand!;var handle=MetadataTokens.EntityHandle(token);
                    if(handle.Kind==HandleKind.MethodSpecification)token=MetadataTokens.GetToken(md.GetMethodSpecification((MethodSpecificationHandle)handle).Method);
                    string name=FloorHearingTests.Name(md,token);calls.Add(name);
                    // I01 runs startup registrations through named, checked steps. Follow the actual IL delegate.
                    if(instruction.op==OpCodes.Ldftn&&name.StartsWith("<"+method+">",StringComparison.Ordinal))calls.AddRange(Calls(name));
                }
                return calls;
            }
            foreach(var (method,call) in new[]{("Awake","PatchDoorAmbush"),("Choose","KeepDoorAmbush"),("Release","EndDoorAmbush"),("AmbushGate","FakeScope"),("AmbushGate","get_GrenadeDangerPoint"),
                ("KeepDoorAmbush","RefreshProgress"),("PlanAmbushStep","TryBegin"),("PlanAmbushStep","WalkToPointByWay"),("ValidateAmbushPoint","ValidateDoor"),("StepDoorAmbush","get_WeaponRoot"),("StepDoorAmbush","get_EyePosition"),
                ("RecordDoorAmbush","Publish"),("RecordDoorAmbush","get_DoorAmbush"),("TryStartDoorFake","AmbushGate"),("FakeGate","AmbushGate")})
                Check(Calls(method).Contains(call),"native integration "+method+" -> "+call);
        }
        var fixture=Arrive();fixture.Observe(102,4,true,new(0,0,5),2,20,12);
        var envelope=new{schema=1,feature="D09",stage="evaluated",decision="DoorAmbush",reason=fixture.Trace!.Reason,botInstance="instance",doorAmbush=fixture.Trace,doorAmbushConfig=c};
        File.WriteAllText("artifacts/d09-fixture.json",JsonSerializer.Serialize(envelope,new JsonSerializerOptions{PropertyNamingPolicy=JsonNamingPolicy.CamelCase,WriteIndented=true}));
        var restored=JsonSerializer.Deserialize<DoorAmbushTrace>(JsonSerializer.Serialize(fixture.Trace));
        Check(restored!.AngleVerified&&restored.Arrived&&!restored.VisualContact&&restored.Remaining==15,"observations survive JSON without invented visual detection");
        Console.WriteLine($"PASS D09: {n} checks (native raid geometry not simulated)");return n;
    }
}
