using System;
using System.Collections.Generic;

namespace Comet.Core;

public sealed class QuietApproachConfig
{
    public bool Enabled { get; set; } = true;
    public double WalkDistance { get; set; } = 30;
    public double CrouchDistance { get; set; } = 8;
    public double WalkExitMargin { get; set; } = 3;
    public double CrouchExitMargin { get; set; } = 2;
    public double Budget { get; set; } = 20;
    public double ContactAge { get; set; } = 10;
    public double SeenDelay { get; set; } = 3;
    public double FireDelay { get; set; } = 5;
    public double WalkSpeed { get; set; } = .7;
    public double CrouchSpeed { get; set; } = .45;
    public double CrouchPose { get; set; } = .6;
    public double LootBonus { get; set; } = .15;
    public double LootCap { get; set; } = .95;
    public double LootWindow { get; set; } = 2;
    public double ResetSilence { get; set; } = 30;
    public Dictionary<string,double> Chances { get; set; } = new Dictionary<string,double> {
        ["Rat"]=.9,["SnappingTurtle"]=.9,["Coward"]=.9,["Timmy"]=.8,["Normal"]=.7,["Chad"]=.5,["GigaChad"]=.3,["Wreckless"]=.15 };
    public void Validate()
    {
        CometConfig.Range(WalkDistance,5,60,nameof(WalkDistance)); CometConfig.Range(CrouchDistance,1,WalkDistance,nameof(CrouchDistance));
        CometConfig.Range(WalkExitMargin,0,10,nameof(WalkExitMargin)); CometConfig.Range(CrouchExitMargin,0,5,nameof(CrouchExitMargin));
        if(CrouchDistance+CrouchExitMargin>WalkDistance)throw new ArgumentException("Crouch exit must not exceed walk entry.");
        CometConfig.Range(Budget,1,40,nameof(Budget)); CometConfig.Range(ContactAge,1,30,nameof(ContactAge));
        CometConfig.Range(SeenDelay,0,10,nameof(SeenDelay)); CometConfig.Range(FireDelay,1,15,nameof(FireDelay));
        CometConfig.Range(WalkSpeed,.4,1,nameof(WalkSpeed)); CometConfig.Range(CrouchSpeed,.25,WalkSpeed,nameof(CrouchSpeed));
        CometConfig.Range(CrouchPose,.4,1,nameof(CrouchPose)); CometConfig.Range(LootBonus,0,.3,nameof(LootBonus));
        CometConfig.Range(LootCap,0,1,nameof(LootCap)); CometConfig.Range(LootWindow,.5,5,nameof(LootWindow));
        CometConfig.Range(ResetSilence,20,120,nameof(ResetSilence));
        if(Chances==null || Chances.Count!=8)throw new ArgumentException("Missing quiet approach personality chances.");
        foreach(var name in new[]{"Rat","SnappingTurtle","Coward","Timmy","Normal","Chad","GigaChad","Wreckless"})
            if(!Chances.TryGetValue(name,out var value))throw new ArgumentException("Missing "+name);else CometConfig.Range(value,0,1,name);
    }
}

public sealed class QuietFrame
{
    public double Now, Age, Path, SinceSeen=double.MaxValue, SinceFire=double.MaxValue, X,Y,Z;
    public string Target="", Personality="Normal", Action="", Source="";
    public int TargetInstance;
    public bool Executing = true;
    public bool Scoped, Enabled, HasThreat, HasContact, Complete, Visible, OtherVisibleThreat, Urgent, Panic, Suppressed, Paused, Blocked, LootHeard;
}
public sealed class QuietTrace
{
    public string State { get; set; }="normal";
    public string Reason { get; set; }="quiet-no-contact";
    public string Target { get; set; }="";
    public string Action { get; set; }="";
    public string Source { get; set; }="";
    public string Personality { get; set; }="";
    public long Encounter { get; set; }
    public long Draw { get; set; }
    public double At { get; set; }
    public double? Age { get; set; }
    public double? Path { get; set; }
    public double? Chance { get; set; }
    public double? Roll { get; set; }
    public bool LootBonus { get; set; }
    public double Used { get; set; }
    public double Remaining { get; set; }
    public double Stalled { get; set; }
    public double SpeedCap { get; set; }=1;
    public double PoseCap { get; set; }=1;
    public bool Active { get; set; }
    public bool Applied { get; set; }
    public double ValidFor { get; set; }
    public double? NativeSpeed { get; set; }
    public double? AppliedSpeed { get; set; }
    public double? NativePose { get; set; }
    public double? AppliedPose { get; set; }
    public QuietTrace Copy()=>(QuietTrace)MemberwiseClone();
}

// Per-PMC encounter memory. Target/action changes cannot refund time or reroll a previous target.
public sealed class QuietApproach
{
    private readonly QuietApproachConfig config;
    private readonly Func<double> random;
    private sealed class Draw { public double Roll,Chance; public bool Loot; public long Index; }
    private readonly Dictionary<string,Draw> draws=new Dictionary<string,Draw>();
    private readonly List<(double X,double Y,double Z)> trail=new List<(double,double,double)>();
    private double last=-1,silence=-1,used,stalled;
    private bool previousActive, exhausted, blocked;
    private string band="normal",bandTarget="";
    private long encounter=1,drawIndex;
    public QuietTrace Current { get; private set; }=new QuietTrace();
    public int RememberedTargets=>draws.Count;
    public QuietApproach(QuietApproachConfig config,Func<double> random){this.config=config;this.random=random;}
    private static bool Finite(double n)=>!double.IsNaN(n)&&!double.IsInfinity(n);
    public void Clear(){draws.Clear();trail.Clear();last=silence=-1;used=stalled=0;previousActive=exhausted=blocked=false;band="normal";bandTarget="";encounter++;Current=new QuietTrace();}
    public QuietTrace Stop(double now,string reason)
    {
        previousActive=false;band="normal";bandTarget="";stalled=0;trail.Clear();last=Finite(now)?now:last;
        var t=Current.Copy();t.At=Finite(now)?now:0;t.Active=t.Applied=false;t.State="normal";t.Reason=reason;t.ValidFor=0;Current=t;return t;
    }
    public QuietTrace Evaluate(QuietFrame f)
    {
        if(!Finite(f.Now)||f.Now<0||!Finite(f.X)||!Finite(f.Y)||!Finite(f.Z))return Stop(f.Now,"quiet-invalid-observation");
        if(last>f.Now) { exhausted=true; return Stop(f.Now,"quiet-clock-reset"); }
        double dt=last<0?0:Math.Max(0,Math.Min(1,f.Now-last));last=f.Now;
        if(!f.HasThreat){if(silence<0)silence=f.Now;else if(f.Now-silence>=config.ResetSilence){Clear();last=f.Now;silence=f.Now;}}
        else silence=-1;
        var t=new QuietTrace { At=f.Now,Target=f.Target,Action=f.Action,Source=f.Source,Personality=f.Personality,Encounter=encounter,
            Age=Finite(f.Age)&&f.Age>=0?f.Age:(double?)null,Path=Finite(f.Path)&&f.Path>=0?f.Path:(double?)null,
            Used=used,Remaining=Math.Max(0,config.Budget-used),Stalled=stalled };
        QuietTrace Reject(string reason){t.Reason=reason;previousActive=false;band="normal";bandTarget="";stalled=0;trail.Clear();Current=t;return t;}
        if(!f.Scoped||!f.Enabled||!config.Enabled)return Reject("quiet-out-of-scope");
        if(!Finite(f.SinceSeen)||!Finite(f.SinceFire))return Reject("quiet-invalid-observation");
        if(f.Visible||f.OtherVisibleThreat||f.SinceSeen<config.SeenDelay)return Reject("quiet-visible-or-recent-fight");
        if(f.Urgent||f.Panic||f.Suppressed||f.SinceFire<config.FireDelay)return Reject("quiet-urgent");
        if(f.Action!="Search"&&f.Action!="MoveToEngage")return Reject("quiet-other-action");
        if(f.Paused)return Reject("quiet-native-pause");
        if(!f.HasContact||f.Target.Length==0||!t.Age.HasValue||f.Age>config.ContactAge)return Reject("quiet-stale-contact");
        if(f.Blocked){blocked=true;return Reject("quiet-progress-blocked");}
        if(blocked)return Reject("quiet-stalled-encounter");
        if(exhausted||used>=config.Budget){exhausted=true;return Reject("quiet-budget-ended");}
        if(!f.Complete||!t.Path.HasValue)return Reject("quiet-no-complete-route");
        string targetKey=f.Target+":"+f.TargetInstance;
        bool same=bandTarget==targetKey;
        if(f.Path>config.WalkDistance+(same&&band!="normal"?config.WalkExitMargin:0))return Reject("quiet-too-far");
        if(!draws.TryGetValue(targetKey,out var d))
        {
            if(draws.Count>=32)return Reject("quiet-target-memory-full");
            double chance=config.Chances.TryGetValue(f.Personality,out var p)?p:config.Chances["Normal"];
            // Zero chance stays disabled. A bonus never lowers a user's existing chance.
            double bonus=f.LootHeard&&chance>0?Math.Max(0,Math.Min(config.LootCap,chance+config.LootBonus)-chance):0;
            double roll=random();if(!Finite(roll)||roll<0||roll>=1)return Reject("quiet-invalid-roll");
            d=new Draw{Chance=chance+bonus,Roll=roll,Loot=bonus>0,Index=++drawIndex};draws.Add(targetKey,d);
        }
        t.Roll=d.Roll;t.Chance=d.Chance;t.LootBonus=d.Loot;t.Draw=d.Index;
        if(d.Roll>=d.Chance)return Reject("quiet-roll-declined");
        if(previousActive && f.Executing)
        {
            used=Math.Min(config.Budget,used+dt);
            bool moved=trail.Count==0;
            if(!moved){moved=true;foreach(var q in trail){double x=q.X-f.X,y=q.Y-f.Y,z=q.Z-f.Z;if(x*x+y*y+z*z<.75*.75){moved=false;break;}}}
            if(moved){if(trail.Count==32)trail.RemoveAt(0);trail.Add((f.X,f.Y,f.Z));stalled=0;}
            else stalled+=dt;
        }
        else {trail.Clear();trail.Add((f.X,f.Y,f.Z));stalled=0;}
        t.Used=used;t.Remaining=Math.Max(0,config.Budget-used);t.Stalled=stalled;
        if(used>=config.Budget){exhausted=true;return Reject("quiet-budget-ended");}
        if(stalled>=5){blocked=true;return Reject("quiet-no-progress");}
        band=f.Path<=config.CrouchDistance+(same&&band=="crouch"?config.CrouchExitMargin:0)?"crouch":"walk";
        bandTarget=targetKey;previousActive=true;
        t.State=band;t.Active=true;t.Reason=band=="crouch"?"quiet-close-approach":"quiet-approach";
        t.SpeedCap=band=="crouch"?config.CrouchSpeed:config.WalkSpeed;t.PoseCap=band=="crouch"?config.CrouchPose:1;
        Current=t;return t;
    }
}
