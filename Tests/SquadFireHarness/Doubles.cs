using Comet.Core;
using SAIN.Components;
using UnityEngine;
namespace UnityEngine {
 public struct Vector3(float x,float y,float z){public float x=x,y=y,z=z;public static Vector3 up=>new(0,1,0);public static Vector3 operator +(Vector3 a,Vector3 b)=>new(a.x+b.x,a.y+b.y,a.z+b.z);public static Vector3 operator -(Vector3 a,Vector3 b)=>new(a.x-b.x,a.y-b.y,a.z-b.z);public static Vector3 operator *(Vector3 a,float b)=>new(a.x*b,a.y*b,a.z*b);public float sqrMagnitude=>x*x+y*y+z*z;public float magnitude=>(float)Math.Sqrt(sqrMagnitude);public Vector3 normalized=>magnitude>0?this*(1/magnitude):this;public void Normalize(){float m=magnitude;if(m>0){x/=m;y/=m;z/=m;}}public static Vector3 ClampMagnitude(Vector3 a,float max)=>a.magnitude>max?a*(max/a.magnitude):a;public static float Distance(Vector3 a,Vector3 b)=>(a-b).magnitude;public static float Dot(Vector3 a,Vector3 b)=>a.x*b.x+a.y*b.y+a.z*b.z;}
 public static class Time{public static float time=10,realtimeSinceStartup=10;public static int frameCount;}
 public enum QueryTriggerInteraction{Ignore} public class Collider{public object? Surface;public T? GetComponentInParent<T>()where T:class=>Surface as T;public EFT.Player Player=new();}public struct RaycastHit{public Collider collider;public float distance;}
 public static class LayerMask{public static int NameToLayer(string name)=>2;}
 public static class Physics{public static bool Raycast(Vector3 a,Vector3 d,out RaycastHit hit,float distance,int mask,QueryTriggerInteraction q){Calls++;if(Fail)throw new Exception("injected physics failure");hit=Hits.FirstOrDefault();return Hits.Count>0&&hit.distance<distance;}public static List<Collider> Near=new();public static List<RaycastHit> Hits=new();public static bool Fail;public static int Calls;
  public static int OverlapSphereNonAlloc(Vector3 a,float radius,Collider[] target,int mask,QueryTriggerInteraction q){Calls++;if(Fail)throw new Exception("injected physics failure");int n=Math.Min(Near.Count,target.Length);for(int i=0;i<n;i++)target[i]=Near[i];return n;}
  public static int SphereCastNonAlloc(Vector3 a,float radius,Vector3 dir,RaycastHit[] target,float distance,int mask,QueryTriggerInteraction q){Calls++;int n=Math.Min(Hits.Count,target.Length);for(int i=0;i<n;i++)target[i]=Hits[i];return n;}}
}
public static class LayersMaskController{public const int PlayerMask=1;}
public class ShootData{public EFT.BotOwner? _owner;public bool Shooting,IgnoreStop;public int Stops,Shots;public bool Shoot(){Shots++;Shooting=true;return true;}public void EndShoot(){Stops++;if(!IgnoreStop)Shooting=false;}}
namespace EFT {
 public class Player{public string ProfileId="";public Vector3 Velocity;public Profile Profile=new();public Health HealthController=new();public object? HandsController=new FirearmController();public class FirearmController{public object Item=new();}}
 public class Health{public bool IsAlive=true;}public class Profile{public string Nickname="Squad <mate>";}
 public class BotOwner{public BotComponent? Bot;public ShootData ShootData;public Aiming AimingManager=new();public BotOwner(){ShootData=new(){_owner=this};}public T? GetComponent<T>() where T:class=>Bot as T;}
 public class Aiming{public Aim CurrentAiming=new();}public class Aim{public float LastDist2Target=20;}
 public class GlobalConfiguration{public Ballistic Ballistic=new();}public class Ballistic{public double GlobalDamageDegradationCoefficient=1;}
}
namespace EFT.Ballistics{public static class BallisticsCalculatorConstants{public const int StaticObjectsHitMask=1;}}
namespace Comfort.Common{public static class Singleton<T> where T:new(){public static T Instance=new();}}
namespace SAIN.Preset.Shared.Enums{public enum ECombatDecision{None,Search,StandAndShoot}}
namespace SAIN.Models.Enums{public enum EShootReason{None,Suppression}}
namespace SAIN.SAINComponent.Classes{public class SAINFriendlyFireClass{public BotComponent Bot=null!;public void ManualUpdate(){}public float LastDistance;
 public bool UpdateFriendlyFireStatus(float distance,Vector3 port,Vector3 direction,BotComponent bot){LastDistance=distance;var result=Comet.Client.Plugin.Check(bot,port,distance,direction);if(result==SAIN.Preset.Shared.Enums.FriendlyFireStatus.FriendlyBlock)bot.BotOwner.ShootData.EndShoot();return result!=SAIN.Preset.Shared.Enums.FriendlyFireStatus.FriendlyBlock;}}}
namespace SAIN.SAINComponent.Classes.WeaponFunction{public class ManualShootClass{public BotComponent Bot=null!;public bool TryShoot(object enemy,Vector3 targetPos,bool checkFF=true,int reason=0)=>true;}}
namespace SAIN.Components {
 public class BotComponent{public string ProfileId="pmc-1";public bool IsDead,GameEnding,IsSupportedBot=true,BotActive=true,IsGhost;public EFT.Player Player=new();public EFT.BotOwner BotOwner;public Vector3 Position;public TransformData Transform=new();public Shoot ManualShoot=new();public Decision Decision=new();public Squad Squad=new();public BotComponent(){BotOwner=new(){Bot=this};}}
 public class TransformData{public WeaponData WeaponData=new();public HeadData HeadData=new();}public class HeadData{public Vector3 HeadPosition=new(0,1.6f,5);}public class WeaponData{public Vector3 FirePort=new(0,1,0),PointDirection=new(0,0,1);}
 public class Shoot{public SAIN.Models.Enums.EShootReason Reason;public Vector3 ShootPosition;}
 public class Decision{public SAIN.Preset.Shared.Enums.ECombatDecision CurrentCombatDecision=SAIN.Preset.Shared.Enums.ECombatDecision.StandAndShoot;}
 public class Squad{public Dictionary<string,BotComponent> Members=new();}
 public class GameWorldComponent{public static GameWorldComponent? Instance=new();public World GameWorld=new();}public class World{public EFT.Player? GetPlayerByCollider(Collider c)=>c.Player;}
}
namespace Comet.Client {
 internal class BotState{public string ProfileId,Name,Id="s08-instance";public SquadFireRuntime SquadFire=new();public BotState(BotComponent b,CometConfig c,string session){ProfileId=b.ProfileId;Name=b.Player.Profile.Nickname;}}
 internal static class NativeAdapter{public static bool IsSupportedAi(BotComponent b)=>b.IsSupportedBot;}
 internal class Budget{public bool Allow=true;public int Calls;public bool TryBegin(int frame,object? owner=null){Calls++;return Allow;}public void Charge(double n){}}
 internal class Log{public List<string> Warnings=new();public void LogWarning(string s)=>Warnings.Add(s);}
 internal class Sink{public List<string> Events=new();public void Write(string s)=>Events.Add(s);}
 internal static class Diagnostics{ internal static string Serialize(object value)=>Newtonsoft.Json.JsonConvert.SerializeObject(value,Json);public static Newtonsoft.Json.JsonSerializerSettings Json=new(){ContractResolver=new Newtonsoft.Json.Serialization.CamelCasePropertyNamesContractResolver()};}
 internal static class Api{public static class Decisions{public static void Publish(string s,Action<Exception> e){}}}
 internal class ShotGeometryRuntime{public object[] Held=new object[32];}
 internal class Ammo{public double PenetrationPower=50,PenetrationPowerDiviation=0,PenetrationChanceObstacle=1,RicochetChance=0;}
 public sealed partial class Plugin {
  internal static Plugin? Instance;internal CometConfig? config=new();internal bool Authority=true,subscriberWarned;internal object? world=new();internal Dictionary<BotComponent,BotState> states=new();internal Budget doorBudget=new();internal System.Diagnostics.Stopwatch doorClock=new();internal Log Logger=new();internal Sink? diagnostics=new();internal string session="s08-harness";internal int sequence;internal List<BotComponent> Mates=new();
  internal List<ShotSurface> Surfaces=new();internal bool GeometryComplete=true,AmmoKnown=true;internal float GeometryDistance;internal Ammo Ammunition=new();
  internal Plugin(){Instance=this;}private bool Ghost(string id)=>states.Keys.Any(b=>b.ProfileId==id&&b.IsGhost)||Mates.Any(b=>b.ProfileId==id&&b.IsGhost);private static bool ValidPoint(Vector3 v)=>float.IsFinite(v.x)&&float.IsFinite(v.y)&&float.IsFinite(v.z);private static DoorPoint DP(Vector3 v)=>new(v.x,v.y,v.z);
  private Ammo? GeometryAmmo(object? weapon)=>AmmoKnown&&weapon!=null?Ammunition:null;
  private bool GeometryLane(ShotGeometryRuntime r,Vector3 start,Vector3 dir,float distance,int mask,string lane,List<ShotSurface> surfaces,ref bool stable){GeometryDistance=distance;r.Held[0]=new();surfaces.AddRange(Surfaces.Where(s=>s.Distance<distance));return GeometryComplete;}
  private void GeometryClearBuffers(ShotGeometryRuntime r)=>Array.Clear(r.Held);
  internal static SAIN.Preset.Shared.Enums.FriendlyFireStatus Check(BotComponent b,Vector3 p,float d,Vector3 v){var result=SAIN.Preset.Shared.Enums.FriendlyFireStatus.Clear;SquadFireCheck(p,d,v,b,ref result);return result;}
  internal void Update(BotComponent b)=>SquadFireUpdate(new(){Bot=b});internal void Sample(BotComponent b)=>SampleSquadFire(b,states[b]);internal void End(BotComponent b)=>EndSquadFire(states[b],"raid-ended");
 }
}

namespace EFT.Interactive {public class Door {}}
namespace EFT.Ballistics {public enum MaterialType {Glass,GlassShattered,Concrete}public class BallisticCollider {public MaterialType TypeOfMaterial;}}

namespace UnityEngine { public static class Mathf {public static float Max(float a,float b)=>Math.Max(a,b);} }
namespace SAIN.Preset.Shared.Enums { public enum FriendlyFireStatus { None,Clear,FriendlyBlock } }
