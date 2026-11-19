using Comet.Client;
using Comet.Core;
using Newtonsoft.Json.Linq;
using UnityEngine;

int checks = 0;
void Check(bool ok, string why)
{
    checks++;
    if (!ok)
        throw new Exception(why);
}
var events = new List<string>();
Action<string> collect = events.Add;
Comet.Api.Decisions.Published += collect;
var p = new Plugin();
var a = p.Add();
var b = p.Add();
var id = p.states[a].Id;
Time.unscaledTimeAsDouble = 0;
p.ThrowWork = true;
p.Sample(a);
Check(
    p.WorkCalls == 1 && p.CleanupCalls == 1 && !p.states[a].OwnCommand,
    "failure releases owned command"
);
Check(p.OtherCleanupCalls == 1 && !p.Disabled(a), "cleanup continues and bot remains eligible");
for (int i = 0; i < 100; i++)
    p.Sample(a);
Check(p.WorkCalls == 1 && events.Count == 1, "no hot-loop retry or event spam");
p.ThrowWork = false;
p.Sample(b);
Check(p.WorkCalls == 2, "other bot proceeds");
p.Fresh();
Check(p.states[a].Id == id, "RR bot identity preserved across fresh state");
p.Healthy(a);
Check(p.HealthyCalls == 1, "healthy module proceeds after reset");
p.Depends(a);
Check(p.DependencyCalls == 0, "dependency quarantine aborts dependent action");
int value = 12;
bool result = true;
p.RefPrefix(a, ref value, ref result, out int state);
Check(value == 12 && result, "failed dependency restores ref arguments");
Check(state == 0, "failed dependency clears partial out state before native fallback");
Time.unscaledTimeAsDouble = 4.99;
p.Sample(a);
Check(p.WorkCalls == 2, "five-second boundary respected");
Time.unscaledTimeAsDouble = 5;
p.ThrowWork = true;
p.Sample(a);
Check(
    p.WorkCalls == 3 && events.Any(x => x.Contains("retrying")),
    "retry recorded at five seconds"
);
p.Fresh();
Time.unscaledTimeAsDouble = 10;
p.Sample(a);
p.Fresh();
Check(events.Any(x => x.Contains("module-disabled")), "third failure disables module");
Time.unscaledTimeAsDouble = 500;
p.Sample(a);
Check(p.WorkCalls == 4, "module remains disabled after window expires");
p.Healthy(a);
Check(p.HealthyCalls == 2 && !p.Disabled(a), "other modules still run");
p.Clear();
p.ThrowWork = false;
p.Sample(a);
Check(p.WorkCalls == 5, "next raid clears quarantine");
p.RecordSample(p.states[a]);
p.Sample(a);
Check(p.WorkCalls == 6 && !p.Disabled(a), "telemetry fault never quarantines combat");
var first = JObject.Parse(events[0]);
Check(
    (string?)first["feature"] == "C08" && (string?)first["faultIsolation"]?["module"] == "Sample",
    "production C08 event schema"
);
Check(
    (string?)first["faultIsolation"]?["error"]
        == "InvalidOperationException: injected sample failure",
    "original error retained, legacy handler bypassed"
);
p.ThrowWork = true;
p.ThrowCleanup = true;
p.Sample(a);
Check(
    p.Disabled(a) && p.OtherCleanupCalls >= 4,
    "failed cleanup disables bot but tries remaining cleanup"
);
p.Fresh();
int before = p.HealthyCalls;
p.Healthy(a);
Check(p.HealthyCalls == before, "disabled bot cannot resume any COMET action");
p.ThrowCleanup = false;
p.ThrowWork = false;
p.Healthy(b);
Check(p.HealthyCalls == before + 1, "cleanup failure isolated from other bot");

var noAuthority = new Plugin { Authority = false, ThrowWork = true };
var remote = noAuthority.Add();
noAuthority.Sample(remote);
Check(
    noAuthority.CleanupCalls == 0 && !noAuthority.Disabled(remote),
    "Fika replica cannot mutate cleanup state"
);
var nonPmc = new Plugin { ThrowWork = true };
var scav = nonPmc.Add();
scav.Pmc = false;
nonPmc.Sample(scav);
Check(nonPmc.CleanupCalls == 0 && !nonPmc.Disabled(scav), "non-PMC excluded");
var loop = new Plugin { ThrowWork = true };
var firstBot = loop.Add();
var nextBot = loop.Add();
loop.FailOnly = firstBot;
loop.Update();
Check(
    loop.WorkCalls == 2 && loop.HealthyCalls == 1,
    "one failed bot does not abort the shared update or the next bot"
);
var replicaService = new Plugin { Authority = false, ThrowGlobal = true };
int replicaEvents = events.Count;
replicaService.UpdateDoorMotion();
Check(events.Count == replicaEvents, "shared service recovery only runs on Fika AI authority");
var service = new Plugin { ThrowGlobal = true };
var consumer = service.Add();
Time.unscaledTimeAsDouble = 0;
service.UpdateDoorMotion();
service.ThrowGlobal = false;
Time.unscaledTimeAsDouble = 5;
int serviceEvents = events.Count;
service.Healthy(consumer);
Check(
    events.Count == serviceEvents + 1 && events.Last().Contains("retrying"),
    "bot consumer records shared service retry transition"
);
var reset = new Plugin { ThrowWork = true };
var resetBot = reset.Add();
reset.Sample(resetBot);
BotState.FailCreation = true;
reset.Fresh();
BotState.FailCreation = false;
Check(
    reset.Disabled(resetBot) && events.Any(x => x.Contains("fault-state-reset-failed")),
    "fresh-state failure disables the bot and is visible in RR"
);

int bad = 0,
    good = 0;
Action<string> broken = _ =>
{
    bad++;
    throw new Exception("RR consumer failed");
};
Action<string> healthy = _ => good++;
Comet.Api.Decisions.Published += broken;
Comet.Api.Decisions.Published += healthy;
Comet.Api.Decisions.Publish("a", _ => throw new Exception("logger also failed"));
Comet.Api.Decisions.Publish("b", _ => throw new Exception());
Check(bad == 1 && good == 2, "broken RR consumer and error reporter isolated, no spam");
Comet.Api.Decisions.Clear();
Comet.Api.Decisions.Publish("c", _ => { });
Check(bad == 2 && good == 3, "consumer retry on next raid");
Comet.Api.Decisions.Published -= broken;
Comet.Api.Decisions.Published -= healthy;
var logger = new Plugin { ThrowWork = true };
logger.Logger.Throw = true;
logger.Sample(logger.Add());
Check(true, "logger failure does not escape");

var policy = new FaultIsolation();
var key = new object();
var cfg = new FaultIsolationConfig();
cfg.Validate();
var error = new Exception("test");
policy.Fail(key, "M", "entry", error, 0, cfg);
policy.Fail(key, "M", "entry", error, 60, cfg);
var tr = policy.Fail(key, "M", "entry", error, 65, cfg);
Check(tr.Count == 2 && tr.State == "suspended", "rolling window excludes exact boundary");
policy.Ready(key, "M", 10, out _);
Check(
    !policy.Ready(key, "M", 14, out _) && policy.Ready(key, "M", 15, out _),
    "clock rollback retains delay"
);
foreach (
    var invalid in new[]
    {
        new FaultIsolationConfig { RetrySeconds = 0 },
        new FaultIsolationConfig { ErrorLimit = 1 },
        new FaultIsolationConfig { WindowSeconds = 9 },
        new FaultIsolationConfig { RetrySeconds = 30, WindowSeconds = 10 },
    }
)
{
    try
    {
        invalid.Validate();
        throw new Exception("accepted invalid config");
    }
    catch (ArgumentException)
    {
        checks++;
    }
}

// The generated preflight must skip expensive ownership/lifecycle work only for no-ops.
var hot = new Plugin();
var vision = new VisionProbe();
var search = new SearchProbe();
bool nativeKnown = true;
int guards = hot.GuardCalls;
for (int i = 0; i < 1000; i++)
{
    hot.CloseVisibleAfter(vision, false);
    hot.CheckSearchedPlace(search, ref nativeKnown);
    hot.SearchForgetObserved(search);
}
Check(
    hot.GuardCalls == guards
        && hot.SearchChecks == 0
        && hot.PendingChecks == 0
        && hot.Acquisitions == 0
        && nativeKnown,
    "no-op callbacks preserve native result without lifecycle scans"
);
vision.IsVisible = true;
search.Relevant = search.Pending = true;
hot.CloseVisibleAfter(vision, false);
hot.CheckSearchedPlace(search, ref nativeKnown);
hot.SearchForgetObserved(search);
Check(
    hot.GuardCalls == guards + 3
        && hot.SearchChecks == 1
        && hot.PendingChecks == 1
        && hot.Acquisitions == 1
        && !nativeKnown,
    "all actual work retains its lifecycle guard"
);
int recorded = events.Count;
hot.ThrowPreflight = true;
nativeKnown = true;
hot.CheckSearchedPlace(search, ref nativeKnown);
Check(
    nativeKnown && events.Count == recorded + 1,
    "preflight exceptions remain caught and native refs preserved"
);

// Normal-path cost: reusable stack, no per-call closure or fault table allocation.
var perf = new Plugin();
var perfBot = perf.Add();
for (int i = 0; i < 1000; i++)
    perf.Healthy(perfBot);
long allocated = GC.GetAllocatedBytesForCurrentThread();
var watch = System.Diagnostics.Stopwatch.StartNew();
for (int i = 0; i < 100000; i++)
    perf.Healthy(perfBot);
watch.Stop();
allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
Check(allocated < 1024, "normal boundary path must not allocate per call");
var directory = Path.GetFullPath(
    Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts/c08")
);
Directory.CreateDirectory(directory);
File.WriteAllLines(
    Path.Combine(directory, "fault-isolation.jsonl"),
    events.Where(x => x.StartsWith("{"))
);
File.WriteAllText(
    Path.Combine(directory, "fault-isolation.json"),
    "[" + string.Join(",", events.Where(x => x.StartsWith("{"))) + "]"
);
Console.WriteLine(
    $"C08 production runtime + generated boundaries: {checks} checks; {events.Count} events. 100k healthy calls: {watch.Elapsed.TotalMilliseconds:F2} ms, {allocated} bytes (desktop harness only)."
);
Comet.Api.Decisions.Published -= collect;
