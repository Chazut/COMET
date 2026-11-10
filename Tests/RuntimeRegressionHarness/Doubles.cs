using System.Reflection;
using System.Numerics;
enum EquipmentSlot { Pockets, TacticalVest, Backpack, SecuredContainer, Weapon, ArmBand }
class Item { public Address? CurrentAddress; public MedKitComponent? Med; public T? GetItemComponent<T>() where T:class=>Med as T; public Address Parent => throw new Exception("EFT Parent throws for detached item"); }
class MedKitComponent {public float HpResource=100;}
class BotAbstractMedsToPart {public Item? CurUsingMeds;public int? _bodyPartToHeal=1;}
class Inventory {public InventoryEquipment Equipment=new();}
class Health {public int Calls;public bool Allowed=true;public bool CanApplyItem(Item i,int part){Calls++;return Allowed;}}
static class LoadoutModel {public static bool UsableMedicalSupply(float hp,bool carried,bool target,bool canApply)=>hp>0&&carried&&target&&canApply;}
class Address { public Container? Container; }
class Container { public Item? ParentItem; }
class Slot { public Item? ContainedItem; }
class InventoryEquipment : Item { public readonly Dictionary<EquipmentSlot,Slot> Slots=new(); public Slot? GetSlot(EquipmentSlot s)=>Slots.GetValueOrDefault(s); }
enum EBotMoveStatus { Stopped, ReadyToMove, Moving }
enum ECombatDecision { StandAndShoot, DogFight, Retreat }
enum ESelfActionType { None, FirstAid }
enum ESquadDecision { None, Regroup }
static class Time { public static float time=10; }
interface IBotPathData { Vector3 Destination {get;set;} float TimeStarted {get;set;} EBotMoveStatus Status {get;set;} }
class BotPathDataManual : IBotPathData {
 public Vector3 Destination {get;set;} public float TimeStarted {get;set;} public EBotMoveStatus Status {get;set;}
 public int Stops; public Action? OnStop;
 public void Stop(bool ignored,string reason){Stops++;Status=EBotMoveStatus.Stopped;OnStop?.Invoke();}
}
class OtherPath : IBotPathData {public Vector3 Destination {get;set;} public float TimeStarted {get;set;} public EBotMoveStatus Status {get;set;}}
class Mover {public IBotPathData? ActivePath;public BotPathDataManual One=new(),Two=new();}
class Decision {public ECombatDecision CurrentCombatDecision;public ESelfActionType CurrentSelfDecision;public ESquadDecision CurrentSquadDecision;}
class MovementContext {public bool IsGrounded=true;}
class Player {public MovementContext MovementContext=new();public Inventory Inventory=new();public Health HealthController=new();}
class Grenade {public object? GrenadeDangerPoint;}
class Medicine {public bool Using;}
class Reload {public bool Reloading;}
class Grenades {public bool ThrowindNow;}
class WeaponManager {public Reload Reload=new();public Grenades Grenades=new();}
class Owner {public Medicine Medecine=new();public WeaponManager WeaponManager=new();}
class BotComponent {public Mover Mover=new();public Decision Decision=new();public Player Player=new();public Grenade Grenade=new();public Owner BotOwner=new();}
partial class Runtime {
 static BotPathDataManual combatPathOne(Mover m)=>m.One;
 static BotPathDataManual combatPathTwo(Mover m)=>m.Two;
 static readonly MethodInfo combatStopPath=typeof(BotPathDataManual).GetMethod("Stop")!;
 public static bool Release(BotComponent b,out int released)=>ReleaseShootingRoute(b,out released);
 public static bool Medical(BotAbstractMedsToPart m,Player p)=>UsableMed(m,p);
}
