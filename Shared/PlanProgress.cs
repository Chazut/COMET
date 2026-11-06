using System;
using System.Collections.Generic;
using System.Linq;

namespace Comet.Core;

public sealed class ProgressFrame
{
    public double Now, X, Y, Z;
    public bool Scoped = true, HasThreat, Owned, Paused, Arrived, Productive;
    public string Action = "", Attempt = "", Intent = "", Route = "", PauseReason = "", Target = "";
    public double? Remaining;
    public double CueAt = -1, CueX, CueY, CueZ;
}

public sealed class ProgressTrace
{
    public string State { get; set; } = "idle";
    public string Reason { get; set; } = "progress-not-owned";
    public string Action { get; set; } = "";
    public string Attempt { get; set; } = "";
    public string Intent { get; set; } = "";
    public string LastOutcome { get; set; } = "";
    public double At { get; set; }
    public int Encounter { get; set; }
    public double HoldSeconds { get; set; }
    public double StalledSeconds { get; set; }
    public double MovementSeconds { get; set; }
    public double? Remaining { get; set; }
    public double ProgressMeters { get; set; }
    public double RetryRemaining { get; set; }
    public int Failures { get; set; }
    public bool Reconsider { get; set; }
    public bool Productive { get; set; }
}

// One bounded memory per bot, shared by A01/A02/A03. Only detached observations enter it.
public sealed class PlanProgress
{
    private sealed class AttemptMemory
    {
        public double Until, Last, ActiveSeconds, Best = double.NaN;
        public int Failures;
        public string Route = "";
    }
    private readonly ProgressConfig config;
    private readonly Dictionary<string, AttemptMemory> attempts = new Dictionary<string, AttemptMemory>();
    private readonly Dictionary<string, (double at, double x, double y, double z)> cues = new Dictionary<string, (double, double, double, double)>();
    private readonly List<(double x, double y, double z)> visited = new List<(double, double, double)>();
    private double last = double.NaN, quietSince = double.NaN, hold, stalled, moving, justifiedUntil;
    private bool eligibleInterval;
    private bool priorWaiting;
    private string priorAction = "", priorAttempt = "";
    private int encounter = 1;
    public readonly Dictionary<string, string> CandidateAttempts = new Dictionary<string, string>();
    public ProgressTrace Current { get; private set; } = new ProgressTrace();
    public bool Enabled => config.Enabled;
    public PlanProgress(ProgressConfig config) { config.Validate(); this.config = config; }
    private static bool Finite(double x) => !double.IsNaN(x) && !double.IsInfinity(x);
    private static double Distance(double x, double y, double z, double a, double b, double c)
        => Math.Sqrt((x-a)*(x-a)+(y-b)*(y-b)+(z-c)*(z-c));
    public static bool IsMove(string action) => action == "Push" || action == "ContinuePush" || action == "Search" || action == "FallBack" || action == "Cover" || action == "Flank";
    private static bool IsHold(string action) => action == "Hold" || action == "KeepAngle";
    private AttemptMemory Memory(string key, double now)
    {
        if (!attempts.TryGetValue(key, out var m))
        {
            if (attempts.Count >= 32) attempts.Remove(attempts.OrderBy(x=>x.Value.Last).First().Key);
            attempts[key] = m = new AttemptMemory();
        }
        m.Last = now; return m;
    }
    public bool Blocked(string key, double now) => Enabled && key.Length > 0 && attempts.TryGetValue(key, out var m) && now < m.Until;
    public void Clear()
    {
        attempts.Clear(); cues.Clear(); visited.Clear(); CandidateAttempts.Clear(); hold = stalled = moving = justifiedUntil = 0;
        last = quietSince = double.NaN; eligibleInterval = priorWaiting = false; priorAction = priorAttempt = ""; encounter++;
        Current = new ProgressTrace { Encounter = encounter, Reason = "progress-reset" };
    }
    public bool Observe(ProgressFrame f)
    {
        if (!Enabled || !f.Scoped || !Finite(f.Now) || !Finite(f.X) || !Finite(f.Y) || !Finite(f.Z)) { Clear(); return false; }
        double dt = Finite(last) && f.Now >= last && f.Now-last <= 1 ? f.Now-last : 0;
        if (Finite(last) && f.Now < last) Clear();
        last = f.Now;
        if (f.HasThreat) quietSince = double.NaN;
        else if (!Finite(quietSince)) quietSince = f.Now;
        else if (f.Now-quietSince >= config.QuietReset) { Clear(); last = quietSince = f.Now; }
        var oldOutcome = Current.LastOutcome;
        Current = new ProgressTrace { At=f.Now, Encounter=encounter, Action=f.Action, Attempt=f.Attempt, Intent=f.Intent, HoldSeconds=hold,
            StalledSeconds=stalled, MovementSeconds=moving, Remaining=f.Remaining.HasValue && Finite(f.Remaining.Value) && f.Remaining>=0 ? f.Remaining : null, LastOutcome=oldOutcome };
        // A genuinely changed, dated location can justify a brief fresh ambush, not reset the accumulated budget.
        if (f.Target.Length>0 && Finite(f.CueAt) && f.CueAt>=0 && f.Now-f.CueAt>=0 && f.Now-f.CueAt<=2
            && Finite(f.CueX) && Finite(f.CueY) && Finite(f.CueZ))
        {
            if (cues.TryGetValue(f.Target,out var cue) && f.CueAt>cue.at+.05
                && Distance(f.CueX,f.CueY,f.CueZ,cue.x,cue.y,cue.z)>=1
                && Distance(f.X,f.Y,f.Z,f.CueX,f.CueY,f.CueZ)<Distance(f.X,f.Y,f.Z,cue.x,cue.y,cue.z)-.5)
                justifiedUntil=f.Now+2;
            if (!cues.TryGetValue(f.Target,out cue) || f.CueAt>cue.at+.05)
            {
                if (cues.Count>=32 && !cues.ContainsKey(f.Target)) cues.Remove(cues.OrderBy(x=>x.Value.at).First().Key);
                cues[f.Target]=(f.CueAt,f.CueX,f.CueY,f.CueZ);
            }
        }
        bool active=f.Owned && !f.Paused;
        // Actual firing is useful work, unlike a paused path, a fresh target or
        // an animation request. A cover's idle clock measures continuous idle
        // time since useful work, not the sum of gaps between shots in a fight.
        if (f.Owned && f.Productive)
        {
            hold=0; Current.HoldSeconds=0; Current.Productive=true;
        }
        if (!active)
        {
            eligibleInterval=false; Current.State=f.Paused?"paused":"idle";
            Current.Reason=f.Paused?f.PauseReason:"progress-not-owned"; return false;
        }
        // Pauses and ownership gaps do not count as elapsed work and do not erase the encounter budget.
        if (!eligibleInterval) dt=0;
        eligibleInterval=true;
        Current.State="tracking"; Current.Reason="progress-observing";
        bool waiting=IsHold(f.Action) || IsMove(f.Action) && f.Arrived;
        if (waiting && priorWaiting && !f.Productive) hold+=dt;
        if (IsHold(f.Action))
        {
            Current.State=f.Now<justifiedUntil?"justified":"holding";
            Current.Reason=hold>=config.HoldLimit && f.Now>=justifiedUntil?"progress-hold-limit":hold>=config.HoldSoft?"progress-hold-fatigue":"progress-hold-useful";
            Current.Reconsider=hold>=config.HoldLimit && f.Now>=justifiedUntil;
        }
        else if (IsMove(f.Action))
        {
            if (IsMove(priorAction)) { stalled+=dt; moving+=dt; }
            double gain=0;
            var m=f.Attempt.Length>0?Memory(f.Attempt,f.Now):null;
            if(m!=null && priorAttempt==f.Attempt)m.ActiveSeconds+=dt;
            if (m!=null && Current.Remaining.HasValue && f.Route.Length>0)
            {
                if (m.Route!=f.Route || !Finite(m.Best)) { m.Route=f.Route; m.Best=Current.Remaining.Value; }
                else gain=Math.Max(0,m.Best-Current.Remaining.Value);
            }
            // A stationary bot cannot earn progress from a recalculated/shorter path. Novel displacement also handles detours.
            bool novel=visited.Count==0 || visited.All(p=>Distance(f.X,f.Y,f.Z,p.x,p.y,p.z)>=config.ProgressMeters);
            bool displaced=visited.Count>0 && Distance(f.X,f.Y,f.Z,visited[visited.Count-1].x,visited[visited.Count-1].y,visited[visited.Count-1].z)>=config.ProgressMeters;
            bool advanced=dt>0 && displaced && novel;
            if (advanced)
            {
                Current.ProgressMeters=gain>=config.ProgressMeters?gain:Distance(f.X,f.Y,f.Z,visited[visited.Count-1].x,visited[visited.Count-1].y,visited[visited.Count-1].z);
                stalled=0; hold=0; Current.Reason="progress-moving";
                if (m!=null && Current.Remaining.HasValue)m.Best=Current.Remaining.Value;
            }
            if (visited.Count==0 || advanced) { if(visited.Count>=32)visited.RemoveAt(0); visited.Add((f.X,f.Y,f.Z)); }
            if (f.Arrived)
            {
                stalled=0; Current.State="arrived"; Current.Reason="progress-arrived";
                if(!priorWaiting && m!=null)m.Failures=0;
                // Arrival is native evidence, not a license to wait forever without firing or another useful action.
                if(hold>=config.HoldLimit && f.Now>=justifiedUntil)
                { Fail(f.Attempt,f.Now,"progress-arrived-idle"); FailIntent(f); }
            }
            else if (stalled>=config.StalledLimit && m!=null && (m.ActiveSeconds>=1 || stalled>=config.StalledLimit+1))
            {
                Fail(f.Attempt,f.Now,"progress-stalled");
                FailIntent(f);
                Current.StalledSeconds=stalled; stalled=0; moving=0; visited.Clear();
            }
            else if(moving>=config.ReviewInterval)
            { moving=0; Current.State="reviewed"; Current.Reason="progress-review-moving"; }
        }
        // Shoot and other purposeful actions are not subject to an arbitrary duration limit.
        priorAction=f.Action; priorAttempt=f.Attempt; priorWaiting=waiting;
        Current.HoldSeconds=hold; if(!Current.Reconsider)Current.StalledSeconds=stalled;
        Current.MovementSeconds=moving;
        if(attempts.TryGetValue(f.Attempt,out var failure)){Current.RetryRemaining=Math.Max(0,failure.Until-f.Now);Current.Failures=failure.Failures;}
        return Current.Reconsider;
    }
    private void FailIntent(ProgressFrame f)
    {
        // Search corners are intermediate native destinations. Also exclude that same requested goal,
        // so another action cannot immediately retry the identical failed route under a different name.
        if(f.Intent.Length==0 || f.Intent==f.Attempt)return;
        var trace=Current; var m=Memory(f.Intent,f.Now);
        m.Failures=Math.Max(m.Failures,trace.Failures);m.Until=Math.Max(m.Until,f.Now+trace.RetryRemaining);m.ActiveSeconds=0;
    }
    public void Fail(string key,double now,string reason)
    {
        if(!Enabled || key.Length==0 || !Finite(now))return;
        var m=Memory(key,now); if(m.Until>now)return;
        m.Failures++;m.Until=now+(m.Failures>1?config.RepeatRetry:config.Retry);
        m.ActiveSeconds=0;
        Current.State="blocked";Current.Reason=reason;Current.LastOutcome=reason;Current.Attempt=key;
        Current.RetryRemaining=m.Until-now;Current.Failures=m.Failures;Current.Reconsider=true;
    }
    public void Adjust(List<Candidate> candidates,double now)
    {
        foreach(var c in candidates)
        {
            c.ScoreBeforeProgress=c.Score;c.ProgressDelta=0;c.ProgressReason="progress-neutral";
            if(!Enabled || !c.Eligible)continue;
            if(CandidateAttempts.TryGetValue(c.Decision,out var key) && Blocked(key,now))
            {c.Eligible=false;c.Reason=c.ProgressReason="progress-attempt-cooldown";continue;}
            if(c.Decision!="Hold")continue;
            if(hold>=config.HoldLimit && now>=justifiedUntil)
            {c.Eligible=false;c.Reason=c.ProgressReason="progress-hold-limit";continue;}
            if(now<justifiedUntil){c.ProgressReason="progress-fresh-approach";continue;}
            double penalty=25*Math.Max(0,Math.Min(1,(hold-config.HoldSoft)/(config.HoldLimit-config.HoldSoft)));
            c.Score=Math.Round(Math.Max(0,c.Score-penalty),2);c.ProgressDelta=c.Score-c.ScoreBeforeProgress;
            if(penalty>0)c.ProgressReason="progress-hold-fatigue";
        }
    }
}
