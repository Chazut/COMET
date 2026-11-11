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
 var(p,b,m)=Setup();Plan(p,b);C(p.Claims==1&&!Api.Decisions.Labels.ContainsKey(b.ProfileId),"reservation not execution");var a=Start(p,b);var r=p.states[b].Crossfire;C(r.Policy.Trace!.Applied&&Api.Decisions.Labels[b.ProfileId]=="COMET:CrossfireMoving","moving label only after actual route");a.OnSteeringTicked();C(b.Steering.Moves==1,"native steering hook");var path=(BotPathDataManual)b.Mover.ActivePath!;b.Position=r.Goal;Step();a.Update(new());C(r.Policy.Trace.State=="holding"&&path.Stops==1,"arrival stops owned route");a.OnSteeringTicked();C(b.Steering.Points==1,"watch remembered contact");int logs=p.diagnostics!.Lines.Count;for(int i=0;i<10;i++)p.Sample(b);C(p.diagnostics.Lines.Count==logs,"unchanged same-frame coalescing");b.GoalEnemy!.IsVisible=true;p.Sample(b);C(!r.Policy.Active&&p.Claims==0&&!Api.Decisions.Labels.ContainsKey(b.ProfileId),"visible contact returns native combat immediately");a.Stop();C(a.Stops==1,"native stop lifecycle");Save(p);
}
{
 var(p,b,m)=Setup();var two=new BotComponent{Position=new(3,0,0)};var three=new BotComponent{Position=new(5,0,-3)};
 foreach(var bot in new[]{two,three}){m.Squad.Members.Add(bot.ProfileId,bot);bot.Squad.Members=m.Squad.Members;bot.EnemyController.KnownEnemies.Add(bot.GoalEnemy!);SAINEnableClass.Bots[bot.ProfileId]=bot;}
 Plan(p,b,new(-12,0,10));Start(p,b);Plan(p,two,new(12,0,10));Start(p,two);Plan(p,three,new(12,0,20));Start(p,three);
 C(p.Claims==3&&new[]{b,two,three}.All(x=>p.states[x].Crossfire.Policy.Trace!.Applied),"4v1: all three teammates may reposition simultaneously");
 C(new[]{b,two,three}.Select(x=>p.states[x].Crossfire.Policy.Trace!.Angle).Distinct().Count()>1,"distinct useful angles");p.EndAll();C(p.Claims==0&&Api.Decisions.Labels.Count==0,"raid end releases all reservations and labels");Save(p);
}
{
 var(p,b,m)=Setup();p.config!.Crossfire.Participation=0;p.Observe(b);C(p.states[b].Crossfire.Policy.Trace?.Reason=="crossfire-keep-current-plan"&&p.Claims==0&&b.Mover.Walks==0,"individual may decline, even with support");int logs=p.diagnostics!.Lines.Count;p.Observe(b);C(logs==p.diagnostics.Lines.Count,"no reroll or log spam during retry");Save(p);
}
foreach(string scope in new[]{"scav","human","dead","inactive","ghost","remote","world","enabled","feature","ending"}){
 var(p,b,m)=Setup();Plan(p,b);var a=Start(p,b);var path=(BotPathDataManual)b.Mover.ActivePath!;
 switch(scope){case"scav":b.Pmc=false;break;case"human":b.Player.IsAI=false;break;case"dead":b.IsDead=true;break;case"inactive":b.BotActive=false;break;case"ghost":p.Ghosts.Add(b.ProfileId);break;case"remote":p.Authority=false;break;case"world":p.world=null;break;case"enabled":p.config!.Enabled=false;break;case"feature":p.config!.Crossfire.Enabled=false;break;case"ending":b.GameEnding=true;break;}
 p.Sample(b);C(!p.states[b].Crossfire.Policy.Active&&p.Claims==0&&path.Stops==1,"scope releases owned route "+scope);Save(p);
}
foreach(string priority in new[]{"visible","under-fire","suppression","grenade","flash","self","squad","medicine","reload","throw","hands","ammo","airborne","injury","door","medical-guard","recovery","relocation","feint","door-plan","native-combat","contact-changed","position-changed","no-contact","unknown","support-dead","support-ghost","support-moved"}){
 var(p,b,m)=Setup();Plan(p,b);Start(p,b);var r=p.states[b].Crossfire;var path=(BotPathDataManual)b.Mover.ActivePath!;
 switch(priority){case"visible":b.GoalEnemy!.IsVisible=true;break;case"under-fire":b.BotOwner.Memory.IsUnderFire=true;break;case"suppression":b.Suppression.IsSuppressed=true;break;case"grenade":b.Grenade.GrenadeDangerPoint=new();break;case"flash":b.Flashed.IsFlashed=true;break;case"self":b.Decision.CurrentSelfDecision=ESelfActionType.Heal;break;case"squad":b.Decision.CurrentSquadDecision=ESquadDecision.Retreat;break;case"medicine":b.BotOwner.Medecine.Using=true;break;case"reload":b.BotOwner.WeaponManager.Reload.Reloading=true;break;case"throw":b.BotOwner.WeaponManager.Grenades.ThrowindNow=true;break;case"hands":((Player.FirearmController)b.Player.HandsController).Ready=false;break;case"ammo":b.BotOwner.WeaponManager.HaveBullets=false;break;case"airborne":b.Player.MovementContext.IsGrounded=false;break;case"sprint":b.Player.IsSprintEnabled=true;break;case"injury":b.Memory.Health.HealthStatus=ETagStatus.BadlyInjured;break;case"door":b.BotOwner.DoorOpener.Interacting=true;break;case"medical-guard":p.Guard=true;break;case"recovery":p.states[b].PostCombat.Policy.Active=true;break;case"relocation":p.states[b].Relocation.Policy.Active=true;break;case"feint":p.states[b].CoverFeint.Policy.Active=true;break;case"door-plan":p.states[b].Feature="D01";p.states[b].Decision="Hold";break;case"native-combat":b.Decision.CurrentCombatDecision=ECombatDecision.RushEnemy;break;case"contact-changed":b.GoalEnemy!.EnemyProfileId="new";break;case"position-changed":b.GoalEnemy!.KnownPlaces.LastKnownPosition=new(40,0,40);break;case"no-contact":b.GoalEnemy=null;break;case"unknown":b.GoalEnemy!.EnemyKnown=false;break;case"stale":b.GoalEnemy!.TimeSinceLastKnownUpdated=9;break;case"support-dead":m.IsDead=true;break;case"support-ghost":p.Ghosts.Add(m.ProfileId);break;case"support-moved":m.Position=new(5,0,0);break;}
 if(priority=="airborne"){p.Sample(b);C(r.Policy.Active,"short ground loss retains active plan");Step(.61f);}
 p.Sample(b);C(!r.Policy.Active&&p.Claims==0,"priority abort "+priority);C(path.Stops==(priority=="airborne"?0:1),"do not cancel airborne native movement "+priority);Save(p);
}
{
 var(p,b,m)=Setup();Plan(p,b);Start(p,b);m.GoalEnemy!.IsVisible=false;p.Sample(b);C(p.states[b].Crossfire.Policy.Active,"brief support loss allowed");Step(2);p.Sample(b);C(!p.states[b].Crossfire.Policy.Active&&p.Claims==0,"support grace expires");Save(p);
}
// Old information cannot start a plan, but it does not erase a progressing remembered route.
{
 var(p,b,m)=Setup();b.GoalEnemy!.TimeSinceLastKnownUpdated=9;p.Observe(b);
 C(!p.states[b].Crossfire.Policy.Active,"stale information cannot initiate flank");
}
{
 var(p,b,m)=Setup();Plan(p,b);var a=Start(p,b);var r=p.states[b].Crossfire;
 b.GoalEnemy!.TimeSinceLastKnownUpdated=9;p.Sample(b);
 C(r.Policy.Active&&r.Policy.Trace!.ContinuingRememberedRoute,"moving remembered route survives initial freshness limit");
 b.Position=r.Goal;Step();a.Update(new());
 C(r.Policy.Trace!.State=="holding","remembered route reaches its validated angle");
 Step();p.Sample(b);C(!r.Policy.Active,"stale arrival does not hold indefinitely");
}
{
 var(p,b,m)=Setup();Plan(p,b);Start(p,b);b.GoalEnemy!.TimeSinceLastKnownUpdated=9;
 Step(3.1f);p.Sample(b);C(!p.states[b].Crossfire.Policy.Active,"stalled remembered route still expires");
}
foreach(string mode in new[]{"foreign-path","reused-path","stalled","early-end","budget-timeout","occupied-goal"}){
 var(p,b,m)=Setup();Plan(p,b);var a=Start(p,b);var path=(BotPathDataManual)b.Mover.ActivePath!;var r=p.states[b].Crossfire;
 switch(mode){case"foreign-path":b.Mover.ActivePath=new BotPathDataManual{Destination=new(90,0,0)};break;case"reused-path":path.TimeStarted+=1;path.Destination=new(90,0,0);break;case"stalled":Step(3.1f);break;case"early-end":b.Mover.ActivePath=null;break;case"budget-timeout":p.doorBudget.Available=false;Step(2.1f);break;case"occupied-goal":m.Position=r.Goal;Step(.6f);break;}
 a.Update(new());C(!r.Policy.Active&&p.Claims==0,"movement ends "+mode);if(mode is "foreign-path" or "reused-path")C(path.Stops==0,"foreign/reused native route never cancelled");Save(p);
}
foreach(string mode in new[]{"blocked-sight","navmesh","partial-route","long-route","lane","other-threat","too-many-threats"}){
 var(p,b,m)=Setup();p.Observe(b);var r=p.states[b].Crossfire;r.Candidates.Clear();r.Candidates.Add(new(-12,0,10));r.Candidate=0;r.Route=null;r.Samples.Clear();
 switch(mode){case"blocked-sight":Physics.Blocked=true;break;case"navmesh":NavMesh.Valid=false;break;case"partial-route":b.Mover.Status=NavMeshPathStatus.PathPartial;break;case"long-route":b.Mover.Corners=g=>[b.Position,new(-100,0,0),g];break;case"lane":b.Mover.Corners=g=>[b.Position,new(0,0,6),g];break;case"other-threat":b.EnemyController.KnownEnemies.Add(new(){EnemyProfileId="other"});p.Protection=(pos,k)=>Vector3.Distance(pos,b.Position)<.01f?7:0;break;case"too-many-threats":for(int i=0;i<17;i++)b.EnemyController.KnownEnemies.Add(new(){EnemyProfileId="other"+i});break;}
 for(int i=0;i<100&&r.Policy.Active;i++){Step();p.Sample(b);}C(!r.Policy.Active&&p.Claims==0&&b.Mover.Walks==0,"unsafe candidate never executes "+mode);Save(p);
}
foreach(string mode in new[]{"refuse","unobserved","native-busy","exception"}){
 var(p,b,m)=Setup();Plan(p,b);switch(mode){case"refuse":b.Mover.Accept=false;break;case"unobserved":b.Mover.Observe=false;break;case"native-busy":p.Queued=true;break;case"exception":b.Mover.Throw=true;break;}
 Step();var a=new CrossfireAction(b.BotOwner);a.Start();if(mode=="native-busy"){Step(1.1f);a.Update(new());}C(!p.states[b].Crossfire.Policy.Active&&p.Claims==0&&!Api.Decisions.Labels.ContainsKey(b.ProfileId),"no fictitious execution "+mode);Save(p);
}
{
 var(p,b,m)=Setup();p.doorBudget.Available=false;p.Observe(b);int queries=b.Mover.Queries;for(int i=0;i<10;i++)p.Observe(b);C(b.Mover.Queries==queries&&p.Claims==0,"shared budget blocks expensive search");p.doorBudget.Available=true;Step();p.Observe(b);queries=b.Mover.Queries;for(int i=0;i<10;i++)p.Observe(b);C(b.Mover.Queries==queries,"at most one planning probe per bot/frame");p.End(b);int count=p.diagnostics!.Lines.Count;for(int i=0;i<10;i++)p.End(b);C(p.diagnostics.Lines.Count==count,"inactive sweeps do not repeat terminal logs");Save(p);
}
foreach(var native in new[]{ECombatDecision.Search,ECombatDecision.SeekCover,ECombatDecision.ShiftCover,ECombatDecision.MoveToEngage,ECombatDecision.CreepOnEnemy,ECombatDecision.Freeze,ECombatDecision.StandAndShoot,ECombatDecision.ShootDistantEnemy,ECombatDecision.None}){
 var(p,b,m)=Setup(); b.Decision.CurrentCombatDecision=native;
 var previous=new BotPathDataManual{TimeStarted=Time.time,Destination=new(1,0,4),OnStop=()=>b.Mover.ActivePath=null}; b.Mover.ActivePath=previous;
 b.Decision.CurrentSquadDecision=ESquadDecision.Regroup;
 Plan(p,b); C(previous.Stops==0,"planning never interrupts the current native route");
 b.Player.IsSprintEnabled=true;
 b.Decision.CurrentCombatDecision=ECombatDecision.Freeze; var a=Start(p,b);
 C(previous.Stops==1,"layer acquisition replaces exactly the observed search route");
 var handoff=p.states[b].Crossfire.Policy.Trace!.Handoff!;
 C(handoff.PlannedDecision==native.ToString()&&handoff.Decision=="Freeze"&&handoff.ExpectedPath!=0&&handoff.CurrentPath==handoff.ExpectedPath,"handoff records native decision and exact observed path");
 C(handoff.Reason=="released-observed-tactical-path"&&handoff.CurrentStarted==handoff.ExpectedStarted,"confirmed release evidence");
 b.Player.IsSprintEnabled=true; a.Update(new());
 C(p.states[b].Crossfire.Policy.Active,"own sprint and ordinary squad regroup do not cancel a flank");
 Save(p);
}
foreach(var native in new[]{ECombatDecision.Retreat,ECombatDecision.RunAway,ECombatDecision.DogFight,ECombatDecision.ThrowGrenade,ECombatDecision.AvoidGrenade,ECombatDecision.MeleeAttack,ECombatDecision.FightZombies}){
 var(p,b,m)=Setup();b.Decision.CurrentCombatDecision=native;
 var path=new BotPathDataManual{TimeStarted=Time.time};b.Mover.ActivePath=path;p.Observe(b);
 C(!p.states[b].Crossfire.Policy.Active&&path.Stops==0,"squad cannot steal protected native decision "+native);
}
{
 var(p,b,m)=Setup(); b.Decision.CurrentCombatDecision=ECombatDecision.Search;
 var previous=new BotPathDataManual{TimeStarted=Time.time,Destination=new(1,0,4)}; b.Mover.ActivePath=previous;
 Plan(p,b); var foreign=new BotPathDataManual{TimeStarted=Time.time+1}; b.Mover.ActivePath=foreign;
 b.Decision.CurrentCombatDecision=ECombatDecision.Freeze;b.Player.IsSprintEnabled=true;
 var a=new CrossfireAction(b.BotOwner); a.Start();
 C(!p.states[b].Crossfire.Policy.Active&&previous.Stops==0&&foreign.Stops==0,"handoff never cancels a replaced route");
}
{
 var(p,b,m)=Setup();b.Decision.CurrentCombatDecision=ECombatDecision.Search;
 var previous=new BotPathDataManual{TimeStarted=Time.time};b.Mover.ActivePath=previous;Plan(p,b);
 b.Mover.ActivePath=null;b.Decision.CurrentCombatDecision=ECombatDecision.Freeze;b.Player.IsSprintEnabled=true;
 Start(p,b);C(previous.Stops==0,"already released native path permits residual sprint without another stop");
}
// Queued during planning: the unchanged native active leg still certifies acquisition.
{
 var(p,b,m)=Setup();b.Decision.CurrentCombatDecision=ECombatDecision.SeekCover;
 var old=new BotPathDataManual{TimeStarted=Time.time,Destination=new(1,0,2)};b.Mover.ActivePath=old;Plan(p,b);
 var queued=new BotPathDataManual{TimeStarted=-1,Destination=new(3,0,4),OnStop=()=>b.Mover.ActivePath=null};
 b.Mover.QueuedPath=queued;old.OnStop=()=>{b.Mover.ActivePath=queued;b.Mover.QueuedPath=null;queued.TimeStarted=Time.time;};
 Start(p,b);C(old.Stops==1&&queued.Stops==1&&p.states[b].Crossfire.Policy.Trace!.Handoff!.QueueCapturedAtHandoff,"late ordinary queued leg released with identity evidence");Save(p);
}
foreach(bool emergency in new[]{false,true})
{
 var(p,b,m)=Setup();b.Decision.CurrentCombatDecision=ECombatDecision.Search;
 var old=new BotPathDataManual{TimeStarted=Time.time,Destination=new(1,0,2)};b.Mover.ActivePath=old;Plan(p,b);
 var queued=new BotPathDataManual{TimeStarted=-1,Destination=new(3,0,4)};b.Mover.QueuedPath=queued;
 if(emergency)b.Grenade.GrenadeDangerPoint=new();else old.TimeStarted+=1;
 new CrossfireAction(b.BotOwner).Start();C(old.Stops==0&&queued.Stops==0&&!p.states[b].Crossfire.Policy.Active,"emergency/restarted path never cancelled with pending queue");
}
{
 var(p,b,m)=Setup();b.Decision.CurrentCombatDecision=ECombatDecision.SeekCover;
 var old=new BotPathDataManual{TimeStarted=Time.time,Destination=new(1,0,2)};
 var queued=new BotPathDataManual{TimeStarted=-1,Destination=new(3,0,4),OnStop=()=>b.Mover.ActivePath=null};
 b.Mover.ActivePath=old;b.Mover.QueuedPath=queued;Plan(p,b);
 // SAIN completes its own transition before BigBrain starts COMET.
 b.Mover.ActivePath=queued;b.Mover.QueuedPath=null;queued.TimeStarted=Time.time;
 Start(p,b);C(old.Stops==0&&queued.Stops==1,"already promoted observed queue can hand over without cancelling old path");Save(p);
}
foreach(bool urgent in new[]{false,true})
{
 var(p,b,m)=Setup();b.Decision.CurrentCombatDecision=ECombatDecision.Search;
 b.Mover.ActivePath=new BotPathDataManual{TimeStarted=Time.time};Plan(p,b);
 var unknown=new BotPathDataManual{TimeStarted=-1,Destination=new(9,0,9)};b.Mover.ActivePath=null;b.Mover.QueuedPath=unknown;
 var action=new CrossfireAction(b.BotOwner);action.Start();
 C(p.states[b].Crossfire.Policy.Active&&unknown.Stops==0,"unobserved queued transition waits without cancellation");
 if(urgent){b.Grenade.GrenadeDangerPoint=new();Step(.01f);}else Step(.8f);
 action.Update(new());C(!p.states[b].Crossfire.Policy.Active&&unknown.Stops==0,"unknown queue wait is bounded and still yields immediately to grenade");
 Save(p);
}
// Ground Zero raid401: short body bounces must retain the same reserved angle.
foreach(float bounce in new[]{.02f,.18f,.42f,.58f}) {
 var(p,b,m)=Setup();Plan(p,b);var a=Start(p,b);var r=p.states[b].Crossfire;
 var path=(BotPathDataManual)b.Mover.ActivePath!;int episode=r.Policy.Trace!.Episode;
 b.Player.MovementContext.IsGrounded=false;p.Sample(b);int walks=b.Mover.Walks;
 Step(bounce);a.Update(new());C(r.Policy.Active&&r.Policy.Trace.GroundPaused&&path.Stops==0&&b.Mover.Walks==walks,"airborne plan held without route mutation");
 b.Player.MovementContext.IsGrounded=true;Step(.001f);a.Update(new());
 C(r.Policy.Active&&r.Policy.Trace.Episode==episode&&ReferenceEquals(path,b.Mover.ActivePath)&&p.Claims==1,"same owned plan resumes after landing");
 C(!r.Policy.Trace.GroundPaused&&r.Policy.Trace.GroundPauses==1&&r.Policy.Trace.GroundPauseSeconds>=bounce,"pause and resume evidence retained");
 b.Position=r.Goal;Step();a.Update(new());C(r.Policy.Trace.State=="holding","recovered angle actually reached");Save(p);
}
foreach(string abort in new[]{"grenade","support","foreign","contact"}) {
 var(p,b,m)=Setup();Plan(p,b);var a=Start(p,b);var r=p.states[b].Crossfire;
 b.Player.MovementContext.IsGrounded=false;p.Sample(b);
 if(abort=="grenade")b.Grenade.GrenadeDangerPoint=new();
 if(abort=="support")m.IsDead=true;
 if(abort=="foreign")b.Mover.ActivePath=new BotPathDataManual{Destination=new(50,0,50)};
 if(abort=="contact")b.GoalEnemy!.EnemyProfileId="changed";
 Step(.1f);a.Update(new());C(!r.Policy.Active&&p.Claims==0,"ground grace retains immediate abort "+abort);Save(p);
}
// Raid432: a nearby valid angle in a narrow room must be tried before distant rings.
{
 var(p,b,m)=Setup();b.GoalEnemy!.KnownPlaces.LastKnownPosition=m.GoalEnemy!.KnownPlaces.LastKnownPosition=new(0,0,8);
 NavMesh.Sample=point=>Vector3.Distance(point,b.Position)<3.1f;
 try{Plan(p,b);C(Vector3.Distance(p.states[b].Crossfire.Goal,b.Position)<3.1f,"nearby reachable angle succeeds when all distant candidates are outside navmesh");}
 finally{NavMesh.Sample=null;}
}
{
 var(p,b,m)=Setup();NavMesh.Sample=_=>false;
 try{
  p.Observe(b);for(int i=0;i<200&&p.states[b].Crossfire.Policy.Active;i++){Step();p.Sample(b);}
  var t=p.states[b].Crossfire.Policy.Trace!;
  C(!p.states[b].Crossfire.Policy.Active&&t.RejectionCounts["no nearby navigation"]==36,"bounded failed search records every rejected candidate");
  C(t.Alternatives.Count(x=>x=="no nearby navigation")==1&&t.CandidateX.HasValue,"repeated rejection keeps compact alternatives and last candidate position");
 }finally{NavMesh.Sample=null;}
}
{
 var(p,b,m)=Setup();var s=p.states[b]=new(b,p.config!,"raid432");
 var f=new Comet.Core.MovementRecoveryFrame{Intent=true,X=b.Position.x,Y=b.Position.y,Z=b.Position.z};
 for(int i=0;i<=60;i++){f.Now=i*.5;var a=s.MovementRecovery.Policy.Observe(f,p.config!.MovementRecovery);if(a.Length>0)s.MovementRecovery.Policy.Result(true,"accepted");}
 p.Observe(b);C(!s.Crossfire.Policy.Active&&p.Claims==0,"C06 exhausted mover cannot immediately receive a new flank route");
 f.X+=.7;f.Now+=.5;s.MovementRecovery.Policy.Observe(f,p.config!.MovementRecovery);b.Position=new((float)f.X,0,0);
 Step(1);Plan(p,b);C(s.Crossfire.Policy.Active,"real progress restores flank eligibility");
}
SecondaryEntranceHarness.Run();
Raid397Harness.Run();
SquadManeuverHarness.Run();
CoverMateHarness.Run();
SquadTradeHarness.Run();
SquadStormHarness.Run();
var dest=Path.GetFullPath("artifacts/s01-adapter-events.jsonl");Directory.CreateDirectory(Path.GetDirectoryName(dest)!);File.WriteAllLines(dest,events);Console.WriteLine($"{n} S01 production-adapter checks passed; {events.Count} events exported");
