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
    var deadline = s.CornerPeek.Policy.Deadline;
    b.Position = new(.3f, 0, 0);
    b.Player.Velocity = new(1, 0, 0);
    s.CornerPeek.Speed = 2;
    p.Tick(b, s, 10.08f);
    Check(s.CornerPeek.PlanStage == 1 && Math.Abs(s.CornerPeek.Origin.x - .3) < .00001,
        "the source samples the actual origin while the bot is still moving");
    b.Position = new(.7f, 0, 0);
    for (int i = 0; i < 5 && s.CornerPeek.Policy.Trace!.State == "planning"; i++)
        p.Tick(b, s, 10.2f + .1f * i);
    Check(s.CornerPeek.Policy.Trace!.State == "exposed" && s.CornerPeek.LaneSafe,
        "drift between frames allows a usable plan");
    Check(s.CornerPeek.Policy.Deadline == deadline, "original deadline retained");
}
{
    var (p, b, s) = Setup();
    Physics.CorridorHasCover = false;
    p.Tick(b, s, 10.1f);
    p.Tick(b, s, 10.2f);
    p.Tick(b, s, 10.3f);
    Check(s.CornerPeek.Policy.Trace!.Reason == "corner-no-local-cover",
        "both candidate sides are exposed to the enemy");
    Physics.CorridorHasCover = true;
}
{
    var (p, b, s) = Setup();
    p.Route = false;
    for (int i = 0; i < 3; i++)
        p.Tick(b, s, 10.1f + .1f * i);
    Check(!s.CornerPeek.Policy.Active && s.CornerPeek.Policy.Trace!.Reason == "corner-no-local-cover",
        "no reachable candidate leaves the native mover in control");
}
Console.WriteLine($"PASS {checks} production corner planning checks");
