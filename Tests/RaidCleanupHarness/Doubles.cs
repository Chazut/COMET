using System.Collections;
using System.Runtime.CompilerServices;
using Comet.Core;

namespace HarmonyLib
{
    public class Harmony
    {
        public void Patch(object target, HarmonyMethod prefix) { }
    }

    public class HarmonyMethod(Type type, string method)
    {
        public int priority;
    }

    public static class Priority
    {
        public const int First = 1;
    }

    public static class AccessTools
    {
        public static object Method(Type t, string n, Type[] args) => new();
    }
}

namespace UnityEngine
{
    public static class Time
    {
        public static float realtimeSinceStartup = 100;
    }
}

namespace EFT
{
    public class GameWorld { }
}

namespace Comet.Api
{
    public static class DoorMotionNetwork
    {
        public static bool Fail;
        public static int Cleared;

        public static void Clear()
        {
            Cleared++;
            if (Fail)
                throw new Exception("transport failed");
        }
    }
}

namespace Comet.Client
{
    internal class Owned
    {
        internal readonly List<object> Refs = new();
        internal bool Fail;
        internal int Releases;

        internal void Clear()
        {
            Refs.Clear();
            Releases++;
        }

        internal void Reset() => Clear();

        internal void Dispose()
        {
            try
            {
                if (Fail)
                    throw new Exception("native disposal failure");
            }
            finally
            {
                Clear();
            }
        }
    }

    internal class Bot
    {
        internal bool Fail;
    }

    internal class BotState
    {
        internal string ProfileId = Guid.NewGuid().ToString("N");
        internal bool Cleaned;
    }

    internal class Scan
    {
        internal CoverScanRunner<object> Runner = null!;
    }

    internal class Events
    {
        internal bool FailThrowRemove;
        private Action? thrown,
            exploded;
        internal event Action OnGrenadeThrow
        {
            add => thrown += value;
            remove
            {
                thrown -= value;
                if (FailThrowRemove)
                    throw new Exception("throw unsubscription failure");
            }
        }
        internal event Action OnGrenadeExplosive
        {
            add => exploded += value;
            remove => exploded -= value;
        }
        internal int Count =>
            (thrown?.GetInvocationList().Length ?? 0) + (exploded?.GetInvocationList().Length ?? 0);
    }

    public sealed partial class Plugin
    {
        internal static Plugin? Instance;
        internal EFT.GameWorld? world;
        internal readonly RaidConfigLoad<CometConfig> loading = new();
        internal CometConfig? config = new();
        internal long sequence;
        internal string session = "";
        internal Diagnostics? diagnostics;
        internal readonly Dictionary<Bot, BotState> states = new();
        internal readonly List<KeyValuePair<Bot, BotState>> maintenanceSnapshot = new();
        internal Events? clearEvents;
        internal readonly List<Owned> clearOwnGrenades = new();
        internal readonly Dictionary<object, Scan> coverScans = new();
        internal object? scanningFinder,
            scanningRunner;
        internal bool coverFactoryBypass;
        internal Dictionary<object, Owned> supportOperations = new(),
            movementPending = new(),
            fearMemory = new(),
            fearDeaths = new();
        internal ConditionalWeakTable<object, Dictionary<string, Owned>> nativeStormPlans = new();
        internal readonly Owned doorSwings = new(),
            motionWatches = new(),
            pushContexts = new(),
            closingSelected = new(),
            closingPending = new(),
            supportBoard = new(),
            doorReservations = new(),
            doorBudget = new(),
            doorClock = new(),
            crossfireClaims = new(),
            maneuverBoard = new(),
            coverMateBoard = new(),
            stormBoard = new(),
            laneShots = new(),
            voicePolicy = new(),
            movementQueue = new(),
            movementSources = new(),
            movementListeners = new(),
            movementLog = new(),
            movementCounts = new(),
            floorCache = new(),
            floorPhysics = new(),
            floorCounters = new(),
            closeCounters = new(),
            impactCounters = new(),
            blastQueue = new(),
            blastThrows = new(),
            blastRecent = new(),
            ownProjectiles = new(),
            gasBridge = new(),
            grenadeArcBudget = new(),
            grenadeArcClock = new(),
            faultIsolation = new(),
            pendingFaultReset = new();
        internal Owned validityOwners = new(),
            validityContacts = new(),
            validitySources = new(),
            validityLogs = new();
        internal Owned closingHistory = new(),
            closingLogs = new(),
            recoveryEpochs = new(),
            transitControllers = new(),
            medicalConsumption = new();
        internal readonly object[] spatialPanels = new object[2],
            swingHits = new object[2],
            doorOccupants = new object[2],
            recoveryVolumes = new object[2],
            jumpHits = new object[2];
        internal object? stormObservationConfig,
            stormOpportunityConfig,
            voiceSource,
            voiceRequest,
            voicePlayback,
            voiceEmission,
            movementInjection,
            movementReaction,
            gunfireCall,
            quietCaller,
            blastCall;
        internal float nextMovementCleanup,
            nextMovementSummary,
            movementFaultUntil,
            nextFloorSummary,
            floorFaultUntil,
            nextCloseSummary,
            closeFaultUntil,
            nextImpactSummary,
            ownSampleAt,
            nextSweep;
        internal int stormObservationFrame,
            movementSequence,
            impactProbeFrame,
            blastSequence,
            blastThrowRayFrame,
            blastThrowRays,
            recoveryFrame,
            recoverySamples;

        private void ClearBlasts()
        {
            blastCall = null;
            blastWarned = false;
        }

        internal bool movementWarned,
            floorWarned,
            closeWarned,
            blastWarned,
            faultSinkWarned,
            subscriberWarned;
        internal static bool faultCleaning,
            faultCleanupFailed;
        internal static Exception? faultCleanupError;

        internal struct FaultScope
        {
            internal bool Cleanup;
            internal string Entry;
        }

        partial void GuardRaid(FaultScope scope, ref bool allowed);

        internal bool Allowed(string name, bool cleanup = false)
        {
            bool allow = true;
            GuardRaid(new() { Entry = name, Cleanup = cleanup }, ref allow);
            return allow;
        }

        internal void Begin(EFT.GameWorld w)
        {
            if (!raidLifetime.Begin(w))
                throw new Exception("retired world");
            world = w;
            config = new();
            session = Guid.NewGuid().ToString("N");
            Api.Decisions.RaidSession = session;
            Api.Decisions.RaidClosed = false;
            Instance = this;
        }

        internal void End() => EndRaid();

        internal void WorldDisposed(EFT.GameWorld w) => BeforeRaidDisposed(w);

        internal bool CanBegin(EFT.GameWorld w) => raidLifetime.CanBegin(w);

        internal long Epoch => raidLifetime.Generation;
        internal int Cleaned,
            OtherSteps;

        internal LifecycleTrace CleanupLifecycle(Bot b, BotState s, string why)
        {
            Cleaned++;
            s.Cleaned = true;
            return new()
            {
                CleanupSteps = 72,
                FailedSteps = b.Fail ? 1 : 0,
                Failures = b.Fail ? new[] { "native bot cleanup failed" } : Array.Empty<string>(),
            };
        }

        private void ClearProjectileThrown() { }

        private void ClearProjectileExplosion() { }

        internal Events Subscribe()
        {
            clearEvents = new();
            clearEvents.OnGrenadeThrow += ClearProjectileThrown;
            clearEvents.OnGrenadeExplosive += ClearProjectileExplosion;
            return clearEvents;
        }

        private void EndSupportOperation(Owned r, string why) => r.Dispose();

        private void RemoveMovement(Owned r, string why) => r.Dispose();

        private void StopBlast(BotState s, string r) { }

        private void ClearSainPreset()
        {
            OtherSteps++;
        }

        private void FlushTacticalProbes(bool force = false) { }

        private void ClearLocalDoors()
        {
            OtherSteps++;
        }

        private void ClearValidity()
        {
            OtherSteps++;
        }

        private void ClearLifecycles()
        {
            OtherSteps++;
        }

        private void ClearEnvironment()
        {
            OtherSteps++;
        }

        private void FlushFloorCounters(bool all) { }

        private void FlushCloseCounters(bool all) { }

        private void FlushImpactCounters(bool all) { }

        private void FlushMovementSummary() { }

        public int LightClears;

        private void ClearLightDetection()
        {
            LightClears++;
        }

        internal int RemixClears;

        partial void ClearRemixRaidState()
        {
            RemixClears++;
        }

        private void ClearNativeFixes()
        {
            OtherSteps++;
        }

        private void WarnFaultSink(Exception e) { }

        private void SafeFaultLog(string text) { }
    }
}
