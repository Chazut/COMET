using Comet.Client;
using SAIN.Components;
using SAIN.Preset.Shared.Enums;
using UnityEngine;
using UnityEngine.AI;
using Newtonsoft.Json.Linq;
int checks=0;var events=new List<string>();
void Check(bool b,string message){checks++;if(!b)throw new Exception(message);}
Plugin p=null!;BotComponent bot=null!,mate=null!;BotState state=null!;
void Setup(float x=-1.1f){
 if(p!=null)events.AddRange(p.diagnostics!.Events);
 Time.time=Time.realtimeSinceStartup=10;Time.frameCount++;Physics.WallAt=-1;Physics.Rays=0;NavMesh.Valid=true;NavMesh.Blocked=false;
 p=new();bot=new(){Position=new(x,0,5)};mate=new(){ProfileId="shooter",Position=new(0,0,0)};mate.Player.Profile.Nickname="Ally <shooter>";
 bot.Squad.Members[bot.ProfileId]=bot;bot.Squad.Members[mate.ProfileId]=mate;p.Mates.Add(mate);state=new();p.states[bot]=state;
}
void Advance(float dt=.1f){Time.time+=dt;Time.realtimeSinceStartup=Time.time;Time.frameCount++;}
bool Move(bool original=true){var d=new Vector3(1,0,0);var goal=new Vector3(10,0,5);float stop=1;return p.Move(bot,ref d,ref goal,ref stop,original);}
Setup();Check(Move()&&state.FireLane.Policy.Trace==null,"no real shot means no intervention");mate.GoalEnemy=new(){IsVisible=true};Check(Move(),"seeing enemy does not reserve a lane");
Advance();p.Shot(mate);var route=bot.Mover.ActivePath;Check(!Move(),"intercept predicted crossing");Check(state.FireLane.Policy.Trace is {State:"holding",Applied:true,Confirmed:true},"stationary observed hold");Check(ReferenceEquals(route,bot.Mover.ActivePath)&&route!.Destination.x==10,"native route untouched");Check(bot.Player.ZeroInputs==1,"zero input submitted");
int rays=Physics.Rays;Advance(.01f);Check(!Move()&&Physics.Rays==rays,"bounded geometry refresh");Advance(.3f);mate.ManualShoot.Reason=SAIN.Models.Enums.EShootReason.None;mate.ManualShoot.Shooting=false;Check(Move()&&!state.FireLane.Policy.Active,"expired shot releases immediately");
Setup();NavMesh.Blocked=true;p.Shot(mate);Check(!Move(),"hold while no lateral clearance");for(int i=0;i<9;i++){Advance();p.Shot(mate);Move();}Check(!state.FireLane.Policy.Active&&state.FireLane.Policy.Trace!.Reason=="lane-source-deadlock-release","bounded hold returns native");Check(Move(),"retry period does not keep stopping");Advance(2.1f);p.Shot(mate);Check(!Move(),"recheck after retry");
Setup(-.2f);p.Shot(mate);var direction=new Vector3(1,0,0);var destination=new Vector3(10,0,5);float stopDistance=1;
Check(p.Move(bot,ref direction,ref destination,ref stopDistance)&&direction.x<0&&destination.x==10,"exit same side immediately");Check(stopDistance==1&&state.FireLane.Policy.Trace is {Confirmed:false,ValidFor:0},"short detour cannot sprint and unobserved input has no map lease");
Advance(.11f);bot.Position=new(-.25f,0,5);bot.Player.Velocity=new(-1,0,0);p.Shot(mate);Move();Check(state.FireLane.Policy.Trace is {Confirmed:true,State:"stepping"},$"confirm displacement: at={state.FireLane.StepInputAt} dir={state.FireLane.StepInputDirection.x} from={state.FireLane.StepInputFrom.x} trace={state.FireLane.Policy.Trace!.State} applied={state.FireLane.Policy.Trace.Applied}");
Advance(.11f);bot.Position=new(-1.21f,0,5);p.Shot(mate);Move();Check(state.FireLane.Policy.Trace!.State=="holding","source reevaluates the next metre without an added detour lease");
Advance();NavMesh.Blocked=true;p.Shot(mate);Check(!Move(),"outside lane still waits for shooter when side is blocked");
Setup(-.2f);bot.Player.Velocity=new(-1,0,0);p.Shot(mate);Move();Check(state.FireLane.Policy.Trace!.Confirmed==false,"preexisting velocity is not COMET confirmation");
Setup();Physics.WallAt=2;p.Shot(mate);Check(!Move(),"source manual shot segment has no additional occlusion veto");
Setup();bot.Position=new(-1.1f,3,5);p.Shot(mate);Check(Move(),"other floor excluded");
Setup();bot.Position=new(-1.1f,0,-5);p.Shot(mate);Check(Move(),"behind muzzle excluded");
Setup();p.Shot(mate);mate.Transform.WeaponData.PointDirection=new(1,0,0);Check(!Move(),"source uses current manual shoot target");
Setup();p.Shot(mate);mate.Transform.WeaponData.FirePort=new(0,1,1);Check(!Move(),"current muzzle location participates directly");
Setup();p.Shot(mate);Check(Move(false)&&bot.Player.ZeroInputs==0,"another prefix owner keeps authority");
foreach(var block in new Action[]{()=>p.Authority=false,()=>p.config!.Enabled=false,()=>p.config!.FireLane.Enabled=false,()=>bot.IsSupportedBot=false,()=>bot.IsDead=true,()=>bot.BotActive=false,()=>bot.IsGhost=true,()=>bot.GameEnding=true,()=>bot.GoalEnemy=new(){IsVisible=true},()=>bot.BotOwner.Memory.IsUnderFire=true,()=>bot.Decision.CurrentCombatDecision=ECombatDecision.AvoidGrenade,()=>bot.Decision.CurrentCombatDecision=ECombatDecision.SeekCover,()=>bot.Decision.CurrentCombatDecision=ECombatDecision.Retreat,()=>bot.Decision.CurrentCombatDecision=ECombatDecision.RunAway}){
 Setup();p.Shot(mate);Check(!Move(),"start before source priority/scope");block();Check(Move()&&!state.FireLane.Policy.Active,"source priority / integration scope release");
}
foreach(var stateChange in new Action[]{()=>bot.Suppression.IsSuppressed=true,()=>bot.BotOwner.Medecine.Using=true,()=>bot.BotOwner.WeaponManager.Reload.Reloading=true,()=>bot.BotOwner.DoorOpener.Interacting=true,()=>bot.Player.MovementContext.IsGrounded=false,()=>bot.Player.IsInPronePose=true,()=>bot.Mover.ActivePath=null}){
 Setup();p.Shot(mate);stateChange();Check(!Move(),"removed extra input veto does not block source lane handling");
}
Setup();p.Shot(mate);Move();Advance(.3f);p.Sample(bot);Check(!state.FireLane.Policy.Active,"callback lease expiration is display-only");
Setup();p.Shot(mate);Move();p.Clear();Check(p.Shots==0&&!state.FireLane.Policy.Active,"raid cleanup releases references");
Setup(-.2f);bot.Player.IsSprintEnabled=true;p.Shot(mate);Check(Move()&&state.FireLane.Policy.Active&&bot.Player.IsSprintEnabled,"source sidestep leaves sprint setting untouched");
// A continuous short hold, then observed side-step: real adapter telemetry for RR fixtures.
Setup();bot.BotOwner.Memory.IsUnderFire=true;p.Shot(mate);int published=Api.Decisions.Count;
Check(Move()&&state.FireLane.Policy.Trace==null,"urgent fight does not create a fake lane action");
Check(p.diagnostics!.Events.Count==1&&p.diagnostics.Events[0].Contains("lane-direct-combat-priority"),"recent squad fire refusal recorded");
Check(Api.Decisions.Count==published,"diagnostic gate does not publish an RR intervention");
for(int i=0;i<100;i++)Move();Check(p.diagnostics.Events.Count==1,"diagnostic bounded even with repeated input callbacks");
Advance(5);mate.ManualShoot.Reason=SAIN.Models.Enums.EShootReason.None;Move();Check(p.diagnostics.Events.Count==1,"no current shot does not invent opportunity");
Advance(5);p.Shot(mate);Move();Check(p.diagnostics.Events.Count==2,"new recent shot can renew diagnostic after throttle");
Setup();Time.time=Time.realtimeSinceStartup=22;for(int i=0;i<32;i++){p.Shot(mate);Move();Advance(.11f);if(state.FireLane.Policy.Trace?.State=="stepping"){bot.Player.Velocity=new(-1,0,0);bot.Position+=new Vector3(-.05f,0,0);}}
p.End(bot);events.AddRange(p.diagnostics!.Events);
foreach(var json in events){var row=JObject.Parse(json);Check((string?)row["feature"]=="S07","feature identifier");var t=row["fireLane"]!;Check((double)t["validFor"]!<=.25,"bounded map lease");if((double)t["validFor"]!>0)Check((bool)t["applied"]!&&(bool)t["confirmed"]!,"map only observed effects");}
var output=System.IO.Path.GetFullPath(System.IO.Path.Combine(AppContext.BaseDirectory,"../../../../../artifacts/s07-adapter-events.jsonl"));Directory.CreateDirectory(System.IO.Path.GetDirectoryName(output)!);File.WriteAllLines(output,events);
Console.WriteLine($"S07 native adapter: {checks} checks passed; {events.Count} production events. Engine doubles, no Unity raid simulated. {output}");
