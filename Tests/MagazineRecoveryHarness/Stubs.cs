using System.Reflection;
using Comet.Core;
using EFT;
using EFT.Interactive;
using SAIN.Components;
using SAIN.SAINComponent.Classes.Mover;
using UnityEngine;

public interface IResult { bool Succeed { get; } }
public record Result(bool Succeed):IResult;
namespace Comfort.Common { }
namespace UnityEngine
{
    public struct Vector3(float x,float y,float z)
    {
        public float x=x,y=y,z=z;
        public static Vector3 up=>new(0,1,0);
        public static Vector3 operator +(Vector3 a,Vector3 b)=>new(a.x+b.x,a.y+b.y,a.z+b.z);
        public static Vector3 operator *(Vector3 a,float b)=>new(a.x*b,a.y*b,a.z*b);
        public static float Distance(Vector3 a,Vector3 b)=>(float)Math.Sqrt((a.x-b.x)*(a.x-b.x)+(a.y-b.y)*(a.y-b.y)+(a.z-b.z)*(a.z-b.z));
    }
    public class Transform { public Vector3 position; }
    public struct Vector2 { public static Vector2 zero=>new(); }
    public class GameObject { public bool activeInHierarchy=true; }
    public class Collider { public string name="wall"; public object? Parent; public T? GetComponentInParent<T>() where T:class=>Parent as T; }
    public struct RaycastHit { public Collider collider; }
    public static class Time { public static float time; public static float realtimeSinceStartup=>time; }
    public enum QueryTriggerInteraction { Ignore }
    public static class LayerMask { public static int GetMask(params string[] values)=>1; }
    public static class Physics
    {
        public static Collider? Obstacle;
        public static bool Linecast(Vector3 a,Vector3 b,out RaycastHit hit,int mask,QueryTriggerInteraction q){hit=new(){collider=Obstacle!};return Obstacle!=null;}
    }
}
namespace EFT.InventoryLogic
{
    public class Item(string id) { public string Id=id,TemplateId="template";public string LocalizedShortName()=>"6L20"; }
    public class Equipment { public readonly List<Item> Items=new(); public IEnumerable<Item> GetAllItems()=>Items; }
    public class Inventory { public Equipment Equipment=new(); }
    public class InventoryController
    {
        public Inventory Inventory=new(); public bool HasSpace=true; public int Calls;
        public TaskCompletionSource<IResult> Completion=new();
        public Task<IResult> TryRunNetworkTransaction(Place place,object? cb){Calls++;return Completion.Task;}
    }
    public record Place(bool Succeeded);
    public static class ItemManipulator
    {
        public enum EMoveItemOrder { PickUp }
        public static Place QuickFindAppropriatePlace(Item item,InventoryController controller,Equipment[] containers,EMoveItemOrder order,bool simulate)=>new(controller.HasSpace);
    }
}
namespace EFT.Interactive
{
    public class LootItem(string id)
    {
        public EFT.InventoryLogic.Item Item=new(id);public GameObject gameObject=new();public Transform transform=new();
    }
}
namespace EFT
{
    public class BotOwner { public string ProfileId="pmc";public ActionFlag DoorOpener=new(),Medecine=new();public Patrol PatrollingData=new(); }
    public class ActionFlag { public bool Interacting,Using; }
    public class Patrol { public void Pause(){} }
    public class Player
    {
        public EFT.InventoryLogic.InventoryController InventoryController=new();
        public EFT.InventoryLogic.Inventory Inventory=>InventoryController.Inventory;
        public Movement MovementContext=new();
        public ManagedState CurrentManagedState=>MovementContext.OverridenState??MovementContext.NativeState;
        public int StopInputs;
        public void Move(Vector2 input){StopInputs++;}
        public void UpdateInteractionCast(){}
    }
    public class Movement
    {
        public bool IsGrounded=true;public Action? PickupAction;public int Starts,Stops;
        public ManagedState? OverridenState;
        public ManagedState NativeState,PickUpState,IdleState,MoveState;
        public Movement(){NativeState=IdleState=new(this,true);MoveState=new(this,false);PickUpState=new(this,false);}
    }
    public class ManagedState(Movement movement,bool acceptsPickup)
    {
        public Action? Callback=>movement.PickupAction;
        public int Starts=>movement.Starts;public int Stops=>movement.Stops;
        public void Pickup(bool start,Action? callback)
        {
            if(ReferenceEquals(movement.OverridenState,this)){movement.Stops++;movement.OverridenState=null;return;}
            if(start&&acceptsPickup){movement.Starts++;movement.OverridenState=movement.PickUpState;movement.PickupAction=callback;}
        }
    }
}
namespace SAIN.Preset.Shared.Enums { public enum ECombatDecision { None,Fight } public enum ESquadDecision { None,Help } public enum ESelfActionType { None,Heal } }
namespace SAIN.SAINComponent.Classes.Mover
{
    public interface IBotPathData { }
    public class BotPathDataManual:IBotPathData { public int Stops;public void Stop(bool a,string why){Stops++;} }
    public class Mover
    {
        public IBotPathData? ActivePath;public bool Accept=true;public int Moves;
        public bool WalkToPoint(Vector3 goal){Moves++;if(Accept)ActivePath=new BotPathDataManual();return Accept;}
        public void SetTargetMoveSpeed(float speed){} public void SetTargetPose(float pose){}
    }
}
namespace SAIN.Components
{
    public class Decision
    {
        public SAIN.Preset.Shared.Enums.ECombatDecision CurrentCombatDecision;
        public SAIN.Preset.Shared.Enums.ESquadDecision CurrentSquadDecision;
        public SAIN.Preset.Shared.Enums.ESelfActionType CurrentSelfDecision;
    }
    public class BotComponent
    {
        public bool BotActive=true,IsDead,GameEnding,Danger,Supported=true;
        public BotOwner BotOwner=new();public Player Player=new();public Decision Decision=new();public Mover Mover=new();
        public Vector3 Position;public string ProfileId=>BotOwner.ProfileId;
    }
}
namespace SAIN
{
    public static class SAINEnableClass
    {
        public static BotComponent Bot=new();public static bool GetSAIN(string id,out BotComponent bot){bot=Bot;return id==bot.ProfileId;}
    }
}
namespace Comet.Client
{
    public class Config { public bool Enabled=true;public int Revision=1;public QuickConfig QuickReload=new(); }
    public class QuickConfig { public bool Enabled=true,RecoverDroppedMagazine=true; }
    public class Log { public void LogWarning(string text){} }
    public class Writer { public void Write(string text){} }
    public static class Diagnostics { public static string Serialize(object value)=>System.Text.Json.JsonSerializer.Serialize(value); }
    public static class Api { public static EventBus Decisions=new(); }
    public class EventBus { public List<string> Events=new();public void Publish(string json,Action<Exception> error)=>Events.Add(json); }
    public static class NativeAdapter { public static bool IsSupportedAi(BotComponent bot)=>bot.Supported; }
    public sealed partial class Plugin
    {
        internal sealed class BotState(BotComponent owner)
        {
            public BotComponent Owner=owner;public MagazineRecoveryRuntime MagazineRecovery=new();public bool Retired;
            public string ProfileId=>Owner.ProfileId;public string Name="PMC",Id="instance";
        }
        public static Plugin? Instance;private Dictionary<BotComponent,BotState> states=new();
        private Config? config=new();private object? world=new();private static bool Authority=true;private static bool Sleeping;
        private string session="test";private long sequence;private Writer? diagnostics=new();private bool subscriberWarned;private Log Logger=new();
        private static MethodInfo combatStopPath=typeof(BotPathDataManual).GetMethod("Stop")!;
        private static bool Ghost(string id)=>Sleeping;private static bool PostDanger(BotComponent bot)=>bot.Danger;
    }
}
