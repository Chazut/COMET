using Comet.Client;
using Comet.Core;
using EFT;
using SAIN.Components;
using SAIN.SAINComponent.Classes.Mover;
using SAIN.SAINComponent.SubComponents;
using UnityEngine;
using UnityEngine.AI;

int checks = 0;
void C(bool v, string why)
{
    checks++;
    if (!v)
        throw new Exception("G06 adapter: " + why);
}
var stream = new List<string>();
(Plugin, BotComponent[]) Setup(int count = 2)
{
    Time.Set(10);
    Physics.Hit = null;
    NavMesh.Sample = true;
    NavMesh.Route = null;
    NavMesh.Status = NavMeshPathStatus.PathComplete;
    NavMesh.Calls = 0;
    var p = new Plugin();
    var group = new Squad { SquadInfo = new() { GUID = "team" } };
    var bots = Enumerable
        .Range(0, count)
        .Select(i => new BotComponent
        {
            ProfileId = "pmc-" + i,
            Position = new(2, 0, 0),
            Squad = group,
        })
        .ToArray();
    var grenade = new Grenade { Id = -8, ProfileId = "enemy" };
    foreach (var b in bots)
    {
        p.Add(b);
        group.Members.Add(b.ProfileId, b);
        b.Grenade.GrenadeReactionClass.EnemyGrenadesList.Add(
            grenade,
            new GrenadeTrackerClass(b, grenade, new(0, 0, 0), 0, true) { CanReact = true }
        );
    }
    return (p, bots);
}
void Step(Plugin p, BotComponent b, float now)
{
    Time.Set(now);
    p.Tick(b);
    p.Act(b);
}
var (p, bots) = Setup();
var a = bots[0];
var b = bots[1];
Step(p, a, 10);
Step(p, b, 10.01f);
C(
    p.states[a].GrenadeEscape.Path != null && p.states[b].GrenadeEscape.Path != null,
    "both members get native escape routes"
);
C(
    Vector3.Distance(p.states[a].GrenadeEscape.Goal, p.states[b].GrenadeEscape.Goal) > 1,
    "source squad-index bearing disperses members"
);
var held = b.Mover.ActivePath;
Step(p, b, 10.3f);
C(ReferenceEquals(held, b.Mover.ActivePath), "committed route remains stable");
stream.AddRange(p.diagnostics!.Lines);

(p, bots) = Setup();
p.config!.GrenadeDispersion.Enabled = false;
Step(p, bots[0], 10);
Step(p, bots[1], 10.01f);
C(
    Vector3.Distance(p.states[bots[0]].GrenadeEscape.Goal, p.states[bots[1]].GrenadeEscape.Goal)
        < .01,
    "toggle restores native shared bearing"
);
(p, bots) = Setup();
p.Authority = false;
Step(p, bots[0], 10);
C(bots[0].Mover.ActivePath == null, "replica does not move bots");
(p, bots) = Setup();
p.ghosts.Add(bots[0].ProfileId);
Step(p, bots[0], 10);
C(bots[0].Mover.ActivePath == null, "ghost stays under ORBIT");
(p, bots) = Setup();
Step(p, bots[1], 10);
p.End();
C(
    p.states[bots[1]].GrenadeEscape.DispersionLease == null,
    "raid end releases diagnostic reservations"
);
var artifacts = Path.GetFullPath(
    Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts")
);
Directory.CreateDirectory(artifacts);
File.WriteAllLines(
    Path.Combine(artifacts, "g06-adapter-events.jsonl"),
    stream.Where(l => l.Contains("\"feature\":\"G06\""))
);
Console.WriteLine($"G06 source dispersion: {checks} checks passed with engine doubles.");
