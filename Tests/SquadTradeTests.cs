using Comet.Core;
using System.Text.Json;
internal static class SquadTradeTests
{
 public static int Run()
 {
  int n=0;void C(bool ok,string why){n++;if(!ok)throw new Exception("S05: "+why);}
  var c=new SquadTradeConfig();c.Validate();
  C(c.Enabled&&c.Window==10&&c.DelayMin==.3&&c.DelayMax==.8&&c.ContactAge==5&&c.MaxDistance==40&&c.MaxPath==25,"start defaults");
  C(c.Duration==15&&c.StallTimeout==3&&c.MinimumAmmo==50&&c.AggressiveChance==60&&c.OtherChance==20,"execution defaults");
  C(JsonSerializer.Deserialize<CometConfig>("{}")!.SquadTrade.Enabled,"old server config migrates");
  foreach(var p in typeof(SquadTradeConfig).GetProperties().Where(p=>p.PropertyType==typeof(double)))foreach(double v in new[]{double.NaN,double.PositiveInfinity,-1d,10000d})
  {var bad=new SquadTradeConfig();p.SetValue(bad,v);try{bad.Validate();C(false,"reject "+p.Name);}catch(ArgumentException){n++;}}
  foreach(var bad in new[]{new SquadTradeConfig{DelayMin=2,DelayMax=1},new SquadTradeConfig{Window=1,DelayMax=2},new SquadTradeConfig{Duration=3,StallTimeout=4}})
  {try{bad.Validate();C(false,"cross-field limit");}catch(ArgumentException){n++;}}
  var m=new SquadTradeMemory();
  foreach(var flags in new[]{(false,false,true),(true,true,true),(true,false,false)})C(m.Observe("mate","Name",1,flags.Item1,flags.Item2,flags.Item3,"enemy",.5,c)==null,"death/downed/perception guard");
  var loss=m.Observe("mate","Name",1,true,false,true,"enemy",1,c)!;
  C(loss.ReadyAt==1.8&&m.Take(1.79,c)==null,"reaction delay");C(m.Take(1.8,c)==loss,"first reaction");
  C(m.Observe("mate","Name",3,true,false,true,"other",0,c)==loss&&m.Take(3,c)==null,"duplicate event cannot reroll or change killer");
  C(m.Observe("mate","Name",3,true,false,true,"other",0,c,99)==loss&&loss.Group==0&&m.Take(3,c)==null,"regrouping cannot reset consumed death");
  m.Observe("other","Name",3,true,false,true,"",0,c);C(m.Take(14,c)==null,"expired event");
  C(m.Take(double.NaN,c)==null,"invalid clock");
  C(m.Observe("new","Name",1,true,false,true,"",0,c)!=null&&m.Count==1,"raid clock reset");m.Clear();C(m.Count==0,"cleanup");
  for(int i=0;i<128;i++)C(m.Observe("m"+i,"Name",2,true,false,true,"",0,c)!=null,"bounded record accepted");
  C(m.Observe("overflow","Name",2,true,false,true,"",0,c)==null&&m.Count==128,"bounded cap without evict/reroll");
  foreach(bool aggressive in new[]{true,false})
  {int pushes=0;for(int i=0;i<100;i++)if(SquadTradeMemory.Choose(true,aggressive,50,false,i/100d,c,out _) =="push")pushes++;C(pushes==(aggressive?60:20),"exact independent roll weights");}
  foreach(var state in new[]{(false,100d,false),(true,49d,false),(true,100d,true),(true,double.NaN,false)})
  C(SquadTradeMemory.Choose(state.Item1,true,state.Item2,state.Item3,0,c,out var why)=="watch"&&why.Length>0,"push veto");
  foreach(var roll in new[]{double.NaN,-1,1,2})C(SquadTradeMemory.Choose(true,true,100,false,roll,c,out _) =="watch","invalid/boundary roll");
  var a=new SquadTradeMemory();var b=new SquadTradeMemory();a.Observe("m","",1,true,false,true,"",0,c);b.Observe("m","",1,true,false,true,"",0,c);
  C(a.Take(2,c)!=null&&b.Take(2,c)!=null,"separate squadmates not one-per-enemy");
  Console.WriteLine($"{n} S05 policy/config checks passed");return n;
 }
}
