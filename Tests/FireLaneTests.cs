using System;
using System.Linq;
using Comet.Core;

// Behavioral contract: zzap fbb86b83 (see SOURCE_BASELINE.md).
internal static class FireLaneTests
{
 public static int Run()
 {
  int n=0; void Check(bool ok,string why){n++;if(!ok)throw new Exception("FireLane: "+why);}

  var c=new FireLaneConfig();c.Validate();Check(c.Width==.9&&c.MaxHold==.8&&c.SideDistance==1.2,"source spacing and deadlock duration");
  var ray=new FireLaneRay{Shooter="mate",From=new(0,1.2,0),To=new(10,1.2,0)};
  Check(FireLaneGeometry.Blocks(new(5,0,0),new(5,0,1),ray,c.Width),"current chest in firing lane");
  Check(FireLaneGeometry.Blocks(new(5,0,1.5),new(5,0,.5),ray,c.Width),"one metre ahead enters lane");
  Check(!FireLaneGeometry.Blocks(new(5,0,-2),new(5,0,2),ray,c.Width),"no added swept-volume veto");
  Check(!FireLaneGeometry.Blocks(new(-1,0,0),new(),ray,c.Width),"behind shooter excluded");
  var p=new FireLanePolicy();var f=new FireLaneFrame{Now=10,Threat=ray,Inside=false};
  Check(p.Tick(f,c)&&p.Trace!.State=="holding","wait before crossing");f.Now=10.81;
  Check(!p.Tick(f,c),"source deadlock release after .8s");f.Now=11;Check(!p.Tick(f,c),"two second deadlock cooldown");
  f.Now=13;f.Inside=true;f.LeftSafe=true;f.Left=new(5,0,1.2);Check(p.Tick(f,c)&&p.Trace!.State=="stepping","step away when already obstructing");
  f.Threat=null;Check(!p.Tick(f,c),"clear lane releases immediately");

  return n;
 }
}
