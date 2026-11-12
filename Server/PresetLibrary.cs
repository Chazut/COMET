using Comet.Core;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Comet.Server;

public sealed record PresetImport(string Name, CometConfig Config, IReadOnlyList<string> AddedSettings);
public sealed record PersonalPreset(string Id, string Name, long Revision, CometConfig Config, IReadOnlyList<string> AddedSettings);
public sealed record PresetLibrarySnapshot(long Revision, IReadOnlyList<PersonalPreset> Presets);

// Names are display labels; no user-controlled path is ever used. One atomically replaced
// document provides one conflict token for all tabs, including rename/delete/import.
public sealed class PresetLibrary
{
    public const int MaxImportBytes = 2 * 1024 * 1024;
    public const int MaxPresets = 100;
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true, UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow };
    static readonly ConcurrentDictionary<string, object> Gates = new(StringComparer.OrdinalIgnoreCase);
    readonly string path;
    readonly object gate;
    public PresetLibrary(string path) { this.path = Path.GetFullPath(path); gate = Gates.GetOrAdd(this.path, _ => new()); }
    sealed class Document
    {
        public int Version { get; set; } = 1;
        public long Revision { get; set; }
        public List<Entry> Presets { get; set; } = new();
    }
    sealed class Entry
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public long Revision { get; set; }
        public JsonObject Settings { get; set; } = new();
    }
    public static string Name(string name)
    {
        name = (name ?? "").Trim().Normalize(NormalizationForm.FormC);
        PresetIdentity.ValidateName(name);
        if (new[] { "Easy", "Normal", "Hard", "Saved" }.Contains(name, StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException("This name is reserved. Choose a name for your personal copy.");
        return name;
    }
    static JsonObject Settings(CometConfig config)
    {
        var node = JsonSerializer.SerializeToNode(config)!.AsObject();
        node.Remove("PresetSource"); node.Remove("Revision");
        return node;
    }
    static void NoDuplicates(JsonElement node)
    {
        if (node.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in node.EnumerateObject())
            { if (!names.Add(p.Name)) throw new InvalidDataException("Duplicate JSON property: " + p.Name); NoDuplicates(p.Value); }
        }
        else if (node.ValueKind == JsonValueKind.Array) foreach (var v in node.EnumerateArray()) NoDuplicates(v);
    }
    static JsonObject Parse(string json, int limit)
    {
        if (Encoding.UTF8.GetByteCount(json) > limit) throw new InvalidDataException("The preset file is too large.");
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 40 });
        NoDuplicates(document.RootElement);
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Expected a COMET JSON object.");
        return JsonNode.Parse(json)!.AsObject();
    }
    static void KnownKeys(JsonObject node, params string[] keys)
    {
        foreach (var p in node) if (!keys.Contains(p.Key, StringComparer.Ordinal)) throw new InvalidDataException("Unsupported field: " + p.Key);
    }
    static PresetImport Decode(string name, JsonObject supplied)
    {
        // Missing values in old presets are explicit, reviewable defaults. Unknown fields,
        // wrong versions, nulls and duplicates are never silently dropped or rewritten.
        if (supplied["Schema"]?.GetValue<int>() != 1) throw new InvalidDataException("Unsupported or missing COMET settings schema.");
        string level = supplied["DifficultyPreset"]?.GetValue<string>() ?? "Hard";
        if (!DifficultyPresets.IsKnown(level)) throw new InvalidDataException("Unknown COMET base level.");
        var baseline = Settings(DifficultyPresets.Create(level));
        var missing = new List<string>();
        JsonObject Merge(JsonObject incoming, JsonObject defaults, string prefix)
        {
            foreach (var p in incoming) if (!defaults.ContainsKey(p.Key)) throw new InvalidDataException("Unsupported setting: " + prefix + p.Key);
            var result = new JsonObject();
            foreach (var p in defaults)
            {
                string key = prefix + p.Key;
                if (!incoming.TryGetPropertyValue(p.Key, out var value))
                {
                    result[p.Key] = p.Value?.DeepClone();
                    void Missing(JsonNode? node, string at)
                    { if (node is JsonObject obj) foreach (var child in obj) Missing(child.Value, at + "." + child.Key); else missing.Add(at); }
                    Missing(p.Value, key); continue;
                }
                if (value == null) throw new InvalidDataException("Missing value for " + key);
                result[p.Key] = p.Value is JsonObject nested
                    ? value is JsonObject input ? Merge(input, nested, key + ".") : throw new InvalidDataException("Expected settings object: " + key)
                    : value.DeepClone();
            }
            return result;
        }
        var merged = Merge(supplied, baseline, "");
        // The merge fills absent metadata too; consult the imported document, not
        // the defaults, before deciding whether this was an old behaviour preset.
        if (supplied[nameof(CometConfig.BehaviorBaseline)]?.GetValue<string>() != CometConfig.SourceBaseline)
        {
            merged.Remove(nameof(CometConfig.BehaviorBaseline));
            SourceBaselineMigration.Upgrade(merged);
            if (!missing.Contains(nameof(CometConfig.BehaviorBaseline))) missing.Add(nameof(CometConfig.BehaviorBaseline));
        }
        var config = merged.Deserialize<CometConfig>(Json) ?? throw new InvalidDataException("Empty COMET preset.");
        config.Validate();
        return new(name, config, missing);
    }
    public static PresetImport PreviewImport(string json)
    {
        var root = Parse(json, MaxImportBytes);
        KnownKeys(root, "Format", "Version", "Name", "Settings");
        if (root["Format"]?.GetValue<string>() != "COMET preset" || root["Version"]?.GetValue<int>() != 1)
            throw new InvalidDataException("This is not a supported COMET preset export (version 1).");
        string name = Name(root["Name"]?.GetValue<string>() ?? "");
        if (root["Settings"] is not JsonObject settings) throw new InvalidDataException("The preset has no settings.");
        return Decode(name, settings);
    }
    public static string Export(PersonalPreset preset)
    {
        var settings = Settings(preset.Config);
        // Preserve omissions in an older library entry: export must not silently accept new defaults.
        foreach (var path in preset.AddedSettings)
        {
            // Export the migrated baseline explicitly, otherwise its next import
            // would migrate the already reviewed settings a second time.
            if (path == nameof(CometConfig.BehaviorBaseline)) continue;
            var parts = path.Split('.'); JsonObject parent = settings;
            foreach (var part in parts[..^1]) parent = parent[part]!.AsObject();
            parent.Remove(parts[^1]);
        }
        return new JsonObject { ["Format"] = "COMET preset", ["Version"] = 1, ["Name"] = preset.Name, ["Settings"] = settings }.ToJsonString(Json);
    }
    Document ReadDocument()
    {
        if (!File.Exists(path)) return new();
        var raw = Parse(File.ReadAllText(path), 32 * 1024 * 1024);
        var doc = raw.Deserialize<Document>(Json) ?? throw new InvalidDataException("Empty personal preset library.");
        if (doc.Version != 1 || doc.Revision < 0 || doc.Revision >= 9007199254740991L || doc.Presets == null || doc.Presets.Count > MaxPresets)
            throw new InvalidDataException("Unsupported or invalid personal preset library. Existing files have not been changed.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in doc.Presets)
        {
            if (e == null || e.Name != Name(e.Name) || !names.Add(e.Name) || !Guid.TryParseExact(e.Id, "N", out _) || !ids.Add(e.Id)
                || e.Revision < 1 || e.Revision >= 9007199254740991L || e.Settings == null)
                throw new InvalidDataException("Invalid or duplicate personal preset entry.");
            Decode(e.Name, e.Settings);
        }
        return doc;
    }
    static PersonalPreset Materialize(Entry entry)
    {
        var decoded = Decode(entry.Name, entry.Settings);
        decoded.Config.PresetSource = new PresetIdentity { Id = entry.Id, Name = entry.Name, Revision = entry.Revision,
            SettingsHash = PresetIdentity.Fingerprint(decoded.Config) };
        return new(entry.Id, entry.Name, entry.Revision, decoded.Config, decoded.AddedSettings);
    }
    static PresetLibrarySnapshot Snapshot(Document doc) => new(doc.Revision, doc.Presets.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).Select(Materialize).ToArray());
    public PresetLibrarySnapshot Read() { lock (gate) return Snapshot(ReadDocument()); }
    static void Unique(Document doc, string name, string? except = null)
    {
        if (doc.Presets.Any(e => e.Id != except && string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("A personal preset already has that name. Choose another name or explicitly update it.");
    }
    static Entry Find(Document doc, string id) => doc.Presets.SingleOrDefault(e => e.Id == id)
        ?? throw new InvalidOperationException("This personal preset no longer exists. Refresh the library.");
    T Mutate<T>(long expectedRevision, Func<Document, T> edit)
    {
        lock (gate)
        {
            var doc = ReadDocument();
            if (doc.Revision != expectedRevision) throw new InvalidOperationException("The preset library changed in another tab. Refresh it and review again. Your draft is unchanged.");
            T result = edit(doc);
            doc.Revision = checked(doc.Revision + 1);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { JsonSerializer.Serialize(stream, doc, Json); stream.Flush(true); }
                if (File.Exists(path)) File.Replace(temp, path, path + ".bak"); else File.Move(temp, path);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
            return result;
        }
    }
    public PersonalPreset Create(string name, CometConfig config, long expectedRevision)
    {
        name = Name(name); var frozen = Decode(name, Settings(config));
        return Mutate(expectedRevision, doc =>
        {
            if (doc.Presets.Count >= MaxPresets) throw new InvalidOperationException("The library already contains 100 presets.");
            Unique(doc, name);
            var e = new Entry { Id = Guid.NewGuid().ToString("N"), Name = name, Revision = 1, Settings = Settings(frozen.Config) };
            doc.Presets.Add(e); return Materialize(e);
        });
    }
    public PersonalPreset Update(string id, CometConfig config, long expectedRevision) => Mutate(expectedRevision, doc =>
    {
        var e = Find(doc, id); var frozen = Decode(e.Name, Settings(config));
        e.Settings = Settings(frozen.Config); e.Revision = checked(e.Revision + 1); return Materialize(e);
    });
    public PersonalPreset Rename(string id, string name, long expectedRevision) => Mutate(expectedRevision, doc =>
    {
        name = Name(name); var e = Find(doc, id); Unique(doc, name, id);
        e.Name = name; e.Revision = checked(e.Revision + 1); return Materialize(e);
    });
    public void Delete(string id, long expectedRevision) => Mutate(expectedRevision, doc => doc.Presets.Remove(Find(doc, id)));
}
