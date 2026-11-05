using Comet.Client;
using Comet.Core;
using Newtonsoft.Json.Linq;
using Clock=UnityEngine.Time;

int checks=0;
void Check(bool ok,string why){checks++;if(!ok)throw new Exception("Performance runtime: "+why);}
string dir=Path.Combine(Path.GetTempPath(),"comet-perf-runtime-"+Guid.NewGuid().ToString("N"));
var warnings=new List<string>();
try
{
    Check(PerformanceCatalog.MethodCount==2,"generator excludes async and iterator lifetimes");
    PerformanceCapture.Start(new PerformanceConfig(),dir,"off",1,"test",true,warnings.Add);
    using(PerformanceCapture.Enter(0)){}
    long EmptyScopes(){long start=GC.GetAllocatedBytesForCurrentThread();for(int i=0;i<10000;i++){using var scope=PerformanceCapture.Enter(0);}return GC.GetAllocatedBytesForCurrentThread()-start;}
    EmptyScopes();int reads=Clock.Reads;long allocated=EmptyScopes();
    Check(allocated==0,"disabled scopes allocate nothing after runtime warmup: "+allocated);
    Check(Clock.Reads==reads&&!Directory.Exists(dir),"disabled scopes never access Unity or create a file");

    Clock.Frame=100;Clock.Now=10;
    var sink=PerformanceCapture.Start(new PerformanceConfig{Enabled=true},dir,"first",7,"map",true,warnings.Add);
    Check(sink!=null,"capture starts");
    Fixture.EmitRoot(false);
    bool thrown=false;try{Fixture.EmitRoot(true);}catch(InvalidOperationException){thrown=true;}Check(thrown,"instrumentation preserves original exception");
    reads=Clock.Reads;
    var worker=new Thread(()=>{using var scope=PerformanceCapture.Enter(0);});worker.Start();worker.Join();
    Check(Clock.Reads==reads,"worker callback never touches Unity main-thread clocks");
    Clock.Frame++;Clock.Now+=.120;Clock.Delta=.120f;PerformanceCapture.Tick();
    var outer=PerformanceCapture.Enter(0);var drain=new TaskCompletionSource();var done=PerformanceCapture.Stop(drain.Task);
    Check(!done.IsCompleted,"raid end with active outer scope does not block or finalize early");
    outer.Dispose();drain.SetResult();done.GetAwaiter().GetResult();
    var lines=File.ReadAllLines(Directory.GetFiles(dir).Single()).Select(JObject.Parse).ToArray();
    Check((string?)lines[0]["session"]=="first"&&(int?)lines[0]["configRevision"]==7,"metadata identifies exact raid and config");
    var report=lines.Single(x=>(string?)x["stage"]=="performance-summary");
    Check((long?)report["modules"]?[0]?["calls"]==3,"same-module children reuse parent timing, including exceptions");
    Check((long?)report["coalescedScopes"]==2,"reused nested scopes explicitly counted");
    Check((int?)report["hitches"]?[0]?["frame"]==100,"real runtime aligns hitch to previous frame");
    Check((bool?)lines.Last()["complete"]==true&&warnings.Count==0,"teardown emits complete clean footer");
    reads=Clock.Reads;using(PerformanceCapture.Enter(0)){}Check(Clock.Reads==reads,"no stale active session after raid end");

    Clock.Frame+=10;Clock.Now+=1;
    PerformanceCapture.Start(new PerformanceConfig{Enabled=true,MeasureAllocations=false},dir,"second",8,"other-map",true,warnings.Add);
    Fixture.EmitRoot(false);PerformanceCapture.Stop(null).GetAwaiter().GetResult();
    var second=File.ReadAllLines(Directory.GetFiles(dir).Single(x=>x.Contains("second"))).Select(JObject.Parse).ToArray();
    Check((long?)second.Single(x=>(string?)x["stage"]=="performance-summary")["modules"]?[0]?["calls"]==1,"next raid has independent totals");
    Check((bool?)second[0]["allocationsAvailable"]==false,"allocation switch honored at runtime");

    Clock.Frame++;PerformanceCapture.Start(new PerformanceConfig{Enabled=true},dir,"overhead",8,"map",true,warnings.Add);
    EmptyScopes();var timer=System.Diagnostics.Stopwatch.StartNew();long measuredBytes=EmptyScopes();timer.Stop();
    Check(measuredBytes==0,"enabled hot scopes allocate nothing after warmup: "+measuredBytes);
    Console.WriteLine($"10,000 enabled empty scopes: {timer.Elapsed.TotalMilliseconds:0.###} ms, {measuredBytes} bytes (desktop harness, not Unity performance).");
    PerformanceCapture.Stop(null).GetAwaiter().GetResult();

    Clock.Frame++;PerformanceCapture.Start(new PerformanceConfig{Enabled=true},dir,"broken",9,"map",true,warnings.Add);
    var one=PerformanceCapture.Enter(0);var two=PerformanceCapture.Enter(1);one.Dispose();two.Dispose();
    Check(warnings.Count==1,"malformed scopes disable only profiler and warn once");
    // No exception escapes into gameplay; capture can start again after failure.
    Clock.Frame++;PerformanceCapture.Start(new PerformanceConfig{Enabled=true},dir,"recovered",10,"map",true,warnings.Add);
    Fixture.EmitRoot(false);PerformanceCapture.Stop(null).GetAwaiter().GetResult();
    Check(File.Exists(Directory.GetFiles(dir).Single(x=>x.Contains("recovered"))),"later raid can record after measurement failure");
    Console.WriteLine($"Performance runtime: {checks} checks passed.");
}
finally { PerformanceCapture.Stop(null).GetAwaiter().GetResult(); /* Artifacts retained for inspection of failure handling. */ }

namespace Comet.Client { public sealed class Plugin {} }
namespace UnityEngine
{
    internal static class Time
    {
        internal static int Frame,Reads;internal static double Now;internal static float Delta=.016f;
        public static int frameCount {get{Reads++;return Frame;}}
        public static double realtimeSinceStartupAsDouble => Now;
        public static float unscaledDeltaTime => Delta;
    }
}
