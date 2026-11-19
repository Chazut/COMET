using System;
using SAIN.Components;

namespace Comet.Client;

public sealed partial class Plugin
{
    internal void EndDoorSpatial(BotState s, string reason)
    {
        CleanupLog.Add("EndDoorSpatial");
        if (FailCleanup == "EndDoorSpatial")
            throw new InvalidOperationException("injected EndDoorSpatial");
        s.OwnCommand = false;
    }

    internal void EndDoorRecovery(BotState s, string reason)
    {
        CleanupLog.Add("EndDoorRecovery");
        if (FailCleanup == "EndDoorRecovery")
            throw new InvalidOperationException("injected EndDoorRecovery");
        s.OwnCommand = false;
    }

    internal void EndDoorTransit(BotState s, string reason)
    {
        CleanupLog.Add("EndDoorTransit");
        if (FailCleanup == "EndDoorTransit")
            throw new InvalidOperationException("injected EndDoorTransit");
        s.OwnCommand = false;
    }

    internal void EndEncounter(BotState s, string reason)
    {
        CleanupLog.Add("EndEncounter");
        if (FailCleanup == "EndEncounter")
            throw new InvalidOperationException("injected EndEncounter");
        s.OwnCommand = false;
    }

    internal void EndOutcome(BotState s, string reason)
    {
        CleanupLog.Add("EndOutcome");
        if (FailCleanup == "EndOutcome")
            throw new InvalidOperationException("injected EndOutcome");
        s.OwnCommand = false;
    }

    internal void EndAngle(BotState s, string reason)
    {
        CleanupLog.Add("EndAngle");
        if (FailCleanup == "EndAngle")
            throw new InvalidOperationException("injected EndAngle");
        s.OwnCommand = false;
    }

    internal void EndCombatStep(BotState s, string reason)
    {
        CleanupLog.Add("EndCombatStep");
        if (FailCleanup == "EndCombatStep")
            throw new InvalidOperationException("injected EndCombatStep");
        s.OwnCommand = false;
    }

    internal void EndCornerPeek(BotState s, string reason)
    {
        CleanupLog.Add("EndCornerPeek");
        if (FailCleanup == "EndCornerPeek")
            throw new InvalidOperationException("injected EndCornerPeek");
        s.OwnCommand = false;
    }

    internal void EndCombatLean(BotState s, string reason)
    {
        CleanupLog.Add("EndCombatLean");
        if (FailCleanup == "EndCombatLean")
            throw new InvalidOperationException("injected EndCombatLean");
        s.OwnCommand = false;
    }

    internal void EndChase(BotState s, string reason)
    {
        CleanupLog.Add("EndChase");
        if (FailCleanup == "EndChase")
            throw new InvalidOperationException("injected EndChase");
        s.OwnCommand = false;
    }

    internal void EndRelocation(BotState s, string reason, bool clear)
    {
        CleanupLog.Add("EndRelocation");
        if (FailCleanup == "EndRelocation")
            throw new InvalidOperationException("injected EndRelocation");
        s.OwnCommand = false;
    }

    internal void EndReloadTiming(BotState s, string reason)
    {
        CleanupLog.Add("EndReloadTiming");
        if (FailCleanup == "EndReloadTiming")
            throw new InvalidOperationException("injected EndReloadTiming");
        s.OwnCommand = false;
    }

    internal void EndPistolSwap(BotState s, string reason)
    {
        CleanupLog.Add("EndPistolSwap");
        if (FailCleanup == "EndPistolSwap")
            throw new InvalidOperationException("injected EndPistolSwap");
        s.OwnCommand = false;
    }

    internal void EndWeaponReturn(BotState s, string reason)
    {
        CleanupLog.Add("EndWeaponReturn");
        if (FailCleanup == "EndWeaponReturn")
            throw new InvalidOperationException("injected EndWeaponReturn");
        s.OwnCommand = false;
    }

    internal void EndQuickReload(BotState s, string reason)
    {
        CleanupLog.Add("EndQuickReload");
        if (FailCleanup == "EndQuickReload")
            throw new InvalidOperationException("injected EndQuickReload");
        s.OwnCommand = false;
    }

    internal void PauseMagazineRecovery(BotState s, string reason, bool clear)
    {
        CleanupLog.Add("PauseMagazineRecovery");
        if (FailCleanup == "PauseMagazineRecovery")
            throw new InvalidOperationException("injected PauseMagazineRecovery");
        s.OwnCommand = false;
    }

    internal void EndWeaponRange(BotState s, string reason)
    {
        CleanupLog.Add("EndWeaponRange");
        if (FailCleanup == "EndWeaponRange")
            throw new InvalidOperationException("injected EndWeaponRange");
        s.OwnCommand = false;
    }

    internal void EndWeaponSync(BotState s, string reason)
    {
        CleanupLog.Add("EndWeaponSync");
        if (FailCleanup == "EndWeaponSync")
            throw new InvalidOperationException("injected EndWeaponSync");
        s.OwnCommand = false;
    }

    internal void EndDiscipline(BotState s, string reason)
    {
        CleanupLog.Add("EndDiscipline");
        if (FailCleanup == "EndDiscipline")
            throw new InvalidOperationException("injected EndDiscipline");
        s.OwnCommand = false;
    }

    internal void EndGrenadeJudgment(BotState s, string reason)
    {
        CleanupLog.Add("EndGrenadeJudgment");
        if (FailCleanup == "EndGrenadeJudgment")
            throw new InvalidOperationException("injected EndGrenadeJudgment");
        s.OwnCommand = false;
    }

    internal void StopGrenadeChoice(BotState s, string reason)
    {
        CleanupLog.Add("StopGrenadeChoice");
        if (FailCleanup == "StopGrenadeChoice")
            throw new InvalidOperationException("injected StopGrenadeChoice");
        s.OwnCommand = false;
    }

    internal void StopGrenadeTrajectory(BotState s, string reason)
    {
        CleanupLog.Add("StopGrenadeTrajectory");
        if (FailCleanup == "StopGrenadeTrajectory")
            throw new InvalidOperationException("injected StopGrenadeTrajectory");
        s.OwnCommand = false;
    }

    internal void EndHandsRecovery(BotState s, string reason)
    {
        CleanupLog.Add("EndHandsRecovery");
        if (FailCleanup == "EndHandsRecovery")
            throw new InvalidOperationException("injected EndHandsRecovery");
        s.OwnCommand = false;
    }

    internal void EndRecovery(BotState s, string reason)
    {
        CleanupLog.Add("EndRecovery");
        if (FailCleanup == "EndRecovery")
            throw new InvalidOperationException("injected EndRecovery");
        s.OwnCommand = false;
    }

    internal void StopGeometryOwned(BotState s)
    {
        CleanupLog.Add("StopGeometryOwned");
        if (FailCleanup == "StopGeometryOwned")
            throw new InvalidOperationException("injected StopGeometryOwned");
        s.OwnCommand = false;
    }

    internal void ReleaseGeometry(BotState s, string reason)
    {
        CleanupLog.Add("ReleaseGeometry");
        if (FailCleanup == "ReleaseGeometry")
            throw new InvalidOperationException("injected ReleaseGeometry");
        s.OwnCommand = false;
    }

    internal void EndFeint(BotState s, string reason, bool clear)
    {
        CleanupLog.Add("EndFeint");
        if (FailCleanup == "EndFeint")
            throw new InvalidOperationException("injected EndFeint");
        s.OwnCommand = false;
    }

    internal void EndHoldAngle(BotState s, string reason)
    {
        CleanupLog.Add("EndHoldAngle");
        if (FailCleanup == "EndHoldAngle")
            throw new InvalidOperationException("injected EndHoldAngle");
        s.OwnCommand = false;
    }

    internal void EndCoverTravel(BotState s, string reason)
    {
        CleanupLog.Add("EndCoverTravel");
        if (FailCleanup == "EndCoverTravel")
            throw new InvalidOperationException("injected EndCoverTravel");
        s.OwnCommand = false;
    }

    internal void EndAdsTravel(BotState s, string reason)
    {
        CleanupLog.Add("EndAdsTravel");
        if (FailCleanup == "EndAdsTravel")
            throw new InvalidOperationException("injected EndAdsTravel");
        s.OwnCommand = false;
    }

    internal void EndWeave(BotState s, string reason)
    {
        CleanupLog.Add("EndWeave");
        if (FailCleanup == "EndWeave")
            throw new InvalidOperationException("injected EndWeave");
        s.OwnCommand = false;
    }

    internal void EndLane(BotState s, string reason)
    {
        CleanupLog.Add("EndLane");
        if (FailCleanup == "EndLane")
            throw new InvalidOperationException("injected EndLane");
        s.OwnCommand = false;
    }

    internal void EndSquadFire(BotState s, string reason)
    {
        CleanupLog.Add("EndSquadFire");
        if (FailCleanup == "EndSquadFire")
            throw new InvalidOperationException("injected EndSquadFire");
        s.OwnCommand = false;
    }

    internal void EndSquadVoice(BotState s, string reason)
    {
        CleanupLog.Add("EndSquadVoice");
        if (FailCleanup == "EndSquadVoice")
            throw new InvalidOperationException("injected EndSquadVoice");
        s.OwnCommand = false;
    }

    internal void EndLightDiscipline(BotState s, string reason)
    {
        CleanupLog.Add("EndLightDiscipline");
        if (FailCleanup == "EndLightDiscipline")
            throw new InvalidOperationException("injected EndLightDiscipline");
        s.OwnCommand = false;
    }

    internal void EndTacticalDevices(BotState s, string reason)
    {
        CleanupLog.Add("EndTacticalDevices");
        if (FailCleanup == "EndTacticalDevices")
            throw new InvalidOperationException("injected EndTacticalDevices");
        s.OwnCommand = false;
    }

    internal void EndRestLights(BotState s, string reason)
    {
        CleanupLog.Add("EndRestLights");
        if (FailCleanup == "EndRestLights")
            throw new InvalidOperationException("injected EndRestLights");
        s.OwnCommand = false;
    }

    internal void EndTacticalJump(BotState s, string reason)
    {
        CleanupLog.Add("EndTacticalJump");
        if (FailCleanup == "EndTacticalJump")
            throw new InvalidOperationException("injected EndTacticalJump");
        s.OwnCommand = false;
    }

    internal void EndDoorEntry(BotState s, string reason)
    {
        CleanupLog.Add("EndDoorEntry");
        if (FailCleanup == "EndDoorEntry")
            throw new InvalidOperationException("injected EndDoorEntry");
        s.OwnCommand = false;
    }

    internal void ReleaseDoorReservation(BotState s, string reason)
    {
        CleanupLog.Add("ReleaseDoorReservation");
        if (FailCleanup == "ReleaseDoorReservation")
            throw new InvalidOperationException("injected ReleaseDoorReservation");
        s.OwnCommand = false;
    }

    internal void ReleaseDoorEmergency(BotState s, string reason)
    {
        CleanupLog.Add("ReleaseDoorEmergency");
        if (FailCleanup == "ReleaseDoorEmergency")
            throw new InvalidOperationException("injected ReleaseDoorEmergency");
        s.OwnCommand = false;
    }

    internal void ReleaseDoorInterruption(BotState s, string reason)
    {
        CleanupLog.Add("ReleaseDoorInterruption");
        if (FailCleanup == "ReleaseDoorInterruption")
            throw new InvalidOperationException("injected ReleaseDoorInterruption");
        s.OwnCommand = false;
    }

    internal void EndDoorSupport(BotState s, string reason)
    {
        CleanupLog.Add("EndDoorSupport");
        if (FailCleanup == "EndDoorSupport")
            throw new InvalidOperationException("injected EndDoorSupport");
        s.OwnCommand = false;
    }

    internal void EndSilentSearch(BotState s, string reason)
    {
        CleanupLog.Add("EndSilentSearch");
        if (FailCleanup == "EndSilentSearch")
            throw new InvalidOperationException("injected EndSilentSearch");
        s.OwnCommand = false;
    }

    internal void EndDoorAmbush(BotState s, string reason)
    {
        CleanupLog.Add("EndDoorAmbush");
        if (FailCleanup == "EndDoorAmbush")
            throw new InvalidOperationException("injected EndDoorAmbush");
        s.OwnCommand = false;
    }

    internal void EndDoorClear(BotState s, string reason)
    {
        CleanupLog.Add("EndDoorClear");
        if (FailCleanup == "EndDoorClear")
            throw new InvalidOperationException("injected EndDoorClear");
        s.OwnCommand = false;
    }

    internal void EndDoorFake(BotState s, string reason)
    {
        CleanupLog.Add("EndDoorFake");
        if (FailCleanup == "EndDoorFake")
            throw new InvalidOperationException("injected EndDoorFake");
        s.OwnCommand = false;
    }

    internal void EndDoorStep(BotState s, string reason)
    {
        CleanupLog.Add("EndDoorStep");
        if (FailCleanup == "EndDoorStep")
            throw new InvalidOperationException("injected EndDoorStep");
        s.OwnCommand = false;
    }

    internal void EndDoorRun(BotState s, string reason)
    {
        CleanupLog.Add("EndDoorRun");
        if (FailCleanup == "EndDoorRun")
            throw new InvalidOperationException("injected EndDoorRun");
        s.OwnCommand = false;
    }

    internal void EndDoorJump(BotState s, string reason)
    {
        CleanupLog.Add("EndDoorJump");
        if (FailCleanup == "EndDoorJump")
            throw new InvalidOperationException("injected EndDoorJump");
        s.OwnCommand = false;
    }

    internal void EndDoor(BotState s, string reason)
    {
        CleanupLog.Add("EndDoor");
        if (FailCleanup == "EndDoor")
            throw new InvalidOperationException("injected EndDoor");
        s.OwnCommand = false;
    }

    internal void EndAbandonment(BotState s, string reason, bool clear)
    {
        CleanupLog.Add("EndAbandonment");
        if (FailCleanup == "EndAbandonment")
            throw new InvalidOperationException("injected EndAbandonment");
        s.OwnCommand = false;
    }

    internal void EndManeuver(BotState s, string reason)
    {
        CleanupLog.Add("EndManeuver");
        if (FailCleanup == "EndManeuver")
            throw new InvalidOperationException("injected EndManeuver");
        s.OwnCommand = false;
    }

    internal void EndCoverMate(BotState s, string reason)
    {
        CleanupLog.Add("EndCoverMate");
        if (FailCleanup == "EndCoverMate")
            throw new InvalidOperationException("injected EndCoverMate");
        s.OwnCommand = false;
    }

    internal void EndSquadTrade(BotState s, string reason)
    {
        CleanupLog.Add("EndSquadTrade");
        if (FailCleanup == "EndSquadTrade")
            throw new InvalidOperationException("injected EndSquadTrade");
        s.OwnCommand = false;
    }

    internal void EndStorm(BotState s, string reason)
    {
        CleanupLog.Add("EndStorm");
        if (FailCleanup == "EndStorm")
            throw new InvalidOperationException("injected EndStorm");
        s.OwnCommand = false;
    }

    internal void ReleaseEntrance(BotState s)
    {
        CleanupLog.Add("ReleaseEntrance");
        if (FailCleanup == "ReleaseEntrance")
            throw new InvalidOperationException("injected ReleaseEntrance");
        s.OwnCommand = false;
    }

    internal void EndCrossfire(BotComponent b, BotState s, string reason)
    {
        CleanupLog.Add("EndCrossfire");
        if (FailCleanup == "EndCrossfire")
            throw new InvalidOperationException("injected EndCrossfire");
        s.OwnCommand = false;
    }

    internal void EndPostCombat(BotComponent b, BotState s, string reason, bool clear)
    {
        CleanupLog.Add("EndPostCombat");
        if (FailCleanup == "EndPostCombat")
            throw new InvalidOperationException("injected EndPostCombat");
        s.OwnCommand = false;
    }

    internal void EndReadyStance(BotState s, string reason)
    {
        CleanupLog.Add("EndReadyStance");
        if (FailCleanup == "EndReadyStance")
            throw new InvalidOperationException("injected EndReadyStance");
        s.OwnCommand = false;
    }

    internal void EndMedicalPosture(BotState s, string reason)
    {
        CleanupLog.Add("EndMedicalPosture");
        if (FailCleanup == "EndMedicalPosture")
            throw new InvalidOperationException("injected EndMedicalPosture");
        s.OwnCommand = false;
    }

    internal void EndMedical(BotState s, string reason)
    {
        CleanupLog.Add("EndMedical");
        if (FailCleanup == "EndMedical")
            throw new InvalidOperationException("injected EndMedical");
        s.OwnCommand = false;
    }

    internal void EndMovementRecovery(BotState s, string reason)
    {
        CleanupLog.Add("EndMovementRecovery");
        if (FailCleanup == "EndMovementRecovery")
            throw new InvalidOperationException("injected EndMovementRecovery");
        s.OwnCommand = false;
    }

    internal void DropGrenadeEscape(BotComponent b, BotState s, string reason)
    {
        CleanupLog.Add("DropGrenadeEscape");
        if (FailCleanup == "DropGrenadeEscape")
            throw new InvalidOperationException("injected DropGrenadeEscape");
        s.OwnCommand = false;
    }

    internal void DropOwnSafety(BotComponent b, BotState s, string reason)
    {
        CleanupLog.Add("DropOwnSafety");
        if (FailCleanup == "DropOwnSafety")
            throw new InvalidOperationException("injected DropOwnSafety");
        s.OwnCommand = false;
    }

    internal void DropGas(BotComponent b, BotState s, string reason)
    {
        CleanupLog.Add("DropGas");
        if (FailCleanup == "DropGas")
            throw new InvalidOperationException("injected DropGas");
        s.OwnCommand = false;
    }

    internal void DropFlash(BotComponent b, BotState s, string reason, bool clear)
    {
        CleanupLog.Add("DropFlash");
        if (FailCleanup == "DropFlash")
            throw new InvalidOperationException("injected DropFlash");
        s.OwnCommand = false;
    }

    internal void StopGunfire(BotState s, string reason)
    {
        CleanupLog.Add("StopGunfire");
        if (FailCleanup == "StopGunfire")
            throw new InvalidOperationException("injected StopGunfire");
        s.OwnCommand = false;
    }

    internal void StopQuiet(BotComponent b, BotState s, string reason)
    {
        CleanupLog.Add("StopQuiet");
        if (FailCleanup == "StopQuiet")
            throw new InvalidOperationException("injected StopQuiet");
        s.OwnCommand = false;
    }

    internal void EndImpact(BotComponent b, BotState s, string reason)
    {
        CleanupLog.Add("EndImpact");
        if (FailCleanup == "EndImpact")
            throw new InvalidOperationException("injected EndImpact");
        s.OwnCommand = false;
    }

    internal void ReleaseThreat(BotComponent b, BotState s, string reason)
    {
        CleanupLog.Add("ReleaseThreat");
        if (FailCleanup == "ReleaseThreat")
            throw new InvalidOperationException("injected ReleaseThreat");
        s.OwnCommand = false;
    }

    internal void DisposeDoorFake(BotState s, string reason)
    {
        CleanupLog.Add("DisposeDoorFake");
        if (FailCleanup == "DisposeDoorFake")
            throw new InvalidOperationException("injected DisposeDoorFake");
        s.OwnCommand = false;
    }

    internal void Release(BotState s, string reason)
    {
        CleanupLog.Add("Release");
        if (FailCleanup == "Release")
            throw new InvalidOperationException("injected Release");
        s.OwnCommand = false;
    }
}
