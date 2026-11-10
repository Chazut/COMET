// Detached adapter harness. These shims model only the inspected SAIN/Unity contracts used by W09.
// It executes the production adapter, but is not an in-game physics/animation or Harmony patch test.
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
namespace UnityEngine { public static class Time { public static double timeAsDouble; public static float fixedDeltaTime=.02f;public static float realtimeSinceStartup=>(float)timeAsDouble; } }
namespace HarmonyLib {
 public class Harmony { public void Patch(object? original,HarmonyMethod? prefix=null,HarmonyMethod? postfix=null){} }
 public class HarmonyMethod {public HarmonyMethod(Type t,string name){} }
 public static class AccessTools {public static object? Method(Type t,string name)=>t.GetMethod(name);}
}
namespace EFT {
 public class Weapon {public string Id="w";}
 public class Player {public object? HandsController=new FirearmController();public class FirearmController {public Weapon? Item=new();}}
}
namespace SAIN {
 public static class SAINPlugin {public static Preset LoadedPreset=new();}
 public class Preset {public Settings GlobalSettings=new();}
 public class Settings {public ShootSettings Shoot=new();}
 public class ShootSettings {public float BOT_RECOIL_DECAY_COEF=1;}
}
namespace SAIN.SAINComponent.Classes.Info {
 public enum WeaponKind {marksmanRifle,sniperRifle,assaultRifle,pistol}
 public class WeaponInfo {public EFT.Weapon? Weapon=new();public WeaponKind EWeaponClass=WeaponKind.marksmanRifle;}
}
namespace SAIN.Components {
 public class Info {public SAIN.SAINComponent.Classes.Info.WeaponInfo? WeaponInfo=new();}
 public class Decisions {public string CurrentCombatDecision="StandAndShoot";}
 public class BotComponent {public bool Pmc=true,BotActive=true,IsDead,GameEnding,IsCheater,SAINLayersActive=true;
  public string ProfileId="p";public Info Info=new();public EFT.Player Player=new();public Decisions Decision=new();}
}
namespace SAIN.SAINComponent.Classes.WeaponFunction {
 public class Recoil {public SAIN.Components.BotComponent Bot=new();public void WeaponShot(){}public void CalcRecoilDecay(){} }
}
namespace Comet.Core {
 public class CometConfig {public bool Enabled=true;public int Revision=1;public RecoilRecoveryConfig RecoilRecovery=new();
  public static void Range(double x,double min,double max,string name){if(!double.IsFinite(x)||x<min||x>max)throw new ArgumentException(name);}}
}
namespace Comet.Client {
 public class Situation {public string Target="known";}
 internal class BotState {internal RecoilRecoveryRuntime RecoilRecovery=new();internal string ProfileId="p",Name="PMC",Id="instance";internal Situation? Situation=new();
  internal BotState(SAIN.Components.BotComponent b,Comet.Core.CometConfig c,string session){} }
 internal static class NativeAdapter {internal static bool IsSupportedAi(SAIN.Components.BotComponent b)=>b.Pmc;}
 internal class Diagnostics { internal static string Serialize(object value)=>Newtonsoft.Json.JsonConvert.SerializeObject(value,Json);internal static JsonSerializerSettings Json=new(){ContractResolver=new CamelCasePropertyNamesContractResolver()};
  internal bool Throw;internal List<string> Events=new();internal void Write(string s){if(Throw)throw new Exception("test log fault");Events.Add(s);} }
 internal class Log {internal void LogWarning(string s){} }
 namespace Api {internal static class Decisions {internal static void Publish(string s,Action<Exception> callback){} }}
 public sealed partial class Plugin {
  internal static Plugin? Instance;private Comet.Core.CometConfig? config=new();private Dictionary<SAIN.Components.BotComponent,BotState> states=new();
  private string session="harness";private long sequence;private bool subscriberWarned;private Diagnostics? diagnostics=new();private Log Logger=new();
  internal bool Authority=true,IsGhost;private bool Ghost(string id)=>IsGhost;
  internal Plugin(){Instance=this;config!.RecoilRecovery.Enabled=true;}
  internal Comet.Core.CometConfig Settings=>config!;
  internal bool LogThrows {set=>diagnostics!.Throw=value;}
  internal List<string> Events=>diagnostics!.Events;
  internal int Count=>states.Count;
  internal RecoilRecoveryRuntime Runtime(SAIN.Components.BotComponent b)=>states[b].RecoilRecovery;
  internal void Shot(SAIN.SAINComponent.Classes.WeaponFunction.Recoil recoil,float h,float v)=>RecoveryShot(recoil,recoil.Bot.Info.WeaponInfo!,h,v);
  internal bool Decay(SAIN.SAINComponent.Classes.WeaponFunction.Recoil recoil,ref float h,ref float v,ref bool done)=>RecoveryDecay(recoil,ref h,ref v,ref done);
  internal void Release(SAIN.Components.BotComponent b)=>EndRecovery(states[b],"raid-ended");
 }
}
