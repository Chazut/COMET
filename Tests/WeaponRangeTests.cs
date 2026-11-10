using Comet.Core;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

static class WeaponRangeTests
{
    public static int Run()
    {
        int n=0;void Check(bool v,string why){n++;if(!v)throw new Exception("W05: "+why);}
        void Throws(Action a,string why){try{a();}catch(ArgumentException){n++;return;}throw new Exception("W05: "+why);}
        var c=new WeaponRangeConfig();c.Validate();
        Check(c.Enabled&&c.LongDistance==80&&c.CloseDistance==45&&c.VisualMemory==2&&c.StableSeconds==1&&c.SinceShot==1&&c.LostContactSeconds==20,"approved defaults");
        foreach(var prop in typeof(WeaponRangeConfig).GetProperties().Where(x=>x.PropertyType==typeof(double)))
            foreach(var bad in new[]{double.NaN,double.PositiveInfinity,-1,1000}){var q=new WeaponRangeConfig();prop.SetValue(q,bad);Throws(q.Validate,"bounds "+prop.Name);}
        Throws(()=>new WeaponRangeConfig{LongDistance=45,CloseDistance=45}.Validate(),"strict hysteresis");
        Throws(()=>new CometConfig{WeaponRange=null!}.Validate(),"missing section");
        Check(JsonSerializer.Deserialize<CometConfig>("{}")!.WeaponRange.LongDistance==80,"old config defaults");
        var saved=JsonSerializer.Deserialize<CometConfig>(JsonSerializer.Serialize(new CometConfig{WeaponRange=new(){LongDistance=100,CloseDistance=40,StableSeconds=2,SinceShot=2,VisualMemory=3,LostContactSeconds=25}}))!;
        saved.Validate();Check(saved.WeaponRange.LongDistance==100&&saved.WeaponRange.StableSeconds==2,"server persistence");
        WeaponRangeGun Gun(string id,string slot,string type,string optic,bool auto=false)=>Make(id,slot,type,optic,auto);
        WeaponRangeFrame F()=>new(){Now=10,Current="rifle",Ready=true,SinceShot=2,
            Weapons=new[]{Gun("rifle","FirstPrimaryWeapon","assaultRifle","iron",true),Gun("dmr","SecondPrimaryWeapon","marksmanRifle","scope")},
            Contacts=new[]{new WeaponRangeContact{Id="enemy",Source="personal-vision",Age=1,Distance=100}}};
        var p=new WeaponRangePolicy();var f=F();var wire=new List<JsonElement>();
        void Save()=>wire.Add(JsonSerializer.Deserialize<JsonElement>(Newtonsoft.Json.JsonConvert.SerializeObject(p.Trace,Comet.Client.Diagnostics.Json)));
        string Stable(){string r="";for(int i=0;i<=4;i++){f.Now=10+i*.25;r=p.Evaluate(f,c);}return r;}
        Check(p.Evaluate(f,c)==""&&p.Trace!.Reason=="range-wait-stable-opportunity","first sample not stable");Save();
        Check(Stable()=="dmr","choose scoped DMR after stable window");Save();
        Check(p.Trace!.Choice=="Switch"&&!p.Trace.Requested&&!p.Trace.Ready,"recommendation not animation");
        var w=new WeaponReturnTrace{To="dmr",From="rifle",Choice="Switch",State="requested",Requested=true,Reason="weapon-native-request"};
        p.Arbitration(w,11);Check(p.Pending&&!p.Trace.Ready,"pending shared ownership");Save();
        Check(p.Evaluate(F(),c)==""&&p.Trace.Episode==6,"no overlapping proposal replaces request");
        w.Accepted=true;p.Arbitration(w,11.1);Check(!p.Trace.Ready&&!p.Trace.InHands,"native acceptance is not readiness");Save();
        w.State="drawing";w.InHands=true;p.Arbitration(w,11.2);Check(p.Pending&&!p.Trace.Ready,"hands alone not ready");Save();
        w.State="ready";w.Ready=true;p.Arbitration(w,11.5);Check(!p.Pending&&p.Trace.Ready,"observed readiness");Save();
        f.Current="dmr";f.Now=12;f.Contacts[0].Distance=60;f.Contacts[0].Age=10;
        Check(p.Evaluate(f,c)==""&&p.ProtectCurrent,"hysteresis protects valid long-range choice from native return");Save();
        p.Defer("range-sample-deferred");Check(!p.ProtectCurrent&&p.Trace!.ValidFor==0,"deferred sample does not publish stale context");
        f.Now=12.25;Check(p.Evaluate(f,c)==""&&p.ProtectCurrent,"deferred sample retains long choice for fresh reassessment");
        f.Contacts[0].Age=20;Check(p.Evaluate(f,c)==""&&p.ProtectCurrent,"retain at exact memory limit");
        f.Contacts[0].Age=20.01;Check(p.Evaluate(f,c)==""&&!p.ProtectCurrent&&p.Trace!.Mode=="Close","lost contact starts stable close return");
        for(int i=1;i<=4;i++){f.Now=12.25+i*.25;p.Evaluate(f,c);}Check(p.Trace!.Choice=="Switch"&&p.Trace.To=="rifle","return after all contacts lost");Save();
        foreach(var mode in new[]{"busy","reload","unready","shot","incoming","truncated","unknown","empty","broken","budget","disabled"})
        {
            p=new();f=F();switch(mode){case "busy":f.Busy=true;break;case "reload":f.Reloading=true;break;case "unready":f.Ready=false;break;
                case "shot":f.SinceShot=.99;break;case "incoming":f.Incoming=true;break;case "truncated":f.Truncated=true;break;
                case "unknown":f.Weapons[1].Known=false;break;case "empty":f.Weapons[0].Loaded=0;break;case "broken":f.Weapons[0].Usable=false;break;
                case "budget":f.Block="range-budget";break;case "disabled":c.Enabled=false;break;}
            Check(Stable()=="","priority "+mode);Save();c.Enabled=true;
        }
        p=new();f=F();f.Incoming=f.Protected=true;Check(Stable()=="dmr","verified protection can permit opportunistic swap");
        p=new();f=F();f.Contacts[0].Source="personal-hearing";Check(Stable()=="","hearing cannot initiate distant choice");
        p=new();f=F();f.Contacts=new[]{f.Contacts[0],new(){Id="near",Source="personal-hearing",Distance=20,Age=2}};
        Check(Stable()==""&&p.Trace!.Mode=="Close"&&p.Trace.Target=="near","near remembered threat defeats distant goal");Save();
        f.Current="dmr";Check(Stable()=="rifle","near threat draws suitable loaded close weapon");
        p=new();f=F();f.Weapons[0]=Gun("rifle","FirstPrimaryWeapon","assaultRifle","scope",true);f.Weapons[1]=Gun("dmr","SecondPrimaryWeapon","marksmanRifle","iron");
        Check(Stable()==""&&p.ProtectCurrent,"scoped automatic beats unscoped DMR");
        p=new();f=F();f.Weapons[0].Slot="SecondPrimaryWeapon";f.Weapons[1].Slot="FirstPrimaryWeapon";
        Check(Stable()=="dmr","primary slot order is irrelevant");
        p=new();f=F();f.Weapons[1].Loaded=0;f.Weapons[1].Reserve=90;Check(Stable()=="","range-only choice does not abandon loaded gun to reload another");
        p=new();f=F();f.Weapons[1].Usable=false;Check(Stable()=="","malfunctioning target not suitable");
        p=new();f=F();f.Weapons[1]=Gun("dmr","SecondPrimaryWeapon","assaultRifle","iron",true);Check(Stable()=="","equivalent guns stay held");
        foreach(string change in new[]{"target","looted","attachment","gap","clock"})
        {
            p=new();f=F();p.Evaluate(f,c);f.Now=10.75;p.Evaluate(f,c);f.Now=11;
            switch(change){case "target":f.Contacts[0].Id="other-enemy";break;case "looted":f.Weapons[1].Id="new-dmr";break;case "attachment":f.Weapons[1].Fingerprint="new-optic";break;case "gap":f.Now=12;break;case "clock":f.Now=1;break;}
            Check(p.Evaluate(f,c)==""&&p.Trace!.StableFor==0,"fresh stability after "+change);
        }
        p=new();f=F();Stable();p.Decline("range-arbiter-unavailable");Check(!p.Trace!.Requested&&p.Trace.Choice=="Keep","previous W03 success cannot fabricate this request");Save();
        p=new();f=F();Stable();w=new(){To="dmr",Choice="Keep",Reason="weapon-minimum-hold"};p.Arbitration(w,11);
        Check(p.Trace!.Reason=="weapon-minimum-hold"&&!p.Trace.Requested,"W03 hold preserved");Save();
        p=new();f=F();Stable();w=new(){To="dmr",Choice="Switch",State="ended",Requested=true,Accepted=false,Reason="weapon-native-refused"};p.Arbitration(w,11);
        Check(!p.Pending&&p.Trace!.ValidFor==0&&!p.Trace.Ready,"native refusal terminal");Save();
        p=new();f=F();Stable();p.Clear("raid-ended");Check(!p.Pending&&!p.ProtectCurrent&&p.Trace!.ValidFor==0,"lifecycle cleanup");Save();
        foreach(var edit in new Action<WeaponRangeFrame>[]{x=>x.Now=double.NaN,x=>x.SinceShot=-1,x=>x.Weapons[0].Loaded=-1,x=>x.Weapons[0].LongScore=double.NaN,
            x=>x.Contacts[0].Source="hidden-live",x=>x.Contacts[0].Age=double.PositiveInfinity,x=>x.Contacts[0].Distance=-1,x=>x.Weapons[1].Id=x.Weapons[0].Id})
        {p=new();f=F();edit(f);Check(p.Evaluate(f,c)=="","invalid evidence declined");}
        // Threshold matrix and simultaneous near contacts; include exact boundaries and reversed slot order.
        foreach(double distance in new[]{44.99,45,79.99,80,100})foreach(double age in new[]{0,2,2.001,20,21})foreach(bool near in new[]{false,true})foreach(bool reverse in new[]{false,true})
        {
            p=new();f=F();f.Contacts[0].Distance=distance;f.Contacts[0].Age=age;
            if(near)f.Contacts=f.Contacts.Concat(new[]{new WeaponRangeContact{Id="close",Distance=10,Age=1}}).ToArray();
            if(reverse)Array.Reverse(f.Weapons);
            Check((Stable()=="dmr")==(!near&&distance>=80&&age<=2),"range/age/multiple threats/slot matrix");
        }
        // One physical switch, W03 readiness-based hold and retry ownership, W05 off does not disable W03.
        var arb=new WeaponReturnPolicy();var ac=new WeaponReturnConfig();
        WeaponReturnGun G(string id,string slot)=>new(){Id=id,Slot=slot,Usable=true,Loaded=30,Magazine=30,Capacity=31,Resource="magazine",Reserve=30};
        var af=new WeaponReturnFrame{Now=10,Ready=true,Current=G("rifle","FirstPrimaryWeapon"),Main=G("rifle","FirstPrimaryWeapon"),Other=G("dmr","SecondPrimaryWeapon")};
        arb.Observe(10,"rifle","FirstPrimaryWeapon",true,ac);Check(arb.Evaluate(af,af.Other,"W05",ac),"W05 shares arbiter");arb.Request(10);arb.Result(true,10,ac);
        Check(!arb.Evaluate(af,af.Main,"native-return",ac),"one pending request");arb.Observe(11,"dmr","SecondPrimaryWeapon",false,ac);arb.Observe(12,"dmr","SecondPrimaryWeapon",true,ac);
        af.Current=af.Other;af.Now=14.99;Check(!arb.Evaluate(af,af.Main,"W05",ac),"three seconds start on readiness");af.Now=15;Check(arb.Evaluate(af,af.Main,"W05",ac),"hold completed");
        arb.Request(15);arb.Result(false,15,ac);af.Now=17.99;Check(!arb.Evaluate(af,af.Main,"W05",ac),"refusal cooldown");af.Now=18;Check(arb.Evaluate(af,af.Main,"W05",ac),"retry after cooldown");
        using(var pe=new PEReader(File.OpenRead("Client/bin/Release/netstandard2.1/COMET.dll")))
        {
            var md=pe.GetMetadataReader();var type=md.TypeDefinitions.Select(md.GetTypeDefinition).Single(t=>md.GetString(t.Name)=="Plugin");
            MethodDefinition M(string name)=>type.GetMethods().Select(md.GetMethodDefinition).Single(m=>md.GetString(m.Name)==name);
            foreach(var gate in new[]{"IsSupportedAi","get_Authority","Ghost","get_BotActive","get_IsDead","get_GameEnding","get_SAINLayersActive"})Check(ReloadTimingTests.Calls(pe,md,M("WeaponRangeScope"),gate)==1,"compiled scope "+gate);
            foreach(var method in new[]{"WeaponRangeFrameFor","ReadRangeGun","KeepRangeChoice","SampleWeaponRange"})
                foreach(var forbidden in new[]{"get_RealDistance","get_EnemyPosition","get_CurrentWeaponInfo","get_BulletCount","CanReload","BotCancelReload","SetSlotItem","AddAmmoToMagazines","FastForward"})
                    Check(ReloadTimingTests.Calls(pe,md,M(method),forbidden)==0,"no hidden info, stale held optics or forced animation "+method+forbidden);
            Check(ReloadTimingTests.Calls(pe,md,M("SampleWeaponRange"),"NativeWeaponRequest")==1,"single shared native request");
            Check(ReloadTimingTests.Calls(pe,md,M("AuthorizeWeaponSwitch"),"KeepRangeChoice")==1,"native return cannot undo choice");
            Check(ReloadTimingTests.Calls(pe,md,M("ReadRangeGun"),"get_Mods")==1,"actual attachments per weapon");
        }
        File.WriteAllText("artifacts/w05-wire-traces.json",JsonSerializer.Serialize(wire));
        var active=wire.First(t=>t.GetProperty("state").GetString()=="drawing");var node=JsonSerializer.Deserialize<JsonObject>(active.GetRawText())!;node["action"]="StandAndShoot";
        File.WriteAllText("artifacts/w05-weapon-range.json",JsonSerializer.Serialize(new{botInstance="instance",weaponRange=node,weaponRangeConfig=c}));
        Console.WriteLine($"W05: {n} assertions passed.");return n;
    }
    private static WeaponRangeGun Make(string id,string slot,string type,string optic,bool auto)
    {var g=new WeaponRangeGun{Id=id,Slot=slot,Name=id,Class=type,Optic=optic,Automatic=auto,Loaded=30,Reserve=60,Usable=true,Fingerprint=id+optic+auto};WeaponRangeModel.Classify(g);return g;}
}
