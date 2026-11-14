using System;
using System.Linq;
using Comet.Core;
using EFT;
using HarmonyLib;
using Newtonsoft.Json;
using UnityEngine;

namespace Comet.Client;

// Explicit C13 boundaries. Do not wrap this coordinator in the generated C08 guard.
public sealed partial class Plugin
{
    partial void StopPerformance(System.Threading.Tasks.Task? diagnosticsDone);
    partial void ClearControlHandoffs();
    partial void FinishCombatJournal();
    private readonly RaidLifetime raidLifetime = new();
    private float nextDeliveryReport;
    private static void PatchRaidCleanup(Harmony h) => h.Patch(
        AccessTools.Method(typeof(GameWorld), "Dispose", Type.EmptyTypes) ?? throw new MissingMethodException("GameWorld.Dispose"),
        prefix: new HarmonyMethod(typeof(Plugin), nameof(BeforeRaidDisposed)) { priority = Priority.First });
    private static void BeforeRaidDisposed(GameWorld __instance)
    {
        var p=Instance;
        if(p!=null && ReferenceEquals(p.world,__instance))p.EndRaid("world-disposed");
    }
    partial void GuardRaid(FaultScope scope, ref bool allowed)
    {
        if(!raidLifetime.Active && !scope.Cleanup && scope.Entry!="Update")allowed=false;
    }
    private void EndRaid(string reason = "world-ended")
    {
        if(!raidLifetime.End(world))return;
        try { FinishCombatJournal(); } catch { }
        world=null; // Close producers before any native cleanup can reenter COMET.
        var timer=System.Diagnostics.Stopwatch.StartNew();
        var batch=new RaidCleanupBatch();var trace=batch.Trace;
        trace.Reason=reason;trace.Bots=states.Count;trace.PendingConfigDiscarded=loading.Discard();
        long revision=config?.Revision??0;
        bool old=faultCleaning,oldFailed=faultCleanupFailed;var oldError=faultCleanupError;
        faultCleaning=true;
        void Step(string name,Action call) => batch.Step(name,()=>{
            faultCleanupFailed=false;faultCleanupError=null;
            call();
            // Generated adapter boundaries report failures instead of throwing.
            if(faultCleanupFailed)throw faultCleanupError??new InvalidOperationException("Adapter cleanup failed");
        });
        Step("control-handoff",()=>ClearControlHandoffs());
        try
        {
            // Detach event producers and invalidate every local Fika queue first.
            var events=clearEvents;clearEvents=null;
            if(!ReferenceEquals(events,null))
            {
                Step("grenade-throw-event",()=>events!.OnGrenadeThrow-=ClearProjectileThrown);
                Step("grenade-explosion-event",()=>events!.OnGrenadeExplosive-=ClearProjectileExplosion);
            }
            Step("fika-transport",Api.DoorMotionNetwork.Clear);
            foreach(var link in clearOwnGrenades.ToArray())Step("grenade-tracker",link.Dispose);
            clearOwnGrenades.Clear();
            foreach(var scan in coverScans.Values.ToArray())Step("cover-iterator",scan.Runner.Dispose);
            coverScans.Clear();scanningFinder=null;scanningRunner=null;coverFactoryBypass=false;

            foreach(var pair in states.ToArray())Step("bot:"+pair.Value.ProfileId,()=>{
                var result=CleanupLifecycle(pair.Key,pair.Value,"raid-ended");
                trace.Steps+=result.CleanupSteps;
                trace.FailedSteps+=result.FailedSteps;
                if(result.FailedSteps>0){trace.State="completed-with-errors";trace.Failures=trace.Failures.Concat(result.Failures).Take(12).ToArray();}
                StopBlast(pair.Value,"explosion-raid-ended");
            });
            foreach(var operation in supportOperations.Values.ToArray())Step("door-support",()=>EndSupportOperation(operation,"support-raid-ended"));
            foreach(var delivery in movementPending.Values.ToArray())Step("movement-hearing",()=>RemoveMovement(delivery,"movement-cleared"));
            Step("validity-summary",ClearValidity);
            Step("floor-summary",()=>FlushFloorCounters(true));
            Step("close-summary",()=>FlushCloseCounters(true));
            Step("light-detection",ClearLightDetection);
            Step("impact-summary",()=>FlushImpactCounters(true));
            Step("movement-summary",FlushMovementSummary);
            Step("tactical-probe-summary",()=>FlushTacticalProbes(true));

            // These steps only release COMET-owned references. They still run if
            // native cleanup failed, and never dispose another mod's world/bots.
            Step("door-caches",()=>{
                ClearLocalDoors();
                doorSwings.Clear();motionWatches.Clear();pushContexts.Clear();
                closingSelected.Clear();closingPending.Clear();closingHistory=new();closingLogs=new();
                recoveryEpochs=new();transitControllers=new();medicalConsumption=new();
                supportOperations.Clear();supportBoard.Clear();doorReservations.Clear();
                Array.Clear(spatialPanels,0,spatialPanels.Length);Array.Clear(swingHits,0,swingHits.Length);
                Array.Clear(doorOccupants,0,doorOccupants.Length);doorBudget.Clear();doorClock.Reset();
            });
            Step("squad-caches",()=>{
                fearMemory.Clear();fearDeaths=new();
                crossfireClaims.Clear();maneuverBoard.Clear();coverMateBoard.Clear();stormBoard.Clear();nativeStormPlans.Clear();laneShots.Clear();
                stormObservationConfig=null;stormOpportunityConfig=null;stormObservationFrame=-1;
                voicePolicy.Clear();voiceSource=null;voiceRequest=voicePlayback=voiceEmission=null;
            });
            Step("perception-caches",()=>{
                movementQueue.Clear();movementPending.Clear();movementSources.Clear();movementListeners.Clear();movementLog.Clear();movementCounts.Clear();
                movementInjection=movementReaction=null;gunfireCall=null;quietCaller=null;
                nextMovementCleanup=nextMovementSummary=movementFaultUntil=0;movementSequence=0;movementWarned=false;
                floorCache.Clear();floorPhysics.Clear();floorCounters.Clear();nextFloorSummary=floorFaultUntil=0;floorWarned=false;
                closeCounters.Clear();nextCloseSummary=closeFaultUntil=0;closeWarned=false;
                impactCounters.Clear();nextImpactSummary=0;impactProbeFrame=-1;
                ClearEnvironment();
            });
            Step("grenade-caches",()=>{
                ClearBlasts();
                ownProjectiles.Clear();ownSampleAt=0;gasBridge.Clear();grenadeArcBudget.Clear();grenadeArcClock.Reset();
            });
            Step("movement-caches",()=>{
                Array.Clear(recoveryVolumes,0,recoveryVolumes.Length);recoveryFrame=-1;recoverySamples=0;
                Array.Clear(jumpHits,0,jumpHits.Length);
            });
            Step("sain-preset-context",ClearSainPreset);
            Step("lifecycle-caches",ClearLifecycles);
            Step("validity-caches",()=>{validityOwners=new();validityContacts=new();validitySources=new();validityLogs.Clear();});
            Step("fault-caches",()=>{faultIsolation.Clear();pendingFaultReset.Clear();faultSinkWarned=false;});
        }
        finally
        {
            states.Clear();maintenanceSnapshot.Clear();config=null;loading.Discard();nextSweep=0;subscriberWarned=false;
            Step("diagnostic-delivery",Api.Decisions.FlushPending);
            Step("diagnostic-delivery-report",()=>diagnostics?.WriteEssential(Diagnostics.Serialize(new {schema=1,session,stage="diagnostic-delivery-final",delivery=Api.Decisions.DeliveryStats()})));
            Step("decision-cache",Api.Decisions.Clear);
            trace.RemainingBots=states.Count;trace.RemainingScans=coverScans.Count;
            trace.DurationMs=Math.Round(timer.Elapsed.TotalMilliseconds,3);
            try {
                var json=Diagnostics.Serialize(new {schema=1,session,sequence=++sequence,feature="C13",stage="cleanup",
                    decision="RaidCleanup",reason,realtime=Time.realtimeSinceStartup,utc=DateTime.UtcNow.ToString("O"),
                    configRevision=revision,profileId="",name="Raid cleanup",raidCleanup=trace});
                diagnostics?.WriteEssential(json);Api.Decisions.Publish(json,WarnFaultSink);Api.Decisions.FlushPending();
                SafeFaultLog($"C13 raid cleanup: {trace.Bots} bots, {trace.Steps} steps, {trace.FailedSteps} failures, {trace.DurationMs:0.###} ms.");
                foreach(var failure in trace.Failures)SafeFaultLog("C13 "+failure);
            } catch(Exception ex) { SafeFaultLog("C13 report could not be written: "+ex); }
            var writer=diagnostics;diagnostics=null;
            try { writer?.Dispose(); } catch(Exception ex) { SafeFaultLog("C13 diagnostics close failed: "+ex); } // Drains strings off-thread, no Unity wait.
            StopPerformance(writer?.Completion);
            session="";Api.Decisions.RaidSession="";Api.Decisions.RaidClosed=true;
            faultCleaning=old;faultCleanupFailed=oldFailed;faultCleanupError=oldError;
        }
    }
}
