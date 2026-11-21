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
TacticalAmmoTests.Run();
CoverFeintJumpTests.Run();
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
        bot.BotOwner.BotsGroup = b.BotOwner.BotsGroup;
        bot.Squad.SquadInfo = b.Squad.SquadInfo;
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
foreach (string next in new[] { "crossfire", "killer", "aimed-at" })
{
    var (p, b, support) = Setup();
    p.config!.SquadManeuver.Enabled = false;
    p.config.CoverMate.Enabled = false;
    var casualty = new BotComponent { Squad = b.Squad, IsDead = true };
    casualty.Player.HealthController.IsAlive = false;
    casualty.GoalEnemy!.EnemyProfileId = "aimed-at";
    b.Squad.Members.Add(casualty.ProfileId, casualty);
    p.Death(casualty, b, new Player { ProfileId = "killer" });
    support.GoalEnemy!.IsVisible = next == "crossfire";
    Step(.1f);
    p.Observe(b);
    var r = p.states[b].Crossfire;
    C(r.Policy.RetryAt == 0 && r.Trade.Memory.Peek(Time.time, p.config.SquadTrade) != null,
        "an unrelated goal leaves the trade opportunity and squad cooldown untouched");
    C(p.diagnostics!.Lines.Any(line => line.Contains("tradeOtherEnemy")),
        "S05 reports why the current goal was skipped");
    if (next == "crossfire")
    {
        C(r.Policy.Active && r.Policy.Trace!.Trade == null && r.Support == support.ProfileId
            && r.Contact == b.GoalEnemy!.EnemyProfileId,
            "ordinary crossfire starts against the other enemy on the same decision");
    }
    else
    {
        C(!r.Policy.Active, "no trade starts against the unrelated goal");
        b.GoalEnemy = new Enemy { EnemyProfileId = next };
        b.EnemyController.KnownEnemies.Add(b.GoalEnemy);
        Step((float)p.config.Crossfire.Interval + .01f);
        p.Observe(b);
        C(r.Policy.Active && r.Policy.Trace!.Trade?.Mate == casualty.ProfileId
            && r.Contact == next && r.Trade.Memory.Peek(Time.time, p.config.SquadTrade) == null,
            "switching to the " + next + " starts the retained trade immediately");
    }
    p.EndAll();
}
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
    anchor.GoalEnemy.IsVisible = false;
    anchor.Info.Personality = Personality.Rat;
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
    leader.BotOwner.BotsGroup = flanker.BotOwner.BotsGroup;
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
{
    var (p, flanker, support) = Setup();
    p.config!.SquadManeuver.Chance = 100;
    support.Info.Personality = Personality.Rat;
    support.GoalEnemy!.IsVisible = false;
    var members = flanker.Squad.Members;
    var rear = new BotComponent { Squad = flanker.Squad, Position = new(0, 0, -5) };
    rear.Info.Personality = Personality.Rat;
    members.Add(rear.ProfileId, rear);
    foreach (var b in members.Values)
        b.GoalEnemy!.KnownPlaces.LastKnownPosition = new(0, 0, 30);
    p.Observe(flanker);
    var plan = p.states[flanker].Crossfire.Maneuver;
    C(plan?.Group == flanker.Squad.SquadInfo!.Id && plan.Member(rear.ProfileId)?.Role == "rear",
        "S03 plan belongs to SAIN's squad");
    members.Remove(rear.ProfileId);
    support.GoalEnemy.IsVisible = true;
    p.Observe(support);
    C(p.states[support].Crossfire.Maneuver == plan && p.Choose(support),
        "support can take its assigned role after the rear guard leaves and contact becomes visible");
    C(p.states[support].Crossfire.Policy.Trace?.Maneuver?.SquadId == plan!.Group,
        "the support trace identifies the original SAIN squad");
    Step();
    p.Sample(support);
    C(plan!.Member(rear.ProfileId)!.Status == "ended" && plan.Active,
        "departed role is dropped while the surviving pair keeps its plan");
    support.Squad.SquadInfo = new();
    Step();
    p.Sample(support);
    C(p.states[support].Crossfire.Maneuver == null && plan.Active
        && plan.Member(support.ProfileId)!.Reason == "maneuver-squad-changed",
        "a bot transferred to another SAIN squad releases its own role");
    p.EndAll();
}
{
    var (p, b, mate) = Setup();
    var cfg = p.config!;
    cfg.SquadManeuver.Chance = 0;
    mate.GoalEnemy!.IsVisible = false;
    b.GoalEnemy!.KnownPlaces.LastKnownPosition = mate.GoalEnemy.KnownPlaces.LastKnownPosition = new(0, 0, 30);
    p.Observe(b);
    cfg.SquadManeuver.Chance = 100;
    var newcomer = new BotComponent { Squad = b.Squad, GoalEnemy = null };
    b.Squad.Members.Add(newcomer.ProfileId, newcomer);
    Step(.6f);
    p.Observe(mate);
    C(p.states[mate].Crossfire.Maneuver == null, "arrival cannot bypass a declined S03 roll");
    Step(25.4f);
    mate.GoalEnemy.TimeSinceLastKnownUpdated = 0;
    p.Observe(mate);
    C(p.states[mate].Crossfire.Maneuver == null &&
        p.diagnostics!.Lines.Any(s => s.Contains("maneuver-cached-roll-declined")),
        "S03 still honors the original refusal 26 seconds later");
    Step(4);
    b.GoalEnemy.TimeSinceLastKnownUpdated = 0;
    p.Observe(b);
    C(p.states[b].Crossfire.Maneuver == null, "at exactly 30 seconds the squad still keeps its verdict");
    Step(.6f);
    p.Observe(mate);
    C(p.states[mate].Crossfire.Maneuver != null, "the next eligible proposal can start after 30 seconds");
    p.EndAll();
}
{
    var (p, b, mate) = Setup();
    var cfg = p.config!;
    cfg.SquadManeuver.Chance = 0;
    mate.GoalEnemy!.IsVisible = false;
    b.GoalEnemy!.KnownPlaces.LastKnownPosition = mate.GoalEnemy.KnownPlaces.LastKnownPosition = new(0, 0, 30);
    p.Observe(b);
    cfg.SquadManeuver.Chance = 100;
    mate.Squad.SquadInfo = b.Squad.SquadInfo = new();
    Step(.6f);
    p.Observe(mate);
    C(p.states[mate].Crossfire.Maneuver?.Group == mate.Squad.SquadInfo.Id,
        "a different SAIN squad with identical members gets its own plan");
    p.EndAll();
}
foreach (bool missingInfo in new[] { false, true })
{
    var (p, b, mate) = Setup();
    p.config!.SquadManeuver.Chance = 100;
    mate.GoalEnemy!.IsVisible = false;
    b.GoalEnemy!.KnownPlaces.LastKnownPosition = new(0, 0, 30);
    if (missingInfo)
        b.Squad.SquadInfo = null;
    else
        b.Squad.SquadInfo!.Id = "";
    p.Observe(b);
    C(p.states[b].Crossfire.Maneuver == null
        && p.diagnostics!.Lines.Any(s => s.Contains("maneuver-no-squad-id")),
        "S03 declines with a diagnostic until SAIN has a squad ID");
    p.EndAll();
}
{
    var (p, leader, mate) = Setup();
    var cfg = p.config!;
    cfg.SquadManeuver.Chance = 100;
    leader.GoalEnemy!.IsVisible = mate.GoalEnemy!.IsVisible = false;
    leader.GoalEnemy.KnownPlaces.LastKnownPosition = mate.GoalEnemy.KnownPlaces.LastKnownPosition = new(0, 0, 30);
    var extra = new BotComponent { Squad = leader.Squad, GoalEnemy = null };
    var members = leader.Squad.Members;
    members.Add(extra.ProfileId, extra);
    IssueStormOrder(p, leader, mate);
    members.Remove(extra.ProfileId);
    cfg.SquadStorm.Chad = 0;
    cfg.SquadStorm.Normal = 100;
    Step(.6f);
    C(p.ChooseStorm(leader) && !p.ChooseStorm(mate), "S06 retains both personal verdicts after a departure");
    p.Observe(mate);
    C(p.states[mate].Crossfire.Maneuver == null
        && p.diagnostics!.Lines.Any(s => s.Contains("maneuver-squad-storm-active")),
        "the same S06 verdict still blocks a fresh S03 for the survivors");
    var enemy = mate.GoalEnemy.EnemyProfileId;
    mate.GoalEnemy.EnemyProfileId = "different-enemy";
    C(p.ChooseStorm(mate), "a second enemy has a separate S06 verdict");
    mate.GoalEnemy.EnemyProfileId = enemy;
    var eftGroup = mate.BotOwner.BotsGroup;
    mate.Squad.SquadInfo = new();
    C(!p.ChooseStorm(mate), "S06 is independent of the SAIN squad ID");
    mate.BotOwner.BotsGroup = null;
    C(!p.ChooseStorm(mate), "S06 has no verdict without an EFT group");
    mate.BotOwner.BotsGroup = new();
    C(p.ChooseStorm(mate), "a different EFT group with identical members gets a separate verdict");
    mate.BotOwner.BotsGroup = eftGroup;
    C(!p.ChooseStorm(mate), "returning to the original EFT group finds the original verdict");
    Step((float)cfg.SquadStorm.Retry + .1f);
    mate.GoalEnemy.TimeSinceLastKnownUpdated = 0;
    C(p.ChooseStorm(mate), "the original group re-evaluates the enemy once its verdict expires");
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
    b.GoalEnemy.TimeSinceLastKnownUpdated = 1;
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
        mate.GoalEnemy.IsVisible = true;
        p.Observe(mate);
        C(p.states[mate].Crossfire.Maneuver == runtime!.Maneuver,
            "support that regained sight joins the existing maneuver");
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
foreach (var condition in new[] { "healthy", "injured", "self-care", "short-mag", "no-support" })
{
    var (p, scout, shooter) = Setup();
    var cfg = p.config!;
    cfg.SquadManeuver.Chance = 100;
    cfg.Crossfire.Participation = 0;
    cfg.Crossfire.Enabled = cfg.CoverMate.Enabled = cfg.SquadTrade.Enabled = false;
    scout.Info.Personality = Personality.Chad;
    shooter.Info.Personality = Personality.Normal;
    scout.Position = new(0, 0, 1);
    shooter.Position = new(0, 15, 0);
    var rear = new BotComponent { Squad = scout.Squad, Position = new(1, 0, -1) };
    rear.Info.Personality = Personality.Rat;
    var secondShooter = new BotComponent { Squad = scout.Squad, Position = new(0, 14, 0) };
    secondShooter.Info.Personality = Personality.Rat;
    foreach (var bot in new[] { rear, secondShooter })
    {
        scout.Squad.Members.Add(bot.ProfileId, bot);
        SAINEnableClass.Bots[bot.ProfileId] = bot;
    }
    foreach (var bot in scout.Squad.Members.Values)
    {
        bot.GoalEnemy!.IsVisible = false;
        bot.GoalEnemy.KnownPlaces.LastKnownPosition = new(0, 0, 30);
    }
    rear.BotOwner.WeaponManager.CurrentWeapon.Mag.Count = 0;
    if (condition == "injured")
        scout.Memory.Health.HealthStatus = ETagStatus.BadlyInjured;
    if (condition == "self-care")
        scout.Decision.CurrentSelfDecision = ESelfActionType.Reload;
    if (condition == "short-mag")
        scout.BotOwner.WeaponManager.CurrentWeapon.Mag.Count = 14;
    if (condition == "no-support")
    {
        shooter.BotOwner.WeaponManager.CurrentWeapon.Mag.Count = 9;
        secondShooter.BotOwner.WeaponManager.CurrentWeapon.Mag.Count = 9;
    }
    p.Observe(shooter);
    var plan = p.states[shooter].Crossfire.Maneuver;
    if (condition == "no-support")
    {
        C(plan == null && p.diagnostics!.Lines.Any(x => x.Contains("maneuver-no-suppressor")),
            "no maneuver and a diagnostic when every possible shooter is below 33 percent");
    }
    else
    {
        var flanker = condition == "healthy" ? scout : shooter;
        C(plan?.Member(flanker.ProfileId)?.Role == "flank" && plan.Member(rear.ProfileId)?.Role == "rear",
            "highest eligible flanker and farthest horizontal rear: " + condition);
        p.Observe(flanker);
        if (condition == "healthy")
            C(p.states[flanker].Crossfire.Reference == (shooter.Position + secondShooter.Position) / 2,
                "flank angle uses both shooters' positions");
        p.Observe(secondShooter);
        C(p.states[secondShooter].Crossfire.Maneuver == plan, "nearby stationary shooters both join");
        p.Observe(rear);
        var runtime = p.states[rear].Crossfire;
        C(runtime.Maneuver == plan && runtime.ManeuverRole!.Role == "rear", "empty weapon can watch the rear");
        var action = new CrossfireAction(rear.BotOwner);
        action.Start();
        var away = (rear.Position - new Vector3(0, 0, 30)).normalized;
        rear.Player.LookDirection = away;
        action.OnSteeringTicked();
        C(runtime.ManeuverRole.Confirmed && rear.Steering.LastPoint == rear.Position + away * 10 + Vector3.up * 1.3f,
            "rear steers away from the contact during the live action without a second enemy");
    }
    p.EndAll();
}
foreach (var (observer, veto) in new[]
{
    ("at-radius", true), ("under-fire", true), ("outside-radius", false),
    ("other-enemy", false), ("dead", false), ("door", false), ("door-session", false), ("entry", false),
})
{
    var (p, b, anchor) = Setup();
    var cfg = p.config!;
    cfg.SquadManeuver.Chance = 100;
    cfg.Crossfire.Enabled = cfg.CoverMate.Enabled = cfg.SquadTrade.Enabled = false;
    anchor.GoalEnemy!.IsVisible = false;
    anchor.Info.Personality = Personality.Rat;
    anchor.GoalEnemy.KnownPlaces.LastKnownPosition = b.GoalEnemy!.KnownPlaces.LastKnownPosition = new(0, 0, 30);
    var scout = new BotComponent { Squad = b.Squad };
    scout.Position = b.Position + Vector3.up * (observer == "outside-radius" ? 35.01f : 35f);
    scout.GoalEnemy!.IsVisible = true;
    scout.GoalEnemy.KnownPlaces.LastKnownPosition = b.GoalEnemy.KnownPlaces.LastKnownPosition;
    scout.IsDead = observer == "dead";
    scout.BotOwner.Memory.IsUnderFire = observer == "under-fire";
    if (observer == "other-enemy")
        scout.GoalEnemy.EnemyProfileId = "someone-else";
    b.Squad.Members.Add(scout.ProfileId, scout);
    SAINEnableClass.Bots[scout.ProfileId] = scout;
    var state = new BotState(scout, cfg, p.session);
    p.states.Add(scout, state);
    state.Door.Policy.Active = observer == "door";
    state.Entry.Policy.Active = observer == "entry";
    if (observer == "door-session")
        state.Door.SessionPlan = "clear";

    p.Observe(b);
    C((p.states[b].Crossfire.Maneuver == null) == veto, "third member sight check: " + observer);
    C(p.diagnostics!.Lines.Any(line => line.Contains(Comet.Core.SquadManeuverBoard.SomeoneSees)) == veto,
        "S03 records the sight veto only for the relevant observer: " + observer);
    if (veto)
    {
        scout.GoalEnemy.IsVisible = false;
        Step((float)cfg.Crossfire.Interval + .01f);
        p.Observe(b);
        C(p.states[b].Crossfire.Maneuver != null, "sight veto did not consume the squad retry: " + observer);
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
foreach (var (horizontal, height, age, separation, failure) in new[]
{
    (78f, 20f, 10f, 35f, ""), (80f, 20f, 10f, 35f, ""), (15f, -20f, 10f, 35f, ""),
    (14.99f, 20f, 1f, 35f, "maneuver-proposer-horizontal-range"),
    (80.01f, 0f, 1f, 35f, "maneuver-proposer-horizontal-range"),
    (40f, 0f, 10.01f, 35f, "maneuver-proposer-contact-stale"),
    (40f, 0f, 0f, 35.01f, "maneuver-insufficient-eligible-members"),
})
{
    var (p, proposer, mate) = Setup();
    Step(60);
    p.config!.SquadManeuver.Chance = 100;
    proposer.Position = Vector3.zero;
    mate.Position = Vector3.up * separation;
    mate.Info.Personality = Personality.Rat;
    mate.GoalEnemy!.IsVisible = false;
    mate.GoalEnemy.KnownPlaces.LastKnownPosition = proposer.GoalEnemy!.KnownPlaces.LastKnownPosition =
        new Vector3(0, height, horizontal);
    proposer.GoalEnemy.TimeSinceLastKnownUpdated = age;
    mate.GoalEnemy.TimeSinceLastKnownUpdated = 0;
    p.Observe(proposer);
    C((p.states[proposer].Crossfire.Maneuver != null) == (failure.Length == 0),
        $"S03 proposer at {horizontal}m horizontal, {height}m vertical, {age}s old; teammate {separation}m above");
    if (failure.Length != 0)
        C(p.diagnostics!.Lines.Any(line => line.Contains(failure)), "S03 admission diagnostic: " + failure);
    p.EndAll();
}
foreach (var (trigger, duration) in new[] { ("arrival", 25), ("sighting", 25), ("native-combat", 25), ("arrival", 8) })
{
    var (p, flanker, support) = Setup();
    p.config!.SquadManeuver.Chance = 100;
    p.config.SquadManeuver.Duration = duration;
    p.config.Crossfire.HoldTimeout = 6;
    support.Info.Personality = Personality.Rat;
    support.GoalEnemy!.IsVisible = false;
    flanker.GoalEnemy!.KnownPlaces.LastKnownPosition = support.GoalEnemy.KnownPlaces.LastKnownPosition = new(0, 0, 30);
    Plan(p, flanker);
    var runtime = p.states[flanker].Crossfire;
    var plan = runtime.Maneuver!;
    double originalDeadline = plan.Until;
    p.Observe(support);
    var supportAction = new CrossfireAction(support.BotOwner);
    supportAction.Start();
    supportAction.OnSteeringTicked();
    var flankAction = Start(p, flanker);
    Step(15f - Time.time);
    support.GoalEnemy.IsVisible = true;
    supportAction.Update(new());
    C(plan.State == "active", "a sighting by the support does not start convergence");
    support.GoalEnemy.IsVisible = false;
    if (trigger == "arrival")
        flanker.Position = runtime.Goal;
    else if (trigger == "sighting")
    {
        flanker.GoalEnemy.IsVisible = true;
        flanker.GoalEnemy.CanShoot = false;
    }
    else
        flanker.Decision.CurrentCombatDecision = ECombatDecision.StandAndShoot;
    if (trigger == "native-combat")
        flankAction.Stop();
    else
        flankAction.Update(new());
    double deadline = Math.Min(originalDeadline, (double)Time.time + 5);
    C(plan.State == "converging" && plan.Until == deadline && !plan.MaySuppress,
        trigger + " begins five seconds of convergence and ends coordinated suppression");
    C(runtime.Policy.Active == (trigger == "arrival"),
        "the flanker holds on arrival and returns to native combat on contact");
    if (trigger != "arrival")
        C(runtime.Policy.Trace!.Outcome == "maneuver-enemy-spotted",
            "spotting the target en route does not claim a shot from the new angle");
    if (trigger == "arrival")
    {
        Step(1);
        flankAction.Update(new());
        flanker.GoalEnemy.IsVisible = true;
        flankAction.Update(new());
        C(!runtime.Policy.Active && plan.Until == deadline && runtime.Policy.Trace!.Outcome == "engaged-from-new-angle",
            "a sighting after arrival cannot restart the five-second window");
    }
    Time.time = (float)deadline;
    p.Sample(support);
    C(plan.Active && p.states[support].Crossfire.Policy.Active, "support holds through the deadline");
    Step(.02f);
    p.Sample(support);
    C(!plan.Active && !p.states[support].Crossfire.Policy.Active, "support releases the expired plan");
    p.EndAll();
}
{
    var (p, flanker, support) = Setup();
    p.config!.SquadManeuver.Chance = 100;
    p.config.Crossfire.Enabled = false;
    support.Info.Personality = Personality.Rat;
    support.GoalEnemy!.IsVisible = false;
    flanker.GoalEnemy!.KnownPlaces.LastKnownPosition = support.GoalEnemy.KnownPlaces.LastKnownPosition = new(0, 0, 30);
    Plan(p, flanker);
    var plan = p.states[flanker].Crossfire.Maneuver!;
    var action = Start(p, flanker);
    flanker.GoalEnemy.IsVisible = true;
    action.Update(new());
    flanker.GoalEnemy.IsVisible = false;
    p.Observe(support);
    C(plan.Converging && p.states[support].Crossfire.Maneuver == null,
        "a support that had not joined before the sighting cannot take up its old role");
    Step(5.1f);
    p.Observe(support);
    C(!plan.Active && plan.EndedAt == Time.time && p.states[support].Crossfire.Maneuver == null,
        "the next eligible observation retires a converged plan even without ongoing participants");
    p.EndAll();
}
foreach (string expectedRole in new[] { "support", "flank", "rear" })
{
    var (p, proposer, mate) = Setup();
    p.config!.SquadManeuver.Chance = 100;
    p.config.Crossfire.Enabled = false;
    mate.GoalEnemy!.IsVisible = false;
    proposer.GoalEnemy!.KnownPlaces.LastKnownPosition = mate.GoalEnemy.KnownPlaces.LastKnownPosition = new(0, 0, 30);
    proposer.Info.Personality = expectedRole == "flank" ? Personality.Rat : Personality.Chad;
    mate.Info.Personality = expectedRole == "flank" ? Personality.Chad : Personality.Rat;
    if (expectedRole == "rear")
    {
        mate.Position = new(0, 0, -10);
        var shooter = new BotComponent { Squad = proposer.Squad };
        shooter.GoalEnemy!.KnownPlaces.LastKnownPosition = proposer.GoalEnemy.KnownPlaces.LastKnownPosition;
        proposer.Squad.Members.Add(shooter.ProfileId, shooter);
        SAINEnableClass.Bots[shooter.ProfileId] = shooter;
    }

    mate.BotOwner.Memory.IsUnderFire = true;
    p.Observe(mate);
    var runtime = p.states[mate].Crossfire;
    C(runtime.Maneuver == null, "incoming fire blocks the PMC's own S03 proposal");
    C(p.diagnostics!.Lines.Any(line => line.Contains("\"feature\":\"S03\"") && line.Contains("maneuver-under-fire")),
        "S03 records why the acting PMC cannot start");
    p.Observe(proposer);
    var plan = p.states[proposer].Crossfire.Maneuver;
    C(plan?.Member(mate.ProfileId)?.Role == expectedRole,
        "the squad can assign a member under fire to " + expectedRole);
    Step(.6f);
    p.Observe(mate);
    C(runtime.Maneuver == null && !runtime.Policy.Active,
        "assignment alone does not start the native action while under fire");
    mate.BotOwner.Memory.IsUnderFire = false;
    Step(.6f);
    p.Observe(mate);
    C(runtime.Maneuver == plan && runtime.ManeuverRole?.Role == expectedRole,
        "the member can take its assigned role when incoming fire stops");
    mate.BotOwner.Memory.IsUnderFire = true;
    p.Sample(mate);
    C(runtime.Policy.Active == (expectedRole != "flank"),
        "incoming fire lets established stationary roles hold and releases a moving flanker");
    p.EndAll();
}
foreach (var (assignedRole, personalRange, personalAge, initiallyMissingPoint) in new[]
{
    ("support", 8f, 1f, false), ("support", 90f, 10.01f, false), ("support", 30f, 40f, true),
    ("flank", 90f, 40f, false), ("rear", 8f, 40f, false),
})
{
    var (p, proposer, mate) = Setup();
    Step(60);
    p.config!.SquadManeuver.Chance = 100;
    p.config.Crossfire.Enabled = false;
    proposer.Position = Vector3.zero;
    mate.Position = new(0, 0, -8);
    proposer.GoalEnemy!.KnownPlaces.LastKnownPosition = new(0, 0, 40);
    proposer.GoalEnemy.TimeSinceLastKnownUpdated = 1;
    mate.GoalEnemy!.IsVisible = false;
    var ownPoint = mate.Position + new Vector3(0, 0, personalRange);
    mate.GoalEnemy.KnownPlaces.LastKnownPosition = initiallyMissingPoint ? null : ownPoint;
    mate.GoalEnemy.TimeSinceLastKnownUpdated = personalAge;
    proposer.Info.Personality = assignedRole == "flank" ? Personality.Rat : Personality.Chad;
    mate.Info.Personality = assignedRole == "flank" ? Personality.Chad : Personality.Rat;
    if (assignedRole == "rear")
    {
        var shooter = new BotComponent { Squad = proposer.Squad, Position = new(4, 0, 1) };
        shooter.GoalEnemy!.KnownPlaces.LastKnownPosition = proposer.GoalEnemy.KnownPlaces.LastKnownPosition;
        proposer.Squad.Members.Add(shooter.ProfileId, shooter);
        SAINEnableClass.Bots[shooter.ProfileId] = shooter;
    }
    p.Observe(proposer);
    var plan = p.states[proposer].Crossfire.Maneuver;
    C(plan?.Member(mate.ProfileId)?.Role == assignedRole, "personal range/age do not filter " + assignedRole);
    p.Observe(mate);
    if (initiallyMissingPoint)
    {
        C(p.states[mate].Crossfire.Maneuver == null, "no action without a local remembered position");
        mate.GoalEnemy.KnownPlaces.LastKnownPosition = ownPoint;
        Step(.6f);
        p.Observe(mate);
    }
    var role = p.states[mate].Crossfire;
    C(role.Maneuver == plan && role.Known == ownPoint, "member joins using its own remembered point");
    Step();
    p.Sample(mate);
    C(role.Policy.Active && role.Maneuver == plan, "assigned role survives the next update: " + assignedRole);
    p.EndAll();
}
// The same indoor geometry offers both a usable side entrance and a flanking angle.
foreach (string sighting in new[] { "visible at start", "no sight", "support reacquired during S03" })
{
    var (p, b, support) = Setup();
    p.config!.SquadManeuver.Chance = 100;
    b.Player.Environment = EnvironmentType.Indoor;
    support.Info.Personality = Personality.Rat;
    var known = new Vector3(0, 0, 22);
    b.GoalEnemy!.KnownPlaces.LastKnownPosition = support.GoalEnemy!.KnownPlaces.LastKnownPosition = known;
    support.GoalEnemy.EnemyPosition = known;
    support.GoalEnemy.IsVisible = sighting == "visible at start";
    var front = new Vector3(0, 0, 18);
    var side = new Vector3(4, 0, 22);
    int entrancePaths = 0;
    NavMesh.Route = (from, to) =>
    {
        entrancePaths++;
        return [from, from == support.Position ? front : side, to];
    };
    if (sighting == "support reacquired during S03")
    {
        p.Observe(support);
        C(p.states[support].Crossfire.ManeuverRole?.Role == "support", "support formed S03 before regaining sight");
        support.GoalEnemy.IsVisible = true;
    }
    Plan(p, b);
    NavMesh.Route = null;
    var r = p.states[b].Crossfire;
    var trace = r.Policy.Trace!;
    if (sighting == "visible at start")
    {
        C(r.Maneuver == null && trace.Entrance != null && r.Entrance.Center == side && entrancePaths > 0,
            "ordinary crossfire can select the side entrance while support sees the target");
    }
    else
    {
        C(r.ManeuverRole?.Role == "flank" && trace.Entrance == null && entrancePaths == 0,
            $"S03 flanker never probes the secondary entrance ({sighting})");
        C(trace.SearchPass == "zzap-angle-search" && trace.Candidates > 0
            && r.Indoor && r.Known == known && r.Reference == support.Position,
            "the flanker selects an indoor angle using its remembered position and the base of fire");
    }
    p.EndAll();
}
(Plugin, BotComponent, CrossfireAction) BeginEntranceWatch(bool shortApproach = false)
{
    var (p, b, support) = Setup();
    b.Player.Environment = EnvironmentType.Indoor;
    b.GoalEnemy!.KnownPlaces.LastKnownPosition = support.GoalEnemy!.EnemyPosition = new(0, 0, 22);
    var side = new Vector3(4, 0, 22);
    NavMesh.Route = (from, to) => [from, from == support.Position ? new(0, 0, 18) : side, to];
    Plan(p, b);
    NavMesh.Route = null;
    var r = p.states[b].Crossfire;
    var entrance = r.Policy.Trace!.Entrance!;
    if (shortApproach)
        b.Position = r.Goal - new Vector3(3, 0, 0);
    var action = Start(p, b);
    b.Position = r.Goal;
    Step(18f - Time.time);
    action.Update(new());
    C(entrance.State == "watching" && r.Entrance.Policy.WatchAt == 18, "the watch starts at the side entrance");
    return (p, b, action);
}
foreach (string scenario in new[] { "crossed", "order refused", "foreign path", "renewal refused" })
{
    var (p, b, action) = BeginEntranceWatch(shortApproach: true);
    var r = p.states[b].Crossfire;
    var trace = r.Policy.Trace!;
    var entrance = trace.Entrance!;
    C(b.Mover.SprintOrders == 0 && !trace.SprintRequested, "the short approach to the watch position uses walking");
    b.Position = r.Entrance.Center - r.Entrance.Normal * 2.5f;
    b.Info.Personality = Personality.Chad;
    Step(15.25f);
    b.GoalEnemy!.TimeSinceLastKnownUpdated = 0;
    var foreignPath = new BotPathDataManual { Destination = new(100, 0, 100) };
    if (scenario == "foreign path")
    {
        b.Mover.ActivePath = foreignPath;
        b.Mover.Observe = false;
    }
    b.Mover.Accept = scenario != "order refused";
    int ordersBefore = b.Mover.Walks;
    int traceOrdersBefore = trace.MoveOrders;
    action.Update(new());
    C(b.Mover.SprintOrders == 1 && b.Mover.Walks == ordersBefore + 1 && trace.MoveOrders == traceOrdersBefore + 1,
        "the first entry order is exactly one sprint with no preceding walk");
    C(trace.SprintRequested && !trace.SprintObserved && b.Mover.SprintRequiresCompletePath
        && b.Mover.SprintUrgency == SAIN.Models.Enums.ESprintUrgency.High && b.Mover.SprintReach == .56f,
        "the short crossing requests the source sprint parameters; actual speed remains observational");
    if (scenario is "order refused" or "foreign path")
    {
        C(!r.Policy.Active && !entrance.Crossed, "an unexecuted order cannot confirm a crossing");
        if (scenario == "foreign path")
            C(ReferenceEquals(b.Mover.ActivePath, foreignPath) && foreignPath.Stops == 0, "entry cleanup preserves unrelated movement");
        else
            C(!trace.MoveAccepted, "the trace records a refused sprint");
    }
    else
    {
        C(r.Entrance.Entering && Math.Abs(Vector3.Distance(b.Position, r.Goal) - 5f) < .001f
            && Vector3.Distance(b.Mover.ActivePath!.Destination, r.Goal) < .001f,
            "SAIN received the point on the other side of the entrance");
        Step(.75f);
        action.Update(new());
        C(b.Mover.Walks == ordersBefore + 1, "entry does not renew through exactly 0.75 seconds");
        b.Mover.Accept = scenario != "renewal refused";
        Step(.02f);
        action.Update(new());
        C(b.Mover.SprintOrders == 2 && b.Mover.Walks == ordersBefore + 2 && trace.MoveOrders == traceOrdersBefore + 2,
            "the renewal requests a second sprint at short range");
        if (scenario == "renewal refused")
            C(!r.Policy.Active && !trace.MoveAccepted && !entrance.Crossed, "a refused renewal ends the crossing");
        else
        {
            b.Position = r.Goal;
            Step(.02f);
            action.Update(new());
            C(!r.Policy.Active && entrance.Crossed && trace.Reason == "secondary-entrance-crossed-and-reached"
                && b.Mover.SprintOrders == 2, "arrival records the crossing and stops issuing orders");
        }
    }
    p.EndAll();
}
foreach (string outcome in new[] { "upstairs", "radius boundary", "age boundary", "missing position", "under fire" })
{
    var (p, b, action) = BeginEntranceWatch();
    var r = p.states[b].Crossfire;
    var entrance = r.Policy.Trace!.Entrance!;
    var side = r.Entrance.Center;
    Step(1);
    b.GoalEnemy.TimeSinceLastKnownUpdated = 16;
    b.GoalEnemy.KnownPlaces.LastKnownPosition = outcome == "missing position" ? null : side + new Vector3(20, 0, 0);
    b.BotOwner.Memory.IsUnderFire = outcome == "under fire";
    action.Update(new());
    if (outcome == "under fire")
        C(!r.Policy.Active && r.Policy.Trace.Reason == "crossfire-under-fire", "incoming fire still interrupts the watch");
    else
    {
        C(r.Policy.Active && !entrance.Rolled, "losing room evidence one second after arrival does not end the watch");
        Step(14);
        b.GoalEnemy.TimeSinceLastKnownUpdated = 15;
        action.Update(new());
        C(r.Policy.Active && !entrance.Rolled && entrance.WatchRemaining == 0, "hold through exactly fifteen seconds");
        Step(.02f);
        b.GoalEnemy.TimeSinceLastKnownUpdated = outcome == "age boundary" ? 15 : 14.99f;
        b.GoalEnemy.KnownPlaces.LastKnownPosition = outcome == "missing position" ? null
            : side + new Vector3(outcome == "radius boundary" ? 12 : 11, 6, 0);
        action.Update(new());
        if (outcome == "upstairs")
            C(r.Policy.Active && r.Entrance.Entering && entrance.Rolled, "fresh contact 11 m away horizontally and 6 m above qualifies");
        else
            C(!r.Policy.Active && !entrance.Rolled && entrance.EntryReason == "enemy no longer believed inside",
                "room evidence rejected at expiry: " + outcome);
    }
    p.EndAll();
}
foreach (string outcome in new[] { "enters", "declines twice", "lost contact", "rat", "age boundary", "injured", "entry disabled", "chambered round", "empty chamber" })
{
    var (p, b, action) = BeginEntranceWatch();
    var r = p.states[b].Crossfire;
    var trace = r.Policy.Trace!.Entrance!;
    b.Info.Personality = outcome == "rat" ? Personality.Rat : Personality.Normal;
    if (outcome == "injured")
        b.Memory.Health.HealthStatus = ETagStatus.BadlyInjured;
    bool allowEntry = outcome != "entry disabled";
    p.config!.SecondaryEntrance.AllowEntry = allowEntry;
    var random = new EntryRollSequence(.8, outcome == "declines twice" ? .85 : .1);
    p.jumpRandom = random;
    Step(15.25f);
    b.GoalEnemy!.TimeSinceLastKnownUpdated = outcome switch
    {
        "rat" or "injured" => 9f,
        "age boundary" => 8f,
        _ => 7.99f,
    };
    action.Update(new());
    bool extends = outcome is not ("rat" or "age boundary" or "injured");
    C(trace.Extended == extends && r.Policy.Active == extends,
        "an extension requires contact strictly less than eight seconds old: " + outcome);
    if (!extends)
        C(trace.EntryReason == "no recent contact to extend watch" && trace.WatchRemaining == 0,
            "the final trace explains the departure: " + outcome);
    int firstDraws = allowEntry ? 1 : 0;
    C(random.Draws == firstDraws && trace.Rolled == allowEntry, "only entry-enabled decisions draw");
    if (extends)
    {
        Step(15);
        b.GoalEnemy.TimeSinceLastKnownUpdated = 9;
        action.Update(new());
        C(r.Policy.Active && random.Draws == firstDraws, "hold through the exact extension deadline");
        Step(.02f);
        b.GoalEnemy.TimeSinceLastKnownUpdated = outcome == "lost contact" ? 15 : 9;
        if (outcome is "chambered round" or "empty chamber")
        {
            var gun = b.BotOwner.WeaponManager.CurrentWeapon;
            gun.Mag.Count = 14;
            gun.ChamberAmmoCount = outcome == "chambered round" ? 1 : 0;
        }
        action.Update(new());
        bool secondDraw = allowEntry && outcome != "lost contact";
        C(random.Draws == firstDraws + (secondDraw ? 1 : 0), "a fresh draw at the second decision: " + outcome);
        C(trace.PreviousEntryRoll == (secondDraw ? 80d : null), "the trace retains the first draw only after a second draw");
        if (outcome is "enters" or "chambered round")
            C(r.Entrance.Entering && trace.EntryRoll == 10, "80 percent then 10 percent changes a Normal PMC's decision");
        else
            C(!r.Policy.Active, "no third watch: " + outcome);
    }
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

sealed class EntryRollSequence(params double[] rolls) : Random
{
    public int Draws { get; private set; }
    public override double NextDouble() => rolls[Draws++];
}
