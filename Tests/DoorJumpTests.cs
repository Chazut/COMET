using System;
using System.Linq;
using Comet.Core;

// Behavioral contract: zzap fbb86b83 (see SOURCE_BASELINE.md).
internal static class DoorJumpTests
{
 public static int Run()
 {
  int n=0; void Check(bool ok,string why){n++;if(!ok)throw new Exception("DoorJump: "+why);}

  var c=new DoorJumpPeekConfig();c.Validate();Check(c.MinimumStamina==0&&c.MinimumSpeed==1.2,"source native jump capability, no percentage gate");
  var p=new DoorJumpPeek();p.BeginSelected(new(){Door="d",Target="e",Personality="Chad"},10);p.Start(default,10);
  Check(p.Observe(new(.1,0,0),1.3,0,true,false,false,3,10.1,c),"low stamina does not preempt native CanJump");
  Check(p.MayJump(true,true,1,10.1,c),"source moving speed allows jump");p.JumpAccepted(false,10.1);
  Check(p.MayJump(true,true,1,10.2,c),"native refusal can retry");p.JumpAccepted(true,10.2);
  Check(!p.MayJump(true,true,1,10.3,c),"single outward jump");
  p.Observe(new(1,0,0),1.3,0,true,true,false,3,10.4,c);
  p.Observe(new(1,0,0),0,0,true,false,false,3,11,c);
  Check(p.Trace!.State=="return-pending","no airborne proof required to progress");
  p.ReturnStarted(11);p.Observe(default,0,0,true,true,false,3,11.1,c);Check(p.Trace.Returned,"return arrival");
  var g=new DoorGeometry();Check(DoorJumpGeometry.Route(g,default,default,new(1,1,1),new[]{default(DoorPoint),new DoorPoint(1,1,1)},true)=="","no added corridor proof in policy");

  return n;
 }
}
