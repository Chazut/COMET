using Comet.Client;
using EFT;
using SAIN;
using SAIN.Components;
using SAIN.Preset.Shared.Enums;
using SAIN.SAINComponent.Classes.EnemyClasses;
using SAIN.SAINComponent.Classes.Mover;
using UnityEngine;
using UnityEngine.AI;

int n = 0;
var events = new List<string>();
void C(bool ok, string why)
{
    n++;
    if (!ok)
        throw new Exception("S01 adapter: " + why);
}
SquadGrenadeHarness.Run();
SiegeHarness.Run();
SquadStormHarness.CheckRushAdmission();
void Step(float dt = .02f)
{
    Time.time += dt;
    Time.realtimeSinceStartup = Time.time;
    Time.frameCount++;
}
(Plugin p, BotComponent b, BotComponent support) Setup()
{
    Time.time = Time.realtimeSinceStartup = 10;
    Time.frameCount = 0;
    NavMesh.Valid = true;
    Physics.Blocked = false;
    SAINEnableClass.Bots.Clear();
    Api.Decisions.Labels.Clear();
    var p = new Plugin();
    p.config!.Crossfire.Participation = 100;
    var b = new BotComponent { Position = new(-3, 0, 0) };
    var support = new BotComponent();
    support.GoalEnemy!.IsVisible = true;
    var members = new Dictionary<string, BotComponent>
    {
        { b.ProfileId, b },
        { support.ProfileId, support },
    };
    foreach (var bot in members.Values)
    {
        bot.Squad.Members = members;
        bot.EnemyController.KnownEnemies.Add(bot.GoalEnemy!);
        SAINEnableClass.Bots[bot.ProfileId] = bot;
    }
    return (p, b, support);
}
void Plan(Plugin p, BotComponent b, Vector3? goal = null)
{
    p.Observe(b);
    if (goal.HasValue)
    {
        var r = p.states[b].Crossfire;
        r.Candidates.Clear();
        r.Candidates.Add(goal.Value);
        r.Candidate = 0;
        r.Route = null;
        r.Samples.Clear();
    }
    for (int i = 0; i < 200 && p.states[b].Crossfire.Policy.Trace?.State == "planning"; i++)
    {
        Step();
        p.Observe(b);
    }
    C(
        p.states[b].Crossfire.Policy.Trace?.State == "ready",
        "reachable useful angle planned: " + p.states[b].Crossfire.Policy.Trace?.Reason
    );
}
CrossfireAction Start(Plugin p, BotComponent b)
{
    var l = new CrossfireLayer(b.BotOwner, 21);
    C(l.IsActive() && b.SAINLayersActive, "native layer active");
    C(l.GetNextAction().Type == typeof(CrossfireAction), "native action selected");
    C(p.Choose(b), "generic choice yields while crossfire owns plan");
    Step();
    var a = new CrossfireAction(b.BotOwner);
    a.Start();
    C(a.Starts == 1, "native lifecycle start");
    C(p.states[b].Crossfire.Policy.Trace?.State == "moving", "route observed");
    return a;
}
void Save(Plugin p) => events.AddRange(p.diagnostics!.Lines);
void IssueStormOrder(Plugin p, BotComponent leader, BotComponent stayingBack)
{
    var cfg = p.config!;
    cfg.SquadStorm.Enabled = true;
    cfg.SquadStorm.Chad = 100;
    cfg.SquadStorm.Normal = 0;
    leader.Info.Personality = Personality.Chad;
    stayingBack.Info.Personality = Personality.Normal;
    foreach (var bot in new[] { leader, stayingBack })
        if (!p.states.ContainsKey(bot))
            p.states.Add(bot, new BotState(bot, cfg, p.session));
    C(p.ChooseStorm(leader) && !p.ChooseStorm(stayingBack), "one squad member joins S06 and one declines");
}
foreach (var (chance, contactAge, maneuverRange, crossfireOn, healing, storm, expectManeuver) in new[]
{
    (100, 1, 80, true, false, false, true),   // Both a flank and cover for a reload are available.
    (100, 1, 80, false, true, false, true),   // A patient is nearby, with plain crossfire disabled.
    (0, 1, 80, true, false, false, false),    // The squad declines its roll, then covers the reload.
    (100, 15, 80, false, true, false, false), // Cover can still start on an older contact.
    (100, 1, 20, true, true, false, false),   // Outside the maneuver range, within the cover range.
    (100, 1, 80, false, true, true, false),   // During an assault, cover the teammate who is healing.
})
{
    var (p, b, anchor) = Setup();
    Step(30);
    var cfg = p.config!;
    cfg.Crossfire.Enabled = crossfireOn;
    cfg.SquadManeuver.Chance = chance;
    cfg.SquadManeuver.MaxDistance = maneuverRange;
    b.GoalEnemy!.KnownPlaces.LastKnownPosition = new(0, 0, 30);
    anchor.GoalEnemy!.KnownPlaces.LastKnownPosition = b.GoalEnemy.KnownPlaces.LastKnownPosition;
    anchor.GoalEnemy.TimeSinceLastKnownUpdated = 0;
    b.GoalEnemy.TimeSinceLastKnownUpdated = contactAge;
    var patient = new BotComponent { Position = new(3, 0, 0), GoalEnemy = null };
    patient.BotOwner.Medecine.Using = healing;
    patient.BotOwner.WeaponManager.Reload.Reloading = !healing;
    patient.Squad.Members = b.Squad.Members;
    b.Squad.Members.Add(patient.ProfileId, patient);
    SAINEnableClass.Bots[patient.ProfileId] = patient;

    if (storm)
        IssueStormOrder(p, anchor, b);
    p.Observe(b);
    var runtime = p.states[b].Crossfire;
    var trace = runtime.Policy.Trace;
    C(runtime.Policy.Active && (expectManeuver
            ? runtime.Maneuver?.Support == anchor.ProfileId && trace?.CoverMate == null
            : runtime.Maneuver == null && trace?.CoverMate?.Mate == patient.ProfileId),
        $"maneuver before covering a busy teammate: chance={chance}, age={contactAge}, range={maneuverRange}, storm={storm}");
    if (!expectManeuver)
    {
        cfg.SquadManeuver.Chance = 100;
        cfg.SquadManeuver.MaxDistance = 80;
        Step(.6f);
        b.GoalEnemy.TimeSinceLastKnownUpdated = 0;
        p.Observe(b);
        C(runtime.Policy.Active && ReferenceEquals(trace, runtime.Policy.Trace)
            && runtime.Policy.Trace?.CoverMate?.Mate == patient.ProfileId,
            "a fresh flank opportunity does not interrupt active teammate cover");
    }
    p.EndAll();
}
{
    var (p, b, mate) = Setup();
    p.config!.SquadManeuver.Chance = 100;
    mate.GoalEnemy!.IsVisible = false;
    b.GoalEnemy!.KnownPlaces.LastKnownPosition = new(0, 0, 30);
    mate.GoalEnemy.KnownPlaces.LastKnownPosition = b.GoalEnemy.KnownPlaces.LastKnownPosition;
    IssueStormOrder(p, mate, b);
    p.Observe(b);
    C(p.states[b].Crossfire.Maneuver == null
        && p.diagnostics!.Lines.Any(line => line.Contains("maneuver-squad-storm-active")),
        "a fresh S03 is declined with a diagnostic for the active assault");
    Step((float)p.config.SquadStorm.Retry + .1f);
    b.GoalEnemy.TimeSinceLastKnownUpdated = mate.GoalEnemy.TimeSinceLastKnownUpdated = 0;
    p.Observe(b);
    C(p.states[b].Crossfire.Maneuver != null, "S03 can start once the assault verdict expires");
    p.EndAll();
}
{
    var (p, b, leader) = Setup();
    var casualty = new BotComponent { Squad = b.Squad };
    b.Squad.Members.Add(casualty.ProfileId, casualty);
    IssueStormOrder(p, leader, b);
    casualty.IsDead = true;
    casualty.Player.HealthController.IsAlive = false;
    p.Death(casualty, b, b.GoalEnemy!.EnemyPlayer);
    Step(.81f);
    p.Observe(b);
    C(p.states[b].Crossfire.Policy.Trace?.Trade?.Mate == casualty.ProfileId && p.Choose(b),
        "an eligible trade can start and continue during the assault");
    p.EndAll();
}
foreach (bool indoors in new[] { false, true })
{
    var (p, b, mate) = Setup();
    p.config!.SquadManeuver.Chance = 100;
    b.Player.Environment = indoors ? EnvironmentType.Indoor : EnvironmentType.Outdoor;
    if (indoors)
        NavMesh.Route = (start, end) => [start, new Vector3(4, 0, 8), end];
    b.GoalEnemy!.KnownPlaces.LastKnownPosition = new(0, 0, 30);
    mate.GoalEnemy!.KnownPlaces.LastKnownPosition = b.GoalEnemy.KnownPlaces.LastKnownPosition;
    IssueStormOrder(p, mate, b);
    p.Observe(b);
    NavMesh.Route = null;
    var r = p.states[b].Crossfire;
    C(r.Policy.Active && r.Maneuver == null && (r.Policy.Trace?.Entrance != null) == indoors,
        "staying back during S06 allows " + (indoors ? "a secondary entrance" : "ordinary crossfire"));
    p.EndAll();
}
{
    var (p, flanker, support) = Setup();
    p.config!.SquadManeuver.Chance = 100;
    support.Info.Personality = Personality.Rat;
    support.GoalEnemy!.IsVisible = false;
    flanker.GoalEnemy!.KnownPlaces.LastKnownPosition = new(0, 0, 30);
    support.GoalEnemy.KnownPlaces.LastKnownPosition = flanker.GoalEnemy.KnownPlaces.LastKnownPosition;
    var leader = new BotComponent { Squad = flanker.Squad };
    leader.GoalEnemy!.EnemyProfileId = "other-enemy";
    flanker.Squad.Members.Add(leader.ProfileId, leader);
    p.Observe(flanker);
    var assigned = p.states[flanker].Crossfire.Maneuver;
    C(assigned?.Support == support.ProfileId, "S03 has assigned support before the assault");
    leader.GoalEnemy.EnemyProfileId = flanker.GoalEnemy.EnemyProfileId;
    IssueStormOrder(p, leader, support);
    p.Observe(support);
    C(p.states[support].Crossfire.Maneuver == assigned && p.Choose(support),
        "a member who declined S06 can take its existing S03 role");
    Plan(p, flanker);
    C(p.states[flanker].Crossfire.Maneuver == assigned && p.Choose(flanker),
        "an existing flank keeps progressing during the squad assault");
    p.EndAll();
}
foreach (bool crossfireOn in new[] { false, true })
foreach (bool maneuverOn in new[] { true, false })
foreach (bool mateSees in new[] { false, true })
{
    var (p, b, mate) = Setup();
    var cfg = p.config!;
    cfg.Crossfire.Enabled = crossfireOn;
    cfg.SquadManeuver.Enabled = maneuverOn;
    cfg.SquadManeuver.Chance = 100;
    cfg.CoverMate.Enabled = cfg.SquadTrade.Enabled = cfg.SquadStorm.Enabled = false;
    mate.Info.Personality = Personality.Rat;
    mate.GoalEnemy!.IsVisible = mateSees;
    b.GoalEnemy!.KnownPlaces.LastKnownPosition = new(0, 0, 30);
    mate.GoalEnemy.KnownPlaces.LastKnownPosition = b.GoalEnemy.KnownPlaces.LastKnownPosition;
    // A 15s contact with visible support isolates crossfire. A fresh hidden contact isolates maneuver.
    b.GoalEnemy.TimeSinceLastKnownUpdated = mateSees ? 15 : 1;
    p.Observe(b);
    var runtime = p.states.TryGetValue(b, out var state) ? state.Crossfire : null;
    bool expectManeuver = maneuverOn && !mateSees;
    C(
        (runtime?.Policy.Active == true) == (expectManeuver || crossfireOn && mateSees)
            && (runtime?.Maneuver != null) == expectManeuver,
        $"independent squad settings: crossfire={crossfireOn}, maneuver={maneuverOn}, visible support={mateSees}"
    );
    if (expectManeuver)
    {
        p.Observe(mate);
        C(p.states[mate].Crossfire.Maneuver == runtime!.Maneuver, "support joins the same maneuver");
        var supportAction = new CrossfireAction(mate.BotOwner);
        supportAction.Start();
        supportAction.OnSteeringTicked();
        Plan(p, b);
        var flankAction = Start(p, b);
        cfg.Crossfire.Enabled = !crossfireOn;
        Step();
        flankAction.Update(new());
        supportAction.OnSteeringTicked();
        C(
            runtime.Policy.Active && runtime.ManeuverRole!.Confirmed
                && p.states[mate].Crossfire.ManeuverRole!.Confirmed,
            "both maneuver roles keep executing across a crossfire toggle"
        );
        cfg.SquadManeuver.Enabled = false;
        p.Sample(b);
        p.Sample(mate);
        C(
            !runtime.Policy.Active && !p.states[mate].Crossfire.Policy.Active && p.Claims == 0,
            "disabling maneuver releases both roles and their claims"
        );
    }
    p.EndAll();
}
foreach (bool crossfireOn in new[] { false, true })
{
    var (p, b, mate) = Setup();
    p.config!.SquadManeuver.Enabled = true;
    p.config.SquadManeuver.Chance = 0;
    p.config.Crossfire.Enabled = crossfireOn;
    b.GoalEnemy!.KnownPlaces.LastKnownPosition = new(0, 0, 30);
    mate.GoalEnemy!.KnownPlaces.LastKnownPosition = b.GoalEnemy.KnownPlaces.LastKnownPosition;
    p.Observe(b);
    var runtime = p.states[b].Crossfire;
    C(runtime.Maneuver == null && runtime.Policy.Active == crossfireOn,
        "a declined maneuver falls back to plain crossfire only when enabled");
    p.EndAll();
}
foreach (var (age, mateSees, indoors, maneuverAge, crossfireAge, expected) in new[]
{
    (15f, true, false, 10, 20, "S01"),
    (15f, true, true, 10, 20, "S02"),
    (10f, false, false, 10, 20, "S03"),
    (10.01f, false, false, 10, 20, "none"),
    (20f, true, false, 10, 20, "S01"),
    (20.01f, true, false, 10, 20, "none"),
    (8f, true, false, 5, 9, "S01"),
    (10f, false, false, 12, 8, "S03"),
})
{
    var (p, b, mate) = Setup();
    Step(30);
    p.config!.SquadManeuver.Chance = 100;
    p.config.SquadManeuver.ContactAge = maneuverAge;
    p.config.Crossfire.ContactAge = crossfireAge;
    if (indoors)
    {
        b.Player.Environment = EnvironmentType.Indoor;
        NavMesh.Route = (start, end) => [start, new Vector3(4, 0, 8), end];
    }
    b.GoalEnemy!.KnownPlaces.LastKnownPosition = new(0, 0, 30);
    mate.GoalEnemy!.KnownPlaces.LastKnownPosition = b.GoalEnemy.KnownPlaces.LastKnownPosition;
    b.GoalEnemy.TimeSinceLastKnownUpdated = age;
    mate.GoalEnemy.TimeSinceLastKnownUpdated = mateSees ? 0 : age;
    mate.GoalEnemy.IsVisible = mateSees;
    p.Observe(b);
    NavMesh.Route = null;
    var r = p.states[b].Crossfire;
    var actual = r switch
    {
        { Policy.Active: false } => "none",
        { Maneuver: not null } => "S03",
        { Policy.Trace.Entrance: not null } => "S02",
        _ => "S01",
    };
    C(actual == expected, $"contact age {age}s, mate sees {mateSees}: expected {expected}, got {actual}");
    p.EndAll();
}
{
    var (p, b, m) = Setup();
    Plan(p, b);
    C(p.Claims == 1 && !Api.Decisions.Labels.ContainsKey(b.ProfileId), "planning is not execution");
    var a = Start(p, b);
    var r = p.states[b].Crossfire;
    C(r.Policy.Trace!.Applied, "native route applied");
    C(
        r.Policy.Trace.MoveOrders == 1 && r.Policy.Trace.MoveAccepted,
        "native move request recorded"
    );
    C(
        r.Policy.Trace.SprintRequested
            == (Vector3.ProjectOnPlane(r.Goal - b.Position, Vector3.up).magnitude > 8),
        "source sprint distance rule preserved"
    );
    a.OnSteeringTicked();
    C(b.Steering.Moves == 1, "steering follows native route");
    var path = (BotPathDataManual)b.Mover.ActivePath!;
    b.Position = r.Goal;
    Step();
    a.Update(new());
    C(r.Policy.Trace.State == "holding" && path.Stops == 1, "arrival stops route and holds angle");
    b.GoalEnemy!.IsVisible = true;
    p.Sample(b);
    C(r.Policy.Active, "visibility does not cancel source angle");
    a.OnSteeringTicked();
    C(b.Steering.Points > 0, "hold steers to contact");
    Step(20.1f);
    p.Sample(b);
    C(!r.Policy.Active, "source angle hold has bounded lifetime");
    a.Stop();
    Save(p);
}
{
    var (p, b, m) = Setup();
    b.GoalEnemy!.KnownPlaces.LastKnownPosition = new(0, 0, 12);
    m.GoalEnemy!.EnemyPosition = new(0, 0, 10);
    b.GoalEnemy.EnemyPlayer.Environment = EnvironmentType.Indoor;
    Plan(p, b);
    var r = p.states[b].Crossfire;
    C(
        Vector3.Distance(r.Known, m.GoalEnemy.EnemyPosition) < .001f,
        "source uses supporting bot's visible target, not own stale memory"
    );
    C(r.Indoor, "target indoors uses indoor candidates even when bot is outside");
    C(r.Policy.Trace!.Source == "visible-squadmate-enemy-position", "trace names actual source");
}
foreach (
    string scope in new[]
    {
        "scav",
        "human",
        "dead",
        "inactive",
        "ghost",
        "remote",
        "world",
        "enabled",
        "feature",
        "ending",
    }
)
{
    var (p, b, m) = Setup();
    Plan(p, b);
    Start(p, b);
    var path = (BotPathDataManual)b.Mover.ActivePath!;
    switch (scope)
    {
        case "scav":
            b.Pmc = false;
            break;
        case "human":
            b.Player.IsAI = false;
            break;
        case "dead":
            b.IsDead = true;
            break;
        case "inactive":
            b.BotActive = false;
            break;
        case "ghost":
            p.Ghosts.Add(b.ProfileId);
            break;
        case "remote":
            p.Authority = false;
            break;
        case "world":
            p.world = null;
            break;
        case "enabled":
            p.config!.Enabled = false;
            break;
        case "feature":
            p.config!.Crossfire.Enabled = false;
            break;
        case "ending":
            b.GameEnding = true;
            break;
    }
    p.Sample(b);
    C(
        !p.states[b].Crossfire.Policy.Active && p.Claims == 0 && path.Stops == 1,
        "integration releases route: " + scope
    );
    Save(p);
}
foreach (
    string change in new[]
    {
        "visibility",
        "support-dead",
        "support-ghost",
        "support-hidden",
        "support-moved",
        "suppression",
        "ammo",
        "hands",
        "reload",
        "airborne",
        "injury",
        "squad",
        "position",
    }
)
{
    var (p, b, m) = Setup();
    Plan(p, b);
    Start(p, b);
    var r = p.states[b].Crossfire;
    switch (change)
    {
        case "visibility":
            b.GoalEnemy!.IsVisible = true;
            break;
        case "support-dead":
            m.IsDead = true;
            break;
        case "support-ghost":
            p.Ghosts.Add(m.ProfileId);
            break;
        case "support-hidden":
            m.GoalEnemy!.IsVisible = false;
            break;
        case "support-moved":
            m.Position = new(80, 0, 0);
            break;
        case "suppression":
            b.Suppression.IsSuppressed = true;
            break;
        case "ammo":
            b.BotOwner.WeaponManager.HaveBullets = false;
            break;
        case "hands":
            ((Player.FirearmController)b.Player.HandsController).Ready = false;
            break;
        case "reload":
            b.BotOwner.WeaponManager.Reload.Reloading = true;
            break;
        case "airborne":
            b.Player.MovementContext.IsGrounded = false;
            break;
        case "injury":
            b.Memory.Health.HealthStatus = ETagStatus.BadlyInjured;
            break;
        case "squad":
            b.Decision.CurrentSquadDecision = ESquadDecision.Retreat;
            break;
        case "position":
            b.GoalEnemy!.KnownPlaces.LastKnownPosition = new(40, 0, 40);
            break;
    }
    p.Sample(b);
    Step(2);
    p.Sample(b);
    C(r.Policy.Active, "source active plan survives former COMET veto: " + change);
    p.End(b);
    C(p.Claims == 0, "cleanup releases claim");
    Save(p);
}
foreach (string reason in new[] { "incoming-fire", "contact-changed", "timeout" })
{
    var (p, b, m) = Setup();
    Plan(p, b);
    Start(p, b);
    if (reason == "incoming-fire")
        b.BotOwner.Memory.IsUnderFire = true;
    else if (reason == "contact-changed")
        b.GoalEnemy!.EnemyProfileId = "different";
    else
        Step(12.1f);
    p.Sample(b);
    C(!p.states[b].Crossfire.Policy.Active && p.Claims == 0, "source termination: " + reason);
    Save(p);
}
{
    var (p, b, m) = Setup();
    p.config!.Crossfire.Participation = 0;
    m.Position = new(90, 0, 0);
    p.Observe(b);
    C(p.states[b].Crossfire.Policy.Active, "visible supporting teammate beyond 35m activates regardless of legacy participation");
    Save(p);
}
{
    var (p, b, m) = Setup();
    NavMesh.Valid = false;
    p.Observe(b);
    for (int i = 0; i < 250; i++)
    {
        Step();
        p.Sample(b);
    }
    C(b.Mover.Walks == 0, "no invented route on invalid NavMesh");
    p.EndAll();
    C(p.Claims == 0, "raid cleanup");
    Save(p);
}

// Native firing is part of steering, including while walking and after arriving.
foreach (bool atPost in new[] { false, true })
foreach (bool primaryVisible in new[] { false, true })
{
    var (p, b, m) = Setup();
    Plan(p, b);
    var a = Start(p, b);
    if (atPost)
    {
        b.Position = p.states[b].Crossfire.Goal;
        Step();
        a.Update(new());
    }
    b.GoalEnemy!.IsVisible = primaryVisible;
    b.Shoot.Result = true;
    int points = b.Steering.Points,
        moves = b.Steering.Moves;
    a.OnSteeringTicked();
    C(
        b.Shoot.Calls == 1 && b.Steering.Priorities == 1,
        "visible fire retains native aim, at post=" + atPost + ", primary=" + primaryVisible
    );
    C(
        b.Steering.Points == points && b.Steering.Moves == moves,
        "tactical steering does not overwrite firing aim"
    );
    C(b.Suppression.Calls == 0, "successful fire does not also request suppression");
    C(p.states[b].Crossfire.Policy.Active, "firing does not abandon the angle plan");
}
foreach (TacticalSteeringKind kind in Enum.GetValues<TacticalSteeringKind>())
{
    var (p, b, m) = Setup();
    b.GoalEnemy!.IsVisible = false;
    b.Shoot.Result = true;
    b.Steering.PriorityResult = false;
    C(
        TacticalSteeringFire.TryShoot(b, kind),
        "all four steering routes call visible-target fire: " + kind
    );
    C(
        b.Shoot.Calls == 1
            && b.Steering.Priorities == (kind == TacticalSteeringKind.PostCombat ? 0 : 1),
        "source aiming order: " + kind
    );
    C(
        b.Steering.LastKnown == (kind == TacticalSteeringKind.CornerChase ? 1 : 0),
        "only chase uses last-known aim fallback"
    );
    b.Shoot.Result = false;
    b.Suppression.Result = true;
    int calls = b.Suppression.Calls;
    bool suppress =
        kind is TacticalSteeringKind.SquadAngle or TacticalSteeringKind.CoverFeint or TacticalSteeringKind.OutdoorRelocation;
    C(
        TacticalSteeringFire.TryShoot(b, kind) == suppress
            && b.Suppression.Calls - calls == (suppress ? 1 : 0),
        "source suppression semantics: " + kind
    );
    b.Suppression.Result = false;
    C(
        !TacticalSteeringFire.TryShoot(b, kind),
        "no eligible shot returns to tactical steering: " + kind
    );
}
var roster = new Dictionary<string, int> { { "b", 2 }, { "a", 1 } };
var identity = Comet.Core.SquadMemberIdentity.Get(roster);
C(identity == "a|b", "identity uses sorted member ids");
long before = GC.GetAllocatedBytesForCurrentThread();
for (int i = 0; i < 10000; i++)
    Comet.Core.SquadMemberIdentity.Get(roster);
long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
C(allocated == 0, "unchanged roster has no identity allocations");
roster.Remove("b");
roster.Add("c", 3);
C(Comet.Core.SquadMemberIdentity.Get(roster) == "a|c", "same-count replacement refreshes identity");
roster.Remove("a");
C(Comet.Core.SquadMemberIdentity.Get(roster) == "c", "departure refreshes identity");
roster.Add("z", 9);
C(Comet.Core.SquadMemberIdentity.Get(roster) == "c|z", "arrival refreshes identity");
roster["c"] = 100;
C(Comet.Core.SquadMemberIdentity.Get(roster) == "c|z", "member value does not affect identity");
C(
    Comet.Core.SquadMemberIdentity.Get(new Dictionary<string, int> { { "z", 1 }, { "c", 2 } })
        == "c|z",
    "different rosters retain equivalent identity"
);
roster.Clear();
C(Comet.Core.SquadMemberIdentity.Get(roster) == "", "empty roster clears identity");
var file = Path.GetFullPath(
    Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts/s01-adapter-events.jsonl")
);
File.WriteAllLines(file, events);
Console.WriteLine(
    $"S01 source adapter: {n} checks; {events.Count} events. Engine doubles, no raid simulation."
);
