using Comet.Client;
using Comet.Core;
using EFT;

int checks = 0;
void Check(bool value, string why)
{
    checks++;
    if (!value)
        throw new Exception(why);
}
Player Bot(WildSpawnType role, bool ai, string? main, string? nickname) =>
    new()
    {
        IsAI = ai,
        Profile = new()
        {
            Info = new()
            {
                Settings = new() { Role = role },
                MainProfileNickname = main,
                Nickname = nickname,
            },
        },
    };
var config = new CometConfig();
foreach (var role in Enum.GetValues<WildSpawnType>())
foreach (bool ai in new[] { false, true })
foreach (string? main in new[] { null, "", "Player" })
foreach (string? nickname in new[] { null, "Scav", "Scav (Player)" })
{
    var player = Bot(role, ai, main, nickname);
    bool pmc = role is WildSpawnType.pmcUSEC or WildSpawnType.pmcBEAR;
    bool pscav = role == WildSpawnType.assault && (main == "Player" || nickname == "Scav (Player)");
    string expected =
        !ai ? ""
        : pmc ? "pmc"
        : pscav ? "player-scav"
        : "";
    Check(BotEligibility.Kind(player) == expected, $"identity {role}/{ai}/{main}/{nickname}");
    bool eligible = BotEligibility.Supports(player, config);
    Check(eligible == (expected.Length != 0), "support and identity agree");
    foreach (bool master in new[] { false, true })
    foreach (bool pmcs in new[] { false, true })
    foreach (bool scavPlayers in new[] { false, true })
    {
        var settings = new CometConfig { Enabled = master, EnablePmcs = pmcs, EnableAiPlayerScavs = scavPlayers };
        bool excluded = expected == "pmc" && !pmcs || expected == "player-scav" && !scavPlayers;
        Check(BotEligibility.Supports(player, settings) == (master && eligible && !excluded),
            $"independent switches: {expected}/{master}/{pmcs}/{scavPlayers}");
        Check(BotEligibility.RoleDisabled(player, settings) == excluded, "broader native fixes only bypass opted-out actors");
    }
    // Actual shared tactical policies must use the eligible cohort, not the native assault role.
    var frame = new ThreatFrame
    {
        IsAi = ai,
        IsSupportedBot = eligible,
        IsAuthority = true,
        Active = true,
    };
    Check(ThreatSelection.Scope(frame) == eligible, "threat selection matches scope");
    frame.Ghost = true;
    Check(!ThreatSelection.Scope(frame), "ghosts excluded");
    frame.Ghost = false;
    frame.IsAuthority = false;
    Check(!ThreatSelection.Scope(frame), "Fika clients excluded");
    frame.IsAuthority = true;
    frame.NativeUrgency = true;
    Check(
        ThreatSelection.Scope(frame) == eligible,
        "target selection remains available during native urgency"
    );
}
Check(!BotEligibility.Supports(null, config), "missing player");
var missing = new Player { Profile = null };
Check(!BotEligibility.Supports(missing, config), "missing profile");
missing.Profile = new() { Info = null };
Check(!BotEligibility.Supports(missing, config), "missing info");
missing.Profile.Info = new() { Settings = null };
Check(!BotEligibility.Supports(missing, config), "missing settings");
var changing = Bot(WildSpawnType.assault, true, null, "Scav");
Check(!BotEligibility.Supports(changing, config), "ordinary scav initially excluded");
changing.Profile!.Info!.MainProfileNickname = "Player";
Check(BotEligibility.Supports(changing, config), "late profile initialization not permanently cached out");
changing.IsAI = false;
Check(!BotEligibility.Supports(changing, config), "human control never inherits eligibility");
changing.IsAI = true;
changing.Profile.Info.Settings!.Role = WildSpawnType.bossKilla;
Check(!BotEligibility.Supports(changing, config), "changed role never inherits eligibility");
Console.WriteLine(
    $"Bot scope: {checks} checks. Production classifier and threat policy; game objects doubled, no Unity raid."
);
