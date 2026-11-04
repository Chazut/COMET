using Comet.Core;
using System.Text.Json;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

static class WeaponSyncTests
{
    public static int Run()
    {
        int n=0;void Check(bool v,string why){n++;if(!v)throw new Exception("W06: "+why);}
        void Throws(Action a,string why){try{a();}catch(ArgumentException){n++;return;}throw new Exception("W06: "+why);}
        var c=new WeaponSyncConfig();c.Validate();Check(c.Enabled&&c.BackupSeconds==5,"approved defaults");
        foreach(double bad in new[]{double.NaN,double.PositiveInfinity,0,30.01})Throws(()=>new WeaponSyncConfig{BackupSeconds=bad}.Validate(),"invalid interval");
        Throws(()=>new CometConfig{WeaponSync=null!}.Validate(),"null section");
        var config=JsonSerializer.Deserialize<CometConfig>("{}")!;Check(config.WeaponSync.BackupSeconds==5,"migration default");
        config.WeaponSync=new(){Enabled=false,BackupSeconds=12};config=JsonSerializer.Deserialize<CometConfig>(JsonSerializer.Serialize(config))!;
        config.Validate();Check(!config.WeaponSync.Enabled&&config.WeaponSync.BackupSeconds==12,"server persistence");
        var schedule=new WeaponSyncSchedule();Check(schedule.Due(10),"first observation");schedule.Complete(10,5,schedule.Revision);
        Check(!schedule.Due(14.999)&&schedule.Due(15)&&schedule.Trigger=="backup-check","backup exact boundary");
        schedule.Complete(15,5,schedule.Revision);schedule.Mark(16,"looted");schedule.Mark(16.1,"attachments");schedule.Mark(16.24,"inventory");
        Check(!schedule.Due(16.249)&&schedule.Due(16.25),"burst coalesced at first event, no starvation");
        int rev=schedule.Revision;schedule.Mark(16.26,"newer-event");schedule.Complete(16.3,5,rev);
        Check(schedule.Dirty&&!schedule.Due(16.54)&&schedule.Due(16.55),"event arriving during work not lost");
        schedule.Defer(17);Check(!schedule.Due(17.249)&&schedule.Due(17.25),"busy animation defers without discarding");
        schedule.Complete(18,5,schedule.Revision);schedule.Reset();Check(schedule.Due(18.1),"ghost/wake reset forces fresh read");
        schedule.Complete(18.1,5,schedule.Revision);Check(schedule.Due(1),"clock rollback cannot strand checks");
        Check(!schedule.Due(double.NaN)&&!schedule.Due(-1),"invalid clock");
        WeaponSyncFrame F()=>new(){Safe=true,NativeCurrentMatches=true,SainCurrentMatches=true,Held="rifle",NativeCurrent="rifle",SainCurrent="rifle",Slots=new[]{
            new WeaponSyncSlot{Slot="FirstPrimaryWeapon",Actual="rifle",Name="Rifle",NativeBefore="rifle",SainBefore="rifle",NativePresent=true,SainPresent=true,NativeMatches=true,SainMatches=true,SelectorMatches=true},
            new WeaponSyncSlot{Slot="SecondPrimaryWeapon",NativeMatches=true,SainMatches=true,SelectorMatches=true},
            new WeaponSyncSlot{Slot="Holster",NativeMatches=true,SainMatches=true,SelectorMatches=true}}};
        var f=F();Check(WeaponSyncPlan.Prepare(f)&&!WeaponSyncPlan.NeedsWork(f)&&WeaponSyncPlan.Matches(f),"unchanged equipment costs no repair");
        var wire=new List<JsonElement>();
        void Save(WeaponSyncTrace t)=>wire.Add(JsonSerializer.Deserialize<JsonElement>(Newtonsoft.Json.JsonConvert.SerializeObject(t,Comet.Client.Diagnostics.Json)));
        // Every slot supports add/replace/remove, even a weapon never drawn. References, not equal IDs, decide replacement.
        foreach(int i in new[]{0,1,2})foreach(string op in new[]{"add","replace","remove","refresh","same-id-new-object"})
        {
            f=F();var s=f.Slots[i];s.Actual="new";s.NativeBefore=s.SainBefore="old";s.NativePresent=s.SainPresent=true;s.NativeMatches=s.SainMatches=false;
            if(op=="add"){s.NativePresent=s.SainPresent=false;s.NativeBefore=s.SainBefore="";}
            if(op=="remove")s.Actual="";
            if(op=="refresh"){s.NativeMatches=s.SainMatches=true;s.NativeBefore=s.SainBefore=s.Actual;s.ContentsChanged=true;}
            if(op=="same-id-new-object")s.NativeBefore=s.SainBefore=s.Actual;
            Check(WeaponSyncPlan.Prepare(f)&&WeaponSyncPlan.NeedsWork(f),"plan "+i+op);
            Check(s.NativeOperation==(op=="refresh"?"keep":op=="same-id-new-object"?"replace":op),"native operation "+op);
            Check(s.SainOperation==(op=="same-id-new-object"?"replace":op),"SAIN operation "+op);
            f.Safe=false;f.Block="sync-native-operation-pending";Check(!WeaponSyncPlan.Prepare(f),"pending reload never authorizes mutation");
        }
        f=F();f.Slots[0].NativeMatches=false;f.NativeCurrentMatches=false;f.NativeCurrent="old-rifle";f.Slots[0].NativeBefore="old-rifle";
        f.Slots[1]=new(){Slot="SecondPrimaryWeapon",Actual="looted-dmr",Name="Looted DMR",SelectorMatches=false,ContentsChanged=true};
        f.Slots[2]=new(){Slot="Holster",NativePresent=true,SainPresent=true,NativeBefore="discarded-pistol",SainBefore="discarded-pistol",SelectorMatches=false};
        Check(WeaponSyncPlan.Prepare(f),"mixed repair plan");
        var trace=new WeaponSyncTrace{State="deferred",Reason="sync-native-operation-pending",Trigger="inventory-changed",Held=f.Held,NativeCurrentBefore=f.NativeCurrent,SainCurrentBefore=f.SainCurrent,Slots=f.Slots,Action="StandAndShoot"};Save(trace);
        // Simulate readback from the three cache interfaces, keeping pending/verified distinct on the wire.
        trace.State="applied";trace.Reason="sync-verification-pending";trace.Changed=trace.ResourcesInvalidated=true;Save(trace);
        foreach(var s in trace.Slots){s.NativeAfter=s.SainAfter=s.Actual;}
        trace.NativeCurrentAfter=trace.SainCurrentAfter=trace.Held;trace.State="verified";trace.Reason="sync-equipment-verified";trace.Verified=true;trace.SelectorUpdated=trace.NativeCurrentUpdated=true;Save(trace);
        var fixture=wire[^1];trace.State="ended";trace.ValidFor=0;Save(trace);Save(new(){State="failed",Reason="sync-adapter-error"});
        foreach(var flag in new[]{"native","sain","selector","current-native","current-sain","resources"})
        {
            f=F();switch(flag){case "native":f.Slots[0].NativeMatches=false;break;case "sain":f.Slots[0].SainMatches=false;break;case "selector":f.Slots[0].SelectorMatches=false;break;
                case "current-native":f.NativeCurrentMatches=false;break;case "current-sain":f.SainCurrentMatches=false;break;case "resources":f.ResourcesChanged=true;break;}
            Check(WeaponSyncPlan.Prepare(f)&&WeaponSyncPlan.NeedsWork(f),"independent drift "+flag);
            Check(WeaponSyncPlan.Matches(f)==(flag=="resources"),"identity readback "+flag);
        }
        f=F();f.Slots[1].Actual=f.Slots[0].Actual;Check(!WeaponSyncPlan.Prepare(f),"duplicate gun across slots rejected");
        f=F();f.Slots[1].Slot=f.Slots[0].Slot;Check(!WeaponSyncPlan.Prepare(f),"duplicate slot rejected");
        f=F();f.Slots[1].Slot="Scabbard";Check(!WeaponSyncPlan.Prepare(f),"melee cache outside managed slots");
        using(var pe=new PEReader(File.OpenRead("Client/bin/Release/netstandard2.1/COMET.dll")))
        {
            var md=pe.GetMetadataReader();var type=md.TypeDefinitions.Select(md.GetTypeDefinition).Single(t=>md.GetString(t.Name)=="Plugin");
            MethodDefinition M(string name)=>type.GetMethods().Select(md.GetMethodDefinition).Single(m=>md.GetString(m.Name)==name);
            int Calls(string name,string call)=>ReloadTimingTests.Calls(pe,md,M(name),call);
            foreach(string gate in new[]{"IsPmcAi","get_Authority","Ghost","get_BotActive","get_IsDead","get_GameEnding"})Check(Calls("WeaponSyncScope",gate)==1,"scope "+gate);
            Check(Calls("WeaponSyncScope","get_SAINLayersActive")==0,"maintenance also during noncombat looting");
            Check(Calls("EnsureWeaponSyncTracked","MarkWeaponSync")==1,"enroll before first combat and after delayed config");
            foreach(string call in new[]{"IsInReloadOperation","IsInInteraction","get_IsChanging","get_Reloading","get_Using"})Check(Calls("SyncBlock",call)>0,"native transition protected "+call);
            foreach(string method in new[]{"ReadWeaponSync","ApplyWeaponSync","InvalidateSyncedEquipment","SampleWeaponSync","RefreshSainWeapon"})
                foreach(string forbidden in new[]{"SetSlotItem","AddAmmoToMagazines","BotCancelReload","FastForward","CanReload","TryChangeWeapon","ManualUpdate"})
                    Check(Calls(method,forbidden)==0,"no inventory/animation intervention "+method+forbidden);
            Check(Calls("ApplyWeaponSync","SyncBlock")==1&&Calls("ApplyWeaponSync","WeaponSyncScope")==1,"precommit guards rechecked");
            Check(Calls("SampleWeaponSync","ReadWeaponSync")==2,"independent post-apply readback");
            Check(Calls("SampleWeaponSync","TryBegin")==1&&Calls("SampleWeaponSync","Charge")==1,"shared frame budget");
            Check(Calls("EquipmentNotification","MarkWeaponSync")==1&&Calls("EquipmentNotification","InvalidateLoadout")==1,"A08 and W06 independently notified");
            Check(Calls("EndWeaponSync","Reset")==1&&Calls("EndWeaponSync","Clear")==2,"release gun references on lifecycle exit");
        }
        File.WriteAllText("artifacts/w06-wire-traces.json",JsonSerializer.Serialize(wire));
        File.WriteAllText("artifacts/w06-weapon-sync.json",JsonSerializer.Serialize(new{botInstance="instance",weaponSync=fixture,weaponSyncConfig=c}));
        Console.WriteLine($"W06: {n} assertions passed.");return n;
    }
}
