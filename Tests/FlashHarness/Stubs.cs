using System.Reflection;
using SAIN.Components;
using SAIN.SAINComponent.Classes.Sense;
using UnityEngine;
namespace UnityEngine {
 public struct Vector3(float x,float y,float z){public float x=x,y=y,z=z;public static Vector3 up=>new(0,1,0);public static Vector3 operator +(Vector3 a,Vector3 b)=>new(a.x+b.x,a.y+b.y,a.z+b.z);public static Vector3 operator *(Vector3 a,float b)=>new(a.x*b,a.y*b,a.z*b);}
 public static class Time { public static float time,realtimeSinceStartup;public static void Set(float t){time=realtimeSinceStartup=t;} }
}
namespace HarmonyLib {
 public class Harmony {public void Patch(MethodInfo? method,HarmonyMethod? prefix=null,HarmonyMethod? postfix=null,HarmonyMethod? finalizer=null){if(method==null)throw new MissingMethodException();}}
 public class HarmonyMethod(Type t,string name){public int priority;}
 public static class Priority {public const int Last=0;}
 public static class AccessTools {const BindingFlags Flags=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;public static FieldInfo? Field(Type t,string n)=>t.GetField(n,Flags);public static MethodInfo? PropertySetter(Type t,string n)=>t.GetProperty(n,Flags)?.GetSetMethod(true);public static MethodInfo? Method(Type t,string n,Type[]? args=null)=>args==null?t.GetMethod(n,Flags):t.GetMethod(n,Flags,null,args,null);}
}
namespace SAIN {public static class SAINPlugin {public static Preset LoadedPreset=new();} public class Preset{public Global GlobalSettings=new();}public class Global{public Look Look=new();}public class Look{public FlashSettings Flashbang=new();} public class FlashSettings{public float DurationMultiplier=1.2f,NightVisionMultiplier=2,MaxDuration=30,BlindFireDelay=4,RecoveryPoint=.7f;}}
namespace SAIN.Helpers {public class TemporaryStatModifiers {public object Modifiers=new();}}
namespace SAIN.Components {
 public class BotComponent {public bool IsDead,BotActive=true,GameEnding,Pmc=true;public string ProfileId="pmc";public BotOwner BotOwner=new();public Info Info=new();public Enemy? GoalEnemy=new();public BotFlashedClass Flashed; public BotComponent(){Flashed=new(this);} }
 public class BotOwner{public NightVision NightVision=new();public Settings Settings=new();public Group BotsGroup=new();}
 public class NightVision{public bool UsingNow;}
 public class Group{public int Searches;public Vector3? MemoryAtSearch;public void AddPointToSearch(Vector3 p,BotComponent b){Searches++;MemoryAtSearch=b.Flashed.LastSeenEnemyPoint;}}
 public class Settings{public Current Current=new();}
 public class Current{public Dictionary<object,float> Applied=new();public void Apply(object m,float duration){Applied[m]=Time.time+duration;}public void Dismiss(object m)=>Applied.Remove(m);public void Sweep(){foreach(var k in Applied.Where(x=>x.Value<=Time.time).Select(x=>x.Key).ToArray())Applied.Remove(k);}}
 public class Info{public FileSettings FileSettings=new();}public class FileSettings{public BotLook Look=new();}public class BotLook{public float FlashDurationMulti=1;}
 public class Enemy {public Vector3 EnemyPosition=new(100,0,100);public string EnemyProfileId="enemy";public Known KnownPlaces=new();} public class Known{public Place? LastSeenPlace=new();}public class Place{public Vector3 Position=new(4,0,5);}
}
namespace SAIN.SAINComponent.Classes.Sense {
 // Emulates the inspected official 4.5.1 body; wrappers dispatch the production Harmony callbacks.
 public class BotFlashedClass(BotComponent bot){
  public BotComponent Bot=bot;private float _flashEndTime,_blindFireTime;private SAIN.Helpers.TemporaryStatModifiers? _firstStage,_secondStage;
  private Vector3? memory;public Vector3? LastSeenEnemyPoint{get=>memory;private set{var point=value;Comet.Client.Plugin.Memory(this,ref point);memory=point;}}
  public bool IsFlashed=>_flashEndTime>Time.time;public float TimeRemaining=>Math.Max(0,_flashEndTime-Time.time);public bool BlindFireReady=>_blindFireTime<Time.time;
  public float End=>_flashEndTime;public float Delay=>_blindFireTime;public object? First=>_firstStage?.Modifiers;public object? Second=>_secondStage?.Modifiers;
  public void ApplyFlash(float baseTime,Vector3 position){
   bool run=Comet.Client.Plugin.Before(this,baseTime,position,out var call);
   if(run){var c=SAIN.SAINPlugin.LoadedPreset.GlobalSettings.Look.Flashbang;float d=Math.Min(baseTime*c.DurationMultiplier*Bot.Info.FileSettings.Look.FlashDurationMulti*(Bot.BotOwner.NightVision.UsingNow?c.NightVisionMultiplier:1),c.MaxDuration);
    if(d>0){LastSeenEnemyPoint=Bot.GoalEnemy==null?null:Bot.GoalEnemy.EnemyPosition+Vector3.up*1.2f;Bot.BotOwner.BotsGroup.AddPointToSearch(position,Bot);_flashEndTime=Time.time+d;_blindFireTime=Time.time+c.BlindFireDelay;ApplyModifiers(d,c.RecoveryPoint);}}
   Comet.Client.Plugin.After(this,call,run);
  }
  private void ApplyModifiers(float duration,float recoveryPoint){Comet.Client.Plugin.Stages(this,ref duration,ref recoveryPoint);Dismiss();_firstStage=new();_secondStage=new();Bot.BotOwner.Settings.Current.Apply(_firstStage.Modifiers,duration);Bot.BotOwner.Settings.Current.Apply(_secondStage.Modifiers,duration*recoveryPoint);}
  private void Dismiss(){if(_firstStage!=null)Bot.BotOwner.Settings.Current.Dismiss(_firstStage.Modifiers);if(_secondStage!=null)Bot.BotOwner.Settings.Current.Dismiss(_secondStage.Modifiers);_firstStage=_secondStage=null;}
  public void ManualUpdate(){Bot.BotOwner.Settings.Current.Sweep();if(_firstStage!=null&&!IsFlashed){Dismiss();LastSeenEnemyPoint=null;}Comet.Client.Plugin.Tick(this);}
  public void Dispose(){Dismiss();Comet.Client.Plugin.Disposed(this);}
  public void ReplaceForeign(){_firstStage=new();_secondStage=new();_flashEndTime=Time.time+25;Bot.BotOwner.Settings.Current.Apply(_firstStage.Modifiers,25);Bot.BotOwner.Settings.Current.Apply(_secondStage.Modifiers,25);}
 }
}
namespace Comet.Core{public class CometConfig {public bool Enabled=true;public long Revision=1;public FlashContinuityConfig FlashContinuity=new();}}
namespace Comet.Client {
 public static class NativeAdapter{public static bool IsPmcAi(BotComponent b)=>b.Pmc;}
 internal class GasRuntime{internal double Intensity;}
 internal class BotState {internal readonly FlashRuntime Flash=new();internal readonly GasRuntime Gas=new();internal string ProfileId,Name="test",Id="instance";internal BotState(BotComponent b){ProfileId=b.ProfileId;}}
 public class Sink{public List<string> Lines=new();public void Write(string s)=>Lines.Add(s);public void Publish(string s,Action<Exception> onError){}}
 public static class Api{public static Sink Decisions=new();}
 public static class Diagnostics{ internal static string Serialize(object value)=>Newtonsoft.Json.JsonConvert.SerializeObject(value,Json);public static Newtonsoft.Json.JsonSerializerSettings Json=new(){ContractResolver=new Newtonsoft.Json.Serialization.CamelCasePropertyNamesContractResolver()};}
 public class Log{public void LogWarning(string s)=>Console.WriteLine(s);}
 public sealed partial class Plugin {
  internal static Plugin? Instance;internal Dictionary<BotComponent,BotState> states=new();internal Comet.Core.CometConfig? config=new();internal object? world=new();internal bool Authority=true;internal string session="g09-harness";internal long sequence;internal bool subscriberWarned;internal Sink? diagnostics=new();internal Log Logger=new();internal HashSet<string> ghosts=new();
  public Plugin(){Instance=this;PatchFlashContinuity(new());}private bool Ghost(string id)=>ghosts.Contains(id);internal BotState GrenadeState(BotComponent b){if(!states.TryGetValue(b,out var s))states.Add(b,s=new(b));return s;}
  internal static bool Before(BotFlashedClass f,float d,Vector3 p,out FlashCall? c)=>BeforeRealFlash(f,d,p,out c);internal static void After(BotFlashedClass f,FlashCall? c,bool run)=>AfterRealFlash(f,c,run);internal static void Memory(BotFlashedClass f,ref Vector3? p)=>ProtectFlashMemory(f,ref p);internal static void Stages(BotFlashedClass f,ref float d,ref float r)=>ExtendFlashStages(f,ref d,ref r);internal static void Tick(BotFlashedClass f)=>ObserveFlash(f);internal static void Disposed(BotFlashedClass f)=>DisposedFlash(f);
  internal void End(){foreach(var pair in states)DropFlash(pair.Key,pair.Value,"flash-raid-ended",true);states.Clear();}
  internal void Sweep(BotComponent b){if(states.TryGetValue(b,out var s)&&!FlashScope(b))DropFlash(b,s,"flash-out-of-scope",DeadFlashOwner(b));}
 }
}
