using Comet.Client;
using EFT;
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
Comet.Api.Decisions.Published += events.Add;
var p = new Plugin();
var b = p.Add();
var old = p.states[b];
p.Act(b);
Check(p.Actions == 1 && old.OwnCommand, "healthy action proceeds");
b.Player.HealthController!.IsAlive = false;
int input = 12;
bool native = p.Prefix(b, ref input, out int local);
Check(
    native && input == 12 && local == 0 && p.Actions == 1,
    "same-frame death yields to native without mutating arguments"
);
Check(
    old.Retired && !old.OwnCommand && old.ActiveLabel == "",
    "death retires state and releases owned controls"
);
int cleaned = p.CleanupLog.Count;
for (int i = 0; i < 100; i++)
{
    p.Act(b);
    p.Callback(old);
}
Check(
    p.CleanupLog.Count == cleaned && p.OldCallbacks == 0,
    "duplicate/deferred calls neither clean twice nor act"
);
p.Sweep();
Check(!p.states.ContainsKey(b), "dead bot leaves active state collection");
b.Player.HealthController.IsAlive = true;
p.Act(b);
Check(p.Actions == 1, "terminal death cannot silently resurrect");
var alive = p.Add();
p.Act(alive);
Check(p.Actions == 2, "other bot continues");

var r = new Plugin();
var human = new RevivablePlayer();
var rb = r.Add(human);
var prior = r.states[rb];
r.Act(rb);
human.Downed = true;
human.HealthController!.IsAlive = false;
rb.IsDead = true;
r.Act(rb);
r.Sweep();
Check(prior.Retired && !r.states.ContainsKey(rb), "Fika downed takes precedence over dead health");
Check(events.Any(x => x.Contains("\"state\":\"downed\"")), "downed observation recorded");
human.Downed = false;
human.HealthController.IsAlive = true;
rb.IsDead = false;
Time.unscaledTimeAsDouble = 1;
r.Sweep();
Check(
    r.states.ContainsKey(rb)
        && !ReferenceEquals(prior, r.states[rb])
        && r.states[rb].Id != prior.Id,
    "revive creates fresh state and generation"
);
int before = r.Actions;
r.Act(rb);
r.Callback(prior);
r.EndMedical(prior, "late-cleanup");
Check(
    r.Actions == before + 1 && r.OldCallbacks == 0,
    "old actions and late cleanup cannot touch revived bot"
);
Check(events.Any(x => x.Contains("\"freshState\":true")), "resume trace emitted");
human.Downed = true;
r.Act(rb);
r.Sweep();
human.Downed = false;
r.InGhost = true;
Time.unscaledTimeAsDouble = 2;
r.Sweep();
Check(!r.states.ContainsKey(rb), "ghost blocks revive resume");
r.InGhost = false;
rb.BotActive = false;
Time.unscaledTimeAsDouble = 3;
r.Sweep();
Check(!r.states.ContainsKey(rb), "inactive bot cannot resume");
rb.BotActive = true;
Time.unscaledTimeAsDouble = 4;
r.Sweep();
Check(r.states.ContainsKey(rb), "eligible revive resumes");

var f = new Plugin { FailCleanup = "EndMedical" };
var fb = f.Add(new RevivablePlayer { Downed = true });
f.Act(fb);
Check(
    f.Disabled(fb) && f.CleanupLog.Contains("DisposeDoorFake"),
    "cleanup failure disables COMET but remaining steps run"
);
int failedEvents = events.Count;
f.Act(fb);
f.Sweep();
Check(events.Count == failedEvents, "failed cleanup not retried each tick");
Check(
    events.Any(x => x.Contains("Medical: InvalidOperationException")),
    "failed cleanup named in RR"
);
var q = new Plugin { FailPending = true };
var qb = q.Add();
qb.IsDead = true;
q.Act(qb);
q.Sweep();
Check(q.Disabled(qb), "deferred iterator cleanup failure also disables bot");

var e = new Plugin();
var ep = new RevivablePlayer { ThrowRead = true };
var eb = e.Add(ep);
e.Act(eb);
e.Sweep();
Check(!e.states.ContainsKey(eb), "unknown revive state suspends, never assumes death");
ep.ThrowRead = false;
Time.unscaledTimeAsDouble = 5;
e.Sweep();
Check(e.states.ContainsKey(eb), "known healthy state can recover from unknown");
ep.Downed = true;
e.Act(eb);
e.Sweep();
ep.Downed = false;
BotState.FailCreation = true;
Time.unscaledTimeAsDouble = 6;
e.Sweep();
BotState.FailCreation = false;
Check(
    e.Disabled(eb) && events.Any(x => x.Contains("lifecycle-recovery-failed")),
    "constructor failure is terminal and diagnosed"
);

var remote = new Plugin { Authority = false };
var remoteBot = remote.Add();
remoteBot.IsDead = true;
int remoteEvents = events.Count;
remote.Act(remoteBot);
remote.Sweep();
Check(
    remote.CleanupLog.Count == 0 && events.Count == remoteEvents,
    "Fika replica does not run recovery or cleanup"
);
var scope = new Plugin();
var scav = new SAIN.Components.BotComponent();
scav.Player.Profile.Info.Settings.Role = WildSpawnType.assault;
scav.IsDead = true;
int scopedEvents = events.Count;
scope.Act(scav);
Check(events.Count == scopedEvents && scope.CleanupLog.Count == 0, "unowned scav untouched");
var player = new SAIN.Components.BotComponent();
player.Player.IsAI = false;
player.IsDead = true;
scope.Act(player);
Check(scope.CleanupLog.Count == 0, "human untouched");
var untracked = new Plugin();
var untrackedPlayer = new RevivablePlayer { Downed = true };
var ub = new SAIN.Components.BotComponent { Player = untrackedPlayer };
untracked.Act(ub);
Check(untracked.Actions == 0, "untracked downed PMC cannot begin an action");
untrackedPlayer.Downed = false;
untracked.Act(ub);
Check(untracked.Actions == 1, "untracked revived PMC can begin its first action");
var terminal = new Plugin();
var down = new RevivablePlayer { Downed = true };
var tb = terminal.Add(down);
terminal.Act(tb);
terminal.Sweep();
down.Downed = false;
down.HealthController!.IsAlive = false;
Time.unscaledTimeAsDouble = 7;
int deathEvents = events.Count;
terminal.Sweep();
Check(
    events.Count == deathEvents + 1 && events.Last().Contains("\"state\":\"dead\""),
    "downed then death recorded even after state removal"
);
var pooled = new Plugin();
var pp = new RevivablePlayer { Downed = true };
var pb = pooled.Add(pp);
pooled.Act(pb);
pooled.Sweep();
pb.Player = new RevivablePlayer();
Time.unscaledTimeAsDouble = 8;
pooled.Sweep();
Check(!pooled.states.ContainsKey(pb), "replacement player cannot inherit old lifecycle");

var next = new Plugin();
var nb = next.Add();
nb.IsDead = true;
next.Act(nb);
next.Sweep();
next.NextRaid();
nb.IsDead = false;
next.states[nb] = new(nb, next.config!, next.session);
next.Act(nb);
Check(next.Actions == 1, "new raid clears terminal lifecycle");

// Raid teardown may clear the owner before the still-live Player/health objects.
foreach (var missing in new[] { "owner", "manager", "selector", "ending" })
{
    var retiring = new Plugin();
    var bot = retiring.Add();
    var state = retiring.states[bot];
    retiring.Act(bot);
    if (missing == "owner")
        bot.BotOwner = null;
    if (missing == "manager")
        bot.BotOwner!.WeaponManager = null;
    if (missing == "selector")
        bot.BotOwner!.WeaponManager!.Selector = null;
    if (missing == "ending")
        bot.GameEnding = true;
    int beforeRetire = events.Count;
    retiring.Callback(state);
    retiring.Act(bot);
    retiring.Sweep();
    Check(
        state.Retired
            && retiring.Actions == 1
            && retiring.OldCallbacks == 0
            && !retiring.states.ContainsKey(bot),
        "partial teardown blocks deferred execution: " + missing
    );
    Check(
        events.Skip(beforeRetire).Any(x => x.Contains("\"feature\":\"C09\""))
            && !events.Skip(beforeRetire).Any(x => x.Contains("\"feature\":\"C08\"")),
        "teardown is lifecycle, not module failure: " + missing
    );
    int cleanupCount = retiring.CleanupLog.Count;
    for (int i = 0; i < 50; i++)
    {
        retiring.Act(bot);
        retiring.Callback(state);
    }
    Check(retiring.CleanupLog.Count == cleanupCount, "teardown cleanup once: " + missing);
    bot.BotOwner = new();
    bot.GameEnding = false;
    Time.unscaledTimeAsDouble += 1;
    retiring.Sweep();
    Check(
        retiring.states.ContainsKey(bot) == (missing != "ending"),
        "unavailability may recover but raid end stays terminal: " + missing
    );
}
var nestedLife = new Plugin();
var nestedBot = nestedLife.Add();
var nestedState = nestedLife.states[nestedBot];
nestedLife.NestedDeath(nestedState);
Check(
    nestedLife.OldCallbacks == 0 && nestedState.Retired,
    "inherited state still checks immediate death inside nested callback"
);
var crossLife = new Plugin();
var liveBot = crossLife.Add();
var deadBot = crossLife.Add();
deadBot.IsDead = true;
crossLife.OtherBot(crossLife.states[liveBot], deadBot);
Check(
    crossLife.Actions == 0
        && !crossLife.states[liveBot].Retired
        && crossLife.states[deadBot].Retired,
    "nested different bot never inherits caller lifecycle state"
);
var first = events.Select(JObject.Parse).First(x => (string?)x["feature"] == "C09");
Check(
    (int?)first["lifecycle"]?["cleanupSteps"] >= 70 && first["profileId"]!.ToString().Length > 0,
    "actual production cleanup and event schema exercised"
);
var sleep = new Plugin();
var sb = sleep.Add();
var beforeSleep = sleep.states[sb];
sleep.Act(sb);
sleep.InGhost = true;
sleep.Act(sb);
sleep.Sweep();
Check(
    beforeSleep.Retired && !sleep.states.ContainsKey(sb) && !beforeSleep.OwnCommand,
    "I02 sleeping immediately retires owned state"
);
Check(events.Any(x => x.Contains("\"state\":\"sleeping\"")), "I02 sleeping lifecycle recorded");
sleep.InGhost = false;
Time.unscaledTimeAsDouble = 20;
sleep.Sweep();
Check(
    sleep.states.ContainsKey(sb) && sleep.states[sb].Id != beforeSleep.Id,
    "I02 wake creates fresh generation"
);
int wakingActions = sleep.Actions;
sleep.Callback(beforeSleep);
sleep.Act(sb);
Check(
    sleep.OldCallbacks == 0 && sleep.Actions == wakingActions + 1,
    "I02 pre-sleep callbacks cannot act after wake"
);

// Player Scavs must follow the very same real lifecycle path as PMCs.
foreach (bool legacy in new[] { false, true })
{
    var sc = new Plugin();
    var playerScav = sc.Add();
    playerScav.Player.Profile.Info.Settings.Role = WildSpawnType.assault;
    if (legacy)
        playerScav.Player.Profile.Info.Nickname = "Scav (Player)";
    else
        playerScav.Player.Profile.Info.MainProfileNickname = "Player";
    sc.Act(playerScav);
    var generation = sc.states[playerScav];
    Check(sc.Actions == 1, "Player Scav action allowed");
    sc.InGhost = true;
    sc.Act(playerScav);
    sc.Sweep();
    Check(
        generation.Retired && !sc.states.ContainsKey(playerScav),
        "Player Scav sleeps and releases state"
    );
    sc.InGhost = false;
    Time.unscaledTimeAsDouble += 2;
    sc.Sweep();
    Check(sc.states.ContainsKey(playerScav), "Player Scav resumes after wake");
    int actions = sc.Actions;
    sc.Callback(generation);
    sc.Act(playerScav);
    Check(sc.Actions == actions + 1 && sc.OldCallbacks == 0, "Player Scav rejects stale callbacks");
    playerScav.Player.HealthController!.IsAlive = false;
    sc.Act(playerScav);
    sc.Sweep();
    Check(!sc.states.ContainsKey(playerScav), "Player Scav death clears state");
}
var dir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts/c09"));
Directory.CreateDirectory(dir);
File.WriteAllLines(
    Path.Combine(dir, "lifecycle.jsonl"),
    events.Where(x => x.Contains("\"feature\":\"C09\""))
);
File.WriteAllText(
    Path.Combine(dir, "lifecycle.json"),
    "[" + string.Join(",", events.Where(x => x.Contains("\"feature\":\"C09\""))) + "]"
);
Console.WriteLine(
    $"C09 production lifecycle + cleanup + generated boundaries: {checks} checks, {events.Count} events. Unity/native calls doubled; no live raid."
);
