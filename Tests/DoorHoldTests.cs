using System;
using System.Linq;
using Comet.Core;

// Behavioral contract: zzap fbb86b83 (see SOURCE_BASELINE.md).
internal static class DoorHoldTests
{
 public static int Run()
 {
  int n=0; void Check(bool ok,string why){n++;if(!ok)throw new Exception("DoorHold: "+why);}

  foreach(double protection in new[]{0d,.33,1}){
   var stay=DoorHoldSelection.Evaluate(true,protection,true,0,100,.1,30,10,1);Check(stay.Outcome=="stay","current angle kept irrespective of protection score");
   var move=DoorHoldSelection.Evaluate(false,1,true,0,100,.1,30,10,1);Check(move.Outcome=="move","no ETA/useful wait/protection delta veto");
   Check(DoorHoldSelection.Evaluate(false,0,false,0,0,1,30,0,12).Outcome=="native-hold","no candidate returns to native action");
  }

  return n;
 }
}
