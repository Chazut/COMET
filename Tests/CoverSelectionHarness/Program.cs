using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Reflection;
using System.Runtime.Loader;

var root=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../../"));
string Method(string file,string method)=>CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(root,"Client",file+".cs"))).GetRoot()
 .DescendantNodes().OfType<MethodDeclarationSyntax>().Single(m=>m.Identifier.Text==method).ToString();
string source="using System; namespace Comet.Client; partial class Plugin {"+Method("Plugin","FindCover")+"} static partial class VisibleAdapter {"+Method("VisibleAdapter","SelectCover")+"} static partial class ProgressAdapter {"+Method("ProgressAdapter","RetireFailedCover")+"}";
var capabilities=CSharpSyntaxTree.ParseText("class Extract {"+Method("NativeAdapter","Capabilities")+"}").GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>().Single().Body!.Statements;
var coverStatements=capabilities.SkipWhile(s=>!s.ToString().StartsWith("bool cover ="))
 .TakeWhile(s=>!s.ToString().StartsWith("string flankReason="));
source+=" static partial class NativeAdapter { public static bool RetreatAvailable(BotComponent bot,BotState state,Config config) { var s=new Snapshot(); "+string.Join("\n",coverStatements)+" return !s.Unavailable.ContainsKey(Tactic.FallBack); } }";
var trees=new[]{CSharpSyntaxTree.ParseText(source),CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"Cases.cs")))};
var refs=((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator).Select(p=>MetadataReference.CreateFromFile(p));
var compilation=CSharpCompilation.Create("CoverRegression",trees,refs,new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,optimizationLevel:OptimizationLevel.Release));
using var image=new MemoryStream();var result=compilation.Emit(image);
if(!result.Success)throw new Exception(string.Join("\n",result.Diagnostics.Where(d=>d.Severity==DiagnosticSeverity.Error)));
image.Position=0;var assembly=AssemblyLoadContext.Default.LoadFromStream(image);
try{assembly.GetType("Comet.Client.Cases")!.GetMethod("Run")!.Invoke(null,null);}catch(TargetInvocationException ex){throw ex.InnerException!;}
