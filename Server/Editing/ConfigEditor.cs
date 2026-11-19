using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Comet.Core;

namespace Comet.Server.Editing;

public sealed class EditorSection
{
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public string Summary { get; set; } = "";
    public string Category { get; set; } = "";
}

public sealed class EditorField
{
    public string Path { get; set; } = "";
    public string Label { get; set; } = "";
    public string Help { get; set; } = "";
    public string Unit { get; set; } = "";
    public string Example { get; set; } = "";
    public string Section { get; set; } = "";
    public bool Simple { get; set; }
    public double? Min { get; set; }
    public double? Max { get; set; }
    public double Scale { get; set; } = 1;
    public Type ValueType { get; set; } = typeof(double);
    public bool Boolean => ValueType == typeof(bool);
    public bool Text => ValueType == typeof(string);
    public bool Integer => ValueType == typeof(int);
}

public sealed class EditorCatalog
{
    public Dictionary<string, EditorSection> Sections { get; set; } = new();
    public Dictionary<string, EditorField> Fields { get; set; } = new();
    public List<EditorSection> Notes { get; set; } = new();

    public static EditorCatalog Read()
    {
        using var stream = typeof(EditorCatalog).Assembly.GetManifestResourceStream(
            "Comet.EditorCatalog.json"
        ) ?? throw new InvalidOperationException("COMET editor catalogue is missing.");
        var catalog = JsonSerializer.Deserialize<EditorCatalog>(stream)!;
        foreach (
            var path in catalog.Fields.Keys.Where(SourceBaselineSettings.IsRetiredPath).ToArray()
        )
            catalog.Fields.Remove(path);
        foreach (var (path, field) in catalog.Fields)
        {
            field.Path = path;
            Type type = typeof(CometConfig);
            foreach (string part in path.Split('.'))
                type =
                    type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Dictionary<,>)
                        ? type.GetGenericArguments()[1]
                        : type.GetProperty(part)?.PropertyType
                            ?? throw new InvalidOperationException(
                                "Unknown editor setting: " + path
                            );
            field.ValueType = type;
            if (
                type != typeof(bool)
                && type != typeof(string)
                && type != typeof(int)
                && type != typeof(double)
            )
                throw new InvalidOperationException("Unsupported editor setting: " + path);
        }
        return catalog;
    }
}

public sealed record EditorChange(string Path, string Label, string Before, string After);

// Preview owns a detached destination. Changing the comparison afterwards cannot change a confirmed restore.
public sealed class EditorPreview
{
    internal JsonObject Target { get; }
    internal string[] Paths { get; }
    internal bool Whole { get; }
    public string Title { get; }
    public IReadOnlyList<EditorChange> Changes { get; }

    internal EditorPreview(
        string title,
        JsonObject target,
        string[] paths,
        bool whole,
        IReadOnlyList<EditorChange> changes
    )
    {
        Title = title;
        Target = target;
        Paths = paths;
        Whole = whole;
        Changes = changes;
    }
}

// Editor state only. It never writes SAIN, game instances or files; ConfigStore remains the persistence boundary.
public sealed class ConfigEditor
{
    static readonly CultureInfo Culture = CultureInfo.InvariantCulture;
    JsonObject saved,
        draft,
        reference;
    JsonObject? defaults;
    string defaultsProfile = "";
    readonly Dictionary<string, string> inputErrors = new();
    readonly Dictionary<string, string> invalidInputs = new();
    public EditorCatalog Catalog { get; }
    public string Comparison { get; private set; } = "Saved";
    public long Revision => saved["Revision"]!.GetValue<long>();
    public string Profile => draft["DifficultyPreset"]!.GetValue<string>();
    public string PresetLabel =>
        draft["PresetSource"] is JsonObject source
            ? source["Name"]!.GetValue<string>() + " · v" + source["Revision"]!.GetValue<long>()
            : Profile;
    public bool Customized
    {
        get { return Catalog.Fields.Keys.Any(p => Different(draft, Defaults, p)); }
    }
    public int PendingCount =>
        Catalog.Fields.Keys.Count(p => Different(draft, saved, p))
        + (Different(draft, saved, "DifficultyPreset") ? 1 : 0)
        + (Different(draft, saved, "PresetSource") ? 1 : 0);
    public int InvalidCount => inputErrors.Count;
    public bool HasPending => PendingCount > 0 || InvalidCount > 0;
    public string ValidationError { get; private set; } = "";

    public ConfigEditor(CometConfig config)
    {
        Catalog = EditorCatalog.Read();
        saved = Node(config);
        draft = (JsonObject)saved.DeepClone();
        reference = (JsonObject)saved.DeepClone();
        ValidateDraft();
    }

    static JsonObject Node(CometConfig value) => JsonSerializer.SerializeToNode(value)!.AsObject();

    // One detached reference per base profile, reused by all fields and renders.
    // Personal copies and unsaved edits must never become the supplied defaults.
    JsonObject Defaults
    {
        get
        {
            if (defaults == null || defaultsProfile != Profile)
            {
                defaults = Node(DifficultyPresets.Create(Profile));
                defaultsProfile = Profile;
            }
            return defaults;
        }
    }

    static JsonNode? Get(JsonObject root, string path)
    {
        JsonNode? node = root;
        foreach (string part in path.Split('.'))
            node = node?[part];
        return node;
    }

    static void Put(JsonObject root, string path, JsonNode? value)
    {
        var parts = path.Split('.');
        JsonObject parent = root;
        foreach (string part in parts[..^1])
            parent = parent[part]!.AsObject();
        parent[parts[^1]] = value?.DeepClone();
    }

    static bool Different(JsonObject a, JsonObject b, string path) =>
        !JsonNode.DeepEquals(Get(a, path), Get(b, path));

    public bool Changed(EditorField field) => Different(draft, reference, field.Path);

    public bool Pending(EditorField field) => Different(draft, saved, field.Path);

    public string Error(EditorField field) => inputErrors.GetValueOrDefault(field.Path, "");

    public int DifferenceCount => Catalog.Fields.Values.Count(Changed);
    public bool ProfileChanged => Different(draft, reference, "DifficultyPreset");
    public string ReferenceProfile => reference["DifficultyPreset"]!.GetValue<string>();

    public string Input(EditorField field) =>
        invalidInputs.GetValueOrDefault(field.Path, Format(draft, field));

    public string ReferenceValue(EditorField field) => Format(reference, field, human: true);

    public string DefaultValue(EditorField field) => Format(Defaults, field, human: true);

    public string DisplayValue(EditorField field) => Format(draft, field, human: true);

    public bool IsOn(EditorField field) => Get(draft, field.Path)!.GetValue<bool>();

    string Format(JsonObject root, EditorField field, bool human = false)
    {
        var node = Get(root, field.Path)!;
        if (field.Boolean)
            return node.GetValue<bool>() ? (human ? "On" : "true") : (human ? "Off" : "false");
        if (field.Text)
            return node.GetValue<string>();
        double value = double.Parse(node.ToJsonString(), Culture) * field.Scale;
        string unit = field.Unit == "0-1" ? "(0-1)" : field.Unit;
        return value.ToString("G15", Culture) + (human && unit.Length > 0 ? " " + unit : "");
    }

    public void Compare(string value)
    {
        reference =
            value == "Saved"
                ? (JsonObject)saved.DeepClone()
                : Node(DifficultyPresets.Create(value));
        Comparison = value;
    }

    public bool Set(EditorField field, string input)
    {
        JsonNode? value = null;
        string error = "";
        if (field.Boolean)
        {
            if (bool.TryParse(input, out bool enabled))
                value = JsonValue.Create(enabled);
            else
                error = "Choose On or Off.";
        }
        else if (field.Text)
        {
            if (field.Path == "SquadVoice.Mode" && input is "Normal" or "Disabled")
                value = JsonValue.Create(input);
            else
                error = "Choose Normal or Disabled.";
        }
        else if (
            !double.TryParse(input, NumberStyles.Float, Culture, out double number)
            || !double.IsFinite(number)
        )
            error = "Enter a finite number.";
        else if (
            (field.Min.HasValue && number < field.Min) || (field.Max.HasValue && number > field.Max)
        )
            error =
                $"Use {field.Min?.ToString(Culture) ?? "any lower value"} to {field.Max?.ToString(Culture) ?? "any higher value"}{(field.Unit.Length > 0 ? " " + field.Unit : "")}.";
        else
        {
            double raw = number / field.Scale;
            if (
                field.Integer
                && (raw != Math.Truncate(raw) || raw < int.MinValue || raw > int.MaxValue)
            )
                error = "Enter a whole number.";
            else
                value = field.Integer ? JsonValue.Create((int)raw) : JsonValue.Create(raw);
        }
        if (error.Length > 0)
        {
            inputErrors[field.Path] = error;
            invalidInputs[field.Path] = input;
            return false;
        }
        Put(draft, field.Path, value);
        ClearError(field.Path);
        ValidateDraft();
        return true;
    }

    void ClearError(string path)
    {
        inputErrors.Remove(path);
        invalidInputs.Remove(path);
    }

    void ValidateDraft()
    {
        try
        {
            draft.Deserialize<CometConfig>()!.Validate();
            ValidationError = "";
        }
        catch (Exception e)
            when (e is ArgumentException or InvalidOperationException or JsonException)
        {
            ValidationError = e.Message;
        }
    }

    public bool CanSave => HasPending && InvalidCount == 0 && ValidationError.Length == 0;

    public CometConfig Snapshot()
    {
        if (InvalidCount != 0)
            throw new InvalidOperationException("Correct invalid values before saving.");
        var config = draft.Deserialize<CometConfig>()!;
        config.Validate();
        return config;
    }

    public IEnumerable<EditorField> Visible(bool advanced, string search, bool modifiedOnly)
    {
        string[] terms = search.Split(
            ' ',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
        );
        return Catalog.Fields.Values.Where(f =>
        {
            var s = Catalog.Sections[f.Section];
            string haystack =
                f.Label
                + " "
                + f.Help
                + " "
                + f.Unit
                + " "
                + f.Example
                + " "
                + f.Path
                + " "
                + s.Title
                + " "
                + s.Description
                + " "
                + s.Category;
            return (advanced || modifiedOnly || terms.Length > 0 || f.Simple)
                && (!modifiedOnly || Changed(f) || inputErrors.ContainsKey(f.Path))
                && terms.All(t => haystack.Contains(t, StringComparison.OrdinalIgnoreCase));
        });
    }

    public void Restore(EditorField field)
    {
        Put(draft, field.Path, Get(reference, field.Path));
        ClearError(field.Path);
        ValidateDraft();
    }

    EditorPreview Preview(string title, JsonObject target, IEnumerable<string> paths, bool whole)
    {
        var selected = paths.ToArray();
        var changes = new List<EditorChange>();
        foreach (
            string p in selected.Where(p =>
                Different(draft, target, p) || inputErrors.ContainsKey(p)
            )
        )
        {
            var f = Catalog.Fields[p];
            changes.Add(
                new(
                    p,
                    f.Label,
                    invalidInputs.GetValueOrDefault(p, Format(draft, f, true)),
                    Format(target, f, true)
                )
            );
        }
        if (whole && Different(draft, target, "DifficultyPreset"))
            changes.Insert(
                0,
                new(
                    "DifficultyPreset",
                    "Base profile",
                    Profile,
                    target["DifficultyPreset"]!.GetValue<string>()
                )
            );
        if (whole && Different(draft, target, "PresetSource"))
            changes.Insert(
                0,
                new(
                    "PresetSource",
                    "Personal preset",
                    PresetLabel,
                    target["PresetSource"] is JsonObject source
                        ? source["Name"]!.GetValue<string>()
                            + " · v"
                            + source["Revision"]!.GetValue<long>()
                        : target["DifficultyPreset"]!.GetValue<string>()
                )
            );
        return new EditorPreview(title, (JsonObject)target.DeepClone(), selected, whole, changes);
    }

    public EditorPreview PreviewSection(string key) =>
        Preview(
            "Restore " + Catalog.Sections[key].Title + " from " + Comparison,
            reference,
            Catalog.Fields.Values.Where(f => f.Section == key).Select(f => f.Path),
            false
        );

    public EditorPreview PreviewProfile(string name) =>
        Preview(
            "Load " + name + " in editor",
            Node(DifficultyPresets.Create(name)),
            Catalog.Fields.Keys,
            true
        );

    public EditorPreview PreviewPreset(CometConfig config) =>
        Preview(
            "Load " + (config.PresetSource?.Name ?? config.DifficultyPreset) + " in editor",
            Node(config),
            Catalog.Fields.Keys,
            true
        );

    public void AttachPreset(PresetIdentity identity)
    {
        identity.Validate();
        if (PresetIdentity.Fingerprint(Snapshot()) != identity.SettingsHash)
            throw new InvalidOperationException("The draft changed since this copy was prepared.");
        draft["PresetSource"] = JsonSerializer.SerializeToNode(identity);
    }

    public EditorPreview PreviewDiscard() =>
        Preview("Discard all unsaved edits", saved, Catalog.Fields.Keys, true);

    public IReadOnlyList<EditorChange> PendingChanges() =>
        PreviewDiscard()
            .Changes.Select(c => new EditorChange(c.Path, c.Label, c.After, c.Before))
            .ToArray();

    public void Apply(EditorPreview preview)
    {
        if (preview.Whole)
        {
            draft = (JsonObject)preview.Target.DeepClone();
            draft["Revision"] = Revision;
            inputErrors.Clear();
            invalidInputs.Clear();
        }
        else
            foreach (string path in preview.Paths)
            {
                Put(draft, path, Get(preview.Target, path));
                ClearError(path);
            }
        ValidateDraft();
    }

    public void AcceptSaved(CometConfig actual)
    {
        saved = Node(actual);
        draft = (JsonObject)saved.DeepClone();
        inputErrors.Clear();
        invalidInputs.Clear();
        Compare(Comparison);
        ValidateDraft();
    }
}
