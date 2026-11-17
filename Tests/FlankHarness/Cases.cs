using System;
using System.Collections.Generic;
namespace Comet.Client;
public static class Cases
{
 static int checks;static void Check(bool b,string why){checks++;if(!b)throw new Exception(why);}
 static (Plugin p,BotComponent b,BotState s,CoverPoint point) Setup()
 {
  Time.time=10;var p=Plugin.Instance=new Plugin();var b=new BotComponent();var s=new BotState();p.states[b]=s;
  var point=new CoverPoint{Position=new Vector3(8,0,0),CoverHeight=2};return(p,b,s,point);
 }
 public static void Run()
 {
  {
   var(p,b,s,point)=Setup();b.Decision.EnemyDecisions.ShiftCoverComplete=true;
   p.Observe(b,point,true);
   Check(s.Flank.Point==point&&s.Flank.MoveAccepted&&s.Flank.Target==b.GoalEnemy.EnemyProfileId,"native result recorded");
   Check(b.Mover.Calls==0,"observer never issues or replaces a route");
   Check(!FlankAdapter.Arrived(b,s),"native completion flag alone is not arrival evidence");
   b.Position=point.Position;Check(!FlankAdapter.Arrived(b,s),"proximity alone is not arrival evidence");
   b.Mover.ActivePath=new Path{Status=EBotMoveStatus.Complete,Destination=point.Position};
   Check(FlankAdapter.Arrived(b,s),"completed native path to selected point recorded as arrival");
   b.Mover.ActivePath.Destination=new Vector3(99,0,0);Check(!FlankAdapter.Arrived(b,s),"another path is not proof of arrival");
   point.BotInThisCover=true;Check(FlankAdapter.Arrived(b,s),"native cover occupancy recorded");
   p.Observe(b,null,false);Check(s.Flank.Point==null&&!s.Flank.MoveAccepted&&b.Mover.Calls==0,"native no-point result retained without retry blacklist");
   Check(p.Events.Contains("flank-native-no-point"),"native failure observable");
  }
  foreach(string guard in new[]{"disabled","scope","other-plan","other-feature"})
  {
   var(p,b,s,point)=Setup();switch(guard){case "disabled":p.config.Enabled=false;break;case "scope":p.Scope=false;break;
    case "other-plan":s.ActiveLabel="";break;case "other-feature":s.Feature="A02";break;}
   p.Observe(b,point,true);Check(s.Flank.Point==null&&p.Events.Count==0&&b.Mover.Calls==0,"irrelevant native work untouched: "+guard);
  }
  {
   var(p,b,s,point)=Setup();s.Progress.IsBlocked=true;
   p.Observe(b,point,true);Check(s.Flank.MoveAccepted&&b.Mover.Calls==0,"progress diagnostics cannot veto or resubmit native work");
  }
  Console.WriteLine($"Native flank observer: {checks} checks passed.");
 }
}
struct Vector3{public float x,y,z;public Vector3(float x,float y,float z){this.x=x;this.y=y;this.z=z;}public float sqrMagnitude=>x*x+y*y+z*z;public static Vector3 operator-(Vector3 a,Vector3 b)=>new(a.x-b.x,a.y-b.y,a.z-b.z);}
static class Time{public static float time;}
enum Tactic{Hold,Flank}enum ECombatDecision{None,ShiftCover}enum ESelfActionType{None,Heal}enum ESquadDecision{None,Retreat}enum EBotMoveStatus{Moving,Complete}
class CoverPoint{public Vector3 Position;public float CoverHeight;public bool BotInThisCover,Valid=true;public PathData PathData=new();}
class PathData{public float PathLength=10;}class Path{public EBotMoveStatus Status;public Vector3 Destination;}
class Cover{public CoverPoint CoverInUse;public List<CoverPoint> CoverPoints=new();}
class Enemy{public string EnemyProfileId="target";}class EnemyDecision{public bool ShiftCoverComplete;}
class Decision{public ECombatDecision CurrentCombatDecision=ECombatDecision.ShiftCover;public ESelfActionType CurrentSelfDecision;public ESquadDecision CurrentSquadDecision;public EnemyDecision EnemyDecisions=new();}
class Mover{public bool Accept=true;public int Calls;public CoverPoint Destination;public Path ActivePath;public bool GoToCoverPoint(CoverPoint p,bool run){Calls++;Destination=p;return Accept;}}
class BotComponent{public Vector3 Position;public Enemy GoalEnemy=new();public Cover Cover=new();public Decision Decision=new();public Mover Mover=new();}
class BotState{public bool ProgressUnavailable;public string Feature="A01",ActiveLabel="COMET:Flank";public FlankRuntime Flank=new();public Engine Engine=new();public Progress Progress=new();}
class Progress{public bool IsBlocked;public bool Blocked(string key,float now)=>IsBlocked;}
class Engine{public Tactic Current;public bool Failed;public void Fail(Tactic t,float now,string why)=>Failed=true;}
static class ProgressAdapter{public static string CoverKey(CoverPoint p)=>p.Position.x.ToString();}
static partial class VisibleAdapter{public static bool ValidCover(BotComponent b,CoverPoint p)=>p.Valid;}
class Config{public bool Enabled=true;}
partial class Plugin{public static Plugin Instance;public Config config=new();public bool Scope=true,Priority;public Dictionary<BotComponent,BotState> states=new();public List<string> Events=new();
 static BotComponent FaultOwner(object b)=>(BotComponent)b;bool TacticalScope(BotComponent b,Enemy e)=>Scope;static bool NativeChoiceHasPriority(BotComponent b,BotState s)=>Instance.Priority;
 void RecordHiddenExecution(BotComponent b,BotState s,string reason)=>Events.Add(reason);public void Observe(BotComponent b,CoverPoint p,bool result)=>ObserveFlankPoint(b,p,result);}
