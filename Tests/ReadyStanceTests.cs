using Comet.Core;
using System.Text.Json;
internal static class ReadyStanceTests
{
 public static int Run()
 {
  int n=0;void C(bool ok,string why){n++;if(!ok)throw new Exception("H05: "+why);}
  var c=new ReadyStanceConfig();c.Validate();C(c.Enabled&&c.OrdinaryDuration==15&&c.NearbyDuration==25&&c.NearbyDistance==40&&c.CueFreshness==5&&c.Interval==.25,"approved defaults");
  foreach(var prop in typeof(ReadyStanceConfig).GetProperties().Where(p=>p.PropertyType==typeof(double)))foreach(var bad in new[]{-1d,double.NaN,double.PositiveInfinity,1000d})
  {var v=new ReadyStanceConfig();prop.SetValue(v,bad);bool fail=false;try{v.Validate();}catch(ArgumentException){fail=true;}C(fail,"invalid "+prop.Name);}
  {var v=new CometConfig();v.ReadyStance.NearbyDuration=10;bool fail=false;try{v.Validate();}catch(ArgumentException){fail=true;}C(fail,"nearby duration shorter than ordinary rejected");}
  C(JsonSerializer.Deserialize<CometConfig>("{}")!.ReadyStance.NearbyDuration==25,"old configs receive defaults");
  {var v=new CometConfig();v.ReadyStance.Enabled=false;v.ReadyStance.OrdinaryDuration=12;v.ReadyStance.NearbyDistance=30;v.Validate();var x=JsonSerializer.Deserialize<CometConfig>(JsonSerializer.Serialize(v))!;C(!x.ReadyStance.Enabled&&x.ReadyStance.OrdinaryDuration==12&&x.ReadyStance.NearbyDistance==30,"server config roundtrip");}
  {var p=new ReadyStance();C(!p.Observe(10,10,"footstep","enemy",10,false,c)&&!p.Evaluate(10,true,"",c),"no fight means no post-combat posture");}
  foreach(var tuple in new[]{(100d,10d,25d),(40d,10d,35d),(40.01,10d,25d),(20d,5d,30d),(20d,4.99,25d)})
  {
   var p=new ReadyStance();p.Observe(10,tuple.Item2,"fight","enemy",tuple.Item1,true,c);C(p.Deadline==tuple.Item3,"distance/freshness boundary");
   C(p.Evaluate(11,true,"",c),"ordinary native patrol veto");p.ObservePosture(false);C(!p.Trace!.Applied&&p.Trace.State=="pending","request not evidence of raised posture");
   p.ObservePosture(true);C(p.Trace.Applied&&p.Trace.State=="holding","actual raised posture observed");
   C(!p.Evaluate(tuple.Item3,true,"",c)&&p.Trace.State=="expired","exact expiry");
  }
  {var p=new ReadyStance();p.Observe(10,10,"fight","enemy",100,true,c);C(!p.Observe(11,10,"footstep","enemy",10,false,c),"same clue cannot refresh");
   C(p.Observe(15,14,"footstep","enemy",20,false,c)&&p.Deadline==39,"fresh heard extension uses original event timestamp");
   for(int i=16;i<=19;i++)C(!p.Observe(i,14,"footstep","enemy",20,false,c)&&p.Deadline==39,"repeated reading does not extend");
   foreach(var t in new[]{(20d,14d,10d),(20d,21d,10d),(20d,20d,41d),(20d,20d,double.NaN)})C(!p.Observe(t.Item1,t.Item2,"heard","enemy",t.Item3,false,c)&&p.Deadline==39,"stale/future/far/invalid clue rejected");
   C(!p.Observe(40,40,"footstep","enemy",10,false,c),"expired post-combat episode cannot be rearmed by sound alone");
   C(p.Observe(41,double.NaN,"incoming fire","",null,true,c)&&p.Deadline==56,"new fight without known position rearms ordinary hold");}
  foreach(var reason in new[]{"ready-stance-medicine","ready-stance-reload","ready-stance-sprint","ready-stance-door","ready-stance-hands"})
  {var p=new ReadyStance();p.Observe(10,10,"fight","e",10,true,c);C(!p.Evaluate(11,true,reason,c)&&p.Trace!.State=="suspended","priority "+reason);C(p.Deadline==35&&p.Evaluate(20,true,"",c),"resume remaining window, no reset");C(!p.Evaluate(36,true,"",c),"long interaction consumes window");}
  {var p=new ReadyStance();p.Observe(10,10,"fight","e",10,true,c);C(!p.Evaluate(11,false,"",c)&&p.Trace!.NativePatrol==false,"native already raised: no ownership claim");p.ObservePosture(true);C(!p.Trace!.Applied,"observation alone does not imply H05 ownership");p.Evaluate(34.8,true,"",c);p.ObservePosture(true);C(p.Trace.ValidFor<=.21,"lease cannot outlive deadline");}
  foreach(string mode in new[]{"clock","nan","off","clear"})
  {var p=new ReadyStance();var v=new ReadyStanceConfig();p.Observe(10,10,"fight","e",10,true,v);p.Evaluate(11,true,"",v);p.ObservePosture(true);if(mode=="clear")p.Clear("ready-stance-raid-ended");else{if(mode=="off")v.Enabled=false;p.Evaluate(mode=="clock"?5:mode=="nan"?double.NaN:12,true,"",v);}C(!p.Trace!.Applied&&!p.Trace.Requested,"release "+mode);}
  Console.WriteLine($"{n} H05 policy/config checks passed");return n;
 }
}
