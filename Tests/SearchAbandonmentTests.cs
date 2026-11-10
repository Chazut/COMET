using Comet.Core;
using Comet.Server;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.Json;

internal static class SearchAbandonmentTests
{
    public static int Run()
    {
        int n=0;void Check(bool ok,string why){n++;if(!ok)throw new Exception("P09: "+why);}
        void Invalid(Action a){try{a();}catch(ArgumentException){n++;return;}throw new Exception("P09 accepted invalid configuration");}
        var cfg=new SearchAbandonmentConfig();cfg.Validate();
        Check(cfg.Enabled&&cfg.Silence==20&&cfg.Inspection==3&&cfg.Radius==3&&cfg.FailedMemory==30,"approved defaults");
        foreach(var v in new[]{double.NaN,double.PositiveInfinity,-1d})
            foreach(var set in new Action<SearchAbandonmentConfig>[] {c=>c.Silence=v,c=>c.Inspection=v,c=>c.Radius=v,c=>c.FailedMemory=v})
            {var c=new SearchAbandonmentConfig();set(c);Invalid(c.Validate);}
        Invalid(()=>new SearchAbandonmentConfig{Silence=121}.Validate());Invalid(()=>new SearchAbandonmentConfig{Inspection=16}.Validate());
        Invalid(()=>new SearchAbandonmentConfig{Radius=6}.Validate());Invalid(()=>new SearchAbandonmentConfig{FailedMemory=121}.Validate());
        Invalid(()=>new CometConfig{SearchAbandonment=null!}.Validate());
        var old=JsonSerializer.Deserialize<CometConfig>("{\"Revision\":4,\"Enabled\":false}")!;old.Validate();Check(old.SearchAbandonment.Enabled&&!old.Enabled,"migration respects master disabled");
        var dir=Path.Combine(Path.GetTempPath(),"comet-p09-"+Guid.NewGuid().ToString("N"));
        try{
            var store=new ConfigStore(Path.Combine(dir,"config.json"));var c=store.Read();c.SearchAbandonment=new(){Silence=28,Inspection=5,Radius=4,FailedMemory=12,Enabled=false};store.Save(c,c.Revision);
            var game=JsonSerializer.Deserialize<CometConfig>(store.ForGame())!.SearchAbandonment;
            Check(!game.Enabled&&game.Silence==28&&game.Inspection==5&&game.Radius==4&&game.FailedMemory==12,"server save and authoritative response");
            c=store.Read();c.SearchAbandonment.Radius=99;Invalid(()=>store.Save(c,c.Revision));Check(store.Read().SearchAbandonment.Radius==4,"invalid save cannot corrupt config");
        }finally{Directory.Delete(dir,true);}
        SearchAbandonmentFrame F(double now=120)=>new(){Now=now,Stamp=100,Target="contact",Instance=42,X=2,Y=0,Z=3,Distance=2};
        SearchAbandonment Ready(double start=120){var a=new SearchAbandonment();for(int i=0;i<=12;i++)a.Evaluate(F(start+i*.25),cfg);return a;}
        var a=new SearchAbandonment();Check(!a.NeedsSafetyCheck("contact"),"dormant inspection needs no per-callback safety work");
        var first=a.Evaluate(F(),cfg);Check(first.State=="reached"&&!first.ForgetRequested,"arrival alone never sufficient");
        Check(a.NeedsSafetyCheck("contact"),"arrival keeps immediate interruption guards between geometry probes");
        for(int i=1;i<12;i++)Check(!a.Evaluate(F(120+i*.25),cfg).ForgetRequested,"inspection not early");
        var request=a.Evaluate(F(123),cfg);Check(request.ForgetRequested&&!request.ForgetObserved&&request.Inspected==3,"request not success");
        Check(!a.ObserveForgotten(F(123),cfg,false)&&a.RememberedCount==0,"failed native callback not recorded as abandonment");
        Check(a.ObserveForgotten(F(123),cfg,true)&&a.Current!.ForgetObserved&&a.Current.State=="abandoned"&&a.RememberedCount==1,"native confirmation");
        Check(a.NeedsSafetyCheck("contact"),"failed memory still reacts immediately to visible contact");
        Check(!a.NeedsSafetyCheck("another"),"unrelated dormant contact does not inherit failed memory checks");
        Check(a.Evaluate(F(124),cfg).ForgetRequested,"same old observation suppressed");
        a.ObserveForgotten(F(124),cfg,true);Check(a.Current!.MemoryRemaining==29,"retry cannot extend memory");
        Check(a.Evaluate(F(153),cfg).State=="reached"&&a.RememberedCount==0,"failed memory expires exactly");
        var early=Ready(110);Check(!early.Current!.ForgetRequested&&early.Current.Inspected==3,"inspection alone does not bypass silence");
        for(int i=53;i<=80;i++)early.Evaluate(F(100+i*.25),cfg);
        Check(early.Current!.ForgetRequested,"both gates required");
        var late=Ready();var fresh=F(123.25);fresh.Stamp=123;Check(!late.Evaluate(fresh,cfg).ForgetRequested&&late.Current!.Inspected==0,"new evidence restarts inspection");
        foreach(var edit in new Action<SearchAbandonmentFrame>[] {f=>f.Scoped=false,f=>f.Active=false,f=>f.PersonalSearch=false,f=>f.Visible=true,f=>f.Unsafe=true,
            f=>f.NativeKnown=false,f=>f.Sampled=false,f=>f.Distance=3.01,f=>f.SameFloor=false,f=>f.ClearView=false,f=>f.Facing=false})
        {
            var x=Ready();var f=F(123.25);edit(f);Check(!x.Evaluate(f,cfg).ForgetRequested,"danger/geometry/scope guard");
            Check(!x.Evaluate(F(123.5),cfg).ForgetRequested&&x.Current!.Inspected==0,"interruption requires fresh continuous inspection");
        }
        foreach(var edit in new Action<SearchAbandonmentFrame>[] {f=>f.Now=double.NaN,f=>f.Stamp=124,f=>f.Stamp=-1,f=>f.Distance=double.NaN,f=>f.Distance=-1,f=>f.X=double.PositiveInfinity,f=>f.Y=double.NaN,f=>f.Z=double.NegativeInfinity,f=>f.Target=""})
        {var f=F(123);edit(f);Check(!Ready().Evaluate(f,cfg).ForgetRequested,"invalid evidence");}
        var changed=Ready();var moved=F(123.25);moved.X+=.1;Check(changed.Evaluate(moved,cfg).Inspected==0,"changed place without timestamp reset is still a new check");
        Check(Ready().Evaluate(F(125),cfg).Inspected==0,"missing updates cannot count as inspection");
        Check(Ready().Evaluate(F(121),cfg).Inspected==0,"clock reversal resets inspection");
        var shared=Ready();var squad=F(123.25);squad.PersonalSearch=false;Check(!shared.Evaluate(squad,cfg).ForgetRequested,"squad arrival alone rejected");
        var clone=new SearchAbandonment();var mutable=F();clone.Evaluate(mutable,cfg);mutable.Now=120.25;Check(clone.Evaluate(mutable,cfg).Inspected==.25,"frame caller mutation isolated");
        foreach(var edit in new Action<SearchAbandonmentFrame>[] {f=>f.Stamp=124,f=>f.Visible=true,f=>f.Instance=43,f=>f.X=4})
        {var x=Ready();x.ObserveForgotten(F(123),cfg,true);var f=F(124);edit(f);Check(!x.Evaluate(f,cfg).ForgetRequested&&x.RememberedCount==0,"real new information or instance clears failure");}
        var noMemory=new SearchAbandonmentConfig{FailedMemory=0};var nm=Ready();nm.ObserveForgotten(F(123),noMemory,true);Check(nm.RememberedCount==0,"optional memory off");
        var bounded=new SearchAbandonment();var fast=new SearchAbandonmentConfig{Inspection=1};
        for(int j=0;j<40;j++)
        {SearchAbandonmentFrame f=F();for(int i=0;i<=4;i++){f=F(120+i*.25);f.Target="t"+j;bounded.Evaluate(f,fast);}bounded.ObserveForgotten(f,fast,true);}
        Check(bounded.RememberedCount==32,"failure cache bounded");bounded.Clear();Check(bounded.RememberedCount==0&&bounded.Current==null,"raid lifecycle clears all values");
        using(var doc=JsonDocument.Parse(Newtonsoft.Json.JsonConvert.SerializeObject(new{feature="P09",searchAbandonment=request},Comet.Client.Diagnostics.Json)))
            Check(doc.RootElement.GetProperty("searchAbandonment").GetProperty("requiredSilence").GetDouble()==20,"wire format");
        using(var pe=new PEReader(File.OpenRead("references/sain-4.5.1/SAIN.dll")))
        {
            var md=pe.GetMetadataReader();TypeDefinition Type(string name)=>md.TypeDefinitions.Select(md.GetTypeDefinition).Single(x=>md.GetString(x.Name)==name);
            List<string> Calls(string type,string method)=>FloorHearingTests.ReadIl(pe.GetMethodBody(Type(type).GetMethods().Select(md.GetMethodDefinition).Single(x=>md.GetString(x.Name)==method).RelativeVirtualAddress).GetILBytes()!)
                .Where(x=>x.op==OpCodes.Call||x.op==OpCodes.Callvirt).Select(x=>FloorHearingTests.Name(md,(int)x.operand!)).ToList();
            var tick=Calls("EnemyKnownChecker","TickEnemy");Check(tick.IndexOf("ShallKnowEnemy")>=0&&tick.IndexOf("SetEnemyKnown")>tick.IndexOf("ShallKnowEnemy"),"native result applied in same tick");
            Check(Calls("EnemyKnownPlaces","OnEnemyKnownChanged").Contains("clearAllPlaces"),"native forgetting clears locations");
            Check(Type("EnemyKnownChecker").GetFields().Select(md.GetFieldDefinition).Any(x=>md.GetString(x.Name)=="Enemy"),"verified owner field");
        }
        using(var pe=new PEReader(File.OpenRead("Client/bin/Release/netstandard2.1/COMET.dll")))
        {
            var md=pe.GetMetadataReader();var plugin=md.TypeDefinitions.Select(md.GetTypeDefinition).Single(x=>md.GetString(x.Name)=="Plugin");
            List<string> Calls(string name)=>FloorHearingTests.ReadIl(pe.GetMethodBody(plugin.GetMethods().Select(md.GetMethodDefinition).Single(x=>md.GetString(x.Name)==name).RelativeVirtualAddress).GetILBytes()!)
                .Where(x=>x.op==OpCodes.Call||x.op==OpCodes.Callvirt).Select(x=>FloorHearingTests.Name(md,(int)x.operand!)).ToList();
            var calls=Calls("CheckSearchedPlace");Check(calls.Contains("IsSupportedAi")&&calls.Contains("get_Authority")&&calls.Contains("Ghost"),"PMC authority ghost guards");
            Check(calls.Contains("get_TimeLastKnownUpdated")&&calls.Contains("Linecast"),"remembered timestamp and local physics");
            Check(calls.Contains("get_LookDirection")&&calls.Contains("get_IsFlashed"),"actual view and blindness guard");
            foreach(var name in new[]{"CheckSearchedPlace","SearchForgetObserved"})
                foreach(var forbidden in new[]{"CheckAddEnemy","AddEnemy","UpdatePersonalHeardPosition","UpdatePersonalSeenPosition","CalculatePath","FindObjectsOfType","get_EnemyPosition","get_EnemyRealPositionWhenUpdated","get_PlayerLeftArea","get_HasSeenPersonal","get_HasArrivedSquad","SetEnemyKnown"})
                    Check(!Calls(name).Contains(forbidden),"no hidden oracle/forced knowledge: "+name+" "+forbidden);
        }
        Console.WriteLine($"P09: {n} checks passed (inspection, interruptions, failure memory, native contracts, server persistence).");return n;
    }
}
