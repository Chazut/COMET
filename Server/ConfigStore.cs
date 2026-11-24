using System.Text.Json;
using System.Text.Json.Nodes;
using Comet.Core;
using SPTarkov.DI.Annotations;

namespace Comet.Server;

[Injectable(InjectionType.Singleton)]
public sealed class ConfigStore
{
    // Disk metadata, kept out of the editor, preset exports and the raid protocol.
    private const string QuietDefaultApplied = "QuietLoggingDefaultApplied";
    // A per-install receipt survives saving a preset or briefly running the previous server.
    internal const string ReportDefaultsReceipt = ".report-defaults-v1.applied";
    // The original receipt was empty. Keep its path so older builds also honor user choices.
    private const int ReportDefaultsVersion = 2;
    private static readonly (string Section, bool Enabled)[] ReportDefaults =
    [
        (nameof(CometConfig.CombatJournal), true),
        (nameof(CometConfig.WeaponJournal), true),
        (nameof(CometConfig.ControlJournal), true),
        (nameof(CometConfig.TacticCounters), true),
        (nameof(CometConfig.Performance), false),
    ];
    private readonly object gate = new();
    private readonly string path;
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    public ConfigStore()
        : this(
            Path.Combine(
                Path.GetDirectoryName(typeof(ConfigStore).Assembly.Location)!,
                "config",
                "config.json"
            )
        ) { }

    public PresetLibrary Presets { get; }
    public IntegrationReports Integration { get; } = new();

    internal ConfigStore(string path)
    {
        this.path = path;
        Presets = new PresetLibrary(Path.Combine(Path.GetDirectoryName(path)!, "presets.json"));
    }

    public CometConfig Read()
    {
        lock (gate)
        {
            if (!File.Exists(path))
                return new CometConfig();
            string original = File.ReadAllText(path);
            var document =
                JsonNode.Parse(original) as JsonObject
                ?? throw new InvalidDataException("Invalid COMET configuration.");
            bool baselineChanged = SourceBaselineMigration.Upgrade(document);
            var config = ReadValidated(document);
            string markerKey = SourceBaselineMigration.Key(document, QuietDefaultApplied);
            bool migrateQuiet = document[markerKey]?.GetValue<bool>() != true;
            bool settingsChanged = false;
            if (migrateQuiet)
            {
                string quietKey = SourceBaselineMigration.Key(document, nameof(CometConfig.QuietLogging));
                // An absent setting also meant OFF in 1.0.0. Reject stale editor drafts.
                settingsChanged = document[quietKey]?.GetValue<bool>() != true;
                document[quietKey] = true;
                document[markerKey] = true;
            }
            string receipt = path + ReportDefaultsReceipt;
            bool firstReportMigration = !File.Exists(receipt);
            bool migrateReports = firstReportMigration
                || !int.TryParse(File.ReadAllText(receipt), out int version) || version < ReportDefaultsVersion;
            if (migrateReports)
                settingsChanged |= ApplyReportDefaults(document, firstReportMigration);
            if (settingsChanged)
            {
                config = ReadValidated(document);
                config.Revision = checked(config.Revision + 1);
                document[SourceBaselineMigration.Key(document, nameof(CometConfig.Revision))] = config.Revision;
            }
            if (baselineChanged || migrateQuiet || settingsChanged)
            {
                var reason = baselineChanged ? "zzap" : migrateQuiet ? "quiet" : "reports";
                Write(document, $"{path}.before-{reason}-{DateTime.UtcNow:yyyyMMdd-HHmmss-fffffff}.bak");
            }
            if (migrateReports)
                MarkReportDefaultsApplied();
            return config;
        }
    }

    public string ForGame() => JsonSerializer.Serialize(Read(), Json);

    private static CometConfig ReadValidated(JsonObject document)
    {
        var config = document.Deserialize<CometConfig>(Json)
            ?? throw new InvalidDataException("Empty COMET configuration.");
        config.Validate();
        return config;
    }

    public CometConfig Save(CometConfig proposed, long expectedRevision)
    {
        lock (gate)
        {
            var detached = JsonSerializer.Deserialize<CometConfig>(
                JsonSerializer.Serialize(proposed, Json),
                Json
            )!;
            detached.Validate();
            var current = Read();
            if (current.Revision != expectedRevision)
                throw new InvalidOperationException(
                    "Settings changed in another tab. Reload before saving."
                );
            detached.Revision = checked(current.Revision + 1);
            var document = JsonSerializer.SerializeToNode(detached, Json)!.AsObject();
            document[QuietDefaultApplied] = true;
            // A first save is already an explicit choice made against the new defaults.
            if (!File.Exists(path))
                MarkReportDefaultsApplied();
            Write(document, path + ".bak");
            return detached;
        }
    }

    private static bool ApplyReportDefaults(JsonObject document, bool firstReportMigration)
    {
        string master = SourceBaselineMigration.Key(document, nameof(CometConfig.ExtraRaidReviewInfo));
        bool changed = document[master]?.GetValue<bool>() != true;
        document[master] = true;
        foreach (var (name, enabled) in ReportDefaults)
        {
            if (name == nameof(CometConfig.Performance) && !firstReportMigration)
                continue;
            string key = SourceBaselineMigration.Key(document, name);
            if (document[key] == null)
                document[key] = new JsonObject();
            var section = document[key] as JsonObject
                ?? throw new InvalidDataException($"Invalid COMET configuration section '{key}'.");
            string flag = SourceBaselineMigration.Key(section, "Enabled");
            if (section[flag]?.GetValue<bool>() == enabled)
                continue;
            section[flag] = enabled;
            changed = true;
        }
        return changed;
    }

    private void MarkReportDefaultsApplied()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string receipt = path + ReportDefaultsReceipt;
        string temp = receipt + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, ReportDefaultsVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));
            File.Move(temp, receipt, true);
        }
        finally
        {
            File.Delete(temp);
        }
    }

    private void Write(JsonObject document, string backup)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, document, Json);
                stream.Flush(true);
            }
            if (File.Exists(path))
                File.Replace(temp, path, backup);
            else
                File.Move(temp, path);
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }
    }
}
