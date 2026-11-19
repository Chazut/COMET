using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

// Scopes surround the final generated fault boundary; Harmony signatures stay intact.
sealed class PerformanceInstrumentation : CSharpSyntaxRewriter
{
    public string File = "";
    private readonly List<string> modules = new();
    private readonly List<object> methods = new();

    public override SyntaxNode? VisitMethodDeclaration(MethodDeclarationSyntax m)
    {
        string name = m.Identifier.Text;
        // Lifecycle guards run inside every nested fault boundary. Timing each tiny
        // read recursively dominated the observer itself (raid385: 231M scopes).
        // Their cost remains charged to the enclosing action, plus the lifecycle sweep.
        if (
            File.StartsWith("Performance")
            || File == "FaultIsolationAdapter"
            || File is "TacticCounters" or "CombatReport"
            || File.StartsWith("Lifecycle") && name is not ("SampleLifecycles" or "RetireLife")
            || File.StartsWith("RaidCleanup") && name == "GuardRaid"
            || File == "ControlIntegration" && name is "Sleeping" or "WatchControl"
            || m.Modifiers.Any(SyntaxKind.AsyncKeyword)
            || m.ReturnType is RefTypeSyntax
            || name.StartsWith("Patch")
            || name is "Awake" or "ValidateContracts"
            || m.Body == null && m.ExpressionBody == null
            || m.DescendantNodes().OfType<YieldStatementSyntax>().Any()
        )
            return m;
        string body = m.ToString();
        bool telemetry =
            (name.StartsWith("Record") || name.StartsWith("Emit") || name.StartsWith("Configure"))
            && (
                body.Contains("JsonConvert.SerializeObject")
                || body.Contains("Diagnostics.Serialize(")
            );
        bool diagnostic =
            File == "Diagnostics" && (name is "Publish" or "Dispatch" or "Write" || telemetry);
        bool observer =
            File
                is "CombatJournalAdapter"
                    or "ShotJournalAdapter"
                    or "ControlJournalAdapter"
                    or "ControlIntegration"
                    or "TacticCountersAdapter"
                    or "ValidityAdapter"
            || File.StartsWith("Lifecycle")
            || File.StartsWith("RaidCleanup");
        bool evidence =
            File == "ZzapUtilityCapture" && name == "Fill"
            || File == "NativeAdapter"
                && name
                    is "Capture"
                        or "Capabilities"
                        or "GrenadeCapability"
                        or "ContinueGrenadeSearch"
                        or "PrepareRoomGrenade"
            || File == "VisibleAdapter" && name is "Capture" or "SelectCover"
            || File == "MedicalChoiceAdapter" && name is "MedicalFrame" or "MedicalSupplies"
            || File == "LoadoutAdapter" && name == "Read"
            || File == "OpponentAdapter" && name == "ReadOpponent"
            || File == "EnvironmentContextAdapter" && name == "RefreshEnvironment"
            || File == "SightIntegration" && name == "RefreshSight"
            || File == "Diagnostics" && name == "Serialize";
        if (
            !body.Contains("Plugin.EnterFault(")
            && !telemetry
            && !diagnostic
            && !observer
            && !evidence
        )
            return m;
        string module =
            telemetry ? "Diagnostics.BuildEvent." + name
            : File == "Diagnostics" && name == "Dispatch" ? "Diagnostics.Subscribers"
            : File == "Diagnostics" && name == "Write" ? "Diagnostics.Queue"
            : File.Replace("Adapter", "").Replace("Integration", "");
        if (module == "Plugin")
            module = "DecisionCoordinator." + name;
        if (evidence)
            module = "DecisionEvidence." + File.Replace("Adapter", "") + "." + name;
        int id = modules.IndexOf(module);
        if (id < 0)
        {
            id = modules.Count;
            modules.Add(module);
        }
        var original = m.Body
            ?? SyntaxFactory.Block(
                SyntaxFactory.ParseStatement(
                    m.ReturnType.ToString() == "void"
                        ? m.ExpressionBody!.Expression + ";"
                        : "return " + m.ExpressionBody!.Expression + ";"
                )
            );
        var enter = SyntaxFactory.ParseStatement(
            $"using var __cometPerformanceScope = global::Comet.Client.PerformanceCapture.Enter({id});"
        );
        methods.Add(
            new
            {
                file = File,
                method = name,
                module,
            }
        );
        return m.WithBody(original.WithStatements(original.Statements.Insert(0, enter)))
            .WithExpressionBody(null)
            .WithSemicolonToken(default);
    }

    public void Save(string output)
    {
        System.IO.File.WriteAllText(
            Path.Combine(output, "PerformanceCatalog.Generated.cs"),
            $$"""
            namespace Comet.Client;

            internal static class PerformanceCatalog
            {
                internal const int MethodCount = {{methods.Count}};
                internal static readonly string[] Modules =
                {
                    {{string.Join(",\n        ", modules.Select(s => JsonSerializer.Serialize(s)))}}
                };
            }
            """
                + "\n"
        );
        System.IO.File.WriteAllText(
            Path.Combine(output, "performance-manifest.json"),
            JsonSerializer.Serialize(methods, new JsonSerializerOptions { WriteIndented = true })
        );
        Console.WriteLine(
            $"Performance: {methods.Count} synchronous scopes across {modules.Count} modules (inactive until enabled)."
        );
    }
}
