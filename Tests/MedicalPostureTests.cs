using System;
using System.Linq;
using Comet.Core;

// Behavioral contract: zzap fbb86b83 (see SOURCE_BASELINE.md).
internal static class MedicalPostureTests
{
 public static int Run()
 {
  int n=0; void Check(bool ok,string why){n++;if(!ok)throw new Exception("MedicalPosture: "+why);}

  var c=new MedicalPostureConfig();c.Validate();
  Check(c.ResumeDelay==0,"no additional settling delay");
  foreach(var mode in new[]{"FirstAid","Surgery","Stims"}){
   var p=new MedicalPosture();var f=new MedicalPostureFrame{Now=10,Using=true,Treatment=mode,Tilt=5};
   Check(p.Tick(f,c),"native medical operation prevents lean");
   f.Using=false;f.MedicineHands=true;f.Now=10.1;Check(p.Tick(f,c),"medicine hands still prevent lean");
   f.MedicineHands=false;f.Now=10.2;Check(!p.Tick(f,c),"resume immediately without firearm, ground or tilt proofs");
   f.Using=true;c.Enabled=false;Check(!p.Tick(f,c),"explicit disable");c.Enabled=true;
  }

  return n;
 }
}
