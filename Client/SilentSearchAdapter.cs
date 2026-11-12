using System;
using System.Runtime.CompilerServices;
using Comet.Core;
using HarmonyLib;
using Newtonsoft.Json;
using SAIN.Classes.Bot.Search;
using SAIN.Components;
using SAIN.Models.Enums;
using SAIN.Preset.Shared.Enums;
using SAIN.SAINComponent.Classes.Decision;
using SAIN.SAINComponent.Classes.EnemyClasses;
using UnityEngine;

namespace Comet.Client;

public sealed partial class Plugin
{
    // Stable native timestamp, not now-age repeatedly rounded into a new observation.
    private static readonly AccessTools.FieldRef<EnemyPlace,float> silentHeardAt = AccessTools.FieldRefAccess<EnemyPlace,float>("_timeLastUpdated");
    private static void PatchSilentSearch(Harmony h)
    {
        var search=AccessTools.Method(typeof(SearchClass),nameof(SearchClass.Search),new[]{typeof(bool),typeof(Enemy)});
        if(search==null)throw new MissingMethodException("P08 native Search(bool, Enemy)");
        h.Patch(search,prefix:new HarmonyMethod(typeof(Plugin),nameof(SilentSearchStep)),postfix:new HarmonyMethod(typeof(Plugin),nameof(SilentSearchExecuted)));
    }
    private SilentSearchTrace ReadSilentSearch(BotComponent b,Enemy e,BotState s,Situation situation)
    {
        var cfg=config ?? throw new InvalidOperationException("Missing authoritative configuration");
        var heard=e.KnownPlaces.LastHeardPlace;var known=e.KnownPlaces.LastKnownPlace;
        var personal=heard!=null&&e.Heard;
        var f=new SilentSearchFrame{
            Now=Time.time,Target=e.EnemyProfileId,Instance=RuntimeHelpers.GetHashCode(e),Personality=situation.Personality,
            Scoped=ContactSafety.AllowsGoal(b)&&!s.SilentFault&&cfg.Enabled&&NativeAdapter.IsSupportedAi(b)&&b.BotActive&&!b.IsDead&&!b.GameEnding&&Authority&&!Ghost(b.ProfileId),
            Personal=personal,Seen=e.Seen||e.IsVisible,HasContact=situation.HasContact,
            HeardAt=known!=null?silentHeardAt(known):0,ContactAge=situation.ContactAge,Lifetime=cfg.ContactLifetime,
            TimeBeforeSearch=b.Info.TimeBeforeSearch,
            X=personal?heard!.Position.x:0,Y=personal?heard!.Position.y:0,Z=personal?heard!.Position.z:0,
            SearchAllowed=b.Info.PersonalitySettings.Search.WillSearchForEnemy,AudioAllowed=b.Info.PersonalitySettings.Search.WillSearchFromAudio,
            Arrived=known?.HasArrivedPersonal==true||known?.HasArrivedSquad==true,
            Path=!situation.Unavailable.ContainsKey(Tactic.Search),
            Failed=false
        };
        var t=SilentSearchPolicy.Evaluate(f,cfg.SilentSearch);
        var previous=s.SilentTrace;
        bool same=previous!=null&&previous.Target==t.Target&&previous.Instance==t.Instance&&previous.HeardAt==t.HeardAt;
        if(!t.Eligible||!same) {s.SilentAccepted=s.SilentObserved=s.SilentMoved=false;}
        t.Accepted=s.SilentAccepted;t.ActionObserved=s.SilentObserved;t.MovementObserved=s.SilentMoved;
        t.Walking=same&&s.SilentAccepted&&previous!.Walking;
        s.SilentTrace=t;return t;
    }
    private void RecordSilentChoice(BotState s,Situation situation)
    {
        var t=situation.SilentSearch;if(t==null)return;
        t.Choice=s.Decision;
        var search=s.Candidates.Find(c=>c.Decision=="Search");
        t.SearchRefusal=search?.Eligible==true?"":search?.Reason??"no-search-candidate";
        if(t.Eligible&&search?.Eligible!=true){t.Eligible=false;t.Bonus=0;t.State="blocked";t.Reason=t.SearchRefusal;}
        if(s.Decision!="Search")
        {s.SilentAccepted=s.SilentObserved=s.SilentMoved=false;t.Accepted=t.ActionObserved=t.MovementObserved=t.Walking=false;}
        if(t.Eligible&&s.Decision=="Search")t.State=t.ActionObserved?"observed":t.Accepted?"accepted":"requested";
        s.SilentTrace=t;RecordSilent(s,t.Reconsider);
    }
    private void SilentDecisionObserved(BotDecisionManager d,BotState s)
    {
        var t=s.SilentTrace;if(t==null)return;
        bool accepted=s.Feature=="A01"&&s.Decision=="Search"&&s.RequestedFrame==Time.frameCount&&t.Eligible
            &&d.CurrentCombatDecision==ECombatDecision.Search&&d.CurrentSelfDecision==ESelfActionType.None&&d.CurrentSquadDecision==ESquadDecision.None;
        if(!accepted){if(s.SilentAccepted)EndSilentSearch(s,"silent-search-native-takeover");return;}
        if(s.SilentAccepted)return;
        s.SilentAccepted=t.Accepted=true;s.SilentStart=d.Bot.Position;t.State="accepted";RecordSilent(s,true);
    }
    private bool SilentExecuting(BotComponent b,Enemy e,out BotState s)
    {
        s=null!;
        if(config?.Enabled!=true||!config.SilentSearch.Enabled||b==null||e==null||!states.TryGetValue(b,out s!)||!s.SilentAccepted)return false;
        if(s.Fake.Policy.Active||s.StepPeek.Policy.Active||s.Jump.Policy.Active||s.RunBy.Policy.Active||s.Entry.Policy.Active)return false;
        if(s.ActiveLabel!="COMET:Search"||s.Feature!="A01"||b.Decision.CurrentCombatDecision!=ECombatDecision.Search
            ||b.CurrentAction?.GetType().Name!="SearchAction"||s.SilentTrace?.Target!=e.EnemyProfileId)
        {EndSilentSearch(s,"silent-search-action-changed");return false;}
        var situation=NativeAdapter.Capture(b,e,Authority,Ghost(b.ProfileId));
        // This read consumes existing native path data only; it does not run a scene/path search.
        var path=e.Path.PathToEnemy;
        if(path==null||path.status!=UnityEngine.AI.NavMeshPathStatus.PathComplete)situation.Unavailable[Tactic.Search]="no-current-complete-path";
        var t=ReadSilentSearch(b,e,s,situation);
        if(!t.Eligible){RecordSilent(s,true);return false;}
        t.Choice="Search";return true;
    }
    private static void SilentSearchStep(SearchClass __instance,ref bool shallSprint,Enemy enemy,out bool __state)
    {
        __state=false;
        var p=Instance;if(p==null)return;
        try{
            if(!p.SilentExecuting(__instance.Bot,enemy,out var s))return;
            shallSprint=false;
            if(__instance.Bot.Mover.Running)__instance.Bot.Mover.ActivePath?.RequestEndSprint(ESprintUrgency.None,"COMET P08 cautious search");
            __state=true;
        }catch(Exception ex){p.SilentError(__instance.Bot,ex);}
    }
    private static void SilentSearchExecuted(SearchClass __instance,Enemy enemy,bool __state,bool __runOriginal)
    {
        var p=Instance;if(!__runOriginal||!__state||p==null||!p.states.TryGetValue(__instance.Bot,out var s)||!s.SilentAccepted)return;
        try{
            var b=__instance.Bot;var t=s.SilentTrace;if(t==null||t.Target!=enemy.EnemyProfileId)return;
            bool first=!s.SilentObserved;s.SilentObserved=t.ActionObserved=true;t.State="observed";
            t.Walking=!b.Player.IsSprintEnabled&&!b.Mover.Running;
            var destination=new Vector3((float)t.X,(float)t.Y,(float)t.Z);
            s.SilentMoved|=ImpactPolicy.Moved(Vector3.Distance(s.SilentStart,destination),Vector3.Distance(b.Position,destination),Vector3.Distance(s.SilentStart,b.Position));
            t.MovementObserved=s.SilentMoved;p.RecordSilent(s,first);
        }catch(Exception ex){p.SilentError(__instance.Bot,ex);}
    }
    private void EndSilentSearch(BotState s,string reason)
    {
        if(s.SilentTrace!=null){s.SilentTrace.State="ended";s.SilentTrace.Reason=reason;s.SilentTrace.Eligible=false;s.SilentTrace.ValidFor=0;RecordSilent(s,true);}
        s.SilentTrace=null;s.SilentAccepted=s.SilentObserved=s.SilentMoved=false;
    }
    private void SilentError(BotComponent b,Exception ex)
    {
        if(!states.TryGetValue(b,out var s))return;
        s.SilentFault=true;EndSilentSearch(s,"silent-search-adapter-error");
        if(!s.SilentWarned){s.SilentWarned=true;Logger.LogWarning("COMET P08 yielded: "+ex.Message);}
    }
    private void RecordSilent(BotState s,bool force=false)
    {
        var t=s.SilentTrace;if(t==null)return;
        if(!force&&Time.time-s.SilentLogAt<1)return;
        // Waiting details have a short heartbeat for replay. No per-frame diagnostic serialization.
        s.SilentLogAt=Time.time;
        var json=Diagnostics.Serialize(new{schema=1,session,sequence=++sequence,feature="P08",stage="evaluated",decision="SilentSearch",
            reason=t.Reason,realtime=Time.realtimeSinceStartup,utc=DateTime.UtcNow.ToString("O"),configRevision=config?.Revision??0,
            silentSearchConfig=config?.SilentSearch,profileId=s.ProfileId,name=s.Name,botInstance=s.Id,silentSearch=t});
        diagnostics?.Write(json);Api.Decisions.Publish(json,ex=>{if(!subscriberWarned){subscriberWarned=true;Logger.LogWarning(ex.Message);}});
    }
}
