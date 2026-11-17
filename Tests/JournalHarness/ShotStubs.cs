using EFT.InventoryLogic;
using SAIN.Components;

namespace SAIN.Models.Enums
{
    public enum EShootReason { None, Suppress, SquadSuppressing, WalkToCoverSuppress, Blindfire }
}
namespace SAIN.Components.PlayerComponentSpace
{
    public class PlayerComponent
    {
        public BotComponent? BotComponent;
        public Flashlight Flashlight = new();
        public void OnMakingShot() { }
    }
    public class Flashlight { public bool WhiteLight, Laser; }
}
namespace SAIN.SAINComponent.Classes.EnemyClasses
{
    public class Enemy
    {
        public string EnemyProfileId = "enemy";
        public bool IsVisible, InLineOfSight, CanShoot, Seen;
        public float TimeSinceLastKnownUpdated = 1, TimeSinceSeen;
        public Places KnownPlaces = new();
        public Vision Vision = new();
        public Aim AimTarget = new();
        public SAIN.Components.PlayerComponentSpace.PlayerComponent EnemyPlayerComponent = new();
    }
    public class Vision { public Parts EnemyParts = new(); }
    public class Parts { public bool CanBeSeen; public Part[] PartsArray = []; }
    public class Part { public bool CanBeSeen, CanShoot; }
    public class Aim { public string? ChosenPart; }
}
namespace SAIN.Components
{
    public class ShotData { public SAIN.SAINComponent.Classes.EnemyClasses.Enemy? LastShotEnemy; }
    public class ManualData
    {
        public SAIN.Models.Enums.EShootReason Reason;
        public SAIN.SAINComponent.Classes.EnemyClasses.Enemy? ManualShootEnemy;
    }
}
namespace Comet.Client
{
    public sealed partial class Plugin
    {
        private static readonly System.Reflection.FieldInfo prefireEnemy = typeof(ManualData).GetField("ManualShootEnemy")!;
        internal void Shot(BotComponent b) => JournalShotObserved(new() { BotComponent = b }, (IWeapon)((EFT.Player.FirearmController)b.Player.HandsController!).Item);
    }
}
