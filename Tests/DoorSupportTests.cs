using System;
using System.Linq;
using Comet.Core;

// Behavioral contract: zzap fbb86b83 (see SOURCE_BASELINE.md).
internal static class DoorSupportTests
{
 public static int Run()
 {
  int n=0; void Check(bool ok,string why){n++;if(!ok)throw new Exception("DoorSupport: "+why);}

  var c=new DoorSupportConfig();c.Validate();var p=new DoorSupportBoard();
  Check(c.Duration==150&&c.MaxDistance==20,"source session and recruitment defaults");
  var leader=new SourceDoorLeaderLifetime();
  Check(leader.Continue(1,true,true)&&leader.Continue(2,true,false),"source leader departure grace begins");
  Check(leader.Continue(4,true,false)&&!leader.Continue(4.001,true,false),"source two-second departure boundary");
  Check(leader.Continue(5,true,true)&&leader.Continue(6,true,false),"return resets grace");
  Check(!leader.Continue(6.1,false,true),"death immediate");
  Check(p.Begin("op","leader","door","enemy",10,30,c),"support operation");
  Check(p.Offer("op","a","overwatch",default,new(1,0,0),100,0,10,c)!=null,"no path ETA or cover ratio gate");
  Check(p.Offer("op","b","rear-guard",default,new(1,0,0),0,0,10,c)!=null,"rear guard may hold existing position");
  Check(p.Accept("a",10),"native acceptance");p.RequestMove("a",true);
  Check(p.Arrive("a",default,false,false,false),"arrival not vetoed by redundant native completion/protection proofs");
  p.Clear();Check(!p.Accept("a",11),"raid cleanup");

  return n;
 }
}
