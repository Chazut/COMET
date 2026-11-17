using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Reflection;
using System.Runtime.Loader;
var root=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../../"));
var tree=CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(root,"Client/FlankAdapter.cs"))).GetRoot();
string source="using System;using System.Collections.Generic;namespace Comet.Client;"+string.Join("\n",tree.DescendantNodes().OfType<ClassDeclarationSyntax>().Where(c=>c.Identifier.Text!="Plugin"))
 +"partial class Plugin {"+tree.DescendantNodes().OfType<MethodDeclarationSyntax>().Single(m=>m.Identifier.Text=="ObserveFlankPoint")+"}";
var refs=((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator).Select(p=>MetadataReference.CreateFromFile(p));
var compilation=CSharpCompilation.Create("FlankRegression",new[]{CSharpSyntaxTree.ParseText(source),CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"Cases.cs")))},refs,new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
using var bytes=new MemoryStream();var result=compilation.Emit(bytes);
if(!result.Success)throw new Exception(string.Join("\n",result.Diagnostics.Where(d=>d.Severity==DiagnosticSeverity.Error)));
bytes.Position=0;var asm=AssemblyLoadContext.Default.LoadFromStream(bytes);
try{asm.GetType("Comet.Client.Cases")!.GetMethod("Run")!.Invoke(null,null);}catch(TargetInvocationException ex){throw ex.InnerException!;}
