using System;
using Comet.Core;
using Newtonsoft.Json;
using SAIN.Components;
using UnityEngine;

namespace Comet.Client;

// Independent diagnostic boundary. Never enter C08's combat fault circuit.
public sealed partial class Plugin
{
    partial void ObserveControlJournal(BotComponent b,ControlWatch watch,string owner,string layer)
    {
        if(config?.ControlJournal.Enabled!=true||watch.JournalFailed||!Authority||world==null)return;
        try
        {
            var s=watch.State;
            bool suspended=owner=="sleep"||ReadDowned(b.Player)||s.Retired;
            var o=new ControlObservation{Owner=suspended?(owner=="sleep"?"sleep":ReadDowned(b.Player)?"downed":"removed"):owner,Layer=layer};
            if(!suspended)
            {
                var contact=b.GoalEnemy;
                o.Target=contact?.EnemyProfileId??"";o.Visible=contact?.IsVisible==true;
                o.ContactAt=contact==null?-1:Time.time-contact.TimeSinceLastKnownUpdated;
                o.UnderFire=b.BotOwner.Memory.IsUnderFire;
                o.Action=b.SAINLayersActive?b.CurrentAction?.GetType().Name??"unknown":"Layer action unobserved";
                // Use T01's same execution evidence. Layer ownership and COMET attribution are separate.
                string choice=s.Feature=="A03"?s.Decision=="ContinuePush"?"Push":s.Decision=="KeepAngle"?"Shoot":"":s.Decision;
                if(s.ActiveLabel.Length>0&&b.SAINLayersActive&&b.Decision.CurrentCombatDecision==s.Expected
                    &&b.Decision.CurrentSelfDecision.ToString()=="None"&&b.Decision.CurrentSquadDecision.ToString()=="None"&&OutcomeAction(b,choice))o.Feature=s.Feature;
                if(ControlHandoff.IsCometLayer(layer))o.Feature=layer=="COMET : Crossfire"?"S01":"H04";
                // These are observed states, not guesses about why another mod declined control.
                o.Blocker=b.BotOwner.Medecine?.Using==true?"medical-in-progress":
                    b.BotOwner.WeaponManager.Reload.Reloading?"reload-in-progress":
                    owner=="recovery"?"post-combat-recovery":owner=="combat"?"combat-active":
                    owner is "orbit" or "questing" or "looting" or "extraction" or "other"?"selected-layer-active":"unknown";
                if(s.ActiveLabel.Length>0)o.Residual="intent:"+s.ActiveLabel;
                if(s.Crossfire.Layer)o.Residual+=" crossfire-layer-record";
                if(s.PostCombat.Layer)o.Residual+=" recovery-layer-record";
                if(s.Reservation.Lease!=null&&s.Reservation.Lease.Until>Time.time)o.Residual+=" unexpired-door-lease-record";
            }
            foreach(var e in watch.Journal.Observe(Time.time,o,config.ControlJournal))
            {
                if(Time.time-watch.JournalBudgetAt>=1){watch.JournalBudgetAt=Time.time;watch.JournalEvents=0;}
                if(++watch.JournalEvents>16)
                {if(watch.JournalEvents==17)ControlJournalUnavailable("per-bot-event-limit");continue;}
                string json=JsonConvert.SerializeObject(new{schema=1,session,sequence=++sequence,feature="T04",stage="evaluated",decision="ControlJournal",
                    reason=e.Reason,profileId=s.ProfileId,name=s.Name,botInstance=s.Id,personality=b.Info.Personality.ToString(),
                    realtime=Time.realtimeSinceStartup,configRevision=config.Revision,controlJournal=e},Diagnostics.Json);
                diagnostics?.Write(json);Api.Decisions.Publish(json,_=>{});
            }
        }
        catch(Exception ex){watch.JournalFailed=true;ControlJournalUnavailable("bot-context");try{Logger.LogWarning("T04 diagnostic unavailable: "+ex.Message);}catch{}}
    }
    partial void ControlJournalUnavailable(string source)
    {
        if(config?.ControlJournal.Enabled!=true||world==null||!Authority)return;
        try{diagnostics?.Write(JsonConvert.SerializeObject(new{schema=1,session,feature="T04",stage="observer-unavailable",source},Diagnostics.Json));}catch{}
    }
}
