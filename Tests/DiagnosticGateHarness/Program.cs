using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
var fragments = new List<string>();
foreach (
    var (file, method, runtime, key) in new[]
    {
        ("ReloadTimingAdapter.cs", "RecordReloadTiming", "ReloadTimingRuntime", "LogKey"),
        ("PistolSwapAdapter.cs", "RecordPistolSwap", "PistolSwapRuntime", "LogKey"),
        ("WeaponRangeAdapter.cs", "RecordWeaponRange", "WeaponRangeRuntime", "LogKey"),
        ("DoorTransitAdapter.cs", "RecordDoorTransit", "DoorTransitRuntime", "Key"),
    }
)
{
    var syntax = CSharpSyntaxTree
        .ParseText(File.ReadAllText(Path.Combine(root, "Client", file)))
        .GetRoot();
    fragments.Add(
        "partial class Probe {"
            + syntax
                .DescendantNodes()
                .OfType<MethodDeclarationSyntax>()
                .Single(x => x.Identifier.Text == method)
            + "}"
    );
    var type = syntax
        .DescendantNodes()
        .OfType<ClassDeclarationSyntax>()
        .Single(x => x.Identifier.Text == runtime);
    fragments.Add(
        "partial class "
            + runtime
            + " {"
            + type.Members.OfType<FieldDeclarationSyntax>()
                .Single(x => x.Declaration.Variables.Any(v => v.Identifier.Text == key))
            + "}"
    );
}
var silent = CSharpSyntaxTree
    .ParseText(File.ReadAllText(Path.Combine(root, "Client/SilentSearchAdapter.cs")))
    .GetRoot();
fragments.Add(
    "partial class Probe {"
        + silent
            .DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Single(x => x.Identifier.Text == "RecordSilent")
        + "}"
);
var abandonment = CSharpSyntaxTree
    .ParseText(File.ReadAllText(Path.Combine(root, "Client/SearchAbandonmentAdapter.cs")))
    .GetRoot();
fragments.Add(
    "partial class Probe {"
        + abandonment
            .DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Single(x => x.Identifier.Text == "RecordAbandonment")
        + "}"
);
fragments.Add(
    "partial class Probe {"
        + abandonment
            .DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Single(x => x.Identifier.Text == "NeedsAbandonment")
        + "}"
);
var source = "using System; using Comet.Core;\n" + string.Join("\n", fragments);
var refs = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
    .Split(Path.PathSeparator)
    .Select(p => MetadataReference.CreateFromFile(p))
    .ToList();
refs.Add(MetadataReference.CreateFromFile(typeof(Comet.Core.PistolSwapTrace).Assembly.Location));
var build = CSharpCompilation.Create(
    "DiagnosticGateRegression",
    new[]
    {
        CSharpSyntaxTree.ParseText(source),
        CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(root, "Shared/ReplayCapture.cs")) +
            "\ninternal static class CaptureSetup { [System.Runtime.CompilerServices.ModuleInitializer] internal static void Init() => ReplayCapture.Configure(true, true, true, true); }"),
        CSharpSyntaxTree.ParseText(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Cases.cs"))
        ),
    },
    refs,
    new CSharpCompilationOptions(
        OutputKind.DynamicallyLinkedLibrary,
        optimizationLevel: OptimizationLevel.Release
    )
);
using var bytes = new MemoryStream();
var result = build.Emit(bytes);
if (!result.Success)
    throw new Exception(string.Join("\n", result.Diagnostics));
Assembly.Load(bytes.ToArray()).GetType("Probe")!.GetMethod("Run")!.Invoke(null, null);
