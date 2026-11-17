using Comet.Client;
using EFT;
using EFT.InventoryLogic;
using EFT.Ballistics;
using EFT.HealthSystem;
using SAIN.Components;
using SAIN.SAINComponent.Classes.EnemyClasses;
using SAIN.SAINComponent.Classes.Mover;
using SAIN.Preset.Shared.Enums;
using UnityEngine;
int n=0;var events=new List<string>();void C(bool ok,string why){n++;if(!ok)throw new Exception("H04 adapter: "+why);}
(Plugin p,BotComponent b,PostCombatLayer layer,PostCombatAction action) Setup(bool fight=true){Time.time=Time.realtimeSinceStartup=10;Time.frameCount++;var p=new Plugin();var b=new BotComponent();foreach(var hp in b.Player.HealthController.Parts.Values)hp.Current=100;b.Player.HandsController=new Player.FirearmController();b.SAINLayersActive=false;b.GoalEnemy=new(b){IsVisible=fight};b.GoalEnemy.KnownPlaces.LastSeenPlace!.Position=new(20,0,0);b.GoalEnemy.KnownPlaces.LastSeenPlace.TimeSincePositionUpdated=0;p.Observe(b);if(fight){b.GoalEnemy.IsVisible=false;b.GoalEnemy.EnemyKnown=false;}return(p,b,new(b.BotOwner,21),new(b.BotOwner));}
void Step(float dt=.3f){Time.time+=dt;Time.realtimeSinceStartup=Time.time;Time.frameCount++;}
void Save(Plugin p){events.AddRange(p.diagnostics!.Lines.Where(x=>x.Contains("\"feature\":\"H04\"")));}
foreach(var kind in new[]{"bush","moving","bad","spotted","solid"})
{
 var(p,b,l,a)=Setup();
 b.Cover.CoverInUse=new(){BotInThisCover=kind!="moving",Spotted=kind=="spotted"};
 b.Cover.CoverInUse.CoverData.IsBad=kind=="bad";
 b.Cover.CoverInUse.Collider.gameObject.layer=kind=="bush"?7:4;
 C(l.IsActive(),"post-combat recovery still allowed: "+kind);
 C(p.states[b].PostCombat.Protected==(kind=="solid"),"no stale/camouflage protection at episode start: "+kind);
}
{
 var(p,b,l,a)=Setup(false);C(!l.IsActive()&&p.states.Count==0,"no fight no state or recovery scan");
}
{
 var(p,b,l,a)=Setup();b.GoalEnemy!.EnemyKnown=true;C(l.IsActive(),"source permits recovery while contact remains hidden");b.GoalEnemy.EnemyKnown=false;
 b.Decision.CurrentCombatDecision=ECombatDecision.AvoidGrenade;C(!l.IsActive(),"native combat preempts");b.Decision.CurrentCombatDecision=ECombatDecision.None;(p,b,l,a)=Setup();
 C(l.IsActive()&&b.SAINLayersActive,"actual SAIN layer activation allows mover ticks");a.Start();C(a.Starts==1&&p.states[b].PostCombat.Layer,"native action lifecycle retained");a.OnSteeringTicked();C(b.Steering.Points>0&&b.Steering.LastPoint.x==20,"only remembered point steered");
 b.GoalEnemy.KnownPlaces.LastSeenPlace!.Position=new(999,0,999);Step();a.Update(new());C(p.states[b].PostCombat.Threat!.Value.x==20,"hidden memory changes not live tracking");
 b.BotOwner.Memory.IsUnderFire=true;C(!l.IsActive()&&!p.states[b].PostCombat.Policy.Active,"urgent threat bypasses cadence");a.Stop();C(!p.states[b].PostCombat.Layer,"stop cleanup");Save(p);
}
{
 var(p,b,l,a)=Setup();C(l.IsActive(),"recovery active before secondary threat");a.Start();
 b.Shoot.Result=true;int points=b.Steering.Points,moving=b.Steering.Moving;
 a.OnSteeringTicked();
 C(b.Shoot.Calls==1&&b.Steering.Points==points&&b.Steering.Moving==moving,"post-combat fires at another visible threat without overwriting aim");
 C(b.Suppression.Calls==0&&b.Steering.Priorities==0,"post-combat keeps source fire-only behavior");
}
foreach(string scope in new[]{"disabled","feature","scav","player","ghost","remote","inactive","dead","ending","world"}){
 var(p,b,l,a)=Setup();C(l.IsActive(),"before "+scope);a.Start();switch(scope){case"disabled":p.config!.Enabled=false;break;case"feature":p.config!.PostCombat.Enabled=false;break;case"scav":b.Pmc=false;break;case"player":b.Player.IsAI=false;break;case"ghost":p.IsGhost=true;break;case"remote":p.Authority=false;break;case"inactive":b.BotActive=false;break;case"dead":b.IsDead=true;break;case"ending":b.GameEnding=true;break;default:p.world=null;break;}
 C(!l.IsActive()&&!p.states[b].PostCombat.Policy.WantsRecovery(11),"scope release "+scope);Save(p);
}
{
 var(p,b,l,a)=Setup();b.Cover.CoverInUse=null;C(l.IsActive(),"source recovery starts in open ground");a.Start();
 C(b.Mover.Prone.Value&&b.Mover.Routes==0,"source drops prone when no sheltered native point is available");
 Step();a.Update(new());C(p.states[b].PostCombat.Policy.Trace!.State=="watch","failed cover search advances to watch rather than repeated probes");
 p.Cleanup(b);C(!p.states[b].PostCombat.Policy.Active,"cleanup releases recovery");Save(p);
}
foreach(bool available in new[]{true,false}){
 var(p,b,l,a)=Setup();b.Player.HandsController=new Player.FirearmController{Item=new(){Magazine=new(){Count=5}}};b.BotOwner.WeaponManager.Reload.Available=available;l.IsActive();a.Start();Step();a.Update(new());var reload=b.BotOwner.WeaponManager.Reload;
 C(reload.Requests==(available?1:0),"native reload availability");C(((Player.FirearmController)b.Player.HandsController).Item.Magazine.Count==5,"no fake ammo");reload.Reloading=false;Step();a.Update(new());C(reload.Requests==(available?1:0),"no repeated reload request");Save(p);
}
{
 var(p,b,l,a)=Setup();b.Player.HealthController.Parts[EBodyPart.LeftArm].Current=20;b.Player.HealthController.Effects.Add(new Heavy(EBodyPart.LeftArm));
 var item=new MedKit{Id="carried-kit",AllowedPart=EBodyPart.LeftArm};item.HealthEffectsComponent.DamageEffects[EDamageEffectType.HeavyBleeding]=new();b.Player.InventoryController.Items.Add(item);b.BotOwner.Medecine.FirstAid.CurUsingMeds=item;
 l.IsActive();a.Start();Step();a.Update(new());C(b.BotOwner.Medecine.FirstAid.Requests==1&&b.BotOwner.Medecine.FirstAid.Using,"real carried kit requested once");
 C(b.Player.HealthController.Parts[EBodyPart.LeftArm].Current==20,"no instant healing");Step(61);p.Sample(b);a.Update(new());C(b.BotOwner.Medecine.FirstAid.Cancels==0,"budget cannot cancel medicine");
 b.BotOwner.Medecine.FirstAid.Finish();b.BotOwner.Medecine.FirstAid.CurUsingMeds=null;b.Player.HandsController=new Player.FirearmController();p.Sample(b);Step();C(!l.IsActive(),"after long medicine routine yields");Save(p);
}
{
 var(p,b,l,a)=Setup();b.Player.HealthController.Destroyed.Add(EBodyPart.LeftLeg);var item=new Meds{Id="cms",AllowedPart=EBodyPart.LeftLeg};item.HealthEffectsComponent.DamageEffects[EDamageEffectType.DestroyedPart]=new();b.Player.InventoryController.Items.Add(item);b.BotOwner.Medecine.SurgicalKit.CurUsingMeds=item;
 l.IsActive();a.Start();Step();a.Update(new());C(b.BotOwner.Medecine.SurgicalKit.Requests==1,"surgery allowed after quiet interval within medical budget");C(b.Player.HealthController.Restores==0,"no free limb restoration");Save(p);
}
{
 var(p,b,l,a)=Setup();p.config!.MedicalChoice.Enabled=false;b.Player.HealthController.Parts[EBodyPart.LeftArm].Current=5;l.IsActive();a.Start();Step(15);C(!l.IsActive()&&b.BotOwner.Medecine.FirstAid.Requests==0,"H01 disabled leaves medicine native");Save(p);
}
{
 var(p,b,l,a)=Setup();l.IsActive();a.Start();var s=p.states[b];Step();a.Update(new());int count=p.diagnostics!.Lines.Count;for(int i=0;i<10;i++)a.Update(new());C(p.diagnostics.Lines.Count==count,"same-frame logs coalesced");C(Api.Decisions.Recovery.ContainsKey(b.ProfileId),"RR sampling API set while actual layer holds");p.Cleanup(b);C(!s.PostCombat.Policy.Active&&s.PostCombat.Threat==null&&!s.PostCombat.MedicalOwner&&!Api.Decisions.Recovery.ContainsKey(b.ProfileId),"raid releases references and API ownership");Save(p);
}
{
 var(p,b,l,a)=Setup();b.Player.HandsController=new Player.FirearmController{Item=new(){Magazine=null!,ChamberAmmoCount=0}};l.IsActive();a.Start();Step();a.Update(new());C(b.BotOwner.WeaponManager.Reload.Requests==1,"missing empty magazine is not full");Save(p);
}
foreach(bool regroup in new[]{false,true}){
 var(p,b,l,a)=Setup();p.config!.PostCombat.AdvanceChances["Normal"]=100;p.config.PostCombat.WatchMin=p.config.PostCombat.WatchMax=0;
 if(regroup){b.Squad.IAmLeader=false;b.Squad.LeaderComponent=new(){Position=new(30,0,0)};SAIN.SAINEnableClass.Bots[b.ProfileId]=b;}
 l.IsActive();a.Start();Step();a.Update(new());var s=p.states[b];C(s.PostCombat.RouteStage==(regroup?"regroup":"check")&&b.Mover.Routes==1,"optional native route "+regroup);C(s.PostCombat.Goal.x==(regroup?30:20),"leader or frozen known location only");Save(p);
}
{
 var(p,b,l,a)=Setup();l.IsActive();a.Start();Step();a.Update(new());var r=p.states[b].PostCombat;
 C(r.ProneInOpen&&r.Policy.Trace!.State=="watch","first episode uses source prone fallback in open ground");
 r.MedicalDisabled=true;r.MedicalFailures=2;r.MedicalUseful=true;r.MedicalUrgent=true;r.MedicalNext=1000;
 b.Position=new(10,0,0);b.GoalEnemy!.IsVisible=true;b.GoalEnemy.EnemyKnown=true;p.Observe(b);a.Stop();
 C(!l.IsActive(),"renewed fight owns bot between episodes");
 b.GoalEnemy.IsVisible=false;b.GoalEnemy.EnemyKnown=false;p.Protection=0;p.ProtectionOrigin=b.Position;b.Cover.CoverPoints.Add(new(){Position=new(14,0,0)});Step();
 C(l.IsActive(),"second recovery can begin");a.Start();
 C(r.Policy.Trace!.Episode==2&&r.ProneInOpen&&!r.Protected,"second fight reevaluates shelter instead of retaining previous protection");
 C(!r.MedicalDisabled&&r.MedicalFailures==0&&!r.MedicalUseful&&!r.MedicalUrgent&&r.MedicalNext<1000,"second episode resets failed and stale medical observations before evaluation");Save(p);
}
var output=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../../artifacts/h04-adapter-events.jsonl"));File.WriteAllLines(output,events);Console.WriteLine($"{n} H04 production adapter checks passed; {events.Count} JSON events; controlled doubles, not Unity.");
