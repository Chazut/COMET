using Comet.Core;
using Comet.Server;
using System.Text.Json;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

internal static class DoorHoldTests
{
    internal static int Run()
    {
        int n=0;void Check(bool ok,string why){if(!ok)throw new Exception("D10: "+why);n++;}
        void Invalid(Action a){try{a();}catch(ArgumentException){n++;return;}throw new Exception("D10 invalid config accepted");}
        var c=new DoorAmbushConfig();Check(c.MinimumUsefulWait==2,"approved useful-wait default");
        foreach(double bad in new[]{-1d,10.1,double.NaN,double.PositiveInfinity})Invalid(()=>new DoorAmbushConfig{MinimumUsefulWait=bad}.Validate());
        foreach(double limit in new[]{0d,2,10}){new DoorAmbushConfig{MinimumUsefulWait=limit}.Validate();n++;}
        Check(JsonSerializer.Deserialize<CometConfig>("{}")!.DoorAmbush.MinimumUsefulWait==2,"migration from old config");
        var directory=Path.Combine(Path.GetTempPath(),"comet-d10-"+Guid.NewGuid().ToString("N"));
        try
        {
            var store=new ConfigStore(Path.Combine(directory,"config.json"));var draft=store.Read();draft.DoorAmbush.MinimumUsefulWait=3.5;
            var saved=store.Save(draft,draft.Revision);Check(store.Read().DoorAmbush.MinimumUsefulWait==3.5,"atomic save/reload");
            saved.DoorAmbush.MinimumUsefulWait=11;Invalid(()=>store.Save(saved,saved.Revision));Check(store.Read().DoorAmbush.MinimumUsefulWait==3.5,"invalid save preserves config");
        }finally{Directory.Delete(directory,true);}
        DoorHoldTrace Pick(bool angle=false,double current=0,bool safe=true,double protection=1,double length=3,double available=10,double wait=8,double min=2)
            =>DoorHoldSelection.Evaluate(angle,current,safe,protection,length,available,wait,min,12);
        var stay=Pick(true,1,length:99,available:.3);Check(stay.Outcome=="stay"&&stay.EstimatedTravel==0&&stay.UsefulWait==.3,"good actual angle stays even with little remaining time");
        Check(Pick(true,1d/3).Outcome=="move","angle alone without protection is insufficient");
        var move=Pick();Check(move.Outcome=="move"&&move.EstimatedTravel==3&&move.UsefulWait==7,"route estimate includes walking and settling");
        Check(Pick(available:5).Outcome=="move","exact two second useful wait accepted");
        Check(Pick(available:4.9).Outcome=="reconsider","not enough useful wait while exposed");
        Check(Pick(current:1,available:4.9).Outcome=="native-hold","not enough time preserves good native cover");
        Check(Pick(current:1,protection:2d/3).Reason=="hold-door-worse-protection","do not trade good cover for a nearer worse doorway");
        Check(Pick(protection:1d/3).Reason=="hold-door-insufficient-protection","two cover samples required");
        Check(Pick(current:1,safe:false).Outcome=="native-hold","no route/door does not ban useful native Hold");
        Check(Pick(safe:false).Outcome=="reconsider","no useful cover requests reevaluation");
        Check(Pick(length:20,available:40).Reason=="hold-door-not-enough-time","approach timeout considered too");
        Check(Pick(wait:1).Reason=="hold-door-not-enough-time","personality duration limits useful wait");
        Check(Pick(available:3,min:0).Outcome=="reconsider","zero minimum never permits an already exhausted arrival");
        Check(Pick(available:3.01,min:0).Outcome=="move","zero setting disables reserved wait, not deadlines");
        Check(DoorHoldSelection.Rank(1,8)<DoorHoldSelection.Rank(2d/3,1),"protection outranks a small path advantage");
        Check(DoorHoldSelection.Rank(1,2)<DoorHoldSelection.Rank(1,4),"equal protection prefers shorter path");
        foreach(double bad in new[]{-1d,double.NaN,double.PositiveInfinity})
        {Check(Pick(length:bad).Outcome=="reconsider","bad path");Check(Pick(available:bad).Outcome=="reconsider","bad budget");Check(Pick(current:bad).Outcome=="reconsider","bad cover");}
        Check(JsonSerializer.Serialize(Pick(length:double.NaN,current:double.PositiveInfinity,available:double.NaN)).Length>0,"invalid native measurements still produce serializable diagnostics");
        for(int distance=0;distance<=12;distance++)for(int remain=1;remain<=20;remain++)
        {
            var result=Pick(length:distance,available:remain);
            Check(result.Outcome!="move"||(result.UsefulWait>=2&&result.EstimatedTravel+result.UsefulWait<=remain+1e-6),"no move exceeds available time");
        }
        DoorAmbush Prepared()
        {
            var p=new DoorAmbush();Check(p.Begin("enemy","heard","Rat",new(0,0,5),2,100,.5,25,0,20,c),"Hold accepted");
            p.Prepare(new(){Door="door",Side="current",Eligible=true,X=-1,Z=-1},new(0,1.2,-.65),.4,"Shut");return p;
        }
        var retained=Prepared();Check(retained.Retain(new(-1,0,-1),true,true,100.2),"actual retained point confirmed");
        var t=retained.Trace!;Check(t.RetainedPosition&&!t.Arrived&&!t.MovementRequested&&!t.MovementAccepted&&!t.MovementObserved&&t.AngleVerified,"retention never invents a path or arrival");
        Check(retained.Observe(102,4,true,new(0,0,5),2,20,12)&&Math.Abs(t.HoldElapsed-1.8)<.00001,"retained hold consumes original timer");
        Check(!retained.MaintainPosition(new(-1,0,0))&&!retained.Active,"retained point lost ends hold");
        Check(!retained.Retain(new(-1,0,-1),true,true,103),"late retained check cannot restart");
        foreach(string bad in new[]{"muzzle","eyes","position","time","requested"})
        {
            var p=Prepared();if(bad=="requested")p.Request(new(-1,0,-1),true);
            Check(!p.Retain(bad=="position"?new(3,0,3):new(-1,0,-1),bad!="eyes",bad!="muzzle",bad=="time"?121:100.2),"retention causal guard "+bad);
        }
        var budget=Prepared();Check(budget.Retain(new(-1,0,-1),true,true,100.2),"retain for budget test");
        Check(!budget.Observe(120,22,true,new(0,0,5),0,20,12),"changing Hold representation never refreshes deadline");
        using(var pe=new PEReader(File.OpenRead("Client/bin/Release/netstandard2.1/COMET.dll")))
        {
            var md=pe.GetMetadataReader();var plugin=md.TypeDefinitions.Select(md.GetTypeDefinition).Single(x=>md.GetString(x.Name)=="Plugin");
            List<string> Calls(string method)=>FloorHearingTests.ReadIl(pe.GetMethodBody(plugin.GetMethods().Select(md.GetMethodDefinition).Single(x=>md.GetString(x.Name)==method).RelativeVirtualAddress).GetILBytes()!)
                .Where(x=>x.op==OpCodes.Call||x.op==OpCodes.Callvirt).Select(x=>{var token=(int)x.operand!;var h=MetadataTokens.EntityHandle(token);if(h.Kind==HandleKind.MethodSpecification)token=MetadataTokens.GetToken(md.GetMethodSpecification((MethodSpecificationHandle)h).Method);return FloorHearingTests.Name(md,token);}).ToList();
            foreach(var (method,call) in new[]{("PlanAmbushStep","TryRetainDoorHold"),("PlanAmbushStep","EvaluateDoorHold"),("PlanAmbushStep","FinishDoorHoldChoice"),("EvaluateDoorHold","HoldProtection"),("EvaluateDoorHold","Evaluate"),("TryRetainDoorHold","Retain"),("TryRetainDoorHold","get_EyePosition"),("TryRetainDoorHold","get_WeaponRoot"),("RecordDoorHold","Publish")})Check(Calls(method).Contains(call),"native integration "+method);
            Check(!Calls("TryRetainDoorHold").Contains("WalkToPointByWay"),"retained route must not issue a move");
        }
        var fixture=Prepared();fixture.Retain(new(-1,0,-1),true,true,100.2);fixture.Observe(102,4,true,new(0,0,5),2,20,12);
        var choice=Pick(true,1);choice.Door="door";
        File.WriteAllText("artifacts/d10-fixture.json",JsonSerializer.Serialize(new{schema=1,feature="D10",stage="evaluated",decision="DoorHold",botInstance="instance",doorAmbush=fixture.Trace,doorHold=choice,doorAmbushConfig=c},new JsonSerializerOptions{PropertyNamingPolicy=JsonNamingPolicy.CamelCase,WriteIndented=true}));
        Console.WriteLine($"PASS D10: {n} checks (native raid geometry not simulated)");return n;
    }
}
