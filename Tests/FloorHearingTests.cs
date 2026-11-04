using Comet.Core;
using Comet.Server;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text.Json;

static class FloorHearingTests
{
    public static int Run()
    {
        int n=0;
        void Check(bool ok,string why) { if(!ok) throw new Exception("P04: "+why);n++; }
        void Near(double a,double b,string why) => Check(Math.Abs(a-b)<1e-6,why);
        var config=new CometConfig(); config.Validate(); var c=config.FloorHearing;
        Check(c.Enabled&&c.OneFloorOther==.6&&c.SeveralFloorsOther==.4&&c.OneFloorGunfire==1&&c.SeveralFloorsGunfire==.9&&c.MinimumHeight==2.5,"approved defaults");
        var old=JsonSerializer.Deserialize<CometConfig>("{\"Revision\":8,\"Enabled\":false}")!;old.Validate();
        Check(old.Revision==8&&!old.Enabled&&old.FloorHearing.OneFloorOther==.6,"old configuration gains independent defaults");
        foreach(var edit in new Action<FloorHearingConfig>[] {x=>x.OneFloorOther=double.NaN,x=>x.OneFloorGunfire=1.01,x=>x.SeveralFloorsGunfire=0,x=>x.SeveralFloorsOther=.7,x=>x.MinimumHeight=1,x=>{x.OneFloorGunfire=.5;x.SeveralFloorsGunfire=.6;}})
        {var bad=new FloorHearingConfig();edit(bad);try{bad.Validate();throw new Exception("Invalid P04 config accepted");}catch(ArgumentException){n++;}}
        try{new CometConfig{FloorHearing=null!}.Validate();throw new Exception("Null config accepted");}catch(ArgumentException){n++;}
        var dir=Path.Combine(Path.GetTempPath(),"comet-p04-"+Guid.NewGuid().ToString("N"));
        try
        {
            var store=new ConfigStore(Path.Combine(dir,"config.json"));var saved=store.Read();saved.FloorHearing.OneFloorOther=.7;saved.FloorHearing.Enabled=false;
            store.Save(saved,saved.Revision);var loaded=store.Read();Check(loaded.FloorHearing.OneFloorOther==.7&&!loaded.FloorHearing.Enabled,"real server persistence");
            var forGame=JsonSerializer.Deserialize<CometConfig>(store.ForGame())!;Check(forGame.FloorHearing.OneFloorOther==.7,"game receives server config");
        }
        finally { if(Directory.Exists(dir))Directory.Delete(dir,true); }
        FloorHearingFrame Frame(int floors=1,bool gun=false)=>new(){Distance=20,NativeRange=40,Geometry=new(floors,"confirmed"),Gunfire=gun};
        Near(FloorHearing.Evaluate(c,Frame()).FinalRange,24,"one floor movement");
        Near(FloorHearing.Evaluate(c,Frame(2)).FinalRange,16,"several floors movement");
        Near(FloorHearing.Evaluate(c,Frame(1,true)).FinalRange,40,"one floor gunfire unchanged");
        Near(FloorHearing.Evaluate(c,Frame(2,true)).FinalRange,36,"several floors gunfire");
        foreach(double distance in new[]{23.99,24,24.01}){var f=Frame();f.Distance=distance;Check(FloorHearing.Evaluate(c,f).Allowed==(distance<=24),"range boundary "+distance);}
        foreach(var edit in new Action<FloorHearingFrame>[]{f=>f.Priority=true,f=>f.Supported=false,f=>f.Scoped=false,f=>f.Geometry=new(0,"open"),f=>f.Geometry=new(3,"invalid"),f=>f.Environment=double.NaN,f=>f.Occlusion=0})
        {var f=Frame();edit(f);Near(FloorHearing.Evaluate(c,f).FinalRange,40,"priority/unsupported/uncertain/scope pass-through");}
        Near(FloorHearing.Evaluate(new(){Enabled=false},Frame()).FinalRange,40,"feature off");
        {var f=Frame();f.NativeAudible=false;Check(!FloorHearing.Evaluate(c,f).Allowed,"cannot undo native random rejection");f.NativeAudible=true;f.Distance=41;Check(!FloorHearing.Evaluate(c,f).Allowed,"cannot extend native range");}
        {var f=Frame();f.NativeRange=32;f.Occlusion=.8;Near(FloorHearing.Evaluate(c,f).FinalRange,24,"credit native obstruction: 32 to 24, not 19.2");
         f.NativeRange=20;f.Occlusion=.5;Near(FloorHearing.Evaluate(c,f).FinalRange,20,"stronger native penalty kept");
         f.NativeRange=12;f.Environment=.6;Near(FloorHearing.Evaluate(c,f).FinalRange,12,"bunker and occlusion already stronger");}
        // Range can only decrease; native equipment/clamp results are preserved even with multiple native modifiers.
        foreach(double env in new[]{.1,.3,.7,1})foreach(double occ in new[]{.1,.5,.9,1})foreach(int floors in new[]{1,2})foreach(bool gun in new[]{false,true})
        {var f=Frame(floors,gun);f.NativeRange=75*env*occ;f.Distance=f.NativeRange/2;f.Environment=env;f.Occlusion=occ;var t=FloorHearing.Evaluate(c,f);Check(t.FinalRange<=f.NativeRange&&t.FinalRange>0,"monotonic range");Check(t.Allowed==(f.Distance<=t.FinalRange),"decision and reported range agree");}
        var probe=new FloorGeometryProbe();var below=new AcousticPoint(0,1.6,0);var above=new AcousticPoint(6,3,0);
        var one=new Planes(3);var g=probe.Probe(one,below,above);Check(g.Floors==1&&one.Calls<=7,"single closed slab confirmed");
        Check(probe.Probe(new Planes(3),new(6,4.6,0),new(0,0,0)).Floors==1,"sound travelling down uses emission height");
        var several=new Planes(3,6);Check(probe.Probe(several,below,new(6,6,0)).Floors==2,"two physical floors, not height division");
        Check(probe.Probe(new Planes(3),below,new(6,9,0)).Floors==1,"large height alone does not imply two floors");
        Check(probe.Probe(new Planes(),below,above).Reason=="floor-open-direct","open stairwell/atrium retains native");
        Check(probe.Probe(new Planes(3){Normal=.5},below,above).Floors==0,"sloped surface not a floor");
        Check(probe.Probe(new Planes(3){Structural=false},below,above).Floors==0,"terrain or moving prop ignored");
        Check(probe.Probe(new Planes(3){WallFirst=true},below,above).Reason=="floor-obstacle-uncertain","wall alone is not floor evidence");
        Check(probe.Probe(new Planes(3){MaxX=5.5},below,above).Reason=="floor-columns-disagree","mezzanine edge/partial platform not confirmed");
        Check(probe.Probe(new Planes(3){StairSlope=.1},below,above).Floors==0,"stair treads at different heights do not match");
        Check(probe.Probe(new Planes(3){MaxZ=.4},below,above).Reason=="floor-open-nearby-route","nearby open route retains native");
        Check(probe.Probe(new Planes(3){MaxX=.2},new(0,1.6,0),new(0,3,0)).Floors==0,"aligned small prop not a full slab");
        foreach(int cutoff in Enumerable.Range(0,7))
        {var world=new Planes(3){Limit=cutoff};var r=probe.Probe(world,below,above);Check(r.Floors==0||world.Calls<=cutoff,"partial probe never confirms a floor");}
        Check(probe.Probe(new Saturated(),below,above).Floors==0,"full ray buffer is uncertain");
        Check(probe.Probe(new Planes(3),new(double.NaN,1,0),above).Floors==0,"invalid positions never query geometry");
        var cache=new FloorGeometryCache();cache.Put("pair",10,below,above,g);
        Check(cache.TryGet("pair",10.249,below,above,out _)&&!cache.TryGet("pair",10.25,below,above,out _),"short exact TTL");
        Check(!cache.TryGet("pair",9,below,above,out _)&&!cache.TryGet("pair",10.1,new(1,1.6,0),above,out _),"clock/movement invalidate cache");
        cache.Put("incomplete",10,below,above,new(0,"floor-probe-incomplete"));Check(!cache.TryGet("incomplete",10,below,above,out _),"budget fallback is never cached");
        for(int i=0;i<200;i++)cache.Put(i.ToString(),10,below,above,g);
        Check(cache.Count==128&&!cache.TryGet("pair",10,below,above,out _),"cache capacity and FIFO eviction");cache.Clear();Check(cache.Count==0,"raid cleanup");
        var budget=new AcousticWorkBudget();budget.Begin(1);for(int i=0;i<32;i++)Check(budget.TryCast(0),"frame cast allowance");
        Check(!budget.TryCast(0),"global 32-ray cap");budget.Begin(1);Check(!budget.TryCast(0),"second bot cannot reset cap");
        budget.Begin(2);Check(budget.TryCast(0),"next frame renews");budget.Charge(.3);Check(!budget.TryCast(.2)&&budget.TryCast(.19),"shared soft time limit");
        budget.Clear();budget.Begin(2);Check(budget.TryCast(0),"new raid clears budget");
        // Contract of the actual official bytecode used by the production transpiler: final range at local 4,
        // native env/occlusion at 0/2, native detection BEFORE the injection, final comparison AFTER it.
        using(var pe=new PEReader(File.OpenRead("references/sain-4.5.1/SAIN.dll")))
        {
            var md=pe.GetMetadataReader();var type=md.TypeDefinitions.Select(md.GetTypeDefinition).Single(t=>md.GetString(t.Name)=="HearingAnalysis");
            var method=type.GetMethods().Select(md.GetMethodDefinition).Single(m=>md.GetString(m.Name)=="CheckIfSoundHeard");
            var il=ReadIl(pe.GetMethodBody(method.RelativeVirtualAddress).GetILBytes()!).ToList();
            int Call(string name)=>il.FindIndex(x=>(x.op==OpCodes.Call||x.op==OpCodes.Callvirt)&&Name(md,(int)x.operand!)==name);
            Check(il[Call("CalcEnvironmentMod")+1].op==OpCodes.Stloc_0&&il[Call("CalcOcclusionMod")+1].op==OpCodes.Stloc_2,"native modifier local contract");
            int range=il.FindIndex(x=>x.op==OpCodes.Stloc_S&&(int)x.operand! ==4);
            Check(range>Call("DoIDetectFootsteps")&&range>Call("ShallLimitAI")&&il[range-1].op==OpCodes.Mul,"native eligibility/random roll precedes injection");
            Check(il[range+1].op==OpCodes.Ldarg_1&&il[range+2].op==OpCodes.Ldfld&&Name(md,(int)il[range+2].operand!)=="PlayerDistance","injection precedes native range check");
            Check(il.Count(x=>x.op==OpCodes.Stloc_S&&(int)x.operand! ==4)==1,"unique injection point");
            var sensor=md.TypeDefinitions.Select(md.GetTypeDefinition).Single(t=>md.GetString(t.Name)=="HearingSensor");
            var react=sensor.GetMethods().Select(md.GetMethodDefinition).Single(m=>md.GetString(m.Name)=="ReactToHeardSound");
            var calls=ReadIl(pe.GetMethodBody(react.RelativeVirtualAddress).GetILBytes()!).Where(x=>x.op==OpCodes.Call||x.op==OpCodes.Callvirt).Select(x=>Name(md,(int)x.operand!)).ToList();
            Check(calls.IndexOf("CheckIfSoundHeard")<calls.IndexOf("ShallChaseGunshot"),"P04 audibility precedes P03 reaction");
        }
        var trace=FloorHearing.Evaluate(c,Frame(2));
        using(var doc=JsonDocument.Parse(Newtonsoft.Json.JsonConvert.SerializeObject(new{feature="P04",stage="evaluated",floorHearing=trace},Comet.Client.Diagnostics.Json)))
        {var t=doc.RootElement.GetProperty("floorHearing");Check(t.GetProperty("state").GetString()=="blocked"&&!t.GetProperty("allowed").GetBoolean()&&t.GetProperty("nativeRange").GetDouble()==40,"telemetry records negative perception, not action ownership");}
        Console.WriteLine($"P04: {n} checks passed (geometry, audibility, native penalty credit, cache/budget, config and exact native IL).");return n;
    }
    private sealed class Saturated:IFloorAcoustics { public int Cast(AcousticPoint a,AcousticPoint b,AcousticHit[] hits)=>hits.Length; }
    // Analytic horizontal planes test the SAME production geometry classifier with actual intersections,
    // including gaps and differing tread elevations. No Unity/game assets required.
    private sealed class Planes(params double[] levels):IFloorAcoustics
    {
        public int Calls,Limit=int.MaxValue;public double Normal=1,MaxX=double.PositiveInfinity,MaxZ=double.PositiveInfinity,StairSlope;
        public bool Structural=true,WallFirst;
        public int Cast(AcousticPoint a,AcousticPoint b,AcousticHit[] hits)
        {
            if(++Calls>Limit)return -1;int n=0;
            foreach(double level in levels)
            {
                double denominator=b.Y-a.Y-StairSlope*(b.X-a.X);if(Math.Abs(denominator)<1e-8)continue;
                double t=(level+StairSlope*a.X-a.Y)/denominator;
                if(t<0||t>1)continue;double x=a.X+t*(b.X-a.X),z=a.Z+t*(b.Z-a.Z);if(Math.Abs(x)>MaxX||Math.Abs(z)>MaxZ)continue;
                hits[n++]=new(a.Y+t*(b.Y-a.Y),Normal,t*Math.Sqrt(a.DistanceSquared(b)),Structural);
            }
            if(WallFirst&&Calls==1)hits[n++]=new(2,0,.01,true);
            return n;
        }
    }
    internal static string Name(MetadataReader md,int token)
    {
        var h=MetadataTokens.Handle(token);
        return h.Kind switch {HandleKind.MethodDefinition=>md.GetString(md.GetMethodDefinition((MethodDefinitionHandle)h).Name),HandleKind.MemberReference=>md.GetString(md.GetMemberReference((MemberReferenceHandle)h).Name),HandleKind.FieldDefinition=>md.GetString(md.GetFieldDefinition((FieldDefinitionHandle)h).Name),_=>""};
    }
    internal static IEnumerable<(OpCode op,object? operand)> ReadIl(byte[] bytes)
    {
        var ops=typeof(OpCodes).GetFields(BindingFlags.Public|BindingFlags.Static).Where(f=>f.FieldType==typeof(OpCode)).Select(f=>(OpCode)f.GetValue(null)!).ToDictionary(o=>unchecked((ushort)o.Value));
        for(int i=0;i<bytes.Length;)
        {
            ushort token=bytes[i++];if(token==0xfe)token=(ushort)(0xfe00|bytes[i++]);var op=ops[token];object? value=null;
            int size=op.OperandType switch {OperandType.InlineNone=>0,OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar=>1,OperandType.InlineVar=>2,OperandType.InlineI8 or OperandType.InlineR=>8,OperandType.InlineSwitch=>4+4*BitConverter.ToInt32(bytes,i),_=>4};
            if(size==1)value=(int)bytes[i];else if(size==4)value=BitConverter.ToInt32(bytes,i);
            i+=size;yield return(op,value);
        }
    }
}
