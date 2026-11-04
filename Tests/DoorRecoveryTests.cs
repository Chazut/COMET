using Comet.Core;
using Comet.Server;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;

internal static class DoorRecoveryTests
{
    internal static int Run()
    {
        int n=0;void Check(bool value,string reason){n++;if(!value)throw new Exception("C02: "+reason);}
        var c=new DoorRecoveryConfig();c.Validate();Check(c.Enabled&&c.SuspicionDelay==1.2&&c.StableDuration==.5&&c.SampleInterval==.25&&c.UncertaintyLimit==5,"defaults");
        foreach(var edit in new Action<DoorRecoveryConfig>[] {v=>v.SuspicionDelay=double.NaN,v=>v.StableDuration=.1,v=>v.SampleInterval=1,v=>v.UncertaintyLimit=double.PositiveInfinity,v=>{v.SuspicionDelay=5;v.UncertaintyLimit=5;},v=>{v.StableDuration=.25;v.SampleInterval=.5;}})
        {var bad=new DoorRecoveryConfig();edit(bad);bool rejected=false;try{bad.Validate();}catch(ArgumentException){rejected=true;}Check(rejected,"invalid config rejected");}
        var migrated=Newtonsoft.Json.JsonConvert.DeserializeObject<CometConfig>("{\"doorRecovery\":null}")!;migrated.Validate();Check(migrated.DoorRecovery.Enabled,"old config migration");
        string file=Path.Combine(Path.GetTempPath(),"comet-c02-"+Guid.NewGuid().ToString("N"));
        try{var store=new ConfigStore(file);var v=store.Read();v.DoorRecovery.SuspicionDelay=2;store.Save(v,v.Revision);Check(store.Read().DoorRecovery.SuspicionDelay==2,"server persistence");}
        finally{File.Delete(file);File.Delete(file+".bak");}
        var policy=new DoorRecovery();
        void Begin()=>policy.Begin("door","Open",new(1,2,3),0,"solo");
        string Step(double t,double angle=90,bool active=false,bool busy=false,bool complete=true,bool coherent=false,bool network=true,string state="Interacting")
            =>policy.Observe(t,angle,90,state,active,busy,complete,coherent,network,c);
        Begin();for(int i=0;i<5;i++)Check(Step(i*.25)=="","wait before suspicion");Check(Step(1.25)=="finalize-state","known finished endpoint");
        policy.Requested("finalize-state");Check(!policy.Trace.Confirmed,"request separate from outcome");Step(1.75,coherent:true,state:"Open");Check(policy.Trace.Confirmed&&policy.Trace.ValidFor==0,"observed confirmation");
        Begin();Step(0);Step(.25);Step(1.5);Check(Step(1.75)=="","gap resets stability");Check(Step(2)=="finalize-state","continuous fresh samples");
        Begin();Step(0);Step(.25,angle:70);Step(.5,angle:90);Check(policy.Trace.Stable==0,"movement restarts stability");
        Begin();for(int i=0;i<5;i++)Step(i*.25,angle:30,active:true,complete:false);Check(Step(1.25,angle:30,active:true,complete:false)=="finish-angle","frozen interpolation can be corrected without native completion");
        foreach(string reason in new[]{"busy","network"})
        {Begin();for(int i=0;i<=20;i++)Check(Step(i*.25,angle:reason=="endpoint"?0:90,busy:reason=="busy",complete:reason!="completion",network:reason!="network")=="","protected "+reason);Check(policy.Trace.State==(reason=="busy"?"waiting":"blocked"),"bounded uncertainty allows animation grace");}
        Begin();Step(0,coherent:true);Check(policy.Trace.State=="ended"&&!policy.Trace.Requested,"native repair respected");
        Begin();Step(0,state:"Locked");Check(policy.Trace.State=="ended","protected state");
        Begin();Step(1);Step(.5);Check(policy.Trace.State=="ended","clock rewind");
        Begin();Step(double.NaN);Check(policy.Trace.State=="ended","nonfinite observation");
        Begin();Step(0);Step(.5);Step(1);Step(1.5);policy.Requested("finalize-state");Step(1.75);Check(policy.Trace.State=="blocked"&&!policy.Trace.Confirmed,"failed correction stops retries");
        string game="C:/Games/SPT-4.1/EscapeFromTarkov_Data/Managed/Assembly-CSharp.dll";
        using(var pe=new PEReader(File.OpenRead(game)))
        {
            var md=pe.GetMetadataReader();TypeDefinition Type(string name)=>md.TypeDefinitions.Select(md.GetTypeDefinition).Single(t=>md.GetString(t.Name)==name);
            var door=Type("WorldInteractiveObject");
            foreach(string field in new[]{"_interaction","_previousInteraction"})Check(door.GetFields().Select(md.GetFieldDefinition).Any(f=>md.GetString(f.Name)==field),"native "+field);
            foreach(string method in new[]{"LockForInteraction","InitializeSmoothOpenInteraction","set_DoorState","get_CurrentAngle"})Check(door.GetMethods().Select(md.GetMethodDefinition).Any(m=>md.GetString(m.Name)==method),"native "+method);
            var state=Type("InteractionState");foreach(string method in new[]{"get_IsInProgress","get_TimeIsOver","set_ResultState","set_Break","SetEndData"})Check(state.GetMethods().Select(md.GetMethodDefinition).Any(m=>md.GetString(m.Name)==method),"native record "+method);
        }
        string fika="C:/Users/Jagut/Documents/Codex/2026-09-19/tu-reprends-le-travail-sur-orbit-2/work/fika-setup-20261014/Fika-Plugin/BepInEx/plugins/Fika/Fika.Core.dll";
        if(File.Exists(fika))using(var pe=new PEReader(File.OpenRead(fika)))
        {
            var md=pe.GetMetadataReader();var type=md.TypeDefinitions.Select(md.GetTypeDefinition).Single(t=>md.GetString(t.Name)=="FikaPlayer");
            var method=type.GetMethods().Select(md.GetMethodDefinition).Single(m=>md.GetString(m.Name)=="ExecuteInteraction");
            Check(Convert.ToHexString(SHA256.HashData(pe.GetMethodBody(method.RelativeVirtualAddress).GetILBytes()!))=="4A113304AF7AEFC536B8CA345036FFAE4EA348450F926C57E60DC45411A828C6","verified Fika native sender fingerprint");
        }
        Console.WriteLine($"C02 policy/native contracts: {n} checks passed.");return n;
    }
}
