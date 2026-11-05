using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Comet.Core;
using Newtonsoft.Json;

namespace Comet.Client;

// Independent of the decision journal's quota. Never retains a bot or GameWorld.
internal sealed class PerformanceWriter
{
    private readonly BlockingCollection<PerformanceReport> queue=new(8);
    private readonly Action<string> warning;
    private readonly Task writer;
    private PerformanceReport? final;
    private Task diagnosticsDone=Task.CompletedTask;
    private long dropped;
    private int closed;
    internal readonly PerformanceBackground Background;
    internal Task Completion => writer;
    internal PerformanceWriter(string path, object metadata, Action<string> warning, bool allocations, long maxBytes=64*1024*1024)
    {
        this.warning=warning;Background=new PerformanceBackground(allocations);
        writer=Task.Run(async()=>
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                using var output=new StreamWriter(new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.Read));
                long bytes=0,limited=0;
                void Line(object value,bool mandatory=false)
                {
                    string line=JsonConvert.SerializeObject(value,Diagnostics.Json);int size=Encoding.UTF8.GetByteCount(line)+2;
                    if(!mandatory&&bytes+size>maxBytes){limited++;return;}
                    output.WriteLine(line);bytes+=size;
                }
                Line(metadata,true);output.Flush();
                foreach(var report in queue.GetConsumingEnumerable()){Line(report);output.Flush();}
                // The decision writer drains off-thread too. Do not wait on Unity.
                await diagnosticsDone.ConfigureAwait(false);
                if(final!=null)Line(final,true);
                Line(new{stage="performance-end",complete=final?.Stage=="performance-summary",queueDropped=Interlocked.Read(ref dropped),fileLimitDropped=limited,
                    backgroundDiagnostics=Background.Snapshot(),note="Background elapsed time is separate from main-thread COMET time."},true);
                output.Flush();
                if(limited>0||dropped>0)Warn("COMET performance report is incomplete; missing windows are counted in its footer. The final totals are retained.");
            }
            catch(Exception ex){Interlocked.Exchange(ref closed,1);Warn("COMET performance file failed: "+ex.Message);}
        });
    }
    internal void Enqueue(PerformanceReport report)
    {
        if(Volatile.Read(ref closed)!=0)return;
        try{if(!queue.TryAdd(report))Interlocked.Increment(ref dropped);}catch(InvalidOperationException){}
    }
    internal void Finish(PerformanceReport report,Task? diagnostics)
    {
        if(Interlocked.Exchange(ref closed,1)!=0)return;
        final=report;diagnosticsDone=diagnostics??Task.CompletedTask;queue.CompleteAdding();
    }
    private void Warn(string message){try{warning(message);}catch{}}
}

// Capability detection once. Unsupported Mono runtimes report null allocations.
internal static class PerformanceAllocation
{
    internal static readonly Func<long>? Read=Create();
    private static Func<long>? Create()
    {
        try { var method=typeof(GC).GetMethod("GetAllocatedBytesForCurrentThread",Type.EmptyTypes);
            if(method==null)return null;var read=(Func<long>)Delegate.CreateDelegate(typeof(Func<long>),method);
            return Probe(read)?read:null; }
        catch{return null;}
    }
    internal static bool Probe(Func<long> read)
    {
        // Some Mono implementations expose the API but always return zero.
        // Probe once, outside combat; unavailable must never be reported as zero allocation.
        try { long before=read();var proof=new byte[4096];long after=read();GC.KeepAlive(proof);return before>=0&&after-before>=4096; }
        catch{return false;}
    }
}
internal sealed class PerformanceBackground
{
    private int allocations;
    private long calls,ticks,maxTicks,bytes;
    internal PerformanceBackground(bool allocations){this.allocations=allocations&&PerformanceAllocation.Read!=null?1:0;}
    internal long Allocated()
    {
        if(Volatile.Read(ref allocations)==0)return 0;
        try{return PerformanceAllocation.Read!();}catch{Interlocked.Exchange(ref allocations,0);return 0;}
    }
    internal void Add(long elapsed,long allocated)
    {
        Interlocked.Increment(ref calls);Interlocked.Add(ref ticks,elapsed);Interlocked.Add(ref bytes,Math.Max(0,allocated));
        long prior;do{prior=Interlocked.Read(ref maxTicks);if(elapsed<=prior)break;}while(Interlocked.CompareExchange(ref maxTicks,elapsed,prior)!=prior);
    }
    internal object Snapshot()=>new{events=Interlocked.Read(ref calls),elapsedMs=Interlocked.Read(ref ticks)*1000.0/Stopwatch.Frequency,
        maxEventMs=Interlocked.Read(ref maxTicks)*1000.0/Stopwatch.Frequency,allocatedBytes=Volatile.Read(ref allocations)!=0?Interlocked.Read(ref bytes):(long?)null};
}
