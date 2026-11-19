using SAIN.Components;
using SAIN.SAINComponent.Classes;
using UnityEngine;

public enum ESeason
{
    Summer,
    Autumn,
    Winter,
    Spring,
    AutumnLate,
    SpringEarly,
}

public class SeasonsController
{
    public static SeasonsController? Controller = new();
    public ESeason Season;
}

public static class LayersMaskController
{
    public const int Foliage = 1 << 26;
    public const int HighPolyWithTerrainMask = (1 << 12) | (1 << 11);
}

namespace UnityEngine
{
    public record struct Vector3(float x, float y, float z)
    {
        public static Vector3 up => new(0, 1, 0);

        public static Vector3 operator +(Vector3 a, Vector3 b) =>
            new(a.x + b.x, a.y + b.y, a.z + b.z);

        public static Vector3 operator -(Vector3 a, Vector3 b) =>
            new(a.x - b.x, a.y - b.y, a.z - b.z);

        public static Vector3 operator /(Vector3 a, float n) => new(a.x / n, a.y / n, a.z / n);

        public float magnitude => MathF.Sqrt(x * x + y * y + z * z);
    }

    public class GameObject
    {
        public int layer;
    }

    public class Collider
    {
        public GameObject gameObject = new();
        public string name = "test";
    }

    public enum QueryTriggerInteraction
    {
        Ignore,
        Collide,
    }

    public static class Mathf
    {
        public static float Min(float a, float b) => MathF.Min(a, b);
    }

    public static class Time
    {
        public static int frameCount;
        public static float time = 10;
        public static float realtimeSinceStartup => time;
    }

    public static class Physics
    {
        public static int Calls;
        public static Func<Vector3, bool> Inside = _ => false;
        public static Collider[]? LastBuffer;
        public static bool Fail;
        public static int SolidQueries;
        public static (Vector3 Center, int Layer, bool Trigger)[] Obstacles = [];

        public static bool Raycast(
            Vector3 origin,
            Vector3 direction,
            float distance,
            int mask,
            QueryTriggerInteraction triggers
        )
        {
            if (
                mask != LayersMaskController.HighPolyWithTerrainMask
                || triggers != QueryTriggerInteraction.Ignore
            )
                throw new Exception("Unexpected solid cover mask or trigger policy");
            SolidQueries++;
            foreach (var obstacle in Obstacles)
            {
                if (obstacle.Trigger || ((1 << obstacle.Layer) & mask) == 0)
                    continue;
                // Intersect a small sphere with the finite, directed ray.
                var offset = obstacle.Center - origin;
                float along =
                    offset.x * direction.x + offset.y * direction.y + offset.z * direction.z;
                float perpendicularSq =
                    offset.x * offset.x + offset.y * offset.y + offset.z * offset.z - along * along;
                if (perpendicularSq > .04f)
                    continue;
                float entry = along - MathF.Sqrt(.04f - perpendicularSq);
                if (entry >= 0 && entry <= distance)
                    return true;
            }
            return false;
        }

        public static int OverlapSphereNonAlloc(
            Vector3 point,
            float radius,
            Collider[] buffer,
            int mask,
            QueryTriggerInteraction triggers
        )
        {
            if (
                mask != LayersMaskController.Foliage
                || triggers != QueryTriggerInteraction.Collide
                || radius > .05f
            )
                throw new Exception("Unexpected occupancy query");
            Calls++;
            LastBuffer = buffer;
            buffer[0] = new Collider();
            if (Fail)
                throw new Exception("Physics test fault");
            return Inside(point) ? 1 : 0;
        }
    }
}

namespace UnityEngine.AI
{
    public struct NavMeshHit
    {
        public Vector3 position;
    }
}

namespace SAIN.Components
{
    public class BotComponent
    {
        public bool BotActive = true,
            IsDead,
            GameEnding,
            SAINLayersActive = true,
            Supported = true;
        public Enemy? GoalEnemy = new();
        public string ProfileId = "test";
        public SAINCoverClass Cover;

        public BotComponent()
        {
            Cover = new() { Bot = this };
        }
    }

    public class Enemy
    {
        public Vector3? LastKnownPosition = new Vector3(10, 0, 0);
    }
}

namespace SAIN.Models.Enums
{
    public enum ESprintUrgency
    {
        Low,
        High,
    }
}

namespace SAIN.SAINComponent.SubComponents.CoverFinder
{
    public class CoverData
    {
        public bool IsBad;
    }

    public class CoverPoint
    {
        public Collider Collider = new();
        public Vector3 Position;
        public CoverData CoverData = new();
        public bool Spotted,
            BotInThisCover = true;
    }

    public class CoverAnalyzer
    {
        public BotComponent Bot = new();
    }
}

namespace SAIN.SAINComponent.Classes
{
    using SAIN.SAINComponent.SubComponents.CoverFinder;

    public class SAINCoverClass
    {
        public BotComponent Bot = null!;
        public List<CoverPoint> CoverPoints = new();
        public CoverPoint? CoverInUse,
            CoverPoint_MovingTo;
    }
}

namespace SAIN.SAINComponent.Classes.Mover
{
    public class SAINMoverClass
    {
        public BotComponent Bot = new();
    }
}

namespace HarmonyLib
{
    public static class AccessTools
    {
        public static object Method(Type t, string s, Type[]? args = null) => s;
    }

    public class HarmonyMethod
    {
        public HarmonyMethod(Type t, string s) { }
    }

    public class Harmony
    {
        public void Patch(object m, HarmonyMethod? prefix = null, HarmonyMethod? postfix = null) { }
    }
}

namespace Comet.Client
{
    public class Config
    {
        public bool Enabled = true;
        public int Revision = 3;
    }

    internal class BotState
    {
        internal SeasonalCoverRuntime SeasonalCover = new();
        internal CoverExposureRuntime CoverExposure = new();
        internal string ProfileId = "test",
            Name = "Test",
            Id = "instance";
    }

    internal static class NativeAdapter
    {
        internal static bool IsSupportedAi(BotComponent b) => b.Supported;
    }

    internal static class Diagnostics
    {
        internal static bool Fail;

        internal static string Serialize(object value) =>
            Fail
                ? throw new Exception("observer fault")
                : System.Text.Json.JsonSerializer.Serialize(value);
    }

    internal class Writer
    {
        public int Count;
        public string? Last;

        public void Write(string json)
        {
            Count++;
            Last = json;
        }
    }

    internal static class Api
    {
        internal static class Decisions
        {
            public static int Count;

            public static void Publish(string json, Action<Exception> onError)
            {
                Count++;
            }
        }
    }

    public sealed partial class Plugin
    {
        private static Plugin? Instance;
        private Config? config = new();
        private bool Authority = true,
            IsGhost;
        private readonly Dictionary<BotComponent, BotState> states = new();
        private string session = "session";
        private long sequence;
        private Writer? diagnostics = new();

        private bool Ghost(string id) => IsGhost;
    }
}
