// Executes both production G01/G02 adapters against deterministic native contracts. Does not simulate Unity physics or Fika networking.
using Comet.Core;
using EFT;
using EFT.InventoryLogic;
using SAIN.Components;
using SAIN.SAINComponent.Classes.EnemyClasses;
using SAIN.SAINComponent.Classes.WeaponFunction;
using UnityEngine;

namespace UnityEngine
{
    public static class Time
    {
        public static double timeAsDouble;
        public static int frameCount;
        public static float time => (float)timeAsDouble;
        public static float realtimeSinceStartup => time;
    }

    public struct Vector3
    {
        public float x,
            y,
            z;

        public Vector3(float x, float y, float z)
        {
            this.x = x;
            this.y = y;
            this.z = z;
        }

        public static Vector3 zero => new();
        public static Vector3 up => new(0, 1, 0);
        public float sqrMagnitude => x * x + y * y + z * z;
        public float magnitude => (float)Math.Sqrt(sqrMagnitude);
        public Vector3 normalized => magnitude > 0 ? this / magnitude : zero;

        public static Vector3 operator +(Vector3 a, Vector3 b) =>
            new(a.x + b.x, a.y + b.y, a.z + b.z);

        public static Vector3 operator -(Vector3 a, Vector3 b) =>
            new(a.x - b.x, a.y - b.y, a.z - b.z);

        public static Vector3 operator -(Vector3 a) => zero - a;

        public static Vector3 operator *(Vector3 a, float k) => new(a.x * k, a.y * k, a.z * k);

        public static Vector3 operator /(Vector3 a, float k) => a * (1 / k);

        public static float Dot(Vector3 a, Vector3 b) => a.x * b.x + a.y * b.y + a.z * b.z;

        public static Vector3 Cross(Vector3 a, Vector3 b) =>
            new(a.y * b.z - a.z * b.y, a.z * b.x - a.x * b.z, a.x * b.y - a.y * b.x);

        public static float Distance(Vector3 a, Vector3 b) => (a - b).magnitude;
    }

    public static class Mathf
    {
        public const float Deg2Rad = (float)Math.PI / 180;

        public static float Lerp(float a, float b, float t) => a + (b-a) * Math.Clamp(t,0,1);
        public static float InverseLerp(float a, float b, float v) => Math.Clamp((v-a)/(b-a),0,1);
        public static float Tan(float x) => (float)Math.Tan(x);

        public static float Cos(float x) => (float)Math.Cos(x);
    }

    public enum QueryTriggerInteraction
    {
        Ignore,
    }

    public struct RaycastHit { }

    public static class Physics
    {
        public static bool Linecast(Vector3 a, Vector3 b, int mask) => false;

        public static bool BlockArc,
            BlockLanding,
            BlockMargin,
            BlastShield;
        public static int Sweeps;

        public static bool CheckSphere(Vector3 v, float r, int mask, QueryTriggerInteraction q) =>
            BlockLanding;

        public static bool SphereCast(
            Vector3 a,
            float r,
            Vector3 d,
            out RaycastHit hit,
            float distance,
            int mask,
            QueryTriggerInteraction q
        )
        {
            hit = new();
            Sweeps++;
            return BlockArc || BlockMargin && r > .1f;
        }
    }
}

namespace HarmonyLib
{
    public class Harmony
    {
        public void Patch(object? m, HarmonyMethod? prefix = null, HarmonyMethod? postfix = null)
        {
            if (m == null)
                throw new Exception("missing native contract");
        }
    }

    public class HarmonyMethod
    {
        public int priority;

        public HarmonyMethod(Type t, string n) { }
    }

    public static class Priority
    {
        public const int Last = 0;
    }

    public static class AccessTools
    {
        public static object? Method(Type t, string n, Type[]? types = null) =>
            t.GetMethod(
                n,
                System.Reflection.BindingFlags.Public
                    | System.Reflection.BindingFlags.NonPublic
                    | System.Reflection.BindingFlags.Instance
            );
    }
}

namespace EFT.InventoryLogic
{
    public class Item
    {
        public string Id = "nade",
            TemplateId = "tpl-frag";
    }

    public class ThrowWeap : Item
    {
        public float EmitTime;
        public int FragmentsCount = 70;
        public Vector3 Blindness,
            Contusion;
        public string ShortName = "Test frag";
        public bool IsDummy;
        public string ThrowType = "frag_grenade";
        public float MinTimeToContactExplode = -1;
        public float GetExplDelay = 3,
            MaxExplosionDistance = 6;
    }
}

namespace EFT
{
    public enum EnvironmentType
    {
        Indoor,
        Outdoor,
    }

    public enum EPlayerItems
    {
        Equipment,
    }

    public class Inventory
    {
        public List<Item> Items = new();

        public IEnumerable<Item> GetPlayerItems(EPlayerItems type) => Items;
    }

    public class Player
    {
        public object? HandsController;
        public EnvironmentType Environment = EnvironmentType.Outdoor;
        public string ProfileId = "player";
        public Inventory Inventory = new();
        public Vector3 Position;
        public Health HealthController = new();
    }

    public class Health
    {
        public bool IsAlive = true;
    }

    public class Grenade
    {
        public string ProfileId = "bot";
        public Item? WeaponSource;
    }
}

public class AIGreanageThrowData
{
    public string? GrenadeType = "frag_grenade";
    public bool ThrowComplete;
    public bool CanThrow = true,
        Fresh = true;
    public Vector3 Target = new(20, 0, 0),
        Position;
    public float Ang = 25,
        Force = 15,
        CreatedTime = Time.time;

    public bool IsUpToDate() => Fresh && Time.time - CreatedTime < 1;
}

public enum AIGreandeAng
{
    ang5 = 5,
    ang15 = 15,
    ang25 = 25,
    ang35 = 35,
    ang45 = 45,
}

public static class AIGrenadeHelper
{
    public static int Calls;
    public static Func<Vector3, int, bool> Allowed = (v, a) => true;

    public static AIGreanageThrowData CanThrowGrenade2(
        Vector3 from,
        Vector3 target,
        float power,
        AIGreandeAng angle,
        float min,
        float percent
    )
    {
        Calls++;
        return new()
        {
            Position = from,
            Target = target,
            Ang = (int)angle,
            CanThrow = Allowed(target, (int)angle),
        };
    }

    public static float YByX(float x, float power, float tan, float cos) => 0; // tests gates/ownership, not projectile flight
}

public static class LayersMaskController
{
    public const int HighPolyWithTerrainMask = 1;
}

namespace UnityEngine.AI
{
    public enum NavMeshPathStatus
    {
        PathComplete,
        PathPartial,
    }

    public class NavMeshPath
    {
        public Vector3[] corners = Array.Empty<Vector3>();
        public NavMeshPathStatus status;
    }

    public struct NavMeshHit
    {
        public Vector3 position;
    }

    public static class NavMesh
    {
        public const int AllAreas = -1;
        public static bool NoLanding,
            NoRoute,
            Detour;

        public static bool SamplePosition(Vector3 v, out NavMeshHit hit, float range, int mask)
        {
            hit = new() { position = new(v.x, 0, v.z) };
            return !NoLanding;
        }

        public static bool CalculatePath(Vector3 from, Vector3 to, int mask, NavMeshPath p)
        {
            p.corners = Detour ? new[] { from, new Vector3(90, 0, 90), to } : new[] { from, to };
            p.status = NoRoute ? NavMeshPathStatus.PathPartial : NavMeshPathStatus.PathComplete;
            return !NoRoute;
        }
    }
}

namespace SAIN.SAINComponent.Classes.Mover
{
    public class DoorLink
    {
        public object? Door = new();
        internal Comet.Core.DoorGeometry Geometry = new() { Id = "door", State = "Open", Center = new(8, 0, 0), Normal = new(1, 0, 0), Axis = new(0, 0, 1), HalfWidth = 1 };
    }

    public class DoorDataStruct
    {
        public DoorLink Link;

        public DoorDataStruct(DoorLink link)
        {
            Link = link;
        }
    }
}

namespace SAIN.Preset.Shared.Enums
{
    public enum ETagStatus
    {
        Healthy,
        BadlyInjured,
        Dying,
    }
}

namespace SAIN.SAINComponent.Classes.EnemyClasses
{
    public class EnemyPlace
    {
        public Vector3 Position = new(20, 0, 0);
    }

    public class Places
    {
        public EnemyPlace? LastKnownPlace,
            LastSeenPlace,
            LastHeardPlace;
        public float TimeLastKnownUpdated = 10;
        public Vector3? LastKnownPosition => LastKnownPlace?.Position;
    }

    public class Enemy
    {
        public Status Status = new();
        public PathData Path = new();
        public Vector3? VisiblePathPoint;
        public string EnemyProfileId = "enemy";
        public Places KnownPlaces = new();
        public bool IsVisible = true;
        public Player EnemyPlayer = new();

        public Enemy()
        {
            KnownPlaces.LastKnownPlace = KnownPlaces.LastSeenPlace = new();
        }
    }
}

namespace SAIN.SAINComponent.Classes.WeaponFunction
{
    public class GrenadeThrowDecider
    {
        public BotComponent Bot = null!;

        private bool FindThrowTarget(Enemy e) => true;

        private bool TryThrowGrenade() => true;
    }
}

namespace SAIN.Components
{
    public class Grenades
    {
        public string GrenadeActionType = "ready";
        public bool ThrowindNow;
        public int Selections;

        public void SetThrowParams(ThrowWeap item)
        {
            grenade = item;
            Selections++;
        }

        public float MaxPower = 30;
        public ThrowWeap? grenade = new();
        public AIGreanageThrowData? AIGreanageThrowData = new();

        public bool SetThrowData(AIGreanageThrowData data)
        {
            AIGreanageThrowData = data;
            return data.CanThrow;
        }
    }

    public class Weapons
    {
        public Grenades Grenades = new();
    }

    public class Medicine
    {
        public bool Using;
    }

    public class OwnerMemory
    {
        public bool IsUnderFire;
    }

    public class Group
    {
        public Func<Player, bool>? Enemy;

        public bool IsEnemy(Player p) => Enemy?.Invoke(p) ?? false;
    }

    public class Voxel
    {
        public List<SAIN.SAINComponent.Classes.Mover.DoorLink> DoorLinks = new();
    }

    public class VoxelData
    {
        public Voxel? CurVoxel = new();
    }

    public class Owner
    {
        public string ProfileId = "bot";
        public VoxelData VoxelesPersonalData = new();
        public Weapons WeaponManager = new();
        public Medicine Medecine = new();
        public OwnerMemory Memory = new();
        public Group? BotsGroup = new();
    }

    public class HealthMemory
    {
        public SAIN.Preset.Shared.Enums.ETagStatus HealthStatus;
    }

    public class Location { public bool IsIndoors; }
    public class TestInfo { public TestProfile Profile = new(); }
    public class TestProfile { public float DifficultyModifier = .5f; }
    public class Memory
    {
        public Location Location = new();
        public HealthMemory Health = new();
    }

    public class Suppression
    {
        public bool IsHeavySuppressed;
    }

    public class Flashed
    {
        public bool IsFlashed;
    }

    public class BotGrenade
    {
        public object? GrenadeDangerPoint;
    }

    public class EnemyController
    {
        public List<Enemy> VisibleEnemies = new();
    }

    public class WeaponTransform
    {
        public Vector3 WeaponRoot;
    }

    public class Transform
    {
        public WeaponTransform WeaponData = new();
    }

    public class BotComponent
    {
        public TestInfo Info = new();
        public bool Pmc = true,
            BotActive = true,
            IsDead,
            GameEnding;
        public string ProfileId = "bot";
        public Player Player = new();
        public Owner BotOwner = new();
        public Memory Memory = new();
        public Suppression Suppression = new();
        public Flashed Flashed = new();
        public BotGrenade Grenade = new();
        public Transform Transform = new();
        public Enemy? GoalEnemy = new();
        public EnemyController EnemyController = new();
        public Vector3 Position => Player.Position;

        public void ManualUpdate() { }

        public BotComponent()
        {
            Player.Inventory.Items.Add(BotOwner.WeaponManager.Grenades.grenade!);
            Player.Inventory.Items.Add(new ThrowWeap { Id = "spare" });
        }
    }
}

namespace Comet.Core
{
    public static class ImpactReaction
    {
        public static bool Finite(double v) => double.IsFinite(v);
    }

    public class CometConfig
    {
        public bool Enabled = true;
        public long Revision = 1;
        public double GrenadeContactLifetime = GrenadeJudgmentConfig.DefaultMaxInfoAge;
        public GrenadeFilterConfig GrenadeFilter = new() { Enabled = false };
        public GrenadeChoiceConfig GrenadeChoice = new();
        public DoorApproachConfig DoorApproach = new();
        public GrenadeJudgmentConfig GrenadeJudgment = new();
        public GrenadeTrajectoryConfig GrenadeTrajectory = new() { Enabled = true };
        public object Opportunity = new();
        public object EnvironmentContext = new();

        public static void Range(double v, double lo, double hi, string n)
        {
            if (!double.IsFinite(v) || v < lo || v > hi)
                throw new ArgumentException(n);
        }
    }


    public class EnvironmentTrace
    {
        public string Ceiling = "clear-to-limit";
        public double? CeilingHeight;
    }

    public class EnvironmentContext
    {
        public EnvironmentTrace Value = new();

        public EnvironmentTrace Read(
            double now,
            float x,
            float y,
            float z,
            object config,
            string purpose
        ) => Value;
    }

    public static class DoorRoomClear
    {
        public static bool Valid(double v) => double.IsFinite(v) && v >= 0;
    }

    public class OpportunityTrace
    {
        public string State = "unknown",
            Cue = "";
    }

    public class OpportunityMemory
    {
        public OpportunityTrace Value = new();

        public OpportunityTrace Snapshot(string id, int instance, double now, object c) => Value;
    }
}

namespace Comet.Client
{
    internal class Diagnostics
    {
        internal static string Serialize(object value) =>
            Newtonsoft.Json.JsonConvert.SerializeObject(value, Json);

        internal static Newtonsoft.Json.JsonSerializerSettings Json = new()
        {
            ContractResolver =
                new Newtonsoft.Json.Serialization.CamelCasePropertyNamesContractResolver(),
        };
        internal List<string> Events = new();

        internal void Write(string value) => Events.Add(value);
    }

    internal class Log
    {
        internal void LogWarning(string s) { }
    }

    internal static class NativeAdapter
    {
        internal static bool FriendsClear = true;

        internal static bool GrenadeFriendsClear(BotComponent b, Vector3 p) => FriendsClear;

        internal static void ContinueGrenadeSearch(BotComponent b, BotState s, CometConfig c)
        {
            Plugin.Instance!.Find(b);
        }

        internal static bool IsSupportedAi(BotComponent b) => b.Pmc && !b.IsDead;
    }

    internal class FakeRuntime
    {
        internal FakePolicy Policy = new();
        internal bool CallbackPending;
    }

    internal class FakePolicy
    {
        internal bool Active;
    }

    internal class DoorWork
    {
        internal SAIN.SAINComponent.Classes.Mover.DoorDataStruct Data = null!;
        internal DoorGeometry Geometry = new();
    }


    internal class BotState
    {
        internal GrenadeFilterRuntime GrenadeFilter = new();
        internal GrenadeChoiceRuntime GrenadeChoice = new();
        internal EnvironmentContext Environment = new();
        internal GrenadeTrajectoryRuntime GrenadeTrajectory = new();
        internal GrenadeJudgmentRuntime GrenadeJudgment = new();
        internal OpportunityMemory Opportunity = new();
        internal FakeRuntime Fake = new();
        internal string ProfileId,
            Name = "Test PMC",
            Id = "instance";

        internal BotState(BotComponent b, CometConfig c, string session)
        {
            ProfileId = b.ProfileId;
        }
    }

    internal class World
    {
        internal List<Player> RegisteredPlayers = new();
    }

    namespace Api
    {
        internal static class Decisions
        {
            internal static void Publish(string json, Action<Exception> error) { }
        }
    }

    public sealed partial class Plugin
    {
        internal static Plugin? Instance;
        private CometConfig? config = new();
        private Dictionary<BotComponent, BotState> states = new();
        private World? world = new();
        private Diagnostics? diagnostics = new();
        private Log Logger = new();
        private bool subscriberWarned;
        private long sequence;
        private string session = "harness";
        internal bool Authority = true,
            IsGhost,
            FeintGuard,
            EventsAvailable = true;
        private object? clearEvents;

        private bool Ghost(string id) => IsGhost;

        private static bool FakeGuarded(BotComponent b) => Instance!.FeintGuard;

        private class Rng : Random
        {
            internal int Draws;

            public override double NextDouble()
            {
                Draws++;
                return .1;
            }
        }

        private Rng jumpRandom = new();

        private static DoorPoint DP(Vector3 v) => new(v.x, v.y, v.z);

        private static double ClearDangerRadius(ThrowWeap item) =>
            Math.Max(
                6,
                Math.Max(item.MaxExplosionDistance, Math.Max(item.Blindness.y, item.Contusion.y))
            ) + 1;

        private static bool ClearBlastProtected(Vector3 pos, Vector3 target, double radius) =>
            Physics.BlastShield || Vector3.Distance(pos, target) > radius;

        private void RefreshEnvironment(BotComponent b, BotState s, bool ceiling) { }

        private static Vector3 VP(DoorPoint p) => new((float)p.X, (float)p.Y, (float)p.Z);

        private static bool TryDoorGeometry(
            SAIN.SAINComponent.Classes.Mover.DoorDataStruct data,
            out DoorGeometry g
        )
        {
            g = data.Link.Geometry;
            return true;
        }

        private static float Length(Vector3[] corners)
        {
            float n = 0;
            for (int i = 1; i < corners.Length; i++)
                n += Vector3.Distance(corners[i - 1], corners[i]);
            return n;
        }

        private void EnsureClearEvents()
        {
            clearEvents = EventsAvailable ? new object() : null;
        }

        internal Plugin()
        {
            NativeAdapter.FriendsClear = true;
            Instance = this;
            PatchGrenadeJudgment(new());
            Physics.BlockArc = Physics.BlockLanding = Physics.BlockMargin = false;
            UnityEngine.AI.NavMesh.NoLanding =
                UnityEngine.AI.NavMesh.NoRoute =
                UnityEngine.AI.NavMesh.Detour =
                    false;
            AIGrenadeHelper.Calls = 0;
            AIGrenadeHelper.Allowed = (v, a) => true;
        }

        internal CometConfig Settings => config!;
        internal int Count => states.Count;
        internal int Draws => jumpRandom.Draws;
        internal List<string> Events => diagnostics!.Events;
        internal List<Player> Players => world!.RegisteredPlayers;

        internal BotState State(BotComponent b) => states[b];

        internal void Tick(BotComponent b, double t)
        {
            Time.timeAsDouble = t;
            Time.frameCount++;
            TrackGrenadeJudgment(b);
        }

        internal bool Find(BotComponent b, bool arc = true)
        {
            var d = new GrenadeThrowDecider { Bot = b };
            bool result = false;
            bool original = GrenadeFindBefore(d, b.GoalEnemy!, ref result, out var state);
            if (original)
            {
                b.BotOwner.WeaponManager.Grenades.AIGreanageThrowData!.CreatedTime = Time.time;
                b.BotOwner.WeaponManager.Grenades.AIGreanageThrowData!.ThrowComplete = false;
                result = arc;
            }
            GrenadeFindAfter(d, b.GoalEnemy!, ref result, state);
            return result;
        }

        internal int Requests;

        internal bool Throw(
            BotComponent b,
            bool nativeAccepted = true,
            bool immediateRelease = false
        )
        {
            var d = new GrenadeThrowDecider { Bot = b };
            bool result = false;
            bool original = GrenadeThrowBefore(d, ref result, out var state);
            if (original)
            {
                Requests++;
                result = nativeAccepted;
                b.BotOwner.WeaponManager.Grenades.ThrowindNow = nativeAccepted;
                if (immediateRelease)
                    Projectile(b);
            }
            GrenadeThrowAfter(d, result, state);
            return result;
        }

        internal void Projectile(BotComponent b, string? id = null) =>
            JudgmentProjectile(
                new()
                {
                    ProfileId = b.ProfileId,
                    WeaponSource = new()
                    {
                        Id = id ?? b.BotOwner.WeaponManager.Grenades.grenade!.Id,
                    },
                }
            );

        internal void End(BotComponent b) => EndGrenadeJudgment(states[b], "raid-ended");
    }
}

public static class LocalizedText
{
    public static string Localized(this string s) => s;
}

namespace SAIN.SAINComponent.Classes.EnemyClasses
{
    public class Status
    {
        public bool VulnerableAction;
        public bool EnemyIsReloading,
            EnemyIsHealing,
            EnemyHasGrenadeOut;
    }

    public class PathData
    {
        public float PathDistance = 20,
            PathLength = 20;
    }
}
