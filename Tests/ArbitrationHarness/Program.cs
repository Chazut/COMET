using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Reflection;
using System.Runtime.Loader;

var root=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../../"));
MethodDeclarationSyntax Method(string file,string name)=>CSharpSyntaxTree.ParseText(
 File.ReadAllText(Path.Combine(root,"Client",file+".cs"))).GetRoot().DescendantNodes()
 .OfType<MethodDeclarationSyntax>().Single(m=>m.Identifier.Text==name);
string[] methods={"TacticalScope","TacticalHandsBusy","NativeChoiceHasPriority","ArbitrateSquad","ArbitrationEvent","ExecutionAction","SampleDecisionExecution"};
string source="using System; using Comet.Core; namespace Comet.Client; partial class Plugin {\n"
 +string.Join("\n",methods.Select(n=>Method("DecisionArbitrationAdapter",n).ToString()))
 +Method("Plugin","ContinueCover")+"\n"+Method("OutcomeAdapter","OutcomeAction")
 +"\n"+Method("CrossfireAdapter","ContinueCrossfireDecision")+"\n"+Method("CrossfireAdapter","CrossfirePreparing")+"\n}";
source += "\npartial class Plugin {"+Method("PlanContinuityAdapter","OwnedPlanService")
 +"\n"+Method("PlanContinuityAdapter","ObservePlanService")+"\n"+Method("MedicalChoiceAdapter","MedicalAction")+"}";
source += CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(root,"Client/PlanContinuityAdapter.cs"))).GetRoot()
 .DescendantNodes().OfType<ClassDeclarationSyntax>().Single(c=>c.Identifier.Text=="PlanServiceRuntime").ToString();
// Check the actual chooser uses the audited gate; the gate is executed below, not copied.
var choose=Method("Plugin","Choose");
if(!choose.ToString().Contains("NativeChoiceHasPriority(bot,state)")||!choose.ToString().Contains("state.Execution.CoolingDown(Time.time)"))
 throw new Exception("Chooser bypassed arbitration or execution recovery gate.");
var trees=new[]{CSharpSyntaxTree.ParseText(source),
 CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(root,"Shared/DecisionExecution.cs"))),
 CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(root,"Shared/IntegrationContract.cs"))),
 CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"Cases.cs")))};
var refs=((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator).Select(p=>MetadataReference.CreateFromFile(p));
var compilation=CSharpCompilation.Create("ArbitrationRegression",trees,refs,new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,optimizationLevel:OptimizationLevel.Release));
using var image=new MemoryStream();var result=compilation.Emit(image);
if(!result.Success)throw new Exception(string.Join("\n",result.Diagnostics.Where(d=>d.Severity==DiagnosticSeverity.Error)));
image.Position=0;var assembly=AssemblyLoadContext.Default.LoadFromStream(image);
try{assembly.GetType("Comet.Client.Cases")!.GetMethod("Run")!.Invoke(null,null);}
catch(TargetInvocationException ex){throw ex.InnerException!;}
