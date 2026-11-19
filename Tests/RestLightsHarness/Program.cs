using Comet.Client;
using Comet.Core;
using EFT;
using EFT.InventoryLogic;
using Newtonsoft.Json.Linq;
using SAIN;
using SAIN.Components;
using SAIN.SAINComponent.Classes.WeaponFunction;
using UnityEngine;

Plugin.Patch();
int n = 0;
var events = new List<string>();
void C(bool v, string why)
{
    n++;
    if (!v)
        throw new Exception("L03 adapter: " + why);
}
(Plugin p, BotComponent b, BotLightController l, Player.FirearmController h) Setup(int count = 2)
{
    Time.time = Time.realtimeSinceStartup = 10;
    Orbit.Api.OrbitTelemetry.Value = new();
    var p = new Plugin();
    var b = new BotComponent { SAINLayersActive = false, GoalEnemy = null };
    b.BotOwner.Memory.GoalEnemy = null;
    b.Player.ProfileId = b.ProfileId;
    SAINEnableClass.Bots.Clear();
    SAINEnableClass.Bots[b.ProfileId] = b;
    var h = new Player.FirearmController { _player = b.Player };
    b.Player.HandsController = h;
    b.BotOwner.WeaponManager.CurrentWeapon = h.Item;
    for (int i = 0; i < count; i++)
    {
        var d = new LightComponent { IsActive = true, SelectedMode = i + 2 };
        d.Item.ShortName = i % 2 == 0 ? "test-device" : "test-laser";
        h.Item.Mods.Add(d.Item);
    }
    return (p, b, new(b), h);
}
void Tick(BotLightController l, float dt = 0)
{
    Time.time += dt;
    Time.realtimeSinceStartup = Time.time;
    l.Tick();
}
void Save(Plugin p) =>
    events.AddRange(
        p.diagnostics!.Lines.Where(x => JObject.Parse(x)["feature"]!.Value<string>() == "L03")
    );

foreach (int count in new[] { 0, 1, 2, 5, 16 })
{
    var (p, b, l, h) = Setup(count);
    Tick(l);
    C(h.Commands.Count == 0, "first sample waits");
    Tick(l, 1.99f);
    C(h.Commands.Count == 0, "two seconds required");
    Tick(l, .26f);
    C(h.Commands.Count == (count == 0 ? 0 : 1), "one batch");
    C(h.Item.Mods.All(x => !x.Light!.IsActive), "devices off");
    C(
        h.Item.Mods.Select(x => x.Light!.SelectedMode).SequenceEqual(Enumerable.Range(2, count)),
        "modes unchanged"
    );
    Save(p);
}
{
    var (p, b, l, h) = Setup();
    Tick(l);
    Tick(l, 2.1f);
    h.Item.Mods[0].Light!.IsActive = true;
    Tick(l, 1);
    C(h.Item.Mods[0].Light!.IsActive, "native ten second throttle");
    Tick(l, 9.1f);
    C(
        !h.Item.Mods[0].Light!.IsActive && h.Commands.Count == 2,
        "reapply source darkness in same pause"
    );
    Save(p);
}
{
    var (p, b, l, h) = Setup();
    b.SAINLayersActive = true;
    Tick(l);
    Tick(l, 3);
    C(h.Commands.Count == 0, "SAIN active is not idle");
    b.SAINLayersActive = false;
    Tick(l);
    Tick(l, 2.1f);
    C(h.Commands.Count == 1, "idle after layer exit");
    Save(p);
}
Console.WriteLine($"L03 source adapter: {n} checks passed");
