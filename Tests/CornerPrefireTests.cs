using Comet.Core;
using Comet.Server;
using System.Text.Json;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

internal static class CornerPrefireTests
{
    public static int Run()
    {
        int n=0;void Check(bool yes,string why){if(!yes)throw new Exception("M06: "+why);n++;}
        void Invalid(Action a){try{a();}catch(ArgumentException){n++;return;}throw new Exception("M06 accepted invalid config");}
        var c=new CornerPrefireConfig();c.Validate();
        CornerPrefireFrame Frame(double now=10)=>new(){Now=now,SeenAt=9.5,ParentDeadline=14,ParentActive=true,ParentEpisode=1,
            Target="enemy",Weapon="weapon",Mode="fullauto",Automatic=true,Ammo=30,Capacity=30,CornerDistance=2,KnownCornerDistance=2,
            Known=new(4,0,4),Aim=new(3,1.2,4),Clear=true,AllySafe=true,Aligned=true};
        CornerPrefire Begin(){var p=new CornerPrefire();Check(p.Begin(Frame(),c,0),"eligible personal walking pursuit");return p;}
        Check(c.Enabled&&c.Chance==40&&c.ObservationMaxAge==2.5&&c.KnownCornerDistance==3&&c.FireCornerDistance==4,"approved chance and geometry");
        Check(c.AutomaticRounds==4&&c.SingleRounds==1&&c.FireLimit==.6&&c.StartRounds==10&&c.StartPercent==40&&c.ReserveRounds==5&&c.ReservePercent==30,"approved fire and ammunition caps");
        Check(c.Start(30)==12&&c.Reserve(30)==9&&c.Start(31)==13&&c.Reserve(31)==10&&c.Start(10)==10&&c.Reserve(10)==5,"max of absolute and rounded-up percentage");
        int chosen=0;for(int i=0;i<1000;i++){var p=new CornerPrefire();if(p.Begin(Frame(),c,i/1000d))chosen++;Check(!p.Begin(Frame(),c,0),"same opportunity cannot reroll");}
        Check(chosen==400,"40 percent actual probability, not a style weight");
        var x=Begin();Check(!x.Trace!.Requested&&!x.Trace.Accepted&&x.Trace.ActualShots==0,"selection is not a shot");
        var f=Frame();f.Clear=false;Check(!x.Request(f,c)&&x.Active&&x.Trace.Reason=="prefire-awaiting-muzzle-clear","wall before start waits");
        f=Frame(10.1);f.CornerDistance=4.01;Check(!x.Request(f,c)&&x.Active,"outside firing distance waits");
        f=Frame(10.2);f.Aligned=false;Check(!x.Request(f,c)&&x.Active,"native aim alignment required");
        f=Frame(10.3);f.Fresh=false;Check(!x.Request(f,c)&&x.Active,"no firing on stale geometry");
        Check(x.Request(Frame(10.4),c),"clear aligned barrel starts one request");x.Result(true);
        Check(x.Trace.Accepted&&x.Trace.ActualShots==0,"native acceptance not a fired cartridge");
        Check(!x.Request(Frame(10.45),c),"no repeated trigger requests");
        for(int i=1;i<=4;i++){f=Frame(10.4+i*.05);f.Ammo=31-i;x.Shot(f,c);Check(x.Trace.ActualShots==i,"count physical rounds");Check(x.Active==(i<4),"stop at quota");}
        Check(x.Trace.Reason=="prefire-round-limit"&&x.Trace.ValidFor==0,"round cap ends lease");x.Shot(Frame(10.65),c);Check(x.Trace.ActualShots==4,"unowned shots not attributed");
        x=Begin();f=Frame();f.Automatic=false;f.Mode="single";x.End("test");x=new();x.Begin(f,c,0);x.Request(f,c);x.Shot(f,c);x.Result(true);
        Check(x.Trace!.ActualShots==1&&x.Trace.Accepted&&!x.Active,"synchronous single shot before request returns is preserved");
        x=Begin();x.Request(Frame(),c);x.Result(false);Check(!x.Active&&!x.Trace!.Accepted&&x.Trace.ActualShots==0,"native refusal");
        x=Begin();x.Request(Frame(),c);x.Result(true);x.Observe(Frame(10.3),c);x.Observe(Frame(10.6),c);
        Check(!x.Active&&x.Trace!.Reason=="prefire-window-expired","0.6s deadline even with no shot events");
        x=Begin();f=Frame();f.Ammo=12;Check(x.Request(f,c),"start at exact required ammo");x.Result(true);
        f=Frame(10.1);f.Ammo=10;x.Shot(f,c);Check(!x.Active&&x.Trace!.Reason=="prefire-ammo-reserve","conservative reserve when callback precedes magazine update");
        x=Begin();f=Frame();f.Ammo=11;Check(!x.Request(f,c)&&x.Trace!.Reason=="prefire-start-ammo","recheck start ammo after waiting");
        foreach(var change in new Action<CornerPrefireFrame>[] {
            f=>f.ParentActive=false,f=>f.ParentEpisode=2,f=>f.ParentStyle="jump",f=>f.Target="other",f=>f.Weapon="switched",f=>f.Mode="single",
            f=>f.Block="prefire-ghost",f=>f.Block="prefire-non-authority",f=>f.Block="prefire-non-pmc",f=>f.Block="prefire-contact-reacquired",
            f=>f.Block="prefire-medical-priority",f=>f.Block="prefire-grenade-danger",f=>f.Block="prefire-route-lost",
            f=>f.AllySafe=false,f=>f.Clear=false,f=>f.Fresh=false,f=>f.Aligned=false,f=>f.CornerDistance=4.01,f=>f.Ammo=9,f=>f.Capacity=0
        }) {x=Begin();x.Request(Frame(),c);x.Result(true);f=Frame(10.1);change(f);Check(!x.Observe(f,c)&&x.Trace!.ValidFor==0,"active fire cancels on safety/ownership change");}
        foreach(var change in new Action<CornerPrefireFrame>[] {f=>f.ParentStyle="jump",f=>f.SeenAt=7.49,f=>f.KnownCornerDistance=3.01,f=>f.Ammo=11,f=>f.Capacity=0,f=>f.AllySafe=false,f=>f.Target="",f=>f.Weapon="",f=>f.Block="heard-only",f=>f.ParentDeadline=9})
        {f=Frame();change(f);x=new();Check(!x.Begin(f,c,0),"ineligible start");Check(!x.Begin(Frame(),c,0),"refusal consumes parent opportunity");}
        x=Begin();x.End("native-reset");f=Frame(10.1);Check(!x.Begin(f,c,0),"no retry after reset");f.ParentEpisode=2;Check(x.Begin(f,c,0),"new M05 opportunity allowed; M05 enforces sight and cooldown");
        x=Begin();Check(!x.Observe(Frame(10.41),c),"callback staleness");x=Begin();Check(!x.Observe(Frame(9.9),c),"clock reversal");
        x=new();f=Frame();f.SeenAt=7.6;x.Begin(f,c,0);x.Request(f,c);x.Result(true);f.Now=10.11;
        Check(!x.Observe(f,c)&&x.Trace!.Reason=="prefire-observation-expired","personal age expires during burst");
        x=Begin();f=Frame(10.1);f.Known=new(999,0,999);f.Aim=new(888,1,888);x.Observe(f,c);
        Check(x.Trace!.KnownX==4&&x.Trace.AimX==3,"trace uses frozen observation and aim, never hidden updates");
        x=Begin();Check(!x.AuthorizeRequest(Frame(),c),"A03 exception needs an actual guarded request");
        x.Request(Frame(),c);Check(x.AuthorizeRequest(Frame(),c),"A03 permits the pending M06 request");
        x.Result(true);Check(!x.AuthorizeRequest(Frame(),c),"accepted request cannot authorize another call");
        foreach(var change in new Action<CornerPrefireFrame>[] {f=>f.Fresh=false,f=>f.Clear=false,f=>f.AllySafe=false,f=>f.Aligned=false,
            f=>f.Target="other",f=>f.ParentActive=false,f=>f.Block="native-priority",f=>f.Now=10.61}) {
            x=Begin();x.Request(Frame(),c);f=Frame(10.1);change(f);
            Check(!x.AuthorizeRequest(f,c)&&!x.Active,"A03 exception rejects stale or unsafe requests");
        }
        var fixture=Begin();fixture.Request(Frame(),c);fixture.Result(true);fixture.Shot(Frame(10.05),c);
        File.WriteAllText("artifacts/m06-corner-prefire.json",JsonSerializer.Serialize(new{cornerPrefire=fixture.Trace},new JsonSerializerOptions{IncludeFields=true,PropertyNamingPolicy=JsonNamingPolicy.CamelCase,WriteIndented=true}));
        foreach(var prop in typeof(CornerPrefireConfig).GetProperties().Where(p=>p.PropertyType==typeof(double)))
            foreach(double bad in new[]{double.NaN,double.PositiveInfinity,-1d,1001d}){var badC=new CornerPrefireConfig();prop.SetValue(badC,bad);Invalid(badC.Validate);}
        foreach(var prop in typeof(CornerPrefireConfig).GetProperties().Where(p=>p.PropertyType==typeof(int)))
            foreach(int bad in new[]{-1,1001}){var badC=new CornerPrefireConfig();prop.SetValue(badC,bad);Invalid(badC.Validate);}
        Invalid(()=>new CornerPrefireConfig{StartRounds=5}.Validate());Invalid(()=>new CornerPrefireConfig{StartPercent=30}.Validate());
        Invalid(()=>new CometConfig{CornerPrefire=null!}.Validate());
        var legacy=JsonSerializer.Deserialize<CometConfig>("{}")!;legacy.Validate();Check(legacy.CornerPrefire.Chance==40,"old configs receive defaults");
        var dir=Path.Combine(Path.GetTempPath(),"comet-m06-"+Guid.NewGuid().ToString("N"));var file=Path.Combine(dir,"config.json");
        try {var store=new ConfigStore(file);var cfg=store.Read();cfg.CornerPrefire.Chance=65;cfg.CornerPrefire.FireLimit=.4;store.Save(cfg,cfg.Revision);
            var saved=store.Read();Check(saved.CornerPrefire.Chance==65&&saved.CornerPrefire.FireLimit==.4,"server UI roundtrip");
            var before=File.ReadAllText(file);saved.CornerPrefire.ReservePercent=100;Invalid(()=>store.Save(saved,saved.Revision));Check(before==File.ReadAllText(file),"invalid save atomic");
        }finally{foreach(var path in new[]{file,file+".bak"})if(File.Exists(path))File.Delete(path);if(Directory.Exists(dir))Directory.Delete(dir);}
        using(var pe=new PEReader(File.OpenRead("references/sain-4.5.1/SAIN.dll"))) {
            var md=pe.GetMetadataReader();var type=md.TypeDefinitions.Select(h=>md.GetTypeDefinition(h)).Single(t=>md.GetString(t.Name)=="ManualShootClass");
            var methods=type.GetMethods().Select(h=>md.GetString(md.GetMethodDefinition(h).Name)).ToArray();
            foreach(var name in new[]{"TryShoot","Reset","CanShoot"})Check(methods.Contains(name),"official shooting hook "+name);
            var fields=type.GetFields().Select(h=>md.GetString(md.GetFieldDefinition(h).Name)).ToArray();
            Check(fields.Contains("ManualShootEnemy")&&fields.Contains("_timeStartManualShoot"),"official ownership fields");
        }
        Console.WriteLine($"M06: {n} checks passed (one roll, muzzle waiting, actual rounds, reserves, deadlines, interruptions, config)");return n;
    }
}
