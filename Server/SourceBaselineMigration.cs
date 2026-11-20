using System.Text.Json;
using System.Text.Json.Nodes;
using Comet.Core;

namespace Comet.Server;

// Saved settings from the original COMET policies must not silently restore their
// behavioural limits. The caller archives the original file before persisting.
internal static class SourceBaselineMigration
{
    private static string Key(JsonObject target, string name) =>
        target.FirstOrDefault(p => p.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Key ?? name;

    // Prior configs could omit the grenade lifetime while setting a contact memory below 20 s.
    internal static double MissingGrenadeLifetime(double? contactMemory) =>
        contactMemory < GrenadeJudgmentConfig.DefaultMaxInfoAge ? 8 : GrenadeJudgmentConfig.DefaultMaxInfoAge;

    private static (double Age, double Distance) GrenadeWindows(JsonObject original, bool personalPreset)
    {
        var age = original[Key(original, nameof(CometConfig.GrenadeContactLifetime))]?.Deserialize<double>();
        var memory = original[Key(original, nameof(CometConfig.ContactLifetime))]?.Deserialize<double>();
        var trajectory = original[Key(original, nameof(CometConfig.GrenadeTrajectory))] as JsonObject;
        var distance = trajectory?[Key(trajectory, nameof(GrenadeTrajectoryConfig.Distance))]?.Deserialize<double>();
        if (!personalPreset && original[Key(original, nameof(CometConfig.PresetSource))] == null
            && (age == 8 || distance == 40) && age is (null or 8) && distance is (null or 40))
        {
            var comparison = original.Deserialize<CometConfig>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
            // Compare against the built-in profile with just its two former defaults normalized.
            comparison.GrenadeContactLifetime = GrenadeJudgmentConfig.DefaultMaxInfoAge;
            if (comparison.GrenadeTrajectory != null && DifficultyPresets.IsKnown(comparison.DifficultyPreset))
            {
                comparison.GrenadeTrajectory.Distance = GrenadeTrajectoryConfig.DefaultDistance;
                if (!DifficultyPresets.Describe(comparison).Customized)
                    return (comparison.GrenadeContactLifetime, comparison.GrenadeTrajectory.Distance);
            }
        }
        return (age ?? MissingGrenadeLifetime(memory), distance ?? GrenadeTrajectoryConfig.DefaultDistance);
    }

    internal static bool Upgrade(JsonObject document, JsonObject? importedSettings = null)
    {
        string Existing(string name) => Key(document, name);
        var marker = Existing(nameof(CometConfig.BehaviorBaseline));
        var baseline = document[marker]?.GetValue<string>();
        if (baseline == CometConfig.SourceBaseline)
            return false;
        var original = importedSettings ?? document;
        var (nextAge, nextDistance) = GrenadeWindows(original, importedSettings != null);
        if (baseline == "zzap-d8655293-1" || baseline == "zzap-fbb86b83-2")
        {
            document[Existing(nameof(CometConfig.GrenadeContactLifetime))] = nextAge;
            if (document[Existing(nameof(CometConfig.GrenadeTrajectory))] is JsonObject currentTrajectory)
                currentTrajectory[Key(currentTrajectory, nameof(GrenadeTrajectoryConfig.Distance))] = nextDistance;
        }
        if (baseline == "zzap-d8655293-1")
        {
            document[marker] = CometConfig.SourceBaseline;
            return true;
        }
        if (baseline == "zzap-fbb86b83-2")
        {
            // This revision changes only the door admission defaults in persisted settings.
            // Preserve every other setting and each personality value the user tuned.
            if (document[Existing(nameof(CometConfig.DoorJumpPeek))] is JsonObject module
                && module.FirstOrDefault(x => x.Key.Equals("Chances", StringComparison.OrdinalIgnoreCase)).Value is JsonObject chances)
            {
                var previous = new Dictionary<string, double>
                {
                    ["GigaChad"] = 35, ["Chad"] = 25, ["Wreckless"] = 15,
                    ["Normal"] = 10, ["Rat"] = 0, ["SnappingTurtle"] = 0,
                };
                var current = new DoorJumpPeekConfig().Chances;
                var profile = document[Existing(nameof(CometConfig.DifficultyPreset))]?.GetValue<string>();
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
        var legacyDefaults = new CometConfig { GrenadeContactLifetime = nextAge };
        legacyDefaults.GrenadeTrajectory.Distance = nextDistance;
        if (nextAge > legacyDefaults.ContactLifetime && original[Key(original, nameof(CometConfig.ContactLifetime))] is JsonNode memory)
            legacyDefaults.ContactLifetime = memory.Deserialize<double>();
        if (legacyDefaults.GrenadeJudgment.FadeStart > nextAge - .1
            && original[Key(original, nameof(CometConfig.GrenadeJudgment))] is JsonObject judgment
            && judgment[Key(judgment, nameof(GrenadeJudgmentConfig.FadeStart))] is JsonNode fade)
            legacyDefaults.GrenadeJudgment.FadeStart = fade.Deserialize<double>();
        var defaults = JsonSerializer.SerializeToNode(legacyDefaults)!.AsObject();
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
                document[oldKey] is JsonObject previous
                    ? previous
                        .FirstOrDefault(x =>
                            string.Equals(x.Key, "Enabled", StringComparison.OrdinalIgnoreCase)
                        )
                        .Value?.GetValue<bool>()
                    : null;
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
