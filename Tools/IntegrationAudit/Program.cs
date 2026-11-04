using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

// Narrow runtime harnesses execute selected production methods with native test doubles.
if(args[0]=="--methods")
{
    var tree=CSharpSyntaxTree.ParseText(File.ReadAllText(args[1])).GetCompilationUnitRoot();
    var names=args[3].Split(',');
    var selected=names.Select(name=>tree.DescendantNodes().OfType<MethodDeclarationSyntax>().Single(m=>m.Identifier.Text==name));
    var code="using System; using Comet.Core; using EFT; using EFT.InventoryLogic; using SAIN; using SAIN.Components; using UnityEngine;\nnamespace Comet.Client; public sealed partial class Plugin {\n"
        +string.Join("\n",selected.Select(m=>m.ToFullString()))+"\n}";
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[2]))!);File.WriteAllText(args[2],code);return;
}

// Execute the real registration bodies against reference assemblies, replacing only patch installation
// and BigBrain registration with recorders. No Unity game is started and no native action is executed.
string input=Path.GetFullPath(args[0]), output=Path.GetFullPath(args[1]);Directory.CreateDirectory(output);
var files=Directory.GetFiles(input,"*.cs").Select(f=>CSharpSyntaxTree.ParseText(File.ReadAllText(f)).GetCompilationUnitRoot()).ToArray();
var methods=files.SelectMany(r=>r.DescendantNodes().OfType<MethodDeclarationSyntax>()).Where(m=>m.Identifier.Text.StartsWith("Patch")
    &&m.Identifier.Text!="PatchGasExposure"&&m.ParameterList.Parameters.Any(p=>p.Type?.ToString()=="Harmony")).ToArray();
var declarations=files.SelectMany(r=>r.DescendantNodes().OfType<FieldDeclarationSyntax>()).Where(f=>(f.Parent as ClassDeclarationSyntax)?.Identifier.Text=="Plugin").ToArray();
var needed=new HashSet<string>(methods.SelectMany(m=>m.DescendantNodes().OfType<IdentifierNameSyntax>()).Select(n=>n.Identifier.Text));
var fields=new List<FieldDeclarationSyntax>();
bool again=true;while(again){again=false;foreach(var f in declarations)
    if(!fields.Contains(f)&&f.Declaration.Variables.Any(v=>needed.Contains(v.Identifier.Text))){fields.Add(f);foreach(var n in f.DescendantNodes().OfType<IdentifierNameSyntax>())needed.Add(n.Identifier.Text);again=true;}}
var helpers=files.SelectMany(f=>f.DescendantNodes().OfType<MethodDeclarationSyntax>()).Where(m=>m.Identifier.Text=="SeekCoverType");
// Run production transpiler bodies against real method IL as well as resolving registrations.
// Only reflection lookup helpers are substituted: the bundled Harmony runtime targets Unity Mono.
var transpilers=files.SelectMany(f=>f.DescendantNodes().OfType<MethodDeclarationSyntax>())
    .Where(m=>m.ReturnType.ToString()=="IEnumerable<CodeInstruction>").ToArray();
var transpilerHelpers=files.SelectMany(f=>f.DescendantNodes().OfType<MethodDeclarationSyntax>()).Where(m=>m.Identifier.Text=="LocalIndex");
var usings=files.SelectMany(f=>f.Usings).Select(u=>u.ToString()).Distinct().OrderBy(s=>s);
var rewrite=new RegistrationRewrite();
string body=string.Join("\n",fields.Select(f=>(f.Modifiers.Any(SyntaxKind.StaticKeyword)||f.Modifiers.Any(SyntaxKind.ConstKeyword)?f:f.AddModifiers(SyntaxFactory.Token(SyntaxKind.StaticKeyword))).NormalizeWhitespace().ToFullString()))+"\n"+string.Join("\n",methods.Select(m=>rewrite.Visit(m.Modifiers.Any(SyntaxKind.StaticKeyword)?m:m.AddModifiers(SyntaxFactory.Token(SyntaxKind.StaticKeyword)))!.NormalizeWhitespace().ToFullString()));
body+="\n"+string.Join("\n",helpers.Select(m=>m.ToFullString()));
var transpilerRewrite=new TranspilerRewrite();
body+="\n"+string.Join("\n",transpilers.Concat(transpilerHelpers).Select(m=>transpilerRewrite.Visit(m)!.ToFullString()));
var awake=files.SelectMany(r=>r.DescendantNodes().OfType<MethodDeclarationSyntax>()).Single(m=>m.Identifier.Text=="Awake");
var direct=awake.DescendantNodes().OfType<ExpressionStatementSyntax>().Where(e=>e.Expression is InvocationExpressionSyntax i&&i.Expression.ToString()=="harmony.Patch");
body+="\nprivate static void Core(Harmony harmony){"+string.Join("\n",direct.Select(s=>rewrite.Visit(s)!.ToFullString()))+"}";
body+="\npublic static void Run(Harmony h){Core(h);"+string.Join("",awake.DescendantNodes().OfType<InvocationExpressionSyntax>().Where(i=>methods.Any(m=>m.Identifier.Text==i.Expression.ToString())).Select(i=>$"System.Console.WriteLine(\"Checking {i.Expression}\");{i.Expression}(h);"))+"}";
// Verify the actual acceptance observer, including its early returns, in a small separate test double world.
var observer=files.SelectMany(r=>r.DescendantNodes().OfType<MethodDeclarationSyntax>()).Single(m=>m.Identifier.Text=="Accepted");
File.WriteAllText(Path.Combine(output,"Observer.cs"),"using Comet.Core; namespace Comet.ObserverProbe; partial class Plugin {"+observer.ToFullString()+"}");
File.WriteAllText(Path.Combine(output,"Registrations.cs"),string.Join("\n",usings)+"\nusing Comet.Client;\nusing AccessTools = Comet.IntegrationProbe.ProbeAccess;\nusing Harmony = Comet.IntegrationProbe.ProbeHarmony;\nusing HarmonyMethod = Comet.IntegrationProbe.ProbeHook;\nnamespace Comet.IntegrationProbe; static partial class Plugin {"+body+"}");

sealed class RegistrationRewrite:CSharpSyntaxRewriter
{
    public override SyntaxNode? VisitTypeOfExpression(TypeOfExpressionSyntax node)
    {if(node.Type.ToString()=="ClosingInteractionHook"||node.Type.ToString().StartsWith("I01"))return SyntaxFactory.ParseExpression("typeof(Comet.Client.Plugin).GetNestedType(\""+node.Type.ToString()+"\", System.Reflection.BindingFlags.NonPublic)!");return base.VisitTypeOfExpression(node);}
    public override SyntaxNode? VisitInvocationExpression(InvocationExpressionSyntax node)
    {
        if(node.Expression.ToString()=="nameof")
            return SyntaxFactory.LiteralExpression(SyntaxKind.StringLiteralExpression,SyntaxFactory.Literal(node.ArgumentList.Arguments[0].ToString().Split('.').Last()));
        if(node.Expression.ToString()=="BrainManager.AddCustomLayer")
            return node.WithExpression(SyntaxFactory.ParseExpression("ProbeHarmony.AddLayer"));
        return base.VisitInvocationExpression(node);
    }
}

sealed class TranspilerRewrite:CSharpSyntaxRewriter
{
    public override SyntaxNode? VisitTypeOfExpression(TypeOfExpressionSyntax node)
        =>node.Type.ToString()=="Plugin"?SyntaxFactory.ParseExpression("typeof(Comet.Client.Plugin)"):base.VisitTypeOfExpression(node);
    public override SyntaxNode? VisitInvocationExpression(InvocationExpressionSyntax node)
    {
        if(node.Expression.ToString()=="nameof")
            return SyntaxFactory.LiteralExpression(SyntaxKind.StringLiteralExpression,SyntaxFactory.Literal(node.ArgumentList.Arguments[0].ToString().Split('.').Last()));
        return base.VisitInvocationExpression(node);
    }
}
