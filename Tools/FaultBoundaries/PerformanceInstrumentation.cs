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
            // Charge shared guards and counter increments to the calling fix.
            || File == "LiquidwarpFixesAdapter" && name is "NativeFixScope" or "CountNativeFix"
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
            || File == "SiegeRoomCatalog" && name is "Refresh" or "RoomAt"
            || File == "SiegeBuilding" && name == "Read"
            || File == "Diagnostics" && name == "Serialize";
        bool hearingPhase = File == "MovementHearingAdapter" && name is
            "PruneMovementHearing" or "SnapshotMovementRecipients" or "DeliverMovementSound" or "FlushMovementSummary";
        bool combatPhase = File == "OwnGrenadeSafetyAdapter" && name is "SampleOwnProjectiles" or "RefreshOwnSafety"
            || File == "CombatStepAdapter" && name is "CombatStepFrame" or "RefreshCombatStep" or "CombatStepSafety"
            || File == "ReloadTimingAdapter" && name is "ReloadFrame" or "ReloadResources" or "SourceReloadNow"
            || File == "GasExposureAdapter" && name == "FilterNativeGas"
            || File == "GrenadeReactionAdapter" && name == "SelectGrenadeEscape";
        bool smoothing = File == "NativeTimingAdapter" && name == "SmoothingElapsed";
        bool nativeSummary = File == "LiquidwarpFixesAdapter" && name == "FlushNativeFixes";
        if (
            !hearingPhase && !combatPhase && !smoothing && !nativeSummary
            && !body.Contains("Plugin.EnterFault(")
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
        if (File == "CombatLeanAdapter" && name is "SelectCombatLean" or "LeanFrame" or "ApplyCombatLeanAnimation" or "GuardCombatLean"
            || File == "SearchAbandonmentAdapter" && name is "CheckSearchedPlace" or "SearchForgetObserved")
            module += "." + name;
        if (module == "Plugin")
            module = "DecisionCoordinator." + name;
        if (evidence)
            module = "DecisionEvidence." + File.Replace("Adapter", "") + "." + name;
        if (combatPhase || File is "MovementHearingAdapter" or "LiquidwarpFixesAdapter" or "NativeTimingAdapter" or "ReloadInventoryAdapter" or "ReloadTimingAdapter" or "OwnGrenadeSafetyAdapter" or "CombatStepAdapter")
            module += "." + (smoothing ? "PoseStep" : name);
        int id = ModuleId(module);
        string scopeId = smoothing
            ? $"owner is PoseClass ? {id} : {ModuleId("NativeTiming.LeanStep")}"
            : id.ToString();
        var original = m.Body
            ?? SyntaxFactory.Block(
                SyntaxFactory.ParseStatement(
                    m.ReturnType.ToString() == "void"
                        ? m.ExpressionBody!.Expression + ";"
                        : "return " + m.ExpressionBody!.Expression + ";"
                )
            );
        var enter = SyntaxFactory.ParseStatement(
            $"using var __cometPerformanceScope = global::Comet.Client.PerformanceCapture.Enter({scopeId});"
        );
        methods.Add(
            new
            {
                file = File,
                method = name,
                module,
                alternateModule = smoothing ? "NativeTiming.LeanStep" : null,
            }
        );
        return m.WithBody(original.WithStatements(original.Statements.Insert(0, enter)))
            .WithExpressionBody(null)
            .WithSemicolonToken(default);
    }

    private int ModuleId(string name)
    {
        int id = modules.IndexOf(name);
        if (id >= 0)
            return id;
        modules.Add(name);
        return modules.Count - 1;
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
