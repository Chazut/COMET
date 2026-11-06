using System;
using System.Collections.Generic;
namespace Comet.Client;
public static class Cases
{
 static int checks;
 static void Check(bool ok,string why){checks++;if(!ok)throw new Exception(why);}
 static (Plugin,BotComponent,BotState,CoverPoint) Setup(string feature="A02")
 {
  Time.time=10;ContactSafety.Allowed=true;
  var p=new Plugin();var b=new BotComponent();b.Cover.Bot=b;
  var c=new CoverPoint("owned",8);var s=new BotState{SelectedCover=c,Feature=feature,ActiveLabel=feature=="A02"?"COMET:Cover":"COMET:FallBack",Probe=new(){Cover=c,At=10}};
  b.Cover.CoverInUse=c;b.Cover.CoverPoints.Add(c);p.states.Add(b,s);Plugin.Instance=p;return(p,b,s,c);
 }
 public static void Run()
 {
  {
   var(p,b,s,c)=Setup("A01");
   Check(NativeAdapter.RetreatAvailable(b,s,p.config),"guided retreat requires and retains its actual valid cover");
   c.Valid=false;
   Check(!NativeAdapter.RetreatAvailable(b,s,p.config)&&s.SelectedCover==null,"loose native existence cannot advertise rejected guided cover");
   p.config.Progress.Enabled=false;
   Check(NativeAdapter.RetreatAvailable(b,s,p.config),"explicit progress OFF preserves native cover fallback");
   p.config.Progress.Enabled=true;s.ProgressUnavailable=true;
   Check(NativeAdapter.RetreatAvailable(b,s,p.config),"unavailable progress also preserves native fallback");
   c.PathToPoint.status=NavMeshPathStatus.PathPartial;
   Check(!NativeAdapter.RetreatAvailable(b,s,p.config),"native fallback still requires complete route");
  }
  foreach(string feature in new[]{"A01","A02"})foreach(bool inUse in new[]{true,false})
  {
   var(p,b,s,c)=Setup(feature);var alternative=new CoverPoint("next-cover",12);b.Cover.CoverPoints.Add(alternative);
   b.Cover.CoverInUse=inUse?c:null;b.Cover.CoverPoint_MovingTo=inUse?null:c;
   Check(!ProgressAdapter.RetireFailedCover(b,s.Progress,10),"useful native cover is untouched");
   s.Progress.Fail(c.Key,10,"progress-arrived-idle");
   s.SelectedCover=VisibleAdapter.SelectCover(b,s.Progress);
   s.Probe.Cover=s.SelectedCover;
   Check(ReferenceEquals(s.SelectedCover,alternative),"new feasible destination selected after old cover failed");
   Check(ProgressAdapter.RetireFailedCover(b,s.Progress,10.047),"MD_WAHAHA stale native destination is retired");
   Check(b.Cover.CoverInUse==null&&b.Cover.CoverPoint_MovingTo==null,"native holding branch cannot keep failed cover");
   Check(!ProgressAdapter.RetireFailedCover(b,s.Progress,10.08),"retirement happens once");
   CoverPoint chosen=null;Check(!p.Find(b.Cover,ref chosen)&&ReferenceEquals(chosen,alternative),"native cover chooser receives replacement");
   Check(c.Valid,"failed destination not permanently blacklisted");
  }
  {
   var(p,b,s,c)=Setup();var replacement=new CoverPoint("replacement",4);b.Cover.CoverPoints.Add(replacement);
   Check(ReferenceEquals(VisibleAdapter.SelectCover(b,s.Progress),c),"valid owned cover retains priority");
   c.Valid=false;Check(ReferenceEquals(VisibleAdapter.SelectCover(b,s.Progress),replacement),"invalid owned cover cannot mask valid alternative");
   c.Valid=true;s.Progress.Fail("owned",10,"refused");Check(ReferenceEquals(VisibleAdapter.SelectCover(b,s.Progress),replacement),"temporarily blocked owned cover cannot mask alternative");
   s.Progress.Fail("replacement",10,"refused");Check(VisibleAdapter.SelectCover(b,s.Progress)==null,"all refused candidates unavailable");
  }
  foreach(string feature in new[]{"A01","A02"})foreach(string failure in new[]{"invalid","stale-probe","native-refusal"})
  {
   if(feature=="A01"&&failure=="stale-probe")continue;
   var(p,b,s,c)=Setup(feature);
   if(failure=="invalid")c.Valid=false;
   if(failure=="stale-probe")s.Probe.At=0;
   if(failure=="native-refusal")b.Mover.Accept=false;
   CoverPoint result=null;Check(p.Find(b.Cover,ref result),"failed COMET move yields native chooser");
   Check(s.Progress.Blocked(c.Key,Time.time),"failed cover remembered even before mover was called");
   Check(b.Mover.Calls==(failure=="native-refusal"?1:0),"mover called only for validated cover");
   Check(p.Events.Contains(failure=="native-refusal"?"selected-cover-refused-by-native-mover":"selected-cover-invalidated-before-move"),"precise failure evidence");
   Check(p.Yields==1&&s.VisibleEngine.Refusals==1,"one failed plan release");
   var other=new CoverPoint("other",12);b.Cover.CoverPoints.Add(other);
   Check(ReferenceEquals(VisibleAdapter.SelectCover(b,s.Progress),other),"next choice can use other cover");
  }
  {
   var(p,b,s,c)=Setup();CoverPoint result=null;
   Check(!p.Find(b.Cover,ref result)&&ReferenceEquals(result,c)&&b.Mover.Calls==1,"native successful or already-moving return accepted");
   Check(s.Progress.Failures.Count==0&&p.Yields==0,"no rejection for successful continuation");
  }
  {
   var(p,b,s,c)=Setup();ContactSafety.Allowed=false;CoverPoint result=null;
   Check(p.Find(b.Cover,ref result)&&b.Mover.Calls==0&&s.Progress.Failures.Count==0,"own squad yields without COMET movement or poisoning cover");
  }
  {
   var(p,b,s,c)=Setup();c.Valid=false;b.Cover.CoverPoints.Clear();
   for(int i=0;i<24;i++)b.Cover.CoverPoints.Add(new("bad"+i,1){Valid=false});
   b.Cover.CoverPoints.Add(new("late",1));Check(VisibleAdapter.SelectCover(b)==null,"candidate scan remains bounded");
  }
  Console.WriteLine($"Cover selection: {checks} production adapter checks passed; native movement doubled, no raid.");
 }
}
static class Time{public static float time;}
enum ESelfActionType{None,Reload}enum ESquadDecision{None,Help}enum ECombatDecision{SeekCover,Retreat}enum ESprintUrgency{High}
class BotComponent{public string ProfileId="pmc";public bool BotActive=true,Pmc=true;public Decision Decision=new();public SAINCoverClass Cover=new();public Mover Mover=new();}
class Decision{public ESelfActionType CurrentSelfDecision;public ESquadDecision CurrentSquadDecision;public ECombatDecision CurrentCombatDecision;}
class SAINCoverClass{public BotComponent Bot;public CoverPoint CoverInUse,CoverPoint_MovingTo;public List<CoverPoint> CoverPoints=new();public void StopSeekingCover(){CoverInUse=CoverPoint_MovingTo=null;}}
class Mover{public bool Accept=true;public int Calls;public bool GoToCoverPoint(CoverPoint p,bool slow,ESprintUrgency urgency){Calls++;return Accept;}}
enum NavMeshPathStatus{PathComplete,PathPartial}
class MeshPath{public NavMeshPathStatus status;}
class CoverData{public bool IsBad;}
class CoverPoint{public string Key;public bool Valid=true;public PathData PathData=new();public MeshPath PathToPoint=new();public CoverData CoverData=new();public CoverPoint(string key,float length){Key=key;PathData.PathLength=length;}}
class PathData{public float PathLength,TimeSinceLastUpdated;}
enum Tactic{FallBack}
class Snapshot{public double Now=10;public Dictionary<Tactic,string> Unavailable=new();}
class PlanProgress{public List<string> Failures=new();public object Current=new();public bool Blocked(string key,double now)=>Failures.Contains(key);public void Fail(string key,float now,string reason)=>Failures.Add(key);}
class Settings{public bool Enabled=true;}class Config{public Settings Progress=new();}class Probe{public CoverPoint Cover;public float At;}
class Engine{public int Refusals;public void NotifyNativeRefusal()=>Refusals++;}
class BotState{public string Feature,ActiveLabel;public bool ProgressUnavailable;public CoverPoint SelectedCover;public PlanProgress Progress=new();public object ProgressAtSelection;public Probe Probe;public Engine VisibleEngine=new();}
static class ContactSafety{public static bool Allowed=true;public static bool AllowsGoal(BotComponent b)=>Allowed;}
static partial class NativeAdapter{public static bool IsPmcAi(BotComponent b)=>b.Pmc;}
static partial class ProgressAdapter{public static string CoverKey(CoverPoint p)=>p?.Key??"";}
static partial class VisibleAdapter{public static bool ValidCover(BotComponent b,CoverPoint p)=>p?.Valid==true;}
class Log{public void LogWarning(string text)=>throw new Exception(text);}
partial class Plugin
{
 public static Plugin Instance;public Config config=new();public Dictionary<BotComponent,BotState> states=new();public bool Authority=true;public int Yields;public List<string> Events=new();Log Logger=new();
 bool Ghost(string id)=>false;bool FindRelocationCover(SAINCoverClass c,BotState s,ref CoverPoint point)=>false;
 internal static bool RetainedCover(BotComponent b,CoverPoint c)=>ReferenceEquals(b.Cover.CoverInUse,c);
 void Emit(BotState s,string stage,string reason)=>Events.Add(reason);
 void YieldToSain(BotState s,string reason,string stage="ended"){Yields++;s.ActiveLabel="";}
 public bool Find(SAINCoverClass c,ref CoverPoint point)=>FindCover(c,ref point);
}
