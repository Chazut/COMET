using System;
using System.Linq;
using System.Collections.Generic;
using Comet.Core;

// zzap fbb86b83 behavior; see SOURCE_BASELINE.md.
internal static class FirePauseTests
{
 public static int Run()
 {
  int n=0;void Check(bool ok,string why){n++;if(!ok)throw new Exception("FirePause: "+why);}

 var c=new FirePauseConfig();c.Validate();var p=new FirePause();p.Begin(1,"e",10,0,0,c);
 var f=new FirePauseFrame{Now=10,Target="e",Weapon="rifle",Mode="automatic",Distance=20,SinceHit=10,CanFire=true,RouteFree=true,SequenceDeadline=100,CompletedSteps=1};
 Check(!p.TryStart(f,c),"plant requires two taps");f.CompletedSteps=2;Check(p.TryStart(f,c),"source plant starts immediately");
 f.Now=10.01;f.Speed=3;Check(p.Observe(f,c),"inertia does not require stop confirmation");
 f.Now=11;Check(!p.Observe(f,c)&&p.Waiting,"timer completes even without a shot");
 Check(!p.TryStart(f,c),"two more taps before next plant");f.CompletedSteps=4;Check(p.TryStart(f,c),"second plant");
 f.SinceHit=0;Check(!p.Observe(f,c),"source recent hit cancels plant");

 return n;
 }
}
