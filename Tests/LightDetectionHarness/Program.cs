using Comet.Client;
using SAIN.Components;
using SAIN.Components.PlayerComponentSpace;
using UnityEngine;

Plugin.Patch();
int checks = 0;
void Check(bool ok, string message)
{
    checks++;
    if (!ok)
        throw new Exception(message);
}
(Plugin p, PlayerComponent observer, PlayerComponent source, FlashlightRaycastJob job) Setup()
{
    GameWorldComponent.Players.Clear();
    Time.time = Time.realtimeSinceStartup = 10;
    var p = new Plugin();
    var observer = new PlayerComponent("pmc");
    observer.BotComponent = new("pmc");
    observer.Flashlight = new(observer);
    var source = new PlayerComponent("human");
    source.Flashlight = new(source);
    GameWorldComponent.Players.Add("pmc", observer);
    GameWorldComponent.Players.Add("human", source);
    return (p, observer, source, new() { Observer = observer, Source = source });
}
RaycastHit Wall(float x) =>
    new()
    {
        collider = new object(),
        point = new(x, 0, 0),
        normal = new(-1, 0, 0),
    };

// Actual production transpiler is installed by Harmony onto the native-shaped
// fixture. NativeArray copies share disposal state, matching Unity ownership.
{
    var (p, observer, source, job) = Setup();
    source.Flashlight.Laser = true;
    job.SourcePass(new RaycastHit());
    job.DetectionPass();
    Check(
        job.Jobs.Count == 1 && job.Jobs[0].Commands.Length == 14,
        "laser without surface hit gets native 5..70 m beam samples"
    );
    Check(
        source.Flashlight.LightDetection.LightPoints.Count == 0,
        "does not change the native list used by other bots"
    );
    Check(
        observer.Flashlight.LightDetection.Investigations == 1,
        "one native search reaction, not duplicated by observer"
    );
    var commands = job.Jobs[0].Commands;
    Check(
        Math.Abs(commands[0].distance - MathF.Sqrt(26)) < .001,
        "ray reaches its point instead of stopping at one metre"
    );
    Check(
        Math.Abs(commands[0].direction.magnitude - 1) < .001
            && commands[0].queryParameters.layerMask == 123,
        "normalized direction and native mask preserved"
    );
    Check(
        p.RR.Single().Contains("native-light-investigation-requested")
            && p.RR.Single().Contains("\"grantsVisualContact\":false"),
        "journal records investigation, not visual identification"
    );
    job.DetectionPass(2);
    Check(
        observer.Flashlight.LightDetection.Investigations == 1,
        "wall beyond 1 m blocks all beam rays"
    );
    Check(p.RR.Count == 1, "detailed diagnostics throttled per observer");
    Time.time += 5;
    job.DetectionPass(2);
    Check(
        p.RR.Last().Contains("light-points-occluded"),
        "occlusion observable after diagnostic interval"
    );
    p.Flush();
    Check(
        p.diagnostics.Lines.Last().Contains("\"occluded\":2"),
        "summary counts every blocked job despite detail throttling"
    );
    var liveCommands = job.Jobs[0].Commands;
    Check(liveCommands.IsCreated, "current job arrays are alive before stop");
    job.Stop();
    Check(
        !liveCommands.IsCreated
            && p.CachedSources == 0
            && p.CachedDetails == 0
            && p.PooledLists == 0,
        "stop releases arrays and raid caches"
    );
}
foreach (float distance in new[] { 2f, 12f, 100f })
{
    var (p, observer, source, job) = Setup();
    source.Flashlight.Laser = true;
    job.SourcePass(Wall(distance));
    job.DetectionPass();
    int expected =
        1
        + (
            distance == 2 ? 0
            : distance == 12 ? 2
            : 14
        );
    Check(
        job.Jobs[0].Commands.Length == expected,
        "beam truncated at first laser impact, capped at 75 m"
    );
    job.Stop();
}
{
    var (p, observer, source, job) = Setup();
    source.Flashlight.WhiteLight = true;
    job.SourcePass(Wall(20));
    job.DetectionPass(4);
    Check(
        job.Jobs.Count == 1 && observer.Flashlight.LightDetection.Investigations == 0,
        "flashlight surface beyond wall does not leak through"
    );
    Check(
        Math.Abs(job.Jobs[0].Commands[0].distance - MathF.Sqrt(19.95f * 19.95f + 1)) < .001,
        "native surface offset retained"
    );
    job.DetectionPass();
    Check(
        observer.Flashlight.LightDetection.Investigations == 1,
        "unoccluded light still investigated"
    );
    job.Stop();
}
foreach (string mode in new[] { "ir-laser", "ir-lamp", "visible-laser", "visible-lamp" })
{
    var (p, observer, source, job) = Setup();
    source.Flashlight.IRLaser = mode == "ir-laser";
    source.Flashlight.IRLight = mode == "ir-lamp";
    source.Flashlight.Laser = mode == "visible-laser";
    source.Flashlight.WhiteLight = mode == "visible-lamp";
    job.SourcePass(Wall(20));
    job.DetectionPass();
    Check((job.Jobs.Count > 0) == mode.StartsWith("visible"), "native wavelength gate: " + mode);
    observer.Player.AIData.BotOwner.NightVision.UsingNow = true;
    job.DetectionPass();
    Check(job.Jobs.Count == 1, "NVG permits native infrared reaction: " + mode);
    source.Flashlight.DeviceActive = false;
    job.SourcePass();
    job.DetectionPass();
    Check(job.Jobs.Count == 0 && p.CachedSources == 0, "device off drops cached beam: " + mode);
    job.Stop();
}
foreach (
    string boundary in new[]
    {
        "disabled",
        "replica",
        "no-world",
        "non-supported",
        "ghost",
        "inactive",
        "ending",
    }
)
{
    var (p, observer, source, job) = Setup();
    source.Flashlight.Laser = true;
    switch (boundary)
    {
        case "disabled":
            p.config.Enabled = false;
            break;
        case "replica":
            p.Authority = false;
            break;
        case "no-world":
            p.world = null;
            break;
        case "non-supported":
            observer.BotComponent!.Supported = false;
            break;
        case "ghost":
            p.Ghosts.Add("pmc");
            break;
        case "inactive":
            observer.BotComponent!.BotActive = false;
            break;
        case "ending":
            observer.BotComponent!.GameEnding = true;
            break;
    }
    job.SourcePass(new RaycastHit());
    job.DetectionPass();
    Check(job.Jobs.Count == 0, "no laser augmentation outside scope: " + boundary);
    job.SourcePass(Wall(20));
    job.DetectionPass();
    Check(
        job.Jobs[0].Commands[0].distance == 1 && p.RR.Count == 0,
        "native constructor and behavior unchanged outside scope: " + boundary
    );
    job.Stop();
}
{
    var (p, observer, source, job) = Setup();
    source.Flashlight.Laser = true;
    job.SourcePass(new RaycastHit());
    job.RealDistance = 126;
    job.DetectionPass();
    Check(job.Jobs.Count == 0, "native 125 m enemy-distance gate preserved");
    job.RealDistance = 125;
    job.DetectionPass();
    Check(job.Jobs.Count == 1, "native boundary still accepted");
    source.PointDirection = Vector3.zero;
    job.SourcePass(new RaycastHit());
    job.DetectionPass();
    Check(job.Jobs.Count == 0, "zero laser direction not fabricated");
    source.PointDirection = new(1, 0, 0);
    p.diagnostics.Quiet = true;
    Time.time += 5;
    int logs = p.diagnostics.Lines.Count,
        events = p.RR.Count;
    job.SourcePass(new RaycastHit());
    job.DetectionPass();
    p.Flush();
    Check(
        p.diagnostics.Lines.Count == logs && p.RR.Count == events + 1,
        "Quiet suppresses disk detail but does not suppress RR"
    );
    p.config.CombatJournal.Enabled = false;
    Time.time += 5;
    job.DetectionPass();
    Check(
        observer.Flashlight.LightDetection.Investigations == 3 && p.RR.Count == events + 1,
        "journal off does not veto native reaction"
    );
    Check(p.Logger.Warnings.Count == 0, "no runtime adapter failure");
    job.Stop();
}
Console.WriteLine(
    $"Light detection: {checks} production adapter/Harmony checks passed (detached physics fixture)."
);
