using Comet.Client;
using Comet.Core;
using Comfort.Common;
using EFT;
using EFT.Interactive;
using SAIN.Components;
using SAIN.SAINComponent.Classes.Mover;
using UnityEngine;

int checks = 0;
void Check(bool value, string message)
{
    checks++;
    if (!value)
        throw new Exception("C02 adapter: " + message);
}
Plugin.RecoveryHooks();
Plugin.Hooks();
(Plugin p, BotComponent b, BotState s, DoorDataStruct d) Setup()
{
    var p = new Plugin();
    Plugin.Instance = p;
    p.At(0);
    Plugin.VerifiedFika(false);
    Singleton<GameWorld>.Instance = new();
    var b = new BotComponent();
    b.Mover.ActivePath = new BotPathDataManual(b);
    var s = new BotState();
    p.states[b] = s;
    var link = new Link();
    b.BotOwner.VoxelesPersonalData.CurVoxel!.DoorLinks.Add(link);
    var d = new DoorDataStruct(link);
    b.DoorOpener.TestDoors.Add(d);
    return (p, b, s, d);
}
void Finish(Door d)
{
    d.CurrentAngle = d.Native.ResultAngle;
    d.Native.ProgressTime = d.Native.Duration;
}
void Run(Plugin p, BotComponent b, float until)
{
    for (float t = .25f; t <= until + .001f; t += .25f)
        p.RecoverTick(b, t);
}
{
    var (p, bot, _, _) = Setup();
    bot.DoorOpener.Interacting = true;
    Plugin.Layer(bot, true, true);
    Check(bot.DoorOpener.Interacting, "active layer keeps ongoing opening");
    Plugin.Layer(bot, true, false);
    Check(!bot.DoorOpener.Interacting && bot.DoorOpener.ClearCount == 1, "dropping layer clears collision immediately");
    Plugin.Deactivate(bot);
    Check(bot.DoorOpener.ClearCount == 1, "overlapping deactivation does not clear twice");
    bot.DoorOpener.Interacting = true;
    Plugin.Deactivate(bot);
    Check(!bot.DoorOpener.Interacting && bot.DoorOpener.ClearCount == 2, "standby also clears collision without a path update");
}
var x = Setup();
x.p.Start(x.b, x.d);
Finish(x.d.Door);
Run(x.p, x.b, 1.25f);
Check(
    x.d.Door.DoorState == EDoorState.Open && x.s.Recovery.Policy.Trace.Requested,
    "finished stuck interaction repaired"
);
Check(!x.s.Recovery.Policy.Trace.Confirmed, "write is not confirmation");
x.p.RecoverTick(x.b, 1.5f);
Check(
    x.s.Recovery.Policy.Trace.Confirmed && x.s.Recovery.Door == null,
    "later observation confirms then releases references"
);
Check(x.b.DoorOpener.ClearCount == 1, "expired owned SAIN collision-ignore latch cleared");
var fixture = x.p.diagnostics!.Lines.ToList();
x = Setup();
x.p.Start(x.b, x.d);
Finish(x.d.Door);
x.d.Door.DoorState = EDoorState.Open;
Run(x.p, x.b, 2);
Check(
    !x.s.Recovery.Policy.Trace.Requested && x.s.Recovery.Door == null,
    "normal completion yields without repair"
);
x = Setup();
x.p.Start(x.b, x.d);
Finish(x.d.Door);
x.d.Door.DoorState = EDoorState.Open;
x.d.Door.Native.ResultState = EDoorState.Shut;
Run(x.p, x.b, 2);
Check(
    x.s.Recovery.Policy.Trace.Confirmed && x.s.Recovery.Policy.Trace.Repair == "align-record",
    "completed internal record realigned"
);
fixture.AddRange(x.p.diagnostics!.Lines);
x = Setup();
x.d.Door.DoorState = EDoorState.Open;
x.d.Door.CurrentAngle = 90;
x.d.Door.Native.ResultState = EDoorState.Shut;
x.d.Door.Native.ProgressTime = 1;
x.p.Encounter(x.b, 0);
Run(x.p, x.b, 2);
Check(
    x.s.Recovery.Policy.Trace.Confirmed && x.d.Door.Native.ResultState == EDoorState.Open,
    "discovered idle record realigned without replaying old close"
);
x = Setup();
x.p.Start(x.b, x.d);
x.d.Door.Native.ProgressTime = .5f;
Run(x.p, x.b, 5);
Check(x.s.Recovery.Policy.Trace.Confirmed, "frozen coroutine force-finished");
Check(
    x.d.Door.CurrentAngle == 90 && x.d.Door.DoorState == EDoorState.Open,
    "angle and state corrected"
);
fixture.AddRange(x.p.diagnostics!.Lines);
x = Setup();
x.p.Start(x.b, x.d);
Finish(x.d.Door);
x.d.Door.CurrentAngle = 30;
Run(x.p, x.b, 5);
Check(
    x.s.Recovery.Policy.Trace.Confirmed && x.d.Door.CurrentAngle == 90,
    "unreached endpoint corrected"
);
x = Setup();
x.p.Start(x.b, x.d);
Finish(x.d.Door);
x.d.Door.Native.Break = true;
x.d.Door.Native.ProgressTime = .5f;
Run(x.p, x.b, 5);
Check(x.s.Recovery.Policy.Trace.Confirmed, "broken interpolation recovered");
x = Setup();
x.p.Start(x.b, x.d);
Finish(x.d.Door);
var human = new SAIN.Components.Player { CurrentStateName = EPlayerState.DoorInteraction };
human.MovementContext.InteractionInfo.WorldInteractiveObject = x.d.Door;
Singleton<GameWorld>.Instance.AllAlivePlayersList.Add(human);
Run(x.p, x.b, 2);
Check(!x.s.Recovery.Policy.Trace.Requested, "human animation protected");
human.CurrentStateName = EPlayerState.Idle;
x.p.RecoverTick(x.b, 2.25f);
x.p.RecoverTick(x.b, 2.5f);
Check(x.s.Recovery.Policy.Trace.Confirmed, "recovery after player releases door");
x = Setup();
x.p.Start(x.b, x.d);
Finish(x.d.Door);
x.p.Busy = true;
Run(x.p, x.b, 5);
Check(!x.s.Recovery.Policy.Trace.Requested, "D14 reservation protected");
x = Setup();
x.p.Start(x.b, x.d);
x.b.Player.CurrentStateName = EPlayerState.DoorInteraction;
x.b.Player.MovementContext.InteractionInfo.WorldInteractiveObject = x.d.Door;
Singleton<GameWorld>.Instance.AllAlivePlayersList.Add(x.b.Player);
Run(x.p, x.b, 7.75f);
Check(!x.s.Recovery.Policy.Trace.Requested, "own animation protected before grace");
x.p.RecoverTick(x.b, 8);
x.p.RecoverTick(x.b, 8.25f);
x.p.RecoverTick(x.b, 8.5f);
Check(x.s.Recovery.Policy.Trace.Confirmed, "own frozen animation eventually force-finished");

x = Setup();
x.p.Start(x.b, x.d);
Finish(x.d.Door);
var other = new BotComponent();
other.DoorOpener.TestDoors.Add(x.d);
other.DoorOpener.Interacting = true;
x.p.states[other] = new();
Run(x.p, x.b, 5);
Check(!x.s.Recovery.Policy.Trace.Requested, "other bot native interaction protected");
x = Setup();
x.p.Start(x.b, x.d);
Finish(x.d.Door);
x.d.Door.LockForInteraction();
Run(x.p, x.b, 2);
Check(
    !x.s.Recovery.Policy.Trace.Requested && x.s.Recovery.Door == null,
    "newer interaction generation invalidates ownership"
);
x = Setup();
x.p.Start(x.b, x.d);
Finish(x.d.Door);
x.d.Door.DoorState = EDoorState.Locked;
Run(x.p, x.b, 2);
Check(!x.s.Recovery.Policy.Trace.Requested, "lock cannot be bypassed");
x = Setup();
x.p.Start(x.b, x.d);
Finish(x.d.Door);
x.d.Door.OnSet = () => x.d.Door.LockForInteraction();
Run(x.p, x.b, 2);
Check(
    !x.s.Recovery.Policy.Trace.Confirmed,
    "reentrant foreign action during setter preserves its record"
);
x = Setup();
x.p.Start(x.b, x.d);
Finish(x.d.Door);
x.d.Door.IgnoreSet = true;
Run(x.p, x.b, 2);
Check(
    x.s.Recovery.Policy.Trace.Requested
        && !x.s.Recovery.Policy.Trace.Confirmed
        && ((BotPathDataManual)x.b.Mover.ActivePath!).CancelRequested,
    "failed write observed, no retry storm"
);
fixture.AddRange(x.p.diagnostics!.Lines);
foreach (
    var edit in new Action<Plugin, BotComponent, BotState>[]
    {
        (p, b, s) => p.Authority = false,
        (p, b, s) => p.IsGhost = true,
        (p, b, s) => b.Pmc = false,
        (p, b, s) => b.IsAI = false,
        (p, b, s) => b.IsDead = true,
        (p, b, s) => b.GameEnding = true,
        (p, b, s) => b.BotActive = false,
        (p, b, s) => b.CurrentAction = new(),
        (p, b, s) => b.Mover.ActivePath = new BotPathDataManual(b),
        (p, b, s) => ((BotPathDataManual)b.Mover.ActivePath!).TimeStarted = 9,
        (p, b, s) => p.config!.DoorRecovery.Enabled = false,
        (p, b, s) => s.TacticalDoor = true,
        (p, b, s) => p.config!.Enabled = false,
    }
)
{
    x = Setup();
    x.p.Start(x.b, x.d);
    Finish(x.d.Door);
    edit(x.p, x.b, x.s);
    Run(x.p, x.b, 2);
    Check(
        !x.s.Recovery.Policy.Trace.Requested && x.s.Recovery.Door == null,
        "scope ends immediately"
    );
}
foreach (
    var edit in new Action<Plugin, BotComponent, BotState>[]
    {
        (p, b, s) => b.Pmc = false,
        (p, b, s) => b.IsAI = false,
        (p, b, s) => p.Authority = false,
        (p, b, s) => p.config!.DoorRecovery.Enabled = false,
    }
)
{
    x = Setup();
    edit(x.p, x.b, x.s);
    x.p.Start(x.b, x.d);
    Check(x.s.Recovery.Door == null, "unowned bot never registered");
}
x = Setup();
x.p.config!.DoorTransit.Enabled = false;
x.p.Start(x.b, x.d);
Finish(x.d.Door);
Run(x.p, x.b, 2);
Check(x.s.Recovery.Policy.Trace.Confirmed, "C02 works with C01 disabled");
x = Setup();
((BotPathDataManual)x.b.Mover.ActivePath!).PathCorners[0].Position = new(5, 0, -1);
x.p.Start(x.b, x.d);
Check(x.s.Recovery.Door == null, "nearby door outside route excluded");
x = Setup();
x.p.Start(x.b, x.d, success: false);
Check(x.s.Recovery.Door == null, "failed native command never owned");
x = Setup();
x.p.Start(x.b, x.d, EInteractionType.Breach);
Check(x.s.Recovery.Door == null, "breach excluded");
x = Setup();
x.d.Door.DoorState = EDoorState.Locked;
x.p.Start(x.b, x.d);
Check(x.s.Recovery.Door == null, "locked initial door excluded");
x = Setup();
x.d.Link.Door = new SpecialDoor();
var special = new DoorDataStruct(x.d.Link);
x.p.Start(x.b, special);
Check(x.s.Recovery.Door == null, "special door excluded");
foreach (bool contract in new[] { false, true })
foreach (bool sent in new[] { false, true })
{
    x = Setup();
    x.p.fikaPresent = true;
    x.p.BridgeReady = contract;
    Plugin.VerifiedFika(false);
    x.p.Start(x.b, x.d, sent: sent);
    Finish(x.d.Door);
    Run(x.p, x.b, 2);
    Check(
        x.s.Recovery.Policy.Trace.Confirmed == contract,
        "Fika requires COMET transport, independent of original sender"
    );
}
x = Setup();
x.p.fikaPresent = true;
x.p.BridgeReady = true;
Plugin.VerifiedFika(true);
x.d.Door.ForceLocalInteraction = true;
x.p.Start(x.b, x.d, sent: true);
Finish(x.d.Door);
Run(x.p, x.b, 2);
Check(!x.s.Recovery.Policy.Trace.Requested, "local-only Fika door not finalized");
x = Setup();
x.d.Door.InitializeSmoothOpenInteraction(EDoorState.Open);
x.d.Door.DoorState = EDoorState.Interacting;
Finish(x.d.Door);
x.p.Encounter(x.b, 0);
Run(x.p, x.b, 5);
Check(
    x.s.Recovery.Policy.Trace.Confirmed,
    "unknown local interaction recovered from actual target"
);
x = Setup();
x.p.Start(x.b, x.d);
Finish(x.d.Door);
x.p.RecoverEnd(x.s);
Check(
    x.s.Recovery.Door == null && x.s.Recovery.Path == null && x.s.Recovery.Interaction == null,
    "raid cleanup drops native references"
);
x = Setup();
x.p.config!.DoorRecovery.SampleInterval = .1;
x.p.Start(x.b, x.d);
for (int i = 0; i <= 10; i++)
    x.p.RecoverTick(x.b, i * .1f);
var observations = x
    .p.diagnostics!.Lines.Select(line => Newtonsoft.Json.Linq.JObject.Parse(line))
    .Where(e => (string?)e["doorRecovery"]?["state"] == "waiting")
    .ToArray();
Check(
    observations.Length >= 9,
    "fast sampling publishes fresh RR observations without throttle gaps"
);
var output = Path.GetFullPath(
    Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts/c02")
);
Directory.CreateDirectory(output);
File.WriteAllLines(Path.Combine(output, "recovery.jsonl"), fixture);
File.WriteAllText(Path.Combine(output, "c02-recovery.json"), "[" + string.Join(",", fixture) + "]");
Console.WriteLine(
    $"C02 production adapter harness: {checks} checks passed. {fixture.Count} actual events exported."
);
