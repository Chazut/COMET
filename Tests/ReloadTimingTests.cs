using Comet.Core;
using System.Text.Json;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

static class ReloadTimingTests
{
    public static int Run()
    {
        int n=0;void Check(bool value,string why){n++;if(!value)throw new Exception("W01: "+why);}
        void Throws(Action action,string why){try{action();}catch(ArgumentException){n++;return;}throw new Exception("W01: "+why);}
        ReloadTimingFrame F(double now=10)=>new(){Now=now,Weapon="rifle",WeaponClass="assaultRifle",Target="contact",Source="personal-vision",
            Loaded=8,Magazine=7,Chamber=1,Capacity=31,Reserve=30,Resource="magazine",ResourceKey="spare:30",Protected=true,Distance=20,SightAge=5,ContactAge=5};
        var c=new ReloadTimingConfig();c.Validate();
        Check(c.Enabled&&c.ConsiderBelow==80&&c.CriticalBelow==20&&c.CloseDistance==25&&c.LostContactDelay==2&&c.Commitment==.6&&c.RetryDelay==2,"approved defaults");
        foreach(var prop in typeof(ReloadTimingConfig).GetProperties().Where(p=>p.PropertyType==typeof(double)))foreach(double invalid in new[]{double.NaN,double.PositiveInfinity,-1,1000}){
            var bad=new ReloadTimingConfig();prop.SetValue(bad,invalid);Throws(bad.Validate,"invalid setting "+prop.Name);}
        Throws(()=>new ReloadTimingConfig{ConsiderBelow=30,CriticalBelow=35}.Validate(),"inverted thresholds");
        Throws(()=>new CometConfig{ReloadTiming=null!}.Validate(),"null config");
        Check(JsonSerializer.Deserialize<CometConfig>("{}")!.ReloadTiming.RetryDelay==2,"older config migrates with defaults");
        ReloadTimingPolicy p=new();var wire=new List<JsonElement>();
        void Save()=>wire.Add(JsonSerializer.Deserialize<JsonElement>(Newtonsoft.Json.JsonConvert.SerializeObject(p.Trace,Comet.Client.Diagnostics.Json)));
        Check(p.Evaluate(F(),c)=="Reload","low rifle behind verified cover reloads");Save();
        var f=F(10.1);p.Request(f);Save();p.Result(true,f,c);Save();
        Check(p.Active&&!p.Trace!.Started&&!p.Trace.Finished,"acceptance is not animation or completion");
        p.Observe(10.2,true,true,8,true);Save();Check(p.Trace!.Started&&p.Trace.State=="reloading","native animation observed");
        p.Observe(10.5,false,true,8,true);Check(p.Active&&!p.Trace.Finished,"native operation gaps are not completion");
        p.Observe(11,true,true,20,true);Check(p.Active&&!p.Trace.Finished,"incremental internal load does not finish operation");
        p.Observe(12,false,false,31,true);Save();Check(!p.Active&&p.Trace.Finished&&p.Trace.Improved&&p.Trace.ValidFor==0,"observed native end plus gain");
        p=new();f=F();p.Evaluate(f,c);p.Request(f);p.Result(false,f,c);Save();
        var retry=new ReloadTimingPolicy();var retryFrame=F();retry.Evaluate(retryFrame,c);retry.Request(retryFrame);retry.Result(true,retryFrame,c);
        retry.Observe(13,false,true,8,true);retryFrame.Now=13;retry.CompleteObservation(retryFrame,c);
        Check(retry.RetryBlocked(F(14.9))&&!retry.RetryBlocked(F(15)),"first unconfirmed reload delay");
        retryFrame=F(15);retry.Evaluate(retryFrame,c);retry.Request(retryFrame);retry.Result(true,retryFrame,c);retry.Observe(18,false,true,8,true);retryFrame.Now=18;retry.CompleteObservation(retryFrame,c);
        Check(retry.Trace!.RepeatedFailures==2&&retry.RetryBlocked(F(21.9))&&!retry.RetryBlocked(F(22)),"same failed request backs off");
        var fired=F(19);fired.Loaded=7;fired.Magazine=6;Check(!retry.RetryBlocked(fired),"changed ammunition invalidates failed request backoff");
        retryFrame=F(22);retry.Evaluate(retryFrame,c);retry.Request(retryFrame);retry.Result(true,retryFrame,c);retry.Observe(22.1,true,true,8,true);retry.Observe(24,false,false,31,true);retryFrame.Now=24;retry.CompleteObservation(retryFrame,c);
        Check(!retry.RetryBlocked(F(24)),"successful reload clears failed request cooldown");
        Check(!p.Active&&p.Evaluate(F(10.5),c)=="Native"&&p.Trace!.Reason=="reload-retry-delay","refusal throttles duplicate attempts");
        Check(p.Evaluate(F(12),c)=="Reload","retry after two seconds");
        p.Refuse(F(12),c);f=F(12.1);f.ResourceKey="new-mag:30";Check(p.Evaluate(f,c)=="Reload","new inventory resource reopens attempt");
        p.Refuse(f,c);f.Now=12.2;f.Loaded=f.Magazine=f.Chamber=0;Check(p.Evaluate(f,c)=="Reload","empty weapon overrides old refusal fingerprint");
        foreach(bool operation in new[]{false,true}){p=new();f=F();p.Evaluate(f,c);p.Request(f);p.Result(true,f,c);if(operation)p.Observe(10.1,true,true,8,true);
            p.Observe(14,false,false,8,true);Save();Check(!p.Active&&!p.Trace!.Improved&&p.Trace.Finished==operation,"unconfirmed/interrupted reload never claims ammo gain");}
        p=new();f=F();p.Evaluate(f,c);p.Request(f);p.Result(true,f,c);p.Observe(10.2,true,true,8,false);Save();
        Check(!p.Active&&!p.Trace!.Finished&&p.Trace.Reason=="reload-weapon-or-clock-changed","switch never claims previous gun finished");
        p=new();f=F();p.Evaluate(f,c);p.Request(f);p.Result(true,f,c);p.Observe(80,true,true,8,true);Save();
        Check(!p.Active&&p.Trace!.Reason=="reload-observation-limit"&&!p.Trace.Finished,"long operations only stop observation");
        p=new();f=F();f.Visible=f.Looking=true;f.Protected=false;f.Distance=8;f.Loaded=20;f.Magazine=19;
        Check(p.Evaluate(f,c)=="Hold","do not lower gun in immediate visible fight");Save();
        f.Now=10.2;f.Loaded=f.Chamber=f.Magazine=0;Check(p.Evaluate(f,c)=="Reload","empty overrides hold commitment");
        f=F();f.Loaded=20;f.Magazine=19;f.Visible=false;f.Protected=false;f.Distance=15;f.SightAge=.3;
        p=new();Check(p.Evaluate(f,c)=="Hold"&&p.Trace!.Reason=="reload-expect-repeek","losing sight does not imply safety");
        f=F();p=new();p.Evaluate(f,c);double safe=p.Trace!.ReloadScore;f.Threats=1;f.Protected=false;p=new();p.Evaluate(f,c);
        Check(p.Trace!.ReloadScore<safe,"other immediate threat removes safety benefit");
        f=F();f.Loaded=3;f.Magazine=2;f.Capacity=9;f.WeaponClass="shotgun";p=new();p.Evaluate(f,c);Check(!p.Trace!.Critical,"three shotgun shells are not rifle emergency");
        f.Capacity=31;f.WeaponClass="assaultRifle";p=new();p.Evaluate(f,c);Check(p.Trace!.Critical,"three rifle rounds have higher urgency");
        f=F();f.Visible=true;f.BusyObserved=false;p=new();p.Evaluate(f,c);double hold=p.Trace!.HoldScore;f.BusyObserved=true;p=new();p.Evaluate(f,c);
        Check(p.Trace!.HoldScore>hold,"personally heard action on visible enemy favors keeping gun up");
        f=F();f.Aggression=0;p=new();p.Evaluate(f,c);double cautious=p.Trace!.ReloadScore;f.Aggression=1;p=new();p.Evaluate(f,c);
        Check(p.Trace!.ReloadScore<cautious,"personality influences optional reload without changing aim");
        // Exhaustive useful invariants across chamber/capacity/resource combinations.
        foreach(string kind in new[]{"shotgun","assaultRifle","sniperRifle","machinegun"})for(int cap=1;cap<=100;cap++)for(int loaded=0;loaded<=cap;loaded++){
            f=F();f.WeaponClass=kind;f.Capacity=cap;f.Loaded=loaded;f.Chamber=loaded>0?1:0;f.Magazine=loaded-f.Chamber;
            p=new();string choice=p.Evaluate(f,c);Check(loaded!=0||choice=="Reload","never hold an empty usable gun");
            Check(loaded<cap*.8||choice=="Hold","above consideration retains weapon");
            f.Reserve=0;f.Resource="none";p=new();choice=p.Evaluate(f,c);Check(choice!="Reload","no improving resource cannot request reload");
        }
        foreach(var change in new Action<ReloadTimingFrame>[] {x=>x.Now=double.NaN,x=>x.Distance=-1,x=>x.Capacity=0,x=>x.Loaded=1000,
            x=>x.Magazine=-1,x=>x.Aggression=2,x=>x.Reserve=-1,x=>x.Weapon="",x=>x.ContactAge=double.PositiveInfinity}){
            f=F();change(f);p=new();Check(p.Evaluate(f,c)=="Native","invalid input safely yields");}
        p=new();f=F();p.Evaluate(f,c);p.Request(f);p.Result(true,f,c);p.Clear("ghost");Save();Check(!p.Active,"scope cleanup drops only tracking");
        using(var pe=new PEReader(File.OpenRead("references/sain-4.5.1/SAIN.dll"))){var md=pe.GetMetadataReader();var type=md.TypeDefinitions.Select(md.GetTypeDefinition).Single(t=>md.GetString(t.Name)=="SelfActionDecisionClass");
            Check(type.GetMethods().Select(md.GetMethodDefinition).Any(m=>md.GetString(m.Name)=="CheckDoReload"),"official SAIN reload hook exists");
            Check(type.GetFields().Select(md.GetFieldDefinition).Any(m=>md.GetString(m.Name)=="_lastReloadTime"),"native reload clock exists");}
        using(var pe=new PEReader(File.OpenRead("Client/bin/Release/netstandard2.1/COMET.dll"))){var md=pe.GetMetadataReader();var type=md.TypeDefinitions.Select(md.GetTypeDefinition).Single(t=>md.GetString(t.Name)=="Plugin");
            MethodDefinition Method(string name)=>type.GetMethods().Select(md.GetMethodDefinition).Single(m=>md.GetString(m.Name)==name);
            foreach(var gate in new[]{"IsPmcAi","get_Authority","Ghost","get_BotActive","get_IsDead","get_GameEnding","get_SAINLayersActive"})Check(Calls(pe,md,Method("ReloadScope"),gate)==1,"compiled scope "+gate);
            foreach(var method in new[]{"ReloadFrame","ChooseReloadTiming","SampleReloadTiming"})foreach(var forbidden in new[]{"get_RealDistance","get_EnemyPosition","get_VulnerableAction","BotCancelReload","AddAmmoToMagazines","FastForward","Teleport"})
                Check(Calls(pe,md,Method(method),forbidden)==0,"no hidden info, fake ammo or forced cancel: "+method+forbidden);
            Check(Calls(pe,md,Method("ChooseReloadTiming"),"CanReload")==1&&Calls(pe,md,Method("ChooseReloadTiming"),"ReloadMagazine")==1&&Calls(pe,md,Method("ChooseReloadTiming"),"ReloadAmmo")==1,"actual native reload API");
        }
        Directory.CreateDirectory("artifacts");File.WriteAllText("artifacts/w01-wire-traces.json",JsonSerializer.Serialize(wire));
        var active=wire.First(t=>t.GetProperty("state").GetString()=="reloading");
        File.WriteAllText("artifacts/w01-reload-timing.json",JsonSerializer.Serialize(new{botInstance="instance",reloadTiming=active,reloadTimingConfig=c}));
        Console.WriteLine($"W01: {n} assertions passed.");return n;
    }
    internal static int Calls(PEReader pe,MetadataReader md,MethodDefinition method,string name)
    {
        var ops=typeof(OpCodes).GetFields(BindingFlags.Public|BindingFlags.Static).Where(f=>f.FieldType==typeof(OpCode)).Select(f=>(OpCode)f.GetValue(null)!).ToDictionary(o=>unchecked((ushort)o.Value));
        var il=pe.GetMethodBody(method.RelativeVirtualAddress).GetILBytes()!;int count=0;
        for(int i=0;i<il.Length;){ushort v=il[i++];if(v==0xfe)v=(ushort)(0xfe00|il[i++]);var op=ops[v];
            if(op==OpCodes.Call||op==OpCodes.Callvirt){var h=MetadataTokens.EntityHandle(BitConverter.ToInt32(il,i));string called=h.Kind==HandleKind.MethodDefinition?md.GetString(md.GetMethodDefinition((MethodDefinitionHandle)h).Name):h.Kind==HandleKind.MemberReference?md.GetString(md.GetMemberReference((MemberReferenceHandle)h).Name):"";if(called==name)count++;}
            i+=op.OperandType switch{OperandType.InlineNone=>0,OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar=>1,OperandType.InlineVar=>2,OperandType.InlineI8 or OperandType.InlineR=>8,OperandType.InlineSwitch=>4+4*BitConverter.ToInt32(il,i),_=>4};}
        return count;
    }
}
