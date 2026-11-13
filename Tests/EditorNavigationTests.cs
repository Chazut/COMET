using Comet.Core;
using Comet.Server.Editing;
using System.Text.Json;

internal static class EditorNavigationTests
{
    public static int Run()
    {
        int checks = 0;
        void Check(bool ok, string why) { checks++; if (!ok) throw new Exception("UI navigation: " + why); }
        var config = new CometConfig();
        var editor = new ConfigEditor(config);
        string before = JsonSerializer.Serialize(editor.Snapshot());
        var reached = new HashSet<string>();
        foreach (var topic in EditorNavigation.Topics) {
            var fields = EditorNavigation.Select(editor, topic.Key, "", false);
            Check(fields.Count > 0, "category has content: " + topic.Key);
            Check(fields.All(f => editor.Catalog.Sections[f.Section].Category == topic.Key), "category isolates its settings");
            foreach (var field in fields) Check(reached.Add(field.Path), "each field has exactly one navigation home");
            foreach (var group in fields.GroupBy(f => f.Section))
                Check(EditorNavigation.Primary(group).All(group.Contains), "essentials never introduce another feature's controls");
        }
        Check(reached.SetEquals(editor.Catalog.Fields.Keys), "every advanced field is reachable through a category");
        Check(JsonSerializer.Serialize(editor.Snapshot()) == before && !editor.HasPending, "browsing cannot change any value");
        var quiet = editor.Catalog.Fields["QuietLogging"];
        Check(EditorNavigation.Select(editor, "Doors", "quiet logging", false).Single().Path == quiet.Path, "search reaches another category without enabling advanced mode");
        var advanced = editor.Catalog.Fields.Values.First(f => !f.Simple && !f.Boolean && !f.Text);
        editor.Set(advanced, advanced.Min?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "0");
        if (!editor.Pending(advanced)) editor.Set(advanced, advanced.Max?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "1");
        editor.Set(quiet, "true");
        var differences = EditorNavigation.Select(editor, "Overview", "", true);
        Check(differences.Any(f => f.Path == quiet.Path), "differences include other categories");
        Check(differences.Any(f => f.Path == advanced.Path), "differences include advanced controls");
        editor.Set(advanced, "bad input");
        Check(EditorNavigation.Select(editor, "Overview", "", true).Any(f => f.Path == advanced.Path), "invalid fields remain reachable");
        Check(!editor.CanSave, "invalid input cannot be saved through review");
        editor.Restore(advanced);
        var review = editor.PendingChanges();
        var change = review.Single(c => c.Path == quiet.Path);
        Check(change.Before == "Off" && change.After == "On", "save review runs from saved value to draft, unlike discard");
        editor.Compare("Easy");
        Check(editor.PendingChanges().SequenceEqual(review), "comparison never contaminates save review");
        editor.Set(quiet, "false");
        Check(review.Single(c => c.Path == quiet.Path).After == "On", "review is detached from subsequent edits");
        Check(editor.PendingChanges().Count == 0, "restoring edits clears save review");
        editor.Apply(editor.PreviewProfile("Easy"));
        Check(editor.PendingChanges().Any(c => c.Path == "DifficultyPreset" && c.Before == "Hard" && c.After == "Easy"), "review includes base profile changes");
        Check(!config.QuietLogging && config.DifficultyPreset == "Hard", "the supplied configuration is never mutated");
        Console.WriteLine($"UI navigation and review checks passed: {checks}");
        return checks;
    }
}
