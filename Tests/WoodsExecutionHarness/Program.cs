using System.Reflection;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
string Method(string file, string name) =>
    CSharpSyntaxTree
        .ParseText(File.ReadAllText(Path.Combine(root, "Client", file + ".cs")))
        .GetRoot()
        .DescendantNodes()
        .OfType<MethodDeclarationSyntax>()
        .Single(m => m.Identifier.Text == name)
        .ToString();
string source =
    "using System; namespace Comet.Client { partial class Plugin {"
    + Method("DoorAmbushAdapter", "AmbushHoldStep")
    + Method("RetreatWeaveAdapter", "UpdateWeaveContact")
    + Method("Plugin", "FindCover")
    + Method("Plugin", "ObserveNativeCover")
    + "}}";
var refs = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
    .Split(Path.PathSeparator)
    .Select(p => MetadataReference.CreateFromFile(p));
var compilation = CSharpCompilation.Create(
    "WoodsExecutionRegression",
    new[]
    {
        CSharpSyntaxTree.ParseText(source),
        CSharpSyntaxTree.ParseText(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Cases.cs"))
        ),
    },
    refs,
    new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
);
using var image = new MemoryStream();
var result = compilation.Emit(image);
if (!result.Success)
    throw new Exception(
        string.Join("\n", result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error))
    );
image.Position = 0;
var assembly = AssemblyLoadContext.Default.LoadFromStream(image);
try
{
    assembly.GetType("Comet.Client.Cases")!.GetMethod("Run")!.Invoke(null, null);
}
catch (TargetInvocationException e)
{
    throw e.InnerException!;
}
