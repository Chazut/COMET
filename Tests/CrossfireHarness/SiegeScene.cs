using Audio.SpatialSystem;
using Comet.Client;
using EFT;
using SAIN.Components;
using SAIN.Preset.Shared.Enums;
using UnityEngine;

public class SpatialAudioRoom : UnityEngine.Object
{
    public readonly GameObject gameObject = new();
    public class RoomConnection
    {
        public SpatialAudioRoom connectedRoom = null!;
        public BaseSpatialAudioPortal[] connectingPortals = [];
    }

    public bool IsValid = true,
        IsOutdoor;
    public float RoomSize = 1;
    public Bounds Bounds = new();
    public BoxCollider[] Colliders = [];
    public List<RoomConnection> roomConnections = [];
}

namespace Audio.SpatialSystem
{
    public class SpatialAudioSystem
    {
        public static SpatialAudioSystem? Instance;
        private AudioRoomStorage? _audioRoomStorage;
        internal void Publish(AudioRoomStorage? storage) => _audioRoomStorage = storage;
    }

    public class AudioRoomStorage
    {
        public List<SpatialAudioRoom> _validSpatialAudioRooms = [];
    }

    public class SpatialAudioCrossSceneGroup
    {
        public static readonly List<SpatialAudioCrossSceneGroup> AllCrossGroups = [];
        public List<SpatialAudioRoom> AudioRooms = [];
    }

    public class BaseSpatialAudioPortal
    {
        public enum PortalType
        {
            Opening,
            Static,
            Window,
        }

        public PortalType portalType;
        public SpatialAudioRoom FrontRoom = null!,
            BackRoom = null!;
        public BoxCollider portalCollider = new();
        public short ID;
    }

    public class SpatialAudioPortal : BaseSpatialAudioPortal
    {
        public string DoorID = "";
    }
}

namespace UnityEngine
{
    public class Object
    {
        private static int nextId;
        private readonly int instanceId = ++nextId;
        public int GetInstanceID() => instanceId;

        public static T[] FindObjectsOfType<T>() =>
            throw new InvalidOperationException("S11 performed a global Unity object search");
    }

    public class GameObject
    {
        public bool activeInHierarchy = true;
    }

    public class Bounds
    {
        public int Checks;
        public bool Contains(Vector3 _)
        {
            Checks++;
            return true;
        }
    }

    public class BoxCollider
    {
        public Vector3 size = new(2, 2, 1),
            center;
        public BoxTransform transform = new();
    }

    public class BoxTransform
    {
        public Vector3 position,
            lossyScale = new(1, 1, 1),
            forward = new(0, 0, 1);

        public Vector3 InverseTransformPoint(Vector3 p) => p - position;

        public Vector3 TransformPoint(Vector3 p) => p + position;
    }

    public static partial class Mathf
    {
        public static float Abs(float n) => MathF.Abs(n);
    }

    public static partial class Physics
    {
        public static bool Linecast(
            Vector3 eye,
            Vector3 point,
            out RaycastHit hit,
            int mask,
            QueryTriggerInteraction query
        )
        {
            hit = new() { collider = new() };
            return Linecast(eye, point, mask, query);
        }
    }
}

namespace UnityEngine.SceneManagement
{
    public static class SceneManager
    {
        public static int sceneCount = 1;
    }
}

namespace EFT
{
    public class GameWorld
    {
        public Dictionary<string, EFT.Interactive.Door> Doors = new();

        public object? FindDoor(string id) => Doors.GetValueOrDefault(id);
    }
}

namespace EFT.Interactive
{
    public partial class Door
    {
        public string Id = "";
    }
}

namespace SAIN.Components
{
    public partial class TransformData
    {
        public Vector3 EyePosition;
    }

    public partial class BotComponent
    {
        public StubAction? CurrentAction;
    }

    public class StubAction
    {
        public string Name = "";
    }

    public partial class Squad
    {
        public BotComponent? LeaderComponent;
    }

    public partial class HealthMemory
    {
        public Location Location = new();
    }

    public class Location
    {
        public bool IsIndoors;
    }
}

namespace SAIN.SAINComponent.Classes.EnemyClasses
{
    public partial class Places
    {
        public VisualPlace? LastSeenPlace,
            LastSquadSeenPlace;
    }

    public record VisualPlace(Vector3 Position, float At)
    {
        public float TimeSincePositionUpdated => Time.time - At;
    }
}

namespace Comet.Client
{
    internal partial class BotState
    {
        public readonly BotComponent Owner;
        internal SiegeSquad? Siege;
        internal SiegeExit? SiegeExit;
        internal float FaultUntil;
        internal string ActiveLabel = "";
        internal SiegeService PlanService = new();
    }

    internal class SiegeService
    {
        public bool Active;
    }

    internal static partial class Api
    {
        public static partial class Decisions
        {
            public static void Set(string id, string label) => SetCrossfire(id, label);
        }
    }

    public sealed partial class Plugin
    {
        partial void ClearSieges();

        internal bool TestSiege(BotComponent b)
        {
            var d = ECombatDecision.None;
            bool accepted = false;
            return HoldForSiege(b, states[b], ref d, ref accepted)
                && d == ECombatDecision.Freeze
                && accepted;
        }

        internal void TickSiege(BotComponent b) => SampleSiege(b, states[b]);

        internal bool TestSiegeGaze(BotComponent b, out Vector3 gaze) =>
            SiegeGaze(b, states[b], out gaze);

        internal void ClearSiegeScene() => ClearSieges();
    }
}
