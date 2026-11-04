using Comet.Client;
using EFT;
using EFT.InventoryLogic;
using SAIN.Components;
using SAIN.Preset.Shared.Enums;
using SAIN.SAINComponent.Classes.WeaponFunction;
using SAIN.SAINComponent.Classes.EnemyClasses;
using UnityEngine;
using Newtonsoft.Json.Linq;

Plugin.Patch();int n=0;var events=new List<string>();void C(bool v,string why){n++;if(!v)throw new Exception("L01 adapter: "+why);}
(Plugin p,BotComponent b,BotLightController l,Player.FirearmController h) Setup(int devices=1,bool on=true)
{
 Time.time=Time.realtimeSinceStartup=10;var p=new Plugin();var b=new BotComponent();var h=new Player.FirearmController();b.Player.HandsController=h;b.BotOwner.WeaponManager.CurrentWeapon=h.Item;
 for(int i=0;i<devices;i++){var d=new LightComponent{IsActive=on,SelectedMode=i+2};h.Item.Mods.Add(d.Item);}return(p,b,new(b){Want=on},h);
}
void Tick(BotLightController l,float delta=0){Time.time+=delta;Time.realtimeSinceStartup=Time.time;l.Tick();}
void Save(Plugin p)=>events.AddRange(p.diagnostics!.Lines);
foreach(int count in new[]{1,2,5,16})
{
 var(p,b,l,h)=Setup(count);Tick(l);Tick(l,.5f);C(h.Commands.Count==0,"stable hold does not bypass hidden delay");Tick(l,.5f);
 var r=p.states[b].LightDiscipline;C(r.Policy.Dark&&r.Policy.Trace!.ObservedOff,"OFF confirmed");C(h.Commands.Count==1&&h.Commands[0].Length==count,"one command for all active devices");C(h.Commands[0].All(x=>!x.IsActive),"only OFF commands");
 C(h.Item.Mods.Select(x=>x.Light!.SelectedMode).SequenceEqual(Enumerable.Range(2,count)),"visible and IR modes preserved");C(l.Want&&l.Next==0,"native desire and timer unchanged");
 int logs=p.diagnostics!.Lines.Count;for(int i=0;i<20;i++)Tick(l);C(p.diagnostics.Lines.Count==logs,"same-frame logging coalesced");
 for(int i=0;i<10;i++)Tick(l,.05f);C(h.Commands.Count==1,"no repeat command while dark");
 b.GoalEnemy!.IsVisible=true;Tick(l,.05f);Tick(l,.2f);C(r.Policy.Dark&&h.Commands.Count==1,"brief visibility keeps devices OFF");Tick(l,.06f);
 C(!r.Policy.Dark&&h.Commands.Count==2&&h.Commands[1][0].IsActive,"native first-device preference resumes on confirmed visibility");C(h.Commands[1].Length==1,"no COMET all-ON action");Save(p);
}
{
 var(p,b,l,h)=Setup(2,false);Tick(l);Tick(l,1);C(p.states[b].LightDiscipline.Policy.Dark&&h.Commands.Count==0,"already OFF sends nothing");C(!p.states[b].LightDiscipline.Policy.Trace!.Requested,"no false command attribution");
 l.Want=true;Tick(l,.2f);C(h.Commands.Count==0,"native ON desire suspended, retained");b.GoalEnemy!.IsVisible=true;Tick(l,.06f);Tick(l,.3f);C(h.Commands.Count==1&&h.Commands[0][0].IsActive,"latest native desire restored without resetting original value");Save(p);
}
foreach(string kind in new[]{"active","mode","addition","removal","replace"})
{
 var(p,b,l,h)=Setup(2);Tick(l);Tick(l,1);var r=p.states[b].LightDiscipline;int before=h.Commands.Count;
 switch(kind){case"active":h.Item.Mods[1].Light!.IsActive=true;break;case"mode":h.Item.Mods[1].Light!.SelectedMode=8;break;case"addition":h.Item.Mods.Add(new LightComponent().Item);break;case"removal":h.Item.Mods.RemoveAt(1);break;case"replace":h.Item.Mods[1]=new LightComponent{IsActive=false}.Item;break;}
 Tick(l,.1f);C(!r.Policy.Dark&&r.Policy.Trace!.State=="yielded","external owner wins "+kind);
 int off=h.Commands.Count(x=>x.All(d=>!d.IsActive));for(int i=0;i<20;i++)Tick(l,.1f);C(h.Commands.Count(x=>x.All(d=>!d.IsActive))==off,"no extinction fight "+kind);Save(p);
}
{
 var(p,b,l,h)=Setup();Tick(l);Tick(l,1);var r=p.states[b].LightDiscipline;
 for(int i=0;i<20;i++){b.GoalEnemy!.IsVisible=true;Tick(l,.06f);b.GoalEnemy.IsVisible=false;Tick(l,.06f);}
 C(r.Policy.Dark&&h.Commands.Count==1,"visual flicker never toggles accessories");C(r.Policy.Trace!.Requested&&r.Policy.Trace.ObservedOff,"visual flicker preserves command attribution");Save(p);
}
foreach(string kind in new[]{"refuse","empty","overflow","throw"})
{
 var(p,b,l,h)=Setup(kind=="empty"?0:kind=="overflow"?17:1);h.Refuse=kind=="refuse";h.Throw=kind=="throw";
 Tick(l);Tick(l,1);var r=p.states[b].LightDiscipline;C(!r.Policy.Dark,"unconfirmed or unavailable device fails open "+kind);
 h.Throw=false;int off=h.Commands.Count(x=>x.All(d=>!d.IsActive));for(int i=0;i<10;i++)Tick(l,.1f);C(h.Commands.Count(x=>x.All(d=>!d.IsActive))==off,"no repeated failed requests "+kind);Save(p);
}
foreach(string priority in new[]{"scav","player","dead","inactive","ghost","remote","ending","world","config","feature","orbit","medicine","self","reload","door","sprint","running","grenade","throwing","flash","fire","manual-fire","foreign-action","search","processing","inventory","remove","spawn","interaction","weapon-reload","not-ready","other-visible"})
{
 var(p,b,l,h)=Setup();Tick(l);Tick(l,1);var r=p.states[b].LightDiscipline;
 switch(priority){case"scav":b.Pmc=false;break;case"player":b.Player.IsAI=false;break;case"dead":b.IsDead=true;break;case"inactive":b.BotActive=false;break;case"ghost":p.IsGhost=true;break;case"remote":p.Authority=false;break;case"ending":b.GameEnding=true;break;case"world":p.world=null;break;case"config":p.config!.Enabled=false;break;case"feature":p.config!.LightDiscipline.Enabled=false;break;case"orbit":b.SAINLayersActive=false;break;case"medicine":b.BotOwner.Medecine.Using=true;break;case"self":b.Decision.CurrentSelfDecision=ESelfActionType.FirstAid;break;case"reload":b.BotOwner.WeaponManager.Reload.Reloading=true;break;case"door":b.DoorOpener.Interacting=true;break;case"sprint":b.Player.IsSprintEnabled=true;break;case"running":b.Mover.Running=true;break;case"grenade":b.Grenade.GrenadeDangerPoint=new();break;case"throwing":b.BotOwner.WeaponManager.Grenades.ThrowindNow=true;break;case"flash":b.Flashed.IsFlashed=true;break;case"fire":b.BotOwner.ShootData.Shooting=true;break;case"manual-fire":b.ManualShoot.Shooting=true;break;case"foreign-action":b.CurrentAction="ORBIT";break;case"search":b.CurrentAction=new SAIN.Layers.Combat.Solo.SearchAction();break;case"processing":h.Processing=true;break;case"inventory":h.Inventory=true;break;case"remove":h.Remove=true;break;case"spawn":h.Spawn=true;break;case"interaction":h.Interact=true;break;case"weapon-reload":h.Reload=true;break;case"not-ready":h.Ready=false;break;case"other-visible":b.EnemyController.KnownEnemies.Add(new(){IsVisible=true});break;}
 Tick(l);C(!r.Policy.Dark&&r.Devices.Count==0,"immediate priority releases without sampling wait "+priority);C(h.Commands.Count(x=>x.All(d=>!d.IsActive))==1,"no additional COMET write "+priority);Save(p);
}
foreach(string kind in new[]{"no-contact","stale","far-known","squad-only","moving","slow-search","stand-without-cover","bad-position","bad-speed"})
{
 var(p,b,l,h)=Setup();switch(kind){case"no-contact":b.GoalEnemy!.EnemyKnown=false;break;case"stale":b.GoalEnemy!.KnownPlaces.LastHeardPlace!.Timestamp=-20;break;case"far-known":b.GoalEnemy!.RealDistance=1;b.GoalEnemy.KnownPlaces.LastHeardPlace!.Position=new(61,0,0);break;case"squad-only":b.GoalEnemy!.KnownPlaces.LastHeardPlace=null;b.GoalEnemy.KnownPlaces.LastSquadPlace=new();break;case"moving":b.Player.Velocity=new(1,0,0);break;case"slow-search":b.Decision.CurrentCombatDecision=ECombatDecision.Search;b.Player.Velocity=new(.1f,0,0);break;case"stand-without-cover":b.Decision.CurrentCombatDecision=ECombatDecision.StandAndShoot;b.CurrentAction=new SAIN.Layers.Combat.Solo.StandAndShootAction();break;case"bad-position":b.GoalEnemy!.KnownPlaces.LastHeardPlace!.Position=new(float.NaN,0,0);break;case"bad-speed":b.Player.Velocity=new(float.NaN,0,0);break;}
 Tick(l);Tick(l,1);C(h.Commands.Count==0,"no unjustified command "+kind);Save(p);
}
{
 var(p,b,l,h)=Setup();b.BotOwner.NightVision.UsingNow=true;b.GoalEnemy!.RealDistance=999;Tick(l);Tick(l,1);var r=p.states[b].LightDiscipline;
 C(r.Policy.Dark&&r.Policy.Trace!.Distance==20&&r.Policy.Trace.NightVision,"personal known distance, not hidden real distance; NV recorded");
 C(h.Item.Mods[0].Light!.SelectedMode==2,"NV never selects white light mode");
 var old=h.Item;b.Player.HandsController=new Player.FirearmController();b.BotOwner.WeaponManager.CurrentWeapon=((Player.FirearmController)b.Player.HandsController).Item;Tick(l,.1f);
 C(r.Devices.Count==0&&!r.Policy.Dark&&h.Commands.Count==1,"weapon swap drops old references without restoring ON");Save(p);
}
foreach(bool ambush in new[]{false,true}){
 var(p,b,l,h)=Setup();Tick(l);p.states[b].Ambush.Policy.Active=ambush;p.states[b].HoldAngle.Policy.Active=!ambush;Tick(l,.1f);Tick(l,1);
 C(p.states[b].LightDiscipline.Policy.Trace!.Tactic==(ambush?"door-ambush":"angle-hold"),"actual COMET hold reason");Save(p);
}
{
 var(p,b,l,h)=Setup();b.Decision.CurrentCombatDecision=ECombatDecision.StandAndShoot;b.CurrentAction=new SAIN.Layers.Combat.Solo.StandAndShootAction();b.Cover.CoverInUse=new();Tick(l);Tick(l,1);C(p.states[b].LightDiscipline.Policy.Dark,"native covered hold eligible");
 b.Dispose();C(b.Disposed&&!p.states[b].LightDiscipline.Policy.Dark&&p.states[b].LightDiscipline.Devices.Count==0,"real Harmony disposal releases references");Save(p);
}
{
 var(p,b,l,h)=Setup();Tick(l);Tick(l,1);Time.time+=1.1f;p.Watch(b);C(!p.states[b].LightDiscipline.Policy.Dark,"stale actuator callback yields");
 Tick(l);Tick(l,1);int commands=h.Commands.Count;p.End(b);C(h.Commands.Count==commands&&p.states[b].LightDiscipline.Weapon==null,"raid end never reactivates removed weapon");Save(p);
}
var output=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../../artifacts/l01-adapter-events.jsonl"));File.WriteAllLines(output,events);
Console.WriteLine($"{n} L01 production adapter/Harmony checks passed; {events.Count} JSON events. Controlled native doubles, not a raid.");
