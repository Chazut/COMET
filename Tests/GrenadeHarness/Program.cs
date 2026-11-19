using Comet.Client;
using EFT;
using EFT.InventoryLogic;
using Newtonsoft.Json.Linq;
using SAIN.Components;
using UnityEngine;

int checks = 0;
void Check(bool ok, string why)
{
    checks++;
    if (!ok)
        throw new Exception("G01 adapter: " + why);
}
(Plugin, BotComponent) Setup()
{
    var p = new Plugin();
    var b = new BotComponent();
    p.Tick(b, 10);
    return (p, b);
}
foreach (
    var exclude in new Action<Plugin, BotComponent>[]
    {
        (p, b) => p.Settings.Enabled = false,
        (p, b) => p.Settings.GrenadeJudgment.Enabled = false,
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
    Check(p.Count == 0 && p.Find(b) && p.Throw(b), "excluded bots retain native pipeline");
    Check(p.Draws == 0, "excluded bots consume no roll");
}
{
    var (p, b) = Setup();
    Check(p.Find(b), "eligible native trajectory");
    int n = p.Draws;
    Check(n == 2, "one roll plus retry jitter");
    Check(p.Find(b) && p.Draws == n, "same frame preparation reused");
    Check(
        p.Throw(b) && p.Draws == n && p.State(b).GrenadeJudgment.Policy.Pending,
        "execution rechecks same roll"
    );
    Check(!p.State(b).GrenadeJudgment.Policy.Trace!.Released, "acceptance is not projectile");
    p.Projectile(b, "other");
    Check(!p.State(b).GrenadeJudgment.Policy.Trace!.Released, "foreign projectile ignored");
    p.Projectile(b);
    Check(p.State(b).GrenadeJudgment.Policy.Trace!.Released, "real matching projectile");
    p.Tick(b, 11);
    Check(p.Find(b), "source permits a later roll on the same known contact");
    p.End(b);
    Check(
        p.State(b).GrenadeJudgment.Policy.Count == 0 && !p.State(b).GrenadeJudgment.Arc,
        "raid clear removes history and cache"
    );
    File.WriteAllLines(
        Path.GetFullPath(
            Path.Combine(
                AppContext.BaseDirectory,
                "../../../../../artifacts/g01-adapter-events.jsonl"
            )
        ),
        p.Events
    );
}
foreach (
    var change in new Action<Plugin, BotComponent>[]
    {
        (p, b) => b.BotOwner.Memory.IsUnderFire = true,
        (p, b) => p.State(b).Fake.CallbackPending = true,
        (p, b) => b.EnemyController.VisibleEnemies.Add(new()),
        (p, b) => b.BotOwner.WeaponManager.Grenades.grenade!.IsDummy = true,
        (p, b) => b.BotOwner.WeaponManager.Grenades.grenade!.ThrowType = "smoke_grenade",
        (p, b) => b.GoalEnemy!.KnownPlaces.TimeLastKnownUpdated = -100,
    }
)
{
    var (p, b) = Setup();
    Check(p.Find(b), "initial permission");
    change(p, b);
    int n = p.Draws;
    Check(
        !p.Throw(b) && p.Requests == 0 && p.Draws == n,
        "source safety rechecked at throw without reroll"
    );
}

{
    var (p, b) = Setup();
    Check(!p.Find(b, false) && p.Draws == 0, "native arc failed without roll");
    p.Tick(b, 11);
    Check(!p.Find(b) && p.Draws == 0, "no arc retry interval");
    p.Tick(b, 11.5);
    Check(p.Find(b), "native retry after delay");
    var second = new BotComponent { ProfileId = "second" };
    p.Tick(b, 12);
    Check(p.Find(b), "first full probe of frame");
    Check(
        !p.Find(second) && p.State(second).GrenadeJudgment.Policy.Trace == null,
        "global one trajectory probe per frame"
    );
}
{
    var (p, b) = Setup();
    b.GoalEnemy!.EnemyPlayer.Environment = EnvironmentType.Indoor;
    p.Tick(b, 10.5);
    Check(
        p.Find(b) && p.State(b).GrenadeJudgment.Policy.Trace!.RoomBonus == 10,
        "visible indoor evidence"
    );
    b.GoalEnemy.IsVisible = false;
    b.GoalEnemy.EnemyPlayer.Environment = EnvironmentType.Outdoor;
    p.Tick(b, 11);
    p.Find(b);
    Check(
        p.State(b).GrenadeJudgment.Policy.Trace!.RoomBonus == 0,
        "source reads current enemy environment without extra personal-evidence gate"
    );
    p.State(b).Opportunity.Value.State = "assess";
    p.State(b).Opportunity.Value.Cue = "heal";
    p.Tick(b, 11.5);
    p.Find(b);
    Check(
        p.State(b).GrenadeJudgment.Policy.Trace!.VulnerableBonus == 0,
        "no added opportunity bonus from A10"
    );
    p.State(b).Opportunity.Value.State = "expired";
    p.Tick(b, 12);
    p.Find(b);
    Check(
        p.State(b).GrenadeJudgment.Policy.Trace!.VulnerableBonus == 0,
        "expired cue earns no bonus"
    );
}
{
    var (p, b) = Setup();
    p.Find(b);
    double baseline = p.State(b).GrenadeJudgment.Policy.Trace!.Chance;
    b.Player.Inventory.Items.RemoveAt(1);
    p.Find(b);
    Check(
        p.State(b).GrenadeJudgment.Policy.Trace!.Chance == baseline,
        "source has no last-grenade conservation penalty"
    );
    p.Throw(b, false);
    Check(
        p.State(b).GrenadeJudgment.Policy.Trace!.State == "refused"
            && !p.State(b).GrenadeJudgment.Policy.Pending,
        "native refusal tracked"
    );
}
{
    var (p, b) = Setup();
    p.Find(b);
    p.Throw(b, true, true);
    Check(
        p.State(b).GrenadeJudgment.Policy.Trace!.State == "released",
        "synchronous release not overwritten by return"
    );
}
{
    var (p, b) = Setup();
    p.Find(b);
    p.Throw(b);
    b.GoalEnemy = null;
    p.Tick(b, 21);
    Check(
        p.State(b).GrenadeJudgment.Policy.Trace!.State == "timeout"
            && !p.State(b).GrenadeJudgment.Policy.Pending,
        "request timeout tracked even without a current enemy"
    );
}
{
    var (p, b) = Setup();
    p.Find(b);
    Check(
        p.Throw(b, true, false, false),
        "diagnostic observer preserves native true return even without visible start"
    );
    Check(
        p.State(b).GrenadeJudgment.Policy.Trace!.State == "unconfirmed"
            && !p.State(b).GrenadeJudgment.Policy.Trace!.Released,
        "no native start never becomes a confirmed projectile"
    );
    b.BotOwner.WeaponManager.Grenades.ThrowindNow = true;
    p.Tick(b, 10.3);
    Check(
        p.State(b).GrenadeJudgment.Policy.Trace!.NativeStarted
            && p.State(b).GrenadeJudgment.Policy.Trace!.State == "accepted",
        "delayed native start updates pending request without claiming release"
    );
    p.Projectile(b);
    Check(
        p.State(b).GrenadeJudgment.Policy.Trace!.State == "released",
        "delayed projectile can confirm an unobserved start"
    );
}
{
    var (p, b) = Setup();
    p.Find(b);
    p.Throw(b);
    b.BotOwner.WeaponManager.Grenades.ThrowindNow = false;
    p.Tick(b, 11);
    Check(
        p.State(b).GrenadeJudgment.Policy.Trace!.State == "interrupted",
        "native throw ending without projectile is explicit"
    );
    p.Projectile(b);
    Check(
        p.State(b).GrenadeJudgment.Policy.Trace!.Released,
        "interruption diagnostic does not swallow later release callback"
    );
}
{
    var (p, b) = Setup();
    p.Find(b);
    p.Throw(b);
    p.BrokenObserver(b);
    Check(
        !p.State(b).GrenadeJudgment.Fault && p.State(b).GrenadeJudgment.Policy.Pending,
        "failed observer never faults tactical adapter or clears pending request"
    );
    Check(p.State(b).GrenadeJudgment.ObservationWarned, "observer error emits bounded warning");
    p.Projectile(b);
    Check(
        p.State(b).GrenadeJudgment.Policy.Trace!.Released,
        "projectile observation survives a failed native state read"
    );
    p.Tick(b, 12);
    Check(p.Find(b), "next native grenade remains available after observation error");
}
Console.WriteLine(
    $"G01 adapter: {checks} checks passed (native contracts substituted; no Unity raid)"
);
