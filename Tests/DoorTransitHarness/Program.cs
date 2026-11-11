using Comet.Client;
using Comet.Core;
using EFT;
using EFT.Interactive;
using SAIN.Components;
using SAIN.Models.Enums;
using SAIN.Preset.Shared.Enums;
using SAIN.SAINComponent.Classes.Mover;
using UnityEngine;
int checks=0;void Check(bool value,string reason){checks++;if(!value)throw new Exception("C01 adapter: "+reason);}
var hooks=Plugin.Hooks();Check(hooks.Patches.Count==5,"native hooks resolved");
Check(hooks.Patches.Single(x=>x.Item1.Name=="SelectDoor").Item3!.priority==800,"door selection before D15");
Check(hooks.Patches.Single(x=>x.Item1.Name=="SetTargetMoveSpeed").Item1.DeclaringType==typeof(SAIN.Classes.PlayerMovementController),"direct controller calls also capped");
(Plugin p,BotComponent b,BotState s,BotPathDataManual path,Link door) Setup()
{
    var p=new Plugin();Plugin.Instance=p;var b=new BotComponent();var s=new BotState();p.states.Add(b,s);
    var path=new BotPathDataManual(b);b.Mover.ActivePath=path;var link=new Link();b.BotOwner.VoxelesPersonalData.CurVoxel!.DoorLinks.Add(link);
    Physics.Blocker=link.Door.Collider;Time.time=0;Time.frameCount=0;
    return(p,b,s,path,link);
}
var a=Setup();a.p.Tick(a.b,0);var controller=a.b.PlayerComponent.CharacterController;
Check(controller.Speed==.25f&&!a.path.WantToSprint,"closed route slows and clears sprint intent");
controller.SetTargetMoveSpeed(.6f);Check(controller.Speed==.25f,"native door direct speed cannot override cap");
controller.SetTargetMoveSpeed(.12f);Check(controller.Speed==.12f,"slower command preserved");
controller.SetTargetMoveSpeed(.7f);
for(int i=1;i<=8;i++)a.p.Tick(a.b,i*.125f);
Check(a.s.Transit.Policy.Trace.Recovery,"jam confirmed");
var side=new Link();side.Door.Id="side";a.b.DoorOpener.TestDoors.Add(new(side));a.b.DoorOpener.TestDoors.Add(new(a.door));
DoorDataStruct data=default;var type=EInteractionType.Open;
Check(a.p.Select(a.b,false,ref data,ref type)&&data.Door==a.door.Door&&a.b.DoorOpener.Index==1,"fallback uses correct native list index");
Check(a.s.Transit.Policy.Trace.Requested&&!a.s.Transit.Policy.Trace.Passed,"request does not fabricate success");
var noRange=new DoorDataStruct(a.door){Range=false};a.b.DoorOpener.TestDoors[1]=noRange;data=default;
Check(!a.p.Select(a.b,false,ref data,ref type),"native interaction range respected");
a.b.DoorOpener.TestDoors[1]=new(a.door){Cooldown=false};data=default;
Check(!a.p.Select(a.b,false,ref data,ref type),"native cooldown respected");
a.b.DoorOpener.TestDoors[1]=new(a.door);data=new(side);Physics.Blocker=null;
Check(a.p.Select(a.b,true,ref data,ref type)&&data.Door==a.door.Door,"jammed proximity fallback selects route door even when native ray misses");
a.door.Door.DoorState=EDoorState.Open;a.p.Tick(a.b,1.25f);
Check(Math.Abs(controller.Speed-.7f)<.001f&&a.path.WantToSprint,"clear passage restores latest speed and sprint intent");
type=EInteractionType.Close;data=new(a.door);Check(!a.p.Select(a.b,true,ref data,ref type),"clear open door never closed unnecessarily");
a.b.Position=new(0,0,.5f);a.p.Tick(a.b,1.5f);
Check(a.p.diagnostics!.Lines.Any(x=>x.Contains("\"passed\":true")),"crossing requires actual position change");
var full=a.p.diagnostics.Lines.ToList();
foreach(var edit in new Action<Plugin,BotComponent,BotState,BotPathDataManual>[] {
 (p,b,s,path)=>p.Authority=false,(p,b,s,path)=>p.IsGhost=true,(p,b,s,path)=>p.config!.DoorTransit.Enabled=false,
 (p,b,s,path)=>b.Pmc=false,(p,b,s,path)=>b.IsAI=false,(p,b,s,path)=>b.BotActive=false,(p,b,s,path)=>b.IsDead=true,
 (p,b,s,path)=>b.GameEnding=true,(p,b,s,path)=>b.SAINLayersActive=false,
 (p,b,s,path)=>s.TacticalDoor=true,(p,b,s,path)=>s.Door.Policy.Planning=true,(p,b,s,path)=>s.Support=new(),
 (p,b,s,path)=>s.Interruption.Policy.Pending=true,
 (p,b,s,path)=>b.SAINLayersActive=false,
 (p,b,s,path)=>path.Status=EBotMoveStatus.Paused,(p,b,s,path)=>path.CancelRequested=true,
 (p,b,s,path)=>path.PausedRequested=true,(p,b,s,path)=>b.Player.MovementContext.IsGrounded=false})
{
 var x=Setup();x.p.Tick(x.b,0);edit(x.p,x.b,x.s,x.path);x.p.Tick(x.b,.125f);
 Check(x.s.Transit.Path==null&&!x.s.Transit.Speed.Owned,"scope/priority releases state");
 if(!x.b.IsDead)Check(x.b.PlayerComponent.CharacterController.Speed==.8f,"scope/priority restores owned cap");
 x.b.PlayerComponent.CharacterController.SetTargetMoveSpeed(.9f);Check(x.b.PlayerComponent.CharacterController.Speed==.9f,"no cap after handoff");
}
var newer=Setup();newer.p.Tick(newer.b,0);newer.b.CurrentAction=new();newer.b.Mover.SetTargetMoveSpeed(.42f);newer.p.Watch(newer.b);
Check(newer.b.PlayerComponent.CharacterController.Speed==.42f,"next action speed untouched even before watchdog");
var disposed=Setup();disposed.p.Tick(disposed.b,0);disposed.b.PlayerComponent.CharacterController.ThrowOnSpeed=true;disposed.p.End(disposed.s);
Check(disposed.s.Transit.Path==null&&disposed.s.Transit.Owner==null&&disposed.s.Transit.Door==null&&!disposed.s.Transit.Speed.Owned,"disposed speed controller cannot prevent reference cleanup");
Check(disposed.path.WantToSprint&&disposed.s.Transit.Policy.Trace.State=="ended"&&disposed.s.Transit.Fault,"failed speed restore still ends diagnostics and releases sprint");
disposed.b.PlayerComponent.CharacterController.ThrowOnSpeed=false;disposed.b.Mover.SetTargetMoveSpeed(.9f);
Check(disposed.b.PlayerComponent.CharacterController.Speed==.9f,"controller hook detached after faulty cleanup");
var foreign=Setup();foreign.p.Tick(foreign.b,0);foreign.b.PlayerComponent.CharacterController.ExternalSpeed(.85f);foreign.p.End(foreign.s);
Check(foreign.b.PlayerComponent.CharacterController.Speed==.85f,"external speed preserved");
var stopped=Setup();stopped.p.Tick(stopped.b,0);stopped.path.RequestEndSprint(ESprintUrgency.Normal,"native decision");stopped.p.End(stopped.s);
Check(!stopped.path.WantToSprint,"native sprint cancellation preserved");
var delayed=Setup();delayed.p.Tick(delayed.b,0);Time.time=.5f;delayed.p.Watch(delayed.b);
Check(delayed.s.Transit.Path==null&&delayed.b.PlayerComponent.CharacterController.Speed==.8f,"expired observations release cap");
var reuse=Setup();reuse.p.Tick(reuse.b,0);reuse.path.TimeStarted=2;reuse.b.CurrentAction=new();reuse.b.Mover.SetTargetMoveSpeed(.5f);reuse.p.Watch(reuse.b);
Check(reuse.b.PlayerComponent.CharacterController.Speed==.5f&&!reuse.s.Transit.Speed.Owned,"reused native path generation fenced");
foreach(bool reservation in new[]{false,true})
{var x=Setup();if(reservation)x.p.Busy=true;else x.door.Door.DoorState=EDoorState.Interacting;
 for(int i=0;i<=40;i++)x.p.Tick(x.b,i*.125f);
 data=new(x.door);type=EInteractionType.Open;Check(!x.path.CancelRequested&&x.b.PlayerComponent.CharacterController.Speed==.8f&&!x.p.Select(x.b,true,ref data,ref type),"occupied door no steal or timeout");}
var failed=Setup();for(int i=0;i<=32;i++)failed.p.Tick(failed.b,i*.125f);
Check(failed.path.CancelRequested&&failed.s.ActiveLabel==""&&!failed.s.Transit.Speed.Owned,"persistent blockage cancels owned path and releases plan");
Check(failed.s.Progress.Blocked("goal",4),"A12 marks failed destination");
full.AddRange(failed.p.diagnostics!.Lines);
var recovering=Setup();recovering.p.RecoveryHandling=true;
for(int i=0;i<=96;i++)recovering.p.Tick(recovering.b,i*.125f);
Check(recovering.path.CancelRequested,"C01 persistent door stall cannot deadlock with C06 waiting on the door");
recovering.p.RecoveryHandling=false;recovering.p.Tick(recovering.b,12.125f);
Check(recovering.path.CancelRequested,"C01 can reconsider when C06 releases the stall");
var blockedOpen=Setup();blockedOpen.door.Door.DoorState=EDoorState.Open;for(int i=0;i<=8;i++)blockedOpen.p.Tick(blockedOpen.b,i*.125f);
blockedOpen.b.DoorOpener.TestDoors.Add(new(blockedOpen.door));data=default;type=EInteractionType.Open;
Check(blockedOpen.p.Select(blockedOpen.b,false,ref data,ref type)&&type==EInteractionType.Close,"open leaf blocking the owned route can be closed to release passage");
var open=Setup();open.door.Door.DoorState=EDoorState.Open;Physics.Blocker=null;open.p.Tick(open.b,0);
Check(open.b.PlayerComponent.CharacterController.Speed==.8f&&open.path.WantToSprint,"open route no slowdown");
var sideOnly=Setup();sideOnly.door.Geometry.Center=new(2,0,0);sideOnly.p.Tick(sideOnly.b,0);
Check(sideOnly.b.PlayerComponent.CharacterController.Speed==.8f,"nearby off-route door ignored");
var human=Setup();human.s.GrenadeEscape.Handled=true;human.s.GrenadeEscape.Path=human.path;human.s.GrenadeEscape.Mesh=human.path.NavMeshPath;human.s.GrenadeEscape.CommitUntil=5;
human.s.ActiveLabel="";human.b.Decision.CurrentCombatDecision=ECombatDecision.AvoidGrenade;human.p.Tick(human.b,0);
Check(human.b.PlayerComponent.CharacterController.Speed==.25f,"owned grenade escape handles real closed obstruction");
human.door.Door.DoorState=EDoorState.Open;Physics.Blocker=null;human.p.Tick(human.b,.125f);Check(human.path.WantToSprint&&human.b.PlayerComponent.CharacterController.Speed==.8f,"urgent clear passage restores movement");
string root=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../../"));string output=Path.Combine(root,"artifacts/c01");Directory.CreateDirectory(output);
File.WriteAllLines(Path.Combine(output,"transit.jsonl"),full);
File.WriteAllText(Path.Combine(output,"c01-transit.json"),full.First(x=>x.Contains("\"state\":\"slowing\"")&&x.Contains("\"requested\":true")));
var nativePath=Setup();nativePath.s.ActiveLabel="";nativePath.p.ActionMatches=false;nativePath.p.Tick(nativePath.b,0);
Check(nativePath.s.Transit.Path!=null,"ordinary PMC SAIN path receives door traversal assistance");
Console.WriteLine($"C01 production adapter: {checks} checks passed. Boundary doubles, not a Unity/Fika raid. Output: {output}");
