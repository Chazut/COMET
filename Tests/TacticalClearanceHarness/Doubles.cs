using Comet.Core;
using EFT;
using SAIN.Components;
using UnityEngine;

namespace UnityEngine
{
    public struct Vector3(float x, float y, float z)
    {
        public float x = x,
            y = y,
            z = z;

        public static float Distance(Vector3 a, Vector3 b) => (a - b).magnitude;

        public static Vector3 up => new(0, 1, 0);
        public float sqrMagnitude => x * x + y * y + z * z;
        public float magnitude => (float)Math.Sqrt(sqrMagnitude);
        public Vector3 normalized => magnitude > 0 ? this * (1 / magnitude) : default;

        public static Vector3 operator +(Vector3 a, Vector3 b) =>
            new(a.x + b.x, a.y + b.y, a.z + b.z);

        public static Vector3 operator -(Vector3 a, Vector3 b) =>
            new(a.x - b.x, a.y - b.y, a.z - b.z);

        public static Vector3 operator *(Vector3 a, float f) => new(a.x * f, a.y * f, a.z * f);
    }

    public static class Mathf
    {
        public static float Clamp01(float x) => Math.Clamp(x, 0, 1);
    }

    public class Collider
    {
        public object? Owner;
        public EFT.Interactive.Door? Door;
        public int Mask = 1;
        public float Bottom = 0;

        public T? GetComponentInParent<T>()
            where T : class => Door as T;
    }

    public struct RaycastHit
    {
        public Collider collider;
    }

    public enum QueryTriggerInteraction
    {
        Ignore,
    }

    public static class Physics
    {
        public static bool Raycast(Vector3 a, Vector3 b, float d, int mask) =>
            Cast.Any(c => (c.Mask & mask) != 0);

        public static bool Linecast(Vector3 a, Vector3 b, int mask) =>
            Cast.Any(c => (c.Mask & mask) != 0);

        public static readonly List<Collider> Overlap = new(),
            Cast = new();
        public static int Queries;
        public static bool Throw;

        public static int OverlapCapsuleNonAlloc(
            Vector3 a,
            Vector3 b,
            float radius,
            Collider[] output,
            int mask,
            QueryTriggerInteraction trigger
        )
        {
            Queries++;
            if (Throw)
                throw new InvalidOperationException("unavailable");
            int n = 0;
            foreach (var c in Overlap)
            {
                if ((c.Mask & mask) == 0 || c.Bottom > b.y + radius)
                    continue;
                if (n == output.Length)
                    break;
                output[n++] = c;
            }
            return n;
        }

        public static int CapsuleCastNonAlloc(
            Vector3 a,
            Vector3 b,
            float radius,
            Vector3 dir,
            RaycastHit[] output,
            float distance,
            int mask,
            QueryTriggerInteraction trigger
        )
        {
            Queries++;
            int n = 0;
            foreach (var c in Cast)
            {
                if ((c.Mask & mask) == 0 || c.Bottom > b.y + radius)
                    continue;
                if (n == output.Length)
                    break;
                output[n++] = new() { collider = c };
            }
            return n;
        }

        public static int RaycastNonAlloc(
            Vector3 a,
            Vector3 dir,
            RaycastHit[] output,
            float distance,
            int mask,
            QueryTriggerInteraction trigger
        )
        {
            Queries++;
            if (Throw)
                throw new InvalidOperationException("unavailable");
            int n = 0;
            foreach (var c in Cast)
            {
                if ((c.Mask & mask) == 0)
                    continue;
                if (n == output.Length)
                    break;
                output[n++] = new() { collider = c };
            }
            return n;
        }

        public static void Reset()
        {
            Overlap.Clear();
            Cast.Clear();
            Queries = 0;
            Throw = false;
            UnityEngine.AI.NavMesh.Blocked = false;
        }
    }
}

namespace UnityEngine.AI
{
    public static class NavMesh
    {
        public const int AllAreas = -1;
        public static bool Blocked;

        public static bool Raycast(Vector3 a, Vector3 b, out object? hit, int mask)
        {
            hit = null;
            return Blocked;
        }
    }
}

namespace EFT
{
    public static class LayersMaskController
    {
        public const int PlayerStaticCollisionsMask = 1,
            HighPolyWithTerrainMask = 2,
            PlayerMask = 4,
            PlayerStaticDoorMask = 8;
    }
}

namespace SAIN.Components
{
    public class BotComponent
    {
        public object Player = new();
        public Vector3 Position;
        public Owner BotOwner = new();
        public SAIN.SAINComponent.Classes.EnemyClasses.Enemy? GoalEnemy = new();
    }
}

namespace SAIN.SAINComponent.Classes.EnemyClasses
{
    public class Enemy
    {
        public string EnemyProfileId = "";
        public SAIN.Components.EnemyPath Path = new();
    }
}

namespace Comet.Client
{
    public sealed partial class Plugin
    {
        private readonly DoorWorkBudget doorBudget = new();

        private static bool ValidPoint(Vector3 p) =>
            float.IsFinite(p.x) && float.IsFinite(p.y) && float.IsFinite(p.z);

        private static bool JumpObstacle(Collider c, BotComponent b) =>
            c != null && !ReferenceEquals(c.Owner, b.Player);

        public string Check(BotComponent b, Vector3 end, float pose = 1, bool overhead = false) =>
            TacticalVolume(b, default, end, overhead, pose);

        public bool Sight(BotComponent b, Vector3 end) => TacticalSightline(b, default, end);

        public string Entry(BotComponent b, EFT.Interactive.Door door, bool sweep = false) =>
            TacticalDoorClearance(
                b,
                new(0, .4f, 0),
                new(0, 1.45f, 0),
                .25f,
                door,
                sweep ? new(1, 0, 0) : default
            );

        public bool Empty() =>
            clearanceSmall.All(x => x == null)
            && clearanceLarge.All(x => x == null)
            && clearanceCastSmall.All(x => x.collider == null)
            && clearanceCastLarge.All(x => x.collider == null);

        public static bool Contact(object? a, object? b) => SameTacticalContact(a, b);
    }
}
