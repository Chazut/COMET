using System;
using System.Linq;
using Comet.Core;

// Behavioral contract: zzap fbb86b83 (see SOURCE_BASELINE.md).
internal static class DoorStepTests
{
 public static int Run()
 {
  int n=0; void Check(bool ok,string why){n++;if(!ok)throw new Exception("DoorStep: "+why);}

  var c=new DoorStepPeekConfig();c.Validate();Check(c.ObservationMin==.15&&c.ObservationMax==.3&&c.RestMin==0&&c.RestMax==0,"source short peek timing");
  var p=new DoorStepPeek();Check(p.Begin(new(){State="completed",Returned=true,Door="d",Target="e"},10,0,c),"peek continuation");
  p.Prepared(new(1,0,0),new(2,0,0),1,.2,0,"peek",10);
  Check(p.Active&&p.Trace!.State=="preparing","zero rest accepted");
  Check(DoorStepGeometry.Route(new(),default,default,new(1,.5,0),new[]{default(DoorPoint),new DoorPoint(1,.5,0)},true,.8)=="","no COMET lane or height restrictions");
  Check(DoorStepGeometry.Route(new(),default,default,new(1,0,0),Array.Empty<DoorPoint>(),false,.8)!="","actual path remains required");
  p.End("raid-ended",11);Check(!p.Active,"cleanup");

  return n;
 }
}
