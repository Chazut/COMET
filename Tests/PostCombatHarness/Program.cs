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
// The bot starts exposed at z=0. A wall shelters the first near ring at z=3.
Physics.BlocksSightTo = point => point.z >= 2.9f;
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
    C(reload.Requests == requests && reload.Reloading == (alreadyReloading || spare),
        "cover order and native reload agree on the first update");
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
{
    var (_, b, layer, action) = Setup();
    var reload = b.BotOwner.WeaponManager.Reload;
    reload.Reloading = true;
    Step(59.5f);
    C(layer.IsActive(), "pending native reload can keep recovery up to the combat deadline");
    action.Start();
    Step(.5f);
    action.Stop();
    C(!layer.IsActive() && reload.Reloading && reload.Requests == 0,
        "release at the deadline leaves the pending native reload alone");
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
