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
            var config = document.Deserialize<CometConfig>(Json)
                ?? throw new InvalidDataException("Empty COMET configuration.");
            config.Validate();
            string markerKey = SourceBaselineMigration.Key(document, QuietDefaultApplied);
            bool migrateQuiet = document[markerKey]?.GetValue<bool>() != true;
            if (migrateQuiet)
            {
                string quietKey = SourceBaselineMigration.Key(document, nameof(CometConfig.QuietLogging));
                // An absent setting also meant OFF in 1.0.0. Reject stale editor drafts.
                if (document[quietKey]?.GetValue<bool>() != true)
                {
                    config.Revision = checked(config.Revision + 1);
                    document[SourceBaselineMigration.Key(document, nameof(CometConfig.Revision))] = config.Revision;
                }
                config.QuietLogging = true;
                document[quietKey] = true;
                document[markerKey] = true;
            }
            if (baselineChanged || migrateQuiet)
            {
                var reason = baselineChanged ? "zzap" : "quiet";
                Write(document, $"{path}.before-{reason}-{DateTime.UtcNow:yyyyMMdd-HHmmss-fffffff}.bak");
            }
            return config;
        }
    }

    public string ForGame() => JsonSerializer.Serialize(Read(), Json);

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
            Write(document, path + ".bak");
            return detached;
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
