using System;using System.Collections.Generic;
namespace SAIN.Layers { public class BotAction {public Comet.Client.BotComponent Bot=new();} }
namespace Comet.Client {
public static class Cases {
 static int n;static void C(bool v,string why){n++;if(!v)throw new Exception(why);}
 public static void Run(){
  var p=new Plugin();var a=new SAIN.Layers.BotAction();var b=a.Bot;var s=new BotState();p.states[b]=s;
  C(!p.Ambush(a)&&p.Steps==1&&!s.Ambush.Fault,"FreezeAction uses its real Bot property without uninitialized reflection");
  s.Interruption.Policy.Pending=true;C(!p.Ambush(a)&&p.Resumes==1,"resumed door plan reaches its actuator");s.Interruption.Policy.Pending=false;
  p.states.Clear();C(p.Ambush(a),"unowned bot keeps native action");p.states[b]=s;
  C(p.Weave(b,s)&&s.RetreatWeave.SeenAt==8,"current enemy data matches source");
  b.GoalEnemy=null;C(!p.Weave(b,s)&&p.EndReason=="weave-no-goal-enemy","grenade retreat with no enemy releases weave without fault");
  b.GoalEnemy=new();C(p.Weave(b,s),"same bot can weave after another enemy is selected");
  foreach(var feature in new[]{"A01","A02"}){
   s.Feature=feature;s.ActiveLabel=feature=="A01"?"COMET:FallBack":"COMET:Cover";
   foreach(var point in new CoverPoint[]{null,new(),new(){Bad=true}}){
    s.SelectedCover=point;CoverPoint result=null;
    C(p.Find(b.Cover,ref result),"empty, stale and valid COMET selection all allow native cover chooser");
    var native=new CoverPoint();p.Observe(b.Cover,native);C(ReferenceEquals(s.SelectedCover,native),"record the actual native result");
    int events=p.Events.Count;p.Observe(b.Cover,native);C(p.Events.Count==events,"same native cover does not spam events");
    p.Observe(b.Cover,null);C(s.SelectedCover==null&&s.ActiveLabel.Length>0,"native pending search does not cancel tactical intent");
   }
  }
  Console.WriteLine($"Woods execution: {n} production method checks passed (native actuator doubled, no raid).");
 }
}
public class Enemy {public object EnemyPosition=new();public bool Seen=true;public float TimeSinceSeen=2;public Status Status=new();}
public class Status{public float TimeLastShotAtMe=9;}
public class BotComponent{public Enemy GoalEnemy=new();public SAINCoverClass Cover;public BotComponent(){Cover=new(){Bot=this};}}
public class CoverPoint{public bool Bad;}
public class SAINCoverClass{public BotComponent Bot;}
public class StatePart{public Policy Policy=new();public bool Fault;}
public class Policy{public bool Active,Pending,Resumed;}
public class Weave{public object Known;public string Source;public float SeenAt,FireAt;}
public class BotState{public StatePart Emergency=new(),Interruption=new(),Ambush=new();public object Support;public Weave RetreatWeave=new();public CoverPoint SelectedCover;public string Feature="A01",ActiveLabel="COMET:Hold";}
static class Time{public static float time=10;}
static class ContactSafety{public static bool AllowsGoal(BotComponent b)=>true;}
class Log{public void LogWarning(string msg)=>throw new Exception(msg);}
partial class Plugin{
 public static Plugin Instance;public object config=new();public Dictionary<BotComponent,BotState> states=new();public int Steps,Resumes;public string EndReason;public List<string> Events=new();Log Logger=new();public Plugin(){Instance=this;}
 bool GuardDoorReservation(BotComponent b,BotState s)=>true;void RefreshDoorInterruption(BotComponent b,BotState s){}
 bool StepDoorEmergency(BotComponent b,BotState s,string action)=>false;bool StepDoorResume(BotComponent b,BotState s){Resumes++;return false;}
 bool StepDoorSupport(BotComponent b,BotState s)=>false;bool StepDoorAmbush(BotComponent b,BotState s){Steps++;return false;}
 bool CompleteDoorReservationStep(BotComponent b,BotState s,bool fallback)=>fallback;
 void AbandonDoorResume(BotState s,string r){}void EndDoorSupport(BotState s,string r){}void EndDoorAmbush(BotState s,string r){}void ReleaseDoorReservation(BotState s,string r){}
 void EndWeave(BotState s,string reason){EndReason=reason;}
 bool FindRelocationCover(SAINCoverClass c,BotState s,ref CoverPoint result)=>false;
 void Emit(BotState s,string stage,string reason){Events.Add(reason);}
 public bool Ambush(SAIN.Layers.BotAction a)=>AmbushHoldStep(a);
 public bool Weave(BotComponent b,BotState s)=>UpdateWeaveContact(b,s);
 public bool Find(SAINCoverClass c,ref CoverPoint result)=>FindCover(c,ref result);
 public void Observe(SAINCoverClass c,CoverPoint result)=>ObserveNativeCover(c,result);
}
}
