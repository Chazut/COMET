using System;
using System.Collections.Generic;

namespace Comet.Core;
public static class OutcomeScoring
{
    public static void Adjust(List<Candidate> candidates,Situation s,CometConfig c,bool extraUrgency=false)
    {
        var t=s.Outcome;if(t==null)return;
        foreach(var e in t.Effects)
        {
            e.Applied=0;var candidate=candidates.Find(x=>x.Decision==e.Action);
            if(!c.Outcomes.Enabled||t.State!="review"||t.Target!=s.Target||t.Remaining<=0||!s.IsAi||!s.IsSupportedBot||!s.IsAuthority||!s.IsActive||s.Ghost
                ||s.Hurt||s.LowAmmo||s.Suppressed||s.Fear?.Panic==true||extraUrgency){e.Refusal="outcome-urgent-or-out-of-scope";continue;}
            if(candidate?.Eligible!=true){e.Refusal=candidate?.Reason??"outcome-unavailable-action";continue;}
            if(e.Results<c.Outcomes.MinimumResults){e.Refusal="outcome-insufficient-comparable-results";continue;}
            if(!TacticalOutcomes.Finite(e.Delta)){e.Refusal="outcome-invalid-score";continue;}
            double delta=Math.Max(-c.Outcomes.PenaltyCap,Math.Min(c.Outcomes.BonusCap,e.Delta));
            delta=Math.Max(-candidate.Score,delta);candidate.Score+=delta;candidate.OutcomeDelta=delta;e.Applied=delta;e.Refusal="";
        }
    }
}
