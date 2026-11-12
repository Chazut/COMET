using Comet.Client;
using Comet.Core;
using SAIN.Components;
using SAIN.SAINComponent.SubComponents.CoverFinder;
using UnityEngine;
using UnityEngine.AI;
int checks=0;void C(bool ok,string why){checks++;if(!ok)throw new Exception(why);}
void Step(){Time.time+=.02f;Time.realtimeSinceStartup=Time.time;Time.frameCount++;}
(Plugin p,BotComponent b) Setup(){Time.time=Time.realtimeSinceStartup=10;Time.frameCount=0;Physics.Rays=Physics.Overlaps=NavMesh.Queries=PathData.Queries=0;Physics.Obstacles=[new()];Physics.Cast=(a,b)=>true;NavMesh.Valid=true;NavMesh.Map=p=>p;PathData.Status=NavMeshPathStatus.PathComplete;PathData.Route=(a,b)=>[a,b];ContactSafety.Allowed=true;return(new(),new());}
CoverPoint? Pump(Plugin p,BotComponent b){Plugin.RequestCoverFallback(b,null);for(int i=0;i<200&&p.R(b).Requested;i++){Step();p.Tick();}return p.R(b).Point;}
foreach(bool partial in new[]{false,true}){
 var(p,b)=Setup();if(partial)Physics.Cast=(a,z)=>a.y<1.1;
 var cover=Pump(p,b);C(cover!=null,"local obstacle produces real cover "+partial);C(p.R(b).Tier==(partial?"partial":"full"),"protection tier recorded accurately");
 C(Plugin.TryFallbackCover(b,cover!,out bool usable)&&usable,"selected fallback revalidates");
 C(ReferenceEquals(Plugin.RequestCoverFallback(b,null),cover),"selection reuses checked candidate");
 b.Cover.CoverPoint_MovingTo=cover;b.Position=cover!.Position;Step();p.Tick();
 C(Plugin.TryFallbackCover(b,cover,out usable)&&usable,"owned cover remains valid after movement");
 Physics.Cast=(a,z)=>false;Time.time+=.3f;Step();
 C(Plugin.TryFallbackCover(b,cover,out usable)&&!usable,"lost protection cannot fall through to native acceptance");
 p.Clear();C(p.Count==0,"raid cleanup releases bot and collider references");C(cover.CoverData.IsBad,"retired fallback cannot be resurrected through native acceptance");
}
foreach(string mode in new[]{"wall","partial-path","wrong-end","long-route","floor","nav","danger-route","teammate","firelane","moving-obstacle"}){
 var(p,b)=Setup();
 switch(mode){
 case "wall":Physics.Cast=(a,z)=>false;break;
 case "partial-path":PathData.Status=NavMeshPathStatus.PathPartial;break;
 case "wrong-end":PathData.Route=(a,z)=>[a,new(100,0,100)];break;
 case "long-route":PathData.Route=(a,z)=>[a,new(-30,0,0),z];break;
 case "floor":NavMesh.Map=v=>new(v.x,3,v.z);break;
 case "nav":NavMesh.Valid=false;break;
 case "danger-route":PathData.Route=(a,z)=>[a,new(0,0,11),z];break;
 case "teammate":NavMesh.Map=v=>new(2,0,2);var mate=new BotComponent{Position=new(2,0,2)};b.Squad.Members.Add(mate.ProfileId,mate);break;
 case "firelane":NavMesh.Map=v=>new(0,0,2);var shooter=new BotComponent{Position=new(0,0,-3)};shooter.GoalEnemy!.IsVisible=true;b.Squad.Members.Add(shooter.ProfileId,shooter);break;
 case "moving-obstacle":Physics.Obstacles[0].attachedRigidbody=new();break;
 }
 C(Pump(p,b)==null,"unsafe fallback rejected "+mode);C(p.R(b).Rejected.Count>0,"rejection evidence retained "+mode);
 C(p.R(b).Cursor<=48,"bounded search "+mode);int scans=Physics.Overlaps;
 Plugin.RequestCoverFallback(b,null);for(int i=0;i<20;i++){Step();p.Tick();}C(Physics.Overlaps==scans,"exhausted search cooldown "+mode);
}
foreach(string scope in new[]{"human","scav","dead","inactive","ghost","remote","disabled","feature","contact","stale"}){
 var(p,b)=Setup();var point=Pump(p,b)!;
 switch(scope){case "human":b.Player.IsAI=false;break;case "scav":b.Pmc=false;break;case "dead":b.IsDead=true;break;case "inactive":b.BotActive=false;break;case "ghost":p.IsGhost=true;break;case "remote":p.Authority=false;break;case "disabled":p.config.Enabled=false;break;case "feature":p.config.CoverScan.Enabled=false;break;case "contact":b.GoalEnemy!.EnemyProfileId="new";break;case "stale":b.GoalEnemy!.TimeSinceLastKnownUpdated=1000;break;}
 C(Plugin.TryFallbackCover(b,point,out bool usable)&&!usable,"owned fallback scope invalidates immediately "+scope);
}
{
 var(p,b)=Setup();p.doorBudget.Available=false;Plugin.RequestCoverFallback(b,null);for(int i=0;i<10;i++){Step();p.Tick();}
 C(Physics.Overlaps==0&&PathData.Queries==0,"shared budget prevents all search work");p.doorBudget.Available=true;Step();p.Tick();int rays=Physics.Rays,paths=PathData.Queries;
 for(int i=0;i<100;i++)p.Tick();C(rays==Physics.Rays&&paths==PathData.Queries,"one probe per bot frame");
}
{
 var(p,b)=Setup();var point=Pump(p,b)!;b.GoalEnemy!.KnownPlaces.LastKnownPosition=new(10,0,12);
 C(Plugin.TryFallbackCover(b,point,out bool usable)&&!usable,"moved enemy requires a new protected point");
}
{
 var(p,b)=Setup();var point=Pump(p,b)!;var progress=new PlanProgress(new());progress.Fail(ProgressAdapter.CoverKey(point),Time.time,"failed");
 C(Plugin.RequestCoverFallback(b,progress)==null&&p.R(b).Point==null,"failed destination is not resurrected by fallback cache");
 C(point.CoverData.IsBad,"discarded candidate remains invalid after losing fallback identity");
}
{
 var(p,b)=Setup();b.GoalEnemy!.KnownPlaces.LastKnownPosition=new(float.NaN,0,12);Pump(p,b);
 C(Physics.Rays==0&&PathData.Queries==0,"invalid coordinates never reach engine queries");
}
Directory.CreateDirectory("artifacts");File.WriteAllLines("artifacts/cover-fallback-events.jsonl",Comet.Api.Decisions.Events);
Console.WriteLine($"{checks} cover fallback production-adapter checks passed");
