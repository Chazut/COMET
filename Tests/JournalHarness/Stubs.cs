// Platform doubles only. The observer, request lifecycle and JSON generation are production source.
using Comet.Core;
using EFT;
using EFT.InventoryLogic;
using SAIN.Components;
namespace UnityEngine { public static class Time{public static float time,realtimeSinceStartup;} public struct Vector3{public static float Distance(Vector3 a,Vector3 b)=>10;} }
namespace HarmonyLib {
 public class Harmony {public void Patch(object? x,HarmonyMethod? prefix=null,HarmonyMethod? postfix=null,HarmonyMethod? finalizer=null){} }
 public class HarmonyMethod {public int priority;public HarmonyMethod(Type t,string n){} }
 public static class AccessTools {public static object? Method(Type t,string n)=>null;}
}
namespace EFT.InventoryLogic {
 public enum EquipmentSlot{FirstPrimaryWeapon,SecondPrimaryWeapon,Holster}
 public sealed class Result{public bool Succeeded=true;}
 public class Slot {public Item? Item;public bool CanAccept(Ammo a)=>true;}
 public class Item{public string Id=Guid.NewGuid().ToString("N");}
 public class Magazine:Item{public int Count=20;}
 public class Ammo:Item{public int StackObjectsCount=30;public Result CheckAction(object? o)=>new();}
 public class Weapon:Item { public string ShortName="Rifle";public int ChamberAmmoCount=1;public Magazine Magazine=new();public Slot[] Chambers={new()};public Malf MalfState=new();public Slot MagSlot=new();public Magazine GetCurrentMagazine()=>Magazine;public Slot GetMagazineSlot()=>MagSlot;public class Malf{public string State="None";} }
 public class Inventory {public bool Fail;public int Scans;public void GetReachableItemsOfTypeNonAlloc<T>(List<T> items){Scans++;if(Fail)throw new Exception("inventory unavailable");}public void GetAcceptableItemsNonAlloc<T>(object slots,List<T> items,Func<T,bool> predicate){if(Fail)throw new Exception("inventory unavailable");}}
 public static class ItemManipulator{public static Result CheckMoveIgnoringTargetItem(Magazine m,Slot s,Inventory i)=>new();}
}
namespace EFT {
 public enum EBodyPart{Common,Head}
 public enum EDamageType{Bullet,Undefined}
 public interface IPlayer {string ProfileId{get;}bool IsAI{get;} }
 public class Player:IPlayer {public string ProfileId{get;set;}="pmc";public bool IsAI{get;set;}=true;public Health HealthController=new();public object? HandsController;public Inventory InventoryController=new();public Profile Profile=new();public bool Downed; public void ApplyDamageInfo(){} public void OnDead(){} public class FirearmController{public Weapon Item=new();} }
 public class Profile {public string Nickname="PMC";}
 public class Health{public bool IsAlive=true;public double Current=440;public Health GetBodyPartHealth(EBodyPart p,bool x)=>this;}
 public class BotReload{public static object _availableEquipmentSlots=new();}
 public class BotWeaponSelector {public Owner _owner=new(); public bool Ready=true;public void TryChangeToSlot(){} }
 public class Owner{public Player GetPlayer=new();public Manager WeaponManager=new();public Memory Memory=new();}
 public class Manager {public BotWeaponSelector Selector=null!;}
 public class Memory {public bool IsUnderFire;}
}
namespace EFT.Ballistics {public class DamageInfo {public Bridge? Player;public EDamageType DamageType=EDamageType.Bullet;}public class Bridge{public IPlayer iPlayer=null!;} }
namespace SAIN.Helpers{public static class Names{public static string Localized(this string s)=>s;}}
namespace SAIN {public static class SAINEnableClass{public static Dictionary<string,BotComponent> Bots=new();public static bool GetSAIN(string id,out BotComponent b)=>Bots.TryGetValue(id,out b!);}}
namespace SAIN.Components {
 public class BotComponent {public bool Pmc=true,BotActive=true,IsDead,GameEnding,SAINLayersActive=true;public string ProfileId=>Player.ProfileId;public Player Player=new();public Owner BotOwner=new();public object? CurrentAction=new HoldAction();public Decision Decision=new();public Enemy? GoalEnemy=new();public Info Info=new();public UnityEngine.Vector3 Position;public void ManualUpdate(){} }
 public class HoldAction{}
 public class Decision{public string CurrentCombatDecision="Hold",CurrentSelfDecision="None",CurrentSquadDecision="None";}
 public class Enemy{public string EnemyProfileId="enemy";public bool IsVisible;public double TimeSinceLastKnownUpdated=1;public Places KnownPlaces=new();}
 public class Places{public UnityEngine.Vector3? LastKnownPosition=new UnityEngine.Vector3();}
 public class Info{public string Personality="Normal";}
}
namespace Comet.Api {public static class Decisions {public static List<string> Events=new();public static void Publish(string j,Action<Exception> cb)=>Events.Add(j);}}
namespace Comet.Client {
 internal class Diagnostics { internal static string Serialize(object value)=>Newtonsoft.Json.JsonConvert.SerializeObject(value,Json);public List<string> Events=new();public static Newtonsoft.Json.JsonSerializerSettings Json=new(){ContractResolver=new Newtonsoft.Json.Serialization.CamelCasePropertyNamesContractResolver()};public void Write(string j)=>Events.Add(j);}
 internal static class NativeAdapter {internal static bool IsPmcAi(BotComponent b)=>b.Pmc&&b.Player.IsAI;}
 internal class BotState {internal readonly BotComponent Owner;internal string ProfileId=>Owner.ProfileId;internal string Name="PMC",Id=Guid.NewGuid().ToString("N"),ActiveLabel="",Feature="A01",Decision="Hold",Expected="Hold";internal Choice? Choice;internal readonly CombatJournalRuntime Journal=new();internal SwitchIntent WeaponReturn=new();internal Recovery HandsRecovery=new();internal BotState(BotComponent b,CometConfig c,string s){Owner=b;} }
 internal class SwitchIntent{public string Intent="";}
 internal class Recovery{public bool Issuing;}
 internal class Log{public void LogWarning(string s){} }
 public sealed partial class Plugin {
  internal static Plugin? Instance;internal CometConfig? config=new();internal object? world=new();internal bool Authority=true,Sleep;internal string session="raid";internal long sequence;internal Diagnostics diagnostics=new();internal Log Logger=new();internal Dictionary<BotComponent,BotState> states=new();
  internal bool Ghost(string id)=>Sleep;internal static bool ReadDowned(Player p)=>p.Downed;
  // H01/W01 ownership is exercised by ArbitrationHarness; this harness verifies its journal attribution.
  internal string TestOwnedService="";
  private string OwnedPlanService(BotComponent b,BotState s,bool committed)=>b.SAINLayersActive?TestOwnedService:"";
  private static bool GunSlot(EquipmentSlot slot)=>true;
  internal Dictionary<(BotComponent,EquipmentSlot),Weapon> Guns=new();
  private static Weapon? SlotGun(BotComponent b,EquipmentSlot slot)=>Instance!.Guns.TryGetValue((b,slot),out var w)?w:null;
  private static bool WeaponHandsReady(BotComponent b,Player.FirearmController? f)=>f!=null&&b.BotOwner.WeaponManager.Selector.Ready;
  private static bool OutcomeAction(BotComponent b,string action)=>b.CurrentAction?.GetType().Name==action+"Action";
  partial void EndJournalBot(BotState s,string reason);partial void FinishCombatJournal();
  internal void Track(BotComponent b)=>TrackCombatJournal(b);
  internal void Sample(BotComponent b)=>SampleCombatJournal(b,states[b]);
  internal void Stop(BotComponent b)=>EndJournalBot(states[b],"test-pause");
  internal void End()=>FinishCombatJournal();
  internal void Switch(BotComponent b,bool accepted,bool throws=false)
  {I01JournalWeaponHook.Before(b.BotOwner.WeaponManager.Selector,EquipmentSlot.Holster,out var s);if(throws)I01JournalWeaponHook.Fault(new Exception("native"),s);else I01JournalWeaponHook.After(accepted,s);}
  internal void Hit(Player victim,Player attacker,int damage)
  {var d=new EFT.Ballistics.DamageInfo{Player=new(){iPlayer=attacker}};I01JournalHitHook.Before(victim,d,out var s);victim.HealthController.Current-=damage;I01JournalHitHook.After(victim,d,EBodyPart.Head,s);}
  internal void Die(Player victim,IPlayer? attacker)=>JournalDeathObserved(victim,EDamageType.Bullet,attacker);
 }
}
