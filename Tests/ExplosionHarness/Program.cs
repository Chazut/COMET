using Comet.Client;
using EFT;
using SAIN.Components;
using SAIN.Preset;
using SAIN.SAINComponent.Classes.EnemyClasses;
using UnityEngine;

int n = 0;
void C(bool ok, string why)
{
    n++;
    if (!ok)
        throw new Exception(why);
}
(Plugin, BotComponent, Player) Setup()
{
    BotTypeDefinitions.BotTypes.Clear();
    return (new(), new(), new());
}
var (p, b, enemy) = Setup();
C(p.Blast(b, enemy, new(24.99f, 0, 0)) != null, "unknown owner registered inside 25m");
C(p.ContextClear, "call context restored");
(p, b, enemy) = Setup();
C(p.Blast(b, enemy, new(25, 0, 0)) == null, "source strict radius");
b.EnemyController.Enemies[enemy.ProfileId] = new();
C(p.Blast(b, enemy, new(100, 0, 0)) != null, "known contact outside radius remains native");
(p, b, enemy) = Setup();
b.EnemyController.RequireGroup = true;
C(
    p.Blast(b, enemy, new()) != null && b.BotOwner.BotsGroup.Enemies.ContainsKey(enemy),
    "hostile group fallback"
);
(p, b, enemy) = Setup();
b.Squad.Members[enemy.ProfileId] = 1;
C(p.Blast(b, enemy, new()) == null, "squad owner not registered");
foreach (
    string scope in new[]
    {
        "replica",
        "ghost",
        "disabled",
        "inactive",
        "dead",
        "ending",
        "unsupported",
    }
)
{
    (p, b, enemy) = Setup();
    if (scope == "replica")
        p.Authority = false;
    if (scope == "ghost")
        p.ghosts.Add(b.ProfileId);
    if (scope == "disabled")
        p.config!.Enabled = false;
    if (scope == "inactive")
        b.BotActive = false;
    if (scope == "dead")
        b.IsDead = true;
    if (scope == "ending")
        b.GameEnding = true;
    if (scope == "unsupported")
        b.Supported = false;
    C(p.Blast(b, enemy, new()) == null, "explosion scope " + scope);
}
(p, b, enemy) = Setup();
p.Heard(b, enemy, new(1, 0, 0), new(66, 0, 7));
C(
    p.diagnostics!.Lines.Single().Contains("zzap-owner-position-with-dispersion"),
    "actual native dispersed report logged"
);
C(p.ContextClear, "observer context cleaned");
foreach (int role in new[] { 1, 2, 3 })
{
    (p, b, enemy) = Setup();
    b.Info.Profile.WildSpawnType = role;
    BotTypeDefinitions.BotTypes[role] = new();
    p.Hit(b, enemy);
    C(b.EnemyController.Enemies.Count == 0 && b.Memory.UnderFire == 0, "ordinary role no reveal");
}
foreach (bool brainList in new[] { false, true })
{
    (p, b, enemy) = Setup();
    b.Supported = false;
    b.Info.Profile.WildSpawnType = 777;
    BotTypeDefinitions.BotTypes[777] = brainList
        ? new() { BrainsToApply = new() { "Custom" } }
        : new() { BaseBrain = "Custom" };
    p.Hit(b, enemy);
    var contact = b.EnemyController.Enemies[enemy.ProfileId];
    C(
        contact.KnownPlaces.Reports.Count == 1 && b.Memory.UnderFire == 1,
        "narrow modded role exception"
    );
    var r = contact.KnownPlaces.Reports[0];
    C(
        r.position.x == 70 && r.placeType == EEnemyPlaceType.Injury && !r.shallReportToSquad,
        "source injury exact position, no squad report"
    );
    contact.EnemyKnown = true;
    enemy.Position = new(100, 0, 0);
    p.Hit(b, enemy);
    C(
        contact.KnownPlaces.Reports.Last().position.x == 100,
        "every impact updates source position, logging throttle not gameplay throttle"
    );
    C(!NativeAdapter.IsSupportedAi(b), "custom roles still excluded from other tactics");
}
foreach (
    string scope in new[]
    {
        "replica",
        "ghost",
        "disabled",
        "inactive",
        "dead",
        "ending",
        "ally",
        "dead-shooter",
        "not-hostile",
    }
)
{
    (p, b, enemy) = Setup();
    BotTypeDefinitions.BotTypes[1] = new() { BaseBrain = "Custom" };
    if (scope == "replica")
        p.Authority = false;
    if (scope == "ghost")
        p.ghosts.Add(b.ProfileId);
    if (scope == "disabled")
        p.config!.Enabled = false;
    if (scope == "inactive")
        b.BotActive = false;
    if (scope == "dead")
        b.IsDead = true;
    if (scope == "ending")
        b.GameEnding = true;
    if (scope == "ally")
        b.BotOwner.BotsGroup.Ally = true;
    if (scope == "dead-shooter")
        enemy.HealthController.IsAlive = false;
    if (scope == "not-hostile")
        b.BotOwner.BotsGroup.Hostile = false;
    p.Hit(b, enemy);
    C(b.EnemyController.Enemies.Count == 0, "injury scope " + scope);
}
(p, b, enemy) = Setup();
BotTypeDefinitions.BotTypes[1] = new() { BaseBrain = "Custom" };
b.EnemyController.RequireGroup = true;
p.Hit(b, enemy);
C(b.Memory.UnderFire == 1, "injury group fallback");
p.End();
C(p.ContextClear, "raid cleanup");
var original = HarmonyLib.AccessTools.Method(
    typeof(SAINEnemyController),
    "GetEnemy",
    new[] { typeof(string), typeof(bool) }
);
C(
    Plugin
        .Rewrite(
            new[]
            {
                new HarmonyLib.CodeInstruction(System.Reflection.Emit.OpCodes.Callvirt, original),
            }
        )
        .Single()
        .opcode == System.Reflection.Emit.OpCodes.Call,
    "production native call rewrite"
);
try
{
    Plugin.Rewrite(Array.Empty<HarmonyLib.CodeInstruction>()).ToList();
    throw new Exception("missing hook accepted");
}
catch (InvalidOperationException)
{
    n++;
}
Console.WriteLine(
    $"P07/G10 production adapters: {n} checks passed (controlled native doubles, no raid)."
);
