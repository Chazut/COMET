using Comet.Client;
using EFT;
using SAIN;
using SAIN.Components;
using SAIN.Preset.Shared.Enums;
using SAIN.SAINComponent.Classes.EnemyClasses;
using SAIN.SAINComponent.Classes.Mover;
using UnityEngine;
using UnityEngine.AI;
int n=0;var events=new List<string>();void C(bool ok,string why){n++;if(!ok)throw new Exception("S01 adapter: "+why);}
void Step(float dt=.02f){Time.time+=dt;Time.realtimeSinceStartup=Time.time;Time.frameCount++;}
(Plugin p,BotComponent b,BotComponent support) Setup(){Time.time=Time.realtimeSinceStartup=10;Time.frameCount=0;NavMesh.Valid=true;Physics.Blocked=false;SAINEnableClass.Bots.Clear();Api.Decisions.Labels.Clear();var p=new Plugin();p.config!.Crossfire.Participation=100;var b=new BotComponent{Position=new(-3,0,0)};var support=new BotComponent();support.GoalEnemy!.IsVisible=true;var members=new Dictionary<string,BotComponent>{{b.ProfileId,b},{support.ProfileId,support}};foreach(var bot in members.Values){bot.Squad.Members=members;bot.EnemyController.KnownEnemies.Add(bot.GoalEnemy!);SAINEnableClass.Bots[bot.ProfileId]=bot;}return(p,b,support);}
void Plan(Plugin p,BotComponent b,Vector3? goal=null){p.Observe(b);if(goal.HasValue){var r=p.states[b].Crossfire;r.Candidates.Clear();r.Candidates.Add(goal.Value);r.Candidate=0;r.Route=null;r.Samples.Clear();}for(int i=0;i<200&&p.states[b].Crossfire.Policy.Trace?.State=="planning";i++){Step();p.Observe(b);}C(p.states[b].Crossfire.Policy.Trace?.State=="ready","reachable useful angle planned: "+p.states[b].Crossfire.Policy.Trace?.Reason);}
CrossfireAction Start(Plugin p,BotComponent b){var l=new CrossfireLayer(b.BotOwner,21);C(l.IsActive()&&b.SAINLayersActive,"native layer active");C(l.GetNextAction().Type==typeof(CrossfireAction),"native action selected");C(p.Choose(b),"generic choice yields while crossfire owns plan");Step();var a=new CrossfireAction(b.BotOwner);a.Start();C(a.Starts==1,"native lifecycle start");C(p.states[b].Crossfire.Policy.Trace?.State=="moving","route observed");return a;}
void Save(Plugin p)=>events.AddRange(p.diagnostics!.Lines);
{
 var(p,b,m)=Setup();Plan(p,b);C(p.Claims==1&&!Api.Decisions.Labels.ContainsKey(b.ProfileId),"planning is not execution");
 var a=Start(p,b);var r=p.states[b].Crossfire;C(r.Policy.Trace!.Applied,"native route applied");
 C(r.Policy.Trace.MoveOrders==1&&r.Policy.Trace.MoveAccepted,"native move request recorded");
 C(r.Policy.Trace.SprintRequested==(Vector3.ProjectOnPlane(r.Goal-b.Position,Vector3.up).magnitude>8),"source sprint distance rule preserved");
 a.OnSteeringTicked();C(b.Steering.Moves==1,"steering follows native route");
 var path=(BotPathDataManual)b.Mover.ActivePath!;b.Position=r.Goal;Step();a.Update(new());
 C(r.Policy.Trace.State=="holding"&&path.Stops==1,"arrival stops route and holds angle");
 b.GoalEnemy!.IsVisible=true;p.Sample(b);C(r.Policy.Active,"visibility does not cancel source angle");
 a.OnSteeringTicked();C(b.Steering.Points>0,"hold steers to contact");
 Step(20.1f);p.Sample(b);C(!r.Policy.Active,"source angle hold has bounded lifetime");a.Stop();Save(p);
}
{
 var(p,b,m)=Setup();b.GoalEnemy!.KnownPlaces.LastKnownPosition=new(0,0,12);m.GoalEnemy!.EnemyPosition=new(0,0,10);b.GoalEnemy.EnemyPlayer.Environment=EnvironmentType.Indoor;
 Plan(p,b);var r=p.states[b].Crossfire;
 C(Vector3.Distance(r.Known,m.GoalEnemy.EnemyPosition)<.001f,"source uses supporting bot's visible target, not own stale memory");
 C(r.Indoor,"target indoors uses indoor candidates even when bot is outside");
 C(r.Policy.Trace!.Source=="visible-squadmate-enemy-position","trace names actual source");
}
foreach(string scope in new[]{"scav","human","dead","inactive","ghost","remote","world","enabled","feature","ending"}){
 var(p,b,m)=Setup();Plan(p,b);Start(p,b);var path=(BotPathDataManual)b.Mover.ActivePath!;
 switch(scope){case"scav":b.Pmc=false;break;case"human":b.Player.IsAI=false;break;case"dead":b.IsDead=true;break;case"inactive":b.BotActive=false;break;case"ghost":p.Ghosts.Add(b.ProfileId);break;case"remote":p.Authority=false;break;case"world":p.world=null;break;case"enabled":p.config!.Enabled=false;break;case"feature":p.config!.Crossfire.Enabled=false;break;case"ending":b.GameEnding=true;break;}
 p.Sample(b);C(!p.states[b].Crossfire.Policy.Active&&p.Claims==0&&path.Stops==1,"integration releases route: "+scope);Save(p);
}
foreach(string change in new[]{"visibility","support-dead","support-ghost","support-hidden","support-moved","suppression","ammo","hands","reload","airborne","injury","squad","position"}){
 var(p,b,m)=Setup();Plan(p,b);Start(p,b);var r=p.states[b].Crossfire;
 switch(change){case"visibility":b.GoalEnemy!.IsVisible=true;break;case"support-dead":m.IsDead=true;break;case"support-ghost":p.Ghosts.Add(m.ProfileId);break;case"support-hidden":m.GoalEnemy!.IsVisible=false;break;case"support-moved":m.Position=new(80,0,0);break;case"suppression":b.Suppression.IsSuppressed=true;break;case"ammo":b.BotOwner.WeaponManager.HaveBullets=false;break;case"hands":((Player.FirearmController)b.Player.HandsController).Ready=false;break;case"reload":b.BotOwner.WeaponManager.Reload.Reloading=true;break;case"airborne":b.Player.MovementContext.IsGrounded=false;break;case"injury":b.Memory.Health.HealthStatus=ETagStatus.BadlyInjured;break;case"squad":b.Decision.CurrentSquadDecision=ESquadDecision.Retreat;break;case"position":b.GoalEnemy!.KnownPlaces.LastKnownPosition=new(40,0,40);break;}
 p.Sample(b);Step(2);p.Sample(b);C(r.Policy.Active,"source active plan survives former COMET veto: "+change);p.End(b);C(p.Claims==0,"cleanup releases claim");Save(p);
}
foreach(string reason in new[]{"incoming-fire","contact-changed","timeout"}){
 var(p,b,m)=Setup();Plan(p,b);Start(p,b);
 if(reason=="incoming-fire")b.BotOwner.Memory.IsUnderFire=true;else if(reason=="contact-changed")b.GoalEnemy!.EnemyProfileId="different";else Step(12.1f);
 p.Sample(b);C(!p.states[b].Crossfire.Policy.Active&&p.Claims==0,"source termination: "+reason);Save(p);
}
{
 var(p,b,m)=Setup();p.config!.Crossfire.Participation=0;p.Observe(b);C(!p.states[b].Crossfire.Policy.Active&&p.Claims==0,"source personality roll may decline");Save(p);
}
{
 var(p,b,m)=Setup();NavMesh.Valid=false;p.Observe(b);for(int i=0;i<250;i++){Step();p.Sample(b);}C(b.Mover.Walks==0,"no invented route on invalid NavMesh");p.EndAll();C(p.Claims==0,"raid cleanup");Save(p);
}
var file=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../../artifacts/s01-adapter-events.jsonl"));File.WriteAllLines(file,events);
Console.WriteLine($"S01 source adapter: {n} checks; {events.Count} events. Engine doubles, no raid simulation.");
