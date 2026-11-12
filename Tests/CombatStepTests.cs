using System;
using System.Linq;
using System.Collections.Generic;
using Comet.Core;

// zzap fbb86b83 behavior; see SOURCE_BASELINE.md.
internal static class CombatStepTests
{
 public static int Run()
 {
  int n=0;void Check(bool ok,string why){n++;if(!ok)throw new Exception("CombatStep: "+why);}

 var c=new CombatStepConfig();c.Validate();
 CombatStepFrame F(double now=10)=>new(){Now=now,Target="e",Distance=12,Known=new(0,0,12)};
 var p=new CombatStep();Check(p.Begin(F(),c,0,0,0),"source diamond start");
 Check(p.Request(new(1,0,0),"right",10,0,c),"first tap");
 Check(p.Observe(F(10.4),c)&&p.Trace!.CompletedSteps==1,"tap timer counts even without travel proof");
 Check(p.Request(new(-4,1,0),"left",10.4,0,c),"no COMET anchor or floor cap");
 Check(p.Observe(F(100),c),"engagement not limited to short sequences");
 var f=F(100.1);f.Visible=false;f.ContactAge=.9;Check(p.Observe(f,c),"source recent sight tolerance");
 f.ContactAge=1;Check(!p.Observe(f,c),"source sight grace ends");
 Check(CombatStep.Route(default,default,new(5,2,0),new[]{default(DoorPoint),new DoorPoint(5,2,0)},true,1),"actual complete route, no added corridor");
 foreach(string personality in new[]{"Normal","Chad","GigaChad","Wreckless"})Check(c.Chances[personality]==100,"source aggressive personalities");
 foreach(string personality in new[]{"Rat","Coward","Timmy","SnappingTurtle"})Check(c.Chances[personality]==0,"source cautious personalities");
 f=F();f.Ghost=true;Check(CombatStep.Gate(f,c)!="","ghost handoff retained");

 return n;
 }
}
