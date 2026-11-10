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
    public bool DurationMismatch { get; set; }
    public double CometMs { get; set; }
    public long? CometBytes { get; set; }
    public int[] Collections { get; set; } = Array.Empty<int>();
    public PerformanceModule[] TopModules { get; set; } = Array.Empty<PerformanceModule>();
}
public sealed class PerformanceReport
{
    public object? Serialization { get; set; }
    public string Stage { get; set; } = "performance-window";
    public int FirstFrame { get; set; }
    public int LastFrame { get; set; }
    public long Frames { get; set; }
    public long UnknownDurationFrames { get; set; }
    public long DurationMismatchFrames { get; set; }
    public long UnobservedFrames { get; set; }
    public long SlowFrames { get; set; }
    public long OmittedSlowFrames { get; set; }
    public long ScopeOverflows { get; set; }
    public long CoalescedScopes { get; set; }
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
        public long Frames, Unknown, Missing, Slow, Omitted, Mismatches;
        public double CometMs, MaxCometMs, MaxFrameMs;
        public readonly Metric[] Modules;
        public Totals(int n) { Modules=new Metric[n]; }
    }
    private readonly string[] names;
    private readonly double toMs, slowMs, interval;
    private readonly bool allocations;
    private readonly Scope[] stack = new Scope[256];
    private readonly Metric[] frame;
    private readonly Metric[] pending;
    private int pendingId=-1, pendingGc0,pendingGc1,pendingGc2,pendingEnd0,pendingEnd1,pendingEnd2;
    private double pendingAt;
    private readonly Totals raid;
    private Totals window;
    private readonly List<PerformanceFrame> hitches = new(64);
    private long serial, overflows;
    private int depth, frameId=-1, gc0, gc1, gc2;
    private double realtime, windowStart;
    private bool first=true, finished;
    public int Depth => depth;
    private long coalesced;
    public bool Coalesce(int module)
    {
        if(depth==0||stack[depth-1].Module!=module)return false;
        coalesced++;return true;
    }
    public PerformanceRecorder(string[] modules, long frequency, bool allocations, double slowMs, double interval)
    {
        names=(string[])modules.Clone();toMs=1000.0/frequency;this.allocations=allocations;this.slowMs=slowMs;this.interval=interval;
        frame=new Metric[names.Length];pending=new Metric[names.Length];raid=new Totals(names.Length);window=new Totals(names.Length);
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
    public PerformanceReport? Advance(int id, double now, double previousFrameMs, int g0, int g1, int g2, bool deferDuration=false)
    {
        if(finished)return null;
        if(id==frameId && (pendingId<0||deferDuration))return null;
        if(depth!=0)throw new InvalidOperationException("Performance scope crossed a frame boundary.");
        double? duration=previousFrameMs>0&&!double.IsInfinity(previousFrameMs)&&!double.IsNaN(previousFrameMs)?previousFrameMs:null;
        if(pendingId>=0)CompletePending(!deferDuration&&pendingId==id-1?duration:null);
        if(id==frameId)return WindowReport(now);
        if(frameId>=0)
        {
            bool consecutive=id==frameId+1;
            if(deferDuration&&consecutive)
            {
                // FixedUpdate may be the first callback at N. Preserve N-1 until
                // Update supplies its render duration; physics costs still go to N.
                Array.Copy(frame,pending,frame.Length);Array.Clear(frame,0,frame.Length);
                pendingId=frameId;pendingAt=realtime;pendingGc0=gc0;pendingGc1=gc1;pendingGc2=gc2;
                pendingEnd0=g0;pendingEnd1=g1;pendingEnd2=g2;
            }
            else Close(frame,frameId,realtime,consecutive?duration:null,gc0,gc1,gc2,g0,g1,g2);
            int missed=Math.Max(0,id-frameId-1);raid.Missing+=missed;window.Missing+=missed;
        }
        else windowStart=now;
        frameId=id;realtime=now;gc0=g0;gc1=g1;gc2=g2;
        return pendingId<0?WindowReport(now):null;
    }
    private PerformanceReport? WindowReport(double now)
    {
        if(window.Frames==0||now-windowStart<interval)return null;
        var report=Snapshot(window,false);window=new Totals(names.Length);hitches.Clear();windowStart=now;return report;
    }
    private void CompletePending(double? duration)
    {
        if(pendingId<0)return;
        Close(pending,pendingId,pendingAt,duration,pendingGc0,pendingGc1,pendingGc2,pendingEnd0,pendingEnd1,pendingEnd2);
        pendingId=-1;
    }
    private void Close(Metric[] values,int id,double at,double? duration,int start0,int start1,int start2,int g0,int g1,int g2)
    {
        double cost=0;long bytes=0;
        for(int i=0;i<values.Length;i++){cost+=values[i].SelfTicks*toMs;bytes+=values[i].SelfBytes;raid.Modules[i].Add(values[i]);window.Modules[i].Add(values[i]);}
        bool slow=(duration.HasValue&&duration.Value>=slowMs)||cost>=slowMs;
        bool mismatch=duration.HasValue&&cost>duration.Value+1;
        if(mismatch){raid.Mismatches++;window.Mismatches++;}
        AddFrame(raid,id,duration,cost,slow);AddFrame(window,id,duration,cost,slow);
        if(slow)
        {
            int replace=-1;
            if(hitches.Count==64)
            {
                window.Omitted++;raid.Omitted++;
                for(int i=0;i<hitches.Count;i++)if(replace<0||HitchCost(hitches[i])<HitchCost(hitches[replace]))replace=i;
                if(Math.Max(duration??0,cost)<=HitchCost(hitches[replace]))replace=-2;
            }
            if(replace!=-2)
            {
                var top=Metrics(values,true);Array.Sort(top,(a,b)=>b.SelfMs.CompareTo(a.SelfMs));
                // Keep time and allocation leaders; do not dump hundreds of rows per hitch.
                var selected=new List<PerformanceModule>(18);
                for(int i=0;i<Math.Min(12,top.Length);i++)selected.Add(top[i]);
                Array.Sort(top,(a,b)=>(b.SelfBytes??0).CompareTo(a.SelfBytes??0));
                for(int i=0;i<Math.Min(6,top.Length);i++)if(!selected.Contains(top[i]))selected.Add(top[i]);
                var f=new PerformanceFrame{Frame=id,Realtime=at,FrameMs=duration,Partial=first,DurationMismatch=mismatch,CometMs=cost,CometBytes=allocations?bytes:(long?)null,
                    Collections=new[]{Math.Max(0,g0-start0),Math.Max(0,g1-start1),Math.Max(0,g2-start2)},TopModules=selected.ToArray()};
                if(replace>=0)hitches[replace]=f;else hitches.Add(f);
            }
        }
        Array.Clear(values,0,values.Length);first=false;
    }
    private static double HitchCost(PerformanceFrame f)=>Math.Max(f.FrameMs??0,f.CometMs);
    private void AddFrame(Totals t,int id,double? duration,double cost,bool slow)
    {
        if(t.First<0)t.First=id;t.Last=id;t.Frames++;
        if(!duration.HasValue)t.Unknown++;if(slow)t.Slow++;t.CometMs+=cost;
        t.MaxCometMs=Math.Max(t.MaxCometMs,cost);t.MaxFrameMs=Math.Max(t.MaxFrameMs,duration??0);
    }
    public PerformanceReport Finish(int g0,int g1,int g2)
    {
        if(!finished)
        {
            if(depth!=0)throw new InvalidOperationException("Finish after all performance scopes have returned.");
            CompletePending(null);
            if(frameId>=0)Close(frame,frameId,realtime,null,gc0,gc1,gc2,g0,g1,g2); // The last frame has not completed yet.
            finished=true;
        }
        return Snapshot(raid,true);
    }
    private PerformanceReport Snapshot(Totals t,bool final) => new()
    {
        Stage=final?"performance-summary":"performance-window",FirstFrame=t.First,LastFrame=t.Last,Frames=t.Frames,
        UnknownDurationFrames=t.Unknown,DurationMismatchFrames=t.Mismatches,UnobservedFrames=t.Missing,SlowFrames=t.Slow,OmittedSlowFrames=t.Omitted,
        ScopeOverflows=overflows,CoalescedScopes=coalesced,CometMs=t.CometMs,MaxCometFrameMs=t.MaxCometMs,MaxFrameMs=t.MaxFrameMs,
        Modules=Metrics(t.Modules),Hitches=hitches.ToArray()
    };
    private PerformanceModule[] Metrics(Metric[] source,bool singleFrame=false)
    {
        var result=new List<PerformanceModule>();
        for(int i=0;i<source.Length;i++)if(source[i].Calls>0)
        {
            var m=source[i];result.Add(new PerformanceModule{Module=names[i],Calls=m.Calls,InclusiveMs=m.Ticks*toMs,SelfMs=m.SelfTicks*toMs,
                MaxCallMs=m.MaxTicks*toMs,MaxFrameSelfMs=Math.Max(m.MaxFrame,m.SelfTicks*(singleFrame?1:0))*toMs,
                InclusiveBytes=allocations?m.Bytes:(long?)null,SelfBytes=allocations?m.SelfBytes:(long?)null});
        }
        return result.ToArray();
    }
}
