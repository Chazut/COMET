using System.Runtime.CompilerServices;
using EFT;
using HarmonyLib;
using SAIN.Components;
using SAIN.SAINComponent;
using SAIN.SAINComponent.Classes.EnemyClasses;
using SAIN.SAINComponent.Classes.WeaponFunction;
using UnityEngine;

namespace UnityEngine
{
    public readonly record struct Vector3(float x, float y, float z)
    {
        public float magnitude => MathF.Sqrt(x * x + y * y + z * z);
        public Vector3 normalized => magnitude > 0 ? this * (1 / magnitude) : default;

        public static Vector3 operator +(Vector3 a, Vector3 b) =>
            new(a.x + b.x, a.y + b.y, a.z + b.z);

        public static Vector3 operator -(Vector3 a, Vector3 b) =>
            new(a.x - b.x, a.y - b.y, a.z - b.z);

        public static Vector3 operator *(Vector3 a, float n) => new(a.x * n, a.y * n, a.z * n);
    }

    public struct QueryParameters
    {
        public int layerMask,
            hitTriggers;
    }

    public struct RaycastCommand(
        Vector3 from,
        Vector3 direction,
        QueryParameters query,
        float distance
    )
    {
        public Vector3 from = from,
            direction = direction;
        public QueryParameters queryParameters = query;
        public float distance = distance;
    }

    public readonly record struct Ray(Vector3 from, Vector3 direction);

    public struct RaycastHit
    {
        public Vector3 point;
    }

    public static class Physics
    {
        public static float Wall = float.PositiveInfinity;
        public static int Calls;

        public static bool Raycast(Ray ray, out RaycastHit hit, float distance, int mask)
        {
            Calls++;
            float end = ray.from.x + ray.direction.normalized.x * distance;
            bool blocked = Wall > Math.Min(ray.from.x, end) && Wall <= Math.Max(ray.from.x, end);
            hit = new() { point = new(Wall, 0, 0) };
            return blocked;
        }
    }

    public static class Time
    {
        public static float time,
            realtimeSinceStartup;
        public static double timeAsDouble => time;
        public static float fixedDeltaTime => .02f;
    }

    public static class Mathf
    {
        public static float Clamp01(float f) => Math.Clamp(f, 0, 1);
    }
}

public static class LayersMaskController
{
    public const int HighPolyWithTerrainMask = 42;
}

namespace EFT
{
    public enum EBotState
    {
        Active,
        Inactive,
    }

    public enum BotStandByType
    {
        active,
        sleep,
    }

    public sealed class StandBy
    {
        public BotStandByType StandByType;
    }

    public sealed class BotOwner(string id)
    {
        public string ProfileId = id;
        public EBotState BotState;
        public StandBy StandBy = new();
    }

    public sealed class Grenade;
}

namespace SAIN.Components.PlayerComponentSpace
{
    public sealed class PlayerComponent
    {
        public bool IsActive = true,
            IsAI = true,
            Alive = true;
        public BotOwner? BotOwner;
    }
}

namespace SAIN.Components
{
    public sealed class BotComponent(string id = "pmc")
    {
        public string ProfileId = id;
        public bool Supported = true,
            BotActive = true,
            IsDead,
            GameEnding,
            IsCheater;
        public Vector3 Position;
        public PlayerComponentSpace.PlayerComponent PlayerComponent = new() { BotOwner = new(id) };
    }

    public sealed class GrenadeController
    {
        public readonly List<GrenadeReactionClass> Observers = new();
        public bool ThrowAfter;
        public Action? Nested;

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void GrenadeThrown(Grenade grenade, Vector3 position, Vector3 force, float mass)
        {
            Vector3 point = SAIN.Helpers.Vector.DangerPoint(position, force, mass);
            Nested?.Invoke();
            foreach (var observer in Observers)
                observer.EnemyGrenadeThrown(grenade, point, "thrower");
            if (ThrowAfter)
                throw new InvalidOperationException("native failure");
        }

        public void Throw(Grenade grenade) => GrenadeThrown(grenade, default, new(10, 0, 0), 1);
    }
}

namespace SAIN
{
    public static class SAINEnableClass
    {
        public static readonly Dictionary<string, BotComponent> Bots = new();

        public static int Lookups;
        public static bool GetSAIN(string id, out BotComponent bot)
        {
            Lookups++;
            return Bots.TryGetValue(id, out bot!);
        }
    }

    public static class SAINPlugin
    {
        public static Preset LoadedPreset = new();
    }

    public sealed class Preset
    {
        public Globals GlobalSettings = new();
    }

    public sealed class Globals
    {
        public Shoot Shoot = new();
    }

    public sealed class Shoot
    {
        public float BOT_RECOIL_DECAY_COEF = .3f;
    }
}

namespace SAIN.SAINComponent
{
    public class BotBase(BotComponent bot)
    {
        public BotComponent Bot = bot;
    }
}

namespace SAIN.SAINComponent.Classes.EnemyClasses
{
    public sealed class Enemy(BotComponent bot) : BotBase(bot)
    {
        public SAIN.Components.PlayerComponentSpace.PlayerComponent PlayerComponent =>
            Bot.PlayerComponent;
        public SAIN.Components.PlayerComponentSpace.PlayerComponent EnemyPlayerComponent = new()
        {
            BotOwner = new("enemy"),
        };
        public bool IsAI => EnemyPlayerComponent.IsAI;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static bool IsEnemyActive(Enemy enemy)
        {
            if (enemy == null || !enemy.PlayerComponent.IsActive)
                return false;
            var owner = enemy.EnemyPlayerComponent.BotOwner;
            return !enemy.IsAI
                || owner != null
                    && owner.BotState == EBotState.Active
                    && owner.StandBy.StandByType == BotStandByType.active;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static bool IsEnemyValid(
            string botProfileId,
            SAIN.Components.PlayerComponentSpace.PlayerComponent enemyPlayerComp
        )
        {
            if (enemyPlayerComp == null || !enemyPlayerComp.Alive)
                return false;
            var owner = enemyPlayerComp.BotOwner;
            if (enemyPlayerComp.IsAI && owner == null)
            {
                if (owner == null)
                    return false;
                if (owner.ProfileId == botProfileId)
                    return false;
            }
            return true;
        }
    }

    public readonly record struct BotVisiblePathNode(Vector3 Point);

    public sealed class SAINEnemyPath(BotComponent bot) : BotBase(bot)
    {
        private Vector3? _enemyLastPosChecked;
        private Vector3 _botLastPosChecked;
        private float _calcPathTime;
        public Vector3 Target;
        public bool Known = true,
            InRange = true;
        public Vector3? Cached => _enemyLastPosChecked;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public bool ShallCalcNewPath(float now)
        {
            if (!Known || !InRange)
                return false;
            if (_enemyLastPosChecked == Target && _botLastPosChecked == Bot.Position)
                return false;
            _enemyLastPosChecked = Target;
            _botLastPosChecked = Bot.Position;
            if (_calcPathTime + 1 > now)
                return false;
            _calcPathTime = now;
            return true;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void Clear()
        {
            _calcPathTime = 0;
        }
    }
}

namespace SAIN.Types.Jobs
{
    public struct PathVisionJob
    {
        public Enemy Enemy;
        public RaycastCommand[] Commands;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public PathVisionJob(
            BotVisiblePathNode[] nodes,
            int count,
            Vector3 origin,
            Enemy enemy,
            QueryParameters query
        )
        {
            Enemy = enemy;
            Commands = new RaycastCommand[count];
            for (int i = 0; i < count; i++)
                Commands[i] = new(origin, nodes[i].Point - origin, query, 1);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public PathVisionJob(
            List<BotVisiblePathNode> nodes,
            Vector3 origin,
            Enemy enemy,
            QueryParameters query
        )
        {
            Enemy = enemy;
            Commands = new RaycastCommand[nodes.Count];
            for (int i = 0; i < nodes.Count; i++)
                Commands[i] = new(origin, nodes[i].Point - origin, query, 1);
        }
    }
}

namespace SAIN.Helpers
{
    public static class Vector
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static Vector3 DangerPoint(Vector3 position, Vector3 force, float mass)
        {
            Vector3 target = position + force * (1 / mass);
            CheckThreePoints(position, (position + target) * .5f, target, out var point);
            return point;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool CheckThreePoints(
            Vector3 from,
            Vector3 midPoint,
            Vector3 target,
            out Vector3 hitPos
        )
        {
            var first = midPoint - from;
            var wrong = midPoint - target;
            if (
                Physics.Raycast(new(from, first), out var hit, first.magnitude, 42)
                || Physics.Raycast(new(midPoint, wrong), out hit, wrong.magnitude, 42)
            )
            {
                hitPos = hit.point;
                return false;
            }
            hitPos = target;
            return true;
        }
    }
}

namespace SAIN.SAINComponent.Classes.WeaponFunction
{
    public sealed class GrenadeReactionClass(BotComponent bot) : BotBase(bot)
    {
        public Vector3 Point;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void EnemyGrenadeThrown(Grenade grenade, Vector3 dangerPoint, string profileId) =>
            Point = dangerPoint;
    }

    public sealed class Recoil(BotComponent bot) : BotBase(bot)
    {
        private float _currentRecoilHorizAngle,
            _currentRecoilVertAngle;
        private bool _recoilFinished;
        public float H => _currentRecoilHorizAngle;
        public float V => _currentRecoilVertAngle;
        public bool Finished => _recoilFinished;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void WeaponShot(float h, float v)
        {
            _currentRecoilHorizAngle = h;
            _currentRecoilVertAngle = v;
            _recoilFinished = false;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void CalcRecoilDecay()
        {
            if (_recoilFinished)
                return;
            float retention =
                1
                - Time.fixedDeltaTime
                    * SAIN.SAINPlugin.LoadedPreset.GlobalSettings.Shoot.BOT_RECOIL_DECAY_COEF;
            _currentRecoilHorizAngle *= retention;
            _currentRecoilVertAngle *= retention;
            if (_currentRecoilHorizAngle <= .001 && _currentRecoilVertAngle < .001)
            {
                _currentRecoilHorizAngle = _currentRecoilVertAngle = 0;
                _recoilFinished = true;
            }
        }
    }
}

namespace SAIN.SAINComponent.Classes.Mover
{
    public sealed class PoseClass(BotComponent bot) : BotBase(bot)
    {
        public float Elapsed;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void ManualUpdate()
        {
            float dt = Time.fixedDeltaTime;
            Elapsed += dt;
        }
    }

    public sealed class LeanClass(BotComponent bot) : BotBase(bot)
    {
        public bool Reset;
        public float Elapsed;

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void SetTilt()
        {
            if (Reset)
                Elapsed += Time.fixedDeltaTime;
            else
                Elapsed += Time.fixedDeltaTime;
        }

        public void Tick() => SetTilt();
    }
}

namespace Comet.Core
{
    public class CometConfig
    {
        public bool Enabled = true;
        public long Revision = 1;
    }
}

namespace Comet.Client
{
    public static class NativeAdapter
    {
        public static int ScopeChecks;
        public static bool IsSupportedAi(BotComponent b)
        {
            ScopeChecks++;
            return b.Supported;
        }
    }

    public sealed class Diagnostic
    {
        public bool Quiet;
        public readonly List<string> Lines = new();

        public void Write(string s)
        {
            if (!Quiet)
                Lines.Add(s);
        }

        public void WriteEssential(string s) => Lines.Add(s);
    }

    public static class Diagnostics
    {
        public static string Serialize(object value) =>
            System.Text.Json.JsonSerializer.Serialize(value);
    }

    public static class Api
    {
        public static class Decisions
        {
            public static readonly List<string> Lines = new();

            public static void Publish(string s, Action<Exception> error) => Lines.Add(s);
        }
    }

    public sealed partial class Plugin
    {
        internal static Plugin? Instance;
        internal object? world = new();
        internal Comet.Core.CometConfig? config = new();
        internal bool Authority = true,
            IsGhost;
        internal string session = "test";
        internal long sequence;
        internal Diagnostic diagnostics = new();

        public Plugin()
        {
            Instance = this;
            SAIN.SAINEnableClass.Bots.Clear();
        }

        internal static void Patch() => PatchLiquidwarpFixes(new Harmony("native-fix-harness"));

        private bool Ghost(string id) => IsGhost;

        private static void WarnFaultSink(Exception e) => throw e;

        internal void Flush(bool final = false) => FlushNativeFixes(final);

        internal void Clear() => ClearNativeFixes();

        internal long[] Counts => nativeFixCounts;
        internal bool ContextActive => grenadePrediction != null || correctingGrenadeSegment;
    }
}
