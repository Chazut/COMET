using System;
using Comet.Core;
using SAIN.Components;
using SAIN.Models.Enums;
using SAIN.Preset.Shared.Enums;
using UnityEngine;
using UnityEngine.AI;

namespace Comet.Client;

public sealed partial class Plugin
{
    private static HandoffCue HandoffContact(Situation? s) => s == null ? new HandoffCue() : new HandoffCue {
        Target=s.Target, Visible=s.Visible, At=s.HasContact?s.Now-s.ContactAge:-1, X=s.KnownX,Y=s.KnownY,Z=s.KnownZ };

    private void YieldToSain(BotState state,string reason,string kind="unavailable")
    {
        bool changed=state.Handoff.Begin(Time.time,reason,kind,HandoffContact(state.Situation));
        if(kind=="error")state.FaultUntil=Time.time+(float)state.Handoff.Current.RetryRemaining;
        // Only COMET references are released. Native movement, hands and animations are left intact.
        try { Release(state,reason); }
        finally { state.GrenadeReady=false;state.GrenadeTarget=default;state.NextGrenadeProbe=0; }
        if(changed)Emit(state,"released",reason,"A13");
    }

    private bool KeepSafePath(BotComponent bot,BotState state,Situation s,ref ECombatDecision result,ref bool succeeded)
    {
        string action=state.Handoff.LastAction;
        bool move=action=="Search"||action=="Push";
        var tactic=action=="Search"?Tactic.Search:Tactic.Push;
        bool temporary=move && s.Unavailable.TryGetValue(tactic,out var why) && why=="no-current-complete-path";
        if(!temporary){state.Handoff.Healthy(Time.time);return false;}
        var path=bot.Mover.ActivePath;
        var progress=state.Progress.Current;
        // Same continuation gates, but retain why an existing movement could not
        // be kept while SAIN refreshes its route to the known contact.
        string unsafeReason=state.Feature!="A01" || state.ActiveLabel!="COMET:"+action || state.Decision!=action?"no-owned-hidden-plan":
            bot.Decision.CurrentCombatDecision!=state.Expected || !bot.SAINLayersActive || bot.CurrentAction==null?"native-action-changed":
            s.Visible?"contact-visible":s.Hurt?"injured":s.LowAmmo?"low-ammo":s.Suppressed?"suppressed":s.Fear?.Panic==true?"panic":
            !(bot.Medical.TimeSinceShot>=1.5f)?"recent-hit":!VisibleAdapter.WeaponReady(bot)?"weapon-not-ready":
            bot.BotOwner.DoorOpener.Interacting?"door-interaction":path==null?"no-active-route":
            path.PathStatus!=NavMeshPathStatus.PathComplete?"active-route-incomplete":path.Status!=EBotMoveStatus.Moving?"active-route-not-moving":
            state.ProgressUnavailable || !config!.Progress.Enabled?"progress-unavailable":progress.Reason!="progress-moving"?"movement-not-confirmed":
            progress.Reconsider?"progress-reconsider":!(Time.time-progress.At>=0 && Time.time-progress.At<=.5)?"progress-stale":
            !(progress.StalledSeconds<1)?"movement-stalled":
            state.Progress.Blocked(ProgressAdapter.Destination(path.Destination),Time.time) || state.Progress.Blocked(ProgressAdapter.GoalKey(bot),Time.time)?"route-blocked":"";
        bool wasGrace=state.Handoff.Current.State=="grace";
        if(!state.Handoff.TryGrace(Time.time,HandoffContact(s),temporary,unsafeReason.Length==0,unsafeReason))
        {
            if(state.Handoff.ReportPathCheck(Time.time))Emit(state,"evaluated","handoff-path-not-retained","A13");
            return false;
        }
        state.RequestedFrame=Time.frameCount; result=state.Expected; succeeded=true;
        if(!wasGrace)Emit(state,"evaluated","handoff-safe-path-grace","A13");
        return true;
    }
}
