using System.Reflection;
using System.Security.Cryptography;
using Mono.Cecil;
using Comet;

var checks = 0;
void Check(bool condition, string message)
{
    checks++;
    if (!condition) throw new InvalidOperationException(message);
}

bool FixtureLoaded() => AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == "COMET.OptionalTransportFixture");
var messages = new List<string>();
var errors = new List<string>();
var factoryCalls = 0;
object Factory()
{
    factoryCalls++;
    return EmbeddedFika.Load(Assembly.GetExecutingAssembly(), "transport.fixture", "OptionalTransport.Bridge");
}

// The harness has no Fika installation. Solo must never resolve optional types.
Check(!FixtureLoaded(), "optional assembly initially absent");
using (var solo = new EmbeddedFika())
{
    solo.Start(false, false, Factory, new object(), messages.Add, errors.Add);
    solo.Update();
    solo.Dispose();
    Check(factoryCalls == 0 && !FixtureLoaded(), "solo never loads the transport");
    Check(errors.Count == 0 && messages.Single().Contains("Solo"), "solo is an ordinary startup, not a failed plugin");
}

using (var legacy = new EmbeddedFika())
{
    legacy.Start(true, true, Factory, new object(), messages.Add, errors.Add);
    legacy.Update();
    Check(factoryCalls == 0 && !FixtureLoaded(), "old addon prevents double transport initialization");
}

var counters = new Dictionary<string, int>();
var loaded = new EmbeddedFika();
loaded.Start(true, false, Factory, counters, messages.Add, errors.Add);
loaded.Start(true, false, Factory, counters, messages.Add, errors.Add);
loaded.Update();
loaded.Update();
Check(FixtureLoaded() && factoryCalls == 1, "Fika lazily loads exactly one transport from embedded bytes");
Check(counters["initialize"] == 1 && counters["update"] == 2, "logger and updates reach the bridge");
loaded.Dispose();
loaded.Dispose();
loaded.Update();
Check(counters["dispose"] == 1 && counters["update"] == 2, "quit/destroy is idempotent and stops updates");
Check(errors.Count == 0, "normal lifecycle has no errors");

foreach (var failure in new[] { "failInit", "failUpdate", "failDispose" })
{
    counters = new Dictionary<string, int> { [failure] = 1 };
    errors.Clear();
    using var broken = new EmbeddedFika();
    broken.Start(true, false, Factory, counters, messages.Add, errors.Add);
    broken.Update();
    broken.Dispose();
    broken.Update();
    broken.Dispose();
    Check(counters["dispose"] == 1, failure + " cleans up once");
    Check(errors.Count == 1, failure + " reports once without frame-by-frame spam");
    Check(failure != "failInit" || !counters.ContainsKey("update"), "partial initialization never updates");
}

using (var missing = new EmbeddedFika())
{
    errors.Clear();
    missing.Start(true, false,
        () => EmbeddedFika.Load(Assembly.GetExecutingAssembly(), "missing.resource", "OptionalTransport.Bridge"),
        new object(), messages.Add, errors.Add);
    missing.Update();
    Check(errors.Count == 1 && errors[0].Contains("Missing embedded"), "bad packaging is reported without crashing the main plugin");
}

if (args.Length != 2 && args.Length != 3) throw new ArgumentException("Pass the built main DLL and integration DLL paths, optionally the beta 2 server DLL for release checks.");
using var resolver = new DefaultAssemblyResolver();
var sptRoot = Environment.GetEnvironmentVariable("SPT_ROOT") ?? @"C:\Games\SPT-4.1";
resolver.AddSearchDirectory(Path.Combine(sptRoot, "BepInEx", "core"));
using var main = AssemblyDefinition.ReadAssembly(args[0], new ReaderParameters { AssemblyResolver = resolver });
Check(!main.MainModule.AssemblyReferences.Any(r => r.Name.Contains("Fika", StringComparison.OrdinalIgnoreCase)),
    "main assembly has no Fika runtime dependency");
Check(!main.MainModule.GetTypeReferences().Any(t => t.Namespace.StartsWith("Fika.")),
    "main metadata has no optional Fika types");
var plugin = main.MainModule.Types.Single(t => t.CustomAttributes.Any(a => a.AttributeType.Name == "BepInPlugin"));
var dependency = plugin.CustomAttributes.Single(a => a.AttributeType.Name == "BepInDependency" &&
    Equals(a.ConstructorArguments[0].Value, "com.fika.core"));
Check(Convert.ToInt32(dependency.ConstructorArguments[1].Value) == 2, "Fika is a soft BepInEx dependency");
bool CallsLoader(MethodDefinition method) => method.Body.Instructions.Any(i =>
    i.Operand is MethodReference m && m.DeclaringType.Name == "EmbeddedFika" && m.Name == "Start");
Check(!CallsLoader(plugin.Methods.Single(m => m.Name == "Awake")), "Awake does not race legacy addon registration");
Check(CallsLoader(plugin.Methods.Single(m => m.Name == "Start")), "transport starts after all plugin Awake callbacks");
var resource = main.MainModule.Resources.OfType<EmbeddedResource>().Single(r => r.Name == "COMET.Fika.Integration.dll");
var bytes = resource.GetResourceData();
Check(SHA256.HashData(bytes).SequenceEqual(SHA256.HashData(File.ReadAllBytes(args[1]))),
    "main contains the freshly built integration, not a stale binary");
using var bridgeAssembly = AssemblyDefinition.ReadAssembly(new MemoryStream(bytes));
Check(bridgeAssembly.MainModule.Types.All(t => !t.CustomAttributes.Any(a => a.AttributeType.Name == "BepInPlugin")),
    "embedded transport is not a second BepInEx plugin");
Check(bridgeAssembly.MainModule.Types.All(t => t.BaseType?.FullName != "BepInEx.BaseUnityPlugin"),
    "transport lifecycle is owned by the main plugin");
var reference = bridgeAssembly.MainModule.AssemblyReferences.Single(r => r.Name == "COMET");
Check(reference.FullName == main.Name.FullName, "bootstrap reference matches the final main assembly identity");
var entry = bridgeAssembly.MainModule.GetType("Comet.Fika.DoorBridge");
Check(entry != null && new[] { "Initialize", "Update", "Dispose" }.All(n => entry.Methods.Any(m => m.Name == n && m.IsPublic)),
    "embedded bridge exposes the required lifecycle");
if (args.Length == 3)
{
    using var server = AssemblyDefinition.ReadAssembly(args[2]);
    foreach (var assembly in new[] { main, server, bridgeAssembly })
    {
        var version = assembly.CustomAttributes.Single(a => a.AttributeType.Name == "AssemblyInformationalVersionAttribute");
        Check(Equals(version.ConstructorArguments[0].Value, "1.0.0-beta.2"), assembly.Name.Name + " has the beta 2 version");
        var types = assembly.MainModule.GetTypes().ToArray();
        Check(!types.Any(t => t.Name.Contains("MagazineRecovery")), assembly.Name.Name + " has no recovery layer, policy or runtime");
        Check(!types.SelectMany(t => t.Fields).Any(f => f.Name.Contains("MagazineRecovery")), assembly.Name.Name + " has no recovery state");
        Check(!types.SelectMany(t => t.Methods).Any(m => m.Name.Contains("MagazineRecovery") || m.Name == "RememberDroppedMagazine"), assembly.Name.Name + " has no recovery entry points");
        Check(!types.SelectMany(t => t.Properties).Any(p => p.Name == "RecoverDroppedMagazine"), assembly.Name.Name + " has no experimental setting");
    }
    Check(main.MainModule.GetTypes().Any(t => t.Name == "QuickReloadPolicy"), "emergency quick reload is retained");
    Check(plugin.Methods.Any(m => m.Name == "ObserveDroppedReloadMagazine"), "normal dropped-magazine diagnostics are retained");
}
Console.WriteLine($"COMET embedded Fika: {checks} checks passed (solo, Fika, failures, legacy upgrade, built artifacts).");
