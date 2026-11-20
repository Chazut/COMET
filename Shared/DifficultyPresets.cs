using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace Comet.Core;

public sealed class DifficultyPresetTrace
{
    public string? PresetName { get; set; }
    public string? PresetId { get; set; }
    public long? PresetRevision { get; set; }
    public bool? ModifiedSincePreset { get; set; }
    public string BaseProfile { get; set; } = "Hard";
    public int CatalogVersion { get; set; } = DifficultyPresets.Version;
    public bool Customized { get; set; }
    public int ChangedSettings { get; set; }
}

// Materialized COMET configuration, not an extra runtime difficulty multiplier.
// Hard is exactly the existing defaults. Lower profiles only change this explicit list.
public static class DifficultyPresets
{
    public const int Version = 1;

    public static bool IsKnown(string? name) =>
        name == "Hard" || name == "Normal" || name == "Easy";

    internal static double ChanceFactor(string? name) => name switch
    {
        "Normal" => .75,
        "Easy" => .5,
        _ => 1,
    };

    public static CometConfig Create(string name)
    {
        if (!IsKnown(name))
            throw new ArgumentException("Unknown COMET difficulty preset.", nameof(name));
        var c = new CometConfig { DifficultyPreset = name };
        if (name == "Hard")
            return c;
        double chance = ChanceFactor(name);
        double delay = name == "Normal" ? 1.25 : 1.5;
        void Chances(Dictionary<string, double> values)
        {
            foreach (string key in values.Keys.ToArray())
                values[key] *= chance;
        }

        // Door plans share their admission roll. D05/D06 share one feint roll.
        // Style preferences are unchanged, avoiding a second roll or changing the mix.
        Chances(c.DoorJumpPeek.Chances);
        c.DoorJumpPeek.Cooldown *= delay;
        Chances(c.DoorFakeGrenade.Chances);
        c.DoorFakeGrenade.Cooldown *= delay;
        c.DoorFakeGrenade.TargetCooldown *= delay;
        Chances(c.DoorRoomClear.Chances);
        c.DoorRoomClear.TargetCooldown *= delay;
        c.DoorAmbush.TargetCooldown *= delay;

        Chances(c.CornerChase.Chances);
        c.CornerChase.Cooldown *= delay;
        c.CornerPrefire.Chance *= chance;
        c.CornerPeek.Chance *= chance;
        c.CornerPeek.CooldownMin *= delay;
        c.CornerPeek.CooldownMax *= delay;
        c.CombatLean.Chance *= chance;
        c.CombatLean.CooldownMin *= delay;
        c.CombatLean.CooldownMax *= delay;
        c.CoverFeint.PeekChance *= chance;
        c.CoverFeint.MagazineChance *= chance;
        c.CoverFeint.RetryDelay *= delay;
        c.OutdoorRelocation.Chance *= chance;
        c.OutdoorRelocation.RetryDelay *= delay;
        c.TacticalJump.AirShotChance *= chance;

        // Scale positive grenade incentives together. Keep resource penalties and
        // trajectory, safety and observation requirements identical in every profile.
        c.GrenadeJudgment.BaseChance *= chance;
        c.GrenadeJudgment.CampBonus *= chance;
        c.GrenadeJudgment.RoomBonus *= chance;
        c.GrenadeJudgment.VulnerableBonus *= chance;
        c.GrenadeJudgment.RetryMin *= delay;
        c.GrenadeJudgment.RetryMax *= delay;

        c.SquadManeuver.Chance *= chance;
        c.SquadManeuver.Retry *= delay;
        c.SquadStorm.GigaChad *= chance;
        c.SquadStorm.Chad *= chance;
        c.SquadStorm.Wreckless *= chance;
        c.SquadStorm.Normal *= chance;
        c.SquadStorm.Rat *= chance;
        c.SquadStorm.SnappingTurtle *= chance;
        c.SquadStorm.Coward *= chance;
        c.SquadStorm.Timmy *= chance;
        c.SquadStorm.Retry *= delay;
        c.SecondaryEntrance.GigaChad *= chance;
        c.SecondaryEntrance.Chad *= chance;
        c.SecondaryEntrance.Wreckless *= chance;
        c.SecondaryEntrance.Normal *= chance;
        c.SecondaryEntrance.Rat *= chance;
        c.SecondaryEntrance.SnappingTurtle *= chance;
        c.SecondaryEntrance.Coward *= chance;
        c.SecondaryEntrance.Timmy *= chance;
        c.SecondaryEntrance.Retry *= delay;
        c.Validate();
        return c;
    }

    // Editor/load-time only, never run per bot or frame. Include all configurable
    // values so a custom setup cannot silently be presented as an untouched preset.
    public static DifficultyPresetTrace Describe(CometConfig config)
    {
        var expected = Values(Create(config.DifficultyPreset));
        var actual = Values(config);
        int changes = actual
            .Keys.Union(expected.Keys)
            .Count(k =>
                !actual.TryGetValue(k, out var a)
                || !expected.TryGetValue(k, out var b)
                || !Equals(a, b)
            );
        return new DifficultyPresetTrace
        {
            BaseProfile = config.DifficultyPreset,
            PresetName = config.PresetSource?.Name,
            PresetId = config.PresetSource?.Id,
            PresetRevision = config.PresetSource?.Revision,
            ModifiedSincePreset =
                config.PresetSource == null
                    ? null
                    : PresetIdentity.Fingerprint(config) != config.PresetSource.SettingsHash,
            Customized = changes > 0,
            ChangedSettings = changes,
        };
    }

    internal static SortedDictionary<string, object?> Values(CometConfig config)
    {
        var values = new SortedDictionary<string, object?>(StringComparer.Ordinal);
        void Visit(object? value, string path)
        {
            if (value == null)
            {
                values[path] = null;
                return;
            }
            var type = value.GetType();
            if (type.IsPrimitive || type.IsEnum || value is string || value is decimal)
            {
                values[path] = value;
                return;
            }
            if (value is IDictionary dictionary)
            {
                if (dictionary.Count == 0)
                    values[path] = "empty-dictionary";
                foreach (DictionaryEntry entry in dictionary)
                    Visit(entry.Value, path + "[" + entry.Key + "]");
                return;
            }
            if (value is IEnumerable sequence)
            {
                int index = 0;
                foreach (var entry in sequence)
                    Visit(entry, path + "[" + index++ + "]");
                if (index == 0)
                    values[path] = "empty-sequence";
                return;
            }
            foreach (
                var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            )
            {
                if (
                    !property.CanRead
                    || !property.CanWrite
                    || property.GetIndexParameters().Length != 0
                    || SourceBaselineSettings.IsRetired(type, property.Name)
                )
                    continue;
                if (
                    path.Length == 0
                    && (
                        property.Name == nameof(CometConfig.BehaviorBaseline)
                        || property.Name == nameof(CometConfig.Revision)
                        || property.Name == nameof(CometConfig.DifficultyPreset)
                        || property.Name == nameof(CometConfig.PresetSource)
                    )
                )
                    continue;
                Visit(
                    property.GetValue(value),
                    path.Length == 0 ? property.Name : path + "." + property.Name
                );
            }
        }
        Visit(config, "");
        return values;
    }
}
