using System;
using System.Collections.Generic;
using System.Linq;
using Comet.Core;
namespace Comet.Client;

public static class Cases
{
 static int checks;
 static void Check(bool value,string name){checks++;if(!value)throw new Exception(name);}
 static (Plugin p,BotComponent b,BotState s) Setup(string feature="A01",string action="FallBack")
 {
  ContactSafety.Allowed=true;VisibleAdapter.Pending=false;Time.time=10;Time.frameCount=100;var b=new BotComponent();b.Decision.Bot=b;
  var p=new Plugin();var s=new BotState(b,p.config,"raid"){Feature=feature,Decision=action,ActiveLabel="COMET:"+action,
   LastSelection=10,Expected=action=="FallBack"?ECombatDecision.Retreat:ECombatDecision.SeekCover};
  s.SelectedCover=b.Cover.CoverPoint_MovingTo=new CoverPoint();s.Probe.Cover=s.SelectedCover;
  b.Decision.CurrentCombatDecision=s.Expected;b.GoalEnemy.IsVisible=feature=="A02";b.CurrentAction=new SeekCoverAction();
  p.states.Add(b,s);Plugin.Instance=p;return(p,b,s);
 }
 public static void Run()
 {
  foreach(string action in new[]{"FirstAid","Surgery","Stims","Reload"})
  {
   var(p,b,s)=Setup("A02","Shoot");var policy=action=="Reload"?s.ReloadTiming.Policy:s.Medical.Policy;
   policy.Active=true;policy.Trace=new ServiceTrace{Choice=action,Accepted=true,Started=true,Weapon="weapon"};
   b.Decision.CurrentSelfDecision=Enum.Parse<ESelfActionType>(action);
   if(action=="Reload")b.BotOwner.WeaponManager.Reload.Reloading=true;else b.BotOwner.Medecine.Using=true;
   Check(p.Service(b,s,true)&&s.PlanService.Plan=="Shoot"&&s.PlanService.Action==action,"owned service suspends plan "+action);
   Check(s.ActiveLabel==""&&s.Decision==""&&p.Suspends==1&&Api.Decisions.Label=="COMET:"+action,"no old tactical execution label during service");
   for(int i=0;i<50;i++){Time.time+=.1f;Check(p.Service(b,s,true),"native service continuation");}
   Check(p.Suspends==1,"service does not repeatedly release or erase saved plan");
   Check(p.ServiceOwned(b,s,false)==action,"journal requires observed hands");
   b.BotOwner.Medecine.Using=false;b.BotOwner.WeaponManager.Reload.Reloading=false;
   Check(p.ServiceOwned(b,s,false)=="","stale self label is not evidence of execution");
   policy.Active=false;
   Check(!p.Service(b,s,true)&&!s.PlanService.Active&&Api.Decisions.Label=="","completed service clears ownership for fresh reevaluation");
  }
  foreach(string invalid in new[]{"disabled","feature-off","world","retired","authority","scav","inactive","dead","downed","ending","ghost","layer","grenade","avoid","dogfight","melee","fault","inactive-policy","wrong-self","no-commit","native-reload","wrong-weapon"})
  {
   var(p,b,s)=Setup();s.Medical.Policy.Active=true;s.Medical.Policy.Trace=new ServiceTrace{Choice="Surgery"};b.Decision.CurrentSelfDecision=ESelfActionType.Surgery;
   bool committed=true;
   switch(invalid){case "disabled":p.config.Enabled=false;break;case "feature-off":p.config.MedicalChoice.Enabled=false;break;
    case "world":p.world=null;break;case "retired":s.Retired=true;break;case "authority":p.Authority=false;break;case "scav":b.Pmc=false;break;
    case "inactive":b.BotActive=false;break;case "dead":b.IsDead=true;break;case "downed":b.Player.Downed=true;break;case "ending":b.GameEnding=true;break;case "ghost":p.IsGhost=true;break;
    case "layer":b.SAINLayersActive=false;break;case "grenade":b.Grenade.GrenadeDangerPoint=new();break;case "avoid":b.Decision.CurrentCombatDecision=ECombatDecision.AvoidGrenade;break;
    case "dogfight":b.Decision.DogFightDecision.DogFightActive=true;break;case "melee":b.BotOwner.WeaponManager.IsMelee=true;break;
    case "fault":s.Medical.Fault=true;break;case "inactive-policy":s.Medical.Policy.Active=false;break;case "wrong-self":b.Decision.CurrentSelfDecision=ESelfActionType.Reload;break;
    case "no-commit":committed=false;break;case "native-reload":b.Decision.CurrentSelfDecision=ESelfActionType.Reload;b.BotOwner.WeaponManager.Reload.Reloading=true;break;
    case "wrong-weapon":b.Decision.CurrentSelfDecision=ESelfActionType.Reload;s.ReloadTiming.Policy.Active=true;s.ReloadTiming.Policy.Trace=new ServiceTrace{Choice="Reload",Accepted=true,Weapon="other"};break;}
   Check(!p.Service(b,s,committed)&&p.Suspends==0,"no false ownership or suspension: "+invalid);
  }
  foreach(string feature in new[]{"A01","A02"})
  {
   var(p,b,s)=Setup(feature,feature=="A01"?"FallBack":"Cover");
   s.Execution.Accept(Time.time,feature,s.Decision,"target");
   for(int i=1;i<=15;i++)
   {
    Time.time=10+i*.1f;Time.frameCount++;bool continued=true;p.Continue(b.Decision,ref continued);
    Check(continued&&s.RequestedFrame==Time.frameCount&&s.Expected==ECombatDecision.SeekCover,"renew exact owned cover "+feature);
    // Native ContinueMoveToCover commits SeekCover, not Retreat. The real acceptance contract must accept it.
    b.Decision.CurrentCombatDecision=ECombatDecision.SeekCover;
    Check(IntegrationContract.Accept(true,true,s.RequestedFrame,Time.frameCount,(int)s.Expected,(int)b.Decision.CurrentCombatDecision,false,false),"native continuation acceptance");
    s.RequestedFrame=-1;p.Sample(b,s);
    Check(s.Execution.Phase=="executing"&&s.ActiveLabel.Length>0,"cover remains executing without release/reselection");
   }
   Check(p.Events.Count(x=>x=="fallback-cover-continuation")== (feature=="A01"?1:0),"single alias transition log");
   Time.time=18.01f;bool result=true;p.Continue(b.Decision,ref result);Check(result,"no extra periodic cancellation of native cover action");
  }
  {
   var(p,b,s)=Setup();s.SelectedCover=null;bool continued=true;p.Continue(b.Decision,ref continued);
   Check(continued&&ReferenceEquals(s.SelectedCover,b.Cover.CoverPoint_MovingTo),"native-chosen cover works with progress monitoring disabled");
   Time.frameCount++;b.Cover.CoverPoint_MovingTo=new();continued=true;p.Continue(b.Decision,ref continued);
   Check(continued,"native owns its selected cover destination");
  }
  foreach(string change in new[]{"visible","hurt","ammo","suppressed","medical","reload","blocked","timeout","pending"}) {
   var(p,b,s)=Setup();
   if(change=="visible")b.GoalEnemy!.IsVisible=true;
   if(change=="blocked")s.Progress.IsBlocked=true;
   if(change=="timeout")Time.time+=50;
   if(change=="medical")b.BotOwner.Medecine.Using=true;
   if(change=="reload")b.BotOwner.WeaponManager.Reload.Reloading=true;
   if(change=="pending")VisibleAdapter.Pending=true;
   bool continued=true;p.Continue(b.Decision,ref continued);
   Check(continued&&s.RequestedFrame==Time.frameCount,"no second continuation veto: "+change);
   continued=false;s.RequestedFrame=-1;p.Continue(b.Decision,ref continued);
   Check(!continued&&s.RequestedFrame==-1,"native refusal is not overridden: "+change);
  }
  // Direct zzap continuation cases: three-second stalled route and break-off
  // only for a visible close shooter while SeekCover, not Retreat/self-action.
  {
   var(p,b,s)=Setup();var point=b.Cover.CoverPoint_MovingTo;point.Spotted=true;point.Distance=2;
   b.Decision.TimeSinceChangeDecision=1;s.CoverRunAnchorAt=1;
   bool continued=true;p.Continue(b.Decision,ref continued);
   Check(!continued&&b.Cover.CoverInUse==null&&b.Cover.CoverPoint_MovingTo==null,"exposed nearby point cannot be accepted by stalled-cover fallback");
   Check(!point.CoverData.IsBad&&s.CoverRunAnchorAt==-1,"exposed fallback does not permanently blacklist cover");
  }
  foreach(bool close in new[]{false,true}) {
   var(p,b,s)=Setup();b.Decision.TimeSinceChangeDecision=1;
   var point=b.Cover.CoverPoint_MovingTo;point.Distance=close?2:5;
   bool continued=true;p.Continue(b.Decision,ref continued);Check(continued,"source anchor initialized");
   Time.time+=3;continued=true;p.Continue(b.Decision,ref continued);Check(continued,"source strict three-second threshold");
   Time.time+=.01f;continued=true;p.Continue(b.Decision,ref continued);Check(!continued,"source stalled run yields");
   Check(close?ReferenceEquals(b.Cover.CoverInUse,point)&&b.Cover.CoverPoint_MovingTo==null&&!b.Cover.Sprinting:point.CoverData.IsBad,"source accepts near cover or marks distant point bad");
  }
  foreach(string change in new[]{"break","retreat","self","far","empty","reload","near-cover","hidden","no-shot"}) {
   var(p,b,s)=Setup("A02","Cover");b.GoalEnemy.CanShoot=true;b.GoalEnemy.IsVisible=true;b.Decision.TimeSinceChangeDecision=1;
   switch(change){case "retreat":b.Decision.CurrentCombatDecision=ECombatDecision.Retreat;break;case "self":b.Decision.CurrentSelfDecision=ESelfActionType.FirstAid;break;case "far":b.GoalEnemy.RealDistance=31;break;case "empty":b.BotOwner.WeaponManager.HaveBullets=false;break;case "reload":b.BotOwner.WeaponManager.Reload.Reloading=true;break;case "near-cover":b.Cover.CoverPoint_MovingTo.PathDistanceStatus=CoverStatus.CloseToCover;break;case "hidden":b.GoalEnemy.IsVisible=false;break;case "no-shot":b.GoalEnemy.CanShoot=false;break;}
   bool continued=true;p.Continue(b.Decision,ref continued);Check(continued==(change!="break"),"zzap break-off boundary "+change);
  }
  {
   var(p,b,s)=Setup();b.Decision.TimeSinceChangeDecision=1;bool continued=true;p.Continue(b.Decision,ref continued);
   Time.time+=2.9f;b.Position=new Vector3(.6f,0,0);continued=true;p.Continue(b.Decision,ref continued);
   Time.time+=2.9f;continued=true;p.Continue(b.Decision,ref continued);Check(continued,"source anchor resets after half metre of actual movement");
  }
  // All current squad values plus an unknown future value. Safety retreats remain SAIN-owned.
  foreach(bool customLayer in new[]{false,true})
  foreach(ESquadDecision squad in Enum.GetValues(typeof(ESquadDecision)))
  {
   var(p,b,s)=Setup();s.ActiveLabel="";b.Decision.CurrentSquadDecision=squad;
   p.CustomLayerChoice=customLayer;s.Crossfire.Policy.Active=true;s.Crossfire.Policy.Trace=new(){State="ready"};s.RequestedFrame=-1;
   var solo=ECombatDecision.None;var committed=squad;p.Arbitrate(b.Decision,ref solo,ref committed,ESelfActionType.None,b.GoalEnemy);
   bool ordinary=TacticalPriority.OrdinarySquad(squad.ToString());
   Check((p.ChooseCalls==1)==ordinary,"squad whitelist "+squad);
   Check(ordinary?solo==ECombatDecision.Freeze&&committed==ESquadDecision.None:committed==squad&&solo==ECombatDecision.None,"native vs COMET commit "+squad);
   Check(!s.ArbitratingSquad,"no leaked arbitration flag");
   if(customLayer)Check(s.RequestedFrame==-1,"custom layer does not impersonate a generic tactical request");
  }
  foreach(bool customLayer in new[]{false,true})
  foreach(string phase in customLayer?new[]{"ready","planning"}:new[]{"ready"})
  foreach(string guard in new[]{"own-squad","self","grenade-decision","dogfight-decision","melee-decision","zombies","enemy-null","enemy-zombie","no-contact","stale","negative-age","disabled","scav","ghost","replica","inactive","ending","no-world","medicine","meds-controller","reload","switch","throwing","hands","door","danger","dogfight","melee","reentrant","no-choice","throws"})
  {
   var(p,b,s)=Setup();var solo=ECombatDecision.None;var squad=ESquadDecision.GroupSearch;var self=ESelfActionType.None;Enemy e=b.GoalEnemy;
   p.CustomLayerChoice=customLayer;s.Crossfire.Policy.Active=true;s.Crossfire.Policy.Trace=new(){State=phase};
   switch(guard){case "own-squad":ContactSafety.Allowed=false;break;case "self":self=ESelfActionType.Reload;break;case "grenade-decision":solo=ECombatDecision.AvoidGrenade;break;
    case "dogfight-decision":solo=ECombatDecision.DogFight;break;case "melee-decision":solo=ECombatDecision.MeleeAttack;break;case "zombies":solo=ECombatDecision.FightZombies;break;
    case "enemy-null":e=null;break;case "enemy-zombie":e.IsZombie=true;break;case "no-contact":e.KnownPlaces.LastKnownPosition=null;break;
    case "stale":e.TimeSinceLastKnownUpdated=100;break;case "negative-age":e.TimeSinceLastKnownUpdated=-1;break;
    case "disabled":p.config.Enabled=false;break;case "scav":b.Pmc=false;break;case "ghost":p.IsGhost=true;break;case "replica":p.Authority=false;break;
    case "inactive":b.BotActive=false;break;case "ending":b.GameEnding=true;break;case "no-world":p.world=null;break;
    case "medicine":b.BotOwner.Medecine.Using=true;break;case "meds-controller":b.Player.HandsController=new Meds();break;
    case "reload":b.BotOwner.WeaponManager.Reload.Reloading=true;break;case "switch":b.BotOwner.WeaponManager.Selector.IsChanging=true;break;
    case "throwing":b.BotOwner.WeaponManager.Grenades.ThrowindNow=true;break;case "hands":b.Player.HandsController.Busy=true;break;
    case "door":b.BotOwner.DoorOpener.Interacting=true;break;case "danger":b.Grenade.GrenadeDangerPoint=new();break;
    case "dogfight":b.Decision.DogFightDecision.DogFightActive=true;break;case "melee":b.BotOwner.WeaponManager.IsMelee=true;break;
    case "cooldown":s.Execution.Accept(8,"A01","Hold","target");for(int i=1;i<=4;i++)s.Execution.Sample(8+i*.5,false,false);break;
    case "reentrant":s.ArbitratingSquad=true;break;case "no-choice":p.AllowChoose=false;break;case "throws":p.ThrowChoose=true;break;}
   var original=solo;try{p.Arbitrate(b.Decision,ref solo,ref squad,self,e);}catch(Exception){Check(guard=="throws","unexpected arbitration exception");}
   Check(solo==original&&squad==ESquadDecision.GroupSearch,"preserve native emergency/fallback "+guard);
   Check(guard=="reentrant"||!s.ArbitratingSquad,"flag released even on errors");
  }
  {
   var(p,b,s)=Setup();p.CustomLayerChoice=true;s.Crossfire.Policy.Active=true;s.Crossfire.Policy.Trace=new(){State="planning"};s.RequestedFrame=Time.frameCount;
   var solo=ECombatDecision.None;var squad=ESquadDecision.GroupSearch;
   p.Arbitrate(b.Decision,ref solo,ref squad,ESelfActionType.None,b.GoalEnemy);
   Check(solo==ECombatDecision.Freeze&&squad==ESquadDecision.None,"selected preparation replaces ordinary squad movement to keep the search origin stable");
  }
  {
   var(p,b,s)=Setup();b.Decision.CurrentSelfDecision=ESelfActionType.Reload;Check(!p.Priority(b,s),"previous self decision cannot veto a new enemy decision after native self selection declined");
   b.BotOwner.WeaponManager.Reload.Reloading=true;Check(p.Priority(b,s),"actual reload still takes priority");b.BotOwner.WeaponManager.Reload.Reloading=false;
   // SetDecisions has a NEW self=None commit; stale previous self is not proof of an ongoing reload.
   var solo=ECombatDecision.None;var squad=ESquadDecision.GroupSearch;p.Arbitrate(b.Decision,ref solo,ref squad,ESelfActionType.None,b.GoalEnemy);
   Check(solo==ECombatDecision.Freeze,"new native self=None commit replaces stale self state");
  }
  foreach(string action in new[]{"Hold","Shoot","Push","Search","Flank","Cover","FallBack","Grenade","ContinuePush","KeepAngle"})
  {
   var(p,b,s)=Setup(action is "ContinuePush" or "KeepAngle"?"A03":action is "Shoot" or "Cover"?"A02":"A01",action);
   s.Expected=b.Decision.CurrentCombatDecision=ECombatDecision.Freeze;
   s.Execution.Accept(10,s.Feature,action,"target");
   b.CurrentAction=action switch{"Hold"=>new FreezeAction(),"Shoot" or "KeepAngle"=>new StandAndShootAction(),"Push" or "ContinuePush"=>new RushEnemyAction(),
    "Search"=>new SearchAction(),"Flank"=>new ShiftCoverAction(),"Grenade"=>new ThrowGrenadeAction(),_=>new SeekCoverAction()};
   for(int i=1;i<=60;i++){Time.time=10+i*.125f;p.Sample(b,s);s.Execution.Accept(Time.time,s.Feature,action,"target");}
   Check(p.Yields==0&&s.Execution.Phase=="executing","real native action is observed "+action);
   // A null native action must expire even though the decision and label are correct, as in Maksim's raid.
   b.CurrentAction=null;for(int i=61;i<=80;i++){Time.time=10+i*.125f;p.Sample(b,s);}
   Check(p.Yields==0&&s.Execution.MissingFor>0,"observation loss is diagnostic, not a tactical veto: "+action);
   if(s.Feature=="A02")Check(s.VisibleEngine.Refusals==0,"observation cannot mark native refusal");
  }
  {
   var(p,b,s)=Setup("A01","Hold");s.Expected=b.Decision.CurrentCombatDecision=ECombatDecision.Freeze;b.CurrentAction=null;
   s.Execution.Accept(10,s.Feature,s.Decision,"target");
   for(int i=1;i<=20;i++){Time.time=10+i*.125f;if(s.Execution.Active)s.Execution.Accept(Time.time,s.Feature,s.Decision,"target");p.Sample(b,s);}
   Check(p.Yields==0,"missing action observation does not cancel accepted Hold");
   Check(s.Engine.Failures==0&&!s.Execution.CoolingDown(Time.time),"observation adds neither failure penalty nor retry veto");
   Check(!s.Execution.CoolingDown(16),"native retry cooldown bounded");
  }
  foreach(string pause in new[]{"medical","reload","door","self","squad","grenade"})
  {
   var(p,b,s)=Setup();b.CurrentAction=null;s.Execution.Accept(10,"A01",s.Decision,"target");
   switch(pause){case "medical":b.BotOwner.Medecine.Using=true;break;case "reload":b.BotOwner.WeaponManager.Reload.Reloading=true;break;
    case "door":b.BotOwner.DoorOpener.Interacting=true;break;case "self":b.Decision.CurrentSelfDecision=ESelfActionType.Reload;break;
    case "squad":b.Decision.CurrentSquadDecision=ESquadDecision.Retreat;break;case "grenade":b.Decision.CurrentCombatDecision=ECombatDecision.AvoidGrenade;break;}
   for(int i=1;i<=80;i++){Time.time=10+i*.125f;p.Sample(b,s);}
   Check(p.Yields==0&&s.Execution.Phase=="paused"&&s.Execution.MissingFor==0,"watchdog cannot interrupt "+pause);
  }
  foreach(double interval in new[]{.05,.125,.25,.5,3.0})
  {
   var x=new DecisionExecution();x.Accept(0,"A01","Hold","x");double now=0;string reason="";
   for(int i=0;i<100&&reason=="";i++){now+=interval;x.Accept(now,"A01","Hold","x");reason=x.Sample(now,false,false);}
   Check(reason=="execution-start-not-observed"&&!x.CoolingDown(now),"watchdog independent of tick rate "+interval);
   x.Clear();Check(!x.CoolingDown(now),"release adds no retry delay");
  }
  {var x=new DecisionExecution();x.Accept(1,"A01","Hold","x");x.Sample(1.5,false,false);x.Accept(2,"A01","Hold","y");Check(x.MissingFor==0&&x.Target=="y","new target starts separate observation");x.Sample(1,false,false);Check(!x.Active,"clock rollback clears observation");}
  Console.WriteLine($"Arbitration: {checks} checks of production continuation, priority, native observation and timeout. Native objects doubled; no raid.");
 }
}

// Engine data doubles only. Production adapter methods and core policies are compiled unmodified by Program.cs.
static class Time{public static float time;public static int frameCount;}
enum ECombatDecision{None,Freeze,SeekCover,Retreat,AvoidGrenade,DogFight,MeleeAttack,FightZombies,StandAndShoot}
enum ESquadDecision{None,Surround,Retreat,Suppress,PushSuppressedEnemy,BoundingRetreat,Regroup,SpreadOut,HoldPositions,Help,Search,GroupSearch,FutureUnknown}
enum ESelfActionType{None,Reload,FirstAid,Surgery,Stims}
enum Tactic{Hold,FallBack,Shoot,Push,Search,Flank,Cover,Grenade}
class BotComponent{public bool Pmc=true,BotActive=true,IsDead,GameEnding,SAINLayersActive=true,Hurt,LowAmmo,Suppressed;public Vector3 Position;public string ProfileId="pmc";public BotDecisionManager Decision=new();public Owner BotOwner=new();public Player Player=new();public Grenade Grenade=new();public Enemy GoalEnemy=new();public EnemyController EnemyController=new();public Cover Cover=new();public object CurrentAction;}
class BotDecisionManager{public BotComponent Bot;public float TimeSinceChangeDecision;public ECombatDecision CurrentCombatDecision;public ESelfActionType CurrentSelfDecision;public ESquadDecision CurrentSquadDecision;public DogFight DogFightDecision=new();public object EnemyDecisions=new();}
class Owner{public Weapon WeaponManager=new();public Medicine Medecine=new();public Door DoorOpener=new();}
class Weapon{public Reload Reload=new();public Selector Selector=new();public Grenade Grenades=new();public bool IsMelee,HaveBullets=true;public object _currentWeaponInfo=new();}
class Reload{public bool Reloading;}class Selector{public bool IsChanging;}class Medicine{public bool Using;}class Door{public bool Interacting;}class DogFight{public bool DogFightActive;}
class Grenade{public object GrenadeDangerPoint;public bool ThrowindNow;}
class Player{public bool Downed;public Hands HandsController=new();}class Hands{public Item Item=new();public bool Busy;public bool IsInInteractionStrictCheck()=>Busy;}class Item{public string Id="weapon";}interface IMedsController{}class Meds:Hands,IMedsController{}
class Enemy{public bool CanShoot;public float RealDistance=10;public bool IsZombie,IsVisible;public string EnemyProfileId="target";public float TimeSinceLastKnownUpdated;public Places KnownPlaces=new();}class Places{public int? LastKnownPosition=1;}class EnemyController{public object KnownEnemies=new();}
struct Vector3{public float x,y,z;public Vector3(float x,float y,float z){this.x=x;this.y=y;this.z=z;}public float sqrMagnitude=>x*x+y*y+z*z;public static Vector3 operator -(Vector3 a,Vector3 b)=>new(a.x-b.x,a.y-b.y,a.z-b.z);}
enum CoverStatus{Far,CloseToCover,InCover}
class CoverData{public bool IsBad;}
class CoverPoint{public bool Valid=true,Spotted;public float Distance=5;public CoverStatus PathDistanceStatus;public CoverData CoverData=new();public float GetDistance(Vector3 pos)=>Distance;}
class Cover{public CoverPoint CoverPoint_MovingTo,CoverInUse;public bool Sprinting=true;}
static class SourceCoverAccess{public static void Accept(Cover cover){cover.CoverInUse=cover.CoverPoint_MovingTo;cover.CoverPoint_MovingTo=null;cover.Sprinting=false;}}
class Config{public bool Enabled=true;public ConfigToggle MedicalChoice=new(),ReloadTiming=new();public float ContactLifetime=30,Commitment=2;public VisibleConfig VisibleCombat=new();}class VisibleConfig{public float Commitment=2,RiskTolerance=1;}class ConfigToggle{public bool Enabled=true;}
class Situation{public string Target="target";public bool Hurt,LowAmmo,Suppressed;public Fear Fear=new();public Loadout Loadout=new();public object Opponent;}
class VisibleSituation{public float CoverRouteRisk,CoverExposure,RecentHitAt;public bool RecentHit;public string ThreatSignature="",ContinuationReason="",ProbePendingReason="";public bool Pending;}
class Fear{public bool Panic;}class FearRuntime{public Fear Current=new();}class Loadout{public string PlanKey="default";}class Probe{public CoverPoint Cover;}
class Policy{public bool Active;}class Runtime{public Policy Policy=new();}class Progress{public bool IsBlocked;public bool Blocked(string key,float now)=>IsBlocked;}class Intent{public bool Allowed=true;public bool Allows(string target,float time)=>Allowed;}
class Engine{public int Failures;public void Fail(Tactic tactic,float now,string reason)=>Failures++;}
class VisibleEngine{public bool KeepPending=true;public int PendingChecks,Refreshed;public bool TryContinuePending(VisibleSituation v){PendingChecks++;return KeepPending;}public void ProbeRefreshed()=>Refreshed++;public int Refusals;public void NotifyNativeRefusal()=>Refusals++;}
class BotState
{
 public Vector3 Position;public string ProfileId="pmc";public PlanServiceRuntime PlanService=new();public ServiceRuntime Medical=new(),ReloadTiming=new(),Crossfire=new();public Handoff Handoff=new();
 public BotState(BotComponent b,Config c,string session){}public bool ArbitratingSquad,Retired;public string Feature="",Decision="",ActiveLabel="",ArbitrationKey="",ArbitrationReason="",ArbitrationNative="";
 public float LastSelection,ExecutionSampleAt,ArbitrationAt=-100;public int RequestedFrame=-1;public ECombatDecision Expected;public Situation Situation=new();public VisibleSituation VisibleSituation=new();
 public float CoverRunAnchorAt=-1,CoverRunBreakCountedAt;public Vector3 CoverRunAnchor;public DecisionExecution Execution=new();public Engine Engine=new();public VisibleEngine VisibleEngine=new();public Runtime CoverFeint=new(),Relocation=new(),Emergency=new();public Progress Progress=new();public CoverPoint SelectedCover;
 public Probe Probe=new();public FearRuntime Fear=new();public Loadout LoadoutAtSelection=new();public Intent Intent=new();
}
static class NativeAdapter{public static bool IsSupportedAi(BotComponent b)=>b.Pmc;public static Situation Capture(BotComponent b,Enemy e,bool authority,bool ghost)=>new(){Target=e.EnemyProfileId,Hurt=b.Hurt,LowAmmo=b.LowAmmo,Suppressed=b.Suppressed};}
static class VisibleAdapter{public static bool Pending;public static bool WeaponReady(BotComponent b)=>!b.BotOwner.WeaponManager.Reload.Reloading;public static bool ValidCover(BotComponent b,CoverPoint p)=>p?.Valid==true;public static VisibleSituation Capture(BotComponent b,Enemy e,Situation s,BotState state,Config c)=>new(){Pending=Pending};}
static class ProgressAdapter{public static string CoverKey(CoverPoint p)=>"cover";}static class VisibleEnemyDecision{public static string Ineligible(VisibleSituation v,Config c)=>v.Pending?"exposure-probe-pending":null;}
class Log{public void LogWarning(string text)=>throw new Exception(text);}
class FreezeAction{}class StandAndShootAction{}class RushEnemyAction{}class SearchAction{}class ShiftCoverAction{}class SeekCoverAction{}class ThrowGrenadeAction{}
partial class Plugin
{
 // Native scope/release helpers are exercised with production bodies in SeasonalCoverHarness.
 static bool CoverExposed(BotComponent b,CoverPoint p)=>p?.Spotted==true;
 static void CoverExposureRetained(Cover c){if(c.CoverInUse?.Spotted==true)c.CoverInUse=null;if(c.CoverPoint_MovingTo?.Spotted==true)c.CoverPoint_MovingTo=null;}
 private static bool ReadDowned(Player p)=>p.Downed;
 public int Suspends;public bool Service(BotComponent b,BotState s,bool committed)=>ObservePlanService(b,s,committed);
 public string ServiceOwned(BotComponent b,BotState s,bool committed)=>OwnedPlanService(b,s,committed);
 void Release(BotState s,string why,bool clearIntent=true,bool suspendPlan=false){if(!suspendPlan||clearIntent)throw new Exception("must preserve engine plans");Suspends++;s.ActiveLabel=s.Decision="";s.Execution.Clear();}
 void ContinuityEvent(BotState s,string phase,string reason,string feature=null,string action=null)=>Events.Add(phase+":"+reason);
 public static Plugin Instance;public Config config=new();public object world=new();public bool Authority=true,IsGhost,AllowChoose=true,ThrowChoose,CustomLayerChoice;public string session="raid";
 public Dictionary<BotComponent,BotState> states=new();public List<string> Events=new();public int ChooseCalls,Yields;Log Logger=new();
 bool Ghost(string profile)=>IsGhost;
 public void Continue(BotDecisionManager d,ref bool result)=>ContinueCover(d,ref result);
 public void Arbitrate(BotDecisionManager d,ref ECombatDecision solo,ref ESquadDecision squad,ESelfActionType self,Enemy e)=>ArbitrateSquad(d,ref solo,ref squad,self,e);
 public void Sample(BotComponent b,BotState s)=>SampleDecisionExecution(b,s);
 public bool Priority(BotComponent b,BotState s)=>NativeChoiceHasPriority(b,s);
 static bool Choose(object decisions,ref ECombatDecision result,Enemy enemy,object known,ref bool chosen)
 {
  var p=Instance;p.ChooseCalls++;var s=p.states.Values.Single();if(p.ThrowChoose)throw new Exception("test");
  if(NativeChoiceHasPriority(p.states.Keys.Single(),s)||!p.AllowChoose)return true;
  if(p.CustomLayerChoice)return !p.ContinueCrossfireDecision(p.states.Keys.Single(),s,ref result,ref chosen);
  s.RequestedFrame=Time.frameCount;
  s.Expected=result=ECombatDecision.Freeze;chosen=true;return false;
 }
 void RefreshProgress(BotComponent b,BotState s){}void RefreshFear(BotComponent b,BotState s){}Loadout ReadLoadout(BotComponent b,BotState s)=>new();object ReadOpponent(BotComponent b,Enemy e,BotState s,Situation f)=>null;
 bool RefreshCrossfire(BotComponent b,BotState s,bool mayStart)=>s.Crossfire.Policy.Active;
 void Emit(BotState s,string stage,string reason,string feature=null)=>Events.Add(reason);
 void YieldToSain(BotState s,string reason,string kind="unavailable"){Yields++;s.ActiveLabel="";s.Decision="";s.RequestedFrame=-1;s.Execution.Clear();}
}

internal static class ContactSafety {internal static bool Allowed=true;internal static bool Allows(object b,object e)=>Allowed;}
class ServiceRuntime{public bool Fault;public ServicePolicy Policy=new();}
class ServicePolicy{public bool Active;public ServiceTrace Trace;}
class ServiceTrace{public string Choice="",Weapon="",State="";public bool Started,Accepted;}
class Handoff{public void Clear(){}}
static class Api{public static class Decisions{public static string Label="";public static void Set(string profile,string label)=>Label=label;}}
