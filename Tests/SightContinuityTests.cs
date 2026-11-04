using Comet.Core;
using Comet.Server;
using System.Text.Json;
using System.Text.Json.Nodes;

internal static class SightContinuityTests
{
    internal static int Run()
    {
        int count = 0;
        void Check(bool ok, string why) { if (!ok) throw new Exception("A03: " + why); count++; }
        SightFrame Frame(string action = "Shoot", double distance = 5) => new() {
            Now = 10, Target = "seen-enemy", Action = action, Eligible = true, Visible = true, Observed = true,
            WeaponReady = true, PushPathValid = true, X = 4, Y = 2, Z = 1, Distance = distance, HitAt = 1
        };
        void Arm(SightContinuity e, SightFrame f)
        {
            double start = f.Now;
            for (int i = 0; i <= 5; i++) { f.Now = start + i * .1; e.Evaluate(f); }
        }
        foreach (var (distance, duration) in new[] { (5d,.8), (8d,.8), (8.01,1d), (45d,1d) })
        {
            var e = new SightContinuity(new()); var f = Frame(distance: distance); Arm(e,f);
            double last = f.Now; f.Visible = false; f.Now += .1;
            var hold = e.Evaluate(f);
            Check(hold != null && Math.Abs(hold.Until-last-duration)<1e-8, "range-specific default deadline");
            for (int i=1; i<=12; i++)
            {
                f.Now = last + i*.1;
                var result=e.Evaluate(f);
                Check((result != null)==(f.Now < last+duration), "expires at the absolute boundary");
            }
        }
        {
            var e = new SightContinuity(new()); var f = Frame();
            e.Evaluate(f); f.Visible=false; f.Now+=.1;
            Check(e.Evaluate(f)==null, "a single visible sample cannot arm continuity");
            f.Visible=true; f.Now+=.1; Arm(e,f); f.Visible=false; f.Now+=.1;
            var before=e.Evaluate(f)!;
            // Simulated hidden enemy motion, hearing updates and invalid hidden coordinates cannot change the snapshot.
            f.X=999; f.Y=double.NaN; f.Z=-777; f.Distance=.1; f.SeenAge=0; f.Now+=.1;
            var after=e.Evaluate(f)!;
            Check(after.X==4 && after.Y==2 && after.Z==1 && after.Distance==5, "hidden changes cannot move the frozen observation");
            Check(ReferenceEquals(before,after), "stable hidden samples do not allocate or mutate the hold");
        }
        foreach (Action<SightFrame> interrupt in new Action<SightFrame>[] {
            f=>f.Eligible=false, f=>f.Target="new-target", f=>f.Target="", f=>f.WeaponReady=false,
            f=>f.OtherThreat=true, f=>f.Urgent=true, f=>f.Hurt=true, f=>f.LowAmmo=true, f=>f.Suppressed=true,
            f=>f.HitAt=11, f=>f.Action="Cover", f=>f.Action="Push", f=>f.Now+=.31, f=>f.Now-=1,
            f=>f.Now=double.NaN, f=>f.HitAt=double.NaN
        })
        {
            var e=new SightContinuity(new());var f=Frame();Arm(e,f);f.Visible=false;f.Now+=.05;
            Check(e.Evaluate(f)!=null,"hold before interruption");interrupt(f);
            Check(e.Evaluate(f)==null && e.Current==null,"interruption immediately revokes the hold");
        }
        foreach (Action<SightFrame> invalid in new Action<SightFrame>[] {
            f=>f.Observed=false, f=>f.X=double.NaN,f=>f.Y=double.PositiveInfinity,f=>f.Z=double.NaN,
            f=>f.Distance=46,f=>f.Distance=-1,f=>f.SeenAge=.3,f=>f.SeenAge=-1,f=>f.SeenAge=double.NaN
        })
        {
            var e=new SightContinuity(new());var f=Frame();invalid(f);Arm(e,f);f.Visible=false;f.Now+=.1;
            Check(e.Evaluate(f)==null,"invalid or nonvisual knowledge cannot arm");
        }
        {
            var e=new SightContinuity(new());var f=Frame();Arm(e,f);f.Visible=false;f.Now+=.05;e.Evaluate(f);
            f.Target="different-hidden-enemy";f.Now+=.05;
            Check(e.Evaluate(f)==null && e.Reason=="sight-target-changed","target handoff keeps its diagnostic reason");
        }
        {
            var e=new SightContinuity(new());var f=Frame();Arm(e,f);
            double deadline=f.Now+.8; f.Now+=.1;f.Visible=false; e.Evaluate(f);
            // Repeated 100 ms peeks never provide 350 ms of continuous sight.
            for(int i=0;i<15;i++)
            {
                f.Now+=.1; f.Visible=i%2==0; f.X=i;
                var hold=e.Evaluate(f);
                if(hold!=null) Check(Math.Abs(hold.Until-deadline)<1e-8,"micro-peeks do not extend the original deadline");
                if(f.Now>=deadline) Check(hold==null,"micro-peeks cannot keep the plan alive indefinitely");
            }
            f.Visible=true; f.Now+=.1;Arm(e,f);double last=f.Now;f.Visible=false;f.Now+=.1;
            Check(Math.Abs(e.Evaluate(f)!.Until-last-.8)<1e-8,"stable reacquisition rearms a fresh window");
        }
        foreach(double distance in new[]{5d,12d})
        {
            var e=new SightContinuity(new());var f=Frame("Push",distance);Arm(e,f);f.Visible=false;f.Now+=.1;
            Check((e.Evaluate(f)!=null)==(distance<=8),"hidden push is close-range only");
            f.PushPathValid=false;f.Now+=.05;
            Check(e.Evaluate(f)==null,"lost or changed push path interrupts");
        }
        foreach(var action in new[]{"Cover","DogFight","AvoidGrenade","Search",""})
        {
            var e=new SightContinuity(new());var f=Frame(action);Arm(e,f);f.Visible=false;f.Now+=.1;
            Check(e.Evaluate(f)==null,"A03 never takes over native urgent/cover/other actions");
        }
        {
            var c=new CometConfig();var e=new SightContinuity(c);var f=Frame();Arm(e,f);f.Visible=false;f.Now+=.1;
            Check(e.Evaluate(f)!=null,"enabled initially");c.SightContinuity.Enabled=false;
            Check(e.Evaluate(f)==null,"feature toggle revokes");c.SightContinuity.Enabled=true;c.Enabled=false;
            f.Visible=true;Arm(e,f);f.Visible=false;f.Now+=.1;Check(e.Evaluate(f)==null,"global toggle revokes");
        }
        var defaults=new CometConfig();
        Check(defaults.SightContinuity.Enabled && defaults.SightContinuity.CloseDuration==.8
            && defaults.SightContinuity.MediumDuration==1 && defaults.SightContinuity.CloseDistance==8
            && defaults.SightContinuity.MaxDistance==45 && defaults.SightContinuity.StableSight==.35,"approved defaults");
        var legacy=JsonNode.Parse(JsonSerializer.Serialize(defaults))!.AsObject();legacy.Remove("SightContinuity");
        Check(JsonSerializer.Deserialize<CometConfig>(legacy.ToJsonString())!.SightContinuity.CloseDuration==.8,"old config gains defaults");
        string root=Path.Combine(Path.GetTempPath(),"COMET-A03-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        string configPath=Path.Combine(root,"config.json");var store=new ConfigStore(configPath);var saved=store.Read();saved.SightContinuity.CloseDuration=.6;saved.SightContinuity.Enabled=false;
        store.Save(saved,1);Check(store.Read().SightContinuity.CloseDuration==.6 && !store.Read().SightContinuity.Enabled,"server persistence");
        foreach(Action<CometConfig> invalid in new Action<CometConfig>[] {
            c=>c.SightContinuity=null!,c=>c.SightContinuity.CloseDuration=double.NaN,c=>c.SightContinuity.MediumDuration=2.1,
            c=>c.SightContinuity.CloseDistance=0,c=>c.SightContinuity.MaxDistance=7,c=>c.SightContinuity.StableSight=.1
        })
        {
            var c=store.Read();string before=File.ReadAllText(configPath);
            invalid(c);bool rejected=false;try { store.Save(c,c.Revision); } catch(ArgumentException){rejected=true;}
            Check(rejected,"invalid settings rejected");Check(File.ReadAllText(configPath)==before,"invalid save is atomic");
        }
        // Real production state machines under reproducible visibility/urgency/resource churn.
        var a=new SightContinuity(new());var b=new SightContinuity(new());var random=new Random(303);
        var input=Frame();
        for(int i=0;i<10000;i++)
        {
            input.Now=20+i*.05;input.Visible=random.Next(10)<8;input.OtherThreat=random.Next(100)==0;
            input.WeaponReady=random.Next(100)!=0;input.Target="target-"+i/1000;input.X=i;
            var x=a.Evaluate(input);var y=b.Evaluate(input);
            Check((x==null)==(y==null) && a.Reason==b.Reason,"deterministic continuity");
            if(x!=null) Check(!input.Visible && x.Until>input.Now && x.Target==input.Target && x.X<=i,"bounded dated snapshot");
        }
        return count;
    }
}
