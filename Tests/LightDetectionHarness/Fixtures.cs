// Detached physics/SAIN fixture. The adapter itself is compiled from production.
// RaycastJob's faulty List overload, the unused laser helper and the reader flow
// model official SAIN 4.5.1 (MIT, SAIN contributors). The real DLL hook/IL contract
// is additionally checked by IntegrationHarness; this does not run Unity physics.
using System.Runtime.CompilerServices;
using EFT;
using HarmonyLib;
using SAIN.Components;
using SAIN.Components.PlayerComponentSpace;
using SAIN.Types.Jobs;
using Unity.Collections;
using UnityEngine;

namespace UnityEngine
{
    public readonly record struct Vector3(float x, float y, float z)
    {
        public static Vector3 zero => default;
        public float magnitude => MathF.Sqrt(x * x + y * y + z * z);
        public Vector3 normalized => magnitude > 0 ? this * (1 / magnitude) : zero;

        public static Vector3 operator +(Vector3 a, Vector3 b) =>
            new(a.x + b.x, a.y + b.y, a.z + b.z);

        public static Vector3 operator -(Vector3 a, Vector3 b) =>
            new(a.x - b.x, a.y - b.y, a.z - b.z);

        public static Vector3 operator *(Vector3 a, float n) => new(a.x * n, a.y * n, a.z * n);

        public static float Distance(Vector3 a, Vector3 b) => (a - b).magnitude;
    }

    public struct LayerMask
    {
        public int value;

        public static implicit operator int(LayerMask m) => m.value;
    }

    public struct QueryParameters
    {
        public int layerMask;
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

    public struct RaycastHit
    {
        public object? collider;
        public Vector3 point,
            normal;
    }

    public static class Time
    {
        public static float time,
            realtimeSinceStartup;
    }
}

namespace Unity.Collections
{
    public struct NativeArray<T>
    {
        private sealed class Storage(int length)
        {
            public T[] values = new T[length];
            public bool disposed;
        }

        private Storage storage;

        public NativeArray(int length)
        {
            storage = new(length);
        }

        public int Length
        {
            get
            {
                Alive();
                return storage.values.Length;
            }
        }
        public bool IsCreated => storage != null && !storage.disposed;

        private void Alive()
        {
            if (!IsCreated)
                throw new ObjectDisposedException(nameof(NativeArray<T>));
        }

        public T this[int index]
        {
            get
            {
                Alive();
                return storage.values[index];
            }
            set
            {
                Alive();
                storage.values[index] = value;
            }
        }

        public void Dispose()
        {
            Alive();
            storage.disposed = true;
        }
    }
}

namespace EFT
{
    public interface IPlayer
    {
        string ProfileId { get; }
    }

    public sealed class Player(string id) : IPlayer
    {
        public string ProfileId => id;
        public AiData AIData = new();
    }

    public sealed class AiData
    {
        public BotOwner BotOwner = new();
    }

    public sealed class BotOwner
    {
        public NightVision NightVision = new();
    }

    public sealed class NightVision
    {
        public bool UsingNow;
    }
}

namespace SAIN.Components.PlayerComponentSpace
{
    public sealed class PlayerComponent(string id)
    {
        public Player Player = new(id);
        public BotComponent? BotComponent;
        public FlashLightClass Flashlight = null!;
        public Vector3 FirePort,
            PointDirection = new(1, 0, 0);
    }

    public sealed class LightDetectionClass(PlayerComponent player)
    {
        public PlayerComponent PlayerComponent = player;
        public List<Vector3> LightPoints = new();
        public int Investigations;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public bool CheckIsBeamVisible(FlashLightClass source)
        {
            if (
                !source.WhiteLight
                && !source.Laser
                && !PlayerComponent.Player.AIData.BotOwner.NightVision.UsingNow
            )
                return false;
            return source.LightDetection.LightPoints.Count > 0;
        }

        public void TryToInvestigate(IPlayer target)
        {
            Investigations++;
        }
    }

    public sealed class FlashLightClass(PlayerComponent player)
    {
        public bool DeviceActive = true,
            WhiteLight,
            Laser,
            IRLight,
            IRLaser;
        public LightDetectionClass LightDetection = new(player);
    }
}

namespace SAIN.Types.Jobs
{
    public struct RaycastJob
    {
        public NativeArray<RaycastHit> Hits;
        public NativeArray<RaycastCommand> Commands;
        public IPlayer Owner,
            Target;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public RaycastJob(
            List<Vector3> points,
            Vector3 origin,
            LayerMask mask,
            IPlayer owner,
            IPlayer target
        )
        {
            Owner = owner;
            Target = target;
            Hits = new(points.Count);
            Commands = new(points.Count);
            for (int i = 0; i < points.Count; i++)
                Commands[i] = new(
                    origin,
                    points[i] - origin,
                    new QueryParameters { layerMask = mask },
                    1f
                );
        }

        public void Dispose()
        {
            Hits.Dispose();
            Commands.Dispose();
        }
    }
}

namespace SAIN.Components
{
    public sealed class BotComponent(string id)
    {
        public string ProfileId = id;
        public bool Supported = true,
            BotActive = true,
            GameEnding;
    }

    public static class GameWorldComponent
    {
        public static Dictionary<string, PlayerComponent> Players = new();

        public static bool TryGetPlayerComponent(IPlayer player, out PlayerComponent component) =>
            Players.TryGetValue(player.ProfileId, out component!);
    }

    public sealed class FlashlightRaycastJob
    {
        private readonly List<RaycastJob> RaycastJobs = new();
        public PlayerComponent Observer = null!,
            Source = null!;
        public float RealDistance = 20;
        public List<RaycastJob> Jobs => RaycastJobs;

        public void SourcePass(params RaycastHit[] hits)
        {
            DisposeJobs();
            var job = new RaycastJob(
                Enumerable.Repeat(Vector3.zero, hits.Length).ToList(),
                default,
                default,
                Source.Player,
                Source.Player
            );
            for (int i = 0; i < hits.Length; i++)
                job.Hits[i] = hits[i];
            RaycastJobs.Add(job);
            ReadFlashlightJobData(RaycastJobs.Count);
            DisposeJobs();
        }

        public void DetectionPass(float obstacleDistance = float.PositiveInfinity)
        {
            DisposeJobs();
            CreateLightDetectionJobs();
            foreach (var job in RaycastJobs)
            {
                var hits = job.Hits;
                for (int i = 0; i < hits.Length; i++)
                    if (obstacleDistance <= job.Commands[i].distance)
                        hits[i] = new() { collider = new object() };
            }
            ReadLightDetectionJobData(RaycastJobs.Count);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void ReadFlashlightJobData(int total)
        {
            Source.Flashlight.LightDetection.LightPoints.Clear();
            for (int i = 0; i < total; i++)
            for (int j = 0; j < RaycastJobs[i].Hits.Length; j++)
            {
                var hit = RaycastJobs[i].Hits[j];
                if (hit.collider != null)
                    Source.Flashlight.LightDetection.LightPoints.Add(hit.point + hit.normal * .05f);
            }
        }

        private static void AddLaserPathPoints(
            PlayerComponent player,
            NativeArray<RaycastHit> hits,
            List<Vector3> points
        )
        {
            if (!player.Flashlight.Laser && !player.Flashlight.IRLaser)
                return;
            if (player.PointDirection == Vector3.zero)
                return;
            float length = 75;
            if (hits.Length > 0 && hits[0].collider != null)
                length = Math.Min(length, Vector3.Distance(player.FirePort, hits[0].point));
            for (float distance = 5; distance < length; distance += 5)
                points.Add(player.FirePort + player.PointDirection * distance);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void CreateLightDetectionJobs()
        {
            var light = Source.Flashlight;
            if (
                light.DeviceActive
                && Observer.Flashlight.LightDetection.CheckIsBeamVisible(light)
                && RealDistance <= 125
            )
                RaycastJobs.Add(
                    new RaycastJob(
                        light.LightDetection.LightPoints,
                        new Vector3(0, 1, 0),
                        new LayerMask { value = 123 },
                        Observer.Player,
                        Source.Player
                    )
                );
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void ReadLightDetectionJobData(int total)
        {
            for (int i = 0; i < total; i++)
            {
                var hits = RaycastJobs[i].Hits;
                for (int j = 0; j < hits.Length; j++)
                    if (hits[j].collider == null)
                    {
                        Observer.Flashlight.LightDetection.TryToInvestigate(RaycastJobs[i].Target);
                        break;
                    }
            }
        }

        public void DisposeJobs()
        {
            foreach (var job in RaycastJobs)
                job.Dispose();
            RaycastJobs.Clear();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void Stop()
        {
            DisposeJobs();
        }
    }
}

namespace Comet.Client
{
    public static class NativeAdapter
    {
        public static bool IsSupportedAi(BotComponent b) => b.Supported;
    }

    public sealed class Settings
    {
        public bool Enabled = true;
        public JournalSettings CombatJournal = new();
    }

    public sealed class JournalSettings
    {
        public bool Enabled = true;
    }

    public sealed class Diagnostics
    {
        public bool Quiet;
        public List<string> Lines = new();

        public void Write(string text)
        {
            if (!Quiet)
                Lines.Add(text);
        }

        public static string Serialize(object data) =>
            System.Text.Json.JsonSerializer.Serialize(data);
    }

    public sealed class Log
    {
        public List<string> Warnings = new();

        public void LogWarning(string text) => Warnings.Add(text);
    }

    public sealed partial class Plugin
    {
        public static Plugin? Instance;
        public object? world = new();
        public Settings config = new();
        public bool Authority = true;
        public HashSet<string> Ghosts = new();

        public bool Ghost(string id) => Ghosts.Contains(id);

        public Diagnostics diagnostics = new();
        public Log Logger = new();
        public string session = "test";
        public List<string> RR = new();

        public Plugin()
        {
            Instance = this;
        }

        public static void Patch() => PatchLightDetection(new Harmony("comet.light.tests"));

        public int CachedSources => lightBeamPoints.Count;
        public int CachedDetails => lightDetailAt.Count;
        public int PooledLists => lightPointPool.Count;

        public void Flush() => FlushLightDetection(true);

        public object JournalState(IPlayer player) => player;

        public void JournalWrite(
            object state,
            string feature,
            string stage,
            string reason,
            object data
        )
        {
            string json = Diagnostics.Serialize(
                new
                {
                    feature,
                    stage,
                    reason,
                    data,
                }
            );
            diagnostics.Write(json);
            RR.Add(json);
        }
    }
}
