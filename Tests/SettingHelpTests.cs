using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Comet.Core;
using Comet.Server.Editing;

internal static class SettingHelpTests
{
    public static int Run()
    {
        int checks = 0;
        void Check(bool ok, string message) { checks++; if (!ok) throw new Exception("U08: " + message); }
        var editor = new ConfigEditor(new CometConfig());
        EditorField F(string path) => editor.Catalog.Fields[path];
        string original = JsonSerializer.Serialize(editor.Snapshot());
        foreach (var field in editor.Catalog.Fields.Values)
        {
            bool numeric = !field.Boolean && !field.Text;
            Check(numeric == (field.Unit.Length > 0), "numeric units, without fake units on switches: " + field.Path);
            Check(!field.Simple || field.Help.Length > 0, "simple control explains its effect: " + field.Path);
            Check(!Regex.IsMatch(field.Help, @"\bdefaults?\s*[:\d±]", RegexOptions.IgnoreCase), "no stale default number in help: " + field.Path);
            Check(!(field.Label + field.Help + field.Example).Contains('\ufffd'), "no broken characters: " + field.Path);
        }
        foreach (var section in editor.Catalog.Sections.Values)
            Check(!Regex.IsMatch(section.Description, @"\bdefaults?\s*:?\s*\d", RegexOptions.IgnoreCase), "section help has no duplicate numeric defaults");

        // Reference numbers must come from each real profile, not from the current
        // edited or saved value. Walk all fields, including scaled dictionaries.
        foreach (string level in new[] { "Easy", "Normal", "Hard" })
        {
            editor.Apply(editor.PreviewProfile(level));
            var values = JsonSerializer.SerializeToNode(DifficultyPresets.Create(level))!;
            foreach (var field in editor.Catalog.Fields.Values)
            {
                var node = values;
                foreach (var part in field.Path.Split('.')) node = node![part]!;
                string expected = field.Boolean ? (node.GetValue<bool>() ? "On" : "Off")
                    : field.Text ? node.GetValue<string>()
                    : (node.GetValue<double>() * field.Scale).ToString("G15", CultureInfo.InvariantCulture) + " " + (field.Unit == "0-1" ? "(0-1)" : field.Unit);
                Check(editor.DefaultValue(field) == expected, level + " actual default: " + field.Path);
            }
            string draft = JsonSerializer.Serialize(editor.Snapshot());
            editor.Compare(level == "Easy" ? "Hard" : "Easy");
            Check(JsonSerializer.Serialize(editor.Snapshot()) == draft, "comparison and reference lookup do not change draft");
        }
        editor.Apply(editor.PreviewDiscard());
        Check(JsonSerializer.Serialize(editor.Snapshot()) == original, "discard restores original including base profile");

        var peek = F("CoverFeint.PeekChance");
        editor.Apply(editor.PreviewProfile("Normal"));
        editor.Set(peek, "41.25");
        var custom = editor.Snapshot();
        custom.PresetSource = new PresetIdentity { Id = Guid.NewGuid().ToString("N"), Name = "Custom U08", Revision = 1, SettingsHash = PresetIdentity.Fingerprint(custom) };
        editor.AcceptSaved(custom);
        editor.Compare("Saved");
        Check(editor.ReferenceValue(peek) == "41.25 %" && editor.DefaultValue(peek) == "26.25 %", "personal saved value is not the Normal default");
        editor.Set(peek, "23.5"); editor.Compare("Easy");
        Check(editor.ReferenceValue(peek) == "17.5 %" && editor.DefaultValue(peek) == "26.25 %" && editor.Input(peek) == "23.5", "three distinct values: comparison, base default, input");
        var restore = editor.PreviewSection(peek.Section);
        Check(restore.Changes.Any(c => c.Path == peek.Path && c.Before == "23.5 %" && c.After == "17.5 %"), "review values carry units");
        editor.Compare("Hard"); editor.Apply(restore);
        Check(editor.Input(peek) == "17.5" && editor.DefaultValue(peek) == "26.25 %", "frozen restore remains independent from new comparison");
        editor.Set(peek, "101");
        Check(editor.Input(peek) == "101" && editor.Error(peek).Contains("0 to 100 %") && !editor.CanSave, "invalid input stays visible with unit-aware error");
        Check(editor.DefaultValue(peek) == "26.25 %", "invalid draft cannot corrupt reference");
        editor.Apply(editor.PreviewDiscard());
        Check(editor.PresetLabel == "Custom U08 · v1" && editor.DefaultValue(peek) == "26.25 %", "discard restores personal identity and correct base reference");

        var percent = F("ThreatSelection.SwitchMargin");
        Check(editor.Set(percent, "22.5") && editor.Snapshot().ThreatSelection.SwitchMargin == .225, "percentage scaled exactly once");
        Check(editor.Input(percent) == "22.5" && editor.DisplayValue(percent) == "22.5 %", "units never enter number input");
        var fraction = F("MedicalChoice.StimHealth");
        Check(editor.Input(fraction) == "0.35" && editor.DefaultValue(fraction) == "0.35 (0-1)", "existing fraction not silently changed into percent");
        Check(editor.DefaultValue(F("MovementHearing.RangeMultiplier")) == "1 x", "multiplier distinct from probability");
        Check(editor.DefaultValue(F("Enabled")) == "On", "boolean stays readable");
        Check(editor.DefaultValue(F("SquadVoice.Mode")) == "Normal", "text mode stays readable");
        Check(editor.Visible(false, "ceiling", false).Any(f => f.Path == "QuietApproach.LootBonus"), "help searchable in simple view");
        Check(editor.Visible(false, "roughly one in four", false).Any(f => f.Path == peek.Path), "example searchable without changing detail mode");

        var culture = CultureInfo.CurrentCulture;
        try {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            Check(editor.DefaultValue(peek) == "26.25 %", "English reference values remain stable on a French server");
        } finally { CultureInfo.CurrentCulture = culture; }
        Console.WriteLine($"U08 setting help checks passed: {checks}");
        return checks;
    }
}
