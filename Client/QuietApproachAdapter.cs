using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using Comet.Core;
using HarmonyLib;
using Newtonsoft.Json;
using SAIN.Components;
using SAIN.Layers;
using SAIN.Models.Enums;
using SAIN.Preset.Shared.Enums;
using SAIN.SAINComponent.Classes.Decision;
using SAIN.SAINComponent.Classes.EnemyClasses;
using SAIN.SAINComponent.Classes.Mover;
using SAIN.Classes.Bot.Search;
using UnityEngine;
using UnityEngine.AI;

namespace Comet.Client;

public sealed partial class Plugin
{
    private BotComponent? quietCaller;
    private static void PatchQuietApproach(Harmony h)
    {
        var move=AccessTools.TypeByName("SAIN.Layers.Combat.Solo.MoveToEngageAction") ?? throw new MissingMemberException("P02 native approach action");
        var search=AccessTools.TypeByName("SAIN.Layers.Combat.Solo.SearchAction") ?? throw new MissingMemberException("P02 native search action");
        h.Patch(AccessTools.Method(move,"Update"),prefix:new HarmonyMethod(typeof(Plugin),nameof(QuietActionUpdate)),finalizer:new HarmonyMethod(typeof(Plugin),nameof(QuietActionFinally)));
        h.Patch(AccessTools.Method(search,"Update"),prefix:new HarmonyMethod(typeof(Plugin),nameof(QuietActionUpdate)),finalizer:new HarmonyMethod(typeof(Plugin),nameof(QuietActionFinally)));
        h.Patch(AccessTools.Method(typeof(SearchClass),"Search"),prefix:new HarmonyMethod(typeof(Plugin),nameof(QuietSearch)));
        h.Patch(AccessTools.Method(typeof(SAINMoverClass),"SetTargetMoveSpeed"),prefix:new HarmonyMethod(typeof(Plugin),nameof(QuietSpeed)));
        h.Patch(AccessTools.Method(typeof(SAINMoverClass),"SetTargetPose"),prefix:new HarmonyMethod(typeof(Plugin),nameof(QuietPose)));
        // Only MoveToEngage's run attempt is refused; its existing walk fallback executes immediately.
        h.Patch(AccessTools.Method(typeof(SAINMoverClass),"RunToPoint"),prefix:new HarmonyMethod(typeof(Plugin),nameof(QuietRun)));
        h.Patch(AccessTools.Method(typeof(BotDecisionManager),"SetDecisions"),postfix:new HarmonyMethod(typeof(Plugin),nameof(QuietDecisionChanged)));
        h.Patch(AccessTools.Method(typeof(EnemyEvents),nameof(EnemyEvents.EnemyHeard),new[]{typeof(SAINSoundType),typeof(bool),typeof(EnemyPlace)}),
            postfix:new HarmonyMethod(typeof(Plugin),nameof(QuietLootHeard)));
    }
    private static string QuietAction(BotComponent b)=>b==null?"":b.CurrentAction?.GetType().Name switch { "SearchAction"=>"Search","MoveToEngageAction"=>"MoveToEngage",_=>"" };
    private bool QuietScoped(BotComponent b)=>ContactSafety.AllowsGoal(b)&&config?.Enabled==true && config.QuietApproach.Enabled && NativeAdapter.IsSupportedAi(b)
        && b.BotActive && !b.GameEnding && Authority && !Ghost(b.ProfileId) && b.SAINLayersActive;
    private static string QuietUnsafe(BotComponent b,Enemy? enemy)
    {
        if(enemy==null)return "quiet-no-contact";
        if(enemy.IsVisible)return "quiet-visible-or-recent-fight";
        if(b.Decision.CurrentSquadDecision==ESquadDecision.Help)return "quiet-help-squad";
        if(enemy.IsSniper&&b.Info.PersonalitySettings.General.ENEMYSNIPER_ALWAYS_SPRINT_SEARCH)return "quiet-sniper-sprint";
        return "";
    }
    private QuietTrace? ReadQuiet(BotComponent bot,Enemy? enemy=null,bool force=false)
    {
        if(bot==null)return null;
        try
        {
            enemy ??= QuietAction(bot)=="Search"?bot.Search.SearchTarget:bot.GoalEnemy;
            if(!QuietScoped(bot)||QuietAction(bot).Length==0)
            {if(states.TryGetValue(bot,out var old))StopQuiet(bot,old,"quiet-out-of-scope");return null;}
            if(!states.TryGetValue(bot,out var s)){s=new BotState(bot,config!,session);states.Add(bot,s);}
            if(s.Entry.Policy.Active||s.RoomClear.Policy.Active||s.Fake.Policy.Active||s.StepPeek.Policy.Active||s.Jump.Policy.Active||s.RunBy.Policy.Active){StopQuiet(bot,s,"quiet-door-jump-plan");return null;}
            if(s.QuietFault)return null;
            string unsafeReason=QuietUnsafe(bot,enemy);
            if(unsafeReason.Length>0){StopQuiet(bot,s,unsafeReason);return null;}
            if(!force && Time.time<s.NextQuietSample && s.Quiet.Current.Target==enemy!.EnemyProfileId && s.QuietTargetInstance==RuntimeHelpers.GetHashCode(enemy))return s.Quiet.Current;
            s.QuietTargetInstance=RuntimeHelpers.GetHashCode(enemy!);
            s.NextQuietSample=Time.time+.25f;
            var f=CaptureQuiet(bot,enemy!,s);
            var previous=s.Quiet.Current;
            var t=s.Quiet.Evaluate(f);
            if(!t.Active)RestoreQuiet(bot,s);
            else {t.Applied=previous.Active && previous.Applied && previous.State==t.State && previous.Target==t.Target;t.NativeSpeed=previous.NativeSpeed;t.AppliedSpeed=previous.AppliedSpeed;
                t.NativePose=previous.NativePose;t.AppliedPose=previous.AppliedPose;t.ValidFor=t.Applied?1.5:0;}
            RecordQuiet(s);
            return t;
        }
        catch(Exception ex){QuietError(bot,ex);return null;}
    }
    private QuietFrame CaptureQuiet(BotComponent b,Enemy e,BotState s)
    {
        var known=e.KnownPlaces.LastKnownPosition;var p=b.Position;
        bool complete=e.Path.PathToEnemy?.status==NavMeshPathStatus.PathComplete;
        double length=e.Path.PathLength;bool threat=e.EnemyKnown,otherVisible=false;
        return new QuietFrame {Now=Time.time,Scoped=true,Enabled=true,Target=e.EnemyProfileId,TargetInstance=RuntimeHelpers.GetHashCode(e),
            Action=QuietAction(b),Personality=b.Info.Personality.ToString(),Source=NativeAdapter.Capture(b,e,true,false).ContactSource,
            Age=e.TimeSinceLastKnownUpdated,HasContact=known.HasValue,HasThreat=threat,Path=length,Complete=complete,
            Visible=e.IsVisible,OtherVisibleThreat=otherVisible,SinceSeen=e.Seen?e.TimeSinceSeen:double.MaxValue,
            SinceFire=Time.time-b.BotOwner.Memory.UnderFireTime,
            Suppressed=b.Suppression.IsHeavySuppressed,Panic=s.Fear.Current.Panic,Urgent=false,
            Blocked=false,
            LootHeard=e.Status.VulnerableAction==EEnemyAction.Looting,Executing=s.Quiet.Current.Applied,X=p.x,Y=p.y,Z=p.z};
    }
    private OpportunityConfig QuietLootConfig()=>new OpportunityConfig {Enabled=true,LootWindow=config!.QuietApproach.LootWindow,
        ReactionDelay=0,SafetyMargin=0,RearmSilence=config.QuietApproach.ResetSilence};
    private static void QuietLootHeard(Enemy ___Enemy,SAINSoundType __0,EnemyPlace __2)
    {
        var p=Instance;if(p?.config?.Enabled!=true||!p.config.QuietApproach.Enabled)return;
        try
        {
            var b=___Enemy.Bot;
            if(!p.QuietScoped(b)||__2==null||!ReferenceEquals(__2,___Enemy.KnownPlaces.LastHeardPlace))return;
            string cue=__0 switch{SAINSoundType.Looting=>"looting",SAINSoundType.Shot or SAINSoundType.SuppressedShot=>"shot",
                SAINSoundType.Sprint=>"sprint",SAINSoundType.GrenadeDraw or SAINSoundType.GrenadePin=>"grenade",_=>""};
            if(cue.Length==0)return;
            if(!p.states.TryGetValue(b,out var s)){s=new BotState(b,p.config,p.session);p.states.Add(b,s);}
            var pos=__2.Position;s.QuietLoot.Hear(___Enemy.EnemyProfileId,RuntimeHelpers.GetHashCode(___Enemy),Time.time,cue,true,pos.x,pos.y,pos.z,p.QuietLootConfig());
        }
        catch(Exception ex){p.QuietError(___Enemy.Bot,ex);}
    }
    private static void QuietActionUpdate(BotAction __instance,ref BotComponent? __state)
    {
        var p=Instance;if(p==null)return;var b=__instance.Bot;
        __state=p.quietCaller;p.quietCaller=b;
        var t=p.ReadQuiet(b);
        if(t?.Active==true)
        {
            try
            {
                // These are native requests before our caps; do not repeatedly multiply the previous cap.
                if(p.states.TryGetValue(b,out var s) && s.QuietOwnSpeed)b.Mover.SetTargetMoveSpeed(s.QuietNativeSpeed);
                if(p.states.TryGetValue(b,out s) && s.QuietOwnPose)b.Mover.SetTargetPose(s.QuietNativePose);
                if(b.Mover.Running){b.Mover.ActivePath?.RequestEndSprint(ESprintUrgency.None,"COMET quiet approach");p.QuietApplied(b);}
            }
            catch(Exception ex){p.QuietError(b,ex);}
        }
    }
    private static Exception? QuietActionFinally(BotComponent? __state,Exception? __exception)
    {if(Instance!=null)Instance.quietCaller=__state;return __exception;}
    private static void QuietSearch(SearchClass __instance,ref bool shallSprint,Enemy enemy)
    {
        var p=Instance;var t=p?.ReadQuiet(__instance.Bot,enemy);
        if(t?.Active==true && shallSprint){shallSprint=false;p!.QuietApplied(__instance.Bot);}
    }
    private static void QuietSpeed(SAINMoverClass __instance,ref float speed)
    {
        var p=Instance;if(p==null||__instance.Bot==null||!p.states.TryGetValue(__instance.Bot,out var s)||s.QuietRestoring)return;
        try
        {
            var t=p.ReadQuiet(__instance.Bot);s.QuietOwnSpeed=false;s.QuietNativeSpeed=speed;
            if(t?.Active!=true)return;
            float result=Math.Min(speed,(float)t.SpeedCap);t.NativeSpeed=speed;t.AppliedSpeed=result;
            if(result<speed){speed=result;s.QuietOwnSpeed=true;p.QuietApplied(__instance.Bot);}
        }
        catch(Exception ex){p.QuietError(__instance.Bot,ex);}
    }
    private static void QuietPose(SAINMoverClass __instance,ref float pose)
    {
        var p=Instance;if(p==null||__instance.Bot==null||!p.states.TryGetValue(__instance.Bot,out var s)||s.QuietRestoring)return;
        try
        {
            var t=p.ReadQuiet(__instance.Bot);s.QuietOwnPose=false;s.QuietNativePose=pose;
            if(t?.Active!=true)return;
            float result=Math.Min(pose,(float)t.PoseCap);t.NativePose=pose;t.AppliedPose=result;
            if(result<pose){pose=result;s.QuietOwnPose=true;p.QuietApplied(__instance.Bot);}
        }
        catch(Exception ex){p.QuietError(__instance.Bot,ex);}
    }
    private static bool QuietRun(SAINMoverClass __instance,ref bool __result)
    {
        var p=Instance;if(p==null||!ReferenceEquals(p.quietCaller,__instance.Bot)||QuietAction(__instance.Bot)!="MoveToEngage")return true;
        var t=p.ReadQuiet(__instance.Bot);if(t?.Active!=true)return true;
        __result=false;p.QuietApplied(__instance.Bot);return false;
    }
    private static void QuietDecisionChanged(BotDecisionManager __instance)
    {
        var p=Instance;if(p==null||!p.states.TryGetValue(__instance.Bot,out var s))return;
        try {if(QuietUnsafe(__instance.Bot,__instance.Bot.GoalEnemy).Length>0)p.StopQuiet(__instance.Bot,s,"quiet-native-priority");}
        catch(Exception ex){p.QuietError(__instance.Bot,ex);}
    }
    private void QuietApplied(BotComponent b)
    {
        if(!states.TryGetValue(b,out var s))return;
        s.Quiet.Current.Applied=true;s.Quiet.Current.ValidFor=1.5;RecordQuiet(s);
    }
    private void RestoreQuiet(BotComponent b,BotState s)
    {
        if(s.QuietRestoring)return;
        s.QuietRestoring=true;
        try {if(b!=null && !b.IsDead){if(s.QuietOwnSpeed)b.Mover.SetTargetMoveSpeed(s.QuietNativeSpeed);if(s.QuietOwnPose)b.Mover.SetTargetPose(s.QuietNativePose);}}
        finally{s.QuietOwnSpeed=s.QuietOwnPose=s.QuietRestoring=false;}
        // Sprint is not forcibly restarted. Native SAIN chooses it again when appropriate.
    }
    private void StopQuiet(BotComponent b,BotState s,string reason)
    {
        bool changed=s.Quiet.Current.Active||s.Quiet.Current.Applied;
        s.Quiet.Stop(Time.time,reason);RestoreQuiet(b,s);if(changed)RecordQuiet(s,true);
    }
    private void RefreshQuiet(BotComponent b,BotState s)
    {
        if(QuietAction(b).Length>0){ReadQuiet(b);return;}
        StopQuiet(b,s,"quiet-other-action");
        // Let encounter memory expire only after genuine absence of known threats.
        bool threat=false;int n=0;foreach(var e in b.EnemyController.KnownEnemies)
        {if(++n>32){threat=true;break;}if(e!=null&&e.KnownPlaces.LastKnownPosition.HasValue&&e.TimeSinceLastKnownUpdated<=config!.ContactLifetime){threat=true;break;}}
        var p=b.Position;s.Quiet.Evaluate(new QuietFrame{Now=Time.time,HasThreat=threat,X=p.x,Y=p.y,Z=p.z});
    }
    private void QuietError(BotComponent b,Exception ex)
    {
        if(b==null)return;
        if(!states.TryGetValue(b,out var s))return;
        s.QuietFault=true;
        try{StopQuiet(b,s,"quiet-adapter-error");}catch{/* Do not break native movement on cleanup/telemetry failure. */}
        if(!s.QuietWarned){s.QuietWarned=true;Logger.LogWarning("COMET P02 inactive for this bot: "+ex.Message);}
    }
    private void RecordQuiet(BotState s,bool force=false)
    {
        var t=s.Quiet.Current;
        string key=t.Target+":"+t.State+":"+t.Reason+":"+t.Applied+":"+t.Draw;
        if(!force && !t.Applied && Time.time-s.QuietLogAt<1)return;
        if(!force && key==s.QuietLogKey && (!t.Applied || Time.time-s.QuietLogAt<1))return;
        s.QuietLogKey=key;s.QuietLogAt=Time.time;
        var json=Diagnostics.Serialize(new {schema=1,session,sequence=++sequence,feature="P02",stage="evaluated",decision="QuietApproach",reason=t.Reason,
            realtime=Time.realtimeSinceStartup,utc=DateTime.UtcNow.ToString("O"),cometVersion=CometVersion.Release,configRevision=config?.Revision??0,
            quietConfig=config?.QuietApproach,profileId=s.ProfileId,name=s.Name,botInstance=s.Id,quiet=t});
        diagnostics?.Write(json);Api.Decisions.Publish(json,ex=>{if(!subscriberWarned){subscriberWarned=true;Logger.LogWarning(ex.Message);}});
    }
}
