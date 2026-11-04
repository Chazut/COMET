using Comet.Core;
using Comet.Server;
using Comet.Server.Editing;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

internal static class PresetLibraryTests
{
    public static int Run()
    {
        int checks = 0;
        void Check(bool ok, string why) { checks++; if (!ok) throw new Exception("U07: " + why); }
        void Reject(Action action, string why)
        {
            bool rejected = false;
            try { action(); } catch (Exception e) when (e is ArgumentException or InvalidOperationException or InvalidDataException or JsonException or IOException) { rejected = true; }
            Check(rejected, why);
        }
        var dir = Path.Combine(Path.GetTempPath(), "comet-u07-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "presets.json");
        var store = new ConfigStore(Path.Combine(dir, "config.json")); var library = store.Presets;
        Check(library.Read().Revision == 0 && !File.Exists(path), "reading an empty library does not create or overwrite files");
        var hard = DifficultyPresets.Create("Hard"); string unchanged = JsonSerializer.Serialize(hard);
        var config = DifficultyPresets.Create("Normal"); config.CoverFeint.PeekChance = 19.75;
        var saved = library.Create("My setup", config, 0);
        Check(saved.Revision == 1 && saved.Name == "My setup" && saved.Config.CoverFeint.PeekChance == 19.75, "first copy preserves exact settings");
        Check(saved.Config.PresetSource!.SettingsHash == PresetIdentity.Fingerprint(config), "copy has verified content identity");
        Check(JsonSerializer.Serialize(hard) == unchanged && !File.Exists(Path.Combine(dir, "config.json")), "library operations preserve supplied profiles and active config");
        config.CoverFeint.PeekChance = 2; saved.Config.CoverFeint.PeekChance = 3;
        var read = library.Read(); saved = read.Presets.Single();
        Check(saved.Config.CoverFeint.PeekChance == 19.75, "inputs and results are detached from persisted copies");
        var editor = new ConfigEditor(store.Read()); var preview = editor.PreviewPreset(saved.Config);
        Check(!editor.HasPending && preview.Changes.Any(c => c.Path == "PresetSource"), "preview stages no changes and explains identity");
        editor.Apply(preview);
        Check(editor.HasPending && editor.Snapshot().PresetSource!.Name == "My setup" && editor.Snapshot().Revision == 1, "load stages identity but preserves active revision");
        var active = store.Save(editor.Snapshot(), editor.Revision); editor.AcceptSaved(active);
        Check(!editor.HasPending && !DifficultyPresets.Describe(active).ModifiedSincePreset!.Value, "saved config accurately names unmodified copy");
        editor.Set(editor.Catalog.Fields["CoverFeint.PeekChance"], "21.5");
        active = store.Save(editor.Snapshot(), editor.Revision); editor.AcceptSaved(active);
        Check(DifficultyPresets.Describe(active).ModifiedSincePreset == true && active.PresetSource!.Revision == 1, "further tweaks are separate from stored-copy revision");
        Check(library.Read().Presets.Single().Config.CoverFeint.PeekChance == 19.75, "Save for next raid never overwrites the library copy");
        var update = library.Update(saved.Id, editor.Snapshot(), library.Read().Revision);
        editor.AttachPreset(update.Config.PresetSource!);
        Check(editor.PendingCount == 1 && update.Revision == 2 && !DifficultyPresets.Describe(editor.Snapshot()).ModifiedSincePreset!.Value, "explicit update refreshes provenance without gameplay changes");
        editor.Apply(editor.PreviewProfile("Easy")); Check(editor.Snapshot().PresetSource == null, "loading a built-in clears stale personal identity");
        editor.Apply(editor.PreviewDiscard()); Check(editor.Snapshot().PresetSource?.Name == "My setup", "discard restores saved identity too");
        var fingerprint = PresetIdentity.Fingerprint(active);
        var culture = CultureInfo.CurrentCulture;
        try { CultureInfo.CurrentCulture = new("fr-FR"); Check(PresetIdentity.Fingerprint(active) == fingerprint, "fingerprint independent of culture"); }
        finally { CultureInfo.CurrentCulture = culture; }
        var network = Newtonsoft.Json.JsonConvert.DeserializeObject<CometConfig>(JsonSerializer.Serialize(active))!;
        Check(PresetIdentity.Fingerprint(network) == fingerprint && network.PresetSource!.Name == "My setup", "server/client serializers preserve personal metadata and content identity");
        network.Revision += 999; Check(PresetIdentity.Fingerprint(network) == fingerprint, "active revision excluded from content fingerprint");
        network.PresetSource!.Name = "Renamed source"; Check(PresetIdentity.Fingerprint(network) == fingerprint, "display metadata excluded from gameplay comparison");
        var duplicate = library.Create("Duplicate", update.Config, library.Read().Revision);
        Check(duplicate.Id != update.Id && duplicate.Revision == 1 && duplicate.Config.PresetSource!.Id == duplicate.Id, "duplicate owns a new identity");
        var oldRaid = store.Read(); string oldRaidJson = JsonSerializer.Serialize(oldRaid);
        long stale = library.Read().Revision; var renamed = library.Rename(duplicate.Id, "Renamed copy", stale);
        Check(renamed.Revision == 2 && renamed.Id == duplicate.Id, "rename retains identity and increments revision");
        Reject(() => library.Delete(renamed.Id, stale), "stale delete cannot remove a renamed copy");
        Reject(() => library.Update(update.Id, hard, stale), "stale update cannot replace newer library");
        Reject(() => library.Create("Lost tab", hard, stale), "stale create preserves both tabs");
        foreach (var name in new[] { "Hard", " easy ", "NORMAL", "Saved", "", "\n", "Bad\nname", "Bad\u202ename", new string('x',81), "my SETUP" })
            Reject(() => library.Create(name, hard, library.Read().Revision), "protected/duplicate/invalid name: " + name);
        string bytesBefore = File.ReadAllText(path);
        Reject(() => library.Rename(renamed.Id, "Hard", library.Read().Revision), "rename cannot claim a bundled name");
        Reject(() => library.Delete("Hard", library.Read().Revision), "cannot delete built-in by name");
        Check(File.ReadAllText(path) == bytesBefore, "failed operations never write");
        library.Delete(renamed.Id, library.Read().Revision);
        Check(library.Read().Presets.Count == 1 && File.Exists(path + ".bak"), "explicit deletion retains atomic backup");
        Check(JsonSerializer.Serialize(store.Read()) == oldRaidJson, "rename and deletion never rewrite active or loaded raid settings");
        var json = PresetLibrary.Export(update); var imported = PresetLibrary.PreviewImport(json);
        Check(imported.Name == update.Name && imported.AddedSettings.Count == 0 && imported.Config.PresetSource == null, "export is portable and never carries a foreign library identity");
        Check(PresetIdentity.Fingerprint(imported.Config) == PresetIdentity.Fingerprint(update.Config), "all values survive export/import exactly");
        Reject(() => library.Create(imported.Name, imported.Config, library.Read().Revision), "import with existing name does not overwrite");
        var old = JsonNode.Parse(json)!.AsObject(); old["Settings"]!["CoverFeint"]!.AsObject().Remove("PeekChance");
        var migration = PresetLibrary.PreviewImport(old.ToJsonString());
        Check(migration.AddedSettings.SequenceEqual(new[] { "CoverFeint.PeekChance" }) && migration.Config.CoverFeint.PeekChance == 26.25, "missing settings explicitly use the preset base level");
        Check(File.ReadAllText(path) != "" && library.Read().Presets.Count == 1, "import preview does not save");
        void Bad(Action<JsonObject> change, string why) { var root = JsonNode.Parse(json)!.AsObject(); change(root); Reject(() => PresetLibrary.PreviewImport(root.ToJsonString()), why); }
        Bad(r => r["Version"] = 2, "future export refused"); Bad(r => r["Format"] = "SAIN preset", "SAIN export refused");
        Bad(r => r["Settings"]!["Schema"] = 2, "future config refused");
        Bad(r => r["Settings"]!["FutureFeature"] = true, "unsupported fields are not silently dropped");
        Bad(r => r["Settings"]!["CoverFeint"]!["PeekChance"] = 999, "invalid chance refused");
        Bad(r => r["Settings"]!["CoverFeint"] = null, "null section refused, not reset");
        Bad(r => r["Settings"]!["CoverFeint"]!["Enabled"] = "false", "wrong scalar type refused");
        Bad(r => r["Settings"]!["DifficultyPreset"] = "Impossible", "unknown difficulty refused");
        Bad(r => r["Settings"]!["CornerPeek"]!["CooldownMin"] = 100, "cross-field constraints enforced");
        Reject(() => PresetLibrary.PreviewImport("[]"), "array root refused");
        Reject(() => PresetLibrary.PreviewImport(json.Replace("\"Version\": 1", "\"Version\": 1, \"Version\": 1")), "duplicate keys refused");
        Reject(() => PresetLibrary.PreviewImport(new string(' ', PresetLibrary.MaxImportBytes + 1)), "oversize file bounded");
        // Simulate an older on-disk copy; reading and renaming preserve omissions until accepted.
        var disk = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        disk["Presets"]![0]!["Settings"]!["CoverFeint"]!.AsObject().Remove("PeekChance");
        File.WriteAllText(path, disk.ToJsonString()); var diskBefore = File.ReadAllText(path);
        var legacy = library.Read(); Check(legacy.Presets[0].AddedSettings.Contains("CoverFeint.PeekChance") && File.ReadAllText(path) == diskBefore, "old copies read without rewrite");
        var exportedOld = PresetLibrary.PreviewImport(PresetLibrary.Export(legacy.Presets[0]));
        Check(exportedOld.AddedSettings.Contains("CoverFeint.PeekChance"), "export does not silently accept new defaults");
        library.Rename(legacy.Presets[0].Id, "Older copy", legacy.Revision);
        legacy = library.Read(); Check(legacy.Presets[0].AddedSettings.Count == 1, "rename does not migrate old settings");
        library.Update(legacy.Presets[0].Id, legacy.Presets[0].Config, legacy.Revision);
        Check(library.Read().Presets[0].AddedSettings.Count == 0, "explicit acceptance persists missing defaults");
        // Concurrent library instances share the same file lock and optimistic revision.
        var other = new PresetLibrary(path); long raceRevision = library.Read().Revision; int successes = 0;
        Parallel.Invoke(() => { try { library.Create("Race A", hard, raceRevision); Interlocked.Increment(ref successes); } catch (InvalidOperationException) { } },
            () => { try { other.Create("Race B", hard, raceRevision); Interlocked.Increment(ref successes); } catch (InvalidOperationException) { } });
        Check(successes == 1 && library.Read().Presets.Count == 2, "simultaneous writers cannot lose changes");
        Check(Directory.GetFiles(dir,"*.tmp").Length == 0, "atomic operations leave no temporary writes");
        File.WriteAllText(path, "{broken");
        Reject(() => library.Read(), "corrupt library reported"); Reject(() => library.Create("No reset", hard, 0), "corrupt file never reset automatically");
        Check(File.ReadAllText(path) == "{broken", "corrupt data preserved for recovery");
        Console.WriteLine($"U07: {checks} preset-library checks. Isolated evidence: {dir}");
        return checks;
    }
}
