using System;
using System.Collections.Generic;
using System.Linq;
using Comet.Core;
internal static class SquadManeuverTests
{
 public static int Run()
 {
  int checks=0;void C(bool ok,string why){checks++;if(!ok)throw new Exception("S03 policy: "+why);}
  var c=new SquadManeuverConfig();c.Validate();C(c.Chance==60&&c.Duration==25&&c.Retry==25&&c.MinimumAmmo==50&&c.Enabled,"defaults");
  foreach(var prop in typeof(SquadManeuverConfig).GetProperties().Where(x=>x.PropertyType==typeof(double)))foreach(double v in new[]{double.NaN,double.PositiveInfinity,-1d,10000d})
  {var config=new SquadManeuverConfig();prop.SetValue(config,v);bool rejected=false;try{config.Validate();}catch(ArgumentException){rejected=true;}C(rejected,"invalid "+prop.Name);}
  List<ManeuverMember> Team(int count=4)=>Enumerable.Range(0,count).Select(i=>new ManeuverMember{Id="p"+i,Name="PMC"+i,Ammo=100,Score=i==0?-10:2,Roll=0}).ToList();
  {
   var jamTeam=Team();jamTeam[1].MovementBlock="movement-awaiting-real-progress";
   var selected=new SquadManeuverBoard().TryStart("jam","e",10,10,0,jamTeam,c,100)!;
   C(selected.Member("p1")!.Role=="support"&&selected.Member("p1")!.SelectionReason==jamTeam[1].MovementBlock,"confirmed immobile member remains support with explicit reason");
   C(selected.Members.Count(x=>x.Role=="flank")==2,"mobile teammates still flank");
   jamTeam=Team(2);jamTeam[1].MovementBlock="movement-awaiting-real-progress";
   var blocked=new SquadManeuverBoard();C(blocked.TryStart("jam2","e",10,10,0,jamTeam,c,100)==null,"no pretend maneuver when only flanker is immobilized");
  }
  var board=new SquadManeuverBoard();var p=board.TryStart("a","e",10,10,.599,Team(),c,70)!;
  C(p!=null&&p.Members.Count(x=>x.Role=="flank")==3,"4v1 has three flankers");C(p.Support=="p0","prudent support preserves aggressive movers");
  C(!p.SupportReady(10)&&!p.MaySuppress,"assignment is not execution");
  var support=p.Member("p0")!;support.Confirmed=support.Applied=true;support.LastSeen=10;support.Status="holding";
  foreach(var m in p.Members.Skip(1)){m.Status="planning";m.LastSeen=10;}
  C(p.Tick(10.5,c)&&p.SupportReady(10.5)&&!p.MaySuppress,"support confirmed but no observed movement");
  var mover=p.Member("p1")!;mover.Confirmed=mover.Applied=true;mover.Status="moving";mover.LastSeen=10.5;
  C(p.Tick(10.6,c)&&p.MaySuppress,"moving plus confirmed support");mover.Status="holding";C(!p.MaySuppress,"arrival stops support fire");
  p.Drop("p1","arrived");C(p.Active,"other flankers continue");p.Drop("p0","reloading");C(p.Active&&!p.MaySuppress,"support loss leaves remaining flankers active");
  board.Finish(p,11,c,"ended");C(board.TryStart("a","e",35,35,0,Team(),c,70)==null,"retry timer");
  C(board.TryStart("a","e",36,30,0,Team(),c,70)!=null,"same known contact can be retried after cooldown");C(board.TryStart("a","e",36,36,0,Team(),c,70)!=null,"new evidence and cooldown");
  board.Clear();C(board.Count==0,"raid cleanup");C(board.TryStart("a","e",10,10,.6,Team(),c,70)==null,"exact roll boundary");
  C(board.TryStart("a","e",10.1,10.1,0,Team(),c,70)==null,"failed plan roll not retried each frame");
  board.Clear();C(board.TryStart("a","e",10,10,0,Team(),c,0)==null,"all members can keep their plan");
  foreach(var why in new[]{"timeout","clock","disabled","all-flankers"})
  {
   board.Clear();p=board.TryStart("a","e",10,10,0,Team(2),c,100)!;
   switch(why){case "timeout":p.Tick(35,c);break;case "callback":p.Member("p0")!.Confirmed=true;p.Tick(11,c);break;case "confirm":p.Tick(13,c);break;case "clock":p.Tick(9,c);break;case "disabled":var off=new SquadManeuverConfig{Enabled=false};p.Tick(10,off);break;case "all-flankers":p.Drop("p1","route-failed");break;}
   C(!p.Active,"end "+why);
  }
  board.Clear();var team=Team();team[3].Score=-10;team[3].RearContact="rear-known";p=board.TryStart("a","e",10,10,0,team,c,100)!;
  C(p.Members.Count(x=>x.Role=="rear")==1&&p.Member(p.Support)!.Role=="support","rear needs known threat and preserves supporter");
  board.Clear();p=board.TryStart("a","e",10,10,0,Team(),c,100)!;C(p.Members.All(x=>x.Role!="rear"),"no automatic rear guard");
  foreach(var name in new[]{"Timmy","Rat","SnappingTurtle","Coward"})C(SquadManeuverBoard.Score(name,true,100,c)<0,"cautious stays support "+name);
  C(SquadManeuverBoard.Score("Chad",true,100,c)>SquadManeuverBoard.Score("Normal",true,100,c),"aggressive priority");
  C(p.Trace("p1",10,c).Members.Length==4&&p.Trace("p1",10,c).Shots==0,"detached complete trace");

  // A new support plan does not reset the suppressor's source burst pause.
  var fire=new SuppressionDisciplinePolicy();var limits=new SuppressionDisciplineConfig();
  var frame=new SuppressionDisciplineFrame{Now=10,Loaded=30,Capacity=30,Target="e",Identity="e:1",SupportPlan="one"};
  fire.NativeCall(frame,limits,true,default,.5);frame.Now=10.1;frame.Loaded=25;fire.NativeCall(frame,limits,true,default,.5);
  frame.SupportPlan="two";C(fire.NativePaused(11),"plan changes cannot bypass source burst pause");
  C(!fire.NativePaused(13.2),"source pause expires without new-contact ledger");
  Console.WriteLine($"{checks} S03 policy/config checks passed");return checks;
 }
}
