using Comet.Core;
using Newtonsoft.Json;
internal static class FireLaneTests
{
 public static int Run(){int n=0;void C(bool b,string s){n++;if(!b)throw new Exception("S07: "+s);}var c=new FireLaneConfig();
 C(c.Enabled&&c.Width==.9&&c.Prediction==.3&&c.Refresh==.1&&c.ShotGrace==.2&&c.MaxHold==.8&&c.SideDistance==1.2&&c.Retry==1,"defaults");
 foreach(var mutate in new Action<FireLaneConfig>[] {x=>x.Width=double.NaN,x=>x.Prediction=-1,x=>x.Refresh=0,x=>x.ShotGrace=1,x=>x.MaxHold=20,x=>x.SideDistance=double.PositiveInfinity,x=>x.Retry=0}){
  var bad=new FireLaneConfig();mutate(bad);bool threw=false;try{bad.Validate();}catch(ArgumentException){threw=true;}C(threw,"invalid config rejected");}
 var migrated=JsonConvert.DeserializeObject<CometConfig>("{}")!;migrated.Validate();C(migrated.FireLane.Enabled,"old settings migration");
 var ray=new FireLaneRay{Shooter="a",Name="A",From=new(0,1.2,0),To=new(0,1.2,20),Age=.1};
 C(FireLaneGeometry.Blocks(new(-2,0,10),new(2,0,10),ray,.9),"swept crossing, neither endpoint inside");
 C(!FireLaneGeometry.Blocks(new(-2,4,10),new(2,4,10),ray,.9),"other floor");
 C(!FireLaneGeometry.Blocks(new(0,0,-2),new(0,0,-1),ray,.9),"behind muzzle");
 C(!FireLaneGeometry.Blocks(new(0,0,21),new(0,0,22),ray,.9),"past shot endpoint");
 C(FireLaneGeometry.SafeExit(new(.1,0,10),new(1.3,0,10),new[]{ray},.9),"exit on same side");
 C(!FireLaneGeometry.SafeExit(new(.1,0,10),new(-1.2,0,10),new[]{ray},.9),"do not step through firing axis");
 var other=new FireLaneRay{Shooter="b",From=new(1.2,1.2,0),To=new(1.2,1.2,20)};
 C(!FireLaneGeometry.SafeExit(new(.1,0,10),new(1.3,0,10),new[]{ray,other},.9),"do not exit into other lane");
 for(int i=0;i<100;i++){
  double z=1+i*.15;var a=new DoorPoint(-2,0,z);var b=new DoorPoint(2,0,z);
  C(FireLaneGeometry.Blocks(a,b,ray,.9),"crossing "+i);C(Math.Abs(FireLaneGeometry.SegmentDistance(a,b,ray.From,ray.To)-FireLaneGeometry.SegmentDistance(b,a,ray.From,ray.To))<1e-8,"sweep order invariant");
 }
 FireLaneFrame F(double at=1)=>new(){Now=at,Speed=1,Context="route",Action="Search",Position=new(1,0,10),Desired=new(0,0,10),Threat=ray,GeometryFresh=true,Left=new(-1.2,0,10),Right=new(1.2,0,10)};
 {var p=new FireLanePolicy();C(p.Tick(F(),c)&&p.Trace!.State=="holding","wait before crossing");p.Applied(false,c);C(p.Trace!.ValidFor==0,"requested not observed");p.Applied(true,c);C(p.Trace.ValidFor<=.25&&p.Trace.Confirmed,"short observed lease");
  C(!p.Tick(F(1.81),c)&&p.Trace.Reason=="lane-no-safe-detour-return-native","bounded wait returns native");C(!p.Tick(F(2),c),"cooldown");C(p.Tick(F(2.82),c),"later retry");}
 {var p=new FireLanePolicy();var f=F();f.Inside=true;f.RightSafe=true;C(p.Tick(f,c)&&p.Trace!.State=="stepping"&&p.Trace.Side=="right","immediate safe exit");
  f=F(1.1);f.RightSafe=true;C(p.Tick(f,c)&&p.Trace!.Wait==0,"side step time is not counted as waiting");
  f=F(1.2);f.RightSafe=false;f.LeftSafe=true;C(!p.Tick(f,c),"do not oscillate to other side");}
 {var p=new FireLanePolicy();C(p.Tick(F(),c),"start");var f=F(1.81);f.RightSafe=true;C(p.Tick(f,c)&&p.Trace!.State=="stepping","detour after maximum wait");}
 foreach(string why in new[]{"visible combat","grenade escape","other prefix","no geometry","clear","stale","disabled","clock"}){
  var p=new FireLanePolicy();p.Tick(F(),c);var f=F(1.1);var config=new FireLaneConfig();
  if(why=="no geometry")f.GeometryFresh=false;else if(why=="clear")f.Threat=null;else if(why=="stale")f.Threat=new(){Shooter="a",Age=1};else if(why=="disabled")config.Enabled=false;else if(why=="clock")f.Now=.5;else f.Block=why;
  C(!p.Tick(f,config),"release "+why);C(p.Trace!.ValidFor==0&&!p.Trace.Applied,"clear ownership "+why);
 }
 Console.WriteLine($"{n} S07 policy/geometry checks passed");return n;
 }
}
