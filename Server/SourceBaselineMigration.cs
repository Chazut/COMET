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
        string? Existing(string name) =>
            document
                .Select(x => x.Key)
                .FirstOrDefault(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase));
        var marker = Existing(nameof(CometConfig.BehaviorBaseline)) ?? nameof(CometConfig.BehaviorBaseline);
        var baseline = document[marker]?.GetValue<string>();
        if (baseline == CometConfig.SourceBaseline)
            return false;
        if (baseline == "zzap-fbb86b83-2")
        {
            // This revision changes only the door admission defaults in persisted settings.
            // Preserve every other setting and each personality value the user tuned.
            if (document[Existing(nameof(CometConfig.DoorJumpPeek)) ?? nameof(CometConfig.DoorJumpPeek)] is JsonObject module
                && module.FirstOrDefault(x => x.Key.Equals("Chances", StringComparison.OrdinalIgnoreCase)).Value is JsonObject chances)
            {
                var previous = new Dictionary<string, double>
                {
                    ["GigaChad"] = 35, ["Chad"] = 25, ["Wreckless"] = 15,
                    ["Normal"] = 10, ["Rat"] = 0, ["SnappingTurtle"] = 0,
                };
                var current = new DoorJumpPeekConfig().Chances;
                var profile = document[Existing(nameof(CometConfig.DifficultyPreset)) ?? nameof(CometConfig.DifficultyPreset)]?.GetValue<string>();
                double factor = DifficultyPresets.ChanceFactor(profile);
                foreach (var (key, value) in previous)
                {
                    if (chances[key] is JsonValue saved && saved.TryGetValue<double>(out var number) && number == value * factor)
                        chances[key] = current[key] * factor;
                }
            }
            document[marker] = CometConfig.SourceBaseline;
            return true;
        }
        var defaults = JsonSerializer.SerializeToNode(new CometConfig())!.AsObject();
        var preserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Schema",
            "Revision",
            "Enabled",
            "QuietLogging",
            "Performance",
            "CombatJournal",
            "ControlJournal",
            "WeaponJournal",
            "TacticCounters",
            "FaultIsolation",
        };
        foreach (var (key, value) in defaults)
        {
            if (preserved.Contains(key))
                continue;
            // Respect deliberate module exclusions, including optional OFF features.
            var oldKey = Existing(key);
            bool? enabled =
                oldKey != null && document[oldKey] is JsonObject previous
                    ? previous
                        .FirstOrDefault(x =>
                            string.Equals(x.Key, "Enabled", StringComparison.OrdinalIgnoreCase)
                        )
                        .Value?.GetValue<bool>()
                    : null;
            if (oldKey != null)
                document.Remove(oldKey);
            document[key] = value?.DeepClone();
            if (
                enabled.HasValue
                && document[key] is JsonObject module
                && module.ContainsKey("Enabled")
            )
                module["Enabled"] = enabled.Value;
        }
        document[nameof(CometConfig.PresetSource)] = null;
        document[nameof(CometConfig.DifficultyPreset)] = "Hard";
        return true;
    }
}
