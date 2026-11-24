using Comet.Core;
using EFT;

namespace Comet.Client;

// Shared by combat, lifecycle and low-level callbacks. Humans are never eligible,
// including Fika guests and the local player's scav character.
internal static class BotEligibility
{
    internal static string Kind(Player? player)
    {
        if (player == null || !player.IsAI)
            return "";
        var info = player.Profile?.Info;
        if (info?.Settings == null)
            return "";
        var role = info.Settings.Role;
        if (role == WildSpawnType.pmcUSEC || role == WildSpawnType.pmcBEAR)
            return "pmc";
        // SPT's simulated Player Scavs share assault with ordinary scavs.
        // ORBIT recognizes MainProfileNickname and the older "Scav (Player)"
        // nickname format. Require assault for both so custom/boss roles stay out.
        if (
            role == WildSpawnType.assault
            && (
                !string.IsNullOrEmpty(info.MainProfileNickname)
                || info.Nickname?.Contains(" (") == true
            )
        )
            return "player-scav";
        return "";
    }

    internal static bool Supports(Player? player, CometConfig? config) =>
        config?.Enabled == true && Kind(player) switch
        {
            "pmc" => config.EnablePmcs,
            "player-scav" => config.EnableAiPlayerScavs,
            _ => false,
        };

    // Native fixes that also cover ordinary scavs or custom roles keep their wider scope.
    internal static bool RoleDisabled(Player? player, CometConfig config) =>
        (!config.EnablePmcs || !config.EnableAiPlayerScavs) && Kind(player) switch
        {
            "pmc" => !config.EnablePmcs,
            "player-scav" => !config.EnableAiPlayerScavs,
            _ => false,
        };
}
