using System.Text.Json;
using System.Text.Json.Nodes;
using Comet.Core;

namespace Comet.Server;

// Saved settings from the original COMET policies must not silently restore their
// behavioural limits. The caller archives the original file before persisting.
internal static class SourceBaselineMigration
{
    internal static bool Upgrade(JsonObject document)
    {
        string? Existing(string name) => document.Select(x=>x.Key).FirstOrDefault(k=>string.Equals(k,name,StringComparison.OrdinalIgnoreCase));
        if (document[Existing(nameof(CometConfig.BehaviorBaseline)) ?? nameof(CometConfig.BehaviorBaseline)]?.GetValue<string>() == CometConfig.SourceBaseline) return false;
        var defaults = JsonSerializer.SerializeToNode(new CometConfig())!.AsObject();
        var preserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
            "Schema", "Revision", "Enabled", "QuietLogging", "Performance", "CombatJournal", "ControlJournal",
            "WeaponJournal", "TacticCounters", "FaultIsolation"
        };
        foreach (var (key, value) in defaults)
        {
            if (preserved.Contains(key)) continue;
            // Respect deliberate module exclusions, including optional OFF features.
            var oldKey=Existing(key);
            bool? enabled = oldKey != null && document[oldKey] is JsonObject previous ? previous.FirstOrDefault(x=>string.Equals(x.Key,"Enabled",StringComparison.OrdinalIgnoreCase)).Value?.GetValue<bool>() : null;
            if(oldKey != null)document.Remove(oldKey);
            document[key] = value?.DeepClone();
            if (enabled.HasValue && document[key] is JsonObject module && module.ContainsKey("Enabled")) module["Enabled"] = enabled.Value;
        }
        document[nameof(CometConfig.PresetSource)] = null;
        document[nameof(CometConfig.DifficultyPreset)] = "Hard";
        return true;
    }
}
