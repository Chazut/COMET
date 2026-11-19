using Comet.Client;
using Newtonsoft.Json.Linq;
using SAIN.SAINComponent.Classes.Info;
using SAIN.SAINComponent.Classes.WeaponFunction;
using UnityEngine;

int checks = 0;
void Check(bool ok, string why)
{
    checks++;
    if (!ok)
        throw new Exception(why);
}
(Plugin, Recoil) Setup()
{
    Time.timeAsDouble = 10;
    Time.fixedDeltaTime = .02f;
    SAIN.SAINPlugin.LoadedPreset.GlobalSettings.Shoot.BOT_RECOIL_DECAY_COEF = 1;
    return (new(), new());
}
foreach (
    var exclude in new Action<Plugin, Recoil>[]
    {
        (p, r) => p.Settings.Enabled = false,
        (p, r) => p.Settings.RecoilRecovery.Enabled = false,
        (p, r) => p.Settings.RecoilRecovery.DmrMultiplier = 1,
        (p, r) => r.Bot.Pmc = false,
        (p, r) => p.Authority = false,
        (p, r) => p.IsGhost = true,
        (p, r) => r.Bot.IsDead = true,
        (p, r) => r.Bot.GameEnding = true,
        (p, r) => r.Bot.BotActive = false,
        (p, r) => r.Bot.IsCheater = true,
        (p, r) => r.Bot.SAINLayersActive = false,
        (p, r) => r.Bot.Info.WeaponInfo!.EWeaponClass = WeaponKind.assaultRifle,
        (p, r) => r.Bot.Player.HandsController = null,
        (p, r) => r.Bot.Info.WeaponInfo!.Weapon!.Id = "other",
    }
)
{
    var (p, r) = Setup();
    exclude(p, r);
    p.Shot(r, 4, -2);
    float h = 4,
        v = -2;
    bool done = false;
    Check(
        p.Decay(r, ref h, ref v, ref done) && h == 4 && v == -2,
        "excluded adapter leaves original method/fields intact"
    );
    Check(p.Count == 0 && p.Events.Count == 0, "excluded no state or diagnostics");
}
{
    var (p, r) = Setup();
    float h = 4,
        v = -2;
    bool done = false;
    p.Shot(r, h, v);
    Check(
        h == 4 && v == -2 && p.Runtime(r.Bot).Trace!.Shots == 1,
        "shot only observes native initial offset"
    );
    Check(
        !p.Decay(r, ref h, ref v, ref done) && h == 4 && v == -2,
        "same time skips original without extra decay"
    );
    Time.timeAsDouble += .02;
    Check(!p.Decay(r, ref h, ref v, ref done), "one replacement step, original skipped");
    Check(Math.Abs(h - 3.76) < 1e-5 && Math.Abs(v + 1.88) < 1e-5, "not double-decayed");
    Check(
        !p.Decay(r, ref h, ref v, ref done) && Math.Abs(h - 3.76) < 1e-5,
        "duplicate callback no extra step"
    );
    p.Settings.RecoilRecovery.DmrMultiplier = 1;
    Check(p.Decay(r, ref h, ref v, ref done), "live x1 yields original");
    Check(
        !p.Runtime(r.Bot).Active && p.Runtime(r.Bot).Trace!.State == "ended",
        "x1 closes episode"
    );
}
foreach (
    var interrupt in new Action<Plugin, Recoil>[]
    {
        (p, r) => p.IsGhost = true,
        (p, r) => p.Authority = false,
        (p, r) => r.Bot.IsDead = true,
        (p, r) => p.Settings.RecoilRecovery.Enabled = false,
        (p, r) => ((EFT.Player.FirearmController)r.Bot.Player.HandsController!).Item!.Id = "new",
        (p, r) => r.Bot.Info.WeaponInfo!.EWeaponClass = WeaponKind.pistol,
        (p, r) => Time.timeAsDouble += 1,
        (p, r) => Time.timeAsDouble -= 1,
    }
)
{
    var (p, r) = Setup();
    p.Shot(r, 4, -2);
    interrupt(p, r);
    float h = 4,
        v = -2;
    bool done = false;
    Check(
        p.Decay(r, ref h, ref v, ref done) && h == 4 && v == -2 && !p.Runtime(r.Bot).Active,
        "interrupt returns native untouched, no catch-up"
    );
}
foreach (int fps in new[] { 30, 60, 144 })
{
    var (p, r) = Setup();
    float h = 8,
        v = -4;
    bool done = false;
    p.Shot(r, h, v);
    for (int i = 1; i <= fps; i++)
    {
        Time.timeAsDouble = 10 + i / (double)fps;
        Check(!p.Decay(r, ref h, ref v, ref done), "live adapter owns step");
    }
    Check(
        Math.Abs(h - 8 * Math.Pow(.94, 50)) < 2e-5,
        "adapter same real time at different frequencies"
    );
    Check(p.Events.Count <= 3, "telemetry bounded to two per second plus start");
}
{
    var (p, r) = Setup();
    r.Bot.Info.WeaponInfo!.EWeaponClass = WeaponKind.sniperRifle;
    p.Settings.RecoilRecovery.SniperMultiplier = 2;
    float h = -4,
        v = -2;
    bool done = false;
    p.Shot(r, h, v);
    Time.timeAsDouble += .02;
    p.Decay(r, ref h, ref v, ref done);
    Check(Math.Abs(h + 3.84) < 1e-5 && !done, "sniper independent multiplier and negative offsets");
    p.Release(r.Bot);
    Check(
        !p.Runtime(r.Bot).Active && p.Runtime(r.Bot).Weapon == "",
        "raid release clears episode identity"
    );
    Check(p.Decay(r, ref h, ref v, ref done), "release cannot revive old episode");
}
{
    var (p, r) = Setup();
    float h = .0009f,
        v = -.0009f;
    bool done = false;
    p.Shot(r, h, v);
    Time.timeAsDouble += .02;
    p.Decay(r, ref h, ref v, ref done);
    Check(done && h == 0 && v == 0 && !p.Runtime(r.Bot).Active, "settled release");
    p.Shot(r, 4, -2);
    Check(
        p.Runtime(r.Bot).Active && p.Runtime(r.Bot).Trace!.ValidFor == .75,
        "next shot starts fresh lease"
    );
}
{
    var (p, r) = Setup();
    p.Shot(r, 4, -2);
    p.LogThrows = true;
    Time.timeAsDouble += .2;
    float h = 4,
        v = -2;
    bool done = false;
    // Force a telemetry sample after mutation to prove an exception cannot run native decay a second time.
    p.Runtime(r.Bot).LogAt = 0;
    Check(!p.Decay(r, ref h, ref v, ref done), "fault after applied step must still skip native");
    Check(
        p.Runtime(r.Bot).Fault && !p.Runtime(r.Bot).Active,
        "fault releases and disables only this adapter"
    );
    Check(p.Decay(r, ref h, ref v, ref done), "later faulted steps are native");
}
{
    var (p, r) = Setup();
    float h = 4,
        v = -2;
    bool done = false;
    p.Shot(r, h, v);
    for (int i = 1; i <= 60; i++)
    {
        Time.timeAsDouble = 10 + i / 60d;
        if (i % 6 == 0)
            p.Shot(r, 4, -2);
        p.Decay(r, ref h, ref v, ref done);
    }
    Check(p.Events.Count <= 3, "rapid shots coalesce observations");
    Check(p.Runtime(r.Bot).Trace!.Shots == 11, "coalescing retains shot counter");
    p.Release(r.Bot);
    var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts"));
    Directory.CreateDirectory(root);
    File.WriteAllText(
        Path.Combine(root, "w09-adapter-traces.json"),
        new JArray(p.Events.Select(JObject.Parse)).ToString()
    );
}
Console.WriteLine(
    $"W09 production adapter harness: {checks} checks passed (detached SAIN/Unity shims)"
);
