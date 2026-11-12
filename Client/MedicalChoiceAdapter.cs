using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using Comet.Core;
using EFT;
using EFT.Ballistics;
using EFT.HealthSystem;
using EFT.InventoryLogic;
using HarmonyLib;
using Newtonsoft.Json;
using SAIN.Components;
using SAIN.Preset.Shared.Enums;
using SAIN.SAINComponent.Classes.Decision;
using SAIN.SAINComponent.Classes.EnemyClasses;
using UnityEngine;

namespace Comet.Client;

internal sealed class MedicalRuntime
{
    internal readonly MedicalChoice Policy = new();
    internal readonly SurgeryInterruption Interruption = new();
    internal readonly SourceSurgery SourceSurgery = new();
    internal float InterruptionAt, InterruptionLogAt;
    internal string InterruptionKey = "";
    internal readonly List<Meds> Items = new();
    internal readonly Queue<(float at,Vector3 position)> Steps=new();
    internal WeakReference<Meds>? Item;
    internal EBodyPart Part;
    internal int Token;
    internal float Next, SampleAt, LogAt;
    internal double BeforeHealth, BeforeResource;
    internal bool BeforeDestroyed, Callback, Fault, SurgeryOwned;
    internal string Key = "";
}
public sealed partial class Plugin
{
    private static PropertyInfo surgeryBot = null!;
    private static readonly EBodyPart[] medicalParts = { EBodyPart.Head,EBodyPart.Chest,EBodyPart.Stomach,EBodyPart.LeftArm,EBodyPart.RightArm,EBodyPart.LeftLeg,EBodyPart.RightLeg };
    private static void PatchMedicalChoice(Harmony h)
    {
        h.Patch(AccessTools.Method(typeof(SelfActionDecisionClass),"StartBotHeal"),prefix:new HarmonyMethod(typeof(Plugin),nameof(ChooseMedical)));
        h.Patch(AccessTools.Method(typeof(SelfActionDecisionClass),"CheckContinueSelfAction"),prefix:new HarmonyMethod(typeof(Plugin),nameof(ContinueMedical)));
        foreach(var name in new[]{"DoFirstAid","DoStims","DoSurgery"})
            h.Patch(AccessTools.Method(typeof(SAINSelfActionClass),name),prefix:new HarmonyMethod(typeof(Plugin),nameof(ExecuteMedical)));
        h.Patch(AccessTools.Method(typeof(EnemyEvents),nameof(EnemyEvents.EnemyHeard),new[]{typeof(SAINSoundType),typeof(bool),typeof(EnemyPlace)}),postfix:new HarmonyMethod(typeof(Plugin),nameof(MedicalFootstep)));
        var surgery=typeof(SAINSelfActionClass).Assembly.GetType("SAIN.Layers.Combat.Solo.Cover.DoSurgeryAction",true)!;
        surgeryBot=AccessTools.Property(surgery,"Bot")??throw new MissingMemberException("H01 surgery bot");
        h.Patch(AccessTools.Method(surgery,"checkDoSurgery"),prefix:new HarmonyMethod(typeof(Plugin),nameof(ExecuteSurgery)));
        // Original Stop restores destroyed limbs for free. Suppress only for our native kit operations.
        h.Patch(AccessTools.Method(surgery,"Stop"),transpiler:new HarmonyMethod(typeof(Plugin),nameof(SurgeryStopTranspiler)),postfix:new HarmonyMethod(typeof(Plugin),nameof(SurgeryStopped)));
    }
    private static void MedicalFootstep(Enemy ___Enemy,SAINSoundType __0,EnemyPlace __2)
    {
        var p=Instance;var b=___Enemy.Bot;
        try {
        if(p==null||__0 is not (SAINSoundType.FootStep or SAINSoundType.Sprint)||!p.MedicalScope(b)||__2==null||!ReferenceEquals(__2,___Enemy.KnownPlaces.LastHeardPlace)||!ValidPoint(__2.Position))return;
        if(!p.states.TryGetValue(b,out var s)){if(p.states.Count>=512)return;s=new BotState(b,p.config!,p.session);p.states.Add(b,s);}
        var steps=s.Medical.Steps;while(steps.Count>0&&(steps.Count>=16||Time.time-steps.Peek().at>15))steps.Dequeue();steps.Enqueue((Time.time,__2.Position));
        }catch(Exception ex){if(p!=null&&p.states.TryGetValue(b,out var state))p.MedicalError(state,ex);}
    }
    private static IEnumerable<CodeInstruction> SurgeryStopTranspiler(IEnumerable<CodeInstruction> codes)
    {
        int count=0;
        foreach(var code in codes)
        {
            if(code.operand is MethodInfo m&&m.Name=="RestoreBodyPart"&&m.DeclaringType==typeof(ActiveHealthController))
            {count++;var load=new CodeInstruction(code){opcode=OpCodes.Ldarg_0,operand=null};yield return load;yield return new CodeInstruction(OpCodes.Call,typeof(Plugin).GetMethod(nameof(NativeRestoreAfterSurgery),BindingFlags.Static|BindingFlags.NonPublic)!);}
            else yield return code;
        }
        if(count!=1)throw new InvalidOperationException("H01 native surgery Stop changed");
    }
    private static bool NativeRestoreAfterSurgery(ActiveHealthController health,EBodyPart part,float penalty,object action)
    {
        var b=(BotComponent)surgeryBot.GetValue(action)!;var p=Instance;
        if(p!=null&&p.states.TryGetValue(b,out var s)&&s.Medical.SurgeryOwned)return false;
        return health.RestoreBodyPart(part,penalty); // Preserve the bool consumed by SAIN's original IL.
    }
    private static void SurgeryStopped(object __instance)
    {var p=Instance;var b=(BotComponent)surgeryBot.GetValue(__instance)!;if(p!=null&&p.states.TryGetValue(b,out var s))s.Medical.SurgeryOwned=false;}
    private bool MedicalScope(BotComponent b)=>config?.Enabled==true&&config.MedicalChoice.Enabled&&world!=null&&Authority
        &&b!=null&&NativeAdapter.IsSupportedAi(b)&&b.BotActive&&!b.IsDead&&!b.GameEnding&&!Ghost(b.ProfileId)&&(b.SAINLayersActive||PostCombatMedicalScope(b));
    private static ESelfActionType MedicalAction(string choice)=>choice=="FirstAid"?ESelfActionType.FirstAid:choice=="Surgery"?ESelfActionType.Surgery:choice=="Stims"?ESelfActionType.Stims:ESelfActionType.None;
    private string MedicalPriority(BotComponent b,BotState s)
    {
        if(!b.BotOwner.Settings.FileSettings.Mind.CAN_USE_MEDS)return "medical-disabled-by-sain";
        if(b.BotOwner.Medecine==null||b.BotOwner.Medecine.Using||b.Player.HandsController is IMedsController)return "medical-already-in-use";
        return "";
    }
    private static bool ChooseMedical(SelfActionDecisionClass __instance,ref ESelfActionType Decision,ref bool __result)
    {
        var p=Instance;var b=__instance.Bot;if(p==null||!p.MedicalScope(b))return true;
        BotState? s=null;
        try
        {
            if(!p.states.TryGetValue(b,out s)){if(p.states.Count>=512)return true;s=new BotState(b,p.config!,p.session);p.states.Add(b,s);}
            var r=s.Medical;if(r.Fault)return true;
            Decision=ESelfActionType.None;__result=false;
            if(r.Policy.Active){Decision=MedicalAction(r.Policy.Trace!.Choice);__result=true;return false;}
            if(Time.time<r.Next)return false;
            r.Next=Time.time+(float)p.config!.MedicalChoice.Interval;
            p.doorClock.Restart();
            try
            {
                var f=p.MedicalFrame(b,s);var items=new Dictionary<string,(Meds item,EBodyPart part)>();
                if(f.Priority.Length==0)p.MedicalSupplies(b,s,f,items);
                var t=r.Policy.Evaluate(f,p.config.MedicalChoice);
                if(t.Choice!="Wait"&&items.TryGetValue(t.Choice,out var chosen))
                {r.Item=new(chosen.item);r.Part=chosen.part;r.Callback=false;r.Token++;Decision=MedicalAction(t.Choice);__result=true;}
                p.RecordMedical(s,false);
            }
            finally{p.doorClock.Stop();p.doorBudget.Charge(p.doorClock.Elapsed.TotalMilliseconds);r.Items.Clear();}
            return false;
        }
        catch(Exception ex){if(s!=null)p.MedicalError(s,ex);return true;}
    }
    private MedicalFrame MedicalFrame(BotComponent b,BotState s)
    {
        var health=b.Player.HealthController;
        var f=new MedicalFrame{Now=Time.time,Health=health.GetBodyPartHealth(EBodyPart.Common,false).Normalized,
            SinceHit=Math.Max(0,b.Medical.TimeSinceShot),HeavyBleed=health.FindExistingEffect<IHeavyBleeding>()!=null,
            LightBleed=health.FindExistingEffect<ILightBleeding>()!=null,Priority=MedicalPriority(b,s),Moving=b.Player.Velocity.magnitude>.35f||b.Player.IsSprintEnabled};
        f.Vital=health.GetBodyPartHealth(EBodyPart.Head,false).Normalized<=.25||health.GetBodyPartHealth(EBodyPart.Chest,false).Normalized<=.25;
        if(config!.Aggression.TryGetValue(b.Info.Personality.ToString(),out var aggression))f.Aggression=aggression;
        f.ReachedCover=b.Cover.CoverInUse?.BotInThisCover==true||PostReachedCover(b,s);f.ProtectiveCover=f.ReachedCover;
        f.Health=b.Memory.Health.HealthStatus switch { ETagStatus.Injured=>.7, ETagStatus.BadlyInjured=>.35, ETagStatus.Dying=>.1, _=>1 };
        f.RunningToCover=!f.ReachedCover&&b.Player.Velocity.magnitude>.5f&&b.Decision.CurrentCombatDecision is ECombatDecision.SeekCover or ECombatDecision.Retreat or ECombatDecision.RunAway;
        f.LegsHold=!b.Player.MovementContext.CanSprint;
        Vector3? nearest=null,fresh=null;double freshDistance=double.MaxValue,freshAge=999;
        foreach(var e in b.EnemyController.KnownEnemies)
        {
            if(e?.KnownPlaces.LastKnownPosition is not Vector3 pos||e.TimeSinceLastKnownUpdated>120)continue;
            double age=e.TimeSinceLastKnownUpdated;
            double distance=Math.Min(e.Path.PathLength,Vector3.Distance(b.Position,pos)*1.3);
            f.Contacts++;f.Visible|=e.IsVisible||e.InLineOfSight;
            if(distance<f.Nearest){f.Nearest=distance;f.Quiet=age;f.Contact=e.EnemyProfileId;nearest=pos;}
            if(age<30&&distance<40&&distance<freshDistance){freshDistance=distance;freshAge=age;fresh=pos;}
        }
        if(f.Quiet>=30&&fresh.HasValue){nearest=fresh;f.Nearest=freshDistance;f.Quiet=freshAge;}
        if(nearest is Vector3 point)
        {
            f.X=point.x;f.Y=point.y;f.Z=point.z;
            f.InHisView=f.Quiet<30&&!Physics.Linecast(point+Vector3.up*1.5f,b.Position+Vector3.up*1.3f,LayersMaskController.HighPolyWithTerrainMask);
            var cover=b.Cover.CoverInUse;
            f.CoverTowardHim=!f.ReachedCover&&cover!=null&&f.Quiet<30&&Vector3.Distance(cover.Position,point)<Vector3.Distance(b.Position,point)-1.5f;
        }
        foreach(var step in s.Medical.Steps)if(Time.time>=step.at&&Time.time-step.at<=config.MedicalChoice.FootstepAge&&Vector3.Distance(b.Position,step.position)<=config.MedicalChoice.FootstepDistance)f.Footsteps=true;
        if(!MedicalChoice.Finite(f.Health)||!MedicalChoice.Finite(f.SinceHit))f.Priority="medical-invalid-state";
        return f;
    }
    private static bool MedicalUsable(BotComponent b,Meds item,EBodyPart part)
    {
        double resource=item.MedKitComponent?.HpResource??1;
        return resource>0&&MedicalChoice.Finite(resource)&&LoadoutAdapter.RootSlot(item,b.Player.Inventory.Equipment)!=null&&b.Player.HealthController.CanApplyItem(item,part);
    }
    private static bool Treats(Meds item,EDamageEffectType effect)
        =>item.HealthEffectsComponent.DamageEffects.TryGetValue(effect,out var spec)&&(spec.Cost<=0||item.MedKitComponent?.HpResource>=spec.Cost);
    private static bool UsefulStim(Meds item,bool bleeding)
    {
        double regen=0;bool stops=false;
        foreach(var buff in item.HealthEffectsComponent.BuffSettings)
        {
            if(buff.Chance<.5||buff.Duration<=0)continue;
            if(buff.BuffType==EStimulatorBuffType.HealthRate)regen+=buff.Value*buff.Duration;
            if(buff.BuffType==EStimulatorBuffType.RemoveAllBloodLosses&&buff.Delay<=5)stops=true;
        }
        return regen>0||bleeding&&stops;
    }
    private void MedicalSupplies(BotComponent b,BotState s,MedicalFrame f,Dictionary<string,(Meds item,EBodyPart part)> chosen)
    {
        var med=b.BotOwner.Medecine;var r=s.Medical;
        b.Player.InventoryController.GetAcceptableItemsNonAlloc<Meds>(LoadoutAdapter.Carried,r.Items);
        var best=new Dictionary<string,MedicalOption>();f.Meds=r.Items.Count;
        foreach(var item in r.Items)
        {
            var slot=LoadoutAdapter.RootSlot(item,b.Player.Inventory.Equipment);if(slot==null)continue;
            foreach(var part in medicalParts)
            {
                var hp=b.Player.HealthController.GetBodyPartHealth(part,false);
                bool heavy=b.Player.HealthController.FindExistingEffect<IHeavyBleeding>(part)!=null;
                bool light=b.Player.HealthController.FindExistingEffect<ILightBleeding>(part)!=null;
                bool destroyed=b.Player.HealthController.IsBodyPartDestroyed(part);
                string action="",need="";int rank=0;
                if(item is Stimulator)
                {
                    if(part!=EBodyPart.Chest||!med.Stimulators.CanUseNow()||Time.time-med.Stimulators.LastEndUseTime<3||!UsefulStim(item,f.HeavyBleed||f.LightBleed))continue;
                    if(b.BotOwner.Settings.FileSettings.Mind.MEDS_ONLY_SAFE_CONTAINER&&slot!=EquipmentSlot.SecuredContainer)continue;
                    action="Stims";need=f.Vital?"vital":"low-health";rank=1;
                }
                else if(destroyed&&part!=EBodyPart.Head&&part!=EBodyPart.Chest&&Treats(item,EDamageEffectType.DestroyedPart))
                {
                    if(Time.time<med.SurgicalKit._nextPosibleUseTime||b.BotOwner.Settings.FileSettings.Mind.SURGE_KIT_ONLY_SAFE_CONTAINER&&slot!=EquipmentSlot.SecuredContainer)continue;
                    if(!SourceSurgeryAllowed(b,s))continue;
                    action="Surgery";need="destroyed-limb";rank=1;
                }
                else
                {
                    if(!med.FirstAid.CanUseByTime()||destroyed||b.BotOwner.Settings.FileSettings.Mind.MEDS_ONLY_SAFE_CONTAINER&&slot!=EquipmentSlot.SecuredContainer)continue;
                    if(heavy&&Treats(item,EDamageEffectType.HeavyBleeding)){need="heavy-bleed";rank=5;}
                    else if((part==EBodyPart.Head||part==EBodyPart.Chest)&&hp.Normalized<=.25){need="vital";rank=4;}
                    else if(light&&Treats(item,EDamageEffectType.LightBleeding)){need="light-bleed";rank=3;}
                    else if(hp.Normalized<b.BotOwner.Settings.FileSettings.Mind.PART_PERCENT_TO_HEAL){need="injury";rank=2;}
                    else continue;
                    // A splint or bandage may be applicable but cannot heal missing HP.
                    if((need=="vital"||need=="injury")&&(!(item is MedKit)||item.MedKitComponent==null))continue;
                    action="FirstAid";
                }
                if(!MedicalUsable(b,item,part))continue;
                double duration=item.HealthEffectsComponent.UseTimeFor(part);
                var o=new MedicalOption{Action=action,Item=item.Id.ToString(),Part=part.ToString(),Need=need,Usable=true,Duration=duration,Resource=item.MedKitComponent?.HpResource??0,Score=rank};
                if(!best.TryGetValue(action,out var old)||rank>old.Score||rank==old.Score&&duration<old.Duration)
                {best[action]=o;chosen[action]=(item,part);}
            }
        }
        foreach(var action in new[]{"FirstAid","Stims","Surgery"})f.Options.Add(best.TryGetValue(action,out var o)?o:new MedicalOption{Action=action});
    }
    private static bool ContinueMedical(SelfActionDecisionClass __instance,ref ESelfActionType Decision,ref bool __result)
    {
        var p=Instance;var b=__instance.Bot;
        if(p==null||!p.states.TryGetValue(b,out var s)||!p.MedicalScope(b)||s.Medical.Fault)return true;
        try
        {
            p.SampleMedical(b,s);var r=s.Medical;
            if(r.Interruption.Cancelling){Decision=ESelfActionType.None;__result=false;return false;}
            if(r.Policy.Active){Decision=MedicalAction(r.Policy.Trace!.Choice);__result=true;return false;}
            if(r.Policy.Trace?.State=="ended"&&b.Decision.CurrentSelfDecision==MedicalAction(r.Policy.Trace.Choice))
            {Decision=ESelfActionType.None;__result=false;return false;}
            return true;
        }
        catch(Exception ex){p.MedicalError(s,ex);return true;}
    }
    private static bool ExecuteMedical(SAINSelfActionClass __instance,MethodBase __originalMethod,ref bool __result)
    {
        var p=Instance;var b=__instance.Bot;
        if(p==null||!p.MedicalScope(b)||!p.states.TryGetValue(b,out var s)||s.Medical.Fault)return true;
        var action=__originalMethod.Name=="DoFirstAid"?"FirstAid":__originalMethod.Name=="DoStims"?"Stims":"Surgery";
        if(s.Medical.Policy.Trace?.Choice!=action)return true;
        __result=false;
        try{p.StartMedical(b,s);__result=s.Medical.Policy.Trace?.State is "requested" or "started";return false;}
        catch(Exception ex){p.MedicalError(s,ex);return false;}
    }
    private static bool ExecuteSurgery(object __instance)
    {
        var p=Instance;var b=(BotComponent)surgeryBot.GetValue(__instance)!;
        if(p==null||!p.MedicalScope(b)||!p.states.TryGetValue(b,out var s)||s.Medical.Policy.Trace?.Choice!="Surgery")return true;
        // No native instant full-health / restore-all-parts path, only real kit operations.
        s.Medical.SurgeryOwned=true;
        try { if(s.Medical.Policy.Active&&!s.Medical.Interruption.Cancelling){b.Mover.Stop();if(Time.time-s.Medical.Policy.SelectedAt>=1)p.StartMedical(b,s);}return false; }
        catch(Exception ex){p.MedicalError(s,ex);return false;}
    }
    private void StartMedical(BotComponent b,BotState s)
    {
        var r=s.Medical;var t=r.Policy.Trace;if(t?.State!="selected")return;
        if(r.Item==null||!r.Item.TryGetTarget(out var item)||item.Id.ToString()!=t.Item||!MedicalUsable(b,item,r.Part))
        {EndMedical(s,"medical-item-no-longer-usable");return;}
        if(t.Choice=="Surgery"&&PostSurgeryTooSoon(b,s)){EndMedical(s,"medical-post-combat-quiet-time");return;}

        var health=b.Player.HealthController;
        bool stillNeeded=t.Need=="heavy-bleed"?health.FindExistingEffect<IHeavyBleeding>(r.Part)!=null
            :t.Need=="light-bleed"?health.FindExistingEffect<ILightBleeding>(r.Part)!=null
            :t.Choice=="Surgery"?health.IsBodyPartDestroyed(r.Part)
            :t.Choice=="Stims"?UsefulStim(item,health.FindExistingEffect<IHeavyBleeding>()!=null||health.FindExistingEffect<ILightBleeding>()!=null)
            :health.GetBodyPartHealth(r.Part,false).Normalized<b.BotOwner.Settings.FileSettings.Mind.PART_PERCENT_TO_HEAL;
        if(!stillNeeded){EndMedical(s,"medical-need-resolved");return;}
        var f=MedicalFrame(b,s);var validation=new MedicalChoice();
        f.Options.Add(new MedicalOption{Action=t.Choice,Item=t.Item,Part=t.Part,Need=t.Need,Duration=t.Duration,Usable=true});
        if(validation.Evaluate(f,config!.MedicalChoice).Choice!=t.Choice){EndMedical(s,"medical-start-no-longer-safe");return;}
        var med=b.BotOwner.Medecine;
        if(t.Choice=="FirstAid"&&!med.FirstAid.CanUseByTime()||t.Choice=="Surgery"&&Time.time<med.SurgicalKit._nextPosibleUseTime||t.Choice=="Stims"&&!med.Stimulators.CanUseNow())
        {EndMedical(s,"medical-native-cooldown");return;}
        r.BeforeHealth=b.Player.HealthController.GetBodyPartHealth(r.Part,false).Current;r.BeforeDestroyed=b.Player.HealthController.IsBodyPartDestroyed(r.Part);
        r.BeforeResource=item.MedKitComponent?.HpResource??0;r.Policy.Request(Time.time);RecordMedical(s,true);
        var weak=new WeakReference<MedicalRuntime>(r);int token=r.Token;
        Action done=()=>{if(weak.TryGetTarget(out var live)&&live.Token==token)live.Callback=true;};
        if(t.Choice=="FirstAid")
        {med.FirstAid.CurUsingMeds=item;med.FirstAid._bodyPartToHeal=r.Part;med.FirstAid.TryApplyToCurrentPart(callback:done);}
        else if(t.Choice=="Surgery")
        {r.SurgeryOwned=true;if(config.SurgeryInterruption.Enabled)r.Interruption.Begin(Time.time,t.Item,t.Part);med.SurgicalKit.CurUsingMeds=item;med.SurgicalKit._bodyPartToHeal=r.Part;med.SurgicalKit.ApplyToCurrentPart(done);}
        else {med.Stimulators._stimulator=(Stimulator)item;med.Stimulators.TryApply(callback:success=>{if(success)done();});}
        SampleMedical(b,s,true);
    }
    private void SampleMedical(BotComponent b,BotState s,bool force=false)
    {
        var r=s.Medical;if(!MedicalScope(b)){r.Steps.Clear();EndSurgeryInterruption(s,"surgery-out-of-scope");EndMedical(s,"medical-out-of-scope");return;}
        SampleSurgeryInterruption(b,s,force);
        if(!r.Policy.Active)return;
        if(!force&&Time.time<r.SampleAt)return;r.SampleAt=Time.time+.1f;
        var t=r.Policy.Trace!;
        if(r.Item==null||!r.Item.TryGetTarget(out var item)){EndMedical(s,"medical-item-lost");return;}
        bool hands=b.Player.HandsController is IMedsController&&b.Player.HandsController.Item?.Id.ToString()==t.Item;
        if(t.State=="selected")
        {if(Time.time-r.Policy.SelectedAt>5)EndMedical(s,"medical-selection-not-executed");return;}
        if(t.State=="requested"&&hands){r.Policy.ObserveStart(Time.time);RecordMedical(s,true);}
        bool usingMed=t.Choice=="FirstAid"?b.BotOwner.Medecine.FirstAid.Using:t.Choice=="Surgery"?b.BotOwner.Medecine.SurgicalKit.Using:b.BotOwner.Medecine.Stimulators.Using;
        if((r.Callback||!usingMed)&&!hands)
        {
            bool improved=b.Player.HealthController.GetBodyPartHealth(r.Part,false).Current>r.BeforeHealth
                ||r.BeforeDestroyed&&!b.Player.HealthController.IsBodyPartDestroyed(r.Part)
                ||t.Need=="heavy-bleed"&&b.Player.HealthController.FindExistingEffect<IHeavyBleeding>(r.Part)==null
                ||t.Need=="light-bleed"&&b.Player.HealthController.FindExistingEffect<ILightBleeding>(r.Part)==null;
            var outcome=improved?"health-improved":item.MedKitComponent?.HpResource<r.BeforeResource?"resource-consumed":"unconfirmed";
            EndMedical(s,t.Started?"medical-use-ended":"medical-start-not-observed",outcome);return;
        }
        if(t.State=="requested"&&Time.time-r.Policy.RequestedAt>5||t.Started&&Time.time-r.Policy.StartedAt>Math.Max(30,t.Duration+20))
        {EndMedical(s,"medical-native-operation-stalled");Logger.LogWarning("H01: medical observation expired for "+s.ProfileId+"; native recovery retains control.");return;}
        if(t.Started){t.Applied=hands;t.ValidFor=hands?1:0;RecordMedical(s,false);}
    }
    private void EndMedical(BotState s,string reason,string outcome="unconfirmed")
    {var r=s.Medical;if(reason is "medical-raid-ended" or "medical-out-of-scope" or "medical-adapter-error" or "medical-native-operation-stalled" or "medical-item-lost")EndSurgeryInterruption(s,reason);if(!r.Policy.Active)return;r.Policy.End(Time.time,reason,config?.MedicalChoice??new(),outcome);RecordMedical(s,true);r.Token++;r.Item=null;r.Items.Clear();}
    private void MedicalError(BotState s,Exception ex)
    {if(s.Medical.Fault)return;s.Medical.Fault=true;EndMedical(s,"medical-adapter-error");Logger.LogWarning("H01 yielded: "+ex.GetBaseException().Message);}
    private void RecordMedical(BotState s,bool force)
    {
        var r=s.Medical;var t=r.Policy.Trace;if(t==null)return;
        string key=t.State+":"+t.Choice+":"+t.Reason+":"+t.Item;
        if(!force&&key==r.Key&&Time.time<r.LogAt)return;r.Key=key;r.LogAt=Time.time+(t.Applied?.5f:4);
        var json=Diagnostics.Serialize(new{schema=1,session,sequence=++sequence,feature="H01",stage="evaluated",decision="MedicalChoice",reason=t.Reason,
            realtime=Time.realtimeSinceStartup,utc=DateTime.UtcNow.ToString("O"),configRevision=config?.Revision??0,profileId=s.ProfileId,name=s.Name,botInstance=s.Id,
            medicalChoice=t,medicalChoiceConfig=config?.MedicalChoice});
        diagnostics?.Write(json);Api.Decisions.Publish(json,ex=>{if(!subscriberWarned){subscriberWarned=true;Logger.LogWarning(ex.Message);}});
    }
}
