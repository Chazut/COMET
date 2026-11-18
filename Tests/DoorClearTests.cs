using System;
using System.Linq;
using Comet.Core;

// Behavioral contract: zzap fbb86b83 (see SOURCE_BASELINE.md).
internal static class DoorClearTests
{
 public static int Run()
 {
  int n=0; void Check(bool ok,string why){n++;if(!ok)throw new Exception("DoorClear: "+why);}

  var c=new DoorRoomClearConfig();c.Validate();
  for(int i=0;i<64;i++){
   var p=new DoorRoomClear();Check(p.Begin("d"+i,"e"+i,"heard","Chad",default,1,100,0,c),"source room selection");p.End("done");
  }
  var many=new DoorRoomClear();for(int i=0;i<40;i++){Check(many.Begin("d"+i,"e"+i,"heard","Chad",default,1,100+i*100,0,c),"no 32-contact lifetime veto");many.End("done");}
  many.Clear();Check(!many.Active,"lifecycle resets");

  foreach (bool sharedJudgment in new[] { false, true })
  foreach (bool direct in new[] { false, true })
  foreach (double preference in new[] { 0d, 1d, 100d })
  {
   c.GrenadePreference = preference;
   var clear = new DoorRoomClear();
   Check(clear.Begin("door","enemy","heard","Chad",default,1,100,0,c),"room selected");
   clear.Opened(101); clear.Ready(102);
   bool chosen = clear.Choose(direct,true,"blocked","available",0.5,0.5,103,c,sharedJudgment);
   bool grenade = preference > 0 && (sharedJudgment || !direct || preference == 100);
   Check(chosen == (direct || grenade),"0% cannot force a throw when direct entry fails");
   Check((clear.Trace!.Variant == "grenade") == grenade,"positive chance preserves native or shared judgment");
   Check(clear.Trace.Alternatives[1].Contains("disabled by preset") == (preference == 0),"eligibility diagnostic matches choice");
  }
  return n;
 }
}
