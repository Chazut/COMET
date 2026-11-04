using System;
using System.Collections.Generic;

namespace Comet.Core;

public sealed class SearchAbandonmentConfig
{
    public bool Enabled { get; set; } = true;
    public double Silence { get; set; } = 20;
    public double Inspection { get; set; } = 3;
    public double Radius { get; set; } = 3;
    public double FailedMemory { get; set; } = 30;
    public void Validate()
    {
        CometConfig.Range(Silence,5,120,nameof(Silence));
        CometConfig.Range(Inspection,1,15,nameof(Inspection));
        CometConfig.Range(Radius,1,5,nameof(Radius));
        CometConfig.Range(FailedMemory,0,120,nameof(FailedMemory));
    }
}
public sealed class SearchAbandonmentFrame
{
    public double Now, Stamp, X,Y,Z, Distance;
    public string Target="";
    public int Instance;
    public bool Scoped=true, NativeKnown=true, Active=true, PersonalSearch=true, Visible, Unsafe,
        SameFloor=true, ClearView=true, Sampled=true, Facing=true;
}
public sealed class SearchAbandonmentTrace
{
    public string State {get;set;}="inactive";
    public string Reason {get;set;}="search-check-unavailable";
    public string Target {get;set;}="";
    public double Silence {get;set;}
    public double RequiredSilence {get;set;}
    public double Inspected {get;set;}
    public double RequiredInspection {get;set;}
    public double Distance {get;set;}
    public double MemoryRemaining {get;set;}
    public double ValidFor {get;set;}=1.5;
    public double X {get;set;}
    public double Y {get;set;}
    public double Z {get;set;}
    public bool ForgetRequested {get;set;}
    public bool ForgetObserved {get;set;}
    public int Interruptions {get;set;}
}
// Per observer, bounded value-only history. Never keeps a native Enemy/Place or live position.
public sealed class SearchAbandonment
{
    private sealed class Failed { public int Instance; public double Stamp,X,Y,Z,Until; }
    private readonly Dictionary<string,Failed> failed=new();
    private SearchAbandonmentFrame? previous;
    private double inspected;
    private int interruptions;
    public SearchAbandonmentTrace? Current {get;private set;}
    public int RememberedCount=>failed.Count;
    public bool Remembers(string target)=>failed.ContainsKey(target);
    public void ForgetFailure(string target)=>failed.Remove(target);
    public void Clear() {failed.Clear();previous=null;inspected=0;interruptions=0;Current=null;}
    public void Interrupt(string reason)
    {
        if(previous!=null)interruptions++;
        previous=null;inspected=0;
        if(Current!=null){Current.State="ended";Current.Reason=reason;Current.ValidFor=0;Current.ForgetRequested=false;Current.Interruptions=interruptions;}
    }
    private static bool Same(SearchAbandonmentFrame a,SearchAbandonmentFrame b)=>
        a.Target==b.Target&&a.Instance==b.Instance&&a.Stamp==b.Stamp&&a.X==b.X&&a.Y==b.Y&&a.Z==b.Z;
    private static bool Same(Failed a,SearchAbandonmentFrame b)=>
        a.Instance==b.Instance&&a.Stamp==b.Stamp&&a.X==b.X&&a.Y==b.Y&&a.Z==b.Z;
    private void Prune(double now)
    {var gone=new List<string>();foreach(var p in failed)if(p.Value.Until<=now)gone.Add(p.Key);foreach(var key in gone)failed.Remove(key);}
    public SearchAbandonmentTrace Evaluate(SearchAbandonmentFrame f,SearchAbandonmentConfig c)
    {
        var t=new SearchAbandonmentTrace{Target=f.Target,RequiredSilence=c.Silence,RequiredInspection=c.Inspection};
        Current=t;
        if(!ImpactReaction.Finite(f.Now)||!ImpactReaction.Finite(f.Stamp)||f.Stamp<0||f.Stamp>f.Now||string.IsNullOrEmpty(f.Target)
            ||!ImpactReaction.Finite(f.X)||!ImpactReaction.Finite(f.Y)||!ImpactReaction.Finite(f.Z)||!ImpactReaction.Finite(f.Distance)||f.Distance<0)
        {Interrupt("search-check-invalid-evidence");return t;}
        Prune(f.Now);t.Silence=f.Now-f.Stamp;t.X=f.X;t.Y=f.Y;t.Z=f.Z;t.Distance=f.Distance;
        if(failed.TryGetValue(f.Target,out var memory)&&(!Same(memory,f)||f.Visible))failed.Remove(f.Target);
        if(!c.Enabled||!f.Scoped){Clear();t.State="ended";t.Reason="search-check-out-of-scope";t.ValidFor=0;return t;}
        if(f.Visible||f.Unsafe){Interrupt(f.Visible?"search-check-new-visual":"search-check-native-priority");return t;}
        // A native expiry always wins. This policy only changes true to false, never false to true.
        if(!f.NativeKnown){Interrupt("search-check-native-expiry");return t;}
        if(failed.TryGetValue(f.Target,out memory)&&Same(memory,f))
        {previous=null;inspected=0;t.State="requested";t.Reason="search-check-same-failed-evidence";t.MemoryRemaining=memory.Until-f.Now;t.ForgetRequested=true;return t;}
        if(!f.Active||!f.PersonalSearch){Interrupt("search-check-not-personal-search");return t;}
        if(!f.Sampled||f.Distance>c.Radius||!f.SameFloor||!f.ClearView||!f.Facing)
        {Interrupt(f.Distance>c.Radius?"search-check-too-far":!f.SameFloor?"search-check-other-floor":!f.Facing?"search-check-not-looking":!f.Sampled?"search-check-probe-deferred":"search-check-obstructed");return t;}
        bool same=previous!=null&&Same(previous,f);
        double dt=same?f.Now-previous!.Now:0;
        if(!same||dt<0||dt>.75) {if(previous!=null)interruptions++;inspected=0;}
        else inspected=Math.Min(c.Inspection,inspected+dt);
        // Copy values: callers can reuse/mutate their frame without altering history.
        previous=new(){Now=f.Now,Stamp=f.Stamp,Target=f.Target,Instance=f.Instance,X=f.X,Y=f.Y,Z=f.Z};
        t.Inspected=inspected;t.Interruptions=interruptions;
        t.State=inspected==0?"reached":"checking";t.Reason="search-check-local-inspection";
        if(inspected>=c.Inspection&&t.Silence>=c.Silence)
        {t.State="requested";t.Reason="search-check-position-empty";t.ForgetRequested=true;}
        else if(inspected>=c.Inspection)t.Reason="search-check-waiting-for-silence";
        return t;
    }
    // Called only after SAIN actually publishes unknown and clears its known places.
    public bool ObserveForgotten(SearchAbandonmentFrame f,SearchAbandonmentConfig c,bool nativeUnknown)
    {
        if(!nativeUnknown||Current?.ForgetRequested!=true||Current.Target!=f.Target)return false;
        Current.State="abandoned";Current.ForgetObserved=true;Current.ValidFor=3;
        if(c.FailedMemory>0&&(failed.ContainsKey(f.Target)||failed.Count<32))
        {
            // Repeated old evidence cannot move the original expiry forward.
            if(!failed.TryGetValue(f.Target,out var m)||!Same(m,f))
                failed[f.Target]=new(){Instance=f.Instance,Stamp=f.Stamp,X=f.X,Y=f.Y,Z=f.Z,Until=f.Now+c.FailedMemory};
            Current.MemoryRemaining=Math.Max(0,failed[f.Target].Until-f.Now);
        }
        previous=null;inspected=0;return true;
    }
}
