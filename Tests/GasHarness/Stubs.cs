// Production G08 adapter + reflection bridge are compiled unchanged. Engine calls are controlled doubles.
using System.Reflection;
using Comet.Core;
using UnityEngine;
using SAIN.Components;
namespace UnityEngine {
 public class Object{public bool Dead;public static bool operator==(Object? a,Object? b)=>ReferenceEquals(a,b)||(b is null&&a?.Dead==true)||(a is null&&b?.Dead==true);public static bool operator!=(Object? a,Object? b)=>!(a==b);public override bool Equals(object? o)=>ReferenceEquals(this,o);public override int GetHashCode()=>base.GetHashCode();}
 public class Component:Object{static int next;private int id=++next;public int GetInstanceID()=>id;public Transform transform=new();public GameObject gameObject=new();}
 public class GameObject{public bool activeInHierarchy=true;}
 public class Transform{public Vector3 position,lossyScale=new(1,1,1);}
 public class SphereCollider:Component{public float radius=4;}
 public struct Vector3(float x,float y,float z){public float x=x,y=y,z=z;public float sqrMagnitude=>x*x+y*y+z*z;public float magnitude=>(float)Math.Sqrt(sqrMagnitude);public Vector3 normalized=>magnitude>0?this*(1/magnitude):new();public static Vector3 operator+(Vector3 a,Vector3 b)=>new(a.x+b.x,a.y+b.y,a.z+b.z);public static Vector3 operator-(Vector3 a,Vector3 b)=>new(a.x-b.x,a.y-b.y,a.z-b.z);public static Vector3 operator*(Vector3 a,float b)=>new(a.x*b,a.y*b,a.z*b);public static float Distance(Vector3 a,Vector3 b)=>(a-b).magnitude;public static Vector3 Lerp(Vector3 a,Vector3 b,float t)=>a+(b-a)*t;}
 public struct Quaternion(float angle){float angle=angle;public static Quaternion Euler(float x,float y,float z)=>new(y);public static Vector3 operator*(Quaternion q,Vector3 v){double a=q.angle*Math.PI/180;return new((float)(v.x*Math.Cos(a)+v.z*Math.Sin(a)),v.y,(float)(-v.x*Math.Sin(a)+v.z*Math.Cos(a)));}}
 public static class Mathf{public static float Max(float a,float b)=>Math.Max(a,b);public static float Lerp(float a,float b,float t)=>a+(b-a)*Math.Clamp(t,0,1);}
 public static class Physics{public static bool Wall;public static bool Linecast(Vector3 a,Vector3 b,int mask)=>Wall;}
 public static class Time{public static float time=10,realtimeSinceStartup=10;public static int frameCount;public static void Set(float t){time=realtimeSinceStartup=t;frameCount++;}}
}
namespace UnityEngine.AI{
 public enum NavMeshPathStatus{PathComplete,PathPartial}
 public class NavMeshPath{public Vector3[] corners=[];public NavMeshPathStatus status;}
 public struct NavMeshHit{public Vector3 position;}
 public static class NavMesh{public const int AllAreas=-1;public static bool Sample=true;public static int Calls;public static NavMeshPathStatus Status;public static Func<Vector3,Vector3,Vector3[]>? Route;public static bool SamplePosition(Vector3 v,out NavMeshHit h,float d,int a){h=new(){position=v};return Sample;}public static bool CalculatePath(Vector3 a,Vector3 z,int area,NavMeshPath p){Calls++;p.status=Status;p.corners=Route?.Invoke(a,z)??[a,z];return true;}}
}
namespace BepInEx.Configuration{public class ConfigEntry<T>(T v){public T Value=v;}}
namespace BepInEx.Bootstrap{public class Info{public object Instance=new Manimal.CSGas.Plugin();public Metadata Metadata=new();}public class Metadata{public Version Version=new(2,0,0);}public static class Chainloader{public static Dictionary<string,Info> PluginInfos=new();}}
namespace HarmonyLib{public class Harmony{public void Patch(MethodInfo? m,HarmonyMethod? prefix=null,HarmonyMethod? postfix=null){if(m==null)throw new MissingMethodException();}}public class HarmonyMethod(Type t,string n){public int priority;}public static class Priority{public const int First=800;}public static class AccessTools{const BindingFlags Flags=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;public static FieldInfo? Field(Type t,string n)=>t.GetField(n,Flags);public static MethodInfo? Method(Type t,string n,Type[]? args=null)=>args==null?t.GetMethod(n,Flags):t.GetMethod(n,Flags,null,args,null);}}
namespace EFT{
 public class Player{public string Protection="None";public Hands HandsController=new();}public class Hands{public bool Busy;public bool IsInInteractionStrictCheck()=>Busy;}
 public class Grenade:Component{}
 public class SmokeGrenade:Grenade{public SmokeSettings _smokeGrenadeSettings=new();}public class SmokeSettings{public SphereCollider _emissionArea=new();}
 public static class LayersMaskController{public const int GrenadeObstaclesColliderMask=1;}
 public enum EPhraseTrigger{OnBreath}public enum ETagStatus{Dying}
 public class BotOwner{public Settings Settings=new();public WeaponManager WeaponManager=new();public DoorOpener DoorOpener=new();}
 public class DoorOpener{public bool Interacting;}public class WeaponManager{public Thrower Grenades=new();}public class Thrower{public bool ThrowindNow;}
 public class Settings{public Files FileSettings=new();public Applied Current=new();}public class Files{public Change Change=new();}public class Change{public float FLASH_PRECICING=2,FLASH_ACCURATY=2,FLASH_GAIN_SIGHT=2,FLASH_SCATTERING=2,FLASH_VISION_DIST=.3f,FLASH_HEARING=.5f;}
 public class Applied{public HashSet<object> Active=new();public void Apply(object m)=>Active.Add(m);public void Dismiss(object m)=>Active.Remove(m);}
}
namespace SAIN.Helpers{public class TemporaryStatModifiers(float a,float b,float c,float d,float e,float f,float g){public object Modifiers=new();public float Hearing=g;}}
namespace SAIN.Models.Enums{public enum EGrenadeReaction{None,Scatter}
 public enum ECombatDecision{None,AvoidGrenade}public enum ESquadDecision{None}public enum ESprintUrgency{High}}
namespace SAIN.Preset.Shared.Enums{public enum ESelfActionType{None}}
namespace SAIN.Layers{public class BotAction(BotComponent b){public BotComponent Bot=b;}}
namespace SAIN.Layers.Combat.Solo{public class AvoidGrenadeAction{public void Stop(){}}}
namespace SAIN.SAINComponent.Classes.Mover{
 public interface IBotPathData{UnityEngine.AI.NavMeshPath NavMeshPath{get;}}
 public class PathData:IBotPathData{public UnityEngine.AI.NavMeshPath NavMeshPath{get;set;}=new();public bool Stopped;private void Stop(bool x,string why){Stopped=true;}}
 public class Mover{public IBotPathData? ActivePath;public bool Accept=true;public int Requests;public bool RunToPointByWay(UnityEngine.AI.NavMeshPath p,bool x,float d,SAIN.Models.Enums.ESprintUrgency u,bool y){Requests++;if(Accept)ActivePath=new PathData{NavMeshPath=p};return Accept;}}
}
namespace SAIN.SAINComponent.Classes.Decision{public class BotDecisionManager(BotComponent bot){public BotComponent Bot=bot;public int Decisions;private void getDecision(){}private void SetDecisions(SAIN.Models.Enums.ECombatDecision c,SAIN.Models.Enums.ESquadDecision q,SAIN.Preset.Shared.Enums.ESelfActionType s,object? e){Decisions++;}}}
namespace SAIN.Components{
 public class BotComponent:UnityEngine.Object{public string ProfileId="pmc";public bool Pmc=true,BotActive=true,IsDead,GameEnding;public Vector3 Position=new(1,0,0),LookDirection=new(1,0,0);public Bones Transform=new();public EFT.Player Player=new();public EFT.BotOwner BotOwner=new();public Grenades Grenade=new();public Flash Flashed=new();public EFT.DoorOpener DoorOpener=new();public object? GoalEnemy;public Talk Talk=new();public SAIN.SAINComponent.Classes.Mover.Mover Mover=new();public void ManualUpdate(){}}
 public class Bones{public Vector3 EyePosition=new(1,1.6f,0);}public class Grenades{public SAIN.SAINComponent.Classes.WeaponFunction.GrenadeReactionClass GrenadeReactionClass=new();}public class Reaction{public bool Danger;public bool ShallAvoidGrenade()=>Danger;}public class Flash{public bool IsFlashed;}public class Talk{public int Count;public void Say(EFT.EPhraseTrigger t,EFT.ETagStatus s,bool a,bool b)=>Count++;}
}
namespace Comet.Core{public class CometConfig{public bool Enabled=true;public int Revision;public GasExposureConfig GasExposure=new();public static void Range(double v,double lo,double hi,string n){if(!double.IsFinite(v)||v<lo||v>hi)throw new ArgumentException(n);}}}
namespace Manimal.CSGas{public class Plugin{public static BepInEx.Configuration.ConfigEntry<float> GasPointRadiusM=new(1.8f);public static BepInEx.Configuration.ConfigEntry<bool> GasRequiresLineOfSight=new(true);}}
namespace Manimal.CSGas.Gas{
 public class CsGasCloud:Component{
  internal enum GasProtection{None,Respirator,Full}
  public bool _exploded=true;public VolumetricGasEmitter? _emitter;public EFT.SmokeGrenade _smoke=new();private void Start(){}
  private static GasProtection GetProtection(EFT.Player p)=>Enum.Parse<GasProtection>(p.Protection);
 }
 public class VolumetricGasEmitter:Component{public Vector3 _origin;public bool _finished;public float _emitUntil=30,_particleLifetime=15;public Func<Vector3,bool> Test=p=>p.sqrMagnitude<25;public bool IsInside(Vector3 p,float radius)=>!(_finished&&Time.time>=_emitUntil+_particleLifetime)&&Test(p);public static VolumetricGasEmitter Create()=>new();}
}
namespace Comet.Client{
 internal class BotState(BotComponent b){public GasRuntime Gas=new();public string ProfileId=b.ProfileId,Name=b.ProfileId,Id="instance";}
 internal static class NativeAdapter{public static bool IsPmcAi(BotComponent b)=>b.Pmc;}
 public class Sink{public List<string> Lines=new();public void Write(string s)=>Lines.Add(s);public void Publish(string s,Action<Exception> a){}}
 public class Log{public List<string> Lines=new();public void LogWarning(string s)=>Lines.Add(s);public void LogInfo(string s)=>Lines.Add(s);}
 public static class Api{public static Sink Decisions=new();}public static class Diagnostics{public static Newtonsoft.Json.JsonSerializerSettings Json=new(){ContractResolver=new Newtonsoft.Json.Serialization.CamelCasePropertyNamesContractResolver()};}
 public sealed partial class Plugin{
  internal static Plugin? Instance;internal Dictionary<BotComponent,BotState> states=new();internal CometConfig? config=new();internal object? world=new();internal bool Authority=true;internal string session="g08-harness";internal long sequence;internal bool subscriberWarned;internal Sink? diagnostics=new();internal Log Logger=new();internal HashSet<string> ghosts=new();
  private static MethodInfo ownDangerSetter=HarmonyLib.AccessTools.Method(typeof(SAIN.SAINComponent.Classes.WeaponFunction.GrenadeReactionClass),"set_DangerGrenade")!,ownReactionSetter=HarmonyLib.AccessTools.Method(typeof(SAIN.SAINComponent.Classes.WeaponFunction.GrenadeReactionClass),"set_Reaction")!;
  private static MethodInfo combatStopPath=typeof(SAIN.SAINComponent.Classes.Mover.PathData).GetMethod("Stop",BindingFlags.NonPublic|BindingFlags.Instance)!;
  internal Plugin(){Instance=this;PatchGasExposure(new());}
  private bool Ghost(string id)=>ghosts.Contains(id);private BotState GrenadeState(BotComponent b){if(!states.TryGetValue(b,out var s))states.Add(b,s=new(b));return s;}private void Release(BotState s,string why){}
  internal void Filter(BotComponent b){b.Grenade.GrenadeReactionClass.Bot=b;FilterNativeGas(b.Grenade.GrenadeReactionClass);}
  internal void Tick(BotComponent b){b.Grenade.GrenadeReactionClass.Bot=b;GasBotUpdate(b); }internal bool Decide(SAIN.SAINComponent.Classes.Decision.BotDecisionManager m)=>GasDecision(m);internal bool Act(BotComponent b)=>TryGasAction(b);
  internal void StopAction(BotComponent b)=>GasActionStopped(new(b));
  internal bool Ready=>gasBridge.Ready;internal void End(){foreach(var pair in states)DropGas(pair.Key,pair.Value,"gas-raid-ended");gasBridge.Clear();}internal GasRuntime State(BotComponent b)=>states[b].Gas;
 }
}

namespace SAIN.SAINComponent.SubComponents{public class GrenadeTrackerClass{public EFT.Grenade? Grenade;public bool CanReact=true;public Vector3 DangerPoint;}}
namespace SAIN.SAINComponent.Classes.WeaponFunction{public class GrenadeReactionClass{
 public BotComponent Bot=null!;public bool Danger;public SAIN.SAINComponent.SubComponents.GrenadeTrackerClass? DangerGrenade{get;private set;}
 public SAIN.Models.Enums.EGrenadeReaction Reaction{get;private set;}
 public Dictionary<int,SAIN.SAINComponent.SubComponents.GrenadeTrackerClass> EnemyGrenadesList=new();
 public bool ShallAvoidGrenade()=>Danger||DangerGrenade!=null&&Reaction!=SAIN.Models.Enums.EGrenadeReaction.None;
 private SAIN.Models.Enums.EGrenadeReaction GetReaction()=>SAIN.Models.Enums.EGrenadeReaction.Scatter;
 public void Select(SAIN.SAINComponent.SubComponents.GrenadeTrackerClass t){DangerGrenade=t;Reaction=SAIN.Models.Enums.EGrenadeReaction.Scatter;}
}}
