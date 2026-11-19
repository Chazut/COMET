using BepInEx.Bootstrap;
using Comet.Client;
using Manimal.CSGas.Gas;
using SAIN.Components;
using SAIN.SAINComponent.SubComponents;
using UnityEngine;
using UnityEngine.AI;

int checks = 0;
void C(bool v, string why)
{
    checks++;
    if (!v)
        throw new Exception(why);
}
var streams = new List<string>();
(Plugin, BotComponent, CsGasCloud) Setup(Version? version = null)
{
    Time.Set(10);
    Physics.Wall = false;
    NavMesh.Calls = 0;
    Chainloader.PluginInfos.Clear();
    Chainloader.PluginInfos.Add(
        ManimalGasBridge.Guid,
        new() { Metadata = new() { Version = version ?? new(2, 0, 0) } }
    );
    var p = new Plugin();
    var b = new BotComponent();
    var c = new CsGasCloud();
    Plugin.GasCloudCreated(c);
    return (p, b, c);
}
void Step(Plugin p, BotComponent b, float t)
{
    Time.Set(t);
    p.Tick(b);
    p.Filter(b);
    C(!p.Act(b), "gas never owns a custom escape action");
}
void Expose(Plugin p, BotComponent b)
{
    for (int i = 0; i <= 12; i++)
        Step(p, b, 15 + i * .25f);
}
Chainloader.PluginInfos.Clear();
var absent = new Plugin();
C(!absent.Ready, "absent dormant");
var x = new BotComponent();
absent.Tick(x);
C(absent.states.Count == 0, "absent no bot bookkeeping");
Chainloader.PluginInfos[ManimalGasBridge.Guid] = new()
{
    Metadata = new() { Version = new(2, 1, 0) },
};
C(!new Plugin().Ready, "unknown future version inactive");
Chainloader.PluginInfos[ManimalGasBridge.Guid] = new() { Instance = null! };
C(!new Plugin().Ready, "failed provider dormant");
var (p, b, c) = Setup();
Step(p, b, 10);
Step(p, b, 14.75f);
C(p.State(b).Inside && p.State(b).Intensity == 0, "source five-second exposure delay");
Step(p, b, 15);
C(Math.Abs(p.State(b).Intensity - .0625) < .00001, "source quarter-second ramp after delay");
for (int i = 1; i <= 5; i++)
    Step(p, b, 15 + i * .25f);
C(p.FlashCalls == 1 && b.Flashed.IsFlashed, "source threshold .35 starts native blindness");
C(b.BotOwner.Settings.Current.Active.Count == 1, "gradual modifiers active");
C(b.Talk.Count == 1, "source cough");
Step(p, b, 17);
C(p.FlashCalls == 1, "blind refresh not before two seconds");
Step(p, b, 18.25f);
C(p.FlashCalls == 2, "blind refresh after two seconds");
C(NavMesh.Calls == 0 && b.Mover.Requests == 0, "no extra gas escape/path checks");
streams.AddRange(p.diagnostics!.Lines);
b.Player.Protection = "Respirator";
Step(p, b, 18.5f);
C(p.State(b).Modifier?.Hearing == 1, "provider respirator protects hearing");
b.Player.Protection = "Full";
for (int i = 1; i <= 40; i++)
    Step(p, b, 18.5f + i * .25f);
C(
    p.State(b).Intensity == 0 && b.BotOwner.Settings.Current.Active.Count == 0,
    "full protection recovers and removes own modifiers"
);
(p, b, c) = Setup();
Physics.Wall = true;
Step(p, b, 15);
C(!p.State(b).Inside, "provider sphere LOS respected");
(p, b, c) = Setup();
c._exploded = false;
Step(p, b, 15);
C(!p.State(b).Inside, "unemitted cloud ignored");
(p, b, c) = Setup();
c.gameObject.activeInHierarchy = false;
Step(p, b, 15);
C(!p.State(b).Inside, "inactive cloud ignored");
(p, b, c) = Setup();
var emitter = new VolumetricGasEmitter { Test = v => v.x < 3 };
c._emitter = emitter;
Plugin.GasEmitterCreated(emitter, c._smoke);
Physics.Wall = true;
Step(p, b, 15);
Step(p, b, 15.25f);
C(
    p.State(b).Inside && p.State(b).Intensity > 0,
    "provider volume authoritative and shares canister birth"
);
c.Dead = true;
Step(p, b, 15.5f);
C(p.State(b).Inside, "lingering emitter survives canister");
emitter._finished = true;
emitter._emitUntil = 15;
emitter._particleLifetime = .5f;
Step(p, b, 16);
C(!p.State(b).Inside, "actual provider expiry");
foreach (
    string mode in new[]
    {
        "ghost",
        "scav",
        "disabled",
        "client",
        "inactive",
        "dead",
        "game-ending",
    }
)
{
    (p, b, c) = Setup();
    Expose(p, b);
    if (mode == "ghost")
        p.ghosts.Add(b.ProfileId);
    if (mode == "scav")
        b.Pmc = false;
    if (mode == "disabled")
        p.config!.GasExposure.Enabled = false;
    if (mode == "client")
        p.Authority = false;
    if (mode == "inactive")
        b.BotActive = false;
    if (mode == "dead")
        b.IsDead = true;
    if (mode == "game-ending")
        b.GameEnding = true;
    Step(p, b, 19);
    C(
        p.State(b).Intensity == 0 && b.BotOwner.Settings.Current.Active.Count == 0,
        "scope cleanup " + mode
    );
}
(p, b, c) = Setup();
Expose(p, b);
p.End();
Step(p, b, 19);
C(!p.State(b).Inside, "registry cleared between raids");
(p, b, c) = Setup();
Plugin.GasCloudCreated(new CsGasCloud());
Step(p, b, 15);
Step(p, b, 15.25f);
C(Math.Abs(p.State(b).Intensity - .0625) < .00001, "overlap does not stack intensity");
(p, b, c) = Setup();
var native = b.Grenade.GrenadeReactionClass;
var gasTrack = new GrenadeTrackerClass { Grenade = c._smoke, DangerPoint = new(2, 0, 0) };
native.EnemyGrenadesList[1] = gasTrack;
p.Filter(b);
C(ReferenceEquals(native.DangerGrenade, gasTrack), "source initial gas scatter");
gasTrack.TimeSinceThrown = 5;
p.Filter(b);
C(native.DangerGrenade == null, "source gas stops occupying danger slot after five seconds");
var frag = new GrenadeTrackerClass { Grenade = new EFT.Grenade(), DangerPoint = new(6, 0, 0) };
native.EnemyGrenadesList[2] = frag;
p.Filter(b);
C(ReferenceEquals(native.DangerGrenade, frag), "known frag remains selectable");
frag.CanReact = false;
native.Select(gasTrack);
p.Filter(b);
C(native.DangerGrenade == null, "unperceived frag not invented");
frag.CanReact = true;
frag.DangerPoint = new(50, 0, 0);
native.Select(gasTrack);
p.Filter(b);
C(native.DangerGrenade == null, "native 18m initial selection limit");
p.config!.GasExposure.Enabled = false;
native.Select(gasTrack);
p.Filter(b);
C(ReferenceEquals(native.DangerGrenade, gasTrack), "disabled leaves native reaction unchanged");
foreach (
    var kind in new[]
    {
        ("CS", Manimal.CSGas.GasVariant.Cs),
        ("RDA-68", Manimal.CSGas.GasVariant.Rda68),
        ("RDA-78", Manimal.CSGas.GasVariant.Rda78),
    }
)
foreach (bool volume in new[] { false, true })
{
    (p, b, c) = Setup(new(2, 0, 1));
    c.Variant = kind.Item2;
    VolumetricGasEmitter? v = null;
    if (volume)
    {
        v = new() { Test = pos => pos.x < 3 };
        c._emitter = v;
        Plugin.GasEmitterCreated(v, c._smoke);
    }
    Expose(p, b);
    C(
        p.Ready && p.State(b).Inside && p.FlashCalls > 0,
        "2.0.1 " + kind.Item1 + " exposure " + volume
    );
    C(
        p.State(b).Trace!.Provider == "Manimal-CSGas 2.0.1"
            && p.State(b).Trace!.Variant == kind.Item1,
        "provider and variant in trace"
    );
    var tracked = new GrenadeTrackerClass { Grenade = c._smoke, TimeSinceThrown = 5 };
    b.Grenade.GrenadeReactionClass.Select(tracked);
    p.Filter(b);
    C(b.Grenade.GrenadeReactionClass.DangerGrenade == null, "emitting RDA/CS is not frag danger");
    if (volume)
    {
        c.Dead = true;
        Step(p, b, 18.25f);
        C(
            p.State(b).Inside && p.State(b).Trace!.Variant == kind.Item1,
            "variant retained with lingering volume"
        );
    }
    streams.AddRange(p.diagnostics!.Lines);
    p.End();
    Step(p, b, 19);
    C(!p.State(b).Inside, "variant registry cleared");
}
(p, b, c) = Setup();
c.Dead = true;
var smokeTrack = new GrenadeTrackerClass { Grenade = new EFT.SmokeGrenade() };
b.Grenade.GrenadeReactionClass.Select(smokeTrack);
p.Filter(b);
C(
    b.Grenade.GrenadeReactionClass.DangerGrenade == null,
    "ordinary smoke cannot preserve a native dodge"
);
(p, b, c) = Setup();
var stale = new GrenadeTrackerClass { Grenade = new EFT.Grenade(), DangerPoint = new(1, 0, 0) };
var current = b.Grenade.GrenadeReactionClass;
current.Select(stale);
p.Filter(b);
C(current.DangerGrenade == null, "removed grenade clears stale selection");
current.EnemyGrenadesList[1] = stale;
stale.CanReact = false;
current.Select(stale);
p.Filter(b);
C(current.DangerGrenade == null, "inactive grenade clears stale selection");
stale.CanReact = true;
stale.DangerPoint = new(50, 0, 0);
current.Select(stale);
p.Filter(b);
C(current.DangerGrenade == null, "distant grenade clears stale selection");
stale.DangerPoint = new(1, 0, 0);
stale.Grenade!.GrenadeSettings.CollisionSound = EFT.GrenadeSettings.CollisionSounds.smoke;
current.Select(stale);
p.Filter(b);
C(current.DangerGrenade == null, "collision smoke clears stale selection");
var dir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts"));
Directory.CreateDirectory(dir);
File.WriteAllLines(Path.Combine(dir, "g08-adapter-events.jsonl"), streams);
Console.WriteLine(
    $"G08 production adapter + provider bridge: {checks} checks passed (controlled engine doubles, not a raid)."
);
