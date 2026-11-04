using Comet.Core;
using System.Text.Json;
internal static class CoverMateTests
{
 public static int Run()
 {
  int n=0;void C(bool ok,string why){n++;if(!ok)throw new Exception("S04: "+why);}
  var c=new CoverMateConfig();c.Validate();
  C(c.Enabled&&c.MateDistance==20&&c.Near==3&&c.Far==6&&c.MaxPath==15&&c.Protectors==2,"spatial defaults");
  C(c.Duration==15&&c.SurgeryDuration==60&&c.StallTimeout==3&&c.ReadyDelay==.2&&c.Retry==4,"timing defaults");
  C(JsonSerializer.Deserialize<CometConfig>("{}")!.CoverMate.Enabled,"old configuration migrates");
  foreach(var property in typeof(CoverMateConfig).GetProperties().Where(p=>p.PropertyType==typeof(double)))
   foreach(var value in new[]{double.NaN,double.PositiveInfinity,-1d,1000d})
   {var bad=new CoverMateConfig();property.SetValue(bad,value);bool rejected=false;try{bad.Validate();}catch(ArgumentException){rejected=true;}C(rejected,"reject "+property.Name+" "+value);}
  foreach(var max in new[]{0,4}){var bad=new CoverMateConfig{Protectors=max};try{bad.Validate();C(false,"protector range");}catch(ArgumentException){n++;}}
  var finishing=new CoverMateBoard();finishing.Observe("m",0,"Reload","",true,false,0,0,c);
  C(finishing.Observe("m",16,"Reload","",false,false,0,0,c)!.Stalled,"lost busy flag with unready hands still bounded");
  var chain=new CoverMateBoard();chain.Observe("m",0,"Medicine","bandage",true,false,3,100,c);
  chain.Observe("m",4,"Surgery","cms",true,false,30,100,c);
  C(!chain.Observe("m",30,"Surgery","cms",true,false,30,100,c)!.Ended,"new actual operation uses its native animation duration");
  var board=new CoverMateBoard();
  C(board.Observe("mate",0,"Medicine","kit",false,false,10,100,c)==null,"decision alone not operation");
  var op=board.Observe("mate",1,"Surgery","kit",true,false,30,100,c)!;
  C(op.Kind=="Surgery"&&!op.Ended,"observed surgery");
  C(board.Observe("mate",14,"Surgery","kit",true,false,30,100,c)==op&&!op.Ended,"long treatment beyond zzap twelve seconds");
  C(!board.Observe("mate",30,"Surgery","kit",true,false,30,100,c)!.Ended,"unchanged health during animation allowed");
  C(!board.Observe("mate",31,"Medicine","kit",false,false,0,90,c)!.Ended,"medicine ends before firearm ready");
  C(!board.Observe("mate",32,"Reload","",false,true,0,30,c)!.Ended,"readiness interval");
  C(!board.Observe("mate",32.1,"Reload","",false,false,0,30,c)!.Ended,"lost readiness resets interval");
  board.Observe("mate",33,"Reload","",false,true,0,30,c);
  C(board.Observe("mate",33.21,"Reload","",false,true,0,30,c)!.Ended,"stable ready ends cover");
  var next=board.Observe("mate",34,"Reload","",true,false,0,0,c)!;C(next.Id!=op.Id,"genuine next operation");
  C(board.Attempt("guard","mate",next.Id,34),"first attempt");C(!board.Attempt("guard","mate",next.Id,34),"no reroll same operation");
  C(board.Reserve("a","mate",next.Id,"north",0,10,34,c),"first sector");
  C(!board.Reserve("b","mate",next.Id,"north",10,0,34,c),"same sector not duplicated");
  C(!board.Reserve("b","mate",next.Id,"near north",1,10,34,c),"different name same direction not duplicate");
  C(board.Reserve("b","mate",next.Id,"east",10,0,34,c),"second distinct approach");
  C(!board.Reserve("c","mate",next.Id,"south",0,-10,34,c),"two protectors cap");
  C(board.Reserve("c","other",next.Id,"south",0,-10,34,c),"independent patient");
  C(board.Count("mate",next.Id,34)==2,"count actual leases");
  C(board.Renew("a",34.5)&&board.Valid("a",35.49)&&!board.Valid("a",35.51),"execution lease expires");
  C(!board.Renew("a",36),"expired cannot resurrect");
  C(board.Reserve("c","mate",next.Id,"south",0,-10,38,c),"expired reservations free sectors");
  C(board.Observe("mate",49,"Reload","",true,false,0,0,c)!.Stalled,"stuck ordinary operation bounded");
  C(board.Observe("mate",55,"Reload","",true,false,0,0,c)!.Id==next.Id,"stuck flags do not manufacture new operation");
  board.Observe("mate",56,"Reload","",false,true,0,30,c);board.Observe("mate",56.3,"Reload","",false,true,0,30,c);
  C(board.Observe("mate",57,"Reload","",true,false,0,0,c)!.Id!=next.Id,"real recovery permits next reload");
  board.Clear();C(board.Count("mate",next.Id,58)==0,"raid cleanup");
  var longOp=board.Observe("s",60,"Surgery","kit",true,false,55,100,c)!;
  C(!board.Observe("s",110,"Surgery","kit",true,false,55,100,c)!.Ended,"long actual duration respected");
  C(board.Observe("s",120,"Surgery","kit",true,false,55,100,c)!.Stalled,"surgery ceiling");
  C(board.Observe("s",1,"Reload","",true,false,0,0,c)!.Started==1,"clock reset clears stale episode");
  C(board.Observe("x",double.NaN,"Reload","",true,false,0,0,c)==null,"invalid clock rejected");
  Console.WriteLine($"{n} S04 policy/config checks passed");return n;
 }
}
