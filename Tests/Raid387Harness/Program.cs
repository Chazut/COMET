using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Reflection;
using System.Runtime.Loader;
var root=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../../"));
var source=File.ReadAllText(Path.Combine(root,"Client/WeaponSyncAdapter.cs"));
var sample=CSharpSyntaxTree.ParseText(source).GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>()
    .Single(m=>m.Identifier.Text=="SampleWeaponSync").WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PublicKeyword))).NormalizeWhitespace();
var trees=new[]{
    CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(root,"Client/SearchExecutionAdapter.cs"))),
    CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(root,"Shared/WeaponSync.cs"))),
    CSharpSyntaxTree.ParseText("using System; using Comet.Core; using UnityEngine; namespace Comet.Client { public partial class Plugin {"+sample+"}}"),
    CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"Cases.cs")))
};
var refs=((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator).Select(p=>MetadataReference.CreateFromFile(p));
var c=CSharpCompilation.Create("Raid387Regression",trees,refs,new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,optimizationLevel:OptimizationLevel.Release));
using var stream=new MemoryStream();var result=c.Emit(stream);
if(!result.Success)throw new Exception(string.Join("\n",result.Diagnostics.Where(d=>d.Severity==DiagnosticSeverity.Error)));
stream.Position=0;var a=AssemblyLoadContext.Default.LoadFromStream(stream);
try{a.GetType("Comet.Client.Cases")!.GetMethod("Run")!.Invoke(null,null);}catch(TargetInvocationException e){throw e.InnerException!;}
