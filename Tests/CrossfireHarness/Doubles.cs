global using EnvironmentType = EFT.EnvironmentType;
using Comet.Core;
using EFT;
using SAIN.Components;
using SAIN.SAINComponent.Classes.EnemyClasses;
using SAIN.SAINComponent.Classes.Mover;
using UnityEngine;
using UnityEngine.AI;

namespace UnityEngine
{
    public struct Vector3(float x, float y, float z)
    {
        public static bool operator ==(Vector3 a, Vector3 b) =>
            a.x == b.x && a.y == b.y && a.z == b.z;

        public static bool operator !=(Vector3 a, Vector3 b) => !(a == b);

        public static Vector3 zero => new();

        public static Vector3 operator -(Vector3 a) => a * -1;

        public static Vector3 operator /(Vector3 a, float f) => a * (1 / f);

        public static Vector3 Cross(Vector3 a, Vector3 b) =>
            new(a.y * b.z - a.z * b.y, a.z * b.x - a.x * b.z, a.x * b.y - a.y * b.x);

        public static Vector3 ProjectOnPlane(Vector3 v, Vector3 n) => v - n * Dot(v, n);

        public float x = x,
            y = y,
            z = z;

        public static float Dot(Vector3 a, Vector3 b) => a.x * b.x + a.y * b.y + a.z * b.z;

        public static Vector3 up => new(0, 1, 0);

        public static Vector3 operator +(Vector3 a, Vector3 b) =>
            new(a.x + b.x, a.y + b.y, a.z + b.z);

        public static Vector3 operator -(Vector3 a, Vector3 b) =>
            new(a.x - b.x, a.y - b.y, a.z - b.z);

        public static Vector3 operator *(Vector3 a, float b) => new(a.x * b, a.y * b, a.z * b);

        public float sqrMagnitude => x * x + y * y + z * z;
        public float magnitude => (float)Math.Sqrt(sqrMagnitude);
        public Vector3 normalized => magnitude < .0001 ? new() : this * (1 / magnitude);

        public static float Distance(Vector3 a, Vector3 b) => (a - b).magnitude;

        public static float Angle(Vector3 a, Vector3 b) =>
            (float)(
                Math.Acos(
                    Math.Clamp(
                        (a.x * b.x + a.y * b.y + a.z * b.z) / (a.magnitude * b.magnitude),
                        -1,
                        1
                    )
                )
                * 180
                / Math.PI
            );
    }

    public static class Time
    {
        public static float time = 10,
            realtimeSinceStartup = 10;
        public static int frameCount;
    }

    public static partial class Mathf
    {
        public static float Max(float a, float b) => Math.Max(a, b);

        public static float Min(float a, float b) => Math.Min(a, b);

        public const float Deg2Rad = (float)(Math.PI / 180),
            Rad2Deg = 1 / Deg2Rad;

        public static float Clamp(float v, float a, float b) => Math.Clamp(v, a, b);

        public static float Atan2(float a, float b) => MathF.Atan2(a, b);

        public const float PI = (float)Math.PI;

        public static float Sin(float x) => (float)Math.Sin(x);

        public static float Cos(float x) => (float)Math.Cos(x);
    }

    public enum QueryTriggerInteraction
    {
        Ignore,
    }

    public class Collider
    {
        public object? attachedRigidbody;

        public T? GetComponentInParent<T>()
            where T : class => null;
    }

    public struct RaycastHit
    {
        public float distance;
        public Collider? collider;
    }

    public static partial class Physics
    {
        public static Func<Vector3, Vector3, bool>? Cast;
        public static Func<Vector3, Vector3, (bool, float)>? Ray;

        public static bool Raycast(
            Vector3 a,
            Vector3 d,
            out RaycastHit h,
            float max,
            int mask,
            QueryTriggerInteraction q = QueryTriggerInteraction.Ignore
        )
        {
            var r = Ray?.Invoke(a, d) ?? (false, 0f);
            h = new() { distance = r.Item2, collider = new() };
            return r.Item1;
        }

        public static bool Blocked;
        public static int Rays;

        public static bool Linecast(
            Vector3 a,
            Vector3 b,
            int mask,
            QueryTriggerInteraction q = QueryTriggerInteraction.Ignore
        )
        {
            Rays++;
            return Cast?.Invoke(a, b) ?? Blocked;
        }
    }
}

namespace UnityEngine.AI
{
    public enum NavMeshPathStatus
    {
        PathComplete,
        PathPartial,
        PathInvalid,
    }

    public class NavMeshPath
    {
        public NavMeshPathStatus status;
        public Vector3[] corners = [];
    }

    public struct NavMeshHit
    {
        public Vector3 position;
    }

    public static class NavMesh
    {
        public static bool Raycast(Vector3 a, Vector3 b, out NavMeshHit h, int mask)
        {
            h = new();
            return false;
        }

        public const int AllAreas = -1;
        public static Func<Vector3, bool>? Sample;
        public static Func<Vector3, Vector3, Vector3[]>? Route;

        public static bool CalculatePath(Vector3 a, Vector3 b, int mask, NavMeshPath path)
        {
            path.corners = Route?.Invoke(a, b) ?? [a, b];
            path.status = NavMeshPathStatus.PathComplete;
            return Valid;
        }

        public static bool Valid = true;

        public static bool SamplePosition(Vector3 p, out NavMeshHit hit, float radius, int areas)
        {
            hit = new() { position = p };
            return Valid && (Sample?.Invoke(p) ?? true);
        }
    }
}

public static class LayersMaskController
{
    public const int HighPolyWithTerrainMask = 1;
}

namespace EFT
{
    public enum EnvironmentType
    {
        Indoor,
        Outdoor,
    }

    public enum ETagStatus
    {
        Healthy,
        BadlyInjured,
        Dying,
    }

    public interface IPlayer
    {
        string ProfileId { get; }
    }

    public enum EBodyPart
    {
        Common,
    }

    public class HealthController
    {
        public bool IsAlive = true;
    }

    public class Player : IPlayer
    {
        public string ProfileId { get; set; } = "";
        public Vector3 Position,
            Velocity;
        public HealthController HealthController = new();
        public Vector3 LookDirection = new(0, 0, 1);
        public bool IsAI = true,
            IsSprintEnabled;
        public EnvironmentType Environment = EnvironmentType.Outdoor;
        public Movement MovementContext = new();
        public Profile Profile = new();
        public HandsBase HandsController = new FirearmController();

        public class HandsBase
        {
            public EFT.InventoryLogic.MedKitClass? Item;
        }

        public class FirearmController : HandsBase
        {
            public bool Ready = true;
        }
    }

    public class Profile
    {
        public string Nickname = "Test PMC";
    }

    public class Movement
    {
        public bool IsGrounded = true;
    }

    public class BotOwner
    {
        public object? BotsGroup = new();
        public Voxels VoxelesPersonalData = new();
        public string ProfileId = "";
        public Memory Memory = new();
        public Medicine Medecine = new();
        public WeaponManager WeaponManager = new();
        public DoorOpener DoorOpener = new();
        public Patrol PatrollingData = new();
    }

    public class Voxels
    {
        public Voxel CurVoxel = new();
    }

    public class Voxel
    {
        public List<EFT.Interactive.NavMeshDoorLink> DoorLinks = new();
    }

    public class Memory
    {
        public bool IsUnderFire;
    }

    public class Medicine
    {
        public bool Using;
        public MedUse SurgicalKit = new(),
            FirstAid = new();
    }

    public class MedUse
    {
        public bool Using;
        public EFT.InventoryLogic.MedKitClass? CurUsingMeds;
        public int? _bodyPartToHeal = 0;
    }

    public interface IMedsController { }

    public class MedHands : Player.HandsBase, IMedsController { }

    public class Magazine
    {
        public int Count = 30,
            MaxCount = 30;
    }

    public class ShootController
    {
        public EFT.InventoryLogic.Weapon? Item = new();
    }

    public class WeaponManager
    {
        public ShootController? ShootController = new();
        public EFT.InventoryLogic.Weapon CurrentWeapon => ShootController!.Item!;
        public bool HaveBullets = true;
        public Reload Reload;
        public Grenades Grenades = new();

        public WeaponManager() => Reload = new(this);
    }

    public class Reload(WeaponManager manager)
    {
        public bool Reloading;
        private EFT.InventoryLogic.Weapon Item => manager.CurrentWeapon;
        public int BulletCount => Item.ChamberAmmoCount + (Item.GetCurrentMagazine()?.Count ?? 0);
        public int MaxBulletCount => Item.ReloadMode == EFT.InventoryLogic.Weapon.EReloadMode.OnlyBarrel
            ? Item.ChamberCapacity
            : Item.GetCurrentMagazine()!.MaxCount;
    }

    public class Grenades
    {
        public bool ThrowindNow;
    }

    public class DoorOpener
    {
        public bool Interacting;
    }

    public class Patrol
    {
        public int Pauses;

        public void Pause() => Pauses++;
    }
}

namespace SAIN.Preset.Shared.Enums
{
    public enum ECombatDecision
    {
        None,
        Search,
        Freeze,
        RushEnemy,
        StandAndShoot,
        SeekCover,
        ShiftCover,
        MoveToEngage,
        ShootDistantEnemy,
        CreepOnEnemy,
        Retreat,
        RunAway,
        DogFight,
        ThrowGrenade,
        AvoidGrenade,
        MeleeAttack,
        FightZombies,
    }

    public enum ESelfActionType
    {
        None,
        Reload,
        Heal,
        FirstAid,
        Surgery,
    }

    public enum ESquadDecision
    {
        None,
        Regroup,
        Retreat,
    }
}

namespace SAIN.SAINComponent.Classes.EnemyClasses
{
    public partial class Enemy
    {
        public EFT.Player EnemyPlayer = new();
        public Vector3 EnemyPosition = new(0, 0, 10);
        public bool Seen = true;
        public PathData Path = new();
        public bool IsVisible,
            CanShoot = true,
            EnemyKnown = true;
        public string EnemyProfileId = "enemy";
        public Places KnownPlaces = new();
        private float updated = Time.time;
        public float TimeSinceLastKnownUpdated
        {
            get => Time.time - updated;
            set => updated = Time.time - value;
        }
    }

    public partial class Places
    {
        public Vector3? LastKnownPosition = new(0, 0, 12);
    }
}

namespace SAIN.SAINComponent.Classes.Decision
{
    public class BotDecisionManager(BotComponent b)
    {
        public BotComponent Bot = b;

        public void ManualUpdate() { }
    }
}

namespace SAIN.Components
{
    public class Rush
    {
        public bool CanRushEnemyReloadHeal = true;
    }

    public class PersonalitySettings
    {
        public Rush Rush = new();
    }

    public class Info
    {
        public PersonalitySettings PersonalitySettings = new();
        public Personality Personality = Personality.Chad;
    }

    public enum Personality
    {
        Normal,
        Chad,
        GigaChad,
        Wreckless,
        Rat,
        Timmy,
        Coward,
        SnappingTurtle,
    }

    public class ManualShooter
    {
        public int Requests;

        public bool TryShoot(Enemy? e, Vector3 p, bool shoot, SAIN.Models.Enums.EShootReason reason)
        {
            Requests++;
            return true;
        }
    }

    public partial class TransformData
    {
        public Vector3 WeaponRoot = new(0, 1, 0);
    }

    public partial class BotComponent
    {
        public Shooter Shoot = new();
        public TransformData Transform = new();
        public ManualShooter ManualShoot = new();
        public Info Info = new();
        public string ProfileId = Guid.NewGuid().ToString();
        public bool BotActive = true,
            IsDead,
            GameEnding,
            Pmc = true,
            SAINLayersActive;
        public Player Player = new();
        public BotOwner BotOwner = new();
        public Vector3 Position;
        public Enemy? GoalEnemy = new();
        public EnemyController EnemyController = new();
        public Squad Squad = new();
        public Decision Decision = new();
        public Mover Mover;
        public Suppression Suppression = new();
        public Grenade Grenade = new();
        public Flashed Flashed = new();
        public HealthMemory Memory = new();
        public Steering Steering = new();

        public BotComponent()
        {
            BotOwner.ProfileId = ProfileId;
            Player.ProfileId = ProfileId;
            Mover = new(this);
        }
    }

    public partial class EnemyController
    {
        public List<Enemy> KnownEnemies = new();
    }

    public partial class Squad
    {
        public bool BotInGroup = true;
        public Dictionary<string, BotComponent> Members = new();
    }

    public class Decision
    {
        public SelfActionDecisions SelfActionDecisions = new();
        public SAIN.Preset.Shared.Enums.ECombatDecision CurrentCombatDecision;
        public SAIN.Preset.Shared.Enums.ESelfActionType CurrentSelfDecision;
        public SAIN.Preset.Shared.Enums.ESquadDecision CurrentSquadDecision;
    }

    public class SelfActionDecisions
    {
        public float AmmoRatio = 1;
        public bool LowOnAmmo(float threshold) => AmmoRatio < threshold;
    }

    public class Suppression
    {
        public bool Result;
        public int Calls;

        public bool TrySuppressAnyEnemy(params object[] args)
        {
            Calls++;
            return Result;
        }

        public bool IsSuppressed;
    }

    public class Grenade
    {
        public Vector3? GrenadeDangerPoint;
    }

    public class Flashed
    {
        public bool IsFlashed;
    }

    public partial class HealthMemory
    {
        public Health Health = new();
    }

    public class Health
    {
        public ETagStatus HealthStatus;
    }

    public class Steering
    {
        public int Priorities,
            LastKnown;
        public bool PriorityResult = true;

        public bool SteerByPriority(Enemy? e, bool random = true)
        {
            Priorities++;
            return PriorityResult;
        }

        public void LookToLastKnownEnemyPosition(Enemy? e) => LastKnown++;

        public int Moves,
            Points;

        public void LookToMovingDirection() => Moves++;

        public void LookToPoint(Vector3 p) => Points++;
    }

    public class PoseValue
    {
        public float TargetValue = 1;
    }

    public class Pose
    {
        public PoseValue PoseValue = new();
    }

    public class Mover(BotComponent b)
    {
        public void SetTargetMoveSpeed(float v) { }

        public bool RunToPoint(
            Vector3 p,
            bool slow,
            float reach,
            SAIN.Models.Enums.ESprintUrgency urgency
        ) => WalkToPoint(p, slow, reach);

        public bool WalkToPoint(Vector3 p, bool slow, float reach)
        {
            var path = new NavMeshPath { corners = [b.Position, p] };
            return WalkToPointByWay(path, slow, reach, false);
        }

        public void Stop()
        {
            (ActivePath as BotPathDataManual)?.Stop(false, "mover-stop");
            ActivePath = null;
        }

        public Pose Pose = new();

        public void SetTargetPose(float v) => Pose.PoseValue.TargetValue = v;

        public IBotPathData? ActivePath,
            QueuedPath;
        public bool Reachable = true,
            Accept = true,
            Observe = true,
            Throw;
        public NavMeshPathStatus Status;
        public int Queries,
            Walks;
        public Func<Vector3, Vector3[]>? Corners;

        public bool CanGoToPoint(Vector3 target, out NavMeshPath path, bool run, float sample = 2)
        {
            Queries++;
            path = new()
            {
                status = Status,
                corners = Corners?.Invoke(target) ?? [b.Position, target],
            };
            return Reachable;
        }

        public bool WalkToPointByWay(NavMeshPath path, bool slow, float reach, bool crawl)
        {
            Walks++;
            if (Throw)
                throw new Exception("native refusal");
            if (Accept && Observe)
                ActivePath = new BotPathDataManual
                {
                    Destination = path.corners[^1],
                    TimeStarted = Time.time,
                    OnStop = () => ActivePath = null,
                };
            return Accept;
        }
    }
}

namespace SAIN.SAINComponent.Classes.Mover
{
    public interface IBotPathData
    {
        float TimeStarted { get; }
        Vector3 Destination { get; }
    }

    public class BotPathDataManual : IBotPathData
    {
        public float TimeStarted { get; set; }
        public Vector3 Destination { get; set; }
        public int Stops;
        public Action? OnStop;

        public void Stop(bool warp, string why)
        {
            Stops++;
            OnStop?.Invoke();
        }
    }
}

namespace SAIN
{
    public static class SAINEnableClass
    {
        public static Dictionary<string, BotComponent> Bots = new();

        public static bool GetSAIN(string id, out BotComponent? b) => Bots.TryGetValue(id, out b);
    }
}

namespace SAIN.Models.Enums
{
    public enum ESAINLayer
    {
        Squad,
    }

    public enum EShootReason
    {
        Suppress,
    }
}

namespace DrakiaXYZ.BigBrain.Brains
{
    public class CustomLayer
    {
        public class ActionData { }

        public record Action(Type Type, string Reason);
    }

    public static class BrainManager
    {
        public static void AddCustomLayer(Type t, List<string> brains, int priority) { }
    }
}

namespace SAIN.Layers
{
    public abstract class SAINLayer : DrakiaXYZ.BigBrain.Brains.CustomLayer
    {
        protected BotOwner BotOwner;
        protected BotComponent Bot = null!;

        public SAINLayer(
            BotOwner owner,
            int priority,
            string name,
            SAIN.Models.Enums.ESAINLayer layer
        )
        {
            BotOwner = owner;
        }

        protected bool GetBotComponent()
        {
            SAIN.SAINEnableClass.GetSAIN(BotOwner.ProfileId, out var b);
            Bot = b!;
            return b != null;
        }

        protected void CheckActiveChanged(bool v)
        {
            if (Bot != null)
                Bot.SAINLayersActive = v;
        }

        public abstract bool IsActive();
        public abstract bool IsCurrentActionEnding();
        public abstract DrakiaXYZ.BigBrain.Brains.CustomLayer.Action GetNextAction();
    }

    public class BotAction
    {
        protected BotOwner BotOwner;
        protected BotComponent Bot => SAIN.SAINEnableClass.Bots[BotOwner.ProfileId];
        public int Starts,
            Stops,
            NativeSteering;

        public BotAction(BotOwner owner, string name)
        {
            BotOwner = owner;
        }

        public virtual void Start()
        {
            Starts++;
        }

        public virtual void Stop()
        {
            Stops++;
        }

        public virtual void Update(DrakiaXYZ.BigBrain.Brains.CustomLayer.ActionData d) { }

        public virtual void OnSteeringTicked() => NativeSteering++;
    }
}

namespace Comet.Core
{
    public class SuppressionDisciplineConfig
    {
        public bool Enabled = true;
        public double ContactAge = 3;
    }

    public class SuppressionDisciplineFrame
    {
        public string Block = "";
        public bool Visible,
            KnownContact = true;
        public double ObservedAt = 10;
        public DoorPoint Known = new(0, 0, 30);
    }

    public readonly record struct DoorPoint(double X, double Y, double Z);

    public class DoorGeometry
    {
        public string Id = "",
            State = "Open";
        public DoorPoint Center,
            Normal,
            Axis;
        public double HalfWidth;
    }

    public static class DoorPolicy
    {
        public static bool Finite(double v) => double.IsFinite(v);
    }

    public static class MedicalChoice
    {
        public static bool Finite(double x) => double.IsFinite(x);
    }

    public class HoldAngleConfig
    {
        public bool Enabled = true;
        public double MaxStep = 1.5;
    }

    public class CometConfig
    {
        public HoldAngleConfig HoldAngle = new();
        public bool Enabled = true;
        public SquadStormConfig SquadStorm = new() { Enabled = false };
        public SquadTradeConfig SquadTrade = new();
        public CoverMateConfig CoverMate = new();
        public SecondaryEntranceConfig SecondaryEntrance = new();
        public DoorReservationConfig DoorReservation = new();
        public SquadManeuverConfig SquadManeuver = new();
        public SuppressionDisciplineConfig SuppressionDiscipline = new();
        public MovementRecoveryConfig MovementRecovery = new();
        public CrossfireConfig Crossfire = new();
        public long Revision = 1;

        public static void Range(double v, double min, double max, string key)
        {
            if (!double.IsFinite(v) || v < min || v > max)
                throw new ArgumentException(key);
        }
    }
}

namespace Comet.Client
{
    internal class DoorWork
    {
        internal SAIN.SAINComponent.Classes.Mover.DoorDataStruct Data;
        internal DoorGeometry Geometry = new();
    }

    internal class DoorJumpRuntime
    {
        internal DoorWork? Door;
    }

    internal class SuppressionDisciplineRuntime
    {
        public bool Fault;
    }

    internal class Other
    {
        public Flag Policy = new();
    }

    internal class Flag
    {
        public bool Active;
    }

    internal class FearTrace
    {
        public bool Panic;
    }

    internal class FearModel
    {
        public FearTrace Current = new();
    }

    internal class MovementRecoveryRuntime
    {
        public MovementRecoveryPolicy Policy = new();
    }

    internal partial class BotState
    {
        public SAIN.Preset.Shared.Enums.ECombatDecision Expected;
        public int RequestedFrame;
        public MovementRecoveryRuntime MovementRecovery = new();
        public FearModel Fear = new();
        public SuppressionDisciplineRuntime SuppressionDiscipline = new();
        public CrossfireRuntime Crossfire = new();
        public Other Door = new(),
            Jump = new(),
            RunBy = new(),
            StepPeek = new(),
            Fake = new(),
            RoomClear = new(),
            Ambush = new(),
            PostCombat = new(),
            Relocation = new(),
            CoverFeint = new();
        public string Id = Guid.NewGuid().ToString(),
            ProfileId,
            Name = "Test PMC",
            Feature = "",
            Decision = "";

        public BotState(BotComponent b, CometConfig c, string session)
        {
            Owner = b;
            ProfileId = b.ProfileId;
        }
    }

    internal static partial class NativeAdapter
    {
        public static bool IsSupportedAi(BotComponent b) => b.Pmc && b.Player.IsAI;

        public static Situation Capture(BotComponent b, Enemy? e, bool a, bool g) => new();
    }

    internal partial class Situation
    {
        public string ContactSource = "shared";
    }

    internal class Budget
    {
        public bool Available = true;
        public int Calls,
            Used,
            Frame = -1;

        public bool TryBegin(int frame, object? owner = null)
        {
            Calls++;
            if (Frame != frame)
            {
                Frame = frame;
                Used = 0;
            }
            if (!Available || Used >= 2)
                return false;
            Used++;
            return true;
        }

        public void Charge(double ms) { }
    }

    internal class Sink
    {
        public List<string> Lines = new();

        public void Write(string s) => Lines.Add(s);
    }

    internal static class Diagnostics
    {
        internal static string Serialize(object value) =>
            Newtonsoft.Json.JsonConvert.SerializeObject(value, Json);

        public static Newtonsoft.Json.JsonSerializerSettings Json = new()
        {
            ContractResolver =
                new Newtonsoft.Json.Serialization.CamelCasePropertyNamesContractResolver(),
        };
    }

    internal static class Logger
    {
        public static List<string> Warnings = new();

        public static void LogWarning(string s) => Warnings.Add(s);
    }

    internal static partial class Api
    {
        public static partial class Decisions
        {
            public static Dictionary<string, string> Labels = new();

            public static void SetCrossfire(string id, string v)
            {
                if (v.Length == 0)
                    Labels.Remove(id);
                else
                    Labels[id] = v;
            }

            public static void Publish(string json, Action<Exception> fail) { }
        }
    }

    public sealed partial class Plugin
    {
        private double AdjustStormChance(
            BotComponent bot,
            Enemy enemy,
            BotState state,
            double chance
        ) => chance;

        internal static Plugin? Instance;
        internal GameWorld? world = new();
        internal CometConfig? config = new();
        internal bool Authority = true,
            subscriberWarned,
            Guard,
            Queued;
        internal HashSet<string> Ghosts = new();
        internal string session = "s01-harness";
        internal long sequence;
        internal Dictionary<BotComponent, BotState> states = new();
        internal Sink? diagnostics = new();
        internal Budget doorBudget = new();
        private readonly DoorReservationBoard doorReservations = new();
        private System.Diagnostics.Stopwatch doorClock = new();
        private Random jumpRandom = new(3);
        private static System.Reflection.MethodInfo combatStopPath =
            typeof(BotPathDataManual).GetMethod("Stop")!;
        internal string Volume = "";
        internal bool OpenAccepted = true,
            Opening;

        private static bool JumpInteractionState(BotComponent b) => Instance!.Opening;

        private static bool RequestJumpDoorOpen(BotComponent b, DoorJumpRuntime r)
        {
            if (!Instance!.OpenAccepted)
                return false;
            r.Door!.Data.Door.DoorState = EFT.Interactive.EDoorState.Open;
            return true;
        }

        private static string ReservationSquad(BotComponent b, BotState s) =>
            string.Join("|", b.Squad.Members.Keys.Order());

        private static bool TryDoorGeometry(
            SAIN.SAINComponent.Classes.Mover.DoorDataStruct data,
            out DoorGeometry g
        )
        {
            g = data.Link?.Geometry!;
            if (g == null)
                return false;
            g.State = data.Door.DoorState.ToString();
            return true;
        }

        private static DoorPoint DP(Vector3 v) => new(v.x, v.y, v.z);

        private static Vector3 VP(DoorPoint p) => new((float)p.X, (float)p.Y, (float)p.Z);

        private string RunVolume(BotComponent b, Vector3 a, Vector3 z) => Volume;

        private static double Length(Vector3[] p)
        {
            double n = 0;
            for (int i = 1; i < p.Length; i++)
                n += Vector3.Distance(p[i - 1], p[i]);
            return n;
        }

        internal Func<Vector3, Vector3, int> Protection = (p, k) => 0;
        internal SuppressionDisciplineFrame FireFrame = new();

        private SuppressionDisciplineFrame DisciplineFrame(BotComponent b, BotState s, Enemy? e) =>
            FireFrame;

        private void EndDiscipline(BotState s, string why) { }

        public Plugin()
        {
            Instance = this;
        }

        private bool Ghost(string id) => Ghosts.Contains(id);

        private static bool ValidPoint(Vector3 p) =>
            float.IsFinite(p.x) && float.IsFinite(p.y) && float.IsFinite(p.z);

        private static bool WeaponHandsReady(BotComponent b, Player.FirearmController? f) =>
            f?.Ready == true;

        private bool MedicalPostureGuard(BotComponent b) => Guard;

        private bool QueuedCombatMovement(BotComponent b) => Queued || b.Mover.QueuedPath != null;

        private static IBotPathData? CrossfireQueuedPath(BotComponent b) => b.Mover.QueuedPath;

        private int CombatProtection(Vector3 p, Vector3 k) => Protection(p, k);

        private void Release(BotState s, string why, bool a, bool b)
        {
            s.Feature = s.Decision = "";
        }

        private void RefreshFear(BotComponent b, BotState s) { }

        internal bool Witness = true;

        private static bool Witnesses(BotComponent b, Vector3 p, float range) => Instance!.Witness;

        internal Dictionary<string, StormEvidence> StormEvidenceByBot = new();

        private void ObserveStormEquipment(BotComponent b, BotState s) { }

        private StormEvidence ReadStormEvidence(BotComponent b, BotState s) =>
            StormEvidenceByBot.TryGetValue(b.ProfileId, out var e)
                ? new StormEvidence
                {
                    At = e.At,
                    Until = e.Until,
                    Score = e.Score,
                    Source = e.Source,
                    Danger = e.Danger,
                    Cue = e.Cue,
                    Details = e.Details,
                }
                : new() { At = -1 };

        private double StormRouteEta(BotComponent b, CrossfireRuntime r) => r.Length / 2.4 + .25;

        internal void Death(BotComponent mate, BotComponent observer, EFT.IPlayer? attacker) =>
            ObserveTradeDeath(
                new SAIN.BotController.Classes.Squad { Members = observer.Squad.Members },
                mate.Player,
                attacker!
            );

        internal void Observe(BotComponent b) => ObserveCrossfire(new(b));

        internal void Sample(BotComponent b) => SampleCrossfire(b, states[b]);

        internal int Claims => crossfireClaims.Count;

        internal void End(BotComponent b) => EndCrossfire(b, states[b], "crossfire-raid-ended");

        internal void EndAll()
        {
            foreach (var pair in states)
                EndCrossfire(pair.Key, pair.Value, "crossfire-raid-ended");
            crossfireClaims.Clear();
            maneuverBoard.Clear();
            coverMateBoard.Clear();
            stormBoard.Clear();
        }

        internal bool Choose(BotComponent b)
        {
            var d = SAIN.Preset.Shared.Enums.ECombatDecision.None;
            bool succeeded = false;
            return ContinueCrossfireDecision(b, states[b], ref d, ref succeeded)
                && succeeded
                && d == SAIN.Preset.Shared.Enums.ECombatDecision.Freeze;
        }

        internal bool ChooseStorm(BotComponent b)
        {
            var decision = SAIN.Preset.Shared.Enums.ECombatDecision.None;
            bool succeeded = false;
            return ChooseNativeStorm(b, states[b], ref decision, ref succeeded)
                && succeeded && decision == SAIN.Preset.Shared.Enums.ECombatDecision.RushEnemy;
        }
    }
}

namespace EFT.Interactive
{
    public enum EDoorState
    {
        Open,
        Shut,
        Locked,
    }

    public partial class Door
    {
        public EDoorState DoorState;
    }

    public class NavMeshDoorLink
    {
        public Door Door = new();
        public Comet.Core.DoorGeometry Geometry = new();
    }
}

namespace SAIN.SAINComponent.Classes.Mover
{
    public struct DoorDataStruct(EFT.Interactive.NavMeshDoorLink link)
    {
        public EFT.Interactive.NavMeshDoorLink Link = link;
        public EFT.Interactive.Door Door => Link.Door;
    }
}

namespace EFT.InventoryLogic
{
    public class Weapon
    {
        public enum EReloadMode { ExternalMagazine, OnlyBarrel }
        public EReloadMode ReloadMode;
        public int ChamberAmmoCount, ChamberCapacity;
        public EFT.Magazine Mag = new();
        public EFT.Magazine? GetCurrentMagazine() => Mag;
    }

    public class MedKitClass
    {
        public string Id = "kit";
        public HealthEffects HealthEffectsComponent = new();
        public Resource MedKitComponent = new();
    }

    public class Resource
    {
        public float HpResource = 100;
    }

    public class HealthEffects
    {
        public double Time = 30;

        public double UseTimeFor(int part) => Time;
    }
}

namespace SAIN.BotController.Classes
{
    public class Squad
    {
        public Dictionary<string, SAIN.Components.BotComponent> Members = new();

        private void memberWasKilled(
            EFT.Player player,
            EFT.IPlayer killer,
            EFT.Ballistics.DamageInfo info,
            EFT.EBodyPart part
        ) { }
    }
}

namespace EFT.Ballistics
{
    public class DamageInfo { }
}

namespace EFT
{
    public class DownedPlayer : Player
    {
        public bool Downed { get; set; }
    }
}

namespace SAIN.Models.Enums
{
    public enum ESprintUrgency
    {
        High,
    }
}

namespace SAIN.SAINComponent.Classes.EnemyClasses
{
    public class PathData
    {
        public float PathLength = 20;
        public UnityEngine.AI.NavMeshPathStatus PathToEnemyStatus = UnityEngine.AI.NavMeshPathStatus.PathComplete;
    }
}

namespace Comet.Client
{
    internal static class ZzapEnemyWeakness
    {
        internal static float Read(
            SAIN.Components.BotComponent b,
            SAIN.SAINComponent.Classes.EnemyClasses.Enemy e,
            out string[] evidence
        )
        {
            evidence = ["engine vulnerability double"];
            return 100;
        }
    }
}

namespace SAIN.Components
{
    public class Shooter
    {
        public bool Result;
        public int Calls;
        public Enemy? Last;

        public bool ShootAnyVisibleEnemies(Enemy? enemy)
        {
            Last = enemy;
            Calls++;
            return Result;
        }
    }
}

namespace Comet.Client
{
    public sealed partial class Plugin
    {
        internal static void ObserveSteeringFire(
            BotComponent b,
            TacticalSteeringKind k,
            bool f,
            bool s
        ) { }
    }
}
