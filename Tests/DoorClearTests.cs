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

  return n;
 }
}
