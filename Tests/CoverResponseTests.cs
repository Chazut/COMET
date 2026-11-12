using System;
using System.Linq;
using Comet.Core;

// Behavioral contract: zzap fbb86b83 (see SOURCE_BASELINE.md).
internal static class CoverResponseTests
{
 public static int Run()
 {
  int n=0; void Check(bool ok,string why){n++;if(!ok)throw new Exception("CoverResponse: "+why);}

 var c=new CometConfig();var s=new VisibleSituation{Contact=new(){Health=1,Ammo=1,KnownDistance=10,Looking=true},CoverDistance=2,CurrentExposure=1};
 var near=ZzapUtility.Visible(s,.5);s.CoverDistance=15;var far=ZzapUtility.Visible(s,.5);Check(near[VisibleTactic.Cover]>far[VisibleTactic.Cover],"source run cost reduces distant cover preference");
 var choices=new System.Collections.Generic.List<Candidate>{new(){Decision="Cover",Score=40,Eligible=true},new(){Decision="Shoot",Score=50,Eligible=false}};
 CoverResponseModel.Adjust(choices,s,c);Check(choices[0].Score==40&&choices[0].Eligible&&!choices[1].Eligible,"no extra exposure/route/threat layer");
 s.Contact.LowAmmo=s.Contact.Hurt=true;s.Contact.Health=1;s.Contact.Ammo=1;var unchanged=ZzapUtility.Visible(s,.5);Check(unchanged[VisibleTactic.Cover]==far[VisibleTactic.Cover],"coarse COMET flags cannot override real source health/ammo");

  return n;
 }
}
