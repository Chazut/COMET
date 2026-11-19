using Comet.Client;
using Comet.Core;
using Newtonsoft.Json.Linq;
using SAIN.Components;
using SAIN.SAINComponent.Classes.EnemyClasses;
using UnityEngine;

int n = 0;
var events = new List<string>();
void Check(bool ok, string why)
{
    n++;
    if (!ok)
        throw new Exception("E03 adapter: " + why);
}
(Plugin p, BotComponent b, BotState s) Setup()
{
    Time.time = Time.realtimeSinceStartup = 10;
    Time.frameCount = 0;
    Physics.Calls = 0;
    Physics.Blocked = Physics.Open = Physics.Throw = false;
    return (new(), new(), new());
}
void Tick(Plugin p, BotComponent b, BotState s, double now, bool visible)
{
    Time.time = Time.realtimeSinceStartup = (float)now;
    Time.frameCount++;
    b.GoalEnemy!.IsVisible = visible;
    p.Tick(b, s);
}
void Pattern(Plugin p, BotComponent b, BotState s)
{
    for (int i = 0; i < 36; i++)
        Tick(p, b, s, 10 + i * .1, i % 13 < 5);
}
Situation Situation() =>
    new()
    {
        Now = 13.5,
        Target = "target",
        KnownX = 10,
        KnownY = 0,
        KnownZ = 10,
        HasContact = true,
        ContactAge = .1,
        KnownDistance = 14,
    };
{
    var (p, b, s) = Setup();
    Pattern(p, b, s);
    Check(
        s.Encounter.Trace?.Eligible == true,
        "three observed returns recognized through production adapter"
    );
    Check(s.Encounter.Trace?.Count == 3, "continuous visibility counts once");
    Check(Physics.Calls <= 30, "probe cache limits work");
    var t = p.Read(b, s, Situation());
    Check(t?.Eligible == true, "fresh matching context read");
    var sit = Situation();
    sit.Encounter = t;
    sit.IsAi = sit.IsSupportedBot = sit.IsAuthority = sit.IsActive = true;
    var result = new HiddenEnemyDecision(p.config!).Evaluate(sit);
    s.Decision = result.Decision?.ToString() ?? "";
    s.Candidates = result.Candidates;
    p.Choice(s, sit);
    Check(
        sit.Encounter!.AppliedBonus == 0 && sit.Encounter.Alternatives.Length > 0,
        "actual chooser contribution and alternatives attached"
    );
    Check(s.Encounter.Trace!.Choice == s.Decision, "choice snapshot detached from sampler");
    p.End(s);
    Check(s.Encounter.Memory.Count == 0 && s.Encounter.Trace == null, "raid cleanup");
    Check(
        JObject.Parse(p.diagnostics!.Lines.Last())["encounter"]!["state"]!.Value<string>()
            == "ended",
        "cleanup terminal event"
    );
    events.AddRange(p.diagnostics.Lines);
}
foreach (
    string exclude in new[]
    {
        "scav",
        "human",
        "remote",
        "ghost",
        "disabled",
        "global",
        "inactive",
        "dead",
        "ending",
        "forgotten",
        "invalid",
        "shared",
        "open",
        "blocked",
        "look-away",
    }
)
{
    var (p, b, s) = Setup();
    Pattern(p, b, s);
    switch (exclude)
    {
        case "scav":
            b.Pmc = false;
            break;
        case "human":
            b.Player.IsAI = false;
            break;
        case "remote":
            p.Authority = false;
            break;
        case "ghost":
            p.IsGhost = true;
            break;
        case "disabled":
            p.config!.Encounter.Enabled = false;
            break;
        case "global":
            p.config!.Enabled = false;
            break;
        case "inactive":
            b.BotActive = false;
            break;
        case "dead":
            b.IsDead = true;
            break;
        case "ending":
            b.GameEnding = true;
            break;
        case "forgotten":
            b.EnemyController.KnownEnemies.Clear();
            break;
        case "invalid":
            b.GoalEnemy!.Valid = false;
            break;
        case "shared":
            b.GoalEnemy!.KnownPlaces.LastKnownPlace = new();
            break;
        case "open":
            Physics.Open = true;
            break;
        case "blocked":
            Physics.Blocked = true;
            break;
        case "look-away":
            b.PlayerComponent.CharacterController.CurrentControlLookDirection = new(-10, 0, -10);
            break;
    }
    Tick(p, b, s, 13.9, false);
    Check(s.Encounter.Trace?.Eligible != true, "scope/evidence exclusion " + exclude);
    events.AddRange(p.diagnostics!.Lines);
}
foreach (
    string kind in new[] { "same-id-new-instance", "different-target", "personal-place-changed" }
)
{
    var (p, b, s) = Setup();
    Pattern(p, b, s);
    if (kind == "personal-place-changed")
        b.GoalEnemy!.KnownPlaces.LastSeenPlace.Position = new(40, 0, 40);
    else
    {
        b.GoalEnemy = new Enemy
        {
            EnemyProfileId = kind == "different-target" ? "remote-human" : "target",
        };
        b.EnemyController.KnownEnemies.Add(b.GoalEnemy);
    }
    Tick(p, b, s, 13.6, false);
    Check(s.Encounter.Trace?.Eligible != true, "no evidence inheritance " + kind);
    events.AddRange(p.diagnostics!.Lines);
}
{
    var (p, b, s) = Setup();
    Pattern(p, b, s);
    Time.time = Time.realtimeSinceStartup = 13.61f;
    Time.frameCount++;
    p.doorBudget.TryBegin(Time.frameCount);
    p.doorBudget.TryBegin(Time.frameCount);
    int before = Physics.Calls;
    b.Position = new(4, 0, 0);
    b.Player.PlayerBones.Head.position = new(4, 1, 0);
    p.Tick(b, s);
    Check(
        Physics.Calls == before && s.Encounter.Trace?.Eligible != true,
        "shared work budget refuses stale moved geometry"
    );
    events.AddRange(p.diagnostics!.Lines);
}
{
    var (p, b, s) = Setup();
    Physics.Throw = true;
    Tick(p, b, s, 10, true);
    Tick(p, b, s, 11, true);
    Check(s.Encounter.Fault && p.Logger.Warnings.Count == 1, "adapter exception contained once");
    Check(s.Encounter.Memory.Count == 0, "error releases evidence");
}
{
    var (p, b, s) = Setup();
    for (int i = 0; i < 33; i++)
        b.EnemyController.KnownEnemies.Add(new Enemy());
    Tick(p, b, s, 10, true);
    Check(
        Physics.Calls == 0 && s.Encounter.Memory.Count == 0,
        "contact scan bounded before probes"
    );
}
{
    var (p, b, s) = Setup();
    Pattern(p, b, s);
    var other = new BotState();
    Tick(p, b, other, 13.6, false);
    Check(other.Encounter.Trace?.Eligible != true, "another observer cannot borrow evidence");
    var e = b.GoalEnemy!;
    e.IsVisible = true;
    var v = Situation();
    v.Visible = true;
    Check(p.Read(b, s, v)?.Eligible != true, "visible context rechecked inside sample throttle");
}
foreach (var json in events)
{
    var e = JObject.Parse(json);
    Check(
        e["feature"]!.Value<string>() == "E03" && e["stage"]!.Value<string>() == "evaluated",
        "diagnostic event never claims execution"
    );
}
Directory.CreateDirectory("artifacts/e03");
File.WriteAllLines("artifacts/e03/encounter.jsonl", events);
Console.WriteLine(
    $"{n} E03 production adapter checks passed; {events.Count} production events exported"
);
