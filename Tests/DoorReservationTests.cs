using Comet.Core;
using Comet.Server;
using System.Text.Json;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

internal static class DoorReservationTests
{
    internal static int Run()
    {
        int n=0;void Check(bool value,string why){if(!value)throw new Exception("D14: "+why);n++;}
        void Invalid(Action a){try{a();}catch(ArgumentException){n++;return;}throw new Exception("D14 accepted invalid settings");}
        var c=new DoorReservationConfig();c.Validate();
        Check(c.Enabled&&c.LeaseSeconds==2&&c.RenewInterval==.5&&c.BusyRetry==2,"approved defaults");
        Check(JsonSerializer.Deserialize<CometConfig>("{}")!.DoorReservation.LeaseSeconds==2,"old config loads defaults");
        foreach(var prop in typeof(DoorReservationConfig).GetProperties().Where(p=>p.PropertyType==typeof(double)))
        foreach(var bad in new[]{-1d,0,double.NaN,double.PositiveInfinity,100})
        {var cfg=new DoorReservationConfig();prop.SetValue(cfg,bad);Invalid(cfg.Validate);}
        Invalid(()=>new DoorReservationConfig{LeaseSeconds=1,RenewInterval=1}.Validate());
        Invalid(()=>new CometConfig{DoorReservation=null!}.Validate());
        var dir=Path.Combine(Path.GetTempPath(),"comet-d14-"+Guid.NewGuid().ToString("N"));
        try
        {
            var store=new ConfigStore(Path.Combine(dir,"config.json"));var cfg=store.Read();
            cfg.DoorReservation.Enabled=false;cfg.DoorReservation.LeaseSeconds=3;cfg.DoorReservation.BusyRetry=4;
            var saved=store.Save(cfg,cfg.Revision);Check(!store.Read().DoorReservation.Enabled&&store.Read().DoorReservation.BusyRetry==4,"server persistence");
            saved.DoorReservation.LeaseSeconds=-1;Invalid(()=>store.Save(saved,saved.Revision));Check(store.Read().DoorReservation.LeaseSeconds==3,"invalid save atomic");
        }finally{Directory.Delete(dir,true);}
        var board=new DoorReservationBoard();
        DoorReservationLease Get(string group,string actor,double now=100,string door="door",double deadline=110)
        {Check(board.Acquire(group,door,actor,actor+"-instance","approach",now,deadline,c,out var lease,out var why)&&why=="reservation-acquired","acquire "+actor);return lease!;}
        var a=Get("squad", "a");
        Check(!board.Acquire("squad","door","b","b-instance","entry",100,110,c,out var busy,out var reason)&&ReferenceEquals(busy,a)&&reason=="reservation-door-busy","same-frame requests serialize");
        Check(!board.Acquire("squad","door","a","a-instance","entry",100,110,c,out _,out _),"acquire cannot replace existing token");
        var enemy=Get("enemy-squad","enemy");Check(board.Count==2,"opposing squad isolated at same door");
        var other=Get("squad","b",door:"other-door");Check(board.Count==3,"another door remains usable");
        foreach(var phase in new[]{"feint:drawing","feint:recovering","entry:planning","entry:moving"})
        {Check(board.Renew(a,"squad","a-instance",phase,100.5,true,c),"phase transition keeps owner");Check(a.Deadline==110&&a.Token==1,"transition does not refund time or token");}
        Check(board.Owns(a,"squad","a-instance",102.49),"live until expiry");
        Check(!board.Renew(a,"squad","a-instance","entry",102.5,true,c),"expired lease cannot renew");
        var next=Get("squad","b",102.5);
        Check(!board.Release(a)&&board.Owns(next,"squad","b-instance",102.5),"late old release cannot erase successor");
        Check(!board.Renew(a,"squad","a-instance","entry",102.5,true,c)&&board.Owns(next,"squad","b-instance",102.5),"late old renewal cannot erase successor");
        Check(!board.Renew(next,"changed-squad","b-instance","entry",102.6,true,c),"squad change releases");
        Check(board.Find("squad","door",102.6)==null,"changed-squad claim gone");
        a=Get("squad","a",103);Check(!board.Renew(a,"squad","respawn-instance","entry",103,true,c),"respawn instance is not owner");
        a=Get("squad","a",103.1);Check(!board.Renew(a,"squad","a-instance","entry",103.2,false,c),"invalid native callback releases");
        a=Get("squad","a",103.3);Check(!board.Renew(a,"squad","a-instance","entry",103.4,true,new(){Enabled=false}),"disable releases");
        foreach(var end in new[]{"death","ghost","authority","interruption","D13 cancel","raid end"})
        {a=Get("squad","a",104);Check(board.Release(a)&&board.Find("squad","door",104)==null,"explicit lifecycle release "+end);}
        // D12 retains its own absolute deadline. A busy door does not consume/extend the ticket.
        var resume=new DoorInterruption();var rc=new DoorInterruptionConfig();
        Check(resume.Interrupt(new(){Attempt="ticket",Plan="D01",Phase="approaching",Door="door",Target="known",SafeStage=true},104,110,"incoming-fire","","heard",rc),"interrupt ticket");
        next=Get("squad","b",104);Check(board.Find("squad","door",105)!=null&&resume.Deadline<=110,"busy resume waits under original deadline");
        board.Release(next);a=Get("squad","a",106,deadline:resume.Deadline);long token=a.Token;
        Check(a.Until<=resume.Deadline,"reacquire capped to original resume deadline");
        for(double t=106.5;t<resume.Deadline;t+=.5)Check(board.Renew(a,"squad","a-instance","entry",t,true,c)&&a.Deadline==resume.Deadline,"fixed budget during resume");
        Check(!board.Owns(a,"squad","a-instance",resume.Deadline),"exact original cap");
        board.Clear();a=Get("squad","a",200,deadline:210);Check(a.Token>token,"clear cannot reuse token");board.Sweep(202);Check(board.Count==0,"idle expiry cleanup");
        a=Get("squad","a",203,deadline:210);Check(board.Find("squad","door",202)==null&&board.Count==0,"backwards clock clears");
        foreach(double bad in new[]{double.NaN,double.PositiveInfinity,-1d})Check(!board.Acquire("s","d","a","i","approach",bad,210,c,out _,out _),"bad clock");
        foreach(double bad in new[]{double.NaN,double.PositiveInfinity,199d,200d})Check(!board.Acquire("s","d","a","i","approach",200,bad,c,out _,out _),"bad deadline");
        Check(board.Acquire("s","d","a","i","approach",200,210,new(){Enabled=false},out var disabled,out _)&&disabled==null&&board.Count==0,"disabled pass through");
        // Exercise many independently interleaved groups without leaking ownership across keys.
        board.Clear();for(int i=0;i<40;i++)Get("s"+i,"a"+i,300,deadline:305);
        for(int i=0;i<40;i++)Check(!board.Acquire("s"+i,"door","b","bi","entry",300,305,c,out _,out _),"parallel group contention");
        board.Sweep(302);Check(board.Count==0,"all expired references removed");
        using(var pe=new PEReader(File.OpenRead("references/sain-4.5.1/SAIN.dll")))
        {var md=pe.GetMetadataReader();var squad=md.TypeDefinitions.Select(h=>md.GetTypeDefinition(h)).Single(x=>md.GetString(x.Name)=="Squad");Check(squad.GetMethods().Select(h=>md.GetMethodDefinition(h)).Any(x=>md.GetString(x.Name)=="get_GUID"),"official stable squad identity contract");}
        var trace=new DoorReservationTrace{State="active",Reason="reservation-validated",Door="door",Squad="squad",Owner="test-pmc",Token=42,Phase="entry:planning",Target="known",Remaining=2,BudgetRemaining=7,ActionObserved=true};
        File.WriteAllText("artifacts/d14-door-reservation.json",JsonSerializer.Serialize(new{schema=1,botInstance="instance",doorReservation=trace,doorReservationConfig=c},new JsonSerializerOptions{PropertyNamingPolicy=JsonNamingPolicy.CamelCase,WriteIndented=true}));
        Console.WriteLine($"D14: {n} checks passed (ownership, expiry, transitions, isolation, D12 budgets, server config, native contract)");return n;
    }
}
