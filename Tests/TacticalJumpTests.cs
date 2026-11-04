using Comet.Core;
using Comet.Server;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text.Json;

internal static class TacticalJumpTests
{
    public static int Run()
    {
        int n=0;void Check(bool v,string why){if(!v)throw new Exception("M12: "+why);n++;}
        void Invalid(Action a){try{a();}catch(ArgumentException){n++;return;}throw new Exception("M12 accepted invalid config");}
        var c=new TacticalJumpConfig();c.Validate();int rolls=0;
        double Low(){rolls++;return .24;}double High(){rolls++;return .25;}
        TacticalJumpFrame F(double now=10,string target="known-contact")=>new(){Now=now,Feature="M11",Kind="retreat-switch",Target=target,Action="Retreat",
            Speed=5,ForwardSpeed=4.8,MinSpeed=3.8,Stamina=60,MinStamina=0,Grounded=true,CanJump=true,Clearance=true,Position=new(now,0,0),Landing=new(now+3.5,0,0)};
        void Takeoff(TacticalJumpPolicy p,double now=10.1)=>p.Observe(false,now,new(now,.2,0),4.8,55,c);
        void Land(TacticalJumpPolicy p,double now=10.8)=>p.Observe(true,now,new(now+3,0,0),4.7,55,c);
        Check(c.Enabled&&c.NativeRush&&c.AirShotChance==25&&c.RushMinSpeed==2&&c.MinimumStamina==0&&c.TakeoffTimeout==.5,"approved defaults");
        var p=new TacticalJumpPolicy();Check(p.Request(F(),c,Low),"authorize moving jump");
        Check(p.Trace!.Requested&&!p.Trace.Accepted&&!p.Trace.TakeoffObserved&&!p.HoldFire,"request alone proves neither flight nor fire block");
        p.Result(true,10);Check(p.Trace.Accepted&&!p.Trace.TakeoffObserved&&!p.HoldFire,"native acceptance is not takeoff");
        Takeoff(p);Check(p.Trace.TakeoffObserved&&p.Trace.State=="airborne"&&p.Trace.AirShotAllowed&&!p.HoldFire,"24 percent roll permits native fire");
        p.Shot(false,10.2);Check(p.Trace.AirShots==1&&p.Trace.GroundShots==0,"actual air shot recorded");
        Land(p);Check(p.Trace.LandingObserved&&!p.Active&&!p.HoldFire&&p.Trace.ValidFor==0,"landing immediately releases");
        p.Shot(true,11);Check(p.Trace.GroundShots==1,"post-landing shot distinct");p.Shot(false,13);Check(p.Trace.AirShots==1,"old attempts do not collect later shots");
        int before=rolls;p.Request(F(12,"other-contact"),c,High);p.Result(false,12);p.Request(F(13),c,High);
        Check(rolls==before+1&&p.Trace!.AirShotAllowed&&p.Trace.StyleCached,"A B A target changes do not reroll A");
        p.Result(false,13);Check(!p.HoldFire&&!p.Active,"failed native request never blocks fire");
        p=new();p.Request(F(),c,High);p.Result(true,10);Takeoff(p);
        Check(p.HoldFire&&p.Trace!.FireHeld&&!p.Trace.AirShotAllowed,"25 boundary excluded");
        Check(!p.Request(F(10.2,"other"),c,Low)&&p.Trace!.Target=="known-contact"&&p.Trace.ConcurrentRefusals==1,"concurrent request preserves original flight and style");
        p.Observe(false,11.2,new(14,.1,0),2,50,c);Check(p.HoldFire,"hold remains beyond fork 0.55 seconds while actually airborne");
        Land(p,11.21);Check(!p.HoldFire&&p.Trace!.LandingObserved,"actual landing ends hold without an extra timer");
        p=new();p.Request(F(),c,High);p.Result(true,10);p.Observe(true,10.51,new(10,0,0),4,50,c);
        Check(!p.Active&&!p.HoldFire&&!p.Trace!.TakeoffObserved&&p.Trace.Reason=="jump-takeoff-not-observed","accepted input with no flight expires harmlessly");
        p=new();p.Request(F(),c,High);p.Result(true,10);Takeoff(p);Land(p,10.3);
        Check(!p.Request(F(10.4),c,Low)&&p.Trace!.Reason=="jump-native-retry-delay","minimum native retry interval");
        var stationary=F(11);stationary.Position=new(10.1,0,0);
        Check(!p.Request(stationary,c,Low)&&p.Trace!.Reason=="jump-no-progress-since-last-takeoff","speed does not permit colliding hops at same place");
        Check(p.Request(F(12),c,Low),"multiple meaningful jumps remain possible");
        foreach(var mutate in new Action<TacticalJumpFrame>[] {f=>f.Block="ghost",f=>f.Block="non-pmc",f=>f.Block="remote",f=>f.Block="dead",f=>f.Block="medical",
            f=>f.Feature="unstuck",f=>f.Target="",f=>f.Kind="",f=>f.Grounded=false,f=>f.CanJump=false,f=>f.Speed=3.7,f=>f.ForwardSpeed=-5,
            f=>f.ForwardSpeed=3.7,f=>f.Stamina=101,f=>f.MinStamina=61,f=>f.Clearance=false,f=>f.GeometryAge=.11,f=>f.GeometryAge=-1,
            f=>f.Now=double.NaN,f=>f.Speed=double.PositiveInfinity,f=>f.Position=new(double.NaN,0,0),f=>f.Landing=new(0,0,double.PositiveInfinity)}) {
            p=new();var f=F();mutate(f);before=rolls;Check(!p.Request(f,c,Low)&&!p.HoldFire&&!p.Active,"unsafe input refuses with no fire ownership");Check(rolls==before,"refusal does not draw style");
        }
        foreach(var source in new[]{("D02",1.2,35d),("M03",1.1,30d),("M05",2.2,35d),("M11",3.8,0d),("SAIN",2d,0d)}) {
            var f=F();f.Feature=source.Item1;f.MinSpeed=source.Item2;f.MinStamina=source.Item3;f.Stamina=source.Item3;f.Speed=f.ForwardSpeed=source.Item2;
            Check(TacticalJumpPolicy.Gate(f,c)=="","per-feature thresholds retained: "+source.Item1);
            f.ForwardSpeed-=.01;Check(TacticalJumpPolicy.Gate(f,c)=="jump-insufficient-directional-speed","directional threshold: "+source.Item1);
        }
        var floor=new TacticalJumpConfig{MinimumStamina=65};Check(TacticalJumpPolicy.Gate(F(),floor)=="jump-insufficient-stamina","extra floor only raises native feature floors");
        var native=F();native.Feature="SAIN";Check(TacticalJumpPolicy.Gate(native,new(){NativeRush=false})=="jump-native-rush-disabled","native controller opt out");
        Check(TacticalJumpPolicy.Gate(F(),new(){Enabled=false})=="jump-control-disabled","module opt out");
        foreach(var chance in new[]{0d,100d}) {
            p=new();var special=new TacticalJumpConfig{AirShotChance=chance};p.Request(F(),special,Low);p.Result(true,10);Takeoff(p);
            Check(p.HoldFire==(chance==0)&&p.Trace!.AirShotAllowed==(chance==100),"0 and 100 chance exact");
        }
        p=new();for(int i=0;i<70;i++){p.Request(F(10+i,"target-"+i),c,Low);p.Result(false,10+i);}
        Check(p.StyleCount==64&&!p.Trace!.AirShotAllowed&&p.Trace.Roll== -1,"bounded IDs-only history falls back without eviction/reroll");
        p.Request(F(90,"target-0"),c,High);Check(p.Trace!.AirShotAllowed&&p.Trace.StyleCached,"oldest style remains stable");
        p.Clear("raid-ended");Check(p.StyleCount==0&&!p.Active&&!p.HoldFire,"raid cleanup");
        foreach(var reason in new[]{"ghost","authority-lost","bot-ended","raid-ended","adapter-error"}) {
            p=new();p.Request(F(),c,High);p.Result(true,10);Takeoff(p);p.Clear(reason);
            Check(!p.HoldFire&&!p.Active&&p.StyleCount==0&&p.Trace!.ValidFor==0,"scope release "+reason);
        }
        p=new();p.Request(F(),c,High);p.Result(true,10);Takeoff(p);p.Observe(false,9,new(0,0,0),1,50,c);Check(!p.Active&&!p.HoldFire,"clock reset releases");
        foreach(var value in new[]{-1d,double.NaN,double.PositiveInfinity,1d}){p=new();Check(!p.Request(F(),c,()=>value)&&!p.Active,"bad random source refuses");}
        foreach(var prop in typeof(TacticalJumpConfig).GetProperties().Where(x=>x.PropertyType==typeof(double)))
            foreach(double v in new[]{double.NaN,double.PositiveInfinity,-1,999}){var cfg=new TacticalJumpConfig();prop.SetValue(cfg,v);Invalid(cfg.Validate);}
        Invalid(()=>new CometConfig{TacticalJump=null!}.Validate());
        Check(JsonSerializer.Deserialize<CometConfig>("{}")!.TacticalJump.AirShotChance==25,"old server config receives defaults");
        var dir=Path.Combine(Path.GetTempPath(),"comet-m12-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(dir);var file=Path.Combine(dir,"config.json");
        try {var store=new ConfigStore(file);var save=store.Read();save.TacticalJump.AirShotChance=35;store.Save(save,save.Revision);
            var read=new ConfigStore(file).Read();Check(read.TacticalJump.AirShotChance==35,"UI config persists");var prior=File.ReadAllText(file);read.TacticalJump.AirShotChance=101;
            Invalid(()=>store.Save(read,read.Revision));Check(File.ReadAllText(file)==prior,"invalid save preserves previous file");
        }finally{foreach(var path in new[]{file,file+".bak"})if(File.Exists(path))File.Delete(path);Directory.Delete(dir);}
        // Decode official IL rather than matching source strings: only the three intended Rush calls are replaced.
        using(var pe=new PEReader(File.OpenRead("references/sain-4.5.1/SAIN.dll"))) {
            var md=pe.GetMetadataReader();var types=md.TypeDefinitions.Select(h=>md.GetTypeDefinition(h)).ToArray();
            var rush=types.Single(t=>md.GetString(t.Name)=="RushEnemyAction");
            foreach(var expected in new[]{("checkJump",1),("checkJumpEnemyInSight",2)}) {
                var method=rush.GetMethods().Select(h=>md.GetMethodDefinition(h)).Single(m=>md.GetString(m.Name)==expected.Item1);
                Check(Calls(pe,md,method,"TryJump")==expected.Item2,"official Rush interception "+expected.Item1);
            }
            foreach(var pair in new[]{("SAINShootData","ShootWhenAimComplete"),("ManualShootClass","TryShoot"),("PlayerComponent","OnMakingShot")})
                Check(types.Single(t=>md.GetString(t.Name)==pair.Item1).GetMethods().Any(h=>md.GetString(md.GetMethodDefinition(h).Name)==pair.Item2),"official firing hook "+pair);
        }
        using(var pe=new PEReader(File.OpenRead("Client/bin/Release/netstandard2.1/COMET.dll"))) {
            var md=pe.GetMetadataReader();var plugin=md.TypeDefinitions.Select(md.GetTypeDefinition).Single(t=>md.GetString(t.Name)=="Plugin");
            MethodDefinition Method(string name)=>plugin.GetMethods().Select(md.GetMethodDefinition).Single(m=>md.GetString(m.Name)==name);
            foreach(var method in new[]{"StepDoorJump","RefreshCornerPeek","RefreshChase","WeaveMovement","NativeRushJump"})
                Check(Calls(pe,md,Method(method),"ControlledJump")==1,"common gate integration "+method);
            foreach(var gate in new[]{"IsPmcAi","get_Authority","Ghost","get_IsDead","get_BotActive","get_GameEnding","get_SAINLayersActive"})
                Check(Calls(pe,md,Method("JumpScope"),gate)==1,"compiled scope "+gate);
            Check(Calls(pe,md,Method("ControlledJump"),"TryJump")==2,"native jump execution plus module-disabled pass through");
            foreach(var method in new[]{"ControlledJump","SampleTacticalJump","JumpFireAllowed","NativeRushJump"})
                foreach(var banned in new[]{"BlockFor","Shoot","Teleport","SetEnemyVisible","get_EnemyPosition","FindObjectsOfType"})
                    Check(Calls(pe,md,Method(method),banned)==0,"no forced fire, timer block or hidden search "+method+":"+banned);
            Check(Calls(pe,md,Method("JumpAimedFire"),"JumpFireAllowed")==1&&Calls(pe,md,Method("JumpManualFire"),"JumpFireAllowed")==1,"both native fire paths gated");
        }
        // Actual DTO serialization is fed to the TS parser suite as well as C# wire assertions.
        var events=new List<object>();p=new();p.Request(F(),c,High);
        void Save()=>events.Add(JsonSerializer.Deserialize<JsonElement>(Newtonsoft.Json.JsonConvert.SerializeObject(p.Trace,Comet.Client.Diagnostics.Json)));
        Save();p.Result(true,10);Save();Takeoff(p);Save();p.Shot(false,10.2);Save();Land(p);Save();p.Request(F(12),c,Low);p.Result(false,12);Save();p.End("raid-ended");Save();
        Directory.CreateDirectory("artifacts");File.WriteAllText("artifacts/m12-wire-traces.json",JsonSerializer.Serialize(events));
        using(var wire=JsonDocument.Parse(JsonSerializer.Serialize(events))) {
            Check(wire.RootElement[2].GetProperty("takeoffObserved").GetBoolean()&&!wire.RootElement[2].GetProperty("landingObserved").GetBoolean(),"wire flight evidence");
            Check(wire.RootElement[3].GetProperty("airShots").GetInt32()==1,"wire shot evidence");
        }
        var random=new Random(1212);
        for(int i=0;i<500;i++) {
            p=new();var f=F();double roll=random.NextDouble();p.Request(f,c,()=>roll);p.Result(i%7!=0,10);
            if(i%7==0){Check(!p.Active&&!p.HoldFire,"mixed failed jump");continue;}
            Takeoff(p);Check(p.HoldFire==(roll>=.25),"random style firing gate");
            double flight=.1+random.NextDouble()*1.3;p.Observe(false,10.1+flight,new(12,.2,0),4,55,c);
            Check(p.HoldFire==(roll>=.25),"flight length does not change gate");
            Land(p,10.2+flight);Check(!p.HoldFire&&!p.Active&&p.Trace!.LandingObserved,"every observed landing releases");
        }
        Console.WriteLine($"M12: {n} assertions passed.");return n;
    }
    private static int Calls(PEReader pe,MetadataReader md,MethodDefinition method,string name)
    {
        var ops=typeof(OpCodes).GetFields(BindingFlags.Public|BindingFlags.Static).Where(f=>f.FieldType==typeof(OpCode)).Select(f=>(OpCode)f.GetValue(null)!).ToDictionary(o=>unchecked((ushort)o.Value));
        var il=pe.GetMethodBody(method.RelativeVirtualAddress).GetILBytes()!;int count=0;
        for(int i=0;i<il.Length;) {
            ushort value=il[i++];if(value==0xfe)value=(ushort)(0xfe00|il[i++]);var op=ops[value];
            if(op==OpCodes.Call||op==OpCodes.Callvirt) {
                var h=MetadataTokens.EntityHandle(BitConverter.ToInt32(il,i));
                string called=h.Kind==HandleKind.MethodDefinition?md.GetString(md.GetMethodDefinition((MethodDefinitionHandle)h).Name):h.Kind==HandleKind.MemberReference?md.GetString(md.GetMemberReference((MemberReferenceHandle)h).Name):"";
                if(called==name)count++;
            }
            i+=op.OperandType switch {OperandType.InlineNone=>0,OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar=>1,OperandType.InlineVar=>2,
                OperandType.InlineI8 or OperandType.InlineR=>8,OperandType.InlineSwitch=>4+4*BitConverter.ToInt32(il,i),_=>4};
        }
        return count;
    }
}
