using Comet.Core;
using Comet.Server;
using System.Collections;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

internal static class CoverScanTests
{
    internal static int Run()
    {
        int n=0;void Check(bool v,string why){n++;if(!v)throw new Exception("C07 "+why);}
        var cfg=new CoverScanConfig();cfg.Validate();Check(cfg.Enabled&&cfg.MaxScanSeconds==10,"defaults");
        foreach(double value in new[]{4.99,30.01,double.NaN,double.PositiveInfinity})
        {bool failed=false;try{new CoverScanConfig{MaxScanSeconds=value}.Validate();}catch(ArgumentException){failed=true;}Check(failed,"invalid duration");}
        foreach(string json in new[]{"{}","{\"coverScan\":null}"}){var c=Newtonsoft.Json.JsonConvert.DeserializeObject<CometConfig>(json)!;c.Validate();Check(c.CoverScan.Enabled&&c.CoverScan.MaxScanSeconds==10,"migration");}
        string file=Path.Combine(Path.GetTempPath(),"comet-c07-"+Guid.NewGuid().ToString("N"));
        try{var store=new ConfigStore(file);var c=store.Read();c.CoverScan.Enabled=false;c.CoverScan.MaxScanSeconds=25;store.Save(c,c.Revision);c=new ConfigStore(file).Read();Check(!c.CoverScan.Enabled&&c.CoverScan.MaxScanSeconds==25,"server persistence");}finally{File.Delete(file);File.Delete(file+".bak");}
        var wait=new object();double now=0;int mode=1,created=0,steps=3;string enemy="enemy-a";
        var published=new List<int>{99};CoverScanRunner<int>? runner=null;bool redirected=false,throws=false,midSwitch=false;
        List<int> Access()=>redirected?runner!.Working:published;
        IEnumerator Native(){created++;while(true){Access().Clear();for(int i=0;i<steps;i++){if(throws)throw new Exception("native failure");Access().Add(i);if(midSwitch)enemy="enemy-c";yield return null;}yield return wait;}}
        var trace=new List<CoverScanTrace>();int errors=0;
        runner=new(Native(),published,Native,()=>mode,()=>enemy,()=>now,()=>10,it=>ReferenceEquals(it.Current,wait),(_,e)=>redirected=e,t=>trace.Add(t),_=>errors++,wait);
        runner.MoveNext();Check(published.SequenceEqual(new[]{99})&&runner.Working.Count==1&&!redirected,"partial result invisible outside native step");
        now=4;runner.MoveNext();Check(published.SequenceEqual(new[]{99}),"results do not expire at 3s");
        now=9;runner.MoveNext();Check(published.SequenceEqual(new[]{99}),"preserve until full completion");
        runner.MoveNext();Check(published.SequenceEqual(new[]{0,1,2})&&!runner.Scanning&&runner.Working.Count==0,"atomic publication");
        now=10;runner.MoveNext();now=20;runner.MoveNext();Check(trace[^1].State=="timeout"&&published.Count==3&&runner.Working.Count==0,"10s restarts only unfinished scan");
        now=21;runner.MoveNext();Check(created==2&&runner.Working.Count==1,"fresh native iterator after timeout");
        enemy="enemy-b";runner.CheckContext();Check(published.Count==0&&runner.Working.Count==0&&!runner.Retains("enemy-a"),"enemy invalidation even without coroutine progress");
        steps=0;runner.MoveNext();Check(published.Count==0&&trace[^1].Reason=="cover-scan-complete-empty","completed empty replaces last list");
        steps=2;runner.MoveNext();runner.MoveNext();runner.MoveNext();Check(published.Count==2,"new complete result");
        mode=2;runner.CheckContext();Check(published.Count==0&&trace[^1].ValidFor==0,"Ghost and inactive discard context");
        int before=created;runner.MoveNext();Check(created==before&&ReferenceEquals(runner.Current,wait),"no work while suspended");
        mode=1;runner.MoveNext();midSwitch=true;runner.MoveNext();Check(published.Count==0&&runner.Working.Count==0,"mid-step target replacement rejects mixed scan");midSwitch=false;
        runner.MoveNext();mode=0;runner.MoveNext();Check(!redirected&&published.Count==1,"disable or authority loss restores native path");
        mode=1;runner.MoveNext();throws=true;runner.MoveNext();Check(errors==1&&!redirected&&published.Count==0,"exception clears partial and always releases redirect");throws=false;
        runner.MoveNext();Check(published.Count==1,"failed custom adapter yields to native");runner.Dispose();runner.Dispose();Check(!runner.MoveNext(),"idempotent cleanup");
        // Async configuration arrival cannot adopt a partially populated vanilla list.
        mode=0;published.Clear();now=0;enemy="enemy-a";errors=0;steps=3;
        runner=new(Native(),published,Native,()=>mode,()=>enemy,()=>now,()=>10,it=>ReferenceEquals(it.Current,wait),(_,e)=>redirected=e,t=>trace.Add(t),_=>errors++,wait);
        runner.MoveNext();Check(published.Count==1,"vanilla scan has a partial result");mode=1;runner.MoveNext();Check(published.Count==0,"config arrival discards vanilla partial list");
        now=-1;runner.MoveNext();Check(!runner.Scanning&&runner.Working.Count==0,"clock rollback restarts unfinished scan");
        throws=true;runner.MoveNext();Check(errors==1,"first error reports once");Check(!runner.MoveNext()&&errors==1&&!redirected,"failed native fallback terminates without error spam");throws=false;
        // Exercise actual Release iterator IL, not only a source-text match: these are the
        // six getter calls the targeted transpiler redirects, and no other reader is patched.
        string sain=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../references/sain-4.5.1/SAIN.dll"));
        using var pe=new PEReader(File.OpenRead(sain));var md=pe.GetMetadataReader();
        var type=md.TypeDefinitions.Select(h=>md.GetTypeDefinition(h)).Single(t=>md.GetString(t.Name).StartsWith("<FindCoverLoop>"));
        var method=type.GetMethods().Select(h=>md.GetMethodDefinition(h)).Single(m=>md.GetString(m.Name)=="MoveNext");
        var il=pe.GetMethodBody(method.RelativeVirtualAddress).GetILBytes()!;
        var ops=typeof(OpCodes).GetFields(BindingFlags.Public|BindingFlags.Static).Where(f=>f.FieldType==typeof(OpCode)).Select(f=>(OpCode)f.GetValue(null)!).ToDictionary(o=>unchecked((ushort)o.Value));
        int calls=0,waits=0;
        for(int i=0;i<il.Length;){ushort op=il[i++];if(op==0xfe)op=(ushort)(0xfe00|il[i++]);var code=ops[op];
            if(code.OperandType==OperandType.InlineMethod){int token=BitConverter.ToInt32(il,i);var h=MetadataTokens.EntityHandle(token);string name=h.Kind==HandleKind.MethodDefinition?md.GetString(md.GetMethodDefinition((MethodDefinitionHandle)h).Name):h.Kind==HandleKind.MemberReference?md.GetString(md.GetMemberReference((MemberReferenceHandle)h).Name):"";if(name=="get_CoverPoints")calls++;if(name=="set_CurrentStatus")waits++;}
            i+=code.OperandType switch{OperandType.InlineNone=>0,OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar=>1,OperandType.InlineVar=>2,OperandType.InlineI8 or OperandType.InlineR=>8,OperandType.InlineSwitch=>4+4*BitConverter.ToInt32(il,i),_=>4};}
        Check(calls==6,"official SAIN iterator has exactly 6 redirected accesses; got "+calls);Check(waits>0,"native completion status contract");
        Console.WriteLine($"C07 core and native IL: {n} checks passed");return n;
    }
}
