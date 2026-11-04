using Comet.Core;
using System.Text.Json;
using SPTarkov.DI.Annotations;

namespace Comet.Server;

[Injectable(InjectionType.Singleton)]
public sealed class ConfigStore
{
    private readonly object gate = new();
    private readonly string path;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    public ConfigStore() : this(Path.Combine(Path.GetDirectoryName(typeof(ConfigStore).Assembly.Location)!, "config", "config.json")) { }
    public PresetLibrary Presets { get; }
    public IntegrationReports Integration { get; } = new();
    internal ConfigStore(string path) { this.path = path; Presets = new PresetLibrary(Path.Combine(Path.GetDirectoryName(path)!, "presets.json")); }
    public CometConfig Read()
    {
        lock (gate)
        {
            var config = File.Exists(path) ? JsonSerializer.Deserialize<CometConfig>(File.ReadAllText(path), Json)
                ?? throw new InvalidDataException("Empty COMET configuration.") : new CometConfig();
            config.Validate();
            return config;
        }
    }
    public string ForGame() => JsonSerializer.Serialize(Read(), Json);
    public CometConfig Save(CometConfig proposed, long expectedRevision)
    {
        lock (gate)
        {
            var detached = JsonSerializer.Deserialize<CometConfig>(JsonSerializer.Serialize(proposed, Json), Json)!;
            detached.Validate();
            var current = Read();
            if (current.Revision != expectedRevision) throw new InvalidOperationException("Settings changed in another tab. Reload before saving.");
            detached.Revision = checked(current.Revision + 1);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    JsonSerializer.Serialize(stream, detached, Json);
                    stream.Flush(true);
                }
                if (File.Exists(path)) File.Replace(temp, path, path + ".bak");
                else File.Move(temp, path);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
            return detached;
        }
    }
}
