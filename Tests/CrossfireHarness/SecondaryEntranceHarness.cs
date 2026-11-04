using Comet.Client;
using Comet.Core;
using EFT;
using EFT.Interactive;
using SAIN;
using SAIN.Components;
using SAIN.SAINComponent.Classes.Mover;
using UnityEngine;
using UnityEngine.AI;
internal static class SecondaryEntranceHarness
{
 public static int Run(){int n=0;var events=new List<string>();void C(bool v,string why){n++;if(!v)throw new Exception("S02 adapter: "+why);}
 void Step(float dt=.1f){Time.time+=dt;Time.realtimeSinceStartup=Time.time;Time.frameCount++;}
 (Plugin p,BotComponent b,BotComponent m) Setup(){Time.time=Time.realtimeSinceStartup=10;Time.frameCount=0;SAINEnableClass.Bots.Clear();Api.Decisions.Labels.Clear();NavMesh.Valid=true;NavMesh.Route=null;Physics.Blocked=false;Physics.Ray=null;Physics.Cast=(a,z)=>true;var p=new Plugin();p.config!.Crossfire.Participation=100;p.config.SecondaryEntrance.Chad=100;var b=new BotComponent{Position=new(-10,0,0)};b.Player.Environment=EnvironmentType.Indoor;var m=new BotComponent();m.GoalEnemy!.IsVisible=true;var members=new Dictionary<string,BotComponent>{{b.ProfileId,b},{m.ProfileId,m}};foreach(var bot in members.Values){bot.Squad.Members=members;bot.EnemyController.KnownEnemies.Add(bot.GoalEnemy!);SAINEnableClass.Bots[bot.ProfileId]=bot;}
 b.BotOwner.VoxelesPersonalData.CurVoxel.DoorLinks.Add(new(){Geometry=new(){Id="front",Center=new(0,0,8),Axis=new(1,0,0),Normal=new(0,0,1),HalfWidth=1}});
 b.BotOwner.VoxelesPersonalData.CurVoxel.DoorLinks.Add(new(){Geometry=new(){Id="side",Center=new(-6,0,12),Axis=new(0,0,1),Normal=new(1,0,0),HalfWidth=1}});
 return(p,b,m);}
 void Refresh(BotComponent b){foreach(var x in b.Squad.Members.Values)if(x.GoalEnemy!=null)x.GoalEnemy.TimeSinceLastKnownUpdated=0;}
 void Plan(Plugin p,BotComponent b){p.Observe(b);for(int i=0;i<200&&p.states[b].Crossfire.Policy.Trace!.State=="planning";i++){Step(.02f);Refresh(b);p.Observe(b);}var r=p.states[b].Crossfire;C(r.Policy.Trace!.State=="ready"&&r.Policy.Trace.Entrance!=null,"secondary access ready: "+r.Policy.Trace.Reason+" "+string.Join(",",r.Rejects));C(r.Policy.Trace.Entrance!.Passage=="side"&&r.Policy.Trace.Entrance.Primary=="front","distinct actual accesses");}
 CrossfireAction Start(Plugin p,BotComponent b){Step();Refresh(b);var a=new CrossfireAction(b.BotOwner);a.Start();C(p.states[b].Crossfire.Policy.Trace!.Applied,"movement observed");C(Api.Decisions.Labels[b.ProfileId]=="COMET:CrossfireEntranceMoving","S02 movement label");return a;}
 void Arrive(Plugin p,BotComponent b,CrossfireAction a){b.Position=p.states[b].Crossfire.Goal;Step();Refresh(b);a.Update(new());C(p.states[b].Crossfire.Policy.Trace!.State=="holding","watch begins at arrival");}
 void Wait(Plugin p,BotComponent b,CrossfireAction a,float seconds){for(int i=0;i<seconds*4&&p.states[b].Crossfire.Policy.Active;i++){Step(.25f);Refresh(b);a.Update(new());}}
 void Save(Plugin p)=>events.AddRange(p.diagnostics!.Lines);
 {
  var(p,b,m)=Setup();Plan(p,b);var a=Start(p,b);Arrive(p,b,a);var r=p.states[b].Crossfire;C(!r.Entrance.Policy.Trace!.Rolled,"arrival does not roll entry yet");Wait(p,b,a,16);C(r.Entrance.Entering&&r.Policy.Trace!.Applied&&Api.Decisions.Labels[b.ProfileId]=="COMET:CrossfireEntranceEntering","ready partner allows actual native entry");b.Position=r.Goal;Step();Refresh(b);a.Update(new());C(!r.Policy.Active&&r.Policy.Trace!.Entrance!.Crossed&&r.Policy.Trace.Reason=="secondary-entrance-crossed-and-reached","actual threshold and destination complete");C(p.Claims==0&&!Api.Decisions.Labels.ContainsKey(b.ProfileId),"cleanup claims and RR");Save(p);
 }
 foreach(string mode in new[]{"reload","medicine","weapon","withdraw","squad","injured","pressure","under-fire","ammo","chance","off","dead","ghost","stale","door-locked","blocked","route-replaced"}){
  var(p,b,m)=Setup();Plan(p,b);var a=Start(p,b);Arrive(p,b,a);var r=p.states[b].Crossfire;
  switch(mode){case"reload":m.BotOwner.WeaponManager.Reload.Reloading=true;break;case"medicine":m.BotOwner.Medecine.Using=true;break;case"weapon":((Player.FirearmController)m.Player.HandsController).Ready=false;break;case"withdraw":m.Position=new(5,0,0);break;case"squad":m.Decision.CurrentSquadDecision=SAIN.Preset.Shared.Enums.ESquadDecision.Regroup;break;case"injured":m.Memory.Health.HealthStatus=ETagStatus.Dying;break;case"under-fire":m.BotOwner.Memory.IsUnderFire=true;break;case"pressure":m.Suppression.IsSuppressed=true;break;case"ammo":b.BotOwner.WeaponManager.CurrentWeapon.Mag.Count=14;break;case"chance":p.config!.SecondaryEntrance.Chad=0;break;case"off":p.config!.SecondaryEntrance.AllowEntry=false;break;case"dead":m.IsDead=true;break;case"ghost":p.Ghosts.Add(m.ProfileId);break;case"stale":b.GoalEnemy!.TimeSinceLastKnownUpdated=9;Step();a.Update(new());break;case"door-locked":r.Entrance.Chosen!.Data.Door.DoorState=EDoorState.Locked;break;case"blocked":p.Volume="occupied";break;case"route-replaced":b.Mover.ActivePath=new BotPathDataManual{Destination=new(100,0,0)};break;}
  Wait(p,b,a,21);C(!r.Policy.Active&&!r.Entrance.Entering&&b.Mover.Walks==1,"no forced entry "+mode);Save(p);
 }
 {
  var(p,b,m)=Setup();Plan(p,b);var a=Start(p,b);Arrive(p,b,a);m.BotOwner.WeaponManager.Reload.Reloading=true;Wait(p,b,a,15);var t=p.states[b].Crossfire.Policy.Trace!.Entrance!;C(t.Extended&&t.Rolled,"occupied partner extends once");double roll=t.EntryRoll;m.BotOwner.WeaponManager.Reload.Reloading=false;Wait(p,b,a,.5f);C(p.states[b].Crossfire.Entrance.Entering&&t.EntryRoll==roll,"ready partner uses same entry roll");Save(p);
 }
 foreach(string mode in new[]{"open","refused","timeout"}){
  var(p,b,m)=Setup();Plan(p,b);var a=Start(p,b);Arrive(p,b,a);var r=p.states[b].Crossfire;r.Entrance.Chosen!.Data.Door.DoorState=EDoorState.Shut;
  if(mode=="refused")p.OpenAccepted=false;if(mode=="timeout")p.Opening=true;Wait(p,b,a,16);
  if(mode=="open")C(r.Entrance.Entering&&r.Policy.Trace!.Entrance!.OpenRequested&&r.Policy.Trace.Entrance.OpenObserved,"native opening observed before entry");
  else{Wait(p,b,a,10);C(!r.Policy.Active&&b.Mover.Walks==1,"opening fails safely "+mode);}Save(p);
 }
 foreach(string mode in new[]{"same","locked","room-route","exposed","partial","long","volume","ambiguous"}){
  var(p,b,m)=Setup();switch(mode){case"same":b.BotOwner.VoxelesPersonalData.CurVoxel.DoorLinks.RemoveAt(1);break;case"locked":b.BotOwner.VoxelesPersonalData.CurVoxel.DoorLinks[1].Door.DoorState=EDoorState.Locked;break;case"room-route":b.Mover.Corners=g=>[b.Position,new(0,0,12),g];break;case"exposed":Physics.Cast=(a,z)=>false;break;case"partial":b.Mover.Status=NavMeshPathStatus.PathPartial;break;case"long":b.Mover.Corners=g=>[b.Position,new(-100,0,0),g];break;case"volume":p.Volume="occupied";break;case"ambiguous":b.BotOwner.VoxelesPersonalData.CurVoxel.DoorLinks.Clear();break;}
  p.Observe(b);var r=p.states[b].Crossfire;for(int i=0;i<250&&r.Policy.Active&&r.Policy.Trace!.Entrance!=null;i++){Step(.02f);Refresh(b);p.Sample(b);}C(r.Policy.Trace!.Entrance==null||!r.Policy.Active,"unsafe access falls back or yields "+mode);C(b.Mover.Walks==0&&p.Claims==0,"planning never pretends to execute "+mode);Save(p);
 }
 {
  var(p,b,m)=Setup();p.config!.Crossfire.Participation=0;p.Observe(b);C(p.states[b].Crossfire.Policy.Trace!.Entrance==null&&p.Claims==0,"no S02 second participation roll");Save(p);
 }
 {
  var(p,b,m)=Setup();Plan(p,b);var a=Start(p,b);Step(2.1f);Refresh(b);p.Sample(b);C(!p.states[b].Crossfire.Policy.Active&&p.Claims==0,"no action callbacks expires access reservation");Save(p);
 }
 {
  var(p,b,m)=Setup();NavMesh.Sample=point=>Math.Abs(point.x+8.5f)>.01f;Plan(p,b);C(Math.Abs(p.states[b].Crossfire.Goal.x+7.875f)<.01f,"tight space uses closer protected watch position");NavMesh.Sample=null;Save(p);
 }
 // 4v1: same supporting contact, three distinct usable accesses, shared reservation board.
 {
  var(p,b,m)=Setup();var two=new BotComponent{Position=new(10,0,0)};var three=new BotComponent{Position=new(0,0,24)};
  var links=b.BotOwner.VoxelesPersonalData.CurVoxel.DoorLinks;
  links.Add(new(){Geometry=new(){Id="right",Center=new(6,0,12),Axis=new(0,0,1),Normal=new(-1,0,0),HalfWidth=1}});
  links.Add(new(){Geometry=new(){Id="rear",Center=new(0,0,18),Axis=new(1,0,0),Normal=new(0,0,-1),HalfWidth=1}});
  foreach(var bot in new[]{two,three}){bot.Player.Environment=EnvironmentType.Indoor;bot.Squad.Members=m.Squad.Members;m.Squad.Members.Add(bot.ProfileId,bot);bot.BotOwner.VoxelesPersonalData.CurVoxel.DoorLinks.AddRange(links);bot.EnemyController.KnownEnemies.Add(bot.GoalEnemy!);SAINEnableClass.Bots[bot.ProfileId]=bot;}
  Plan(p,b);var first=Start(p,b);Arrive(p,b,first);
  foreach(var bot in new[]{two,three}){
   p.Observe(bot);for(int i=0;i<250&&p.states[bot].Crossfire.Policy.Trace!.State=="planning";i++){Step(.02f);Refresh(b);first.Update(new());if(p.states.TryGetValue(two,out var second)&&second.Crossfire.Layer)Plugin.RunCrossfire(two.BotOwner,true);p.Sample(bot);}
   C(p.states[bot].Crossfire.Policy.Trace!.State=="ready"&&p.states[bot].Crossfire.Policy.Trace!.Entrance!=null,"additional squadmate finds a distinct access");var action=Start(p,bot);Arrive(p,bot,action);
  }
  C(p.Claims==3&&new[]{b,two,three}.Select(x=>p.states[x].Crossfire.Policy.Trace!.Entrance!.Passage).Distinct().Count()==3,"three simultaneous distinct access reservations, no per-enemy cap");
  p.EndAll();C(p.Claims==0&&Api.Decisions.Labels.Count==0,"all S02 reservations and labels cleared at raid end");Save(p);
 }
 // A doorless throat needs physical side walls and a navigable crossing, not just a path bend.
 foreach(bool walls in new[]{true,false}){
  var(p,b,m)=Setup();b.BotOwner.VoxelesPersonalData.CurVoxel.DoorLinks.RemoveAt(1);
  NavMesh.Route=(from,to)=>Vector3.Distance(to,new(0,0,12))<.01f&&Math.Abs(from.x+5)<.1f&&Math.Abs(from.z-12)<.1f?[from,new(-4,0,12),to]:[from,to];
  Physics.Ray=(from,dir)=>(walls&&Math.Abs(from.x+4.5f)<.1f,1f);p.Observe(b);
  for(int i=0;i<250&&p.states[b].Crossfire.Policy.Trace!.State=="planning"&&p.states[b].Crossfire.Policy.Trace!.Entrance!=null;i++){Step(.02f);Refresh(b);p.Sample(b);}
  var r=p.states[b].Crossfire;C(walls?r.Policy.Trace!.State=="ready"&&r.Entrance.Virtual&&r.Policy.Trace.Entrance!.Passage.StartsWith("passage:"):r.Policy.Trace!.Entrance==null,"doorless throat requires real geometric evidence "+walls);Save(p);
 }
 foreach(string mode in new[]{"human","scav","ghost","remote","disabled","contact-instance","squad-change","visible","support-not-covering","primary-blocked"}){
  var(p,b,m)=Setup();Plan(p,b);var action=Start(p,b);Arrive(p,b,action);var r=p.states[b].Crossfire;
  switch(mode){case"human":b.Player.IsAI=false;break;case"scav":b.Pmc=false;break;case"ghost":p.Ghosts.Add(b.ProfileId);break;case"remote":p.Authority=false;break;case"disabled":p.config!.SecondaryEntrance.Enabled=false;break;case"contact-instance":b.GoalEnemy=new();break;case"squad-change":b.Squad.Members.Add("new",new());break;case"visible":b.GoalEnemy!.IsVisible=true;break;case"support-not-covering":m.GoalEnemy!.IsVisible=false;m.Player.LookDirection=new(1,0,0);break;case"primary-blocked":b.BotOwner.VoxelesPersonalData.CurVoxel.DoorLinks[0].Door.DoorState=EDoorState.Shut;break;}
  Wait(p,b,action,21);C(!r.Policy.Active&&p.Claims==0&&b.Mover.Walks==1,"S02 guards "+mode);Save(p);
 }
 {
  var(p,b,m)=Setup();Plan(p,b);var a=Start(p,b);Arrive(p,b,a);Wait(p,b,a,16);var r=p.states[b].Crossfire;var owned=(BotPathDataManual)b.Mover.ActivePath!;m.BotOwner.WeaponManager.Reload.Reloading=true;Step();Refresh(b);a.Update(new());C(!r.Policy.Active&&owned.Stops==1&&p.Claims==0,"partner support loss interrupts actual entry");Save(p);
 }
 File.WriteAllLines(Path.GetFullPath("artifacts/s02-adapter-events.jsonl"),events);Console.WriteLine($"{n} S02 production-adapter checks passed; {events.Count} events exported");Physics.Cast=null;Physics.Ray=null;NavMesh.Route=null;NavMesh.Sample=null;return n;
 }
}
