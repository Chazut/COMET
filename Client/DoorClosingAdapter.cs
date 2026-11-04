using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Comet.Core;
using Comfort.Common;
using EFT;
using EFT.Interactive;
using HarmonyLib;
using Newtonsoft.Json;
using SAIN.Components;
using SAIN.SAINComponent.Classes.Mover;
using UnityEngine;

namespace Comet.Client;

internal sealed class DoorClosingHistory
{
    internal readonly Dictionary<string,float> Opened=new();
    internal float Retry;
}
internal sealed class DoorClosingAttempt
{
    internal DoorDataStruct Data;
    internal BotComponent Bot=null!;
    internal BotState State=null!;
    internal IBotPathData Path=null!;
    internal object? Action;
    internal float Started,PathStarted,Next;
    internal int Frame;
    internal long Generation;
    internal WorldInteractiveObject.InteractionState? Interaction;
    internal bool Requested;
}
internal sealed class DoorClosingLog {internal readonly Dictionary<string,float> Times=new();}

public sealed partial class Plugin
{
    private ConditionalWeakTable<Door,DoorClosingHistory> closingHistory=new();
    private ConditionalWeakTable<BotComponent,DoorClosingLog> closingLogs=new();
    private readonly Dictionary<BotComponent,DoorClosingAttempt> closingSelected=new();
    private readonly List<DoorClosingAttempt> closingPending=new();
    private static void PatchDoorClosing(Harmony h)
    {
        h.Patch(AccessTools.Method(typeof(DoorOpener),nameof(DoorOpener.SelectDoor)),
            postfix:new HarmonyMethod(typeof(Plugin),nameof(ClosingSelect)){priority=Priority.Last});
        h.Patch(AccessTools.Method(typeof(DoorOpener),nameof(DoorOpener.TryInteractWithDoor)),
            prefix:new HarmonyMethod(typeof(ClosingInteractionHook),nameof(ClosingInteractionHook.Before)){priority=Priority.Last},
            finalizer:new HarmonyMethod(typeof(ClosingInteractionHook),nameof(ClosingInteractionHook.After)));
        h.Patch(AccessTools.Method(typeof(WorldInteractiveObject),"InitializeSmoothOpenInteraction"),
            finalizer:new HarmonyMethod(typeof(Plugin),nameof(ClosingOpened)));
    }
    // Harmony keys __state by declaring type. C02 already owns a different capture on this same method.
    private static class ClosingInteractionHook
    {
        internal static bool Before(DoorOpener __instance,EInteractionType __0,DoorDataStruct __2,ref bool __result,out DoorClosingAttempt? __state)
            =>ClosingBefore(__instance,__0,__2,ref __result,out __state);
        internal static void After(bool __result,DoorClosingAttempt? __state,Exception? __exception)=>ClosingAfter(__result,__state,__exception);
    }
    private bool ClosingScoped(BotComponent b)=>config?.Enabled==true&&Authority&&b!=null&&NativeAdapter.IsPmcAi(b)
        &&b.BotActive&&!b.IsDead&&!b.GameEnding&&!Ghost(b.ProfileId);
    private static void ClosingOpened(WorldInteractiveObject __instance,EDoorState __0,Exception? __exception)
    {
        var p=Instance;if(p==null||__exception!=null||__0!=EDoorState.Open||__instance is not Door door||!p.Authority)return;
        foreach(var pair in p.states)
            if(ReferenceEquals(pair.Key.Player,door.InteractingPlayer)&&p.ClosingScoped(pair.Key))
            {
                var history=p.closingHistory.GetValue(door,_=>new());
                // Store time independently of SAIN's rebuilt value-type door lists. No actor/world references.
                string group=ReservationSquad(pair.Key,pair.Value);
                if(history.Opened.Count>=16&&!history.Opened.ContainsKey(group))
                {
                    string oldest="";float at=float.MaxValue;
                    foreach(var stamp in history.Opened)if(stamp.Value<at){oldest=stamp.Key;at=stamp.Value;}
                    history.Opened.Remove(oldest);
                }
                history.Opened[group]=Time.time;return;
            }
    }
    private static void ClosingSelect(DoorOpener __instance,IBotPathData pathData,EInteractionType interactionType,DoorDataStruct currentDoor,ref bool __result)
    {
        var p=Instance;var b=__instance.Bot;if(p==null)return;
        p.closingSelected.Remove(b);
        if(!__result||__instance.Interacting||interactionType!=EInteractionType.Close||!p.ClosingScoped(b)||!p.states.TryGetValue(b,out var s))return;
        try
        {
            var a=new DoorClosingAttempt{Bot=b,State=s,Data=currentDoor,Path=pathData,PathStarted=pathData.TimeStarted,Action=b.CurrentAction,Frame=Time.frameCount};
            var why=p.ClosingAllowed(a,false);
            if(why.Length>0){__result=false;p.RecordDoorClosing(a,"rejected",why);return;}
            if(p.closingSelected.Count>=128||p.closingPending.Count>=32)
            {__result=false;p.RecordDoorClosing(a,"rejected","closing-capacity");return;}
            p.closingSelected[b]=a;p.RecordDoorClosing(a,"considered","closing-selected-for-check");
        }
        catch(Exception ex){__result=false;p.Logger.LogWarning("COMET C05 selection: "+ex.Message);}
    }
    private string ClosingAllowed(DoorClosingAttempt a,bool checkGeometry=true)
    {
        var b=a.Bot;var s=a.State;var d=a.Data.Door;
        if(!ClosingScoped(b))return "closing-authority-or-scope-ended";
        if(!config!.DoorClosing.Enabled)return "closing-disabled";
        if(d==null||d.GetType()!=typeof(Door)||!d.Operatable||!d.enabled||!d.gameObject.activeInHierarchy||d.NoInteractionsAllowed||d.DoorState!=EDoorState.Open)return "closing-door-unavailable";
        if(!DoorSyncReady||(fikaPresent&&d.ForceLocalInteraction))return "closing-fika-not-ready";
        if(!ReferenceEquals(a.Action,b.CurrentAction)||a.PathStarted!=a.Path.TimeStarted||!ReferenceEquals(a.Path,b.Mover.ActivePath)
            ||a.Path is not BotPathDataManual path||!path.Moving||path.CancelRequested||path.PausedRequested)return "closing-route-changed";
        if(SupportingDoor(s)!=null||s.Door.Policy.Planning||s.Interruption.Policy.Pending||s.Support!=null||RecoveryBusy(b,s,d))return "closing-tactical-priority";
        if(doorSwings.Exists(x=>x.Door==d)||DoorMotion.Reversed(d.GetAngle(EDoorState.Shut),d.GetAngle(EDoorState.Open),d.CurrentAngle))return "closing-motion-priority";
        var h=closingHistory.GetValue(d,_=>new());
        if(Time.time<h.Retry)return "closing-failed-retry";
        if(h.Opened.TryGetValue(ReservationSquad(b,s),out var opened)&&Time.time-opened<config.DoorClosing.OpenGrace)return "closing-squad-open-grace";
        if(!checkGeometry)return ""; // Pay for the sweep once, at the actual request, within the shared work budget.
        if(!doorBudget.TryBegin(Time.frameCount))return "closing-budget-deferred";
        var watch=System.Diagnostics.Stopwatch.StartNew();
        try
        {
            if(!TryDoorGeometry(a.Data,out var g))return "closing-geometry-unknown";
            var players=Singleton<GameWorld>.Instance?.AllAlivePlayersList;if(players==null||players.Count>256)return "closing-occupancy-unknown";
            foreach(var actor in players)
            {
                if(actor==null||actor==b.Player||Math.Abs(actor.Position.y-g.Center.Y)>2)continue;
                var offset=DP(actor.Position)-g.Center;
                if(offset.X*offset.X+offset.Z*offset.Z<config.DoorClosing.OccupiedRadius*config.DoorClosing.OccupiedRadius)return "closing-doorway-occupied";
            }
            if(!TransitCollider(b,path,d))return "closing-leaf-not-blocking";
            if(!ClosingRouteClear(a))return "closing-shut-leaf-blocks-route";
            if(!SwingClear(d,d.CurrentAngle,d.GetAngle(EDoorState.Shut),b,false))return "closing-sweep-occupied";
            return "";
        }
        finally{watch.Stop();doorBudget.Charge(watch.Elapsed.TotalMilliseconds);}
    }
    private bool ClosingRouteClear(DoorClosingAttempt a)
    {
        var d=a.Data.Door;if(!LeafFrame(d,out var local,out _)||a.Path.PathCorners==null||a.Path.PathCorners.Count>128
            ||a.Path.CurrentIndex<0||a.Path.CurrentIndex>=a.Path.PathCorners.Count)return false;
        local.y=0;
        var hinge=DP(d.transform.position);
        var tip=DP(d.transform.position+d.GetDoorRotation(d.GetAngle(EDoorState.Shut))*d.transform.parent.rotation*(local*2));
        var from=DP(a.Bot.Position);double remaining=3;
        for(int i=a.Path.CurrentIndex;i<a.Path.PathCorners.Count&&remaining>0;i++)
        {
            var to=DP(a.Path.PathCorners[i].Position);var delta=to-from;double length=delta.Length;
            if(length>remaining)to=from+delta*(remaining/length);
            if(DoorClosing.Blocks(from,to,hinge,tip))return false;
            remaining-=length;from=to;
        }
        return true;
    }
    private static bool ClosingBefore(DoorOpener __instance,EInteractionType __0,DoorDataStruct __2,ref bool __result,out DoorClosingAttempt? __state)
    {
        __state=null;var p=Instance;var b=__instance.Bot;
        // Only consume the exact movement request selected this frame. Tactical closes do not use this token.
        if(p==null||__0!=EInteractionType.Close||!p.closingSelected.TryGetValue(b,out var a))return true;
        p.closingSelected.Remove(b);
        if(a.Frame!=Time.frameCount||a.Data.Door!=__2.Door)return true;
        try
        {
            string why=p.ClosingAllowed(a);
            if(why.Length>0){__result=false;p.RecordDoorClosing(a,"rejected",why);return false;}
            __state=a;return true;
        }
        catch(Exception ex){__result=false;p.Logger.LogWarning("COMET C05 recheck: "+ex.Message);return false;}
    }
    private static void ClosingAfter(bool __result,DoorClosingAttempt? __state,Exception? __exception)
    {
        var p=Instance;if(p==null||__state==null)return;var a=__state;
        try
        {
            if(__exception!=null||!__result){p.ClosingFailed(a,"closing-interaction-failed");return;}
            a.Requested=true;a.Started=Time.time;a.Generation=p.recoveryEpochs.GetValue(a.Data.Door,_=>new()).Generation;
            a.Interaction=(WorldInteractiveObject.InteractionState)recoveryInteraction.GetValue(a.Data.Door)!;
            p.closingPending.Add(a);p.RecordDoorClosing(a,"requested","closing-interaction-started",true);
        }
        catch(Exception ex){p.ClosingFailed(a,"closing-observation-failed");p.Logger.LogWarning("COMET C05 request: "+ex.Message);}
    }
    private void ClosingFailed(DoorClosingAttempt a,string reason)
    {
        closingHistory.GetValue(a.Data.Door,_=>new()).Retry=Time.time+(float)(config?.DoorClosing.FailedRetry??10);
        RecordDoorClosing(a,"failed",reason,true);
    }
    private void UpdateDoorClosing()
    {
        // Selection and use occur within SAIN's TickPath. Never retain a stale unused selection.
        if(closingSelected.Count>0)closingSelected.Clear();
        for(int i=closingPending.Count-1;i>=0;i--)
        {
            var a=closingPending[i];var d=a.Data.Door;if(Time.time<a.Next)continue;a.Next=Time.time+.1f;
            try
            {
                if(d==null||!ClosingScoped(a.Bot)||!states.TryGetValue(a.Bot,out var s)||!ReferenceEquals(s,a.State)
                    ||!ReferenceEquals(a.Interaction,recoveryInteraction.GetValue(d))||a.Generation!=recoveryEpochs.GetValue(d,_=>new()).Generation
                    ||a.Interaction==null||a.Interaction.ResultState!=EDoorState.Shut)
                {RecordDoorClosing(a,"ended","closing-newer-interaction-or-owner-ended",true);closingPending.RemoveAt(i);continue;}
                if(d.DoorState==EDoorState.Shut&&Math.Abs(DoorMotion.Delta(d.CurrentAngle,d.GetAngle(EDoorState.Shut)))<=.5)
                {RecordDoorClosing(a,"confirmed","closing-shut-observed",true);closingPending.RemoveAt(i);continue;}
                if(Time.time-a.Started>config!.DoorRecovery.BotAnimationGrace+config.DoorRecovery.UncertaintyLimit)
                {ClosingFailed(a,"closing-confirmation-timeout");closingPending.RemoveAt(i);continue;}
                RecordDoorClosing(a,"requested","closing-awaiting-shut",true);
            }
            catch(Exception ex){closingPending.RemoveAt(i);Logger.LogWarning("COMET C05 observation: "+ex.Message);}
        }
    }
    private void RecordDoorClosing(DoorClosingAttempt a,string state,string reason,bool force=false)
    {
        var d=a.Data.Door;if(d==null)return;
        var l=closingLogs.GetValue(a.Bot,_=>new());string key=d.Id+state+reason;
        if(!force&&l.Times.TryGetValue(key,out var logged)&&Time.time-logged<1)return;
        if(l.Times.Count>=32&&!l.Times.ContainsKey(key))l.Times.Clear();l.Times[key]=Time.time;
        var h=closingHistory.GetValue(d,_=>new());h.Opened.TryGetValue(ReservationSquad(a.Bot,a.State),out var opened);
        double grace=h.Opened.ContainsKey(ReservationSquad(a.Bot,a.State))?Math.Max(0,(config?.DoorClosing.OpenGrace??6)-(Time.time-opened)):0;
        var pos=d.transform.position;
        var t=new DoorClosingTrace{State=state,Reason=reason,Door=d.Id,DoorState=d.DoorState.ToString(),GraceRemaining=grace,
            RetryRemaining=Math.Max(0,h.Retry-Time.time),OccupiedRadius=config?.DoorClosing.OccupiedRadius??1.5,
            ValidFor=state is "considered" or "requested"?.35:0,X=pos.x,Y=pos.y,Z=pos.z};
        var json=JsonConvert.SerializeObject(new{schema=1,session,sequence=++sequence,feature="C05",stage="evaluated",decision="DoorClosing",reason,
            realtime=Time.realtimeSinceStartup,utc=DateTime.UtcNow.ToString("O"),configRevision=config?.Revision??0,doorClosingConfig=config?.DoorClosing,
            profileId=a.State.ProfileId,name=a.State.Name,botInstance=a.State.Id,doorClosing=t},Diagnostics.Json);
        diagnostics?.Write(json);Api.Decisions.Publish(json,ex=>Logger.LogWarning(ex.Message));
    }
    private void ClearDoorClosing(){closingSelected.Clear();closingPending.Clear();closingHistory=new();closingLogs=new();}
}
