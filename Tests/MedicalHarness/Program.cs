using Comet.Client;
using Comet.Core;
using EFT;
using EFT.Ballistics;
using EFT.InventoryLogic;
using EFT.HealthSystem;
using SAIN.Components;
using SAIN.Preset.Shared.Enums;
using SAIN.SAINComponent.Classes.EnemyClasses;
using UnityEngine;
using Newtonsoft.Json.Linq;
int checks=0;var events=new List<string>();
void C(bool x,string why){checks++;if(!x)throw new Exception("H01 adapter: "+why);}
(Plugin p,BotComponent b) Setup(){Time.time=10;Time.frameCount++;var p=new Plugin();var b=new BotComponent();b.EnemyController.KnownEnemies.Add(new(b));return(p,b);}
Meds Aid(BotComponent b,string id="aid"){var item=new MedKit{Id=id,AllowedPart=EBodyPart.Chest};b.Player.HealthController.Parts[EBodyPart.Chest].Current=50;b.Player.InventoryController.Items.Add(item);return item;}
Meds Surgery(BotComponent b){var item=new Meds{Id="surgery",AllowedPart=EBodyPart.LeftLeg};item.HealthEffectsComponent.Duration=20;item.HealthEffectsComponent.DamageEffects[EDamageEffectType.DestroyedPart]=new();b.Player.HealthController.Destroyed.Add(EBodyPart.LeftLeg);b.Player.InventoryController.Items.Add(item);return item;}
Stimulator Stim(BotComponent b,bool useful=true){var item=new Stimulator{Id="stim",MedKitComponent=null,AllowedPart=EBodyPart.Chest};item.HealthEffectsComponent.Duration=2;item.HealthEffectsComponent.BuffSettings=new[]{new Buff{BuffType=useful?EStimulatorBuffType.HealthRate:EStimulatorBuffType.SkillRate}};b.Player.InventoryController.Items.Add(item);b.Player.HealthController.Parts[EBodyPart.Common].Current=30;return item;}
MedicalTrace T(Plugin p,BotComponent b)=>p.states[b].Medical.Policy.Trace!;
var(p,b)=Setup();Aid(b);var choice=p.Choose(b);C(!choice.native&&choice.decision==ESelfActionType.FirstAid,"real start hook selects aid");C(!T(p,b).Started,"selection not started");p.Start(b);C(b.BotOwner.Medecine.FirstAid.Requests==1&&T(p,b).State=="started","native action and observed hands");events.AddRange(p.diagnostics!.Lines);Time.time=18;p.Sample(b);C(p.Continue(b),"8 seconds does not cancel native med at 6 second SAIN timeout");p.Start(b);C(b.BotOwner.Medecine.FirstAid.Requests==1,"no second attempt during operation");b.BotOwner.Medecine.FirstAid.Finish();p.Sample(b);C(T(p,b).State=="ended"&&T(p,b).Outcome=="health-improved","actual health improvement observed");C(!p.Continue(b),"native continuation released at completion");C(!p.Choose(b).result,"retry delay");events.AddRange(p.diagnostics.Lines.Skip(events.Count));
foreach(var bhf in new[]{false,true}){
 (p,b)=Setup();Surgery(b);b.BotOwner.Medecine.SurgicalKit.Bhf=bhf;C(p.Choose(b).decision==ESelfActionType.Surgery,"surgery selected");p.Start(b);C(b.BotOwner.Medecine.SurgicalKit.Requests==1&&T(p,b).Started,"native surgery start");Time.time=25;p.Sample(b);C(p.Continue(b),"long real surgery retained");b.BotOwner.Medecine.SurgicalKit.Finish();p.Sample(b);C(T(p,b).State=="ended"&&T(p,b).Outcome=="health-improved","native/BHF callback ordering");C(b.Player.HealthController.Restores==0,"no synthetic restoration");events.AddRange(p.diagnostics!.Lines);
}
foreach(var mode in new[]{"disabled","off","not-pmc","dead","inactive","ghost","authority","not-sain","no-world"}){
 (p,b)=Setup();Aid(b);switch(mode){case"disabled":p.config!.MedicalChoice.Enabled=false;break;case"off":p.config!.Enabled=false;break;case"not-pmc":b.Pmc=false;break;case"dead":b.IsDead=true;break;case"inactive":b.BotActive=false;break;case"ghost":p.IsGhost=true;break;case"authority":p.Authority=false;break;case"not-sain":b.SAINLayersActive=false;break;default:p.world=null;break;}C(p.Choose(b).native,mode+" leaves native untouched");C(b.BotOwner.Medecine.FirstAid.Requests==0,mode+" no side effect");
}
foreach(var mode in new[]{"refuse","removed","unusable","empty","hit","moving","cooldown","door","reload","grenade"}){
 (p,b)=Setup();var item=Aid(b);p.Choose(b);switch(mode){case"refuse":b.BotOwner.Medecine.FirstAid.Refuse=true;break;case"removed":item.Removed=true;break;case"unusable":item.Applicable=false;break;case"empty":item.MedKitComponent!.HpResource=0;break;case"hit":b.Medical.TimeSinceShot=0;break;case"moving":b.Player.Velocity=new(2,0,0);break;case"cooldown":b.BotOwner.Medecine.FirstAid._nextPosibleUseTime=20;break;case"door":b.BotOwner.DoorOpener.Interacting=true;break;case"reload":b.BotOwner.WeaponManager.Reload.Reloading=true;break;default:b.Grenade.GrenadeDangerPoint=new();break;}
 p.Start(b);C(!T(p,b).Started&&T(p,b).State=="ended",mode+" start refused without false start");C(b.BotOwner.Medecine.FirstAid.Requests==(mode=="refuse"?1:0),mode+" bounded start");Time.time=11;C(!p.Choose(b).result,mode+" no frame retry");events.AddRange(p.diagnostics!.Lines);
}
(p,b)=Setup();Aid(b);b.BotOwner.Medecine.FirstAid.DelayHands=true;p.Choose(b);p.Start(b);C(T(p,b).State=="requested"&&!T(p,b).Started,"using flag not proof of hands");Time.time=11;b.BotOwner.Medecine.FirstAid.Draw();p.Sample(b);C(T(p,b).Started,"asynchronous real draw observed");
(p,b)=Setup();Aid(b);b.BotOwner.Medecine.FirstAid.DelayHands=true;p.Choose(b);p.Start(b);Time.time=16;p.Sample(b);C(p.states[b].Medical.Fault&&T(p,b).Reason=="medical-native-operation-stalled","missing callback bounded and fail open");C(b.BotOwner.Medecine.FirstAid.Using,"do not cancel another mod's operation");
(p,b)=Setup();Stim(b);C(p.Choose(b).decision==ESelfActionType.Stims,"single-use resource-less useful stim selected");p.Start(b);C(T(p,b).Started&&b.BotOwner.Medecine.Stimulators.Requests==1,"native stim used");Time.time=14;C(p.Continue(b),"stim not cancelled at native 3 second deadline");events.AddRange(p.diagnostics!.Lines);
(p,b)=Setup();Stim(b,false);C(!p.Choose(b).result,"skill-only stim not a heal");
(p,b)=Setup();Aid(b,"ordinary");var bandage=new Meds{Id="heavy-bandage",AllowedPart=EBodyPart.LeftArm,MedKitComponent=null};bandage.HealthEffectsComponent.DamageEffects[EDamageEffectType.HeavyBleeding]=new();b.Player.InventoryController.Items.Add(bandage);b.Player.HealthController.Effects.Add(new Heavy(EBodyPart.LeftArm));b.EnemyController.KnownEnemies[0].IsVisible=true;p.Choose(b);C(T(p,b).Item=="heavy-bandage"&&T(p,b).Part=="LeftArm","bandage on actual bleed ahead of ordinary HP");p.Start(b);C(b.BotOwner.Medecine.FirstAid.CurUsingMeds==bandage&&b.BotOwner.Medecine.FirstAid._bodyPartToHeal==EBodyPart.LeftArm,"selected usable item/part sent to native");
foreach(var mode in new[]{"new","near","no-cover","not-protective","incomplete","feet"}){
 (p,b)=Setup();Surgery(b);var e=b.EnemyController.KnownEnemies[0];switch(mode){case"new":b.EnemyController.KnownEnemies.Add(new(b){EnemyProfileId="fresh",KnownPlaces=new(){LastSeenPlace=new(){TimeSincePositionUpdated=2,Position=new(100,0,0)}}});break;case"near":e.KnownPlaces.LastSeenPlace!.Position=new(30,0,0);break;case"no-cover":b.Cover.CoverInUse=null;break;case"not-protective":p.Protection=0;break;case"incomplete":for(int i=0;i<17;i++)b.EnemyController.KnownEnemies.Add(new(b));break;case"feet":e.KnownPlaces.LastHeardPlace=new(){Position=new(20,0,0),TimeSincePositionUpdated=0};p.Hear(e,SAINSoundType.FootStep,e.KnownPlaces.LastHeardPlace);break;}
 C(!p.Choose(b).result,mode+" refuses surgery");if(mode=="new")C(T(p,b).Contact=="fresh"&&T(p,b).Quiet==2,"fresh other threat represented");
}
(p,b)=Setup();Aid(b);p.Choose(b);p.Start(b);p.IsGhost=true;p.Sample(b);C(T(p,b).State=="ended"&&p.states[b].Medical.Item==null,"ghost clears owned state without cancelling native");
(p,b)=Setup();Aid(b);p.Choose(b);p.Start(b);p.End(b);C(p.states[b].Medical.Item==null&&!p.states[b].Medical.Policy.Active,"raid cleanup drops item");b.BotOwner.Medecine.FirstAid.Finish();C(!p.states[b].Medical.Callback,"late callback token invalidated");
(p,b)=Setup();Aid(b);p.doorBudget.Available=false;C(!p.Choose(b).result&&b.BotOwner.Medecine.FirstAid.Requests==0,"budget defers without inventory/operation");
(p,b)=Setup();Aid(b);p.Choose(b);b.BotOwner.Medecine.FirstAid.Throw=true;p.Execute(b,"DoFirstAid");C(p.states[b].Medical.Fault&&T(p,b).State=="ended","native exception no retry spam");

(p,b)=Setup();Surgery(b);p.Choose(b);Time.time=11;C(!p.SurgeryAction(b)&&b.BotOwner.Medecine.SurgicalKit.Requests==1,"surgery action executes only native kit");p.SurgeryAction(b);C(b.BotOwner.Medecine.SurgicalKit.Requests==1,"surgery update cannot repeat use");p.RestoreOnStop(b);C(b.Player.HealthController.Restores==0,"surgery Stop cannot restore limbs for free");C(!p.states[b].Medical.SurgeryOwned,"Stop releases ownership");
(p,b)=Setup();p.RestoreOnStop(b);C(b.Player.HealthController.Restores==1,"unowned surgery Stop unchanged");
(p,b)=Setup();b.Player.HealthController.Destroyed.Add(EBodyPart.LeftLeg);C(p.RestoreOnStop(b),"native restore success bool retained");C(!p.RestoreOnStop(b),"native restore refusal bool retained");
(p,b)=Setup();Surgery(b);p.Choose(b);Time.time=11;p.SurgeryAction(b);C(!p.RestoreOnStop(b)&&b.Player.HealthController.Restores==0,"owned surgery returns false without restoring");
(p,b)=Setup();Aid(b);p.Choose(b);b.Player.HealthController.Parts[EBodyPart.Chest].Current=100;p.Start(b);C(T(p,b).Reason=="medical-need-resolved"&&b.BotOwner.Medecine.FirstAid.Requests==0,"resolved injury not treated again");
var nativeRestore=typeof(ActiveHealthController).GetMethod("RestoreBodyPart")!;var code=p.Transform(new[]{new HarmonyLib.CodeInstruction(System.Reflection.Emit.OpCodes.Callvirt,nativeRestore)}).ToArray();C(code.Length==2&&code[0].opcode==System.Reflection.Emit.OpCodes.Ldarg_0&&((System.Reflection.MethodInfo)code[1].operand).Name=="NativeRestoreAfterSurgery","scoped surgery Stop transpiler");
try{p.Transform(Array.Empty<HarmonyLib.CodeInstruction>()).ToArray();C(false,"missing native restore accepted");}catch(InvalidOperationException){checks++;}
string root=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../../"));Directory.CreateDirectory(Path.Combine(root,"artifacts"));File.WriteAllLines(Path.Combine(root,"artifacts/h01-adapter-events.jsonl"),events);
var fixture=events.Select(JObject.Parse).First(e=>e["medicalChoice"]!["state"]!.Value<string>()=="started"&&e["medicalChoice"]!["choice"]!.Value<string>()=="Surgery");
File.WriteAllText(Path.Combine(root,"artifacts/h01-medical-choice.json"),fixture.ToString());
Console.WriteLine($"H01 production adapter: {checks} checks passed; {events.Count} actual JSON events");

H02Tests.Run();
