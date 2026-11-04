using Comet.Core;
using Newtonsoft.Json;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

internal static class TacticalDevicesTests
{
 public static int Run()
 {
  int n=0;void C(bool ok,string why){n++;if(!ok)throw new Exception("L02: "+why);}
  var c=new TacticalDevicesConfig();C(c.Enabled&&c.CommandInterval==.25&&c.ConfirmationTimeout==.8&&c.AttemptsBeforePause==2&&c.InitialPause==5&&c.MaximumPause==60,"defaults");
  foreach(var json in new[]{"{}","{\"TacticalDevices\":null}"}){var old=JsonConvert.DeserializeObject<CometConfig>(json)!;old.Validate();C(old.TacticalDevices.Enabled,"migration");}
  foreach(var prop in typeof(TacticalDevicesConfig).GetProperties().Where(x=>x.PropertyType==typeof(double)))
  foreach(var bad in new[]{double.NaN,double.PositiveInfinity,-1d,10000d}){var invalid=new TacticalDevicesConfig();prop.SetValue(invalid,bad);bool thrown=false;try{invalid.Validate();}catch(ArgumentException){thrown=true;}C(thrown,"invalid "+prop.Name);}
  foreach(int bad in new[]{0,6,int.MaxValue}){c=new(){AttemptsBeforePause=bad};bool thrown=false;try{c.Validate();}catch(ArgumentException){thrown=true;}C(thrown,"attempt limit");}
  c=new(){InitialPause=30,MaximumPause=10};bool mismatch=false;try{c.Validate();}catch(ArgumentException){mismatch=true;}C(mismatch,"backoff ordering");c=new();
  var a=new TacticalDeviceAttempt();a.SetTarget(true,3);C(a.Observe(0,false,3,c),"initial mismatch eligible");a.Request(0,c);C(!a.Observe(.79,false,3,c),"confirmation grace");C(a.Observe(.8,false,3,c)&&a.Failures==1,"first failure exact boundary");
  a.Request(.8,c);C(!a.Observe(1.6,false,3,c)&&a.RetryAt==6.6&&a.Rounds==1,"two failures pause");C(!a.Observe(6.59,false,3,c),"pause boundary");C(a.Observe(6.6,false,3,c),"retry at boundary");
  double now=6.6;
  foreach(double pause in new[]{10d,20,40,60,60,60})
  {a.Request(now,c);now+=.81;C(a.Observe(now,false,3,c),"next round first failure");a.Request(now,c);now+=.81;C(!a.Observe(now,false,3,c)&&Math.Abs(a.RetryAt-now-pause)<1e-6,"bounded exponential pause");now=a.RetryAt;}
  C(!a.Observe(now,true,3,c)&&a.Confirmed&&a.Rounds==0,"recovery resets");C(!a.Observe(now+1,false,3,c)&&a.Yielded,"external activation wins");
  a.SetTarget(false,3);C(!a.Observe(now+2,false,3,c)&&a.Confirmed&&!a.Yielded,"new desired state");
  C(!a.Observe(now+3,false,4,c)&&a.Yielded,"external mode wins");
  a=new();a.SetTarget(true,2);a.Request(0,c);a.Suspend();C(a.Observe(10,false,2,c)&&a.Failures==0&&a.Rounds==0,"busy pause consumes no failure");
  a.Request(10,c);a.Observe(10.1,true,2,c);a.SetTarget(false,2);C(!a.Observe(10.2,true,2,c),"rapid opposing desired state respects interval");C(a.Observe(10.25,true,2,c),"new state after interval");
  var p=new TacticalDeviceAttempt();p.SetTarget(true,2);p.Request(10,c);C(!p.Observe(10.2,true,2,c)&&p.Confirmed,"early confirmation");
  using(var pe=new PEReader(File.OpenRead("references/sain-4.5.1/SAIN.dll")))
  {var md=pe.GetMetadataReader();var t=md.TypeDefinitions.Select(md.GetTypeDefinition).Single(t=>md.GetString(t.Name)=="BotLightController");C(t.GetFields().Select(md.GetFieldDefinition).Any(f=>md.GetString(f.Name)=="wantLightOn"),"read-only native preference contract");}
  var folder=Path.Combine(Path.GetTempPath(),"comet-l02-config-"+Guid.NewGuid().ToString("N"));
  try{var store=new Comet.Server.ConfigStore(Path.Combine(folder,"config.json"));var d=store.Read();d.TacticalDevices.Enabled=false;d.TacticalDevices.InitialPause=8;var saved=store.Save(d,d.Revision);
   C(saved.Revision==2&&!store.Read().TacticalDevices.Enabled,"persisted toggle");var game=JsonConvert.DeserializeObject<CometConfig>(store.ForGame())!;C(game.TacticalDevices.InitialPause==8,"game payload");
   d=store.Read();d.TacticalDevices.MaximumPause=2;bool rejected=false;try{store.Save(d,d.Revision);}catch(ArgumentException){rejected=true;}C(rejected&&store.Read().TacticalDevices.MaximumPause==60,"invalid save preserved prior config");
  }finally{if(Directory.Exists(folder))Directory.Delete(folder,true);}
  Console.WriteLine($"{n} L02 policy/config/native-contract checks passed");return n;
 }
}
