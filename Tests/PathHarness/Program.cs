using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Reflection;
using System.Runtime.Loader;

var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
MethodDeclarationSyntax Method(string file, string name) => CSharpSyntaxTree.ParseText(
    File.ReadAllText(Path.Combine(root, "Client", file + ".cs"))).GetRoot()
    .DescendantNodes().OfType<MethodDeclarationSyntax>().Single(m => m.Identifier.Text == name);

// Execute the actual production calculations, replacing only engine objects.
// Do not copy the algorithms into tests: changes in these methods must be tested.
var capabilities = Method("NativeAdapter", "Capabilities").Body!.Statements;
var opportunity = Method("OpportunityAdapter", "ReadOpportunity").DescendantNodes()
    .OfType<TryStatementSyntax>().First().Block.Statements
    .SkipWhile(s => !s.ToString().StartsWith("var corners ="))
    .TakeWhile(s => !s.ToString().StartsWith("var own ="));
var quiet = Method("QuietApproachAdapter", "CaptureQuiet").Body!.Statements
    .TakeWhile(s => !s.ToString().StartsWith("bool threat="));
string probe = """
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;
namespace Comet.Client;
internal static class Probe {
    internal static readonly List<Vector3> Samples = new();
    private static double Exposure(Vector3 p, List<KnownThreat> threats, double lifetime) { Samples.Add(p); return 1; }
    private static string GrenadeCapability(Bot bot, Enemy enemy, Vector3 known, State state, Config config) => "ready";
""" + "internal static Situation Capabilities(Bot bot, Enemy enemy, Config config) { var s=new Situation(); var state=new State();\n" +
    string.Join("\n", capabilities) + "\nreturn s; }\n" +
    "internal static (int Count, double Length, double Vertical) Opportunity(Enemy enemy) {\n" +
    string.Join("\n", opportunity) + "\nreturn (count,length,vertical); }\n" +
    "internal static (bool Complete,double Length) Quiet(Bot b, Enemy e) {\n" +
    string.Join("\n", quiet) + "\nreturn (complete,length); }\n" + "\n}";
var trees = new[] {
    CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(root,"Client/EnemyPathCorners.cs"))),
    CSharpSyntaxTree.ParseText(probe),
    CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"Cases.cs")))
};
var refs = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
    .Select(p => MetadataReference.CreateFromFile(p));
var compilation = CSharpCompilation.Create("PathRegression", trees, refs,
    new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Release));
using var image = new MemoryStream();
var result = compilation.Emit(image);
if (!result.Success) throw new Exception(string.Join("\n", result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
image.Position = 0;
var assembly = AssemblyLoadContext.Default.LoadFromStream(image);
try { assembly.GetType("Comet.Client.Cases")!.GetMethod("Run")!.Invoke(null,null); }
catch (TargetInvocationException ex) { throw ex.InnerException!; }
