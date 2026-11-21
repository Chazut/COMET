using Comet.Client;
using EFT;
using EFT.Ballistics;
using EFT.HealthSystem;
using EFT.InventoryLogic;
using SAIN.Components;
using SAIN.Models.Enums;
using SAIN.Preset.Shared.Enums;
using SAIN.SAINComponent.Classes.EnemyClasses;
using SAIN.SAINComponent.Classes.Mover;
using UnityEngine;

int n = 0;
var events = new List<string>();
void C(bool ok, string why)
{
    n++;
    if (!ok)
        throw new Exception("H04 adapter: " + why);
}
(Plugin p, BotComponent b, PostCombatLayer layer, PostCombatAction action) Setup(bool fight = true)
{
    Time.time = Time.realtimeSinceStartup = fight ? 8.5f : 10f;
    Time.frameCount++;
    var p = new Plugin();
    var b = new BotComponent();
    foreach (var hp in b.Player.HealthController.Parts.Values)
        hp.Current = 100;
    b.Player.HandsController = new Player.FirearmController();
    b.SAINLayersActive = false;
    b.GoalEnemy = new(b) { IsVisible = fight };
    b.GoalEnemy.KnownPlaces.LastSeenPlace!.Position = new(20, 0, 0);
    b.GoalEnemy.KnownPlaces.LastSeenPlace.TimeSincePositionUpdated = 0;
    b.ActiveLayer = fight ? ESAINLayer.Combat : ESAINLayer.None;
    p.Observe(b);
    if (fight)
    {
        Step(1.5f);
        p.Observe(b);
        b.GoalEnemy.IsVisible = false;
        b.GoalEnemy.EnemyKnown = false;
    }
    return (p, b, new(b.BotOwner, 21), new(b.BotOwner));
}
void Step(float dt = .3f)
{
    Time.time += dt;
    Time.realtimeSinceStartup = Time.time;
    Time.frameCount++;
}
void Save(Plugin p)
{
    events.AddRange(p.diagnostics!.Lines.Where(x => x.Contains("\"feature\":\"H04\"")));
}
foreach (var (delay, duration, steeringFirst) in new[] { (0f, 14f, false), (8f, 14f, false), (8f, 14f, true), (4f, 9f, false) })
{
    var (p, b, layer, action) = Setup();
    var r = p.states[b].PostCombat;
    Physics.BlocksSightTo = _ => true;
    p.config!.PostCombat.Duration = duration;
    b.ActiveLayer = ESAINLayer.Squad;
    b.Squad.IAmLeader = false;
    b.Squad.LeaderComponent = new() { Position = new(40, 0, 0) };
    SAIN.SAINEnableClass.Bots[b.ProfileId] = b;
    Step(delay);
    p.Observe(b);
    C(layer.IsActive(), "the quiet follower can use the rest of the ordinary window");
    action.Start();
    var start = Newtonsoft.Json.Linq.JObject.Parse(p.diagnostics!.Lines.Last())["postCombat"]!;
    C((double?)start["remaining"] == duration - delay && (double?)start["elapsed"] == 0
        && (double?)start["secondsSinceCombat"] == delay, "a late start reports only the remaining combat-relative budget");
    Step(duration - delay - .0625f);
    action.Update(new());
    C(r.Layer && r.RouteStage == "regroup", "the route remains eligible immediately before the deadline");
    var path = (BotPathDataManual)r.Path!;
    int orders = b.Mover.Routes;
    Step(.0625f);
    if (steeringFirst) action.OnSteeringTicked();
    else action.Update(new());
    C(!r.Layer && !r.Policy.Active && path.Stops == 1 && b.Mover.Routes == orders
        && !Api.Decisions.Recovery.ContainsKey(b.ProfileId), "release the quiet route on the deadline, even between policy polls");
    var end = Newtonsoft.Json.Linq.JObject.Parse(p.diagnostics.Lines.Last());
    C((string?)end["reason"] == "post-combat-time-budget-ended"
        && (double?)end["postCombat"]?["elapsed"] == duration - delay
        && (double?)end["postCombat"]?["secondsSinceCombat"] == duration
        && (double?)end["postCombat"]?["remaining"] == 0, "the end event separates episode duration from combat age");
    action.Stop();
    C(!layer.IsActive() && path.Stops == 1, "a later action callback leaves the released route alone");
    Physics.BlocksSightTo = null;
}
foreach (bool treatment in new[] { false, true })
{
    var (p, b, layer, action) = Setup();
    Physics.BlocksSightTo = _ => true;
    b.Squad.IAmLeader = false;
    b.Squad.LeaderComponent = new() { Position = new(40, 0, 0) };
    SAIN.SAINEnableClass.Bots[b.ProfileId] = b;
    var hearing = new SAIN.Components.BotControllerSpace.Classes.BotHearingClass();
    SourceSoundWatch.Attach(hearing);
    try
    {
        Step(8);
        C(layer.IsActive(), "a quiet delayed follower starts before the deadline");
        action.Start();
        Step(5.9375f);
        action.Update(new());
        Step(.0625f);
        if (treatment)
            b.BotOwner.Medecine.FirstAid.Using = true;
        else
            hearing.Emit(SAINSoundType.FootStep, new(5, 0, 0), new() { ProfileId = "new-contact" });
        C(layer.IsActive(), "a new sound or treatment at the ordinary deadline extends the current episode");
        action.Update(new());
        var r = p.states[b].PostCombat;
        C(r.Policy.Trace!.State == (treatment ? "medical" : "watch") && r.Policy.Trace.Episode == 1
            && r.Policy.Trace.SecondsSinceCombat == 14 && r.Policy.Trace.Remaining > 0,
            "the deadline samples the live extension after the previous poll");
        b.BotOwner.Medecine.FirstAid.Using = false;
        Step(10);
        action.Update(new());
        C(!r.Layer && !r.Policy.Trace.MedicalBusy && r.Policy.Trace.Reason == Comet.Core.PostCombat.OrdinaryWindowExpired,
            "clearing the only extension restores the original combat-relative cutoff");
    }
    finally
    {
        SourceSoundWatch.Detach(hearing);
        Physics.BlocksSightTo = null;
    }
}
foreach (float initialPose in new[] { 1f, .6f })
foreach (bool reloading in new[] { false, true })
{
    var (p, b, layer, action) = Setup();
    Physics.BlocksSightTo = _ => true;
    b.BotOwner.WeaponManager.Reload.Reloading = reloading;
    b.Mover.SetTargetPose(initialPose);
    C(layer.IsActive() && b.Mover.Stops == 0 && b.Mover.Pose == initialPose,
        "querying the layer leaves movement and pose alone");
    action.Start();
    var r = p.states[b].PostCombat;
    C(r.Policy.Trace!.State == (reloading ? "reload" : "watch")
        && b.Mover.Stops == 1 && b.Mover.Pose == .75f && !b.Mover.Prone.Value,
        "a sheltered watcher stops and lowers to 0.75 regardless of its previous pose");
    b.Mover.SetTargetPose(1);
    Step(.125f);
    action.Update(new());
    C(b.Mover.Stops == 2 && b.Mover.Pose == .75f, "the watch maintains its pose between movement orders");
    b.BotOwner.Medecine.FirstAid.Using = true;
    b.Mover.SetTargetPose(.6f);
    Step(.0625f);
    action.Update(new());
    C(b.Mover.Stops == 2 && b.Mover.Pose == .6f, "a new native treatment keeps its pose between decision ticks");
    b.GoalEnemy!.IsVisible = true;
    b.Mover.SetTargetPose(1);
    action.Update(new());
    C(!r.Policy.Active && b.Mover.Stops == 2 && b.Mover.Pose == 1, "combat takes immediate ownership of movement");
}
foreach (bool sheltered in new[] { false, true })
foreach (bool reloading in new[] { false, true })
{
    var (p, b, layer, action) = Setup();
    Physics.BlocksSightTo = _ => sheltered;
    b.BotOwner.WeaponManager.Reload.Reloading = reloading;
    // A prone request from outside H04 must survive a sheltered watch and its release.
    b.Mover.Prone.SetProne(sheltered);
    C(layer.IsActive(), "stationary recovery starts with either form of protection");
    action.Start();
    Step();
    action.Update(new());
    var r = p.states[b].PostCombat;
    C(r.Policy.Trace!.State == (reloading ? "reload" : "watch")
        && b.Mover.Prone.Value, "keep a native or H04 prone request while watching");
    var snapshot = Newtonsoft.Json.Linq.JObject.Parse(p.diagnostics!.Lines.Last())["postCombat"]!;
    C((bool?)snapshot["reloading"] == reloading
        && (string?)snapshot["execution"] == (sheltered ? "stopped to watch at pose 0.75" : "watching prone in the open"),
        "ordinary H04 logs distinguish the watch orders");
    action.Stop();
    C(b.Mover.Prone.Value == sheltered, "release only H04's own prone request");
}
Physics.BlocksSightTo = null;
foreach (bool alreadyReloading in new[] { true, false })
{
    var (p, b, layer, action) = Setup();
    Physics.BlocksSightTo = _ => true;
    var reload = b.BotOwner.WeaponManager.Reload;
    var hands = (Player.FirearmController)b.Player.HandsController;
    hands.Item.Magazine.Count = 5;
    reload.Reloading = alreadyReloading;
    b.Squad.IAmLeader = false;
    b.Squad.LeaderComponent = new() { Position = new(25, 0, 0) };
    SAIN.SAINEnableClass.Bots[b.ProfileId] = b;
    b.Mover.SetTargetPose(1);
    C(layer.IsActive() && b.Mover.Stops == 0, "observing a stationary reload issues no movement order");
    action.Start();
    C(b.Mover.Stops == 1 && b.Mover.Pose == .75f && b.Mover.Routes == 0 && !b.Mover.Prone.Value,
        "a sheltered bot watches low on the update that starts or observes the native reload");
    var snapshot = Newtonsoft.Json.Linq.JObject.Parse(p.diagnostics!.Lines.Last())["postCombat"]!;
    C((string?)snapshot["state"] == "reload" && (bool?)snapshot["reloading"] == true
        && (string?)snapshot["execution"] == "stopped to watch at pose 0.75", "log the native reload and the watch together");
    // Consume the initial forced sample, then test action updates inside its interval.
    Step(.0625f);
    action.Update(new());
    b.Mover.SetTargetPose(1);
    int stops = b.Mover.Stops;
    Step(.0625f);
    action.Update(new());
    C(b.Mover.Stops == stops + 1 && b.Mover.Pose == .75f, "maintain the reload watch between samples");
    b.Squad.LeaderComponent.Position = new(40, 0, 0);
    Step(.03125f);
    action.Update(new());
    C(b.Mover.Routes == 1 && b.Mover.LastWalkPoint.x == 40 && b.Mover.Pose == 1
        && b.Mover.Stops == stops + 1, "a distant leader immediately replaces the reload watch with movement");
    b.Squad.LeaderComponent.Position = new(25, 0, 0);
    Step(.03125f);
    action.Update(new());
    C(b.Mover.Stops == stops + 2 && b.Mover.Pose == .75f && p.states[b].PostCombat.Path == null,
        "resume the reload watch when the leader comes back within range");
    action.Stop();
    C(reload.Reloading && reload.Requests == (alreadyReloading ? 0 : 1)
        && ReferenceEquals(b.Player.HandsController, hands) && hands.Item.Magazine.Count == 5,
        "the watch leaves the native reload, hands and ammunition alone");
    Physics.BlocksSightTo = null;
}
foreach (float fraction in new[] { 0f, 1f })
{
    var (p, b, layer, action) = Setup();
    p.config!.PostCombat.AdvanceChances["Normal"] = 0;
    C(layer.IsActive(), "recovery starts before an idle watch");
    action.Start();
    UnityEngine.Random.RangeFraction = fraction;
    UnityEngine.Random.value = .5f;
    action.OnSteeringTicked();
    Vector3 side = new(20, 1.3f, fraction == 0 ? 10 : -10);
    C(Vector3.Distance(b.Steering.LastPoint, side) < .001f,
        "watch uses a side offset proportional to horizontal threat distance");
    UnityEngine.Random.value = .49f;
    Step(.3f);
    action.Update(new());
    C(Vector3.Distance(b.Steering.LastPoint, side) < .001f, "action update keeps the steering tick's glance");
    Time.time = 10 + (fraction == 0 ? 1.5f : 3f);
    action.OnSteeringTicked();
    C(Vector3.Distance(b.Steering.LastPoint, side) < .001f, "watch holds the offset through its exact deadline");
    Step(.01f);
    action.OnSteeringTicked();
    C(Vector3.Distance(b.Steering.LastPoint, new(20, 1.3f, 0)) < .001f, "next glance can recenter on the last contact");
}
UnityEngine.Random.RangeFraction = 0;
UnityEngine.Random.value = .99f;
foreach (bool far in new[] { false, true })
foreach (bool advance in new[] { false, true })
{
    var (p, b, layer, action) = Setup();
    p.config!.PostCombat.AdvanceChances["Normal"] = advance ? 100 : 0;
    Physics.BlocksSightTo = point => point.z >= (far ? 15.9f : 2.9f);
    var hearing = new SAIN.Components.BotControllerSpace.Classes.BotHearingClass();
    SourceSoundWatch.Attach(hearing);
    try
    {
        C(layer.IsActive(), "recovery starts in the open");
        action.Start();
        var r = p.states[b].PostCombat;
        C(r.RouteStage == "cover" && r.Goal.z >= (far ? 15.9f : 2.9f), "native cover order was accepted");
        action.OnSteeringTicked();
        C(b.Steering.Moving == 0 && r.Policy.Trace!.LastLook == "watch-side",
            "a route alone cannot turn a stationary bot toward travel");
        b.Mover.Moving = true;
        action.OnSteeringTicked();
        C(r.Policy.Trace!.LastLook == (far ? "travel" : advance ? "advance" : "watch-side"),
            $"actual movement to far={far} cover with advance={advance}");
        C(far ? b.Steering.Moving == 1 : Vector3.Distance(b.Steering.LastPoint, new(20, 1.3f, advance ? 0 : 10)) < .001f,
            "native steering receives the travel direction or the correct remembered point");
        var stranger = new SAIN.Components.PlayerComponentSpace.PlayerComponent { ProfileId = "audible" };
        hearing.Emit(SAINSoundType.FootStep, new(5, 12, 0), stranger);
        Step(.6f);
        action.Update(new());
        action.OnSteeringTicked();
        var levelAim = new Vector3(5, 1.3f + 5 * MathF.Tan(10 * Mathf.Deg2Rad), 0);
        C(r.Policy.Trace.LastLook == "sound" && Vector3.Distance(b.Steering.LastPoint, levelAim) < .001f,
            "a heard step takes priority over travel to cover and uses the pitch limit");
        Step(.6f);
        action.Update(new());
        var snapshot = Newtonsoft.Json.Linq.JObject.Parse(p.diagnostics!.Lines.Last());
        C((string?)snapshot["postCombat"]?["lastLook"] == "sound", "ordinary H04 events report the last issued look order");
        b.Shoot.Result = true;
        int pointOrders = b.Steering.Points;
        action.OnSteeringTicked();
        C(b.Steering.Points == pointOrders && r.Policy.Trace.LastLook == "engage", "native firing owns aim despite sound");
    }
    finally
    {
        SourceSoundWatch.Detach(hearing);
        Physics.BlocksSightTo = null;
    }
}
{
    var (p, b, layer, action) = Setup();
    C(layer.IsActive(), "start the recovery layer for a missing remembered threat");
    action.Start();
    p.states[b].PostCombat.Threat = null;
    b.Mover.Moving = true;
    action.OnSteeringTicked();
    C(b.Steering.Moving == 1, "travel without a remembered threat even with no COMET path");
    b.Mover.Moving = false;
    action.OnSteeringTicked();
    C(b.Steering.Random == 1, "look around when stationary without a remembered threat");
}
foreach (var scenario in new[] { "incoming-fire", "none", "last-fight", "current-enemy" })
{
    var (p, b, layer, action) = Setup(false);
    Physics.BlocksSightTo = _ => true;
    p.config!.PostCombat.AdvanceChances["Normal"] = 100;
    b.Memory.UnderFireFromPosition = scenario == "none" ? Vector3.zero : new(30, 0, 0);
    b.BotOwner.Memory.IsUnderFire = true;
    var places = b.GoalEnemy!.KnownPlaces;
    if (scenario is "incoming-fire" or "none") places.LastSeenPlace = null;
    b.ActiveLayer = ESAINLayer.Combat;
    p.Observe(b);
    Step(1.5f);
    p.Observe(b);
    b.ActiveLayer = ESAINLayer.Squad;
    C(!layer.IsActive(), "incoming fire still preempts recovery");
    b.BotOwner.Memory.IsUnderFire = false;
    if (scenario == "current-enemy")
    {
        places.LastSeenPlace = null;
        places.LastHeardPlace = new() { Position = new(12, 0, 0), TimeSincePositionUpdated = .5f };
    }
    else b.GoalEnemy = null;
    C(layer.IsActive(), "recovery can begin when fire ceases");
    action.Start();
    var r = p.states[b].PostCombat;
    float? x = scenario switch { "incoming-fire" => 30f, "current-enemy" => 12f, "last-fight" => 20f, _ => null };
    C(r.Threat?.x == x && r.Policy.Trace!.AdvanceSelected == x.HasValue,
        "use the current enemy memory, then the last fight, then the incoming-fire origin: " + scenario);
    var snapshot = Newtonsoft.Json.Linq.JObject.Parse(p.diagnostics!.Lines.Last())["postCombat"]!;
    C((string?)snapshot["threatSource"] == scenario && (float?)snapshot["x"] == x,
        "the H04 event identifies the selected source and point");
    C(scenario is "current-enemy" or "last-fight"
        ? (string?)snapshot["contact"] == "enemy" && (double?)snapshot["contactAge"] == (scenario == "current-enemy" ? .5 : 0)
        : (string?)snapshot["contact"] == "" && (double?)snapshot["contactAge"] == null,
        "a shot origin alone carries no assumed shooter or observation age");
    b.Memory.UnderFireFromPosition = new(35, 0, 0);
    if (places.LastHeardPlace != null) places.LastHeardPlace.Position = new(39, 0, 0);
    action.OnSteeringTicked();
    C(x.HasValue ? b.Steering.Points > 0 && b.Steering.LastPoint.x == x.Value : b.Steering.Random == 1,
        "steering uses the frozen position or looks around if none exists");
    Step(7);
    action.Update(new());
    C(x.HasValue ? r.RouteStage == "check" && b.Mover.LastWalkPoint.x == x.Value : b.Mover.Routes == 0,
        "the optional inspection uses the same snapshot");
    action.Stop();
    if (scenario == "incoming-fire")
    {
        b.ActiveLayer = ESAINLayer.Combat;
        p.Observe(b);
        Step(1.5f);
        p.Observe(b);
        b.ActiveLayer = ESAINLayer.Squad;
        C(layer.IsActive() && r.Threat?.x == 35, "a new fight reads the current origin instead of reusing the last fallback");
        action.Stop();
        b.Memory.UnderFireFromPosition = Vector3.zero;
        b.ActiveLayer = ESAINLayer.Combat;
        p.Observe(b);
        Step(1.5f);
        p.Observe(b);
        b.ActiveLayer = ESAINLayer.Squad;
        C(layer.IsActive() && r.Threat == null, "clearing the native origin leaves no fictitious enemy memory");
        action.Stop();
    }
    Physics.BlocksSightTo = null;
}
{
    var (p, b, layer, action) = Setup(false);
    b.GoalEnemy = null;
    b.Memory.UnderFireFromPosition = new(30, 0, 0);
    Physics.BlocksSightTo = point => point.z >= 15.9f;
    b.ActiveLayer = ESAINLayer.Combat;
    p.Observe(b);
    Step(1.5f);
    p.Observe(b);
    b.ActiveLayer = ESAINLayer.Squad;
    C(layer.IsActive(), "incoming-fire origin is available for the initial cover assessment");
    action.Start();
    var r = p.states[b].PostCombat;
    C(r.RouteStage == "cover" && r.Goal.z >= 15.9f && b.Mover.Routes == 1,
        "open ground with only the remembered shot sends the bot to a point that breaks sight");
    action.Stop();
    Physics.BlocksSightTo = null;
}
foreach (var kind in new[] { "bush", "moving", "bad", "spotted", "solid" })
{
    var (p, b, l, a) = Setup();
    b.Cover.CoverInUse = new() { BotInThisCover = kind != "moving", Spotted = kind == "spotted" };
    b.Cover.CoverInUse.CoverData.IsBad = kind == "bad";
    b.Cover.CoverInUse.Collider.gameObject.layer = kind == "bush" ? 7 : 4;
    C(l.IsActive(), "post-combat recovery still allowed: " + kind);
    C(
        p.states[b].PostCombat.Protected == (kind == "solid"),
        "no stale/camouflage protection at episode start: " + kind
    );
}
{
    var (p, b, l, a) = Setup(false);
    C(!l.IsActive() && p.states.Count == 0, "no fight no state or recovery scan");
}
foreach (var layer in new[] { ESAINLayer.Combat, ESAINLayer.AvoidThreat, ESAINLayer.Flashed })
{
    var (p, b, l, _) = Setup(false);
    b.GoalEnemy = null;
    b.ActiveLayer = layer;
    p.Observe(b);
    Step(1.499f);
    p.Observe(b);
    C(!l.IsActive(), "just below the minimum combat-layer duration: " + layer);
    Time.time = 11.5f;
    p.Observe(b);
    C(l.IsActive() && p.states[b].PostCombat.Policy.Trace!.CombatLayerSeconds == 1.5,
        "exactly 1.5 seconds qualifies without a visible enemy, a shot or incoming fire: " + layer);
}
foreach (var layer in new[] { ESAINLayer.None, ESAINLayer.Squad, ESAINLayer.Run, ESAINLayer.Extract })
{
    var (p, b, l, _) = Setup(false);
    b.ActiveLayer = layer;
    b.GoalEnemy!.IsVisible = b.BotOwner.ShootData.Shooting = b.BotOwner.Memory.IsUnderFire = true;
    p.Observe(b);
    Step(2);
    p.Observe(b);
    b.GoalEnemy.IsVisible = b.BotOwner.ShootData.Shooting = b.BotOwner.Memory.IsUnderFire = false;
    C(!l.IsActive() && p.states.Count == 0, "contact signals alone cannot arm recovery: " + layer);
}
{
    var (p, b, l, _) = Setup(false);
    b.ActiveLayer = ESAINLayer.Combat;
    p.Observe(b);
    Step(.75f);
    b.ActiveLayer = ESAINLayer.AvoidThreat;
    p.Observe(b);
    Step(2);
    b.ActiveLayer = ESAINLayer.Squad;
    p.Observe(b);
    var policy = p.states[b].PostCombat.Policy;
    C(!policy.WantsRecovery(Time.time) && policy.LastFight == 10.75,
        "time on Squad alone cannot finish an incomplete combat window");
    b.ActiveLayer = ESAINLayer.Flashed;
    p.Observe(b);
    Step(.25f);
    b.ActiveLayer = ESAINLayer.Squad;
    p.Observe(b);
    C(l.IsActive() && policy.Trace!.CombatLayerSeconds == 2.75,
        "Squad preserves the first and last qualifying timestamps for the handoff");
}
foreach (var interruption in new[] { ESAINLayer.None, ESAINLayer.Run, ESAINLayer.Extract })
{
    var (p, b, l, _) = Setup();
    b.ActiveLayer = interruption;
    p.Observe(b);
    C(!l.IsActive(), "leaving the combat and squad layers discards unused eligibility: " + interruption);
    b.ActiveLayer = ESAINLayer.Combat;
    p.Observe(b);
    Step(.75f);
    p.Observe(b);
    C(!l.IsActive(), "a new fight starts its own clock after " + interruption);
    Step(.75f);
    p.Observe(b);
    C(l.IsActive(), "the new fight qualifies at its own boundary after " + interruption);
}
{
    var (p, b, l, _) = Setup(false);
    b.ActiveLayer = ESAINLayer.Combat;
    p.Observe(b);
    Step(1);
    p.Authority = false;
    p.Observe(b);
    Step(2);
    p.Authority = true;
    p.Observe(b);
    C(!l.IsActive(), "time on a Fika replica does not carry over into a new authoritative window");
    Step(1.5f);
    p.Observe(b);
    C(l.IsActive(), "authoritative combat qualifies again after 1.5 seconds");
    var json = Newtonsoft.Json.Linq.JObject.Parse(p.diagnostics!.Lines.Last());
    C((double?)json["postCombat"]?["combatLayerSeconds"] == 1.5,
        "the existing H04 event includes the qualifying combat duration");
}
{
    var (p, b, l, a) = Setup();
    b.GoalEnemy!.EnemyKnown = true;
    C(l.IsActive(), "source permits recovery while contact remains hidden");
    b.GoalEnemy.EnemyKnown = false;
    b.Decision.CurrentCombatDecision = ECombatDecision.AvoidGrenade;
    C(!l.IsActive(), "native combat preempts");
    b.Decision.CurrentCombatDecision = ECombatDecision.None;
    (p, b, l, a) = Setup();
    C(l.IsActive() && b.SAINLayersActive, "actual SAIN layer activation allows mover ticks");
    a.Start();
    C(a.Starts == 1 && p.states[b].PostCombat.Layer, "native action lifecycle retained");
    a.OnSteeringTicked();
    C(b.Steering.Points > 0 && b.Steering.LastPoint.x == 20, "only remembered point steered");
    b.GoalEnemy.KnownPlaces.LastSeenPlace!.Position = new(999, 0, 999);
    Step();
    a.Update(new());
    C(p.states[b].PostCombat.Threat!.Value.x == 20, "hidden memory changes not live tracking");
    b.BotOwner.Memory.IsUnderFire = true;
    C(!l.IsActive() && !p.states[b].PostCombat.Policy.Active, "urgent threat bypasses cadence");
    a.Stop();
    C(!p.states[b].PostCombat.Layer, "stop cleanup");
    Save(p);
}
{
    var (p, b, l, a) = Setup();
    C(l.IsActive(), "recovery active before secondary threat");
    a.Start();
    b.Shoot.Result = true;
    int points = b.Steering.Points,
        moving = b.Steering.Moving;
    a.OnSteeringTicked();
    C(
        b.Shoot.Calls == 1 && b.Steering.Points == points && b.Steering.Moving == moving,
        "post-combat fires at another visible threat without overwriting aim"
    );
    C(
        b.Suppression.Calls == 0 && b.Steering.Priorities == 0,
        "post-combat keeps source fire-only behavior"
    );
}
foreach (
    string scope in new[]
    {
        "disabled",
        "feature",
        "scav",
        "player",
        "ghost",
        "remote",
        "inactive",
        "dead",
        "ending",
        "world",
    }
)
{
    var (p, b, l, a) = Setup();
    C(l.IsActive(), "before " + scope);
    a.Start();
    switch (scope)
    {
        case "disabled":
            p.config!.Enabled = false;
            break;
        case "feature":
            p.config!.PostCombat.Enabled = false;
            break;
        case "scav":
            b.Pmc = false;
            break;
        case "player":
            b.Player.IsAI = false;
            break;
        case "ghost":
            p.IsGhost = true;
            break;
        case "remote":
            p.Authority = false;
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
        default:
            p.world = null;
            break;
    }
    C(!l.IsActive() && !p.states[b].PostCombat.Policy.WantsRecovery(11), "scope release " + scope);
    Save(p);
}
{
    var (p, b, l, a) = Setup();
    b.Cover.CoverInUse = null;
    C(l.IsActive(), "source recovery starts in open ground");
    a.Start();
    C(
        b.Mover.Prone.Value && b.Mover.Routes == 0,
        "source drops prone when no sheltered native point is available"
    );
    Step();
    a.Update(new());
    C(
        p.states[b].PostCombat.Policy.Trace!.State == "watch",
        "failed cover search advances to watch rather than repeated probes"
    );
    p.Cleanup(b);
    C(!p.states[b].PostCombat.Policy.Active, "cleanup releases recovery");
    Save(p);
}
// Seven seconds to shelter is longer than the initial watch, even on the longest draw.
foreach (float lastDraw in new[] { 3f, 6f })
{
    var (p, b, layer, action) = Setup();
    p.config!.PostCombat.AdvanceChances["Normal"] = 100;
    Physics.BlocksSightTo = point => point.z >= 15.9f;
    UnityEngine.Random.RangeFraction = lastDraw == 3f ? 1f : 0f;
    C(layer.IsActive(), "a distant shelter is available");
    action.Start();
    var r = p.states[b].PostCombat;
    var cover = r.Goal;
    C(r.RouteStage == "cover", "the native route leads to shelter");
    C(b.Mover.Pose == 1 && b.Mover.Stops == 0, "a long cover approach keeps the running stance");
    for (int i = 1; i <= 13; i++)
    {
        Step(.5f);
        b.Position = cover * (i / 14f);
        if (i == 13)
            UnityEngine.Random.RangeFraction = lastDraw == 3f ? 0f : 1f;
        action.Update(new());
    }
    C(r.Policy.Trace!.WatchRemaining == lastDraw, "each cover update replaces the previous draw");
    Step(.5f);
    b.Position = cover - new Vector3(0, 0, 1);
    action.Update(new());
    C(r.Protected && r.Path == null, "exactly one metre is close enough to stop renewing");
    C(r.Policy.Trace.WatchRemaining == lastDraw - .5f, "arrival preserves the last travel deadline");
    Step(.25f);
    action.Update(new());
    C(r.Policy.Trace.State == "watch" && r.RouteStage == "", "the bot watches after a seven-second trip");
    C(b.Mover.Stops > 0 && b.Mover.Pose == .75f, "the watcher stops and lowers after reaching distant shelter");
    var snapshot = Newtonsoft.Json.Linq.JObject.Parse(p.diagnostics!.Lines.Last())["postCombat"]!;
    C((double?)snapshot["lastCoverWatchDuration"] == lastDraw
        && (double?)snapshot["watchRemaining"] == lastDraw - .75f, "the watch event records draw and time left");
    Step(lastDraw - .75f);
    action.Update(new());
    C(r.Policy.Trace.State == "watch" && r.Policy.Trace.WatchRemaining == 0,
        "the bot still watches at the exact deadline");
    Step(.25f);
    action.Update(new());
    C(r.RouteStage == "check", "the first decision after expiry orders the approach");
    C(b.Mover.Pose == 1 && !b.Mover.Prone.Value, "the watcher stands to inspect the last contact");
}
UnityEngine.Random.RangeFraction = 0;
// The bot starts exposed at z=0. A wall shelters the first near ring at z=3.
Physics.BlocksSightTo = point => point.z >= 2.9f;
foreach (bool acceptFirstOrder in new[] { false, true })
{
    var (p, b, layer, action) = Setup();
    var c = p.config!.PostCombat;
    c.WatchMin = 2;
    c.WatchMax = 4;
    b.Mover.Walk = acceptFirstOrder;
    C(layer.IsActive(), "the layer may select a cover even if native movement later refuses it");
    action.Start();
    var r = p.states[b].PostCombat;
    C(r.Policy.Trace!.LastCoverWatchDuration == 2, "a first cover attempt uses the configured duration");
    UnityEngine.Random.RangeFraction = 1;
    Step(.125f);
    layer.IsActive();
    action.OnSteeringTicked();
    C(r.Policy.Trace.LastCoverWatchDuration == 2, "layer and steering queries leave the cover draw alone");
    action.Update(new());
    if (acceptFirstOrder)
    {
        C(r.Policy.Trace.LastCoverWatchDuration == 4 && b.Mover.Routes == 1,
            "the action renews during the movement throttle without sending another order");
        UnityEngine.Random.RangeFraction = 0;
        b.BotOwner.Medecine.FirstAid.Using = true;
        Step(.0625f);
        action.Update(new());
        C(r.Policy.Trace.LastCoverWatchDuration == 4, "treatment suppresses renewal even before the next policy tick");
        b.BotOwner.Medecine.FirstAid.Using = false;
        b.Mover.Walk = false;
        Step(1);
        action.Update(new());
        C(r.Policy.Trace.LastCoverWatchDuration == 2 && r.RouteStage == "", "a declined repeat order gets its final draw");
    }
    int orders = b.Mover.Routes;
    Step(.5f);
    action.Update(new());
    C(r.Policy.Trace.State == "watch" && r.Policy.Trace.WatchRemaining < 2 && b.Mover.Routes == orders,
        "a declined route leaves a single watch to expire");
    UnityEngine.Random.RangeFraction = 0;
}
foreach (var (alreadyReloading, spare, requests) in new[] { (false, true, 1), (true, true, 0), (false, false, 0) })
{
    var (p, b, layer, action) = Setup();
    b.Player.HandsController = new Player.FirearmController { Item = new() { Magazine = new() { Count = 5 } } };
    var reload = b.BotOwner.WeaponManager.Reload;
    reload.Reloading = alreadyReloading;
    reload.Available = spare;
    C(layer.IsActive(), "post-combat layer available in the open");
    action.Start();
    var r = p.states[b].PostCombat;
    C(r.RouteStage == "cover" && b.Mover.Routes == 1 && r.Goal.z >= 2.9f,
        "first action update orders a route to shelter");
    C(b.Mover.Stops == 0 && b.Mover.Pose == .8f, "moving to nearby cover keeps its own stance during a reload");
    C(reload.Requests == requests && reload.Reloading == (alreadyReloading || spare),
        "cover order and native reload agree on the first update");
    C(r.Policy.Trace!.LastCoverWatchDuration == 3, "cover renews during a native reload or with idle hands");
    C(r.Policy.Trace is { State: "cover", Execution: "native movement requested" }
        && r.Policy.Trace.Reloading == reload.Reloading, "cover trace includes the actual native reload state");
    Step(1.1f);
    action.Update(new());
    C(b.Mover.Routes == 2 && reload.Requests == requests,
        "the route continues without a duplicate reload request");
    b.Position = r.Goal;
    Step();
    action.Update(new());
    C(r.Protected && r.Path == null, "arrival completes the cover move with hands busy or idle");
    action.Stop();
    C(reload.Reloading == (alreadyReloading || spare), "layer release preserves the native reload");
    Save(p);
}
{
    var (p, b, layer, action) = Setup();
    var firstAid = b.BotOwner.Medecine.FirstAid;
    var reload = b.BotOwner.WeaponManager.Reload;
    ((Player.FirearmController)b.Player.HandsController).Item.Magazine.Count = 5;
    C(layer.IsActive(), "cover is selected before treatment begins");
    firstAid.Using = true;
    action.Start();
    Step();
    action.Update(new());
    C(b.Mover.Routes == 0 && reload.Requests == 0, "ongoing treatment takes priority over moving and reloading");
    firstAid.Using = false;
    Step();
    action.Update(new());
    var path = (BotPathDataManual?)p.states[b].PostCombat.Path;
    C(path != null && reload.Requests == 1, "cover and reload start together when treatment finishes");
    firstAid.Using = true;
    Step();
    action.Update(new());
    C(path!.Stops == 1 && p.states[b].PostCombat.Path == null && b.Mover.Routes == 1,
        "starting treatment en route releases the owned cover path");
    Save(p);
}
Physics.BlocksSightTo = null;
foreach (bool available in new[] { true, false })
{
    var (p, b, l, a) = Setup();
    b.Player.HandsController = new Player.FirearmController
    {
        Item = new() { Magazine = new() { Count = 5 } },
    };
    b.BotOwner.WeaponManager.Reload.Available = available;
    l.IsActive();
    a.Start();
    Step();
    a.Update(new());
    var reload = b.BotOwner.WeaponManager.Reload;
    C(reload.Requests == (available ? 1 : 0), "native reload availability");
    C(
        ((Player.FirearmController)b.Player.HandsController).Item.Magazine.Count == 5,
        "no fake ammo"
    );
    reload.Reloading = false;
    Step();
    a.Update(new());
    C(reload.Requests == (available ? 1 : 0), "no repeated reload request");
    Save(p);
}
{
    var (p, b, l, a) = Setup();
    b.Player.HealthController.Parts[EBodyPart.LeftArm].Current = 20;
    b.Player.HealthController.Effects.Add(new Heavy(EBodyPart.LeftArm));
    var item = new MedKit { Id = "carried-kit", AllowedPart = EBodyPart.LeftArm };
    item.HealthEffectsComponent.DamageEffects[EDamageEffectType.HeavyBleeding] = new();
    b.Player.InventoryController.Items.Add(item);
    b.BotOwner.Medecine.FirstAid.CurUsingMeds = item;
    l.IsActive();
    a.Start();
    Step();
    a.Update(new());
    C(
        b.BotOwner.Medecine.FirstAid.Requests == 1 && b.BotOwner.Medecine.FirstAid.Using,
        "real carried kit requested once"
    );
    C(b.Player.HealthController.Parts[EBodyPart.LeftArm].Current == 20, "no instant healing");
    Step(61);
    p.Sample(b);
    a.Update(new());
    C(!l.IsActive() && b.BotOwner.Medecine.FirstAid.Using
        && b.BotOwner.Medecine.FirstAid.Cancels == 0, "H04 releases while the original medicine is still in use");
    b.BotOwner.Medecine.FirstAid.Finish();
    b.BotOwner.Medecine.FirstAid.CurUsingMeds = null;
    b.Player.HandsController = new Player.FirearmController();
    p.Sample(b);
    Step();
    C(!l.IsActive(), "after long medicine routine yields");
    Save(p);
}
foreach (bool bhf in new[] { false, true })
{
    var (p, b, layer, action) = Setup();
    p.config!.PostCombat.AdvanceChances["Normal"] = 0;
    Physics.BlocksSightTo = _ => true;
    var surgery = b.BotOwner.Medecine.SurgicalKit;
    b.Player.HealthController.Destroyed.Add(EBodyPart.LeftLeg);
    surgery.CurUsingMeds = new Meds { Id = "waiting-cms", AllowedPart = EBodyPart.LeftLeg };
    surgery._bodyPartToHeal = EBodyPart.LeftLeg;
    surgery._nextPosibleUseTime = Time.time + 16;
    surgery.Bhf = bhf;
    Step(13.5f);
    C(layer.IsActive(), "H04 starts inside the ordinary window");
    action.Start();
    Step(.5f);
    C(layer.IsActive(), "an injured bot with a kit on cooldown keeps H04 at fourteen seconds");
    action.Update(new());
    var snapshot = Newtonsoft.Json.Linq.JObject.Parse(p.diagnostics!.Lines.Last())["postCombat"]!;
    C((bool?)snapshot["medicalWorkLeft"] == true && (bool?)snapshot["medicalUseful"] == false
        && (bool?)snapshot["medicalBusy"] == false, "telemetry separates pending surgery from readiness and use");
    Step(1.75f);
    action.Update(new());
    C(layer.IsActive() && surgery.Requests == 0, "no early surgery while waiting on the native timer");
    Step(.25f);
    action.Update(new());
    Step(.25f);
    action.Update(new());
    C(surgery.Using && surgery.Requests == 1, "native surgery starts once when the kit becomes ready");
    surgery.Finish();
    b.Player.HandsController = new Player.FirearmController();
    Step(.25f);
    C(!layer.IsActive() && surgery.Cancels == 0, "native completion releases H04, BHF=" + bhf);
    Physics.BlocksSightTo = null;
}
foreach (bool firstAid in new[] { true, false })
foreach (bool alreadyFollowing in new[] { false, true })
{
    var (p, b, layer, action) = Setup();
    Physics.BlocksSightTo = _ => true;
    b.Squad.IAmLeader = false;
    b.Squad.LeaderComponent = new() { Position = new(40, 0, 0) };
    SAIN.SAINEnableClass.Bots[b.ProfileId] = b;
    C(layer.IsActive(), "quiet follower starts recovery before a heal is requested");
    var r = p.states[b].PostCombat;
    if (alreadyFollowing)
    {
        action.Start();
        Step(.25f);
    }
    var originalPath = (BotPathDataManual?)r.Path;
    var sain = b.SelfActions;
    int Attempts() => firstAid ? sain.FirstAidAttempts : sain.SurgeryAttempts;
    NativeMed treatment;
    if (firstAid)
    {
        b.Player.HealthController.Parts[EBodyPart.LeftArm].Current = 20;
        treatment = b.BotOwner.Medecine.FirstAid;
        sain.NextFirstAid = Time.time + 1.5f;
        b.Player.HealthController.Destroyed.Add(EBodyPart.LeftLeg);
        b.BotOwner.Medecine.SurgicalKit.CurUsingMeds = new Meds();
    }
    else
    {
        b.Player.HealthController.Destroyed.Add(EBodyPart.LeftLeg);
        treatment = b.BotOwner.Medecine.SurgicalKit;
        sain.NextSurgery = Time.time + 1.5f;
    }
    treatment.CurUsingMeds = new MedKit();
    if (alreadyFollowing) action.Update(new());
    else
    {
        Step(.25f);
        action.Start();
    }
    C(Attempts() == 1 && !treatment.Using && r.Policy.Trace!.State == "regroup"
        && r.RouteStage == "regroup" && b.Mover.LastWalkPoint.x == 40 && b.Mover.Routes == 1,
        "a refused native treatment starts or continues the leader route in the same update");
    var path = (BotPathDataManual)r.Path!;
    C(originalPath == null || ReferenceEquals(originalPath, path), "a refusal preserves the existing route");
    var snapshot = Newtonsoft.Json.Linq.JObject.Parse(p.diagnostics!.Lines.Last())["postCombat"]!;
    C((string?)snapshot["state"] == "regroup" && (string?)snapshot["lastTreatmentAttempt"]
        == (firstAid ? "first-aid-declined" : "surgery-declined"), "report movement and the native refusal together");
    Step(.25f);
    action.Update(new());
    C(Attempts() == 1 && path.Stops == 0, "no native retry or route teardown during the retry interval");
    Step(.75f);
    action.Update(new());
    C(Attempts() == 2 && !treatment.Using && path.Stops == 0 && r.RouteStage == "regroup",
        "a second refusal still allows regrouping");
    var lastPath = (BotPathDataManual)r.Path!;
    Step(1);
    action.Update(new());
    C(Attempts() == 3 && treatment.Using && treatment.Requests == 1 && r.Path == null
        && lastPath.Stops == 1 && r.Policy.Trace!.State == "medical" && b.Mover.Pose == .6f,
        "an accepted retry stops the movement and starts treatment once");
    Step(.25f);
    action.Update(new());
    C(Attempts() == 3 && treatment.Cancels == 0, "leave the ongoing treatment with SAIN");
    C(!firstAid || sain.SurgeryAttempts == 0, "declined first aid retains zzap's priority over surgery");
    action.Stop();
    Physics.BlocksSightTo = null;
}
foreach (bool gunfire in new[] { true, false })
{
    var (p, b, layer, action) = Setup();
    Physics.BlocksSightTo = _ => true;
    p.config!.PostCombat.AdvanceChances["Normal"] = 100;
    b.Squad.IAmLeader = !gunfire;
    b.Squad.LeaderComponent = new() { Position = new(40, 0, 0) };
    SAIN.SAINEnableClass.Bots[b.ProfileId] = b;
    b.Player.HealthController.Destroyed.Add(EBodyPart.LeftLeg);
    b.BotOwner.Medecine.SurgicalKit.CurUsingMeds = new Meds();
    b.SelfActions.NextSurgery = Time.time + 10;
    var hearing = new SAIN.Components.BotControllerSpace.Classes.BotHearingClass();
    SourceSoundWatch.Attach(hearing);
    try
    {
        if (gunfire)
            SourceSoundWatch.OnShot(new() { ProfileId = "distant-shooter" }, new(45, 0, 0));
        C(layer.IsActive(), "a declined treatment can fall back to watch or inspection");
        action.Start();
        var r = p.states[b].PostCombat;
        C(r.Policy.Trace!.State == "watch" && b.Mover.Stops == 1 && b.SelfActions.SurgeryAttempts == 1,
            "watch after refusal with no eligible regroup, including gunfire with a distant leader");
        if (!gunfire)
        {
            Step(7);
            action.Update(new());
            C(r.RouteStage == "check" && b.Mover.Routes == 1 && b.SelfActions.SurgeryAttempts == 2,
                "refusal permits the selected inspection after the watch expires");
        }
    }
    finally
    {
        SourceSoundWatch.Detach(hearing);
        Physics.BlocksSightTo = null;
    }
}
foreach (var reason in new[] { "no-kit", "healthy", "kit-lost", "first-aid-cooldown", "grenade-throw" })
{
    var (_, b, layer, action) = Setup();
    Physics.BlocksSightTo = _ => true;
    var med = b.BotOwner.Medecine;
    if (reason is "first-aid-cooldown" or "grenade-throw")
    {
        b.Player.HealthController.Parts[EBodyPart.LeftArm].Current = 20;
        med.FirstAid.CurUsingMeds = new MedKit();
        b.BotOwner.WeaponManager.Grenades.ThrowindNow = reason == "grenade-throw";
        if (reason == "first-aid-cooldown") med.FirstAid._nextPosibleUseTime = Time.time + 16;
    }
    else
    {
        if (reason != "healthy") b.Player.HealthController.Destroyed.Add(EBodyPart.LeftLeg);
        if (reason != "no-kit") med.SurgicalKit.CurUsingMeds = new Meds();
        med.SurgicalKit._nextPosibleUseTime = Time.time + 20;
    }
    Step(14);
    C(layer.IsActive() == (reason is "kit-lost" or "grenade-throw"), "native work at late admission: " + reason);
    if (reason == "kit-lost")
    {
        action.Start();
        med.SurgicalKit.CurUsingMeds = null;
        Step(.25f);
        C(!layer.IsActive() && med.SurgicalKit.Requests == 0, "losing the only kit ends the extension");
    }
    if (reason == "grenade-throw")
    {
        action.Start();
        C(med.FirstAid.Requests == 0, "first aid waits while a grenade throw blocks native start");
        b.BotOwner.WeaponManager.Grenades.ThrowindNow = false;
        Step(.25f);
        action.Update(new());
        C(med.FirstAid.Using && med.FirstAid.Requests == 1, "first aid starts when the temporary block clears");
    }
    Physics.BlocksSightTo = null;
}
foreach (bool alreadyReloading in new[] { false, true })
{
    var (p, b, layer, action) = Setup();
    Physics.BlocksSightTo = _ => true;
    var reload = b.BotOwner.WeaponManager.Reload;
    var hands = (Player.FirearmController)b.Player.HandsController;
    hands.Item.Magazine.Count = 5;
    reload.Reloading = alreadyReloading;
    int requests = alreadyReloading ? 0 : 1;
    Step(13.5f);
    C(layer.IsActive(), "a late reload is eligible inside the ordinary window");
    action.Start();
    Step(.4375f);
    action.Update(new());
    var r = p.states[b].PostCombat;
    C(r.Layer && reload.Reloading && reload.Requests == requests, "keep the native reload before the deadline");
    C(r.Policy.Trace!.Remaining == .0625, "a reload alone reports only the remaining ordinary time");
    Step(.0625f);
    if (alreadyReloading) action.OnSteeringTicked();
    else action.Update(new());
    C(!r.Layer && !r.Policy.Active && !Api.Decisions.Recovery.ContainsKey(b.ProfileId),
        "a reload alone cannot keep control at exactly fourteen seconds");
    var end = Newtonsoft.Json.Linq.JObject.Parse(p.diagnostics!.Lines.Last());
    C((string?)end["reason"] == Comet.Core.PostCombat.OrdinaryWindowExpired
        && (double?)end["postCombat"]?["secondsSinceCombat"] == 14
        && (double?)end["postCombat"]?["remaining"] == 0 && (bool?)end["postCombat"]?["reloading"] == true,
        "ordinary end telemetry shows the native reload is still in progress");
    action.Stop();
    Step(1);
    C(!layer.IsActive() && reload.Reloading && reload.Requests == requests
        && ReferenceEquals(hands, b.Player.HandsController) && hands.Item.Magazine.Count == 5,
        "releasing H04 neither cancels nor restarts the original reload");
    Physics.BlocksSightTo = null;
}
{
    var (p, b, layer, _) = Setup();
    var reload = b.BotOwner.WeaponManager.Reload;
    reload.Reloading = true;
    Step(14);
    C(!layer.IsActive() && p.states[b].PostCombat.Policy.Trace == null,
        "a first poll on the ordinary deadline cannot start H04 for an existing reload");
    Step(45.5f);
    C(!layer.IsActive() && reload.Reloading && reload.Requests == 0,
        "a late poll at 59.5 seconds still leaves the externally owned reload alone");
}
foreach (bool bhf in new[] { false, true })
{
    var (p, b, layer, action) = Setup();
    var surgery = b.BotOwner.Medecine.SurgicalKit;
    surgery.CurUsingMeds = new Meds { Id = "external-cms", AllowedPart = EBodyPart.LeftLeg };
    surgery.Bhf = bhf;
    int callbacks = 0;
    surgery.ApplyToCurrentPart(callback: () => callbacks++);
    var hands = b.Player.HandsController;
    Step(59.875f);
    C(layer.IsActive(), "an external surgery can hold the final fraction of the combat window");
    action.Start();
    var r = p.states[b].PostCombat;
    C(layer.IsActive() && r.Next > 70 && r.Policy.Trace!.State == "medical",
        "the next scheduled work tick is after the deadline");
    Step(.125f);
    action.Update(new());
    C(!r.Layer && !r.Policy.Active && surgery.Using && ReferenceEquals(hands, b.Player.HandsController)
        && surgery.Cancels == 0 && surgery.Requests == 1 && callbacks == 0,
        "expiration before polling leaves the original surgery and callback intact, BHF=" + bhf);
    action.Stop();
    surgery.Finish();
    C(callbacks == 1 && !surgery.Using && !layer.IsActive(), "native surgery completion after H04 exits, BHF=" + bhf);
}
{
    var (p, b, layer, action) = Setup();
    var hearing = new SAIN.Components.BotControllerSpace.Classes.BotHearingClass();
    var stranger = new SAIN.Components.PlayerComponentSpace.PlayerComponent { ProfileId = "noisy-neighbour" };
    SourceSoundWatch.Attach(hearing);
    try
    {
        Step(20);
        for (int age = 20; age < 60; age++)
        {
            hearing.Emit(SAINSoundType.FootStep, b.Position, stranger);
            C(layer.IsActive(), "repeated noise extends the delayed H04 episode at age " + age);
            if (age == 20) action.Start();
            else action.Update(new());
            Step(1);
        }
        hearing.Emit(SAINSoundType.FootStep, b.Position, stranger);
        b.Shoot.Result = true;
        int previous = b.Shoot.Calls;
        action.OnSteeringTicked();
        var r = p.states[b].PostCombat;
        C(b.Shoot.Calls == previous && !r.Layer && !r.Policy.Active
            && !Api.Decisions.Recovery.ContainsKey(b.ProfileId), "a stale steering callback releases ownership at 60 seconds");
        int records = p.diagnostics!.Lines.Count;
        var end = Newtonsoft.Json.Linq.JObject.Parse(p.diagnostics.Lines.Last());
        C((string?)end["reason"] == Comet.Core.PostCombat.WindowExpired
            && (double?)end["postCombat"]?["secondsSinceCombat"] == 60
            && (double?)end["postCombat"]?["elapsed"] == 40
            && (double?)end["postCombat"]?["remaining"] == 0, "end event distinguishes combat age from episode duration");
        action.Update(new());
        action.Stop();
        Step(5);
        C(!layer.IsActive() && p.diagnostics.Lines.Count == records,
            "later callbacks neither restart H04 at 65 seconds nor duplicate its end event");
    }
    finally { SourceSoundWatch.Detach(hearing); }
}
{
    var (p, b, l, a) = Setup();
    b.Player.HealthController.Destroyed.Add(EBodyPart.LeftLeg);
    var item = new Meds { Id = "cms", AllowedPart = EBodyPart.LeftLeg };
    item.HealthEffectsComponent.DamageEffects[EDamageEffectType.DestroyedPart] = new();
    b.Player.InventoryController.Items.Add(item);
    b.BotOwner.Medecine.SurgicalKit.CurUsingMeds = item;
    l.IsActive();
    a.Start();
    Step();
    a.Update(new());
    C(
        b.BotOwner.Medecine.SurgicalKit.Requests == 1,
        "surgery allowed after quiet interval within medical budget"
    );
    C(b.Player.HealthController.Restores == 0, "no free limb restoration");
    Save(p);
}
{
    var (p, b, l, a) = Setup();
    p.config!.MedicalChoice.Enabled = false;
    b.Player.HealthController.Parts[EBodyPart.LeftArm].Current = 5;
    l.IsActive();
    a.Start();
    Step(15);
    C(
        !l.IsActive() && b.BotOwner.Medecine.FirstAid.Requests == 0,
        "H01 disabled leaves medicine native"
    );
    Save(p);
}
{
    var (p, b, l, a) = Setup();
    l.IsActive();
    a.Start();
    var s = p.states[b];
    Step();
    a.Update(new());
    int count = p.diagnostics!.Lines.Count;
    for (int i = 0; i < 10; i++)
        a.Update(new());
    C(p.diagnostics.Lines.Count == count, "same-frame logs coalesced");
    C(
        Api.Decisions.Recovery.ContainsKey(b.ProfileId),
        "RR sampling API set while actual layer holds"
    );
    p.Cleanup(b);
    C(
        !s.PostCombat.Policy.Active
            && s.PostCombat.Threat == null
            && !s.PostCombat.MedicalOwner
            && !Api.Decisions.Recovery.ContainsKey(b.ProfileId),
        "raid releases references and API ownership"
    );
    Save(p);
}
{
    var (p, b, l, a) = Setup();
    b.Player.HandsController = new Player.FirearmController
    {
        Item = new() { Magazine = null!, ChamberAmmoCount = 0 },
    };
    l.IsActive();
    a.Start();
    Step();
    a.Update(new());
    C(b.BotOwner.WeaponManager.Reload.Requests == 1, "missing empty magazine is not full");
    Save(p);
}
foreach (int chambered in new[] { 0, 1 })
{
    var (p, b, layer, action) = Setup();
    var gun = ((Player.FirearmController)b.Player.HandsController).Item;
    gun.Magazine.Count = 20;
    gun.ChamberAmmoCount = chambered;
    C(layer.IsActive(), "H04 available at the top-up threshold");
    action.Start();
    C(b.BotOwner.WeaponManager.Reload.Requests == (chambered == 0 ? 1 : 0),
        "the chambered round counts toward the 70 percent top-up threshold");
    C(p.states[b].PostCombat.Policy.Trace!.AmmoPercent == 100d * (20 + chambered) / 30,
        "H04 reports the same loaded percentage used for the decision");
}
foreach (var (status, totalHealth, chestHealth, baseChance, expected) in new[]
{
    (ETagStatus.Dying, 60f, 10f, 40, 8),
    (ETagStatus.Injured, 60f, 100f, 40, 40),
    (ETagStatus.BadlyInjured, 60f, 100f, 40, 8),
    (ETagStatus.Healthy, 100f, 100f, 40, 40),
    (ETagStatus.BadlyInjured, 35f, 100f, 75, 15),
    (ETagStatus.Injured, 35f, 100f, 75, 75), // SAIN has not sampled the latest damage yet.
})
{
    var (p, b, layer, action) = Setup();
    b.Memory.Health.HealthStatus = status;
    var parts = b.Player.HealthController.Parts;
    parts[EBodyPart.Common].Current = totalHealth;
    parts[EBodyPart.Chest].Current = chestHealth;
    if (status == ETagStatus.BadlyInjured && totalHealth == 60f)
        foreach (var part in new[] { EBodyPart.LeftArm, EBodyPart.RightArm, EBodyPart.LeftLeg })
            parts[part].Current = 10;
    p.config!.PostCombat.AdvanceChances["Normal"] = baseChance;
    C(layer.IsActive(), "health modifies the inspection roll while recovery still starts");
    var r = p.states[b].PostCombat;
    var t = r.Policy.Trace!;
    C(t.AdvanceChance == expected && t.AdvanceSelected == (t.AdvanceRoll < expected),
        $"initial {status} at {totalHealth}% total and {chestHealth}% chest uses {expected}%");
    var firstRoll = t.AdvanceRoll;
    b.Memory.Health.HealthStatus = status == ETagStatus.Healthy ? ETagStatus.Dying : ETagStatus.Healthy;
    action.Start();
    Step(.6f);
    action.Update(new());
    var log = Newtonsoft.Json.Linq.JObject.Parse(p.diagnostics!.Lines.Last())["postCombat"]!;
    C((string?)log["advanceHealthStatus"] == status.ToString() && (double?)log["advanceChance"] == expected
        && (double?)log["advanceRoll"] == firstRoll, "the event retains the status and draw from the start of recovery");
}
foreach (var (min, max, start, eligible) in new[]
{
    (4, 40, 4f, false), (4, 40, 4.01f, true), (4, 40, 39.99f, true), (4, 40, 40f, false),
    (8, 25, 8f, false), (8, 25, 8.01f, true), (8, 25, 24.99f, true), (8, 25, 25f, false),
})
{
    var (p, b, layer, action) = Setup();
    Physics.BlocksSightTo = _ => true;
    var settings = p.config!.PostCombat;
    settings.AdvanceMin = min;
    settings.AdvanceMax = max;
    settings.AdvanceChances["Normal"] = 100;
    var r = p.states[b].PostCombat;
    var target = b.GoalEnemy!.KnownPlaces.LastKnownPosition!.Value;
    b.Position = target + Vector3.up * start;
    C(layer.IsActive() && r.Policy.Trace!.AdvanceSelected == eligible, $"initial range {min}..{max} at {start} m");
    action.Start();
    // An accepted check can already be in reach before its first movement order.
    // An initially ineligible bot entering the range during its watch gets no new roll.
    b.Position = target + Vector3.up * (eligible ? 3 : 20);
    Step(6.1f);
    action.Update(new());
    C(r.InspectedThreat == eligible && b.Mover.Routes == 0, "one decision per episode, including arrival during the watch");
    Physics.BlocksSightTo = null;
}
foreach (float remaining in new[] { 41f, 4.01f, 4f, 3.5f, 3.01f, 3f, 2.99f })
{
    var (p, b, layer, action) = Setup();
    Physics.BlocksSightTo = _ => true;
    p.config!.PostCombat.AdvanceChances["Normal"] = 100;
    C(layer.IsActive(), "post-combat starts with the contact at 20 m");
    action.Start();
    Step(6.1f);
    action.Update(new());
    var r = p.states[b].PostCombat;
    C(r.RouteStage == "check" && b.Mover.Routes == 1, "native approach started after the watch");
    var path = (BotPathDataManual)r.Path!;
    b.Position = r.Goal + Vector3.up * remaining;
    Step(1.1f);
    action.Update(new());
    if (remaining > 3)
        C(!r.InspectedThreat && r.RouteStage == "check" && b.Mover.Routes == 2 && path.Stops == 0,
            $"renew the accepted route at {remaining} m");
    else
    {
        C(r.InspectedThreat && r.Path == null && b.Mover.Routes == 1 && path.Stops == 1,
            $"arrival at {remaining} m releases the native route exactly once");
        var arrival = Newtonsoft.Json.Linq.JObject.Parse(p.diagnostics!.Lines.Last())["postCombat"]!;
        C((string?)arrival["execution"] == "destination reached"
            && Math.Abs((double)arrival["inspectionDistance"]! - remaining) < .001,
            "the arrival event records the measured 3D distance");
        Step();
        action.Update(new());
        C(!r.Policy.Active && b.Mover.Routes == 1 && path.Stops == 1, "a completed inspection does not restart");
    }
    Physics.BlocksSightTo = null;
}
{
    var (p, b, layer, action) = Setup();
    Physics.BlocksSightTo = _ => true;
    p.config!.PostCombat.AdvanceChances["Normal"] = 0;
    b.Squad.IAmLeader = false;
    var leader = new BotComponent { Position = new(20, 0, 0) };
    b.Squad.LeaderComponent = leader;
    SAIN.SAINEnableClass.Bots[b.ProfileId] = b;
    C(layer.IsActive(), "a follower starts recovery with the leader nearby");
    action.Start();
    var r = p.states[b].PostCombat;
    int episode = r.Policy.Trace!.Episode;
    C(r.Policy.Trace.State == "watch" && b.Mover.Routes == 0, "watch while the leader is 20 m away");
    void CheckLeaderEvent(string stage, float? distance)
    {
        var snapshot = Newtonsoft.Json.Linq.JObject.Parse(p.diagnostics!.Lines.Last())["postCombat"]!;
        C((string?)snapshot["state"] == stage && (float?)snapshot["leaderDistance"] == distance,
            "the H04 event records the stage and the distance to the live leader");
    }
    CheckLeaderEvent("watch", 20);
    Step(1);
    action.Update(new());
    leader.Position = new(30, 0, 0);
    Step(.0625f);
    action.Update(new());
    C(r.RouteStage == "regroup" && b.Mover.Routes == 1 && r.Goal.x == 30,
        "a leader crossing the boundary during the watch starts a route on the next action update");
    CheckLeaderEvent("regroup", 30);
    var path = (BotPathDataManual)r.Path!;
    leader.Position = new(25, 0, 0);
    Step(.0625f);
    action.Update(new());
    C(r.Policy.Trace.State == "watch" && r.Path == null && path.Stops == 1,
        "returning to exactly 25 m releases the route and resumes the same watch");
    CheckLeaderEvent("watch", 25);
    leader.Position = new(35, 0, 0);
    Step(.0625f);
    action.Update(new());
    C(r.Policy.Trace.State == "regroup" && b.Mover.Routes == 1,
        "another departure is eligible immediately but does not bypass the native order cadence");
    Step(1);
    action.Update(new());
    C(b.Mover.Routes == 2 && r.Goal.x == 35 && r.Policy.Trace.Episode == episode,
        "the next order follows the leader again within the same recovery episode");
    leader.IsDead = true;
    Step(.0625f);
    action.Update(new());
    C(r.Policy.Trace.State == "watch" && r.Path == null, "a lost leader releases the current route");
    CheckLeaderEvent("watch", null);
    Step(7);
    action.Update(new());
    C(!r.Policy.Active, "the watch still expires");
    b.Squad.LeaderComponent = new() { Position = new(40, 0, 0) };
    SAIN.SAINEnableClass.Bots[b.ProfileId] = b;
    Step(1);
    C(!layer.IsActive() && b.Mover.Routes == 2, "a distant leader cannot reopen a finished episode");
    Physics.BlocksSightTo = null;
}
{
    var (p, b, layer, action) = Setup();
    Physics.BlocksSightTo = _ => true;
    p.config!.PostCombat.RegroupDistance = 12;
    b.Squad.IAmLeader = false;
    var leader = new BotComponent { Position = new(0, 12, 0) };
    b.Squad.LeaderComponent = leader;
    SAIN.SAINEnableClass.Bots[b.ProfileId] = b;
    C(layer.IsActive(), "a follower can watch at its configured regroup distance");
    action.Start();
    C(b.Mover.Routes == 0, "the exact configured threshold is inside the watch radius");
    leader.Position = new(0, 12.125f, 0);
    Step(.0625f);
    action.Update(new());
    C(b.Mover.Routes == 1 && b.Mover.LastWalkPoint.y == 12.125f,
        "a vertical crossing uses the preset threshold and the leader's actual position");
    Physics.BlocksSightTo = null;
}
foreach (bool firstAccepted in new[] { false, true })
{
    var (p, b, layer, action) = Setup();
    Physics.BlocksSightTo = _ => true;
    b.Squad.IAmLeader = false;
    b.Squad.LeaderComponent = new() { Position = new(30, 0, 0) };
    SAIN.SAINEnableClass.Bots[b.ProfileId] = b;
    b.Mover.Walk = firstAccepted;
    C(layer.IsActive(), "start recovery with a distant leader");
    action.Start();
    var r = p.states[b].PostCombat;
    var originalPath = (BotPathDataManual?)r.Path;
    C(b.Mover.Routes == 1 && (originalPath != null) == firstAccepted, "issue the initial regroup order once");
    b.Mover.Walk = false;
    for (int i = 0; i < 16; i++)
    {
        Step(.0625f);
        action.Update(new());
    }
    C(r.Policy.Trace!.State == "regroup" && b.Mover.Routes == 1,
        "a refusal leaves regroup eligible and respects the fork's strict one-second deadline");
    Step(.0625f);
    action.Update(new());
    C(b.Mover.Routes == 2 && r.Path == originalPath && (originalPath?.Stops ?? 0) == 0,
        "a refused renewal retains any previous native path");
    var snapshot = Newtonsoft.Json.Linq.JObject.Parse(p.diagnostics!.Lines.Last())["postCombat"]!;
    C((string?)snapshot["state"] == "regroup" && (string?)snapshot["execution"] == "regroup movement declined; will retry",
        "the H04 log distinguishes a refused regroup order from an accepted one");
    b.Squad.LeaderComponent.Position = new(43, 0, 0);
    b.Mover.Walk = true;
    Step(.9375f);
    action.Update(new());
    C(b.Mover.Routes == 2, "a second refusal starts a fresh one-second interval");
    Step(.125f);
    action.Update(new());
    C(b.Mover.Routes == 3 && r.RouteStage == "regroup" && r.Goal.x == 43 && r.Path != null
        && r.Policy.Trace.Execution == "native movement requested", "the accepted retry uses the leader's latest position");
    Physics.BlocksSightTo = null;
}
foreach (var interruption in new[] { "nearby", "dead", "sound", "combat", "expired" })
{
    var (p, b, layer, action) = Setup();
    Physics.BlocksSightTo = _ => true;
    b.Squad.IAmLeader = false;
    b.Squad.LeaderComponent = new() { Position = new(30, 0, 0) };
    SAIN.SAINEnableClass.Bots[b.ProfileId] = b;
    var hearing = new SAIN.Components.BotControllerSpace.Classes.BotHearingClass();
    SourceSoundWatch.Attach(hearing);
    try
    {
        b.Mover.Walk = false;
        C(layer.IsActive(), "regroup is initially eligible before " + interruption);
        action.Start();
        var r = p.states[b].PostCombat;
        C(b.Mover.Routes == 1 && r.Path == null, "the initial order is refused before " + interruption);
        b.Mover.Walk = true;
        switch (interruption)
        {
            case "nearby": b.Squad.LeaderComponent.Position = new(25, 0, 0); break;
            case "dead": b.Squad.LeaderComponent.IsDead = true; break;
            case "sound": hearing.Emit(SAINSoundType.FootStep, new(5, 0, 0), new() { ProfileId = "stranger" }); break;
            case "combat": b.GoalEnemy!.IsVisible = true; break;
        }
        Step(interruption == "expired" ? 60 : 1.0625f);
        action.Update(new());
        C(b.Mover.Routes == 1 && r.Policy.Trace!.State == (interruption is "combat" or "expired" ? "ended" : "watch"),
            "discard a pending retry when the regroup is no longer eligible: " + interruption);
    }
    finally
    {
        SourceSoundWatch.Detach(hearing);
        Physics.BlocksSightTo = null;
    }
}
{
    var (p, b, layer, action) = Setup();
    Physics.BlocksSightTo = _ => true;
    b.Squad.IAmLeader = false;
    b.Squad.LeaderComponent = new() { Position = new(30, 0, 0) };
    SAIN.SAINEnableClass.Bots[b.ProfileId] = b;
    b.Mover.Walk = false;
    C(layer.IsActive(), "start a regroup with a temporary pathfinding budget deferral");
    action.Start();
    p.doorBudget.Available = false;
    Step(1.0625f);
    action.Update(new());
    C(b.Mover.Routes == 1, "a deferred retry never reaches native movement");
    b.Mover.Walk = true;
    p.doorBudget.Available = true;
    Step(.0625f);
    action.Update(new());
    C(b.Mover.Routes == 2 && p.states[b].PostCombat.RouteStage == "regroup",
        "the budget becoming available permits the pending retry without another one-second wait");
    Physics.BlocksSightTo = null;
}
foreach (bool alreadyReloading in new[] { true, false })
{
    var (p, b, layer, action) = Setup();
    Physics.BlocksSightTo = _ => true;
    b.Squad.IAmLeader = false;
    b.Squad.LeaderComponent = new() { Position = new(30, 0, 0) };
    SAIN.SAINEnableClass.Bots[b.ProfileId] = b;
    var reload = b.BotOwner.WeaponManager.Reload;
    reload.Reloading = alreadyReloading;
    ((Player.FirearmController)b.Player.HandsController).Item.Magazine.Count = 5;
    var hearing = new SAIN.Components.BotControllerSpace.Classes.BotHearingClass();
    SourceSoundWatch.Attach(hearing);
    try
    {
        C(layer.IsActive(), "a covered follower is eligible at the end of combat");
        action.Start();
        var r = p.states[b].PostCombat;
        int requests = alreadyReloading ? 0 : 1;
        C(r.RouteStage == "regroup" && b.Mover.Routes == 1 && b.Mover.LastWalkPoint.x == 30,
            "rejoin the distant leader on the first action update, including when H04 starts the reload");
        C(reload.Requests == requests && reload.Reloading, "the original native reload runs alongside movement");
        b.Squad.LeaderComponent.Position = new(37, 0, 0);
        Step(1.1f);
        action.Update(new());
        var trace = Newtonsoft.Json.Linq.JObject.Parse(p.diagnostics!.Lines.Last())["postCombat"]!;
        C((string?)trace["state"] == "regroup" && (bool?)trace["reloading"] == true
            && b.Mover.Routes == 2 && r.Goal.x == 37, "record regroup and reload while following the moving leader");
        var path = (BotPathDataManual)r.Path!;
        hearing.Emit(SAINSoundType.FootStep, new(5, 0, 0), new() { ProfileId = "stranger" });
        Step(.5f);
        action.Update(new());
        C(r.Heard && r.RouteStage == "" && r.Path == null && path.Stops == 1 && b.Mover.Routes == 2,
            "an audible enemy stops the regroup order during the reload");
        C(b.Mover.Stops == 1 && b.Mover.Pose == .75f && r.Policy.Trace!.Reloading,
            "a reloading follower watches low when the heard contact interrupts regrouping");
        action.Stop();
        C(reload.Requests == requests && reload.Reloading, "releasing the layer leaves the reload alone");
    }
    finally
    {
        SourceSoundWatch.Detach(hearing);
        Physics.BlocksSightTo = null;
    }
}
foreach (bool regroup in new[] { false, true })
{
    var (p, b, l, a) = Setup();
    p.config!.PostCombat.AdvanceChances["Normal"] = regroup ? 0 : 100;
    p.config.PostCombat.WatchMin = p.config.PostCombat.WatchMax = 0;
    if (regroup)
    {
        b.Squad.IAmLeader = false;
        b.Squad.LeaderComponent = new() { Position = new(30, 0, 0) };
        SAIN.SAINEnableClass.Bots[b.ProfileId] = b;
    }
    l.IsActive();
    a.Start();
    Step();
    a.Update(new());
    var s = p.states[b];
    C(
        s.PostCombat.RouteStage == (regroup ? "regroup" : "check") && b.Mover.Routes == 1,
        "optional native route " + regroup
    );
    C(s.PostCombat.Goal.x == (regroup ? 30 : 20), "leader or frozen known location only");
    b.Mover.Moving = true;
    a.OnSteeringTicked();
    C(regroup ? b.Steering.Moving == 1
        : Vector3.Distance(b.Steering.LastPoint, new(20, 1.3f, 0)) < .001f,
        "face travel when regrouping and the remembered contact when advancing");
    if (regroup)
    {
        b.Squad.LeaderComponent!.Position = new(55, 0, 0);
        b.Position = new(29.5f, 0, 0);
    }
    else
        b.GoalEnemy!.KnownPlaces.LastSeenPlace!.Position = new(55, 0, 0);
    Step(.4f);
    a.Update(new());
    C(b.Mover.Routes == 1 && s.PostCombat.RouteStage == (regroup ? "regroup" : "check"),
        "keep the route until the next order even on arrival at the leader's old position");
    Step(.7f);
    a.Update(new());
    C(b.Mover.Routes == 2 && b.Mover.LastWalkPoint.x == (regroup ? 55 : 20),
        "renew toward the current leader or the original enemy observation");
    if (!regroup)
    {
        b.Position = s.PostCombat.Goal - new Vector3(2, 0, 0);
        b.Mover.Moving = false;
        Step();
        a.Update(new());
        a.OnSteeringTicked();
        C(b.Steering.Random == 1 && s.PostCombat.InspectedThreat,
            "the check stops watching a contact point it has already reached");
    }
    if (regroup)
    {
        b.Squad.LeaderComponent!.IsDead = true;
        b.Squad.LeaderComponent = new() { Position = new(80, 0, 0) };
        SAIN.SAINEnableClass.Bots[b.ProfileId] = b;
        Step(1.1f);
        a.Update(new());
        C(b.Mover.Routes == 3 && b.Mover.LastWalkPoint.x == 80,
            "renew toward the replacement squad leader");
        var renewedPath = (BotPathDataManual)b.Mover.ActivePath!;
        b.Position = new(55, 0, 0);
        Step();
        a.Update(new());
        C(s.PostCombat.RouteStage != "regroup" && b.Mover.Routes == 3 && renewedPath.Stops == 1,
            "at exactly 25 metres release the renewed route without another regroup order");
    }
    Save(p);
}
{
    var (p, b, l, a) = Setup();
    l.IsActive();
    a.Start();
    Step();
    a.Update(new());
    var r = p.states[b].PostCombat;
    C(
        r.ProneInOpen && r.Policy.Trace!.State == "watch",
        "first episode uses source prone fallback in open ground"
    );
    C(b.Mover.Stops > 0 && b.Mover.Pose == .75f && b.Mover.Prone.Value,
        "the open-ground watch keeps its prone fallback during the stationary pose request");
    r.MedicalDisabled = true;
    r.MedicalFailures = 2;
    r.MedicalUseful = true;
    r.MedicalUrgent = true;
    r.MedicalNext = 1000;
    b.Position = new(10, 0, 0);
    b.ActiveLayer = ESAINLayer.None;
    p.Observe(b);
    b.GoalEnemy!.IsVisible = true;
    b.GoalEnemy.EnemyKnown = true;
    b.ActiveLayer = ESAINLayer.Combat;
    p.Observe(b);
    a.Stop();
    C(!l.IsActive(), "renewed fight owns bot between episodes");
    Step(1.5f);
    p.Observe(b);
    b.GoalEnemy.IsVisible = false;
    b.GoalEnemy.EnemyKnown = false;
    p.Protection = 0;
    p.ProtectionOrigin = b.Position;
    b.Cover.CoverPoints.Add(new() { Position = new(14, 0, 0) });
    Step();
    C(l.IsActive(), "second recovery can begin");
    a.Start();
    C(
        r.Policy.Trace!.Episode == 2 && r.ProneInOpen && !r.Protected,
        "second fight reevaluates shelter instead of retaining previous protection"
    );
    C(
        !r.MedicalDisabled
            && r.MedicalFailures == 0
            && !r.MedicalUseful
            && !r.MedicalUrgent
            && r.MedicalNext < 1000,
        "second episode resets failed and stale medical observations before evaluation"
    );
    Save(p);
}
{
    var (_, bot, _, _) = Setup(false);
    var oldSource = new SAIN.Components.BotControllerSpace.Classes.BotHearingClass();
    var activeSource = new SAIN.Components.BotControllerSpace.Classes.BotHearingClass();
    var stranger = new SAIN.Components.PlayerComponentSpace.PlayerComponent
    {
        ProfileId = "other-squad",
    };
    SourceSoundWatch.Attach(oldSource);
    SourceSoundWatch.Attach(activeSource);
    SourceSoundWatch.Detach(oldSource);
    activeSource.Emit(SAINSoundType.FootStep, bot.Position, stranger);
    C(
        SourceSoundWatch.Heard(bot, out _, out var coming, out _) && coming,
        "old raid disposal preserves new publisher"
    );
    SourceSoundWatch.Detach(activeSource);
    activeSource.Emit(SAINSoundType.FootStep, bot.Position, stranger);
    C(
        !SourceSoundWatch.Heard(bot, out _, out _, out _),
        "current owner detaches and clears sound history"
    );
}
var output = Path.GetFullPath(
    Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts/h04-adapter-events.jsonl")
);
File.WriteAllLines(output, events);
Console.WriteLine(
    $"{n} H04 production adapter checks passed; {events.Count} JSON events; controlled doubles, not Unity."
);
