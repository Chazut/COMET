using Comet.Core;
using System.Text.Json;

static class GrenadeJudgmentTests
{
    public static int Run()
    {
        int checks=0,draws=0;void Check(bool ok,string why){checks++;if(!ok)throw new Exception("G01: "+why);}
        double Draw(){draws++;return .1;}
        var c=new GrenadeJudgmentConfig();c.Validate(8);
        Check(c.Enabled&&c.BaseChance==50&&c.CampSeconds==6&&c.CampBonus==30&&c.RoomBonus==10&&c.VulnerableBonus==15&&c.LastGrenadePenalty==20&&c.FadeStart==4&&c.NoArcRetry==1.5&&c.RetryMin==3&&c.RetryMax==6,"approved defaults");
        JsonSerializer.Deserialize<CometConfig>("{}")!.Validate();
        foreach(var prop in typeof(GrenadeJudgmentConfig).GetProperties().Where(p=>p.PropertyType==typeof(double)))
        foreach(var bad in new[]{double.NaN,double.PositiveInfinity,-1d,1000d})
        {var x=new GrenadeJudgmentConfig();prop.SetValue(x,bad);bool threw=false;try{x.Validate(8);}catch(ArgumentException){threw=true;}Check(threw,"invalid "+prop.Name);}
        bool missing=false;try{new CometConfig{GrenadeJudgment=null!}.Validate();}catch(ArgumentException){missing=true;}Check(missing,"missing object rejected");
        GrenadeJudgmentFrame Frame(double t=10)=>new(){Now=t,EvidenceAt=t,Target="enemy",Instance=1,Source="seen",Personal=true,Item="rgo",Count=2,X=20};
        var wire=new List<GrenadeJudgmentTrace>();
        void Snapshot(GrenadeJudgmentPolicy p)=>wire.Add(JsonSerializer.Deserialize<GrenadeJudgmentTrace>(JsonSerializer.Serialize(p.Trace,new JsonSerializerOptions{IncludeFields=true}),new JsonSerializerOptions{IncludeFields=true})!);
        {
            var p=new GrenadeJudgmentPolicy();var f=Frame();int before=draws;
            Check(!p.Evaluate(f,c,8,false,Draw)&&draws==before,"trajectory failure consumes no roll");
            f.Now=11.49;Check(!p.Prepare(f,c,8),"no arc waits 1.5 s");f.Now=11.5;
            Check(p.Evaluate(f,c,8,true,Draw)&&draws==before+2,"one judgment and one retry jitter draw");
            Snapshot(p);for(int i=0;i<50;i++)Check(p.Evaluate(f,c,8,true,Draw)&&draws==before+2,"A01 D07 M13 native share roll");
            f.Block="grenade-nonhostile-exposed";Check(!p.Evaluate(f,c,8,true,Draw)&&draws==before+2,"moving ally invalidates cached approval");Snapshot(p);
            f.Block="";Check(p.Evaluate(f,c,8,true,Draw)&&draws==before+2,"safety restored does not add a roll");
            p.Request(f);Snapshot(p);p.Result(true,f.Now,c);Snapshot(p);
            Check(p.Pending&&p.Trace!.Accepted&&!p.Trace.Released,"native acceptance is not release");
            Check(!p.Released("different-grenade",f.Now+.1),"wrong projectile rejected");
            Check(p.Released(f.Item,f.Now+.2)&&p.Trace!.Released,"matching actual projectile confirms");Snapshot(p);
            f.Now+=1;Check(p.Evaluate(f,c,8,true,Draw),"source allows another native throw on unchanged information");
            f.EvidenceAt=f.Now;Check(p.Evaluate(f,c,8,true,Draw),"fresh evidence may rearm (native cooldown still applies)");
        }
        {
            var p=new GrenadeJudgmentPolicy();var f=Frame();int n=0;double High(){n++;return .9;}
            Check(!p.Evaluate(f,c,8,true,High)&&p.Trace!.State=="declined","high draw keeps grenade");Snapshot(p);
            for(double t=10.1;t<15.7;t+=.1){f.Now=t;f.EvidenceAt=t;Check(!p.Evaluate(f,c,8,true,High)&&n==2,"new observations cannot bypass declined cooldown");}
            f.Now=f.EvidenceAt=15.71;Check(!p.Evaluate(f,c,8,true,High)&&n==4,"new opportunity after 3-6 second wait");
        }
        {
            var p=new GrenadeJudgmentPolicy();var f=Frame(0);
            for(double t=0;t<=6;t+=.5){f.Now=f.EvidenceAt=t;p.Observe(f);}
            Check(p.Evaluate(f,c,8,true,Draw)&&p.Trace!.CampBonus==0&&p.Trace.Samples==14,"source camp bonus starts strictly after six seconds");
            f.Now=f.EvidenceAt=6.1;f.Room=f.Vulnerable=true;Check(p.Evaluate(f,c,8,true,Draw)&&p.Trace!.Chance==95,"bonuses bounded at 95 percent");Snapshot(p);
            f.Count=1;Check(p.Evaluate(f,c,8,true,Draw)&&p.Trace!.Chance==95,"source does not penalize the last grenade");
            f.Now=12;Check(p.Evaluate(f,c,8,true,()=>.8)&&p.Trace!.Chance==95,"source does not fade recent contact probability");
        }
        foreach(bool gap in new[]{false,true})
        {
            var p=new GrenadeJudgmentPolicy();var f=Frame(0);p.Observe(f);f.Now=6;if(gap)f.EvidenceAt=6;
            p.Evaluate(f,c,8,true,Draw);Check(p.Trace!.CampBonus==0,"old info or sparse observation is not camping");
        }
        {
            var p=new GrenadeJudgmentPolicy();var f=Frame(0);
            for(double t=0;t<=6;t+=.5){f.Now=f.EvidenceAt=t;f.X+=.3;p.Observe(f);}
            p.Evaluate(f,c,8,true,Draw);Check(p.Trace!.CampBonus==0,"slow drift measured from fixed anchor, not last sample");
            f.Instance++;p.Evaluate(f,c,8,true,Draw);Check(p.Trace!.Samples==1,"target object replacement resets evidence");
        }
        foreach(var mutate in new Action<GrenadeJudgmentFrame>[] {f=>f.EvidenceAt=f.Now-8.1,f=>f.EvidenceAt=f.Now+1,
            f=>f.Block="grenade-self-exposed",f=>f.Block="grenade-treatment-priority",f=>f.Block="grenade-other-visible-threat",f=>f.X=double.NaN})
        {var p=new GrenadeJudgmentPolicy();var f=Frame();mutate(f);int n=draws;Check(!p.Evaluate(f,c,8,true,Draw)&&draws==n,"unsafe inputs never roll");}
        foreach(bool accepted in new[]{true,false})
        {
            var p=new GrenadeJudgmentPolicy();var f=Frame();p.Evaluate(f,c,8,true,Draw);p.Request(f);p.Result(accepted,10,c);Snapshot(p);
            f.Now=21;f.EvidenceAt=21;Check(!p.Released(f.Item,21),"no timeout invented as release");
            p.Prepare(f,c,8);Check(!p.Pending&&!p.Trace!.Released,"unconfirmed request expires");Snapshot(p);
        }
        {
            var p=new GrenadeJudgmentPolicy();var f=Frame();p.Evaluate(f,c,8,true,Draw);p.Request(f);p.Released(f.Item,10);p.Result(true,10,c);
            Check(p.Trace!.Released&&p.Trace.State=="released","synchronous native callback survives postfix");
            f.Target="other";p.Evaluate(f,c,8,true,Draw);f.Target="enemy";Check(p.Evaluate(f,c,8,true,Draw),"source permits another native throw at the same evidence");
            p.Clear("ghost");Check(p.Count==0&&!p.Pending&&p.Trace!.ValidFor==0,"lifecycle clear");Snapshot(p);
        }
        {
            var p=new GrenadeJudgmentPolicy();for(int i=0;i<200;i++){var f=Frame(i);f.Target="target"+i;p.Observe(f);}Check(p.Count==200,"source retains encountered contacts for the raid; lifecycle clear owns cleanup");
        }
        // Deterministic quantile check of the percentage interpretation; not a flaky random statistical assertion.
        foreach(int items in new[]{1,2})
        {int yes=0;for(int i=0;i<1000;i++){var p=new GrenadeJudgmentPolicy();var f=Frame();f.Count=items;if(p.Evaluate(f,c,8,true,()=> (i+.5)/1000))yes++;}Check(yes==500,"50 percent source distribution regardless of remaining inventory");}
        var root=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../"));Directory.CreateDirectory(Path.Combine(root,"artifacts"));
        File.WriteAllText(Path.Combine(root,"artifacts/g01-policy-wire.json"),Newtonsoft.Json.JsonConvert.SerializeObject(wire,new Newtonsoft.Json.JsonSerializerSettings{ContractResolver=new Newtonsoft.Json.Serialization.CamelCasePropertyNamesContractResolver()}));
        Console.WriteLine($"G01: {checks} checks passed");return checks;
    }
}
