using Comet.Core;
using Comet.Server;
using Comet.Client;
using Newtonsoft.Json;

internal static class ThreatSelectionTests
{
    internal static int Run()
    {
        int n = 0;
        void Check(bool condition, string message) { n++; if (!condition) throw new Exception("A07: " + message); }
        void Near(double a, double b, string message) => Check(Math.Abs(a-b)<1e-6,message+": "+a+" vs "+b);
        var cfg = new CometConfig(); cfg.Validate(); var c = cfg.ThreatSelection;
        Check(c.Enabled && c.Interval==.25 && c.Commitment==2 && c.SwitchMargin==.2 && c.HitMemory==3 && c.ShotMemory==2
            && c.CloseDistance==15 && c.ImmediateDistance==6 && c.FootstepDistance==10 && c.FootstepMemory==1.5,"approved defaults");
        foreach (Action<ThreatSelectionConfig> change in new Action<ThreatSelectionConfig>[] {
            x=>x.Interval=0, x=>x.Interval=double.NaN, x=>x.Commitment=-1, x=>x.SwitchMargin=1.1, x=>x.HitMemory=double.PositiveInfinity,
            x=>x.ShotMemory=0, x=>x.ImmediateDistance=16, x=>x.CloseDistance=0, x=>x.FootstepDistance=0, x=>x.FootstepMemory=0 })
        { var bad=new ThreatSelectionConfig();change(bad);bool threw=false;try{bad.Validate();}catch(ArgumentException){threw=true;}Check(threw,"invalid configuration rejected"); }
        ThreatContact Contact(string id,double distance,bool visible=true) => new() { Id=id, Valid=true, Visible=visible, Source=visible?"seen":"heard",
            ObservedAt=10,Distance=distance,X=distance,Y=0,Z=0 };
        ThreatFrame Frame(string current,params ThreatContact[] contacts) {var f=new ThreatFrame{Now=10,Current=current,IsAi=true,IsPmc=true,IsAuthority=true,Active=true};f.Contacts.AddRange(contacts);return f;}
        void Advance(ThreatFrame f,double now) {f.Now=now;foreach(var e in f.Contacts.Where(e=>e.Visible)) e.ObservedAt=now;}
        var a=Contact("a",55);var b=Contact("b",20);var frame=Frame("a",a,b);var selector=new ThreatSelection(cfg);
        Check(selector.Evaluate(frame).Reason=="threat-kept-commitment","ordinary better contact cannot instantly steal target");
        Advance(frame,11.9);Check(selector.Evaluate(frame).Selected=="a","two second target commitment");
        Advance(frame,12);var switched=selector.Evaluate(frame);Check(switched.Selected=="b" && switched.Reason=="threat-better-target","better target wins after hold");
        frame.Current="b";Advance(frame,12.25);Near(selector.Evaluate(frame).HoldRemaining,1.75,"keeping target never refreshes its hold");
        Check(frame.Contacts[0]==a && frame.Contacts[1]==b,"input known enemy order is untouched");
        frame=Frame("a",Contact("a",30),Contact("b",25));selector=new(cfg);selector.Evaluate(frame);Advance(frame,13);
        Check(selector.Evaluate(frame).Reason=="threat-kept-margin","small score advantage cannot cause churn");
        frame=Frame("a",Contact("a",30),Contact("b",5));selector=new(cfg);var close=selector.Evaluate(frame);
        Check(close.Selected=="b" && close.Emergency && close.Reason=="threat-immediate-visible","visible immediate danger breaks commitment");
        frame.Current="b";frame.Contacts[0].HitAt=10.1;Advance(frame,10.1);Check(selector.Evaluate(frame).Selected=="b","do not pingpong from immediate opponent to distant attacker");
        // Two similar opponents taking turns landing hits must not cause per-bullet target flips.
        a=Contact("a",12);b=Contact("b",12);frame=Frame("a",a,b);selector=new(cfg);a.HitAt=10;
        for(int i=0;i<19;i++){Advance(frame,10+i*.1);if(i%2==0)a.HitAt=frame.Now;else b.HitAt=frame.Now;
            Check(selector.Evaluate(frame).Selected=="a","equal attackers do not repeatedly break the hold");}
        a=Contact("a",30);b=Contact("b",8,false);b.Footsteps=true;frame=Frame("a",a,b);selector=new(cfg);
        Check(selector.Evaluate(frame).Selected=="a","footsteps cannot routinely turn bot away from visible duel");
        b.Gunfire=true;b.Footsteps=false;b.HitAt=10;var hit=selector.Evaluate(frame);
        Check(hit.Selected=="b" && hit.Emergency,"personally heard and located recent attacker can take priority");
        b.Source="unlocated";var unlocated=selector.Score(b,10);Check(!unlocated.Eligible && unlocated.Distance==null && unlocated.X==null,"damage alone grants no position");
        b=Contact("b",8,false);b.Source="seen";b.ObservedAt=9;b.HitAt=10;
        Near(selector.Score(b,10).Hit,0,"old visual position cannot locate new hidden attacker");
        b.Source="shared-heard";b.Gunfire=true;b.ObservedAt=10;Near(selector.Score(b,10).Hit,0,"shared contact cannot correlate own incoming fire automatically");
        b.Source="heard";b.ObservedAt=8;Near(selector.Score(b,10).Hit,0,"old sound does not identify current shot");
        b.ObservedAt=10;b.HitAt=10;b.ShotAt=10;
        var t0=selector.Score(b,10);var t1=selector.Score(b,11);var t2=selector.Score(b,12);var t3=selector.Score(b,13);
        Near(t0.Hit,100,"hit contribution");Near(t1.Hit,100*2d/3,"hit fades continuously");Near(t2.Fire,0,"shots expire at two seconds");Near(t3.Hit,0,"hits expire at three seconds");
        Check(t0.Score>t1.Score && t1.Score>t2.Score && t2.Score>t3.Score,"recent evidence loses influence gradually");
        b=Contact("b",5,false);b.Footsteps=true;
        Check(selector.Score(b,10).Steps==25 && selector.Score(b,11.5).Steps==0,"footstep memory expires");
        b.Distance=11;Check(selector.Score(b,10).Steps==0,"distant steps get no proximity hearing bonus");
        b=Contact("b",5);b.ObservedAt=0;Check(!selector.Score(b,30).Eligible,"expired contact cannot survive by being selected");
        foreach(Action<ThreatContact> change in new Action<ThreatContact>[] { x=>x.Valid=false,x=>x.Id="",x=>x.Source="injury",x=>x.ObservedAt=11,
            x=>x.ObservedAt=double.NaN,x=>x.Distance=double.PositiveInfinity,x=>x.Distance=-1,x=>x.X=double.NaN,x=>x.Y=null })
        {b=Contact("b",5);change(b);Check(!selector.Score(b,10).Eligible,"malformed or unperceived contact rejected");}
        frame=Frame("a",Contact("a",20),Contact("b",30));selector=new(cfg);selector.Evaluate(frame);
        frame.Contacts[0].Valid=false;Advance(frame,10.1);Check(selector.Evaluate(frame).Selected=="b","death/removal does not wait for hold");
        frame.Contacts[1].Valid=false;Check(selector.Evaluate(frame).Selected=="","no usable target yields to native selection");
        foreach(Action<ThreatFrame> change in new Action<ThreatFrame>[] {x=>x.IsPmc=false,x=>x.IsAi=false,x=>x.IsAuthority=false,
            x=>x.Active=false,x=>x.Ghost=true,x=>x.NativeUrgency=true,x=>x.Now=double.NaN,x=>x.Now=-1})
        {frame=Frame("a",Contact("a",20),Contact("b",5));change(frame);Check(new ThreatSelection(cfg).Evaluate(frame).Selected=="","scope or urgency excluded");}
        cfg.Enabled=false;Check(new ThreatSelection(cfg).Evaluate(Frame("a",Contact("a",5))).Selected=="","master toggle");cfg.Enabled=true;
        c.Enabled=false;Check(new ThreatSelection(cfg).Evaluate(Frame("a",Contact("a",5))).Selected=="","A07 toggle");c.Enabled=true;
        frame=Frame("a",Contact("a",55),Contact("b",20));frame.Truncated=true;selector=new(cfg);selector.Evaluate(frame);Advance(frame,13);
        Check(selector.Evaluate(frame).Selected=="a","incomplete scan conservatively keeps viable target");
        frame.Contacts[1].Distance=3;Check(selector.Evaluate(frame).Selected=="b","incomplete scan still responds to immediate danger");
        frame=Frame("");for(int i=0;i<100;i++)frame.Contacts.Add(Contact(i.ToString(),20));
        Check(new ThreatSelection(cfg).Evaluate(frame).Candidates.Count==32 && new ThreatSelection(cfg).Evaluate(frame).Truncated,"strict work bound");
        // Scenario permutations: the best equally scored contact is stable without sorting shared input or random rolls.
        var random=new Random(707);
        for(int i=0;i<200;i++) {
            var ids=Enumerable.Range(0,12).OrderBy(_=>random.Next()).ToArray();frame=Frame("");
            foreach(int id in ids)frame.Contacts.Add(Contact("target-"+id.ToString("00"),20));
            var result=new ThreatSelection(cfg).Evaluate(frame);Check(result.Selected=="target-00","stable tie independent of enumeration");
            Check(frame.Contacts.Select(x=>x.Id).SequenceEqual(ids.Select(x=>"target-"+x.ToString("00"))),"shared ordering preserved");
        }
        // No field for faction or opponent health exists in the evidence contract: equal perception gives equal priority.
        Near(new ThreatSelection(cfg).Score(Contact("scav",12),10).Score,new ThreatSelection(cfg).Score(Contact("pmc",12),10).Score,"no blanket PMC bonus");
        var fear=new FearModel(cfg.Fear);fear.Update(new FearInput{Now=10,Personality="Coward",Health=.1,HeavySuppression=true,KnownEnemies=3});
        fear.Update(new FearInput{Now=12,Personality="Coward",Health=.1,HeavySuppression=true,KnownEnemies=3});var fearBefore=fear.Current;
        selector.Evaluate(Frame("a",Contact("b",3)));Check(ReferenceEquals(fearBefore,fear.Current),"target selection cannot reset fear");
        var sight=new SightContinuity(cfg);var sf=new SightFrame{Target="a",Eligible=true,Visible=true,Observed=true,WeaponReady=true,Action="Shoot",Distance=15};
        for(int i=0;i<6;i++){sf.Now=i*.1;sight.Evaluate(sf);}sf.Visible=false;sf.Now=.6;Check(sight.Evaluate(sf)!=null,"A03 established");
        sf.Target="b";sf.Now=.65;Check(sight.Evaluate(sf)==null && sight.Reason=="sight-target-changed","A03 cannot continue old target through handoff");
        var root=Path.Combine(Path.GetTempPath(),"comet-a07-"+Guid.NewGuid());var path=Path.Combine(root,"config.json");
        try {
            var store=new ConfigStore(path);var edit=store.Read();edit.ThreatSelection.Commitment=3;edit.ThreatSelection.SwitchMargin=.3;edit.ThreatSelection.Enabled=false;
            store.Save(edit,edit.Revision);var saved=System.Text.Json.JsonSerializer.Deserialize<CometConfig>(store.ForGame())!;
            Check(saved.ThreatSelection.Commitment==3 && saved.ThreatSelection.SwitchMargin==.3 && !saved.ThreatSelection.Enabled,"server-to-client persistence");
            var before=File.ReadAllText(path);edit=store.Read();edit.ThreatSelection.ImmediateDistance=20;bool threw=false;
            try{store.Save(edit,edit.Revision);}catch(ArgumentException){threw=true;}Check(threw && File.ReadAllText(path)==before,"invalid save atomic");
            var legacy=JsonConvert.DeserializeObject<CometConfig>("{\"Enabled\":true}")!;legacy.Validate();Check(legacy.ThreatSelection.Commitment==2,"older config gains A07 defaults");
        } finally {if(Directory.Exists(root))Directory.Delete(root,true);}
        frame=Frame("");for(int i=0;i<32;i++)frame.Contacts.Add(Contact("0123456789abcdef01234567"+i,20));
        var trace=new ThreatSelection(cfg).Evaluate(frame);var json=JsonConvert.SerializeObject(new { config=Diagnostics.ThreatConfig(cfg), threat=trace, fear=fear.Current,
            schema=1,session="0123456789abcdef0123456789abcdef",sequence=99,feature="A07",stage="accepted",reason=trace.Reason,
            decision="SelectTarget",realtime=10,utc=DateTime.UtcNow.ToString("O"),configRevision=cfg.Revision,cometVersion="0.1.0",
            profileId="0123456789abcdef01234567",name="Test PMC",botInstance="0123456789abcdef0123456789abcdef" },Diagnostics.Json);
        Check(json.Contains("\"previous\"")&&json.Contains("\"selected\"")&&json.Contains("\"candidates\""),"RR diagnostic fields serialized");
        Check(System.Text.Encoding.UTF8.GetByteCount(json)<25000,"bounded snapshot fits RR event transport");
        Console.WriteLine("A07 target priority: "+n+" checks passed (perception, priorities, stability, scope, handoffs, configuration, telemetry).");return n;
    }
}
