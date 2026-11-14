#if COMET_MAGAZINE_RECOVERY
using System;
using System.Linq;
using System.Threading.Tasks;
using Comfort.Common;
using Comet.Core;
using EFT;
using EFT.Interactive;
using EFT.InventoryLogic;
using SAIN;
using SAIN.Components;
using SAIN.Preset.Shared.Enums;
using SAIN.SAINComponent.Classes.Mover;
using UnityEngine;

namespace Comet.Client;

internal sealed class MagazineRecoveryRuntime
{
    internal readonly MagazineRecovery<LootItem> Pending=new();
    internal IBotPathData? Path;
    internal Task<IResult>? Transaction;
    internal bool Active,Picking,Ready;
    internal int Token;
    internal float NextMove,ProgressAt,BestDistance,PickupAt,TransactionAt;
}

public sealed partial class Plugin
{
    private void RememberDroppedMagazine(BotState s,LootItem item)
    {
        if(s.MagazineRecovery.Pending.Remember(item.Item.Id.ToString(),item))
            RecordMagazineRecovery(s,"queued","actual emergency reload drop",item.Item.Id.ToString());
    }

    internal static bool MagazineRecoveryActive(BotOwner owner)
    {
        var p=Instance;
        if(p==null||!SAINEnableClass.GetSAIN(owner.ProfileId,out var b)||b==null||!p.states.TryGetValue(b,out var s))return false;
        try{return p.CanRecoverMagazine(b,s);}
        catch(Exception ex){p.PauseMagazineRecovery(s,"recovery error: "+ex.GetBaseException().Message,true);return false;}
    }

    private bool CanRecoverMagazine(BotComponent b,BotState s)
    {
        var r=s.MagazineRecovery;
        // Observe an in-flight transaction even if the combat layer has taken over.
        if(r.Transaction?.IsCompleted==true)FinishMagazineTransaction(s);
        if(r.Pending.Count==0)return false;
        bool enabled=config?.Enabled==true&&config.QuickReload.Enabled&&config.QuickReload.RecoverDroppedMagazine;
        bool alive=world!=null&&!s.Retired&&NativeAdapter.IsSupportedAi(b)&&b.BotActive&&!b.IsDead&&!b.GameEnding;
        bool priority=b.Decision.CurrentCombatDecision!=ECombatDecision.None||b.Decision.CurrentSquadDecision!=ESquadDecision.None
            ||b.Decision.CurrentSelfDecision!=ESelfActionType.None||b.BotOwner.DoorOpener.Interacting||b.BotOwner.Medecine.Using;
        if(!MagazineRecovery<LootItem>.CanRecover(enabled,alive,Authority,Ghost(b.ProfileId),PostDanger(b),priority))
        {
            PauseMagazineRecovery(s,"combat or native action resumed",!enabled||b.IsDead||b.GameEnding);
            return false;
        }
        if(r.Transaction!=null)
        {
            if(Time.time-r.TransactionAt<=8)return true;
            // A stalled inventory response must not keep the bot in this layer forever.
            // Retain the task and the original item until its actual result is known.
            PauseMagazineRecovery(s,"inventory response delayed; yielding movement",false);
            return false;
        }
        var item=r.Pending.Item;
        if(item==null||item.Item==null||!item.gameObject.activeInHierarchy)
        {
            FinishMagazineRecovery(s,"unavailable","discarded magazine no longer in world");
            return false;
        }
        return true;
    }

    internal static void RunMagazineRecovery(BotOwner owner,bool active)
    {
        var p=Instance;
        if(p==null||!SAINEnableClass.GetSAIN(owner.ProfileId,out var b)||b==null||!p.states.TryGetValue(b,out var s))return;
        try
        {
            if(!active){p.PauseMagazineRecovery(s,"layer yielded",false);return;}
            if(!p.CanRecoverMagazine(b,s))return;
            p.ExecuteMagazineRecovery(b,s);
        }
        catch(Exception ex){p.FinishMagazineRecovery(s,"failed",ex.GetBaseException().Message);}
    }

    private void ExecuteMagazineRecovery(BotComponent b,BotState s)
    {
        var r=s.MagazineRecovery;
        if(r.Transaction!=null)return;
        var loot=r.Pending.Item!;
        var goal=loot.transform.position;
        float distance=Vector3.Distance(b.Position,goal);
        if(!r.Active)
        {
            r.Active=true;r.ProgressAt=Time.time;r.BestDistance=distance;r.NextMove=0;
            b.BotOwner.PatrollingData.Pause();
            RecordMagazineRecovery(s,"started","returning for discarded magazine");
        }
        if(distance<r.BestDistance-.2f){r.BestDistance=distance;r.ProgressAt=Time.time;}
        if(Time.time-r.ProgressAt>20&&!r.Picking)
        {FinishMagazineRecovery(s,"failed","no movement progress toward magazine");return;}
        if(distance>1.5f)
        {
            if(Time.time<r.NextMove)return;
            r.NextMove=Time.time+1;
            if(!b.Mover.WalkToPoint(goal)){FinishMagazineRecovery(s,"failed","native movement declined");return;}
            r.Path=b.Mover.ActivePath;
            b.Mover.SetTargetMoveSpeed(.8f);b.Mover.SetTargetPose(1f);
            return;
        }
        StopMagazineRoute(b,r);
        // Proximity alone must not collect a magazine through a wall or on another floor.
        var head=b.Position+Vector3.up*1.4f;
        if(Physics.Linecast(head,goal+Vector3.up*.05f,out var hit,LayerMask.GetMask("HighPolyCollider","Terrain","Door"),QueryTriggerInteraction.Ignore)
            &&hit.collider.GetComponentInParent<LootItem>()!=loot)
        {FinishMagazineRecovery(s,"failed","pickup obstructed: "+hit.collider.name);return;}
        if(!r.Picking)
        {
            r.Picking=true;r.Ready=false;r.PickupAt=Time.time;int token=++r.Token;
            b.Player.CurrentManagedState.Pickup(true,()=>{if(r.Token==token)r.Ready=true;});
            RecordMagazineRecovery(s,"arrived","pickup animation started");return;
        }
        if(!r.Ready)
        {if(Time.time-r.PickupAt>8)FinishMagazineRecovery(s,"failed","pickup animation did not finish");return;}
        var inventory=b.Player.InventoryController;
        var place=ItemManipulator.QuickFindAppropriatePlace(loot.Item,inventory,new[]{inventory.Inventory.Equipment},ItemManipulator.EMoveItemOrder.PickUp,true);
        if(!place.Succeeded){FinishMagazineRecovery(s,"failed","no compatible inventory space");return;}
        // Native transaction consumes the existing LootItem. No spawned or duplicated magazine.
        r.TransactionAt=Time.time;
        r.Transaction=inventory.TryRunNetworkTransaction(place,null);
    }

    private void FinishMagazineTransaction(BotState s)
    {
        var r=s.MagazineRecovery;var task=r.Transaction!;r.Transaction=null;
        bool confirmed=task.Status==TaskStatus.RanToCompletion&&task.Result.Succeed
            &&s.Owner.Player.Inventory.Equipment.GetAllItems().Any(i=>i.Id.ToString()==r.Pending.Id);
        if(task.IsFaulted)_=task.Exception;
        FinishMagazineRecovery(s,confirmed?"completed":"failed",confirmed?"original magazine confirmed in inventory":"native pickup transaction not confirmed");
    }

    private void FinishMagazineRecovery(BotState s,string stage,string reason)
    {
        var r=s.MagazineRecovery;
        RecordMagazineRecovery(s,stage,reason);
        PauseMagazineRecovery(s,reason,false,false);
        r.Pending.Finish();
    }

    private void PauseMagazineRecovery(BotState s,string reason,bool clear,bool emit=true)
    {
        var r=s.MagazineRecovery;
        if(r.Active&&emit)RecordMagazineRecovery(s,"interrupted",reason);
        r.Active=false;StopMagazineRoute(s.Owner,r);
        if(r.Picking){r.Picking=false;r.Ready=false;r.Token++;try{s.Owner.Player.CurrentManagedState.Pickup(false,null);s.Owner.Player.UpdateInteractionCast();}catch{}}
        if(clear)
        {
            // Observe late faults without retaining a bot/world in a continuation.
            if(r.Transaction!=null)r.Transaction.ContinueWith(t=>{_=t.Exception;},TaskContinuationOptions.OnlyOnFaulted);
            r.Transaction=null;r.Pending.Clear();
        }
    }

    private static void StopMagazineRoute(BotComponent b,MagazineRecoveryRuntime r)
    {
        if(r.Path is BotPathDataManual path&&ReferenceEquals(b.Mover.ActivePath,path)&&b.Player.MovementContext.IsGrounded)
            combatStopPath.Invoke(path,new object[]{false,"magazine recovery yielded"});
        r.Path=null;
    }

    private void RecordMagazineRecovery(BotState s,string stage,string reason,string? magazine=null)
    {
        var item=s.MagazineRecovery.Pending.Item;var at=item==null?(Vector3?)null:item.transform.position;
        var json=Diagnostics.Serialize(new{schema=1,session,sequence=++sequence,feature="W04",stage,decision="RecoverMagazine",reason,
            realtime=Time.realtimeSinceStartup,utc=DateTime.UtcNow.ToString("O"),configRevision=config?.Revision??0,
            profileId=s.ProfileId,name=s.Name,botInstance=s.Id,
            magazineRecovery=new{magazine=magazine??s.MagazineRecovery.Pending.Id,pending=s.MagazineRecovery.Pending.Count,x=at?.x,y=at?.y,z=at?.z}});
        diagnostics?.Write(json);Api.Decisions.Publish(json,ex=>{if(!subscriberWarned){subscriberWarned=true;Logger.LogWarning(ex.Message);}});
    }
}
#endif
