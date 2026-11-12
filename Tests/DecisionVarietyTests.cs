using Comet.Core;
using System;
using System.Linq;
using System.Collections.Generic;
// Reference: R_F SAIN zzap fbb86b838e0fa692b759b2c0b11fc30158a53876.
internal static class DecisionVarietyTests
{
    internal static int Run()
    {
        int n=0;void Check(bool ok,string why){n++;if(!ok)throw new Exception("DecisionVariety: "+why);}
        void Near(double actual,double expected,string why)=>Check(Math.Abs(actual-expected)<1e-9,why);

        List<Candidate> Pool()=>new(){new(){Decision="Push",Score=80,Eligible=true},new(){Decision="Grenade",Score=76,Eligible=true},new(){Decision="Flank",Score=72,Eligible=true},new(){Decision="Hold",Score=69,Eligible=true},new(){Decision="Invalid",Score=999,Eligible=false}};
        var c=new CometConfig().DecisionVariety;var m=new DecisionMixer(c,"source",false);var pool=Pool();m.Select(pool,"e","Normal","test");
        var trace=m.Last!;Near(trace.Temperature,6,"source softmax temperature");
        double sum=1+Math.Exp(-4d/6)+Math.Exp(-8d/6);
        foreach(var o in trace.Options.Take(3))Near(o.Probability,Math.Exp((o.Score-80)/6)/sum,"top-three softmax");
        Check(trace.Options.Skip(3).All(x=>x.Probability==0),"only top three eligible actions");
        var counts=new Dictionary<string,int>();
        for(int i=0;i<40000;i++){var a=m.Select(pool,"e","Normal","test")!;counts[a.Decision]=counts.GetValueOrDefault(a.Decision)+1;}
        foreach(var o in trace.Options.Take(3))Check(Math.Abs(counts[o.Decision]/40000d-o.Probability)<.012,"observed choice distribution");
        foreach(var entry in new[]{("Rat",3d),("Coward",3d),("SnappingTurtle",3.6),("Chad",7.2),("GigaChad",9d),("Wreckless",12d)})
        {m.Select(pool,"e",entry.Item1,"test");Near(m.Last!.Temperature,entry.Item2,"personality temperature");}
        var v=new DecisionMixer(c,"source",true);
        for(int i=0;i<100;i++)Check(v.Select(pool,"e","Wreckless","test")!.Decision=="Push"&&v.Last!.Roll==null,"visible utility chooses best before mistakes");
        pool[1].Score=5;pool[2].Score=-1;pool[3].Score=-2;m.Select(pool,"e","Normal","test");
        Check(m.Last!.Options.Count(x=>x.Probability>0)==1,"secondary choices must exceed 0.05 source utility");
        pool[1].Score=6;m.Select(pool,"e","Normal","test",urgent:true,retry:true);
        Check(m.Last!.Options[1].Probability>0,"no COMET relative-gap or urgency veto");
        pool.ForEach(x=>x.Eligible=false);Check(m.Select(pool,"e","Normal","test")==null,"no eligible native action");

        return n;
    }
}
