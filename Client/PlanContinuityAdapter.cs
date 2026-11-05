using Comet.Core;
using EFT;
using SAIN.Components;
using SAIN.Preset.Shared.Enums;
using UnityEngine;

namespace Comet.Client;

internal sealed class PlanServiceRuntime
{
    internal string Action = "", Feature = "", Plan = "", PlanFeature = "";
    internal bool Active => Action.Length > 0;
    internal void Clear() { Action = Feature = Plan = PlanFeature = ""; }
}

public sealed partial class Plugin
{
    // Native self actions are not automatically SAIN takeovers. H01/W01 can own
    // their choice while SAIN/EFT execute the animation and inventory operation.
    private string OwnedPlanService(BotComponent b, BotState s, bool committed)
    {
        if(config?.Enabled != true || world == null || s.Retired || !Authority || !NativeAdapter.IsPmcAi(b)
            || !b.BotActive || b.IsDead || b.GameEnding || ReadDowned(b.Player) || Ghost(b.ProfileId) || !b.SAINLayersActive
            || b.Grenade.GrenadeDangerPoint != null || b.Decision.CurrentCombatDecision == ECombatDecision.AvoidGrenade
            || b.Decision.DogFightDecision.DogFightActive || b.BotOwner.WeaponManager?.IsMelee == true) return "";
        var medical = s.Medical.Policy.Trace;
        if(config.MedicalChoice.Enabled && !s.Medical.Fault && s.Medical.Policy.Active && medical != null
            && MedicalAction(medical.Choice) != ESelfActionType.None
            && b.Decision.CurrentSelfDecision == MedicalAction(medical.Choice)
            && (committed || medical.Started && (b.BotOwner.Medecine?.Using == true || b.Player.HandsController is IMedsController)))
            return medical.Choice;
        var reload = s.ReloadTiming.Policy.Trace;
        if(config.ReloadTiming.Enabled && !s.ReloadTiming.Fault && s.ReloadTiming.Policy.Active && reload?.Choice == "Reload"
            && reload.Accepted && b.Decision.CurrentSelfDecision == ESelfActionType.Reload
            && b.Player.HandsController?.Item?.Id.ToString() == reload.Weapon
            && (committed || b.BotOwner.WeaponManager?.Reload.Reloading == true)) return "Reload";
        return "";
    }

    private bool ObservePlanService(BotComponent b, BotState s, bool committed)
    {
        string action = OwnedPlanService(b,s,committed);
        var r = s.PlanService;
        if(action.Length == 0)
        {
            if(r.Active)
            {
                ContinuityEvent(s,"service-revalidate","comet-service-ended-revalidate-plan",r.PlanFeature,r.Plan);
                r.Clear(); Api.Decisions.Set(s.ProfileId,"");
            }
            return false;
        }
        if(r.Action != action)
        {
            string plan = r.Active ? r.Plan : s.Decision, feature = r.Active ? r.PlanFeature : s.Feature;
            // The saved engine plan is only a candidate on resumption. All current
            // target, danger, path and capability checks still run in Choose.
            Release(s,"comet-service-pause",clearIntent:false,suspendPlan:true);
            r.Action = action; r.Feature = action == "Reload" ? "W01" : "H01";
            r.Plan = plan; r.PlanFeature = feature;
            s.Handoff.Clear();
            ContinuityEvent(s,"service-paused","comet-service-"+action,feature,plan);
        }
        s.RequestedFrame = -1;
        Api.Decisions.Set(s.ProfileId,"COMET:"+action);
        return true;
    }

    private void ContinuityEvent(BotState s,string phase,string reason,string? feature=null,string? action=null)
    {
        string key=phase+":"+reason+":"+(action??s.Decision);
        if(s.ContinuityKey==key && Time.time-s.ContinuityAt<1)return;
        s.ContinuityKey=key;s.ContinuityAt=Time.time;
        s.ContinuityTrace=new HandoffTrace{State=phase,Reason=reason,Kind="continuity",At=Time.time,
            FromFeature=feature??s.Feature,FromAction=action??s.Decision,
            NativeSelf=s.Owner.Decision.CurrentSelfDecision.ToString(),GraceRemaining=config?.Handoff.Grace??0};
        try{Emit(s,"evaluated",reason,"A13");}finally{s.ContinuityTrace=null;}
    }
}
