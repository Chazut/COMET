using System;
using Comet.Core;
internal static class OwnGrenadeSafetyTests
{
 public static int Run()
 {
  int checks=0;void C(bool value,string why){checks++;if(!value)throw new Exception("G04 "+why);}
  var c=new OwnGrenadeSafetyConfig();c.Validate();C(c.Enabled&&c.ExitPadding==4&&c.DepartureGrace==.7&&c.Refresh==.1,"source defaults");
  OwnGrenadeEvidence G()=>new(){Owner="pmc",Item="item",Projectile=-7,Radius=6,Fuse=3.5,Position=new(0,0,0)};
  bool Open(DoorPoint a,DoorPoint b)=>false;bool Wall(DoorPoint a,DoorPoint b)=>true;
  var g=G();g.Sample(new(1,0,0),.1,new(0,0,0));C(g.Grace(.1,c),"source grace");
  g.Sample(new(.5,0,0),.2,new(0,0,0));C(g.Grace(.2,c),"source age does not require departure motion");
  C(!OwnGrenadeSafetyRules.Threat(g,new(0,0,0),false,.69,c,Open),"source delay");
  C(OwnGrenadeSafetyRules.Threat(g,new(0,0,0),false,.7,c,Open),"source danger begins");
  g=G();C(OwnGrenadeSafetyRules.Threat(g,new(6,0,0),false,1,c,Open),"six metre inclusive radius");
  C(!OwnGrenadeSafetyRules.Threat(g,new(6.01,0,0),false,1,c,Open),"outside source radius");
  C(!OwnGrenadeSafetyRules.Threat(g,new(3,0,0),false,1,c,Wall),"single solid cover ray");
  C(OwnGrenadeSafetyRules.Threat(g,new(10,0,0),true,1,c,Wall),"keep dodging through ten metres");
  C(!OwnGrenadeSafetyRules.Threat(g,new(10.01,0,0),true,1,c,Open),"source escape radius");
  C(!g.Expired(8)&&g.Expired(8.01),"source eight second lifetime");
  g.Missing(2);C(g.Expired(2)&&!g.Exploded,"missing object retires danger without inventing a blast");
  g=G();C(!g.Confirm("other",-7)&&!g.Confirm("pmc",7),"owner and signed id");C(g.Confirm("pmc",-7),"confirmed event");
  C(!OwnGrenadeSafetyRules.Threat(g,new(0,0,0),false,1,c,Open),"confirmed blast retires danger");
  foreach(var change in new Action<OwnGrenadeSafetyConfig>[] {v=>v.Refresh=double.NaN,v=>v.DepartureGrace=3,v=>v.ExitPadding=0,v=>v.DangerMargin=-1,v=>v.Refresh=.01})
  {var bad=new OwnGrenadeSafetyConfig();change(bad);try{bad.Validate();C(false,"invalid accepted");}catch(ArgumentException){C(true,"validation");}}
  Console.WriteLine($"G04 source policy: {checks} checks passed.");return checks;
 }
}
