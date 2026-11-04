using Comet.Core;
using Comet.Server;
using System.Text.Json;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

internal static class DoorClearTests
{
    public static int Run()
    {
        int n=0;void Check(bool ok,string why){n++;if(!ok)throw new Exception("D07: "+why);}
        void Invalid(Action action){try{action();}catch(ArgumentException){n++;return;}throw new Exception("D07 accepted invalid config");}
        var config=new CometConfig();var c=config.DoorRoomClear;config.Validate();
        Check(c.Enabled&&c.AllowFeint&&c.ContactLifetime==10&&c.GrenadePreference==50&&c.MinimumAmmo==50&&c.MinimumStamina==35,"approved defaults");
        Check(c.DepthMin==2&&c.DepthMax==3&&c.BlastPauseMin==.3&&c.BlastPauseMax==.7&&c.TargetCooldown==60,"approved distances and timing");
        Invalid(()=>new CometConfig{DoorRoomClear=null!}.Validate());
        foreach(var x in new[]{-1d,101,double.NaN,double.PositiveInfinity}){
            Invalid(()=>new DoorRoomClearConfig{GrenadePreference=x}.Validate());
            var bad=new DoorRoomClearConfig();bad.Chances["Normal"]=x;Invalid(bad.Validate);
        }
        Invalid(()=>new DoorRoomClearConfig{DepthMin=3,DepthMax=2}.Validate());
        Invalid(()=>new DoorRoomClearConfig{BlastPauseMin=1,BlastPauseMax=.3}.Validate());
        Invalid(()=>new DoorRoomClearConfig{Chances=new()}.Validate());
        Check(JsonSerializer.Deserialize<CometConfig>("{\"DoorFakeMedical\":{\"Preference\":70}}")!.DoorRoomClear.Chances["Chad"]==30,"old config receives D07 defaults");
        var dir=Path.Combine(Path.GetTempPath(),"comet-d07-"+Guid.NewGuid().ToString("N"));
        try{
            var store=new ConfigStore(Path.Combine(dir,"config.json"));var d=store.Read();d.DoorRoomClear.GrenadePreference=65;d.DoorRoomClear.AllowFeint=false;
            store.Save(d,d.Revision);var saved=JsonSerializer.Deserialize<CometConfig>(store.ForGame())!;
            Check(saved.DoorRoomClear.GrenadePreference==65&&!saved.DoorRoomClear.AllowFeint&&saved.DoorFakeMedical.Enabled,"server owns D07 and preserves other fiches");
            d=store.Read();var rev=d.Revision;d.DoorRoomClear.ContactLifetime=100;Invalid(()=>store.Save(d,rev));Check(store.Read().Revision==rev,"invalid changes not persisted");
        }finally{Directory.Delete(dir,true);}
        DoorRoomClear Start(double now=100){var p=new DoorRoomClear();Check(p.Begin("door","enemy","heard","Chad",new(0,0,5),1,now,0,c),"selected");return p;}
        DoorRoomClear Ready(){var p=Start();p.Opening(100);p.Opened(101);p.Route(new(0,0,2),true);p.Ready(101);return p;}
        DoorRoomClear Thrown(){var p=Ready();Check(p.Choose(true,true,"","",0,.5,101,c),"grenade variant");p.ThrowRequest("grenade",3.5,8,101);p.ThrowResult(true,101);return p;}
        foreach(var chance in c.Chances){int yes=0;for(int i=0;i<100;i++)if(new DoorRoomClear().Begin("d","t","heard",chance.Key,new(0,0,3),1,100,i/100d,c))yes++;
            Check(yes==chance.Value,"one personality draw: "+chance.Key);}
        var declined=Start();declined.End("done");Check(!declined.Begin("other-door","enemy","heard","Chad",new(0,0,9),1,110,0,c),"no reroll at another door against same contact");
        Check(!declined.Begin("door","enemy","heard","Chad",new(0,0,5),61,160,0,c),"expired contact refused");
        Check(declined.Begin("door","enemy","heard","Chad",new(0,0,5),1,161,0,c),"cooldown plus new evidence qualifies");
        var rc=new DoorRoomClearConfig{ContactLifetime=15,TargetCooldown=30};var reuse=new DoorRoomClear();reuse.Begin("d","t","heard","Chad",new(0,0,4),0,100,.99,rc);
        Check(!reuse.Begin("d","t","heard","Chad",new(0,0,4),0,100,0,rc),"declined roll consumes opportunity");
        foreach(double v in new[]{-1d,double.NaN,double.PositiveInfinity,1})Check(!new DoorRoomClear().Begin("d","t","heard","Chad",new(0,0,3),1,100,v,c),"invalid roll");
        var bounds=new DoorRoomClear();for(int i=0;i<32;i++){bounds.Begin("d","t"+i,"heard","Chad",new(0,0,3),1,100,0,c);bounds.End("done");}
        Check(!bounds.Begin("d","overflow","heard","Chad",new(0,0,3),1,200,0,c),"bounded contact history");bounds.Clear();Check(bounds.Begin("d","new","heard","Chad",new(0,0,3),1,200,0,c),"raid cleanup");
        var feint=Start();feint.Opened(101);feint.FeintStart("heal",101);feint.FeintStart("grenade",101.2);Check(feint.Trace!.Feint=="heal","only one feint");feint.FeintEnd(false,"consumed",102);Check(!feint.Active,"medical use/failure aborts entry");
        feint=Start();feint.Opened(101);feint.FeintStart("grenade",101);feint.FeintEnd(true,"weapon-ready",102);Check(feint.Trace!.State=="ready","successful feint leads to fresh variant evaluation");
        for(int i=0;i<100;i++){
            var p=Ready();p.Choose(true,true,"","",i/100d,.5,101,c);Check(p.Trace!.Variant==(i<50?"grenade":"direct"),"conditional grenade mix");
            p=Ready();p.Choose(false,true,"unsafe","",i/100d,0,101,c);Check(p.Trace!.Variant=="grenade","only safe grenade");
            p=Ready();p.Choose(true,false,"","no-arc",i/100d,0,101,c);Check(p.Trace!.Variant=="direct","only safe direct entry");
        }
        var none=Ready();Check(!none.Choose(false,false,"occupied","ally",0,0,101,c)&&!none.Active,"no safe alternative stays outside");
        var missing=Thrown();missing.Tick(107,true);Check(!missing.Active&&!missing.Trace!.ExplosionConfirmed&&missing.Trace.GrenadeOutcome=="unknown","accepted throw is not a launched projectile");
        var wrong=Thrown();wrong.Projectile("foreign",3,102);Check(!wrong.Trace!.ProjectileObserved,"wrong item ignored");wrong.Explosion(3,102);Check(!wrong.Trace.ExplosionConfirmed,"explosion without projectile ignored");
        wrong.Projectile("grenade",-234,102);wrong.Explosion(-233,104);Check(!wrong.Trace.ExplosionConfirmed,"wrong projectile ignored incl signed IDs");
        wrong.MissingProjectile();wrong.Explosion(-234,105);Check(!wrong.Active&&!wrong.Trace.ExplosionConfirmed,"disappearance not explosion, late callback cannot resume");
        var wait=Thrown();wait.Projectile("grenade",2,102);wait.Tick(106,true);Check(wait.Trace!.State=="waiting-blast","estimated fuse never releases entry");
        wait.Tick(111,true);Check(!wait.Active&&!wait.Trace.ExplosionConfirmed&&wait.Trace.GrenadeOutcome=="unknown","missing explosion is failure");
        var refused=Ready();refused.Choose(true,true,"","",0,0,101,c);refused.ThrowRequest("grenade",3,8,101);refused.ThrowResult(false,101);Check(!refused.Active&&!refused.Trace!.MovementRequested,"failed grenade never becomes dash");
        var p2=Thrown();p2.Projectile("grenade",5,102);p2.Explosion(5,105);p2.Tick(105.2,true);Check(p2.Trace!.State=="post-blast","tactical pause after real explosion");p2.Tick(106,false);Check(p2.Trace.State=="post-blast","weapon must recover");p2.Tick(106.1,true);Check(p2.Trace.State=="entry-ready","both explosion and weapon proven");
        p2.MoveRequest(new(0,0,-1),106.2);p2.MoveResult(true);p2.Movement(new(0,0,2),true,false,true,106.5);Check(!p2.Trace.Arrived,"proximity alone is not native arrival");
        p2.Movement(new(0,0,2),true,true,true,107);Check(p2.Trace.Arrived&&p2.Trace.State=="completed","arrival requires movement crossing proximity native completion weapon");
        var direct=Ready();direct.Choose(true,false,"","no-grenade",.5,0,101,c);direct.MoveRequest(new(0,0,-1),101.1);direct.MoveResult(true);direct.Tick(106,true);Check(!direct.Active&&!direct.Trace!.Arrived,"timeout never claims room entered");
        foreach(string reason in new[]{"ghost","authority-lost","door-closed","visible","ally-crossed","native-path-replaced","real-medical"}){
            var p=Thrown();p.Projectile("grenade",4,102);p.End(reason);p.Explosion(4,103);p.Tick(104,true);p.MoveRequest(new(0,0,-1),105);
            Check(!p.Active&&!p.Trace!.MovementRequested&&!p.Trace.ExplosionConfirmed,"interruption irreversible: "+reason);
        }
        // Adversarial event schedules: no entry-ready from expiry, wrong projectile or a foreign blast.
        var random=new Random(707);for(int k=0;k<200;k++){
            var p=Thrown();int id=k+1;p.Projectile("grenade",id,102);
            for(int j=0;j<8;j++){double now=102+j*.6;p.Explosion(id+1,now);p.Tick(now,random.Next(2)==0);Check(p.Trace!.State!="entry-ready","foreign event schedule cannot unlock entry");}
            p.Explosion(id,107);p.Tick(108,true);Check(p.Trace!.State=="entry-ready","matching event permits reassessment");
        }
        var g=new DoorGeometry{Center=new(0,0,0),Normal=new(0,0,1),Axis=new(1,0,0),HalfWidth=.65};var start=new DoorPoint(1,0,-1);var end=new DoorPoint(.6,0,2);
        var good=new[]{start,new DoorPoint(0,0,-.5),new DoorPoint(0,0,.5),end};
        Check(DoorClearGeometry.Route(g,start,end,good,true)=="","route crosses actual doorway");
        Check(DoorClearGeometry.Route(g,start,end,new[]{start,end},true)=="clear-route-misses-opening","straight line through wall rejected");
        Check(DoorClearGeometry.Route(g,start,end,new[]{start,new DoorPoint(0,3,0),end},true)=="clear-route-other-floor","other floor rejected");
        Check(DoorClearGeometry.Route(g,start,end,good,false)!="","partial navmesh rejected");
        Check(DoorClearGeometry.Route(g,start,end,new[]{start,new DoorPoint(0,0,1),new DoorPoint(0,0,-1),end},true)!="","multiple crossings refused");
        Check(DoorClearGeometry.Depth(g,start,end)==2&&DoorClearGeometry.Depth(g,new(0,0,1),new(0,0,-2))==2,"both door sides supported");
        // Inspect compiled boundaries as well as the pure policy. These checks do not run Unity.
        using(var pe=new PEReader(File.OpenRead("Client/bin/Release/netstandard2.1/COMET.dll"))){
            var md=pe.GetMetadataReader();string Name(int token){var h=MetadataTokens.EntityHandle(token);if(h.Kind==HandleKind.MethodSpecification)token=MetadataTokens.GetToken(md.GetMethodSpecification((MethodSpecificationHandle)h).Method);return FloorHearingTests.Name(md,token);}
            List<string> Calls(string method)=>FloorHearingTests.ReadIl(pe.GetMethodBody(md.TypeDefinitions.Select(md.GetTypeDefinition).Single(x=>md.GetString(x.Name)=="Plugin").GetMethods().Select(md.GetMethodDefinition).Single(x=>md.GetString(x.Name)==method).RelativeVirtualAddress).GetILBytes()!)
                .Where(x=>x.op==OpCodes.Call||x.op==OpCodes.Callvirt).Select(x=>Name((int)x.operand!)).ToList();
            foreach(string name in new[]{"DoorGate","ClearResources","EnsureClearEvents","Begin"})Check(Calls("TryStartDoorClear").Contains(name),"scoped startup: "+name);
            foreach(string name in new[]{"FakeScope","get_GrenadeDangerPoint","get_CurrentSelfDecision","get_CurrentSquadDecision","get_IsVisible"})Check(Calls("ClearGate").Contains(name),"native priority: "+name);
            foreach(string name in new[]{"StepDoorClear","StepDoorFake"})Check(Calls("DoorSearchStep").Contains(name),"native search dispatch: "+name);
            foreach(string name in new[]{"RequestJumpDoorOpen","RefreshDoorFake","ValidateClearRoute","Throw","WalkToPointByWay","MissingProjectile","ClearBlastProtected"})Check(Calls("StepDoorClear").Contains(name),"phase adapter: "+name);
            foreach(string name in new[]{"ClearGate","ClearThrowSafety","get_grenade"}){
                if(name!="get_grenade")Check(Calls("ClearThrowGuard").Contains(name),"actual native arc guarded: "+name);
            }
            foreach(string name in new[]{"add_OnGrenadeThrow","add_OnGrenadeExplosive"})Check(Calls("EnsureClearEvents").Contains(name),"real event subscription: "+name);
            foreach(string name in new[]{"remove_OnGrenadeThrow","remove_OnGrenadeExplosive","Dispose"})Check(Calls("ClearRoomEvents").Contains(name),"raid event cleanup: "+name);
            Check(Calls("ClearProjectileThrown").Contains("Projectile")&&!Calls("ClearProjectileThrown").Contains("Explosion"),"launch cannot confirm blast");
            Check(Calls("ClearProjectileExplosion").Contains("Explosion"),"native explosion proof wired");
            Check(new[]{"remove_OnGrenadeThrow","remove_OnGrenadeExplosive","Dispose"}.All(CleanupIl.Calls(pe,md,"Plugin","EndRaid").Contains),"independent raid cleanup wired");
            foreach(string method in new[]{"StepDoorClear","ClearThrowSafety","ClearProjectileThrown","ClearProjectileExplosion","PlanClearCandidate"})
            foreach(string forbidden in new[]{"ChangeHealth","ApplyDamage","AddItem","SetEnemyVisible","get_EnemyPosition","Teleport","set_Speed","PlaySound"})Check(!Calls(method).Contains(forbidden),"no artificial effects or omniscience: "+method+":"+forbidden);
        }
        var fixture=new{schema=1,feature="D07",stage="evaluated",decision="DoorRoomClear",reason=p2.Trace.Reason,doorRoomClear=p2.Trace,doorRoomClearConfig=c};
        var opts=new JsonSerializerOptions{PropertyNamingPolicy=JsonNamingPolicy.CamelCase,WriteIndented=true};
        Directory.CreateDirectory("artifacts");File.WriteAllText("artifacts/d07-fixture.json",JsonSerializer.Serialize(fixture,opts));
        var transport=JsonSerializer.Deserialize<DoorClearTrace>(JsonSerializer.Serialize(p2.Trace));Check(transport!.Projectile==5&&transport.ExplosionConfirmed&&transport.Arrived,"telemetry roundtrip evidence");
        Console.WriteLine($"PASS D07: {n} checks (no Unity raid simulated)");return n;
    }
}
