using System.Runtime.CompilerServices;
using Comet.Client;
using Comet.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SAIN.Plugin;
using SAIN.Preset;
using SAIN.Preset.Shared.GlobalSettings;

int checks = 0;
void Check(bool ok, string why)
{
    checks++;
    if (!ok)
        throw new Exception(why);
}
GlobalSettingsClass Settings(bool enabled) =>
    JsonConvert.DeserializeObject<GlobalSettingsClass>(
        JsonConvert.SerializeObject(
            new
            {
                Move = new { JUMP_TOGGLE = enabled, LEAN_TOGGLE = enabled },
                Mind = new { TARGET_SUPPRESS_TOGGLE = enabled },
                General = new { BotsUseGrenades = enabled, BotVsBotGrenade = enabled },
            }
        )
    )!;
void All(bool expected, string why)
{
    Check(SainPresetAccess.AllowsJump == expected, why + " jump");
    Check(SainPresetAccess.AllowsLean == expected, why + " lean");
    Check(SainPresetAccess.AllowsGrenades() == expected, why + " grenades");
    Check(SainPresetAccess.AllowsGrenades(true) == expected, why + " AI grenades");
    Check(SainPresetAccess.AllowsSuppression == expected, why + " suppression");
}

var off = Settings(false);
var chosen = new SAINPresetClass
{
    GlobalSettings = off,
    Info = new() { Name = "Selected OFF" },
};
PresetHandler.LoadedPreset = chosen;
var preview = Settings(true);
Check(
    ReferenceEquals(GlobalSettingsClass.Instance, preview),
    "real SAIN deserialization replaces singleton"
);
All(false, "unselected ON preset cannot enable COMET");
Check(ReferenceEquals(GlobalSettingsClass.Instance, preview), "COMET never changes SAIN singleton");
var active = new SAINPresetClass
{
    GlobalSettings = preview,
    Info = new() { Name = "Selected ON" },
};
PresetHandler.LoadedPreset = active;
All(true, "genuine selection takes effect without diagnostic tick");
_ = Settings(false);
All(true, "unselected OFF preset cannot disable COMET");
preview.General.BotVsBotGrenade = false;
Check(
    SainPresetAccess.AllowsGrenades() && !SainPresetAccess.AllowsGrenades(true),
    "AI grenade restriction preserved"
);
preview.Move.LEAN_TOGGLE = false;
Check(
    !SainPresetAccess.AllowsLean && SainPresetAccess.AllowsJump,
    "in-place SAIN editor changes read immediately"
);
var description = SainPresetAccess.Describe(active);
Check(
    description.Name == "Selected ON"
        && !description.SingletonMatches
        && description.Availability == "ready",
    "diagnostic names authoritative preset and mismatch"
);
PresetHandler.LoadedPreset = null;
All(false, "missing preset fails closed");
active.GlobalSettings = null;
PresetHandler.LoadedPreset = active;
All(false, "missing globals fail closed");
active.GlobalSettings = preview;
preview.Move = null!;
preview.General = null!;
preview.Mind = null!;
All(false, "missing sections fail closed");
Check(
    SainPresetAccess.Describe(active).Availability == "settings-incomplete",
    "incomplete sections diagnosed"
);
for (int i = 0; i < 32; i++)
{
    chosen.GlobalSettings = Settings(i % 2 == 0);
    PresetHandler.LoadedPreset = chosen;
    _ = Settings(i % 2 != 0);
    All(i % 2 == 0, "alternating load order " + i);
}

var events = new List<string>();
Comet.Api.Decisions.RaidClosed = false;
Comet.Api.Decisions.Published += events.Add;
var p = new Plugin();
p.Tick(0);
Check(events.Count == 1, "initial context emitted");
var initial = JObject.Parse(events[0]);
Check(
    initial["configRevision"]!.Value<int>() == 17
        && initial["profileId"]!.Value<string>() == ""
        && initial["feature"]!.Value<string>() == "C14",
    "raid context not a bot action"
);
for (int i = 1; i < 50; i++)
    p.Tick(i);
Check(events.Count == 1, "unchanged context does not spam logs");
chosen.GlobalSettings = Settings(true);
p.Tick(50);
Check(events.Count == 2, "changed context diagnosed");
chosen.GlobalSettings = Settings(false);
p.Tick(51);
chosen.GlobalSettings = Settings(true);
p.Tick(52);
p.Tick(55);
Check(
    events.Count == 3
        && JObject.Parse(events[2])["sainPreset"]!["coalescedChanges"]!.Value<int>() == 1,
    "rapid observed edits coalesced at five seconds"
);
PresetHandler.LoadedPreset = new() { Info = chosen.Info, GlobalSettings = chosen.GlobalSettings };
p.Tick(60);
Check(
    JObject.Parse(events.Last())["reason"]!.Value<string>() == "selected-preset-changed",
    "same-name selection replacement diagnosed"
);
int before = events.Count;
p.Authority = false;
PresetHandler.LoadedPreset = null;
p.Tick(70);
Check(events.Count == before, "Fika remote does not report authority context");
p.Authority = true;
p.Tick(71);
Check(
    JObject.Parse(events.Last())["sainPreset"]!["availability"]!.Value<string>()
        == "preset-missing",
    "missing selected preset reported"
);
before = events.Count;
p.config = null;
p.Tick(80);
Check(events.Count == before, "no fake config revision before config loads");
p.config = new();
for (int i = 0; i < 25; i++)
{
    p.Reset();
    p.session = "raid-" + i;
    p.Tick(90 + i);
    Check(
        JObject.Parse(events.Last())["reason"]!.Value<string>() == "initial",
        "fresh raid initial report " + i
    );
}
var audit = new SainPresetAudit();
var weak = Capture(audit);
GC.Collect();
GC.WaitForPendingFinalizers();
GC.Collect();
Check(!weak.IsAlive, "audit does not retain old preset");
GC.KeepAlive(audit);
var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts/c14"));
Directory.CreateDirectory(root);
File.WriteAllText(Path.Combine(root, "preset.json"), events[0]);
File.WriteAllLines(Path.Combine(root, "preset.jsonl"), events);
Console.WriteLine(
    $"C14 production access and diagnostic adapter: {checks} checks, {events.Count} reports. Real SAIN settings/deserialization; preset holder and Unity clock doubled."
);
int difficultyStart = checks;
events.Clear();
PresetHandler.LoadedPreset = chosen;
var sainBefore = JsonConvert.SerializeObject(chosen);
var singletonBefore = GlobalSettingsClass.Instance;
foreach (string name in new[] { "Hard", "Normal", "Easy" })
{
    p.config = DifficultyPresets.Create(name);
    p.config.Revision = 38;
    p.ReportDifficulty();
    var report = JObject.Parse(events.Last());
    Check(
        report["feature"]!.Value<string>() == "U01" && report["profileId"]!.Value<string>() == "",
        "difficulty is a raid context, not a bot action"
    );
    Check(
        report["configRevision"]!.Value<int>() == 38
            && report["cometPreset"]!["baseProfile"]!.Value<string>() == name,
        "server profile and revision reported"
    );
    Check(
        !report["cometPreset"]!["customized"]!.Value<bool>(),
        "standard profile recorded accurately"
    );
    Check(
        ReferenceEquals(PresetHandler.LoadedPreset, chosen)
            && ReferenceEquals(singletonBefore, GlobalSettingsClass.Instance)
            && JsonConvert.SerializeObject(chosen) == sainBefore,
        "difficulty never changes real SAIN settings or preset references"
    );
}
p.config!.CoverFeint.PeekChance = 19;
p.ReportDifficulty();
Check(
    JObject.Parse(events.Last())["cometPreset"]!["changedSettings"]!.Value<int>() == 1,
    "custom tweak recorded"
);
before = events.Count;
p.Authority = false;
p.ReportDifficulty();
Check(events.Count == before, "Fika remote cannot publish difficulty context");
p.Authority = true;
p.config = null;
p.ReportDifficulty();
Check(events.Count == before, "missing config never invents difficulty");
string u01Root = Path.GetFullPath(Path.Combine(root, "../u01"));
Directory.CreateDirectory(u01Root);
File.WriteAllText(Path.Combine(u01Root, "preset.json"), events.Last());
File.WriteAllLines(Path.Combine(u01Root, "preset.jsonl"), events);
Check(events.All(x => x.Length < 1200), "bounded metadata without full config payload");
Console.WriteLine(
    $"U01 production diagnostic adapter: {checks - difficultyStart} checks, {events.Count} reports."
);
int personalStart = checks;
events.Clear();
p.config = DifficultyPresets.Create("Normal");
p.config.Revision = 57;
p.config.PresetSource = new()
{
    Id = "2aa31d28f1c44b3e84c270e321ac2171",
    Name = "My Customs setup",
    Revision = 3,
    SettingsHash = PresetIdentity.Fingerprint(p.config),
};
p.ReportDifficulty();
var personal = JObject.Parse(events.Last())["cometPreset"]!;
Check(
    personal["presetName"]!.Value<string>() == "My Customs setup"
        && personal["presetRevision"]!.Value<long>() == 3,
    "U07 personal name and revision from accepted config"
);
Check(!personal["modifiedSincePreset"]!.Value<bool>(), "U07 unmodified personal copy");
p.config.CoverFeint.PeekChance = 19;
p.config.Revision = 58;
p.ReportDifficulty();
var edited = JObject.Parse(events.Last());
Check(
    edited["cometPreset"]!["modifiedSincePreset"]!.Value<bool>()
        && edited["configRevision"]!.Value<int>() == 58,
    "U07 subsequent edits and active revision reported"
);
Check(
    edited["cometPreset"]!["presetId"]!.Value<string>() == p.config.PresetSource.Id,
    "U07 stable identity for duplicate display names across recordings"
);
Check(
    p.Logger.Messages.Last().Contains("My Customs setup")
        && p.Logger.Messages.Last().Contains("preset revision=3")
        && p.Logger.Messages.Last().Contains("further edits=True"),
    "U07 readable log with provenance"
);
before = events.Count;
p.Authority = false;
p.ReportDifficulty();
Check(events.Count == before, "U07 no remote Fika duplicate context");
p.Authority = true;
Check(
    ReferenceEquals(PresetHandler.LoadedPreset, chosen)
        && ReferenceEquals(singletonBefore, GlobalSettingsClass.Instance)
        && JsonConvert.SerializeObject(chosen) == sainBefore,
    "U07 no SAIN settings changes"
);
Check(events.All(x => x.Length < 1500), "U07 metadata stays bounded");
string u07Root = Path.GetFullPath(Path.Combine(root, "../u07"));
Directory.CreateDirectory(u07Root);
File.WriteAllText(Path.Combine(u07Root, "preset.json"), events.Last());
File.WriteAllLines(Path.Combine(u07Root, "preset.jsonl"), events);
Console.WriteLine(
    $"U07 production diagnostic adapter: {checks - personalStart} checks, {events.Count} reports."
);
[MethodImpl(MethodImplOptions.NoInlining)]
static WeakReference Capture(SainPresetAudit audit)
{
    var preset = new object();
    audit.Observe(preset, new() { Name = "Old" }, 0);
    return new(preset);
}
