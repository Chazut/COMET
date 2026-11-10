using Comet.Core;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

static class WeaponReturnTests
{
    public static int Run()
    {
        int n=0;void Check(bool v,string why){n++;if(!v)throw new Exception("W03: "+why);}
        void Throws(Action a,string why){try{a();}catch(ArgumentException){n++;return;}throw new Exception("W03: "+why);}
        var c=new WeaponReturnConfig();c.Validate();
        Check(c.Enabled&&c.MinimumHold==3&&c.CalmDelay==6&&c.DistanceMargin==15&&c.PistolReturnDelay==5&&c.RetryDelay==3,"approved defaults");
        foreach(var prop in typeof(WeaponReturnConfig).GetProperties().Where(p=>p.PropertyType==typeof(double)))
            foreach(var bad in new[]{double.NaN,double.PositiveInfinity,-1,1000}){var v=new WeaponReturnConfig();prop.SetValue(v,bad);Throws(v.Validate,prop.Name+" bounds");}
        Throws(()=>new CometConfig{WeaponReturn=null!}.Validate(),"null settings");
        var old=JsonSerializer.Deserialize<CometConfig>("{}");Check(old!.WeaponReturn.CalmDelay==6,"old configs get defaults");
        var saved=JsonSerializer.Deserialize<CometConfig>(JsonSerializer.Serialize(new CometConfig{WeaponReturn=new(){MinimumHold=2,CalmDelay=8,DistanceMargin=20}}));
        saved!.Validate();Check(saved.WeaponReturn.MinimumHold==2&&saved.WeaponReturn.CalmDelay==8&&saved.WeaponReturn.DistanceMargin==20,"server config roundtrip");
        WeaponReturnGun G(string id,string slot,int loaded,int reserve=0)=>new(){Id=id,Slot=slot,Loaded=loaded,Chamber=loaded>0?1:0,Magazine=Math.Max(0,loaded-1),Capacity=31,Reserve=reserve,Resource=reserve>0?"magazine":"none",Usable=true};
        WeaponReturnFrame F(double now=10)=>new(){Now=now,Current=G("pistol","Holster",8,15),Pistol=G("pistol","Holster",8,15),Main=G("rifle","FirstPrimaryWeapon",25,30),
            Other=G("second","SecondPrimaryWeapon",12,20),Ready=true,Target="opponent",Source="personal-vision",Distance=8,ContactAge=.2,LastThreatAt=now-.2};
        var wire=new List<JsonElement>();WeaponReturnPolicy policy=new();
        void Save(){wire.Add(JsonSerializer.Deserialize<JsonElement>(Newtonsoft.Json.JsonConvert.SerializeObject(policy.Trace,Comet.Client.Diagnostics.Json)));}
        void Observe(WeaponReturnFrame f)=>policy.Observe(f.Now,f.Current.Id,f.Current.Slot,f.Ready,c);
        var f=F();Observe(f);
        Check(policy.Recommend(f,c)=="","retain pistol during nearby contact");
        foreach(var origin in new[]{"native-return","native-toggle","native-slot","return-policy"})
            Check(!policy.Evaluate(f,f.Main,origin,c)&&policy.Trace!.Reason=="weapon-keep-pistol-under-pressure","every return route respects close pressure "+origin);
        Save();
        // Quiet time survives a goal change and cannot be claimed merely by deleting the current enemy.
        f=F(12);f.Target="";f.ContactAge=999;f.LastThreatAt=-1;
        Check(policy.Recommend(f,c)=="","two seconds unseen is not safe");
        f.Now=15;f.Target="other";f.LastThreatAt=14.8;Check(policy.Recommend(f,c)=="","different recent contact retains danger");
        f.Now=20;f.Target="";f.LastThreatAt=-1;Check(policy.Recommend(f,c)=="","memory persists across target loss");
        f.Now=20.8;Check(policy.Recommend(f,c)=="rifle","six quiet seconds allow return");
        Check(policy.Evaluate(f,f.Main,"return-policy",c),"calm loaded main allowed");Save();
        policy.Request(f.Now);Save();Check(policy.Active&&policy.Trace!.Requested&&!policy.Trace.Accepted&&!policy.Trace.Ready,"request is not native acceptance");
        policy.Result(true,f.Now,c);Save();Check(policy.Active&&!policy.Trace!.InHands,"acceptance is not hand change");
        Check(!policy.Evaluate(f,f.Other,"native-slot",c)&&policy.Trace!.To=="rifle","one owner preserves original target");
        policy.Observe(21,"","",false,c);Check(policy.Active&&!policy.Trace!.InHands,"empty hands during native animation allowed");
        policy.Observe(21.1,"pistol","Holster",true,c);Check(policy.Active&&!policy.Trace!.Ready,"old ready weapon not mistaken for switch");
        policy.Observe(21.4,"rifle","FirstPrimaryWeapon",false,c);Save();Check(policy.Trace!.InHands&&!policy.Trace.Ready,"observed drawing");
        Check(policy.PistolBlockedFor(21.4)==0,"post-return grace has not begun before actual readiness");
        policy.Observe(22,"rifle","FirstPrimaryWeapon",true,c);Save();Check(!policy.Active&&policy.Trace!.Ready&&policy.PistolBlockedFor(22)==5,"readiness starts return grace");
        // Holding period begins at readiness, not at selection or animation request.
        f=F(24.99);f.Current=f.Main;f.Main=G("rifle","FirstPrimaryWeapon",25,30);f.Target="";f.LastThreatAt=-1;f.Far=true;
        Check(!policy.Evaluate(f,f.Other,"native-slot",c)&&policy.Trace!.Reason=="weapon-minimum-hold","hold lasts three ready seconds");
        f.Now=25;Check(policy.Evaluate(f,f.Other,"native-slot",c),"hold exact boundary");
        // Even an empty primary gets time to reload after a calm return, except for a genuine new emergency.
        f.Now=25;f.Current=G("rifle","FirstPrimaryWeapon",0,30);f.Main=f.Current;f.ContactAge=999;f.Incoming=false;f.Target="";
        Check(!policy.Evaluate(f,f.Pistol,"native-slot",c)&&policy.Trace!.Reason=="weapon-let-primary-reload","no immediate empty-primary/pistol loop during safe reload grace");Save();
        f.Incoming=true;Check(policy.Evaluate(f,f.Pistol,"W02",c),"new incoming danger permits emergency pistol despite return grace");
        f.Incoming=false;f.Reloading=true;Check(!policy.Evaluate(f,f.Pistol,"W02",c),"never interrupt native reload even on empty gun");
        f.Reloading=false;f.Now=27;Check(policy.Evaluate(f,f.Pistol,"W02",c),"return delay ends without extending");
        // Empty or unusable current weapon: prefer a loaded alternative, independent of slot and range.
        foreach(var broken in new[]{false,true})foreach(var slot in new[]{"FirstPrimaryWeapon","SecondPrimaryWeapon","Holster"})
        {
            policy=new();f=F();f.Current=G("held",slot,broken?12:0,30);f.Current.Usable=!broken;Observe(f);
            Check(policy.Recommend(f,c)=="rifle","loaded primary fallback "+slot+broken);
            Check(policy.Evaluate(f,f.Main,"native-toggle",c),"emergency bypasses ordinary hold");
            f.Main=G("rifle","FirstPrimaryWeapon",0,0);Check(policy.Recommend(f,c)=="second","skip unusable empty main");
            f.Other=G("second","SecondPrimaryWeapon",0,0);Check(policy.Recommend(f,c)=="pistol","loaded holster is fallback if primaries empty");
        }
        policy=new();f=F();f.Current=f.Pistol=G("pistol","Holster",0,15);f.Main=G("rifle","FirstPrimaryWeapon",0,30);f.Other=new();Observe(f);
        Check(policy.Recommend(f,c)=="","reload current empty pistol if no gun is loaded");
        Check(!policy.Evaluate(f,f.Main,"native-return",c)&&policy.Trace!.Reason=="weapon-reload-current","do not swap two empty reloadable guns");
        f.Current.Reserve=0;f.Current.Resource="none";Check(policy.Recommend(f,c)=="rifle","reloadable main is useful if current cannot be reloaded");
        Check(policy.Evaluate(f,f.Main,"native-return",c),"no rounds anywhere still permits useful reload fallback");
        f.Main.Reserve=0;f.Main.Resource="none";Check(policy.Recommend(f,c)=="","no infinite switch when everything is empty");
        Check(!policy.Evaluate(f,f.Main,"native-return",c)&&policy.Trace!.Reason=="weapon-target-unusable","never return to gun with no ammo or reserves");Save();
        // Far contact has already been checked using recent personal evidence in the adapter.
        policy=new();f=F();f.Far=true;f.Distance=46;Observe(f);Check(policy.Evaluate(f,f.Main,"native-return",c),"loaded rifle for far known contact");
        f.Main=G("rifle","FirstPrimaryWeapon",0,30);Check(!policy.Evaluate(f,f.Main,"native-return",c),"far alone not a safe return to empty primary");
        f.Protected=true;Check(policy.Evaluate(f,f.Main,"native-return",c),"reached cover supports return to reload");
        f.Main.Usable=false;Check(!policy.Evaluate(f,f.Main,"native-return",c),"broken return target rejected despite ammo");
        // Native random/distance selectors cannot steal the nearly empty but usable primary for a pistol.
        policy=new();f=F();f.Current=f.Main=G("rifle","FirstPrimaryWeapon",1,30);Observe(f);
        Check(!policy.Evaluate(f,f.Pistol,"native-slot",c),"native selection does not duplicate W02");
        Check(policy.Evaluate(f,f.Pistol,"W02",c),"W02 remains the near-empty arbitration authority");
        Check(!policy.Evaluate(f,f.Other,"native-toggle",c),"blind toggle cannot replace loaded gun");
        f.Other=G("second","SecondPrimaryWeapon",0,30);f.Now=30;f.Target="";f.LastThreatAt=-1;
        Check(!policy.Evaluate(f,f.Other,"native-slot",c),"no elective loaded rifle to empty secondary");
        // A refusal suppresses the same target only, not emergency access to a different loaded gun.
        policy=new();f=F();f.Far=true;Observe(f);policy.Evaluate(f,f.Main,"native-return",c);policy.Request(10);policy.Result(false,10,c);Save();
        Check(!policy.Active&&!policy.Trace!.Accepted,"refusal frees the pending owner");
        for(double now=10.1;now<13;now+=.1){f.Now=now;Check(!policy.Evaluate(f,f.Main,"native-return",c)&&policy.Trace!.Reason=="weapon-refusal-delay","refusal does not extend itself");}
        f.Now=11;f.Current=G("pistol","Holster",0);Check(policy.Evaluate(f,f.Other,"native-slot",c),"another loaded gun remains available during failed-target cooldown");
        f.Now=13;Check(policy.Evaluate(f,f.Main,"native-return",c),"refusal exact expiry");
        foreach(var mode in new[]{"timeout","external","source-return","ghost","clock","nan"})
        {
            policy=new();f=F();f.Far=true;Observe(f);policy.Evaluate(f,f.Main,"native-return",c);policy.Request(10);policy.Result(true,10,c);
            if(mode=="external")policy.Observe(11,"new-looted-gun","SecondPrimaryWeapon",true,c);
            else if(mode=="source-return"){policy.Observe(11,"rifle","FirstPrimaryWeapon",false,c);policy.Observe(11.1,"pistol","Holster",true,c);}
            else if(mode=="ghost")policy.Clear("weapon-ghost");
            else policy.Observe(mode=="clock"?9:mode=="nan"?double.NaN:15,"pistol","Holster",false,c);
            Check(!policy.Active&&!policy.Trace!.Ready&&policy.Trace.ValidFor==0,"no fabricated completion on "+mode);Save();
        }
        foreach(var mode in new[]{"busy","reload","not-ready","blocked","truncated"})
        {
            policy=new();f=F();Observe(f);
            if(mode=="busy")f.Busy=true;if(mode=="reload")f.Reloading=true;if(mode=="not-ready")f.Ready=false;if(mode=="blocked")f.Block="inventory-unknown";if(mode=="truncated")f.Truncated=true;
            Check(!policy.Evaluate(f,f.Main,"native-return",c),"conservative "+mode);
        }
        policy=new();f=F();Observe(f);Check(!policy.Evaluate(f,f.Main,"native-return",new(){Enabled=false})&&policy.Trace!.Choice=="Native","disabled yields");Save();
        foreach(var change in new Action<WeaponReturnFrame>[] {x=>x.Now=double.NaN,x=>x.ContactAge=-1,x=>x.Distance=double.PositiveInfinity,x=>x.Main.Loaded=99,x=>x.Main.Reserve=-1,x=>x.Main.Chamber=-1})
        {policy=new();f=F();change(f);Check(!policy.Evaluate(f,f.Main,"native-return",c),"invalid evidence rejected");}
        // Deterministic repeated polls cannot manufacture returns to an empty primary, even with two caller paths.
        for(int rounds=1;rounds<=17;rounds++)
        {
            policy=new();f=F();f.Current=f.Pistol=G("pistol","Holster",rounds,0);f.Main=G("rifle","FirstPrimaryWeapon",0,0);f.Other=new();Observe(f);
            for(int step=0;step<100;step++){f.Now=10+step*.25;f.LastThreatAt=-1;f.Target="";f.ContactAge=999;
                Check(policy.Recommend(f,c)=="","no suggested empty-primary loop");
                Check(!policy.Evaluate(f,f.Main,step%2==0?"native-return":"native-slot",c),"both callers blocked for unusable return target");}
        }
        using(var pe=new PEReader(File.OpenRead("Client/bin/Release/netstandard2.1/COMET.dll")))
        {
            var md=pe.GetMetadataReader();var type=md.TypeDefinitions.Select(md.GetTypeDefinition).Single(t=>md.GetString(t.Name)=="Plugin");
            MethodDefinition M(string name)=>type.GetMethods().Select(md.GetMethodDefinition).Single(m=>md.GetString(m.Name)==name);
            foreach(var gate in new[]{"IsSupportedAi","get_Authority","Ghost","get_BotActive","get_IsDead","get_GameEnding","get_SAINLayersActive"})Check(ReloadTimingTests.Calls(pe,md,M("WeaponReturnScope"),gate)==1,"compiled scope "+gate);
            Check(ReloadTimingTests.Calls(pe,md,M("PatchWeaponReturn"),"Patch")==5,"both main-return entry points and three arbitration hooks installed");
            foreach(var method in new[]{"WeaponReturnFrameFor","ReadReturnGun","AuthorizeWeaponSwitch","ObserveWeaponReturn","NativeWeaponRequest"})
                foreach(var forbidden in new[]{"get_RealDistance","get_EnemyPosition","get_BulletCount","CanReload","BotCancelReload","SetSlotItem","AddAmmoToMagazines","FastForward"})
                    Check(ReloadTimingTests.Calls(pe,md,M(method),forbidden)==0,"no hidden info, cached ammo, mutating reload query or forced animation "+method+forbidden);
            Check(ReloadTimingTests.Calls(pe,md,M("ReadReturnGun"),"ReloadResources")==1,"actual compatible resources reused");
            Check(ReloadTimingTests.Calls(pe,md,M("ReadReturnGun"),"get_ChamberAmmoCount")==1,"each gun chamber counted");
            Check(ReloadTimingTests.Calls(pe,md,M("NativeWeaponRequest"),"TryChangeToSlot")==1,"native weapon route preserved");
            Check(ReloadTimingTests.Calls(pe,md,M("ObserveWeaponReturn"),"get_HandsController")==1,"actual hands determine progress");
            Check(ReloadTimingTests.Calls(pe,md,M("WeaponHandsReady"),"CanStartReload")==1,"native ready operation checked");
        }
        // Real C# event serialization is reused by RR tests and the synthetic visual preview.
        Directory.CreateDirectory("artifacts");File.WriteAllText("artifacts/w03-wire-traces.json",JsonSerializer.Serialize(wire));
        var active=wire.First(t=>t.GetProperty("state").GetString()=="drawing");var node=JsonSerializer.Deserialize<JsonObject>(active.GetRawText())!;node["action"]="StandAndShoot";
        File.WriteAllText("artifacts/w03-weapon-return.json",JsonSerializer.Serialize(new{botInstance="instance",weaponReturn=node,weaponReturnConfig=c}));
        Console.WriteLine($"W03: {n} assertions passed.");return n;
    }
}
