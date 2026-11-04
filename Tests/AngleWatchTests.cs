using Comet.Core;
using Comet.Server;
using System.Text.Json;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

internal static class AngleWatchTests
{
    public static int Run()
    {
        int n=0;void Check(bool v,string why){if(!v)throw new Exception("M07: "+why);n++;}
        void Invalid(Action action){try{action();}catch(ArgumentException){n++;return;}throw new Exception("M07 invalid config accepted");}
        var c=new AngleWatchConfig();c.Validate();
        AngleCandidate A(string id="left",bool clear=true)=>new(){Id=id,Kind="corner",Aim=new(-1,1.2,3),Clear=clear,Side=-1,LeanSafe=true};
        AngleWatchFrame F(double now=10)=>new(){Now=now,ObservedAt=9,GeometryAt=now,Target="enemy-a",Source="personal-vision",Action="Search",
            Known=new(1,0,5),Position=new(0,0,0),Origin=new(0,1.2,0),NativeDirection=new(0,0,1),ObservedDirection=new(0,0,1),Moving=true,Candidates=new[]{A()}};
        Check(c.Enabled&&c.WallCorrection&&c.SafeLean&&c.CornerRange==6&&c.MovingLimit==4,"approved switches and preparation");
        Check(c.VisionAge==10&&c.HearingAge==5&&c.MovingWall==1.2&&c.StillWall==3&&c.HoldMinimum==1&&c.Refresh==.25&&c.PitchLimit==25,"approved ages and geometry");
        var p=new AngleWatch();Check(p.Tick(F(),c),"eligible unseen personal sighting");Check(p.Trace!.Requested&&!p.Trace.Aligned,"request differs from observed orientation");
        var f=F(10.1);f.Candidates=new[]{A("right"),A()};Check(p.Tick(f,c)&&p.Trace!.Candidate=="left","valid previous side wins minimum hold");
        f=F(10.2);f.Candidates=new[]{A("right"),A("left",false)};Check(p.Tick(f,c)&&p.Trace!.Candidate=="right","unsafe held angle never kept");
        f=F(10.3);f.Candidates=new[]{A("right")};f.ObservedDirection=f.Candidates[0].Aim-f.Origin;Check(p.Tick(f,c)&&p.Trace!.Aligned,"actual aligned gaze observed");
        f=F(10.4);f.Block="angle-native-fire-or-aim";Check(!p.Tick(f,c)&&p.Trace!.ValidFor==0,"fire preempts stable hold");
        foreach(var block in new[]{"angle-ineligible","angle-visible-or-no-enemy","angle-urgent-or-busy","angle-preserve-tactical-aim","angle-native-action-priority","angle-other-threat"}) {
            p=new();p.Tick(F(),c);f=F(10.1);f.Block=block;Check(!p.Tick(f,c)&&!p.Active&&p.Trace!.Reason==block,"scope/priority interruption "+block);
        }
        foreach(var change in new Action<AngleWatchFrame>[] {f=>f.Source="shared-heard",f=>f.Source="sain-last-known",f=>f.Target="",f=>f.Action="",f=>f.ObservedAt=11,f=>f.ObservedAt=double.NaN,f=>f.Now=double.PositiveInfinity,
            f=>f.Known=new(double.NaN,0,0),f=>f.GeometryAt=9.6,f=>f.GeometryAt=11,f=>f.NativeDirection=new(0,0,0),f=>f.ObservedDirection=new(double.NaN,0,0)}) {
            p=new();f=F();change(f);Check(!p.Tick(f,c),"bad/stale input fails closed");
        }
        f=F();f.ObservedAt=0;Check(!new AngleWatch().Tick(f,c),"vision expires at ten seconds");
        f=F();f.Source="personal-hearing";f.ObservedAt=5;Check(!new AngleWatch().Tick(f,c),"hearing expires at five seconds");
        f.ObservedAt=5.01;Check(new AngleWatch().Tick(f,c),"recent personal sound permits watching only");
        p=new();f=F();f.Source="personal-hearing";p.Tick(f,c);Check(p.Trace!.Source=="personal-hearing","sound source exposed in diagnostics");
        p=new();p.Tick(F(),c);f=F(10.1);f.Target="enemy-b";Check(p.Tick(f,c)&&p.Trace!.Episode==2&&p.Trace.Target=="enemy-b","rapid contact switch invalidates previous hold");
        p=new();p.Tick(F(),c);f=F(10.6);Check(p.Tick(f,c)&&p.Trace!.Episode==2,"callback gap starts fresh evidence episode");
        p=new();p.Tick(F(),c);for(int i=1;i<40;i++)Check(p.Tick(F(10+i*.1),c),"bounded moving preparation");
        Check(!p.Tick(F(14),c)&&p.Trace!.Reason=="angle-preparation-completed","four seconds maximum");
        f=F(14.1);f.ObservedAt=14;Check(!p.Tick(f,c),"new sound/sighting timestamp alone cannot spam same angle");
        f.Position=new(2.1,0,0);Check(p.Tick(f,c),"new area permits new opportunity");
        p=new();f=F();f.Candidates[0].Clear=false;Check(!p.Tick(f,c)&&p.Trace!.State=="ended"&&!p.Trace.Requested&&p.Trace.ValidFor==0,"refusal recorded without claiming orientation");
        p=new();f=F();f.Candidates[0].Aim=new(0,6,3);Check(!p.Tick(f,c),"steep unsupported angle rejected");
        f.Candidates[0].Elevation=true;Check(p.Tick(f,c)&&p.Trace!.RequestedPitch>25&&p.Trace.Elevation,"stairs or known balcony exempt from clamp");
        for(int pitch=-89;pitch<=89;pitch++) {
            var d=new DoorPoint(0,Math.Tan(pitch*Math.PI/180),1);var clipped=AngleWatch.ClampPitch(d,25);
            Check(Math.Abs(AngleWatch.Pitch(clipped))<=25.00001,"vertical bound");
            if(Math.Abs(pitch)<=25)Check(Math.Abs(clipped.Y-d.Y)<1e-8,"ordinary gaze unchanged");
        }
        p=new();f=F();f.Candidates[0].Kind="wall";Check(!p.Tick(f,c),"no wall correction if gaze not blocked");
        f.WallBlocked=true;Check(p.Tick(f,c)&&p.Trace!.Reason=="angle-wall-blocks-native-gaze","useful wall correction");
        c.WallCorrection=false;Check(!p.Tick(f,c),"wall switch honored");c.WallCorrection=true;
        c.SafeLean=false;p=new();Check(p.Tick(F(),c)&&p.Side==0,"orientation independent of optional lean");c.SafeLean=true;
        p=new();f=F();f.Candidates[0].LeanSafe=false;Check(p.Tick(f,c)&&p.Side==0,"unsafe lean does not prevent safe gaze");
        p=new();p.Tick(F(),c);c.Enabled=false;Check(!p.Tick(F(10.1),c),"disable releases active gaze");c.Enabled=true;
        foreach(var prop in typeof(AngleWatchConfig).GetProperties().Where(x=>x.PropertyType==typeof(double)))
            foreach(double v in new[]{double.NaN,double.PositiveInfinity,-1,999}) {var bad=new AngleWatchConfig();prop.SetValue(bad,v);Invalid(bad.Validate);}
        Invalid(()=>new CometConfig{AngleWatch=null!}.Validate());
        Check(JsonSerializer.Deserialize<CometConfig>("{}")!.AngleWatch.PitchLimit==25,"old server config inherits new defaults");
        var dir=Path.Combine(Path.GetTempPath(),"comet-m07-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(dir);
        var file=Path.Combine(dir,"config.json");try {
            var store=new ConfigStore(file);var saved=store.Read();saved.AngleWatch.CornerRange=5;saved=store.Save(saved,saved.Revision);
            Check(new ConfigStore(file).Read().AngleWatch.CornerRange==5,"server save/reload");
            string before=File.ReadAllText(file);saved.AngleWatch.Refresh=0;Invalid(()=>store.Save(saved,saved.Revision));Check(File.ReadAllText(file)==before,"invalid update atomic");
        }finally{foreach(var path in new[]{file,file+".bak"})if(File.Exists(path))File.Delete(path);Directory.Delete(dir);}
        using(var pe=new PEReader(File.OpenRead("references/sain-4.5.1/SAIN.dll"))) {
            var md=pe.GetMetadataReader();var type=md.TypeDefinitions.Select(h=>md.GetTypeDefinition(h)).Single(t=>md.GetString(t.Name)=="SAINSteeringClass");
            Check(type.GetMethods().Any(h=>md.GetString(md.GetMethodDefinition(h).Name)=="TickPlayerSteering"),"official steering hook exists");
            Check(type.GetFields().Any(h=>md.GetString(md.GetFieldDefinition(h).Name)=="_targetLookDirection"),"official desired direction exists");
        }
        // Long mixed stream: no stale/nonfinite geometry, expired contact or urgent action may retain a lease.
        var random=new Random(707);p=new();
        for(int i=0;i<10000;i++) {
            f=F(20+i*.05);f.ObservedAt=f.Now-random.NextDouble()*12;f.GeometryAt=f.Now-random.NextDouble()*.5;
            if(i%7==0)f.Block="angle-visible-or-no-enemy";if(i%11==0)f.Source="shared-heard";
            if(i%31==0)f.Target="other";bool active=p.Tick(f,c);
            Check(!active||AngleWatch.Gate(f,c)=="","adversarial stream respects gates");
            Check(!active||p.Trace!.ValidFor>0&&p.Trace.ValidFor<=.2&&p.Trace.Remaining>0,"replay lifetime bounded");
        }
        Console.WriteLine($"M07: {n} checks passed (evidence, priorities, stable safe angles, stairs, deadlines, config and hooks)");return n;
    }
}
