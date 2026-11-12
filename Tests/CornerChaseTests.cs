using Comet.Core;
using Comet.Server;
using System.Text.Json;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

internal static class CornerChaseTests
{
    public static int Run()
    {
        int n=0;void Check(bool x,string why){if(!x)throw new Exception("M05: "+why);n++;}
        void Invalid(Action a){try{a();}catch(ArgumentException){n++;return;}throw new Exception("M05 accepted invalid config");}
        var c=new CornerChaseConfig();c.Validate();
        CornerChaseFrame Frame(double now=10)=>new(){Combat=new(){Now=Math.Round(now,6),Action="RushEnemy",Target="enemy",Visible=false,Known=new(4,0,4),Position=new(0,0,0),Personality="Chad"},
            SeenAt=9.5,ObservationEpisode=1,RealCorner=true,PathLength=8,Speed=3,CanJump=true,Fleeing=true};
        CornerChase Begin(double style=.5){var p=new CornerChase();Check(p.Begin(Frame(),c,0,style,1),"eligible personal pursuit");return p;}
        Check(c.LostSightMin==.3&&c.ObservationMaxAge==6&&c.PathLimit==15&&c.SequenceLimit==4&&c.WideOffset==1.8&&c.Cooldown==6,"agreed limits");
        Check(c.JumpMinSpeed==2.2&&c.JumpMinStamina==35&&c.Chances["GigaChad"]==90&&c.Chances["Wreckless"]==85&&c.Chances["Chad"]==75&&c.Chances["Normal"]==40,"agreed chances and jump limits");
        foreach(var pair in c.Chances) {
            var f=Frame();f.Combat.Personality=pair.Key;var p=new CornerChase();
            Check(p.Begin(f,c,Math.Max(0,pair.Value/100-.0001),.5,1)==(pair.Value>0),"personality chance boundary below");
            p=new();Check(!p.Begin(f,c,pair.Value/100,.5,1),"exact chance boundary declines");
            Check(!p.Begin(Frame(10.1),c,0,0,1),"no same-frame reroll");
        }
        var mem=new ChaseObservation();mem.Sample("a",true,new(3,0,3),1);mem.Sample("a",true,new(3.3,0,3),1.1);
        Check(mem.Speed>2.9&&mem.Direction.X>.99,"movement measured only between personal samples");
        mem.Sample("a",false,new(100,0,100),1.2);int episode=mem.Episode;
        mem.Sample("a",false,new(200,0,200),2);
        Check(mem.Lost&&mem.Known.X==3.3&&mem.SeenAt==1.1&&mem.Episode==episode,"hidden updates never move memory, age or episode");
        mem.Sample("b",false,new(100,0,0),3);Check(!mem.Lost&&mem.SeenAt<0,"switch to an unseen contact cannot reuse memory");
        mem.Sample("a",false,new(100,0,0),3.1);Check(!mem.Lost,"switching back cannot manufacture a new personal observation");
        mem.Sample("a",true,new(1,0,1),4);mem.Sample("a",false,default,4.1);Check(mem.Episode>episode&&mem.Speed==0,"new sight gives new episode, old direction discarded");
        mem.Sample("a",true,new(2,0,2),double.NaN);Check(mem.SeenAt<0,"invalid observation clock discarded");
        var chase=Begin();Check(chase.Trace!.Style=="lean-in"&&!chase.Trace.Requested&&!chase.Trace.MovementObserved,"direct style based on prior observed retreat");
        var frame=Frame();frame.Fleeing=false;chase=new();chase.Begin(frame,c,0,0,-1);Check(chase.Trace!.Style=="pie","low confidence favors wide angle even on favorable jump roll");
        frame=Frame();frame.Combat.Personality="Normal";chase=new();chase.Begin(frame,c,0,0,1);Check(chase.Trace!.Style=="lean-in","non-bold profile does not jump");
        frame=Frame();frame.CanJump=false;chase=new();chase.Begin(frame,c,0,0,1);Check(chase.Trace!.Style=="lean-in","no jump without safe space/resources");
        chase=Begin(0);chase.Request(new(2,0,2));
        Check(chase.Trace!.Style=="jump"&&chase.Trace.Requested&&!chase.Trace.MovementObserved,"movement request is not movement");
        Check(!chase.MayJump(10.1,2.19,1,true,true,100,c)&&!chase.MayJump(10.1,3,1,true,true,34.9,c),"observed momentum and stamina thresholds");
        Check(!chase.MayJump(10.1,3,4,true,true,100,c)&&!chase.MayJump(10.1,3,1,false,true,100,c),"no distant or airborne launch");
        Check(chase.MayJump(10.1,2.2,1,true,true,35,c),"inclusive jump minima");
        chase.JumpResult(true,10.1);Check(chase.Trace.JumpRequested&&chase.Trace.JumpAccepted&&!chase.Trace.TakeoffObserved&&!chase.Trace.LandingObserved,"input acceptance is not takeoff");
        frame=Frame(10.1);frame.Combat.Position=new(.3,0,0);frame.Combat.Grounded=false;
        Check(chase.Observe(frame,c)&&chase.Trace.TakeoffObserved&&chase.Trace.MovementObserved,"physical takeoff and travel observed");
        frame=Frame(10.2);frame.Combat.Position=new(.6,0,0);Check(chase.Observe(frame,c)&&chase.Trace.LandingObserved,"landing after flight");
        Check(!chase.MayJump(10.3,3,1,true,true,100,c)&&chase.Trace.Jumps==1,"one jump only");
        string fixture=JsonSerializer.Serialize(new{cornerChase=chase.Trace},new JsonSerializerOptions{IncludeFields=true,PropertyNamingPolicy=JsonNamingPolicy.CamelCase,WriteIndented=true});
        chase=Begin(0);chase.JumpResult(false,10.1);Check(chase.Trace!.Style=="lean-in"&&chase.Trace.Jumps==0&&chase.Trace.JumpRequested,"refusal walks, cannot retry jump");
        chase=Begin(0);chase.JumpResult(true,10);
        for(int i=1;i<=6;i++)chase.Observe(Frame(10+i*.1),c);
        Check(chase.Trace!.Style=="lean-in"&&!chase.Trace.TakeoffObserved,"unobserved launch becomes walking, not fake flight");
        chase=Begin();chase.Request(new(4,0,4));
        for(int i=1;i<40;i++){Check(chase.Observe(Frame(10+i*.1),c),"within bounded sequence");Check(chase.Deadline==14,"deadline never extended");}
        Check(!chase.Observe(Frame(14),c)&&chase.Trace!.Reason=="chase-time-limit"&&chase.NextAttempt==20,"inclusive hard deadline and cooldown from end");
        frame=Frame(20);frame.SeenAt=19.5;Check(!chase.Begin(frame,c,0,0,1),"same observation episode cannot retry after cooldown");
        frame.ObservationEpisode=2;Check(chase.Begin(frame,c,0,0,1),"new personal sight plus cooldown permits another attempt");
        foreach(var change in new Action<CornerChaseFrame>[] {
            f=>f.Combat.Pmc=false,f=>f.Combat.Authority=false,f=>f.Combat.Ghost=true,f=>f.Combat.Active=false,f=>f.Combat.Enabled=false,
            f=>f.Combat.Action="Hold",f=>f.Combat.Action="Retreat",f=>f.Combat.Visible=true,f=>f.Combat.Source="shared",f=>f.Combat.Target="",
            f=>f.Combat.Busy=true,f=>f.Combat.Urgent=true,f=>f.Combat.Ready=false,f=>f.Combat.Immobile=true,f=>f.Combat.ProtectedCover=true,
            f=>f.NativeLeanOwned=true,f=>f.RouteValid=false,f=>f.GeometryFresh=false,f=>f.LaneSafe=false,
            f=>f.Combat.Known=new(double.NaN,0,0),f=>f.Speed=double.NaN,f=>f.SeenAt=double.PositiveInfinity
        }) {
            frame=Frame();change(frame);Check(!new CornerChase().Begin(frame,c,0,0,1),"invalid start or higher priority");
            chase=Begin();frame=Frame(10.1);change(frame);Check(!chase.Observe(frame,c)&&chase.Trace!.ValidFor==0,"immediate cancellation with no stale badge");
        }
        foreach(double seen in new[]{9.71,3.99,-1d}){frame=Frame();frame.SeenAt=seen;Check(!new CornerChase().Begin(frame,c,0,0,1),"outside personal sight window");}
        foreach(double seen in new[]{9.7,6d}){frame=Frame();frame.SeenAt=seen;Check(new CornerChase().Begin(frame,c,0,0,1),"personal sight window boundary");}
        foreach(var change in new Action<CornerChaseFrame>[] { f=>f.RealCorner=false,f=>f.PathLength=15.01,f=>f.PathLength=0,f=>f.PathLength=double.NaN }) {
            frame=Frame();change(frame);chase=new();Check(!chase.Begin(frame,c,0,0,1)&&!chase.Begin(Frame(10.1),c,0,0,1),"geometry failure consumes one episode, no repeated expensive trials");
        }
        frame=Frame();frame.RealCorner=false;frame.GeometryReason="route-has-no-bend";chase=new();
        Check(!chase.Begin(frame,c,0,0,1)&&chase.Trace!.GeometryReason=="route-has-no-bend"&&!chase.Trace.Requested,
            "geometry rejection retains the concrete cause without reporting movement");
        chase=Begin();frame=Frame(10.1);frame.Combat.Visible=true;Check(!chase.Observe(frame,c)&&chase.Trace!.Reason=="chase-contact-reacquired","immediate native combat handback");
        chase=Begin();frame=Frame(10.1);frame.Combat.Position=new(100,0,0);Check(!chase.Observe(frame,c)&&!chase.Trace!.MovementObserved,"teleport is not chase movement");
        chase=Begin();Check(!chase.Observe(Frame(10.41),c),"stale callback");chase=Begin();Check(!chase.Observe(Frame(9.9),c),"clock reversal");
        chase=Begin();frame=Frame(10.1);frame.Combat.Grounded=false;Check(!chase.Observe(frame,c),"uncommanded fall yields");
        var points=new[]{new DoorPoint(0,0,0),new DoorPoint(3,0,0),new DoorPoint(3,0,3)};
        Check(CornerChase.Route(points[0],points[2],points,true,6,out double len)&&len==6,"bounded complete route");
        Check(!CornerChase.Route(points[0],points[2],points,true,5.99,out _)&&!CornerChase.Route(points[0],points[2],points,false,15,out _),"length and completion enforced");
        Check(!CornerChase.Route(points[0],new(99,0,0),points,true,15,out _)&&!CornerChase.Route(points[0],points[2],new[]{points[0],new DoorPoint(1,3,0),points[2]},true,15,out _),"endpoint and floor checks");
        foreach(var prop in typeof(CornerChaseConfig).GetProperties().Where(p=>p.PropertyType==typeof(double)))
            foreach(double bad in new[]{double.NaN,double.PositiveInfinity,-1d,1001d}){var badC=new CornerChaseConfig();prop.SetValue(badC,bad);Invalid(badC.Validate);}
        Invalid(()=>new CometConfig{CornerChase=null!}.Validate());var invalid=new CornerChaseConfig();invalid.Chances.Remove("Rat");Invalid(invalid.Validate);
        invalid=new();invalid.Chances["Rat"]=double.NaN;Invalid(invalid.Validate);
        var legacy=JsonSerializer.Deserialize<CometConfig>("{}")!;legacy.Validate();Check(legacy.CornerChase.Chances["Chad"]==75,"older server configs gain safe defaults");
        var dir=Path.Combine(Path.GetTempPath(),"comet-m05-"+Guid.NewGuid().ToString("N"));var file=Path.Combine(dir,"config.json");
        try {
            var store=new ConfigStore(file);var cfg=store.Read();cfg.CornerChase.PathLimit=12;cfg.CornerChase.Chances["Rat"]=5;store.Save(cfg,cfg.Revision);
            var saved=store.Read();Check(saved.CornerChase.PathLimit==12&&saved.CornerChase.Chances["Rat"]==5,"UI config roundtrip");
            var before=File.ReadAllText(file);saved.CornerChase.JumpMinStamina=0;Invalid(()=>store.Save(saved,saved.Revision));Check(before==File.ReadAllText(file),"invalid save is atomic");
        }finally{foreach(var path in new[]{file,file+".bak"})if(File.Exists(path))File.Delete(path);if(Directory.Exists(dir))Directory.Delete(dir);}
        using(var pe=new PEReader(File.OpenRead("references/sain-4.5.1/SAIN.dll"))) {
            var md=pe.GetMetadataReader();var type=md.TypeDefinitions.Select(h=>md.GetTypeDefinition(h)).Single(t=>md.GetString(t.Name)=="RushEnemyAction");
            var methods=type.GetMethods().Select(h=>md.GetString(md.GetMethodDefinition(h).Name)).ToArray();
            foreach(var name in new[]{"updateRushBehavior","OnSteeringTicked","Stop"})Check(methods.Contains(name),"official native hook "+name);
            var fields=type.GetFields().Select(h=>md.GetString(md.GetFieldDefinition(h).Name)).ToArray();
            Check(fields.Contains("_enemy")&&fields.Contains("_updateMoveTime"),"official bookkeeping contract");
        }
        File.WriteAllText("artifacts/m05-corner-chase.json",fixture);
        Console.WriteLine($"M05: {n} checks passed (personal memory, one attempt, priorities, styles, physical movement, jump lifecycle, configuration)");return n;
    }
}
