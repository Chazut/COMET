using System;
using System.Collections.Generic;

namespace Comet.Core;

public sealed class PerformanceModule
{
    public string Module { get; set; } = "";
    public long Calls { get; set; }
    public double InclusiveMs { get; set; }
    public double SelfMs { get; set; }
    public double MaxCallMs { get; set; }
    public double MaxFrameSelfMs { get; set; }
    public long? InclusiveBytes { get; set; }
    public long? SelfBytes { get; set; }
}
public sealed class PerformanceFrame
{
    public int Frame { get; set; }
    public double Realtime { get; set; }
    public double? FrameMs { get; set; }
    public bool Partial { get; set; }
    public double CometMs { get; set; }
    public long? CometBytes { get; set; }
    public int[] Collections { get; set; } = Array.Empty<int>();
    public PerformanceModule[] TopModules { get; set; } = Array.Empty<PerformanceModule>();
}
public sealed class PerformanceReport
{
    public string Stage { get; set; } = "performance-window";
    public int FirstFrame { get; set; }
    public int LastFrame { get; set; }
    public long Frames { get; set; }
    public long UnknownDurationFrames { get; set; }
    public long UnobservedFrames { get; set; }
    public long SlowFrames { get; set; }
    public long OmittedSlowFrames { get; set; }
    public long ScopeOverflows { get; set; }
    public double CometMs { get; set; }
    public double MaxCometFrameMs { get; set; }
    public double MaxFrameMs { get; set; }
    public PerformanceModule[] Modules { get; set; } = Array.Empty<PerformanceModule>();
    public PerformanceFrame[] Hitches { get; set; } = Array.Empty<PerformanceFrame>();
}

// One main-thread recorder. No clocks, Unity objects, IO or per-call allocation.
// Inclusive time contains native callees; self time subtracts measured children.
public sealed class PerformanceRecorder
{
    private struct Scope { public int Module; public long Token, Start, Bytes, ChildTicks, ChildBytes; }
    private struct Metric
    {
        public long Calls, Ticks, SelfTicks, MaxTicks, MaxFrame, Bytes, SelfBytes;
        public void Add(Metric m) { Calls+=m.Calls;Ticks+=m.Ticks;SelfTicks+=m.SelfTicks;Bytes+=m.Bytes;SelfBytes+=m.SelfBytes;MaxTicks=Math.Max(MaxTicks,m.MaxTicks);MaxFrame=Math.Max(MaxFrame,m.SelfTicks); }
    }
    private sealed class Totals
    {
        public int First=-1, Last;
        public long Frames, Unknown, Missing, Slow, Omitted;
        public double CometMs, MaxCometMs, MaxFrameMs;
        public readonly Metric[] Modules;
        public Totals(int n) { Modules=new Metric[n]; }
    }
    private readonly string[] names;
    private readonly double toMs, slowMs, interval;
    private readonly bool allocations;
    private readonly Scope[] stack = new Scope[256];
    private readonly Metric[] frame;
    private readonly Totals raid;
    private Totals window;
    private readonly List<PerformanceFrame> hitches = new(64);
    private long serial, overflows;
    private int depth, frameId=-1, gc0, gc1, gc2;
    private double realtime, windowStart;
    private bool first=true, finished;
    public int Depth => depth;
    public PerformanceRecorder(string[] modules, long frequency, bool allocations, double slowMs, double interval)
    {
        names=(string[])modules.Clone();toMs=1000.0/frequency;this.allocations=allocations;this.slowMs=slowMs;this.interval=interval;
        frame=new Metric[names.Length];raid=new Totals(names.Length);window=new Totals(names.Length);
    }
    public long Enter(int module, long ticks, long bytes)
    {
        if(finished)return 0;
        if(depth==stack.Length){overflows++;return 0;}
        if((uint)module>=(uint)names.Length)throw new ArgumentOutOfRangeException(nameof(module));
        long token=++serial;stack[depth++]=new Scope{Module=module,Token=token,Start=ticks,Bytes=bytes};return token;
    }
    public void Leave(long token, long ticks, long bytes)
    {
        if(token==0||finished)return;
        if(depth==0||stack[depth-1].Token!=token)throw new InvalidOperationException("Performance scopes must close in order.");
        var s=stack[--depth];long elapsed=Math.Max(0,ticks-s.Start), allocated=allocations?Math.Max(0,bytes-s.Bytes):0;
        ref var m=ref frame[s.Module];m.Calls++;m.Ticks+=elapsed;m.SelfTicks+=Math.Max(0,elapsed-s.ChildTicks);
        m.MaxTicks=Math.Max(m.MaxTicks,elapsed);m.Bytes+=allocated;m.SelfBytes+=Math.Max(0,allocated-s.ChildBytes);
        if(depth>0){stack[depth-1].ChildTicks+=elapsed;stack[depth-1].ChildBytes+=allocated;}
    }
    // Unity's delta at frame N describes frame N-1. Never attach it to N.
    public PerformanceReport? Advance(int id, double now, double previousFrameMs, int g0, int g1, int g2)
    {
        if(finished||id==frameId)return null;
        if(depth!=0)throw new InvalidOperationException("Performance scope crossed a frame boundary.");
        PerformanceReport? report=null;
        if(frameId>=0)
        {
            bool consecutive=id==frameId+1;
            Close(consecutive && previousFrameMs>0 && !double.IsInfinity(previousFrameMs) && !double.IsNaN(previousFrameMs)?previousFrameMs:(double?)null,g0,g1,g2);
            int missed=Math.Max(0,id-frameId-1);raid.Missing+=missed;window.Missing+=missed;
            if(now-windowStart>=interval){report=Snapshot(window,false);window=new Totals(names.Length);hitches.Clear();windowStart=now;}
        }
        else windowStart=now;
        frameId=id;realtime=now;gc0=g0;gc1=g1;gc2=g2;return report;
    }
    private void Close(double? duration,int g0,int g1,int g2)
    {
        double cost=0;long bytes=0;
        for(int i=0;i<frame.Length;i++){cost+=frame[i].SelfTicks*toMs;bytes+=frame[i].SelfBytes;raid.Modules[i].Add(frame[i]);window.Modules[i].Add(frame[i]);}
        bool slow=duration.HasValue&&duration.Value>=slowMs;
        AddFrame(raid,duration,cost,slow);AddFrame(window,duration,cost,slow);
        if(slow)
        {
            int replace=-1;
            if(hitches.Count==64)
            {
                window.Omitted++;raid.Omitted++;
                for(int i=0;i<hitches.Count;i++)if(replace<0||hitches[i].FrameMs<hitches[replace].FrameMs)replace=i;
                if(duration<=hitches[replace].FrameMs)replace=-2;
            }
            if(replace!=-2)
            {
                var top=Metrics(frame);Array.Sort(top,(a,b)=>b.SelfMs.CompareTo(a.SelfMs));
                // Keep time and allocation leaders; do not dump hundreds of rows per hitch.
                var selected=new List<PerformanceModule>(18);
                for(int i=0;i<Math.Min(12,top.Length);i++)selected.Add(top[i]);
                Array.Sort(top,(a,b)=>(b.SelfBytes??0).CompareTo(a.SelfBytes??0));
                for(int i=0;i<Math.Min(6,top.Length);i++)if(!selected.Contains(top[i]))selected.Add(top[i]);
                var f=new PerformanceFrame{Frame=frameId,Realtime=realtime,FrameMs=duration,Partial=first,CometMs=cost,CometBytes=allocations?bytes:(long?)null,
                    Collections=new[]{Math.Max(0,g0-gc0),Math.Max(0,g1-gc1),Math.Max(0,g2-gc2)},TopModules=selected.ToArray()};
                if(replace>=0)hitches[replace]=f;else hitches.Add(f);
            }
        }
        Array.Clear(frame,0,frame.Length);first=false;
    }
    private void AddFrame(Totals t,double? duration,double cost,bool slow)
    {
        if(t.First<0)t.First=frameId;t.Last=frameId;t.Frames++;
        if(!duration.HasValue)t.Unknown++;if(slow)t.Slow++;t.CometMs+=cost;
        t.MaxCometMs=Math.Max(t.MaxCometMs,cost);t.MaxFrameMs=Math.Max(t.MaxFrameMs,duration??0);
    }
    public PerformanceReport Finish(int g0,int g1,int g2)
    {
        if(!finished)
        {
            if(depth!=0)throw new InvalidOperationException("Finish after all performance scopes have returned.");
            if(frameId>=0)Close(null,g0,g1,g2); // The last frame has not completed yet.
            finished=true;
        }
        return Snapshot(raid,true);
    }
    private PerformanceReport Snapshot(Totals t,bool final) => new()
    {
        Stage=final?"performance-summary":"performance-window",FirstFrame=t.First,LastFrame=t.Last,Frames=t.Frames,
        UnknownDurationFrames=t.Unknown,UnobservedFrames=t.Missing,SlowFrames=t.Slow,OmittedSlowFrames=t.Omitted,
        ScopeOverflows=overflows,CometMs=t.CometMs,MaxCometFrameMs=t.MaxCometMs,MaxFrameMs=t.MaxFrameMs,
        Modules=Metrics(t.Modules),Hitches=hitches.ToArray()
    };
    private PerformanceModule[] Metrics(Metric[] source)
    {
        var result=new List<PerformanceModule>();
        for(int i=0;i<source.Length;i++)if(source[i].Calls>0)
        {
            var m=source[i];result.Add(new PerformanceModule{Module=names[i],Calls=m.Calls,InclusiveMs=m.Ticks*toMs,SelfMs=m.SelfTicks*toMs,
                MaxCallMs=m.MaxTicks*toMs,MaxFrameSelfMs=Math.Max(m.MaxFrame,m.SelfTicks*(ReferenceEquals(source,frame)?1:0))*toMs,
                InclusiveBytes=allocations?m.Bytes:(long?)null,SelfBytes=allocations?m.SelfBytes:(long?)null});
        }
        return result.ToArray();
    }
}
