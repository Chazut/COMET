namespace Comet.Server.Editing;

public sealed record EditorTopic(string Key, string Title, string Description, string Icon);

// Presentation only: never changes configuration, eligibility or tactical defaults.
public static class EditorNavigation
{
    public static readonly EditorTopic[] Topics = {
        new("Overview", "General", "Choose who COMET controls and how it fits into your setup.", "settings"),
        new("Decisions", "Combat decisions", "Hold an angle, apply pressure, search or fall back as the fight develops.", "target"),
        new("Perception", "Awareness & search", "React to sights and sounds, investigate contacts and approach quietly.", "radar"),
        new("Movement", "Movement & cover", "Peek corners, move between cover and reposition during a fight.", "route"),
        new("Doors", "Doors & room entry", "Watch doorways, clear rooms and use different approaches at an entrance.", "door"),
        new("Weapons & lights", "Weapons & lights", "Manage reloads, weapon swaps, flashlights and tactical devices.", "crosshair"),
        new("Grenades", "Grenades", "Choose a grenade, plan a throw and react to nearby explosions.", "grenade"),
        new("Squad", "Squad tactics", "Cover teammates, find new angles and coordinate pressure on an enemy.", "squad"),
        new("Medical", "Healing & recovery", "Choose when to heal, perform surgery and recover after combat.", "medical"),
        new("Recovery", "Getting unstuck", "Recover from blocked doors, interrupted actions and movement problems.", "recovery"),
        new("Diagnostics", "Logs & diagnostics", "Control local logging, performance recording and test reports.", "chart")
    };

    public static EditorTopic Topic(string key) => Topics.First(t => t.Key == key);
    public static string Title(string value) => System.Text.RegularExpressions.Regex.Replace(value, @"\s*[-·]\s*[A-Z]\d{2}.*$", "");
    public static IEnumerable<EditorField> Primary(IEnumerable<EditorField> fields) => fields.Where(f =>
        f.Simple || f.Boolean && (f.Path.EndsWith(".Enabled", StringComparison.Ordinal) || f.Path == "Enabled"));
    public static List<EditorField> Select(ConfigEditor editor, string category, string search, bool modifiedOnly) =>
        editor.Visible(true, search, modifiedOnly).Where(f => !string.IsNullOrWhiteSpace(search) || modifiedOnly ||
            editor.Catalog.Sections[f.Section].Category == category).ToList();
}
