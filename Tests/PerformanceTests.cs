using Comet.Core;
using Comet.Client;
using Comet.Server.Editing;
using Newtonsoft.Json.Linq;

internal static class PerformanceTests
{
    private sealed class Gate
    {
        internal readonly ManualResetEventSlim Entered=new(),Release=new();
        public string Value {get{Entered.Set();if(!Release.Wait(10000))throw new TimeoutException();return "ready";}}
    }
    public static async Task<int> Run()
    {
        int n=0;void Check(bool v,string why){n++;if(!v)throw new Exception("Performance: "+why);}
        Check(!PerformanceAllocation.Probe(()=>0),"Mono constant allocation stub is unavailable, not zero allocations");
        Check(!PerformanceAllocation.Probe(()=>throw new NotSupportedException()),"unsupported allocation API safely disabled");
        Check(PerformanceAllocation.Probe(GC.GetAllocatedBytesForCurrentThread),"working allocation API detected");
        void Near(double actual,double expected,string why)=>Check(Math.Abs(actual-expected)<.00001,why+": "+actual);
        var c=new CometConfig();Check(!c.Performance.Enabled&&c.Performance.MeasureAllocations,"capture opt-in, allocations available by default");
        var old=Newtonsoft.Json.JsonConvert.DeserializeObject<CometConfig>("{\"Performance\":null}")!;old.Validate();Check(!old.Performance.Enabled,"old/null configs migrate OFF");
        foreach(double bad in new[]{double.NaN,double.PositiveInfinity,0,1001})
        { bool rejected=false;try{new PerformanceConfig{SlowFrameMs=bad}.Validate();}catch(ArgumentException){rejected=true;}Check(rejected,"invalid threshold rejected"); }
        var ui=new ConfigEditor(c);Check(ui.Set(ui.Catalog.Fields["Performance.Enabled"],"true"),"server UI can enable");
        Check(ui.Set(ui.Catalog.Fields["Performance.SlowFrameMs"],"80")&&ui.Snapshot().Performance.SlowFrameMs==80,"server UI applies threshold");
        Check(ui.Catalog.Sections["Performance"].Category=="Diagnostics","discoverable diagnostic section");

        var r=new PerformanceRecorder(new[]{"Combat","Diagnostics","RR"},1000,true,50,5);
        r.Advance(100,10,16,0,0,0);
        long a=r.Enter(0,0,100),b=r.Enter(1,10,200),d=r.Enter(2,20,250);
        r.Leave(d,25,300);r.Leave(b,40,400);r.Leave(a,100,600);
        var w=r.Advance(101,15,120,1,0,0)!;
        Check(w.FirstFrame==100&&w.LastFrame==100&&w.Hitches[0].Frame==100,"delta at N+1 assigned to N");
        Near(w.CometMs,100,"nested time counted once");
        Near(w.Modules.Single(m=>m.Module=="Combat").SelfMs,70,"combat self excludes telemetry");
        Near(w.Modules.Single(m=>m.Module=="Diagnostics").SelfMs,25,"telemetry excludes RR");
        Near(w.Modules.Sum(m=>m.SelfMs),100,"self costs sum to total");
        Check(w.Modules.Sum(m=>m.SelfBytes)==500,"nested allocations counted once");
        Check(w.Hitches[0].Collections.SequenceEqual(new[]{1,0,0})&&w.Hitches[0].Partial,"GC correlation and partial first frame");
        Near(w.Modules[0].MaxCallMs,100,"slow native-inclusive call retained");
        a=r.Enter(0,110,650);b=r.Enter(0,115,680);r.Leave(b,125,700);r.Leave(a,140,750);
        r.Advance(102,15.016,16,1,0,0);
        var end=r.Finish(1,0,0);
        Near(end.CometMs,130,"recursive module self has no double-count");
        Check(end.Modules[0].Calls==3&&end.Frames==3&&end.UnknownDurationFrames==1,"final totals include all windows and incomplete final frame");
        Near(end.Modules[0].MaxFrameSelfMs,70,"max self frame is not cumulative total");
        Check(r.Enter(0,200,0)==0,"completed capture ignores new work");
        Check(end.DurationMismatchFrames==1,"scope versus Unity duration mismatch explicitly counted");

        r=new PerformanceRecorder(new[]{"Combat"},1000,false,50,60);
        r.Advance(1,0,16,0,0,0);a=r.Enter(0,0,0);r.Leave(a,90,0);
        r.Advance(2,.09,double.NaN,0,0,0);end=r.Finish(0,0,0);
        Check(end.Hitches.Length==1&&end.Hitches[0].FrameMs==null&&end.Hitches[0].CometMs==90,"slow scope retained when frame clock unavailable");
        Check(end.UnknownDurationFrames==2&&end.DurationMismatchFrames==0,"unknown duration is never presented as valid frame timing");
        r=new PerformanceRecorder(new[]{"Combat"},1000,false,50,60);
        r.Advance(1,0,16,0,0,0);a=r.Enter(0,0,0);r.Leave(a,90,0);
        r.Advance(2,.09,20,0,0,0);end=r.Finish(0,0,0);
        Check(end.Hitches.Single().DurationMismatch&&end.DurationMismatchFrames==1,"inconsistent hitch clocks remain visible with flag");

        r=new PerformanceRecorder(new[]{"Combat"},1000,false,50,60);
        r.Advance(1,0,10,0,0,0);a=r.Enter(0,0,0);r.Leave(a,2,200);
        r.Advance(4,1,200,0,0,0);end=r.Finish(0,0,0);
        Check(end.UnobservedFrames==2&&end.UnknownDurationFrames==2&&end.SlowFrames==0,"missing frames are not falsely attributed");
        Check(end.Modules[0].SelfBytes==null,"unavailable allocations are null, not zero");
        r=new PerformanceRecorder(new[]{"Combat"},1000,true,50,60);r.Advance(1,0,0,0,0,0);
        for(int i=1;i<=100;i++){a=r.Enter(0,i*10,0);r.Leave(a,i*10+1,5);r.Advance(i+1,i*.1,50+i,0,0,0);}
        end=r.Finish(0,0,0);
        Check(end.SlowFrames==100&&end.Hitches.Length==64&&end.OmittedSlowFrames==36,"hitch details bounded without losing totals");
        Check(end.Hitches.Min(h=>h.FrameMs)==87&&end.Hitches.Max(h=>h.FrameMs)==150,"keeps worst hitches, not just first arrivals");
        r=new PerformanceRecorder(new[]{"Combat"},1000,true,50,10);r.Advance(1,0,0,0,0,0);
        var tokens=Enumerable.Range(0,257).Select(i=>r.Enter(0,i,0)).ToArray();
        Check(tokens.Last()==0,"scope depth bounded");
        foreach(var t in tokens.Reverse())r.Leave(t,300,0);
        end=r.Finish(0,0,0);Check(end.ScopeOverflows==1&&r.Depth==0,"overflow safely unwinds and is reported");
        r=new PerformanceRecorder(new[]{"Combat"},1000,true,50,60);r.Advance(1,0,16,0,0,0);
        a=r.Enter(0,0,0);r.Leave(a,1,0);r.Advance(2,.01,16,0,0,0);
        long allocatedBefore=GC.GetAllocatedBytesForCurrentThread();
        for(int i=3;i<1000;i++){a=r.Enter(0,i*10,0);r.Leave(a,i*10+1,0);r.Advance(i,i*.01,16,0,0,0);}
        long allocatedAfter=GC.GetAllocatedBytesForCurrentThread();
        Check(allocatedAfter==allocatedBefore,"ordinary frames and hot scopes allocate nothing between reports");
        r=new PerformanceRecorder(new[]{"Combat"},1000,false,50,10);a=r.Enter(0,0,0);b=r.Enter(0,1,0);
        bool invalid=false;try{r.Leave(a,2,0);}catch(InvalidOperationException){invalid=true;}Check(invalid,"misordered scopes detected, no silent wrong attribution");

        string dir=Path.Combine(Path.GetTempPath(),"comet-performance-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(dir);
        try
        {
            var warnings=new List<string>();var gate=new Gate();string path=Path.Combine(dir,"bounded.jsonl");
            var writer=new PerformanceWriter(path,gate,warnings.Add,false,maxBytes:1);
            Check(gate.Entered.Wait(10000),"background writer started");
            for(int i=0;i<12;i++)writer.Enqueue(new PerformanceReport());
            var drain=new TaskCompletionSource();writer.Background.Add(100,200);
            writer.Finish(new PerformanceReport{Stage="performance-summary",Frames=123},drain.Task);
            Check(!writer.Completion.IsCompleted,"finish never blocks on diagnostics drain");
            gate.Release.Set();drain.SetResult();await writer.Completion.WaitAsync(TimeSpan.FromSeconds(10));
            var lines=File.ReadAllLines(path).Select(JObject.Parse).ToArray();var footer=lines.Last();
            Check((bool?)footer["complete"]==true&&(long?)footer["queueDropped"]==4,"bounded nonblocking queue counts dropped windows");
            Check((long?)footer["fileLimitDropped"]==8,"independent file quota counted");
            Check((long?)lines[^2]["frames"]==123,"totals survive file limit and queue pressure");
            Check((long?)footer["backgroundDiagnostics"]?["events"]==1&&footer["backgroundDiagnostics"]?["allocatedBytes"]?.Type==JTokenType.Null,"background isolated, no fake allocation figures");
            Check(warnings.Count==1,"one summary warning, no per-event spam");
            path=Path.Combine(dir,"aborted.jsonl");writer=new PerformanceWriter(path,new{stage="performance-start"},warnings.Add,false);
            writer.Finish(new PerformanceReport{Stage="performance-aborted"},null);await writer.Completion;
            Check((bool?)JObject.Parse(File.ReadLines(path).Last())["complete"]==false,"aborted capture never claims completeness");

            // Real diagnostic queue processing is measured off-thread and drained into final footer.
            path=Path.Combine(dir,"real.jsonl");writer=new PerformanceWriter(path,new{stage="performance-start"},warnings.Add,true);
            var log=new Diagnostics(Path.Combine(dir,"decisions"),"raid",warnings.Add);
            log.PerformanceAllocation=writer.Background.Allocated;log.PerformanceSample=writer.Background.Add;
            for(int i=0;i<100;i++)log.Write("{\"feature\":\"A01\",\"stage\":\"test\"}");
            log.Dispose();writer.Finish(new PerformanceReport{Stage="performance-summary"},log.Completion);await writer.Completion;
            footer=JObject.Parse(File.ReadLines(path).Last());
            Check((long?)footer["backgroundDiagnostics"]?["events"]==100,"final report awaits all diagnostic events");
            Check((double?)footer["backgroundDiagnostics"]?["elapsedMs"]>0,"worker processing timed");
            if(PerformanceAllocation.Read!=null)Check((long?)footer["backgroundDiagnostics"]?["allocatedBytes"]>0,"worker allocations measured independently");
        }
        finally{Directory.Delete(dir,true);}
        Console.WriteLine($"Performance capture: {n} assertions passed.");return n;
    }
}
