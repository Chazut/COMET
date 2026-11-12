using System;
using System.Linq;
using Comet.Core;

// Behavioral contract: zzap fbb86b83 (see SOURCE_BASELINE.md).
internal static class HoldAngleTests
{
 public static int Run()
 {
  int n=0; void Check(bool ok,string why){n++;if(!ok)throw new Exception("HoldAngle: "+why);}

 var c=new HoldAngleConfig();var p=new HoldAngle();var f=new HoldAngleFrame{Now=10,HoldStarted=9,ObservedAt=1,Pose=.6,Target="enemy",Known=new(10,0,0),Source="shared-hearing"};
 Check(p.Begin(f,c),"shared known contact is usable without extra age or origin veto");
 p.SourceObservation(true,false,new(.3,0,0),1);Check(p.Active&&p.Trace!.MoveRequested&&p.Trace.Candidate=="peek-try-1","source peek step is displayed");
 p.SourceObservation(false,true,new(.3,0,0),2);Check(p.Pose==1&&p.Trace!.Arrived,"source standing adjustment");
 f.Now=40;p.SourceObservation(false,false,new(.3,0,0),2);Check(p.Active,"no COMET four-second action deadline");
 p.End("native-ended",f,c,true);Check(!p.Active,"source action release");
 Check(p.Begin(f,c),"no extra failed-attempt cooldown");
 c.Enabled=false;Check(HoldAngle.Gate(f,c)=="hold-angle-disabled","feature toggle preserved");

  return n;
 }
}
