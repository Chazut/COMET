using Mono.Cecil;
using Mono.Cecil.Cil;

var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
var configuration = Directory.GetParent(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar))!.Name;
using var comet = ModuleDefinition.ReadModule(Path.Combine(root, "Client/bin", configuration, "netstandard2.1/COMET.dll"));
using var sain = ModuleDefinition.ReadModule(Path.Combine(root, "references/sain-4.5.1/SAIN.dll"));
var plugin = comet.Types.Single(t => t.FullName == "Comet.Client.Plugin");
var prefix = plugin.Methods.Single(m => m.Name == "FindCover");
var native = sain.Types.Single(t => t.Name == "SAINCoverClass").Methods.Single(m => m.Name == "FindCoverPoint");

int checks = 0;
void Check(bool ok, string why)
{
    if (!ok) throw new InvalidOperationException(why);
    checks++;
}

var calls = prefix.Body.Instructions.Where(i => i.Operand is MethodReference)
    .Select(i => (MethodReference)i.Operand).ToArray();
Check(calls.Any(m => m.Name == "AllowsGoal"), "The cover hook must check the hostile contact.");
Check(calls.Count(m => m.DeclaringType.FullName == plugin.FullName && m.Name.Contains("Cover")) == 1
    && calls.Any(m => m.Name == "FindRelocationCover"), "Unexpected cover selection calls: " + string.Join(", ", calls.Select(m => m.FullName)));
Check(calls.All(m => m.Name != "GoToCoverPoint" && m.Name != "SetDestination"),
    "The fallback hook must leave movement to SAIN.");

var il = native.Body.Instructions;
Instruction Called(string name) => il.First(i => i.Operand is MethodReference m && m.Name == name);
var filter = Called("GetGoodCover");
var sort = Called("Sort");
var move = Called("GoToCoverPoint");
Check(filter.Offset < sort.Offset && sort.Offset < move.Offset,
    "SAIN must filter and sort its cached candidates before requesting movement.");
Check(il.TakeWhile(i => i != filter).Any(i => i.OpCode.Code == Code.Ldnull),
    "An empty native cache may yield no cover.");
Check(il.Any(i => i.Offset > move.Offset && i.OpCode.FlowControl == FlowControl.Cond_Branch
    && i.Operand is Instruction retry && retry.Offset > sort.Offset && retry.Offset < move.Offset),
    "SAIN must be able to try another candidate after a movement refusal.");
Console.WriteLine($"Cover fallback: {checks} contracts checked against the built COMET DLL and official SAIN.");
