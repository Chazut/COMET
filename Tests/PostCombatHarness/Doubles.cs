using Comet.Core;
using EFT;
using EFT.HealthSystem;
using EFT.InventoryLogic;
using SAIN.Components;
using SAIN.Preset.Shared.Enums;
using SAIN.SAINComponent.Classes.Decision;
using SAIN.SAINComponent.Classes.EnemyClasses;
using UnityEngine;

namespace UnityEngine
{
    public struct Vector3(float x, float y, float z)
    {
        public static Vector3 operator -(Vector3 a, Vector3 b) =>
            new(a.x - b.x, a.y - b.y, a.z - b.z);

        public static Vector3 forward => new(0, 0, 1);
        public Vector3 normalized => magnitude == 0 ? new() : this * (1 / magnitude);

        public static float Dot(Vector3 a, Vector3 b) => a.x * b.x + a.y * b.y + a.z * b.z;

        public static Vector3 up => new(0, 1, 0);

        public static Vector3 operator +(Vector3 a, Vector3 b) =>
            new(a.x + b.x, a.y + b.y, a.z + b.z);

        public static Vector3 operator *(Vector3 a, float b) => new(a.x * b, a.y * b, a.z * b);

        public float x = x,
            y = y,
            z = z;
        public float magnitude => (float)Math.Sqrt(x * x + y * y + z * z);

        public static float Distance(Vector3 a, Vector3 b) =>
            new Vector3(a.x - b.x, a.y - b.y, a.z - b.z).magnitude;
    }

    public static class Time
    {
        public static float time = 10,
            realtimeSinceStartup = 10;
        public static int frameCount;
    }
}

namespace EFT
{
    public enum EBodyPart
    {
        Head,
        Chest,
        Stomach,
        LeftArm,
        RightArm,
        LeftLeg,
        RightLeg,
        Common,
    }

    public enum ETagStatus
    {
        Healthy,
        Injured,
        BadlyInjured,
        Dying,
    }

    public interface IMedsController { }

    public class Hands
    {
        public Item? Item;
        public bool Busy;

        public bool IsInInteractionStrictCheck() => Busy;
    }

    public class MedHands : Hands, IMedsController { }

    public class Player
    {
        public Vector3 Position;
        public bool IsAI = true;
        public Movement MovementContext = new();

        public class FirearmController : Hands
        {
            public new Weapon Item = new();
            public bool Ready = true;
        }

        public Hands HandsController = new();
        public Vector3 Velocity;
        public bool IsSprintEnabled;
        public ActiveHealthController HealthController = new();
        public Inventory Inventory = new();
        public InventoryController InventoryController = new();
    }

    public class Inventory
    {
        public object Equipment = new();
    }

    public class InventoryController
    {
        public List<Meds> Items = new();

        public void GetAcceptableItemsNonAlloc<T>(EquipmentSlot[] slots, List<T> list)
        {
            list.AddRange(Items.OfType<T>());
        }
    }

    public class BotOwner
    {
        public BotMedecine Medecine;
        public Player GetPlayer;
        public bool IsDead;
        public Settings Settings = new();
        public WeaponManager WeaponManager = new();
        public DoorOpener DoorOpener = new();
        public Memory Memory = new();
        public ShootData ShootData = new();
        public Patrol PatrollingData = new();
        public string ProfileId = "pmc";

        public BotOwner(Player p)
        {
            GetPlayer = p;
            Medecine = new(p);
        }
    }

    public class Settings
    {
        public FileSettings FileSettings = new();
    }

    public class FileSettings
    {
        public Mind Mind = new();
    }

    public class Mind
    {
        public bool CAN_USE_MEDS = true,
            MEDS_ONLY_SAFE_CONTAINER,
            SURGE_KIT_ONLY_SAFE_CONTAINER;
        public float PART_PERCENT_TO_HEAL = .8f;
    }

    public class WeaponManager
    {
        public Reload Reload = new();
        public Selector Selector = new();
        public Grenades Grenades = new();
    }

    public class Reload
    {
        public bool Reloading,
            Available = true;
        public int Requests;
        public Magazine? Mag = new() { Count = 30 };
        public List<object>? Ammo;

        public bool CanReload(bool check, out Magazine? mag, out List<object>? ammo)
        {
            mag = Mag;
            ammo = Ammo;
            return Available;
        }

        public void ReloadMagazine(Magazine m)
        {
            Requests++;
        }

        public void ReloadAmmo(List<object>? a)
        {
            Requests++;
        }
    }

    public class Selector
    {
        public bool IsChanging;
    }

    public class Grenades
    {
        public bool ThrowindNow;
    }

    public class DoorOpener
    {
        public bool Interacting;
    }

    public class ValueStruct
    {
        public float Current = 80,
            Maximum = 100;
        public float Normalized => Current / Maximum;
    }
}

namespace EFT.Ballistics
{
    public enum EDamageEffectType
    {
        HeavyBleeding,
        LightBleeding,
        DestroyedPart,
    }
}

namespace EFT.HealthSystem
{
    public interface IEffect
    {
        EBodyPart BodyPart { get; }
    }

    public interface IHeavyBleeding : IEffect { }

    public interface ILightBleeding : IEffect { }

    public class Heavy(EBodyPart p) : IHeavyBleeding
    {
        public EBodyPart BodyPart => p;
    }

    public class Light(EBodyPart p) : ILightBleeding
    {
        public EBodyPart BodyPart => p;
    }

    public enum EStimulatorBuffType
    {
        HealthRate,
        RemoveAllBloodLosses,
        SkillRate,
    }

    public class ActiveHealthController
    {
        public bool IsAlive = true;
        public Dictionary<EBodyPart, ValueStruct> Parts = Enum.GetValues<EBodyPart>()
            .ToDictionary(x => x, x => new ValueStruct());
        public List<IEffect> Effects = new();
        public HashSet<EBodyPart> Destroyed = new();
        public int Restores;

        public ValueStruct GetBodyPartHealth(EBodyPart p, bool ignored = false) => Parts[p];

        public T? FindExistingEffect<T>(EBodyPart p = EBodyPart.Common)
            where T : class =>
            Effects
                .Where(x => p == EBodyPart.Common || x.BodyPart == p)
                .OfType<T>()
                .FirstOrDefault();

        public bool IsBodyPartDestroyed(EBodyPart p) => Destroyed.Contains(p);

        public bool CanApplyItem(Meds item, EBodyPart p) =>
            item.Applicable
            && !item.Removed
            && (!item.AllowedPart.HasValue || item.AllowedPart == p);

        public bool RestoreBodyPart(EBodyPart p, float penalty)
        {
            Restores++;
            return Destroyed.Remove(p);
        }
    }
}

namespace EFT.InventoryLogic
{
    public enum EquipmentSlot
    {
        Pockets,
        TacticalVest,
        Backpack,
        SecuredContainer,
    }

    public class Item
    {
        public string Id = "item";
        public bool Removed;
        public EquipmentSlot Slot = EquipmentSlot.Pockets;
    }

    public class Meds : Item
    {
        public HealthEffectsComponent HealthEffectsComponent = new();
        public MedKitComponent? MedKitComponent = new();
        public bool Applicable = true;
        public EBodyPart? AllowedPart;
    }

    public class MedKit : Meds { }

    public class Stimulator : Meds { }

    public class MedKitComponent
    {
        public float HpResource = 100;
    }

    public class Spec
    {
        public float Cost;
    }

    public class Buff
    {
        public float Chance = 1,
            Duration = 30,
            Value = 1,
            Delay;
        public EStimulatorBuffType BuffType = EStimulatorBuffType.HealthRate;
    }

    public class HealthEffectsComponent
    {
        public float Duration = 4;
        public Dictionary<EFT.Ballistics.EDamageEffectType, Spec> DamageEffects = new();
        public Buff[] BuffSettings = Array.Empty<Buff>();

        public float UseTimeFor(EBodyPart p) => Duration;
    }
}

public class BotMedecine
{
    public BotFirstAid FirstAid;
    public BotSurgicalKit SurgicalKit;
    public BotStimulators Stimulators;

    public BotMedecine(Player p)
    {
        FirstAid = new(p);
        SurgicalKit = new(p);
        Stimulators = new(p);
    }

    public bool Using => FirstAid.Using || SurgicalKit.Using || Stimulators.Using;
}

public class NativeMed(Player p)
{
    public bool _lastIsCanceled;
    public bool _using => Using;
    public int Cancels;

    public void CancelCurrent()
    {
        if (!Using || _lastIsCanceled)
            return;
        Cancels++;
        _lastIsCanceled = true;
    }

    public bool Using,
        Refuse,
        DelayHands,
        Bhf,
        Throw;
    public int Requests;
    public float _nextPosibleUseTime;
    public Meds? CurUsingMeds;
    public EBodyPart? _bodyPartToHeal;
    public Action? Done;
    public Player Player = p;

    public void Begin(Meds? item, Action? callback)
    {
        _lastIsCanceled = false;
        Requests++;
        if (Throw)
            throw new Exception("native failure");
        if (Refuse)
            return;
        Using = true;
        CurUsingMeds = item;
        Done = callback;
        if (!DelayHands)
            Draw();
    }

    public void Draw()
    {
        Player.HandsController = new MedHands { Item = CurUsingMeds };
    }

    public void Finish(bool improve = true)
    {
        if (improve && _bodyPartToHeal.HasValue)
        {
            Player.HealthController.Parts[_bodyPartToHeal.Value].Current += 10;
            Player.HealthController.Destroyed.Remove(_bodyPartToHeal.Value);
            Player.HealthController.Effects.RemoveAll(e => e.BodyPart == _bodyPartToHeal);
        }
        if (CurUsingMeds?.MedKitComponent != null)
            CurUsingMeds.MedKitComponent.HpResource -= 1;
        if (Bhf)
        {
            Using = false;
            Done?.Invoke();
        }
        else
        {
            Done?.Invoke();
            Using = false;
        }
        Player.HandsController = new();
    }
}

public class BotFirstAid(Player p) : NativeMed(p)
{
    public bool IsBleeding;

    public bool ShallStartUse() => CurUsingMeds != null && !Using;

    public bool CanUseByTime() => Time.time >= _nextPosibleUseTime;

    public void TryApplyToCurrentPart(int? animation = null, Action? callback = null) =>
        Begin(CurUsingMeds, callback);
}

public class BotSurgicalKit(Player p) : NativeMed(p)
{
    public bool ShallStartUse() => CurUsingMeds != null && !Using;

    public void ApplyToCurrentPart(Action? callback = null) => Begin(CurUsingMeds, callback);
}

public class BotStimulators(Player p) : NativeMed(p)
{
    public Stimulator? _stimulator;
    public float LastEndUseTime;

    public bool CanUseNow() => Time.time >= _nextPosibleUseTime;

    public void TryApply(
        bool noCheckDelay = false,
        int? animation = null,
        Action<bool>? callback = null
    ) => Begin(_stimulator, () => callback?.Invoke(true));
}

namespace SAIN.Preset.Shared.Enums
{
    public enum ESquadDecision
    {
        None,
        Regroup,
    }

    public enum ESelfActionType
    {
        None,
        Reload,
        FirstAid,
        Stims,
        Surgery,
    }

    public enum ECombatDecision
    {
        None,
        AvoidGrenade,
        SeekCover,
        Retreat,
        RunAway,
    }

    public enum SAINSoundType
    {
        FootStep,
        Sprint,
        Shot,
        SuppressedShot,
        Jump,
        Land,
        Door,
        DoorBreach,
    }
}

namespace SAIN.Components
{
    public class SelfActions(BotOwner owner)
    {
        public bool DoFirstAid()
        {
            owner.Medecine.FirstAid.TryApplyToCurrentPart();
            return true;
        }

        public bool DoSurgery()
        {
            owner.Medecine.SurgicalKit.ApplyToCurrentPart();
            return true;
        }
    }

    public class BotComponent
    {
        public SAIN.Models.Enums.ESAINLayer ActiveLayer;
        public Shooter Shoot = new();
        public Suppression Suppression = new();
        public SelfActions SelfActions;
        public HealthMemory Memory = new();
        public string ProfileId = "pmc";
        public bool Pmc = true,
            BotActive = true,
            IsDead,
            GameEnding,
            SAINLayersActive = true;
        public Player Player = new();
        public BotOwner BotOwner;
        public Medical Medical = new();
        public Info Info = new();
        public Cover Cover = new();
        public Vector3 Position;
        public EnemyController EnemyController = new();
        public Decision Decision = new();
        public Grenade Grenade = new();
        public Mover Mover = new();
        public Steering Steering = new();
        public Enemy? GoalEnemy;
        public Squad Squad = new();
        public Flashed Flashed = new();

        public BotComponent()
        {
            BotOwner = new(Player);
            SelfActions = new(BotOwner);
            SAIN.SAINEnableClass.Bots[ProfileId] = this;
        }
    }

    public class Medical
    {
        public void TryCancelHeal() { }

        private float hitAt = Time.time - 10;
        public float TimeSinceShot
        {
            get => Time.time - hitAt;
            set => hitAt = Time.time - value;
        }
    }

    public class Info
    {
        public string Personality = "Normal";
    }

    public class Cover
    {
        public Point? CoverInUse;
        public List<Point> CoverPoints = new();
    }

    public class Point
    {
        public Vector3 Position;
        public CoverData CoverData = new();
        public bool BotInThisCover = true,
            Spotted;
        public UnityEngine.Collider Collider = new();
    }

    public class EnemyController
    {
        public List<Enemy> KnownEnemies = new();
    }

    public class Decision
    {
        public ESelfActionType CurrentSelfDecision;
        public ECombatDecision CurrentCombatDecision;
        public ESquadDecision CurrentSquadDecision;
    }

    public class Grenade
    {
        public object? GrenadeDangerPoint;
    }

    public class ProneController
    {
        public bool Value;

        public void SetProne(bool v) => Value = v;
    }

    public class Mover
    {
        public ProneController Prone = new();
        public float Pose,
            Speed;

        public void SetTargetPose(float p) => Pose = p;

        public void SetTargetMoveSpeed(float p) => Speed = p;

        public bool WalkToPoint(Vector3 p, float reach = 1, bool crawl = false)
        {
            Routes++;
            if (Walk)
                ActivePath = new SAIN.SAINComponent.Classes.Mover.BotPathDataManual();
            return Walk;
        }

        public SAIN.SAINComponent.Classes.Mover.IBotPathData? ActivePath;
        public bool CanPath = true,
            Walk = true,
            Complete = true;
        public int Routes;
        public int Stops;

        public void Stop() => Stops++;

        public bool CanGoToPoint(
            Vector3 target,
            out UnityEngine.AI.NavMeshPath route,
            bool check,
            float radius
        )
        {
            route = new()
            {
                status = Complete
                    ? UnityEngine.AI.NavMeshPathStatus.PathComplete
                    : UnityEngine.AI.NavMeshPathStatus.PathPartial,
                corners = new[] { new Vector3(0, 0, 0), target },
            };
            return CanPath;
        }

        public bool WalkToPointByWay(
            UnityEngine.AI.NavMeshPath route,
            bool slow,
            float reach,
            bool crawl
        )
        {
            Routes++;
            if (Walk)
                ActivePath = new SAIN.SAINComponent.Classes.Mover.BotPathDataManual();
            return Walk;
        }
    }
}

namespace SAIN.SAINComponent.Classes.Decision
{
    public class SelfActionDecisionClass(BotComponent bot)
    {
        public BotComponent Bot = bot;
    }

    public class SAINSelfActionClass(BotComponent bot)
    {
        public BotComponent Bot = bot;

        public void DoFirstAid() { }

        public void DoStims() { }

        public void DoSurgery() { }
    }
}

namespace SAIN.SAINComponent.Classes.EnemyClasses
{
    public class Enemy(BotComponent b)
    {
        public BotComponent Bot = b;
        public bool Seen;
        public float TimeSinceSeen = 100;
        public bool EnemyKnown = true,
            IsVisible,
            InLineOfSight;
        public float TimeSinceLastKnownUpdated =>
            KnownPlaces.LastSeenPlace?.TimeSincePositionUpdated ?? 0;
        public EnemyPath Path = new();
        public string EnemyProfileId = "enemy";
        public Places KnownPlaces = new();
    }

    public class Places
    {
        public Vector3? LastKnownPosition => LastSeenPlace?.Position ?? LastHeardPlace?.Position;
        public EnemyPlace? LastSeenPlace = new();
        public EnemyPlace? LastHeardPlace;
    }

    public class EnemyPlace
    {
        public Vector3 Position = new(60, 0, 0);
        public float TimeSincePositionUpdated = 30;
    }

    public class EnemyEvents
    {
        public void EnemyHeard(SAINSoundType type, bool report, EnemyPlace p) { }
    }
}

namespace Comet.Core
{
    public class CometConfig
    {
        public TacticalMistakesConfig TacticalMistakes = new();
        public PostCombatConfig PostCombat = new();
        public bool Enabled = true;
        public MedicalChoiceConfig MedicalChoice = new();
        public SurgeryInterruptionConfig SurgeryInterruption = new() { Enabled = false };
        public long Revision = 1;
        public Dictionary<string, double> Aggression = new() { { "Normal", .5 } };

        public static void Range(double v, double min, double max, string key)
        {
            if (!double.IsFinite(v) || v < min || v > max)
                throw new ArgumentException(key);
        }
    }
}

namespace Comet.Client
{
    internal class SurgeryDouble(BotComponent b)
    {
        public BotComponent Bot => b;
    }

    internal class BotState
    {
        public PostCombatRuntime PostCombat = new();
        public MedicalRuntime Medical = new();
        public string ProfileId = "pmc",
            Name = "Medic",
            Id = "instance";
        public ActionState HandsRecovery = new(),
            Fake = new();

        public BotState(BotComponent b, CometConfig c, string session)
        {
            ProfileId = b.ProfileId;
        }
    }

    internal class ActionState
    {
        public Flags Policy = new();
    }

    internal class Flags
    {
        public bool BlocksSwitch,
            Active;
    }

    internal class NativeAdapter
    {
        public static bool IsSupportedAi(BotComponent b) => b.Pmc && b.Player.IsAI;
    }

    internal class LoadoutAdapter
    {
        public static EquipmentSlot[] Carried = Enum.GetValues<EquipmentSlot>();

        public static EquipmentSlot? RootSlot(Item i, object equipment) =>
            i.Removed ? null : i.Slot;
    }

    internal class Budget
    {
        public bool Available = true;
        public int Calls;

        public bool TryBegin(int f, object? owner = null)
        {
            Calls++;
            return Available;
        }

        public void Charge(double ms) { }
    }

    internal class Diagnostic
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

    internal class Logger
    {
        public static void LogWarning(string m) { }
    }

    internal static class Api
    {
        public static class Decisions
        {
            public static Dictionary<string, string> Recovery = new();

            public static void SetPostCombat(string id, string value)
            {
                if (value.Length == 0)
                    Recovery.Remove(id);
                else
                    Recovery[id] = value;
            }

            public static void Publish(string json, Action<Exception> error) { }
        }
    }

    public sealed partial class Plugin
    {
        internal static UnityEngine.Vector3 RemixMovementOrigin(
            BotComponent bot,
            UnityEngine.Vector3 original
        ) => original;

        internal static Plugin? Instance;
        internal object? world = new();
        internal CometConfig? config = new();
        internal bool Authority = true,
            IsGhost,
            subscriberWarned;
        internal int Protection = 7;
        internal Vector3 ProtectionOrigin;
        internal string session = "h01-harness";
        internal long sequence;
        internal Dictionary<BotComponent, BotState> states = new();
        internal Budget doorBudget = new();
        internal System.Diagnostics.Stopwatch doorClock = new();
        internal Diagnostic? diagnostics = new();

        private static bool WeaponHandsReady(BotComponent b, Player.FirearmController? firearm) =>
            firearm?.Ready == true;

        public Plugin()
        {
            Instance = this;
        }

        private bool Ghost(string id) => IsGhost;

        private static bool ValidPoint(Vector3 v) =>
            float.IsFinite(v.x) && float.IsFinite(v.y) && float.IsFinite(v.z);

        private int CombatProtection(Vector3 pos, Vector3 known) =>
            Vector3.Distance(pos, ProtectionOrigin) < .01f ? Protection : 7;

        internal (bool native, ESelfActionType decision, bool result) Choose(BotComponent b)
        {
            ESelfActionType d = ESelfActionType.None;
            bool result = false;
            bool native = ChooseMedical(new(b), ref d, ref result);
            if (!native)
                b.Decision.CurrentSelfDecision = d;
            return (native, d, result);
        }

        internal void Start(BotComponent b) => StartMedical(b, states[b]);

        internal void Sample(BotComponent b) => SampleMedical(b, states[b], true);

        internal void Tick(BotComponent b) => SampleMedical(b, states[b], false);

        internal void End(BotComponent b) => EndMedical(states[b], "medical-raid-ended");

        internal MedicalFrame Frame(BotComponent b) => MedicalFrame(b, states[b]);

        internal void Hear(Enemy e, SAINSoundType sound, EnemyPlace place) =>
            MedicalFootstep(e, sound, place);

        internal bool Continue(BotComponent b)
        {
            ESelfActionType d = ESelfActionType.None;
            bool result = false;
            ContinueMedical(new(b), ref d, ref result);
            return result;
        }

        internal bool SurgeryAction(BotComponent b)
        {
            surgeryBot = typeof(SurgeryDouble).GetProperty("Bot")!;
            return ExecuteSurgery(new SurgeryDouble(b));
        }

        internal void RestoreOnStop(BotComponent b)
        {
            surgeryBot = typeof(SurgeryDouble).GetProperty("Bot")!;
            NativeRestoreAfterSurgery(
                b.Player.HealthController,
                EBodyPart.LeftLeg,
                1,
                new SurgeryDouble(b)
            );
            SurgeryStopped(new SurgeryDouble(b));
        }

        internal System.Collections.Generic.IEnumerable<HarmonyLib.CodeInstruction> Transform(
            System.Collections.Generic.IEnumerable<HarmonyLib.CodeInstruction> code
        ) => SurgeryStopTranspiler(code);

        internal void Execute(BotComponent b, string method)
        {
            bool r = false;
            ExecuteMedical(new(b), typeof(SAINSelfActionClass).GetMethod(method)!, ref r);
        }
    }
}

namespace UnityEngine
{
    public static class Mathf
    {
        public const float PI = (float)Math.PI;

        public static float Cos(float v) => (float)Math.Cos(v);

        public static float Sin(float v) => (float)Math.Sin(v);
    }

    public enum QueryTriggerInteraction
    {
        Ignore,
    }

    public struct RaycastHit
    {
        public Vector3 point,
            normal;
    }

    public static class Physics
    {
        public static bool Raycast(Vector3 a, Vector3 d, float max, int mask) =>
            Raycast(a, d, out _, max, mask, QueryTriggerInteraction.Ignore);

        public static bool Linecast(Vector3 a, Vector3 b, int mask) => false;

        public static int Rays;

        public static bool Raycast(
            Vector3 start,
            Vector3 dir,
            out RaycastHit hit,
            float distance,
            int mask,
            QueryTriggerInteraction trigger
        )
        {
            Rays++;
            hit = new();
            return false;
        }
    }
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
        public NavMeshPathStatus status;
        public Vector3[] corners = [];
    }

    public struct NavMeshHit
    {
        public Vector3 position;
    }

    public static class NavMesh
    {
        public const int AllAreas = -1;

        public static bool CalculatePath(Vector3 a, Vector3 b, int mask, NavMeshPath p)
        {
            p.corners = [a, b];
            p.status = NavMeshPathStatus.PathComplete;
            return true;
        }

        public static bool SamplePosition(
            Vector3 point,
            out NavMeshHit hit,
            float radius,
            int areas
        )
        {
            hit = new() { position = point };
            return true;
        }
    }
}

public static class LayersMaskController
{
    public const int HighPolyWithTerrainMask = 1;
    public const int Foliage = 128;
}

namespace EFT
{
    public class Movement
    {
        public bool CanSprint = true;
        public bool IsGrounded = true;
    }

    public class Memory
    {
        public bool IsUnderFire;
    }

    public class ShootData
    {
        public bool Shooting;
        public int Ends;

        public void EndShoot()
        {
            Shooting = false;
            Ends++;
        }
    }

    public class Patrol
    {
        public int Pauses;

        public void Pause() => Pauses++;
    }
}

namespace EFT.InventoryLogic
{
    public class Weapon : Item
    {
        public object[] Chambers = new object[1];
        public int ChamberAmmoCount;
        public Magazine Magazine = new();

        public Magazine GetCurrentMagazine() => Magazine;
    }

    public class Magazine
    {
        public int Count = 30,
            MaxCount = 30;
    }
}

namespace SAIN.Components
{
    public class Squad
    {
        public Dictionary<string, BotComponent> Members = new();
        public BotComponent? LeaderComponent;
        public bool IAmLeader = true;
    }

    public class Flashed
    {
        public bool IsFlashed;
    }

    public class CoverData
    {
        public bool IsBad;
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

        public int Moving,
            Points,
            Random;
        public Vector3 LastPoint;

        public void LookToMovingDirection() => Moving++;

        public void LookToPoint(Vector3 p)
        {
            Points++;
            LastPoint = p;
        }

        public void LookToRandomPosition() => Random++;
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

namespace SAIN.SAINComponent.Classes.Mover
{
    public interface IBotPathData { }

    public class BotPathDataManual : IBotPathData
    {
        public int Stops;

        public void Stop(bool warp, string why)
        {
            Stops++;
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
        None,
        Combat,
        Squad,
        Extract,
        Run,
        AvoidThreat,
        Flashed,
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
        private readonly string name;
        private readonly SAIN.Models.Enums.ESAINLayer layer;

        public SAINLayer(
            BotOwner owner,
            int priority,
            string name,
            SAIN.Models.Enums.ESAINLayer layer
        )
        {
            BotOwner = owner;
            this.name = name;
            this.layer = layer;
        }

        protected bool GetBotComponent()
        {
            SAIN.SAINEnableClass.GetSAIN(BotOwner.ProfileId, out var b);
            Bot = b!;
            return b != null;
        }

        protected void CheckActiveChanged(bool v)
        {
            if (Bot == null)
                return;
            if (v)
                Bot.ActiveLayer = layer;
            else if (Bot.ActiveLayer == layer)
                Bot.ActiveLayer = SAIN.Models.Enums.ESAINLayer.None;
            Bot.SAINLayersActive = Bot.ActiveLayer != SAIN.Models.Enums.ESAINLayer.None;
        }

        public string GetName() => name;

        public abstract bool IsActive();
        public abstract bool IsCurrentActionEnding();
        public abstract DrakiaXYZ.BigBrain.Brains.CustomLayer.Action GetNextAction();
    }

    public class BotAction
    {
        protected BotOwner BotOwner;
        protected BotComponent Bot => SAIN.SAINEnableClass.Bots[BotOwner.ProfileId];
        public int Starts,
            NativeSteering;

        public BotAction(BotOwner owner, string name)
        {
            BotOwner = owner;
        }

        public virtual void Start()
        {
            Starts++;
        }

        public virtual void Stop() { }

        public virtual void Update(DrakiaXYZ.BigBrain.Brains.CustomLayer.ActionData d) { }

        public virtual void OnSteeringTicked()
        {
            NativeSteering++;
        }
    }
}

namespace Comet.Client
{
    public sealed partial class Plugin
    {
        private System.Random jumpRandom = new(7);
        private static System.Reflection.MethodInfo combatStopPath =
            typeof(SAIN.SAINComponent.Classes.Mover.BotPathDataManual).GetMethod("Stop")!;
        internal bool Guard,
            Queued;

        private bool MedicalPostureGuard(BotComponent b) => Guard;

        private bool QueuedCombatMovement(BotComponent b) => Queued;

        internal void Observe(BotComponent b) => ObservePostCombat(new(b));

        internal void Cleanup(BotComponent b) =>
            EndPostCombat(b, states[b], "post-combat-raid-ended", true);
    }
}

namespace SAIN.Components
{
    public class HealthMemory
    {
        public HealthMemoryStatus Health = new();
    }

    public class HealthMemoryStatus
    {
        public EFT.ETagStatus HealthStatus = EFT.ETagStatus.Injured;
    }
}

namespace SAIN.SAINComponent.Classes.EnemyClasses
{
    public class EnemyPath
    {
        public float PathLength = 60;
    }
}

namespace SAIN.Components
{
    public class BotManagerComponent
    {
        public SAIN.Components.BotControllerSpace.Classes.BotHearingClass BotHearing = new();

        public void Activate(object world) { }

        public void Dispose() { }
    }
}

namespace SAIN.Components.PlayerComponentSpace
{
    public class PlayerComponent
    {
        public string ProfileId = "sound";
        public EFT.Player Player = new();

        public void OnMakingShot() { }
    }
}

namespace SAIN.Components.BotControllerSpace.Classes
{
    public class BotHearingClass
    {
        public event System.Action<
            SAIN.Preset.Shared.Enums.SAINSoundType,
            UnityEngine.Vector3,
            SAIN.Components.PlayerComponentSpace.PlayerComponent,
            float,
            float
        >? AISoundPlayed;

        public void Emit(
            SAIN.Preset.Shared.Enums.SAINSoundType t,
            UnityEngine.Vector3 p,
            SAIN.Components.PlayerComponentSpace.PlayerComponent s
        ) => AISoundPlayed?.Invoke(t, p, s, 0, 1);
    }
}

namespace UnityEngine
{
    public struct Quaternion(float angle)
    {
        public static Quaternion AngleAxis(float a, Vector3 axis) => new(a);

        public static Vector3 operator *(Quaternion q, Vector3 v)
        {
            double a = q.angle * Math.PI / 180;
            return new(
                (float)(v.x * Math.Cos(a) + v.z * Math.Sin(a)),
                v.y,
                (float)(v.z * Math.Cos(a) - v.x * Math.Sin(a))
            );
        }

        private float angle = angle;
    }
}

namespace UnityEngine
{
    public static class Random
    {
        public static float value = .99f;
        public static float Range(float min, float max) => min;
    }
}

namespace SAIN.Components
{
    public class Suppression
    {
        public bool Result;
        public int Calls;

        public bool TrySuppressAnyEnemy(params object[] args)
        {
            Calls++;
            return Result;
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

namespace UnityEngine
{
    public class GameObject
    {
        public int layer = 4;
    }

    public class Collider
    {
        public GameObject gameObject = new();
    }
}
