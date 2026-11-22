using Comet.Client;
using Comet.Core;
using EFT.InventoryLogic;
using SAIN.Components;
using UnityEngine;

int checks = 0;
void Check(bool ok, string why)
{
    checks++;
    if (!ok)
        throw new Exception("G07: " + why);
}
string wire = Path.GetFullPath(
    Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts/g07-adapter-events.jsonl")
);
File.WriteAllText(wire, "");
ThrowWeap Frag(string id = "frag") => new() { Id = id, GetExplDelay = 5 };
ThrowWeap Smoke() =>
    new()
    {
        Id = "smoke",
        TemplateId = "mod-smoke",
        ThrowType = "smoke_grenade",
        EmitTime = 90,
        MaxExplosionDistance = 0,
        FragmentsCount = 0,
        IsDummy = true,
    };
ThrowWeap Flash() =>
    new()
    {
        Id = "flash",
        TemplateId = "mod-flash",
        ShortName = "Flash",
        ThrowType = "flash_grenade",
        GetExplDelay = 2,
        MaxExplosionDistance = 0,
        FragmentsCount = 0,
        Blindness = new(0, 20, 0),
    };
(Plugin, BotComponent) Setup(params ThrowWeap[] items)
{
    var p = new Plugin();
    Physics.BlastShield = true;
    p.Settings.GrenadeFilter.Enabled = true;
    var b = new BotComponent();
    b.Player.Inventory.Items.Clear();
    b.Player.Inventory.Items.AddRange(items);
    b.BotOwner.WeaponManager.Grenades.grenade = items.FirstOrDefault();
    b.BotOwner.WeaponManager.Grenades.AIGreanageThrowData!.Target = new(10, 0, 0);
    b.GoalEnemy!.KnownPlaces.LastKnownPlace!.Position = new(10, 0, 0);
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
            p.State(b).GrenadeFilter.Trace?.State == "blocked"
            || p.State(b).GrenadeChoice.Trace?.State == "blocked"
            || p.State(b).GrenadeTrajectory.Trace?.State == "blocked"
        )
            return false;
        p.Tick(b, Time.timeAsDouble + .01);
    }
    return false;
}
void Save(Plugin p) => File.AppendAllLines(wire, p.Events);
foreach (bool impact in new[] { false, true })
{
    var native = Frag();
    native.GetExplDelay = impact ? 3 : 2;
    native.MinTimeToContactExplode = impact ? 0 : -1;
    var (p, b) = Setup(native, Smoke());
    Check(Ready(p, b) && p.State(b).GrenadeChoice.Trace!.Reason == "choice-native-fuse-retained",
        "G03 defers to the native frag while G07 excludes the smoke");
    p.Tick(b, Time.timeAsDouble + .01);
    Check(p.NativeThrow(b) && p.Throw(b) && ReferenceEquals(b.BotOwner.WeaponManager.Grenades.grenade, native),
        "the G07 execution guard accepts the retained frag");
    Save(p);
}
foreach (bool g01 in new[] { false, true })
foreach (bool g02 in new[] { false, true })
foreach (bool g03 in new[] { false, true })
{
    var (p, b) = Setup(Smoke(), Frag(), Flash());
    p.Settings.GrenadeJudgment.Enabled = g01;
    p.Settings.GrenadeTrajectory.Enabled = g02;
    p.Settings.GrenadeChoice.Enabled = g03;
    p.FilterPatch();
    Check(Ready(p, b), $"replacement ready G01={g01}/G02={g02}/G03={g03}");
    Check(
        b.BotOwner.WeaponManager.Grenades.grenade!.Id == "frag",
        "actual carried frag replaces excluded smoke"
    );
    Check(p.NativeThrow(b), "native final guard accepts prepared carried frag");
    p.Tick(b, Time.timeAsDouble + .01);
    Check(p.Throw(b), $"SAIN final guard agrees G01={g01}/G02={g02}/G03={g03}");
    Check(
        p.State(b).GrenadeFilter.Trace!.Alternatives.Single(x => x.Item == "smoke").Reason
            == "filter-smoke-not-offensive",
        "logs excluded smoke"
    );
    p.End(b);
    Check(
        p.State(b).GrenadeFilter.Trace!.State == "ended"
            && p.State(b).GrenadeFilter.BoundItem == "",
        "cleanup expires binding"
    );
    Save(p);
}
{
    var (p, b) = Setup(Smoke(), Flash());
    Check(Ready(p, b), "safe flash fallback with default G01/G02/G03 enabled");
    Check(
        p.State(b).GrenadeChoice.Trace!.Preference == "flash-fallback",
        "flash is fallback, not frag fuse preference"
    );
    Check(
        p.State(b).GrenadeChoice.Trace!.Fuse == 2 && p.State(b).GrenadeChoice.Trace!.Margin >= .75,
        "2 second flash passes real flight margin without 2.5 second frag minimum"
    );
    p.Tick(b, Time.timeAsDouble + .01);
    Check(p.NativeThrow(b) && p.Throw(b), "flash accepted at execution");
    Save(p);
}
foreach (
    var edit in new Action<Plugin, BotComponent>[]
    {
        (p, b) => b.BotOwner.WeaponManager.Grenades.grenade = Smoke(),
        (p, b) => b.Player.Inventory.Items.Clear(),
        (p, b) => b.Player.Inventory.Items.Add(Frag()),
        (p, b) => b.BotOwner.WeaponManager.Grenades.grenade!.EmitTime = 10,
        (p, b) => b.BotOwner.WeaponManager.Grenades.grenade!.MaxExplosionDistance = float.NaN,
        (p, b) =>
            b.BotOwner.WeaponManager.Grenades.AIGreanageThrowData!.GrenadeType = "flash_grenade",
        (p, b) => b.BotOwner.WeaponManager.Grenades.grenade = Frag("changed"),
    }
)
{
    var (p, b) = Setup(Frag());
    Check(Ready(p, b), "prepared before mutation");
    edit(p, b);
    Check(!p.NativeThrow(b), "final native guard rejects changed item/effect/type/inventory");
    Check(
        p.State(b).GrenadeFilter.Trace!.Item == b.BotOwner.WeaponManager.Grenades.grenade!.Id,
        "rejection describes the actual current item"
    );
    Save(p);
}
{
    var (p, b) = Setup(Flash());
    p.Settings.GrenadeFilter.AllowFlash = false;
    Check(!Ready(p, b), "disabled flash blocked");
    Save(p);
    var (q, c) = Setup(Flash());
    Check(Ready(q, c), "flash before settings change");
    q.Settings.GrenadeFilter.AllowFlash = false;
    Check(!q.NativeThrow(c), "flash toggle rechecked before execution");
}
foreach (
    var item in new[]
    {
        Smoke(),
        new ThrowWeap { ThrowType = "gas_grenade" },
        new ThrowWeap { EmitTime = 10 },
        new ThrowWeap { ThrowType = "custom" },
        new ThrowWeap { IsDummy = true },
    }
)
{
    var (p, b) = Setup(item);
    Check(!Ready(p, b), "excluded-only inventory cannot throw");
    Save(p);
}
{
    var (p, b) = Setup(Flash());
    b.BotOwner.WeaponManager.Grenades.grenade!.GetExplDelay = 1;
    Check(Ready(p, b), "source flash selection does not add a flight-margin veto");
    Save(p);
    var (q, c) = Setup(Flash(), Frag());
    Check(
        Ready(q, c) && c.BotOwner.WeaponManager.Grenades.grenade!.Id == "frag",
        "G03 prefers suitable frag over current flash"
    );
    var (r, d) = Setup(Frag(), Flash());
    d.Player.Inventory.Items.OfType<ThrowWeap>().First().GetExplDelay = 1;
    Check(
        Ready(r, d) && d.BotOwner.WeaponManager.Grenades.grenade!.Id == "flash",
        "unsafe frag does not hide suitable flash"
    );
    Save(r);
}
foreach (
    var exclude in new Action<Plugin, BotComponent>[]
    {
        (p, b) => p.Settings.Enabled = false,
        (p, b) => p.Settings.GrenadeFilter.Enabled = false,
        (p, b) => p.Authority = false,
        (p, b) => p.IsGhost = true,
        (p, b) => b.Pmc = false,
        (p, b) => b.IsDead = true,
        (p, b) => b.GameEnding = true,
    }
)
{
    var (p, b) = Setup(Smoke());
    exclude(p, b);
    Check(
        p.FilterPrepare(b) && p.NativeThrow(b) && b.BotOwner.WeaponManager.Grenades.Selections == 0,
        "out-of-scope native behavior untouched"
    );
}
{
    var (p, b) = Setup(Frag());
    Check(!p.NativeThrow(b), "unprepared direct offensive request fails closed");
    Check(p.UnrelatedNativeThrow(), "unrelated native controller left untouched");
    b.BotOwner.WeaponManager.Grenades.ThrowindNow = true;
    Check(
        !p.FilterPrepare(b) && b.BotOwner.WeaponManager.Grenades.Selections == 0,
        "hands in progress never swapped"
    );
}
{
    var (p, b) = Setup(Flash());
    Physics.BlastShield = false;
    Check(Ready(p, b), "source selection does not add a separate blast-shield veto");
    var (q, c) = Setup(Frag());
    Check(
        q.FilterPrepare(c) && !q.NativeThrow(c),
        "category selection alone does not bind an unprepared arc"
    );
}
{
    var (p, b) = Setup(Frag());
    Check(Ready(p, b), "ready for diagnostics throttle");
    int n = p.Events.Count(x => x.Contains("\"feature\":\"G07\""));
    for (int i = 0; i < 50; i++)
        p.Find(b);
    Check(
        p.Events.Count(x => x.Contains("\"feature\":\"G07\"")) == n,
        "same frame repeated searches do not spam G07 logs"
    );
}
Console.WriteLine(
    $"G07 production adapter harness: {checks} checks passed (native contracts, not Unity/Fika raid)."
);
