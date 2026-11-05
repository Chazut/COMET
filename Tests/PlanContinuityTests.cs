using Comet.Core;

internal static class PlanContinuityTests
{
    internal static int Run()
    {
        int n=0;
        void Check(bool b,string message){n++;if(!b)throw new Exception("Plan continuity: "+message);}
        var lease=new PlanContinuity();
        for(int i=0;i<=600;i++)Check(lease.Keep(10+i*.001,.5,"target:Shoot",true)==(i<500),"repeated callbacks never extend deadline");
        lease.Clear();Check(lease.Keep(20,.5,"target:Shoot",true),"healthy observations reset lease");
        Check(!lease.Keep(20.1,.5,"target:Shoot",false),"invalid action cancels");
        Check(!lease.Keep(double.NaN,.5,"x",true),"invalid clock cancels");
        Check(!lease.Keep(21,0,"x",true),"zero tolerance");
        VisibleSituation Frame()=>new(){Contact=new(){Now=10,Target="enemy",IsAi=true,IsPmc=true,IsAuthority=true,IsActive=true,
            Visible=true,HasContact=true,KnownDistance=12,ContactAge=.1,Personality="Normal"},WeaponReady=true,CanShoot=true,
            ProbeReady=true,ThreatCount=1,ThreatSignature="enemy:visible",ContinuationSafe=true};
        CometConfig Config(){var c=new CometConfig();c.VisibleCombat.CoverWeight=0;c.VisibleCombat.PushWeight=0;return c;}
        {
            var e=new VisibleEnemyDecision(Config());var f=Frame();var first=e.Evaluate(f);Check(first.Decision==VisibleTactic.Shoot,"baseline");
            f.ProbeReady=false;
            for(int i=0;i<5;i++){f.Contact.Now=10.1+i*.1;var kept=e.Evaluate(f);Check(kept.Decision==VisibleTactic.Shoot&&!kept.Changed&&kept.Reason=="plan-kept-pending-exposure","queued probe retains plan without reroll");Check(ReferenceEquals(first.Candidates,kept.Candidates),"retained scores, no invented probe");}
            f.Contact.Now=10.61;Check(e.Evaluate(f).Decision==null,"pending work eventually yields");
            f.ProbeReady=true;f.Contact.Now=10.7;Check(e.Evaluate(f).Decision!=null,"full result can resume");
        }
        foreach(Action<VisibleSituation> invalidate in new Action<VisibleSituation>[] {
            f=>f.Contact.Target="new",f=>f.ThreatSignature="new-threat",f=>f.Contact.Hurt=true,f=>f.Contact.LowAmmo=true,
            f=>f.Contact.Suppressed=true,f=>f.Contact.Ghost=true,f=>f.Contact.IsAuthority=false,f=>f.Contact.Visible=false,
            f=>f.Contact.Fear=new FearTrace{Panic=true},f=>f.RecentHit=true,f=>f.WeaponReady=false,f=>f.ContinuationSafe=false})
        {
            var e=new VisibleEnemyDecision(Config());var f=Frame();e.Evaluate(f);f.Contact.Now+=.1;f.ProbeReady=false;invalidate(f);
            Check(e.Evaluate(f).Decision==null,"unsafe pending continuation rejected");
        }
        {
            var e=new VisibleEnemyDecision(Config());var f=Frame();e.Evaluate(f);e.NotifyNativeRefusal();f.Contact.Now+=.1;f.ProbeReady=false;
            Check(e.Evaluate(f).Decision==null,"native execution failure gets no grace");
        }
        {
            var e=new VisibleEnemyDecision(Config());var f=Frame();e.Evaluate(f);f.ProbeReady=false;f.Contact.Now=10.1;
            Check(e.TryContinuePending(f),"native continuation can use the same lease");
            f.Contact.Now=10.4;Check(e.Evaluate(f).Decision==VisibleTactic.Shoot,"chooser shares continuation deadline");
            f.Contact.Now=10.61;Check(!e.TryContinuePending(f)&&f.ContinuationReason=="grace-expired","switching entry points cannot reset deadline");
            e.ProbeRefreshed();f.Contact.Now=10.7;Check(e.TryContinuePending(f),"new measured probe clears pending lease");
            f.ContinuationSafe=false;f.ContinuationReason="";
            Check(!e.TryContinuePending(f)&&f.ContinuationReason=="unverified-plan","missing diagnostic reason cannot bypass safety");
        }
        return n;
    }
}
