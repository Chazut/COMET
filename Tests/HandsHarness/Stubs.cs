// Executes the production W10 adapter against inspected native contracts. No Unity animation or network simulation.
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using EFT;
using EFT.InventoryLogic;
namespace UnityEngine {public static class Time {public static double timeAsDouble;public static float realtimeSinceStartup=>(float)timeAsDouble;}}
namespace HarmonyLib {
 public class Harmony {public void Patch(object? original,HarmonyMethod? prefix=null,HarmonyMethod? postfix=null){if(original==null)throw new Exception("missing patch");}}
 public class HarmonyMethod {public int priority;public HarmonyMethod(Type t,string n){}}
 public static class Priority {public const int First=800;}
 public static class AccessTools {public static object? Method(Type t,string n)=>t.GetMethod(n);}
}
namespace EFT.InventoryLogic {public enum EquipmentSlot {FirstPrimaryWeapon,SecondPrimaryWeapon,Holster,Scabbard} public class Weapon {public string Id="rifle";public int Loaded=20;}}
namespace EFT {
 public class Item {public string Id="grenade";}
 public class Player {
  public string ProfileId="p";public AbstractHandsController? HandsController=new FirearmController();public Dictionary<EquipmentSlot,Weapon> Equipment=new(){{EquipmentSlot.FirstPrimaryWeapon,new()}};
  public bool UsedSimplifiedSkeleton;public Inventory InventoryController=new();public class Inventory{public bool IsChangingWeapon;}
  public class AbstractHandsController {public Item? Item=new();}
  public class FirearmController:AbstractHandsController {public new Weapon? Item=new();public object CurrentOperation=new Idling();public bool Reload,Interaction,CanReload=true;
   public bool IsInReloadOperation()=>Reload;public bool IsInInteraction()=>Interaction;public bool CanStartReload()=>CanReload;public class Idling{}
   public class ReloadExternalMagOperation{public int State;public bool _magPulledOutFromWeapon,_magAppeared,_magInsertedToWeapon,_addAmmoInChamber;public int IdleCalls;public Action? Complete;public void OnIdleStartEvent(){IdleCalls++;Complete?.Invoke();}}}
  public class GrenadeHandsController:AbstractHandsController {public object CurrentOperation=new Idling();public bool WaitingForHighThrow,WaitingForLowThrow;public class Idling{}public class HighThrow{} }
 }
}
public class BotReload {public Player? _player;public bool Reloading;public int FillCalls;public void TryFillMagazines(){if(Comet.Client.Plugin.FillAllowed(this))FillCalls++;}}
public class BotWeaponSelector {
 public SAIN.Components.Owner? _owner;public EquipmentSlot _lastEquipmentSlot=EquipmentSlot.FirstPrimaryWeapon,_mainWeapon=EquipmentSlot.FirstPrimaryWeapon;
 public bool IsChanging,Accept=true,Throw;public int Calls;public Action? DuringRequest;
 public bool IsWeaponReady=true;public EquipmentSlot EquipmentSlot=>_lastEquipmentSlot;
 public bool TryChangeToSlot(EquipmentSlot slot,bool main){bool result=false;if(!Comet.Client.Plugin.SwitchAllowed(this,ref result))return result;Calls++;_owner!.WeaponManager.Reload.TryFillMagazines();DuringRequest?.Invoke();if(Throw)throw new Exception("native failure");return Accept;}
}
namespace SAIN {
 public static class SAINEnableClass {public static SAIN.Components.BotComponent? Bot;public static bool GetSAIN(string id,out SAIN.Components.BotComponent b){b=Bot!;return Bot!=null&&Bot.ProfileId==id;}}
}
namespace SAIN.Components {
 public class Grenades {public bool ThrowindNow;}
 public class Medicine {public bool Using;}
 public class Weapons {public BotReload Reload=new();public BotWeaponSelector Selector=new();public Grenades Grenades=new();public bool Ready=true,AnimatorIdle;public object? ShootController,CurrentWeapon;public bool InIdleState()=>AnimatorIdle;}
 public class Owner {public string ProfileId="p";public Weapons WeaponManager=new();public Medicine Medecine=new();}
 public class Decisions {public string CurrentCombatDecision="StandAndShoot";}
 public class BotComponent {public bool Pmc=true,BotActive=true,IsDead,GameEnding;public string ProfileId="p";public Player Player=new();public Owner BotOwner=new();public Decisions Decision=new();
  public void ManualUpdate(){}public BotComponent(){BotOwner.WeaponManager.Reload._player=Player;BotOwner.WeaponManager.Selector._owner=BotOwner;SAIN.SAINEnableClass.Bot=this;
   var gun=(Player.FirearmController)Player.HandsController!;BotOwner.WeaponManager.ShootController=gun;BotOwner.WeaponManager.CurrentWeapon=gun.Item;Player.Equipment[EquipmentSlot.FirstPrimaryWeapon]=gun.Item!;}}
}
namespace Comet.Core {public class CometConfig {public bool Enabled=true;public int Revision=1;public HandsRecoveryConfig HandsRecovery=new();
 public static void Range(double v,double lo,double hi,string n){if(!double.IsFinite(v)||v<lo||v>hi)throw new ArgumentException(n);}}}
namespace Comet.Client {
 public class Situation {public string Target="known";}
 public class Policy {public bool Active;}
 public class OtherRuntime {public Policy Policy=new();public bool CallbackPending;}
 internal class BotState {internal HandsRecoveryRuntime HandsRecovery=new();internal string ProfileId="p",Name="PMC",Id="instance";internal Situation? Situation=new();
  internal OtherRuntime Fake=new(),RoomClear=new(),WeaponReturn=new(),PistolSwap=new(),QuickReload=new();
  internal BotState(SAIN.Components.BotComponent b,Comet.Core.CometConfig c,string session){} }
 internal static class NativeAdapter {internal static bool IsPmcAi(SAIN.Components.BotComponent b)=>b.Pmc;}
 internal class Diagnostics { internal static string Serialize(object value)=>Newtonsoft.Json.JsonConvert.SerializeObject(value,Json);internal static JsonSerializerSettings Json=new(){ContractResolver=new CamelCasePropertyNamesContractResolver()};
  internal bool Throw;internal List<string> Events=new();internal void Write(string s){if(Throw)throw new Exception("log failure");Events.Add(s);}}
 internal class Log {internal void LogWarning(string s){} }
 namespace Api {internal static class Decisions {internal static void Publish(string s,Action<Exception> cb){}}}
 public sealed partial class Plugin {
  internal static Plugin? Instance;private Comet.Core.CometConfig? config=new();private Dictionary<SAIN.Components.BotComponent,BotState> states=new();
  private string session="harness";private long sequence;private bool subscriberWarned;private Diagnostics? diagnostics=new();private Log Logger=new();
  internal bool Authority=true,IsGhost,FeintGuard;private bool Ghost(string id)=>IsGhost;
  private static EquipmentSlot[] SyncSlots={EquipmentSlot.FirstPrimaryWeapon,EquipmentSlot.SecondPrimaryWeapon,EquipmentSlot.Holster};
  private static bool GunSlot(EquipmentSlot s)=>s!=EquipmentSlot.Scabbard;
  private static Weapon? SlotGun(SAIN.Components.BotComponent b,EquipmentSlot s)=>b.Player.Equipment.GetValueOrDefault(s);
  private static int ReloadLoaded(Weapon w)=>w.Loaded;
  private static bool WeaponHandsReady(SAIN.Components.BotComponent b,Player.FirearmController? g)=>g!=null&&b.BotOwner.WeaponManager.Ready&&!b.BotOwner.WeaponManager.Selector.IsChanging&&!g.Reload&&!g.Interaction&&g.CanReload;
  private static bool FakeGuarded(SAIN.Components.BotComponent b)=>Instance!.FeintGuard;
  internal Plugin(){Instance=this;PatchHandsRecovery(new());}
  internal Comet.Core.CometConfig Settings=>config!;
  internal List<string> Events=>diagnostics!.Events;
  internal bool LogThrows{set=>diagnostics!.Throw=value;}
  internal int Count=>states.Count;
  internal BotState State(SAIN.Components.BotComponent b)=>states[b];
  internal void Tick(SAIN.Components.BotComponent b,double now){UnityEngine.Time.timeAsDouble=now;TrackHandsRecovery(b);if(states.TryGetValue(b,out var s))try{SampleHandsRecovery(b,s);}catch(Exception ex){HandsRecoveryError(s,ex);}}
  internal void Release(SAIN.Components.BotComponent b)=>EndHandsRecovery(states[b],"raid-ended");
  public static bool FillAllowed(BotReload reload)=>HandsFillGuard(reload);
  public static bool SwitchAllowed(BotWeaponSelector selector,ref bool result)=>HandsSwitchGuard(selector,ref result);
 }
}
