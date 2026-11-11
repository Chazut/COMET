using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata.Ecma335;
using Comet.Core;
using Comet.Server;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.Json;

internal static class CoverFeintTests
{
    public static int Run()
    {
        int n=0;void Check(bool ok,string why){if(!ok)throw new Exception("M14: "+why);n++;}
        void Invalid(Action a){try{a();}catch(ArgumentException){n++;return;}throw new Exception("M14 accepted invalid settings");}
        CoverFeintFrame F(double now=10)=>new(){Now=now,Age=2,SightAge=2,Target="contact",Key="contact:1",Known=new(0,0,12),Position=new(0,0,0),
            MagazineAudible=true,Outdoor=true,Cover=true,Ready=true,Healthy=true,Bold=true,Grounded=true,Protected=true,Distance=12,AmmoRatio=.8};
        var c=new CoverFeintConfig();c.Validate();
        Check(c.Enabled&&c.BaitPeek&&c.MagazineCheck&&c.Jump&&c.PeekChance==35&&c.MagazineChance==30&&c.MinDistance==5&&c.MaxDistance==40
            &&c.MagazineDistance==20&&c.SightLoss==2&&c.Memory==10&&c.MinStep==.9&&c.MaxStep==2&&c.ExposureMin==.2&&c.ExposureMax==.35
            &&c.Peeks==2&&c.HoldMin==4&&c.HoldMax==7&&c.RetryDelay==20,"approved defaults");
        var wire=new List<JsonElement>();CoverFeintPolicy p=new();
        void Save()=>wire.Add(JsonSerializer.Deserialize<JsonElement>(Newtonsoft.Json.JsonConvert.SerializeObject(p.Trace,Comet.Client.Diagnostics.Json)));
        int rolls=0;double Low(){rolls++;return .1;}
        var deferred=new CoverFeintPolicy();deferred.Begin(F(),c,Low);
        deferred.DeferSafety(F(10.1),c,-1000);
        Check(deferred.Active&&!deferred.Moving&&!deferred.WantsRoute&&deferred.Trace!.MoveRequests==0
            &&deferred.Trace.Reason=="feint-waiting-for-safety-budget","first probe waits without inventing safety or moving");
        deferred.DeferSafety(F(10.74),c,-1000);Check(deferred.Active,"bounded first-probe grace");
        deferred.DeferSafety(F(10.75),c,-1000);Check(!deferred.Active&&deferred.Trace!.Reason=="feint-initial-safety-budget-expired","starvation remains bounded");
        deferred=new();deferred.Begin(F(),c,Low);var danger=F(10.1);danger.Visible=true;
        deferred.DeferSafety(danger,c,-1000);Check(!deferred.Active,"reacquired contact interrupts initial wait");
        deferred=new();deferred.Begin(F(),c,Low);deferred.DeferSafety(F(10.1),c,10);
        Check(deferred.Active,"brief deferral with recent proof");deferred.DeferSafety(F(10.21),c,10);
        Check(!deferred.Active&&deferred.Trace!.Reason=="feint-safety-budget-expired","existing proof keeps strict expiry");
        deferred=new();deferred.Begin(F(),c,Low);deferred.DeferSafety(F(9),c,-1000);Check(!deferred.Active,"reversed clock fails closed");
        deferred=new();deferred.Begin(F(),c,Low);deferred.DeferSafety(F(10.1),c,-1000);
        Check(deferred.Observe(F(10.2),c)&&deferred.Plan(new(1,0,0),10.2),"validated plan can continue after temporary deferral");
        rolls=0;
        Check(p.Begin(F(),c,Low)&&p.Trace!.Kind=="peek","peek selected once");Save();
        Check(rolls==6&&!p.Begin(F(10.1),c,Low)&&rolls==6,"active tactic never rerolls");p.Accepted(true);Save();
        Check(p.Plan(new(1,0,0),10.1),"short peek plan");Save();p.RequestMove(10.2);Save();
        var f=F(10.4);f.Position=new(.5,0,0);f.Speed=1;Check(p.Observe(f,c)&&p.Trace!.MovementObserved&&!p.Trace.ExposedObserved,"movement is not exposure");Save();
        f=F(10.5);f.Position=new(1,0,0);f.AtGoal=true;f.Speed=1;f.Exposed=true;
        Check(p.Observe(f,c)&&p.Trace!.State=="out","passing the goal at speed does not start exposure");
        f.Speed=0;f.Grounded=false;Check(p.Observe(f,c)&&p.Trace!.State=="out","airborne arrival is not settled");
        f.Grounded=true;Check(p.Observe(f,c)&&p.Trace!.State=="show"&&p.Trace.ExposedObserved,"actual grounded arrival starts exposure");Save();
        f.Now=10.6;p.Observe(f,c);Check(p.Trace!.State=="show","exposure duration is stable rather than rerolled each frame");
        f.Now=10.8;p.Observe(f,c);Check(p.Trace.State=="back-pending"&&!p.Trace.ReturnedObserved,"elapsed exposure asks to return, never teleports");Save();
        p.RequestMove(10.8);f=F(11);f.Position=new(.5,0,0);p.Observe(f,c);Save();
        f=F(11.1);f.AtGoal=true;f.Protected=true;p.Observe(f,c);Check(p.Trace.State=="pause"&&p.Trace.CompletedPeeks==1&&p.Trace.ReturnedObserved,"actual protected return counted");Save();
        f.Now=12.1;p.Observe(f,c);Check(p.Trace.State=="planning","stable pause opens second peek");p.Plan(new(-1,0,0),12.1);p.RequestMove(12.1);
        f=F(12.3);f.Position=new(-1,0,0);f.Exposed=f.AtGoal=true;p.Observe(f,c);f.Now=12.6;p.Observe(f,c);p.RequestMove(12.6);
        f=F(12.9);f.AtGoal=true;Check(!p.Observe(f,c)&&p.Trace.CompletedPeeks==2&&p.Trace.ReturnedObserved&&p.Trace.ValidFor==0,"two real returns finish");Save();
        Check(!p.Begin(F(40),c,Low),"time alone cannot reroll same encounter");f=F(40);f.Heard=39;Check(p.Begin(f,c,Low),"fresh personal sound plus delay allows retry");p.End("test");
        f=F(41);f.Fire=40;Check(!p.Begin(f,c,Low),"fresh cue cannot bypass delay");
        p=new();int call=0;double MagRoll()=>call++==0?.99:.1;
        Check(p.Begin(F(),c,MagRoll)&&p.Trace!.Kind=="magazine","weighted order selects at most one eligible feint");p.Accepted(true);p.Plan(p.Home,10.1);Save();
        p.MagazineResult(true,false,10.2);Check(!p.Trace!.AnimationObserved,"accepted check does not prove animation");Save();
        p.MagazineObserved(false,true,10.3);Check(p.Trace.State=="mag-check","no observed operation cannot start ready hold");
        p.MagazineObserved(true,false,10.4);p.Sound("check-magazine",false);Check(p.Trace.SoundEvents==1&&p.Trace.PlaybackSubmissions==0,"sound event is not playback");
        p.Sound("",true);p.MagazineObserved(false,false,11);Check(p.Trace.State=="mag-check","animation completion still needs weapon readiness");Save();
        p.MagazineObserved(false,true,11.2);Check(p.Trace.State=="hold"&&p.Trace.WeaponReady,"ready weapon begins 4-7 s hold");Save();
        f=F(15.4);Check(p.Observe(f,c),"hold measured from readiness");f.Now=15.6;Check(!p.Observe(f,c)&&p.Trace.Reason=="feint-hold-finished","bounded ready hold ends");Save();
        p=new();call=0;p.Begin(F(),c,MagRoll);p.Plan(p.Home,10);p.MagazineResult(false,false,10);Check(!p.Active&&!p.Trace!.AnimationObserved,"native refusal releases");Save();
        p=new();call=0;p.Begin(F(),c,MagRoll);p.Plan(p.Home,10);p.MagazineResult(true,false,10);p.MagazineObserved(false,false,14.1);
        Check(!p.Active&&p.Trace!.Reason=="feint-animation-or-ready-unconfirmed","no animation hard deadline");Save();
        foreach(string scenario in new[]{"disabled","ghost","client","non-PMC","indoor","visible","old","too-close","far","hands","no-cover","air","nan"}){
            p=new();f=F();var cfg=new CoverFeintConfig();
            switch(scenario){case "disabled":cfg.Enabled=false;break;case "ghost":case "client":case "non-PMC":f.Block=scenario;break;
                case "indoor":f.Outdoor=false;break;case "visible":f.Visible=true;break;case "old":f.Age=11;break;case "too-close":f.Distance=4;break;
                case "far":f.Distance=41;break;case "hands":f.Ready=false;break;case "no-cover":f.Cover=false;break;case "air":f.Grounded=false;break;case "nan":f.Known=new(double.NaN,0,0);break;}
            int before=rolls;Check(!p.Begin(f,cfg,Low)&&rolls==before,"ineligible encounter consumes no random roll: "+scenario);
        }
        foreach(string scenario in new[]{"emergency","route","clock","age","visible","stuck","floor","unsafe-return"}){
            p=new();p.Begin(F(),c,Low);p.Accepted(true);p.Plan(new(1,0,0),10);p.RequestMove(10);f=F(10.1);p.Observe(f,c);
            f=F(10.2);f.Position=new(.5,0,0);
            switch(scenario){case "emergency":f.Block="urgent-medical";break;case "route":f.RouteValid=false;break;case "clock":f.Now=9;break;case "age":f.Age=double.NaN;break;
                case "visible":f.Visible=true;Check(p.Observe(f,c)&&p.Trace!.State=="back-pending"&&!p.Trace.ReturnedObserved,"visible threat requests verified return");continue;
                case "stuck":f=F(11.2);break;
                case "floor":f.Position=new(1,1,0);f.AtGoal=f.Exposed=true;Check(p.Observe(f,c)&&!p.Trace!.ExposedObserved,"wrong floor is not arrival");continue;
                case "unsafe-return":f.Visible=true;p.Observe(f,c);p.RequestMove(10.2);f=F(10.4);f.AtGoal=true;f.Protected=false;break;}
            Check(!p.Observe(f,c)&&p.Trace!.ValidFor==0,"failed sequence releases: "+scenario);
        }
        p=new();p.Begin(F(),c,Low);p.Plan(new(1,0,0),10);f=F(10.1);f.Visible=true;Check(!p.Observe(f,c),"new sight before moving cancels bait");
        p=new();f=F();f.Healthy=false;Check(p.Begin(f,c,Low)&&p.Trace!.Kind=="magazine","injured PMC never bait peeks but can use magazine if otherwise medically safe");
        p=new();f=F();f.AmmoRatio=.2;f.Bold=false;Check(!p.Begin(f,c,Low),"no eligible feint yields to SAIN");
        p=new();Check(!p.Begin(F(),c,()=>.99)&&p.Trace!.Kind==""&&!p.Active,"both failed chances mean staying");Save();
        for(int i=0;i<70;i++){f=F(100+i*30);f.Key="key-"+i;p.Begin(f,c,Low);p.End("done");}Check(p.HistoryCount==64,"bounded history never evicts to enable rerolls");
        p.Clear("raid-ended");Check(p.HistoryCount==0&&!p.Active,"raid/scope cleanup");
        foreach(var prop in typeof(CoverFeintConfig).GetProperties().Where(x=>x.PropertyType==typeof(double)))foreach(double value in new[]{double.NaN,double.PositiveInfinity,-1,1000}){
            var cfg=new CoverFeintConfig();prop.SetValue(cfg,value);Invalid(cfg.Validate);}
        Invalid(()=>new CoverFeintConfig{ExposureMin=.5,ExposureMax=.2}.Validate());Invalid(()=>new CoverFeintConfig{MinStep=2,MaxStep=1}.Validate());
        Invalid(()=>new CoverFeintConfig{HoldMin=8,HoldMax=2}.Validate());Invalid(()=>new CoverFeintConfig{Peeks=3}.Validate());Invalid(()=>new CometConfig{CoverFeint=null!}.Validate());
        Check(JsonSerializer.Deserialize<CometConfig>("{}")!.CoverFeint.PeekChance==35,"old server configs receive defaults");
        var dir=Path.Combine(Path.GetTempPath(),"comet-m14-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(dir);var file=Path.Combine(dir,"config.json");
        try{var store=new ConfigStore(file);var cfg=store.Read();cfg.CoverFeint.PeekChance=60;store.Save(cfg,cfg.Revision);Check(new ConfigStore(file).Read().CoverFeint.PeekChance==60,"server setting persists");
            var prior=File.ReadAllText(file);cfg=store.Read();cfg.CoverFeint.Peeks=99;Invalid(()=>store.Save(cfg,cfg.Revision));Check(File.ReadAllText(file)==prior,"invalid edit leaves config intact");
        }finally{foreach(var name in new[]{file,file+".bak"})if(File.Exists(name))File.Delete(name);Directory.Delete(dir);}
        using(var pe=new PEReader(File.OpenRead("references/sain-4.5.1/SAIN.dll"))){var md=pe.GetMetadataReader();var type=md.TypeDefinitions.Select(md.GetTypeDefinition).Single(t=>md.GetString(t.Name)=="FreezeAction");
            Check(type.GetMethods().Select(md.GetMethodDefinition).Any(m=>md.GetString(m.Name)=="Update"),"official Freeze callback exists");}
        using(var pe=new PEReader(File.OpenRead("Client/bin/Release/netstandard2.1/COMET.dll"))) {
            var md=pe.GetMetadataReader();var plugin=md.TypeDefinitions.Select(md.GetTypeDefinition).Single(t=>md.GetString(t.Name)=="Plugin");
            MethodDefinition Method(string name)=>plugin.GetMethods().Select(md.GetMethodDefinition).Single(m=>md.GetString(m.Name)==name);
            foreach(var gate in new[]{"IsSupportedAi","get_Authority","Ghost","get_BotActive","get_IsDead","get_GameEnding","get_SAINLayersActive"})
                Check(Calls(pe,md,Method("FeintScope"),gate)==1,"compiled scope gate "+gate);
            Check(Calls(pe,md,Method("FeintPathInitialized"),"Remove")==1,"reused path invalidates old generation");
            Check(Calls(pe,md,Method("RefreshFeint"),"RemainingRelocationRoute")==1&&Calls(pe,md,Method("RefreshFeint"),"FeintRouteClear")==1,"actual native steering route rechecked");
            Check(Calls(pe,md,Method("RefreshFeint"),"CheckAmmo")==1&&Calls(pe,md,Method("MagazineOperation"),"get_CurrentHandsOperation")==1,"native virtual magazine check and observed operation");
            Check(Calls(pe,md,Method("RefreshFeint"),"ControlledJump")==1,"running jumps use M12 authority and physics");
            Check(Calls(pe,md,Method("FeintSteering"),"EndShoot")==1,"hidden feint does not add suppressive fire");
            Check(Calls(pe,md,Method("StopFeintRoutes"),"OwnFeintRoute")==1,"cleanup only touches owned generation");
            foreach(var method in new[]{"ChooseFeint","RefreshFeint","FeintFrame","PlanFeint","MoveFeint","FeintSteering"})
                foreach(var forbidden in new[]{"Teleport","SetEnemyVisible","get_EnemyPosition","FindObjectsOfType","Shoot","Reload","FastForward"})
                    Check(Calls(pe,md,Method(method),forbidden)==0,"no hidden transforms, forced reactions or fake completion: "+method+" "+forbidden);
        }
        p=new();f=F();f.Bold=false;f.MagazineAudible=false;Check(!p.Begin(f,c,Low),"no usable native sound asset means no magazine feint");
        Directory.CreateDirectory("artifacts");File.WriteAllText("artifacts/m14-wire-traces.json",JsonSerializer.Serialize(wire));
        // Shared fixture uses real serialized fields, so RR checks the same contract as COMET logs.
        var active=wire.First(t=>t.GetProperty("state").GetString()=="hold");
        File.WriteAllText("artifacts/m14-cover-feint.json",JsonSerializer.Serialize(new{botInstance="instance",coverFeint=active,coverFeintConfig=c}));
        Console.WriteLine($"M14: {n} assertions passed.");return n;
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
