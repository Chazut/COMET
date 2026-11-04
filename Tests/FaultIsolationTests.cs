using Comet.Core;
using Comet.Server;
using Newtonsoft.Json;
internal static class FaultIsolationTests
{
 internal static int Run()
 {
  int n=0;void Check(bool ok,string why){n++;if(!ok)throw new Exception("C08 "+why);}
  foreach(var json in new[]{"{}","{\"faultIsolation\":null}"}){var c=JsonConvert.DeserializeObject<CometConfig>(json)!;c.Validate();Check(c.FaultIsolation.RetrySeconds==5&&c.FaultIsolation.ErrorLimit==3&&c.FaultIsolation.WindowSeconds==60,"migration defaults");}
  string file=Path.Combine(Path.GetTempPath(),"comet-c08-"+Guid.NewGuid().ToString("N"));
  try {var store=new ConfigStore(file);var c=store.Read();c.FaultIsolation.RetrySeconds=8;c.FaultIsolation.ErrorLimit=4;c.FaultIsolation.WindowSeconds=90;store.Save(c,c.Revision);c=new ConfigStore(file).Read();Check(c.FaultIsolation.RetrySeconds==8&&c.FaultIsolation.ErrorLimit==4&&c.FaultIsolation.WindowSeconds==90,"server round trip");}
  finally{File.Delete(file);File.Delete(file+".bak");}
  var policy=new FaultIsolation();object a=new(),b=new();var cfg=new FaultIsolationConfig();var ex=new Exception("fail");
  for(int i=0;i<3;i++){var t=policy.Fail(a,"M08","tick",ex,i*5,cfg);Check(t.Count==i+1&&t.Total==i+1,"count");}
  Check(!policy.Ready(a,"M08",1000,out _)&&policy.Ready(a,"M09",1000,out _)&&policy.Ready(b,"M08",1000,out _),"module and bot isolation");
  policy.DisableBot(a);Check(!policy.Ready(a,"M09",1000,out _),"cleanup failure bot scope");policy.Clear();Check(policy.Ready(a,"M08",1000,out _),"raid reset");
  // No cap on raid-long modules or bots retained after Clear.
  for(int i=0;i<100;i++)policy.Fail(new object(),"M", "tick", ex,0,cfg);policy.Clear();Check(!policy.HasFaults,"raid releases fault records");
  Console.WriteLine($"C08 config/policy: {n} checks passed");return n;
 }
}
