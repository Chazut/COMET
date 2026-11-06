using Comet.Core;
using Comet.Client;
using SAIN.Components;
using SAIN.Models.Enums;
using SAIN.SAINComponent.Classes.Mover;
using SAIN.Preset.Shared.Enums;
using EFT;
using EFT.Interactive;
using UnityEngine;
using System.Reflection;

namespace UnityEngine {
 public static class Time {public static float time,realtimeSinceStartup;public static int frameCount;}
 public struct Vector3(float x,float y,float z) {
  public float x=x,y=y,z=z;public static Vector3 up=>new(0,1,0);public float sqrMagnitude=>x*x+y*y+z*z;public float magnitude=>(float)Math.Sqrt(sqrMagnitude);
  public Vector3 normalized=>magnitude>0?new(x/magnitude,y/magnitude,z/magnitude):default;
  public static Vector3 operator +(Vector3 a,Vector3 b)=>new(a.x+b.x,a.y+b.y,a.z+b.z);
  public static Vector3 operator -(Vector3 a,Vector3 b)=>new(a.x-b.x,a.y-b.y,a.z-b.z);
 }
 public enum QueryTriggerInteraction {Ignore}
 public class Collider {public Door? Door;public T? GetComponentInParent<T>() where T:class=>Door as T;}
 public struct RaycastHit {public Collider collider;}
 public static class Physics {public static Collider? Blocker;public static bool SphereCast(Vector3 o,float r,Vector3 d,out RaycastHit hit,float length,int mask,QueryTriggerInteraction q){hit=new(){collider=Blocker!};return Blocker!=null;}}
}
namespace HarmonyLib {
 public static class Priority {public const int First=800,Last=0;}
 public class HarmonyMethod(Type t,string n){public string Name=n;public int priority=400;}
 public class Harmony {public List<(MethodInfo, HarmonyMethod?,HarmonyMethod?)> Patches=new();public void Patch(MethodInfo? m,HarmonyMethod? prefix=null,HarmonyMethod? postfix=null,HarmonyMethod? finalizer=null){if(m==null)throw new Exception("Missing hook");Patches.Add((m,prefix,postfix));}}
 public static class AccessTools {const BindingFlags Flags=BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
  public static PropertyInfo? Property(Type t,string n)=>t.GetProperty(n,Flags);public static FieldInfo? Field(Type t,string n)=>t.GetField(n,Flags);public static MethodInfo? Method(Type t,string n)=>t.GetMethod(n,Flags);public static Type? TypeByName(string n)=>null;}
}
namespace EFT {public enum EInteractionType {Open,Close,Breach} public static class LayersMaskController {public const int PlayerStaticDoorMask=1;}}
namespace EFT.Interactive {
 public enum EDoorState {Open,Shut,Interacting,Locked,Breaching}
 public class WorldInteractiveObject {
  public class InteractionState {public EDoorState ResultState;public bool Break;public float ResultAngle,ProgressTime,Duration=1;
   public bool IsInProgress=>ProgressTime<Duration&&!Break;public bool TimeIsOver=>ProgressTime>=Duration;
   public void SetEndData(InteractionState x){ResultState=x.ResultState;ProgressTime=x.ProgressTime;Duration=x.Duration;}}
  protected InteractionState _interaction=new(),_previousInteraction=new();public InteractionState Native=>_interaction;
  public bool ForceLocalInteraction,NoInteractionsAllowed,Operatable=true,enabled=true;public GameObject gameObject=new();
  public string Id="door";public float CurrentAngle;public EDoorState FallbackState=EDoorState.Shut;
  public void LockForInteraction()=>Plugin.Generation(this);
  public void InitializeSmoothOpenInteraction(EDoorState state,bool confirmed=false){Plugin.Generation(this);_interaction.ResultState=state;_interaction.ResultAngle=state==EDoorState.Open?90:0;_interaction.ProgressTime=0;_interaction.Break=false;}
 }
 public class GameObject {public bool activeInHierarchy=true;}
 public class Door:WorldInteractiveObject {private EDoorState state=EDoorState.Shut;public Action? OnSet;public bool IgnoreSet;
  public EDoorState DoorState{get=>state;set{if(!IgnoreSet)state=value;OnSet?.Invoke();}}public Collider Collider=new();public Door(){Collider.Door=this;}}
 public class SpecialDoor:Door {}
}
namespace SAIN.Models.Enums {public enum EBotMoveStatus {Moving,DoorInteraction,Paused,Complete} public enum ESprintUrgency {Normal}}
namespace SAIN.Preset.Shared.Enums {public enum ECombatDecision {Search,SeekCover,RushEnemy,StandAndShoot,AvoidGrenade} public enum ESelfActionType {None,FirstAid} public enum ESquadDecision {None,Help}}
namespace SAIN.Classes {
 public class PlayerMovementController {private float _targetMoveSpeed=.8f;public float Speed=>_targetMoveSpeed;public bool Sprint;
  public bool ThrowOnSpeed;
  public void SetTargetMoveSpeed(float value){if(ThrowOnSpeed)throw new ObjectDisposedException("controller");Plugin.SpeedHook(this,ref value);_targetMoveSpeed=value;}public void SetWantToSprint(bool value){Sprint=value;}
  public void ExternalSpeed(float value)=>_targetMoveSpeed=value;
 }
}
namespace SAIN.Components {
 public class BotComponent {
  public bool Pmc=true,BotActive=true,IsDead,GameEnding,SAINLayersActive=true;public string ProfileId="pmc";
  public Vector3 Position=new(0,0,-1);public object? CurrentAction=new object();public Decisions Decision=new();public Owner BotOwner=new();public Player Player=new();
  public PlayerComponent PlayerComponent=new();public SAINMoverClass Mover;public DoorOpener DoorOpener;public bool IsAI=true;
  public BotComponent(){Mover=new(this);DoorOpener=new(this);}
 }
 public class PlayerComponent {public SAIN.Classes.PlayerMovementController CharacterController=new();}
 public class Player {public MovementContext MovementContext=new();public EFT.EPlayerState CurrentStateName;}
 public class MovementContext {public bool IsGrounded=true;public object? CurrentState=new();public InteractionInfo InteractionInfo=new();}
 public class InteractionInfo {public WorldInteractiveObject? WorldInteractiveObject;}
 public class Decisions {public ECombatDecision CurrentCombatDecision=ECombatDecision.Search;public ESelfActionType CurrentSelfDecision;public ESquadDecision CurrentSquadDecision;}
 public class Owner {public VoxelData VoxelesPersonalData=new();public NativeOpener DoorOpener=new();}
 public class NativeOpener {public bool Interacting;}
 public class VoxelData {public Voxel? CurVoxel=new();}public class Voxel {public List<Link> DoorLinks=new();}
 public class Link {public Door Door=new();public DoorGeometry Geometry=new(){Id="door",Axis=new(1,0,0),Normal=new(0,0,1),Center=new(0,0,0),HalfWidth=.5};}
}
namespace SAIN.SAINComponent.Classes.Mover {
 public class Corner {public Vector3 Position;}
 public interface IBotPathData {bool Moving{get;} EBotMoveStatus Status{get;}float TimeStarted{get;}object? NavMeshPath{get;}Vector3 Destination{get;}List<Corner> PathCorners{get;}int CurrentIndex{get;}Corner GetCurrentCorner();}
 public class BotPathDataManual(BotComponent bot):IBotPathData {
  protected readonly BotComponent Bot=bot;
  public bool CancelRequested,PausedRequested,WantToSprint=true;public ESprintUrgency SprintUrgency;public EBotMoveStatus Status{get;set;}=EBotMoveStatus.Moving;public bool Moving=>Status!=EBotMoveStatus.Complete;
  public float TimeStarted{get;set;}=0;public object? NavMeshPath{get;set;}=new();public Vector3 Destination{get;set;}=new(0,0,5);
  public List<Corner> PathCorners{get;set;}=new(){new(){Position=new(0,0,5)}};public int CurrentIndex{get;set;}
  public Corner GetCurrentCorner()=>PathCorners[CurrentIndex];public void TickPath(float a,float b){}
  public void RequestStartSprint(ESprintUrgency u,string reason){WantToSprint=true;Plugin.SprintHook(this,Bot,true);}
  public void RequestEndSprint(ESprintUrgency u,string reason){WantToSprint=false;Plugin.SprintHook(this,Bot,false);}
  public void Cancel(float time)=>CancelRequested=true;
 }
 public class SAINMoverClass(BotComponent bot) {public BotComponent Bot=bot;public IBotPathData? ActivePath;public void SetTargetMoveSpeed(float speed)=>Bot.PlayerComponent.CharacterController.SetTargetMoveSpeed(speed);}
 public struct DoorDataStruct(Link link) {
  public Link Link=link;public Door Door=link.Door;public bool Range=true,Cooldown=true;
  public void ManualUpdate(Vector3 p){}public bool InRangeToInteract(Door d)=>Range;public bool CanInteractByTime(float time)=>Cooldown;
 }
 public class DoorOpener(BotComponent bot) {
  public BotComponent Bot=bot;public bool Interacting;private float _doorInteractionEndTime=1;public int ClearCount;private void Clear(){Interacting=false;ClearCount++;}
  public bool TryInteractWithDoor(EInteractionType t,float time,DoorDataStruct d)=>true;
  private int _interactionDoorIndex;private List<DoorDataStruct> _interactionDoors{get;}=new();public int Index=>_interactionDoorIndex;
  public DoorDataStruct GetActiveDoor()=>_interactionDoors.Count>0?_interactionDoors[_interactionDoorIndex]:default;
  public List<DoorDataStruct> TestDoors=>_interactionDoors;
  public bool SelectDoor(out EInteractionType interactionType,out DoorDataStruct currentDoor,IBotPathData pathData){interactionType=EInteractionType.Open;currentDoor=default;return false;}
 }
}
namespace Comet.Client {
 internal static class NativeAdapter {internal static bool IsPmcAi(BotComponent b)=>b.Pmc&&b.IsAI;}
 internal static class ProgressAdapter {internal static string Destination(Vector3 p)=>"goal";}
 internal class DoorWork {internal DoorDataStruct Data;internal DoorGeometry Geometry=new();}
 internal class Policy {internal bool Active,Planning,Pending;}
 internal class DoorRuntime {internal Policy Policy=new();}
 internal class GrenadeRuntime {internal bool Handled;internal IBotPathData? Path;internal object? Mesh;internal float CommitUntil;}
 internal class CrossfireRuntime {internal bool Layer;internal Policy Policy=new();internal IBotPathData? Path;internal float PathStarted;}
 internal class ReservationRuntime {internal DoorReservationLease? Lease;}
 internal class BotState {internal DoorTransitRuntime Transit=new();internal DoorRecoveryRuntime Recovery=new();internal DoorRuntime Door=new(),Interruption=new();internal object? Support;
  internal bool TacticalDoor;internal GrenadeRuntime GrenadeEscape=new();internal CrossfireRuntime Crossfire=new();internal ReservationRuntime Reservation=new();
  internal string Feature="A01",Decision="Search",ActiveLabel="COMET:Search",Id="native-pmc",ProfileId="pmc",Name="PMC";internal ECombatDecision Expected=ECombatDecision.Search;
  internal PlanProgress Progress=new(new());
 }
 internal class Diagnostics { internal static string Serialize(object value)=>Newtonsoft.Json.JsonConvert.SerializeObject(value,Json);internal List<string> Lines=new();internal void Write(string text)=>Lines.Add(text);
  internal static readonly Newtonsoft.Json.JsonSerializerSettings Json=new(){ContractResolver=new Newtonsoft.Json.Serialization.CamelCasePropertyNamesContractResolver()};}
 internal class Log {internal List<string> Warnings=new();internal void LogWarning(string s)=>Warnings.Add(s);}
 internal static class Api {internal static class DoorMotionNetwork {internal static void Publish(Door d){}internal static void Completed(Door d){}}internal static class Decisions {internal static void Publish(string json,Action<Exception> error){}}}
 public sealed partial class Plugin {
 private bool RecoveryOwnsStall(BotState s)=>false;
  internal static Plugin? Instance;internal CometConfig? config=new();internal bool Authority=true,IsGhost,Busy,ActionMatches=true;internal bool fikaPresent,BridgeReady,SweepAllowed=true;private bool DoorSyncReady=>!fikaPresent||BridgeReady;private bool SwingClear(Door d,float a,float z,BotComponent b,bool h)=>SweepAllowed;
  internal Dictionary<BotComponent,BotState> states=new();internal Diagnostics? diagnostics=new();internal Log Logger=new();private bool subscriberWarned;private string session="c02-harness";private int sequence;
  private readonly DoorWorkBudget doorBudget=new();private static FieldInfo combatNativeSpeed=typeof(SAIN.Classes.PlayerMovementController).GetField("_targetMoveSpeed",BindingFlags.NonPublic|BindingFlags.Instance)!;
  private bool Ghost(string profile)=>IsGhost;private static DoorPoint DP(Vector3 p)=>new(p.x,p.y,p.z);
  private static bool OutcomeAction(BotComponent b,string action)=>Instance!.ActionMatches;
  private static DoorWork? SupportingDoor(BotState s)=>s.TacticalDoor?new():null;
  private static bool TryDoorGeometry(DoorDataStruct d,out DoorGeometry g){g=d.Link.Geometry;return true;}
  private bool DoorBusy(BotComponent b,BotState s,string id)=>Busy;
  private void Release(BotState s,string why){EndDoorRecovery(s,why);EndDoorTransit(s,why);s.ActiveLabel="";}
  internal void Tick(BotComponent b,float now){Time.time=Time.realtimeSinceStartup=now;Time.frameCount++;TransitTick((BotPathDataManual)b.Mover.ActivePath!,b);}
  internal void Watch(BotComponent b)=>WatchDoorTransit(b,states[b]);internal void End(BotState s)=>EndDoorTransit(s,"raid-ended");
  internal static void SpeedHook(SAIN.Classes.PlayerMovementController c,ref float speed)=>TransitSpeed(c,ref speed);
  internal static void SprintHook(BotPathDataManual path,BotComponent b,bool start)=>TransitSprintIntent(path,b,start);
  internal bool Select(BotComponent b,bool nativeResult,ref DoorDataStruct data,ref EInteractionType type){TransitSelect(b.DoorOpener,b.Mover.ActivePath!,ref type,ref data,ref nativeResult);return nativeResult;}
  internal static HarmonyLib.Harmony Hooks(){var h=new HarmonyLib.Harmony();PatchDoorTransit(h);return h;}
 }
}

namespace EFT {public enum EPlayerState {Idle,Approach,BreachDoor,DoorInteraction,DoorInteractionZombieState}
 public class GameWorld {public List<SAIN.Components.Player> AllAlivePlayersList=new();}}
namespace Comfort.Common {public static class Singleton<T> where T:new(){public static T Instance=new();}}
namespace Comet.Client {public sealed partial class Plugin {

 internal static void Generation(WorldInteractiveObject d)=>RecoveryGeneration(d);
 internal static HarmonyLib.Harmony RecoveryHooks(){var h=new HarmonyLib.Harmony();PatchDoorRecovery(h);return h;}
 internal void At(float now){Time.time=Time.realtimeSinceStartup=now;Time.frameCount++;}
 internal void RecoverTick(BotComponent b,float now){At(now);WatchDoorRecovery(b,states[b]);}
 internal void Encounter(BotComponent b,float now){At(now);RecoveryOnPath((BotPathDataManual)b.Mover.ActivePath!,b);}
 internal void RecoverEnd(BotState s)=>EndDoorRecovery(s,"raid-ended");
 internal void Start(BotComponent b,DoorDataStruct data,EInteractionType t=EInteractionType.Open,bool success=true,bool sent=false){
  RecoveryBefore(b.DoorOpener,t,data,out var capture);
  if(success){data.Door.InitializeSmoothOpenInteraction(t==EInteractionType.Close?EDoorState.Shut:EDoorState.Open);data.Door.DoorState=EDoorState.Interacting;
   b.DoorOpener.Interacting=true;if(sent)RecoveryFikaSent(b.Player,data.Door);}
  RecoveryAfter(b.DoorOpener,success,capture);
 }
 internal static void VerifiedFika(bool value)=>recoveryFikaContract=value;
 internal bool Holds(Door d)=>recoveryEpochs.TryGetValue(d,out _);
}}
