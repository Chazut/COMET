using System;
using System.Collections.Generic;
namespace Comet.Core;
public static class EncounterScoring
{
    public static void Adjust(List<Candidate> candidates,Situation s,CometConfig c)
    {
        var t=s.Encounter;
        if(t==null)return;
        t.AppliedBonus=0;
        var hold=candidates.Find(x=>x.Decision=="Hold");
        if(!c.Encounter.Enabled||!t.Eligible||t.Target!=s.Target||t.Remaining<=0||s.Visible||s.Hurt||s.LowAmmo||s.Suppressed||s.Fear?.Panic==true)
        {t.HoldRefusal="encounter-ineligible-or-urgent";return;}
        if(hold?.Eligible!=true){t.HoldRefusal=hold?.Reason??"hold-unavailable";return;}
        // Personality remains in the base score; weights cannot expand this independent cap.
        hold.EncounterDelta=Math.Max(0,Math.Min(c.Encounter.ScoreCap,t.Bonus));
        hold.Score+=hold.EncounterDelta;t.AppliedBonus=hold.EncounterDelta;t.HoldRefusal="";
    }
}
