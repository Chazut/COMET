using System;
using System.Linq;
using Comet.Core;

// Behavioral contract: zzap fbb86b83 (see SOURCE_BASELINE.md).
internal static class PlanProgressTests
{
 public static int Run()
 {
  int n=0; void Check(bool ok,string why){n++;if(!ok)throw new Exception("PlanProgress: "+why);}

 var p=new PlanProgress(new());var f=new ProgressFrame{Owned=true,HasThreat=true,Action="Hold",Attempt="a"};
 for(int i=0;i<240;i++){f.Now=i*.25;Check(!p.Observe(f),"diagnosis cannot force replan");}
 Check(p.Current.HoldSeconds>50&&!p.Current.Reconsider,"long wait remains observable, not forbidden");
 p.Fail("a",60,"native-path-failed");Check(!p.Blocked("a",60),"failure does not create a COMET cooldown");
 var choices=new System.Collections.Generic.List<Candidate>{new(){Decision="Hold",Score=60,Eligible=true},new(){Decision="Push",Score=90,Eligible=false}};
 p.Adjust(choices,60);Check(choices[0].Score==60&&choices[0].Eligible&&!choices[1].Eligible,"observer cannot alter utility or resurrect native rejection");
 f.Action="Search";for(int i=240;i<280;i++){f.Now=i*.25;p.Observe(f);}Check(p.Current.StalledSeconds>5&&!p.Current.Reconsider,"stalled movement is recorded only");
 f.X=2;f.Now+=.25;p.Observe(f);Check(p.Current.StalledSeconds==0,"actual displacement clears diagnostic stall");
 f.Scoped=false;p.Observe(f);Check(p.Current.Reason=="progress-reset","raid/scope cleanup");

  return n;
 }
}
