// Minimal controllable engine doubles. The G04 adapter itself is linked unchanged from Client.
using System.Reflection;
using Comet.Core;
using UnityEngine;
using SAIN.Components;
namespace UnityEngine {
 public struct Vector3(float x,float y,float z){public float x=x,y=y,z=z;public static Vector3 zero=>new(0,0,0);public static Vector3 up=>new(0,1,0);public float sqrMagnitude=>x*x+y*y+z*z;public float magnitude=>(float)Math.Sqrt(sqrMagnitude);public Vector3 normalized=>magnitude>0?this*(1/magnitude):zero;public static Vector3 operator+(Vector3 a,Vector3 b)=>new(a.x+b.x,a.y+b.y,a.z+b.z);public static Vector3 operator-(Vector3 a,Vector3 b)=>new(a.x-b.x,a.y-b.y,a.z-b.z);public static Vector3 operator*(Vector3 a,float b)=>new(a.x*b,a.y*b,a.z*b);public static float Distance(Vector3 a,Vector3 b)=>(a-b).magnitude;}
 public struct Vector2{public static Vector2 zero=>new();}
 public static class Time{public static float time,realtimeSinceStartup;public static int frameCount;public static void Set(float v){time=realtimeSinceStartup=v;frameCount++;}}
 public class Transform{public Vector3 position;}
 public class Collider{public bool isTrigger;public object? attachedRigidbody;public Dictionary<Type,object> Parts=new();public T? GetComponent<T>() where T:class=>Parts.TryGetValue(typeof(T),out var v)?v as T:null;public T? GetComponentInParent<T>() where T:class=>GetComponent<T>();}
 public class TerrainCollider:Collider{}
 public struct RaycastHit{public Collider? collider;}
 public static class Physics{public static Collider? Hit;public static bool Linecast(Vector3 a,Vector3 b,int mask)=>Hit!=null;public static bool Linecast(Vector3 a,Vector3 b,out RaycastHit hit,int mask){hit=new(){collider=Hit};return Hit!=null;}}
}
namespace EFT {public static class LayersMaskController{public static int HighPolyWithTerrainMask=1;}public class Player{public Vector3 Last;public void Move(Vector2 v){Last=Vector3.zero;}}public class Grenade{public string ProfileId="owner";public int Id;public EFT.InventoryLogic.ThrowWeap? WeaponSource=new();public Transform transform=new();}}
namespace EFT.Interactive{public class Door{}}
namespace EFT.InventoryLogic{public class ThrowWeap{public string Id="item",ThrowType="frag_grenade";public bool IsDummy;public double GetExplDelay=3.5;}}
namespace EFT.Ballistics{public enum MaterialType{Glass,GlassShattered,Concrete,Stone,MetalThick,MetalThin,WoodThin}public class BallisticCollider{public MaterialType TypeOfMaterial;}}
namespace SAIN.Models.Enums{public enum EGrenadeReaction{None,Scatter,Push}}
namespace SAIN.Components{
 public class BotComponent{public string ProfileId="owner";public bool IsDead,GameEnding,Pmc=true,BotActive=true;public Vector3 Position;public EFT.Player Player=new();public Squad Squad=new();public Grenades Grenade;public SAIN.SAINComponent.Classes.Mover.SAINMoverClass Mover;public BotComponent(){Grenade=new(this);Mover=new(this);}public void ManualUpdate(){}}
 public class Squad{public Dictionary<string,BotComponent> Members=new();}public class Grenades{public SAIN.SAINComponent.Classes.WeaponFunction.GrenadeReactionClass GrenadeReactionClass;public Grenades(BotComponent b){GrenadeReactionClass=new(b);}}
}
namespace SAIN.Components.PlayerComponentSpace{public class PlayerComponent{public BotComponent? BotComponent;}}
namespace SAIN.Classes{public class PlayerMovementController{public void SetTargetMoveDirection(){}}}
namespace SAIN.SAINComponent.SubComponents{public class GrenadeTrackerClass(BotComponent bot,EFT.Grenade grenade,Vector3 p,float delay,bool notice){public Vector3 DangerPoint=p;public EFT.Grenade Grenade=grenade;}}
namespace SAIN.SAINComponent.Classes.WeaponFunction{
 public class GrenadeReactionClass(BotComponent bot){public BotComponent Bot=bot;public SAIN.SAINComponent.SubComponents.GrenadeTrackerClass? DangerGrenade{get;private set;}public SAIN.Models.Enums.EGrenadeReaction Reaction{get;private set;}public void UpdateDangerGrenade(){DangerGrenade=null;Reaction=SAIN.Models.Enums.EGrenadeReaction.None;}}
}
namespace SAIN.SAINComponent.Classes.Mover{
 public interface IBotPathData{}
 public class SAINMoverClass(BotComponent bot){public BotComponent Bot=bot;public IBotPathData? ActivePath;}
 public class BotPathDataManual:IBotPathData{public object? PathFinder;public int CurrentIndex;public List<Vector3> PathPoints=new();public bool Stopped;public void TickPath(){}private void Stop(bool success,string reason){Stopped=true;((SAINMoverClass)PathFinder!).ActivePath=null;}}
}
namespace HarmonyLib{
 public static class Priority{public const int First=800;}public class HarmonyMethod(Type t,string n){public int priority;}public class Harmony{public void Patch(MethodInfo? m,HarmonyMethod? prefix=null,HarmonyMethod? postfix=null){if(m==null)throw new Exception("missing hook");}}
 public static class AccessTools{public static MethodInfo? Method(Type t,string n)=>t.GetMethod(n,BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static);public static MethodInfo? PropertySetter(Type t,string n)=>t.GetProperty(n)!.GetSetMethod(true);}
}
namespace Comet.Core{
 public readonly struct DoorPoint(double x,double y,double z){public readonly double X=x,Y=y,Z=z;public bool Finite=>double.IsFinite(X)&&double.IsFinite(Y)&&double.IsFinite(Z);public double Length=>Math.Sqrt(Dot(this));public double Dot(DoorPoint b)=>X*b.X+Y*b.Y+Z*b.Z;public static DoorPoint operator+(DoorPoint a,DoorPoint b)=>new(a.X+b.X,a.Y+b.Y,a.Z+b.Z);public static DoorPoint operator-(DoorPoint a,DoorPoint b)=>new(a.X-b.X,a.Y-b.Y,a.Z-b.Z);public static DoorPoint operator*(DoorPoint a,double b)=>new(a.X*b,a.Y*b,a.Z*b);}
 public class CometConfig{public bool Enabled=true;public long Revision=1;public OwnGrenadeSafetyConfig OwnGrenadeSafety=new();public static void Range(double v,double a,double b,string n){if(!double.IsFinite(v)||v<a||v>b)throw new ArgumentException(n);}}
 public static class GrenadeChoiceRules{public static bool Finite(double v)=>double.IsFinite(v);}
}
namespace Comet.Client{
 internal class BotState(BotComponent b){internal OwnGrenadeRuntime OwnGrenade=new();internal string ProfileId=b.ProfileId,Name=b.ProfileId,Id=b.ProfileId+"-instance";}
 internal static class NativeAdapter{internal static bool IsSupportedAi(BotComponent b)=>b.Pmc;}
 public class Sink{public List<string> Lines=new();public void Write(string s)=>Lines.Add(s);public void Publish(string s,Action<Exception> a){}}
 public static class Api{public static Sink Decisions=new();}public static class Diagnostics{ internal static string Serialize(object value)=>Newtonsoft.Json.JsonConvert.SerializeObject(value,Json);public static Newtonsoft.Json.JsonSerializerSettings Json=new(){ContractResolver=new Newtonsoft.Json.Serialization.CamelCasePropertyNamesContractResolver()};}
 public class Log{public void LogWarning(string s){}}
 public sealed partial class Plugin{
  private void FilterNativeGas(SAIN.SAINComponent.Classes.WeaponFunction.GrenadeReactionClass n){}

  internal static Plugin? Instance;internal Dictionary<BotComponent,BotState> states=new();internal CometConfig? config=new();internal object? world=new();internal bool Authority=true;internal string session="g04-harness";internal long sequence;internal bool subscriberWarned;internal Sink? diagnostics=new();internal Log Logger=new();internal HashSet<string> ghosts=new();internal List<string> Releases=new();
  private static MethodInfo combatStopPath=typeof(SAIN.SAINComponent.Classes.Mover.BotPathDataManual).GetMethod("Stop",BindingFlags.NonPublic|BindingFlags.Instance)!;
  internal Plugin(){Instance=this;PatchOwnGrenadeSafety(new());}
  private bool SelectGrenadeEscape(SAIN.SAINComponent.Classes.WeaponFunction.GrenadeReactionClass n)=>false;
  private bool Ghost(string id)=>ghosts.Contains(id);private void EnsureClearEvents(){}private static DoorPoint DP(Vector3 p)=>new(p.x,p.y,p.z);private static Vector3 VP(DoorPoint p)=>new((float)p.X,(float)p.Y,(float)p.Z);private static double ClearDangerRadius(EFT.InventoryLogic.ThrowWeap item)=>7;
  private BotState GrenadeState(BotComponent b){if(!states.TryGetValue(b,out var s))states.Add(b,s=new(b));return s;}private void Release(BotState s,string reason)=>Releases.Add(reason);
  internal void Add(BotComponent b)=>GrenadeState(b);internal void Throw(EFT.Grenade g)=>OwnProjectileThrown(g);internal void Tick(BotComponent b){OwnGrenadeUpdate(b);b.Grenade.GrenadeReactionClass.UpdateDangerGrenade();OwnNativeDanger(b.Grenade.GrenadeReactionClass);}internal void Explode(string owner,int id)=>OwnExplosion(owner,id);internal void End()=>ClearOwnSafety();
  internal bool Path(SAIN.SAINComponent.Classes.Mover.BotPathDataManual path)=>OwnPathTick(path);internal Vector3 Move(BotComponent b,Vector3 v){OwnMoveInput(new(){BotComponent=b},ref v);return v;}
  internal int Count=>ownProjectiles.Count;internal OwnGrenadeSafetyTrace? Trace(BotComponent b)=>states[b].OwnGrenade.Trace;internal bool Cover()=>OwnSolidCover(new(3,0,0),new(0,0,0));internal void Lose(int id){ownProjectiles.Single(x=>x.Evidence.Projectile==id).Grenade.SetTarget(null!);}
 }
}
