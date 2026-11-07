using System;
using System.Collections.Generic;
using System.Linq;

namespace Comet.Core;

public sealed class OutcomeConfig
{
    public bool Enabled { get; set; } = true;
    public double EvaluationWindow { get; set; } = 6;
    public int MinimumResults { get; set; } = 2;
    public double PenaltyCap { get; set; } = 10;
    public double BonusCap { get; set; } = 5;
    public double Retention { get; set; } = 30;
    public void Validate()
    {
        CometConfig.Range(EvaluationWindow,2,12,nameof(EvaluationWindow));
        CometConfig.Range(MinimumResults,2,8,nameof(MinimumResults));
        CometConfig.Range(PenaltyCap,0,20,nameof(PenaltyCap));
        CometConfig.Range(BonusCap,0,10,nameof(BonusCap));
        CometConfig.Range(Retention,5,60,nameof(Retention));
    }
}

// Own state and already perceived contact only. No enemy health or live hidden position.
public sealed class OutcomeFrame
{
    public double Now, ContactAge, ContactLifetime=25, Health=1;
    public string Target="", Action="";
    public int Instance;
    public bool Scoped=true, HasContact=true, Owned, Hurt, Visible, ForcedRetreat, Paused;
    public DoorPoint Origin, Known, Destination;
    public bool HasDestination, Protected, ExposureKnown;
    public double Exposure;
    public int RangeBand => (Known-Origin).Length < 15 ? 0 : (Known-Origin).Length < 40 ? 1 : 2;
}

public sealed class OutcomeEffect
{
    public string Action {get;set;}="";
    public int Results {get;set;}
    public int Favorable {get;set;}
    public int Unfavorable {get;set;}
    public double Delta {get;set;}
    public double Applied {get;set;}
    public string Refusal {get;set;}="";
    public OutcomeEffect Copy()=>(OutcomeEffect)MemberwiseClone();
}
public sealed class OutcomeTrace
{
    public string State {get;set;}="idle";
    public string Reason {get;set;}="outcome-no-attempt";
    public string Target {get;set;}="";
    public int Instance {get;set;}
    public int Episode {get;set;}
    public string Action {get;set;}="";
    public string Result {get;set;}="pending";
    public double Elapsed {get;set;}
    public double Remaining {get;set;}
    public double ValidFor {get;set;}
    public double Age {get;set;}
    public double HealthLoss {get;set;}
    public double RecognizedDamage {get;set;}
    public bool Ambiguous {get;set;}
    public double X {get;set;}
    public double Y {get;set;}
    public double Z {get;set;}
    public string Choice {get;set;}="";
    public string[] Alternatives {get;set;}=Array.Empty<string>();
    public OutcomeEffect[] Effects {get;set;}=Array.Empty<OutcomeEffect>();
    public OutcomeTrace Copy() {var t=(OutcomeTrace)MemberwiseClone();t.Effects=Effects.Select(x=>x.Copy()).ToArray();t.Alternatives=(string[])Alternatives.Clone();return t;}
}

// A single observer. Value-only bounded history; never saved as gameplay knowledge.
public sealed class TacticalOutcomes
{
    private sealed class Attempt
    {
        internal OutcomeFrame Start=null!;
        internal double Began, Deadline, Last, ProtectedSince=-1, Damage, HealthLoss;
        internal bool Ambiguous;
        internal int Id;
    }
    private sealed class Result
    {
        internal OutcomeFrame Context=null!;
        internal double At;
        internal int Sign;
    }
    private readonly List<Result> history=new();
    private Attempt? active;
    private OutcomeFrame? latch;
    private double last=-1, detachedSince=-1, lastResultAt=-1;
    private int nextEpisode;
    public OutcomeTrace Trace {get;private set;}=new();
    public int HistoryCount=>history.Count;
    public bool HasAttempt=>active!=null;
    public static bool Finite(double x)=>!double.IsNaN(x)&&!double.IsInfinity(x);
    public static bool Supported(string a)=>a is "Hold" or "Shoot" or "Push" or "Search" or "Flank" or "Cover" or "FallBack";
    private static bool Near(DoorPoint a,DoorPoint b,double horizontal,double vertical=1)=>EncounterMemory.Near(a,b,horizontal,vertical);
    private static bool SameEnemy(OutcomeFrame a,OutcomeFrame b)=>a.Target==b.Target&&a.Instance==b.Instance;
    private static bool Context(OutcomeFrame a,OutcomeFrame b)=>SameEnemy(a,b)&&a.Visible==b.Visible&&a.Hurt==b.Hurt
        &&a.RangeBand==b.RangeBand&&Near(a.Origin,b.Origin,3)&&Near(a.Known,b.Known,3)
        &&a.HasDestination==b.HasDestination&&(!a.HasDestination||Near(a.Destination,b.Destination,2));
    private static bool Continuous(OutcomeFrame a,OutcomeFrame b)=>SameEnemy(a,b)&&a.Action==b.Action
        &&Near(a.Known,b.Known,3)&&a.HasDestination==b.HasDestination&&(!a.HasDestination||Near(a.Destination,b.Destination,2));
    private static OutcomeFrame Copy(OutcomeFrame f)=>new(){Now=f.Now,Target=f.Target,Instance=f.Instance,Action=f.Action,
        Origin=f.Origin,Known=f.Known,Destination=f.Destination,HasDestination=f.HasDestination,Visible=f.Visible,Hurt=f.Hurt,
        Health=f.Health,Exposure=f.Exposure,ExposureKnown=f.ExposureKnown,Protected=f.Protected,ContactLifetime=f.ContactLifetime};
    public void Clear(string reason="outcome-ended")
    {
        if(history.Count==0&&active==null&&latch==null&&Trace.State=="ended"&&Trace.Reason==reason)return;
        history.Clear();active=null;latch=null;last=detachedSince=lastResultAt=-1;
        Trace=Trace.Copy();Trace.State="ended";Trace.Reason=reason;Trace.Result="neutral";Trace.Effects=Array.Empty<OutcomeEffect>();Trace.ValidFor=Trace.Remaining=0;
    }
    public void Retain(ISet<string> known)
    {
        for(int i=history.Count-1;i>=0;i--)if(!known.Contains(history[i].Context.Target))history.RemoveAt(i);
        if(active!=null&&!known.Contains(active.Start.Target))Clear("outcome-native-forgetting");
    }
    public void Hit(double now,string recognizedTarget,int instance,double damage)
    {
        if(active==null||!Finite(now)||!Finite(damage)||damage<=0||now<active.Began||now>active.Deadline||now-active.Last>.35)return;
        if(recognizedTarget==active.Start.Target&&instance==active.Start.Instance)active.Damage+=damage;
        else active.Ambiguous=true;
    }
    public OutcomeTrace Observe(OutcomeFrame f,OutcomeConfig c)
    {
        if(!c.Enabled||!f.Scoped){Clear("outcome-out-of-scope");return Trace;}
        if(!Finite(f.Now)||!Finite(f.ContactAge)||!Finite(f.ContactLifetime)||!Finite(f.Health)||f.Health<0||f.Health>1
            ||(f.ExposureKnown&&(!Finite(f.Exposure)||f.Exposure<0||f.Exposure>1))
            ||!f.Origin.Finite||!f.Known.Finite||(f.HasDestination&&!f.Destination.Finite)||!f.HasContact||f.Target.Length==0
            ||f.ContactAge<0||f.ContactAge>=f.ContactLifetime||f.ContactLifetime<=0)
        {Clear("outcome-contact-ended-or-invalid");return Trace;}
        for(int i=history.Count-1;i>=0;i--)
            if(f.Now-history[i].At>=c.Retention||(history[i].Context.Target==f.Target&&history[i].Context.Instance!=f.Instance))history.RemoveAt(i);
        bool gap=last>=0&&(f.Now<last||f.Now-last>.35);last=f.Now;
        if(active!=null&&(gap||!SameEnemy(active.Start,f)))Resolve(f,c,"neutral",gap?"outcome-sampling-gap":"outcome-target-changed");
        if(gap){latch=Copy(f);detachedSince=-1;}
        if(active!=null)
        {
            var a=active; a.HealthLoss=Math.Max(a.HealthLoss,Math.Max(0,a.Start.Health-f.Health));
            bool retreat=a.Damage>0&&a.HealthLoss>0&&!a.Ambiguous&&f.ForcedRetreat&&(!f.Owned||f.Action!=a.Start.Action||f.Paused);
            if(f.Now>a.Deadline)Resolve(f,c,"neutral","outcome-window-inconclusive");
            else if(retreat)Resolve(f,c,"unfavorable","outcome-hit-forced-retreat");
            else if(!f.Owned||f.Paused||!Continuous(a.Start,f))Resolve(f,c,"neutral","outcome-interrupted-or-context-changed");
            else
            {
                a.Last=f.Now;
                // Arrival alone is not success. Require a real cover, lower observed exposure and a stable second without damage.
                bool safer=(a.Start.Action is "Cover" or "FallBack") && a.Start.HasDestination && a.Start.ExposureKnown && a.Start.Exposure>=.5
                    &&f.ExposureKnown&&f.Exposure<=.25&&f.Protected&&Near(f.Origin,a.Start.Destination,1.5)
                    &&a.HealthLoss==0&&a.Damage==0&&!a.Ambiguous;
                if(!safer)a.ProtectedSince=-1;else if(a.ProtectedSince<0)a.ProtectedSince=f.Now;
                if(a.ProtectedSince>=0&&f.Now-a.ProtectedSince>=1)Resolve(f,c,"favorable","outcome-protection-confirmed");
                else if(f.Now-a.Began>=c.EvaluationWindow)Resolve(f,c,"neutral","outcome-window-inconclusive");
                else Trace=Snapshot(a,f,c);
            }
        }
        // Once evaluated, a continuous action never becomes a new trial just because its timer elapsed.
        if(latch!=null)
        {
            if(!f.Owned||!Continuous(latch,f))
            {if(detachedSince<0)detachedSince=f.Now;if(f.Now-detachedSince>=.5){latch=null;detachedSince=-1;}}
            else detachedSince=-1;
        }
        if(active==null&&latch==null&&f.Owned&&!f.Paused&&Supported(f.Action))
        {
            active=new Attempt{Start=Copy(f),Began=f.Now,Deadline=f.Now+c.EvaluationWindow,Last=f.Now,Id=++nextEpisode};Trace=Snapshot(active,f,c);
        }
        Trace.ValidFor=Trace.State=="ended"||Trace.Target!=f.Target||Trace.Instance!=f.Instance?0:.35;
        if(active==null&&lastResultAt>=0){Trace.Age=Math.Max(0,f.Now-lastResultAt);Trace.Remaining=Math.Max(0,c.Retention-Trace.Age);}
        return Trace;
    }
    private OutcomeTrace Snapshot(Attempt a,OutcomeFrame f,OutcomeConfig c)=>new(){State="observing",Reason="outcome-observed-execution",
        Target=a.Start.Target,Instance=a.Start.Instance,Episode=a.Id,Action=a.Start.Action,Elapsed=Math.Max(0,f.Now-a.Began),
        Remaining=Math.Max(0,c.EvaluationWindow-(f.Now-a.Began)),ValidFor=.35,HealthLoss=a.HealthLoss,RecognizedDamage=a.Damage,
        Ambiguous=a.Ambiguous,X=a.Start.Origin.X,Y=a.Start.Origin.Y,Z=a.Start.Origin.Z};
    private void Resolve(OutcomeFrame f,OutcomeConfig c,string result,string reason)
    {
        var a=active!;Trace=Snapshot(a,f,c);Trace.State="resolved";Trace.Result=result;Trace.Reason=reason;
        Trace.Remaining=c.Retention;lastResultAt=f.Now;
        if(result!="neutral")
        {
            if(history.Count>=32)history.RemoveAt(0);
            history.Add(new Result{Context=a.Start,At=f.Now,Sign=result=="favorable"?1:-1});
        }
        latch=a.Start;active=null;
    }
    public OutcomeTrace Review(OutcomeFrame f,IEnumerable<(string Action,DoorPoint? Destination)> options,OutcomeConfig c)
    {
        var t=new OutcomeTrace{State="review",Reason="outcome-comparable-results",Target=f.Target,Instance=f.Instance,ValidFor=.35,
            Remaining=Math.Min(c.Retention,Math.Max(0,f.ContactLifetime-f.ContactAge)),X=f.Origin.X,Y=f.Origin.Y,Z=f.Origin.Z};
        if(!c.Enabled||!f.Scoped||!f.HasContact||!Finite(f.Now)||!Finite(f.ContactAge)||f.ContactAge<0||!Finite(f.ContactLifetime)||f.ContactAge>=f.ContactLifetime
            ||!f.Origin.Finite||!f.Known.Finite||f.Target.Length==0){t.ValidFor=t.Remaining=0;return t;}
        var effects=new List<OutcomeEffect>(7);var q=Copy(f);int count=0;
        foreach(var option in options)
        {
            if(count++==7)break;
            q.Action=option.Action;q.HasDestination=option.Destination.HasValue;q.Destination=option.Destination??default;
            int good=0,bad=0;
            foreach(var x in history)if(f.Now>=x.At&&f.Now-x.At<c.Retention&&x.Context.Action==q.Action&&Context(x.Context,q))
                {if(x.Sign>0)good++;else bad++;}
            int matches=good+bad;
            double delta=matches>=c.MinimumResults?(good*c.BonusCap-bad*c.PenaltyCap)/matches:0;
            effects.Add(new OutcomeEffect{Action=q.Action,Results=matches,Favorable=good,Unfavorable=bad,Delta=delta,
                Refusal=matches<c.MinimumResults?"outcome-insufficient-comparable-results":""});
        }
        t.Effects=effects.ToArray();return t;
    }
}
