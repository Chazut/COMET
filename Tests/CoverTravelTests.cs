using Comet.Core;
using Comet.Server;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.Json;

internal static class CoverTravelTests
{
    public static int Run()
    {
        int n=0;
        void Check(bool v,string why){if(!v)throw new Exception("M09: "+why);n++;}
        void Invalid(Action a){try{a();}catch(ArgumentException){n++;return;}throw new Exception("M09 accepted invalid config");}
        var c=new CoverTravelConfig();c.Validate();
        CoverTravelFrame F(double now=10)=>new(){Now=now,ObservedAt=now-.5,GeometryAt=now,Target="enemy",Context="cover-a",
            Position=new(0,0,0),Known=new(0,0,8),Goal=new(10,0,0),Length=10,Turn=45,Exposure=.33,
            Geometry=true,Protected=true,Ready=true,CanSprint=true,NativeSprint=true,Speed=1,FacingDot=-.8};
        Check(c.Enabled&&c.CloseDistance==12&&c.VisionAge==4&&c.ShortDash==8&&c.FaceLimit==2&&c.Stability==.6&&c.Refresh==.25,"approved defaults");
        var p=new CoverTravelPolicy();var f=F();
        Check(p.Tick(f,c)=="face-walk","ready close threat, low exposure on actual route");
        Check(p.Trace!.Observed=="backpedaling"&&!p.Trace.WalkRequested,"observation differs from command");
        p.Requested(true,false);Check(p.Trace.WalkRequested&&!p.Trace.SprintRequested,"request explicit");
        f=F(10.7);f.Position=new(.2,0,0);Check(p.Tick(f,c)=="face-walk","continue progressing");
        f=F(12);f.Position=new(1,0,0);Check(p.Tick(f,c)=="native"&&p.Trace.Reason=="travel-facing-time-limit","hard two second limit");
        Check(p.Tick(F(20),c)=="native"&&p.Trace.Reason=="travel-facing-phase-spent","no repeated backward phases on same goal");
        p.End("travel-native-priority");Check(p.Tick(F(21),c)=="native","end does not rearm same context");
        f=F(22);f.Context="cover-b";Check(p.Tick(f,c)=="face-walk","new cover opportunity");
        p=new();p.Tick(F(),c);Check(p.Tick(F(10.81),c)=="native"&&p.Trace!.Reason=="travel-no-progress","stuck yields early");
        p=new();f=F();f.Length=8;Check(p.Tick(f,c)=="dash","8m real route protected sprint");
        f=F(10.2);f.Length=8.1;Check(p.Tick(f,c)=="dash"&&p.Trace!.Reason=="travel-stable-dash","stability near distance boundary");
        f=F(10.61);f.Length=8.1;Check(p.Tick(f,c)=="face-walk","choice updates after stability");
        f=F(10.7);f.Length=8;Check(p.Tick(f,c)=="face-walk"&&p.Trace!.Reason=="travel-stable-face-walk","walk stable with low exposure");
        f.Exposure=1;Check(p.Tick(f,c)=="dash","higher exposure immediately allows short protected dash");
        f=F(10.71);f.Block="travel-grenade-danger";Check(p.Tick(f,c)=="native","urgency bypasses stable dash");
        p=new();f=F();f.Length=20;f.Goal=new(1,0,0);f.Turn=120;
        Check(p.Tick(f,c)=="native"&&p.Trace!.Reason=="travel-sharp-route-turn","nearby Euclidean goal is not a short dash");
        f.Turn=30;f.Exposure=.8;Check(p.Tick(f,c)=="native"&&p.Trace.Reason=="travel-exposed-crossing","long exposed crossing retains native survival movement");
        foreach(var change in new Action<CoverTravelFrame>[] {f=>f.Block="medical",f=>f.Block="reload",f=>f.Block="non-pmc",f=>f.Block="ghost",f=>f.Block="client",f=>f.Ready=false,
            f=>f.Geometry=false,f=>f.GeometryAt=9,f=>f.ObservedAt=6,f=>f.Known=new(0,0,12.1),f=>f.Context="",f=>f.Target="",
            f=>f.Now=double.NaN,f=>f.ObservedAt=11,f=>f.GeometryAt=11,f=>f.Turn=double.NaN,f=>f.Length=double.PositiveInfinity,f=>f.Exposure=-1,
            f=>f.Position=new(double.NaN,0,0),f=>f.Goal=new(double.PositiveInfinity,0,0),f=>f.Speed=double.NaN,f=>f.FacingDot=2}) {
            p=new();p.Tick(F(),c);f=F(10.1);change(f);Check(p.Tick(f,c)=="native","unsafe input or priority releases immediately");
            Check(p.Trace!.ValidFor==0&&!p.Trace.WalkRequested&&!p.Trace.SprintRequested,"no active telemetry after rejection");
            Check(double.IsFinite(p.Trace.Length)&&double.IsFinite(p.Trace.ContactAge)&&double.IsFinite(p.Trace.X),"finite rejection telemetry");
        }
        p=new();f=F();f.Length=6;f.CanSprint=false;Check(p.Tick(f,c)=="face-walk","no impossible dash when exhausted");
        f.ActualSprint=true;f.Speed=4;Check(p.Tick(f,c)=="face-walk"&&p.Trace!.Observed=="sprinting","sprint inertia is not claimed as walking");
        f.ActualSprint=false;f.Speed=.1;Check(p.Tick(f,c)=="face-walk"&&p.Trace.Observed=="stationary","stationary not motion");
        f.Speed=1;f.FacingDot=.2;p.Tick(f,c);Check(p.Trace.Observed=="strafing","observed lateral movement");
        f.FacingDot=.9;p.Tick(f,c);Check(p.Trace.Observed=="walking-forward","observed forward movement");
        c.Enabled=false;Check(p.Tick(f,c)=="native","disable immediately");c.Enabled=true;
        p=new();p.Tick(F(),c);f=F(9);Check(p.Tick(f,c)=="native","clock rollback fails closed");
        foreach(var prop in typeof(CoverTravelConfig).GetProperties().Where(p=>p.PropertyType==typeof(double)))
            foreach(double value in new[]{double.NaN,double.PositiveInfinity,-1,999}){var bad=new CoverTravelConfig();prop.SetValue(bad,value);Invalid(bad.Validate);}
        Invalid(()=>new CoverTravelConfig{FaceLimit=.5,Stability=1}.Validate());Invalid(()=>new CometConfig{CoverTravel=null!}.Validate());
        Check(JsonSerializer.Deserialize<CometConfig>("{}")!.CoverTravel.CloseDistance==12,"old server configs inherit M09");
        var dir=Path.Combine(Path.GetTempPath(),"comet-m09-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(dir);var file=Path.Combine(dir,"config.json");
        try {
            var store=new ConfigStore(file);var saved=store.Read();saved.CoverTravel.CloseDistance=11;saved.CoverTravel.Enabled=false;store.Save(saved,saved.Revision);
            var read=new ConfigStore(file).Read();Check(read.CoverTravel.CloseDistance==11&&!read.CoverTravel.Enabled,"server persistence");
            string before=File.ReadAllText(file);read.CoverTravel.Refresh=0;Invalid(()=>store.Save(read,read.Revision));Check(File.ReadAllText(file)==before,"bad save leaves config intact");
        }finally {foreach(var path in new[]{file,file+".bak"})if(File.Exists(path))File.Delete(path);Directory.Delete(dir);}
        using(var pe=new PEReader(File.OpenRead("references/sain-4.5.1/SAIN.dll"))) {
            var md=pe.GetMetadataReader();var types=md.TypeDefinitions.Select(h=>md.GetTypeDefinition(h)).ToArray();
            foreach(var pair in new[]{("SAINCoverClass","CheckStartRun"),("SAINCoverClass","UpdateCover"),("SeekCoverAction","Stop"),("IBotPathData","RequestEndSprint"),("IBotPathData","RequestStartSprint")})
                Check(types.Single(t=>md.GetString(t.Name)==pair.Item1).GetMethods().Any(h=>md.GetString(md.GetMethodDefinition(h).Name)==pair.Item2),"official native hook "+pair);
        }
        var rng=new Random(909);
        for(int i=0;i<2000;i++) {
            p=new();p.Tick(F(),c);f=F(10+rng.NextDouble()*2.5);f.Position=new(rng.NextDouble(),0,0);f.Length=2+rng.NextDouble()*20;
            f.Turn=rng.NextDouble()*180;f.Exposure=rng.NextDouble();f.GeometryAt=f.Now-rng.NextDouble()*.5;f.CanSprint=i%5!=0;
            if(i%7==0)f.Block="urgent";
            string mode=p.Tick(f,c);
            Check(mode=="native"||CoverTravelPolicy.Gate(f,c)=="","random mixed stream obeys safety gates");
            Check(mode!="dash"||f.CanSprint&&f.Protected&&f.Length<=c.ShortDash+.5&&f.Turn<=60,"dash constraints");
            Check(mode!="face-walk"||f.Now<12&&f.Exposure<.67&&f.Turn<=100,"bounded facing");
        }
        Console.WriteLine($"M09: {n} assertions passed.");return n;
    }
}
