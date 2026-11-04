using Comet.Core;
using System.Text.Json;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

static class ShotGeometryTests
{
    public static int Run()
    {
        int n=0;void Check(bool ok,string why){n++;if(!ok)throw new Exception("W08: "+why);}
        void Invalid(Action a,string why){try{a();}catch(ArgumentException){n++;return;}throw new Exception("W08: "+why);}
        var c=new ShotGeometryConfig();c.Validate();Check(c.Enabled&&c.AllowPenetration&&c.CacheSeconds==.15&&c.MuzzleMovement==.25&&c.DirectionDegrees==5,"approved defaults");
        foreach(double bad in new[]{double.NaN,double.PositiveInfinity,-.1,1})
        {Invalid(()=>new ShotGeometryConfig{CacheSeconds=bad}.Validate(),"cache bounds");Invalid(()=>new ShotGeometryConfig{MuzzleMovement=bad}.Validate(),"movement bounds");}
        foreach(double bad in new[]{double.NaN,double.NegativeInfinity,0,11})Invalid(()=>new ShotGeometryConfig{DirectionDegrees=bad}.Validate(),"direction bounds");
        Invalid(()=>new CometConfig{ShotGeometry=null!}.Validate(),"null settings");
        var config=JsonSerializer.Deserialize<CometConfig>("{}")!;config.Validate();Check(config.ShotGeometry.Enabled,"old config default");
        config.ShotGeometry=new(){Enabled=false,AllowPenetration=false,CacheSeconds=.1,MuzzleMovement=.2,DirectionDegrees=4};
        config=JsonSerializer.Deserialize<CometConfig>(JsonSerializer.Serialize(config))!;config.Validate();Check(!config.ShotGeometry.Enabled&&!config.ShotGeometry.AllowPenetration&&config.ShotGeometry.DirectionDegrees==4,"persistence");
        ShotSurface Surface(string kind="solid")=>new(){Kind=kind,Known=true,Material="WoodThin",Lane="aim",Distance=2,Level=5,Chance=.8,Incidence=0,Forward=true};
        ShotGeometryFrame F()=>new(){Complete=true,OriginClear=true,AllySafe=true,AmmoKnown=true,Power=40,Deviation=2,AmmoChance=.4,AmmoRicochet=.1,Degradation=.8};
        var wire=new List<JsonElement>();var options=new JsonSerializerOptions{IncludeFields=true,PropertyNamingPolicy=JsonNamingPolicy.CamelCase};
        void Save(ShotGeometryTrace t){t.Consumer="W07";t.Target="known-enemy";t.Weapon="rifle";t.Ammo="cartridge";t.Action="Search";t.AimZ=12;t.MuzzleY=1.4;wire.Add(JsonSerializer.SerializeToElement(t,options));}
        var f=F();var clear=ShotGeometryPolicy.Evaluate(f,c);Save(clear);Check(clear.Allowed&&clear.State=="clear"&&!clear.PenetrationEstimated,"open stairway needs no height veto");
        f.Surfaces=new[]{Surface()};var penetrable=ShotGeometryPolicy.Evaluate(f,c);Save(penetrable);Check(penetrable.Allowed&&penetrable.PenetrationEstimated&&Math.Abs(penetrable.ConservativePower-28.8)<1e-6,"native lower estimate");
        f.Surfaces[0].Level=100;var solid=ShotGeometryPolicy.Evaluate(f,c);Save(solid);Check(!solid.Allowed&&solid.Reason=="geometry-solid-obstacle","solid wall even on same floor or near aim");
        foreach(string kind in new[]{"door","glass"})
        foreach(bool enabled in new[]{true,false})
        foreach(double power in new[]{0d,1d,100d,double.NaN})
        {
            f=F();f.AmmoKnown=false;f.Power=power;var s=Surface(kind);s.Known=false;s.Level=10000;s.Chance=0;s.Ricochet=1;s.Incidence=60;f.Surfaces=new[]{s};
            var t=ShotGeometryPolicy.Evaluate(f,new(){AllowPenetration=enabled});Check(t.Allowed&&t.State=="pass-through"&&!t.PenetrationEstimated&&t.PassThroughSurfaces==1,"glass/door always permit attempt, not confirmed penetration");
            Check(t.ConservativePower==0&&t.EstimatedChance==0,"no invented penetration prediction");
        }
        f=F();f.Surfaces=new[]{Surface("door"),Surface("glass")};var passthrough=ShotGeometryPolicy.Evaluate(f,c);Save(passthrough);
        f.Surfaces=new[]{Surface("door"),Surface()};f.Surfaces[1].Distance=3;f.Surfaces[1].Level=100;
        Check(!ShotGeometryPolicy.Evaluate(f,c).Allowed,"door cannot authorize wall behind it");
        f.Surfaces[1]=Surface("glass");f.AllySafe=false;var ally=ShotGeometryPolicy.Evaluate(f,c);Save(ally);Check(!ally.Allowed&&ally.Reason=="geometry-ally-in-line","glass/door cannot override allied bystander");
        f=F();f.Surfaces=new[]{Surface("door")};f.OriginClear=false;Check(ShotGeometryPolicy.Evaluate(f,c).Reason=="geometry-muzzle-inside","muzzle inside geometry stays unsafe");
        f=F();f.Surfaces=new[]{Surface()};Check(!ShotGeometryPolicy.Evaluate(f,new(){AllowPenetration=false}).Allowed,"other material estimate disabled");
        foreach(var mutate in new Action<ShotGeometryFrame>[] {x=>x.AmmoKnown=false,x=>x.Power=0,x=>x.Deviation=-1,x=>x.Deviation=double.NaN,x=>x.AmmoChance=2,x=>x.AmmoRicochet=-1,x=>x.Degradation=double.NaN,x=>x.Degradation=0})
        {f=F();f.Surfaces=new[]{Surface()};mutate(f);var t=ShotGeometryPolicy.Evaluate(f,c);Check(!t.Allowed,"unreliable ammo declined");Save(t);}
        foreach(var mutate in new Action<ShotSurface>[] {x=>x.Known=false,x=>x.Level=double.NaN,x=>x.Distance=double.PositiveInfinity,x=>x.Chance=-1,x=>x.Ricochet=2,x=>x.Incidence=181,x=>x.Kind="door-like-name",x=>x.Lane="unknown"})
        {f=F();f.Surfaces=new[]{Surface()};mutate(f.Surfaces[0]);var t=ShotGeometryPolicy.Evaluate(f,c);Check(!t.Allowed,"unknown/custom/bad surface declined");Save(t);}
        f=F();f.Surfaces=new ShotSurface[]{null!};Check(!ShotGeometryPolicy.Evaluate(f,c).Allowed,"null surface rejected");
        f=F();f.Surfaces=Enumerable.Range(0,33).Select(i=>Surface()).ToArray();Check(!ShotGeometryPolicy.Evaluate(f,c).Allowed,"surface bound");
        f=F();f.Complete=false;var deferred=ShotGeometryPolicy.Evaluate(f,c);Save(deferred);Check(!deferred.Allowed&&!deferred.Fresh,"deferred query is never permission");
        f=F();f.Surfaces=new[]{Surface()};f.Surfaces[0].Level=28.8;Check(!ShotGeometryPolicy.Evaluate(f,c).Allowed,"strict native power threshold");
        f.Surfaces[0].Level=5;f.Surfaces[0].Ricochet=.2;f.Surfaces[0].Incidence=60;Check(!ShotGeometryPolicy.Evaluate(f,c).Allowed,"native ricochet angle rejected");
        f.Surfaces[0].Incidence=42.5;Check(ShotGeometryPolicy.Evaluate(f,c).Allowed,"native ricochet lower boundary");
        f.Surfaces[0].Incidence=80;Check(ShotGeometryPolicy.Evaluate(f,c).Allowed,"native ricochet upper boundary");
        f=F();f.AmmoChance=.1;f.Surfaces=new[]{Surface(),Surface()};f.Surfaces[0].Chance=f.Surfaces[1].Chance=.5;
        f.Surfaces[1].Forward=false;f.Surfaces[1].Distance=2.2;Check(!ShotGeometryPolicy.Evaluate(f,c).Allowed,"entry/exit compound chance considered");
        f.Surfaces[1].Lane="barrel";Check(ShotGeometryPolicy.Evaluate(f,c).Allowed,"two alternative lanes do not multiply each other's probability");
        f.Surfaces[1].Lane="aim";f.Surfaces[1].Distance=1;Check(!ShotGeometryPolicy.Evaluate(f,c).Allowed,"unsorted surface evidence rejected");
        f=F();f.Surfaces=new[]{Surface(),Surface()};f.Surfaces[1].Distance=3;f.Surfaces[1].Level=24;
        Check(!ShotGeometryPolicy.Evaluate(f,c).Allowed,"conservative falloff repeated across forward barriers");
        // Both side/aim lanes, thick or distant obstacles are treated by geometry rather than floor heuristics.
        foreach(double at in new[]{.01,.5,2.49,2.51,20,49.8})foreach(string lane in new[]{"aim","barrel"})
        {f=F();f.Surfaces=new[]{Surface()};f.Surfaces[0].Level=100;f.Surfaces[0].Distance=at;f.Surfaces[0].Lane=lane;Check(!ShotGeometryPolicy.Evaluate(f,c).Allowed,"opaque obstacle at every distance/lane");}
        bool Reuse(ShotGeometryTrace t,double now=10.1,int frame=2,DoorPoint? muzzle=null,DoorPoint? aim=null,DoorPoint? dir=null,string key="same",bool stable=true)=>
            ShotGeometryPolicy.CanReuse(now,10,frame,1,t,muzzle??default,default,aim??new(0,0,10),new(0,0,10),dir??new(0,0,1),new(0,0,1),key,"same",stable,c);
        Check(Reuse(solid),"static blocked cache");Check(!Reuse(solid,10.151),"cache expired");Check(!Reuse(solid,9.9),"clock rollback");Check(!Reuse(solid,double.NaN),"invalid clock");
        Check(!Reuse(solid,stable:false),"dynamic doors/objects not cached across frames");Check(!Reuse(solid,key:"new-ammo"),"ammo/weapon/source identity invalidates");
        Check(!Reuse(solid,muzzle:new(.25,0,0))&&!Reuse(solid,aim:new(0,0,10.25)),"muzzle and aim movement boundary");
        Check(!Reuse(solid,dir:new(.1,0,1)),"five degree turn invalidates");Check(!Reuse(solid,dir:new(0,0,0)),"invalid direction");
        Check(!Reuse(clear)&&!Reuse(penetrable)&&!Reuse(passthrough)&&!Reuse(ally)&&!Reuse(deferred),"permissions/ally/deferred never persist to another frame");
        Check(Reuse(clear,frame:1)&&Reuse(solid,frame:1,stable:false),"same-frame reuse");
        var released=new ShotGeometryTrace{State="released",Reason="geometry-native-reset",ValidFor=0};Save(released);
        Check(!Reuse(new ShotGeometryTrace{State="released",Fresh=true,ValidFor=0},frame:1),"released trace cannot be reused even in the same frame");
        f=F();f.Surfaces=new[]{Surface(),Surface()};f.Surfaces[0].Kind="door";f.Surfaces[1].Distance=3;f.Surfaces[1].Level=24;
        Check(!ShotGeometryPolicy.Evaluate(f,c).Allowed,"door always permits attempt but subsequent solid accounts for native power degradation");
        using(var pe=new PEReader(File.OpenRead("Client/bin/Release/netstandard2.1/COMET.dll")))
        {
            var md=pe.GetMetadataReader();var type=md.TypeDefinitions.Select(md.GetTypeDefinition).Single(t=>md.GetString(t.Name)=="Plugin");
            MethodDefinition M(string name)=>type.GetMethods().Select(md.GetMethodDefinition).Single(m=>md.GetString(m.Name)==name);
            int Calls(string name,string call)=>ReloadTimingTests.Calls(pe,md,M(name),call);
            foreach(string gate in new[]{"IsPmcAi","get_Authority","Ghost","get_BotActive","get_IsDead","get_GameEnding","get_SAINLayersActive"})Check(Calls("GeometryScope",gate)==1,"scope "+gate);
            Check(Calls("CheckShotGeometry","TryBegin")==1&&Calls("CheckShotGeometry","Charge")==1,"shared budget");
            Check(Calls("PrefireFrame","CheckShotGeometry")==1&&Calls("DisciplineGeometry","CheckShotGeometry")==1,"shared M06/W07 checker");
            Check(Calls("GeometryAmmo","get_CurrentAmmoTemplate")==0&&Calls("GeometryAmmo","FirstRealAmmo")==1,"actual ammunition only");
            foreach(string method in new[]{"CheckShotGeometry","GeometrySurface","GeometryRequest","GeometryUpdate","GeometryLane"})
                foreach(string forbidden in new[]{"get_EnemyPlayer","get_EnemyPosition","CreateShot","Shoot","IsPenetrated","ApplyHit","FindObjectsOfType","SetFireMode"})Check(Calls(method,forbidden)==0,"read-only geometry, no hidden target or simulated bullet "+method+forbidden);
            Check(Calls("GeometryRequest","get_IsVisible")>0&&Calls("GeometryUpdate","get_IsVisible")>0,"visible target bypass at request and update");
            Check(Calls("GeometryUpdate","StopGeometryOwned")==1,"blocked ongoing native fire interrupted");
        }
        File.WriteAllText("artifacts/w08-wire-traces.json",JsonSerializer.Serialize(wire,options));
        File.WriteAllText("artifacts/w08-shot-geometry.json",JsonSerializer.Serialize(new{botInstance="instance",shotGeometry=passthrough,shotGeometryConfig=c},options));
        Console.WriteLine($"W08: {n} assertions passed.");return n;
    }
}
