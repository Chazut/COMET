using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
var syntax = CSharpSyntaxTree
    .ParseText(File.ReadAllText(Path.Combine(root, "Client/WeaponRangeAdapter.cs")))
    .GetRoot();
var weaponReturn = CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(root, "Client/WeaponReturnAdapter.cs"))).GetRoot();
string Method(SyntaxNode tree, string name) => tree.DescendantNodes()
    .OfType<MethodDeclarationSyntax>().Single(m => m.Identifier.Text == name).ToString();
var contacts = syntax.DescendantNodes().OfType<StructDeclarationSyntax>()
    .Single(t => t.Identifier.Text == "NativeRangeContacts");
string source = "using System; using Comet.Core; using EFT.InventoryLogic; public partial class Probe {"
    + Method(syntax, "ReadRangeGun") + Method(syntax, "WeaponRangeFrameFor")
    + Method(weaponReturn, "ObserveWeaponReturn") + "}" + contacts;
var trees = new[]
{
    CSharpSyntaxTree.ParseText(source),
    CSharpSyntaxTree.ParseText("using System; namespace Comet.Core; " + string.Join("\n",
        CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(root, "Shared/WeaponRange.cs"))).GetRoot()
            .DescendantNodes().OfType<TypeDeclarationSyntax>().Where(t => t.Identifier.Text is
                "IWeaponRangeContacts" or "RecordedRangeContacts" or "WeaponRangePolicy"))),
    CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Cases.cs"))),
};
var refs = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
    .Split(Path.PathSeparator)
    .Select(p => MetadataReference.CreateFromFile(p))
    .ToList();
refs.Add(MetadataReference.CreateFromFile(typeof(Comet.Core.WeaponRangeGun).Assembly.Location));
var build = CSharpCompilation.Create(
    "WeaponRangeAdapterRegression",
    trees,
    refs,
    new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
);
using var bytes = new MemoryStream();
var result = build.Emit(bytes);
if (!result.Success)
    throw new Exception(string.Join("\n", result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
Assembly.Load(bytes.ToArray()).GetType("Probe")!.GetMethod("Run")!.Invoke(null, null);
