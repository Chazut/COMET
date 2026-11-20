// Narrow inspected native contracts. Executes the production G01 adapter; no claim of Unity physics/network simulation.
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
        public float sqrMagnitude => x * x + y * y + z * z;

        public static Vector3 operator -(Vector3 a, Vector3 b) =>
            new(a.x - b.x, a.y - b.y, a.z - b.z);

        public static float Distance(Vector3 a, Vector3 b) =>
            (float)Math.Sqrt((a - b).sqrMagnitude);
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
        public string Id = "nade";
    }

    public class ThrowWeap : Item
    {
        public bool IsDummy;
        public string ThrowType = "frag_grenade";
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
    public bool CanThrow = true,
        Fresh = true;
    public Vector3 Target = new(20, 0, 0),
        Position;

    public bool IsUpToDate() => Fresh;
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
        public bool ThrowindNow;
        public string GrenadeActionType = "ready";
        public ThrowWeap? grenade = new();
        public AIGreanageThrowData? AIGreanageThrowData = new();
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

    public class Owner
    {
        public Weapons WeaponManager = new();
        public Medicine Medecine = new();
        public OwnerMemory Memory = new();
        public Group? BotsGroup = new();
    }

    public class HealthMemory
    {
        public SAIN.Preset.Shared.Enums.ETagStatus HealthStatus;
    }

    public class Memory
    {
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
        public GrenadeJudgmentConfig GrenadeJudgment = new();
        public GrenadeTrajectoryConfig GrenadeTrajectory = new() { Enabled = false };
        public object Opportunity = new();

        public static void Range(double v, double lo, double hi, string n)
        {
            if (!double.IsFinite(v) || v < lo || v > hi)
                throw new ArgumentException(n);
        }
    }

    public readonly struct DoorPoint
    {
        public readonly double X,
            Y,
            Z;

        public DoorPoint(double x, double y, double z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        public bool Finite => double.IsFinite(X) && double.IsFinite(Y) && double.IsFinite(Z);
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
        internal static bool GrenadeFriendsClear(BotComponent b, Vector3 p) => true;

        internal static void ContinueGrenadeSearch(BotComponent b, BotState s, CometConfig c) { }

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

    internal class GrenadeTrajectoryRuntime
    {
        internal bool Pending,
            Fault;
        internal object? Selected;
    }

    internal class BotState
    {
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
            Math.Max(6, item.MaxExplosionDistance) + 1;

        private static bool ClearBlastProtected(Vector3 pos, Vector3 target, double radius) =>
            Vector3.Distance(pos, target) > radius;

        private bool GrenadeTrajectoryScope(BotComponent b) => false;

        private bool? PrepareGrenadeTrajectory(
            BotComponent b,
            Enemy e,
            BotState s,
            GrenadeJudgmentFrame f
        ) => null;

        private void RecordGrenadeTrajectory(BotState s) { }

        private void StopGrenadeTrajectory(BotState s, string reason) { }

        private void FailGrenadeTrajectory(BotState s, string reason) { }

        private bool ValidateGrenadeTrajectory(BotComponent b, Enemy e, BotState s) => true;

        private bool GrenadeFilterScope(BotComponent b) => false;

        private bool PrepareOffensiveGrenade(BotComponent b, BotState s) => true;

        private bool BindOffensiveGrenade(BotComponent b, BotState s, bool arc) => arc;

        private bool ValidateOffensiveGrenade(BotComponent b, BotState s) => true;

        private string FilterReject(BotComponent b, ThrowWeap? item) => "";

        private void EndGrenadeFilter(BotState s, string reason) { }

        private bool GrenadeChoiceInProgress(BotComponent b) => false;

        private bool GrenadeChoiceScope(BotComponent b) => false;

        private bool ChooseGrenade(BotComponent b, Enemy e, BotState s, bool arc) => arc;

        private bool ValidateGrenadeChoice(BotComponent b, Enemy e, BotState s, bool execution) =>
            true;

        private void StopGrenadeChoice(BotState s, string reason, string state = "ended") { }

        private void EnsureClearEvents()
        {
            clearEvents = EventsAvailable ? new object() : null;
        }

        internal Plugin()
        {
            Instance = this;
            PatchGrenadeJudgment(new());
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
                result = arc;
            GrenadeFindAfter(d, b.GoalEnemy!, ref result, state);
            return result;
        }

        internal int Requests;

        internal bool Throw(
            BotComponent b,
            bool nativeAccepted = true,
            bool immediateRelease = false,
            bool nativeStarted = true
        )
        {
            var d = new GrenadeThrowDecider { Bot = b };
            bool result = false;
            bool original = GrenadeThrowBefore(d, ref result, out var state);
            if (original)
            {
                Requests++;
                result = nativeAccepted;
                b.BotOwner.WeaponManager.Grenades.ThrowindNow = nativeAccepted && nativeStarted;
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

        internal void BrokenObserver(BotComponent b)
        {
            var controller = b.BotOwner.WeaponManager.Grenades;
            b.BotOwner.WeaponManager.Grenades = null!;
            try
            {
                GrenadeThrowAfter(new GrenadeThrowDecider { Bot = b }, true, states[b]);
            }
            finally
            {
                b.BotOwner.WeaponManager.Grenades = controller;
            }
        }
    }
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
