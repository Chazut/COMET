// Platform doubles only. The observer, request lifecycle and JSON generation are production source.
using Comet.Core;
using EFT;
using EFT.InventoryLogic;
using SAIN.Components;

namespace UnityEngine
{
    public static class Time
    {
        public static int frameCount;
        public static float time,
            realtimeSinceStartup;
    }

    public struct Vector3
    {
        public static float Distance(Vector3 a, Vector3 b) => 10;
    }
}

namespace HarmonyLib
{
    public class Harmony
    {
        public void Patch(
            object? x,
            HarmonyMethod? prefix = null,
            HarmonyMethod? postfix = null,
            HarmonyMethod? finalizer = null
        ) { }
    }

    public class HarmonyMethod
    {
        public int priority;

        public HarmonyMethod(Type t, string n) { }
    }

    public static class AccessTools
    {
        public static object? Method(Type t, string n) => null;

        public static object? PropertyGetter(Type t, string n) => null;
    }
}

namespace EFT.InventoryLogic
{
    public enum EquipmentSlot
    {
        FirstPrimaryWeapon,
        SecondPrimaryWeapon,
        Holster,
        ArmBand,
        Pockets,
        TacticalVest,
        Backpack,
        SecuredContainer,
    }

    public sealed class Result
    {
        public bool Succeeded = true;
        public object? Error;
    }

    public class Slot
    {
        public object Filters = new();
        public string? RequiredCaliber;
        public Item? Item;
        public Item? ContainedItem => Item;
        public bool Accept = true;

        public bool CanAccept(Item a) => Accept && (RequiredCaliber == null || a is Ammo ammo && ammo.Caliber == RequiredCaliber);
    }

    public class Item
    {
        public int StackObjectsCount = 30;
        public string Id = Guid.NewGuid().ToString("N"),
            ShortName = "Item",
            TemplateId = "template";
        public EquipmentSlot? Root;
        public bool Movable = true;
    }

    public class Cartridges
    {
        public Item? Last = new Ammo();
    }

    public class Magazine : Item
    {
        public Cartridges Cartridges = new();
        public bool AmmoCompatible = true;

        public int CompatibilityChecks;
        public bool IsAmmoCompatible(Slot[] chambers)
        {
            CompatibilityChecks++;
            return Count == 0 || Cartridges.Last == null || chambers.Length == 0 ||
                AmmoCompatible && chambers.Any(c => c.CanAccept(Cartridges.Last));
        }

        public int Count = 20,
            MaxCount = 20;

        public bool CheckCompatibility(Ammo a) => true;
    }

    public class Ammo : Item
    {
        public string Caliber = "Caliber556x45NATO";

        public Result CheckAction(object? o) => new();
    }

    public interface IWeapon { }

    public class Weapon : Item, IWeapon
    {
        public string AmmoCaliber = "Caliber556x45NATO";
        public EReloadMode ReloadMode;

        public enum EReloadMode
        {
            ExternalMagazine,
            ExternalMagazineWithInternalReloadSupport,
            InternalMagazine,
            OnlyBarrel,
        }

        public int ChamberAmmoCount = 1;
        public Magazine Magazine = new();
        public Slot[] Chambers = { new() };
        public Malf MalfState = new();
        public Slot MagSlot = new();

        public Magazine GetCurrentMagazine() => Magazine;

        public Slot GetMagazineSlot() => MagSlot;

        public class Malf
        {
            public string State = "None";
        }
    }

    public class Revolver : Weapon { }

    public class Container
    {
        public List<Item> Items = new();
    }

    public class ContainerCollection : Item
    {
        public List<Container> Containers = new();
    }

    public class InventoryEquipment
    {
        public Slot Belt = new();

        public Slot? GetSlot(EquipmentSlot s) => s == EquipmentSlot.ArmBand ? Belt : null;
    }

    public class Inventory
    {
        public InventoryEquipment Equipment = new();
    }

    public class InventoryController
    {
        public Inventory Inventory = new();
        public bool Fail;
        public int Scans;
        public List<Item> Reachable = new();

        public bool Examined(Item x) => true;

        public void GetReachableItemsOfTypeNonAlloc<T>(
            IList<T> items,
            Predicate<T>? predicate = null
        )
        {
            Scans++;
            if (Fail)
                throw new Exception("inventory unavailable");
            foreach (var x in Reachable.OfType<T>())
                if (predicate == null || predicate(x))
                    items.Add(x);
        }

        public void GetAcceptableItemsNonAlloc<T>(
            object slots,
            List<T> items,
            Func<T, bool>? predicate = null
        )
        {
            if (Fail)
                throw new Exception("inventory unavailable");
        }
    }

    public static class ItemManipulator
    {
        public static Result CheckMoveIgnoringTargetItem(
            Magazine m,
            Slot s,
            InventoryController i
        ) =>
            new()
            {
                Succeeded = m.Movable && s.Accept,
                Error = m.Movable ? "incompatible-slot" : "item-locked",
            };
    }
}

namespace EFT
{
    public enum EBodyPart
    {
        Common,
        Head,
    }

    public enum EDamageType
    {
        Bullet,
        Undefined,
    }

    public interface IPlayer
    {
        string ProfileId { get; }
        bool IsAI { get; }
    }

    public class Player : IPlayer
    {
        public string ProfileId { get; set; } = "pmc";
        public bool IsAI { get; set; } = true;
        public Health HealthController = new();
        public object? HandsController;
        public InventoryController InventoryController = new();
        public Inventory Inventory => InventoryController.Inventory;
        public Profile Profile = new();
        public bool Downed;

        public void ApplyDamageInfo() { }

        public void OnDead() { }

        public class FirearmController
        {
            public Weapon Item = new();
        }
    }

    public class Profile
    {
        public string Nickname = "PMC";
    }

    public class Health
    {
        public bool IsAlive = true;
        public double Current = 440;

        public Health GetBodyPartHealth(EBodyPart p, bool x) => this;
    }

    public class BotReload
    {
        public static object _availableEquipmentSlots = new();
        public Player _player = new();
        public Player.FirearmController? ShootController;
        public int Refilled;
        public bool FailFill;

        public Magazine? GetMagazineForReload() => null;

        public void AddAmmoToMagazines() { }

        public void AddAmmoToMag(Weapon w, Magazine m)
        {
            if (FailFill)
                throw new Exception("refill unavailable");
            Refilled++;
            m.Count = m.MaxCount;
        }
    }

    public class BotWeaponSelector
    {
        public Owner _owner = new();
        public bool Ready = true;

        public void TryChangeToSlot() { }
    }

    public class Owner
    {
        public class TriggerData { public float LastTriggerPressd; }
        public TriggerData ShootData = new();
        public Player GetPlayer = new();
        public Manager WeaponManager = new();
        public Memory Memory = new();
    }

    public class BotWeaponManager
    {
        public Weapon? CurrentWeapon;
        public BotWeaponSelector Selector = new();
        public float CriticalBulletCount;
        public bool HaveBullets => true;
    }

    public class Manager
    {
        public BotWeaponSelector Selector = null!;
    }

    public class Memory
    {
        public bool IsUnderFire;
    }
}

namespace EFT.Ballistics
{
    public class DamageInfo
    {
        public Bridge? Player;
        public EDamageType DamageType = EDamageType.Bullet;
    }

    public class Bridge
    {
        public IPlayer iPlayer = null!;
    }
}

namespace SAIN.Helpers
{
    public static class Names
    {
        public static string Localized(this string s) => s;
    }
}

namespace SAIN
{
    public static class SAINEnableClass
    {
        public static Dictionary<string, BotComponent> Bots = new();

        public static bool GetSAIN(string id, out BotComponent b) => Bots.TryGetValue(id, out b!);
    }
}

namespace SAIN.Components
{
    public class BotComponent
    {
        public bool Pmc = true,
            BotActive = true,
            IsDead,
            GameEnding,
            SAINLayersActive = true;
        public string ProfileId => Player.ProfileId;
        public Player Player = new();
        public Owner BotOwner = new();
        public object? CurrentAction = new HoldAction();
        public Decision Decision = new();
        public Enemy? GoalEnemy = new();
        public ShotData Shoot = new();
        public ManualData ManualShoot = new();
        public Info Info = new();
        public UnityEngine.Vector3 Position;

        public void ManualUpdate() { }
    }

    public class HoldAction { }

    public class Decision
    {
        public string CurrentCombatDecision = "Hold",
            CurrentSelfDecision = "None",
            CurrentSquadDecision = "None";
    }

    public class Enemy : SAIN.SAINComponent.Classes.EnemyClasses.Enemy { }

    public class Places
    {
        public UnityEngine.Vector3? LastKnownPosition = new UnityEngine.Vector3();
    }

    public class Info
    {
        public string Personality = "Normal";
    }
}

namespace Comet.Api
{
    public static class Decisions
    {
        public static List<string> Events = new();

        public static void Publish(string j, Action<Exception> cb) => Events.Add(j);
    }
}

namespace Comet.Client
{
    internal class Diagnostics
    {
        internal static bool Fail;

        internal static string Serialize(object value) =>
            Fail
                ? throw new Exception("observer failed")
                : Newtonsoft.Json.JsonConvert.SerializeObject(value, Json);

        public List<string> Events = new();
        public static Newtonsoft.Json.JsonSerializerSettings Json = new()
        {
            ContractResolver =
                new Newtonsoft.Json.Serialization.CamelCasePropertyNamesContractResolver(),
        };

        public void Write(string j) => Events.Add(j);

        public void WriteEssential(string j) => Write(j);
    }

    internal static class NativeAdapter
    {
        internal static bool IsSupportedAi(BotComponent b) => b.Pmc && b.Player.IsAI;
    }

    internal class BotState
    {
        internal Comet.Core.SightHold? SightEvent;
        internal readonly BotComponent Owner;
        internal string ProfileId => Owner.ProfileId;
        internal string Name = "PMC",
            Id = Guid.NewGuid().ToString("N"),
            ActiveLabel = "",
            Feature = "A01",
            Decision = "Hold",
            Expected = "Hold";
        internal Choice? Choice;
        internal readonly CombatJournalRuntime Journal = new();
        internal ReloadTimingRuntime ReloadTiming = new();
        internal SwitchIntent WeaponReturn = new();
        internal Recovery HandsRecovery = new();

        internal BotState(BotComponent b, CometConfig c, string s)
        {
            Owner = b;
        }
    }

    internal class ReloadTimingRuntime
    {
        internal bool InventoryFault;
        internal float BeltRefillAt;
        internal List<Magazine> BeltMagazines = new(),
            Magazines = new();
        internal List<Ammo> Ammo = new();
    }

    internal static class LoadoutAdapter
    {
        internal static EquipmentSlot? RootSlot(Item item, InventoryEquipment equipment) =>
            item.Root;
    }

    internal class SwitchIntent
    {
        public string Intent = "";
    }

    internal class Recovery
    {
        public bool Issuing;
    }

    internal class Log
    {
        public void LogWarning(string s) { }
    }

    public sealed partial class Plugin
    {
        private sealed record OpeningAim(Weapon Weapon, OpeningShot Shot);
        private readonly System.Runtime.CompilerServices.ConditionalWeakTable<SAIN.SAINComponent.Classes.EnemyClasses.Enemy, OpeningAim> openingShots = new();
        internal static Plugin? Instance;
        internal CometConfig? config = new();
        internal object? world = new();
        internal bool Authority = true,
            Sleep;
        internal string session = "raid";
        internal long sequence;
        internal Diagnostics diagnostics = new();
        internal Log Logger = new();
        internal Dictionary<BotComponent, BotState> states = new();

        private bool WeaponReturnState(
            BotWeaponSelector selector,
            out BotComponent b,
            out BotState s
        )
        {
            b = null!;
            s = null!;
            if (!SAIN.SAINEnableClass.GetSAIN(selector._owner.GetPlayer.ProfileId, out b))
                return false;
            s = JournalState(b.Player)!;
            return s != null;
        }

        internal bool Ghost(string id) => Sleep;

        internal static bool ReadDowned(Player p) => p.Downed;

        // H01/W01 ownership is exercised by ArbitrationHarness; this harness verifies its journal attribution.
        internal string TestOwnedService = "";

        private string OwnedPlanService(BotComponent b, BotState s, bool committed) =>
            b.SAINLayersActive ? TestOwnedService : "";

        private static bool GunSlot(EquipmentSlot slot) => true;

        internal Dictionary<(BotComponent, EquipmentSlot), Weapon> Guns = new();

        private static Weapon? SlotGun(BotComponent b, EquipmentSlot slot) =>
            Instance!.Guns.TryGetValue((b, slot), out var w) ? w : null;

        private static bool WeaponHandsReady(BotComponent b, Player.FirearmController? f) =>
            f != null && b.BotOwner.WeaponManager.Selector.Ready;

        private static bool OutcomeAction(BotComponent b, string action) =>
            b.CurrentAction?.GetType().Name == action + "Action";

        partial void EndJournalBot(BotState s, string reason);

        partial void FinishCombatJournal();

        internal void Track(BotComponent b) => TrackCombatJournal(b);

        internal void Sample(BotComponent b) => SampleCombatJournal(b, states[b]);

        internal void Stop(BotComponent b) => EndJournalBot(states[b], "test-pause");

        internal void End() => FinishCombatJournal();

        internal void Switch(BotComponent b, bool accepted, bool throws = false)
        {
            I01JournalWeaponHook.Before(
                b.BotOwner.WeaponManager.Selector,
                EquipmentSlot.Holster,
                out var s
            );
            if (throws)
                I01JournalWeaponHook.Fault(new Exception("native"), s);
            else
                I01JournalWeaponHook.After(accepted, s);
        }

        internal void Hit(Player victim, Player attacker, int damage)
        {
            var d = new EFT.Ballistics.DamageInfo { Player = new() { iPlayer = attacker } };
            I01JournalHitHook.Before(victim, d, out var s);
            victim.HealthController.Current -= damage;
            I01JournalHitHook.After(victim, d, EBodyPart.Head, s);
        }

        internal void Die(Player victim, IPlayer? attacker) =>
            JournalDeathObserved(victim, EDamageType.Bullet, attacker);
    }
}
