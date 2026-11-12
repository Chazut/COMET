using Comet.Core;
internal static class SquadTradeTests
{
 public static int Run()
 {
  int n=0;void C(bool ok,string why){n++;if(!ok)throw new Exception("S05 source: "+why);}
  var c=new SquadTradeConfig();c.Validate();C(c.Window==10&&c.ContactAge==20&&c.MaxDistance==80,"source acquisition limits");
  var m=new SquadTradeMemory();
  C(m.Observe("a","A",1,false,false,true,"enemy",.5,c)==null,"alive is not a death");
  C(m.Observe("a","A",1,true,true,true,"enemy",.5,c)==null,"Fika downed is not dead");
  var loss=m.Observe("a","A",1,true,false,false,"enemy",.5,c)!;
  C(loss!=null&&loss.ReadyAt==1&&m.Peek(1,c)==loss,"squad death needs no visual witness or added delay");
  C(m.Take(1,c)==loss&&m.Take(1,c)==null,"death consumed once");
  C(m.Observe("a","A",2,true,false,true,"other",0,c)==loss,"duplicate callback cannot retrade");
  m.Observe("b","B",2,true,false,false,"enemy",0,c);C(m.Take(12.01,c)==null,"source ten-second trade window");
  for(int i=0;i<150;i++)m.Observe("m"+i,"",20,true,false,false,"e",0,c);
  C(m.Count==152,"no arbitrary COMET per-raid death cap");
  foreach(bool panic in new[]{true,false})foreach(double ammo in new[]{0d,20,100})foreach(double roll in new[]{0d,.99})
  {C(SquadTradeMemory.Choose(true,true,ammo,panic,roll,c,out _)=="push","rush personality pushes without an added fear/ammo/chance gate");C(SquadTradeMemory.Choose(true,false,ammo,panic,roll,c,out _)=="watch","other personalities seek an angle");}
  m.Clear();C(m.Count==0,"raid cleanup");return n;
 }
}
