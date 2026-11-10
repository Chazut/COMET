using Comet.Core;
using Comet.Server;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.Json;

static class CloseDetectionTests
{
    public static int Run()
    {
        int n=0;
        void Check(bool ok,string why) { if(!ok) throw new Exception("P05: "+why);n++; }
        void Near(double a,double b,string why) => Check(Math.Abs(a-b)<1e-9,why);
        var config=new CometConfig();config.Validate();var c=config.CloseDetection;
        Check(c.Enabled&&c.StartDistance==6&&c.FullDistance==3&&c.ReductionPercent==100,"approved defaults");
        var old=JsonSerializer.Deserialize<CometConfig>("{\"Revision\":8,\"Enabled\":false}")!;old.Validate();
        Check(old.CloseDetection.StartDistance==6&&old.Revision==8&&!old.Enabled,"older config gains safe independent defaults");
        foreach(var edit in new Action<CloseDetectionConfig>[] {x=>x.StartDistance=double.NaN,x=>x.StartDistance=13,x=>x.FullDistance=6,x=>x.FullDistance=0,x=>x.ReductionPercent=-1,x=>x.ReductionPercent=101,x=>x.ReductionPercent=double.PositiveInfinity})
        {var bad=new CloseDetectionConfig();edit(bad);try{bad.Validate();throw new Exception("Invalid config accepted");}catch(ArgumentException){n++;}}
        try{new CometConfig{CloseDetection=null!}.Validate();throw new Exception("Null config accepted");}catch(ArgumentException){n++;}
        var dir=Path.Combine(Path.GetTempPath(),"comet-p05-"+Guid.NewGuid().ToString("N"));
        try
        {
            var store=new ConfigStore(Path.Combine(dir,"config.json"));var saved=store.Read();
            saved.CloseDetection.StartDistance=5;saved.CloseDetection.FullDistance=2;saved.CloseDetection.ReductionPercent=75;saved.CloseDetection.Enabled=false;
            store.Save(saved,saved.Revision);var loaded=store.Read();Check(loaded.CloseDetection.StartDistance==5&&!loaded.CloseDetection.Enabled,"server persistence");
            var game=JsonSerializer.Deserialize<CometConfig>(store.ForGame())!;Check(game.CloseDetection.FullDistance==2&&game.CloseDetection.ReductionPercent==75,"client receives authoritative values");
        }
        finally { if(Directory.Exists(dir))Directory.Delete(dir,true); }
        CloseDetectionFrame Frame(double distance=3)=>new(){MasterEnabled=true,SupportedAi=true,Active=true,Authority=true,ValidTarget=true,InFov=true,ClearRecentRay=true,Distance=distance,ThirdParty=1.5,Unknown=1.5,NativeModifier=9};
        foreach(var (d,attention) in new[]{(0d,1d),(2.99,1),(3,1),(4.5,1.625),(5.999,2.2495833333333333),(6,2.25),(9,2.25)})
        {var t=CloseDetection.Evaluate(c,Frame(d));Near(t.FinalModifier,4*attention,"distance ramp "+d);Check(!t.VisualAcquired,"correction is not acquisition");}
        foreach(var edit in new Action<CloseDetectionFrame>[] {f=>f.MasterEnabled=false,f=>f.SupportedAi=false,f=>f.Active=false,f=>f.Authority=false,f=>f.Ghost=true,f=>f.ValidTarget=false,f=>f.Blind=true,f=>f.InFov=false,f=>f.ClearRecentRay=false})
        {var f=Frame();edit(f);var t=CloseDetection.Evaluate(c,f);Near(t.FinalModifier,9,"excluded or obstructed target retains native");Check(t.State=="native","refusal never claims action");}
        foreach(var percent in new[]{0d,25,50,75,100})
        {var t=CloseDetection.Evaluate(new(){ReductionPercent=percent},Frame(4.5));Near(t.FinalModifier,9-(5*.5*percent/100),"strength affects only extra penalty");}
        Near(CloseDetection.Evaluate(new(){Enabled=false},Frame()).FinalModifier,9,"disabled leaves native");
        // Vary native lighting/gear/pose/peripheral/repeat bonuses independently of attention.
        foreach(double other in new[]{.01,.2,.66,1,5,30})foreach(double third in new[]{.5,1,1.5,2})foreach(double unknown in new[]{.7,1,1.5})foreach(double d in new[]{0d,3,4.5,6,30})
        {
            var f=Frame(d);f.ThirdParty=third;f.Unknown=unknown;f.NativeModifier=other*third*unknown;
            var t=CloseDetection.Evaluate(c,f);
            Check(t.FinalModifier>0&&t.FinalModifier<=f.NativeModifier,"never slows native gain or removes native bonuses");
            if(d<=3)Near(t.FinalModifier,other*Math.Min(1,third)*Math.Min(1,unknown),"all other preset effects and attention bonuses preserved");
            if(d>=6)Near(t.FinalModifier,f.NativeModifier,"native beyond boundary");
        }
        foreach(double bad in new[]{double.NaN,double.PositiveInfinity,-1,0})
        {var f=Frame();f.ThirdParty=bad;Near(CloseDetection.Evaluate(c,f).FinalModifier,9,"invalid component falls back");}
        Check(CloseDetection.FreshRay(10,9.75,9.75,false,true),"native cache inclusive limit");
        Check(!CloseDetection.FreshRay(10,9.749,9.749,false,true),"stale ray rejected");
        Check(!CloseDetection.FreshRay(10,9.8,9.9,false,true),"new failed obstruction check overrides old success");
        Check(!CloseDetection.FreshRay(10,10,10,true,true),"same-frame new obstruction rejected");
        Check(!CloseDetection.FreshRay(.1,0,0,false,false),"uninitialized native cache cannot see");
        Check(!CloseDetection.FreshRay(10,10.01,10.01,false,true),"future evidence rejected");
        Check(!CloseDetection.FreshRay(10,double.NaN,10,false,true),"invalid evidence rejected");
        var memory=new CloseDetectionEvidence();
        memory.Observe("a",10,CloseDetection.Evaluate(c,Frame()));Check(memory.Acquired("b",10.1)==null,"another target cannot use correction");
        var acquired=memory.Acquired("a",10.2)!;Check(acquired.VisualAcquired&&acquired.State=="acquired"&&memory.Acquired("a",10.3)==null,"only observed transition consumes matching evidence");
        Near(acquired.CorrectionAge!.Value,.2,"correlation age");
        foreach(double at in new[]{9d,11.01}) {memory.Observe("a",10,CloseDetection.Evaluate(c,Frame()));Check(memory.Acquired("a",at)==null,"expired or reversed correlation rejected");}
        memory.Observe("a",10,CloseDetection.Evaluate(c,Frame()));var blocked=Frame();blocked.ClearRecentRay=false;memory.Observe("a",10.1,CloseDetection.Evaluate(c,blocked));Check(memory.Acquired("a",10.2)==null,"new refused check invalidates old correction");
        for(int i=0;i<30;i++){memory.Observe(i.ToString(),10,CloseDetection.Evaluate(c,Frame()));Check(memory.Count<=8,"bounded targets without game references");}
        memory.Clear();Check(memory.Count==0,"raid cleanup");
        using(var doc=JsonDocument.Parse(Newtonsoft.Json.JsonConvert.SerializeObject(new{feature="P05",stage="evaluated",closeDetection=acquired},Comet.Client.Diagnostics.Json)))
        {var t=doc.RootElement.GetProperty("closeDetection");Check(t.GetProperty("visualAcquired").GetBoolean()&&t.GetProperty("state").GetString()=="acquired","wire distinguishes native visual acquisition");}
        using(var pe=new PEReader(File.OpenRead("references/sain-4.5.1/SAIN.dll")))
        {
            var md=pe.GetMetadataReader();
            MethodDefinition Method(string type,string method)=>md.TypeDefinitions.Select(md.GetTypeDefinition).Single(t=>md.GetString(t.Name)==type).GetMethods().Select(md.GetMethodDefinition).Single(m=>md.GetString(m.Name)==method);
            List<(OpCode op,object? operand)> Il(string type,string method)=>FloorHearingTests.ReadIl(pe.GetMethodBody(Method(type,method).RelativeVirtualAddress).GetILBytes()!).ToList();
            List<string> Calls(string type,string method)=>Il(type,method).Where(x=>x.op==OpCodes.Call||x.op==OpCodes.Callvirt).Select(x=>FloorHearingTests.Name(md,(int)x.operand!)).ToList();
            var il=Il("VisionSpeedPatch","PatchPostfix");var calls=Calls("VisionSpeedPatch","PatchPostfix");
            Check(calls.Count(x=>x=="GetGainSightModifier")==1&&il.Count(x=>x.op==OpCodes.Div)==1,"exact native call-site contract");
            Check(calls.IndexOf("GetGainSightModifier")<calls.IndexOf("set_LastGainSightResult"),"native stores corrected result after calculation");
            Check(Calls("EnemyGainSightClass","GetGainSightModifier").SequenceEqual(new[]{"CalcModifier","get_KnownPlaces","CalcRepeatSeenCoef"}),"repeat-seen/heard multiplication remains native");
            foreach(string helper in new[]{"CalcThirdPartyMod","CalcUnknownMod"})
            {var hc=Calls("EnemyGainSightClass",helper);Check(!hc.Any(x=>x.StartsWith("set_")||x.Contains("Random")||x.Contains("Raycast")),"native attention helper is a pure read: "+helper);}
            Check(Method("EnemyVisionClass","UpdateVisibleState").GetParameters().Select(md.GetParameter).Count(x=>x.SequenceNumber>0)==2,"actual native visibility transition hook");
            Check((Method("EnemyBase","get_Enemy").Attributes&MethodAttributes.Static)==0,"protected owner getter is an instance accessor");
            var ray=md.TypeDefinitions.Select(md.GetTypeDefinition).Single(t=>md.GetString(t.Name)=="EnemyPartDataClass");
            Check(ray.GetFields().Select(md.GetFieldDefinition).Any(f=>md.GetString(f.Name)=="_raycastResults"),"native ray cache field exists");
            var rayCalls=Calls("EnemyPartDataClass","Update");Check(rayCalls.Contains("get_TimeLastSuccess"),"native part visibility uses success freshness");
        }
        Console.WriteLine($"P05: {n} checks passed (range ramp, preset preservation, scope, native visibility cache, acquisition correlation, persistence and official IL).");return n;
    }
}
