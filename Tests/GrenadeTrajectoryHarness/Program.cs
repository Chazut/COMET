using Comet.Client;
using SAIN.Components;
using SAIN.SAINComponent.Classes.Mover;
using UnityEngine;
using UnityEngine.AI;

int checks = 0;
void Check(bool ok, string why)
{
    checks++;
    if (!ok)
        throw new Exception("G02 adapter: " + why);
}
(Plugin, BotComponent) Setup(bool judgment = true)
{
    var p = new Plugin();
    p.Settings.GrenadeJudgment.Enabled = judgment;
    var b = new BotComponent();
    p.Tick(b, 10);
    return (p, b);
}
bool Ready(Plugin p, BotComponent b)
{
    for (int i = 0; i < 180; i++)
    {
        if (p.Find(b))
            return true;
        if (p.State(b).GrenadeTrajectory.Trace?.State == "blocked")
            return false;
        p.Tick(b, Time.timeAsDouble + .01);
    }
    return false;
}
foreach (bool useCometArcs in new[] { true, false })
foreach (var (age, distance, allowed) in new[] {
    (12d, 45f, true), (12d, 45.01f, false), (20.01d, 42f, false) })
{
    var (p, b) = Setup();
    p.Settings.GrenadeTrajectory.Enabled = useCometArcs;
    b.GoalEnemy!.KnownPlaces.LastKnownPlace!.Position = new(distance, 0, 0);
    p.Tick(b, 10 + age);
    bool ready = Ready(p, b);
    Check(ready == allowed, $"throw preparation with COMET arcs {useCometArcs}, age {age}, distance {distance}");
    if (allowed)
    {
        Check(p.State(b).GrenadeJudgment.Policy.Trace!.LastPenalty == 20 && p.Throw(b),
            "the stale penalty applies and the prepared throw can start");
        Check(p.Events.Any(json => json.Contains("\"maxContactAgeSeconds\":20") &&
            json.Contains("\"maxTargetDistanceMeters\":45")), "G01 reports the active limits");
    }
    else
        Check(AIGrenadeHelper.Calls == 0, "an expired or distant contact spends no trajectory probes");
}
foreach (bool useCometArcs in new[] { true, false })
{
    var (p, b) = Setup();
    p.Settings.GrenadeTrajectory.Enabled = useCometArcs;
    p.Settings.GrenadeContactLifetime = 25;
    p.Settings.GrenadeTrajectory.Distance = 52;
    b.GoalEnemy!.KnownPlaces.LastKnownPlace!.Position = new(51, 0, 0);
    p.Tick(b, 32);
    Check(Ready(p, b), "a longer custom age and range can prepare a throw");
    b.GoalEnemy.KnownPlaces.LastKnownPlace.Position = new(53, 0, 0);
    Check(!p.Throw(b), "both arc paths recheck the custom range before release");
}
foreach (
    var exclude in new Action<Plugin, BotComponent>[]
    {
        (p, b) => p.Settings.Enabled = false,
        (p, b) => p.Authority = false,
        (p, b) => p.IsGhost = true,
        (p, b) => b.Pmc = false,
        (p, b) => b.IsDead = true,
        (p, b) => b.GameEnding = true,
        (p, b) => b.BotActive = false,
    }
)
{
    var p = new Plugin();
    var b = new BotComponent();
    exclude(p, b);
    p.Tick(b, 10);
    Check(
        p.Count == 0 && p.Find(b) && p.Throw(b) && AIGrenadeHelper.Calls == 0,
        "excluded bots keep native pipeline"
    );
}
{
    var (p, b) = Setup();
    p.Draw = .95;
    for (int i = 0; i < 30 && p.State(b).GrenadeTrajectory.Selected == null; i++)
    {
        p.Find(b);
        p.Tick(b, Time.timeAsDouble + .01);
    }
    var arcs = p.State(b).GrenadeTrajectory;
    Check(arcs.Trace?.Reason == "trajectory-prepared" && arcs.Selected?.Alternative.Angle == 45,
        "uniform selection can choose the fourth viable arc even though the third requires less force");
}
{
    var (p, b) = Setup();
    Check(Ready(p, b), "native arc search completes across frames");
    var r = p.State(b).GrenadeTrajectory;
    Check(
        r.Trace!.Kind == "last-known" && r.Trace.Attempts == 4,
        $"source compares the four outdoor arcs: kind={r.Trace.Kind}, attempts={r.Trace.Attempts}"
    );
    Check(
        r.Trace.Alternatives.Count(a => a.Accepted) == 4 && r.Selected?.Alternative.Angle == 35,
        "the low-force branch selects the third arc without extra clearance probes"
    );
    int preparations = b.BotOwner.WeaponManager.Grenades.Preparations;
    Check(
        preparations > 0 && preparations == 1 && AIGrenadeHelper.Calls == 4,
        "the selected arc is registered once after comparing all angles"
    );
    int calls = AIGrenadeHelper.Calls,
        draws = p.Draws;
    Check(
        p.Find(b) && AIGrenadeHelper.Calls == calls && p.Draws == draws,
        "prepared arc reuse consumes no calls or rolls"
    );
    Check(
        b.BotOwner.WeaponManager.Grenades.Preparations == preparations,
        "cached arc does not repeat group grenade registration"
    );
    p.Tick(b, Time.timeAsDouble + .01);
    b.Transform.WeaponData.WeaponRoot = new(.1f, 0, 0);
    Check(p.Throw(b) && p.Requests == 1, "final validation passes native request");
    Check(
        b.BotOwner.WeaponManager.Grenades.Preparations == preparations + 1,
        "recalculated execution arc uses native preparation too"
    );
    Check(
        Vector3.Distance(r.Selected!.Data.Position, b.Transform.WeaponData.WeaponRoot) < .001f,
        "small origin change recomputes the arc from the current hand position"
    );
    Check(
        r.Trace.State == "validated" && !p.State(b).GrenadeJudgment.Policy.Trace!.Released,
        "validation is not projectile release"
    );
    p.Projectile(b);
    Check(
        p.State(b).GrenadeJudgment.Policy.Trace!.Released,
        "G01 still observes actual projectile"
    );
    p.End(b);
    Check(
        !r.Pending
            && r.Selected == null
            && r.Targets.Count == 0
            && r.Arcs.Count == 0
            && r.Trace.State == "ended",
        "raid clear drops door and native references"
    );
    File.WriteAllLines(
        Path.GetFullPath(
            Path.Combine(
                AppContext.BaseDirectory,
                "../../../../../artifacts/g02-adapter-events.jsonl"
            )
        ),
        p.Events
    );
}
foreach (bool judgment in new[] { true, false })
foreach (
    var change in new Action<Plugin, BotComponent>[]
    {
        (p, b) => NativeAdapter.FriendsClear = false,
        (p, b) => b.BotOwner.WeaponManager.Grenades.grenade!.Id = "changed",
        (p, b) => b.GoalEnemy = new(),
        (p, b) => p.Settings.Revision++,
        (p, b) => b.BotOwner.WeaponManager.Grenades.AIGreanageThrowData = new(),
    }
)
{
    var (p, b) = Setup(judgment);
    Check(Ready(p, b), "ready before binding change");
    p.Tick(b, Time.timeAsDouble + .01);
    change(p, b);
    int draws = p.Draws;
    Check(
        !p.Throw(b) && p.Requests == 0 && p.Draws == draws,
        "native friend check and selected grenade binding apply, also with G01 off"
    );
}

{
    var (p, b) = Setup();
    AIGrenadeHelper.Allowed = (v, a) => false;
    Check(
        !Ready(p, b) && p.State(b).GrenadeTrajectory.Trace!.Reason == "trajectory-no-safe-arc",
        "actual arc exhaustion fails"
    );
    Check(
        p.State(b).GrenadeJudgment.Policy.Trace!.Roll == null,
        "no G01 probability roll on failed search"
    );
    int calls = AIGrenadeHelper.Calls,
        draws = p.Draws;
    for (int i = 0; i < 50; i++)
    {
        p.Tick(b, Time.timeAsDouble + .01);
        p.Find(b);
    }
    Check(
        AIGrenadeHelper.Calls == calls && p.Draws == draws,
        "shared no-arc retry prevents scanning/random fishing"
    );
    p.Tick(b, Time.timeAsDouble + 1.5);
    AIGrenadeHelper.Allowed = (v, a) => true;
    Check(Ready(p, b), "retry can recover");
}
{
    var (p, b) = Setup();
    b.GoalEnemy!.VisiblePathPoint = new(17, 0, 0);
    AIGrenadeHelper.Allowed = (v, a) =>
        p.State(b).GrenadeTrajectory.Targets[p.State(b).GrenadeTrajectory.Index].Kind
        == "blind-corner";
    Check(
        Ready(p, b) && p.State(b).GrenadeTrajectory.Trace!.Kind == "blind-corner",
        "nearby reachable blind corner after direct arcs fail"
    );
    Check(
        p.State(b).GrenadeTrajectory.Trace!.Alternatives.Any(a => !a.Accepted),
        "rejected direct alternatives retained"
    );
}
{
    var (p, b) = Setup(false);
    var link = new DoorLink();
    b.BotOwner.VoxelesPersonalData.CurVoxel!.DoorLinks.Add(link);
    AIGrenadeHelper.Allowed = (v, a) =>
        p.State(b).GrenadeTrajectory.Targets[p.State(b).GrenadeTrajectory.Index].Kind
        == "open-door";
    Check(
        Ready(p, b) && p.State(b).GrenadeTrajectory.Trace!.Kind == "open-door",
        "open door alternative uses native arc"
    );
    p.Tick(b, Time.timeAsDouble + .01);
    link.Geometry.State = "Shut";
    Check(
        !p.Throw(b) && p.State(b).GrenadeTrajectory.Trace!.Reason == "trajectory-door-changed",
        "door closed before execution cancels, G01 off"
    );
}
foreach (
    var obstruction in new Action[] { () => NavMesh.NoLanding = true, () => NavMesh.NoRoute = true }
)
{
    var (p, b) = Setup();
    obstruction();
    Check(
        Ready(p, b) && AIGrenadeHelper.Calls > 0,
        "source arc does not require a walking route or NavMesh landing"
    );
}
{
    var (p, b) = Setup();
    b.GoalEnemy!.VisiblePathPoint = new(17, 0, 0);
    b.BotOwner.VoxelesPersonalData.CurVoxel!.DoorLinks.Add(new());
    AIGrenadeHelper.Allowed = (v, a) => false;
    Check(!Ready(p, b) && AIGrenadeHelper.Calls <= 12, "all candidates obey total arc cap");
}
{
    var (p, b) = Setup(false);
    Check(Ready(p, b), "G01 off still plans");
    var point = p.State(b).GrenadeTrajectory.Selected!.Data.Target;
    int draws = p.Draws;
    p.Tick(b, Time.timeAsDouble + .26);
    Check(Ready(p, b), "expired data refreshed");
    Check(
        Vector3.Distance(point, p.State(b).GrenadeTrajectory.Selected!.Data.Target) < .001f,
        "refresh retains dispersed aim"
    );
    Check(p.Draws == draws, "source cache reuses solution for 0.5 seconds");
}
{
    var (p, b) = Setup();
    Check(Ready(p, b), "all arcs evaluated before preparation");
    Check(
        p.State(b).GrenadeJudgment.Policy.Trace != null,
        "completed native arc is assessed by G01"
    );
    int calls = AIGrenadeHelper.Calls;
    for (int i = 0; i < 20; i++)
        p.Find(b);
    Check(AIGrenadeHelper.Calls - calls <= 2, "same-frame repeated consumers obey shared budget");
    p.IsGhost = true;
    p.Tick(b, Time.timeAsDouble + .01);
    Check(
        !p.State(b).GrenadeTrajectory.Pending && p.State(b).GrenadeTrajectory.Targets.Count == 0,
        "ghost transition drops pending work"
    );
}
Console.WriteLine(
    $"G02 production adapters: {checks} checks passed (substituted native contracts; no Unity raid)"
);
