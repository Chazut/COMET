using System;
using System.Collections.Generic;

namespace Comet.Core;

public sealed class SilentSearchPersonality
{
    public bool Enabled { get; set; } = true;
    public double Delay { get; set; } = 15;
}
public sealed class SilentSearchConfig
{
    public bool Enabled { get; set; } = true;
    public double ScoreBonus { get; set; } = 15;
    public Dictionary<string,SilentSearchPersonality> Personalities { get; set; } = new()
    {
        ["Normal"]=new(){Delay=15}, ["Chad"]=new(){Delay=15}, ["GigaChad"]=new(){Delay=15},
        ["Wreckless"]=new(){Delay=15}, ["Rat"]=new(){Delay=15}, ["SnappingTurtle"]=new(){Delay=15},
        ["Timmy"]=new(){Delay=15}, ["Coward"]=new(){Enabled=false,Delay=15}
    };
    public void Validate()
    {
        CometConfig.Range(ScoreBonus,0,25,nameof(ScoreBonus));
        if(Personalities==null||Personalities.Count!=8)throw new ArgumentException("Expected eight silent search personalities.");
        foreach(var name in new[]{"Normal","Chad","GigaChad","Wreckless","Rat","SnappingTurtle","Timmy","Coward"})
        {
            if(!Personalities.TryGetValue(name,out var rule)||rule==null)throw new ArgumentException("Missing silent search personality: "+name);
            CometConfig.Range(rule.Delay,1,120,name+" silence delay");
        }
    }
}
public sealed class SilentSearchFrame
{
    public double Now, HeardAt, ContactAge, Lifetime=60, TimeBeforeSearch=40, X,Y,Z;
    public string Target="", Personality="Normal";
    public int Instance;
    public bool Scoped=true, Personal=true, Seen, HasContact=true, SearchAllowed=true, AudioAllowed=true,
        Arrived, Unsafe, Path=true, Failed;
}
public sealed class SilentSearchTrace
{
    public string State {get;set;}="inactive";
    public string Reason {get;set;}="silent-search-unavailable";
    public string Target {get;set;}="";
    public string Personality {get;set;}="";
    public string Choice {get;set;}="";
    public string SearchRefusal {get;set;}="";
    public int Instance {get;set;}
    public double HeardAt {get;set;}
    public double Silence {get;set;}
    public double Delay {get;set;}
    public double Remaining {get;set;}
    public double Bonus {get;set;}
    public double ValidFor {get;set;}=1.5;
    public double X {get;set;}
    public double Y {get;set;}
    public double Z {get;set;}
    public bool Eligible {get;set;}
    public bool Reconsider {get;set;}
    public bool Accepted {get;set;}
    public bool ActionObserved {get;set;}
    public bool Walking {get;set;}
    public bool MovementObserved {get;set;}
    public SilentSearchTrace Copy()=>(SilentSearchTrace)MemberwiseClone();
}
public static class SilentSearchPolicy
{
    public static SilentSearchTrace Evaluate(SilentSearchFrame f,SilentSearchConfig c)
    {
        var t=new SilentSearchTrace{Target=f.Target,Personality=f.Personality,Instance=f.Instance};
        if(!c.Enabled||!f.Scoped){t.Reason="silent-search-out-of-scope";return t;}
        if(!f.HasContact||f.Target.Length==0||f.Seen){t.Reason="silent-search-not-personal-heard-only";return t;}
        if(!ImpactReaction.Finite(f.Now)||!ImpactReaction.Finite(f.HeardAt)||f.HeardAt<0||f.HeardAt>f.Now
            ||!ImpactReaction.Finite(f.ContactAge)||f.ContactAge<0||!ImpactReaction.Finite(f.Lifetime)||f.Lifetime<=0
            ||!ImpactReaction.Finite(f.X)||!ImpactReaction.Finite(f.Y)||!ImpactReaction.Finite(f.Z))
        {t.Reason="silent-search-invalid-evidence";return t;}
        t.HeardAt=f.HeardAt;t.Silence=f.Now-f.HeardAt;t.X=f.X;t.Y=f.Y;t.Z=f.Z;
        t.Remaining=Math.Max(0,f.Lifetime-Math.Max(f.ContactAge,t.Silence));t.ValidFor=Math.Min(1.5,t.Remaining);
        // Native EnemyKnown owns forgetting; no additional COMET expiry.
        t.ValidFor=1.5;
        if(!c.Personalities.TryGetValue(f.Personality,out var rule)||!rule.Enabled){t.Reason="silent-search-personality-disabled";return t;}
        t.Delay=15;
        if(!f.SearchAllowed){t.Reason="sain-personality-search-disabled";return t;}
        if(f.AudioAllowed&&f.TimeBeforeSearch<=15){t.Reason="native-search-already-allowed";return t;}
        if(f.Arrived)
        {t.State="blocked";t.Reason=f.Arrived?"silent-search-already-searched":f.Failed?"silent-search-failed-destination":!f.Path?"silent-search-no-complete-path":"silent-search-native-danger";return t;}
        if(t.Silence<t.Delay){t.State="waiting";t.Reason="silent-search-waiting";return t;}
        t.State="ready";t.Reason="silent-search-reevaluate";t.Eligible=true;t.Bonus=c.ScoreBonus;return t;
    }
}
// One re-evaluation per real personal observation, even after target churn or ordinary handoff.
// No timer refresh, fake contact or repeated probability roll. Bounded and value-only.
public sealed class SilentSearchReviews
{
    private readonly Dictionary<string,(int instance,double stamp)> consumed=new();
    public int Count=>consumed.Count;
    public bool TryReview(SilentSearchTrace t)
    {
        if(!t.Eligible||string.IsNullOrEmpty(t.Target)||!ImpactReaction.Finite(t.HeardAt)||t.HeardAt<0)return false;
        if(consumed.TryGetValue(t.Target,out var old))
        {if(old.instance==t.Instance&&t.HeardAt<=old.stamp)return false;}

        consumed[t.Target]=(t.Instance,t.HeardAt);return true;
    }
}
