using System;
using System.Collections;
using System.Collections.Generic;

namespace Comet.Core;

public sealed class CoverScanConfig
{
    public bool Enabled { get; set; } = true;
    public double MaxScanSeconds { get; set; } = 10;

    public void Validate() => CometConfig.Range(MaxScanSeconds, 5, 30, nameof(MaxScanSeconds));
}

public sealed class CoverScanTrace
{
    public string State { get; set; } = "idle";
    public string Reason { get; set; } = "cover-scan-idle";
    public string Contact { get; set; } = "";
    public string Selected { get; set; } = "";
    public int Scan { get; set; }
    public int Published { get; set; }
    public int Pending { get; set; }
    public double Elapsed { get; set; }
    public double ValidFor { get; set; }
}

// Owns publication, not the search algorithm. The native iterator still does one normal
// step per MoveNext; its list accesses alone are redirected to Working during that step.
public sealed class CoverScanRunner<T> : IEnumerator, IDisposable
{
    private IEnumerator native;
    private Func<IEnumerator> restart;
    private Func<int> mode; // 0 native, 1 authoritative awake PMC, 2 suspended
    private Func<string> contact;
    private Func<double> clock,
        limit;
    private Func<IEnumerator, bool> complete;
    private Action<CoverScanRunner<T>, bool> redirect;
    private Action<CoverScanTrace> record;
    private Action<Exception> error;
    private object? pause;
    private Func<bool>? retryReady;
    private bool owned,
        disposed,
        failed,
        nativePartial;
    private double started;
    private string context = "";
    public readonly List<T> Working = new();
    public List<T> Published { get; private set; }

    // Live diagnostics, consumed synchronously. Reuse this object on coroutine steps;
    // serializers create the durable snapshot only when an event is actually emitted.
    public CoverScanTrace Trace { get; } = new();
    public bool Scanning { get; private set; }
    public object? Current { get; private set; }

    public CoverScanRunner(
        IEnumerator original,
        List<T> published,
        Func<IEnumerator> restart,
        Func<int> mode,
        Func<string> contact,
        Func<double> clock,
        Func<double> limit,
        Func<IEnumerator, bool> complete,
        Action<CoverScanRunner<T>, bool> redirect,
        Action<CoverScanTrace> record,
        Action<Exception> error,
        object pause,
        Func<bool>? retryReady = null
    )
    {
        native = original;
        Published = published;
        this.restart = restart;
        this.mode = mode;
        this.contact = contact;
        this.clock = clock;
        this.limit = limit;
        this.complete = complete;
        this.redirect = redirect;
        this.record = record;
        this.error = error;
        this.pause = pause;
        this.retryReady = retryReady;
    }

    public bool Retains(string target) =>
        owned && !failed && !disposed && mode() == 1 && target.Length > 0 && context == target;

    private void Report(string state, string reason, double ttl = 2, string selected = "")
    {
        Trace.State = state;
        Trace.Reason = reason;
        Trace.Contact = context;
        Trace.Selected = selected;
        Trace.Published = Published.Count;
        Trace.Pending = Working.Count;
        Trace.Elapsed = Scanning ? Math.Max(0, clock() - started) : 0;
        Trace.ValidFor = ttl;
        record(Trace);
    }

    public void Selection(bool valid, string reason, string selected)
    {
        if (!disposed)
            Report(valid ? "validated" : "rejected", reason, 2, selected);
    }

    private void Restart()
    {
        (native as IDisposable)?.Dispose();
        native = restart();
        Working.Clear();
        Scanning = false;
    }

    private void Invalidate(string reason)
    {
        Published.Clear();
        Working.Clear();
        Report("ended", reason, 0);
        context = "";
        Restart();
        owned = false;
    }

    // Also called outside the coroutine, so sleeping/stopping cannot leave stale context alive.
    public void CheckContext()
    {
        if (disposed || !owned)
            return;
        int m = mode();
        string target = contact();
        if (m != 1)
            Invalidate(m != 1 ? "cover-scan-out-of-scope" : "cover-scan-contact-changed");
    }

    public bool MoveNext()
    {
        if (disposed)
            return false;
        try
        {
            if (failed && retryReady?.Invoke() == true)
            {
                Restart();
                failed = false;
                nativePartial = false;
            }
            CheckContext();
            int m = failed ? 0 : mode();
            if (m == 0)
            {
                bool more = native.MoveNext();
                Current = native.Current;
                nativePartial = more && !complete(native);
                return more;
            }
            string target = contact();
            if (m == 2 || target.Length == 0)
            {
                Current = pause;
                return true;
            }
            if (!owned)
            {
                // Config can arrive while a vanilla scan is halfway through. Do not
                // mistake those partial results for a completed snapshot.
                if (nativePartial)
                    Published.Clear();
                nativePartial = false;
                Restart();
                context = target;
                owned = true;
            }
            if (Scanning && clock() < started)
            {
                Restart();
                Current = pause;
                return true;
            }
            context = target;
            if (!Scanning)
            {
                Scanning = true;
                started = clock();
                Working.Clear();
                Trace.Scan++;
            }
            bool next;
            redirect(this, true);
            try
            {
                next = native.MoveNext();
            }
            finally
            {
                redirect(this, false);
            }
            Current = native.Current;
            // A contact may be replaced by native callbacks during MoveNext.
            if (mode() != 1)
            {
                Invalidate("cover-scan-context-changed-during-step");
                Current = pause;
                return true;
            }
            if (!next)
            {
                Dispose();
                return false;
            }
            if (complete(native))
            {
                Published.Clear();
                Published.AddRange(Working);
                Working.Clear();
                Report(
                    "published",
                    Published.Count == 0 ? "cover-scan-complete-empty" : "cover-scan-complete"
                );
                Scanning = false;
            }
            else
                Report("scanning", "cover-scan-retaining-results");
            return true;
        }
        catch (Exception ex)
        {
            if (disposed)
                return false;
            if (failed)
            {
                Dispose();
                return false;
            }
            // Never repeatedly throw on every frame. Discard partial results and yield to SAIN.
            failed = true;
            Working.Clear();
            Published.Clear();
            Report("ended", "cover-scan-adapter-error", 0);
            owned = false;
            Scanning = false;
            error(ex);
            Restart();
            Current = pause;
            return true;
        }
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        try
        {
            if (owned)
                Report("ended", "cover-scan-stopped", 0);
        }
        finally
        {
            try
            {
                (native as IDisposable)?.Dispose();
            }
            finally
            {
                Working.Clear();
                if (owned)
                    Published.Clear();
                Published = new();
                native = null!;
                restart = null!;
                mode = null!;
                contact = null!;
                clock = null!;
                limit = null!;
                complete = null!;
                redirect = null!;
                record = null!;
                error = null!;
                retryReady = null;
                pause = null;
                Current = null;
                owned = false;
                Scanning = false;
                context = "";
            }
        }
    }

    public void Reset() => throw new NotSupportedException();
}
