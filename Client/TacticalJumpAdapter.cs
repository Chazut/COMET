using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using Comet.Core;
using EFT;
using HarmonyLib;
using Newtonsoft.Json;
using SAIN.Components;
using SAIN.Components.PlayerComponentSpace;
using SAIN.Models.Enums;
using SAIN.Preset.Shared.Enums;
using SAIN.SAINComponent.Classes;
using SAIN.SAINComponent.Classes.Mover;
using SAIN.SAINComponent.Classes.WeaponFunction;
using UnityEngine;

namespace Comet.Client;

internal sealed class TacticalJumpRuntime
{
    internal readonly TacticalJumpPolicy Policy=new();
    internal bool Fault;
    internal float NextRush,LogAt=-1000;
    internal string LogKey="";
}
public sealed partial class Plugin
{
    private static void PatchTacticalJump(Harmony h)
    {
        var rush=typeof(BotComponent).Assembly.GetType("SAIN.Layers.Combat.Solo.RushEnemyAction",true);
        foreach(string name in new[]{"checkJump","checkJumpEnemyInSight"})
            h.Patch(AccessTools.Method(rush,name)??throw new MissingMethodException(name),
                transpiler:new HarmonyMethod(typeof(Plugin),nameof(RushJumpCalls)));
        h.Patch(AccessTools.Method(typeof(SAINShootData),"ShootWhenAimComplete")??throw new MissingMethodException("M12 native shooting"),
            prefix:new HarmonyMethod(typeof(Plugin),nameof(JumpAimedFire)));
        h.Patch(AccessTools.Method(typeof(ManualShootClass),nameof(ManualShootClass.TryShoot)),
            prefix:new HarmonyMethod(typeof(Plugin),nameof(JumpManualFire)));
        h.Patch(AccessTools.Method(typeof(PlayerComponent),nameof(PlayerComponent.OnMakingShot)),
            postfix:new HarmonyMethod(typeof(Plugin),nameof(JumpObservedShot)));
    }
    // Only Rush's tactical calls. Never intercept global TryJump (unstuck and vault systems use it too).
    private static IEnumerable<CodeInstruction> RushJumpCalls(IEnumerable<CodeInstruction> instructions,MethodBase __originalMethod)
    {
        var result=new List<CodeInstruction>();int count=0;
        var original=AccessTools.Method(typeof(SAINMoverClass),nameof(SAINMoverClass.TryJump));
        var replacement=AccessTools.Method(typeof(Plugin),nameof(NativeRushJump));
        foreach(var instruction in instructions) {
            if(instruction.Calls(original)){instruction.opcode=OpCodes.Call;instruction.operand=replacement;count++;}
            result.Add(instruction);
        }
        int expected=__originalMethod.Name=="checkJump"?1:2;
        if(count!=expected)throw new InvalidOperationException("M12 unexpected official Rush jump call count: "+count);
        return result;
    }
    private bool JumpScope(BotComponent b,BotState s)=>ContactSafety.AllowsGoal(b)&&config?.Enabled==true&&Authority&&!Ghost(s.ProfileId)
        &&NativeAdapter.IsSupportedAi(b)&&!b.IsDead&&b.BotActive&&!b.GameEnding&&b.SAINLayersActive;
    private TacticalJumpFrame JumpFrame(BotComponent b,BotState s,string source,string kind,Vector3 direction,Vector3 landing,double minSpeed,double minStamina,bool clearance,double age)
    {
        var v=b.Player.Velocity;v.y=0;direction.y=0;
        string block=!JumpScope(b,s)?"jump-ineligible":b.Player.IsInPronePose||b.Decision.CurrentSelfDecision!=ESelfActionType.None
            ||b.DoorOpener.Interacting||b.BotOwner.DoorOpener.Interacting||!VisibleAdapter.WeaponReady(b)?"jump-native-priority":"";
        return new(){Now=Time.time,Feature=source,Kind=kind,Target=b.GoalEnemy?.EnemyProfileId??"",Action=b.Decision.CurrentCombatDecision.ToString(),Block=block,
            Speed=v.magnitude,ForwardSpeed=Vector3.Dot(v,direction.normalized),MinSpeed=minSpeed,Stamina=b.Player.Physical.Stamina.NormalValue*100,
            MinStamina=minStamina,Grounded=b.Player.MovementContext.IsGrounded,CanJump=ChaseCanJump(b),Clearance=clearance,GeometryAge=age,
            Position=DP(b.Position),Landing=DP(landing)};
    }
    private bool ControlledJump(BotComponent b,BotState s,string source,string kind,Vector3 direction,Vector3 landing,double minSpeed,double minStamina,bool clearance,double age=0,string clearanceDetail="")
    {
        if(config==null||!JumpScope(b,s)||MedicalPostureGuard(b))return false;
        if(!config.TacticalJump.Enabled||s.TacticalJump.Fault)return b.Mover.TryJump();
        try {
            SampleTacticalJump(b,s);
            var r=s.TacticalJump;var f=JumpFrame(b,s,source,kind,direction,landing,minSpeed,minStamina,clearance,age);
            f.ClearanceDetail=clearanceDetail;
            if(!r.Policy.Request(f,config.TacticalJump,jumpRandom.NextDouble)){RecordTacticalJump(s);return false;}
            RecordTacticalJump(s,true);
            bool accepted=b.Mover.TryJump();r.Policy.Result(accepted,Time.time);
            SampleTacticalJump(b,s);RecordTacticalJump(s,true);return accepted;
        }catch(Exception ex){TacticalJumpError(s,ex);return false;}
    }
    private static bool NativeRushJump(SAINMoverClass mover)
    {
        var p=Instance;var b=mover.Bot;
        if(MedicalPostureGuard(b))return false;
        if(p?.config==null||!p.config.Enabled||!p.config.TacticalJump.Enabled||!p.config.TacticalJump.NativeRush
            ||!NativeAdapter.IsSupportedAi(b)||!p.Authority||p.Ghost(b.ProfileId))return mover.TryJump();
        if(!p.states.TryGetValue(b,out var s))return mover.TryJump();
        if(s.TacticalJump.Fault)return mover.TryJump();
        try {
            p.SampleTacticalJump(b,s);var r=s.TacticalJump;
            if(Time.time<r.NextRush)return false;r.NextRush=Time.time+.1f;
            var direction=b.Player.Velocity;direction.y=0;direction=direction.normalized;
            float length=Mathf.Clamp(b.Player.Velocity.magnitude*.7f,3,6);var landing=b.Position+direction*length;
            var f=p.JumpFrame(b,s,"SAIN","rush",direction,landing,p.config.TacticalJump.RushMinSpeed,0,true,0);
            if(r.Policy.Active||TacticalJumpPolicy.Gate(f,p.config.TacticalJump).Length>0) {
                r.Policy.Request(f,p.config.TacticalJump,p.jumpRandom.NextDouble);p.RecordTacticalJump(s);return false;
            }
            string clearanceDetail="geometry-budget-deferred";
            if(p.doorBudget.TryBegin(Time.frameCount)) {
                var watch=System.Diagnostics.Stopwatch.StartNew();
                try{clearanceDetail=p.WeaveLaneBlock(b,direction,length,true);}finally{watch.Stop();p.doorBudget.Charge(watch.Elapsed.TotalMilliseconds);}
            }
            return p.ControlledJump(b,s,"SAIN","rush",direction,landing,p.config.TacticalJump.RushMinSpeed,0,clearanceDetail.Length==0,0,clearanceDetail);
        }catch(Exception ex){p.TacticalJumpError(s,ex);return false;}
    }
    private void SampleTacticalJump(BotComponent b,BotState s)
    {
        var r=s.TacticalJump;if(r.Fault||config==null)return;
        if(!JumpScope(b,s)||!config.TacticalJump.Enabled){EndTacticalJump(s,"jump-ineligible");return;}
        if(!r.Policy.Active)return;
        var v=b.Player.Velocity;v.y=0;
        bool changed=r.Policy.Observe(b.Player.MovementContext.IsGrounded,Time.time,DP(b.Position),v.magnitude,b.Player.Physical.Stamina.NormalValue*100,config.TacticalJump);
        if(r.Policy.HoldFire)b.Shoot.EndShoot();
        RecordTacticalJump(s,changed);
    }
    private static bool JumpFireAllowed(BotComponent b)
    {
        var p=Instance;if(p==null||!p.states.TryGetValue(b,out var s))return true;
        try{p.SampleTacticalJump(b,s);return !s.TacticalJump.Policy.HoldFire;}
        catch(Exception ex){p.TacticalJumpError(s,ex);return true;}
    }
    private static bool JumpAimedFire(SAINShootData __instance)=>JumpFireAllowed(__instance.Bot);
    private static bool JumpManualFire(ManualShootClass __instance,ref bool __result)
    {if(JumpFireAllowed(__instance.Bot))return true;__result=false;return false;}
    private static void JumpObservedShot(PlayerComponent __instance)
    {
        var p=Instance;var b=__instance.BotComponent;
        if(p==null||b==null||!p.states.TryGetValue(b,out var s)||s.TacticalJump.Fault)return;
        try {
            if(!p.JumpScope(b,s))return;
            s.TacticalJump.Policy.Shot(b.Player.MovementContext.IsGrounded,Time.time);p.RecordTacticalJump(s);
        }catch(Exception ex){p.TacticalJumpError(s,ex);}
    }
    private void EndTacticalJump(BotState s,string reason)
    {var r=s.TacticalJump;bool active=r.Policy.Active;r.Policy.Clear(reason);if(active)RecordTacticalJump(s,true);}
    private void TacticalJumpError(BotState s,Exception ex)
    {if(s.TacticalJump.Fault)return;s.TacticalJump.Fault=true;EndTacticalJump(s,"jump-adapter-error");Logger.LogWarning("M12 yielded: "+ex.GetBaseException().Message);}
    private void RecordTacticalJump(BotState s,bool force=false)
    {
        var r=s.TacticalJump;var t=r.Policy.Trace;if(t==null||t.Target.Length==0)return;
        string key=t.State+":"+t.Reason+":"+t.SourceFeature+":"+t.Kind+":"+t.Target+":"+t.AirShots+":"+t.GroundShots+":"+t.ClearanceDetail;
        if(!force&&key==r.LogKey&&(!r.Policy.Active||Time.time-r.LogAt<.1f))return;r.LogKey=key;r.LogAt=Time.time;
        var json=Diagnostics.Serialize(new{schema=1,session,sequence=++sequence,feature="M12",stage="evaluated",decision="TacticalJump",reason=t.Reason,
            realtime=Time.realtimeSinceStartup,utc=DateTime.UtcNow.ToString("O"),configRevision=config?.Revision??0,profileId=s.ProfileId,name=s.Name,botInstance=s.Id,
            tacticalJump=t,tacticalJumpConfig=config?.TacticalJump});
        diagnostics?.Write(json);Api.Decisions.Publish(json,ex=>{if(!subscriberWarned){subscriberWarned=true;Logger.LogWarning(ex.Message);}});
    }
}
