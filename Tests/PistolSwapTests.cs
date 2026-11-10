using Comet.Core;
using System.Text.Json;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

static class PistolSwapTests
{
    public static int Run()
    {
        int n=0;void Check(bool v,string why){n++;if(!v)throw new Exception("W02: "+why);}
        void Throws(Action action,string why){try{action();}catch(ArgumentException){n++;return;}throw new Exception("W02: "+why);}
        ReloadTimingFrame F(double now=10)=>new(){Now=now,Weapon="rifle",WeaponClass="assaultRifle",Target="contact",Source="personal-vision",
            Loaded=1,Chamber=1,Capacity=31,Reserve=30,Resource="magazine",ResourceKey="spare:30",Visible=true,Looking=true,Incoming=true,Distance=8,ContactAge=0};
        PistolSwapFrame P()=>new(){Pistol="pistol",Loaded=10,Chamber=1,Magazine=9,Capacity=16,Usable=true,PrimaryInHands=true};
        ReloadTimingTrace Reload(ReloadTimingFrame f,bool enabled=true){var r=new ReloadTimingPolicy();r.Evaluate(f,new(){Enabled=enabled});return r.Trace!;}
        var c=new PistolSwapConfig();c.Validate();
        Check(c.Enabled&&c.MaxDistance==30&&c.LowAmmoPercent==10&&c.MaxMainRounds==3&&c.ContactMemory==1.5&&c.RetryDelay==3,"approved defaults");
        foreach(var prop in typeof(PistolSwapConfig).GetProperties().Where(p=>p.PropertyType==typeof(double)))foreach(double bad in new[]{double.NaN,double.PositiveInfinity,-1,1000}){
            var v=new PistolSwapConfig();prop.SetValue(v,bad);Throws(v.Validate,"invalid setting "+prop.Name);}
        foreach(int bad in new[]{-1,11})Throws(()=>new PistolSwapConfig{MaxMainRounds=bad}.Validate(),"round cap bounds");
        Throws(()=>new CometConfig{PistolSwap=null!}.Validate(),"null section");
        Check(JsonSerializer.Deserialize<CometConfig>("{}")!.PistolSwap.MaxDistance==30,"old config defaults");
        PistolSwapPolicy policy=new();var f=F();var pistol=P();var wire=new List<JsonElement>();
        void Save()=>wire.Add(JsonSerializer.Deserialize<JsonElement>(Newtonsoft.Json.JsonConvert.SerializeObject(policy.Trace,Comet.Client.Diagnostics.Json)));
        Check(policy.Evaluate(f,pistol,Reload(f),c),"exposed nearby threat prefers loaded pistol");Save();
        policy.Request(10);Save();Check(policy.Active&&policy.Trace!.Requested&&!policy.Trace.Accepted&&!policy.Trace.InHands,"request is not success");
        policy.Result(true,10,c);Save();Check(!policy.Trace!.Ready&&!policy.Trace.InHands,"native acceptance is not drawing complete");
        Check(!policy.Evaluate(F(10.1),P(),Reload(F(10.1)),c)&&policy.Trace!.Episode==1,"one pending request");
        policy.Observe(10.2,"",false,c);Check(policy.Active&&!policy.Trace!.InHands,"empty hands during animation remain pending");
        policy.Observe(10.3,"rifle",true,c);Check(policy.Active&&!policy.Trace!.Ready,"source weapon ready does not confirm sidearm");
        policy.Observe(10.8,"pistol",false,c);Save();Check(policy.Trace!.InHands&&!policy.Trace.Ready&&policy.Active,"actual sidearm drawing observed");
        Check(Math.Abs(policy.Trace.ContactAge-.8)<.0001,"personal information ages during the draw");
        policy.Observe(11,"pistol",true,c);Save();Check(policy.Trace!.Ready&&!policy.Active,"native sidearm readiness observed");
        Check(!policy.Evaluate(F(11.2),P(),Reload(F(11.2)),c)&&policy.Trace!.Reason=="pistol-retry-delay","cooldown after success");
        Check(policy.Evaluate(F(14),P(),Reload(F(14)),c),"cooldown expires without extending itself");
        policy=new();f=F();policy.Evaluate(f,P(),Reload(f),c);policy.Request(10);policy.Result(false,10,c);Save();
        Check(!policy.Active&&!policy.Trace!.Accepted&&policy.Trace.State=="ended","refused native call ends ownership");
        for(double now=10.1;now<13;now+=.2)Check(!policy.Evaluate(F(now),P(),Reload(F(now)),c)&&policy.RetryBlocked(now),"refusal throttled");
        Check(policy.Evaluate(F(13),P(),Reload(F(13)),c),"refusal reopens after exact delay");
        foreach(var mode in new[]{"timeout","other","back-to-primary","ghost","clock","nan"}){
            policy=new();f=F();policy.Evaluate(f,P(),Reload(f),c);policy.Request(10);policy.Result(true,10,c);
            if(mode=="back-to-primary"){policy.Observe(11,"pistol",false,c);policy.Observe(11.1,"rifle",true,c);}
            else if(mode=="other")policy.Observe(11,"other-weapon",true,c);
            else if(mode=="ghost")policy.Clear("pistol-ghost");
            else policy.Observe(mode=="clock"?9:mode=="nan"?double.NaN:15,"rifle",false,c);
            Save();Check(!policy.Active&&!policy.Trace!.Ready&&policy.Trace.ValidFor==0,"no invented ready on "+mode);
        }
        // Cover and resources are compared to the same W01 scores, including an empty primary.
        f=F();f.Loaded=f.Chamber=0;f.Protected=true;f.Visible=f.Looking=f.Incoming=false;f.Distance=20;f.ContactAge=.5;
        policy=new();Check(!policy.Evaluate(f,P(),Reload(f),c)&&policy.Trace!.Reason=="pistol-reload-preferred","reached cover favors reload");Save();
        f.Reserve=0;f.Resource="none";policy=new();Check(policy.Evaluate(f,P(),Reload(f),c),"empty primary without resource uses loaded pistol even in cover");
        pistol=P();pistol.Loaded=pistol.Chamber=1;pistol.Magazine=0;policy=new();Check(policy.Evaluate(f,pistol,Reload(f),c),"one chambered pistol round beats empty primary");
        f.Loaded=f.Chamber=1;policy=new();Check(!policy.Evaluate(f,pistol,Reload(f),c)&&policy.Trace!.Reason=="pistol-not-more-useful","equal remaining rounds do not justify swap");
        foreach(var edit in new Action<ReloadTimingFrame>[] {x=>x.Distance=30.01,x=>{x.Visible=false;x.ContactAge=1.51;},x=>x.Target="",x=>x.Block="reload-no-personal-contact"}){
            f=F();edit(f);policy=new();Check(!policy.Evaluate(f,P(),Reload(f),c),"no stale/far/unknown contact");}
        f=F();f.Visible=false;f.ContactAge=1.5;f.Distance=30;f.Reserve=0;f.Resource="none";policy=new();
        Check(policy.Evaluate(f,P(),Reload(f),c),"known contact exact boundary");
        foreach(var edit in new Action<PistolSwapFrame>[] {x=>x.PrimaryInHands=false,x=>x.Usable=false,x=>{x.Loaded=x.Magazine=x.Chamber=0;},x=>x.Pistol="",x=>x.Block="pistol-malfunction"}){
            pistol=P();edit(pistol);policy=new();Check(!policy.Evaluate(F(),pistol,Reload(F()),c),"unusable/non-primary pistol input yields");Save();}
        // Both limits must hold, for rifles, low-capacity guns and large magazines.
        foreach(int cap in new[]{5,10,30,31,60,100})for(int loaded=0;loaded<=cap;loaded++){
            f=F();f.Capacity=cap;f.Loaded=loaded;f.Chamber=loaded>0?1:0;f.Magazine=loaded-f.Chamber;f.Reserve=0;f.Resource="none";
            policy=new();bool expected=loaded<=3&&100.0*loaded/cap<=10;
            Check(policy.Evaluate(f,P(),Reload(f),c)==expected,"combined percent and absolute ammo limits");
        }
        f=F();policy=new();Check(policy.Evaluate(f,P(),Reload(f,false),c),"W02 works with W01 disabled");
        policy=new();Check(!policy.Evaluate(f,P(),Reload(f),new(){Enabled=false}),"W02 disabled");
        foreach(var edit in new Action<ReloadTimingFrame>[] {x=>x.Now=double.NaN,x=>x.Distance=-1,x=>x.ContactAge=double.PositiveInfinity,x=>x.Capacity=0,x=>x.Loaded=99,x=>x.Aggression=2,x=>x.Reserve=-1}){
            f=F();edit(f);policy=new();Check(!policy.Evaluate(f,P(),Reload(f),c),"invalid context yields");}
        foreach(var edit in new Action<PistolSwapFrame>[] {x=>x.Chamber=-1,x=>x.Loaded=99,x=>x.Capacity=-1,x=>x.Magazine=50}){
            pistol=P();edit(pistol);policy=new();Check(!policy.Evaluate(F(),pistol,Reload(F()),c),"invalid sidearm input yields");}
        var score=Reload(F());score.ReloadScore=double.NaN;policy=new();Check(!policy.Evaluate(F(),P(),score,c),"invalid score yields");
        using(var pe=new PEReader(File.OpenRead("Client/bin/Release/netstandard2.1/COMET.dll"))){var md=pe.GetMetadataReader();var type=md.TypeDefinitions.Select(md.GetTypeDefinition).Single(t=>md.GetString(t.Name)=="Plugin");
            MethodDefinition Method(string name)=>type.GetMethods().Select(md.GetMethodDefinition).Single(m=>md.GetString(m.Name)==name);
            foreach(var gate in new[]{"IsSupportedAi","get_Authority","Ghost","get_BotActive","get_IsDead","get_GameEnding","get_SAINLayersActive"})Check(ReloadTimingTests.Calls(pe,md,Method("PistolScope"),gate)==1,"compiled scope "+gate);
            foreach(var method in new[]{"ReadPistol","TryPistolSwap","SamplePistolSwap"})foreach(var forbidden in new[]{"get_RealDistance","get_EnemyPosition","get_BulletCount","BotCancelReload","AddAmmoToMagazines","FastForward","SetSlotItem","Teleport"})
                Check(ReloadTimingTests.Calls(pe,md,Method(method),forbidden)==0,"no hidden info, cached ammo or forced animation: "+method+forbidden);
            Check(ReloadTimingTests.Calls(pe,md,Method("TryPistolSwap"),"TryChangeToSlot")==1,"one native switch API");
            Check(ReloadTimingTests.Calls(pe,md,Method("ReadPistol"),"get_ChamberAmmoCount")==1&&ReloadTimingTests.Calls(pe,md,Method("ReadPistol"),"GetCurrentMagazine")>0,"actual holstered weapon ammo");
            Check(ReloadTimingTests.Calls(pe,md,Method("SamplePistolSwap"),"get_HandsController")==1,"actual hands observed");
        }
        Directory.CreateDirectory("artifacts");File.WriteAllText("artifacts/w02-wire-traces.json",JsonSerializer.Serialize(wire));
        var active=wire.First(t=>t.GetProperty("state").GetString()=="drawing");
        var node=JsonSerializer.Deserialize<System.Text.Json.Nodes.JsonObject>(active.GetRawText())!;node["action"]="StandAndShoot";
        File.WriteAllText("artifacts/w02-pistol-swap.json",JsonSerializer.Serialize(new{botInstance="instance",pistolSwap=node,pistolSwapConfig=c}));
        Console.WriteLine($"W02: {n} assertions passed.");return n;
    }
}
