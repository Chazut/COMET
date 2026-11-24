using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Comet.Core;
using UnityEngine;

namespace Comet.Client;

// Generated scopes are value types. OFF: one null check, no clocks or allocations.
internal static class PerformanceCapture
{
    private static Session? active;

    internal static CombatWorkCounts? Work
    {
        get
        {
            var s = active;
            return s != null && s.Thread == Environment.CurrentManagedThreadId ? s.Work : null;
        }
    }

    internal readonly struct Scope : IDisposable
    {
        private readonly Session? owner;
        private readonly long token;

        internal Scope(Session owner, long token)
        {
            this.owner = owner;
            this.token = token;
        }

        public void Dispose()
        {
            owner?.Leave(token);
        }
    }

    internal static Scope Enter(int module)
    {
        var s = active;
        if (s == null || s.Thread != Environment.CurrentManagedThreadId)
            return default;
        try
        {
            if (s.Recorder.Coalesce(module))
                return default;
            if (s.Recorder.Depth == 0)
                s.Tick();
            return new Scope(s, s.Recorder.Enter(module, Stopwatch.GetTimestamp(), s.Allocated()));
        }
        catch (Exception ex)
        {
            s.Fail(ex);
            return default;
        }
    }

    internal static void Tick()
    {
        var s = active;
        if (s == null)
            return;
        try
        {
            s.Tick();
        }
        catch (Exception ex)
        {
            s.Fail(ex);
        }
    }

    internal static PerformanceBackground? Start(
        PerformanceConfig config,
        string directory,
        string session,
        long revision,
        string map,
        bool authority,
        AiLightingFrameCounts lighting,
        Action<string> warning
    )
    {
        Stop(null);
        if (!config.Enabled)
            return null;
        try
        {
            string path = Path.Combine(
                directory,
                "performance-"
                    + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")
                    + "-"
                    + session
                    + ".jsonl"
            );
            var s = new Session(config, path, session, revision, map, authority, lighting, warning);
            s.Tick();
            active = s;
            s.Sampling.Start();
            return s.Writer.Background;
        }
        catch (Exception ex)
        {
            try
            {
                warning("COMET performance capture unavailable: " + ex.Message);
            }
            catch { }
            return null;
        }
    }

    internal static Task Stop(Task? diagnostics)
    {
        var s = active;
        active = null;
        if (s == null)
            return Task.CompletedTask;
        s.Sampling.Stop();
        s.Closing = true;
        s.DiagnosticsDone = diagnostics;
        if (s.Recorder.Depth == 0)
            s.Finish(); // Otherwise the outermost scope finishes after cleanup returns.
        return s.Writer.Completion;
    }

    internal sealed class Session
    {
        internal readonly int Thread = Environment.CurrentManagedThreadId;
        internal readonly CombatWorkCounts Work = new();
        internal readonly RaidSamplingMetrics Sampling = new(() => Time.frameCount);
        internal readonly PerformanceRecorder Recorder;
        internal readonly PerformanceWriter Writer;
        internal bool Closing;
        internal Task? DiagnosticsDone;
        private readonly bool allocations;
        private readonly AiLightingFrameCounts lighting;
        private readonly Action<string> warning;
        private bool failed,
            finished;
        private int sampledFrame = -1;
        private bool sampledRenderTime;

        internal Session(
            PerformanceConfig c,
            string path,
            string session,
            long revision,
            string map,
            bool authority,
            AiLightingFrameCounts lighting,
            Action<string> warning
        )
        {
            this.lighting = lighting;
            this.warning = warning;
            allocations = c.MeasureAllocations && PerformanceAllocation.Read != null;
            Recorder = new PerformanceRecorder(
                PerformanceCatalog.Modules,
                Stopwatch.Frequency,
                allocations,
                c.SlowFrameMs,
                c.ReportIntervalSeconds
            );
            Recorder.SubscribersInFrame = Sampling.SubscribersInFrame;
            Writer = new PerformanceWriter(
                path,
                new
                {
                    schema = 1,
                    stage = "performance-start",
                    session,
                    configRevision = revision,
                    map,
                    authority,
                    version = typeof(Plugin).Assembly.GetName().Version?.ToString(),
                    utc = DateTime.UtcNow.ToString("O"),
                    realtime = Time.realtimeSinceStartupAsDouble,
                    frame = Time.frameCount,
                    settings = c,
                    serialization = Diagnostics.SerializerMetrics(),
                    allocationsAvailable = allocations,
                    modules = PerformanceCatalog.Modules,
                    instrumentedMethods = PerformanceCatalog.MethodCount,
                    scopePolicy = "Nested calls in the same module share one timer. Lifecycle and control guard cost is charged to its caller; lifecycle sweeps and control sampling retain their own timer. Calls count measured entries, not every helper invocation.",
                    timing = "Main-thread elapsed time, including native callees and synchronous subscribers. Not GPU time or whole-game CPU time.",
                    nesting = "Inclusive includes children; add SelfMs, not InclusiveMs. Instrumentation overhead remains in measured frames.",
                    coverage = "Synchronous COMET entry points and adapters; suspended iterators, async continuations and Fika addon internals are excluded. Background diagnostics reported separately.",
                    combatWorkTotals = "Cumulative main-thread counts since capture start. Difference successive windows; do not sum. RangeKnownCandidates counts known-list entries on goal lookup, RangeGoalDetailReads includes fallback goals. SearchReports includes confirmations and forced events.",
                    frames = "Unity frame number and realtime. Delta observed at N is assigned to N-1. First frame partial; last duration unknown. GC counters indicate coincidence, not causation.",
                    limits = "8 queued windows; 64 detailed hitches per window, prioritizing COMET over budget; exact COMET and game budget counts in totals; 64 MiB plus final totals. Interrupted sessions have no performance-end footer.",
                },
                warning,
                allocations
            );
        }

        internal long Allocated() => allocations ? PerformanceAllocation.Read!() : 0;

        internal void Tick()
        {
            int frame = Time.frameCount;
            if (frame == sampledFrame && sampledRenderTime)
                return;
            bool physics = Time.inFixedTimeStep;
            if (frame == sampledFrame && physics)
                return;
            sampledFrame = frame;
            sampledRenderTime = !physics;
            // In FixedUpdate Unity returns the physics step, not the rendered frame duration.
            var duration = physics ? double.NaN : Time.unscaledDeltaTime * 1000.0;
            var report = Recorder.Advance(
                frame,
                Time.realtimeSinceStartupAsDouble,
                duration,
                GC.CollectionCount(0),
                GC.CollectionCount(1),
                GC.CollectionCount(2),
                physics
            );
            if (report != null)
            {
                AddSummaryMetrics(report);
                Writer.Enqueue(report);
            }
        }

        private void AddSummaryMetrics(PerformanceReport report)
        {
            report.CombatWorkTotals = Work.Snapshot();
            report.Serialization = Diagnostics.SerializerMetrics();
            report.AiLightingMaxSamplesPerFrame = lighting.MaxSamplesPerFrame;
            report.AiLightingFramesOverBudget = lighting.FramesOverBudget;
        }

        internal void Leave(long token)
        {
            if (failed)
                return;
            try
            {
                Recorder.Leave(token, Stopwatch.GetTimestamp(), Allocated());
                if (Closing && Recorder.Depth == 0)
                    Finish();
            }
            catch (Exception ex)
            {
                Fail(ex);
            }
        }

        internal void Finish()
        {
            if (finished)
                return;
            try
            {
                var report = Recorder.Finish(
                    GC.CollectionCount(0),
                    GC.CollectionCount(1),
                    GC.CollectionCount(2)
                );
                AddSummaryMetrics(report);
                report.RaidSampling = Sampling.Snapshot();
                Writer.Finish(report, DiagnosticsDone);
                finished = true;
            }
            catch (Exception ex)
            {
                Fail(ex);
            }
        }

        internal void Fail(Exception ex)
        {
            if (failed)
                return;
            failed = true;
            Sampling.Stop();
            if (ReferenceEquals(active, this))
                active = null;
            // Close the writer even on a broken measurement; never interrupt gameplay.
            Writer.Finish(new PerformanceReport { Stage = "performance-aborted" }, DiagnosticsDone);
            try
            {
                warning("COMET performance capture stopped: " + ex.Message);
            }
            catch { }
        }
    }
}
