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

// Passive diagnostic only. zzap's tactics own their own timing and retries.
public sealed class PlanProgress
{
 private readonly ProgressConfig config;
 private double last=double.NaN,x,y,z,hold,stalled,moving;
 private bool priorOwned;
 public readonly Dictionary<string,string> CandidateAttempts=new();
 public ProgressTrace Current { get; private set; }=new();
 public bool Enabled=>config.Enabled;
 public PlanProgress(ProgressConfig config){this.config=config;}
 public static bool IsMove(string a)=>a is "Push" or "ContinuePush" or "Search" or "FallBack" or "Cover" or "Flank";
 public bool Blocked(string key,double now)=>false;
 public void Clear(){last=double.NaN;priorOwned=false;hold=stalled=moving=0;CandidateAttempts.Clear();Current=new(){Reason="progress-reset"};}
 public bool Observe(ProgressFrame f)
 {
  if(!Enabled||!f.Scoped||!MedicalChoice.Finite(f.Now)||!MedicalChoice.Finite(f.X)||!MedicalChoice.Finite(f.Y)||!MedicalChoice.Finite(f.Z)){Clear();return false;}
  if(!double.IsNaN(last)&&f.Now<last)Clear();
  double dt=priorOwned&&f.Owned&&!f.Paused&&!double.IsNaN(last)&&f.Now-last<=1?Math.Max(0,f.Now-last):0;
  double distance=double.IsNaN(last)?0:Math.Sqrt((f.X-x)*(f.X-x)+(f.Y-y)*(f.Y-y)+(f.Z-z)*(f.Z-z));
  if(f.Productive)hold=0;else if(f.Action is "Hold" or "KeepAngle"||f.Arrived)hold+=dt;
  if(IsMove(f.Action)&&!f.Arrived){moving+=dt;stalled=distance>.05?0:stalled+dt;}else stalled=0;
  Current=new(){At=f.Now,State=f.Paused?"paused":f.Arrived?"arrived":f.Owned?"tracking":"idle",Reason=f.Paused?f.PauseReason:"progress-observing",Action=f.Action,Attempt=f.Attempt,Intent=f.Intent,HoldSeconds=hold,StalledSeconds=stalled,MovementSeconds=moving,ProgressMeters=distance,Remaining=f.Remaining,Productive=f.Productive,LastOutcome=Current.LastOutcome};
  last=f.Now;x=f.X;y=f.Y;z=f.Z;priorOwned=f.Owned&&!f.Paused;return false;
 }
 public void Fail(string key,double now,string reason){Current.LastOutcome=reason;Current.Failures++;}
 public void Adjust(List<Candidate> candidates,double now){foreach(var c in candidates){c.ScoreBeforeProgress=c.Score;c.ProgressDelta=0;c.ProgressReason="progress-observer-only";}}
}
