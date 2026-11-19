using System;
using System.Diagnostics;
using System.Linq;
using Comet.Core;
using SAIN.Classes.Bot.Search;
using SAIN.SAINComponent.Classes.EnemyClasses;
using UnityEngine;

namespace UnityEngine
{
    public static class Time
    {
        public static int frameCount;
        public static float time;
    }
}

namespace Comet.Core
{
    public static class DoorPolicy
    {
        public static bool Finite(double n) => !double.IsNaN(n) && !double.IsInfinity(n);
    }

    public sealed class CometConfig
    {
        public WeaponSyncConfig WeaponSync = new();

        public static void Range(double n, double min, double max, string name) { }
    }
}

namespace SAIN.SAINComponent.Classes.EnemyClasses
{
    public sealed class Place
    {
        public bool HasArrivedPersonal,
            HasArrivedSquad;
    }

    public sealed class Places
    {
        public Place LastKnownPlace = new();
    }

    public sealed class Enemy
    {
        public Places KnownPlaces = new();
    }
}

namespace SAIN.Classes.Bot.Search
{
    public sealed class Reasons
    {
        public string PathCalcFailReason = "",
            NotSearchReason = "native",
            Want = "";
        public Reasons WantSearchReasons => this;
        public string NotWantToSearchReason = "audio",
            CantStartReason = "suppressed";
    }

    public sealed class Decider
    {
        public bool Allowed;
        public Reasons Reasons = new();
        public int Calls;

        public bool ShallStartSearch(Enemy e, out Reasons r)
        {
            Calls++;
            r = Reasons;
            return Allowed;
        }
    }

    public sealed class SearchClass
    {
        public Finder PathFinder = new();
        public Decider Decider = new();
    }

    public sealed class Finder
    {
        public Place TargetPlace;
        public int Checks;
        public bool Available = true;

        public bool CheckEnemyPath(Enemy e, out string why)
        {
            Checks++;
            why = "unreachable";
            if (!Available)
                return false;
            TargetPlace = e.KnownPlaces.LastKnownPlace;
            return true;
        }
    }
}

namespace Comet.Client
{
    public sealed class BotComponent
    {
        public Info Info = new();
    }

    public sealed class Info
    {
        public NativeInfo WeaponInfo = new();
    }

    public sealed class NativeInfo
    {
        public bool Recalculate;
    }

    public sealed class Runtime
    {
        public WeaponSyncSchedule Schedule = new();
        public bool Applying,
            WasInScope,
            ResourcesDirty = true;
        public float RetryAt;
        public WeaponSyncTrace Trace;
        public string[] Contents = new string[3];
        public object[] Observed = new object[3];
    }

    public sealed class LoadoutRuntime
    {
        public LoadoutTrace Cached;
        public LoadoutRefresh Refresh = new();
    }

    public sealed class ResourceRuntime
    {
        public object LastFrame = new(),
            Frame = new();
        public float Next = 10;
        public string Key = "old",
            ReturnId = "held";
    }

    public sealed class BotState
    {
        public Runtime WeaponSync = new();
        public LoadoutRuntime Loadout = new();
        public ResourceRuntime ReloadTiming = new(),
            WeaponReturn = new(),
            WeaponRange = new();
    }

    public sealed class WeaponSyncSnapshot
    {
        public WeaponSyncFrame Frame;
        public string[] Contents = new string[3];
        public object[] Guns = new object[3];
    }

    public sealed class Budget
    {
        public bool Allowed = true;

        public bool TryBegin(int f, object? owner = null) => Allowed;

        public void Charge(double ms) { }
    }

    public partial class Plugin
    {
        public int Reads,
            Applies,
            Records,
            Ends;
        public bool Scope = true,
            Work,
            ApplySucceeds = true;
        public string Block = "";
        private int weaponSyncFrame = -1;
        public Budget doorBudget = new();
        private Stopwatch doorClock = new();
        private CometConfig config = new();

        private bool WeaponSyncScope(BotComponent b) => Scope;

        private static readonly System.Reflection.FieldInfo syncForceRecalc =
            typeof(NativeInfo).GetField("Recalculate");
        public int RangeEnds;

        private void EndWeaponRange(BotState s, string reason)
        {
            RangeEnds++;
        }

        private string SyncBlock(BotComponent b, BotState s) => Block;

        private void EndWeaponSync(BotState s, string r)
        {
            Ends++;
        }

        private void RecordWeaponSync(BotComponent b, BotState s, bool force = false)
        {
            Records++;
        }

        private WeaponSyncSnapshot ReadWeaponSync(BotComponent b, BotState s)
        {
            Reads++;
            return new()
            {
                Frame = new()
                {
                    Safe = true,
                    ResourcesChanged = Work,
                    NativeCurrentMatches = true,
                    SainCurrentMatches = true,
                    Slots = new[] { "FirstPrimaryWeapon", "SecondPrimaryWeapon", "Holster" }
                        .Select(x => new WeaponSyncSlot
                        {
                            Slot = x,
                            NativeMatches = true,
                            SainMatches = true,
                            SelectorMatches = true,
                        })
                        .ToArray(),
                },
            };
        }

        private bool ApplyWeaponSync(
            BotComponent b,
            BotState s,
            WeaponSyncSnapshot snap,
            WeaponSyncTrace t
        )
        {
            Applies++;
            return ApplySucceeds;
        }
    }

    public static class Cases
    {
        static int checks;

        static void Check(bool ok, string reason)
        {
            checks++;
            if (!ok)
                throw new Exception(reason);
        }

        public static void Run()
        {
            var e = new Enemy();
            var search = new SearchClass();
            search.PathFinder.TargetPlace = e.KnownPlaces.LastKnownPlace;
            Check(
                !SearchExecutionAdapter.CanStart(search, e, out var rejection)
                    && rejection.Contains("audio"),
                "prepared path cannot bypass native eligibility"
            );
            search.Decider.Reasons.PathCalcFailReason = "partial";
            Check(
                !SearchExecutionAdapter.CanStart(search, e, out rejection)
                    && rejection == "search-native-path-refused:partial",
                "native path refusal preserved"
            );
            search.Decider.Allowed = true;
            Check(
                SearchExecutionAdapter.CanStart(search, e, out rejection)
                    && rejection == ""
                    && search.Decider.Calls == 3,
                "native decider queried lazily and can recover without cooldown"
            );
            search.PathFinder.TargetPlace = null;
            Check(
                SearchExecutionAdapter.Prepare(search, e, out var reason)
                    && reason == "search-target-prepared",
                "direct Search prepares SAIN target"
            );
            Check(
                ReferenceEquals(search.PathFinder.TargetPlace, e.KnownPlaces.LastKnownPlace),
                "uses known place, not live target"
            );
            for (int i = 0; i < 100; i++)
                Check(
                    SearchExecutionAdapter.Prepare(search, e, out reason)
                        && reason == "search-target-retained",
                    "keep native peek/wait state"
                );
            Check(search.PathFinder.Checks == 1, "no repeated path reset");
            e.KnownPlaces.LastKnownPlace = new();
            Check(
                SearchExecutionAdapter.Prepare(search, e, out reason)
                    && search.PathFinder.Checks == 2,
                "new known place initializes once"
            );
            e.KnownPlaces.LastKnownPlace.HasArrivedSquad = true;
            Check(
                !SearchExecutionAdapter.Prepare(search, e, out reason)
                    && reason == "search-place-already-visited",
                "visited squad place rejected"
            );
            e.KnownPlaces.LastKnownPlace = new() { HasArrivedPersonal = true };
            Check(
                !SearchExecutionAdapter.Prepare(search, e, out reason),
                "visited personal place rejected"
            );
            e.KnownPlaces.LastKnownPlace = null;
            Check(
                !SearchExecutionAdapter.Prepare(search, e, out reason)
                    && reason == "search-no-known-place",
                "missing contact rejected"
            );
            e.KnownPlaces.LastKnownPlace = new();
            search.PathFinder.Available = false;
            var old = search.PathFinder.TargetPlace;
            Check(
                !SearchExecutionAdapter.Prepare(search, e, out reason)
                    && reason.StartsWith("search-native-path-refused"),
                "native refusal propagated"
            );
            Check(
                ReferenceEquals(search.PathFinder.TargetPlace, old),
                "failure never invents a target"
            );
            search.PathFinder.Available = true;
            Check(
                SearchExecutionAdapter.Prepare(search, e, out reason),
                "retry can initialize when route recovers"
            );
            var bot = new BotComponent();
            foreach (
                var block in new[]
                {
                    "sync-not-initialized",
                    "sync-weapon-not-ready",
                    "sync-native-operation-pending",
                    "sync-medical-operation",
                    "sync-hands-busy",
                    "sync-held-weapon-between-slots",
                    "sync-firing",
                    "sync-melee-not-initialized",
                }
            )
            {
                Time.time = 0;
                Time.frameCount = 0;
                var p = new Plugin { Block = block };
                var s = new BotState();
                p.SampleWeaponSync(bot, s);
                Check(
                    p.Reads == 0
                        && p.Applies == 0
                        && p.Records == 1
                        && s.WeaponSync.Trace.Reason == block,
                    "busy operation avoids equipment scans: " + block
                );
                Check(
                    s.WeaponSync.Schedule.Dirty && !s.WeaponSync.Applying,
                    "pending repair retained, scope released"
                );
                Time.time = .1f;
                Time.frameCount++;
                p.SampleWeaponSync(bot, s);
                Check(p.Records == 1, "retry cadence preserved");
                Time.time = .3f;
                Time.frameCount++;
                p.Block = "";
                p.Work = true;
                p.SampleWeaponSync(bot, s);
                Check(
                    p.Reads == 2
                        && p.Applies == 1
                        && !s.WeaponSync.Schedule.Dirty
                        && s.WeaponSync.Trace.Verified,
                    "repair and post-mutation validation after native operation"
                );
            }
            Time.time = 0;
            Time.frameCount = 0;
            var clean = new Plugin();
            var cleanState = new BotState();
            clean.SampleWeaponSync(bot, cleanState);
            Check(
                clean.Reads == 1 && clean.Applies == 0 && !cleanState.WeaponSync.Schedule.Dirty,
                "unchanged equipment read only once"
            );
            var failed = new Plugin { Work = true, ApplySucceeds = false };
            var fs = new BotState();
            failed.SampleWeaponSync(bot, fs);
            Check(
                failed.Reads == 1 && failed.Applies == 1 && fs.WeaponSync.Schedule.Dirty,
                "failed transaction remains pending"
            );
            var outside = new Plugin { Scope = false };
            outside.SampleWeaponSync(bot, new());
            Check(outside.Reads == 0 && outside.Ends == 1, "out-of-scope cleanup unchanged");
            var budget = new Plugin();
            budget.doorBudget.Allowed = false;
            budget.SampleWeaponSync(bot, new());
            Check(budget.Reads == 0, "shared work budget respected");
            foreach (bool changed in new[] { false, true })
            {
                var p = new Plugin();
                var s = new BotState();
                var held = new object();
                s.WeaponSync.Observed[0] = held;
                var snap = new WeaponSyncSnapshot
                {
                    Frame = new() { Slots = new[] { new WeaponSyncSlot { Actual = "held" } } },
                };
                snap.Guns[0] = changed ? new object() : held;
                var cached = new LoadoutTrace
                {
                    WeaponId = "held",
                    Profile = "BoltScope",
                    Optic = "scope",
                    Ammo = "bullet",
                    State = "ready",
                    Ready = true,
                    ResourcesKnown = true,
                    LoadedRounds = 8,
                    CompatibleMagazines = 2,
                    CanFirstAid = true,
                };
                s.Loadout.Cached = cached;
                s.Loadout.Refresh.Complete(1, "held", "ammo", 10);
                bot.Info.WeaponInfo.Recalculate = false;
                for (int i = 0; i < 100; i++)
                {
                    p.InvalidateSyncedEquipment(bot, s, snap);
                    Check(
                        ReferenceEquals(cached, s.Loadout.Cached),
                        "sync retains last classification until a measured refresh"
                    );
                    Check(
                        !s.Loadout.Refresh.IsFresh(1.1, "held", "ammo"),
                        "sync invalidates freshness"
                    );
                    var pending = LoadoutTrace.Pending(
                        s.Loadout.Cached,
                        "held",
                        1.1,
                        "weapon-sync"
                    );
                    Check(
                        pending.PlanKey == cached.PlanKey,
                        "same gun cannot manufacture a tactical equipment change"
                    );
                    Check(
                        !pending.Ready
                            && !pending.ResourcesKnown
                            && !pending.CanFirstAid
                            && pending.LoadedRounds == 0
                            && pending.CompatibleMagazines == 0,
                        "pending sync never claims stale supplies"
                    );
                }
                Check(
                    LoadoutTrace.Pending(s.Loadout.Cached, "new-gun", 1.1, "weapon-sync").Profile
                        == "",
                    "different held gun cannot inherit classification"
                );
                Check(
                    s.ReloadTiming.LastFrame == null
                        && s.WeaponReturn.Frame == null
                        && s.ReloadTiming.Next == 0,
                    "resource consumers invalidated"
                );
                Check(
                    (p.RangeEnds > 0) == changed && bot.Info.WeaponInfo.Recalculate == changed,
                    "actual equipment changes still refresh range and native weapon info"
                );
            }
            Console.WriteLine($"Raid387 native-boundary regression: {checks} checks passed.");
        }
    }
}
