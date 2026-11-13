using Comet.Core;
using SAIN.Components;
using System.Reflection;
namespace UnityEngine { public struct Vector3 {public float x,y,z;} public class Player {public UnityEngine.Vector3 Velocity;public float PoseLevel=1;public bool IsInPronePose,IsSprintEnabled;} public static class Time {public static float time,realtimeSinceStartup;public static double unscaledTimeAsDouble;} }
namespace EFT {
 public enum BotLogicDecision { Test }
 public class AICoreLayer<T>(string name){public string Name()=>name;}
 public class BrainCore {public event Action<AICoreLayer<BotLogicDecision>>? OnLayerChangedTo;public int Listeners=>OnLayerChangedTo?.GetInvocationList().Length??0;public string Name="SAIN : Combat Layer";public void Select(string name){Name=name;OnLayerChangedTo?.Invoke(new(name));}}
 public class Brain {public BrainCore BaseBrain=new();public bool ThrowRead;public string ActiveLayerName(){if(ThrowRead)throw new Exception("injected layer read");return BaseBrain.Name;}}
 public class Owner {public Brain Brain=new();public Memory Memory=new();public Medicine Medecine=new();public Weapons WeaponManager=new();}
 public class Memory {public bool IsUnderFire;} public class Medicine {public bool Using;} public class Weapons {public Reload Reload=new();} public class Reload {public bool Reloading;}
}
namespace SAIN.Components {public class BotComponent {public bool IsDead,Pmc=true,Human;public string ProfileId=Guid.NewGuid().ToString();public EFT.Owner BotOwner=new();public UnityEngine.Player Player=new();public bool SAINLayersActive=true;public object? CurrentAction=new();public Decision Decision=new();public Contact? GoalEnemy;public Info Info=new();}}
namespace SAIN.Components {public class Decision {public string CurrentCombatDecision="Hold",CurrentSelfDecision="None",CurrentSquadDecision="None";} public class Contact {public string EnemyProfileId="enemy";public bool IsVisible;public float TimeSinceLastKnownUpdated;}public class Info {public string Personality="Normal";} }
namespace BepInEx.Bootstrap {
 public class Info {public object Instance=new();}
 public static class Chainloader {public static Dictionary<string,Info> PluginInfos=new();}
}
namespace HarmonyLib {
 public class HarmonyMethod(MethodInfo? method){public MethodInfo? Method=method;}
 public static class AccessTools {public static MethodInfo? Method(Type type,string name)=>type.GetMethod(name,BindingFlags.Static|BindingFlags.NonPublic|BindingFlags.Public);}
 public class Harmony {public int Hooks;public MethodInfo? Callback;public void Patch(MethodInfo m,HarmonyMethod postfix){if(m.ReturnType!=typeof(bool)||postfix.Method==null)throw new Exception("contract");Hooks++;Callback=postfix.Method;}}
}
namespace QuestingBots.Controllers {public static class BotRegistrationManager {public static readonly HashSet<string> Asleep=new();public static bool Throw;public static bool IsBotSleeping(string id)=>Throw?throw new Exception("QB unavailable"):Asleep.Contains(id);}}
namespace QuestingBots.BotLogic.ExternalMods.ModInfo {public static class SAINModInfo {public static bool IsSAINLayer(string name)=>name.StartsWith("SAIN");}}
namespace Comet.Client {
 internal static class NativeAdapter {internal static bool IsSupportedAi(BotComponent b)=>b.Pmc&&!b.Human;}
 internal sealed class BotState(BotComponent b) {
  internal BotComponent Owner=b;internal string ProfileId=b.ProfileId,Id=Guid.NewGuid().ToString(),Name="PMC",ActiveLabel="COMET:Hold",Decision="Hold";internal bool Retired;internal string Feature="A01",Expected="Hold";internal Recovery Crossfire=new();internal Reservation Reservation=new();
  internal Handoff Handoff=new();internal Recovery PostCombat=new();
 }
 internal class Reservation {internal Lease? Lease;} internal class Lease {internal double Until;}
 internal class Handoff{public int Cleared;internal void Clear()=>Cleared++;}
 internal class Policy{internal bool Active;}
 internal class Recovery{internal Policy Policy=new();internal bool Layer;}
 internal class Config{internal bool Enabled=true;internal int Revision=42;internal ControlJournalConfig ControlJournal=new(){Enabled=false};}
 internal class Integration{internal Report Report=new();}
 internal class Report{internal string[] Notices=Array.Empty<string>();}
 internal class Log{internal List<string> Lines=new();internal void LogWarning(string text)=>Lines.Add(text);internal void LogInfo(string text)=>Lines.Add(text);}
 internal class Diagnostics { internal static string Serialize(object value)=>Newtonsoft.Json.JsonConvert.SerializeObject(value,Json);internal static Newtonsoft.Json.JsonSerializerSettings Json=new(){ContractResolver=new Newtonsoft.Json.Serialization.CamelCasePropertyNamesContractResolver()};internal void Write(string text){} internal void WriteEssential(string text)=>Write(text); }
 public sealed partial class Plugin {
  private bool ReadDowned(object player)=>Downed;private bool OutcomeAction(BotComponent b,string choice)=>b.Decision.CurrentCombatDecision==choice;internal bool Downed;
  // ArbitrationHarness covers actual H01/W01 ownership; supply it here to exercise journal transitions.
  internal string TestOwnedService="";
  private string OwnedPlanService(BotComponent b,BotState s,bool committed)=>b.SAINLayersActive?TestOwnedService:"";
  internal static Plugin? Instance;internal Config? config=new();internal Integration integration=new();internal Log Logger=new();
  internal Dictionary<BotComponent,BotState> states=new();internal Func<string,bool>? isGhost;internal bool orbitUnavailable,Authority=true;
  internal object? world=new();internal string session="i02-controlled";internal long sequence;internal Diagnostics? diagnostics;
  internal int Releases,RecoveryEnds,CrossfireEnds;internal object ForeignRoute=new(),NativeHands=new(),Quest=new();
  internal Plugin(){Instance=this;}
  partial void ObserveControlRelease(BotState s,string reason);
  partial void ClearControlHandoffs();
  internal void EndCrossfire(BotComponent b,BotState s,string reason)=>CrossfireEnds++;
  internal void EndPostCombat(BotComponent b,BotState s,string reason,bool clear){RecoveryEnds++;ObserveControlRelease(s,reason);}
  internal void Release(BotState s,string reason){Releases++;ObserveControlRelease(s,reason);s.ActiveLabel="";}
  internal void WarnFaultSink(Exception ex)=>Logger.LogWarning(ex.Message);
  internal BotState Add(){var s=new BotState(new());states[s.Owner]=s;return s;}
  internal void Connect(HarmonyLib.Harmony h)=>ConnectControlIntegrations(h);
  internal void Sample()=>SampleControlHandoffs();
  internal void Freed(BotState s,string why)=>ObserveControlRelease(s,why);
  internal bool Sleep(string id)=>Sleeping(id);
  internal void Clear()=>ClearControlHandoffs();
 }
}
namespace Comet.Api {public static class Decisions {public static readonly List<string> Events=new();public static void Publish(string text,Action<Exception> warn)=>Events.Add(text);}}

namespace Comet.Core {public class CometConfig {public static void Range(double v,double min,double max,string name){if(double.IsNaN(v)||v<min||v>max)throw new InvalidOperationException(name);}}}
