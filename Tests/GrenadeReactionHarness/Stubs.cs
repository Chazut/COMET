// Controlled native doubles; G04 and G05 production adapters are linked unchanged.
using System.Reflection;
using Comet.Core;
using UnityEngine;
using SAIN.Components;
namespace UnityEngine {
 public struct Vector3(float x,float y,float z){public static Vector3 operator-(Vector3 a)=>new(-a.x,-a.y,-a.z);public void Normalize(){this=normalized;}public static float Dot(Vector3 a,Vector3 b)=>a.x*b.x+a.y*b.y+a.z*b.z;public float x=x,y=y,z=z;public static Vector3 zero=>new(0,0,0);public static Vector3 up=>new(0,1,0);public float sqrMagnitude=>x*x+y*y+z*z;public float magnitude=>(float)Math.Sqrt(sqrMagnitude);public Vector3 normalized=>magnitude>0?this*(1/magnitude):zero;public static Vector3 operator+(Vector3 a,Vector3 b)=>new(a.x+b.x,a.y+b.y,a.z+b.z);public static Vector3 operator-(Vector3 a,Vector3 b)=>new(a.x-b.x,a.y-b.y,a.z-b.z);public static Vector3 operator*(Vector3 a,float b)=>new(a.x*b,a.y*b,a.z*b);public static float Distance(Vector3 a,Vector3 b)=>(a-b).magnitude;}
 public struct Vector2{public static Vector2 zero=>new();}
 public static class Time{public static float time,realtimeSinceStartup;public static int frameCount;public static void Set(float v){time=realtimeSinceStartup=v;frameCount++;}}
 public class Transform{public Vector3 position;}
 public class Collider{public bool isTrigger;public object? attachedRigidbody;public Dictionary<Type,object> Parts=new();public T? GetComponent<T>() where T:class=>Parts.TryGetValue(typeof(T),out var v)?v as T:null;public T? GetComponentInParent<T>() where T:class=>GetComponent<T>();}
 public class TerrainCollider:Collider{}
 public struct RaycastHit{public Collider? collider;}
 public static class Physics{public static Collider? Hit;public static bool Linecast(Vector3 a,Vector3 b,int mask)=>Hit!=null;public static bool Linecast(Vector3 a,Vector3 b,out RaycastHit hit,int mask){hit=new(){collider=Hit};return Hit!=null;}}
}
namespace EFT {public static class LayersMaskController{public static int HighPolyWithTerrainMask=1;}public class Player{public Vector3 Last;public void Move(Vector2 v){Last=Vector3.zero;}}public class Grenade{public GrenadeSettings GrenadeSettings=new();public string ProfileId="owner";public int Id;public EFT.InventoryLogic.ThrowWeap? WeaponSource=new();public Transform transform=new();}}
namespace EFT.Interactive{public class Door{}}
namespace EFT.InventoryLogic{public class ThrowWeap{public string Id="item",ThrowType="frag_grenade";public bool IsDummy;public double GetExplDelay=3.5;}}
namespace EFT.Ballistics{public enum MaterialType{Glass,GlassShattered,Concrete,Stone,MetalThick,MetalThin,WoodThin}public class BallisticCollider{public MaterialType TypeOfMaterial;}}
namespace SAIN.Models.Enums{public enum ESprintUrgency{High,Low}public enum EGrenadeReaction{None,Scatter,Push}}
namespace SAIN.Components{
 public class BotComponent{public string ProfileId="owner";public bool IsDead,GameEnding,Pmc=true,BotActive=true,SAINLayersActive=true;public Vector3 LookDirection=new(1,0,0);public BotInfo Info=new();public Enemy? GoalEnemy;public Covers Cover=new();public Vector3 Position;public EFT.Player Player=new();public Squad Squad=new();public Grenades Grenade;public SAIN.SAINComponent.Classes.Mover.SAINMoverClass Mover;public BotComponent(){Grenade=new(this);Mover=new(this);}public void ManualUpdate(){}}
 public class BotInfo{public string Personality="Normal";} public class Enemy{public Vector3 EnemyPosition=>KnownPlaces.LastKnownPosition??new();public string EnemyProfileId="enemy",Source="seen";public bool IsVisible;public float TimeSinceLastKnownUpdated;public Places KnownPlaces=new();} public class Places{public Vector3? LastKnownPosition;} public class Covers{public CoverPoint? FindPointInDirection(Vector3 d,float a,float m)=>CoverPoints.FirstOrDefault();public List<CoverPoint> CoverPoints=new();} public class CoverPoint{public Vector3 Position;public CoverData CoverData=new();} public class CoverData{public bool IsBad;}
 public class Squad{public bool BotInGroup=>Members.Count>1;public SquadInfo? SquadInfo;public Dictionary<string,BotComponent> Members=new();}public class SquadInfo{public string GUID="";}public class Grenades{public SAIN.SAINComponent.Classes.WeaponFunction.GrenadeReactionClass GrenadeReactionClass;public Grenades(BotComponent b){GrenadeReactionClass=new(b);}}
}
namespace SAIN.Components.PlayerComponentSpace{public class PlayerComponent{public BotComponent? BotComponent;}}
namespace SAIN.Classes{public class PlayerMovementController{public void SetTargetMoveDirection(){}}}
namespace SAIN.SAINComponent.SubComponents{public class GrenadeTrackerClass(BotComponent bot,EFT.Grenade grenade,Vector3 p,float delay,bool notice){public Vector3 DangerPoint=p;public EFT.Grenade Grenade=grenade;public bool CanReact;}}
namespace SAIN.SAINComponent.Classes.WeaponFunction{
 public class GrenadeReactionClass(BotComponent bot){public float EstimatedTimeRemaining=4;public BotComponent Bot=bot;public Dictionary<object,SAIN.SAINComponent.SubComponents.GrenadeTrackerClass> EnemyGrenadesList=new();public SAIN.SAINComponent.SubComponents.GrenadeTrackerClass? DangerGrenade{get;private set;}public SAIN.Models.Enums.EGrenadeReaction Reaction{get;private set;}public void UpdateDangerGrenade(){DangerGrenade=null;Reaction=SAIN.Models.Enums.EGrenadeReaction.None;}}
}
namespace SAIN.SAINComponent.Classes.Mover{
 public interface IBotPathData{UnityEngine.AI.NavMeshPath NavMeshPath{get;} int CurrentIndex{get;} List<Vector3> PathPoints{get;}}
 public class SAINMoverClass(BotComponent bot){public bool GoToCoverPoint(SAIN.Components.CoverPoint p,bool run,SAIN.Models.Enums.ESprintUrgency u)=>RunToPoint(p.Position,false,-1,u); public bool RunToPoint(Vector3 p,bool complete,float reach,SAIN.Models.Enums.ESprintUrgency u)=>RunToPointByWay(new UnityEngine.AI.NavMeshPath{corners=new[]{Bot.Position,p}},complete,reach,u,false); public bool WalkToPoint(Vector3 p,bool complete)=>RunToPoint(p,complete,-1,SAIN.Models.Enums.ESprintUrgency.Low);public BotComponent Bot=bot;public IBotPathData? ActivePath;public ProneState Prone=new();public int Requests;public bool Accept=true;public bool RunToPointByWay(UnityEngine.AI.NavMeshPath mesh,bool complete,float reach,SAIN.Models.Enums.ESprintUrgency urgency,bool same){Requests++;if(!Accept)return false;ActivePath=new BotPathDataManual{PathFinder=this,NavMeshPath=mesh,PathPoints=mesh.corners.ToList()};return true;}} public class ProneState{public bool Value;public void SetProne(bool v){Value=v;}}
 public class BotPathDataManual:IBotPathData{public object? PathFinder;public int CurrentIndex{get;set;}public UnityEngine.AI.NavMeshPath NavMeshPath{get;set;}=new();public List<Vector3> PathPoints{get;set;}=new();public bool Stopped;public void TickPath(){}private void Stop(bool success,string reason){Stopped=true;((SAINMoverClass)PathFinder!).ActivePath=null;}}
}
namespace HarmonyLib{
 public static class Priority{public const int First=800;}public class HarmonyMethod(Type t,string n){public int priority;}public class Harmony{public void Patch(MethodInfo? m,HarmonyMethod? prefix=null,HarmonyMethod? postfix=null){if(m==null)throw new Exception("missing hook");}}
 public static class AccessTools{public static Type? TypeByName(string n)=>typeof(SAIN.Layers.AvoidGrenadeAction);public static MethodInfo? Method(Type t,string n)=>t.GetMethod(n,BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static);public static MethodInfo? PropertySetter(Type t,string n)=>t.GetProperty(n)!.GetSetMethod(true);}
}
namespace Comet.Core{
 public readonly struct DoorPoint(double x,double y,double z){public readonly double X=x,Y=y,Z=z;public bool Finite=>double.IsFinite(X)&&double.IsFinite(Y)&&double.IsFinite(Z);public double Length=>Math.Sqrt(Dot(this));public double Dot(DoorPoint b)=>X*b.X+Y*b.Y+Z*b.Z;public static DoorPoint operator+(DoorPoint a,DoorPoint b)=>new(a.X+b.X,a.Y+b.Y,a.Z+b.Z);public static DoorPoint operator-(DoorPoint a,DoorPoint b)=>new(a.X-b.X,a.Y-b.Y,a.Z-b.Z);public static DoorPoint operator*(DoorPoint a,double b)=>new(a.X*b,a.Y*b,a.Z*b);}
 public class CometConfig{public bool Enabled=true;public long Revision=1;public OwnGrenadeSafetyConfig OwnGrenadeSafety=new();public GrenadeReactionConfig GrenadeReaction=new();public GrenadeDispersionConfig GrenadeDispersion=new();public static void Range(double v,double a,double b,string n){if(!double.IsFinite(v)||v<a||v>b)throw new ArgumentException(n);}}
 public static class GrenadeChoiceRules{public static bool Finite(double v)=>double.IsFinite(v);}
}
namespace Comet.Client{
 internal class BotState(BotComponent b){internal OwnGrenadeRuntime OwnGrenade=new();internal GrenadeEscapeRuntime GrenadeEscape=new();internal string ProfileId=b.ProfileId,Name=b.ProfileId,Id=b.ProfileId+"-instance";}
 internal static class NativeAdapter{internal static bool IsSupportedAi(BotComponent b)=>b.Pmc;}
 public class Sink{public List<string> Lines=new();public void Write(string s)=>Lines.Add(s);public void Publish(string s,Action<Exception> a){}}
 public static class Api{public static Sink Decisions=new();}public static class Diagnostics{ internal static string Serialize(object value)=>Newtonsoft.Json.JsonConvert.SerializeObject(value,Json);public static Newtonsoft.Json.JsonSerializerSettings Json=new(){ContractResolver=new Newtonsoft.Json.Serialization.CamelCasePropertyNamesContractResolver()};}
 public class Log{public void LogWarning(string s){}}
 public sealed partial class Plugin{
  private bool IsNativeGasDanger(SAIN.SAINComponent.SubComponents.GrenadeTrackerClass? t)=>false;
  private void FilterNativeGas(SAIN.SAINComponent.Classes.WeaponFunction.GrenadeReactionClass n){}

  private bool TryGasAction(BotComponent b)=>false;
  internal static Plugin? Instance;internal Dictionary<BotComponent,BotState> states=new();internal CometConfig? config=new();internal object? world=new();internal bool Authority=true;internal string session="g05-harness";internal long sequence;internal bool subscriberWarned;internal Sink? diagnostics=new();internal Log Logger=new();internal HashSet<string> ghosts=new();internal List<string> Releases=new();
  private static MethodInfo combatStopPath=typeof(SAIN.SAINComponent.Classes.Mover.BotPathDataManual).GetMethod("Stop",BindingFlags.NonPublic|BindingFlags.Instance)!;
  internal Plugin(){Instance=this;PatchOwnGrenadeSafety(new());PatchGrenadeReaction(new());}
  private static string RelocationContactSource(SAIN.Components.Enemy e)=>e.Source;
  private bool Ghost(string id)=>ghosts.Contains(id);private void EnsureClearEvents(){}private static DoorPoint DP(Vector3 p)=>new(p.x,p.y,p.z);private static Vector3 VP(DoorPoint p)=>new((float)p.X,(float)p.Y,(float)p.Z);private static double ClearDangerRadius(EFT.InventoryLogic.ThrowWeap item)=>7;
  private BotState GrenadeState(BotComponent b){if(!states.TryGetValue(b,out var s))states.Add(b,s=new(b));return s;}private void Release(BotState s,string reason)=>Releases.Add(reason);
  internal void Add(BotComponent b)=>GrenadeState(b);internal void Throw(EFT.Grenade g)=>OwnProjectileThrown(g);internal void Tick(BotComponent b){OwnGrenadeUpdate(b);b.Grenade.GrenadeReactionClass.UpdateDangerGrenade();OwnNativeDanger(b.Grenade.GrenadeReactionClass);}internal void Explode(string owner,int id)=>OwnExplosion(owner,id);internal void End(){foreach(var pair in states)DropGrenadeEscape(pair.Key,pair.Value,"grenade-raid-ended");ClearOwnSafety();}
  internal bool Act(BotComponent b)=>EscapeAction(new SAIN.Layers.AvoidGrenadeAction(b));internal bool EscapePath(SAIN.SAINComponent.Classes.Mover.BotPathDataManual p)=>true;internal GrenadeReactionTrace? EscapeTrace(BotComponent b)=>states[b].GrenadeEscape.Trace;
  internal int Count=>ownProjectiles.Count;internal OwnGrenadeSafetyTrace? Trace(BotComponent b)=>states[b].OwnGrenade.Trace;internal bool Cover()=>OwnSolidCover(new(3,0,0),new(0,0,0));internal void Lose(int id){ownProjectiles.Single(x=>x.Evidence.Projectile==id).Grenade.SetTarget(null!);}
 }
}

namespace SAIN.Layers {public class BotAction(BotComponent b){public BotComponent Bot=b;}public class AvoidGrenadeAction(BotComponent b):BotAction(b){public void Update(){}public void Stop(){}}}
namespace UnityEngine {
 public struct Quaternion(float angle){public static Quaternion Euler(float x,float y,float z)=>new(y);public static Vector3 operator*(Quaternion q,Vector3 v){double a=q.angle*Math.PI/180;return new((float)(v.x*Math.Cos(a)+v.z*Math.Sin(a)),v.y,(float)(-v.x*Math.Sin(a)+v.z*Math.Cos(a)));}private float angle=angle;}
}
namespace UnityEngine.AI {
 public enum NavMeshPathStatus{PathComplete,PathPartial,PathInvalid}
 public class NavMeshPath{public Vector3[] corners=Array.Empty<Vector3>();public NavMeshPathStatus status;}
 public struct NavMeshHit{public Vector3 position;}
 public static class NavMesh {
  public const int AllAreas=-1;public static bool Sample=true;public static int Calls;public static Func<Vector3,Vector3,Vector3[]>? Route;public static NavMeshPathStatus Status=NavMeshPathStatus.PathComplete;
  public static bool SamplePosition(Vector3 p,out NavMeshHit hit,float radius,int mask){hit=new(){position=p};return Sample;}
  public static bool CalculatePath(Vector3 a,Vector3 b,int mask,NavMeshPath p){Calls++;p.corners=Route?.Invoke(a,b)??new[]{a,b};p.status=Status;return true;}
 }
}

namespace EFT {public class SmokeGrenade:Grenade{}}

namespace EFT {public class GrenadeSettings {public enum CollisionSounds { frag, smoke } public CollisionSounds CollisionSound;}}
