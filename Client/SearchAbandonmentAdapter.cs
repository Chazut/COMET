using System;
using System.Runtime.CompilerServices;
using Comet.Core;
using HarmonyLib;
using Newtonsoft.Json;
using SAIN.Components.BotComponentSpace.Classes.EnemyClasses;
using SAIN.Models.Enums;
using SAIN.Preset.Shared.Enums;
using SAIN.SAINComponent.Classes.EnemyClasses;
using UnityEngine;

namespace Comet.Client;

public sealed partial class Plugin
{
    private static readonly AccessTools.FieldRef<EnemyKnownChecker,Enemy> abandonmentEnemy = AccessTools.FieldRefAccess<EnemyKnownChecker,Enemy>("Enemy");
    private int abandonmentProbeFrame=-1,abandonmentProbes;
    private static void PatchSearchAbandonment(Harmony h)
    {
        h.Patch(AccessTools.Method(typeof(EnemyKnownChecker),"ShallKnowEnemy"),postfix:new HarmonyMethod(typeof(Plugin),nameof(CheckSearchedPlace)));
        h.Patch(AccessTools.Method(typeof(EnemyKnownChecker),nameof(EnemyKnownChecker.TickEnemy)),postfix:new HarmonyMethod(typeof(Plugin),nameof(SearchForgetObserved)));
    }
    private static bool NeedsAbandonment(EnemyKnownChecker checker,bool pending)
    {
        var p=Instance;if(p?.config==null)return false;
        var e=abandonmentEnemy(checker);
        if(!p.states.TryGetValue(e.Bot,out var s))return false;
        return pending ? s.AbandonPending?.Target==e.EnemyProfileId
            : ReferenceEquals(e.Bot.GoalEnemy,e)||s.Abandonment.Remembers(e.EnemyProfileId);
    }
    private static void CheckSearchedPlace(EnemyKnownChecker __instance,float currentTime,ref bool __result)
    {
        var p=Instance;if(p?.config==null)return;
        var e=abandonmentEnemy(__instance);var b=e.Bot;
        if(!p.states.TryGetValue(b,out var s))return;
        // Irrelevant contacts cannot invalidate the current inspection. Filter them
        // before repeated authority, ghost, hands and lifecycle reads.
        if(!ReferenceEquals(b.GoalEnemy,e)&&!s.Abandonment.Remembers(e.EnemyProfileId))return;
        try
        {
            if(s.AbandonFault||!p.config.Enabled||!p.config.SearchAbandonment.Enabled||!NativeAdapter.IsPmcAi(b)
                ||!b.BotActive||b.IsDead||b.GameEnding||!p.Authority||p.Ghost(b.ProfileId))
            {p.EndAbandonment(s,"search-check-out-of-scope",true);return;}
            if(s.Entry.Policy.Active||s.Fake.Policy.Active||s.StepPeek.Policy.Active||s.Jump.Policy.Active||s.RunBy.Policy.Active){p.EndAbandonment(s,"search-check-door-jump");return;}
            var place=e.KnownPlaces.LastKnownPlace;
            if(!__result||place==null){p.EndAbandonment(s,"search-check-native-expiry");return;}
            bool unsafeNow=b.Flashed.IsFlashed||e.IsVisible||b.EnemyController.VisibleEnemies.Count>0||b.BotOwner.Memory.IsUnderFire
                ||b.Suppression.IsSuppressed||b.Medical.TimeSinceShot<3||s.Fear.Current.Panic
                ||b.BotOwner.DoorOpener.Interacting||b.Decision.CurrentSelfDecision!=ESelfActionType.None
                ||b.Decision.CurrentSquadDecision!=ESquadDecision.None||b.Decision.CurrentCombatDecision==ECombatDecision.AvoidGrenade
                ||!VisibleAdapter.WeaponReady(b);
            if(unsafeNow) {p.EndAbandonment(s,"search-check-native-priority");if(e.IsVisible)s.Abandonment.ForgetFailure(e.EnemyProfileId);return;}
            if(currentTime<s.AbandonProbeAt)return;
            s.AbandonProbeAt=currentTime+.25f;
            var point=place.Position;var origin=b.Position;
            bool near=Vector3.Distance(point,origin)<=p.config.SearchAbandonment.Radius;
            // Conservative floor guard plus two independent sight lines to the remembered place.
            // No target transform, real enemy position, path search or scene enumeration.
            bool sameFloor=Math.Abs(point.y-origin.y)<=1;
            bool personal=b.Decision.CurrentCombatDecision==ECombatDecision.Search||place.HasArrivedPersonal;
            var viewOffset=point+Vector3.up-b.Transform.EyePosition;
            bool facing=viewOffset.sqrMagnitude<.25f||Vector3.Angle(b.Transform.LookDirection,viewOffset)<=Math.Min(60,b.Info.FileSettings.Core.VisibleAngle/2);
            bool sampled=false,clear=false;
            if(near&&sameFloor&&facing&&personal&&ReferenceEquals(b.GoalEnemy,e))
            {
                if(p.abandonmentProbeFrame!=Time.frameCount){p.abandonmentProbeFrame=Time.frameCount;p.abandonmentProbes=0;}
                if(p.abandonmentProbes<4)
                {
                    p.abandonmentProbes++;sampled=true;
                    clear=!Physics.Linecast(origin+Vector3.up*.6f,point+Vector3.up*.6f,LayersMaskController.HighPolyWithTerrainMask,QueryTriggerInteraction.Ignore)
                        &&!Physics.Linecast(origin+Vector3.up*1.2f,point+Vector3.up*1.2f,LayersMaskController.HighPolyWithTerrainMask,QueryTriggerInteraction.Ignore);
                }
            }
            var f=new SearchAbandonmentFrame{Now=currentTime,Stamp=e.KnownPlaces.TimeLastKnownUpdated,Target=e.EnemyProfileId,
                Instance=RuntimeHelpers.GetHashCode(e),X=point.x,Y=point.y,Z=point.z,Distance=Vector3.Distance(origin,point),
                NativeKnown=__result,Active=ReferenceEquals(b.GoalEnemy,e),PersonalSearch=personal,Visible=e.IsVisible,
                SameFloor=sameFloor,ClearView=clear,Sampled=sampled,Facing=facing};
            var t=s.Abandonment.Evaluate(f,p.config.SearchAbandonment);
            p.RecordAbandonment(s);
            if(t.ForgetRequested){s.AbandonPending=f;__result=false;}
        }
        catch(Exception ex){s.AbandonFault=true;p.EndAbandonment(s,"search-check-adapter-error",true);p.Logger.LogWarning("COMET P09 yielded: "+ex.Message);}
    }
    private static void SearchForgetObserved(EnemyKnownChecker __instance)
    {
        var p=Instance;if(p?.config==null)return;
        var e=abandonmentEnemy(__instance);
        if(!p.states.TryGetValue(e.Bot,out var s)||s.AbandonPending is not SearchAbandonmentFrame f||f.Target!=e.EnemyProfileId)return;
        s.AbandonPending=null;
        if(s.Abandonment.ObserveForgotten(f,p.config.SearchAbandonment,!e.EnemyKnown&&e.KnownPlaces.LastKnownPlace==null))p.RecordAbandonment(s,true);
    }
    private void EndAbandonment(BotState s,string reason,bool clear=false)
    {
        if(s.Abandonment.Current is SearchAbandonmentTrace t&&(t.State=="reached"||t.State=="checking"||t.State=="requested"))
        {s.Abandonment.Interrupt(reason);RecordAbandonment(s,true);}
        s.AbandonPending=null;
        if(clear)s.Abandonment.Clear();
    }
    private void RecordAbandonment(BotState s,bool force=false)
    {
        var t=s.Abandonment.Current;if(t==null)return;
        string key=t.Target+":"+t.State+":"+t.Reason;
        if(!force&&key==s.AbandonLogKey&&Time.time-s.AbandonLogAt<1)return;
        s.AbandonLogAt=Time.time;s.AbandonLogKey=key;
        var json=JsonConvert.SerializeObject(new{schema=1,session,sequence=++sequence,feature="P09",stage="evaluated",decision="SearchAbandonment",
            reason=t.Reason,realtime=Time.realtimeSinceStartup,utc=DateTime.UtcNow.ToString("O"),configRevision=config?.Revision??0,
            searchAbandonmentConfig=config?.SearchAbandonment,profileId=s.ProfileId,name=s.Name,botInstance=s.Id,searchAbandonment=t},Diagnostics.Json);
        diagnostics?.Write(json);Api.Decisions.Publish(json,ex=>{if(!subscriberWarned){subscriberWarned=true;Logger.LogWarning(ex.Message);}});
    }
}
