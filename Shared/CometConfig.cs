using System;
using System.Collections.Generic;

namespace Comet.Core;

public enum Tactic
{
    Push,
    Grenade,
    Hold,
    Flank,
    Search,
    FallBack,
}

public sealed class MovementHearingConfig
{
    public bool Enabled { get; set; } = true;
    public bool Footsteps { get; set; } = true;
    public bool GaitBasedRange { get; set; } = true;
    public double QuietRangeMeters { get; set; } = 5;
    public double WalkRangeMeters { get; set; } = 20;
    public double SprintRangeMeters { get; set; } = 30;
    public bool JumpsAndLandings { get; set; } = true;
    public double RangeMultiplier { get; set; } = 1;
    public double MinimumInterval { get; set; } = .2;

    public void Validate()
    {
        CometConfig.Range(RangeMultiplier, .25, 2, nameof(RangeMultiplier));
        CometConfig.Range(QuietRangeMeters, .1, 100, nameof(QuietRangeMeters));
        CometConfig.Range(WalkRangeMeters, .1, 100, nameof(WalkRangeMeters));
        CometConfig.Range(SprintRangeMeters, .1, 100, nameof(SprintRangeMeters));
        if (QuietRangeMeters >= WalkRangeMeters || WalkRangeMeters >= SprintRangeMeters)
            throw new ArgumentException("Quiet steps must carry less far than walking, and walking less far than sprinting.");
        CometConfig.Range(MinimumInterval, .05, 1, nameof(MinimumInterval));
    }
}

public sealed class CometConfig
{
    public const string SourceBaseline = "zzap-d8655293-3";
    public const double DefaultAmbushRadius = 45;
    public const bool DefaultQuietLogging = true;
    public string BehaviorBaseline { get; set; } = SourceBaseline;
    public bool QuietLogging { get; set; } = DefaultQuietLogging;
    public bool EssentialReplayData { get; set; } = true;
    public bool ExtendedReplayData { get; set; }
    // Legacy preset fields remain readable. Replay levels own collection now.
    public bool ExtraRaidReviewInfo { get; set; } = true;
    // Per-raid client state. The editor and exported presets keep the user's preference.
    internal bool RaidReportReceiverAvailable = true;
    internal static bool HasRaidReportReceiver(Func<string, bool> isLoaded) =>
        isLoaded("ekky.raidreview") || isLoaded("com.fums.satellite");

    public bool RecordExtendedReplay() => RaidReportReceiverAvailable && ExtendedReplayData;
    public bool RecordEssentialReplay() => RaidReportReceiverAvailable && (EssentialReplayData || ExtendedReplayData);
    public bool RecordCombatJournal() => RecordExtendedReplay();
    public bool RecordWeaponJournal() => RecordExtendedReplay();
    public bool RecordControlJournal() => RecordExtendedReplay();
    public bool RecordTacticCounters() => RecordExtendedReplay();

    public PerformanceConfig Performance { get; set; } = new();
    public CombatJournalConfig CombatJournal { get; set; } = new();
    public ControlJournalConfig ControlJournal { get; set; } = new();
    public WeaponJournalConfig WeaponJournal { get; set; } = new();
    public TacticCountersConfig TacticCounters { get; set; } = new();
    public PresetIdentity? PresetSource { get; set; }
    public string DifficultyPreset { get; set; } = "Hard";
    public FaultIsolationConfig FaultIsolation { get; set; } = new();
    public CoverScanConfig CoverScan { get; set; } = new();
    public MovementRecoveryConfig MovementRecovery { get; set; } = new();
    public DoorClosingConfig DoorClosing { get; set; } = new();
    public DoorMotionConfig DoorMotion { get; set; } = new();
    public DoorSpatialConfig DoorSpatial { get; set; } = new();
    public DoorRecoveryConfig DoorRecovery { get; set; } = new();
    public DoorTransitConfig DoorTransit { get; set; } = new();
    public OutcomeConfig Outcomes { get; set; } = new();
    public EncounterConfig Encounter { get; set; } = new();
    public SecondaryEntranceConfig SecondaryEntrance { get; set; } = new();
    public SquadManeuverConfig SquadManeuver { get; set; } = new();
    public RestLightsConfig RestLights { get; set; } = new();
    public TacticalDevicesConfig TacticalDevices { get; set; } = new();
    public LightDisciplineConfig LightDiscipline { get; set; } = new();
    public SquadVoiceConfig SquadVoice { get; set; } = new();
    public SquadFireConfig SquadFire { get; set; } = new();
    public FireLaneConfig FireLane { get; set; } = new();
    public SquadStormConfig SquadStorm { get; set; } = new();
    public SquadTradeConfig SquadTrade { get; set; } = new();
    public CoverMateConfig CoverMate { get; set; } = new();
    public CrossfireConfig Crossfire { get; set; } = new();
    public ReadyStanceConfig ReadyStance { get; set; } = new();
    public PostCombatConfig PostCombat { get; set; } = new();
    public MedicalPostureConfig MedicalPosture { get; set; } = new();
    public SurgeryInterruptionConfig SurgeryInterruption { get; set; } = new();
    public MedicalChoiceConfig MedicalChoice { get; set; } = new();
    public ExplosionAwarenessConfig ExplosionAwareness { get; set; } = new();
    public FlashContinuityConfig FlashContinuity { get; set; } = new();
    public GasExposureConfig GasExposure { get; set; } = new();
    public GrenadeFilterConfig GrenadeFilter { get; set; } = new();
    public GrenadeReactionConfig GrenadeReaction { get; set; } = new();
    public GrenadeDispersionConfig GrenadeDispersion { get; set; } = new();
    public OwnGrenadeSafetyConfig OwnGrenadeSafety { get; set; } = new();
    public GrenadeChoiceConfig GrenadeChoice { get; set; } = new();
    public GrenadeTrajectoryConfig GrenadeTrajectory { get; set; } = new();
    public GrenadeJudgmentConfig GrenadeJudgment { get; set; } = new();
    public HandsRecoveryConfig HandsRecovery { get; set; } = new();
    public RecoilRecoveryConfig RecoilRecovery { get; set; } = new();
    public ShotGeometryConfig ShotGeometry { get; set; } = new();
    public SuppressionDisciplineConfig SuppressionDiscipline { get; set; } = new();
    public WeaponSyncConfig WeaponSync { get; set; } = new();
    public WeaponRangeConfig WeaponRange { get; set; } = new();
    public QuickReloadConfig QuickReload { get; set; } = new();
    public WeaponReturnConfig WeaponReturn { get; set; } = new();
    public PistolSwapConfig PistolSwap { get; set; } = new();
    public ReloadTimingConfig ReloadTiming { get; set; } = new();
    public CoverTravelConfig CoverTravel { get; set; } = new();
    public AdsTravelConfig AdsTravel { get; set; } = new();
    public RetreatWeaveConfig RetreatWeave { get; set; } = new();
    public CoverFeintConfig CoverFeint { get; set; } = new();
    public OutdoorRelocationConfig OutdoorRelocation { get; set; } = new();
    public TacticalJumpConfig TacticalJump { get; set; } = new();
    public HoldAngleConfig HoldAngle { get; set; } = new();
    public int Schema { get; set; } = 1;
    public long Revision { get; set; } = 1;
    public bool Enabled { get; set; } = true;
    public bool EnablePmcs { get; set; } = true;
    public bool EnableAiPlayerScavs { get; set; } = true;
    public bool LightBasedDetection { get; set; } = true;
    public MovementHearingConfig MovementHearing { get; set; } = new MovementHearingConfig();
    public QuietApproachConfig QuietApproach { get; set; } = new QuietApproachConfig();
    public GunfireConfig Gunfire { get; set; } = new GunfireConfig();
    public FloorHearingConfig FloorHearing { get; set; } = new FloorHearingConfig();
    public CloseDetectionConfig CloseDetection { get; set; } = new CloseDetectionConfig();
    public SearchAbandonmentConfig SearchAbandonment { get; set; } = new SearchAbandonmentConfig();
    public EnvironmentContextConfig EnvironmentContext { get; set; } =
        new EnvironmentContextConfig();
    public DoorJumpPeekConfig DoorJumpPeek { get; set; } = new DoorJumpPeekConfig();
    public DoorRunByConfig DoorRunBy { get; set; } = new DoorRunByConfig();
    public DoorFakeGrenadeConfig DoorFakeGrenade { get; set; } = new();
    public DoorFakeMedicalConfig DoorFakeMedical { get; set; } = new();
    public DoorRoomClearConfig DoorRoomClear { get; set; } = new();
    public DoorSearchClearConfig DoorSearchClear { get; set; } = new();
    public DoorSupportConfig DoorSupport { get; set; } = new();
    public DoorInterruptionConfig DoorInterruption { get; set; } = new();
    public DoorEntryConfig DoorEntry { get; set; } = new();
    public CombatStepConfig CombatStep { get; set; } = new();
    public FirePauseConfig FirePause { get; set; } = new();
    public CornerPeekConfig CornerPeek { get; set; } = new();
    public CombatLeanConfig CombatLean { get; set; } = new();
    public AngleWatchConfig AngleWatch { get; set; } = new();
    public CornerPrefireConfig CornerPrefire { get; set; } = new();
    public CornerChaseConfig CornerChase { get; set; } = new();
    public DoorReservationConfig DoorReservation { get; set; } = new();
    public DoorEmergencyConfig DoorEmergency { get; set; } = new();
    public DoorAmbushConfig DoorAmbush { get; set; } = new();
    public DoorStepPeekConfig DoorStepPeek { get; set; } = new DoorStepPeekConfig();
    public DoorApproachConfig DoorApproach { get; set; } = new DoorApproachConfig();
    public SilentSearchConfig SilentSearch { get; set; } = new SilentSearchConfig();
    public ImpactReactionConfig ImpactReaction { get; set; } = new ImpactReactionConfig();
    public VisibleCombatConfig VisibleCombat { get; set; } = new VisibleCombatConfig();
    public SightContinuityConfig SightContinuity { get; set; } = new SightContinuityConfig();
    public DecisionVarietyConfig DecisionVariety { get; set; } = new DecisionVarietyConfig();
    public TacticalMistakesConfig TacticalMistakes { get; set; } = new TacticalMistakesConfig();
    public FearConfig Fear { get; set; } = new FearConfig();
    public OpponentConfig Opponent { get; set; } = new OpponentConfig();
    public OpportunityConfig Opportunity { get; set; } = new OpportunityConfig();
    public CoverResponseConfig CoverResponse { get; set; } = new CoverResponseConfig();
    public ProgressConfig Progress { get; set; } = new ProgressConfig();
    public HandoffConfig Handoff { get; set; } = new HandoffConfig();
    public LoadoutConfig Loadout { get; set; } = new LoadoutConfig();
    public ThreatSelectionConfig ThreatSelection { get; set; } = new ThreatSelectionConfig();
    public double ContactLifetime { get; set; } = 60;
    public double Commitment { get; set; } = 2;
    public double SwitchMargin { get; set; } = 15;
    public double HoldLimit { get; set; } = 5;
    public double AmbushRadius { get; set; } = DefaultAmbushRadius;
    public double PlanLimit { get; set; } = 12;
    public double RetryDelay { get; set; } = 5;
    public double PushMaxDistance { get; set; } = 25;
    public double GrenadeContactLifetime { get; set; } = GrenadeJudgmentConfig.DefaultMaxInfoAge;
    public Dictionary<string, double> Weights { get; set; } =
        new Dictionary<string, double>
        {
            ["Push"] = 1,
            ["Grenade"] = 1,
            ["Hold"] = 1,
            ["Flank"] = 1,
            ["Search"] = 1,
            ["FallBack"] = 1,
        };
    public Dictionary<string, double> Aggression { get; set; } =
        new Dictionary<string, double>
        {
            ["Normal"] = .5,
            ["Chad"] = .8,
            ["GigaChad"] = 1,
            ["Wreckless"] = 1,
            ["Rat"] = .2,
            ["SnappingTurtle"] = .3,
            ["Coward"] = 0,
            ["Timmy"] = .4,
        };

    public void Validate()
    {
        if (Performance == null)
            Performance = new();
        Performance.Validate();
        if (CombatJournal == null)
            CombatJournal = new();
        CombatJournal.Validate();
        if (ControlJournal == null)
            ControlJournal = new();
        ControlJournal.Validate();
        if (WeaponJournal == null)
            WeaponJournal = new();
        WeaponJournal.Validate();
        if (TacticCounters == null)
            TacticCounters = new();
        TacticCounters.Validate();
        if (!DifficultyPresets.IsKnown(DifficultyPreset))
            throw new ArgumentException("Unknown COMET difficulty preset.");
        PresetSource?.Validate();
        if (MedicalChoice == null)
            throw new ArgumentException("Missing medical choice settings.");
        MedicalChoice.Validate();
        if (SurgeryInterruption == null)
            throw new ArgumentException("Missing surgery interruption settings.");
        SurgeryInterruption.Validate();
        if (PostCombat == null)
            throw new ArgumentException("Missing post-combat settings.");
        PostCombat.Validate();
        if (ReadyStance == null)
            throw new ArgumentException("Missing ready stance settings.");
        ReadyStance.Validate();
        if (Crossfire == null)
            throw new ArgumentException("Missing crossfire settings.");
        Crossfire.Validate();
        if (CoverMate == null)
            throw new ArgumentException("Missing cover mate settings.");
        CoverMate.Validate();
        if (SquadTrade == null)
            throw new ArgumentException("Missing squad trade settings.");
        SquadTrade.Validate();
        if (SquadStorm == null)
            throw new ArgumentException("Missing squad storm settings.");
        SquadStorm.Validate();
        if (FaultIsolation == null)
            FaultIsolation = new();
        FaultIsolation.Validate();
        if (CoverScan == null)
            CoverScan = new();
        CoverScan.Validate();
        if (MovementRecovery == null)
            MovementRecovery = new();
        MovementRecovery.Validate();
        if (DoorClosing == null)
            DoorClosing = new();
        DoorClosing.Validate();
        if (DoorMotion == null)
            DoorMotion = new();
        DoorMotion.Validate();
        if (DoorSpatial == null)
            DoorSpatial = new();
        DoorSpatial.Validate();
        if (DoorRecovery == null)
            DoorRecovery = new();
        DoorRecovery.Validate();
        if (DoorTransit == null)
            DoorTransit = new();
        DoorTransit.Validate();
        if (Outcomes == null)
            Outcomes = new();
        Outcomes.Validate();
        if (Encounter == null)
            Encounter = new();
        Encounter.Validate();
        if (RestLights == null)
            RestLights = new();
        RestLights.Validate();
        if (TacticalDevices == null)
            TacticalDevices = new();
        TacticalDevices.Validate();
        if (LightDiscipline == null)
            LightDiscipline = new();
        LightDiscipline.Validate();
        if (SquadVoice == null)
            SquadVoice = new();
        SquadVoice.Validate();
        if (SquadFire == null)
            SquadFire = new();
        SquadFire.Validate();
        if (FireLane == null)
            FireLane = new();
        FireLane.Validate();
        if (SquadManeuver == null)
            throw new ArgumentException("Missing squad maneuver settings.");
        SquadManeuver.Validate();
        if (SecondaryEntrance == null)
            throw new ArgumentException("Missing secondary entrance settings.");
        SecondaryEntrance.Validate();
        if (MedicalPosture == null)
            throw new ArgumentException("Missing medical posture settings.");
        MedicalPosture.Validate();
        if (ExplosionAwareness == null)
            throw new ArgumentException("Missing explosion awareness settings.");
        ExplosionAwareness.Validate();
        if (HandsRecovery == null)
            throw new ArgumentException("Missing hands recovery settings.");
        HandsRecovery.Validate();
        if (RecoilRecovery == null)
            throw new ArgumentException("Missing recoil recovery settings.");
        RecoilRecovery.Validate();
        if (ShotGeometry == null)
            throw new ArgumentException("Missing shot geometry settings.");
        ShotGeometry.Validate();
        if (SuppressionDiscipline == null)
            throw new ArgumentException("Missing suppression discipline settings.");
        SuppressionDiscipline.Validate();
        if (QuickReload == null)
            throw new ArgumentException("Missing quick reload settings.");
        QuickReload.Validate();
        if (WeaponRange == null)
            throw new ArgumentException("Missing weapon range settings.");
        WeaponRange.Validate();
        if (WeaponSync == null)
            throw new ArgumentException("Missing weapon sync settings.");
        WeaponSync.Validate();
        if (WeaponReturn == null)
            throw new ArgumentException("Missing weapon return settings.");
        WeaponReturn.Validate();
        if (PistolSwap == null)
            throw new ArgumentException("Missing pistol swap settings.");
        PistolSwap.Validate();
        if (ReloadTiming == null)
            throw new ArgumentException("Missing reload timing settings.");
        ReloadTiming.Validate();
        if (CoverFeint == null)
            throw new ArgumentException("Missing cover feint settings.");
        CoverFeint.Validate();
        if (OutdoorRelocation == null)
            throw new ArgumentException("Missing outdoor relocation settings.");
        OutdoorRelocation.Validate();
        if (HoldAngle == null)
            throw new ArgumentException("Missing hold angle settings.");
        HoldAngle.Validate();
        if (CoverTravel == null)
            throw new ArgumentException("Missing cover travel settings.");
        CoverTravel.Validate();
        if (AdsTravel == null)
            throw new ArgumentException("Missing ADS travel settings.");
        AdsTravel.Validate();
        if (RetreatWeave == null)
            throw new ArgumentException("Missing retreat weave settings.");
        RetreatWeave.Validate();
        if (TacticalJump == null)
            throw new ArgumentException("Missing tactical jump settings.");
        TacticalJump.Validate();
        if (AngleWatch == null)
            throw new ArgumentException("Missing angle watch settings.");
        AngleWatch.Validate();
        if (Schema != 1 || Revision < 1)
            throw new ArgumentException("Unsupported config schema or revision.");
        if (MovementHearing == null)
            throw new ArgumentException("Missing movement hearing settings.");
        MovementHearing.Validate();
        if (QuietApproach == null)
            throw new ArgumentException("Missing quiet approach settings.");
        QuietApproach.Validate();
        if (Gunfire == null)
            throw new ArgumentException("Missing gunfire reaction settings.");
        Gunfire.Validate();
        if (FloorHearing == null)
            throw new ArgumentException("Missing floor hearing settings.");
        FloorHearing.Validate();
        if (CloseDetection == null)
            throw new ArgumentException("Missing close detection settings.");
        CloseDetection.Validate();
        if (ImpactReaction == null)
            throw new ArgumentException("Missing impact reaction settings.");
        ImpactReaction.Validate();
        if (SilentSearch == null)
            throw new ArgumentException("Missing silent search settings.");
        SilentSearch.Validate();
        if (SearchAbandonment == null)
            throw new ArgumentException("Missing search abandonment settings.");
        SearchAbandonment.Validate();
        if (EnvironmentContext == null)
            throw new ArgumentException("Missing environment context settings.");
        EnvironmentContext.Validate();
        if (DoorApproach == null)
            throw new ArgumentException("Missing door approach settings.");
        DoorApproach.Validate();
        if (DoorJumpPeek == null)
            throw new ArgumentException("Missing door jump peek settings.");
        DoorJumpPeek.Validate();
        if (DoorRunBy == null)
            throw new ArgumentException("Missing doorway run-by settings.");
        DoorRunBy.Validate();
        if (DoorStepPeek == null)
            throw new ArgumentException("DoorStepPeek is required");
        DoorStepPeek.Validate();
        if (DoorFakeGrenade == null)
            throw new ArgumentException("DoorFakeGrenade is required");
        DoorFakeGrenade.Validate();
        if (DoorFakeMedical == null)
            throw new ArgumentException("DoorFakeMedical is required");
        DoorFakeMedical.Validate();
        if (DoorRoomClear == null)
            throw new ArgumentException("DoorRoomClear is required");
        DoorRoomClear.Validate();
        if (DoorSearchClear == null)
            throw new ArgumentException("DoorSearchClear is required");
        DoorSearchClear.Validate(DoorRoomClear.ContactLifetime);
        if (DoorAmbush == null)
            throw new ArgumentException("DoorAmbush is required");
        DoorAmbush.Validate();
        if (DoorSupport == null)
            throw new ArgumentException("Missing door support settings.");
        DoorSupport.Validate();
        if (DoorInterruption == null)
            throw new ArgumentException("Missing door interruption settings.");
        DoorInterruption.Validate();
        if (DoorEmergency == null)
            throw new ArgumentException("Missing door emergency settings.");
        DoorEmergency.Validate();
        if (DoorReservation == null)
            throw new ArgumentException("Missing door reservation settings.");
        DoorReservation.Validate();
        if (DoorEntry == null)
            throw new ArgumentException("Missing door ram settings.");
        DoorEntry.Validate();
        if (CombatStep == null)
            throw new ArgumentException("Missing combat movement settings.");
        CombatStep.Validate();
        if (FirePause == null)
            throw new ArgumentException("Missing firing pause settings.");
        FirePause.Validate();
        if (CornerPeek == null)
            throw new ArgumentException("Missing corner peek settings.");
        CornerPeek.Validate();
        if (CombatLean == null)
            throw new ArgumentException("Missing combat lean settings.");
        CombatLean.Validate();
        if (CornerChase == null)
            throw new ArgumentException("Missing corner chase settings.");
        CornerChase.Validate();
        if (CornerPrefire == null)
            throw new ArgumentException("Missing corner prefire settings.");
        CornerPrefire.Validate();
        if (VisibleCombat == null)
            throw new ArgumentException("Missing visible combat settings.");
        VisibleCombat.Validate();
        if (SightContinuity == null)
            throw new ArgumentException("Missing sight continuity settings.");
        SightContinuity.Validate();
        if (DecisionVariety == null)
            throw new ArgumentException("Missing decision variety settings.");
        DecisionVariety.Validate();
        if (TacticalMistakes == null)
            throw new ArgumentException("Missing tactical mistake settings.");
        TacticalMistakes.Validate();
        if (Fear == null)
            throw new ArgumentException("Missing fear settings.");
        Fear.Validate();
        if (ThreatSelection == null)
            throw new ArgumentException("Missing threat selection settings.");
        ThreatSelection.Validate();
        if (Loadout == null)
            throw new ArgumentException("Missing loadout settings.");
        Loadout.Validate();
        if (Opponent == null)
            throw new ArgumentException("Missing opponent assessment settings.");
        Opponent.Validate();
        if (Opportunity == null)
            throw new ArgumentException("Missing opportunity settings.");
        Opportunity.Validate();
        if (CoverResponse == null)
            throw new ArgumentException("Missing cover response settings.");
        CoverResponse.Validate();
        if (Progress == null)
            throw new ArgumentException("Missing plan progress settings.");
        Progress.Validate();
        if (Handoff == null)
            throw new ArgumentException("Missing SAIN handoff settings.");
        Handoff.Validate();
        Range(ContactLifetime, 3, 120, nameof(ContactLifetime));
        Range(Commitment, .25, 10, nameof(Commitment));
        Range(SwitchMargin, 0, 100, nameof(SwitchMargin));
        Range(HoldLimit, Commitment, 30, nameof(HoldLimit));
        Range(AmbushRadius, 10, 100, nameof(AmbushRadius));
        Range(PlanLimit, HoldLimit, 60, nameof(PlanLimit));
        Range(RetryDelay, 1, 30, nameof(RetryDelay));
        Range(PushMaxDistance, 3, 50, nameof(PushMaxDistance));
        Range(GrenadeContactLifetime, 1, ContactLifetime, nameof(GrenadeContactLifetime));
        if (GrenadeJudgment == null)
            throw new ArgumentException("Missing grenade judgment settings.");
        GrenadeJudgment.Validate(GrenadeContactLifetime);
        if (GrenadeTrajectory == null)
            throw new ArgumentException("Missing grenade trajectory settings.");
        GrenadeTrajectory.Validate();
        if (GrenadeChoice == null)
            throw new ArgumentException("Missing grenade choice settings.");
        GrenadeChoice.Validate();
        if (OwnGrenadeSafety == null)
            throw new ArgumentException("Missing own grenade safety settings.");
        OwnGrenadeSafety.Validate();
        if (GrenadeReaction == null)
            throw new ArgumentException("Missing grenade reaction settings.");
        GrenadeReaction.Validate();
        if (GrenadeDispersion == null)
            throw new ArgumentException("Missing grenade dispersion settings.");
        GrenadeDispersion.Validate();
        if (FlashContinuity == null)
            throw new ArgumentException("Missing flash continuity settings.");
        if (GasExposure == null)
            throw new ArgumentException("Missing gas exposure settings.");
        GasExposure.Validate();
        if (GrenadeFilter == null)
            throw new ArgumentException("Missing offensive grenade settings.");
        if (Weights == null || Aggression == null)
            throw new ArgumentException("Missing tactic or personality settings.");
        if (Weights.Count != 6 || Aggression.Count != 8)
            throw new ArgumentException("Unexpected tactic or personality settings.");
        foreach (Tactic tactic in Enum.GetValues(typeof(Tactic)))
            if (!Weights.TryGetValue(tactic.ToString(), out var weight))
                throw new ArgumentException("Missing " + tactic);
            else
                Range(weight, 0, 3, tactic.ToString());
        foreach (
            var name in new[]
            {
                "Normal",
                "Chad",
                "GigaChad",
                "Wreckless",
                "Rat",
                "SnappingTurtle",
                "Coward",
                "Timmy",
            }
        )
            if (!Aggression.TryGetValue(name, out var aggression))
                throw new ArgumentException("Missing " + name);
            else
                Range(aggression, 0, 1, name);
    }

    internal static void Range(double value, double min, double max, string name)
    {
        if (double.IsNaN(value) || double.IsInfinity(value) || value < min || value > max)
            throw new ArgumentException(name + " must be between " + min + " and " + max + ".");
    }
}

public sealed class ProgressConfig
{
    public bool Enabled { get; set; } = true;
    public double HoldSoft { get; set; } = 8;
    public double HoldLimit { get; set; } = 20;
    public double StalledLimit { get; set; } = 5;
    public double ProgressMeters { get; set; } = .75;
    public double ReviewInterval { get; set; } = 12;
    public double Retry { get; set; } = 8;
    public double RepeatRetry { get; set; } = 15;
    public double QuietReset { get; set; } = 20;

    public void Validate()
    {
        CometConfig.Range(HoldSoft, 1, 30, nameof(HoldSoft));
        CometConfig.Range(HoldLimit, 2, 60, nameof(HoldLimit));
        if (HoldLimit <= HoldSoft)
            throw new ArgumentException("Hold limit must exceed fatigue start.");
        CometConfig.Range(StalledLimit, 2, 15, nameof(StalledLimit));
        CometConfig.Range(ProgressMeters, .25, 2, nameof(ProgressMeters));
        CometConfig.Range(ReviewInterval, StalledLimit, 60, nameof(ReviewInterval));
        CometConfig.Range(Retry, 2, 30, nameof(Retry));
        CometConfig.Range(RepeatRetry, Retry, 60, nameof(RepeatRetry));
        CometConfig.Range(QuietReset, 5, 120, nameof(QuietReset));
    }
}

public sealed class CoverResponseConfig
{
    public bool Enabled { get; set; } = true;
    public double ShortExposure { get; set; } = .75;
    public double LongExposure { get; set; } = 1.5;
    public double ScoreCap { get; set; } = 25;

    public void Validate()
    {
        CometConfig.Range(ShortExposure, .1, 2, nameof(ShortExposure));
        CometConfig.Range(LongExposure, .2, 6, nameof(LongExposure));
        if (LongExposure <= ShortExposure)
            throw new ArgumentException("Long exposure must exceed short exposure.");
        CometConfig.Range(ScoreCap, 0, 25, nameof(ScoreCap));
    }
}

public sealed class ThreatSelectionConfig
{
    public bool Enabled { get; set; } = true;
    public double Interval { get; set; } = .25;
    public double Commitment { get; set; } = 2;
    public double SwitchMargin { get; set; } = .20;
    public double HitMemory { get; set; } = 3;
    public double ShotMemory { get; set; } = 2;
    public double CloseDistance { get; set; } = 15;
    public double ImmediateDistance { get; set; } = 6;
    public double FootstepDistance { get; set; } = 10;
    public double FootstepMemory { get; set; } = 1.5;

    public void Validate()
    {
        CometConfig.Range(Interval, .1, 2, nameof(Interval));
        CometConfig.Range(Commitment, 0, 10, nameof(Commitment));
        CometConfig.Range(SwitchMargin, 0, 1, nameof(SwitchMargin));
        CometConfig.Range(HitMemory, .25, 10, nameof(HitMemory));
        CometConfig.Range(ShotMemory, .25, 10, nameof(ShotMemory));
        CometConfig.Range(CloseDistance, 1, 30, nameof(CloseDistance));
        CometConfig.Range(ImmediateDistance, 1, CloseDistance, nameof(ImmediateDistance));
        CometConfig.Range(FootstepDistance, 1, 20, nameof(FootstepDistance));
        CometConfig.Range(FootstepMemory, .1, 5, nameof(FootstepMemory));
    }
}

public sealed class FearConfig
{
    public bool Enabled { get; set; } = true;
    public double Multiplier { get; set; } = 1;
    public double AllyDeath { get; set; } = .30;
    public double DeathMemory { get; set; } = 30;
    public double Injury { get; set; } = .25;
    public double Outnumbered { get; set; } = .15;
    public double HeavySuppression { get; set; } = .10;
    public double RisePerSecond { get; set; } = .50;
    public double RecoveryPerSecond { get; set; } = .05;
    public double RecoveryDelay { get; set; } = 3;
    public double PanicEnter { get; set; } = .75;
    public double PanicExit { get; set; } = .55;
    public double ScoreCap { get; set; } = 15;
    public Dictionary<string, double> Base { get; set; } =
        new Dictionary<string, double>
        {
            ["Coward"] = .35,
            ["Timmy"] = .20,
            ["Rat"] = .20,
            ["Normal"] = .10,
            ["SnappingTurtle"] = .10,
            ["Chad"] = .05,
            ["GigaChad"] = 0,
            ["Wreckless"] = 0,
        };
    public Dictionary<string, double> Sensitivity { get; set; } =
        new Dictionary<string, double>
        {
            ["Coward"] = 1.5,
            ["Timmy"] = 1.3,
            ["Rat"] = 1.2,
            ["Normal"] = 1,
            ["SnappingTurtle"] = .9,
            ["Chad"] = .6,
            ["GigaChad"] = .4,
            ["Wreckless"] = .3,
        };

    public void Validate()
    {
        CometConfig.Range(Multiplier, 0, 3, nameof(Multiplier));
        foreach (var x in new[] { AllyDeath, Injury, Outnumbered, HeavySuppression })
            CometConfig.Range(x, 0, 1, "Fear contribution");
        CometConfig.Range(DeathMemory, 1, 120, nameof(DeathMemory));
        CometConfig.Range(RisePerSecond, .01, 2, nameof(RisePerSecond));
        CometConfig.Range(RecoveryPerSecond, .01, 1, nameof(RecoveryPerSecond));
        CometConfig.Range(RecoveryDelay, 0, 30, nameof(RecoveryDelay));
        CometConfig.Range(PanicExit, 0, .98, nameof(PanicExit));
        CometConfig.Range(PanicEnter, PanicExit + .01, 1, nameof(PanicEnter));
        CometConfig.Range(ScoreCap, 0, 25, nameof(ScoreCap));
        if (Base == null || Sensitivity == null || Base.Count != 8 || Sensitivity.Count != 8)
            throw new ArgumentException("Expected eight fear personalities.");
        foreach (
            var name in new[]
            {
                "Normal",
                "Chad",
                "GigaChad",
                "Wreckless",
                "Rat",
                "SnappingTurtle",
                "Coward",
                "Timmy",
            }
        )
        {
            if (!Base.TryGetValue(name, out var b) || !Sensitivity.TryGetValue(name, out var s))
                throw new ArgumentException("Missing fear personality: " + name);
            CometConfig.Range(b, 0, 1, name + " base fear");
            CometConfig.Range(s, 0, 3, name + " fear sensitivity");
        }
    }
}

public sealed class DecisionVarietyConfig
{
    public bool Enabled { get; set; } = true;
    public int MaxOptions { get; set; } = 3;
    public double HiddenScoreGap { get; set; } = .15;
    public double VisibleScoreGap { get; set; } = .10;
    public double HiddenIntensity { get; set; } = 1;
    public double VisibleIntensity { get; set; } = .6;

    public void Validate()
    {
        CometConfig.Range(MaxOptions, 1, 3, nameof(MaxOptions));
        CometConfig.Range(HiddenScoreGap, 0, .3, nameof(HiddenScoreGap));
        CometConfig.Range(VisibleScoreGap, 0, .3, nameof(VisibleScoreGap));
        CometConfig.Range(HiddenIntensity, 0, 3, nameof(HiddenIntensity));
        CometConfig.Range(VisibleIntensity, 0, 3, nameof(VisibleIntensity));
    }
}

public sealed class SightContinuityConfig
{
    public bool Enabled { get; set; } = true;
    public double CloseDuration { get; set; } = .8;
    public double CloseDistance { get; set; } = 8;
    public double MediumDuration { get; set; } = 1.5;
    public double MaxDistance { get; set; } = 45;
    public double StableSight { get; set; } = 0;

    public void Validate()
    {
        CometConfig.Range(CloseDuration, .1, 2, nameof(CloseDuration));
        CometConfig.Range(CloseDistance, 1, 20, nameof(CloseDistance));
        CometConfig.Range(MediumDuration, .1, 2, nameof(MediumDuration));
        CometConfig.Range(MaxDistance, CloseDistance, 80, nameof(MaxDistance));
        CometConfig.Range(StableSight, 0, 2, nameof(StableSight));
    }
}

public sealed class VisibleCombatConfig
{
    public bool Enabled { get; set; } = true;
    public double ShootWeight { get; set; } = 1;
    public double CoverWeight { get; set; } = 1;
    public double PushWeight { get; set; } = 1;
    public double RiskTolerance { get; set; } = .55;
    public double Commitment { get; set; } = 2;
    public double SwitchMargin { get; set; } = 12;
    public double MaxPushPath { get; set; } = 25;
    public double CoverMemory { get; set; } = 3;

    public void Validate()
    {
        CometConfig.Range(ShootWeight, 0, 3, nameof(ShootWeight));
        CometConfig.Range(CoverWeight, 0, 3, nameof(CoverWeight));
        CometConfig.Range(PushWeight, 0, 3, nameof(PushWeight));
        CometConfig.Range(RiskTolerance, 0, 1, nameof(RiskTolerance));
        CometConfig.Range(Commitment, .25, 5, nameof(Commitment));
        CometConfig.Range(SwitchMargin, 0, 50, nameof(SwitchMargin));
        CometConfig.Range(MaxPushPath, 3, 50, nameof(MaxPushPath));
        CometConfig.Range(CoverMemory, .5, 8, nameof(CoverMemory));
    }
}

public sealed class LoadoutConfig
{
    public bool Enabled { get; set; } = true;
    public double ScoreCap { get; set; } = 15;
    public double RefreshSeconds { get; set; } = 5;
    public int GrenadeStockCap { get; set; } = 3;
    public Dictionary<string, double> Ranges { get; set; } =
        new Dictionary<string, double>
        {
            ["Pistol"] = 8,
            ["Shotgun"] = 8,
            ["ShotgunSlug"] = 25,
            ["Smg"] = 12,
            ["RifleIron"] = 20,
            ["RifleDot"] = 25,
            ["RifleScope"] = 35,
            ["DmrIron"] = 30,
            ["DmrScope"] = 50,
            ["BoltIron"] = 40,
            ["BoltScope"] = 70,
        };

    public void Validate()
    {
        CometConfig.Range(ScoreCap, 0, 25, nameof(ScoreCap));
        CometConfig.Range(RefreshSeconds, 1, 30, nameof(RefreshSeconds));
        CometConfig.Range(GrenadeStockCap, 1, 10, nameof(GrenadeStockCap));
        if (Ranges == null || Ranges.Count != 11)
            throw new ArgumentException("Expected eleven loadout ranges.");
        foreach (
            var key in new[]
            {
                "Pistol",
                "Shotgun",
                "ShotgunSlug",
                "Smg",
                "RifleIron",
                "RifleDot",
                "RifleScope",
                "DmrIron",
                "DmrScope",
                "BoltIron",
                "BoltScope",
            }
        )
            if (!Ranges.TryGetValue(key, out var value))
                throw new ArgumentException("Missing loadout range: " + key);
            else
                CometConfig.Range(value, 3, 150, key);
    }
}

public sealed class OpponentConfig
{
    public bool Enabled { get; set; } = true;
    public double ScoreCap { get; set; } = 10;
    public double ObservationSeconds { get; set; } = .5;
    public double WeaponDistance { get; set; } = 50;
    public double ProtectionDistance { get; set; } = 25;
    public double WeaponMemory { get; set; } = 5;
    public double ProtectionMemory { get; set; } = 15;
    public double InjuryMemory { get; set; } = 3;

    public void Validate()
    {
        CometConfig.Range(ScoreCap, 0, 25, nameof(ScoreCap));
        CometConfig.Range(ObservationSeconds, .25, 3, nameof(ObservationSeconds));
        CometConfig.Range(WeaponDistance, 5, 100, nameof(WeaponDistance));
        CometConfig.Range(ProtectionDistance, 5, WeaponDistance, nameof(ProtectionDistance));
        CometConfig.Range(WeaponMemory, 1, 20, nameof(WeaponMemory));
        CometConfig.Range(ProtectionMemory, 1, 30, nameof(ProtectionMemory));
        CometConfig.Range(InjuryMemory, .5, 10, nameof(InjuryMemory));
    }
}

public sealed class OpportunityConfig
{
    public bool Enabled { get; set; } = true;
    public double ReloadWindow { get; set; } = 2.5;
    public double HealWindow { get; set; } = 4;
    public double SurgeryWindow { get; set; } = 12;
    public double LootWindow { get; set; } = 2;
    public double DryFireWindow { get; set; } = 1.5;
    public double ReactionDelay { get; set; } = .25;
    public double SafetyMargin { get; set; } = .5;
    public double MaxPath { get; set; } = 25;
    public double ScoreCap { get; set; } = 20;
    public double CombinedScoreCap { get; set; } = 25;

    // A long action can emit many sounds. Silence must outlast even the largest allowed window.
    public double RearmSilence { get; set; } = 30;

    public double Window(string cue) =>
        cue switch
        {
            "reload" => ReloadWindow,
            "heal" => HealWindow,
            "surgery" => SurgeryWindow,
            "looting" => LootWindow,
            "dry-fire" => DryFireWindow,
            _ => 0,
        };

    public void Validate()
    {
        foreach (
            var v in new[] { ReloadWindow, HealWindow, SurgeryWindow, LootWindow, DryFireWindow }
        )
            CometConfig.Range(v, 1, 25, "Opportunity window");
        CometConfig.Range(ReactionDelay, .1, 1, nameof(ReactionDelay));
        CometConfig.Range(SafetyMargin, .1, 2, nameof(SafetyMargin));
        foreach (
            var v in new[] { ReloadWindow, HealWindow, SurgeryWindow, LootWindow, DryFireWindow }
        )
            if (v <= ReactionDelay + SafetyMargin)
                throw new ArgumentException(
                    "Opportunity windows must exceed reaction delay plus safety margin."
                );
        CometConfig.Range(MaxPath, 1, 50, nameof(MaxPath));
        CometConfig.Range(ScoreCap, 0, 25, nameof(ScoreCap));
        CometConfig.Range(CombinedScoreCap, 25, 50, nameof(CombinedScoreCap));
        CometConfig.Range(RearmSilence, 25, 120, nameof(RearmSilence));
    }
}

public sealed class HandoffConfig
{
    public double Grace { get; set; } = .5;
    public double Retry { get; set; } = 1;
    public double ErrorRetry { get; set; } = 10;

    public void Validate()
    {
        CometConfig.Range(Grace, 0, 2, nameof(Grace));
        CometConfig.Range(Retry, 0, 5, nameof(Retry));
        CometConfig.Range(ErrorRetry, 1, 60, nameof(ErrorRetry));
    }
}
