using System;
using System.Collections.Generic;
using Comet.Core;
using SAIN.Components;

namespace Comet.Client;

public sealed partial class Plugin
{
    partial void EndJournalBot(BotState s, string reason);

    private LifecycleTrace CleanupLifecycle(BotComponent b, BotState s, string phase)
    {
        string reason = "lifecycle-" + phase + "-bot-ended";
        EndJournalBot(s, reason);
        var trace = new LifecycleTrace { State = phase, Reason = reason };
        var failures = new List<string>();
        bool oldCleaning = faultCleaning,
            oldLife = lifeCleaning,
            oldFailed = faultCleanupFailed;
        var oldError = faultCleanupError;
        lifeCleaning = true;
        faultCleaning = true;
        void Step(string name, Action cleanup)
        {
            trace.CleanupSteps++;
            faultCleanupFailed = false;
            faultCleanupError = null;
            FaultCleanup(cleanup);
            if (!faultCleanupFailed)
                return;
            trace.FailedSteps++;
            if (failures.Count < 8)
                failures.Add(
                    BoundLife(
                        name
                            + ": "
                            + faultCleanupError?.GetType().Name
                            + ": "
                            + faultCleanupError?.Message
                    )
                );
            SafeFaultLog(
                "C09 cleanup " + name + " failed for " + s.ProfileId + ": " + faultCleanupError
            );
        }
        try
        {
            // Deliberately independent: Release() historically chains these together.
            Step("DoorSpatial", () => EndDoorSpatial(s, reason));
            Step("DoorRecovery", () => EndDoorRecovery(s, reason));
            Step("DoorTransit", () => EndDoorTransit(s, reason));
            Step("Encounter", () => EndEncounter(s, reason));
            Step("Outcome", () => EndOutcome(s, reason));
            Step("Angle", () => EndAngle(s, reason));
            Step("CombatStep", () => EndCombatStep(s, reason));
            Step("CornerPeek", () => EndCornerPeek(s, reason));
            Step("CombatLean", () => EndCombatLean(s, reason));
            Step("Chase", () => EndChase(s, reason));
            Step("Relocation", () => EndRelocation(s, reason, true));
            Step("Reload", () => EndReloadTiming(s, reason));
            Step("PistolSwap", () => EndPistolSwap(s, reason));
            Step("WeaponReturn", () => EndWeaponReturn(s, reason));
            Step("QuickReload", () => EndQuickReload(s, reason));
            Step("MagazineRecovery", () => PauseMagazineRecovery(s, reason, true));
            Step("WeaponRange", () => EndWeaponRange(s, reason));
            Step("WeaponSync", () => EndWeaponSync(s, reason));
            Step("Discipline", () => EndDiscipline(s, reason));
            Step("GrenadeJudgment", () => EndGrenadeJudgment(s, reason));
            Step("GrenadeChoice", () => StopGrenadeChoice(s, reason));
            Step("GrenadeTrajectory", () => StopGrenadeTrajectory(s, reason));
            Step("Hands", () => EndHandsRecovery(s, reason));
            Step("Recoil", () => EndRecovery(s, reason));
            Step("GeometryFire", () => StopGeometryOwned(s));
            Step("Geometry", () => ReleaseGeometry(s, reason));
            Step("Feint", () => EndFeint(s, reason, true));
            Step("HoldAngle", () => EndHoldAngle(s, reason));
            Step("CoverTravel", () => EndCoverTravel(s, reason));
            Step("AdsTravel", () => EndAdsTravel(s, reason));
            Step("Weave", () => EndWeave(s, reason));
            Step("FireLane", () => EndLane(s, reason));
            Step("SquadFire", () => EndSquadFire(s, reason));
            Step("SquadVoice", () => EndSquadVoice(s, reason));
            Step("Lights", () => EndLightDiscipline(s, reason));
            Step("Devices", () => EndTacticalDevices(s, reason));
            Step("RestLights", () => EndRestLights(s, reason));
            Step("Jump", () => EndTacticalJump(s, reason));
            Step("DoorEntry", () => EndDoorEntry(s, reason));
            Step("DoorReservation", () => ReleaseDoorReservation(s, reason));
            Step("DoorEmergency", () => ReleaseDoorEmergency(s, reason));
            Step("DoorInterruption", () => ReleaseDoorInterruption(s, reason));
            Step("DoorSupport", () => EndDoorSupport(s, reason));
            Step("SilentSearch", () => EndSilentSearch(s, reason));
            Step("DoorAmbush", () => EndDoorAmbush(s, reason));
            Step("DoorClear", () => EndDoorClear(s, reason));
            Step("DoorFake", () => EndDoorFake(s, reason));
            Step("DoorStep", () => EndDoorStep(s, reason));
            Step("DoorRun", () => EndDoorRun(s, reason));
            Step("DoorJump", () => EndDoorJump(s, reason));
            Step("DoorApproach", () => EndDoor(s, reason));
            Step("Abandonment", () => EndAbandonment(s, reason, true));
            Step("Maneuver", () => EndManeuver(s, reason));
            Step("CoverMate", () => EndCoverMate(s, reason));
            Step("SquadTrade", () => EndSquadTrade(s, reason));
            Step("Entrance", () => ReleaseEntrance(s));
            Step("Crossfire", () => EndCrossfire(b, s, reason));
            Step("PostCombat", () => EndPostCombat(b, s, reason, true));
            Step("ReadyStance", () => EndReadyStance(s, reason));
            Step("MedicalPosture", () => EndMedicalPosture(s, reason));
            Step("Medical", () => EndMedical(s, reason));
            Step("MovementRecovery", () => EndMovementRecovery(s, reason));
            Step("GrenadeEscape", () => DropGrenadeEscape(b, s, reason));
            Step("OwnGrenade", () => DropOwnSafety(b, s, reason));
            Step("Gas", () => DropGas(b, s, reason));
            Step("Flash", () => DropFlash(b, s, reason, true));
            Step("Gunfire", () => StopGunfire(s, reason));
            Step("Quiet", () => StopQuiet(b, s, reason));
            Step("Impact", () => EndImpact(b, s, reason));
            Step("Threat", () => ReleaseThreat(b, s, reason));
            Step("FakeIterator", () => DisposeDoorFake(s, reason));
            Step("Decisions", () => ResetLifecycleDecisions(s, reason));
        }
        finally
        {
            s.ActiveLabel = "";
            s.Decision = "";
            s.RequestedFrame = -1;
            s.Retired = true;
            Api.Decisions.Set(s.ProfileId, "");
            Api.Decisions.SetCrossfire(s.ProfileId, "");
            Api.Decisions.SetPostCombat(s.ProfileId, "");
            faultCleaning = oldCleaning;
            lifeCleaning = oldLife;
            faultCleanupFailed = oldFailed;
            faultCleanupError = oldError;
        }
        trace.Failures = failures.ToArray();
        if (trace.FailedSteps > 0)
            faultIsolation.DisableBot(b);
        return trace;
    }
}
