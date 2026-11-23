using System.Globalization;
using System.Text.Json;
using Comet.Core;
using Comet.Server;
using Comet.Server.Editing;

internal static class ConfigEditorTests
{
    public static int Run()
    {
        int checks = 0;
        void Check(bool condition, string why)
        {
            checks++;
            if (!condition)
                throw new Exception("U05: " + why);
        }
        void Reject(Action action, string why)
        {
            bool failed = false;
            try
            {
                action();
            }
            catch (InvalidOperationException)
            {
                failed = true;
            }
            Check(failed, why);
        }
        var original = new CometConfig();
        var before = JsonSerializer.Serialize(original);
        var editor = new ConfigEditor(original);
        EditorField Field(string path) => editor.Catalog.Fields[path];
        Check(!editor.Catalog.Fields.ContainsKey("SecondaryEntrance.Retry")
            && editor.Catalog.Fields.ContainsKey("Crossfire.Retry"),
            "only the working shared cooldown is exposed for secondary entrance retries");
        var editablePaths = DifficultyPresets.Values(original).Keys
            .Except(new[]
            {
                "Schema", "PushMaxDistance", "OutdoorRelocation.RetryDelay", "SquadStorm.MinimumAttackers",
                "SquadStorm.PrepareTimeout", "SquadStorm.Duration",
            })
            .Select(path => path.Replace('[', '.').Replace("]", ""));
        Check(
            editor.Catalog.Fields.Keys.ToHashSet().SetEquals(editablePaths),
            "the editor exposes all current controls and omits legacy preset fields"
        );
        Check(
            editor.Catalog.Fields.Keys.All(p =>
                p is not "Schema" and not "Revision" and not "DifficultyPreset"
            ),
            "metadata cannot be edited as raw fields"
        );
        Check(
            editor.Visible(false, "", false).Count() <= 19,
            "simple mode is deliberately short, including Quiet logging"
        );
        Check(
            editor.Visible(true, "", false).Count() == editor.Catalog.Fields.Count,
            "advanced mode has every control"
        );
        Check(
            !editor.HasPending && !editor.CanSave && !editor.Customized,
            "opening editor does not change defaults"
        );
        var invalidDefaults = new List<string>();
        foreach (var f in editor.Catalog.Fields.Values)
        {
            Check(
                editor.Catalog.Sections.TryGetValue(f.Section, out var s)
                    && s.Title.Length > 0
                    && s.Description.Length > 0,
                "readable feature context: " + f.Path
            );
            Check(
                f.Label.Length > 0 && !f.Label.Contains('@') && !f.Label.Contains('\ufffd'),
                "clean label: " + f.Path
            );
            Check(f.Scale > 0, "positive display scale");
            Check(
                !s!.Description.Contains('@')
                    && !s.Title.Contains('\u00c3')
                    && s.Summary.Length > 0,
                "no raw Razor or broken encoding in help: " + f.Path
            );
            if (!editor.Set(f, editor.Input(f)))
                invalidDefaults.Add(f.Path + " / " + editor.Error(f));
        }
        Check(
            invalidDefaults.Count == 0,
            "invalid default controls: " + string.Join("; ", invalidDefaults)
        );
        Check(
            editor.PendingCount == 0 && editor.ValidationError == "",
            "all original values round-trip without changes"
        );
        foreach (string level in new[] { "Easy", "Normal", "Hard" })
        {
            var profile = new ConfigEditor(DifficultyPresets.Create(level));
            foreach (var f in profile.Catalog.Fields.Values)
                Check(
                    profile.Set(f, profile.Input(f)),
                    level + " value fits editor: " + f.Path + " / " + profile.Error(f)
                );
            Check(
                profile.PendingCount == 0 && profile.ValidationError == "",
                level + " precision and validation retained"
            );
        }
        editor.Compare("Easy");
        Check(
            editor.DifferenceCount > 0 && editor.PendingCount == 0,
            "reference selection is read-only"
        );
        Check(
            editor.ProfileChanged && editor.ReferenceProfile == "Easy",
            "profile metadata comparison is separate"
        );
        Check(
            JsonSerializer.Serialize(editor.Snapshot()) == before
                && JsonSerializer.Serialize(original) == before,
            "comparison leaves draft and source untouched"
        );
        Check(
            editor.Visible(false, "grenade retry", false).Any(f => !f.Simple),
            "search reaches advanced help and settings"
        );
        Check(
            editor.Visible(false, "gReNaDe Retry", false).Count()
                == editor.Visible(false, "grenade retry", false).Count(),
            "search ignores case"
        );
        Check(
            !editor.Visible(true, "no_such_setting", false).Any(),
            "empty search results are honest"
        );
        Check(
            editor.Visible(true, "", true).All(editor.Changed),
            "modified-only uses selected comparison"
        );
        Check(
            editor.Visible(false, "", true).Count() == editor.DifferenceCount,
            "modified-only includes advanced changes while in simple mode"
        );
        var chance = Field("CoverFeint.PeekChance");
        Check(editor.Set(chance, "17.125"), "fractional chances accepted");
        Check(
            editor.PendingCount == 1 && editor.Pending(chance),
            "pending compares saved, not reference"
        );
        Check(editor.Snapshot().CoverFeint.PeekChance == 17.125, "fractional chance preserved");
        var percent = Field("QuietApproach.Chances.Normal");
        Check(
            editor.Set(percent, "22.5")
                && editor.Snapshot().QuietApproach.Chances["Normal"] == .225,
            "display percent converts once"
        );
        var nested = Field("QuietApproach.Chances.Chad");
        Check(
            editor.Set(nested, "32.5") && editor.Snapshot().QuietApproach.Chances["Chad"] == .325,
            "dictionary fractions keep their units"
        );
        editor.Restore(percent);
        Check(
            editor.Snapshot().QuietApproach.Chances["Normal"]
                == DifficultyPresets.Create("Easy").QuietApproach.Chances["Normal"],
            "field reset touches correct reference value"
        );
        Check(
            editor.Snapshot().CoverFeint.PeekChance == 17.125,
            "field reset preserves unrelated edits"
        );
        Check(editor.Set(Field("CoverFeint.RetryDelay"), "32"), "advanced timing edited");
        var restore = editor.PreviewSection("CoverFeint");
        Check(
            restore.Changes.Any(c => c.Path == "CoverFeint.RetryDelay")
                && restore.Changes.Any(c => c.Path == chance.Path),
            "section preview includes hidden advanced values"
        );
        Check(editor.Snapshot().CoverFeint.RetryDelay == 32, "preview does not mutate draft");
        editor.Compare("Hard");
        editor.Apply(restore);
        Check(
            editor.Snapshot().CoverFeint.PeekChance == 17.5
                && editor.Snapshot().CoverFeint.RetryDelay == 30,
            "restore uses frozen Easy snapshot, not later comparison"
        );
        Check(
            editor.Snapshot().QuietApproach.Chances["Chad"] == .325,
            "section reset preserves another section"
        );
        editor.Compare("Saved");
        editor.Apply(editor.PreviewDiscard());
        Check(
            !editor.HasPending && JsonSerializer.Serialize(editor.Snapshot()) == before,
            "discard restores entire saved configuration"
        );
        foreach (string invalid in new[] { "", "NaN", "Infinity", "1e999", "-1", "101", "1,5" })
        {
            Check(
                !editor.Set(chance, invalid) && editor.InvalidCount == 1 && !editor.CanSave,
                "invalid input blocks save: " + invalid
            );
            Check(
                editor.HasPending && editor.Input(chance) == invalid,
                "invalid text stays visible, not silently coerced"
            );
            Reject(() => editor.Snapshot(), "cannot bypass invalid input via snapshot");
            Check(
                editor.Visible(true, "", true).Contains(chance),
                "invalid value stays discoverable in modified-only"
            );
            editor.Restore(chance);
            Check(
                editor.InvalidCount == 0 && !editor.HasPending,
                "individual restore clears invalid input"
            );
        }
        var integer = editor.Catalog.Fields.Values.First(f => f.Integer);
        Check(!editor.Set(integer, "2.5"), "integer setting rejects fractions");
        editor.Restore(integer);
        Check(!editor.Set(Field("SquadVoice.Mode"), "unknown"), "enum mode validated");
        editor.Restore(Field("SquadVoice.Mode"));
        var dependentSet = editor.Set(Field("DoorStepPeek.ObservationMax"), "0.1");
        Check(
            dependentSet && editor.ValidationError.Length > 0 && !editor.CanSave,
            $"related-field invariant: set={dependentSet}, validation={editor.ValidationError}, invalid={editor.InvalidCount}"
        );
        Check(
            editor.Set(Field("DoorStepPeek.ObservationMin"), "0.1")
                && editor.ValidationError == ""
                && editor.CanSave,
            "second edit resolves dependent constraint"
        );
        Check(editor.Customized, "customization can be shown without saving");
        editor.Apply(editor.PreviewProfile("Normal"));
        Check(
            editor.Profile == "Normal" && !editor.Customized && editor.HasPending,
            "profile load is a draft, not a save"
        );
        Check(
            editor.Snapshot().HoldLimit == original.HoldLimit,
            "loading profile resets whole config"
        );
        var copy = editor.Snapshot();
        copy.CoverFeint.PeekChance = 99;
        Check(editor.Snapshot().CoverFeint.PeekChance == 26.25, "snapshot detached from draft");
        var culture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            Check(
                editor.Set(chance, "18.75") && editor.Input(chance) == "18.75",
                "HTML numeric input is invariant under French server culture"
            );
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
        }

        string directory = Path.Combine(
            Path.GetTempPath(),
            "comet-u05-" + Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "config.json");
            var store = new ConfigStore(path);
            var a = new ConfigEditor(store.Read());
            var b = new ConfigEditor(store.Read());
            a.Apply(a.PreviewProfile("Easy"));
            var loadedRaid = store.Read();
            a.AcceptSaved(store.Save(a.Snapshot(), a.Revision));
            Check(
                !a.HasPending && a.Revision == 2,
                "saved snapshot clears pending and accepts revision"
            );
            Check(
                store.Read().DifficultyPreset == "Easy" && loadedRaid.DifficultyPreset == "Hard",
                "saved profile applies to future reads, old raid stays detached"
            );
            b.Set(b.Catalog.Fields["Enabled"], "false");
            Reject(
                () => store.Save(b.Snapshot(), b.Revision),
                "stale browser cannot overwrite another save"
            );
            Check(b.PendingCount == 1 && !b.Snapshot().Enabled, "conflict preserves draft");
            a.Set(a.Catalog.Fields["SquadVoice.Mode"], "Normal");
            var radius = a.Catalog.Fields["AmbushRadius"];
            Check(radius.Section == "LostContact" && !radius.Simple && radius.Unit == "m",
                "ambush radius is available in advanced lost-contact settings");
            Check(a.Set(radius, "10") && !a.Set(radius, "9") && a.Set(radius, "100")
                && !a.Set(radius, "101") && a.Set(radius, "70"),
                "the editor accepts both source limits and a custom radius");
            a.AcceptSaved(store.Save(a.Snapshot(), a.Revision));
            Check(store.Read().AmbushRadius == 70 && JsonSerializer.Deserialize<CometConfig>(store.ForGame())!.AmbushRadius == 70,
                "custom ambush radius persists and reaches the raid config");
            Check(
                File.Exists(path + ".bak") && store.Read().SquadVoice.Mode == "Normal",
                "normal atomic backup and persistence preserved"
            );
            var legacy = System.Text.Json.Nodes.JsonNode.Parse(before)!.AsObject();
            legacy.Remove("DifficultyPreset");
            legacy.Remove("AmbushRadius");
            legacy["Enabled"] = false;
            File.WriteAllText(path, legacy.ToJsonString());
            string legacyText = File.ReadAllText(path);
            var old = new ConfigEditor(store.Read());
            Check(
                old.Profile == "Hard"
                    && old.Customized
                    && !old.HasPending
                    && !old.Snapshot().Enabled
                    && old.Snapshot().AmbushRadius == 45,
                "legacy custom file opens without resetting values"
            );
            Check(File.ReadAllText(Directory.GetFiles(directory, "*.before-quiet-*.bak").Single()) == legacyText,
                "opening an old config preserves the original before applying the quiet default");
        }
        finally
        {
            Directory.Delete(directory, true);
        }
        Console.WriteLine($"U05 editor checks passed: {checks}");
        return checks;
    }
}
