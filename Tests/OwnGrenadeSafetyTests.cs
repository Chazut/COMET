using System;
using Comet.Core;
internal static class OwnGrenadeSafetyTests
{
 public static int Run()
 {
  int checks=0;void C(bool value,string why){checks++;if(!value)throw new Exception("G04 "+why);}
  var c=new OwnGrenadeSafetyConfig();c.Validate();C(c.Enabled&&c.DangerMargin==1&&c.ExitPadding==4&&c.DepartureGrace==.35&&c.Refresh==.1,"defaults");
  OwnGrenadeEvidence G()=>new(){Owner="pmc",Item="item",Projectile=-7,Radius=7,Fuse=3.5,Position=new(0,0,0)};
  bool Open(DoorPoint a,DoorPoint b)=>false;bool Wall(DoorPoint a,DoorPoint b)=>true;
  var g=G();g.Sample(new(1,0,0),.1,new(0,0,0));C(g.Grace(.1,c),"departing grace");
  C(!OwnGrenadeSafetyRules.Threat(g,new(2,0,0),false,.1,c,Open),"own departure permitted");
  C(OwnGrenadeSafetyRules.Threat(g,new(2,0,0),false,.1,c,Open,false),"no ally departure immunity");
  g.Sample(new(.5,0,0),.2,new(0,0,0));C(!g.Grace(.2,c),"rebound loses grace");C(OwnGrenadeSafetyRules.Threat(g,new(0,0,0),false,.2,c,Open),"rebound danger");
  g.Sample(new(.5,0,0),.3,new(0,0,0));C(!g.Grace(.3,c),"stalled grenade");
  g=G();C(OwnGrenadeSafetyRules.Threat(g,new(3,0,0),false,1,c,Open),"exposed");C(!OwnGrenadeSafetyRules.Threat(g,new(3,0,0),false,1,c,Wall),"solid cover");
  C(OwnGrenadeSafetyRules.Threat(g,new(9,0,0),true,1,c,Wall),"escape hysteresis even after transient occlusion");C(!OwnGrenadeSafetyRules.Threat(g,new(11,0,0),true,1,c,Open),"exit reached");
  C(!OwnGrenadeSafetyRules.SafeRoute(g,new[]{new DoorPoint(-15,0,0),new DoorPoint(15,0,0)},1,c,Open),"safe endpoints unsafe crossing");
  C(OwnGrenadeSafetyRules.SafeRoute(g,new[]{new DoorPoint(3,0,0),new DoorPoint(15,0,0)},1,c,Open),"allow native escape");
  C(!OwnGrenadeSafetyRules.SafeRoute(g,new[]{new DoorPoint(3,0,0),new DoorPoint(-15,0,0)},1,c,Open),"reject escape through grenade");
  C(!OwnGrenadeSafetyRules.SafeRoute(g,new[]{new DoorPoint(3,0,0),new DoorPoint(15,0,0),new DoorPoint(3,0,0)},1,c,Open),"no return");
  C(OwnGrenadeSafetyRules.SafeRoute(g,new[]{new DoorPoint(-15,20,0),new DoorPoint(15,20,0)},1,c,Open),"separate floor far away");
  C(OwnGrenadeSafetyRules.SafeRoute(g,new[]{new DoorPoint(-15,0,0),new DoorPoint(15,0,0)},1,c,Wall),"protected route");
  g.Missing(2);C(!g.Exploded&&g.Status=="outcome-unknown","missing is not blast");C(!g.Expired(10),"bounded unknown retention");C(g.Expired(11),"unknown timeout");C(!g.Exploded,"timeout is not blast");
  g=G();C(!g.Confirm("other",-7)&&!g.Confirm("pmc",7),"owner and signed id");C(g.Confirm("pmc",-7),"confirmed event");
  var second=G();C(!OwnGrenadeSafetyRules.Threat(g,new(0,0,0),false,1,c,Open)&&OwnGrenadeSafetyRules.Threat(second,new(0,0,0),false,1,c,Open),"another grenade still live");
  foreach(var change in new Action<OwnGrenadeSafetyConfig>[] {v=>v.Refresh=double.NaN,v=>v.DepartureGrace=.36,v=>v.ExitPadding=0,v=>v.DangerMargin=-1,v=>v.Refresh=.01})
  {var bad=new OwnGrenadeSafetyConfig();change(bad);try{bad.Validate();C(false,"invalid accepted");}catch(ArgumentException){C(true,"validation");}}
  var round=Newtonsoft.Json.JsonConvert.DeserializeObject<CometConfig>("{}")!;round.Validate();C(round.OwnGrenadeSafety.Refresh==.1,"old config defaults");
  Console.WriteLine($"G04 policy: {checks} checks passed.");return checks;
 }
}
