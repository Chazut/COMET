using Comet.Client;
using EFT;
using SAIN.Components;
using SAIN.Models.Enums;
using SAIN.Preset.Shared.Enums;
using UnityEngine;

int n = 0;
void C(bool v, string m)
{
    n++;
    if (!v)
        throw new Exception(m);
}
foreach (var layer in new[] { ESAINLayer.Combat, ESAINLayer.AvoidThreat, ESAINLayer.Flashed })
{
    Time.time = 10;
    var p = new Plugin();
    var b = new BotComponent { ActiveLayer = layer };
    p.Fight(b);
    b.ActiveLayer = ESAINLayer.None;
    C(!p.Wish(b, true), "recent combat retains raised stance");
    C(p.Wish(b, false) == false, "native false unchanged");
    Time.time = 35;
    C(!p.Wish(b, true), "inclusive 25 second boundary");
    Time.time = 35.01f;
    C(p.Wish(b, true), "native patrol restored");
    p.End(b);
    C(!p.states[b].ReadyStance.Policy.Pending(Time.time), "cleanup");
}
{
    Time.time = 100;
    var p = new Plugin();
    var b = new BotComponent();
    p.Fight(b);
    C(p.Wish(b, true), "no combat means no veto");
}
{
    Time.time = 10;
    var p = new Plugin();
    var b = new BotComponent { ActiveLayer = ESAINLayer.Combat };
    p.IsGhost = true;
    p.Fight(b);
    C(p.Wish(b, true) && p.states.Count == 0, "ghost stays out of scope");
}
{
    Time.time = 10;
    var p = new Plugin();
    var b = new BotComponent { ActiveLayer = ESAINLayer.Combat };
    p.Fight(b);
    // Repeated native queries must keep identical choices without allocating the
    // same alternatives array or a concatenated diagnostic key on every call.
    for (int i = 0; i < 100; i++)
    {
        p.Wish(b, true);
        p.After(b);
    }
    var alternatives = p.states[b].ReadyStance.Policy.Trace!.Alternatives;
    int logs = p.diagnostics!.Lines.Count;
    long before = GC.GetAllocatedBytesForCurrentThread();
    for (int i = 0; i < 10000; i++)
    {
        p.states[b].ReadyStance.Next = 0;
        p.Watch(b);
    }
    long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
    C(
        bytes < 400000,
        "posture sampling retains only native reflection boxing, not arrays or log keys"
    );
    C(p.diagnostics.Lines.Count == logs, "unchanged posture sampling preserves log suppression");
    var policy = p.states[b].ReadyStance.Policy;
    before = GC.GetAllocatedBytesForCurrentThread();
    for (int i = 0; i < 10000; i++)
        policy.Evaluate(10, true, "", p.config!.ReadyStance);
    bytes = GC.GetAllocatedBytesForCurrentThread() - before;
    C(
        bytes == 0 && ReferenceEquals(alternatives, policy.Trace!.Alternatives),
        "stance choices reuse immutable explanations"
    );
    b.Player.MovementContext._isInPatrol = false;
    p.Wish(b, true);
    p.After(b);
    C(
        p.diagnostics.Lines.Count == logs + 1,
        "real raised-posture transition still logged immediately"
    );
    Console.WriteLine($"H05 unchanged policy: {bytes} bytes / 10000 evaluations");
}
Console.WriteLine($"H05 source adapter: {n} checks passed");
