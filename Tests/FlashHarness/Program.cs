using Comet.Client;
using SAIN.Components;
using UnityEngine;

int checks = 0;
void C(bool ok, string why)
{
    checks++;
    if (!ok)
        throw new Exception(why);
}
void Near(float a, float b, string why) => C(Math.Abs(a - b) < .001, why);
(Plugin, BotComponent) Setup()
{
    Time.Set(10);
    SAIN.SAINPlugin.LoadedPreset = new();
    return (new(), new());
}
var (p, b) = Setup();
b.Flashed.ApplyFlash(10, new());
Near(b.Flashed.End, 22, "native duration");
Near(b.Flashed.LastSeenEnemyPoint!.Value.x, 100, "live memory on actual flash");
Time.Set(11);
b.GoalEnemy!.EnemyPosition = new(200, 0, 0);
b.Flashed.ApplyFlash(2, new());
Near(b.Flashed.End, 13.4f, "new flash can shorten old flash");
Near(b.Flashed.LastSeenEnemyPoint!.Value.x, 200, "new flash refreshes position");
Near(b.Flashed.Delay, 15, "new flash resets blind-fire delay");
C(b.BotOwner.BotsGroup.Searches == 2, "two real flashes, two searches");
float delay = b.Flashed.Delay;
int searches = b.BotOwner.BotsGroup.Searches;
b.GoalEnemy.EnemyPosition = new(300, 0, 0);
Time.Set(12);
C(p.RefreshOngoingFlash(b, 3), "continuous refresh extends");
Near(b.Flashed.End, 15.6f, "refresh deadline");
Near(b.Flashed.Delay, delay, "refresh cannot postpone blind fire");
Near(b.Flashed.LastSeenEnemyPoint!.Value.x, 200, "refresh cannot track live enemy");
C(b.BotOwner.BotsGroup.Searches == searches, "refresh no search point");
C(!p.RefreshOngoingFlash(b, 1), "shorter refresh ignored");
(p, b) = Setup();
p.GasEnabled = true;
p.GasFlash(b, new());
C(b.Flashed.IsFlashed, "gas starts flashed layer");
C(b.BotOwner.Settings.Current.Applied.Count == 0, "gas no fixed flash penalties");
float gasDelay = b.Flashed.Delay;
Time.Set(12);
p.GasFlash(b, new());
Near(b.Flashed.Delay, gasDelay, "gas keeps blind fire");
C(b.BotOwner.BotsGroup.Searches == 1, "gas no repeated searches");
b.Flashed.ApplyFlash(10, new());
C(b.BotOwner.Settings.Current.Applied.Count == 2, "real flash modifiers coexist");
p.End();
C(!b.Flashed.IsFlashed, "raid cleanup");
(p, b) = Setup();
b.GoalEnemy = null;
b.Flashed.ApplyFlash(2, new());
C(b.Flashed.LastSeenEnemyPoint == null, "no fabricated target");
foreach (
    string mode in new[]
    {
        "disabled",
        "master",
        "scav",
        "human",
        "ghost",
        "replica",
        "inactive",
        "dead",
        "ending",
    }
)
{
    (p, b) = Setup();
    if (mode == "disabled")
        p.config!.FlashContinuity.Enabled = false;
    if (mode == "master")
        p.config!.Enabled = false;
    if (mode is "scav" or "human")
        b.Pmc = false;
    if (mode == "ghost")
        p.ghosts.Add(b.ProfileId);
    if (mode == "replica")
        p.Authority = false;
    if (mode == "inactive")
        b.BotActive = false;
    if (mode == "dead")
        b.IsDead = true;
    if (mode == "ending")
        b.GameEnding = true;
    b.Flashed.ApplyFlash(2, new());
    C(p.states.Count == 0, "observation scope: " + mode);
    Near(b.Flashed.LastSeenEnemyPoint!.Value.x, 100, "native retained: " + mode);
}
Console.WriteLine(
    $"G09 production adapter: {checks} checks passed (controlled native doubles, no raid)."
);
