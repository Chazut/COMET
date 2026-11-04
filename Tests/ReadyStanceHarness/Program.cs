using Comet.Client;
using EFT;
using SAIN.Components;
using SAIN.Preset.Shared.Enums;
using SAIN.SAINComponent.Classes.EnemyClasses;
using UnityEngine;
int n=0;var events=new List<string>();void C(bool ok,string why){n++;if(!ok)throw new Exception("H05 adapter: "+why);}
(Plugin p,BotComponent b) Setup(bool fight=true,float distance=100){Time.time=Time.realtimeSinceStartup=10;var p=new Plugin();var b=new BotComponent();b.GoalEnemy=new(b){IsVisible=fight};b.GoalEnemy.KnownPlaces.LastSeenPlace!.Position=new(distance,0,0);p.Fight(b);b.GoalEnemy.IsVisible=false;return(p,b);}
void Step(float s=.3f){Time.time+=s;Time.realtimeSinceStartup=Time.time;}
void NativeTick(Plugin p,BotComponent b,bool native=true){bool result=p.Wish(b,native);b.Player.MovementContext.SetPatrol(result);p.After(b);}
void Save(Plugin p)=>events.AddRange(p.diagnostics!.Lines);
{var(p,b)=Setup(false);C(p.states.Count==0&&p.Wish(b,true),"no fight no allocation or patrol change");p.Heard(b.GoalEnemy!,SAINSoundType.Shot,new());C(p.states.Count==0,"sound alone does not allocate recovery");}
foreach(float distance in new[]{100f,40f}){
 var(p,b)=Setup(true,distance);C(!b.SAINLayersActive,"no combat-layer requirement (H04 and ORBIT independent)");
 C(!p.Wish(b,true)&&b.Player.MovementContext._isInPatrol&&b.Player.MovementContext.Writes==0,"veto only, no direct posture or aim write");p.After(b);var r=p.states[b].ReadyStance;C(!r.Policy.Trace!.Applied&&r.Policy.Trace.State=="pending","native request not yet observed");
 NativeTick(p,b);C(r.Policy.Trace.Applied&&r.Policy.Trace.ObservedRaised&&r.Policy.Deadline==(distance==40?35:25),"native raised result and duration");
 int logs=p.diagnostics!.Lines.Count;for(int i=0;i<10;i++)p.After(b);C(p.diagnostics.Lines.Count==logs,"same-frame logging coalesced");
 Step(distance==40?25:15);NativeTick(p,b);C(b.Player.MovementContext._isInPatrol&&!r.Policy.Trace.Applied&&r.Policy.Trace.State=="expired","native patrol returns at deadline");Save(p);
}
foreach(string priority in new[]{"sprint","running","door","near-door","medicine","med-hands","posture","reload","remove","inventory","interact","spawn","processing","weapon-not-ready","weapon-reload"}){
 var(p,b)=Setup();NativeTick(p,b);var r=p.states[b].ReadyStance;var f=(Player.FirearmController)b.Player.HandsController;
 switch(priority){case"sprint":b.Player.IsSprintEnabled=true;break;case"running":b.Mover.Running=true;break;case"door":b.BotOwner.DoorOpener.Interacting=true;break;case"near-door":b.BotOwner.Mover.CurrentState=EBotMoverState.NearDoor;break;case"medicine":b.BotOwner.Medecine.Using=true;break;case"med-hands":b.Player.HandsController=new MedHands();break;case"posture":p.states[b].MedicalPosture.Policy.Blocking=true;break;case"reload":b.BotOwner.WeaponManager.Reload.Reloading=true;break;case"remove":f.Remove=true;break;case"inventory":f.Inventory=true;break;case"interact":f.Interact=true;break;case"spawn":f.Spawn=true;break;case"processing":f.Processing=true;break;case"weapon-not-ready":f.Ready=false;break;case"weapon-reload":f.Reload=true;break;}
 p.Watch(b);C(!r.Policy.Trace!.Applied&&r.Policy.Trace.State=="suspended","urgent sampling bypasses interval "+priority);C(p.Wish(b,true),"native priority "+priority);C(r.Policy.Deadline==25,"no timer reset "+priority);Save(p);
}
foreach(string mode in new[]{"scav","player","dead","inactive","ghost","remote","ending","world","config","feature"}){
 var(p,b)=Setup();NativeTick(p,b);switch(mode){case"scav":b.Pmc=false;break;case"player":b.Player.IsAI=false;break;case"dead":b.IsDead=true;break;case"inactive":b.BotActive=false;break;case"ghost":p.IsGhost=true;break;case"remote":p.Authority=false;break;case"ending":b.GameEnding=true;break;case"world":p.world=null;break;case"config":p.config!.Enabled=false;break;case"feature":p.config!.ReadyStance.Enabled=false;break;}
 p.Watch(b);C(p.Wish(b,true)&&!p.states[b].ReadyStance.Policy.Pending(Time.time),"scope clears window "+mode);Save(p);
}
{
 var(p,b)=Setup();NativeTick(p,b);Step(4);var clue=new EnemyPlace{Position=new(20,0,0),TimeSincePositionUpdated=1};p.Heard(b.GoalEnemy!,SAINSoundType.FootStep,clue);NativeTick(p,b);
 var r=p.states[b].ReadyStance;C(r.Policy.Deadline==38&&r.Policy.Trace!.Contact=="enemy","heard clue uses original timestamp");
 for(int i=0;i<5;i++){Step();p.Heard(b.GoalEnemy!,SAINSoundType.FootStep,clue);NativeTick(p,b);}C(r.Policy.Deadline==38,"cached sound never extends window");
 Step(1);var fresh=new EnemyPlace{Position=new(60,0,0)};p.Heard(b.GoalEnemy!,SAINSoundType.Shot,fresh);C(r.Policy.Deadline==38,"far sound ignored");
 fresh.Position=new(20,0,0);b.GoalEnemy!.EnemyKnown=false;p.Heard(b.GoalEnemy,SAINSoundType.Shot,fresh);C(r.Policy.Deadline==38,"unknown opponent event ignored");Save(p);
}
{
 var(p,b)=Setup();NativeTick(p,b,false);C(!p.states[b].ReadyStance.Policy.Trace!.Applied,"no H05 claim for native combat posture");
 Step(2);b.BotOwner.Medecine.Using=true;NativeTick(p,b);Step(3);b.BotOwner.Medecine.Using=false;NativeTick(p,b);C(p.states[b].ReadyStance.Policy.Trace!.Applied&&p.states[b].ReadyStance.Policy.Deadline==25,"resume after medicine without prolonging");
 Step(5);b.BotOwner.Memory.IsUnderFire=true;p.Fight(b);b.BotOwner.Memory.IsUnderFire=false;NativeTick(p,b);C(p.states[b].ReadyStance.Policy.Deadline==35,"new exchange refreshes ordinary window");
 int writes=b.Player.MovementContext.Writes;p.End(b);C(!p.states[b].ReadyStance.Policy.Pending(Time.time)&&b.Player.MovementContext.Writes==writes,"cleanup releases veto without forcing posture");Save(p);
}
var output=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../../artifacts/h05-adapter-events.jsonl"));File.WriteAllLines(output,events);Console.WriteLine($"{n} H05 production adapter checks passed; {events.Count} JSON events; controlled doubles, not Unity.");
