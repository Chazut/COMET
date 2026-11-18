using Comet.Core;
using Comet.Server;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.Json;

// Run one policy suite independently while updating the source-parity baseline.
// The normal invocation still runs every suite and integration check below.
if(args.Length==2 && args[0]=="--suite")
{
    try
    {
        var type=typeof(CometConfig).Assembly.GetType(args[1]) ?? System.Reflection.Assembly.GetExecutingAssembly().GetType(args[1],true)!;
        var method=type.GetMethod("Run",System.Reflection.BindingFlags.Public|System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Static)!;
        var result=method.Invoke(null,method.GetParameters().Select(p => p.HasDefaultValue ? p.DefaultValue : Type.Missing).ToArray());
        if(result is Task<int> task)await task;
    }
    catch(Exception ex){Console.Error.WriteLine(ex.InnerException ?? ex);Environment.ExitCode=1;}
    return;
}

int checks = 0;
checks += EnemyRoleAssessmentTests.Run();
checks += HiddenExecutionTests.Run();
checks += MagazineRecoveryTests.Run();
checks += HotPathAllocationTests.Run();
checks += DiagnosticWarmupTests.Run();
checks += DiagnosticEventReaderTests.Run();
checks += PlanContinuityTests.Run();
checks += await PerformanceTests.Run();
checks += await DiagnosticConfigurationTests.Run();
checks += await QuietLoggingTests.Run();
checks += TacticCountersTests.Run();
checks += ControlJournalTests.Run()+CombatJournalTests.Run();
checks += IntegrationTests.Run();
checks += PresetLibraryTests.Run();
checks += ConfigEditorTests.Run();
checks += EditorNavigationTests.Run();
checks += SecretBrandTests.Run();
checks += SettingHelpTests.Run();
checks += DifficultyPresetTests.Run();
checks += FaultIsolationTests.Run();
checks += CoverScanTests.Run();
checks += DoorMotionTests.Run();
checks += DoorClosingTests.Run();
checks += MovementRecoveryTests.Run();
checks += DoorSpatialTests.Run();
checks += DoorRecoveryTests.Run();
checks += DoorTransitTests.Run();
checks += OutcomeTests.Run();
checks += EncounterTests.Run();
checks += FlashContinuityTests.Run();
checks += ExplosionAwarenessTests.Run();
checks += MedicalChoiceTests.Run();
checks += SurgeryInterruptionTests.Run();
checks += MedicalPostureTests.Run();
checks += PostCombatTests.Run();
checks += ReadyStanceTests.Run();
checks += SquadManeuverTests.Run();
checks += CoverMateTests.Run();
checks += SquadTradeTests.Run();
checks += SquadStormTests.Run();
checks += SquadFireTests.Run();
checks += SquadVoiceTests.Run();
checks += LightDisciplineTests.Run();
checks += TacticalDevicesTests.Run();
checks += RestLightsTests.Run();
checks += FireLaneTests.Run();
checks += SecondaryEntranceTests.Run() + CrossfireTests.Run();
checks += GasExposureTests.Run();
if(args.Contains("--skip-installed-provider"))Console.WriteLine("G08 installed-provider binary audit explicitly skipped; gas policy tests still run.");
else checks += ManimalContractTests.Run();
checks += VisibleDecisionTests.Run();
checks += SightContinuityTests.Run();
checks += DecisionVarietyTests.Run();
checks += TacticalMistakeTests.Run();
checks += FearTests.Run();
checks += ThreatSelectionTests.Run();
checks += LoadoutTests.Run();
checks += OpponentTests.Run();
checks += OpportunityTests.Run();
checks += CoverResponseTests.Run();
checks += PlanProgressTests.Run();
checks += SainHandoffTests.Run();
checks += MovementHearingTests.Run();
checks += QuietApproachTests.Run();
checks += GunfireTests.Run();
checks += FloorHearingTests.Run();
checks += CloseDetectionTests.Run();
checks += ImpactReactionTests.Run();
checks += SilentSearchTests.Run();
checks += SearchAbandonmentTests.Run();
checks += EnvironmentContextTests.Run();
checks += DoorApproachTests.Run();
checks += DoorJumpTests.Run();
checks += DoorRunTests.Run();
checks += DoorStepTests.Run();
checks += DoorFakeTests.Run();
checks += DoorMedicalTests.Run();
checks += DoorClearTests.Run();
checks += DoorSearchClearTests.Run();
checks += DoorAmbushTests.Run();
checks += DoorHoldTests.Run();
checks += DoorSupportTests.Run();
checks += DoorInterruptionTests.Run();
checks += DoorEmergencyTests.Run();
checks += DoorReservationTests.Run();
checks += DoorEntryTests.Run();
checks += CombatStepTests.Run();
checks += FirePauseTests.Run();
checks += CornerPeekTests.Run();
checks += CombatLeanTests.Run();
checks += CornerChaseTests.Run();
checks += CornerPrefireTests.Run();
checks += AngleWatchTests.Run();
checks += HoldAngleTests.Run();
checks += CoverTravelTests.Run();
checks += AdsTravelTests.Run();
checks += RetreatWeaveTests.Run();
checks += TacticalJumpTests.Run();
checks += OutdoorRelocationTests.Run();
checks += CoverFeintTests.Run();
checks += ReloadTimingTests.Run();
checks += PeriodicWorkSlotTests.Run();
checks += PistolSwapTests.Run();
checks += WeaponReturnTests.Run();
checks += QuickReloadTests.Run();
checks += WeaponRangeTests.Run();
checks += WeaponSyncTests.Run();
checks += SuppressionDisciplineTests.Run();
checks += ShotGeometryTests.Run();
checks += RecoilRecoveryTests.Run();
checks += HandsRecoveryTests.Run();
checks += GrenadeJudgmentTests.Run();
checks += GrenadeTrajectoryTests.Run();
checks += GrenadeChoiceTests.Run();
checks += OwnGrenadeSafetyTests.Run();
checks += GrenadeReactionTests.Run();
checks += GrenadeDispersionTests.Run();
checks += GrenadeFilterTests.Run();
void Check(bool ok, string message) { if (!ok) throw new Exception(message); checks++; }
void Throws(Action action, string message) { try { action(); } catch (ArgumentException) { checks++; return; } catch (InvalidOperationException) { checks++; return; } throw new Exception(message); }
Situation Contact() => new() { Target = "enemy-1", Now = 10, HasContact = true, ContactAge = 2, KnownDistance = 12, IsAi = true, IsSupportedBot = true, IsAuthority = true, IsActive = true };
CometConfig Only(params Tactic[] enabled)
{
    var config = new CometConfig();
    foreach (var key in config.Weights.Keys.ToArray()) config.Weights[key] = enabled.Any(t => t.ToString() == key) ? 1 : 0;
    return config;
}

// Eligibility must revoke an existing plan immediately, even during its commitment window.
foreach (var mutation in new Action<Situation>[] {
    s => s.IsAi = false, s => s.IsSupportedBot = false, s => s.IsAuthority = false, s => s.IsActive = false,
    s => s.Ghost = true, s => s.Visible = true, s => s.HasContact = false, s => s.Target = "",
    s => s.ContactAge = 61, s => s.ContactAge = double.NaN, s => s.KnownDistance = double.PositiveInfinity,
    s => s.ContactAge = -1, s => s.Now = double.NaN
})
{
    var engine = new HiddenEnemyDecision(new()); var s = Contact();
    Check(engine.Evaluate(s).Decision.HasValue, "initial plan"); mutation(s);
    var choice = engine.Evaluate(s);
    Check(choice.Decision == null && choice.Changed && engine.Current == null, "eligibility revocation");
}

// zzap caches ranking, while native feasibility is checked on every selection.
foreach (Tactic tactic in Enum.GetValues<Tactic>())
{
    var config=Only(tactic);var engine=new HiddenEnemyDecision(config);var s=Contact();
    if(tactic==Tactic.FallBack)s.Health=.7; // Healthy/full-ammo fallback scores exactly 5 and is not runnable in zzap.
    Check(engine.Evaluate(s).Decision==tactic,"source selectable "+tactic);
    s.Unavailable[tactic]="test-route-blocked";s.Now+=.1;
    var result=engine.Evaluate(s);
    Check(result.Decision==null && result.Candidates.Single(c=>c.Decision==tactic.ToString()).Reason=="test-route-blocked","native feasibility before rescore "+tactic);
    Check(!engine.NeedsEvidence(s),"temporary refusal keeps source ranking "+tactic);
    int draw=result.Mixing!.DrawIndex,opportunity=result.Mixing.Opportunity;
    s.Unavailable.Clear();s.Now+=.1;result=engine.Evaluate(s);
    Check(result.Decision==tactic&&result.Mixing!.DrawIndex==draw&&result.Mixing.Opportunity==opportunity,"capability recovery without new random choice "+tactic);
}
{
    var engine=new HiddenEnemyDecision(Only(Tactic.Hold));var s=Contact();
    Check(engine.Evaluate(s).Decision==Tactic.Hold,"initial hold");
    s.Now+=3.9;
    Check(!engine.NeedsEvidence(s)&&engine.Evaluate(s).Reason=="zzap-ranked-memory","source hold lasts at least four seconds");
    s.Now=17.01;
    Check(engine.NeedsEvidence(s)&&engine.Evaluate(s).Decision==Tactic.Hold,"source rescores by seven seconds without COMET's forced release");
}
{
    // DustAndPellets: grenade ranking wins, arc is initially pending, then ready
    // during the cached ranking. Hold must not mask the now usable grenade.
    var config=Only(Tactic.Grenade,Tactic.Hold);
    config.DecisionVariety.Enabled=false;config.TacticalMistakes.Enabled=false;
    config.Weights[nameof(Tactic.Grenade)]=3;
    config.Weights[nameof(Tactic.Hold)]=.1;
    var engine=new HiddenEnemyDecision(config);var s=Contact();
    s.Unavailable[Tactic.Grenade]="no-ready-throw-trajectory";
    var pending=engine.Evaluate(s);int opportunity=pending.Mixing!.Opportunity;
    var scores=pending.Candidates.Select(c=>c.Score).ToArray();
    Check(pending.Decision==null,"pending grenade yields to native when Hold is below the source score floor");
    s.Now+=.1;s.Unavailable.Clear();
    var ready=engine.Evaluate(s);
    Check(ready.Decision==Tactic.Grenade&&ready.Changed&&ready.Reason=="zzap-capability-refreshed","prepared grenade replaces cached fallback immediately");
    Check(ready.Mixing!.Opportunity==opportunity&&ready.Mixing.DrawIndex==pending.Mixing.DrawIndex&&ready.Candidates.Select(c=>c.Score).SequenceEqual(scores),"arc readiness does not change scores or reroll");
    engine.Fail(Tactic.Grenade,s.Now,"native-throw-refused");s.Unavailable[Tactic.Grenade]="native-throw-refused";
    var fallback=engine.Evaluate(s);
    Check(fallback.Decision==null&&fallback.Mixing!.Selected==""&&fallback.Mixing.Opportunity==opportunity,"native refusal cannot promote a below-floor fallback or reroll its preference");
    s.Unavailable.Clear();s.Now+=.1;
    Check(engine.Evaluate(s).Decision==Tactic.Grenade,"next callback can retry without extra cooldown");
    s.Now=14.01;Check(engine.NeedsEvidence(s),"capability refresh never extends source ranking lifetime");
}
{
    var engine=new HiddenEnemyDecision(Only(Tactic.Grenade));var s=Contact();
    Check(engine.Evaluate(s).Decision==Tactic.Grenade,"prepared grenade selected");
    engine.Fail(Tactic.Grenade,s.Now,"native-refusal");
    Check(engine.Evaluate(s).Decision==Tactic.Grenade,"no COMET resource retry ban");
    s.Target="other";
    Check(engine.Evaluate(s).Decision==Tactic.Grenade,"source can reevaluate another contact immediately");
}
{
    var s=Contact();s.KnownDistance=30;
    Check(new HiddenEnemyDecision(Only(Tactic.Push)).Evaluate(s).Decision==Tactic.Push,"distance affects source utility; native route remains responsible for feasibility");
    s.ContactAge=9;
    Check(new HiddenEnemyDecision(Only(Tactic.Grenade)).Evaluate(s).Decision==Tactic.Grenade,"no added short grenade contact lifetime");
    s.ContactAge=59;
    Check(new HiddenEnemyDecision(Only(Tactic.Search)).Evaluate(s).Decision==Tactic.Search,"source sixty-second known contact window");
}
{
    var s=Contact();s.Health=.2;s.Ammo=.1;s.Fear=new FearTrace{Level=.9};
    var scores=ZzapUtility.Hidden(s,.5,0,false);
    Check(scores[Tactic.FallBack]>scores[Tactic.Push],"source injured empty scared bot favors retreat");
    Check(ZzapUtility.Hidden(s,.5,40,true)[Tactic.Hold]==-100,"source hold ban is a utility penalty, not COMET's no-action veto");
}

// Reproducible long-running state machine: actual production engine, not a reimplementation.
var random = new Random(20261030);
var fuzz = new HiddenEnemyDecision(new());
var twin = new HiddenEnemyDecision(new());
for (int i = 0; i < 20000; i++)
{
    var s = Contact(); s.Now = i * .1; s.ContactAge = random.NextDouble() * 40;
    s.Target = "enemy-" + i / 500; s.Personality = i % 2 == 0 ? "Rat" : "Chad";
    s.Visible = random.Next(20) == 0; s.Ghost = random.Next(100) == 0;
    s.LowAmmo = random.Next(5) == 0; s.Hurt = random.Next(6) == 0; s.Suppressed = random.Next(4) == 0;
    foreach (Tactic t in Enum.GetValues<Tactic>()) if (random.Next(4) == 0) s.Unavailable[t] = "blocked";
    var choice = fuzz.Evaluate(s); var other = twin.Evaluate(s);
    Check(choice.Decision == other.Decision && choice.Reason == other.Reason, "deterministic scenario " + i);
    if (choice.Decision.HasValue)
        Check(choice.Candidates.Single(c => c.Decision == choice.Decision.ToString()).Eligible, "never selects infeasible action " + i);
}
foreach (var mutate in new Action<CometConfig>[] {
    c => c.Schema = 2, c => c.Commitment = 6, c => c.HoldLimit = 99, c => c.ContactLifetime = double.NaN,
    c => c.Weights["Push"] = double.PositiveInfinity, c => c.Weights.Remove("Search"), c => c.Aggression["Rat"] = -1,
    c => c.GrenadeContactLifetime = 100, c => c.PlanLimit = 1
}) { var c = new CometConfig(); mutate(c); Throws(c.Validate, "invalid config accepted"); }

var root = Path.Combine(Path.GetTempPath(), "COMET-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var path = Path.Combine(root, "config.json");
var store = new ConfigStore(path);
var first = store.Read(); Check(first.Revision == 1 && !File.Exists(path), "read defaults without overwriting anything");
var saved = store.Save(first, 1); Check(saved.Revision == 2 && store.Read().Revision == 2, "atomic config save");
first.Weights["Hold"] = 0; Check(store.Read().Weights["Hold"] == 1, "editing cannot mutate saved config");
Throws(() => store.Save(first, 1), "stale editor must not overwrite");
var raidConfig = JsonSerializer.Deserialize<CometConfig>(store.ForGame())!;
var editorConfig = store.Read(); editorConfig.Weights["Hold"] = 2;
store.Save(editorConfig, 2); Check(File.Exists(path + ".bak"), "previous config backup");
Check(raidConfig.Revision == 2 && raidConfig.Weights["Hold"] == 1, "C14 server edits cannot mutate current raid config");
Check(JsonSerializer.Deserialize<CometConfig>(store.ForGame())!.Weights["Hold"] == 2, "C14 next raid receives new server settings");
var before = File.ReadAllText(path); saved.ContactLifetime = -10;
Throws(() => store.Save(saved, 3), "invalid save must fail"); Check(File.ReadAllText(path) == before, "invalid save preserves file");
File.WriteAllText(path, "[]");
try { store.Read(); throw new Exception("corrupt configuration was silently replaced"); } catch (Exception ex) when (ex is JsonException or InvalidDataException) { checks++; }
Check(File.ReadAllText(path) == "[]", "corrupt config preserved for recovery");

// Private runtime hooks checked against the real official DLL metadata without initializing Unity.
var sain = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../references/sain-4.5.1/SAIN.dll"));
using var pe = new PEReader(File.OpenRead(sain)); var metadata = pe.GetMetadataReader();
foreach (var contract in new[] { ("EnemyEvents", "EnemyHeard", 3), ("EnemyEvents", "enemyHit", 3), ("WeaponInfo", "WeaponModified", 1), ("GrenadeThrowDecider", "CheckCanThrow", 1), ("GrenadeThrowDecider", "CanThrowAtEnemy", 2),
    ("GrenadeThrowDecider", "FindThrowTarget", 1), ("EnemyDecisionClass", "GetDecision", 3), ("BotDecisionManager", "SetDecisions", 4),
    ("BotDecisionManager", "ContinueMoveToCover", 0), ("SAINCoverClass", "FindCoverPoint", 0),
    ("SAINFriendlyFireClass", "ManualUpdate", 0), ("SAINMoverClass", "WantsPatrolStance", 0), ("SAINMoverClass", "ManualUpdate", 0), ("BotDecisionManager", "ManualUpdate", 0),
    ("Squad", "memberWasKilled", 4), ("PlayerMovementController", "SetTargetMoveDirection", 5), ("PlayerComponent", "OnMakingShot", 2),
    ("SAINEnemyController", "SelectEnemy", 0), ("SAINEnemyController", "ChooseEnemy", 0),
    ("DogFightDecisionClass", "ShallDogfightEnemy", 1), ("EnemyList", "AddEnemy", 1),
    ("SAINSteeringClass", "SteerByPriority", 3), ("SAINSteeringClass", "LookToEnemy", 1),
    ("ManualShootClass", "TryShoot", 4), ("RushEnemyAction", "updateRushBehavior", 0), ("BotAction", "get_Bot", 0) })
{
    var type = metadata.TypeDefinitions.Select(h => metadata.GetTypeDefinition(h)).Single(t => metadata.GetString(t.Name) == contract.Item1);
    var method = type.GetMethods().Select(h => metadata.GetMethodDefinition(h)).Single(m => metadata.GetString(m.Name) == contract.Item2);
    var parameters = method.GetParameters().Select(h => metadata.GetParameter(h)).Count(p => p.SequenceNumber > 0);
    Check(parameters == contract.Item3, "official SAIN hook contract: " + contract);
    if(contract.Item2=="TryShoot") {
        var names=method.GetParameters().Select(h=>metadata.GetParameter(h)).Where(p=>p.SequenceNumber>0).Select(p=>metadata.GetString(p.Name)).ToArray();
        Check(names.Contains("targetPos"),"S08 scoped manual-fire target argument matches original SAIN");
    }
    if(contract.Item2=="SetTargetMoveDirection") {
        var names=method.GetParameters().Select(h=>metadata.GetParameter(h)).Where(p=>p.SequenceNumber>0).Select(p=>metadata.GetString(p.Name)).ToArray();
        Check(names.SequenceEqual(new[]{"direction","finalMoveDestination","playerComp","START_SLOW_DIST","STOP_SPRINT_DIST"}),"S07 Harmony named arguments match official SAIN");
    }
}
using (var gamePe = new PEReader(File.OpenRead(@"C:\Games\SPT-4.1\EscapeFromTarkov_Data\Managed\Assembly-CSharp.dll")))
{
    var gameMetadata = gamePe.GetMetadataReader();
    var shootType=gameMetadata.TypeDefinitions.Select(h=>gameMetadata.GetTypeDefinition(h)).Single(t=>gameMetadata.GetString(t.Name)=="ShootData");
    foreach(var name in new[]{"Shoot","EndShoot","get_Shooting"}) {
        var method=shootType.GetMethods().Select(h=>gameMetadata.GetMethodDefinition(h)).Single(m=>gameMetadata.GetString(m.Name)==name);
        Check(!method.GetParameters().Select(h=>gameMetadata.GetParameter(h)).Any(p=>p.SequenceNumber>0),"S08 native parameterless hook "+name);
        var signature=gameMetadata.GetBlobReader(method.Signature);signature.ReadSignatureHeader();signature.ReadCompressedInteger();
        Check(signature.ReadSignatureTypeCode()==(name=="EndShoot"?SignatureTypeCode.Void:SignatureTypeCode.Boolean),"S08 native return type "+name);
    }
    Check(shootType.GetFields().Select(h=>gameMetadata.GetFieldDefinition(h)).Any(f=>gameMetadata.GetString(f.Name)=="_owner"),"S08 native owner contract");
    var movementType = gameMetadata.TypeDefinitions.Select(h => gameMetadata.GetTypeDefinition(h)).Single(t => gameMetadata.GetString(t.Name) == "MovementContext");
    var patrolField = movementType.GetFields().Select(h => gameMetadata.GetFieldDefinition(h)).Single(f => gameMetadata.GetString(f.Name) == "_isInPatrol");
    var patrolSignature = gameMetadata.GetBlobReader(patrolField.Signature); patrolSignature.ReadSignatureHeader();
    Check(patrolSignature.ReadSignatureTypeCode() == SignatureTypeCode.Boolean, "H05 observed native patrol field is boolean");
    var playerType = gameMetadata.TypeDefinitions.Select(h => gameMetadata.GetTypeDefinition(h))
        .Single(t => gameMetadata.GetString(t.Namespace) == "EFT" && gameMetadata.GetString(t.Name) == "Player");
    var inventoryHook = playerType.GetMethods().Select(h => gameMetadata.GetMethodDefinition(h))
        .Single(m => gameMetadata.GetString(m.Name) == "OnItemAddedOrRemoved");
    Check((inventoryHook.Attributes & System.Reflection.MethodAttributes.Static) == 0,
        "A08 inventory notification hook exists unambiguously on the player instance");
}
int delivered = 0, errors = 0;
Action<string> broken = _ => throw new Exception("broken optional subscriber");
Action<string> working = _ => delivered++;
Comet.Api.Decisions.Published += broken;
Comet.Api.Decisions.Published += working;
Comet.Api.Decisions.Publish("{}", _ => errors++);
Check(delivered == 1 && errors == 1, "bad optional subscriber cannot break another subscriber or the chooser");
Comet.Api.Decisions.Published -= broken;
Comet.Api.Decisions.Published -= working;
Comet.Api.Decisions.Set("pmc", "COMET:Push");
Check(Comet.Api.Decisions.GetCurrentDecision("pmc") == "COMET:Push", "accepted telemetry label");
Comet.Api.Decisions.Clear();
Check(Comet.Api.Decisions.GetCurrentDecision("pmc") == "", "raid cleanup removes stale telemetry");
var diagnostic = new Comet.Client.Diagnostics(root, "test", _ => errors++);
for (int i = 0; i < 100; i++) diagnostic.Write(JsonSerializer.Serialize(new { sequence = i, stage = "selected" }));
diagnostic.Dispose(); await diagnostic.Completion;
var lines = File.ReadAllLines(Directory.GetFiles(root, "*.jsonl").Single());
Check(lines.Length == 101 && lines[^1].Contains("diagnostics-end"), "async diagnostic file drains and closes with coverage footer");
Check(lines.Take(100).Select(l => JsonDocument.Parse(l).RootElement.GetProperty("sequence").GetInt32()).SequenceEqual(Enumerable.Range(0, 100)), "diagnostic sequence preserved");
Console.WriteLine($"PASS: {checks} assertions, 50,000 deterministic A01/A02/A03 state transitions, config persistence, telemetry and official SAIN hook contracts. No Unity raid simulated. Temp evidence: {root}");
