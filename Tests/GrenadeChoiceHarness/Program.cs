using Comet.Client;
using Comet.Core;
using EFT;
using EFT.InventoryLogic;
using SAIN.Components;
using UnityEngine;

string wire = Path.GetFullPath(
    Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts/g03-adapter-events.jsonl")
);
File.WriteAllText(wire, "");
int checks = 0;
void Check(bool ok, string why)
{
    checks++;
    if (!ok)
        throw new Exception("G03 adapter: " + why);
}
(Plugin, BotComponent) Setup(bool g01 = true, bool g02 = true)
{
    var p = new Plugin();
    p.Settings.GrenadeJudgment.Enabled = g01;
    p.Settings.GrenadeTrajectory.Enabled = g02;
    var b = new BotComponent();
    b.Player.Inventory.Items.Clear();
    b.BotOwner.WeaponManager.Grenades.grenade = new ThrowWeap { Id = "long", GetExplDelay = 5 };
    b.Player.Inventory.Items.Add(b.BotOwner.WeaponManager.Grenades.grenade);
    b.Player.Inventory.Items.Add(new ThrowWeap { Id = "short", GetExplDelay = 3 });
    p.Tick(b, 10);
    return (p, b);
}
bool Ready(Plugin p, BotComponent b)
{
    for (int i = 0; i < 180; i++)
    {
        if (p.Find(b))
            return true;
        if (
            p.State(b).GrenadeChoice.Trace?.State == "blocked"
            || p.State(b).GrenadeTrajectory.Trace?.State == "blocked"
        )
            return false;
        p.Tick(b, Time.timeAsDouble + .01);
    }
    return false;
}
foreach (bool g01 in new[] { false, true })
foreach (bool g02 in new[] { false, true })
{
    var (p, b) = Setup(g01, g02);
    b.Player.Environment = EnvironmentType.Indoor;
    bool ready = Ready(p, b);
    if (!ready)
        Console.WriteLine(string.Join("\n", p.Events));
    Check(ready, $"short indoor selection with independent G01={g01}/G02={g02} toggles");
    var r = p.State(b).GrenadeChoice;
    Check(r.Item == "short" && r.Trace!.Margin >= .75, "real shorter carried item selected");
    Check(
        b.BotOwner.WeaponManager.Grenades.AIGreanageThrowData!.GrenadeType == null,
        "native type replacement disabled for selected arc"
    );
    int draws = p.Draws,
        selections = b.BotOwner.WeaponManager.Grenades.Selections;
    Check(
        p.Find(b) && p.Draws == draws && b.BotOwner.WeaponManager.Grenades.Selections == selections,
        "selection and probability stay stable"
    );
    p.Tick(b, Time.timeAsDouble + .01);
    Check(p.Throw(b) && p.Requests == 1, "native throw is authorized after validation");
    Check(
        r.Trace!.State == "validated" && r.Trace.Margin >= .75,
        "validation trace does not claim projectile release"
    );
    if (g01)
    {
        p.Projectile(b, "short");
        Check(
            p.State(b).GrenadeJudgment.Policy.Trace!.Released,
            "selected item matches projectile callback"
        );
    }
    p.End(b);
    Check(
        r.Item == "" && r.Trace.State == "ended" && r.Trace.ValidFor == 0,
        "raid cleanup clears binding"
    );
    File.AppendAllLines(wire, p.Events);
}
int mutation = 0;
foreach (
    var changed in new Action<Plugin, BotComponent>[]
    {
        (p, b) => b.Player.Inventory.Items.Remove(b.BotOwner.WeaponManager.Grenades.grenade!),
        (p, b) => b.BotOwner.WeaponManager.Grenades.grenade!.GetExplDelay = 1,
        (p, b) => b.BotOwner.WeaponManager.Grenades.grenade!.MinTimeToContactExplode = 0,
        (p, b) => b.BotOwner.WeaponManager.Grenades.grenade!.IsDummy = true,
        (p, b) => b.BotOwner.WeaponManager.Grenades.grenade = new ThrowWeap { Id = "imposter" },
        (p, b) => p.Settings.Revision++,
        (p, b) => b.GoalEnemy = new(),
        (p, b) => b.BotOwner.WeaponManager.Grenades.AIGreanageThrowData!.ThrowComplete = true,
        (p, b) => b.BotOwner.WeaponManager.Grenades.AIGreanageThrowData!.Fresh = false,
        (p, b) => b.BotOwner.WeaponManager.Grenades.AIGreanageThrowData!.Ang = float.NaN,
        (p, b) => b.GoalEnemy = null,
    }
)
{
    var (p, b) = Setup(false, false);
    Check(Ready(p, b), "ready before changed facts");
    p.Tick(b, Time.timeAsDouble + .01);
    changed(p, b);
    mutation++;
    Check(
        !p.Throw(b) && p.Requests == 0,
        $"revalidation #{mutation} blocks stale, changed, unsafe item/arc/facts with G01/G02 OFF"
    );
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
        p.Count == 0
            && p.Find(b)
            && p.Throw(b)
            && b.BotOwner.WeaponManager.Grenades.Selections == 0,
        "excluded bots/clients retain native pipeline"
    );
}
{
    var (p, b) = Setup(false, false);
    p.Settings.GrenadeChoice.Enabled = false;
    Check(
        p.Find(b) && p.Count == 1 && b.BotOwner.WeaponManager.Grenades.Selections == 0,
        "G03 disabled leaves item unchanged"
    );
}
{
    var (p, b) = Setup(false, false);
    b.Player.Environment = EnvironmentType.Indoor;
    b.BotOwner.WeaponManager.Grenades.AIGreanageThrowData!.Force = 9;
    Check(
        Ready(p, b) && p.State(b).GrenadeChoice.Item == "short",
        "source indoor preference has no added flight-time margin"
    );
    Check(
        p.State(b)
            .GrenadeChoice.Trace!.Alternatives.Any(a =>
                a.Item == "short" && a.Reason != "insufficient-arrival-margin"
            ),
        "rejected alternative reason recorded"
    );
}
foreach (bool visible in new[] { false, true })
foreach (bool targetIndoors in new[] { true, false })
foreach (var (sainIndoors, eftIndoors, preferUnderRoof, target, item, reason) in new[]
{
    (false, false, true, new Vector3(25, 0, 0), "long", "long-outdoors"),
    (true, false, true, new Vector3(25, 0, 0), "short", "short-indoors"),
    (false, true, true, new Vector3(25, 0, 0), "short", "short-indoors"),
    (true, true, false, new Vector3(25, 0, 0), "long", "long-outdoors"),
    (true, true, false, new Vector3(19.99f, 0, 0), "short", "short-close"),
    (false, false, true, new Vector3(16, 12, 0), "long", "long-outdoors"), // exactly 20 m in 3D
})
{
    var (p, b) = Setup(false, false);
    b.Memory.Location.IsIndoors = sainIndoors;
    b.Player.Environment = eftIndoors ? EnvironmentType.Indoor : EnvironmentType.Outdoor;
    b.GoalEnemy!.IsVisible = visible;
    b.GoalEnemy.EnemyPlayer.Environment = targetIndoors ? EnvironmentType.Indoor : EnvironmentType.Outdoor;
    b.GoalEnemy.KnownPlaces.LastKnownPlace!.Position = target;
    p.Settings.GrenadeChoice.PreferShortIndoors = preferUnderRoof;
    var grenades = b.BotOwner.WeaponManager.Grenades;
    grenades.AIGreanageThrowData!.Target = target;
    Check(Ready(p, b) && grenades.grenade!.Id == item && p.State(b).GrenadeChoice.Trace!.Preference == reason,
        $"fuse for SAIN roof={sainIndoors}, EFT roof={eftIndoors}, roof setting={preferUnderRoof}, " +
        $"target indoors={targetIndoors}, visible={visible}, distance={target.magnitude}");
    var selection = p.Events.Select(Newtonsoft.Json.Linq.JObject.Parse)
        .Single(ev => (string?)ev["feature"] == "G03" && (string?)ev["grenadeChoice"]?["state"] == "selected")
        ["grenadeChoice"]!;
    Check((bool)selection["throwerUnderRoof"]! == (sainIndoors || eftIndoors)
        && Math.Abs((double)selection["distance"]! - target.magnitude) < .001,
        "the selection event records the thrower's roof and the 3D arc distance");
}
{
    var (p, b) = Setup(false, false);
    p.Settings.GrenadeChoice.ShortFuseDistance = 30;
    b.BotOwner.WeaponManager.Grenades.AIGreanageThrowData!.Target = new(25, 0, 0);
    Check(Ready(p, b) && p.State(b).GrenadeChoice.Trace!.Preference == "short-close"
        && p.State(b).GrenadeChoice.Item == "short", "the configured distance still applies outdoors");
}
{
    var (p, b) = Setup(false, false);
    var grenades = b.BotOwner.WeaponManager.Grenades;
    var arc = grenades.AIGreanageThrowData!;
    arc.Target = new(25, 0, 0);
    b.GoalEnemy!.KnownPlaces.LastKnownPlace!.Position = arc.Target;
    Check(Ready(p, b) && grenades.grenade!.Id == "long", "prepare the outdoor throw");
    b.GoalEnemy.EnemyPlayer.Environment = EnvironmentType.Indoor;
    p.Tick(b, 10.1);
    Check(p.Find(b) && grenades.grenade!.Id == "long" && grenades.Selections == 1,
        "a target entering a building does not change the prepared fuse");
    b.Memory.Location.IsIndoors = true;
    p.Tick(b, 10.2);
    Check(p.Find(b) && grenades.grenade!.Id == "short" && grenades.Selections == 2,
        "moving under a roof updates the choice within the same opportunity");
    b.Memory.Location.IsIndoors = false;
    p.Tick(b, 10.3);
    Check(p.Find(b) && grenades.grenade!.Id == "long", "leaving the roof restores the long choice");
    arc.Position = new(6, 0, 0);
    p.Tick(b, 10.4);
    Check(p.Find(b) && grenades.grenade!.Id == "short", "a new arc starting 19 m from the target prefers short");
    Check(p.Throw(b) && p.Requests == 1, "the updated choice is accepted by the native throw path");
}
{
    var (p, b) = Setup();
    b.Player.Environment = EnvironmentType.Indoor;
    Check(Ready(p, b), "choice bound to prepared G02 arc");
    Check(
        p.State(b).GrenadeTrajectory.Item == "short"
            && p.State(b).GrenadeJudgment.ArcItem == "short",
        "intentional selection updates arc item fingerprints"
    );
    int draws = p.Draws;
    double? roll = p.State(b).GrenadeJudgment.Policy.Trace!.Roll;
    p.Tick(b, Time.timeAsDouble + .1);
    Check(
        p.Find(b) && p.Draws == draws && p.State(b).GrenadeJudgment.Policy.Trace!.Roll == roll,
        "item choice creates no extra G01 roll"
    );
    b.BotOwner.WeaponManager.Grenades.ThrowindNow = true;
    int selections = b.BotOwner.WeaponManager.Grenades.Selections;
    Check(
        !p.Find(b) && b.BotOwner.WeaponManager.Grenades.Selections == selections,
        "no swap during animation"
    );
}
mutation = 0;
foreach (
    var changed in new Action<Plugin, BotComponent>[]
    {
        (p, b) =>
        {
            for (int i = 0; i < 33; i++)
                b.Player.Inventory.Items.Add(new ThrowWeap { Id = "extra" + i });
        },
        (p, b) =>
        {
            for (int i = 0; i < 256; i++)
                b.Player.Inventory.Items.Add(new Item());
        },
        (p, b) => b.Player.Inventory.Items.Add(new ThrowWeap { Id = "short" }),
        (p, b) =>
        {
            foreach (var item in b.Player.Inventory.Items.OfType<ThrowWeap>())
                item.GetExplDelay = 1;
        },
    }
)
{
    var (p, b) = Setup(false, false);
    changed(p, b);
    mutation++;
    bool ready = Ready(p, b);
    Check(mutation <= 2 ? ready : !ready, $"inventory source selection case {mutation}");
}
{
    var (p, b) = Setup(false, false);
    Check(Ready(p, b), "owned grenade prepared against hostile");
    ContactSafety.Allowed = false;
    Check(
        !p.Throw(b) && p.Requests == 0 && !p.State(b).GrenadeJudgment.Arc,
        "contact becoming squadmate blocks prepared throw"
    );
    Check(!p.Find(b), "own squad cannot start another grenade plan");
    ContactSafety.Allowed = true;
}
Console.WriteLine(
    $"G03 production adapter harness: {checks} checks passed (native contracts, not Unity/Fika raid)."
);
