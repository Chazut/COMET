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
    var probe=new FrameProbeGate();
    Check(probe.TryEnter(10,"enemy",1,2,3),"first probe in a frame advances");
    for(int i=0;i<100;i++)Check(!probe.TryEnter(10,"enemy",1,2,3)&&probe.Same(10,"enemy",1,2,3),"duplicate callers share geometry but retain their live safety checks");
    Check(!probe.TryEnter(10,"other",1,2,3)&&!probe.Same(10,"other",1,2,3),"changed target cannot borrow cached readiness");
    Check(!probe.Same(10,"enemy",2,2,3),"moved target cannot borrow cached readiness");
    Check(probe.TryEnter(11,"other",1,2,3),"next frame continues incremental search");
    long tick=0;var received=new List<int>();
    var delivery=new FrameDispatchQueue<int>(x=>{received.Add(x);tick+=10;},()=>tick,30,4096);
    for(int i=0;i<207;i++)delivery.Enqueue(i,0);
    Check(received.Count==3&&delivery.Pending==204,"burst yields after budget, preserves pending events");
    for(int frame=1;delivery.Pending>0;frame++)delivery.Drain(frame);
    Check(received.SequenceEqual(Enumerable.Range(0,207)),"deferred delivery preserves order without loss");
    received.Clear();delivery=new FrameDispatchQueue<int>(x=>{received.Add(x);tick+=100;},()=>tick,30,2);
    for(int i=0;i<10;i++)delivery.Enqueue(i,1);
    delivery.Drain(1,true);
    Check(received.SequenceEqual(Enumerable.Range(0,10))&&delivery.Pending==0,"capacity backpressure and shutdown flush lose no messages");
    Check(delivery.ForcedDrains>0&&delivery.OverBudgetCallbacks==10,"slow subscriber and emergency drain are measurable");
    var deliveredJson=new List<string>();Action<string> capture=deliveredJson.Add;
    Comet.Api.Decisions.Published+=capture;
    for(int i=0;i<207;i++)Comet.Api.Decisions.Publish(i.ToString(),_=>{});
    Comet.Api.Decisions.FlushPending();
    Check(deliveredJson.SequenceEqual(Enumerable.Range(0,207).Select(x=>x.ToString())),"public flush delivers raid tail before RR closes");
    Comet.Api.Decisions.Published-=capture;Comet.Api.Decisions.Clear();
    Check(PerformanceCatalog.MethodCount==2,"generator excludes async/iterator lifetimes and hot control guards");
    Check(ControlIntegration.Sleeping(true)&&!ControlIntegration.Sleeping(false)&&ControlIntegration.WatchControl(true)&&!ControlIntegration.WatchControl(false)&&ControlIntegration.Checks==4,"uninstrumented control guards still execute every check");
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
    Check(report["modules"]!.Count()==2&&(string?)report["modules"]?[1]?["module"]=="Diagnostics.BuildEvent.EmitChild","event builders have separate attribution");
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

    Clock.Frame++;PerformanceCapture.Start(new PerformanceConfig{Enabled=true},dir,"physics",8,"map",true,warnings.Add);
    Fixture.EmitRoot(false);Clock.Frame++;Clock.inFixedTimeStep=true;Clock.Delta=.2f;PerformanceCapture.Tick();Clock.inFixedTimeStep=false;
    PerformanceCapture.Stop(null).GetAwaiter().GetResult();
    var physics=File.ReadAllLines(Directory.GetFiles(dir).Single(x=>x.Contains("physics"))).Select(JObject.Parse).Single(x=>(string?)x["stage"]=="performance-summary");
    Check((long?)physics["unknownDurationFrames"]==2&&(double?)physics["maxFrameMs"]==0,"physics time step never treated as rendered frame duration");

    Clock.Frame++;Clock.Now+=1;Clock.Delta=.016f;
    PerformanceCapture.Start(new PerformanceConfig{Enabled=true,ReportIntervalSeconds=1},dir,"fixed-first",8,"map",true,warnings.Add);
    int completedFrame=Clock.Frame;Fixture.EmitRoot(false);
    Clock.Frame++;Clock.Now+=1.054;Clock.inFixedTimeStep=true;Clock.Delta=.02f;
    PerformanceCapture.Tick();Fixture.EmitRoot(false);
    Clock.inFixedTimeStep=false;Clock.Delta=1.054f;PerformanceCapture.Tick();PerformanceCapture.Tick();
    PerformanceCapture.Stop(null).GetAwaiter().GetResult();
    var fixedFirst=File.ReadAllLines(Directory.GetFiles(dir).Single(x=>x.Contains("fixed-first"))).Select(JObject.Parse).ToArray();
    var fixedSummary=fixedFirst.Single(x=>(string?)x["stage"]=="performance-summary");
    var fixedWindow=fixedFirst.Single(x=>(string?)x["stage"]=="performance-window");
    Check((long?)fixedSummary["frames"]==2&&(long?)fixedSummary["unknownDurationFrames"]==1,"Update completes duration missing at first FixedUpdate without double counting frames");
    Check((double?)fixedWindow["hitches"]?[0]?["frameMs"]>1053&&(int?)fixedWindow["hitches"]?[0]?["frame"]==completedFrame,"window retains the real render hitch on its previous frame");
    Check((long?)fixedSummary["modules"]?[0]?["calls"]==2,"physics callbacks stay attributed to their own frame");

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
        public static bool inFixedTimeStep;
        public static int frameCount {get{Reads++;return Frame;}}
        public static double realtimeSinceStartupAsDouble => Now;
        public static float unscaledDeltaTime => Delta;
    }
}
