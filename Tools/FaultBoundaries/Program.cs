using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Text.Json;
using System.Text.RegularExpressions;

// Compile-time C08 boundaries. No Harmony patch on COMET itself, runtime stack inspection,
// or reflection Invoke in the normal execution path. Originals remain usable by narrow harnesses.
var input=Path.GetFullPath(args[0]);var output=Path.GetFullPath(args[1]);Directory.CreateDirectory(output);
var files=Directory.GetFiles(input,"*.cs");var callbacks=new Dictionary<string,string>();
var sourceNames=new HashSet<string>(files.Select(f=>Path.GetFileName(f)!),StringComparer.OrdinalIgnoreCase);
foreach(var stale in Directory.GetFiles(output,"*.cs"))if(!sourceNames.Contains(Path.GetFileName(stale)))File.Delete(stale);
foreach(var file in files)foreach(Match m in Regex.Matches(File.ReadAllText(file),@"(prefix|postfix|finalizer|transpiler):\s*new HarmonyMethod\(typeof\(\w+\),\s*nameof\((?:\w+\.)?(\w+)\)"))callbacks[m.Groups[2].Value]=m.Groups[1].Value;
var manifest=new List<object>();
var fields=new List<object>();
foreach(var file in files){var source=File.ReadAllText(file);var tree=CSharpSyntaxTree.ParseText(source);var name=Path.GetFileNameWithoutExtension(file);
 foreach(var f in tree.GetRoot().DescendantNodes().OfType<FieldDeclarationSyntax>().Where(f=>(f.Parent as ClassDeclarationSyntax)?.Identifier.Text=="Plugin"))
 foreach(var v in f.Declaration.Variables)fields.Add(new{file=name,name=v.Identifier.Text,type=f.Declaration.Type.ToString(),modifiers=f.Modifiers.ToString()});
 // Counter observation has its own local sink boundary. Never quarantine/release
 // a combat action because a diagnostic read failed (including nested methods).
 var root=name=="FaultIsolationAdapter"||name.StartsWith("Lifecycle")||name.StartsWith("RaidCleanup")||name=="ControlIntegration"||name=="ControlJournalAdapter"||name=="ValidityAdapter"||name=="Diagnostics"||name=="TacticCountersAdapter"||name=="CombatJournalAdapter"?tree.GetRoot():new Boundaries(name,callbacks,manifest).Visit(tree.GetRoot())!;
 File.WriteAllText(Path.Combine(output,Path.GetFileName(file)),root.NormalizeWhitespace().ToFullString());}
File.WriteAllText(Path.Combine(output,"boundaries.json"),JsonSerializer.Serialize(manifest,new JsonSerializerOptions{WriteIndented=true}));
File.WriteAllText(Path.Combine(output,"fields.json"),JsonSerializer.Serialize(fields,new JsonSerializerOptions{WriteIndented=true}));
if (File.Exists(Path.Combine(input,"IntegrationAdapter.cs")))
{
 var required=new SortedSet<string>(StringComparer.Ordinal);
 var gas=new SortedSet<string>(StringComparer.Ordinal);
 var enums=new SortedSet<string>(StringComparer.Ordinal);
 foreach(var file in files)
 {
  var source=File.ReadAllText(file);
  foreach(Match m in Regex.Matches(source,@"(prefix|postfix|transpiler|finalizer):\s*new HarmonyMethod\(typeof\((\w+)\),\s*nameof\((?:\w+\.)?(\w+)\)"))
  {
   if(m.Groups[3].Value=="RecoveryFikaSent")continue;
   string owner=m.Groups[2].Value;
   if(owner is not ("Plugin" or "ClosingInteractionHook")&&!owner.StartsWith("I01",StringComparison.Ordinal))throw new InvalidOperationException("Unclassified I01 hook owner: "+owner);
   string callback=owner=="Plugin"?m.Groups[3].Value:"Comet.Client.Plugin+"+owner+"."+m.Groups[3].Value;
   (Path.GetFileName(file) is "GasExposureAdapter.cs" or "ManimalGasBridge.cs"?gas:required).Add(m.Groups[1].Value+":"+callback);
  }
  foreach(Match m in Regex.Matches(source,@"\b(ECombatDecision|ESquadDecision|ESelfActionType)\.(\w+)"))enums.Add(m.Value);
 }
 string enumChecks=string.Join("\n",enums.Select(e=>{var type=e.Split('.')[0];return $"if (!System.Enum.IsDefined(typeof({type}), {e})) throw new System.InvalidOperationException(\"Unsupported SAIN enum: {e}\");";}));
 string manifestSource="namespace Comet.Client; internal static class IntegrationManifest { internal static readonly string[] RequiredCallbacks = new string[]{"
  +string.Join(",",required.Select(s=>JsonSerializer.Serialize(s)))+"}; internal static readonly string[] GasCallbacks=new string[]{"+string.Join(",",gas.Select(s=>JsonSerializer.Serialize(s)))+"}; internal static void ValidateEnums(){"+enumChecks+"} }";
 File.WriteAllText(Path.Combine(output,"IntegrationManifest.Generated.cs"),"using SAIN.Preset.Shared.Enums;\n"+manifestSource);
 File.WriteAllText(Path.Combine(output,"integration-manifest.json"),JsonSerializer.Serialize(new{RequiredCallbacks=required,GasCallbacks=gas,Enums=enums},new JsonSerializerOptions{WriteIndented=true}));
}
Console.WriteLine($"C08: {manifest.Count} execution/cleanup boundaries generated.");

sealed class Boundaries(string file,Dictionary<string,string> callbacks,List<object> manifest):CSharpSyntaxRewriter
{
 static readonly HashSet<string> Global=new(){"Update","UpdateDoorMotion","UpdateDoorClosing","UpdateDoorSupports","UpdateMovementHearing","UpdateBlasts","CheckCoverScans","SweepClearGrenades","FlushFloorCounters","FlushCloseCounters","FlushImpactCounters","EndRaid","ClearDoorMotion","ClearDoorClosing","EndCoverScans","ClearBlasts","ClearDoorSupports"};
 public override SyntaxNode? VisitMethodDeclaration(MethodDeclarationSyntax m)
 {
  string name=m.Identifier.Text,owner=(m.Parent as TypeDeclarationSyntax)?.Identifier.Text??"";
  bool callback=callbacks.TryGetValue(name,out var kind)&&kind!="transpiler";
  if(m.Body==null&&m.ExpressionBody==null||m.Modifiers.Any(SyntaxKind.PartialKeyword)||name.StartsWith("Patch")||kind=="transpiler"||name.EndsWith("Error")||name=="IsPmcAi")return m;
  // Skip diagnostics as their own independent sink boundary below; never quarantine combat for a consumer failure.
  bool telemetry=(name.StartsWith("Record")||name=="Emit")&&m.ToString().Contains("JsonConvert.SerializeObject");
  var pars=m.ParameterList.Parameters;
  var bot=pars.FirstOrDefault(p=>p.Type?.ToString().TrimEnd('?')=="BotComponent"&&!p.Modifiers.Any(SyntaxKind.OutKeyword));
  var state=pars.FirstOrDefault(p=>p.Type?.ToString().TrimEnd('?')=="BotState"&&!p.Modifiers.Any(SyntaxKind.OutKeyword));
  var other=pars.FirstOrDefault(p=>p.Identifier.Text=="__instance"||p.Type?.ToString().TrimEnd('?') is "BotOwner" or "PlayerComponent" or "Enemy");
  bool eligible=bot!=null||state!=null||callback||owner=="Plugin"&&(Global.Contains(name)||Regex.IsMatch(name,@"^(Clear|End)[A-Z]"));
  if(!eligible)return m;
  if(m.DescendantNodes().OfType<YieldStatementSyntax>().Any())return m;
  string module=file.Replace("Adapter","").Replace("Integration","");
  if(file.StartsWith("Crossfire")||file.StartsWith("SquadStorm")||file.StartsWith("SquadManeuver")||file.StartsWith("CoverMate")||file.StartsWith("SecondaryEntrance")||file.StartsWith("SquadTrade"))module="SquadPositioning";
  if(file.StartsWith("PostCombat"))module="PostCombat";
  if(file.StartsWith("MedicalPosture"))module="MedicalPosture";
  if(file.StartsWith("DoorMotion"))module="DoorMotion";
  if(file.StartsWith("MovementRecovery"))module="MovementRecovery";
  if(file.StartsWith("GrenadeFilter"))module="GrenadeFilter";
  if(file.StartsWith("Sight"))module="Sight";
  bool cleanup=Regex.IsMatch(name,@"^(End|Stop|Release|Dispose|Drop|Clear|Restore)")||kind=="finalizer"||name.Contains("Stopped")||name.EndsWith("Finally");
  if(name is "ClearThrowGuard" or "ClearSafety" or "ClearPath" or "ClearRoute")cleanup=false;
  string stateOwner=state!=null?state.Identifier.Text+(state.Type!.ToString().EndsWith("?")?"?.Owner":".Owner"):"null";
  string identity=bot!=null?(state!=null?$"global::Comet.Client.Plugin.FaultBot({bot.Identifier.Text}, {state.Identifier.Text})":bot.Identifier.Text):state!=null?stateOwner:other!=null?$"global::Comet.Client.Plugin.FaultOwner({other.Identifier.Text})":"null";
  string ret=m.ReturnType.ToString(),fallback=ret=="void"?"return;":ret=="bool"&&kind=="prefix"?"return true;":ret.Contains("Exception")&&pars.Any(p=>p.Identifier.Text=="__exception")?"return __exception;":"return default!;";
  var snapshots=pars.Where(p=>p.Modifiers.Any(SyntaxKind.RefKeyword)).ToArray();
  string saves=string.Join("\n",snapshots.Select(p=>$"var __saved_{p.Identifier}={p.Identifier};"));
  string restore=string.Join("\n",snapshots.Select(p=>$"{p.Identifier}=__saved_{p.Identifier};"));
  string outs=string.Join("\n",pars.Where(p=>p.Modifiers.Any(SyntaxKind.OutKeyword)).Select(p=>$"{p.Identifier}=default!;"));
  var original=m.Body??SyntaxFactory.Block(SyntaxFactory.ParseStatement(ret=="void"?m.ExpressionBody!.Expression+";":"return "+m.ExpressionBody!.Expression+";"));
  var body=new Catches().Visit(original)!.NormalizeWhitespace().ToFullString();
  string wrapped=$$"""
  {
   {{outs}}
   {{saves}}
   var __faultScope = global::Comet.Client.Plugin.EnterFault("{{module}}", "{{name}}", null, {{cleanup.ToString().ToLower()}}, {{telemetry.ToString().ToLower()}});
   try {
    global::Comet.Client.Plugin.BindFault(ref __faultScope, {{identity}});
    {{(state!=null?$"global::Comet.Client.Plugin.BindFaultState(ref __faultScope, {state.Identifier});":"")}}
    if (!global::Comet.Client.Plugin.AllowFault(__faultScope)) { {{restore}} {{outs}} {{fallback}} }
    {{body}}
   } catch (global::System.Exception __boundaryError) {
    if (!global::Comet.Client.Plugin.HandleFault(__faultScope, __boundaryError)) throw;
    {{restore}} {{outs}} {{fallback}}
   } finally { global::Comet.Client.Plugin.LeaveFault(__faultScope); }
  }
  """;
  manifest.Add(new{file,owner,name,module,identity,cleanup,telemetry,callback,kind,fallback});
  return m.WithBody(SyntaxFactory.ParseStatement(wrapped) as BlockSyntax).WithExpressionBody(null).WithSemicolonToken(default);
 }
 sealed class Catches:CSharpSyntaxRewriter
 {
  public override SyntaxNode? VisitCatchClause(CatchClauseSyntax node)
  {
   // Local handlers predate C08 and often permanently disable a feature or immediately retry.
   // With an active boundary, let that boundary own accounting and cleanup instead.
   var visited=(CatchClauseSyntax)base.VisitCatchClause(node)!;
   var condition=node.Filter==null?"global::Comet.Client.Plugin.LocalFaultCatch()":$"global::Comet.Client.Plugin.LocalFaultCatch() && ({node.Filter.FilterExpression})";
   return visited.WithFilter(SyntaxFactory.CatchFilterClause(SyntaxFactory.ParseExpression(condition)));
  }
 }
}
