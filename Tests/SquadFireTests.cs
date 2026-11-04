using Comet.Core;
using Newtonsoft.Json;
internal static class SquadFireTests
{
 public static int Run(){int n=0;void C(bool b,string s){n++;if(!b)throw new Exception("S08: "+s);}var c=new SquadFireConfig();
 C(c.Enabled&&c.Interval==.05&&c.Prediction==.3&&c.PredictionRadius==.55&&c.BodyRadius==.2&&c.ClearDelay==.1,"defaults");
 foreach(var mutate in new Action<SquadFireConfig>[] {x=>x.Interval=0,x=>x.Prediction=double.NaN,x=>x.PredictionRadius=2,x=>x.BodyRadius=-1,x=>x.ClearDelay=double.PositiveInfinity}){
  var bad=new SquadFireConfig();mutate(bad);bool threw=false;try{bad.Validate();}catch(ArgumentException){threw=true;}C(threw,"reject invalid config");}
 foreach(var json in new[]{"{}","{\"SquadFire\":null}"}){var m=JsonConvert.DeserializeObject<CometConfig>(json)!;m.Validate();C(m.SquadFire.Enabled&&m.SquadFire.ClearDelay==.1,"old config migration");}
 var a=new DoorPoint(0,1,0);var b=new DoorPoint(0,1,20);
 for(int i=0;i<100;i++){double z=1+i*.18;var x=new DoorPoint(-2,1,z);var y=new DoorPoint(2,1,z);
  C(SquadFireGeometry.Crosses(a,b,x,y,.55),"crossing with endpoints outside");C(SquadFireGeometry.Crosses(a,b,y,x,.55),"reverse direction");}
 C(!SquadFireGeometry.Crosses(a,b,new(0,4,5),new(0,4,6),.55),"floor separation");
 C(!SquadFireGeometry.Crosses(a,b,new(0,1,-2),new(0,1,-1),.55),"behind muzzle");
 C(!SquadFireGeometry.Crosses(a,b,new(0,1,21),new(0,1,22),.55),"beyond endpoint");
 C(!SquadFireGeometry.Crosses(a,a,new(0,1,1),new(0,1,2),.55),"invalid ray");
 C(!SquadFireGeometry.Crosses(a,b,new(double.NaN,0,0),a,.55),"invalid point");
 C(SquadFireGeometry.CanReach(new(){Reason="geometry-solid-obstacle",Surfaces=new[]{new ShotSurface{Known=true,Chance=.2}}}),"low power estimate is not certain safety");
 C(!SquadFireGeometry.CanReach(new(){Reason="geometry-solid-obstacle",Surfaces=new[]{new ShotSurface{Known=true,Chance=0}}}),"impenetrable known solid");
 foreach(string reason in new[]{"geometry-door-or-glass","geometry-budget","geometry-ammo-unknown","geometry-muzzle-inside","geometry-low-penetration-chance"})C(SquadFireGeometry.CanReach(new(){Reason=reason}),"uncertain/pass through path protects squad");
 SquadFireFrame F(double at=1,bool risk=true)=>new(){Now=at,Fresh=true,Risk=risk,Ally="mate",AllyName="Mate",Kind="body",Action="StandAndShoot",Muzzle=a,End=b,Position=new(0,1,5),Predicted=new(0,1,5)};
 {var p=new SquadFirePolicy();C(p.Tick(F(),c),"body risk");C(p.Trace!.ValidFor==0,"request alone not map confirmation");p.Applied(true,false,false,c);C(p.Trace.Applied&&!p.Trace.Stopped&&p.Trace.ValidFor==0,"unobserved stop");p.Applied(true,true,false,c);C(p.Trace.Stopped&&Math.Abs(p.Trace.ValidFor-.15)<1e-9,"observed stop");
  C(p.Tick(F(1.05,false),c),"clear debounce");C(p.Tick(F(1.1),c),"reenter resets debounce");C(p.Tick(F(1.15,false),c),"clear restarts");C(p.Tick(F(1.2,false),c),"not yet clear long enough");C(!p.Tick(F(1.25,false),c)&&p.Trace.State=="ended"&&!p.Trace.Applied,"release only own veto");}
 {var p=new SquadFirePolicy();p.Tick(F(),c);p.Applied(false,false,true,c);C(p.Trace!.StartPrevented&&p.Trace.ValidFor>0,"prevented trigger confirmed");for(int i=1;i<200;i++)C(p.Tick(F(1+i*.05),c),"never force fire through occupied lane");}
 {var p=new SquadFirePolicy();p.Tick(F(),c);p.Tick(F(1.05,false),c);C(p.Tick(F(2,false),c),"missing checks cannot count as continuously clear");C(!p.Tick(F(2.1,false),c),"new continuous delay");}
 {var p=new SquadFirePolicy();p.Tick(F(),c);var f=F(1.1);f.Fresh=false;C(p.Tick(f,c),"brief unavailable check retains veto");f.Now=1.3;C(!p.Tick(f,c),"unknown expires back to native");}
 foreach(double clock in new[]{double.NaN,-1,.5}){var p=new SquadFirePolicy();p.Tick(F(),c);C(!p.Tick(F(clock),c)&&!p.Blocking,"bad clock releases");}
 {var p=new SquadFirePolicy();p.Tick(F(),c);C(!p.Tick(F(1.1),new(){Enabled=false}),"disable releases");}
 Console.WriteLine($"{n} S08 policy/geometry checks passed");return n;
 }
}
