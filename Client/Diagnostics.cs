using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace Comet.Api
{
    // Optional consumers use reflection and Action<string>. No COMET assembly reference is required.
    public static class Decisions
    {
        public const int SchemaVersion = 1;
        internal static string RaidSession = "";
        internal static bool RaidClosed;
        public static string GetRaidSession() => RaidSession;
        private static readonly object SubscriberLock=new();
        private static Action<string>? published;
        private static Delegate[] subscribers=Array.Empty<Delegate>();
        public static event Action<string>? Published
        {
            add { lock(SubscriberLock){published+=value;Volatile.Write(ref subscribers,published?.GetInvocationList()??Array.Empty<Delegate>());} }
            remove { lock(SubscriberLock){published-=value;Volatile.Write(ref subscribers,published?.GetInvocationList()??Array.Empty<Delegate>());} }
        }
        // Final, value-only T01-T04 pages. May arrive off-thread after raid teardown.
        // Consumers must use the supplied session, never Unity or the current raid.
        public static event Action<string>? SummaryPublished;
        internal static void PublishSummary(string json, Action<string> warning)
        {
            var handlers=SummaryPublished;if(handlers==null)return;
            foreach(Action<string> subscriber in handlers.GetInvocationList())
                try{subscriber(json);}catch(Exception ex){try{warning("COMET summary subscriber failed: "+ex.Message);}catch{}}
        }
        private static readonly Dictionary<string, string> Current = new Dictionary<string, string>();
        private static readonly HashSet<Action<string>> FailedSubscribers = new();
        private static readonly Dictionary<string,string> Crossfire=new();
        public static string GetCrossfireDecision(string profileId)=>Crossfire.TryGetValue(profileId,out var value)?value:"";
        internal static void SetCrossfire(string profileId,string value){if(value.Length==0)Crossfire.Remove(profileId);else Crossfire[profileId]=value;}
        private static readonly Dictionary<string, string> PostCombat = new Dictionary<string, string>();
        public static string GetPostCombatDecision(string profileId) => PostCombat.TryGetValue(profileId, out var value) ? value : "";
        internal static void SetPostCombat(string profileId,string value) { if(value.Length==0)PostCombat.Remove(profileId);else PostCombat[profileId]=value; }
        public static string GetCurrentDecision(string profileId) => Current.TryGetValue(profileId, out var value) ? value : "";
        internal static void Set(string profileId, string value) { if (value.Length == 0) Current.Remove(profileId); else Current[profileId] = value; }
        internal static void Clear() { Current.Clear();PostCombat.Clear();Crossfire.Clear(); FailedSubscribers.Clear(); }
        internal static void Publish(string json, Action<Exception> onError)
        {
            if (RaidClosed) return;
            // Subscription changes are rare. Keep dispatch synchronous for RR's
            // main-thread contract without allocating a delegate array for every event.
            var handlers = Volatile.Read(ref subscribers);
            foreach (Action<string> subscriber in handlers)
            {
                if (FailedSubscribers.Contains(subscriber)) continue;
                try { subscriber(json); }
                catch (Exception ex) { FailedSubscribers.Add(subscriber); try { onError(ex); } catch { } }
            }
        }
    }
}

namespace Comet.Client
{
    // The revision on every decision resolves to one full snapshot per raid/revision.
    // Keep snapshots in both the local journal and RR's ordinary decision stream.
    internal sealed class DecisionConfiguration
    {
        private string session="";
        private long revision;
        private bool sent;
        internal Comet.Core.CometConfig? Take(Comet.Core.CometConfig? config,string raid)
        {
            if(config==null)return null;
            if(sent&&session==raid&&revision==config.Revision)return null;
            sent=true;session=raid;revision=config.Revision;return config;
        }
    }
    internal sealed class Diagnostics : IDisposable
    {
        internal static readonly JsonSerializerSettings Json = new JsonSerializerSettings { ContractResolver = new CamelCasePropertyNamesContractResolver() };
        private readonly BlockingCollection<string> queue = new BlockingCollection<string>(4096);
        private readonly Action<string> warn;
        private readonly Task writer;
        private long dropped;
        private int closed;
        private int completed;
        internal Task Completion => writer;
        // Optional per-raid worker measurements. No profiler dependency in journal harnesses.
        internal Action<long,long>? PerformanceSample;
        internal Func<long>? PerformanceAllocation;
        private readonly DecisionConfiguration configuration=new();
        internal void ConfigureSnapshot(Comet.Core.CometConfig config,string session)
        {
            var snapshot=configuration.Take(config,session);if(snapshot==null)return;
            Write(JsonConvert.SerializeObject(new{schema=1,session,feature="U01",stage="configuration",configRevision=config.Revision,config=snapshot},Json));
        }
        internal void ConfigureJournal(bool combatEnabled,bool weaponEnabled,long revision,string session)
            => Write(JsonConvert.SerializeObject(new{schema=1,session,feature="T01",stage="configuration",combatEnabled,weaponEnabled,configRevision=revision},Json));
        internal void ConfigureControlJournal(bool enabled,long revision,string session)
            => Write(JsonConvert.SerializeObject(new{schema=1,session,feature="T04",stage="configuration",enabled,configRevision=revision},Json));
        internal void ConfigureCounters(bool enabled,int maxOpportunities,long revision,string session)
            => Write(JsonConvert.SerializeObject(new{schema=1,session,feature="T02",stage="configuration",enabled,maxOpportunities,configRevision=revision},Json));
        // Keep target-selection events bounded as unrelated module configuration grows.
        internal static object ThreatConfig(Comet.Core.CometConfig c)=>new { c.Enabled, c.ThreatSelection };
        public Diagnostics(string directory, string session, Action<string> warning, long segmentBytes = 64L*1024*1024)
        {
            warn = warning;
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + session + ".jsonl");
            writer = Task.Run(() =>
            {
                var counters=new TacticCounters(session);var journal=new CombatReport(session);
                StreamWriter? output=null;long bytes=0,skipped=0;int segment=0;
                string SegmentPath()=>segment==0?path:Path.Combine(directory,Path.GetFileNameWithoutExtension(path)+".part"+segment.ToString("D4")+".jsonl");
                void Open(){output=new StreamWriter(new FileStream(SegmentPath(),FileMode.CreateNew,FileAccess.Write,FileShare.Read));bytes=0;}
                void Line(string line,bool final=false)
                {
                    if(output==null){skipped++;return;}
                    try
                    {
                        // Only this background writer touches files. Keep every event;
                        // segment boundaries never reset sequence numbers or summaries.
                        if(!final&&bytes>=Math.Max(128,segmentBytes)){output.Dispose();output=null;segment++;Open();}
                        output!.WriteLine(line);bytes+=System.Text.Encoding.UTF8.GetByteCount(line)+2;
                    }
                    catch(Exception ex){skipped++;try{output?.Dispose();}catch{}output=null;Warn("COMET diagnostic file failed; counters continue: "+ex.Message);}
                }
                try
                {
                    try{Open();}
                    catch(Exception ex){Warn("COMET diagnostic file unavailable; counters continue: "+ex.Message);}
                    foreach (string line in queue.GetConsumingEnumerable())
                    {
                        var sample=Volatile.Read(ref PerformanceSample);
                        long start=sample!=null?System.Diagnostics.Stopwatch.GetTimestamp():0;
                        long allocated=sample!=null?(PerformanceAllocation?.Invoke()??0):0;
                        counters.Observe(line);journal.Observe(line);Line(line);
                        if(queue.Count==0&&output!=null)try{output.Flush();}catch(Exception ex){try{output.Dispose();}catch{}output=null;Warn("COMET diagnostic flush failed: "+ex.Message);}
                        if(sample!=null)try{sample(System.Diagnostics.Stopwatch.GetTimestamp()-start,(PerformanceAllocation?.Invoke()??0)-allocated);}catch{}
                    }
                    foreach(var page in System.Linq.Enumerable.Concat(counters.Finish(Interlocked.Read(ref dropped)),journal.Finish(Interlocked.Read(ref dropped))))
                    {
                        string json=JsonConvert.SerializeObject(page,Json);
                        if(json.Length>32768){Warn("COMET counter summary page exceeded 32 KiB.");continue;}
                        Line(json,true);Comet.Api.Decisions.PublishSummary(json,Warn);
                    }
                    Line(JsonConvert.SerializeObject(new { schema = 1, session, stage = "diagnostics-end", segments=segment+1,
                        queueDropped = Interlocked.Read(ref dropped), fileLimitDropped = 0, writeFailed = skipped }, Json),true);
                }
                catch (Exception ex) { Interlocked.Exchange(ref closed,1); Warn("COMET diagnostics writer failed: " + ex.Message); }
                finally{try{output?.Dispose();}catch(Exception ex){Warn("COMET diagnostic close failed: "+ex.Message);}}
            });
        }
        public void Write(string json)
        {
            if (Volatile.Read(ref closed)!=0) return;
            try { if (!queue.TryAdd(json))
            {
                long count = Interlocked.Increment(ref dropped);
                if (count == 1) Warn("COMET diagnostics queue full. Missing events will be counted in the file footer.");
            } } catch (InvalidOperationException) { /* Concurrent disposal; never break gameplay. */ }
        }
        private void Warn(string message) { try { warn(message); } catch { } }
        public void Dispose() { Interlocked.Exchange(ref closed,1); if (Interlocked.Exchange(ref completed,1)!=0) return; queue.CompleteAdding(); /* Writer drains without blocking Unity. */ }
    }
}
