using SAIN;
using SAIN.Preset;
using SAIN.Preset.Shared.GlobalSettings;
using Comet.Core;

namespace Comet.Client;

// LoadedPreset is the selected preset. Constructing/deserializing another bundle
// changes GlobalSettingsClass.Instance, even when that bundle is never selected.
// Read at the point of use: neither an editor preview nor a cached earlier preset
// can grant COMET permission. Never repair SAIN's process-wide singleton here.
internal static class SainPresetAccess
{
    internal static bool AllowsJump => SAINPlugin.LoadedPreset?.GlobalSettings?.Move?.JUMP_TOGGLE == true;
    internal static bool AllowsLean => SAINPlugin.LoadedPreset?.GlobalSettings?.Move?.LEAN_TOGGLE == true;
    internal static bool AllowsSuppression => SAINPlugin.LoadedPreset?.GlobalSettings?.Mind?.TARGET_SUPPRESS_TOGGLE == true;
    internal static bool AllowsGrenades(bool againstAi = false)
    {
        var general = SAINPlugin.LoadedPreset?.GlobalSettings?.General;
        return general?.BotsUseGrenades == true && (!againstAi || general.BotVsBotGrenade);
    }
    internal static SainPresetTrace Describe(SAINPresetClass? selected)
    {
        var global = selected?.GlobalSettings;
        string name = selected?.Info?.Name ?? "Unknown";
        return new() {
            Name = name.Length > 160 ? name.Substring(0,160) : name,
            Availability = selected == null ? "preset-missing" : global == null ? "globals-missing"
                : global.Move == null || global.Mind == null || global.General == null ? "settings-incomplete" : "ready",
            SingletonMatches = global != null && ReferenceEquals(global,GlobalSettingsClass.Instance),
            Jump = global?.Move?.JUMP_TOGGLE == true, Lean = global?.Move?.LEAN_TOGGLE == true,
            Grenades = global?.General?.BotsUseGrenades == true,
            AiGrenades = global?.General?.BotsUseGrenades == true && global.General.BotVsBotGrenade,
            Suppression = global?.Mind?.TARGET_SUPPRESS_TOGGLE == true
        };
    }
}
