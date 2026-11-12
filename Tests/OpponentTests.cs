using System;
using System.Linq;
using Comet.Core;

// Behavioral contract: zzap fbb86b83 (see SOURCE_BASELINE.md).
internal static class OpponentTests
{
 public static int Run()
 {
  int n=0; void Check(bool ok,string why){n++;if(!ok)throw new Exception("Opponent: "+why);}

 var s=new Situation{Health=1,Ammo=1,KnownDistance=10,PathLength=10};var a=ZzapUtility.Hidden(s,.5,0,false);s.Weakness=.8;var b=ZzapUtility.Hidden(s,.5,0,false);Check(b[Tactic.Push]>a[Tactic.Push],"source known weakness increases push utility");
 var choices=new System.Collections.Generic.List<Candidate>{new(){Decision="Push",Score=45,Eligible=true}};OpponentAssessment.Adjust(choices,s,new());Check(choices[0].Score==45&&choices[0].Eligible,"no duplicate opponent multiplier/veto");
 s.Hurt=s.LowAmmo=true;var d=ZzapUtility.Hidden(s,.5,0,false);Check(d[Tactic.Push]==b[Tactic.Push],"removed coarse flags do not override source numeric evidence");

  return n;
 }
}
