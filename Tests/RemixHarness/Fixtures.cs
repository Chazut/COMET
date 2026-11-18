using System.Reflection;
using System.Reflection.Emit;
using EFT;
using SAIN.Components;
using SAIN.Components.BotControllerSpace.Classes;
using SAIN.Models.PlayerData;
using SAIN.Preset;
using SAIN.Preset.Shared.Enums;
using SAIN.SAINComponent;
using SAIN.SAINComponent.Classes.Talk;
using SAIN.SAINComponent.Classes.WeaponFunction;
using UnityEngine;
using UnityEngine.AI;

// Small game objects for exercising the production callbacks. The separate
// IntegrationHarness checks their real signatures and the instructions they replace.
namespace HarmonyLib
{
    public class Harmony { public void Patch(MethodInfo original, HarmonyMethod? prefix = null, HarmonyMethod? postfix = null, HarmonyMethod? transpiler = null) => throw new NotSupportedException(); }
    public class HarmonyMethod { public HarmonyMethod(Type type, string name) {} }
    public class CodeInstruction(OpCode code, object? argument) { public OpCode opcode=code; public object? operand=argument; }
    public static class AccessTools
    {
        const BindingFlags Flags = BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
        public static MethodInfo Method(Type t, string s, Type[]? args = null) => args == null ? t.GetMethod(s,Flags)! : t.GetMethod(s,Flags,args)!;
        public static MethodInfo DeclaredMethod(Type t, string s) => t.GetMethod(s,Flags|BindingFlags.DeclaredOnly)!;
        public static MethodInfo PropertySetter(Type t,string s) => t.GetProperty(s,Flags)!.GetSetMethod(true)!;
    }
}
namespace UnityEngine
{
    public static class Time { public static float time = 1; }
    public class Object { public static void Destroy(Object component) {} }
    public class MonoBehaviour : Object {}
    public class GameObject { public T AddComponent<T>() where T : MonoBehaviour,new() => new(); }
    public record struct Vector3(float x,float y,float z)
    {
        public static Vector3 operator -(Vector3 a,Vector3 b) => new(a.x-b.x,a.y-b.y,a.z-b.z);
        public readonly float sqrMagnitude => x*x+y*y+z*z;
    }
    public class Transform
    {
        public Transform? parent;
        public Vector3 position;
        public EFT.Interactive.Door? Door;
        public List<Transform> Children = new();
        public int childCount => Children.Count;
        public Transform GetChild(int i) => Children[i];
        public T? GetComponent<T>() where T:class => Door as T;
        public Transform AddChild() { var child = new Transform { parent=this }; Children.Add(child); return child; }
    }
    public class Collider(Transform node,int id)
    {
        public Transform transform = node;
        public Bounds bounds;
        public int GetInstanceID() => id;
        public T? GetComponentInParent<T>() where T:class
        {
            for (var p=transform;p!=null;p=p.parent) if (p.GetComponent<T>() is T found) return found;
            return null;
        }
    }
    public struct Bounds { public Vector3 center; }
}
namespace UnityEngine.AI
{
    public struct NavMeshHit { public Vector3 position; }
    public sealed class NavMeshPath { }
    public static class NavMesh
    {
        public static bool Raycast(Vector3 a,Vector3 b,out NavMeshHit hit,int mask){hit=default;return true;}
        public static bool CalculatePath(Vector3 a,Vector3 b,int mask,NavMeshPath p)=>true;
    }
}
namespace EFT { public class Player {} public enum EPhraseTrigger { OnDeath } public enum ETagStatus { Healthy, BadlyInjured, Dying } }
namespace EFT.Ballistics { public sealed class Shot {} }
namespace EFT.Interactive { public class Door(Transform node) { public Transform transform=node; } }
public static class MyExtensions { public static int Rolls; public static bool Result; public static bool RandomBool(float chance){Rolls++;return Result;} }
namespace SAIN { public static class SAINPlugin { public static SAINPresetClass LoadedPreset = new(); } }
namespace SAIN.Preset { public class SAINPresetClass { public object GlobalSettings=new(); } }
namespace SAIN.Preset.Shared.Enums { public enum ECombatDecision { None, Search } }
namespace SAIN.Models.PlayerData { public class PlayerNavData { public Vector3 Position {get;set;} public NavMeshHit LastValidHit; } }
namespace SAIN.Extensions { public static class PlayerDataExtensions {} }
namespace SAIN.SAINComponent { public class BotBase(BotComponent bot){public BotComponent Bot=bot;} }
namespace SAIN.SAINComponent.Classes.Talk
{
    public class EnemyTalk(BotComponent bot):BotBase(bot)
    {
        public int Applied;
        private void UpdatePresetSettings(SAINPresetClass preset) { Applied++; }
    }
}
namespace SAIN.SAINComponent.Classes.WeaponFunction
{
    public class GrenadeThrowDecider(BotComponent bot):BotBase(bot)
    {
        public int Applied;
        private void UpdatePresetSettings(SAINPresetClass preset) { Applied++; }
    }
}
namespace SAIN.Components
{
    public class BotComponent
    {
        public bool Supported=true;
        public string ProfileId="test";
        public Cover Cover=new();
        public Enemy? GoalEnemy;
        public Squad Squad=new();
        public Decision Decision=new();
        public NavTransform Transform=new();
        public Info Info=new();
        public Grenades Grenade;
        public Talk Talk;
        public Memory Memory=new();
        public Owner BotOwner=new();
        public BotComponent() { Talk=new(this); Grenade=new(this); }
    }
    public class Cover
    {
        public List<Point> CoverPoints=new();
        public Point? CoverInUse, CoverPoint_MovingTo;
    }
    public record Point(Collider Collider);
    public class Enemy { public Vector3 EnemyPosition; }
    public class Squad { public SquadInfo SquadInfo=new(); public bool BotInGroup; }
    public class SquadInfo { public BotComponent? LeaderComponent; }
    public class Decision { public ECombatDecision CurrentCombatDecision; }
    public class NavTransform { public PlayerNavData NavData=new(); }
    public class Info { public Settings FileSettings=new(); }
    public class Settings { public Mind Mind=new(); }
    public class Mind { public bool BotTaunts; }
    public class Grenades(BotComponent b) { public GrenadeThrowDecider GrenadeThrowDecider=new(b); }
    public class Talk(BotComponent b)
    {
        public EnemyTalk EnemyTalk=new(b); public GroupTalk GroupTalk=new(); public bool CanTalk=true;
        public int Said; public void Say(EPhraseTrigger trigger)=>Said++;
    }
    public class GroupTalk { public int Applied; public void updateConfigSettings(SAINPresetClass preset)=>Applied++; }
    public class Memory { public Health Health=new(); }
    public class Health { public ETagStatus HealthStatus; }
    public class Owner { public Vector3 Position; }
    public class BotManagerComponent
    {
        public static BotManagerComponent? Instance {get;set;}
        public object GameWorld=new();
        public UnityEngine.GameObject gameObject=new();
        public Dictionary<int,BotComponent> Bots=new();
        public BotHearingClass BotHearing=new();
    }
}
namespace SAIN.Components.BotControllerSpace.Classes
{
    public class BotHearingClass
    {
        public event Action<EFT.Ballistics.Shot>? BulletImpact;
        public event Action<EPhraseTrigger,ETagStatus,Player>? PlayerTalk;
        public void Emit(){BulletImpact?.Invoke(new());PlayerTalk?.Invoke(default,default,new());}
    }
    public class SAINWeatherClass(BotManagerComponent owner) { public static SAINWeatherClass? Instance {get;set;} public BotManagerComponent BotController=owner; }
}
namespace SAIN.Components.BotController { public class BotSpawnController(BotManagerComponent owner) { public static BotSpawnController? Instance {get;set;} public BotManagerComponent BotController=owner; } }
namespace SAIN.SAINComponent.Classes.Decision
{
    public class SquadDecisionClass(BotComponent b):BotBase(b)
    {
        private bool doesMemberShareEnemy(BotComponent member) => ReferenceEquals(Bot.GoalEnemy,member.GoalEnemy);
    }
}
namespace SAIN.Classes.Bot.Sense.Hearing { public class HearingInput(BotComponent b):BotBase(b) {} }
namespace SAIN.SAINComponent.Classes.Mover { public class BotPathDataManual {} public class SAINMoverClass {} public class DogFight(BotComponent b):BotBase(b) {} }
namespace SAIN.SAINComponent.Classes.EnemyClasses { public class SAINEnemyPath {} }
namespace SAIN.Layers.Combat.Solo { public class StandAndShootAction {} }
namespace SAIN.SAINComponent.SubComponents.CoverFinder { public class CoverAnalyzer(BotComponent b):BotBase(b) {} }
namespace Comet.Client
{
    internal static class SourceSoundWatch { internal static void Detach(BotHearingClass source) {} }
    internal static class SourceCoverAccess
    {
        internal static void Forget(Cover cover,Point point)
        {
            if (ReferenceEquals(cover.CoverInUse,point)) cover.CoverInUse=null;
            if (ReferenceEquals(cover.CoverPoint_MovingTo,point)) cover.CoverPoint_MovingTo=null;
        }
    }
    internal static class NativeAdapter { internal static bool IsSupportedAi(BotComponent bot)=>bot.Supported; }
    public sealed partial class Plugin
    {
        private static Plugin? Instance;
        private object? world;
        private sealed class Config { internal bool Enabled=true; }
        private Config? config=new();
        private bool Authority=true;
        private bool NativeFixScope(BotComponent bot)=>world!=null && config?.Enabled==true && Authority && bot.Supported;
        partial void ClearRemixRaidState();
        private enum NativeFix { OffMeshStart, DoorCoverRejected, WoundVoiceRoll, FollowSearchingLeader, SpawnPreset }
        private Dictionary<NativeFix,int> counts=new();
        private void CountNativeFix(NativeFix kind,BotComponent bot)=>counts[kind]=counts.GetValueOrDefault(kind)+1;
        private sealed class Log { public void LogDebug(string message) {} public void LogWarning(string message) {} }
        private Log Logger=new();
    }
}
