using Comet.Core;
using EFT;
using SAIN.Components;
using SAIN.SAINComponent.Classes.Mover;
using UnityEngine;

namespace UnityEngine
{
    public static class Time
    {
        public static double timeAsDouble;
        public static float time => (float)timeAsDouble;
        public static float realtimeSinceStartup => time;
        public static int frameCount;
    }

    public struct Vector3(float x, float y, float z)
    {
        public float x = x,
            y = y,
            z = z;
        public static Vector3 up => new(0, 1, 0);
        public float sqrMagnitude => x * x + y * y + z * z;
        public float magnitude => (float)Math.Sqrt(sqrMagnitude);
        public Vector3 normalized => magnitude > 0 ? this * (1 / magnitude) : default;

        public void Normalize()
        {
            this = normalized;
        }

        public static Vector3 operator +(Vector3 a, Vector3 b) =>
            new(a.x + b.x, a.y + b.y, a.z + b.z);

        public static Vector3 operator -(Vector3 a, Vector3 b) =>
            new(a.x - b.x, a.y - b.y, a.z - b.z);

        public static Vector3 operator *(Vector3 a, float f) => new(a.x * f, a.y * f, a.z * f);

        public static float Distance(Vector3 a, Vector3 b) => (a - b).magnitude;

        public static float Dot(Vector3 a, Vector3 b) => a.x * b.x + a.y * b.y + a.z * b.z;

        public static Vector3 Lerp(Vector3 a, Vector3 b, float t) => a + (b - a) * t;
    }

    public class Transform
    {
        public Vector3 position;
        public Transform? parent;

        public T? GetComponent<T>()
            where T : class => null;
    }

    public class Collider
    {
        public object? Parent;
        public Transform transform = new();

        public T? GetComponentInParent<T>()
            where T : class => Parent as T;

        public T? GetComponent<T>()
            where T : class => Parent as T;
    }

    public struct RaycastHit
    {
        public Collider collider;
    }

    public enum QueryTriggerInteraction
    {
        Ignore,
    }

    public static class Mathf
    {
        public static float Clamp01(float f) => Math.Clamp(f, 0, 1);
    }

    public static class Physics
    {
        public static bool Low = true,
            High,
            Volume,
            Sweep;
        public static Func<Vector3, bool>? VolumeAt;
        public static Func<Vector3, Vector3, bool> Hidden = (_, _) => true;
        public static Collider LowCollider = new(),
            SightCollider = new();
        public static int Queries;

        public static bool Raycast(
            Vector3 p,
            Vector3 d,
            out RaycastHit h,
            float l,
            int m,
            QueryTriggerInteraction q
        )
        {
            Queries++;
            h = new() { collider = LowCollider };
            return Low;
        }

        public static bool Raycast(Vector3 p, Vector3 d, float l, int m, QueryTriggerInteraction q)
        {
            Queries++;
            return High;
        }

        public static bool CapsuleCast(
            Vector3 a,
            Vector3 b,
            float r,
            Vector3 d,
            float l,
            int m,
            QueryTriggerInteraction q
        )
        {
            Queries++;
            return Sweep;
        }

        public static int OverlapCapsuleNonAlloc(
            Vector3 a,
            Vector3 b,
            float r,
            Collider[] hits,
            int m,
            QueryTriggerInteraction q
        )
        {
            Queries++;
            if (Volume || VolumeAt?.Invoke(a) == true)
            {
                hits[0] = new();
                return 1;
            }
            return 0;
        }

        public static bool Linecast(Vector3 a, Vector3 b, int m, QueryTriggerInteraction q)
        {
            Queries++;
            return Hidden(a, b);
        }

        public static bool Linecast(
            Vector3 a,
            Vector3 b,
            out RaycastHit h,
            int m,
            QueryTriggerInteraction q
        )
        {
            Queries++;
            h = new() { collider = SightCollider };
            return Hidden(a, b);
        }
    }
}

namespace UnityEngine.AI
{
    public struct NavMeshHit
    {
        public Vector3 position;
    }

    public static class NavMesh
    {
        public const int AllAreas = -1;
        public static bool Available = true,
            Blocked;

        public static bool SamplePosition(Vector3 p, out NavMeshHit h, float d, int a)
        {
            h = new() { position = p };
            return Available;
        }

        public static bool Raycast(Vector3 a, Vector3 b, out NavMeshHit h, int c)
        {
            h = default;
            return Blocked;
        }
    }
}

namespace Comfort.Common
{
    public static class Singleton<T>
        where T : new()
    {
        public static T Instance = new();
    }
}

namespace EFT
{
    public class GameWorld
    {
        public List<Player> AllAlivePlayersList = new();
    }

    public class Player
    {
        public Vector3 Position;
        public bool IsAI = true,
            IsInPronePose;
        public Movement MovementContext = new();
        public Bones PlayerBones = new();
        public Hands HandsController = new();
        public int Teleports;

        public void Teleport(Vector3 p)
        {
            Teleports++;
            Position = p;
        }
    }

    public class Movement
    {
        public bool IsGrounded = true;
    }

    public class Bones
    {
        public Transform Head = new();
    }

    public class Hands
    {
        public bool Busy;

        public bool IsInInteractionStrictCheck() => Busy;
    }

    public static class LayersMaskController
    {
        public const int PlayerStaticDoorMask = 1,
            HighPolyWithTerrainMask = 2;
    }

    public class BotOwner
    {
        public NativeMover Mover = new();
        public Weapons WeaponManager = new();
        public Medical Medecine = new();
        public DoorOpener DoorOpener = new();
        public Shoot ShootData = new();
    }

    public class NativeMover
    {
        public bool IsMoving = true,
            HasPathAndNoComplete = true;
        public NativeController ActualPathController = new();
        public int Repaths,
            Stops;

        public void RecalcWay()
        {
            Repaths++;
        }

        public void Stop()
        {
            Stops++;
            IsMoving = false;
        }
    }

    public class NativeController
    {
        public NativePath CurPath = new();
    }

    public class NativePath
    {
        public int Length => 2;
        public int CurIndex = 1;

        public Vector3 GetPoint(int i) => new(i * 2, 0, 0);
    }

    public class Medical
    {
        public bool Using;
    }

    public class DoorOpener
    {
        public bool Interacting;
    }

    public class Shoot
    {
        public bool Shooting;
    }

    public class Weapons
    {
        public Reload Reload = new();
        public Selector Selector = new();
    }

    public class Reload
    {
        public bool Reloading;
    }

    public class Selector
    {
        public bool IsChanging;
    }
}

namespace EFT.Interactive
{
    public class Door
    {
        public string Id = "door-test",
            DoorState = "Shut";
    }
}

namespace EFT.Ballistics
{
    public enum MaterialType
    {
        Glass,
        GlassShattered,
        Concrete,
    }

    public class BaseBallistic { }

    public class BallisticCollider : BaseBallistic
    {
        public MaterialType TypeOfMaterial;
    }
}

namespace SAIN.Models.Enums { }

namespace SAIN.Preset.Shared.Enums
{
    public enum ESelfActionType
    {
        None,
        Heal,
    }
}

namespace SAIN.SAINComponent.Classes.Mover
{
    public enum EBotMoveStatus
    {
        Moving,
        Complete,
        Paused,
        DoorInteraction,
    }

    public class Corner
    {
        public Vector3 Position = new(2, 0, 0);
    }

    public interface IBotPathData
    {
        bool Moving { get; }
        Vector3 Destination { get; }
        EBotMoveStatus Status { get; }
        List<Corner> PathCorners { get; }
        int CurrentIndex { get; }
    }

    public class BotPathDataManual : IBotPathData
    {
        public bool CancelRequested,
            PausedRequested;
        public bool Moving { get; set; } = true;
        public Vector3 Destination { get; set; } = new(2, 0, 0);
        public EBotMoveStatus Status { get; set; }
        public List<Corner> PathCorners { get; } = new() { new() };
        public int CurrentIndex => 0;
    }

    public class Mover
    {
        public IBotPathData ActivePath = new BotPathDataManual();
        public int Repaths,
            Vaults,
            Jumps,
            Stops;
        public bool Vault,
            Jump = true;

        public void RecalcPath()
        {
            Repaths++;
        }

        public bool TryVault()
        {
            Vaults++;
            return Vault;
        }

        public bool TryJump()
        {
            Jumps++;
            return Jump;
        }

        public void Stop()
        {
            Stops++;
            ((BotPathDataManual)ActivePath).Moving = false;
        }
    }
}

namespace SAIN.Components
{
    public class BotComponent
    {
        public Player Player = new();
        public Vector3 Position => Player.Position;
        public string ProfileId = "pmc";
        public bool Pmc = true,
            BotActive = true,
            IsDead,
            GameEnding,
            SAINLayersActive = true;
        public BotOwner BotOwner = new();
        public Mover Mover = new();
        public Decisions Decision = new();
        public Enemy? GoalEnemy = new();
        public DoorOpener DoorOpener = new();
        public Shoot ManualShoot = new();
        public CoverState Cover = new();
        public SearchState Search = new();

        public void ManualUpdate() { }
    }

    public class CoverData
    {
        public bool IsBad;
    }

    public class CoverPoint
    {
        public Vector3 Position = new(8, 0, 0);
        public CoverData CoverData = new();
    }

    public class CoverState
    {
        public CoverPoint? CoverInUse,
            CoverPoint_MovingTo;
        public int Releases;

        public void StopSeekingCover()
        {
            Releases++;
            CoverInUse = CoverPoint_MovingTo = null;
        }
    }

    public class Decisions
    {
        public string CurrentCombatDecision = "Search";
        public SAIN.Preset.Shared.Enums.ESelfActionType CurrentSelfDecision;
    }

    public class Enemy
    {
        public string EnemyProfileId = "known-enemy";
    }

    public class SearchState
    {
        public string CurrentState = "Move";
    }
}

namespace HarmonyLib
{
    public class Harmony
    {
        public void Patch(System.Reflection.MethodInfo m, HarmonyMethod postfix) { }
    }

    public class HarmonyMethod(Type t, string n) { }

    public static class AccessTools
    {
        public static System.Reflection.MethodInfo Method(Type t, string n) => t.GetMethod(n)!;
    }
}

namespace Comet.Api
{
    public static class Decisions
    {
        public static List<string> Events = new();

        public static void Publish(string s, Action<Exception> error) => Events.Add(s);
    }
}

namespace Comet.Client
{
    internal static class NativeAdapter
    {
        internal static bool IsSupportedAi(BotComponent b) => b.Pmc && b.Player.IsAI;
    }

    internal static class ProgressAdapter
    {
        internal static string CoverKey(CoverPoint p) => Destination(p.Position);

        internal static string Destination(Vector3 p) => $"{p.x}/{p.y}/{p.z}";
    }

    internal class Diagnostics
    {
        internal static string Serialize(object value) =>
            Newtonsoft.Json.JsonConvert.SerializeObject(value, Json);

        internal static Newtonsoft.Json.JsonSerializerSettings Json = new()
        {
            ContractResolver =
                new Newtonsoft.Json.Serialization.CamelCasePropertyNamesContractResolver(),
        };

        internal void Write(string s) { }
    }

    internal class DummyPolicy
    {
        public bool Active;
        public DummyTrace? Trace;
    }

    internal class DummyTrace
    {
        public string State = "";
    }

    internal class DummyRuntime
    {
        public DummyPolicy Policy = new();
    }

    internal sealed class BotState
    {
        internal MovementRecoveryRuntime MovementRecovery = new();
        internal DummyRuntime Fake = new(),
            Entry = new(),
            RoomClear = new();
        internal PlanProgress Progress = new(new());
        internal string ActiveLabel = "",
            Decision = "Search",
            ProfileId = "pmc",
            Name = "Test PMC",
            Id = "instance";

        internal BotState(BotComponent b, CometConfig c, string session) { }
    }

    internal class DoorSwing
    {
        public BotComponent Bot = null!;
    }

    public sealed partial class Plugin
    {
        internal static Plugin? Instance;
        internal CometConfig? config = new();
        internal bool Authority = true,
            Ghosted,
            fikaPresent;

        internal bool Ghost(string id) => Ghosted;

        internal Dictionary<BotComponent, BotState> states = new();
        internal List<DoorSwing> doorSwings = new();
        internal string session = "c06-test";
        internal long sequence;
        private Diagnostics? diagnostics;
        private bool subscriberWarned;

        internal class Log
        {
            public void LogWarning(string s) => Console.WriteLine(s);
        }

        internal Log Logger = new();
        internal int Releases;

        internal void Release(BotState s, string reason)
        {
            Releases++;
            s.ActiveLabel = "";
        }

        internal void Tick(BotComponent b, BotState s, double at)
        {
            Time.timeAsDouble = at;
            Time.frameCount++;
            SampleMovementRecovery(b, s);
        }

        internal bool Owns(BotState s) => RecoveryOwnsStall(s);

        internal void Enroll(BotComponent b)
        {
            Instance = this;
            TrackMovementRecovery(b);
        }
    }
}
