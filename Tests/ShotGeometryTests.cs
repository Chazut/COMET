using System;
using System.Linq;
using Comet.Core;

// Behavioral contract: zzap fbb86b83 (see SOURCE_BASELINE.md).
internal static class ShotGeometryTests
{
 public static int Run()
 {
  int n=0; void Check(bool ok,string why){n++;if(!ok)throw new Exception("ShotGeometry: "+why);}

  var c=new ShotGeometryConfig();c.Validate();Check(c.CacheSeconds==.3,"source scan interval");
  ShotGeometryFrame F(string kind,double hit=1)=>new(){Complete=true,TargetDistance=10,Surfaces=new[]{new ShotSurface{Kind=kind,Distance=hit}}};
  foreach(var kind in new[]{"glass","door"}) Check(ShotGeometryPolicy.Evaluate(F(kind),c).Allowed,"door/glass passage retained");
  Check(!ShotGeometryPolicy.Evaluate(F("solid"),c).Allowed,"wall in front blocks hidden target");
  Check(ShotGeometryPolicy.Evaluate(F("solid",4),c).Allowed,"no added material penetration simulation");
  var f=F("solid",4);f.FloorDifference=2.3;Check(!ShotGeometryPolicy.Evaluate(f,c).Allowed,"native different floor rejection");
  f.Visible=true;Check(ShotGeometryPolicy.Evaluate(f,c).Allowed,"real visibility wins");
  f=F("solid");f.Blindfire=true;Check(ShotGeometryPolicy.Evaluate(f,c).Allowed,"native blindfire exempt from close wall");
  f=F("solid",9.5);Check(ShotGeometryPolicy.Evaluate(f,c).Allowed,"obstacle at target tolerated");
  var trace=ShotGeometryPolicy.Evaluate(F("door"),c);
  bool Reuse(double age,DoorPoint aim)=>ShotGeometryPolicy.CanReuse(10+age,10,2,1,trace,new(),new(),aim,new(),new(),new(),"e","e",true,c);
  Check(Reuse(.2,new(.9,0,0))&&!Reuse(.31,new())&&!Reuse(.2,new(1.1,0,0)),"cache expiration and source target movement threshold");

  return n;
 }
}
