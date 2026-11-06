using System.Reflection;
using System.Reflection.Emit;
using SAIN.Components;
using SAIN.SAINComponent.Classes.EnemyClasses;
using UnityEngine;
namespace UnityEngine {
 public struct Vector3(float a,float b,float c) { public float x=a,y=b,z=c;public static Vector3 up=>new(0,1,0);public float sqrMagnitude=>x*x+y*y+z*z;public Vector3 normalized=>sqrMagnitude==0?this:this*(1/MathF.Sqrt(sqrMagnitude));public static Vector3 operator +(Vector3 a,Vector3 b)=>new(a.x+b.x,a.y+b.y,a.z+b.z);public static Vector3 operator -(Vector3 a,Vector3 b)=>new(a.x-b.x,a.y-b.y,a.z-b.z);public static Vector3 operator *(Vector3 a,float k)=>new(a.x*k,a.y*k,a.z*k);public static float Distance(Vector3 a,Vector3 b)=>MathF.Sqrt((a-b).sqrMagnitude);public static float Dot(Vector3 a,Vector3 b)=>a.x*b.x+a.y*b.y+a.z*b.z; }
 public static class Time { public static float time=10;public static float realtimeSinceStartup=>time;public static int frameCount; }
 public static class Physics { public static bool Blocked;public static int Rays;public static bool Linecast(Vector3 a,Vector3 b,int m){Rays++;return Blocked;} }
 public static class Mathf {public static float Clamp01(float x)=>Math.Clamp(x,0,1);}
}
public static class LayersMaskController { public static int HighPolyWithTerrainMask=1; }
namespace EFT {
 public interface IPlayer { string ProfileId{get;} }
 public class Player(string id):IPlayer {public string ProfileId{get;set;}=id;public bool IsSprintEnabled;public Vector3 LookDirection=new(1,0,0);}
 public class Grenade {public int Id;public string ProfileId="source";public object WeaponSource=new EFT.InventoryLogic.ThrowWeap();}
 public class GlobalEventDispatcher {}
}
namespace EFT.InventoryLogic { public class ThrowWeap {public float EmitTime;public bool IsDummy;public string ThrowType="grenade";public int FragmentsCount=10;public float MaxExplosionDistance=10;} }
public class BotHearingSensor(SAIN.Components.Owner owner) {
 public SAIN.Components.Owner _botOwner=owner;public float Sense=1;public int NativeSounds;
 public bool IsSoundHeard(Vector3 p,float power,out float d){d=Vector3.Distance(_botOwner.Position,p);return d<power*Sense;}
 public void OnSoundPlayed(EFT.IPlayer player,Vector3 position,float power=200,int type=1){if(Comet.Client.Plugin.Vanilla(this,player,position))NativeSounds++;}
 public void OnSoundPlayedInner(EFT.IPlayer player,Vector3 position,float power=200,int type=1){if(Comet.Client.Plugin.Vanilla(this,player,position))NativeSounds++;}
}
namespace HarmonyLib {
 public class Harmony {public void Patch(MethodInfo? m,HarmonyMethod? prefix=null,HarmonyMethod? postfix=null,HarmonyMethod? transpiler=null,HarmonyMethod? finalizer=null){if(m==null)throw new Exception("missing stub hook");}}
 public class HarmonyMethod(Type t,string s){public int priority;}
 public static class Priority {public const int First=800;}
 public static class AccessTools {public static MethodInfo? Method(Type t,string name,Type[]? types=null)=>types==null?t.GetMethods(BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static).FirstOrDefault(m=>m.Name==name):t.GetMethod(name,BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static,null,types,null);}
 public class CodeInstruction(OpCode op,object? arg=null){public OpCode opcode=op;public object? operand=arg;public List<int> labels=new();public List<int> blocks=new();public bool Calls(MethodInfo? m)=>Equals(operand,m);}
}
namespace SAIN.Preset.Shared.Enums {public enum ECombatDecision{None,AvoidGrenade}public enum ESelfActionType{None,Heal}public enum ESquadDecision{None,Help}}
namespace SAIN {
 public static class SAINPlugin {public static PresetData LoadedPreset=new();}
 public class PresetData {public Globals GlobalSettings=new();}public class Globals {public HearingSettings Hearing=new();}public class HearingSettings {public float GUNSHOT_OCCLUSION_MOD=.5f;}
}
namespace SAIN.Components {
 public class BotComponent {
  public string ProfileId;public bool Pmc=true,BotActive=true,IsDead,GameEnding,SAINLayersActive;public bool Ghost;
  public EFT.Player Player;public Owner BotOwner;public SAINEnemyController EnemyController;public SAIN.Classes.Bot.Sense.Hearing.HearingSensor Hearing;public SAIN.SAINComponent.Classes.Mover.SAINSteeringClass Steering;
  public BotComponent(string id="pmc"){ProfileId=id;Player=new(id);BotOwner=new(Player);EnemyController=new(this);Hearing=new(this);Steering=new(this);}
  public Vector3 Position{get=>BotOwner.Position;set{BotOwner.Position=value;Transform.HeadData.HeadPosition=value+Vector3.up;}}
  public TransformData Transform=new();public InfoData Info=new();public FlashData Flashed=new();public SquadData Squad=new();public GrenadeData Grenade=new();public DecisionData Decision=new();public SuppressionData Suppression=new();public ShootData ManualShoot=new();public MoverData Mover=new();public Enemy? GoalEnemy;
 }
 public class TransformData{public HeadData HeadData=new();}public class HeadData{public Vector3 HeadPosition=new(0,1,0);}
 public class InfoData{public Difficulty Difficulty=new();}public class Difficulty{public float HearingDistanceModifier=1;}
 public class FlashData{public bool IsFlashed;}public class SquadData{public Dictionary<string,BotComponent> Members=new();}
 public class GrenadeData{public object? GrenadeDangerPoint;}public class DecisionData{public SAIN.Preset.Shared.Enums.ECombatDecision CurrentCombatDecision;public SAIN.Preset.Shared.Enums.ESelfActionType CurrentSelfDecision;public SAIN.Preset.Shared.Enums.ESquadDecision CurrentSquadDecision;}
 public class SuppressionData{public bool IsSuppressed;}public class ShootData{public bool Shooting;}public class MoverData{public bool Running;}
 public class Owner {public EFT.Player GetPlayer;public Vector3 Position;public BotHearingSensor HearingSensor;public Owner(EFT.Player p){GetPlayer=p;HearingSensor=new(this);}public Group BotsGroup=new();public Door DoorOpener=new();public Weapons WeaponManager=new();public Memory Memory=new();public ShootData ShootData=new();public Steerer Steering=new();}
 public class Group {public bool Ally;public bool Hostile=true;public bool IsAlly(EFT.Player p)=>Ally;public bool IsPlayerEnemy(EFT.Player p)=>Hostile;}
 public class Door{public bool Interacting;}public class Weapons{public Reload Reload=new();}public class Reload{public bool Reloading;}public class Memory{public bool IsUnderFire;}
 public class Steerer {public int Requests;public Vector3 Last;public void LookToPoint(Vector3 p){Requests++;Last=p;}}
 public class BotManagerComponent {public static BotManagerComponent Instance=new();public Spawner BotSpawnController=new();}
 public class Spawner {public Dictionary<string,BotComponent> BotDictionary=new();}
 public class GrenadeController {
  public Dictionary<string,BotComponent> Bots=>BotManagerComponent.Instance.BotSpawnController.BotDictionary;
  public void GrenadeExplosion(){}public void GrenadeThrown(){}public void RegisterGrenadeExplosionForSAINBots(){}
 }
}
namespace SAIN.Components.PlayerComponentSpace {public struct SoundEvent(EFT.IPlayer p,Vector3 v){public Vector3 Position=v;public EFT.IPlayer GetPlayer()=>p;}}
namespace SAIN.Classes.Bot.Sense.Hearing {
 public class HearingSensor(BotComponent b){public HearingInput HearingInput=new(b);}
 public class HearingInput(BotComponent bot){public BotComponent Bot=bot;public bool Ignore,IsBotDeafened;public int Cached;public bool IsIgnoringSounds(bool g)=>Ignore;public void CheckAddSoundToCache(SAIN.Components.PlayerComponentSpace.SoundEvent Sound){if(Comet.Client.Plugin.Cache(this,Sound))Cached++;}}
}
namespace SAIN.SAINComponent.Classes.EnemyClasses {
 public class SAINEnemyController(BotComponent b){public BotComponent Bot=b;public Dictionary<string,Enemy> Enemies=new();public List<Enemy> VisibleEnemies=new();public Enemy? GetEnemy(string id,bool active)=>Enemies.TryGetValue(id,out var e)&&(!active||e.Active)?e:null;}
 public class Enemy(BotComponent b,EFT.Player p){public BotComponent Bot=b;public bool IsVisible,Active=true;public EFT.Player EnemyPlayer=p;public Places KnownPlaces=new();public int Shares;public void EnemyPositionReported(){if(Comet.Client.Plugin.Shared(this))Shares++;}}
 public class Places {public Place? LastSeenPlace;}public class Place{public float TimeSincePositionUpdated;public Vector3 Position;}
}
namespace SAIN.SAINComponent.Classes.Mover {public class SAINSteeringClass(BotComponent b){public BotComponent Bot=b;public int Requests;public Vector3 Last;public void TickPlayerSteering(){Comet.Client.Plugin.Steer(this);}public void LookToPoint(Vector3 p){Requests++;Last=p;}}}
namespace Comet.Core {public class CometConfig{public bool Enabled=true;public long Revision=1;public ExplosionAwarenessConfig ExplosionAwareness=new();public static void Range(double x,double a,double b,string n){if(!Comet.Core.ExplosionAwareness.Finite(x)||x<a||x>b)throw new ArgumentException(n);}}}
namespace Comet.Client {
 public static class NativeAdapter{public static bool IsPmcAi(BotComponent b)=>b.Pmc;}public static class VisibleAdapter{public static bool Ready=true;public static bool WeaponReady(BotComponent b)=>Ready;}
 internal class Fear {internal Fear Current=>this;internal bool Panic;}internal class Sight{internal object? Current;}
 internal class BotState(BotComponent b,Comet.Core.CometConfig c,string session){internal BlastRuntime Blast=new();internal string ProfileId=b.ProfileId,Name="Test PMC",Id="instance";internal Fear Fear=new();internal Sight Sight=new();internal string ActiveLabel="";}
 public class Sink{public List<string> Lines=new();public void Write(string s)=>Lines.Add(s);public void Publish(string s,Action<Exception> ex){}}
 public static class Api{public static Sink Decisions=new();}public class Log{public void LogWarning(string s){}}
 public static class Diagnostics{ internal static string Serialize(object value)=>Newtonsoft.Json.JsonConvert.SerializeObject(value,Json);public static Newtonsoft.Json.JsonSerializerSettings Json=new(){ContractResolver=new Newtonsoft.Json.Serialization.CamelCasePropertyNamesContractResolver()};}
 public sealed partial class Plugin {
  internal static Plugin? Instance;internal object? world=new();internal Comet.Core.CometConfig? config=new();internal bool Authority=true;internal Dictionary<BotComponent,BotState> states=new();internal string session="g10-harness";internal long sequence;internal bool subscriberWarned;internal Sink? diagnostics=new();internal Log Logger=new();internal HashSet<string> ghosts=new();internal string Owner="native";internal int StoppedGunfire;
  internal Plugin(){Instance=this;PatchExplosionAwareness(new());}
  private bool Ghost(string id)=>ghosts.Contains(id);private static bool ValidPoint(Vector3 p)=>Comet.Core.ExplosionAwareness.Finite(p.x)&&Comet.Core.ExplosionAwareness.Finite(p.y)&&Comet.Core.ExplosionAwareness.Finite(p.z);private static string GunfireOwner(BotComponent b)=>Instance!.Owner;private void StopGunfire(BotState s,string r){StoppedGunfire++;}
  internal void Throw(GrenadeController c,EFT.Grenade g)=>BlastThrown(c,g);
  internal void Explosion(GrenadeController c,Vector3 at,string owner,int id,bool smoke=false,Action? native=null){BlastBegin(c,at,owner,smoke,id,out var prior);try{native?.Invoke();}finally{BlastFinally(prior,null);}}
  internal void Update()=>UpdateBlasts();internal void Late()=>LateBlasts();internal void End()=>ClearBlasts();internal void Apply(BotComponent b){if(states.TryGetValue(b,out var s))ApplyBlastLook(b,s,true);}
  internal static bool Vanilla(BotHearingSensor b,EFT.IPlayer p,Vector3 v)=>BlastVanillaSound(b,p,v);internal static bool Cache(SAIN.Classes.Bot.Sense.Hearing.HearingInput i,SAIN.Components.PlayerComponentSpace.SoundEvent s)=>BlastSoundCache(i,s);internal static bool Shared(Enemy e)=>BlastSharedContact(e);internal static void Steer(SAIN.SAINComponent.Classes.Mover.SAINSteeringClass s)=>BlastSteer(s);
  internal static Enemy? Native(SAINEnemyController c,string id)=>BlastNativeEnemy(c,id,true);
  internal static IEnumerable<HarmonyLib.CodeInstruction> Transpile(IEnumerable<HarmonyLib.CodeInstruction> i)=>BlastReportTranspiler(i);
  internal int Pending=>blastQueue.Count;internal int ProofCount=>blastThrows.Count;internal int Recent=>blastRecent.Count;internal bool HasContext=>blastCall!=null;
 }
}
