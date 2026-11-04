using Comet.Client;
using Comet.Core;
using Comet.Api;
using Comfort.Common;
using EFT;
using EFT.Interactive;
using EFT.Ballistics;
using SAIN.Components;
using SAIN.SAINComponent.Classes.Mover;
using UnityEngine;
using UnityEngine.AI;

int checks=0;void Check(bool v,string why){checks++;if(!v)throw new Exception("C06 adapter: "+why);}
(Plugin p,BotComponent b,BotState s) Setup(bool sain=true)
{
 var p=new Plugin();var b=new BotComponent{SAINLayersActive=sain};var s=new BotState(b,p.config!,"test");p.states[b]=s;
 Singleton<GameWorld>.Instance=new();Singleton<GameWorld>.Instance.AllAlivePlayersList.Add(b.Player);
 Physics.Low=true;Physics.High=Physics.Volume=Physics.Sweep=false;Physics.VolumeAt=null;Physics.LowCollider=new();Physics.Hidden=(_,_)=>true;Physics.SightCollider=new(){Parent=new BallisticCollider{TypeOfMaterial=MaterialType.Concrete}};
 NavMesh.Available=true;NavMesh.Blocked=false;MovementRecoveryNetwork.Teleport=null;return(p,b,s);
}
void Run(Plugin p,BotComponent b,BotState s,double until){for(double t=0;t<=until;t+=.5)p.Tick(b,s,t);}
foreach(bool sain in new[]{true,false})
{
 var(p,b,s)=Setup(sain);Run(p,b,s,15);Check(b.Mover.Repaths==1&&b.BotOwner.Mover.Repaths==1,"both owners repath once");Check(b.Mover.Vaults==2&&b.Mover.Jumps==2,"early and late low-obstacle attempts");Check(b.Player.Teleports==0,"OFF by default");Check(b.Mover.Stops==1&&b.BotOwner.Mover.Stops==1,"bounded stop");Check(!p.Owns(s),"A12 ownership released at exhaustion");
 for(double t=15.5;t<30;t+=.5){b.BotOwner.Mover.IsMoving=true;((BotPathDataManual)b.Mover.ActivePath).Moving=true;p.Tick(b,s,t);}Check(b.Mover.Stops==1,"new path intent does not repeat stages");
}
foreach(var change in new Action<Plugin,BotComponent>[] {(p,b)=>b.Pmc=false,(p,b)=>b.Player.IsAI=false,(p,b)=>b.BotActive=false,(p,b)=>b.IsDead=true,(p,b)=>b.GameEnding=true,(p,b)=>p.Authority=false,(p,b)=>p.Ghosted=true,(p,b)=>p.config!.MovementRecovery.Enabled=false})
{var(p,b,s)=Setup();change(p,b);p.Enroll(new(){Pmc=false});Run(p,b,s,15);Check(b.Mover.Repaths+b.Mover.Jumps+b.Mover.Stops==0,"scope excluded");}
foreach(var pause in new Action<Plugin,BotComponent,BotState>[] {(p,b,s)=>b.BotOwner.Medecine.Using=true,(p,b,s)=>b.BotOwner.WeaponManager.Reload.Reloading=true,(p,b,s)=>b.BotOwner.DoorOpener.Interacting=true,(p,b,s)=>b.Player.HandsController.Busy=true,(p,b,s)=>b.BotOwner.ShootData.Shooting=true,(p,b,s)=>b.Search.CurrentState="Wait",(p,b,s)=>{s.ActiveLabel="COMET";s.Decision="Hold";},(p,b,s)=>p.doorSwings.Add(new(){Bot=b}),(p,b,s)=>((BotPathDataManual)b.Mover.ActivePath).Status=EBotMoveStatus.Complete})
{var(p,b,s)=Setup();pause(p,b,s);Run(p,b,s,20);Check(b.Mover.Repaths+b.Mover.Jumps+b.Mover.Stops==0,"intentional pause or arrival protected");}
{var(p,b,s)=Setup();p.config!.HandsRecovery.Enabled=false;p.states.Clear();p.Enroll(b);Check(p.states.ContainsKey(b),"C06 enrolls independently of W10 or combat decisions");}
{var(p,b,s)=Setup();Physics.LowCollider=new(){Parent=new Door()};Run(p,b,s,6);Check(b.Mover.Jumps==0&&b.Mover.Vaults==0&&b.Mover.Repaths==1,"door leaf belongs to C04");}
{var(p,b,s)=Setup();Singleton<GameWorld>.Instance.AllAlivePlayersList.Add(new(){Position=new(.8f,0,0)});Run(p,b,s,6);Check(b.Mover.Jumps+b.Mover.Vaults==0,"no jumping over teammate or another actor");}
{var(p,b,s)=Setup();Singleton<GameWorld>.Instance.AllAlivePlayersList.Add(new(){Position=new(.8f,.9f,0)});Run(p,b,s,6);Check(b.Mover.Jumps+b.Mover.Vaults==0,"actor on a nearby step still occupies the swept body space");}
{var(p,b,s)=Setup();((BotPathDataManual)b.Mover.ActivePath).PausedRequested=true;Run(p,b,s,20);Check(b.Mover.Repaths+b.Mover.Jumps+b.Mover.Stops==0,"pending native pause protected before status update");}
{var(p,b,s)=Setup();Physics.High=true;Run(p,b,s,6);Check(b.Mover.Jumps+b.Mover.Vaults==0,"no jumping through tall blocker");}
{var(p,b,s)=Setup();Physics.Volume=true;Run(p,b,s,6);Check(b.Mover.Jumps==0,"unsafe landing denied");}
{var(p,b,s)=Setup();b.Mover.Vault=true;Run(p,b,s,6);Check(b.Mover.Vaults==2&&b.Mover.Jumps==0,"vault first without redundant jump");}
foreach(var unsafeCase in new Action<Plugin,BotComponent>[] {(p,b)=>Physics.Sweep=true,(p,b)=>Physics.Volume=true,(p,b)=>NavMesh.Available=false,(p,b)=>NavMesh.Blocked=true,(p,b)=>Singleton<GameWorld>.Instance.AllAlivePlayersList.Add(new(){IsAI=false,Position=new(49,0,0)}),(p,b)=>{Singleton<GameWorld>.Instance.AllAlivePlayersList.Add(new(){IsAI=false,Position=new(100,0,0)});Physics.Hidden=(_,_)=>false;},(p,b)=>{Singleton<GameWorld>.Instance.AllAlivePlayersList.Add(new(){IsAI=false,Position=new(100,0,0)});Physics.SightCollider.Parent=new BallisticCollider{TypeOfMaterial=MaterialType.Glass};},(p,b)=>p.fikaPresent=true})
{var(p,b,s)=Setup();p.config!.MovementRecovery.Teleport=true;unsafeCase(p,b);Run(p,b,s,9);Check(b.Player.Teleports==0,"teleport safety refusal");}
{var(p,b,s)=Setup();p.config!.MovementRecovery.Teleport=true;Physics.VolumeAt=v=>v.x<.1f;Run(p,b,s,9);Check(b.Player.Teleports==0,"starting inside a blocker cannot bypass a capsule sweep");}
{var(p,b,s)=Setup();p.config!.MovementRecovery.Teleport=true;Singleton<GameWorld>.Instance.AllAlivePlayersList.Add(new(){IsAI=false,Position=new(100,0,0)});Run(p,b,s,8);Check(b.Player.Teleports==1&&b.Position.x<=2,"optional hidden relocation within cap");p.Tick(b,s,8.5);Check(s.MovementRecovery.Policy.Trace.State!="recovered","relocation not success");b.Player.Position+=new Vector3(.7f,0,0);p.Tick(b,s,9);Check(s.MovementRecovery.Policy.Trace.State=="recovered","success only after real movement");}
{var(p,b,s)=Setup();p.config!.MovementRecovery.Teleport=true;p.fikaPresent=true;int calls=0;MovementRecoveryNetwork.Teleport=(player,v)=>{calls++;player.Teleport(v);return true;};Run(p,b,s,8);Check(calls==1,"Fika host uses transport exactly once");}
{var(p,b,s)=Setup();s.ActiveLabel="COMET";Run(p,b,s,14);Check(p.Releases==1,"COMET owner yields after exhaustion");}
// Fixture comes from the production adapter, not a handcrafted representation of its payload.
var events=Comet.Api.Decisions.Events.Select(x=>Newtonsoft.Json.Linq.JObject.Parse(x)).ToList();
Check(events.Any(e=>(string?)e["movementRecovery"]?["step"]=="repath"),"structured per-stage logs");
var fixture=events.First(e=>(string?)e["movementRecovery"]?["state"]=="attempted"&&(string?)e["movementRecovery"]?["step"]=="repath");
string root=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../../artifacts/c06"));Directory.CreateDirectory(root);File.WriteAllText(Path.Combine(root,"recovery.json"),fixture.ToString());File.WriteAllLines(Path.Combine(root,"recovery.jsonl"),Comet.Api.Decisions.Events);
Console.WriteLine($"C06 production adapter: {checks} checks passed");
