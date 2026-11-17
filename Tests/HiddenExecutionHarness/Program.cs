using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Reflection;
using System.Runtime.Loader;
using Comet.Core;
var root=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../../"));
var methods=new[]{"RecordHiddenExecution","WatchHiddenMovement","HiddenBeingShot","HiddenCanRun","KeepHiddenChoice","SourceSearchScope","QuietHeardSearch","QuietAudioSearch","QuietSearchAllowed","WalkQuietSearch","FinishPartialSearch"};
string source="using System;using System.Collections.Generic;using System.Reflection;using Comet.Core;using UnityEngine;using UnityEngine.AI;using SAIN.Components;using SAIN.Classes.Bot.Search;using SAIN.SAINComponent.Classes.EnemyClasses;using SAIN.SAINComponent.Classes.Decision;using SAIN.Preset.Shared.Enums;using static SAIN.SAINComponent.Classes.Search.SearchReasonsStruct;namespace Comet.Client;";
foreach(var file in new[]{"HiddenExecutionAdapter.cs","SourceSearchAdapter.cs"}) {
 var t=CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(root,"Client",file))).GetRoot();
 source+=string.Join("\n",t.DescendantNodes().OfType<ClassDeclarationSyntax>().Where(c=>c.Identifier.Text!="Plugin"));
 var plugin=t.DescendantNodes().OfType<ClassDeclarationSyntax>().Single(c=>c.Identifier.Text=="Plugin");
 source+="public partial class Plugin {"+string.Join("\n",plugin.Members.Where(m=>m is DelegateDeclarationSyntax || m is FieldDeclarationSyntax || m is MethodDeclarationSyntax d&&methods.Contains(d.Identifier.Text)))+"}";
}
source+=File.ReadAllText(Path.Combine(root,"Client/SearchExecutionAdapter.cs")).Split("namespace Comet.Client;")[1];
var refs=((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator).Append(typeof(HiddenEnemyDecision).Assembly.Location).Distinct().Select(p=>MetadataReference.CreateFromFile(p));
var c=CSharpCompilation.Create("HiddenExecutionRegression",new[]{CSharpSyntaxTree.ParseText(source),CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"Cases.cs")))},refs,new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
using var stream=new MemoryStream();var result=c.Emit(stream);
if(!result.Success)throw new Exception(string.Join("\n",result.Diagnostics.Where(d=>d.Severity==DiagnosticSeverity.Error)));
stream.Position=0;var a=AssemblyLoadContext.Default.LoadFromStream(stream);
try{a.GetType("Comet.Client.Cases")!.GetMethod("Run")!.Invoke(null,null);}catch(TargetInvocationException e){throw e.InnerException!;}
