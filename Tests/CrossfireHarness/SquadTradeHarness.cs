using Comet.Client;
using EFT;
using SAIN;
using SAIN.Components;
using SAIN.SAINComponent.Classes.Mover;
using UnityEngine;
using UnityEngine.AI;
internal static class SquadTradeHarness
{
 public static void Run()
 {
  int n=0;var events=new List<string>();void C(bool ok,string why){n++;if(!ok)throw new Exception("S05 adapter: "+why);}
  void Step(float dt=.05f){Time.time+=dt;Time.realtimeSinceStartup=Time.time;Time.frameCount++;}
  (Plugin p,BotComponent b,BotComponent mate,Player attacker) Setup(bool push=false)
  {
   Time.time=Time.realtimeSinceStartup=10;Time.frameCount=0;Physics.Blocked=false;Physics.Cast=null;Physics.Ray=null;NavMesh.Valid=true;NavMesh.Sample=null;NavMesh.Route=null;Api.Decisions.Labels.Clear();SAINEnableClass.Bots.Clear();
   var p=new Plugin();p.config!.Crossfire.Enabled=p.config.SquadManeuver.Enabled=p.config.CoverMate.Enabled=false;p.config.SquadTrade.AggressiveChance=push?100:0;p.Protection=(a,z)=>3;
   var b=new BotComponent();var mate=new BotComponent{Position=new(4,0,0)};
   var members=new[]{b,mate}.ToDictionary(x=>x.ProfileId);foreach(var x in members.Values){x.Squad.Members=members;x.GoalEnemy!.KnownPlaces.LastKnownPosition=new(0,0,12);x.EnemyController.KnownEnemies.Add(x.GoalEnemy);SAINEnableClass.Bots[x.ProfileId]=x;}
   mate.Player.Position=mate.Position;mate.Player.Profile.Nickname="Lost teammate";mate.Player.HealthController.IsAlive=false;mate.IsDead=true;
   return(p,b,mate,new Player{ProfileId="enemy"});
  }
  void Notify(Plugin p,BotComponent b,BotComponent m,Player killer,bool seen=true)
  {b.GoalEnemy!.IsVisible=seen;p.Death(m,b,killer);b.GoalEnemy.IsVisible=false;}
  CrossfireAction Start(Plugin p,BotComponent b)
  {
   b.Decision.CurrentCombatDecision=SAIN.Preset.Shared.Enums.ECombatDecision.Search;
   b.Mover.ActivePath=new BotPathDataManual{TimeStarted=Time.time,OnStop=()=>b.Mover.ActivePath=null};
   Step(.81f);p.Observe(b);for(int i=0;i<85&&p.states[b].Crossfire.Policy.Trace?.State=="planning";i++){Step(.025f);p.Sample(b);}
   C(p.states[b].Crossfire.Policy.Trace?.State=="ready","ready: "+p.states[b].Crossfire.Policy.Trace?.Reason+" memory="+p.states[b].Crossfire.Trade.Memory.Count+" warnings="+string.Join(" / ",Logger.Warnings));
   C(new CrossfireLayer(b.BotOwner,21).IsActive(),"native layer");var a=new CrossfireAction(b.BotOwner);a.Start();a.OnSteeringTicked();Step();a.Update(new());return a;
  }
  foreach(bool push in new[]{false,true})
  {
   var(p,b,m,k)=Setup(push);Notify(p,b,m,k);p.Observe(b);C(p.states[b].Crossfire.Policy.Trace==null,"delay before roll");var a=Start(p,b);
   C(p.states[b].Crossfire.Policy.Trace!.Trade!.Choice==(push?"push":"watch"),"chosen role");C(b.Mover.Walks==(push?1:0),"actual movement only when needed");
   if(push){b.Position=p.states[b].Crossfire.Goal;b.Player.LookDirection=(p.states[b].Crossfire.Known-b.Position).normalized;Step();a.Update(new());a.OnSteeringTicked();Step();a.Update(new());}
   C(Api.Decisions.Labels[b.ProfileId]=="COMET:CrossfireTradeWatching","observed watching");C(b.ManualShoot.Requests==0,"never shoots hidden enemy");
   b.GoalEnemy!.IsVisible=true;Step();a.Update(new());C(p.states[b].Crossfire.Policy.Active,"visible target does not cancel an established retaliation angle");
   b.GoalEnemy.IsVisible=false;b.BotOwner.Memory.IsUnderFire=true;Step();a.Update(new());C(!p.states[b].Crossfire.Policy.Active,"incoming fire still interrupts retaliation");
   b.BotOwner.Memory.IsUnderFire=false;Step(5);p.Death(m,b,k);p.Observe(b);C(!p.states[b].Crossfire.Policy.Active,"no repeated roll");events.AddRange(p.diagnostics!.Lines);
  }
  foreach(var mode in new[]{"alive","human-dead","downed","not-witnessed","remote","ghost-mate","ghost-self","non-pmc","disabled","group-disappearance"})
  {
   var(p,b,m,k)=Setup();switch(mode){case "alive":m.Player.HealthController.IsAlive=true;break;case "human-dead":m.Player.IsAI=false;break;case "downed":m.Player=new DownedPlayer{ProfileId=m.ProfileId,Downed=true,HealthController=new(){IsAlive=false}};break;case "not-witnessed":p.Witness=false;break;case "remote":p.Authority=false;break;case "ghost-mate":p.Ghosts.Add(m.ProfileId);break;case "ghost-self":p.Ghosts.Add(b.ProfileId);break;case "non-pmc":b.Pmc=false;break;case "disabled":p.config!.SquadTrade.Enabled=false;break;}
   if(mode!="group-disappearance")Notify(p,b,m,k);else b.Squad.Members.Remove(m.ProfileId);
   Step(1);p.Observe(b);C(!p.states.TryGetValue(b,out var s)||s.Crossfire.Policy.Trace?.Trade==null,"no false death "+mode);C(b.Mover.Walks==0,"no movement "+mode);
  }
  foreach(var mode in new[]{"unseen-killer","different-killer","ammo","panic","personality","bad-route"})
  {
   var(p,b,m,k)=Setup(true);if(mode=="different-killer")k.ProfileId="different";Notify(p,b,m,k,mode!="unseen-killer");
   if(mode=="ammo")b.BotOwner.WeaponManager.CurrentWeapon.Mag.Count=14;if(mode=="panic")p.states[b].Fear.Current.Panic=true;
   if(mode=="personality"){b.Info.PersonalitySettings.Rush.CanRushEnemyReloadHeal=false;p.config!.SquadTrade.OtherChance=0;}
   if(mode=="bad-route")b.Mover.Reachable=false;
   Start(p,b);C(p.states[b].Crossfire.Policy.Trace!.Trade!.Choice=="watch","safe fallback "+mode);C(b.Mover.Walks==0,"no automatic pursuit "+mode);events.AddRange(p.diagnostics!.Lines);
  }
  foreach(var mode in new[]{"stale","unknown","far","injury","heal","under-fire","group-change"})
  {
   var(p,b,m,k)=Setup(true);Notify(p,b,m,k);
   switch(mode){case "stale":b.GoalEnemy!.TimeSinceLastKnownUpdated=6;break;case "unknown":b.GoalEnemy!.EnemyKnown=false;break;case "far":b.GoalEnemy!.KnownPlaces.LastKnownPosition=new(0,0,90);break;case "injury":b.Memory.Health.HealthStatus=ETagStatus.BadlyInjured;break;case "heal":b.BotOwner.Medecine.Using=true;break;case "under-fire":b.BotOwner.Memory.IsUnderFire=true;break;case "group-change":b.Squad.Members=new(b.Squad.Members);break;}
   Step(1);p.Observe(b);C(!p.states[b].Crossfire.Policy.Active,"reject opportunity "+mode);C(b.Mover.Walks==0,"reject before movement "+mode);events.AddRange(p.diagnostics!.Lines);
  }
  foreach(var mode in new[]{"stalled","replaced","unsafe-volume","panic","ammo","stale","moved-contact","lost-contact","authority","disabled"})
  {
   var(p,b,m,k)=Setup(true);Notify(p,b,m,k);var a=Start(p,b);var old=(BotPathDataManual)b.Mover.ActivePath!;var foreign=new BotPathDataManual{Destination=new(99,0,0)};
   switch(mode){case "replaced":b.Mover.ActivePath=foreign;break;case "unsafe-volume":p.Volume="new obstruction";break;case "panic":p.states[b].Fear.Current.Panic=true;break;case "ammo":b.BotOwner.WeaponManager.CurrentWeapon.Mag.Count=1;break;case "stale":b.GoalEnemy!.TimeSinceLastKnownUpdated=9;Step(5);break;case "moved-contact":b.GoalEnemy!.KnownPlaces.LastKnownPosition=new(15,0,12);break;case "lost-contact":b.GoalEnemy!.EnemyKnown=false;break;case "visible-goal-only":b.GoalEnemy!.IsVisible=true;b.EnemyController.KnownEnemies.Clear();break;case "authority":p.Authority=false;break;case "disabled":p.config!.SquadTrade.Enabled=false;break;}
   for(int i=0;i<10&&p.states[b].Crossfire.Policy.Active;i++){Step(.5f);if(mode=="stalled")b.GoalEnemy!.TimeSinceLastKnownUpdated=0;a.Update(new());}
   C(!p.states[b].Crossfire.Policy.Active,"stop "+mode);C(!Api.Decisions.Labels.ContainsKey(b.ProfileId),"badge cleared "+mode);C(foreign.Stops==0,"foreign path preserved "+mode);if(mode!="replaced")C(old.Stops==1,"owned route stopped "+mode);events.AddRange(p.diagnostics!.Lines);
  }
  {
   var(p,b,m,k)=Setup();Notify(p,b,m,k);b.Squad.Members.Remove(m.ProfileId);b.Squad.BotInGroup=false;Start(p,b);C(p.states[b].Crossfire.Policy.Active,"last survivor after confirmed death");p.EndAll();C(p.Claims==0,"cleanup");events.AddRange(p.diagnostics!.Lines);
  }
  {
   var(p,b,m,k)=Setup();var others=new[]{new BotComponent{Position=new(4,0,0)},new BotComponent{Position=new(-4,0,0)}};
   foreach(var o in others){o.Squad.Members=b.Squad.Members;b.Squad.Members[o.ProfileId]=o;o.EnemyController.KnownEnemies.Add(o.GoalEnemy!);SAINEnableClass.Bots[o.ProfileId]=o;}
   Notify(p,b,m,k);Start(p,b);foreach(var o in others)Start(p,o);
   C(others.All(o=>p.states[o].Crossfire.Policy.Active),"three survivors may independently watch distinct angles");p.EndAll();C(p.Claims==0,"all reservations released");events.AddRange(p.diagnostics!.Lines);
  }
  {
   var(p,b,m,k)=Setup();Notify(p,b,m,k);var a=Start(p,b);
   for(int i=0;i<32&&p.states[b].Crossfire.Policy.Active;i++){Step(.5f);a.OnSteeringTicked();a.Update(new());}
   C(p.states[b].Crossfire.Policy.Trace!.Reason=="trade-timeout","15s total watchdog");events.AddRange(p.diagnostics!.Lines);
  }
  {
   var(p,b,m,k)=Setup(true);Notify(p,b,m,k);b.BotOwner.Medecine.Using=true;Step(1);p.Observe(b);
   C(!p.states[b].Crossfire.Policy.Active&&p.states[b].Crossfire.Trade.Memory.Count>0,"temporary medicine block retains witnessed death");
   b.BotOwner.Medecine.Using=false;Start(p,b);C(p.states[b].Crossfire.Policy.Active,"trade can start after temporary block without new death");p.EndAll();
  }
  Directory.CreateDirectory("artifacts");File.WriteAllLines("artifacts/s05-adapter-events.jsonl",events);Console.WriteLine($"{n} S05 native-adapter checks passed; {events.Count} events");
 }
}
