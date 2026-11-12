using System;
using System.Collections;
using System.Linq;
using Comet.Core;
using EFT;
using EFT.Interactive;
using HarmonyLib;
using Newtonsoft.Json;
using SAIN.Components;
using SAIN.Models.Enums;
using SAIN.Preset.Shared.Enums;
using SAIN.SAINComponent.Classes.Mover;
using UnityEngine;
using UnityEngine.AI;

namespace Comet.Client;

internal sealed class DoorEntryRuntime
{
    internal readonly DoorEntry Policy=new();
    internal BotComponent? Owner;
    internal DoorWork? Door;
    internal NavMeshPath? Approach,Through;
    internal IBotPathData? OwnedPath;
    internal Vector3 Start,InteractionPoint,Exit,Forward,Contact;
    internal ECombatDecision Decision;
    internal int EnemyInstance,Frame=-1;
    internal float NextProbe,LastSafety,LogAt=-1000;
    internal bool Issuing,Prepared,Fault;
    internal string LogKey="";
    internal void Drop(){Owner=null;Door=null;Approach=null;Through=null;OwnedPath=null;Issuing=false;}
}

public sealed partial class Plugin
{
    private bool nativeEntryReady;
    private void PatchDoorEntry(Harmony harmony)
    {
        harmony.Patch(AccessTools.Method(typeof(DoorOpener),nameof(DoorOpener.SelectDoor)),postfix:new HarmonyMethod(typeof(Plugin),nameof(EntrySelectDoor)));
        harmony.Patch(AccessTools.Method(typeof(BreachDoorState),nameof(BreachDoorState.ExecuteDoorInteraction)),
            prefix:new HarmonyMethod(typeof(Plugin),nameof(NativeEntryBreachBefore)),postfix:new HarmonyMethod(typeof(Plugin),nameof(NativeEntryBreachObserved)));
        foreach(string name in new[]{"RunToPoint","RunToPointByWay","WalkToPoint","WalkToPointByWay"})
            foreach(var method in typeof(SAINMoverClass).GetMethods().Where(x=>x.Name==name&&x.ReturnType==typeof(bool)))
                harmony.Patch(method,prefix:new HarmonyMethod(typeof(Plugin),nameof(EntryKeepPath)));
        nativeEntryReady=true;
    }
    private static void NativeEntryBreachBefore(WorldInteractiveObject interactive,out bool __state)
        =>__state=interactive is Door door&&!door.IsBroken;
    private static void NativeEntryBreachObserved(WorldInteractiveObject interactive,InteractionResult interactionResult,Player user,bool __state)
    {
        var p=Instance;if(p==null||!__state||interactionResult.InteractionType!=EInteractionType.Breach||interactive is not Door door||!door.IsBroken)return;
        try
        {
            foreach(var pair in p.states)
            {
                var r=pair.Value.Entry;var b=pair.Key;
                if(r.Policy.Active&&r.Policy.Trace?.Requested==true&&ReferenceEquals(r.Door?.Data.Door,door)
                    &&b!=null&&!b.IsDead&&p.FakeScope(b)&&ReferenceEquals(user,b.Player))
                    r.Policy.Breached(true,Time.time);
            }
        }
        catch(Exception ex){p.Logger.LogWarning("D15 native breach observation unavailable: "+ex.GetBaseException().Message);}
    }
    private static bool NativeEntryAllowed(Door door,Vector3 position)=>door!=null&&door.DoorState==EDoorState.Shut
        &&door.Operatable&&door.enabled&&door.gameObject.activeInHierarchy&&door is not SlidingDoor&&!door.IsBroken
        &&door.CanBeBreached&&door.CanInteractWithBreach&&door.IsBreachAngle(position)&&door.BreachSuccessRoll(position);
    private static void EntrySelectDoor(DoorOpener __instance,ref bool __result,EInteractionType interactionType,DoorDataStruct currentDoor,IBotPathData pathData)
    {
        var p=Instance;var b=__instance.Bot;
        if(p?.config==null||!p.states.TryGetValue(b,out var s))return;
        try
        {
            if(s.Entry.Policy.Active){if(p.EntryGate(b,s).Length==0){__result=false;return;}p.EndDoorEntry(s,"entry-native-priority");return;}
            if(!__result||interactionType!=EInteractionType.Open||currentDoor.Door==null||SupportingDoor(s)!=null
                ||s.Door.Policy.Planning||s.Interruption.Policy.Pending||s.Support!=null||Time.time<s.Entry.NextProbe)return;
            string context=EntryContext(b.Decision.CurrentCombatDecision);if(context.Length==0)return;
            // A forward route, not just a door in the bot's gaze, must actually cross the threshold.
            if(!TryDoorGeometry(currentDoor,out var g)||pathData==null||(DP(pathData.Destination)-g.Center).Dot(g.Normal)*(DP(b.Position)-g.Center).Dot(g.Normal)>=0)return;
            if(p.TryStartDoorEntry(b,s,new(){Data=currentDoor,Geometry=g},context,false))__result=false;
        }
        catch(Exception ex){s.Entry.Fault=true;p.EndDoorEntry(s,"entry-adapter-error");p.Logger.LogWarning("D15 yielded: "+ex.GetBaseException().Message);}
    }
    private static string EntryContext(ECombatDecision d)=>d switch
    {ECombatDecision.RushEnemy=>"rush",ECombatDecision.Retreat or ECombatDecision.RunAway=>"retreat",ECombatDecision.Search=>"search",ECombatDecision.MoveToEngage or ECombatDecision.SeekCover or ECombatDecision.ShiftCover=>"move",_=>""};
    private static bool EntryKeepPath(SAINMoverClass __instance,ref bool __result)
    {
        var p=Instance;var b=__instance.Bot;
        if(p==null||!p.states.TryGetValue(b,out var s)||!s.Entry.Policy.Active||s.Entry.Issuing)return true;
        if(p.EntryGate(b,s).Length>0){p.EndDoorEntry(s,"entry-native-priority");return true;}
        __result=s.Entry.OwnedPath!=null;return false;
    }
    private bool KeepDoorEntry(BotComponent b,BotState s,ref ECombatDecision result,ref bool accepted)
    {
        if(!s.Entry.Policy.Active)return false;
        RefreshDoorEntry(b,s);if(!s.Entry.Policy.Active)return false;
        s.Expected=s.Entry.Decision;s.RequestedFrame=Time.frameCount;result=s.Expected;accepted=true;return true;
    }
    private string EntryGate(BotComponent b,BotState s,string context="")
    {
        if(config?.Enabled!=true||!config.DoorEntry.Enabled||s.Entry.Fault||!FakeScope(b)||b.IsDead||!b.SAINLayersActive)return "entry-out-of-scope";
        var e=b.GoalEnemy;var r=s.Entry;
        if(e==null||!e.EnemyKnown||!e.KnownPlaces.LastKnownPosition.HasValue||e.TimeSinceLastKnownUpdated>Math.Min(30,config.ContactLifetime))return "entry-contact-unavailable";
        if(b.Decision.CurrentSelfDecision!=ESelfActionType.None||b.Decision.CurrentSquadDecision!=ESquadDecision.None||b.Grenade.GrenadeDangerPoint!=null
            ||b.Flashed.IsFlashed||!b.Player.MovementContext.IsGrounded||b.Player.IsInPronePose||s.Impact.Owned||s.Emergency.Policy.Active
            ||!(r.Policy.Active&&r.Policy.Trace?.State=="interacting")&&!FakeWeaponReady(b)||MedicalPostureGuard(b)||FakeMedicalPriority(b))return "entry-native-priority";
        if(r.Policy.Active)
        {
            if(r.Policy.Trace!.State!="interacting"&&(JumpInteractionState(b)||b.Player.CurrentState.Name==EPlayerState.BreachDoor))return "entry-foreign-interaction";
            var interacting=b.Player.MovementContext.InteractionInfo.WorldInteractiveObject;
            if(r.Policy.Trace.State=="interacting"&&interacting!=null&&!ReferenceEquals(interacting,r.Door?.Data.Door))return "entry-foreign-interaction";
            if(e.EnemyProfileId!=r.Policy.Trace!.Target
                ||Vector3.Distance(e.KnownPlaces.LastKnownPosition.Value,r.Contact)>2)return "entry-contact-changed";
            if(b.Decision.CurrentCombatDecision!=r.Decision)return "entry-action-replaced";
            context=r.Policy.Trace.Context;
        }
        if(context is "prepared" or "search" or "move")
        {
            if(e.IsVisible||b.EnemyController.VisibleEnemies.Count>0)return "entry-visible-contact-native-combat";
            if(b.BotOwner.Memory.IsUnderFire||b.Suppression.IsSuppressed||s.Fear.Current.Panic||b.Medical.TimeSinceShot<3)return "entry-native-priority";
        }
        return nativeEntryReady?"":"entry-native-hooks-unavailable";
    }
    private bool TryStartDoorEntry(BotComponent b,BotState s,DoorWork door,string context,bool prepared)
    {
        var r=s.Entry;r.NextProbe=Time.time+.25f;
        if(r.Policy.Active||!r.Policy.Available(door.Geometry.Id,Time.time)||config?.DoorEntry.Enabled!=true||!FakeScope(b))return false;
        string gate=EntryGate(b,s,context);
        if(gate.Length>0||DoorBusy(b,s,door.Geometry.Id))return RefuseDoorEntry(b,s,door,context,gate.Length>0?gate:"entry-door-reserved");
        if(b.DoorOpener.Interacting||b.BotOwner.DoorOpener.Interacting||JumpInteractionState(b)||!NativeEntryAllowed(door.Data.Door,door.Data.Door.GetBreakInParameters(b.Position).InteractionPosition)
            ||fikaPresent&&door.Data.Door.ForceLocalInteraction
            ||b.Player.Physical.Stamina.NormalValue*100<config!.DoorEntry.MinimumStamina)return RefuseDoorEntry(b,s,door,context,"entry-door-or-resources-unavailable");
        if(!doorBudget.TryBegin(Time.frameCount,b))return false;
        doorClock.Restart();
        try
        {
            var e=b.GoalEnemy!;r.Start=b.Position;r.Door=door;r.Owner=b;r.Prepared=prepared;r.Contact=e.KnownPlaces.LastKnownPosition!.Value;
            r.EnemyInstance=System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(e);r.Decision=b.Decision.CurrentCombatDecision;
            string geometry=PlanEntry(b,r);
            if(geometry.Length>0){r.Drop();return RefuseDoorEntry(b,s,door,context,geometry);}
            double? parent=prepared||s.ActiveLabel.Length>0?s.LastSelection+config.PlanLimit:null;
            if(s.Reservation.Lease!=null)parent=Math.Min(parent??double.PositiveInfinity,s.Reservation.Lease.Deadline);
            double deadline=DoorEntry.Budget(Time.time,Math.Min(30,config.ContactLifetime)-e.TimeSinceLastKnownUpdated,parent,config.DoorEntry);
            if(deadline<=Time.time){r.Drop();return RefuseDoorEntry(b,s,door,context,"entry-original-budget-ended");}
            var previous=r.Policy.Trace;
            bool selected=r.Policy.Begin(door.Geometry.Id,e.EnemyProfileId,context,Time.time,deadline,jumpRandom.NextDouble(),config.DoorEntry);
            if(ReferenceEquals(previous,r.Policy.Trace)){r.Drop();return false;}
            var t=r.Policy.Trace!;t.Source="sain-last-known";t.KnownX=r.Contact.x;t.KnownY=r.Contact.y;t.KnownZ=r.Contact.z;t.ContactAge=e.TimeSinceLastKnownUpdated;
            t.Stamina=b.Player.Physical.Stamina.NormalValue*100;
            t.Network=fikaPresent?"Fika native StartInteraction (host only)":"solo native interaction";
            if(!selected){RecordDoorEntry(s,true);r.Drop();return false;}
            if(!AcquireDoorReservation(b,s,door,"entry:approaching",r.Policy.Deadline)){r.Policy.End("entry-reservation-unavailable");RecordDoorEntry(s,true);r.Drop();return false;}
            r.LastSafety=Time.time;
            if(!IssueEntryMove(b,r,r.Approach!,false)){EndDoorEntry(s,"entry-approach-refused");return false;}
            RecordDoorEntry(s,true);return true;
        }
        finally{doorClock.Stop();doorBudget.Charge(doorClock.Elapsed.TotalMilliseconds);}
    }
    private bool RefuseDoorEntry(BotComponent b,BotState s,DoorWork door,string context,string why)
    {
        if(s.Entry.Policy.Refuse(door.Geometry.Id,b.GoalEnemy?.EnemyProfileId??"",context,Time.time,why,config!.DoorEntry))
        {
            var t=s.Entry.Policy.Trace!;t.Network=fikaPresent?"Fika native interaction":"solo native interaction";t.Stamina=b.Player.Physical.Stamina.NormalValue*100;
            t.Source="sain-last-known";var e=b.GoalEnemy;if(e?.KnownPlaces.LastKnownPosition is Vector3 known){t.KnownX=known.x;t.KnownY=known.y;t.KnownZ=known.z;t.ContactAge=e.TimeSinceLastKnownUpdated;}
            RecordDoorEntry(s,true);
        }
        return false;
    }
    private string PlanEntry(BotComponent b,DoorEntryRuntime r)
    {
        var g=r.Door!.Geometry;var normal=VP(g.Normal)*(Vector3.Dot(r.Start-VP(g.Center),VP(g.Normal))>=0?1:-1);
        var origin=VP(g.Center);r.Forward=-normal;
        var nativePoint=r.Door.Data.Door.GetBreakInParameters(b.Position).InteractionPosition;
        if(!NativeEntryAllowed(r.Door.Data.Door,nativePoint))return "entry-native-side-refused";
        if(!NavMesh.SamplePosition(nativePoint,out var a,.3f,NavMesh.AllAreas)||!NavMesh.SamplePosition(origin-normal*2.5f,out var z,.3f,NavMesh.AllAreas))return "entry-no-navmesh-lane";
        r.InteractionPoint=a.position;r.Exit=z.position;
        if(!DoorEntry.Lane(g,DP(r.InteractionPoint),DP(r.Exit),DP(r.Start)))return "entry-bad-alignment";
        if(!b.Mover.CanGoToPoint(r.InteractionPoint,out var approach,true,.1f)||Length(approach.corners)>6)return "entry-no-approach-route";
        var through=new NavMeshPath();NavMesh.CalculatePath(r.InteractionPoint,r.Exit,NavMesh.AllAreas,through);
        if(through.status!=NavMeshPathStatus.PathComplete||through.corners.Length<2||Length(through.corners)>7)return "entry-no-through-route";
        r.Approach=approach;r.Through=through;
        foreach(var p in through.corners)if(Mathf.Abs(Vector3.Dot(p-origin,VP(g.Axis)))>g.HalfWidth-.2||Mathf.Abs(p.y-origin.y)>.6)return "entry-route-leaves-door-corridor";
        foreach(var p in approach.corners)if(Vector3.Dot(p-origin,normal)<.2)return "entry-approach-crosses-door";
        return EntrySafety(b,r);
    }
    private string EntrySafety(BotComponent b,DoorEntryRuntime r)
    {
        var door=r.Door!.Data.Door;
        if(door==null||!TryDoorGeometry(r.Door.Data,out var g)||(g.Center-r.Door.Geometry.Center).Length>.1)return "entry-door-frame-changed";
        if(door.DoorState!=EDoorState.Shut&&door.DoorState!=EDoorState.Open&&door.DoorState!=EDoorState.Breaching&&door.DoorState!=EDoorState.Interacting)return "entry-door-unavailable";
        try
        {
            foreach(var path in new[]{r.Approach!,r.Through!})
            {
                var corners=path.corners;
                for(int n=0;n<corners.Length;n++)
                {
                    var point=corners[n];var check=TacticalDoorClearance(b,point+Vector3.up*.4f,point+Vector3.up*1.45f,.25f,door,default);
                    if(check.Length>0)return "entry-approach-or-exit-"+check;
                    if(n==0)continue;var delta=point-corners[n-1];
                    check=TacticalDoorClearance(b,corners[n-1]+Vector3.up*.4f,corners[n-1]+Vector3.up*1.45f,.25f,door,delta);
                    if(check.Length>0)return "entry-corridor-"+check;
                }
            }
            // Check the native leaf arc; do not reject the
            // entire room merely because a large enclosing sphere touches the hinge wall.
            float swing=1; // Native opening direction only; never reverse the door.
            for(int sample=1;sample<=6;sample++)
            {
                var rotation=door.GetDoorRotation(Mathf.Lerp(door.CloseAngle,door.OpenAngle*swing,sample/6f));
                var edge=rotation*WorldInteractiveObject.GetRotationAxis(door.DoorForward,door.transform);
                var hinge=door.transform.position;hinge.y=(float)g.Center.Y+1;
                var check=TacticalDoorClearance(b,hinge+edge*.25f,hinge+edge*(float)(g.HalfWidth*1.8),.08f,door,default);
                if(check.Length>0)return "entry-swing-"+check;
            }
            return "";
        }
        finally{Array.Clear(doorOccupants,0,doorOccupants.Length);Array.Clear(jumpHits,0,jumpHits.Length);}
    }
    private static bool IssueEntryMove(BotComponent b,DoorEntryRuntime r,NavMeshPath path,bool sprint)
    {
        r.Issuing=true;
        try{bool accepted=sprint?b.Mover.RunToPointByWay(path,true,.3f,ESprintUrgency.High,false):b.Mover.WalkToPointByWay(path,true,.3f,false);
            r.OwnedPath=b.Mover.ActivePath;return accepted&&r.OwnedPath!=null;}
        finally{r.Issuing=false;}
    }
    private void RefreshDoorEntry(BotComponent b,BotState s)
    {
        var r=s.Entry;if(!r.Policy.Active||r.Frame==Time.frameCount)return;r.Frame=Time.frameCount;
        try
        {
            string gate=EntryGate(b,s);if(gate.Length>0){EndDoorEntry(s,gate);return;}
            if(!r.Policy.Tick(Time.time)){FinishDoorEntry(s);return;}
            if(!GuardDoorReservation(b,s)||!r.Policy.Active)return;
            var t=r.Policy.Trace!;t.ContactAge=b.GoalEnemy!.TimeSinceLastKnownUpdated;var door=r.Door!.Data.Door;
            if(t.State!="interacting"&&(r.OwnedPath==null||b.Mover.ActivePath!=null&&!ReferenceEquals(b.Mover.ActivePath,r.OwnedPath)
                ||b.Mover.ActivePath==null&&r.OwnedPath.Status!=EBotMoveStatus.Complete)){EndDoorEntry(s,"entry-route-replaced");return;}
            if(Time.time-r.LastSafety>=.2f)
            {
                if(!doorBudget.TryBegin(Time.frameCount,b)){if(Time.time-r.LastSafety>.5f)EndDoorEntry(s,"entry-safety-budget-expired");return;}
                doorClock.Restart();try{gate=EntrySafety(b,r);r.LastSafety=Time.time;}finally{doorClock.Stop();doorBudget.Charge(doorClock.Elapsed.TotalMilliseconds);}
                if(gate.Length>0){EndDoorEntry(s,gate);return;}
            }
            if(t.State=="approaching"&&Vector3.Distance(b.Position,r.InteractionPoint)<=.4f)
            {
                var data=r.Door.Data;data.ManualUpdate(b.Position);
                if(!NativeEntryAllowed(door,b.Position)||!data.Link.ShallInteract()||!data.InRangeToInteract(door)||!data.CanInteractByTime(Time.time)
                    ||b.DoorOpener.Interacting||b.BotOwner.DoorOpener.Interacting||JumpInteractionState(b))
                {EndDoorEntry(s,"entry-native-request-refused");return;}
                b.Player.MovementContext.ResetCanUsePropState();
                var result=Door.Interact(b.Player,EInteractionType.Breach);
                if(!result.Succeeded||!r.Policy.Request(Time.time,b.Player.Physical.Stamina.NormalValue*100,door.DoorState==EDoorState.Shut,config!.DoorEntry))
                {EndDoorEntry(s,"entry-native-request-refused");return;}
                r.OwnedPath?.RequestEndSprint(ESprintUrgency.None,"COMET native door interaction");r.OwnedPath?.Cancel();r.OwnedPath=null;
                RecordDoorEntry(s,true);
                // Same native branch as official SAIN. Fika overrides StartInteraction and sends
                // its normal reliable WorldInteraction packet. Never call KickOpen or set door state.
                b.Player.StartInteraction(door,result.Value,null);
            }
            if(door.DoorState==EDoorState.Open)
            {
                bool finished=!JumpInteractionState(b)&&b.Player.CurrentState.Name!=EPlayerState.BreachDoor
                    &&!b.DoorOpener.Interacting&&!b.BotOwner.DoorOpener.Interacting&&FakeWeaponReady(b);
                bool crossing=t.State=="crossing";
                r.Policy.Opened(finished,Time.time,config!.DoorEntry);
                if(!r.Policy.Active){FinishDoorEntry(s);return;}
                if(!crossing&&t.State=="crossing"&&!IssueEntryMove(b,r,r.Through!,true)){EndDoorEntry(s,"entry-crossing-refused");return;}
            }
            if(t.State=="crossing")r.Policy.Movement(Vector3.Dot(b.Position-VP(r.Door!.Geometry.Center),r.Forward)>.4f,Vector3.Distance(b.Position,r.Exit)<=.5f,Time.time);
            if(!r.Policy.Active){FinishDoorEntry(s);return;}
            if(t.State!="interacting")b.Steering.LookToPoint(r.Exit+Vector3.up*1.2f);
            CompleteDoorReservationStep(b,s,false);RecordDoorEntry(s);
        }
        catch(Exception ex){r.Fault=true;EndDoorEntry(s,"entry-adapter-error");Logger.LogWarning("D15 yielded: "+ex.GetBaseException().Message);}
    }
    private void EndDoorEntry(BotState s,string why){if(!s.Entry.Policy.Active)return;s.Entry.Policy.End(why);FinishDoorEntry(s);}
    private void FinishDoorEntry(BotState s)
    {
        var r=s.Entry;
        if(r.Owner!=null&&r.OwnedPath!=null&&ReferenceEquals(r.Owner.Mover.ActivePath,r.OwnedPath))
        {r.OwnedPath.RequestEndSprint(ESprintUrgency.None,"COMET entry ended");r.OwnedPath.Cancel();}
        RecordDoorEntry(s,true);bool prepared=r.Prepared;r.Drop();
        if(prepared)EndDoorClear(s,"clear-entry-ended:"+r.Policy.Trace?.Reason);
        ReleaseDoorReservation(s,"reservation-entry-ended");
    }
    private void RecordDoorEntry(BotState s,bool force=false)
    {
        var r=s.Entry;var t=r.Policy.Trace;if(t==null)return;string key=t.State+":"+t.Reason+":"+t.BreachObserved;
        if(!force&&key==r.LogKey&&Time.time-r.LogAt<.5f)return;r.LogKey=key;r.LogAt=Time.time;
        var json=Diagnostics.Serialize(new{schema=1,session,sequence=++sequence,feature="D15",stage="evaluated",decision="DoorEntry",reason=t.Reason,
            realtime=Time.realtimeSinceStartup,utc=DateTime.UtcNow.ToString("O"),configRevision=config?.Revision??0,profileId=s.ProfileId,name=s.Name,botInstance=s.Id,
            doorEntry=t,doorEntryConfig=config?.DoorEntry});
        diagnostics?.Write(json);Api.Decisions.Publish(json,ex=>{if(!subscriberWarned){subscriberWarned=true;Logger.LogWarning(ex.Message);}});
    }
}
