using Comet.Client;
using SAIN.Components;
using SAIN.SAINComponent.Classes.Mover;
using UnityEngine;
using UnityEngine.AI;

int checks=0;void Check(bool ok,string why){checks++;if(!ok)throw new Exception("G02 adapter: "+why);}
(Plugin,BotComponent) Setup(bool judgment=true)
{var p=new Plugin();p.Settings.GrenadeJudgment.Enabled=judgment;var b=new BotComponent();p.Tick(b,10);return(p,b);}
bool Ready(Plugin p,BotComponent b)
{
 for(int i=0;i<180;i++)
 {
  if(p.Find(b))return true;
  if(p.State(b).GrenadeTrajectory.Trace?.State=="blocked")return false;
  p.Tick(b,Time.timeAsDouble+.01);
 }
 return false;
}
foreach(var exclude in new Action<Plugin,BotComponent>[] {(p,b)=>p.Settings.Enabled=false,(p,b)=>p.Authority=false,(p,b)=>p.IsGhost=true,(p,b)=>b.Pmc=false,(p,b)=>b.IsDead=true,(p,b)=>b.GameEnding=true,(p,b)=>b.BotActive=false})
{var p=new Plugin();var b=new BotComponent();exclude(p,b);p.Tick(b,10);Check(p.Count==0&&p.Find(b)&&p.Throw(b)&&AIGrenadeHelper.Calls==0,"excluded bots keep native pipeline");}
{
 var(p,b)=Setup();Check(Ready(p,b),"native arc search completes across frames");
 var r=p.State(b).GrenadeTrajectory;Check(r.Trace!.Kind=="last-known"&&r.Trace.Attempts==1,$"source stops at first valid angle: kind={r.Trace.Kind}, attempts={r.Trace.Attempts}");
 Check(r.Trace.Alternatives.Any(a=>a.Accepted)&&r.Selected!=null,"native valid arc accepted without added clearance probes");
 int calls=AIGrenadeHelper.Calls,draws=p.Draws;
 Check(p.Find(b)&&AIGrenadeHelper.Calls==calls&&p.Draws==draws,"prepared arc reuse consumes no calls or rolls");
 p.Tick(b,Time.timeAsDouble+.01);b.Transform.WeaponData.WeaponRoot=new(.1f,0,0);
 Check(p.Throw(b)&&p.Requests==1,"final validation passes native request");
 Check(Vector3.Distance(r.Selected!.Data.Position,b.Transform.WeaponData.WeaponRoot)<.001f,"small origin change recomputes the arc from the current hand position");
 Check(r.Trace.State=="validated"&&!p.State(b).GrenadeJudgment.Policy.Trace!.Released,"validation is not projectile release");
 p.Projectile(b);Check(p.State(b).GrenadeJudgment.Policy.Trace!.Released,"G01 still observes actual projectile");
 p.End(b);Check(!r.Pending&&r.Selected==null&&r.Targets.Count==0&&r.Arcs.Count==0&&r.Trace.State=="ended","raid clear drops door and native references");
 File.WriteAllLines(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../../artifacts/g02-adapter-events.jsonl")),p.Events);
}
foreach(bool judgment in new[]{true,false})
foreach(var change in new Action<Plugin,BotComponent>[] {
 (p,b)=>NativeAdapter.FriendsClear=false,
 (p,b)=>b.BotOwner.WeaponManager.Grenades.grenade!.Id="changed",
 (p,b)=>b.GoalEnemy=new(),(p,b)=>p.Settings.Revision++,
 (p,b)=>b.BotOwner.WeaponManager.Grenades.AIGreanageThrowData=new()})
{
 var(p,b)=Setup(judgment);Check(Ready(p,b),"ready before binding change");p.Tick(b,Time.timeAsDouble+.01);change(p,b);
 int draws=p.Draws;Check(!p.Throw(b)&&p.Requests==0&&p.Draws==draws,"native friend check and selected grenade binding apply, also with G01 off");
}

{
 var(p,b)=Setup();AIGrenadeHelper.Allowed=(v,a)=>false;
 Check(!Ready(p,b)&&p.State(b).GrenadeTrajectory.Trace!.Reason=="trajectory-no-safe-arc","actual arc exhaustion fails");
 Check(p.State(b).GrenadeJudgment.Policy.Trace!.Roll==null,"no G01 probability roll on failed search");
 int calls=AIGrenadeHelper.Calls,draws=p.Draws;
 for(int i=0;i<50;i++){p.Tick(b,Time.timeAsDouble+.01);p.Find(b);}
 Check(AIGrenadeHelper.Calls==calls&&p.Draws==draws,"shared no-arc retry prevents scanning/random fishing");
 p.Tick(b,Time.timeAsDouble+1.5);AIGrenadeHelper.Allowed=(v,a)=>true;Check(Ready(p,b),"retry can recover");
}
{
 var(p,b)=Setup();b.GoalEnemy!.VisiblePathPoint=new(17,0,0);
 AIGrenadeHelper.Allowed=(v,a)=>p.State(b).GrenadeTrajectory.Targets[p.State(b).GrenadeTrajectory.Index].Kind=="blind-corner";
 Check(Ready(p,b)&&p.State(b).GrenadeTrajectory.Trace!.Kind=="blind-corner","nearby reachable blind corner after direct arcs fail");
 Check(p.State(b).GrenadeTrajectory.Trace!.Alternatives.Any(a=>!a.Accepted),"rejected direct alternatives retained");
}
{
 var(p,b)=Setup(false);var link=new DoorLink();b.BotOwner.VoxelesPersonalData.CurVoxel!.DoorLinks.Add(link);
 AIGrenadeHelper.Allowed=(v,a)=>p.State(b).GrenadeTrajectory.Targets[p.State(b).GrenadeTrajectory.Index].Kind=="open-door";
 Check(Ready(p,b)&&p.State(b).GrenadeTrajectory.Trace!.Kind=="open-door","open door alternative uses native arc");
 p.Tick(b,Time.timeAsDouble+.01);link.Geometry.State="Shut";
 Check(!p.Throw(b)&&p.State(b).GrenadeTrajectory.Trace!.Reason=="trajectory-door-changed","door closed before execution cancels, G01 off");
}
foreach(var obstruction in new Action[]{()=>NavMesh.NoLanding=true,()=>NavMesh.NoRoute=true})
{
 var(p,b)=Setup();obstruction();Check(Ready(p,b)&&AIGrenadeHelper.Calls>0,"source arc does not require a walking route or NavMesh landing");
}
{
 var(p,b)=Setup();b.GoalEnemy!.VisiblePathPoint=new(17,0,0);b.BotOwner.VoxelesPersonalData.CurVoxel!.DoorLinks.Add(new());AIGrenadeHelper.Allowed=(v,a)=>false;
 Check(!Ready(p,b)&&AIGrenadeHelper.Calls<=12,"all candidates obey total arc cap");
}
{
 var(p,b)=Setup(false);Check(Ready(p,b),"G01 off still plans");var point=p.State(b).GrenadeTrajectory.Selected!.Data.Target;int draws=p.Draws;
 p.Tick(b,Time.timeAsDouble+.26);Check(Ready(p,b),"expired data refreshed");
 Check(Vector3.Distance(point,p.State(b).GrenadeTrajectory.Selected!.Data.Target)<.001f,"refresh retains dispersed aim");
 Check(p.Draws==draws,"source cache reuses solution for 0.5 seconds");
}
{
 var(p,b)=Setup();Check(p.Find(b),"source native arc may complete on its first candidate");
 Check(p.State(b).GrenadeJudgment.Policy.Trace!=null,"completed native arc is assessed by G01");
 int calls=AIGrenadeHelper.Calls;
 for(int i=0;i<20;i++)p.Find(b);
 Check(AIGrenadeHelper.Calls-calls<=2,"same-frame repeated consumers obey shared budget");
 p.IsGhost=true;p.Tick(b,Time.timeAsDouble+.01);Check(!p.State(b).GrenadeTrajectory.Pending&&p.State(b).GrenadeTrajectory.Targets.Count==0,"ghost transition drops pending work");
}
Console.WriteLine($"G02 production adapters: {checks} checks passed (substituted native contracts; no Unity raid)");
