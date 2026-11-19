using Comet.Client;
using Comet.Core;
using UnityEngine;

int checks = 0;
void Check(bool ok, string why)
{
    checks++;
    if (!ok)
        throw new Exception(why);
}
(Plugin p, BotComponent b, BotState s) Setup()
{
    var p = new Plugin();
    var b = new BotComponent();
    var s = new BotState();
    s.CornerPeek.Policy.Begin(
        new()
        {
            Now = 10,
            Position = new(0, 0, 0),
            Known = new(0, 0, 12),
            Target = "enemy",
            Distance = 12,
            Visible = true,
        },
        1,
        p.config.CornerPeek,
        0,
        .8,
        0,
        0,
        true
    );
    return (p, b, s);
}
{
    var (p, b, s) = Setup();
    b.Player.Velocity = new(1, 0, 0);
    p.Tick(b, s, 10);
    Check(
        p.Queries == 0 && s.CornerPeek.Policy.Active,
        "same-frame callback waits for actual native inertia even before speed sample"
    );
    b.Position = new(.2f, 0, 0);
    s.CornerPeek.Speed = 2;
    p.Tick(b, s, 10.08f);
    Check(
        p.Queries == 0 && s.CornerPeek.Policy.Active,
        "20 cm deceleration drift no longer aborts planning"
    );
    b.Position = new(.3f, 0, 0);
    s.CornerPeek.Speed = .1f;
    b.Player.Velocity = default;
    p.Tick(b, s, 10.2f);
    Check(
        Math.Abs(p.LastOrigin.x - .3) < .00001 && s.CornerPeek.PlanStage == 1,
        "geometry measured at actual settled position"
    );
    p.Tick(b, s, 10.25f);
    Check(
        s.CornerPeek.Policy.Trace!.State == "exposed" && s.CornerPeek.LaneSafe,
        "valid plan applied after stop"
    );
    Check(s.CornerPeek.Policy.Deadline == 14, "original deadline retained");
}
{
    var (p, b, s) = Setup();
    b.Position = new(.2f, 0, 0);
    p.Tick(b, s, 10.1f);
    b.Position = new(.7f, 0, 0);
    p.Tick(b, s, 10.2f);
    Check(
        s.CornerPeek.Policy.Trace!.Reason == "corner-planning-origin-moved"
            && !s.CornerPeek.Policy.Active,
        "later drift invalidates already checked geometry"
    );
}
{
    var (p, b, s) = Setup();
    p.Open = false;
    p.Tick(b, s, 10.1f);
    Check(
        s.CornerPeek.Policy.Trace!.Reason == "corner-initial-angle-blocked",
        "real occlusion still rejects peek"
    );
}
{
    var (p, b, s) = Setup();
    b.Player.Velocity = new(1, 0, 0);
    p.Tick(b, s, 10.1f);
    p.Tick(b, s, 12f);
    Check(
        !s.CornerPeek.Policy.Active
            && s.CornerPeek.Policy.Trace!.Reason == "corner-planning-timeout",
        "inertia cannot extend planning deadline"
    );
}
Console.WriteLine($"PASS {checks} production corner planning checks");
