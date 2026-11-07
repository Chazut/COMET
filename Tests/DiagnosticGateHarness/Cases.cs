using System;
using System.Collections.Generic;
using System.Text.Json;
using Comet.Core;
static class Time { public static float time=10; public static float realtimeSinceStartup=>time; }
enum ESelfActionType { None, Reload }
enum Combat { Hold, Search }
class Decision { public ESelfActionType CurrentSelfDecision; public Combat CurrentCombatDecision; }
class BotComponent { public int Reads; public Decision Data=new(); public Decision Decision {get {Reads++;return Data;}} public Enemy? GoalEnemy; }
class Enemy { public BotComponent Bot=new(); public string EnemyProfileId="target"; }
class EnemyKnownChecker { public Enemy Enemy=new(); }
class Policy<T> where T:new() { public T Trace=new(); }
partial class PistolSwapRuntime { public Policy<PistolSwapTrace> Policy=new(); public float LogAt; }
partial class WeaponRangeRuntime { public Policy<WeaponRangeTrace> Policy=new(); public float LogAt; }
partial class DoorTransitRuntime { public Policy<DoorTransitTrace> Policy=new(); public float LogAt; }
class BotState {
    public SilentSearchTrace SilentTrace=new();public float SilentLogAt=-100;
    public PistolSwapRuntime PistolSwap=new(); public WeaponRangeRuntime WeaponRange=new(); public DoorTransitRuntime Transit=new();
    public string ProfileId="pmc",Name="bot",Id="instance";
    public SearchAbandonment Abandonment=new(); public SearchAbandonmentFrame? AbandonPending; public float AbandonProbeAt;
}
class Sink { public List<string> Lines=new(); public void Write(string json)=>Lines.Add(json); }
static class Diagnostics { public static string Serialize(object x)=>JsonSerializer.Serialize(x,new JsonSerializerOptions{IncludeFields=true}); }
static class Api { public static class Decisions { public static List<string> Lines=new(); public static void Publish(string json,Action<Exception> error)=>Lines.Add(json); } }
class Log { public void LogWarning(string message)=>throw new Exception(message); }
partial class Probe {
    static Probe Instance=null!;
    CometConfig config=new(); Sink diagnostics=new(); Log Logger=new(); bool subscriberWarned; string session="test"; long sequence;
    Dictionary<BotComponent,BotState> states=new();
    static Enemy abandonmentEnemy(EnemyKnownChecker c)=>c.Enemy;
    public static void Run() {
        int checks=0;void Check(bool pass,string why){checks++;if(!pass)throw new Exception(why);}
        var p=new Probe();Instance=p;var s=new BotState();var b=new BotComponent();
        s.Transit.Policy.Trace.Door="door";s.Transit.Policy.Trace.State="moving";
        void Record(){p.RecordPistolSwap(b,s);p.RecordWeaponRange(b,s);p.RecordDoorTransit(s);}
        Record();Check(p.sequence==3&&Api.Decisions.Lines.Count==3,"initial events reach file and RR");
        for(int i=0;i<2000;i++)Record();
        int reads=b.Reads;long before=GC.GetAllocatedBytesForCurrentThread();
        for(int i=0;i<100000;i++)Record();
        long allocated=GC.GetAllocatedBytesForCurrentThread()-before;
        Check(allocated==0,"unchanged diagnostic gate allocates "+allocated+" bytes");
        Check(b.Reads==reads,"suppressed diagnostics must not format native action enums");
        Check(p.sequence==3,"duplicate events suppressed");
        Time.time+=.25f;Record();Check(p.sequence==6,"periodic useful heartbeat preserved");
        s.PistolSwap.Policy.Trace.MainLoaded++;p.RecordPistolSwap(b,s);Check(p.sequence==7,"ammo changes immediate");
        s.WeaponRange.Policy.Trace.Ready=true;p.RecordWeaponRange(b,s);Check(p.sequence==8,"weapon readiness changes immediate");
        s.Transit.Policy.Trace.Requested=true;p.RecordDoorTransit(s);Check(p.sequence==9,"door action changes immediate");
        p.RecordPistolSwap(b,s,true);p.RecordWeaponRange(b,s,true);p.RecordDoorTransit(s,true);
        Check(p.sequence==12,"forced outcomes never throttled");
        b.Data.CurrentSelfDecision=ESelfActionType.Reload;p.RecordPistolSwap(b,s,true);
        using(var doc=JsonDocument.Parse(diagnosticsLine(p)))Check(doc.RootElement.GetProperty("pistolSwap").GetProperty("Action").GetString()=="Reload","emitted action stays current");
        s.Transit.Policy.Trace.State="ended";p.RecordDoorTransit(s);long ended=p.sequence;Time.time+=2;p.RecordDoorTransit(s);
        Check(p.sequence==ended,"ended door remains quiet");p.RecordDoorTransit(s,true);Check(p.sequence==ended+1,"forced door end preserved");
        s.WeaponRange.Policy.Trace.State="a:b";s.WeaponRange.Policy.Trace.Reason="c";p.RecordWeaponRange(b,s);long collision=p.sequence;
        s.WeaponRange.Policy.Trace.State="a";s.WeaponRange.Policy.Trace.Reason="b:c";p.RecordWeaponRange(b,s);
        Check(p.sequence==collision+1,"distinct values cannot collide on string delimiters");
        Check(Api.Decisions.Lines.Count==p.diagnostics.Lines.Count,"RR and file receive the same event count");
        p.RecordSilent(s);var silentCount=p.sequence;
        for(int i=0;i<1000;i++)p.RecordSilent(s);
        long silentBefore=GC.GetAllocatedBytesForCurrentThread();for(int i=0;i<100000;i++)p.RecordSilent(s);
        Check(GC.GetAllocatedBytesForCurrentThread()==silentBefore,"suppressed P08 heartbeat does not format an unused string key");
        Check(p.sequence==silentCount,"P08 heartbeat cadence unchanged");
        p.RecordSilent(s,true);Check(p.sequence==silentCount+1,"P08 forced transitions retained immediately");
        Time.time+=1;p.RecordSilent(s);Check(p.sequence==silentCount+2,"P08 periodic evidence retained");
        var e=new Enemy{Bot=b};b.GoalEnemy=e;p.states[b]=s;s.AbandonProbeAt=Time.time+.25f;var checker=new EnemyKnownChecker{Enemy=e};
        Check(!NeedsAbandonment(checker,false),"dormant search skips expensive duplicate safety reads");
        Check(!NeedsAbandonment(checker,true),"no pending forget callback");
        Time.time+=.25f;Check(NeedsAbandonment(checker,false),"next geometry probe still runs on time");
        s.AbandonProbeAt=Time.time+.25f;
        s.Abandonment.Evaluate(new SearchAbandonmentFrame{Now=120,Stamp=100,Target="target",Distance=1},new SearchAbandonmentConfig());
        Check(NeedsAbandonment(checker,false),"active inspection keeps immediate emergency checks");
        s.Abandonment.Interrupt("combat");Check(!NeedsAbandonment(checker,false),"interrupted dormant state avoids repeat work");
        s.AbandonPending=new SearchAbandonmentFrame{Target="target"};Check(NeedsAbandonment(checker,true),"native forget confirmation immediate");
        Check(NeedsAbandonment(checker,false),"pending confirmation retains safety guards");
        e.EnemyProfileId="other";b.GoalEnemy=null;Check(!NeedsAbandonment(checker,false)&&!NeedsAbandonment(checker,true),"unrelated enemy cannot clear active work");
        Console.WriteLine($"Diagnostic/P09 production gate: {checks} checks passed, {allocated} bytes for 300000 suppressed calls; forced/changed/heartbeat/RR events retained.");
    }
    static string diagnosticsLine(Probe p)=>p.diagnostics.Lines[^1];
}
