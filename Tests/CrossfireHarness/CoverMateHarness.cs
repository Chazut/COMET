using Comet.Client;
using EFT;
using EFT.InventoryLogic;
using SAIN;
using SAIN.Components;
using SAIN.SAINComponent.Classes.Mover;
using UnityEngine;
using UnityEngine.AI;
internal static class CoverMateHarness
{
 public static void Run()
 {
  int checks=0;var events=new List<string>();void C(bool ok,string why){checks++;if(!ok)throw new Exception("S04 adapter: "+why);}
  void Step(float dt=.05f){Time.time+=dt;Time.realtimeSinceStartup=Time.time;Time.frameCount++;}
  (Plugin p,BotComponent guard,BotComponent mate) Setup(bool surgery=false,bool move=false)
  {
   Time.time=Time.realtimeSinceStartup=10;Time.frameCount=0;Physics.Blocked=false;Physics.Cast=null;Physics.Ray=null;NavMesh.Valid=true;NavMesh.Sample=null;NavMesh.Route=null;Api.Decisions.Labels.Clear();SAINEnableClass.Bots.Clear();
   var p=new Plugin();p.config!.Crossfire.Enabled=false;p.config.SquadManeuver.Enabled=false;p.Protection=(a,b)=>3;
   var guard=new BotComponent{Position=new(move?-10:-4,0,0)};var mate=new BotComponent{Position=new(0,0,0)};
   var members=new[]{guard,mate}.ToDictionary(x=>x.ProfileId);foreach(var b in members.Values){b.Squad.Members=members;b.GoalEnemy!.KnownPlaces.LastKnownPosition=new(0,0,30);b.EnemyController.KnownEnemies.Add(b.GoalEnemy);SAINEnableClass.Bots[b.ProfileId]=b;}
   if(surgery){mate.BotOwner.Medecine.Using=mate.BotOwner.Medecine.SurgicalKit.Using=true;var kit=new MedKitClass();mate.BotOwner.Medecine.SurgicalKit.CurUsingMeds=kit;mate.Player.HandsController=new MedHands{Item=kit};}
   else mate.BotOwner.WeaponManager.Reload.Reloading=true;
   return(p,guard,mate);
  }
  CrossfireAction Start(Plugin p,BotComponent guard)
  {
   guard.Decision.CurrentCombatDecision=SAIN.Preset.Shared.Enums.ECombatDecision.Search;
   guard.Mover.ActivePath=new BotPathDataManual{TimeStarted=Time.time,OnStop=()=>guard.Mover.ActivePath=null};
   p.Observe(guard);
   for(int n=0;n<50&&p.states[guard].Crossfire.Policy.Trace?.State=="planning";n++){Step();p.Sample(guard);}
   C(p.states[guard].Crossfire.Policy.Trace?.State=="ready","selected native destination: "+p.states[guard].Crossfire.Policy.Trace?.Reason);
   C(new CrossfireLayer(guard.BotOwner,21).IsActive(),"native layer ownership");
   var action=new CrossfireAction(guard.BotOwner);action.Start();action.OnSteeringTicked();Step();action.Update(new());action.OnSteeringTicked();return action;
  }
  {
   var(p,g,m)=Setup();var action=Start(p,g);C(g.Mover.Walks==0,"good existing position retained");C(p.states[g].Crossfire.Policy.Trace!.CoverMate!.Confirmed,"actual facing confirmed");
   C(Api.Decisions.Labels[g.ProfileId]=="COMET:CrossfireCoverMateHolding","observed cover label");C(g.ManualShoot.Requests==0,"no suppressive fire");
   m.BotOwner.WeaponManager.Reload.Reloading=false;((Player.FirearmController)m.Player.HandsController).Ready=false;Step();action.Update(new());C(p.states[g].Crossfire.Policy.Active,"waits after flags end until firearm ready");
   ((Player.FirearmController)m.Player.HandsController).Ready=true;Step();action.Update(new());C(p.states[g].Crossfire.Policy.Active,"readiness grace");Step(.21f);action.Update(new());C(!p.states[g].Crossfire.Policy.Active&&!Api.Decisions.Labels.ContainsKey(g.ProfileId),"ready stops coverage and badge");events.AddRange(p.diagnostics!.Lines);
  }
  {
   var(p,g,m)=Setup(true);var action=Start(p,g);for(int i=0;i<40;i++){Step(.5f);action.Update(new());action.OnSteeringTicked();}
   C(p.states[g].Crossfire.Policy.Active,"surgery cover survives twelve and fifteen seconds");
   for(int i=0;i<32;i++){Step(.5f);action.Update(new());action.OnSteeringTicked();}
   C(!p.states[g].Crossfire.Policy.Active&&p.states[g].Crossfire.Policy.Trace!.Reason.Contains("stalled"),"native duration plus margin detects stuck surgery");
   Step(5);p.Observe(g);C(!p.states[g].Crossfire.Policy.Active,"stuck operation cannot re-recruit");C(m.BotOwner.Medecine.Using,"does not cancel patient medicine");events.AddRange(p.diagnostics!.Lines);
  }
  {
   var(p,g,m)=Setup();g.GoalEnemy!.IsVisible=true;var a=Start(p,g);Step();a.Update(new());
   C(p.states[g].Crossfire.Policy.Active,"visible enemy does not cancel covering a busy teammate");p.EndAll();events.AddRange(p.diagnostics!.Lines);
  }
  foreach(var mode in new[]{"heal-self","reload-self","ghost-self","ghost-mate","remote","mate-moved","mate-dead","under-fire","callbacks","foreign-route","config-off"})
  {
   var(p,g,m)=Setup();var a=Start(p,g);var foreign=new BotPathDataManual{Destination=new(90,0,0)};
   switch(mode){case "visible":g.GoalEnemy!.IsVisible=true;break;case "heal-self":g.BotOwner.Medecine.Using=true;break;case "reload-self":g.BotOwner.WeaponManager.Reload.Reloading=true;break;case "ghost-self":p.Ghosts.Add(g.ProfileId);break;case "ghost-mate":p.Ghosts.Add(m.ProfileId);break;case "remote":p.Authority=false;break;case "mate-moved":m.Position=new(2,0,0);break;case "mate-dead":m.IsDead=true;break;case "under-fire":g.BotOwner.Memory.IsUnderFire=true;break;case "callbacks":Step(1.1f);break;case "foreign-route":g.Mover.ActivePath=foreign;break;case "config-off":p.config!.CoverMate.Enabled=false;break;}
   a.Update(new());C(!p.states[g].Crossfire.Policy.Active,"interruption "+mode);C(foreign.Stops==0,"preserve unrelated path "+mode);events.AddRange(p.diagnostics!.Lines);
  }
  foreach(var mode in new[]{"decision-only","non-pmc-guard","non-pmc-mate","human","no-known","stale","no-cover","too-far","blocked-volume","partial-route"})
  {
   var(p,g,m)=Setup(false,mode=="partial-route");
   switch(mode){case "decision-only":m.BotOwner.WeaponManager.Reload.Reloading=false;m.Decision.CurrentSelfDecision=SAIN.Preset.Shared.Enums.ESelfActionType.Reload;break;case "non-pmc-guard":g.Pmc=false;break;case "non-pmc-mate":m.Pmc=false;break;case "human":g.Player.IsAI=false;break;case "no-known":g.GoalEnemy!.EnemyKnown=false;break;case "stale":g.GoalEnemy!.TimeSinceLastKnownUpdated=31;break;case "no-cover":p.Protection=(a,b)=>0;break;case "too-far":m.Position=new(40,0,0);break;case "blocked-volume":p.Volume="body blocked";break;case "partial-route":g.Mover.Status=NavMeshPathStatus.PathPartial;break;}
   p.Observe(g);for(int i=0;i<70;i++){Step();if(p.states.ContainsKey(g))p.Sample(g);}
   C(!p.states.TryGetValue(g,out var state)||!state.Crossfire.Policy.Active,"reject "+mode);C(g.Mover.Walks==0,"no rejected movement "+mode);events.AddRange(p.diagnostics!.Lines);
  }
  {
   var(p,g,m)=Setup(false,true);var a=Start(p,g);C(g.Mover.Walks==1&&p.states[g].Crossfire.Policy.Trace!.State=="moving","observed approach");
   var route=(BotPathDataManual)g.Mover.ActivePath!;g.Position=p.states[g].Crossfire.Goal;Step();a.Update(new());a.OnSteeringTicked();Step();a.Update(new());
   C(route.Stops==1&&p.states[g].Crossfire.Policy.Trace!.CoverMate!.Confirmed,"arrival owns cancellation then confirmed cover");events.AddRange(p.diagnostics!.Lines);
  }
  {
   var(p,g,m)=Setup();var a=Start(p,g);var second=new BotComponent{Position=new(0,0,-4)};second.GoalEnemy!.EnemyProfileId="east";second.GoalEnemy.KnownPlaces.LastKnownPosition=new(30,0,0);second.Player.LookDirection=new(1,0,0);second.EnemyController.KnownEnemies.Add(second.GoalEnemy);g.Squad.Members[second.ProfileId]=second;second.Squad.Members=g.Squad.Members;SAINEnableClass.Bots[second.ProfileId]=second;
   // Membership is fixed before a new plan; restart the first operation's coverage in an independent setup below.
   a.Update(new());C(!p.states[g].Crossfire.Policy.Active,"squad identity change yields");p.EndAll();events.AddRange(p.diagnostics!.Lines);
  }
  {
   var(p,g,m)=Setup();var second=new BotComponent{Position=new(0,0,-4)};second.GoalEnemy!.EnemyProfileId="east";second.GoalEnemy.KnownPlaces.LastKnownPosition=new(30,0,0);second.Player.LookDirection=new(1,0,0);second.EnemyController.KnownEnemies.Add(second.GoalEnemy);g.Squad.Members[second.ProfileId]=second;second.Squad.Members=g.Squad.Members;SAINEnableClass.Bots[second.ProfileId]=second;
   var a=Start(p,g);var b=Start(p,second);C(p.states[second].Crossfire.Policy.Trace!.CoverMate!.Protectors==2,"two distinct sectors execute");
   p.EndAll();C(p.Claims==0&&Api.Decisions.Labels.Count==0,"cleanup both protectors");events.AddRange(p.diagnostics!.Lines);
  }
  foreach(var mode in new[]{"facing-stall","movement-stall","replacement"})
  {
   var(p,g,m)=Setup(false,mode!="facing-stall");var a=Start(p,g);
   var old=g.Mover.ActivePath as BotPathDataManual;
   var foreign=new BotPathDataManual{Destination=new(100,0,0)};
   if(mode=="replacement")g.Mover.ActivePath=foreign;
   if(mode=="facing-stall")g.Player.LookDirection=new(0,0,-1);
   for(int i=0;i<8&&p.states[g].Crossfire.Policy.Active;i++){Step(.5f);a.OnSteeringTicked();a.Update(new());}
   C(!p.states[g].Crossfire.Policy.Active,"bounded failure "+mode);
   C(!Api.Decisions.Labels.ContainsKey(g.ProfileId),"remove active label "+mode);
   C(foreign.Stops==0,"do not cancel foreign path "+mode);
   if(mode=="movement-stall")C(old!.Stops==1,"cancel stalled owned route");events.AddRange(p.diagnostics!.Lines);
  }
  {
   var(p,g,m)=Setup();var second=new BotComponent{Position=new(0,0,-4)};
   second.GoalEnemy!.KnownPlaces.LastKnownPosition=new(0,0,30);second.EnemyController.KnownEnemies.Add(second.GoalEnemy);
   g.Squad.Members[second.ProfileId]=second;second.Squad.Members=g.Squad.Members;SAINEnableClass.Bots[second.ProfileId]=second;
   Start(p,g);p.Observe(second);C(!p.states[second].Crossfire.Policy.Active,"same sector cannot recruit second protector");p.EndAll();events.AddRange(p.diagnostics!.Lines);
  }
  {
   var(p,g,m)=Setup(true,true);var a=Start(p,g);
   for(int i=0;i<26&&p.states[g].Crossfire.Policy.Active;i++){g.Position+=(p.states[g].Crossfire.Goal-g.Position).normalized*.25f;Step(.5f);a.Update(new());a.OnSteeringTicked();}
   C(!p.states[g].Crossfire.Policy.Active&&p.states[g].Crossfire.Policy.Trace!.Reason=="cover-mate-move-timeout","long treatment does not extend approach deadline");events.AddRange(p.diagnostics!.Lines);
  }
  foreach(var mode in new[]{"actual-firstaid","actual-surgery","medical-hands","own-known-without-goal"})
  {
   var(p,g,m)=Setup();m.BotOwner.WeaponManager.Reload.Reloading=false;
   switch(mode){case "actual-firstaid":m.BotOwner.Medecine.FirstAid.Using=true;break;case "actual-surgery":m.BotOwner.Medecine.SurgicalKit.Using=true;break;case "medical-hands":m.Player.HandsController=new MedHands();break;default:m.BotOwner.WeaponManager.Reload.Reloading=true;g.GoalEnemy=null;break;}
   Start(p,g);C(p.states[g].Crossfire.Policy.Trace!.CoverMate!.Confirmed,"actual observation "+mode);p.EndAll();events.AddRange(p.diagnostics!.Lines);
  }
  File.WriteAllLines("artifacts/s04-adapter-events.jsonl",events);Console.WriteLine($"{checks} S04 production-adapter checks passed; {events.Count} events exported");
 }
}
