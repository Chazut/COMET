using System;
using System.Collections.Generic;
using System.Text.Json;
using Comet.Core;

static class Time
{
    public static float time = 10;
    public static float realtimeSinceStartup => time;
}

enum ESelfActionType
{
    None,
    Reload,
}

enum Combat
{
    Hold,
    Search,
}

class Decision
{
    public ESelfActionType CurrentSelfDecision;
    public Combat CurrentCombatDecision;
}

class SearchDouble
{
    public string CurrentState = "Searching";
    public Enemy? SearchTarget;
    public bool SearchActive;
    public PathFinderDouble PathFinder = new();
}
class PathFinderDouble { public object? TargetPlace; }
class EnemyPathDouble { public RouteDouble? PathToEnemy; }
class RouteDouble { public string status = "PathComplete"; }
class BotComponent
{
    public SearchDouble Search = new();
    public int Reads;
    public Decision Data = new();
    public Decision Decision
    {
        get
        {
            Reads++;
            return Data;
        }
    }
    public Enemy? GoalEnemy;
}

class KnownPlacesDouble { public object? LastKnownPlace = new(); }

class Enemy
{
    public KnownPlacesDouble KnownPlaces = new();
    public BotComponent Bot = new();
    public string EnemyProfileId = "target";
    public EnemyPathDouble Path = new();
}

class EnemyKnownChecker
{
    public Enemy Enemy = new();
}

class Policy<T>
    where T : new()
{
    public T Trace = new();
}

partial class ReloadTimingRuntime
{
    public Policy<ReloadTimingTrace> Policy = new();
    public float LogAt;
}

partial class PistolSwapRuntime
{
    public Policy<PistolSwapTrace> Policy = new();
    public float LogAt;
}

partial class WeaponRangeRuntime
{
    public Policy<WeaponRangeTrace> Policy = new();
    public float LogAt;
}

partial class DoorTransitRuntime
{
    public Policy<DoorTransitTrace> Policy = new();
    public float LogAt;
}

class BotState
{
    public ReloadTimingRuntime ReloadTiming = new();
    public BotComponent Owner = null!;
    public ContactDiagnosticGate AbandonLogs = new();
    public SilentSearchTrace SilentTrace = new();
    public float SilentLogAt = -100;
    public PistolSwapRuntime PistolSwap = new();
    public WeaponRangeRuntime WeaponRange = new();
    public DoorTransitRuntime Transit = new();
    public string ProfileId = "pmc",
        Name = "bot",
        Id = "instance";
    public SearchAbandonment Abandonment = new();
    public SearchAbandonmentFrame? AbandonPending;
    public float AbandonProbeAt;
}

class Sink
{
    public bool WantsOrdinaryEvents;
    public List<string> Lines = new();

    public void Write(string json) => Lines.Add(json);
}

static class Diagnostics
{
    public static string Serialize(object x) =>
        JsonSerializer.Serialize(x, new JsonSerializerOptions { IncludeFields = true });
}

static class Api
{
    public static class Decisions
    {
        public static bool HasSubscribers;
        public static List<string> Lines = new();

        public static void Publish(string json, Action<Exception> error) => Lines.Add(json);
    }
}

class Log
{
    public void LogWarning(string message) => throw new Exception(message);
}

partial class Probe
{
    static Probe Instance = null!;
    CometConfig config = new();
    Sink diagnostics = new();
    Log Logger = new();
    bool subscriberWarned;
    string session = "test";
    long sequence;
    Dictionary<BotComponent, BotState> states = new();

    static Enemy abandonmentEnemy(EnemyKnownChecker c) => c.Enemy;

    public static void Run()
    {
        int checks = 0;
        void Check(bool pass, string why)
        {
            checks++;
            if (!pass)
                throw new Exception(why);
        }
        var p = new Probe();
        Instance = p;
        var s = new BotState();
        var b = s.Owner = new BotComponent();
        s.Transit.Policy.Trace.Door = "door";
        s.Transit.Policy.Trace.State = "moving";
        void Record()
        {
            p.RecordPistolSwap(b, s);
            p.RecordWeaponRange(b, s);
            p.RecordDoorTransit(s);
        }
        Record();
        Check(
            p.sequence == 3 && Api.Decisions.Lines.Count == 3,
            "initial events reach file and RR"
        );
        for (int i = 0; i < 2000; i++)
            Record();
        int reads = b.Reads;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100000; i++)
            Record();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Check(allocated == 0, "unchanged diagnostic gate allocates " + allocated + " bytes");
        Check(b.Reads == reads, "suppressed diagnostics must not format native action enums");
        Check(p.sequence == 3, "duplicate events suppressed");
        Time.time += .25f;
        Record();
        Check(p.sequence == 6, "periodic useful heartbeat preserved");
        s.PistolSwap.Policy.Trace.MainLoaded++;
        p.RecordPistolSwap(b, s);
        Check(p.sequence == 7, "ammo changes immediate");
        s.WeaponRange.Policy.Trace.Ready = true;
        p.RecordWeaponRange(b, s);
        Check(p.sequence == 8, "weapon readiness changes immediate");
        s.Transit.Policy.Trace.Requested = true;
        p.RecordDoorTransit(s);
        Check(p.sequence == 9, "door action changes immediate");
        p.RecordPistolSwap(b, s, true);
        p.RecordWeaponRange(b, s, true);
        p.RecordDoorTransit(s, true);
        Check(p.sequence == 12, "forced outcomes never throttled");
        b.Data.CurrentSelfDecision = ESelfActionType.Reload;
        p.RecordPistolSwap(b, s, true);
        using (var doc = JsonDocument.Parse(diagnosticsLine(p)))
            Check(
                doc.RootElement.GetProperty("pistolSwap").GetProperty("Action").GetString()
                    == "Reload",
                "emitted action stays current"
            );
        s.Transit.Policy.Trace.State = "ended";
        p.RecordDoorTransit(s);
        long ended = p.sequence;
        Time.time += 2;
        p.RecordDoorTransit(s);
        Check(p.sequence == ended, "ended door remains quiet");
        p.RecordDoorTransit(s, true);
        Check(p.sequence == ended + 1, "forced door end preserved");
        s.WeaponRange.Policy.Trace.State = "a:b";
        s.WeaponRange.Policy.Trace.Reason = "c";
        p.RecordWeaponRange(b, s);
        long collision = p.sequence;
        s.WeaponRange.Policy.Trace.State = "a";
        s.WeaponRange.Policy.Trace.Reason = "b:c";
        p.RecordWeaponRange(b, s);
        Check(p.sequence == collision + 1, "distinct values cannot collide on string delimiters");
        Check(
            Api.Decisions.Lines.Count == p.diagnostics.Lines.Count,
            "RR and file receive the same event count"
        );
        p.RecordSilent(s);
        var silentCount = p.sequence;
        for (int i = 0; i < 1000; i++)
            p.RecordSilent(s);
        long silentBefore = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100000; i++)
            p.RecordSilent(s);
        Check(
            GC.GetAllocatedBytesForCurrentThread() == silentBefore,
            "suppressed P08 heartbeat does not format an unused string key"
        );
        Check(p.sequence == silentCount, "P08 heartbeat cadence unchanged");
        p.RecordSilent(s, true);
        Check(p.sequence == silentCount + 1, "P08 forced transitions retained immediately");
        Time.time += 1;
        p.RecordSilent(s);
        Check(p.sequence == silentCount + 2, "P08 periodic evidence retained");
        var e = new Enemy { Bot = b };
        b.GoalEnemy = e;
        p.states[b] = s;
        s.AbandonProbeAt = Time.time + .25f;
        var checker = new EnemyKnownChecker { Enemy = e };
        Check(
            NeedsAbandonment(checker, false),
            "native visited check is not delayed by diagnostic cadence"
        );
        Check(!NeedsAbandonment(checker, true), "no pending forget callback");
        s.Abandonment.Interrupt("combat");
        Check(
            NeedsAbandonment(checker, false),
            "diagnostic outcome cannot disable source visited checks"
        );
        s.AbandonPending = new SearchAbandonmentFrame { Target = "target" };
        Check(NeedsAbandonment(checker, true), "native forget confirmation immediate");
        e.KnownPlaces.LastKnownPlace = null;
        Check(!NeedsAbandonment(checker, false), "no place needs no evaluation");
        Check(NeedsAbandonment(checker, true), "a forgotten place still confirms the pending contact");
        e.KnownPlaces.LastKnownPlace = new();
        e.EnemyProfileId = "other";
        b.GoalEnemy = null;
        Check(
            NeedsAbandonment(checker, false) && !NeedsAbandonment(checker, true),
            "other tracked contacts are evaluated without stealing a pending confirmation"
        );
        long initial = p.sequence;
        for (int tick = 0; tick < 1000; tick++)
        for (int enemy = 0; enemy < 5; enemy++)
        {
            s.Abandonment.Evaluate(
                new SearchAbandonmentFrame
                {
                    Now = 1,
                    Stamp = 1,
                    Target = "enemy" + enemy,
                    Distance = 50,
                },
                p.config.SearchAbandonment
            );
            p.RecordAbandonment(s);
        }
        Check(p.sequence == initial + 5, "P09 alternating contacts emit five events, not 5000");
        s.Abandonment.Interrupt("combat");
        p.RecordAbandonment(s);
        Check(p.sequence == initial + 6, "P09 changed outcome immediate");
        p.RecordAbandonment(s, true);
        Check(p.sequence == initial + 7, "P09 forced outcome immediate");
        Time.time += 10;
        p.RecordAbandonment(s);
        Check(p.sequence == initial + 8, "P09 ten-second heartbeat preserved");
        long reloadSequence = p.sequence;
        int oldReads = b.Reads;
        p.RecordReloadTiming(b, s, true);
        long reloadBefore = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100000; i++)
            p.RecordReloadTiming(b, s, true);
        long reloadAllocated = GC.GetAllocatedBytesForCurrentThread() - reloadBefore;
        Check(reloadAllocated == 0 && p.sequence == reloadSequence && b.Reads == oldReads,
            "W01 without consumers should do no native reads, serialization or allocation");
        Console.WriteLine($"W01 without consumers: {reloadAllocated} bytes for 100000 calls.");
        Api.Decisions.HasSubscribers = true;
        p.RecordReloadTiming(b, s);
        Check(p.sequence == reloadSequence + 1, "W01 subscriber alone receives snapshot");
        oldReads = b.Reads;
        p.RecordReloadTiming(b, s);
        Check(b.Reads == oldReads && p.sequence == reloadSequence + 1, "W01 duplicate skips formatting and serialization");
        Api.Decisions.HasSubscribers = false;
        p.diagnostics.WantsOrdinaryEvents = true;
        s.ReloadTiming.Policy.Trace.Loaded++;
        p.RecordReloadTiming(b, s);
        Check(p.sequence == reloadSequence + 2, "W01 journal alone sees changed rounds");
        Console.WriteLine(
            $"Diagnostic/P09 production gate: {checks} checks passed, {allocated} bytes for 300000 suppressed calls; forced/changed/heartbeat/RR events retained."
        );
    }

    static string diagnosticsLine(Probe p) => p.diagnostics.Lines[^1];
}
