using Comet.Core;
using SAIN.Components;
using SAIN.Models.Enums;
using SAIN.SAINComponent.Classes.EnemyClasses;
using SAIN.Preset.Shared.Enums;
using EFT;
using UnityEngine;
namespace UnityEngine {
 public static class Time {public static float time=10,realtimeSinceStartup=10;}
 public struct Vector3(float x,float y,float z){public float x=x,y=y,z=z;public static float Distance(Vector3 a,Vector3 b)=>(float)Math.Sqrt(Math.Pow(a.x-b.x,2)+Math.Pow(a.y-b.y,2)+Math.Pow(a.z-b.z,2));}
}
namespace Comet.Core {
 public static class MedicalChoice{public static bool Finite(double v)=>double.IsFinite(v);}
 public class CometConfig{public bool Enabled=true;public long Revision=7;public ReadyStanceConfig ReadyStance=new();public static void Range(double v,double min,double max,string key){if(!double.IsFinite(v)||v<min||v>max)throw new ArgumentException(key);}}
}
namespace EFT {
 public interface IMedsController{}
 public class Hands{}
 public class MedHands:Hands,IMedsController{}
 public class Player{public bool IsAI=true,IsSprintEnabled;public Hands HandsController=new FirearmController();public MovementContext MovementContext=new();
 public class FirearmController:Hands{public bool Ready=true,Remove,Reload,Inventory,Interact,Spawn,Processing;public bool IsInRemoveOperation()=>Remove;public bool IsInReloadOperation()=>Reload;public bool IsInventoryOpen()=>Inventory;public bool IsInInteractionStrictCheck()=>Interact;public bool IsInSpawnOperation()=>Spawn;public bool IsHandsProcessing()=>Processing;}}
 public class MovementContext{public bool _isInPatrol=true;public int Writes;public void SetPatrol(bool value){Writes++;_isInPatrol=value;}}
 public enum EBotMoverState{Other,NearDoor}
 public class BotOwner{public Memory Memory=new();public Shoot ShootData=new();public Door DoorOpener=new();public NativeMover Mover=new();public Medicine Medecine=new();public Weapon WeaponManager=new();}
 public class Memory{public bool IsUnderFire;}public class Shoot{public bool Shooting;}public class Door{public bool Interacting;}public class NativeMover{public EBotMoverState CurrentState;}public class Medicine{public bool Using;}public class Weapon{public Reload Reload=new();}public class Reload{public bool Reloading;}
}
namespace SAIN.Components{
 public class BotComponent{public string ProfileId=Guid.NewGuid().ToString();public ESAINLayer ActiveLayer;public bool BotActive=true,IsDead,GameEnding,Pmc=true,SAINLayersActive;public Player Player=new();public BotOwner BotOwner=new();public Enemy? GoalEnemy;public Mover Mover=new();public Vector3 Position;}
 public class Mover{public bool Running;}
}
namespace SAIN.Preset.Shared.Enums{public enum SAINSoundType{FootStep,Sprint,Shot,Other}}
namespace SAIN.SAINComponent.Classes.EnemyClasses{
 public class Enemy(BotComponent bot){public BotComponent Bot=bot;public bool IsVisible=true,EnemyKnown=true;public string EnemyProfileId="enemy";public Places KnownPlaces=new();}
 public class Places{public EnemyPlace? LastSeenPlace=new(),LastHeardPlace;}
 public class EnemyPlace{public Vector3 Position=new(100,0,0);private float timestamp=Time.time;public float TimeSincePositionUpdated{get=>Time.time-timestamp;set=>timestamp=Time.time-value;}}
 public class EnemyEvents{public void EnemyHeard(SAINSoundType type,bool report,EnemyPlace place){}}
}
namespace SAIN.SAINComponent.Classes.Decision{public class BotDecisionManager(BotComponent bot){public BotComponent Bot=bot;public void ManualUpdate(){}}}
namespace SAIN.SAINComponent.Classes.Mover{public class SAINMoverClass(BotComponent bot){public BotComponent Bot=bot;private bool WantsPatrolStance()=>true;public void ManualUpdate(){}}}
namespace Comet.Client{
 internal class PostureState{public PosturePolicy Policy=new();}internal class PosturePolicy{public bool Blocking;}
 internal class BotState{public ReadyStanceRuntime ReadyStance=new();public PostureState MedicalPosture=new();public string ProfileId,Name="Test PMC",Id="test-instance";public BotState(BotComponent b,CometConfig c,string session){ProfileId=b.ProfileId;}}
 internal static class NativeAdapter{public static bool IsSupportedAi(BotComponent b)=>b.Pmc&&b.Player.IsAI;}
 internal static class Diagnostics{ internal static string Serialize(object value)=>Newtonsoft.Json.JsonConvert.SerializeObject(value,Json);public static Newtonsoft.Json.JsonSerializerSettings Json=new(){ContractResolver=new Newtonsoft.Json.Serialization.CamelCasePropertyNamesContractResolver()};}
 internal class Sink{public List<string> Lines=new();public void Write(string line)=>Lines.Add(line);}
 internal static class Logger{public static List<string> Warnings=new();public static void LogWarning(string s)=>Warnings.Add(s);}
 internal static class Api{public static class Decisions{public static void Publish(string json,Action<Exception> failure){}}}
 public sealed partial class Plugin{
  internal static Plugin? Instance;internal object? world=new();internal CometConfig? config=new();internal bool Authority=true,IsGhost,subscriberWarned;
  internal string session="h05-harness";internal long sequence;internal Dictionary<BotComponent,BotState> states=new();internal Sink? diagnostics=new();
  public Plugin(){Instance=this;readyPatrol=typeof(MovementContext).GetField("_isInPatrol")!;}
  private bool Ghost(string id)=>IsGhost;private static bool ValidPoint(Vector3 p)=>float.IsFinite(p.x)&&float.IsFinite(p.y)&&float.IsFinite(p.z);
  private static bool WeaponHandsReady(BotComponent b,Player.FirearmController? f)=>f?.Ready==true;
  internal void Fight(BotComponent b)=>ReadyFight(new(b));internal void Heard(Enemy e,SAINSoundType t,EnemyPlace p){}
  internal bool Wish(BotComponent b,bool native){ReadyNativeWish(new(b),ref native);return native;}
  internal void After(BotComponent b)=>ReadyAfterMover(new(b));internal void Watch(BotComponent b)=>SampleReadyStance(b,states[b]);
  internal void End(BotComponent b)=>EndReadyStance(states[b],"ready-stance-raid-ended");
 }
}

namespace SAIN.Models.Enums { public enum ESAINLayer { None, Combat, AvoidThreat, Flashed } }
