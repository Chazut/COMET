using Comet.Client;
using Comet.Core;
using SAIN.Components;
using SAIN.SAINComponent.Classes.EnemyClasses;
using UnityEngine;
int checks=0;void Check(bool ok,string reason){checks++;if(!ok)throw new Exception(reason);}
var p=new Plugin();var b=new BotComponent();var end=new Vector3(1,0,0);
// zzap movement probes NavMesh and jump headroom, not a second capsule simulation.
for(int count=0;count<140;count++) {
 Physics.Reset();for(int i=0;i<count;i++)Physics.Overlap.Add(new(){Owner=b.Player});
 Check(p.Check(b,end)=="","no custom capsule overflow rejection");Check(p.Empty(),"no retained collider references");
}
Physics.Reset();Physics.Overlap.Add(new());Check(p.Check(b,end)=="","native movement owns body collision");
Physics.Reset();UnityEngine.AI.NavMesh.Blocked=true;Check(p.Check(b,end).Contains("navmesh-edge"),"source NavMesh edge blocks transit");
Physics.Reset();Physics.Cast.Add(new(){Mask=2});Check(p.Check(b,end,1,true).Contains("headroom"),"source jump headroom blocks overhead obstacle");
Physics.Reset();Physics.Cast.Add(new(){Mask=16});Check(p.Check(b,end,1,true)=="","headroom respects collision mask");
Check(Plugin.Contact(new Enemy{EnemyProfileId="a"},new Enemy{EnemyProfileId="a"}),"replacement wrapper preserves contact");
var ownDoor=new EFT.Interactive.Door();
foreach(bool sweep in new[]{false,true}){
 Physics.Reset();var buffer=sweep?Physics.Cast:Physics.Overlap;
 for(int i=0;i<24;i++)buffer.Add(new(){Owner=b.Player});buffer.Add(new(){Door=ownDoor});
 Check(p.Entry(b,ownDoor,sweep)==""&&Physics.Queries==2&&p.Empty(),"entry filters self and selected door after retry");
 buffer.Add(new(){Door=new()});Check(p.Entry(b,ownDoor,sweep)=="blocked"&&p.Empty(),"entry never ignores another door");
 buffer.RemoveAt(buffer.Count-1);for(int i=buffer.Count;i<128;i++)buffer.Add(new(){Door=ownDoor});
 Check(p.Entry(b,ownDoor,sweep)=="incomplete-after-retry"&&p.Empty(),"entry refuses unknown overflow even when known hits are ignored");
}
Physics.Reset();Check(p.Sight(b,end),"clear source sightline");
Physics.Cast.Add(new(){Mask=2});Check(!p.Sight(b,end),"source linecast detects wall");
Physics.Reset();Physics.Cast.Add(new(){Mask=16});Check(p.Sight(b,end),"sightline excludes non collision layer");
Check(!Plugin.Contact(new Enemy{EnemyProfileId="a"},new Enemy{EnemyProfileId="b"})&&!Plugin.Contact(new Enemy(),new Enemy()),"different/unknown contact cannot inherit plan");
var budget=new DoorWorkBudget();var a=new object();var c=new object();
Check(budget.TryBegin(1,a,"a",1)&&budget.TryBegin(1,a,"a",1),"initial allowance");
Check(!budget.TryBegin(1,c,"c",1)&&!budget.TryBegin(2,a,"a",1),"old waiter precedes early callback");
Check(budget.TryBegin(2,c,"c",1)&&budget.TryBegin(2,a,"a",1),"fair waiter and next actor progress");
budget.Charge(.5);Check(!budget.TryBegin(2,c,"c",1),"CPU budget unchanged");
Check(budget.TryBegin(6,a,"a",1),"abandoned waiter expires");budget.Clear();Check(budget.PendingCount==0&&budget.Deferred==0,"raid clears actor references and counters");
var wait=new PreparationWait();for(int i=0;i<1000;i++){wait.Defer(i*.05,.05);wait.Defer(i*.05,.05);}Check(wait.Seconds==2,"wait allowance capped including duplicate callbacks");
wait.Clear();wait.Defer(1,double.NaN);wait.Defer(1,-1);Check(wait.Seconds==0,"invalid wait cannot extend a plan");
var doors=new EFT.BotDoorsController();var near=new EFT.NavMeshDoorLink{Door=new(){Center=new(2,0,0)}};
doors._navMeshDoorLinks.Add(near);for(int i=0;i<79;i++)doors._navMeshDoorLinks.Add(new(){Door=new(){Center=new(300+i*20,0,0)}});
UnityEngine.Object.Found=doors;UnityEngine.Object.Finds=0;Time.time=0;Time.frameCount=0;
p=new();p.Index(false);Check(p.Indexed==0&&UnityEngine.Object.Finds==0,"remote client never builds index");
p.Index();Check(p.Indexed>0&&p.Indexed<=24,"scene indexing bounded to at most 24 links per slice");
for(int i=0;i<100&&p.Indexed<80;i++){Time.frameCount++;Time.time+=.1f;p.Index();}
Check(p.Indexed==80&&UnityEngine.Object.Finds==1,"one discovery, incremental completion");
Check(p.Doors(b).SequenceEqual(new[]{near}),"door found when voxel data absent; distant map doors excluded");
b.BotOwner.VoxelesPersonalData.CurVoxel=new();b.BotOwner.VoxelesPersonalData.CurVoxel.DoorLinks.Add(near);
Check(p.Doors(b).Count(x=>ReferenceEquals(x,near))==1,"voxel and spatial sources deduplicated");
for(int i=0;i<100;i++)b.BotOwner.VoxelesPersonalData.CurVoxel.DoorLinks.Add(new());
Check(p.Doors(b).Length==48,"candidate enumeration bounded");
var remembered=new Vector3(10,0,0);b.GoalEnemy!.Path.PathToEnemy=new(){status=UnityEngine.AI.NavMeshPathStatus.PathPartial,corners=new[]{default(Vector3),new Vector3(0,0,5)}};
Check(Plugin.Bearing(b,remembered).Last().X==10,"partial enemy route replaced only by bearing to remembered contact");
b.GoalEnemy.Path.PathToEnemy.status=UnityEngine.AI.NavMeshPathStatus.PathComplete;
b.GoalEnemy.Path.PathToEnemy.corners=new[]{default(Vector3),new Vector3(5,0,2),remembered};
Check(Plugin.Bearing(b,remembered).Length==3,"complete local remembered route preserves its bends");
b.GoalEnemy.Path.PathToEnemy.corners[2]=new(40,0,0);Check(Plugin.Bearing(b,remembered).Length==2,"route to different contact discarded");
Check(p.Summary().Contains("doorIndexComplete\":true"),"index and query counters exported");
p.ResetIndex();Check(p.NoDoorReferences&&p.Indexed==0,"raid cleanup releases all scene references");
doors=new();doors._navMeshDoorLinks.Add(new(){Door=new(){Throws=true}});doors._navMeshDoorLinks.Add(near);UnityEngine.Object.Found=doors;
p=new();Time.time=10;Time.frameCount++;p.Index();Check(p.Logger.Warnings==1&&p.Indexed==1,"bad geometry isolated and logged once");
Time.time=11;Time.frameCount++;p.Index();Check(p.Indexed==1,"failure retry is throttled");Time.time=16;Time.frameCount++;p.Index();
Check(p.Indexed==2&&p.Doors(new BotComponent()).Contains(near),"later good doors remain discoverable after a failed link");
p.ResetIndex();UnityEngine.Object.Found=null;Time.time=20;Time.frameCount++;int finds=UnityEngine.Object.Finds;p.Index();p.Index();
Check(UnityEngine.Object.Finds==finds+1,"missing scene controller does not cause per-frame searches");
Console.WriteLine($"PASS {checks} production clearance, local-door discovery, identity, fair-budget and wait-credit assertions (mocked engine, no Unity raid).");
