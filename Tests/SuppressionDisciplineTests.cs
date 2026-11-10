using Comet.Core;
using System.Text.Json;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

static class SuppressionDisciplineTests
{
    public static int Run()
    {
        int n=0;void Check(bool v,string why){n++;if(!v)throw new Exception("W07: "+why);}
        void Throws(Action a,string why){try{a();}catch(ArgumentException){n++;return;}throw new Exception("W07: "+why);}
        var c=new SuppressionDisciplineConfig();c.Validate();Check(c.Enabled&&c.ContactAge==3&&c.BurstRounds==5&&c.PauseSeconds==3&&c.ReservePercent==60&&c.SequenceSeconds==6,"approved defaults");
        foreach(var bad in new[]{double.NaN,double.PositiveInfinity,-1d,0d})
        {Throws(()=>new SuppressionDisciplineConfig{ContactAge=bad}.Validate(),"contact bounds");Throws(()=>new SuppressionDisciplineConfig{PauseSeconds=bad}.Validate(),"pause bounds");Throws(()=>new SuppressionDisciplineConfig{SequenceSeconds=bad}.Validate(),"sequence bounds");Throws(()=>new SuppressionDisciplineConfig{ReservePercent=bad}.Validate(),"reserve bounds");}
        foreach(int bad in new[]{0,-1,16})Throws(()=>new SuppressionDisciplineConfig{BurstRounds=bad}.Validate(),"burst bounds");
        Throws(()=>new CometConfig{SuppressionDiscipline=null!}.Validate(),"null config");
        var config=JsonSerializer.Deserialize<CometConfig>("{}")!;Check(config.SuppressionDiscipline.BurstRounds==5,"old config default");
        config.SuppressionDiscipline=new(){Enabled=false,ContactAge=4,BurstRounds=4,PauseSeconds=2,ReservePercent=70,SequenceSeconds=8};
        config=JsonSerializer.Deserialize<CometConfig>(JsonSerializer.Serialize(config))!;config.Validate();Check(!config.SuppressionDiscipline.Enabled&&config.SuppressionDiscipline.SequenceSeconds==8,"persist controls");
        SuppressionDisciplineFrame F()=>new(){Now=10,ObservedAt=9.5,Target="enemy",Identity="enemy:1",Weapon="rifle",Mode="fullauto",Source="personal-vision",Action="Search",Loaded=30,Capacity=30,Known=new(5,0,10),KnownContact=true,FreshGeometry=true,Clear=true,AllySafe=true,Aligned=true};
        var wire=new List<JsonElement>();void Save(SuppressionDisciplinePolicy p)=>wire.Add(JsonSerializer.SerializeToElement(p.Trace,new JsonSerializerOptions{IncludeFields=true,PropertyNamingPolicy=JsonNamingPolicy.CamelCase}));
        var f=F();var p=new SuppressionDisciplinePolicy();Check(p.Evaluate(f,c)&&p.MayFire,"first permission");Check(p.Trace!.Reserve==18,"30 round magazine keeps 18");
        p.Request();p.Result(true);Save(p);
        for(int i=1;i<=5;i++){f.Now+=.05;p.Shot(f,c,.5);f.Loaded--;Check(p.Trace.ActualShots==i&&p.Trace.BurstShots==i,"actual shot counter");if(i<5)Check(p.MayFire,"burst continues");}
        Save(p);Check(!p.MayFire&&p.Active&&p.Trace.State=="paused"&&Math.Abs(p.Trace.PauseRemaining-3)<1e-6,"5 shots pause 3s");
        f.Now+=2.99;f.ObservedAt=f.Now-.2;Check(!p.Evaluate(f,c)&&p.Trace.State=="paused","pause not bypassed by newer cue");
        f.Now+=.02;Check(p.Evaluate(f,c)&&p.Trace.BurstShots==0&&p.Trace.ActualShots==5,"new burst after pause");
        p.Request();p.Result(true);for(int i=0;i<5;i++){p.Shot(f,c,.5);f.Loaded--;}
        f.Now=16.01;f.ObservedAt=16;Check(!p.Evaluate(f,c)&&!p.Active&&p.Trace.Reason=="suppression-sequence-limit","sequence includes pauses and fresh contact cannot extend it");Save(p);
        Check(!p.Evaluate(f,c)&&p.Trace.Reason=="suppression-needs-new-contact","no immediate restart");
        f.Now=16.1;f.ObservedAt=16.05;Check(p.Evaluate(f,c),"new personal evidence permits another episode");
        f.Visible=true;Check(!p.Evaluate(f,c)&&p.Trace.Reason=="suppression-visible-target","visible enemy ends before pause");Save(p);
        foreach(double roll in new[]{0d,.5d,1d,double.NaN})
        {p=new();f=F();p.Evaluate(f,c);p.Request();for(int i=0;i<5;i++)p.Shot(f,c,roll);Check(Math.Abs(p.Trace!.PauseRemaining-(double.IsNaN(roll)?3:3*(.7+.6*roll)))<1e-8,"bounded varied pause");}
        // Generous time/contact windows isolate reserve and observed-round behavior across magazine capacities.
        var reserveConfig=new SuppressionDisciplineConfig{BurstRounds=15,PauseSeconds=.5,ContactAge=10,SequenceSeconds=15};
        for(int cap=1;cap<=100;cap++)
        {p=new();f=F();f.Capacity=cap;f.Loaded=cap;int shots=0;while(shots<120){f.Now+=.01;f.ObservedAt=f.Now;if(!p.Evaluate(f,reserveConfig)){if(p.Trace!.State=="paused"){f.Now+=.66;continue;}break;}p.Request();p.Result(true);p.Shot(f,reserveConfig,.5);f.Loaded--;shots++;if(!p.Active)break;}Check(f.Loaded>=reserveConfig.Reserve(cap),"reserve preserved for capacity "+cap);Check(shots<=cap-reserveConfig.Reserve(cap),"quota bounds before ammo callback "+cap);}
        foreach(var mutation in new Action<SuppressionDisciplineFrame>[] {x=>x.Visible=true,x=>x.KnownContact=false,x=>x.Source="shared-seen",x=>x.ObservedAt=6.99,x=>x.ObservedAt=11,x=>x.ObservedAt=double.NaN,x=>x.Now=double.NaN,x=>x.Known=new(double.NaN,0,0),x=>x.Loaded=18,x=>x.Capacity=0,x=>x.Loaded=31,x=>x.Block="reload",x=>x.SupportUseful=false})
        {p=new();f=F();mutation(f);Check(!p.Evaluate(f,c)&&!p.MayFire,"invalid/urgent input rejected");Save(p);}
        foreach(var mutation in new Action<SuppressionDisciplineFrame>[] {x=>x.Clear=false,x=>x.AllySafe=false,x=>x.Aligned=false,x=>x.FreshGeometry=false})
        {p=new();f=F();mutation(f);Check(!p.Evaluate(f,c)&&p.Active&&!p.MayFire,"geometry cannot grant fire");f.Clear=f.AllySafe=f.Aligned=f.FreshGeometry=true;Check(p.Evaluate(f,c),"deferred check can recover without consuming contact");}
        p=new();f=F();p.Evaluate(f,c);p.Request();p.Result(false);Check(p.Trace!.State=="waiting"&&!p.Trace.Accepted&&p.Trace.ActualShots==0,"request refusal is not a shot");
        f.Now=13;f.ObservedAt=10;Check(p.Evaluate(f,c),"exact contact boundary allowed");f.Now+=.001;Check(!p.Evaluate(f,c),"contact expiry");
        foreach(string change in new[]{"weapon","mode","action","support"})
        {p=new();f=F();p.Evaluate(f,c);p.Request();p.Result(true);if(change=="weapon")f.Weapon="pistol";else if(change=="mode")f.Mode="single";else if(change=="action")f.Action="Retreat";else f.SupportPlan="new-plan";Check(!p.Evaluate(f,c),"context change cannot recycle old evidence "+change);}
        p=new();f=F();p.Evaluate(f,c);p.Request();p.Result(true);p.End("ghost");Check(!p.Evaluate(f,c),"ghost wake does not reuse consumed contact");f.ObservedAt+=.00001;Check(!p.Evaluate(f,c),"float timestamp noise not a new observation");
        p=new();f=F();p.Evaluate(f,c);p.Request();p.Result(true);p.Shot(f,c,.5);f.Loaded=30;Check(p.Trace!.ActualShots==1,"ammo count increase doesn't erase observed shots");
        f.Now=2;f.ObservedAt=1;Check(p.Evaluate(f,c)&&p.Trace.ActualShots==0,"clock rollback starts clean episode");
        p=new();f=F();p.Evaluate(f,c);var aim=p.Aim;f.Known=new(20,0,20);f.ObservedAt=10;p.Evaluate(f,c);Check((p.Aim-aim).Length==0,"aim frozen inside burst");
        p=new();f=F();p.Evaluate(f,c);p.Request();p.Result(true);for(int i=0;i<5;i++)p.Shot(f,c,.5);
        p.Shot(f,c,.5);Check(!p.Active&&p.Trace!.ActualShots==6&&p.Trace.Reason=="suppression-shot-outside-permission","queued native shot recorded honestly, then canceled");Save(p);
        p=new();f=F();p.Evaluate(f,c);p.Request();for(int i=0;i<5;i++)p.Shot(f,c,.5);
        p.Result(true);Check(p.Trace!.Accepted&&p.Trace.State=="paused","synchronous shot callback before request postfix preserves pause");
        f.Now+=3.1;f.ObservedAt=f.Now;p.Evaluate(f,c);Check(p.Trace.ActualShots==5&&!p.Trace.Requested&&p.Trace.BurstShots==0,"new burst permission retains total, resets request");Save(p);
        p=new();f=F();p.Evaluate(f,c);p.Request();f.Now+=6.1;p.Shot(f,c,.5);Check(!p.Active&&p.Trace!.ActualShots==1&&p.Trace.Reason=="suppression-sequence-limit","late native callback counted and stopped");Save(p);
        using(var pe=new PEReader(File.OpenRead("Client/bin/Release/netstandard2.1/COMET.dll")))
        {
            var md=pe.GetMetadataReader();var type=md.TypeDefinitions.Select(md.GetTypeDefinition).Single(t=>md.GetString(t.Name)=="Plugin");
            MethodDefinition M(string name)=>type.GetMethods().Select(md.GetMethodDefinition).Single(m=>md.GetString(m.Name)==name);
            int Calls(string name,string call)=>ReloadTimingTests.Calls(pe,md,M(name),call);
            foreach(string gate in new[]{"IsSupportedAi","get_Authority","Ghost","get_BotActive","get_IsDead","get_GameEnding","get_SAINLayersActive"})Check(Calls("DisciplineScope",gate)==1,"scope "+gate);
            Check(Calls("DisciplineGeometry","TryBegin")==1&&Calls("DisciplineGeometry","Charge")==1,"geometry budget");
            Check(Calls("DisciplineShot","Shot")==1,"actual native shot observation");
            Check(Calls("DisciplineVisibleFire","EndDiscipline")==1,"normal visible firing released");
            foreach(string method in new[]{"DisciplineFrame","DisciplineRequest","SampleDiscipline","DisciplineGeometry","DisciplineShot"})
                foreach(string forbidden in new[]{"get_EnemyPlayer","get_EnemyPosition","get_SuppressionTarget","get_HealthController","SetFireMode","TryChangeWeapon","AddAmmoToMagazines","FastForward","FindObjectsOfType"})Check(Calls(method,forbidden)==0,"no hidden state or firing modification "+method+forbidden);
            foreach(string call in new[]{"IsInReloadOperation","IsInInteraction","get_IsChanging","get_Reloading","get_Using"})Check(Calls("DisciplineFrame",call)>0,"native operations preserved "+call);
        }
        p=new();f=F();p.Evaluate(f,c);p.Request();p.Result(true);p.Shot(f,c,.5);Save(p);
        var options=new JsonSerializerOptions{IncludeFields=true,PropertyNamingPolicy=JsonNamingPolicy.CamelCase};
        File.WriteAllText("artifacts/w07-wire-traces.json",JsonSerializer.Serialize(wire));
        File.WriteAllText("artifacts/w07-suppression-discipline.json",JsonSerializer.Serialize(new{botInstance="instance",suppressionDiscipline=wire[1],suppressionDisciplineConfig=c},options));
        Console.WriteLine($"W07: {n} assertions passed.");return n;
    }
}
