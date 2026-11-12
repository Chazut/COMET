using Comet.Core;
using System;
using System.Linq;
using System.Collections.Generic;
// Reference: R_F SAIN zzap fbb86b838e0fa692b759b2c0b11fc30158a53876.
internal static class TacticalMistakeTests
{
    internal static int Run()
    {
        int n=0;void Check(bool ok,string why){n++;if(!ok)throw new Exception("TacticalMistake: "+why);}
        void Near(double actual,double expected,string why)=>Check(Math.Abs(actual-expected)<1e-9,why);

        var c=new CometConfig();c.DecisionVariety.Enabled=false;
        foreach(var key in c.TacticalMistakes.Chances.Keys.ToArray())c.TacticalMistakes.Chances[key]=1;
        List<Candidate> Pool()=>new(){new(){Decision="Shoot",Score=80,Eligible=true},new(){Decision="Cover",Score=76,Eligible=true},new(){Decision="Push",Score=66,Eligible=true},new(){Decision="Search",Score=64,Eligible=true}};
        var m=new DecisionMixer(c.DecisionVariety,"source",true,new TacticalMistakes(c.TacticalMistakes,"source"));var p=Pool();
        var first=m.Select(p,"a","Normal","test",now:10)!;int draws=m.Last!.Mistake!.DrawIndex;
        Check(first.Decision is "Cover" or "Push","mistake picks second or third, not fourth");
        foreach(double at in new[]{10.1,11,14.999})Check(m.Select(p,"changed-contact","Normal","test",now:at)!.Decision==first.Decision&&m.Last!.Mistake!.DrawIndex==draws,"five-second verdict survives target changes");
        m.Select(p,"a","Normal","test",now:15);Check(m.Last!.Mistake!.DrawIndex>draws,"verdict expires at five seconds");
        var counts=new Dictionary<string,int>();for(int i=0;i<30000;i++){var x=m.Select(p,"a","Normal","test",now:20+i*5)!.Decision;counts[x]=counts.GetValueOrDefault(x)+1;}
        Check(Math.Abs(counts["Push"]/30000d-.3)<.015,"source third-choice rate is 30 percent");
        p[1].Score=45;p[2].Eligible=false;p[3].Eligible=false;
        Check(m.Select(p,"a","Normal","test",now:200000)!.Decision=="Cover","absolute 0.35 utility loss is allowed");
        p[1].Score=44.99;Check(m.Select(p,"a","Normal","test",now:200006)!.Decision=="Shoot","greater absolute loss rejects mistake");
        p[1].Score=79;Check(m.Select(p,"a","Normal","test",urgent:true,retry:true,now:200012)!.Decision=="Cover","no extra COMET urgency or near-score exclusion");
        p[1].Eligible=false;Check(m.Select(p,"a","Normal","test",now:200018)!.Decision=="Shoot","native eligibility still respected");

        return n;
    }
}
