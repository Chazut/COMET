using System;
using System.Linq;
using Comet.Core;

// Behavioral contract: zzap fbb86b83 (see SOURCE_BASELINE.md).
internal static class DoorEmergencyTests
{
 public static int Run()
 {
  int n=0; void Check(bool ok,string why){n++;if(!ok)throw new Exception("DoorEmergency: "+why);}

  var c=new DoorEmergencyConfig();c.Validate();
  foreach(bool space in new[]{false,true})foreach(bool allies in new[]{false,true})foreach(bool exposure in new[]{false,true})
   Check(DoorEmergencyRules.Route(true,space,allies,7,0,3,10,10,exposure,c)=="","source escape path without COMET protection/height/forward veto");
  Check(DoorEmergencyRules.Route(false,true,true,0,7,3,0,0,false,c)!="","real path failure");
  Check(DoorEmergencyRules.Route(true,true,true,0,7,6,0,0,false,c)!="","source five metre recovery cap");

  return n;
 }
}
