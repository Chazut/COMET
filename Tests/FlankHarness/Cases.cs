using System;
using System.Collections.Generic;
namespace Comet.Client;
public static class Cases
{
 static int checks;static void Check(bool b,string why){checks++;if(!b)throw new Exception(why);}
 static (Plugin p,BotComponent b,BotState s,CoverPoint point) Setup()
 {
  Time.time=10;var p=Plugin.Instance=new Plugin();var b=new BotComponent();var s=new BotState();p.states[b]=s;
  b.Cover.CoverInUse=new CoverPoint{CoverHeight=1};var point=new CoverPoint{Position=new Vector3(8,0,0),CoverHeight=2};b.Cover.CoverPoints.Add(point);
  Check(FlankAdapter.Capability(b,s)==""&&s.Flank.Point==point,"exact feasible destination selected");
  s.Engine.Current=Tactic.Flank;return(p,b,s,point);
 }
 public static void Run()
 {
  {
   var(p,b,s,point)=Setup();b.Decision.EnemyDecisions.ShiftCoverComplete=true;
   Check(!FlankAdapter.Arrived(b,s),"native complete flag is not arrival before execution");
   CoverPoint native=null;bool result=false;Check(!p.Select(b,ref native,ref result)&&result&&native==point,"real prefix supplies selected point");
   Check(b.Mover.Destination==point&&s.Flank.MoveAccepted,"native mover acceptance observed");
   Check(!FlankAdapter.Arrived(b,s),"native complete flag is not arrival far from destination");
   Check(FlankAdapter.Capability(b,s)=="flank-native-ended-before-arrival","native termination without movement reported as failure");
   s.Engine.Current=Tactic.Hold;Check(FlankAdapter.Capability(b,s)=="no-native-shift-cover-target","failed destination is not immediately rerolled");
   Time.time=16;Check(FlankAdapter.Capability(b,s)=="","retry possible after bounded backoff");
  }
  {
   var(p,b,s,point)=Setup();b.Mover.Accept=false;CoverPoint native=null;bool result=true;
   Check(!p.Select(b,ref native,ref result)&&!result&&!s.Flank.MoveAccepted&&s.Engine.Failed,"native refusal cannot become selected/arrived success");
   Check(p.Events.Contains("flank-route-refused"),"route failure observable");
  }
  {
   var(p,b,s,point)=Setup();CoverPoint native=null;bool result=false;p.Select(b,ref native,ref result);
   b.Position=point.Position;Check(!FlankAdapter.Arrived(b,s),"proximity alone does not claim completion");
   b.Mover.ActivePath=new Path{Status=EBotMoveStatus.Complete,Destination=point.Position};
   Check(FlankAdapter.Arrived(b,s)&&FlankAdapter.Capability(b,s)=="flank-arrived","accepted completed path at selected destination succeeds");
   b.Mover.ActivePath.Destination=new Vector3(99,0,0);Check(!FlankAdapter.Arrived(b,s),"different native route is not our arrival");
   point.BotInThisCover=true;Check(FlankAdapter.Arrived(b,s),"actual selected cover occupancy confirms arrival");
  }
  foreach(string guard in new[]{"disabled","scope","priority","squad","self","other-action","other-plan","other-feature"})
  {
   var(p,b,s,point)=Setup();switch(guard){case "disabled":p.config.Enabled=false;break;case "scope":p.Scope=false;break;
    case "priority":p.Priority=true;break;case "squad":b.Decision.CurrentSquadDecision=ESquadDecision.Retreat;break;case "self":b.Decision.CurrentSelfDecision=ESelfActionType.Heal;break;
    case "other-action":b.Decision.CurrentCombatDecision=ECombatDecision.None;break;case "other-plan":s.ActiveLabel="";break;case "other-feature":s.Feature="A02";break;}
   CoverPoint native=null;bool result=false;Check(p.Select(b,ref native,ref result)&&b.Mover.Calls==0,"does not replace native/remote/emergency work: "+guard);
  }
  {
   var(p,b,s,point)=Setup();b.GoalEnemy.EnemyProfileId="new";CoverPoint native=null;bool result=true;
   Check(!p.Select(b,ref native,ref result)&&!result&&b.Mover.Calls==0,"old contact's destination never submitted for new contact");
  }
  foreach(bool unavailable in new[]{false,true})
  {
   var(p,b,s,point)=Setup();s.Progress.IsBlocked=true;s.ProgressUnavailable=unavailable;
   s.Engine.Current=Tactic.Hold;
   Check((FlankAdapter.Capability(b,s)=="")==unavailable,"quarantined progress observer cannot veto selection");
   s.Flank.Point=point;s.Flank.Target=b.GoalEnemy.EnemyProfileId;
   CoverPoint native=null;bool result=false;p.Select(b,ref native,ref result);
   Check(result==unavailable&&b.Mover.Calls==(unavailable?1:0),"execution respects live progress rejection only");
  }
  var a=new KnownThreat{Id="primary",Visible=true};var b1=new KnownThreat{Id="b"};var c=new KnownThreat{Id="c"};
  Check(VisibleAdapter.Same(new(){a,b1,c},new(){a,c,b1}),"secondary distance sorting alone is not changed threat evidence");
  Check(!VisibleAdapter.Same(new(){a,b1},new(){b1,a}),"primary target change still invalidates");
  Check(!VisibleAdapter.Same(new(){a},new(){new(){Id="primary",Visible=false}}),"visibility change invalidates");
  Check(VisibleAdapter.Same(new(){a},new(){new(){Id="primary",Visible=true,Position=new Vector3(1.5f,0,0)}}),"existing two-metre threat cache tolerance retained");
  Check(!VisibleAdapter.Same(new(){a},new(){new(){Id="primary",Visible=true,Position=new Vector3(3,0,0)}}),"large known movement invalidates");
  Console.WriteLine($"Flank/known-threat production adapter: {checks} checks passed.");
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
static partial class VisibleAdapter{public static bool ValidCover(BotComponent b,CoverPoint p)=>p.Valid;public static bool Same(List<KnownThreat> a,List<KnownThreat>b)=>SameThreats(a,b,4);}
class Config{public bool Enabled=true;}
partial class Plugin{public static Plugin Instance;public Config config=new();public bool Scope=true,Priority;public Dictionary<BotComponent,BotState> states=new();public List<string> Events=new();
 static BotComponent FaultOwner(object b)=>(BotComponent)b;bool TacticalScope(BotComponent b,Enemy e)=>Scope;static bool NativeChoiceHasPriority(BotComponent b,BotState s)=>Instance.Priority;
 void Emit(BotState s,string stage,string reason)=>Events.Add(reason);public bool Select(BotComponent b,ref CoverPoint p,ref bool result)=>SelectFlankPoint(b,ref p,ref result);}
