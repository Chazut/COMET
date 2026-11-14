using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using BepInEx.Configuration;
using Newtonsoft.Json;
using RaidDiagnostics;

if (args.Length >= 3 && args[0] is "--plan" or "--clean")
{
    string root = Path.GetFullPath(args[1]), mod = args[2];
    if (args[0] == "--clean" && Process.GetProcessesByName("EscapeFromTarkov").Length != 0)
        throw new Exception("Close the game before manual cleanup.");
    var retention = new RaidLogRetention(root, mod, progress: message => Console.Error.WriteLine(message));
    var report = args[0] == "--clean" ? retention.Run() : retention.Plan();
    Console.WriteLine(JsonConvert.SerializeObject(report, Formatting.Indented));
    if (args.Length == 4) File.WriteAllText(args[3], JsonConvert.SerializeObject(report, Formatting.Indented));
    return;
}

int checks = 0;
void Check(bool value, string text) { checks++; if (!value) throw new Exception(text); }
string suiteRoot = Path.Combine(Path.GetTempPath(), "raid-storage-tests-" + Guid.NewGuid().ToString("N"));
string NewRoot() { string root = Path.Combine(suiteRoot, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root); return root; }
string Write(string root, string relative, string content)
{
    string path = Path.Combine(root, relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, content); return path;
}
string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
string Name(int index) => index.ToString("x32");
DateTime Date(int index) => new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(index);
List<string> Raid(string root, string mod, int index, bool indexed = false)
{
    string id = Name(index), stamp = Date(index).ToString("yyyyMMdd-HHmmss"); var paths = new List<string>();
    if (mod == "COMET")
    {
        paths.Add(Write(root, $"diagnostics/{stamp}-{id}.jsonl", new string('a', 4096)));
        paths.Add(Write(root, $"diagnostics/{stamp}-{id}.part0001.jsonl", new string('b', 4096)));
        paths.Add(Write(root, $"performance/performance-{stamp}-{id}.jsonl", "performance"));
    }
    else
    {
        paths.Add(Write(root, $"diagnostics/performance-{stamp}-{id}.jsonl", new string('a', 4096)));
        File.SetLastWriteTimeUtc(paths[0], Date(index).AddSeconds(40));
        paths.Add(Write(root, $"diagnostics/capture-{Date(index).AddSeconds(25):yyyyMMdd-HHmmss}-000-{Guid.NewGuid():N}.json", "capture"));
        File.SetLastWriteTimeUtc(paths[1], Date(index).AddSeconds(26));
        if (indexed)
        {
            var manifest = new RaidLogRetention.Manifest { Mod = mod, Id = id, StartedUtc = Date(index) };
            manifest.Files.AddRange(paths.Select(p => new RaidLogRetention.Entry { Path = Path.GetRelativePath(root, p).Replace('\\', '/') }));
            paths.Add(Write(root, $"diagnostics/raids/{id}.json", JsonConvert.SerializeObject(manifest)));
        }
    }
    return paths;
}
foreach (string mod in new[] { "COMET", "ORBIT" })
{
    string root = NewRoot(); var raids = Enumerable.Range(1, 25).ToDictionary(i => i, i => Raid(root, mod, i, i % 2 == 0));
    string note = Write(root, "diagnostics/notes.json", "keep me");
    string baseline = Write(root, "protected/before-fixes/data.jsonl", new string('z', 100000));
    var engine = new RaidLogRetention(root, mod); var plan = engine.Plan();
    Check(plan.Raids == 25 && plan.KeptRaids == 20, mod + ": groups are raids, not segment counts");
    Check(raids.Values.SelectMany(p => p).All(File.Exists), "planning is read only");
    var report = engine.Run();
    Check(report.ArchivedRaids == 5 && report.PurgedArchives == 0 && report.Warnings.Count == 0, mod + ": old raids compressed without expiry");
    Check(report.AfterBytes < report.BeforeBytes, "compression frees space");
    foreach (int i in Enumerable.Range(1, 25))
        foreach (string path in raids[i]) Check(File.Exists(path) == (i > 5), mod + ": twenty complete recent raids retained");
    foreach (string path in Directory.GetFiles(Path.Combine(root, "archives"), "*.zip"))
    {
        using var zip = ZipFile.OpenRead(path);
        using var reader = new StreamReader(zip.GetEntry("retention.json")!.Open());
        var manifest = JsonConvert.DeserializeObject<RaidLogRetention.Manifest>(reader.ReadToEnd())!;
        foreach (var file in manifest.Files)
        {
            using var stream = zip.GetEntry(file.Path)!.Open();
            Check(Convert.ToHexString(SHA256.HashData(stream)) == file.Sha256, "independent archive SHA256 verification");
        }
    }
    Check(File.Exists(note) && File.Exists(baseline), "unknown and protected data unchanged");
    Check(engine.Run().ArchivedRaids == 0, "second pass is idempotent");
    var tiny = new RaidLogRetention(root, mod, new() { KeepRaids = 20, MaxBytes = 1 }).Run();
    Check(tiny.PurgedArchives == 5 && tiny.OverBudget, "only oldest archives removed; protected floor wins over cap");
    Check(raids.Where(p => p.Key > 5).SelectMany(p => p.Value).All(File.Exists), "quota never removes the newest twenty");
}
{
    string root = NewRoot(); var old = Raid(root, "COMET", 1); Raid(root, "COMET", 2);
    Write(root, Path.GetRelativePath(root, old[1]) + ".keep", "comparison");
    var report = new RaidLogRetention(root, "COMET", new() { KeepRaids = 1, MaxBytes = 1 }).Run();
    Check(report.ArchivedRaids == 0 && old.All(File.Exists), "one .keep protects all raid segments and performance");
}
{
    string root = NewRoot(); var old = Raid(root, "COMET", 1); Raid(root, "COMET", 2);
    var engine = new RaidLogRetention(root, "COMET", new() { KeepRaids = 1 });
    using (var activeWriter = new FileStream(old[0], FileMode.Open, FileAccess.Write, FileShare.Read))
    {
        var report = engine.Run(); Check(report.ArchivedRaids == 0 && report.Warnings.Count > 0 && old.All(File.Exists), "active writer keeps the whole raid");
    }
    Check(engine.Run().ArchivedRaids == 1, "unlocked raid can be archived later");
    string archive = Directory.GetFiles(Path.Combine(root, "archives"), "*.zip").Single();
    Write(root, Path.GetRelativePath(root, archive) + ".keep", "pin");
    var capped = new RaidLogRetention(root, "COMET", new() { KeepRaids = 1, MaxBytes = 1 }).Run();
    Check(File.Exists(archive) && capped.PurgedArchives == 0, "pinned archive survives pressure");
}
{
    string root = NewRoot(); var old = Raid(root, "COMET", 1); Raid(root, "COMET", 2);
    var engine = new RaidLogRetention(root, "COMET", new() { KeepRaids = 1 }); engine.Run();
    string archive = Directory.GetFiles(Path.Combine(root, "archives"), "*.zip").Single(); string hash = Hash(archive);
    using (var zip = ZipFile.OpenRead(archive)) zip.GetEntry(Path.GetRelativePath(root, old[0]).Replace('\\', '/'))!.ExtractToFile(old[0]);
    var retry = engine.Run(); Check(retry.ArchivedRaids == 0 && retry.RemovedOriginals == 1 && Hash(archive) == hash, "partial original removal resumes from verified archive");
    File.WriteAllText(old[0], "changed original");
    retry = engine.Run(); Check(File.Exists(old[0]) && retry.RemovedOriginals == 0 && retry.Warnings.Count > 0, "changed original cannot be removed against stale archive");
    File.WriteAllText(archive, "broken zip");
    retry = engine.Run(); Check(File.Exists(old[0]) && retry.RemovedOriginals == 0 && File.Exists(archive), "corrupt archive cannot authorize deletion");
}
{
    string root = NewRoot(); var old = Raid(root, "COMET", 1); Raid(root, "COMET", 2);
    var cancel = new CancellationTokenSource(); cancel.Cancel();
    try { new RaidLogRetention(root, "COMET", new() { KeepRaids = 1 }).Run(cancel.Token); Check(false, "cancellation expected"); }
    catch (OperationCanceledException) { Check(old.All(File.Exists), "raid-start cancellation preserves originals"); }
    using var held = new FileStream(Path.Combine(root, ".retention.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    try { new RaidLogRetention(root, "COMET").Run(); Check(false, "exclusive cleanup expected"); }
    catch (IOException) { Check(old.All(File.Exists), "concurrent cleanups excluded"); }
}
{
    string root = NewRoot(); var old = Raid(root, "ORBIT", 1, true); Raid(root, "ORBIT", 2, true);
    string outside = Write(suiteRoot, "outside.jsonl", "outside"); string before = Hash(outside);
    string index = old[^1]; var record = JsonConvert.DeserializeObject<RaidLogRetention.Manifest>(File.ReadAllText(index))!;
    record.Files.Add(new() { Path = "../../outside.jsonl" }); File.WriteAllText(index, JsonConvert.SerializeObject(record));
    var report = new RaidLogRetention(root, "ORBIT").Run();
    Check(report.Warnings.Count > 0 && Hash(outside) == before && File.Exists(index), "malformed index cannot escape root");
    string unknown = Write(root, "diagnostics/capture-20100101-000000-000-" + Name(999) + ".json", "unassigned capture");
    new RaidLogRetention(root, "ORBIT", new() { KeepRaids = 1 }).Run();
    Check(File.Exists(unknown), "unassigned legacy captures retained");
}
{
    string root = NewRoot(); var old = Enumerable.Range(1, 4).ToDictionary(i => i, i => Raid(root, "COMET", i));
    using var cancel = new CancellationTokenSource();
    try
    {
        new RaidLogRetention(root, "COMET", new() { KeepRaids = 1 }, _ => cancel.Cancel()).Run(cancel.Token);
        Check(false, "cancellation between groups expected");
    }
    catch (OperationCanceledException)
    {
        Check(Directory.GetFiles(Path.Combine(root, "archives"), "*.zip").Length == 1, "completed archive survives cancellation");
        Check(old[4].All(File.Exists) && old.Values.Count(files => files.All(File.Exists)) == 3, "cancellation leaves other complete raids intact");
        Check(Directory.GetFiles(root, "*.tmp", SearchOption.AllDirectories).Length == 0, "cancellation leaves no partial archives");
    }
}
{
    string root = NewRoot(); var old = Raid(root, "COMET", 1); Raid(root, "COMET", 2);
    var config = new ConfigFile(Path.Combine(suiteRoot, "storage.cfg"), true);
    var messages = new List<string>();
    using var worker = new DiagnosticStorage(config, root, "COMET", messages.Add, messages.Add);
    Check((int)config["Diagnostics storage", "Recent raids to keep"].BoxedValue == 20 && (int)config["Diagnostics storage", "Storage limit GB"].BoxedValue == 5, "real BepInEx defaults: 20 raids, 5 GB");
    config["Diagnostics storage", "Recent raids to keep"].BoxedValue = 1;
    worker.Tick(true, 100); Thread.Sleep(10); Check(old.All(File.Exists), "runtime never starts cleanup in a raid");
    worker.Tick(false, 101); worker.Tick(false, 115); Check(old.All(File.Exists), "runtime lets pending writers finish after raid");
    worker.Tick(false, 117);
    for (int i = 0; i < 200 && messages.Count == 0; i++) { Thread.Sleep(10); worker.Tick(false, 118); }
    Check(messages.Any(s => s.StartsWith("Diagnostic storage: raids=")) && old.All(p => !File.Exists(p)), "runtime archives off-thread after leaving raid");
}
Console.WriteLine($"PASS: {checks} diagnostic storage checks. Evidence: {suiteRoot}");
