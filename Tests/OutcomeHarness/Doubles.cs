using Comet.Core;
using SAIN.Components;
using SAIN.SAINComponent.Classes.EnemyClasses;
using SAIN.Preset.Shared.Enums;
using EFT;
using EFT.Ballistics;
using UnityEngine;
namespace UnityEngine {
 public static class Time {public static float time,realtimeSinceStartup;public static int frameCount;}
 public struct Vector3(float x,float y,float z) {
  public float x=x,y=y,z=z;
  public static float Distance(Vector3 a,Vector3 b)=>(float)Math.Sqrt(Math.Pow(a.x-b.x,2)+Math.Pow(a.y-b.y,2)+Math.Pow(a.z-b.z,2));
 }
}
namespace HarmonyLib {
 public class Harmony {public void Patch(object method,HarmonyMethod? postfix=null){}}
 public class HarmonyMethod(Type type,string name){}
 public static class AccessTools {public static object? Method(Type t,string name,Type[] args)=>t.GetMethod(name,args);}
}
namespace EFT {public enum EBodyPart {Common} public enum EDamageType {Bullet,Other} public enum ETagStatus {Healthy,Injured}}
namespace EFT.Ballistics {public class DamageInfo {public EDamageType DamageType=EDamageType.Bullet;public bool DelayedDamage;public Shooter? Player;}
 public class Shooter {public Identity? iPlayer;} public class Identity {public string ProfileId="enemy";}}
namespace SAIN.Preset.Shared.Enums {
 public enum ECombatDecision {RushEnemy,SeekCover,Retreat,StandAndShoot,Search,ShiftCover,Freeze}
 public enum ESelfActionType {None,FirstAid} public enum ESquadDecision {None,Other}
}
namespace SAIN.Helpers {public static class Sounds {public static bool IsGunShot(this int s)=>s==1;}}
namespace SAIN.SAINComponent.Classes {
 public class SAINBotMedicalClass {public BotComponent Bot=null!;public void GetHit(DamageInfo info,EBodyPart body,float damage){}}
}
namespace SAIN.SAINComponent.Classes.EnemyClasses {
 // This boundary deliberately provides neither live enemy position nor enemy health.
 public class Enemy {public string EnemyProfileId="enemy";public bool IsVisible=true,Valid=true;public float TimeSinceLastKnownUpdated;public Places KnownPlaces=new();}
 public class Place {public float TimeSincePositionUpdated;public int? SoundType=1;}
 public class Places {public Vector3? LastKnownPosition=new(20,0,0);public Place? LastSeenPlace=new(),LastHeardPlace=new();}
}
namespace SAIN.Components {
 public class BotComponent {
  public bool Pmc=true,BotActive=true,IsDead,GameEnding,SAINLayersActive=true;public string ProfileId="observer";
  public Vector3 Position;public Player Player=new();public Mem Memory=new();public Owner BotOwner=new();public Decisions Decision=new();
  public Enemy? GoalEnemy=new();public Enemies EnemyController=new();public Covers Cover=new();public object? CurrentAction=new SeekCoverAction();
  public BotComponent(){EnemyController.KnownEnemies.Add(GoalEnemy!);}
 }
 public class SeekCoverAction {} public class RushEnemyAction {} public class FreezeAction {} public class StandAndShootAction {} public class SearchAction {} public class ShiftCoverAction {}
 public class Enemies {public List<Enemy> KnownEnemies=new();public Enemy? GetEnemy(string id,bool known)=>KnownEnemies.Find(e=>e.EnemyProfileId==id);}
 public class Player {public bool IsAI=true;public HealthController HealthController=new();}
 public class HealthController {public bool Throw;public float Value=1;public Health GetBodyPartHealth(EBodyPart part,bool rounded){if(Throw)throw new Exception("injected own-health failure");return new(){Normalized=Value};}}
 public class Health {public float Normalized;}
 public class Mem {public HealthState Health=new();}public class HealthState {public ETagStatus HealthStatus;}
 public class Owner {public Door DoorOpener=new();public bool WeaponReady=true;}public class Door {public bool Interacting;}
 public class Decisions {public ECombatDecision CurrentCombatDecision=ECombatDecision.SeekCover;public ESelfActionType CurrentSelfDecision;public ESquadDecision CurrentSquadDecision;}
 public class Covers {public CoverPoint? CoverInUse,CoverPoint_MovingTo=new();}
 public class CoverPoint {public Vector3 Position=new(2,0,0);public bool Valid=true;}
}
namespace Comet.Client {
 internal class KnownThreat {public string Id="enemy";public Vector3 Position=new(20,0,0);}
 internal class ExposureProbe {public float At;public Vector3 Origin;public double Current=.8;public List<KnownThreat> Threats=new(){new()};}
 internal static class VisibleAdapter {public static bool ValidCover(BotComponent b,CoverPoint c)=>c.Valid;public static bool WeaponReady(BotComponent b)=>b.BotOwner.WeaponReady;}
 internal static class NativeAdapter {public static bool IsPmcAi(BotComponent b)=>b.Pmc&&b.Player.IsAI;}
 internal class FlankRuntime {public CoverPoint? Point;}
 internal class BotState {public FlankRuntime Flank=new();public OutcomeRuntime Outcomes=new();public string ProfileId="observer",Name="Test PMC",Id="native-observer",Decision="Cover",Feature="A02",ActiveLabel="COMET:Cover";
  public string CounterFeature="";public int CounterEncounter,CounterOpportunity;public bool CounterObserverFailed;public MixTrace? Mixing;
  public ECombatDecision Expected=ECombatDecision.SeekCover;public CoverPoint? SelectedCover=new();public ExposureProbe? Probe=new();public Situation? Situation;public List<Candidate> Candidates=new();}
 internal static class Diagnostics { internal static string Serialize(object value)=>Newtonsoft.Json.JsonConvert.SerializeObject(value,Json);public static Newtonsoft.Json.JsonSerializerSettings Json=new(){ContractResolver=new Newtonsoft.Json.Serialization.CamelCasePropertyNamesContractResolver()};}
 internal class Sink {public List<string> Lines=new();public void Write(string s)=>Lines.Add(s);}
 internal class Log {public List<string> Warnings=new();public void LogWarning(string s)=>Warnings.Add(s);}
 internal static class Api {public static class Decisions {public static List<string> Lines=new();public static void Publish(string line,Action<Exception> failure)=>Lines.Add(line);}}
 public sealed partial class Plugin {
  internal static Plugin? Instance;internal CometConfig? config=new();internal bool Authority=true,IsGhost,subscriberWarned;internal string session="e04-harness";internal long sequence;
  internal Sink? diagnostics=new();internal Log Logger=new();internal Dictionary<BotComponent,BotState> states=new();
  private bool Ghost(string id)=>IsGhost;private bool ThreatUsable(BotComponent b,Enemy e)=>e.Valid&&b.EnemyController.KnownEnemies.Contains(e);
  private static DoorPoint DP(Vector3 p)=>new(p.x,p.y,p.z);
  internal void Tick(BotComponent b,BotState s)=>SampleOutcome(b,s);
  internal DoorPoint? Destination(BotComponent b,BotState s,string action)=>OutcomeDestination(b,s,action);
  internal bool ThrowCounterEmit;internal int CounterEvents;
  internal void CounterTick(BotComponent b,BotState s)=>SampleCounterExecution(b,s);
  private void Emit(BotState s,string stage,string reason){if(ThrowCounterEmit)throw new Exception("injected diagnostic failure");if(stage!="started")throw new Exception("wrong stage");CounterEvents++;}
  internal OutcomeTrace? Read(BotComponent b,BotState s)=>ReviewOutcome(b,s);
  internal void End(BotState s)=>EndOutcome(s,"outcome-raid-ended");
  internal void Hit(BotComponent b,float damage,string id="enemy",bool delayed=false)=>OutcomeHit(new(){Bot=b},new(){Player=new(){iPlayer=new(){ProfileId=id}},DelayedDamage=delayed},damage);
  internal void Choice(BotState s,Situation situation)=>RecordOutcomeChoice(s,situation);
 }
}
