using Comet.Core;
using Newtonsoft.Json;
internal static class SurgeryInterruptionTests
{
 public static int Run()
 {
  int n=0;void C(bool ok,string why){n++;if(!ok)throw new Exception("H02: "+why);}
  var c=new SurgeryInterruptionConfig();c.Validate();C(c.Enabled&&c.Interval==.1&&c.VisibleDistance==40&&c.KnownDistance==20&&c.KnownAge==3&&c.FootstepDistance==15&&c.FootstepAge==2&&c.RetryDelay==8,"defaults");
  foreach(var prop in typeof(SurgeryInterruptionConfig).GetProperties().Where(p=>p.PropertyType==typeof(double)))
   foreach(var bad in new[]{double.NaN,double.PositiveInfinity,double.NegativeInfinity,-1d,100001d}){var v=new SurgeryInterruptionConfig();prop.SetValue(v,bad);try{v.Validate();C(false,"invalid "+prop.Name);}catch(ArgumentException){n++;}}
  var config=JsonConvert.DeserializeObject<CometConfig>("{}")!;config.Validate();C(config.SurgeryInterruption.Enabled,"old config defaults");
  config.SurgeryInterruption=new(){Enabled=false,Interval=.2,VisibleDistance=45,KnownDistance=25,KnownAge=4,FootstepDistance=18,FootstepAge=3,RetryDelay=12};
  string json=JsonConvert.SerializeObject(config);var copy=JsonConvert.DeserializeObject<CometConfig>(json)!;copy.Validate();C(JsonConvert.SerializeObject(copy)==json,"all config fields persist");
  config.SurgeryInterruption=null!;try{config.Validate();C(false,"null config");}catch(ArgumentException){n++;}
  SurgeryInterruption New(){var v=new SurgeryInterruption();v.Begin(10,"kit","LeftLeg");return v;}
  SurgeryFrame Frame()=>new(){Now=11,Hands=true,OperationPending=true};
  foreach(var (kind,distance,age,expected) in new[]{("visible",40d,0d,true),("visible",40.01,0d,false),("known",20d,2.99,true),("known",20d,3d,false),("known",20.01,0d,false),("footstep",15d,1.99,true),("footstep",15d,2d,false),("footstep",15.01,0d,false),("known",1d,-1d,false),("known",double.NaN,0d,false)}){
   var p=New();var f=Frame();f.Evidence.Add(new(){Kind=kind,Distance=distance,Age=age});C(p.Tick(f,c)==expected,$"{kind} {distance}/{age}");C(p.Trace!.Requested==expected,"request truth");
  }
  foreach(var danger in new[]{"hit","grenade","native"}){
   var p=New();var f=Frame();f.Hit=danger=="hit";f.Grenade=danger=="grenade";f.NativeCancelled=danger=="native";
   C(p.Tick(f,c)==(danger!="native"),"native cancellation not repeated");C(p.Cancelling&&p.RetryAt==19,"8 second retry");
   for(int i=0;i<20;i++)C(!p.Tick(f,c),"one cancel per operation");
   f.Now=12;f.Hands=false;f.OperationPending=false;p.Tick(f,c);C(p.Trace!.HandsReleased&&!p.Trace.WeaponReady&&p.Active,"release is not weapon ready");
   f.Now=13;f.WeaponReady=true;p.Tick(f,c);C(!p.Active&&p.Trace.WeaponReady&&!p.Trace.Applied&&p.Trace.ValidFor==0,"weapon observed");C(p.RetryAt==19,"observing cannot extend retry");
  }
  {var p=New();var f=Frame();f.Completed=true;f.Grenade=true;C(!p.Tick(f,c)&&!p.Active&&!p.Trace!.Requested,"same frame completion wins");}
  {var p=New();var f=Frame();f.Grenade=true;p.Tick(f,c);f.Completed=true;p.Tick(f,c);C(p.Trace!.Completed&&p.Cancelling,"completed during cancellation still waits for hands");f.Now=42;p.Tick(f,c);C(!p.Active&&p.Trace.Reason=="surgery-native-return-timeout","bounded recovery observation");}
  {var p=New();var f=Frame();f.Evidence.Add(new(){Kind="known",Age=30,Distance=10});f.Evidence.Add(new(){Kind="known",Age=0,Distance=80});C(!p.Tick(f,c),"near stale and far fresh are not one threat");C(p.Trace!.Evidence?.Age==30&&p.Trace.Evidence.Distance==10,"rejected evidence explains why surgery continued");p.End("scope");C(!p.Active&&!p.Trace!.Applied,"scope releases trace");}
  return n;
 }
}
