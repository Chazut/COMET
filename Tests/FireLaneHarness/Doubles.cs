using Comet.Core;
using SAIN.Components;
using SAIN.Components.PlayerComponentSpace;
using SAIN.SAINComponent.Classes.Mover;
using UnityEngine;
namespace UnityEngine {
 public struct Vector3(float x,float y,float z){public static Vector3 operator -(Vector3 a)=>a*-1;public static Vector3 zero=>new();public static Vector3 up=>new(0,1,0);public static Vector3 Cross(Vector3 a,Vector3 b)=>new(a.y*b.z-a.z*b.y,a.z*b.x-a.x*b.z,a.x*b.y-a.y*b.x);public static bool operator ==(Vector3 a,Vector3 b)=>a.x==b.x&&a.y==b.y&&a.z==b.z;public static bool operator !=(Vector3 a,Vector3 b)=>!(a==b);public float x=x,y=y,z=z;public static Vector3 operator +(Vector3 a,Vector3 b)=>new(a.x+b.x,a.y+b.y,a.z+b.z);public static Vector3 operator -(Vector3 a,Vector3 b)=>new(a.x-b.x,a.y-b.y,a.z-b.z);public static Vector3 operator *(Vector3 a,float b)=>new(a.x*b,a.y*b,a.z*b);public float sqrMagnitude=>x*x+y*y+z*z;public float magnitude=>(float)Math.Sqrt(sqrMagnitude);public Vector3 normalized=>magnitude<.0001?new():this*(1/magnitude);public static float Distance(Vector3 a,Vector3 b)=>(a-b).magnitude;public static float Dot(Vector3 a,Vector3 b)=>a.x*b.x+a.y*b.y+a.z*b.z;public static float Angle(Vector3 a,Vector3 b)=>(float)(Math.Acos(Math.Clamp(Dot(a,b)/(a.magnitude*b.magnitude),-1,1))*180/Math.PI);}
 public struct Vector2{public static Vector2 zero=>new();}
 public static class Time{public static float time=10,realtimeSinceStartup=10;public static int frameCount;}
 public enum QueryTriggerInteraction{Ignore} public struct RaycastHit{public Vector3 point;}
 public static class Physics{public static bool Raycast(Vector3 a,Vector3 d,float max,int mask)=>Raycast(a,d,out _,max,mask);public static float WallAt=-1;public static int Rays;public static bool Raycast(Vector3 a,Vector3 dir,out RaycastHit h,float max,int mask=-1,QueryTriggerInteraction q=QueryTriggerInteraction.Ignore){Rays++;h=new(){point=a+dir*WallAt};return WallAt>=0&&WallAt<max;}}
}
namespace UnityEngine.AI {
 public enum NavMeshPathStatus{PathComplete,PathPartial}
 public struct NavMeshHit{public Vector3 position;}
 public static class NavMesh{public const int AllAreas=-1;public static bool Valid=true,Blocked;public static bool SamplePosition(Vector3 p,out NavMeshHit h,float r,int a){h=new(){position=p};return Valid;}public static bool Raycast(Vector3 a,Vector3 b,out NavMeshHit h,int mask){h=new();return Blocked;}}
}
public static class LayersMaskController{public const int HighPolyWithTerrainMask=1;}
namespace EFT {
 public class Health{public bool IsAlive=true;} public class Player{public Health HealthController=new();public Vector3 Velocity;public Profile Profile=new();public Movement MovementContext=new();public bool IsInPronePose,IsSprintEnabled;public int ZeroInputs;public void Move(Vector2 d){ZeroInputs++;}public void EnableSprint(bool b){IsSprintEnabled=b;}}
 public class Profile{public string Nickname="PMC";}
 public class Movement{public bool IsGrounded=true;}
 public class BotOwner{public Memory Memory=new();public Medicine Medecine=new();public Weapon WeaponManager=new();public Doors DoorOpener=new();public Shoot ShootData=new();}
 public class Memory{public bool IsUnderFire;} public class Medicine{public bool Using;}public class Weapon{public Reload Reload=new();}public class Reload{public bool Reloading;}public class Doors{public bool Interacting;}public class Shoot{public SAIN.Models.Enums.EShootReason Reason;public bool Shooting;}
}
namespace SAIN.Preset.Shared.Enums{public enum ECombatDecision{None,Search,StandAndShoot,AvoidGrenade,SeekCover,Retreat,RunAway}public enum ESelfActionType{None,Heal}}
namespace SAIN.Models.Enums{public enum EBotMoveStatus{Moving,DoorInteraction}}
namespace SAIN.SAINComponent.Classes.Mover{public class Path{public bool Moving=true,Crawling;public SAIN.Models.Enums.EBotMoveStatus Status;public UnityEngine.AI.NavMeshPathStatus PathStatus;public Vector3 Destination=new(10,0,5);}public class Mover{public Path? ActivePath=new();}}
namespace SAIN.Components {
 public class BotComponent{public EFT.Health PlayerHealthController=>Player.HealthController;public string ProfileId="pmc-1";public bool IsDead,GameEnding,IsSupportedBot=true,BotActive=true,IsGhost;public EFT.Player Player=new();public EFT.BotOwner BotOwner=new();public Vector3 Position;public TransformData Transform=new();public Shoot ManualShoot=new();public Enemy? GoalEnemy;public Suppression Suppression=new();public Grenades Grenade=new();public Decision Decision=new();public Mover Mover=new();public Squad Squad=new();}
 public class TransformData{public WeaponData WeaponData=new();}public class WeaponData{public Vector3 FirePort=new(0,1,0),PointDirection=new(0,0,1);}
 public class Shoot{public SAIN.Models.Enums.EShootReason Reason;public bool Shooting;public Vector3 ShootPosition;}
 public class Enemy{public Vector3 EnemyPosition=new(0,0,20);public bool IsVisible;public Known KnownPlaces=new();}public class Known{public Place? LastSeenPlace;}public class Place{public Vector3 Position;}
 public class Suppression{public bool IsSuppressed;}public class Grenades{public object? GrenadeDangerPoint;}public class Decision{public SAIN.Preset.Shared.Enums.ECombatDecision CurrentCombatDecision=SAIN.Preset.Shared.Enums.ECombatDecision.Search;public SAIN.Preset.Shared.Enums.ESelfActionType CurrentSelfDecision;}
 public class Squad{public Dictionary<string,BotComponent> Members=new();}
}
namespace SAIN.Components.PlayerComponentSpace{public class PlayerComponent{public BotComponent? BotComponent;public void OnMakingShot(){}}}
namespace SAIN.Classes{public class PlayerMovementController{public void SetTargetMoveDirection(Vector3 direction,Vector3 finalMoveDestination,PlayerComponent playerComp,float START_SLOW_DIST=.75f,float STOP_SPRINT_DIST=1){}}}
namespace Comet.Client {
 internal class Toggle{public bool Active;}internal class OtherMove{public Toggle Policy=new();}internal class Reservation{public object? Lease;}
 internal class BotState{public string ProfileId="pmc-1",Name="Mover",Id="s07-instance";public FireLaneRuntime FireLane=new();public OtherMove CombatStep=new(),CornerPeek=new(),CornerChase=new(),CornerPrefire=new(),RetreatWeave=new(),Entry=new(),RoomClear=new(),Ambush=new();public Reservation Reservation=new();}
 internal static class NativeAdapter{public static bool IsSupportedAi(BotComponent b)=>b.IsSupportedBot;}
 internal class Budget{public bool Allow=true;public int Calls;public bool TryBegin(int frame,object? owner=null){Calls++;return Allow;}public void Charge(double n){}}
 internal class Log{public List<string> Warnings=new();public void LogWarning(string s)=>Warnings.Add(s);}
 internal class Sink{public List<string> Events=new();public void Write(string s)=>Events.Add(s);}
 internal static class Diagnostics{ internal static string Serialize(object value)=>Newtonsoft.Json.JsonConvert.SerializeObject(value,Json);public static Newtonsoft.Json.JsonSerializerSettings Json=new(){ContractResolver=new Newtonsoft.Json.Serialization.CamelCasePropertyNamesContractResolver()};}
 internal static class Api{public static class Decisions{public static int Count;public static void Publish(string s,Action<Exception> e){Count++;}}}
 public sealed partial class Plugin {
  internal static UnityEngine.Vector3 RemixMovementOrigin(BotComponent b,UnityEngine.Vector3 original,bool preserveOnMesh) => original;
  internal static Plugin? Instance;internal CometConfig? config=new();internal bool Authority=true,subscriberWarned;internal object? world=new();internal Dictionary<BotComponent,BotState> states=new();internal Budget doorBudget=new();internal System.Diagnostics.Stopwatch doorClock=new();internal Log Logger=new();internal Sink? diagnostics=new();internal string session="s07-harness";internal int sequence;internal bool BodyBlocked,MedicalGuard;
  internal Plugin(){Instance=this;}private bool Ghost(string id)=>states.Keys.Any(b=>b.ProfileId==id&&b.IsGhost)||Mates.Any(b=>b.ProfileId==id&&b.IsGhost);internal List<BotComponent> Mates=new();private bool MedicalPostureGuard(BotComponent b)=>MedicalGuard;private static bool ValidPoint(Vector3 v)=>float.IsFinite(v.x)&&float.IsFinite(v.y)&&float.IsFinite(v.z);private static DoorPoint DP(Vector3 v)=>new(v.x,v.y,v.z);private static Vector3 VP(DoorPoint v)=>new((float)v.X,(float)v.Y,(float)v.Z);private string RunVolume(BotComponent b,Vector3 a,Vector3 g)=>BodyBlocked?"blocked":"";
  internal void Shot(BotComponent b){b.ManualShoot.Reason=SAIN.Models.Enums.EShootReason.Enemy;b.ManualShoot.Shooting=true;b.ManualShoot.ShootPosition=new(0,1.2f,20);}internal bool Move(BotComponent b,ref Vector3 d,ref Vector3 goal,ref float stop,bool owner=true)=>LaneMovement(ref d,ref goal,new(){BotComponent=b},ref stop,owner);internal void Sample(BotComponent b)=>SampleLane(b,states[b]);internal void End(BotComponent b)=>EndLane(states[b],"raid-ended");internal void Clear(){laneShots.Clear();foreach(var b in states.Keys)End(b);}internal int Shots=>laneShots.Count;
 }
}

namespace SAIN.Models.Enums {public enum EShootReason {None,Enemy}}
